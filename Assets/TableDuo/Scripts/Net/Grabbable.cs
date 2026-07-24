#nullable enable
using TableDuoVr.Hands;
using Unity.Netcode;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// テーブル小物の掴み。**サーバ駆動追従**方式:
    /// - クライアントは GrabRequest を送るだけ（要求であって主張ではない — 要件 §3）
    /// - サーバが先着裁定し、保持中はサーバが保持者の手 pose からオブジェクトを毎フレーム動かす
    /// - クライアントへは同居必須の NetworkTransform（サーバ権威・補間）で降りる
    /// ownership 移譲はしない（NGO 1.x コアに ClientNetworkTransform が無く、
    /// サーバは全員の pose を ConnectionManager 経由で常に持っているため、この方が部品が少ない）。
    ///
    /// 物理統合（2026-07-08）: Rigidbody を持つピースは 2 状態モデルで動く。
    /// - Held: isKinematic=true でサーバが手 pose 追従（surfaceY クランプは保持中のみ）
    /// - Free: リリース時に dynamic へ戻し、保持中の pose 履歴から推定した速度を与える
    ///   （投げる・ひっくり返す・転がすが物理で成立）。以後の接地はテーブルの Collider 任せ。
    ///   卓外へ落ちたら spawn 位置へ自動リスポーン。
    /// Rigidbody 無しのピース（カード等）は従来どおり kinematic 追従のみ。
    /// 物理はサーバのみ（クライアント側は NetworkRigidbody が非権威を kinematic 化する）。
    /// </summary>
    public sealed class Grabbable : NetworkBehaviour
    {
        private const ulong NoHolder = ulong.MaxValue;

        /// <summary>サーバ側 grab/release 通知（objectName, clientId, isGrab）。SessionLogger 用。</summary>
        public static event System.Action<string, ulong, bool>? GrabLogged;

        private readonly NetworkVariable<ulong> _holder = new(
            NoHolder, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<byte> _holderHand = new(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // サーバ側のみ: 掴んだ瞬間の手→オブジェクト相対姿勢 + 保持者の席（毎フレ GameObject.Find を避け、掴み時に1回解決）
        private Vector3 _grabOffsetPos;
        private Quaternion _grabOffsetRot = Quaternion.identity;
        private Transform? _grabSeat;

        // 保持者の手がトラッキングロストのまま放置されると解放されず空中静止する。
        // この秒数連続で pose が取れなければサーバが自動解放する
        private const float UntrackedReleaseSeconds = 3f;
        private float _untrackedSince = -1f;

        [Header("静止時の物理モード")]
        [Tooltip("true: 静止中は kinematic で凍結し、掴んで離した時だけ dynamic 化する。" +
                 "山札のように薄い剛体を積む物は dynamic のままだと自重で沈み込んで貫入するため、" +
                 "置かれている間は物理シミュレーションから外す（VR の積み重ねグラバブルの定石）。" +
                 "掴み＝kinematic 追従・離す＝dynamic 落下は不変。TableDuoSceneSetup が山札/手札に設定")]
        [SerializeField] private bool restKinematic;

        [Header("卓上拘束（TableDuoSceneSetup が設定。未設定=拘束なし）")]
        [Tooltip("天板の上面 Y。掴み追従時にピースの最下点がこれを下回らないようクランプする（テーブル貫通防止）")]
        [SerializeField] private float surfaceY = float.NegativeInfinity;
        [Tooltip("天板の XZ 中心。surfaceHalf と併せてピースが卓外へ消えないようクランプする")]
        [SerializeField] private Vector2 surfaceCenter;
        [Tooltip("天板の XZ 半径。(0,0)=XZ クランプなし")]
        [SerializeField] private Vector2 surfaceHalf;

        // ピースの pivot→最下点オフセット（クランプは「最下点が天板以上」で判定）。
        // 掴み中に回転すると変わるため、保持中は現在の bounds から都度計算する（spawn 時値は初期値のみ）
        private float _bottomOffset;
        private Renderer[] _renderers = System.Array.Empty<Renderer>();

        // --- 物理（Rigidbody があるピースのみ有効。サーバ専用） ---
        private Rigidbody? _rb;
        private Vector3 _spawnPos;
        private Quaternion _spawnRot = Quaternion.identity;
        // 卓面よりこれだけ下に落ちたら卓外落下と見なして spawn 位置へ戻す
        private const float FallRespawnDepth = 0.8f;

        // リリース速度推定: 保持中の追従ターゲット（クランプ・平滑前の生 pose）をリングバッファに記録し、
        // 離した瞬間に直近 VelocityWindow 秒の差分から線速度・角速度を推定する。
        // 60Hz 受信 pose 由来でノイジーなので上限クランプ必須（無いとダイスが部屋の外へ飛ぶ）。
        private const float VelocityWindow = 0.12f;
        private const float MaxLinearSpeed = 3.5f;   // m/s
        private const float MaxAngularSpeed = 12f;   // rad/s
        private const int PoseHistoryCapacity = 16;
        private readonly (float t, Vector3 p, Quaternion r)[] _poseHistory =
            new (float, Vector3, Quaternion)[PoseHistoryCapacity];
        private int _poseCount;
        private int _poseHead; // 次に書く位置

        private void Awake()
        {
            _renderers = GetComponentsInChildren<Renderer>();
            _bottomOffset = CurrentBottomOffset();
            _rb = GetComponent<Rigidbody>();
            if (_rb != null)
            {
                // PhysX 既定の maxAngularVelocity=7rad/s では投げ回転が頭打ちになる
                _rb.maxAngularVelocity = 20f;
            }
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                _spawnPos = transform.position;
                _spawnRot = transform.rotation;
                // restKinematic ピースは静止状態で凍結して出す（山札の 16 段スタックが
                // dynamic のまま自重で沈み込み・相互貫入するのを根本回避）。クライアントは
                // NetworkRigidbody が非権威側を kinematic 化するのでサーバ側だけ設定すれば足りる
                if (restKinematic && _rb != null) _rb.isKinematic = true;
            }
        }

        /// <summary>
        /// このピースが「静止中は kinematic で凍結」する設計か（山札等）。GameSwitcher が
        /// アクティブ化時に dynamic へ戻すべきか判断するのに使う（restKinematic 物は凍結のまま維持）。
        /// </summary>
        public bool RestKinematic => restKinematic;

        /// <summary>
        /// restKinematic ピースを静止状態（kinematic・凍結）へ戻す（サーバ専用・保持中は無視）。
        /// AlgoDealer の配り直し後に呼び、再配置した山札をクリーンな凍結スタックに戻す。
        /// </summary>
        public void ServerSettleKinematic()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || _rb == null || IsHeld || !restKinematic) return;
            _rb.velocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            _rb.isKinematic = true;
        }

        /// <summary>現在の姿勢での pivot→最下点オフセット（回転で変わる）。Renderer 無しは 0。</summary>
        private float CurrentBottomOffset()
        {
            if (_renderers.Length == 0) return 0f;
            float minY = float.PositiveInfinity;
            foreach (var r in _renderers)
            {
                if (r != null) minY = Mathf.Min(minY, r.bounds.min.y);
            }
            return float.IsPositiveInfinity(minY) ? 0f : minY - transform.position.y;
        }

        /// <summary>掴み中の手が天板の下へ潜っても、ピースは天板上・卓の範囲内に留める。</summary>
        private Vector3 ClampToSurface(Vector3 p)
        {
            if (float.IsNegativeInfinity(surfaceY)) return p;
            if (IsHeld) _bottomOffset = CurrentBottomOffset(); // 細長い駒を回して持つと最下点が変わる
            float minY = surfaceY - _bottomOffset;
            if (p.y < minY) p.y = minY;
            if (surfaceHalf.x > 0f)
            {
                p.x = Mathf.Clamp(p.x, surfaceCenter.x - surfaceHalf.x, surfaceCenter.x + surfaceHalf.x);
                p.z = Mathf.Clamp(p.z, surfaceCenter.y - surfaceHalf.y, surfaceCenter.y + surfaceHalf.y);
            }
            return p;
        }

        public bool IsHeld => _holder.Value != NoHolder;
        public ulong HolderClientId => _holder.Value;

        /// <summary>保持者の手（0=L / 1=R）。<see cref="ToolGripDriver"/> がペン/消しゴムの手 pose 解決に使う。</summary>
        public byte HolderHand => _holderHand.Value;

        /// <summary>サーバ側で掴み時に解決済みの保持者の席アンカー（server のみ有効・非保持中は null）。
        /// <see cref="ToolGripDriver"/> がリモート保持者のピンチ点 FK を席ローカル→ワールド化するのに使う。</summary>
        public Transform? HolderSeat => _grabSeat;

        /// <summary>stow（気配消し）状態。<see cref="GameSwitcher"/> が全 peer で設定する。
        /// replicated var（activeGame）起点なので peer 間で一致する。stow 中は掴み対象から除外され、
        /// サーバ側 grab RPC もはじく（コライダー非依存の距離検索を二重防御）。</summary>
        public bool IsStowed { get; private set; }

        /// <summary>stow 状態を設定する（GameSwitcher が ApplyLocal で全 peer に反映）。</summary>
        public void SetStowed(bool stowed) => IsStowed = stowed;

        public bool IsHeldBy(ulong clientId, byte hand) =>
            _holder.Value == clientId && _holderHand.Value == hand;

        /// <summary>
        /// 保持者本人のクライアントが毎フレーム呼ぶ 0 レイテンシ表示（楽観的ローカルアタッチ）。
        /// サーバ往復＋NetworkTransform 補間バッファを待つと「掴めるまで遅い・追従がゆっくり」になる
        /// （2026-07-08 UX 指摘）ため、本人の画面ではローカル手 pose に直結して見た目だけ上書きする。
        /// 状態（holder）はサーバ権威のまま。サーバ上では Update の権威追従が走るので何もしない。
        /// </summary>
        public void ApplyLocalHoldPose(Vector3 rawPos, Quaternion rawRot)
        {
            if (IsServer) return; // 二重駆動防止（host 上のローカルプレイヤーはサーバ追従が正）
            transform.SetPositionAndRotation(ClampToSurface(rawPos), rawRot);
        }

        [ServerRpc(RequireOwnership = false)]
        public void RequestGrabServerRpc(byte hand, ServerRpcParams rpcParams = default)
        {
            ulong sender = rpcParams.Receive.SenderClientId;
            if (IsHeld) return; // 先着勝ち。敗者は無反応（すり抜け）
            if (IsStowed) return; // 非アクティブゲームのプロップは掴めない（GameSwitcher が stow 中）

            var seat = SeatLocator.FindByClient(sender); // 掴み時に1回だけ席を解決（以後 Update で使い回す）
            if (seat == null || !TryGetHandWorldPose(sender, hand, seat, out var handPos, out var handRot)) return;

            _holder.Value = sender;
            _holderHand.Value = hand;
            _grabSeat = seat;
            _untrackedSince = -1f;
            var inv = Quaternion.Inverse(handRot);
            _grabOffsetPos = inv * (transform.position - handPos);
            _grabOffsetRot = inv * transform.rotation;
            if (_rb != null)
            {
                _rb.isKinematic = true; // Held = kinematic 追従（Free ピースは押し退けられる）
            }
            _poseCount = 0;
            _poseHead = 0;
            Debug.Log($"[TableDuo] Grab {name} ← client{sender} hand{hand}");
            GrabLogged?.Invoke(name, sender, true);
        }

        /// <summary>サーバ側からの強制解放（BoardReset・運用リカバリ用）。保持中でなければ何もしない。</summary>
        public void ServerForceRelease(string reason)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || !IsHeld) return;
            ServerRelease(applyThrow: false, $"（強制解放: {reason}）");
        }

        [ServerRpc(RequireOwnership = false)]
        public void RequestReleaseServerRpc(ServerRpcParams rpcParams = default)
        {
            if (_holder.Value != rpcParams.Receive.SenderClientId) return;
            ServerRelease(applyThrow: true, "");
        }

        /// <summary>サーバ側の解放共通処理。applyThrow=true なら pose 履歴から投擲速度を引き継ぐ。</summary>
        private void ServerRelease(bool applyThrow, string logSuffix)
        {
            ulong holder = _holder.Value;
            _holder.Value = NoHolder;
            _holderHand.Value = 0;
            _grabSeat = null;
            _untrackedSince = -1f;
            ReleaseToPhysics(applyThrow);
            Debug.Log($"[TableDuo] Release {name}{logSuffix}");
            GrabLogged?.Invoke(name, holder, false);
        }

        /// <summary>Held → Free 遷移。Rigidbody を dynamic に戻し、必要なら推定速度を与える。</summary>
        private void ReleaseToPhysics(bool applyThrow)
        {
            if (_rb == null) return;
            _rb.isKinematic = false;
            _rb.WakeUp();
            if (applyThrow && TryEstimateReleaseVelocity(out var linear, out var angular))
            {
                _rb.velocity = linear;
                _rb.angularVelocity = angular;
            }
            else
            {
                _rb.velocity = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
            }
        }

        private void RecordPose(Vector3 pos, Quaternion rot)
        {
            _poseHistory[_poseHead] = (Time.time, pos, rot);
            _poseHead = (_poseHead + 1) % PoseHistoryCapacity;
            if (_poseCount < PoseHistoryCapacity) _poseCount++;
        }

        /// <summary>直近 VelocityWindow 秒の pose 履歴から線速度・角速度を推定（上限クランプ付き）。</summary>
        private bool TryEstimateReleaseVelocity(out Vector3 linear, out Vector3 angular)
        {
            linear = Vector3.zero;
            angular = Vector3.zero;
            if (_poseCount < 2) return false;

            var newest = _poseHistory[(_poseHead - 1 + PoseHistoryCapacity) % PoseHistoryCapacity];
            // 窓内で最も古いサンプルを探す（新しい方から遡る）
            var oldest = newest;
            for (int i = 2; i <= _poseCount; i++)
            {
                var e = _poseHistory[(_poseHead - i + PoseHistoryCapacity) % PoseHistoryCapacity];
                if (newest.t - e.t > VelocityWindow) break;
                oldest = e;
            }
            float dt = newest.t - oldest.t;
            if (dt < 0.02f) return false; // 1 サンプル相当以下では推定しない

            linear = (newest.p - oldest.p) / dt;
            if (linear.magnitude > MaxLinearSpeed) linear = linear.normalized * MaxLinearSpeed;

            var delta = newest.r * Quaternion.Inverse(oldest.r);
            delta.ToAngleAxis(out float angleDeg, out Vector3 axis);
            if (angleDeg > 180f) angleDeg -= 360f; // 最短弧
            if (!float.IsNaN(axis.x) && axis.sqrMagnitude > 0.5f)
            {
                angular = axis.normalized * (angleDeg * Mathf.Deg2Rad / dt);
                if (angular.magnitude > MaxAngularSpeed) angular = angular.normalized * MaxAngularSpeed;
            }
            return true;
        }

        /// <summary>卓外落下ピースを spawn 位置へ戻す（Free 状態のサーバのみ）。</summary>
        private void RespawnIfFallen()
        {
            if (_rb == null || float.IsNegativeInfinity(surfaceY)) return;
            if (transform.position.y >= surfaceY - FallRespawnDepth) return;
            _rb.velocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            _rb.position = _spawnPos;
            _rb.rotation = _spawnRot;
            transform.SetPositionAndRotation(_spawnPos, _spawnRot);
            Debug.Log($"[TableDuo] {name} 卓外落下 → spawn 位置へリスポーン");
        }

        private void Update()
        {
            if (!IsSpawned || !IsServer) return;
            if (!IsHeld)
            {
                RespawnIfFallen();
                return;
            }

            // 保持者の切断で宙に浮くのを防ぐ
            var nm = NetworkManager.Singleton;
            if (nm == null || _grabSeat == null || (!nm.ConnectedClients.ContainsKey(_holder.Value)))
            {
                ServerRelease(applyThrow: false, "（保持者切断）");
                return;
            }

            if (TryGetHandWorldPose(_holder.Value, _holderHand.Value, _grabSeat, out var handPos, out var handRot))
            {
                _untrackedSince = -1f;
                var rawPos = handPos + handRot * _grabOffsetPos;
                var rawRot = handRot * _grabOffsetRot;
                // 投擲速度はクランプ・平滑前の生ターゲットから推定する
                //（平滑 Lerp 後だと速度が減衰して「投げても落ちるだけ」になる）
                RecordPose(rawPos, rawRot);
                // 60Hz 受信 pose を描画フレームへ指数平滑（RemoteAvatarView.SmoothK と同じ τ）。
                // 生スナップだと host 画面で駒だけ段差ステップし、平滑済みのリモート手と噛み合わない
                float k = 1f - Mathf.Exp(-32f * Time.deltaTime);
                var targetPos = ClampToSurface(rawPos);
                transform.SetPositionAndRotation(
                    Vector3.Lerp(transform.position, targetPos, k),
                    Quaternion.Slerp(transform.rotation, rawRot, k));
            }
            else
            {
                // トラッキングロスト継続で自動解放（保持者は掴んだつもりでも手が消えている状態。
                // 短時間の瞬断では解放しない — その間オブジェクトは最終位置で静止）
                if (_untrackedSince < 0f) _untrackedSince = Time.time;
                else if (Time.time - _untrackedSince >= UntrackedReleaseSeconds)
                {
                    // 手が消えた状態からの解放なので投擲速度は与えない（履歴は古い）
                    ServerRelease(applyThrow: false,
                        $"（トラッキングロスト {UntrackedReleaseSeconds:F0}s 継続で自動解放）");
                }
            }
        }

        /// <summary>clientId の手のワールド姿勢（席アンカー × トラッキングスペース pose）。hand: 0=L, 1=R。</summary>
        private static bool TryGetHandWorldPose(ulong clientId, byte hand, Transform seat,
            out Vector3 pos, out Quaternion rot)
        {
            pos = default;
            rot = Quaternion.identity;
            var cm = ConnectionManager.Instance;
            if (cm == null || !cm.TryGetPose(clientId, out AvatarPose pose)) return false;

            bool tracked = hand == 0 ? pose.TrackedL : pose.TrackedR;
            if (!tracked) return false;
            var localPos = hand == 0 ? pose.WristPosL : pose.WristPosR;
            var localRot = hand == 0 ? pose.WristRotL : pose.WristRotR;
            pos = seat.TransformPoint(localPos);
            rot = seat.rotation * localRot;
            return true;
        }
    }
}
