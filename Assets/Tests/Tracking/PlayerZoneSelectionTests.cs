#nullable enable
using System.Reflection;
using FixedCamVr.Tracking;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Tracking.Tests
{
    /// <summary>
    /// PlayerZoneTracker のゾーン選択ロジック (private Pick) を reflection 経由で検証する。
    /// MonoBehaviour 全体のシーン依存（registry / Update ループ）はテスト対象外。
    /// </summary>
    public sealed class PlayerZoneSelectionTests
    {
        private const BindingFlags BFI = BindingFlags.NonPublic | BindingFlags.Instance;

        private readonly System.Collections.Generic.List<GameObject> _spawned = new();

        private PlayerZone MakeZone(string name, Vector3 worldCenter, Vector3 halfExtents, int cameraIndex, int priority,
            Quaternion? rotation = null)
        {
            var go = new GameObject(name);
            _spawned.Add(go);
            go.transform.position = worldCenter;
            go.transform.rotation = rotation ?? Quaternion.identity;
            var z = go.AddComponent<PlayerZone>();
            typeof(PlayerZone).GetField("halfExtents", BFI)!.SetValue(z, halfExtents);
            typeof(PlayerZone).GetField("centerOffset", BFI)!.SetValue(z, Vector3.zero);
            typeof(PlayerZone).GetField("cameraIndex", BFI)!.SetValue(z, cameraIndex);
            typeof(PlayerZone).GetField("priority", BFI)!.SetValue(z, priority);
            return z;
        }

        private PlayerZoneTracker MakeTracker(PlayerZone[] zones, float hysteresisShrink = 0.15f, bool keepLastWhenOutside = true, PlayerZone? current = null)
        {
            var go = new GameObject("Tracker");
            _spawned.Add(go);
            var t = go.AddComponent<PlayerZoneTracker>();
            typeof(PlayerZoneTracker).GetField("zones", BFI)!.SetValue(t, zones);
            typeof(PlayerZoneTracker).GetField("hysteresisShrink", BFI)!.SetValue(t, hysteresisShrink);
            typeof(PlayerZoneTracker).GetField("keepLastWhenOutside", BFI)!.SetValue(t, keepLastWhenOutside);
            if (current != null)
            {
                typeof(PlayerZoneTracker).GetField("_current", BFI)!.SetValue(t, current);
            }
            return t;
        }

        private static PlayerZone? InvokePick(PlayerZoneTracker t, Vector3 head)
        {
            var m = typeof(PlayerZoneTracker).GetMethod("Pick", BFI)!;
            return (PlayerZone?)m.Invoke(t, new object[] { head });
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
        public void Pick_HysteresisBoundary_KeepsCurrentWhileWithinShrunkenAabb()
        {
            // halfExtent.x = 1.0, shrink = 0.15 → 縮小後 hx = 0.85
            // x = 0.9 は「元 AABB 内 / 縮小 AABB 外」
            var zoneA = MakeZone("A", Vector3.zero, new Vector3(1, 2, 1), cameraIndex: 0, priority: 0);
            var t = MakeTracker(new[] { zoneA }, hysteresisShrink: 0.15f, current: zoneA);

            // current は zoneA。head は元 AABB 内なので維持される（Pick の最初の if が成立する）
            // 注意: shrink 後の AABB 内のときに維持される設計なので、x=0.8 で確認する
            var picked = InvokePick(t, new Vector3(0.8f, 0, 0));
            Assert.That(picked, Is.SameAs(zoneA));
        }

        [Test]
        public void Pick_LeavesCurrentWhenOutsideShrunkenAabb_AndAnotherContains()
        {
            // current = zoneA だが head は zoneA の縮小 AABB 外。zoneB が包含する → zoneB に切替
            var zoneA = MakeZone("A", Vector3.zero, new Vector3(1, 2, 1), cameraIndex: 0, priority: 0);
            var zoneB = MakeZone("B", new Vector3(2.0f, 0, 0), new Vector3(1, 2, 1), cameraIndex: 1, priority: 0);
            var t = MakeTracker(new[] { zoneA, zoneB }, hysteresisShrink: 0.15f, current: zoneA);

            // x = 1.5 は zoneA の縮小 AABB (hx=0.85) 外、かつ zoneB (1〜3) 内
            var picked = InvokePick(t, new Vector3(1.5f, 0, 0));
            Assert.That(picked, Is.SameAs(zoneB));
        }

        [Test]
        public void Pick_OverlappingZones_HigherPriorityWins()
        {
            // 同じ位置を覆う 2 ゾーン。priority が高い方が選ばれる。
            var low = MakeZone("Low", Vector3.zero, new Vector3(2, 2, 2), cameraIndex: 0, priority: 0);
            var high = MakeZone("High", Vector3.zero, new Vector3(1, 1, 1), cameraIndex: 1, priority: 5);
            var t = MakeTracker(new[] { low, high }, current: null);

            var picked = InvokePick(t, Vector3.zero);
            Assert.That(picked, Is.SameAs(high));
        }

        [Test]
        public void Pick_OutsideAllZones_KeepsLastWhenFlagTrue()
        {
            var zoneA = MakeZone("A", Vector3.zero, new Vector3(1, 1, 1), cameraIndex: 0, priority: 0);
            var t = MakeTracker(new[] { zoneA }, keepLastWhenOutside: true, current: zoneA);

            // 全ゾーン外 (x = 100) かつ current の縮小 AABB 外 → 直近 (zoneA) を維持
            var picked = InvokePick(t, new Vector3(100, 0, 0));
            Assert.That(picked, Is.SameAs(zoneA));
        }

        [Test]
        public void Pick_OutsideAllZones_ReturnsNullWhenFlagFalse()
        {
            var zoneA = MakeZone("A", Vector3.zero, new Vector3(1, 1, 1), cameraIndex: 0, priority: 0);
            var t = MakeTracker(new[] { zoneA }, keepLastWhenOutside: false, current: zoneA);

            var picked = InvokePick(t, new Vector3(100, 0, 0));
            Assert.That(picked, Is.Null);
        }

        [Test]
        public void Pick_RotatedZone_UsesOrientedBounds()
        {
            // 長辺 x=1.0 / 短辺 z=0.5 のゾーン。点 (0,0,0.9) は無回転だと z=0.9 > 0.5 で外。
            // 90° yaw 回転すると長辺が world Z へ向くので、同じ点が OBB 内に入る。
            var rotated = MakeZone("Rot", Vector3.zero, new Vector3(1f, 2f, 0.5f),
                cameraIndex: 0, priority: 0, rotation: Quaternion.Euler(0f, 90f, 0f));
            var t = MakeTracker(new[] { rotated }, current: null);

            var picked = InvokePick(t, new Vector3(0f, 0f, 0.9f));
            Assert.That(picked, Is.SameAs(rotated), "90° 回転で長辺が world Z を向き、点が OBB 内に入るはず");

            // 同じ点を無回転ゾーンで判定すると外（OBB が AABB と同一に退化）
            var axisAligned = MakeZone("Aabb", Vector3.zero, new Vector3(1f, 2f, 0.5f),
                cameraIndex: 1, priority: 0);
            var t2 = MakeTracker(new[] { axisAligned }, keepLastWhenOutside: false, current: null);
            var picked2 = InvokePick(t2, new Vector3(0f, 0f, 0.9f));
            Assert.That(picked2, Is.Null, "無回転なら z=0.9 は短辺 0.5 の外");
        }

        [Test]
        public void Pick_NoCurrent_PicksOnlyContainingZone()
        {
            var zoneA = MakeZone("A", Vector3.zero, new Vector3(1, 1, 1), cameraIndex: 0, priority: 0);
            var zoneB = MakeZone("B", new Vector3(5, 0, 0), new Vector3(1, 1, 1), cameraIndex: 1, priority: 0);
            var t = MakeTracker(new[] { zoneA, zoneB }, current: null);

            var picked = InvokePick(t, new Vector3(5, 0, 0));
            Assert.That(picked, Is.SameAs(zoneB));
        }
    }
}
