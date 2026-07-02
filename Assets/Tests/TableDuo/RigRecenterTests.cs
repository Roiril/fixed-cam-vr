#nullable enable
using NUnit.Framework;
using TableDuoVr.Hands;
using UnityEngine;

namespace TableDuoVr.Tests
{
    public class RigRecenterTests
    {
        [Test]
        public void HeadToSeat_PutsHeadAtSeatPositionAndYaw()
        {
            // rig > trackingSpace > head の階層（head はリグの孫）
            var rig = new GameObject("rig").transform;
            var ts = new GameObject("ts").transform;
            ts.SetParent(rig);
            var head = new GameObject("head").transform;
            head.SetParent(ts);
            var seat = new GameObject("seat").transform;

            try
            {
                rig.SetPositionAndRotation(new Vector3(1f, 0f, 2f), Quaternion.Euler(0f, 30f, 0f));
                head.localPosition = new Vector3(0.3f, 1.6f, 0.1f);
                head.localRotation = Quaternion.Euler(8f, 40f, 0f); // pitch を入れて yaw のみ合うことを確認
                seat.SetPositionAndRotation(new Vector3(-2f, 1.15f, 3f), Quaternion.Euler(0f, 200f, 0f));

                float pitchBefore = head.eulerAngles.x;

                RigRecenter.HeadToSeat(rig, head, seat);

                Assert.Less(Vector3.Distance(head.position, seat.position), 1e-3f, "頭が席の位置に一致しない");
                Assert.Less(Mathf.Abs(Mathf.DeltaAngle(head.eulerAngles.y, seat.eulerAngles.y)), 0.2f,
                    "頭の yaw が席の向きに一致しない");
                // pitch は変えない（水平を保つ）
                Assert.Less(Mathf.Abs(Mathf.DeltaAngle(head.eulerAngles.x, pitchBefore)), 0.2f,
                    "yaw のみ合わせるはずが pitch が変わった");
            }
            finally
            {
                Object.DestroyImmediate(rig.gameObject);
                Object.DestroyImmediate(seat.gameObject);
            }
        }

        [Test]
        public void HeadToSeat_AlreadyAligned_IsNoOp()
        {
            var rig = new GameObject("rig").transform;
            var head = new GameObject("head").transform;
            head.SetParent(rig);
            var seat = new GameObject("seat").transform;
            try
            {
                rig.SetPositionAndRotation(new Vector3(0f, 0f, 0f), Quaternion.identity);
                head.localPosition = new Vector3(0f, 1.15f, 0f);
                head.localRotation = Quaternion.identity;
                seat.SetPositionAndRotation(new Vector3(0f, 1.15f, 0f), Quaternion.identity);

                var rigPosBefore = rig.position;

                RigRecenter.HeadToSeat(rig, head, seat);

                Assert.Less(Vector3.Distance(rig.position, rigPosBefore), 1e-4f, "整列済みなのにリグが動いた");
                Assert.Less(Vector3.Distance(head.position, seat.position), 1e-4f);
            }
            finally
            {
                Object.DestroyImmediate(rig.gameObject);
                Object.DestroyImmediate(seat.gameObject);
            }
        }
    }
}
