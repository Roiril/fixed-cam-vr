#nullable enable
using FixedCamVr.Tracking;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Tracking.Tests
{
    /// <summary>
    /// PlayerZone.Contains の AABB / shrink (ヒステリシス) ロジックのテスト。
    /// </summary>
    public sealed class PlayerZoneTests
    {
        private readonly System.Collections.Generic.List<GameObject> _spawned = new();

        private PlayerZone MakeZone(Vector3 worldCenter, Vector3 halfExtents, int cameraIndex = 0, int priority = 0)
        {
            var go = new GameObject("TestZone");
            _spawned.Add(go);
            go.transform.position = worldCenter;
            var z = go.AddComponent<PlayerZone>();
            // private SerializeField を reflection でセット（既存 CameraSourceTests と同方式）
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            typeof(PlayerZone).GetField("halfExtents", F)!.SetValue(z, halfExtents);
            typeof(PlayerZone).GetField("centerOffset", F)!.SetValue(z, Vector3.zero);
            typeof(PlayerZone).GetField("cameraIndex", F)!.SetValue(z, cameraIndex);
            typeof(PlayerZone).GetField("priority", F)!.SetValue(z, priority);
            return z;
        }

        [TearDown]
        public void Cleanup()
        {
            foreach (var go in _spawned)
            {
                if (go != null) Object.DestroyImmediate(go);
            }
            _spawned.Clear();
        }

        [Test]
        public void Contains_PointAtCenter_True()
        {
            var z = MakeZone(new Vector3(0, 0, 0), new Vector3(1, 2, 1));
            Assert.That(z.Contains(new Vector3(0, 0, 0)), Is.True);
        }

        [Test]
        public void Contains_PointJustOutsideAabb_False()
        {
            var z = MakeZone(Vector3.zero, new Vector3(1, 2, 1));
            Assert.That(z.Contains(new Vector3(1.01f, 0, 0)), Is.False);
        }

        [Test]
        public void Contains_PointOnBoundary_True()
        {
            var z = MakeZone(Vector3.zero, new Vector3(1, 2, 1));
            // 境界 ( |dx| == hx ) は inclusive
            Assert.That(z.Contains(new Vector3(1f, 0, 0)), Is.True);
        }

        [Test]
        public void Contains_WithShrink_PointNearEdgeIsExcluded()
        {
            var z = MakeZone(Vector3.zero, new Vector3(1, 2, 1));
            // shrink 0.15 で hx は 0.85 になり、x=0.9 は外側扱い
            Assert.That(z.Contains(new Vector3(0.9f, 0, 0), shrink: 0.15f), Is.False);
            // 元の AABB では中
            Assert.That(z.Contains(new Vector3(0.9f, 0, 0), shrink: 0f), Is.True);
        }

        [Test]
        public void Contains_ShrinkLargerThanHalfExtent_ClampsToZero()
        {
            // halfExtent 0.1 の軸を shrink 1.0 で潰しても負値にならず、中心 0 は含まれる
            var z = MakeZone(Vector3.zero, new Vector3(0.1f, 2f, 1f));
            Assert.That(z.Contains(Vector3.zero, shrink: 1.0f), Is.True);
            Assert.That(z.Contains(new Vector3(0.05f, 0, 0), shrink: 1.0f), Is.False);
        }

        [Test]
        public void Contains_RespectsTransformPosition()
        {
            var z = MakeZone(new Vector3(10, 0, 0), new Vector3(1, 1, 1));
            Assert.That(z.Contains(new Vector3(10, 0, 0)), Is.True);
            Assert.That(z.Contains(Vector3.zero), Is.False);
        }
    }
}
