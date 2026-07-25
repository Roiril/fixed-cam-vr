#nullable enable
using System.Collections.Generic;
using FixedCamVr.Tracking;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Tracking.Tests
{
    /// <summary>
    /// ZonePickLogic（ゾーン選択の純ロジック）の検証。段 S1 で PlayerZoneTracker.Pick から抽出したもので、
    /// **Web シミュレータの JS ミラーと共有するセマンティクス**の正本。
    ///
    /// 併せて「PlayerZone.Contains（MonoBehaviour・Quaternion 版）と ZonePickLogic.Contains（yaw 版）が
    /// 実際に使う領域で一致する」ことも固定する。実装が 2 つある以上、機械照合しないと drift するため。
    /// </summary>
    public sealed class ZonePickLogicTests
    {
        private readonly List<Object> _spawned = new();

        [TearDown]
        public void Cleanup()
        {
            foreach (var o in _spawned) if (o != null) Object.DestroyImmediate(o);
            _spawned.Clear();
        }

        private static ZonePickLogic.Box Box(float cx, float cz, float hx, float hz, int cam, int prio = 0)
            => ZonePickLogic.Box.Aabb(cx, 1f, cz, hx, 2f, hz, cam, prio);

        // 点が箱の境界からどれだけ離れているか（各軸の余裕の最小値の絶対値）。0 に近いほど境界上。
        private static float BoundaryMargin(in ZonePickLogic.Box b, Vector3 p, float shrink)
        {
            float dx = p.x - b.CenterX, dy = p.y - b.CenterY, dz = p.z - b.CenterZ;
            float lx = dx * b.CosYaw - dz * b.SinYaw;
            float lz = dx * b.SinYaw + dz * b.CosYaw;
            float mx = Mathf.Max(0f, b.HalfX - shrink) - Mathf.Abs(lx);
            float my = Mathf.Max(0f, b.HalfY - shrink) - Mathf.Abs(dy);
            float mz = Mathf.Max(0f, b.HalfZ - shrink) - Mathf.Abs(lz);
            return Mathf.Abs(Mathf.Min(mx, Mathf.Min(my, mz)));
        }

        // ---- 包含判定 ----

        [Test]
        public void Contains_InsideAndOutside()
        {
            ZonePickLogic.Box b = Box(0f, 0f, 0.5f, 0.5f, 0);
            Assert.That(ZonePickLogic.Contains(b, 0f, 1f, 0f), Is.True);
            Assert.That(ZonePickLogic.Contains(b, 0.49f, 1f, 0.49f), Is.True);
            Assert.That(ZonePickLogic.Contains(b, 0.51f, 1f, 0f), Is.False);
            Assert.That(ZonePickLogic.Contains(b, 0f, 1f, 0.51f), Is.False);
        }

        [Test]
        public void Contains_BoundaryIsInclusive()
        {
            ZonePickLogic.Box b = Box(0f, 0f, 0.5f, 0.5f, 0);
            Assert.That(ZonePickLogic.Contains(b, 0.5f, 1f, 0.5f), Is.True, "境界ちょうどは含む（<=）");
        }

        [Test]
        public void Contains_ShrinkPullsEdgesInward()
        {
            ZonePickLogic.Box b = Box(0f, 0f, 0.5f, 0.5f, 0);
            Assert.That(ZonePickLogic.Contains(b, 0.45f, 1f, 0f, shrink: 0.1f), Is.False);
            Assert.That(ZonePickLogic.Contains(b, 0.35f, 1f, 0f, shrink: 0.1f), Is.True);
        }

        [Test]
        public void Contains_ShrinkLargerThanHalfExtent_ClampsToZero()
        {
            ZonePickLogic.Box b = Box(0f, 0f, 0.5f, 0.5f, 0);
            Assert.That(ZonePickLogic.Contains(b, 0f, 1f, 0f, shrink: 5f), Is.True, "中心は残る");
            Assert.That(ZonePickLogic.Contains(b, 0.01f, 1f, 0f, shrink: 5f), Is.False);
        }

        [Test]
        public void Contains_YawRotatesTheBox()
        {
            // 1.0 x 0.2 の細長い箱を 90° 回すと、長辺が Z 方向になる。
            var b = ZonePickLogic.Box.WithYaw(0f, 1f, 0f, 1f, 2f, 0.1f, 90f, 0);
            Assert.That(ZonePickLogic.Contains(b, 0f, 1f, 0.9f), Is.True, "回転後は Z に長い");
            Assert.That(ZonePickLogic.Contains(b, 0.9f, 1f, 0f), Is.False, "X には短い");
        }

        // ---- 選択（ヒステリシス / 優先度 / タイブレーク） ----

        [Test]
        public void Pick_KeepsCurrentWhileInsideShrunkBox()
        {
            var boxes = new[] { Box(-0.5f, 0f, 0.6f, 2f, 0), Box(0.5f, 0f, 0.6f, 2f, 1) };
            // 重なり帯（x=0 付近）に居るとき、直近ゾーン 0 を維持する。
            Assert.That(ZonePickLogic.Pick(boxes, 0.02f, 1f, 0f, currentIndex: 0,
                hysteresisShrink: 0.1f, keepLastWhenOutside: true), Is.EqualTo(0));
        }

        [Test]
        public void Pick_LeavingShrunkBox_ButStillInsideFullBox_StaysOnFirstInArray()
        {
            // 重要な実挙動: shrink 後の箱から出ても、full 判定で現ゾーンがまだ含んでいて
            // 優先度も同じなら「配列の先頭が勝つ」ので現ゾーンに留まる。
            // ヒステリシス幅は shrink だけでなく **重なり帯の広さとの組み合わせ**で決まる
            // （unity-vr.md「hysteresisShrink は halfExtents の 20〜30%」の根拠）。
            var boxes = new[] { Box(-0.5f, 0f, 0.6f, 2f, 0), Box(0.5f, 0f, 0.6f, 2f, 1) };
            Assert.That(ZonePickLogic.Pick(boxes, 0.09f, 1f, 0f, currentIndex: 0,
                hysteresisShrink: 0.2f, keepLastWhenOutside: true), Is.EqualTo(0));
        }

        [Test]
        public void Pick_SwitchesWhenLeavingFullBox()
        {
            // x=0.15 は box0 の full（x<=0.1）の外・box1 の中 → 切り替わる。
            var boxes = new[] { Box(-0.5f, 0f, 0.6f, 2f, 0), Box(0.5f, 0f, 0.6f, 2f, 1) };
            Assert.That(ZonePickLogic.Pick(boxes, 0.15f, 1f, 0f, currentIndex: 0,
                hysteresisShrink: 0.2f, keepLastWhenOutside: true), Is.EqualTo(1));
        }

        [Test]
        public void Pick_HigherPriorityWins()
        {
            var boxes = new[] { Box(0f, 0f, 1f, 1f, 0, prio: 0), Box(0f, 0f, 1f, 1f, 1, prio: 5) };
            Assert.That(ZonePickLogic.Pick(boxes, 0f, 1f, 0f, -1, 0f, true), Is.EqualTo(1));
        }

        [Test]
        public void Pick_SamePriority_FirstInArrayWins()
        {
            var boxes = new[] { Box(0f, 0f, 1f, 1f, 0), Box(0f, 0f, 1f, 1f, 1) };
            Assert.That(ZonePickLogic.Pick(boxes, 0f, 1f, 0f, -1, 0f, true), Is.EqualTo(0),
                "同優先度は配列の先頭が勝つ（厳密比較 > のため）");
        }

        [Test]
        public void Pick_OutsideAll_KeepsLastOrReturnsNone()
        {
            var boxes = new[] { Box(0f, 0f, 0.5f, 0.5f, 0) };
            Assert.That(ZonePickLogic.Pick(boxes, 9f, 1f, 9f, currentIndex: 0,
                hysteresisShrink: 0f, keepLastWhenOutside: true), Is.EqualTo(0));
            Assert.That(ZonePickLogic.Pick(boxes, 9f, 1f, 9f, currentIndex: 0,
                hysteresisShrink: 0f, keepLastWhenOutside: false), Is.EqualTo(-1));
        }

        [Test]
        public void Pick_EmptyOrNull_IsSafe()
        {
            Assert.That(ZonePickLogic.Pick(null!, 0f, 0f, 0f, -1, 0f, true), Is.EqualTo(-1));
            Assert.That(ZonePickLogic.Pick(new ZonePickLogic.Box[0], 0f, 0f, 0f, -1, 0f, true), Is.EqualTo(-1));
        }

        [Test]
        public void Pick_CurrentIndexOutOfRange_IsIgnored()
        {
            var boxes = new[] { Box(0f, 0f, 1f, 1f, 0) };
            Assert.That(ZonePickLogic.Pick(boxes, 0f, 1f, 0f, currentIndex: 99, hysteresisShrink: 0f,
                keepLastWhenOutside: true), Is.EqualTo(0));
        }

        // ---- MonoBehaviour 実装との一致（実装が 2 つある以上、機械照合しないと drift する） ----

        [Test]
        public void Contains_AgreesWithPlayerZone_AcrossYawAndPositions()
        {
            foreach (float yaw in new[] { 0f, 15f, 45f, 90f, 180f, 270f, -30f })
            {
                var go = new GameObject($"Zone{yaw}");
                _spawned.Add(go);
                go.transform.position = new Vector3(0.3f, 1f, -0.2f);
                go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                var pz = go.AddComponent<PlayerZone>();
                typeof(PlayerZone).GetField("halfExtents",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .SetValue(pz, new Vector3(0.55f, 2f, 0.35f));

                Vector3 c = pz.Center, h = pz.HalfExtents;
                var box = ZonePickLogic.Box.WithYaw(c.x, c.y, c.z, h.x, h.y, h.z,
                    pz.Rotation.eulerAngles.y, pz.CameraIndex, pz.Priority);

                for (float x = -1.4f; x <= 1.4f; x += 0.17f)
                    for (float z = -1.4f; z <= 1.4f; z += 0.17f)
                        foreach (float shrink in new[] { 0f, 0.12f })
                        {
                            var p = new Vector3(x, 1f, z);
                            bool mono = pz.Contains(p, shrink);
                            bool pure = ZonePickLogic.Contains(box, p.x, p.y, p.z, shrink);
                            if (pure == mono) continue;
                            // 境界ちょうど（±1mm）は float の丸めで割れる。Quaternion 経由（mono）と
                            // cos/sin 直（pure）で最下位ビットが違うだけなのでセマンティクスの drift ではない。
                            // 判定を分けているのは境界からの距離なので、それが十分あるときだけ失敗させる。
                            Assert.That(BoundaryMargin(box, p, shrink), Is.LessThan(1e-3f),
                                $"yaw={yaw} pos=({x:F2},{z:F2}) shrink={shrink} で判定が食い違う"
                                + "（境界から十分離れているので実装の drift）");
                        }
            }
        }
    }
}
