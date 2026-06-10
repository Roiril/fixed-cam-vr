#nullable enable
using TableDuoVr.Hands;
using Unity.Netcode;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// プレイヤーごとにスポーンされる NetworkBehaviour。
    /// - 席割当: ホスト（ServerClientId）= 席0（フルアバター）/ クライアント = 席1（手だけ）
    /// - owner: ローカルリグを席にアラインし、IHandPoseSource の pose を 30Hz 送信
    /// - リモート: 席アンカー下に RemoteAvatarView を生成し受信 pose を適用
    /// 自分の手はローカルトラッキング描画のまま（ネット往復させない — 要件 §4）。
    /// </summary>
    public sealed class TableDuoPlayer : NetworkBehaviour
    {
        [SerializeField] private float sendRate = 30f;

        private RemoteAvatarView? _view;
        private PinchGrabInteractor? _interactor;
        private IHandPoseSource? _source;
        private float _nextSend;
        private float _lastPoseAt = -1f;

        public int SeatIndex { get; private set; }

        public override void OnNetworkSpawn()
        {
            SeatIndex = SeatLocator.SeatIndexOf(OwnerClientId);
            var seat = SeatLocator.Find(SeatIndex);
            if (seat == null)
            {
                Debug.LogError($"[TableDuo] 席が見つかりません: Seat{SeatIndex}（Setup TableDuo Scene 未実行？）");
                return;
            }

            if (IsOwner)
            {
                AlignLocalRig(seat);
                _nextSend = Time.time;

                var interactorGo = new GameObject("PinchGrabInteractor");
                interactorGo.transform.SetParent(transform, false);
                _interactor = interactorGo.AddComponent<PinchGrabInteractor>();
                _interactor.Initialize(seat, OwnerClientId);
            }
            else
            {
                bool handsOnly = SeatIndex == 1;
                _view = RemoteAvatarView.Create(seat, handsOnly);
                if (ConnectionManager.Instance != null)
                {
                    ConnectionManager.Instance.RemotePoseReceived += OnRemotePose;
                }
            }
        }

        public override void OnNetworkDespawn()
        {
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
            _lastPoseAt = Time.time;
            _view.Apply(pose);
        }

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
