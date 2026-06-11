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
    public sealed class TableDuoPlayer : NetworkBehaviour
    {
        private const byte RoleUnset = 255;

        [SerializeField] private float sendRate = 30f;

        private readonly NetworkVariable<byte> _role = new(
            RoleUnset, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        private RemoteAvatarView? _view;
        private PinchGrabInteractor? _interactor;
        private RecenterWatcher? _recenterWatcher;
        private System.Action? _onRecentered;
        private IHandPoseSource? _source;
        private float _nextSend;

        public int SeatIndex { get; private set; } = -1;

        /// <summary>同期済みの役割。未確定なら null（SessionLogger が参照）。</summary>
        public StudyConfig.Role? Role =>
            _role.Value == RoleUnset ? null : (StudyConfig.Role)_role.Value;

        public override void OnNetworkSpawn()
        {
            if (IsOwner)
            {
                var role = StudyConfig.ForcedRole
                    ?? (OwnerClientId == NetworkManager.ServerClientId
                        ? StudyConfig.Role.Full
                        : StudyConfig.Role.Hand);
                _role.Value = (byte)role;
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
            Debug.Log($"[TableDuo] 自分の役割={role} 席={SeatIndex}");

            AlignLocalRig(seat);
            _nextSend = Time.time;

            if (role == StudyConfig.Role.Hand && StudyConfig.OneHandMode)
            {
                var sampler = FindObjectOfType<HandPoseSampler>();
                if (sampler != null)
                {
                    sampler.SuppressLeftHand = true;
                    Debug.Log("[TableDuo] 片手モード: 左手を抑制");
                }
            }

            var interactorGo = new GameObject("PinchGrabInteractor");
            interactorGo.transform.SetParent(transform, false);
            _interactor = interactorGo.AddComponent<PinchGrabInteractor>();
            _interactor.Initialize(seat, OwnerClientId);

            // OS recenter で席がズレたら即再アライン（要件 §6）
            _recenterWatcher = FindObjectOfType<RecenterWatcher>();
            if (_recenterWatcher != null)
            {
                var seatRef = seat;
                _onRecentered = () => AlignLocalRig(seatRef);
                _recenterWatcher.Recentered += _onRecentered;
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

        private void Update()
        {
            if (!IsSpawned || !IsOwner) return;
            if (Time.time < _nextSend) return;
            _nextSend += 1f / sendRate;

            _source ??= HandPoseSourceRegistry.Best;
            if (_source == null || !_source.IsValid)
            {
                _source = HandPoseSourceRegistry.Best; // Fake の有効化など差し替えに追従
                if (_source == null) return;
            }
            ConnectionManager.Instance?.SubmitLocalPose(_source.Current);
        }

        private void OnRemotePose(ulong originClientId, AvatarPose pose)
        {
            if (originClientId != OwnerClientId || _view == null) return;
            _view.Apply(pose);
        }

        private static int SeatIndexOf(StudyConfig.Role role) =>
            role == StudyConfig.Role.Full ? 0 : 1;

        /// <summary>OVRCameraRig（あれば）を席へアライン。L0（リグ無し）は何もしない。</summary>
        private static void AlignLocalRig(Transform seat)
        {
            var sampler = FindObjectOfType<HandPoseSampler>();
            var rig = sampler != null ? sampler.RigRoot : null;
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
