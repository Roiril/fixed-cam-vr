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

        [Header("卓上拘束（TableDuoSceneSetup が設定。未設定=拘束なし）")]
        [Tooltip("天板の上面 Y。掴み追従・解放時にピースの最下点がこれを下回らないようクランプする（テーブル貫通防止）")]
        [SerializeField] private float surfaceY = float.NegativeInfinity;
        [Tooltip("天板の XZ 中心。surfaceHalf と併せてピースが卓外へ消えないようクランプする")]
        [SerializeField] private Vector2 surfaceCenter;
        [Tooltip("天板の XZ 半径。(0,0)=XZ クランプなし")]
        [SerializeField] private Vector2 surfaceHalf;

        // ピースの pivot→最下点オフセット（クランプは「最下点が天板以上」で判定）。
        // 掴み中に回転すると変わるため、保持中は現在の bounds から都度計算する（spawn 時値は初期値のみ）
        private float _bottomOffset;
        private Renderer[] _renderers = System.Array.Empty<Renderer>();

        private void Awake()
        {
            _renderers = GetComponentsInChildren<Renderer>();
            _bottomOffset = CurrentBottomOffset();
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

        public bool IsHeldBy(ulong clientId, byte hand) =>
            _holder.Value == clientId && _holderHand.Value == hand;

        [ServerRpc(RequireOwnership = false)]
        public void RequestGrabServerRpc(byte hand, ServerRpcParams rpcParams = default)
        {
            ulong sender = rpcParams.Receive.SenderClientId;
            if (IsHeld) return; // 先着勝ち。敗者は無反応（すり抜け）

            var seat = SeatLocator.FindByClient(sender); // 掴み時に1回だけ席を解決（以後 Update で使い回す）
            if (seat == null || !TryGetHandWorldPose(sender, hand, seat, out var handPos, out var handRot)) return;

            _holder.Value = sender;
            _holderHand.Value = hand;
            _grabSeat = seat;
            _untrackedSince = -1f;
            var inv = Quaternion.Inverse(handRot);
            _grabOffsetPos = inv * (transform.position - handPos);
            _grabOffsetRot = inv * transform.rotation;
            Debug.Log($"[TableDuo] Grab {name} ← client{sender} hand{hand}");
            GrabLogged?.Invoke(name, sender, true);
        }

        /// <summary>サーバ側からの強制解放（BoardReset・運用リカバリ用）。保持中でなければ何もしない。</summary>
        public void ServerForceRelease(string reason)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || !IsHeld) return;
            ulong holder = _holder.Value;
            _holder.Value = NoHolder;
            _holderHand.Value = 0;
            _grabSeat = null;
            _untrackedSince = -1f;
            Debug.Log($"[TableDuo] Release {name}（強制解放: {reason}）");
            GrabLogged?.Invoke(name, holder, false);
        }

        [ServerRpc(RequireOwnership = false)]
        public void RequestReleaseServerRpc(ServerRpcParams rpcParams = default)
        {
            if (_holder.Value != rpcParams.Receive.SenderClientId) return;
            ulong holder = _holder.Value;
            _holder.Value = NoHolder;
            _holderHand.Value = 0;
            _grabSeat = null;
            Debug.Log($"[TableDuo] Release {name}");
            GrabLogged?.Invoke(name, holder, false);
        }

        private void Update()
        {
            if (!IsSpawned || !IsServer || !IsHeld) return;

            // 保持者の切断で宙に浮くのを防ぐ
            var nm = NetworkManager.Singleton;
            if (nm == null || _grabSeat == null || (!nm.ConnectedClients.ContainsKey(_holder.Value)))
            {
                _holder.Value = NoHolder;
                _holderHand.Value = 0;
                _grabSeat = null;
                return;
            }

            if (TryGetHandWorldPose(_holder.Value, _holderHand.Value, _grabSeat, out var handPos, out var handRot))
            {
                _untrackedSince = -1f;
                // 60Hz 受信 pose を描画フレームへ指数平滑（RemoteAvatarView.SmoothK と同じ τ）。
                // 生スナップだと host 画面で駒だけ段差ステップし、平滑済みのリモート手と噛み合わない
                float k = 1f - Mathf.Exp(-32f * Time.deltaTime);
                var targetPos = ClampToSurface(handPos + handRot * _grabOffsetPos);
                var targetRot = handRot * _grabOffsetRot;
                transform.SetPositionAndRotation(
                    Vector3.Lerp(transform.position, targetPos, k),
                    Quaternion.Slerp(transform.rotation, targetRot, k));
            }
            else
            {
                // トラッキングロスト継続で自動解放（保持者は掴んだつもりでも手が消えている状態。
                // 短時間の瞬断では解放しない — その間オブジェクトは最終位置で静止）
                if (_untrackedSince < 0f) _untrackedSince = Time.time;
                else if (Time.time - _untrackedSince >= UntrackedReleaseSeconds)
                {
                    ulong holder = _holder.Value;
                    _holder.Value = NoHolder;
                    _holderHand.Value = 0;
                    _grabSeat = null;
                    _untrackedSince = -1f;
                    Debug.Log($"[TableDuo] Release {name}（トラッキングロスト {UntrackedReleaseSeconds:F0}s 継続で自動解放）");
                    GrabLogged?.Invoke(name, holder, false);
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
