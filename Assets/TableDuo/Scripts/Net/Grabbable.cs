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
                transform.SetPositionAndRotation(
                    handPos + handRot * _grabOffsetPos,
                    handRot * _grabOffsetRot);
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
