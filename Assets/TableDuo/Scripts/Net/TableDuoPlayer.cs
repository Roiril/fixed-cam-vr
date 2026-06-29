#nullable enable
using TableDuoVr.Hands;
using Unity.Netcode;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// プレイヤーごとにスポーンされる NetworkBehaviour。
    /// - 役割（Full=フルアバター / Hand=手だけ）は StudyConfig.ForcedRole（tdv_role 起動フラグ）優先、
    ///   未指定なら従来規則（ホスト=Full / クライアント=Hand）。役割は NetworkVariable で全員に共有
    /// - 席は役割で決まる（Full=席0 / Hand=席1）
    /// - owner: ローカルリグを席にアラインし、IHandPoseSource の pose を 30Hz 送信。
    ///   Hand 役 + 片手モードなら左手を抑制（送信もローカル描画も）
    /// - リモート: 役割が判明した時点で席アンカー下に RemoteAvatarView を生成
    /// 自分の手はローカルトラッキング描画のまま（ネット往復させない — 要件 §4）。
    /// </summary>
    // HandPoseSampler（実行順 0）が LateUpdate で pose を採取した「後」に送信したい。
    // これで送る pose は今フレームの最新になる（Update 送信だと 1 フレ古い pose を送ってしまう）。
    [DefaultExecutionOrder(100)]
    public sealed class TableDuoPlayer : NetworkBehaviour
    {
        private const byte RoleUnset = 255;

        // 60Hz 送信。Quest のハンドトラッキングは ~60Hz なのでこれが採取レートに整合する。
        // 1 pose ≈489B × 60 ≈ 29KB/s/手 で LAN 上は無視できる帯域。NGO TickRate も 60 に揃える
        // （TableDuoSceneSetup / シーンの NetworkConfig.TickRate）こと — 送信が速くても tick で flush
        // 律速されると離散化遅延が残るため。
        [SerializeField] private float sendRate = 60f;

        private readonly NetworkVariable<byte> _role = new(
            RoleUnset, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        // 調査条件を全員へ共有（bit0=頭マーカー / bit1=片手モード）。owner が起動フラグから書く。
        // SessionLogger が clientId 別に記録するため（条件は端末ごとの起動フラグ＝host ローカル値では不正確）。
        private readonly NetworkVariable<byte> _studyFlags = new(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        /// <summary>owner の OS recenter をサーバへ報告した（study-validity: 座標系不連続のマーク用）。</summary>
        public static event System.Action<ulong>? RecenterReported;

        private RemoteAvatarView? _view;
        private PinchGrabInteractor? _interactor;
        private RecenterWatcher? _recenterWatcher;
        private System.Action? _onRecentered;
        private ControllerRecenterWatcher? _controllerRecenter;
        private System.Action? _onControllerRecenter;
        private IHandPoseSource? _source;
        private HandPoseSampler? _sampler;
        private Transform? _seat;
        private uint _seq;
        private float _nextSend;

        public int SeatIndex { get; private set; } = -1;

        /// <summary>確定済みの席アンカー（host 側で SessionLogger が参照。毎フレーム GameObject.Find を避ける）。</summary>
        public Transform? Seat => _seat;

        /// <summary>同期済みの役割。未確定なら null（SessionLogger が参照）。</summary>
        public StudyConfig.Role? Role =>
            _role.Value == RoleUnset ? null : (StudyConfig.Role)_role.Value;

        /// <summary>同期済みの調査条件（host の SessionLogger が clientId 別に記録）。</summary>
        public bool ShowHeadMarker => (_studyFlags.Value & 1) != 0;
        public bool OneHandMode => (_studyFlags.Value & 2) != 0;

        public override void OnNetworkSpawn()
        {
            if (IsOwner)
            {
                var role = StudyConfig.ForcedRole
                    ?? (OwnerClientId == NetworkManager.ServerClientId
                        ? StudyConfig.Role.Full
                        : StudyConfig.Role.Hand);
                _role.Value = (byte)role;
                _studyFlags.Value = (byte)((StudyConfig.ShowHeadMarker ? 1 : 0)
                    | (StudyConfig.OneHandMode ? 2 : 0));
                SetupOwner(role);
            }
            else if (_role.Value != RoleUnset)
            {
                SetupRemote((StudyConfig.Role)_role.Value);
            }
            else
            {
                _role.OnValueChanged += OnRoleSynced;
            }
        }

        public override void OnNetworkDespawn()
        {
            _role.OnValueChanged -= OnRoleSynced;
            if (_recenterWatcher != null && _onRecentered != null)
            {
                _recenterWatcher.Recentered -= _onRecentered;
            }
            if (_controllerRecenter != null && _onControllerRecenter != null)
            {
                _controllerRecenter.Recentered -= _onControllerRecenter;
            }
            if (ConnectionManager.Instance != null)
            {
                ConnectionManager.Instance.RemotePoseReceived -= OnRemotePose;
            }
            if (_view != null)
            {
                Destroy(_view.gameObject);
                _view = null;
            }
        }

        private void OnRoleSynced(byte _, byte current)
        {
            if (current == RoleUnset || IsOwner || _view != null) return;
            SetupRemote((StudyConfig.Role)current);
        }

        private void SetupOwner(StudyConfig.Role role)
        {
            SeatIndex = SeatIndexOf(role);
            var seat = SeatLocator.Find(SeatIndex);
            if (seat == null)
            {
                Debug.LogError($"[TableDuo] 席が見つかりません: Seat{SeatIndex}（Setup TableDuo Scene 未実行？）");
                return;
            }
            _seat = seat;
            _sampler = FindObjectOfType<HandPoseSampler>(); // 1 回だけ取得（recenter/片手で再 Find しない）
            Debug.Log($"[TableDuo] 自分の役割={role} 席={SeatIndex}");

            AlignLocalRig(seat);
            _nextSend = Time.time;

            if (role == StudyConfig.Role.Hand && StudyConfig.OneHandMode && _sampler != null)
            {
                _sampler.SuppressLeftHand = true;
                Debug.Log("[TableDuo] 片手モード: 左手を抑制");
            }

            var interactorGo = new GameObject("PinchGrabInteractor");
            interactorGo.transform.SetParent(transform, false);
            _interactor = interactorGo.AddComponent<PinchGrabInteractor>();
            _interactor.Initialize(seat, OwnerClientId);

            // OS recenter で席がズレたら即再アライン（要件 §6）+ サーバへ報告（座標系不連続のログ）
            _recenterWatcher = FindObjectOfType<RecenterWatcher>();
            if (_recenterWatcher != null)
            {
                var seatRef = seat;
                _onRecentered = () =>
                {
                    AlignLocalRig(seatRef);
                    if (IsSpawned) ReportRecenterServerRpc();
                };
                _recenterWatcher.Recentered += _onRecentered;
            }

            // 手動リセット: コントローラ両手グリップ長押し（人側/手側 両方・ジェスチャー不可）→ 頭を席へ戻す
            _controllerRecenter = FindObjectOfType<ControllerRecenterWatcher>();
            if (_controllerRecenter != null)
            {
                var seatRef = seat;
                _onControllerRecenter = () =>
                {
                    RecenterToSeat(seatRef);
                    if (IsSpawned) ReportRecenterServerRpc();
                };
                _controllerRecenter.Recentered += _onControllerRecenter;
            }
        }

        private void SetupRemote(StudyConfig.Role role)
        {
            SeatIndex = SeatIndexOf(role);
            var seat = SeatLocator.Find(SeatIndex);
            if (seat == null)
            {
                Debug.LogError($"[TableDuo] 席が見つかりません: Seat{SeatIndex}");
                return;
            }
            _seat = seat;
            Debug.Log($"[TableDuo] リモート(client{OwnerClientId}) 役割={role} 席={SeatIndex}");
            WarnIfSeatCollision();

            _view = RemoteAvatarView.Create(seat, handsOnly: role == StudyConfig.Role.Hand);
            if (ConnectionManager.Instance != null)
            {
                ConnectionManager.Instance.RemotePoseReceived += OnRemotePose;
            }
        }

        private void WarnIfSeatCollision()
        {
            foreach (var other in FindObjectsOfType<TableDuoPlayer>())
            {
                if (other != this && other.SeatIndex == SeatIndex)
                {
                    Debug.LogWarning($"[TableDuo] 役割が衝突しています（両者とも席{SeatIndex}）。tdv_role の指定を確認");
                }
            }
        }

        // 送信は LateUpdate（HandPoseSampler.LateUpdate の後＝今フレームの最新 pose を送る）。
        // DefaultExecutionOrder(100) で sampler（順 0）より後に回ることを保証している。
        private void LateUpdate()
        {
            if (!IsSpawned || !IsOwner) return;
            if (Time.time < _nextSend) return;
            // hitch（GC/ドメインリロード）後に Time.time が大きく進むと、毎フレーム +1/rate では
            // 追いつくまで毎フレ送信のバーストになる。次の境界へクランプして 30Hz を保つ
            _nextSend += 1f / sendRate;
            if (_nextSend < Time.time) _nextSend = Time.time + 1f / sendRate;

            _source ??= HandPoseSourceRegistry.Best;
            if (_source == null || !_source.IsValid)
            {
                _source = HandPoseSourceRegistry.Best; // Fake の有効化など差し替えに追従
                if (_source == null) return;
            }
            var pose = _source.Current;
            pose.Seq = unchecked(++_seq);
            pose.CaptureMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            ConnectionManager.Instance?.SubmitLocalPose(pose);
        }

        [ServerRpc]
        private void ReportRecenterServerRpc() => RecenterReported?.Invoke(OwnerClientId);

        private void OnRemotePose(ulong originClientId, AvatarPose pose)
        {
            if (originClientId != OwnerClientId || _view == null) return;
            _view.Apply(pose);
        }

        private static int SeatIndexOf(StudyConfig.Role role) =>
            role == StudyConfig.Role.Full ? 0 : 1;

        /// <summary>
        /// 手動リセット: 現在の頭の姿勢を席（初期目線アンカー）へ合わせる（yaw＋位置）。
        /// OS recenter と違い頭オフセットが残らないよう <see cref="RigRecenter.HeadToSeat"/> を使う。
        /// 頭が取れない L0 ではリグを席へ素直にアライン。
        /// </summary>
        private void RecenterToSeat(Transform seat)
        {
            var rig = _sampler != null ? _sampler.RigRoot : null;
            var head = _sampler != null ? _sampler.CenterEye : null;
            if (rig == null || head == null)
            {
                AlignLocalRig(seat);
                return;
            }
            RigRecenter.HeadToSeat(rig, head, seat);
            Debug.Log("[TableDuo] 手動リセット: 頭を席へ再センタ");
        }

        /// <summary>OVRCameraRig（あれば）を席へアライン。L0（リグ無し）は何もしない。</summary>
        private void AlignLocalRig(Transform seat)
        {
            var rig = _sampler != null ? _sampler.RigRoot : null;
            if (rig == null)
            {
                Debug.Log("[TableDuo] リグ無し（L0 モード想定）— 席アラインをスキップ");
                return;
            }
            rig.SetPositionAndRotation(seat.position, seat.rotation);
            Debug.Log($"[TableDuo] リグを {seat.name} へアライン");
        }
    }
}
