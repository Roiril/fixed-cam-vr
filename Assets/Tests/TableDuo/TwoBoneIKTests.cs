#nullable enable
using NUnit.Framework;
using TableDuoVr.Net;
using UnityEngine;

namespace TableDuoVr.Tests
{
    public class TwoBoneIKTests
    {
        [Test]
        public void Solve_ReachableTarget_PlacesEndAtTarget()
        {
            // a > b > c の 3 ボーン鎖（各 0.25m・軽く曲げて縮退回避）
            var a = new GameObject("a").transform;
            var b = new GameObject("b").transform;
            var c = new GameObject("c").transform;
            b.SetParent(a); c.SetParent(b);
            try
            {
                a.position = Vector3.zero;
                b.localPosition = new Vector3(0f, -0.25f, 0f);
                c.localPosition = new Vector3(0.03f, -0.25f, 0f); // わずかに曲げる

                var target = new Vector3(0.30f, -0.20f, 0.10f); // 到達可能（距離 ~0.37 < 0.5）
                var pole = new Vector3(0.3f, -0.2f, -0.5f);

                for (int i = 0; i < 3; i++) TwoBoneIK.Solve(a, b, c, target, pole); // 数回で収束

                Assert.Less(Vector3.Distance(c.position, target), 0.01f,
                    $"手先がターゲットに届かない: {c.position} vs {target}");
            }
            finally
            {
                Object.DestroyImmediate(a.gameObject);
            }
        }

        [Test]
        public void Solve_OverreachTarget_ExtendsTowardTarget()
        {
            var a = new GameObject("a").transform;
            var b = new GameObject("b").transform;
            var c = new GameObject("c").transform;
            b.SetParent(a); c.SetParent(b);
            try
            {
                a.position = Vector3.zero;
                b.localPosition = new Vector3(0f, -0.25f, 0f);
                c.localPosition = new Vector3(0.03f, -0.25f, 0f);

                var target = new Vector3(0f, -2f, 0f); // 届かない（距離 2 >> 0.5）
                var pole = new Vector3(0f, -1f, -0.5f);

                for (int i = 0; i < 3; i++) TwoBoneIK.Solve(a, b, c, target, pole);

                // 腕は伸び切り、手先はターゲット方向（ほぼ真下）に来る
                Assert.Greater(Vector3.Dot((c.position - a.position).normalized, Vector3.down), 0.9f,
                    "届かない時に腕がターゲット方向へ伸びていない");
                Assert.Greater(Vector3.Distance(a.position, c.position), 0.45f, "腕が伸び切っていない");
            }
            finally
            {
                Object.DestroyImmediate(a.gameObject);
            }
        }
    }
}
