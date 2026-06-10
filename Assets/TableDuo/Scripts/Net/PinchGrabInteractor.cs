#nullable enable
using TableDuoVr.Hands;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// ローカルプレイヤーのピンチ → Grabbable への掴み要求。
    /// TableDuoPlayer(owner) が生成する。ピンチ立ち上がりで最寄りの未保持 Grabbable に
    /// GrabRequest、立ち下がりで Release。裁定はサーバ（Grabbable 側）。
    /// </summary>
    public sealed class PinchGrabInteractor : MonoBehaviour
    {
        private const float GrabRadius = 0.18f;

        private Transform? _seat;
        private ulong _localClientId;
        private Grabbable[] _grabbables = System.Array.Empty<Grabbable>();
        private readonly bool[] _wasPinching = new bool[2];
        private readonly Grabbable?[] _heldCandidate = new Grabbable?[2];

        public void Initialize(Transform seat, ulong localClientId)
        {
            _seat = seat;
            _localClientId = localClientId;
            _grabbables = FindObjectsOfType<Grabbable>();
        }

        private void Update()
        {
            if (_seat == null) return;
            var src = HandPoseSourceRegistry.Best;
            if (src == null || !src.IsValid) return;
            var pose = src.Current;

            Tick(0, pose.TrackedL && pose.PinchL, pose.WristPosL);
            Tick(1, pose.TrackedR && pose.PinchR, pose.WristPosR);
        }

        private void Tick(int hand, bool pinching, Vector3 wristLocal)
        {
            if (pinching && !_wasPinching[hand])
            {
                var target = FindNearestFree(_seat!.TransformPoint(wristLocal));
                if (target != null)
                {
                    target.RequestGrabServerRpc((byte)hand);
                    _heldCandidate[hand] = target;
                }
            }
            else if (!pinching && _wasPinching[hand])
            {
                var held = _heldCandidate[hand];
                if (held != null && held.IsHeldBy(_localClientId, (byte)hand))
                {
                    held.RequestReleaseServerRpc();
                }
                _heldCandidate[hand] = null;
            }
            _wasPinching[hand] = pinching;
        }

        private Grabbable? FindNearestFree(Vector3 worldPos)
        {
            Grabbable? best = null;
            float bestSqr = GrabRadius * GrabRadius;
            foreach (var g in _grabbables)
            {
                if (g == null || g.IsHeld) continue;
                float sqr = (g.transform.position - worldPos).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = g;
                }
            }
            return best;
        }
    }
}
