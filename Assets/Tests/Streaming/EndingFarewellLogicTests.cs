#nullable enable
using NUnit.Framework;
using UnityEngine;
using Phase = FixedCamVr.Streaming.EndingFarewellLogic.Phase;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>帰還の別れの人形（LEDGER 0291）の時刻表・段・立ち位置。</summary>
    public sealed class EndingFarewellLogicTests
    {
        private const float Dt = 1f / 90f;

        // ---- 時刻表 -------------------------------------------------------------------

        [Test]
        public void Timeline_LeavesSixSecondsToRetreatAndFadesDuringMotion()
        {
            Assert.That(EndingFarewellLogic.RetreatSec, Is.EqualTo(6f));
            Assert.That(EndingFarewellLogic.FadeOutSec, Is.EqualTo(4.5f));
            Assert.That(EndingFarewellLogic.FadeOutSec, Is.LessThan(EndingFarewellLogic.RetreatSec));
        }

        [Test]
        public void SampleAt_AppearsFacesTurnsThenRetreatsAndFades()
        {
            var s0 = EndingFarewellLogic.SampleAt(0f);
            Assert.That(s0.phase, Is.EqualTo(Phase.Appear));
            Assert.That(s0.alpha, Is.EqualTo(0f).Within(1e-4f), "現れる瞬間は見えない（ぱっと出ない）");

            float faceMid = EndingFarewellLogic.AppearSec + EndingFarewellLogic.FaceSec * 0.5f;
            var face = EndingFarewellLogic.SampleAt(faceMid);
            Assert.That(face.phase, Is.EqualTo(Phase.Face));
            Assert.That(face.Facing, Is.True);
            Assert.That(face.alpha, Is.EqualTo(1f));
            Assert.That(face.turn01, Is.EqualTo(0f), "見つめている間は回らない");
            Assert.That(face.retreatM, Is.EqualTo(0f), "見つめている間は動かない");

            float turnMid = EndingFarewellLogic.AppearSec + EndingFarewellLogic.FaceSec
                            + EndingFarewellLogic.TurnSec * 0.5f;
            var turn = EndingFarewellLogic.SampleAt(turnMid);
            Assert.That(turn.phase, Is.EqualTo(Phase.Turn));
            Assert.That(turn.turn01, Is.InRange(0.3f, 0.7f));
            Assert.That(turn.retreatM, Is.EqualTo(0f), "回り切るまで下がらない");

            float end = EndingFarewellLogic.TotalSec - 1e-3f;
            var last = EndingFarewellLogic.SampleAt(end);
            Assert.That(last.phase, Is.EqualTo(Phase.Retreat));
            Assert.That(last.alpha, Is.LessThan(0.01f), "消え切ってから終わる");
            Assert.That(last.retreatM, Is.EqualTo(EndingFarewellLogic.RetreatM).Within(0.01f));
            Assert.That(EndingFarewellLogic.SampleAt(EndingFarewellLogic.TotalSec).phase, Is.EqualTo(Phase.Done));
        }

        [Test]
        public void RetreatParameter_IsMonotonic()
        {
            float start = EndingFarewellLogic.AppearSec + EndingFarewellLogic.FaceSec + EndingFarewellLogic.TurnSec;
            float prev = 0f;
            for (float t = start; t < EndingFarewellLogic.TotalSec; t += Dt)
            {
                float d = EndingFarewellLogic.SampleAt(t).retreatM;
                Assert.That(d, Is.GreaterThanOrEqualTo(prev - 1e-5f));
                prev = d;
            }
        }

        // ---- 段 -------------------------------------------------------------------------

        private static EndingFarewellLogic.Step Run(EndingFarewellLogic l, bool active = true, bool ready = true,
                                                     bool cgFree = true, bool accepted = true,
                                                     bool anchored = true, bool showing = true)
            => l.Tick(Dt, active, ready, cgFree, accepted, anchored, showing);

        [Test]
        public void WaitsForTheSwapToFinish_ThenBeginsOnce()
        {
            var l = new EndingFarewellLogic();
            Assert.That(Run(l, ready: false).begin, Is.False, "報告前・入れ替わり中は出さない");
            Assert.That(Run(l, cgFree: false).begin, Is.False, "入れ替わりの人形が消え切るまで出さない");
            Assert.That(Run(l).begin, Is.True);
            Assert.That(l.Current, Is.EqualTo(Phase.Anchoring));
            Assert.That(Run(l).begin, Is.False, "2 回は出さない");
            Assert.That(l.Current, Is.EqualTo(Phase.Appear));
        }

        [Test]
        public void RunsTheWholeTimeline_AndHidesAtTheEnd()
        {
            var l = new EndingFarewellLogic();
            Run(l);   // begin
            bool sawFace = false, sawHide = false;
            for (int i = 0; i < (int)((EndingFarewellLogic.TotalSec + 1f) / Dt); i++)
            {
                var s = Run(l);
                if (s.sample.Facing) l.NotifyShotCaptured();
                sawFace |= s.sample.Facing;
                sawHide |= s.hide;
                if (l.Current == Phase.Done) break;
            }
            Assert.That(sawFace, Is.True);
            Assert.That(sawHide, Is.True, "消え切った縁で CG 層を畳む");
            Assert.That(l.Current, Is.EqualTo(Phase.Done));
            Assert.That(Run(l).begin, Is.False, "同じ体験者で 2 度出さない");
            Assert.That(l.AllowsPlainReleasedShot, Is.False, "消えた後に人形なしの写真で上書きしない");
        }

        [Test]
        public void Unavailable_WhenTheLayerRefuses_AllowsThePlainShot()
        {
            var l = new EndingFarewellLogic();
            Run(l);
            Run(l, accepted: false);
            Assert.That(l.Current, Is.EqualTo(Phase.Unavailable));
            Assert.That(l.AllowsPlainReleasedShot, Is.True);
        }

        [Test]
        public void Unavailable_WhenTheAnchorNeverResolves()
        {
            var l = new EndingFarewellLogic();
            Run(l);
            bool hid = false;
            for (int i = 0; i < 90; i++) hid |= Run(l, anchored: false).hide;
            Assert.That(l.Current, Is.EqualTo(Phase.Unavailable));
            Assert.That(hid, Is.True, "出しかけた人形を畳む");
        }

        [Test]
        public void EndingTheTake_HidesAndResets()
        {
            var l = new EndingFarewellLogic();
            Run(l);
            Run(l);
            Assert.That(l.Showing, Is.True);
            var s = Run(l, active: false);
            Assert.That(s.hide, Is.True);
            Assert.That(l.Current, Is.EqualTo(Phase.Idle));
        }

        [Test]
        public void SomeoneElseHidingTheLayer_RetriesInsteadOfClaimingCompletion()
        {
            var l = new EndingFarewellLogic();
            Run(l);
            Run(l);
            Run(l, showing: false);
            Assert.That(l.Current, Is.EqualTo(Phase.Unavailable));
        }

        // ---- 立ち位置 -------------------------------------------------------------------

        [Test]
        public void Face_WaitsForThePhotoBeforeTurning()
        {
            var l = new EndingFarewellLogic();
            Run(l);
            Run(l);
            for (int i = 0; i < 900; i++) Run(l);
            Assert.That(l.Current, Is.EqualTo(Phase.Face));
            Assert.That(l.ElapsedSec, Is.LessThan(EndingFarewellLogic.AppearSec + EndingFarewellLogic.FaceSec));
            l.NotifyShotCaptured();
            Assert.That(Run(l).sample.phase, Is.EqualTo(Phase.Turn));
        }

        [Test]
        public void WallAnchor_FitsTheVisitorWithWallClearanceAndRequiresMutualFacing()
        {
            Camera cam = MakeEndingCamera(out GameObject go);
            cam.transform.position += Vector3.up * 0.12f;
            try
            {
                Vector3 corner = new Vector3(-0.5f, 0.12f, 0.5f);
                Assert.That(EndingFarewellLogic.TrySolveAtWall(cam, Vector2.one, corner,
                    corner + Vector3.right * 0.955f, corner + Vector3.back * 0.955f, out var a), Is.True);
                Assert.That(a.foot.y, Is.EqualTo(0.12f));
                Assert.That(a.visitorTargetFoot.y, Is.EqualTo(0.12f));
                Assert.That(a.dollUv.x, Is.LessThan(a.visitorUv.x));
                foreach (Vector3 point in new[] { a.visitorTargetFoot, a.visitorTargetFoot + Vector3.up * 1.65f })
                {
                    Vector3 uv = cam.WorldToViewportPoint(point);
                    Assert.That(uv.x, Is.InRange(EndingFarewellLogic.PhotoFrameMargin, 1f - EndingFarewellLogic.PhotoFrameMargin), "頭と足が画面に入る");
                    Assert.That(uv.y, Is.InRange(EndingFarewellLogic.PhotoFrameMargin, 1f - EndingFarewellLogic.PhotoFrameMargin), "頭と足が画面に入る");
                }
                Vector3 horizontal = corner + Vector3.right * Mathf.Clamp(a.visitorTargetFoot.x - corner.x, 0f, 0.955f);
                Vector3 vertical = corner + Vector3.back * Mathf.Clamp(corner.z - a.visitorTargetFoot.z, 0f, 0.955f);
                float clearance = Mathf.Min(Vector3.Distance(a.visitorTargetFoot, horizontal),
                    Vector3.Distance(a.visitorTargetFoot, vertical)) - 0.02f;
                Assert.That(clearance, Is.GreaterThanOrEqualTo(EndingFarewellLogic.PhotoWallClearanceM - 1e-5f));
                float visitorYaw = EndingFarewellLogic.FaceYawDeg(a.visitorTargetFoot, a.foot);
                float dollYaw = EndingFarewellLogic.FaceYawDeg(a.foot, a.visitorTargetFoot);
                Assert.That(EndingFarewellLogic.IsPhotoReady(a, a.visitorTargetFoot, visitorYaw, dollYaw), Is.True);
                Assert.That(EndingFarewellLogic.IsPhotoReady(a, a.visitorTargetFoot + Vector3.right,
                    visitorYaw, dollYaw), Is.False);
                Assert.That(EndingFarewellLogic.IsPhotoReady(a, a.visitorTargetFoot,
                    visitorYaw + 90f, dollYaw), Is.False);
                Assert.That(EndingFarewellLogic.IsPhotoReady(a, a.visitorTargetFoot,
                    visitorYaw, dollYaw + 90f), Is.False);
                Vector3 end = a.foot + a.retreatDir * EndingFarewellLogic.RetreatM;
                Assert.That(end.x, Is.GreaterThan(corner.x));
                Assert.That(end.z, Is.LessThan(corner.z));
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Photo_RefusesAPositionWhoseBodyCrossesTheWall()
        {
            var a = new EndingFarewellLogic.Anchor
            {
                foot = new Vector3(0.5f, 0f, 3.5f), visitorTargetFoot = new Vector3(0.1f, 0f, 3.5f),
                wallCorner = new Vector3(0f, 0f, 4f), wallEndA = new Vector3(0.955f, 0f, 4f),
                wallEndB = new Vector3(0f, 0f, 3.045f), visitorHeightM = 1.65f, visitorRadiusM = 0.354f,
                wallHalfThicknessM = 0.02f,
            };
            Assert.That(EndingFarewellLogic.IsPhotoReady(a, a.visitorTargetFoot, 90f, -90f), Is.False,
                "足元は壁の内側でも、体の厚みが壁を横切る位置では撮らない");
        }

        [Test]
        public void WallAnchor_ContinuesSearchingWhenTheActualBodyRejectsACandidate()
        {
            Camera cam = MakeEndingCamera(out GameObject go);
            // 候補の再探索を測るfixture。保存Aの狭い画角の合否は別テストで測る。
            cam.transform.position = new Vector3(2f, 1.4f, -2f);
            cam.transform.LookAt(new Vector3(0f, 0.9f, 0f));
            cam.ResetProjectionMatrix();
            cam.fieldOfView = 60f;
            try
            {
                Vector3 corner = new Vector3(-0.5f, 0.12f, 0.5f);
                int measured = 0;
                Assert.That(EndingFarewellLogic.TrySolveAtWall(cam, Vector2.one, corner,
                    corner + Vector3.right * 0.955f, corner + Vector3.back * 0.955f, out _,
                    pairAccepted: (visitor, doll) => ++measured >= 2), Is.True);
                Assert.That(measured, Is.GreaterThanOrEqualTo(2), "実体が不合格の候補で止めず次を測る");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void WallAnchor_RefusesACameraThatCannotFrameTheVisitor()
        {
            Camera cam = MakeCamera(out GameObject go);
            try
            {
                cam.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
                Vector3 corner = new Vector3(0f, 0f, 4f);
                Assert.That(EndingFarewellLogic.TrySolveAtWall(cam, Vector2.one, corner,
                    corner + Vector3.right * 0.955f, corner + Vector3.back * 0.955f, out _), Is.False);
            }
            finally { Object.DestroyImmediate(go); }
        }

        private static Camera MakeCamera(out GameObject go)
        {
            go = new GameObject("FarewellTestCam");
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = 50f;
            cam.aspect = 4f / 3f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 30f;
            go.transform.position = new Vector3(0f, 2.2f, 0f);
            go.transform.rotation = Quaternion.Euler(25f, 0f, 0f);
            return cam;
        }

        private static Camera MakeEndingCamera(out GameObject go)
        {
            Camera cam = MakeCamera(out go);
            cam.transform.position = new Vector3(1.038f, 0.944f, -0.988f);
            cam.transform.rotation = Quaternion.Euler(3.95f, -47f, -0.98f);
            cam.projectionMatrix = Cg.ShowCgLayer.BuildProjectionMatrix(399.425f, 399.425f,
                320f, 240f, 640, 480, cam.nearClipPlane, cam.farClipPlane);
            return cam;
        }

        [Test]
        public void Retreat_ShowsItsBackAndClearsTheWall_ThenEndsFartherFromBothObservers()
        {
            Camera cam = MakeEndingCamera(out GameObject go);
            try
            {
                Vector3 corner = new Vector3(-0.5f, 0f, 0.5f);
                Vector3 endA = corner + Vector3.right * 0.955f;
                Vector3 endB = corner + Vector3.back * 0.955f;
                Assert.That(EndingFarewellLogic.TrySolveAtWall(cam, Vector2.one, corner, endA, endB, out var a), Is.True);
                float face = EndingFarewellLogic.FaceYawDeg(a.foot, a.visitorTargetFoot);
                float previous = Vector3.Distance(a.foot, a.visitorTargetFoot);
                Vector3 previousFoot = a.foot;
                float maximumSpeed = 0f;
                float retreatAt = EndingFarewellLogic.AppearSec + EndingFarewellLogic.FaceSec + EndingFarewellLogic.TurnSec;
                for (int i = 0; i <= 100; i++)
                {
                    var s = EndingFarewellLogic.SampleAt(retreatAt + EndingFarewellLogic.RetreatSec * i / 100f);
                    EndingFarewellLogic.PoseAt(a, s, face, out Vector3 foot, out float yaw);
                    Vector3 forward = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                    Assert.That(Vector3.Dot(forward, a.visitorTargetFoot - foot), Is.LessThan(0f), "人へ背を向ける");
                    Assert.That(Vector3.Dot(forward, cam.transform.position - foot), Is.LessThan(0f), "カメラへ背を向ける");
                    float distance = Vector3.Distance(foot, a.visitorTargetFoot);
                    Assert.That(distance, Is.GreaterThanOrEqualTo(previous - 1e-5f));
                    previous = distance;
                    if (i > 0) maximumSpeed = Mathf.Max(maximumSpeed,
                        Vector3.Distance(previousFoot, foot) / (EndingFarewellLogic.RetreatSec / 100f));
                    previousFoot = foot;
                    Assert.That(Mathf.Min(SegmentDistance(foot, corner, endA), SegmentDistance(foot, corner, endB))
                        - a.dollBodyRadiusM - 0.02f, Is.GreaterThanOrEqualTo(EndingFarewellLogic.PhotoWallClearanceM - 1e-5f));
                }
                Assert.That(maximumSpeed, Is.LessThanOrEqualTo(EndingFarewellLogic.RetreatMaxSpeedMps), "実際の道筋の距離/秒");
                var done = new EndingFarewellLogic.Sample { phase = Phase.Done };
                EndingFarewellLogic.PoseAt(a, done, face, out Vector3 last, out _);
                Assert.That(Vector3.Distance(last, a.retreatEnd), Is.LessThan(1e-4f), "Done で初期位置へ戻らない");
                Assert.That(Vector3.Distance(last, cam.transform.position), Is.GreaterThan(Vector3.Distance(a.foot, cam.transform.position)));
                float before = Mathf.Abs(cam.WorldToViewportPoint(a.foot + Vector3.up * 0.4f).y - cam.WorldToViewportPoint(a.foot).y);
                float after = Mathf.Abs(cam.WorldToViewportPoint(last + Vector3.up * 0.4f).y - cam.WorldToViewportPoint(last).y);
                Assert.That(after, Is.LessThan(before));
            }
            finally { Object.DestroyImmediate(go); }
        }

        private static float SegmentDistance(Vector3 point, Vector3 a, Vector3 b)
        {
            Vector3 delta = b - a;
            return Vector3.Distance(point, a + delta * Mathf.Clamp01(Vector3.Dot(point - a, delta) / delta.sqrMagnitude));
        }

        [Test]
        public void Anchor_StandsLeftOfTheVisitor_OnTheFloor_AndRetreatsAwayFromThem()
        {
            Camera cam = MakeCamera(out GameObject go);
            try
            {
                var visitor = new Vector3(0.6f, 0f, 3.5f);
                Vector2 contain = new Vector2(0.75f, 1f);
                Assert.That(EndingFarewellLogic.TrySolve(cam, contain, visitor, out var a), Is.True);
                Assert.That(a.foot.y, Is.EqualTo(0f).Within(1e-4f), "床に立つ");
                Assert.That(a.dollUv.x, Is.LessThan(a.visitorUv.x - 0.05f), "画面の中で体験者の左");
                Assert.That(a.dollUv.y, Is.EqualTo(a.visitorUv.y).Within(0.03f), "同じ奥行き（足元の高さが揃う）");
                float gap = Vector3.Distance(a.foot, visitor);
                Assert.That(gap, Is.InRange(EndingFarewellLogic.MinGapM - 1e-3f, EndingFarewellLogic.MaxGapM + 1e-3f));

                // 見つめる向きは画面の右（カメラの右方向の成分が正）。
                float faceYaw = EndingFarewellLogic.FaceYawDeg(a.foot, visitor);
                Vector3 faceDir = Quaternion.Euler(0f, faceYaw, 0f) * Vector3.forward;
                Assert.That(Vector3.Dot(faceDir, cam.transform.right), Is.GreaterThan(0.8f), "右向き");

                // 下がる向きは体験者から離れ、カメラからも遠ざかる。
                Assert.That(Vector3.Dot(a.retreatDir, visitor - a.foot), Is.LessThan(0f));
                Vector3 fromCam = a.foot - cam.transform.position;
                fromCam.y = 0f;
                Assert.That(Vector3.Dot(a.retreatDir, fromCam), Is.GreaterThan(0f));
                // 後ろを向く = 体験者の方から大きく回る。
                float turn = Mathf.Abs(Mathf.DeltaAngle(faceYaw, a.awayYawDeg));
                Assert.That(turn, Is.GreaterThan(100f));
                // 後ろを向いた後は背中がカメラへ向く（奥へ向かう成分が大きい）。
                Vector3 awayDir = Quaternion.Euler(0f, a.awayYawDeg, 0f) * Vector3.forward;
                Vector3 camFwd = cam.transform.forward;
                camFwd.y = 0f;
                Assert.That(Vector3.Dot(awayDir, camFwd.normalized), Is.GreaterThan(0.7f), "背中を見せる");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Anchor_NearTheLeftEdge_StillStaysInFrameAndLeft()
        {
            Camera cam = MakeCamera(out GameObject go);
            try
            {
                var visitor = new Vector3(-1.0f, 0f, 3.0f);
                Assert.That(EndingFarewellLogic.TrySolve(cam, new Vector2(0.75f, 1f), visitor, out var a), Is.True);
                Assert.That(a.dollUv.x, Is.LessThan(a.visitorUv.x));
                Assert.That(Vector3.Distance(a.foot, visitor), Is.GreaterThanOrEqualTo(EndingFarewellLogic.MinGapM - 1e-3f));
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Anchor_VisitorCloseToTheCamera_PutsTheDollFullyInFrame()
        {
            // 体験者がカメラに近く足元が画の下にはみ出しているとき、人形を同じ奥行きに置くと
            // 頭だけが下端に覗く。人形は奥へ下げて足元を画に入れる。
            Camera cam = MakeCamera(out GameObject go);
            try
            {
                var contain = new Vector2(0.75f, 1f);
                var visitor = new Vector3(0.2f, 0f, 1.2f);
                Assert.That(Cg.ShowCgLayer.TryFrameUv(cam, visitor, contain, out Vector2 vuv), Is.True);
                Assert.That(vuv.y, Is.LessThan(0f), "前提: 体験者の足元は画の外");
                Assert.That(EndingFarewellLogic.TrySolve(cam, contain, visitor, out var a), Is.True);
                Assert.That(a.dollUv.y, Is.GreaterThanOrEqualTo(EndingFarewellLogic.MinFootV - 1e-3f), "人形の足元は画の中");
                Assert.That(a.dollUv.x, Is.LessThan(a.visitorUv.x), "体験者の左");
                Assert.That(a.fromScreen, Is.True);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Anchor_RefusesAVisitorBehindTheCamera()
        {
            Camera cam = MakeCamera(out GameObject go);
            try
            {
                Assert.That(EndingFarewellLogic.TrySolve(cam, Vector2.one, new Vector3(0f, 0f, -2f), out _), Is.False);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void FloorAtFrameUv_InvertsTryFrameUv()
        {
            Camera cam = MakeCamera(out GameObject go);
            try
            {
                var contain = new Vector2(0.75f, 1f);
                var p = new Vector3(0.3f, 0f, 4f);
                Assert.That(Cg.ShowCgLayer.TryFrameUv(cam, p, contain, out Vector2 uv), Is.True);
                Assert.That(EndingFarewellLogic.TryFloorAtFrameUv(cam, contain, uv, 0f, out Vector3 hit), Is.True);
                Assert.That(Vector3.Distance(hit, p), Is.LessThan(1e-3f));
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
