#nullable enable
using FixedCamVr.Streaming.Cg;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// CG 人形を実映像に重ねるための**像空間の対応付け**を固定する。
    ///
    /// ここが狂うと「姿勢を完璧に測っても人形が合わない」状態になる。実際、2026-07-27 の監査では
    /// (1) 水平画角を Unity の垂直 FOV へそのまま渡していた（4:3 で約 25% ずれ）
    /// (2) 光の向きがワールド固定で、トラッキング原点の向き次第で部屋に対する陰影が変わっていた
    /// の 2 件が確定した。両方ともここで数値として固定する。
    /// 設計の正本: .claude/plans/2026-07-27_cg-compositing-rebuild.md
    /// </summary>
    public sealed class CgProjectionTests
    {
        // ---- 水平 FOV → 垂直 FOV ----

        [Test]
        public void Fov_SquareAspect_IsIdentity()
        {
            // 1:1 なら水平と垂直は同じ。変換式の恒等元。
            Assert.AreEqual(70f, ShowCgLayer.HorizontalToVerticalFovDeg(70f, 1f), 1e-3f);
        }

        [Test]
        public void Fov_FourThree_NarrowsVertically()
        {
            // 4:3 で水平 90° → 垂直 2*atan(tan(45°)/1.3333) = 73.74°
            float v = ShowCgLayer.HorizontalToVerticalFovDeg(90f, 4f / 3f);
            Assert.AreEqual(73.7398f, v, 1e-3f);
            Assert.Less(v, 90f, "横長の画では垂直画角は水平より狭い");
        }

        [Test]
        public void Fov_RoundTrip_MatchesTangentRelation()
        {
            // 定義そのもの: tan(h/2) = aspect * tan(v/2)
            const float aspect = 16f / 9f;
            float v = ShowCgLayer.HorizontalToVerticalFovDeg(100f, aspect);
            float lhs = Mathf.Tan(100f * 0.5f * Mathf.Deg2Rad);
            float rhs = aspect * Mathf.Tan(v * 0.5f * Mathf.Deg2Rad);
            Assert.AreEqual(lhs, rhs, 1e-4f);
        }

        [Test]
        public void Fov_WiderAspect_GivesNarrowerVertical()
        {
            // 同じレンズ（水平画角一定）でも、横長の画ほど垂直画角は狭くなる。
            float v43 = ShowCgLayer.HorizontalToVerticalFovDeg(80f, 4f / 3f);
            float v169 = ShowCgLayer.HorizontalToVerticalFovDeg(80f, 16f / 9f);
            Assert.Less(v169, v43);
        }

        [Test]
        public void Fov_DegenerateAspect_DoesNotExplode()
        {
            // アスペクトが未確定（映像未受信）でも NaN / 無限大を出さない。
            float v = ShowCgLayer.HorizontalToVerticalFovDeg(70f, 0f);
            Assert.IsFalse(float.IsNaN(v));
            Assert.IsFalse(float.IsInfinity(v));
        }

        // ---- 較正の内部行列 → 射影行列（卓の JS と同じ画素を出すこと）----

        [Test]
        public void Projection_MatchesPinholeFormula()
        {
            // ⚠ **卓の calib.test.mjs「投影は Unity と同じ画素を出す」と同一の入力・同一の期待値**。
            //    片方だけ直すと沈黙して食い違い、「卓では合うのに実機が違う」になる。必ず両方直すこと。
            //    カメラ (0, 1.2, -1.5) から 30° 下向き、fx=fy=480 / 主点中心 / 640x480。
            //    course 原点の床 (0,0,0) は u=320, v=313.1058 に写る（v は画像の上原点）。
            var rig = new ProbeCam(320f);
            try
            {
                rig.Camera.transform.position = new Vector3(0f, 1.2f, -1.5f);
                rig.Camera.transform.rotation = Quaternion.Euler(30f, 0f, 0f);   // pose の pitchDeg = -30

                Vector3 vp = rig.Camera.WorldToViewportPoint(Vector3.zero);
                Assert.Greater(vp.z, 0f, "カメラ前方にあること");

                float u = vp.x * 640f;
                float v = (1f - vp.y) * 480f;              // viewport は下原点、画像は上原点
                Assert.AreEqual(320f, u, 0.01f, "u");
                Assert.AreEqual(313.1058f, v, 0.01f, "v");
            }
            finally { rig.Dispose(); }
        }

        /// <summary>
        /// 検算用のカメラ一式。**実行時の <see cref="ShowCgLayer"/> の仮想カメラと同じ条件**にする。
        ///
        /// ⚠ **640x480 の RenderTexture を必ず割り当てる。** `Camera.WorldToViewportPoint` は
        /// いったん画素空間を経由するので、描画先が無いカメラは **Editor の Game ビューの幅**を
        /// 使ってしまい、結果がウィンドウサイズ依存になる。2026-08-03 に実測したときは
        /// Game ビューが 311px で、u が一律 +0.107%（例: 400 → 400.429）ずれて 2 本落ちていた。
        /// 実行時は `_virtualCam.targetTexture = _rt`（映像の実寸）なので**実機は無影響**
        /// ＝ 落ちていたのはテストが実行時を写していなかったから。
        /// 射影行列自体は正しい（`m02` は期待どおり -0.25 で格納されていた）。
        /// </summary>
        private sealed class ProbeCam : System.IDisposable
        {
            private readonly GameObject _go;
            private readonly RenderTexture _rt;
            public Camera Camera { get; }

            public ProbeCam(float cxPx)
            {
                _go = new GameObject("calib-probe-cam");
                _rt = new RenderTexture(640, 480, 24);
                Camera = _go.AddComponent<Camera>();
                Camera.enabled = false;                              // 描画はしない（行列の検算だけ）
                Camera.stereoTargetEye = StereoTargetEyeMask.None;   // 実行時と同じ片眼扱い
                Camera.nearClipPlane = 0.05f;
                Camera.farClipPlane = 30f;
                Camera.targetTexture = _rt;                          // ← 画素空間を 640x480 に固定する
                Camera.projectionMatrix = ShowCgLayer.BuildProjectionMatrix(
                    480f, 480f, cxPx, 240f, 640, 480, 0.05f, 30f);
            }

            public void Dispose()
            {
                Camera.targetTexture = null;
                Object.DestroyImmediate(_go);
                Object.DestroyImmediate(_rt);
            }
        }

        [Test]
        public void Projection_PrincipalPointOffset_ShiftsImageCenter()
        {
            // 主点をずらせる（＝非対称 frustum）ことが、physical camera を経由せず
            // projectionMatrix を直接入れている理由。三脚のセンサー中心ズレを吸収できる。
            var rig = new ProbeCam(400f);                            // cx を 320 → 400
            try
            {
                var cam = rig.Camera;
                cam.transform.position = Vector3.zero;
                cam.transform.rotation = Quaternion.identity;

                // 光軸上（真正面）の点は主点へ写る。
                Vector3 vp = cam.WorldToViewportPoint(new Vector3(0f, 0f, 2f));
                Assert.AreEqual(400f, vp.x * 640f, 0.01f, "主点 cx へ写ること");
                Assert.AreEqual(240f, (1f - vp.y) * 480f, 0.01f, "cy は動かしていない");
            }
            finally { rig.Dispose(); }
        }

        [Test]
        public void Projection_DegenerateFocal_DoesNotProduceNaN()
        {
            Matrix4x4 p = ShowCgLayer.BuildProjectionMatrix(0f, 0f, 0f, 0f, 0, 0, 0.05f, 30f);
            for (int i = 0; i < 16; i++) Assert.IsFalse(float.IsNaN(p[i]), $"m[{i}]");
        }

        // ---- course 空間の光の向き → ワールド ----

        [Test]
        public void Light_StraightUp_IsWorldUp()
        {
            Vector3 d = ShowCgLayer.CourseLightDirToWorld(0f, 90f, 0f);
            Assert.AreEqual(0f, d.x, 1e-3f);
            Assert.AreEqual(1f, d.y, 1e-3f);
            Assert.AreEqual(0f, d.z, 1e-3f);
        }

        [Test]
        public void Light_ZeroYaw_PointsAlongCoursePlusZ()
        {
            // 方位角 0・仰角 0 = course の +Z 方向から光が来る（yaw の基準を固定する）。
            Vector3 d = ShowCgLayer.CourseLightDirToWorld(0f, 0f, 0f);
            Assert.AreEqual(0f, d.x, 1e-3f);
            Assert.AreEqual(0f, d.y, 1e-3f);
            Assert.AreEqual(1f, d.z, 1e-3f);
        }

        [Test]
        public void Light_YawIncreases_TowardPlusX()
        {
            // +90° で +X。卓のフロアマップの向きハンドル（sin/cos）と同じ規約。
            Vector3 d = ShowCgLayer.CourseLightDirToWorld(90f, 0f, 0f);
            Assert.AreEqual(1f, d.x, 1e-3f);
            Assert.AreEqual(0f, d.z, 1e-3f);
        }

        [Test]
        public void Light_FollowsCourseYaw()
        {
            // **これが修正の核心**: 部屋（course）が実空間で回っていれば、光もその分だけ回る。
            // 旧実装はワールド固定で、登録のたびに部屋に対する光の向きが変わっていた。
            Vector3 a = ShowCgLayer.CourseLightDirToWorld(30f, 20f, 0f);
            Vector3 b = ShowCgLayer.CourseLightDirToWorld(30f, 20f, 45f);
            Vector3 expected = Quaternion.Euler(0f, 45f, 0f) * a;
            Assert.AreEqual(expected.x, b.x, 1e-3f);
            Assert.AreEqual(expected.y, b.y, 1e-3f);
            Assert.AreEqual(expected.z, b.z, 1e-3f);
        }

        [Test]
        public void Light_IsAlwaysNormalized()
        {
            foreach (float pitch in new[] { -80f, 0f, 45f, 89f })
                foreach (float yaw in new[] { -170f, 0f, 123f })
                {
                    Vector3 d = ShowCgLayer.CourseLightDirToWorld(yaw, pitch, 17f);
                    Assert.AreEqual(1f, d.magnitude, 1e-3f, $"yaw={yaw} pitch={pitch}");
                }
        }

        // ---- 色温度 → 光の色（卓のスウォッチと同じ式であること）----

        [Test]
        public void Kelvin_MatchesConsoleFormula()
        {
            // ⚠ **卓の room-model.test.mjs「kelvinToRgb は Unity と同じ値を出す」と同一の期待値**。
            //    卓のスウォッチと実機の人形の色が食い違うと、著作者は何を信じればいいのか分からなくなる。
            //    比較は sRGB(0..255) で行う（C# 側は Linear を返すので戻してから）。
            Color c = ShowCgLayer.KelvinToLinearColor(4000f);
            Assert.AreEqual(255f, Mathf.LinearToGammaSpace(c.r) * 255f, 0.5f, "R");
            Assert.AreEqual(205.8f, Mathf.LinearToGammaSpace(c.g) * 255f, 0.5f, "G");
            Assert.AreEqual(166.1f, Mathf.LinearToGammaSpace(c.b) * 255f, 0.5f, "B");
        }

        [Test]
        public void Kelvin_WarmIsRedder_CoolIsBluer()
        {
            Color warm = ShowCgLayer.KelvinToLinearColor(2500f);
            Color cool = ShowCgLayer.KelvinToLinearColor(7500f);
            Assert.Greater(warm.r / Mathf.Max(1e-4f, warm.b), cool.r / Mathf.Max(1e-4f, cool.b),
                "低い色温度ほど赤が青より強い");
        }

        [Test]
        public void Kelvin_OutOfRange_IsClampedAndFinite()
        {
            foreach (float k in new[] { -100f, 0f, 500f, 20000f, float.NaN })
            {
                Color c = ShowCgLayer.KelvinToLinearColor(k);
                Assert.IsFalse(float.IsNaN(c.r) || float.IsNaN(c.g) || float.IsNaN(c.b), $"k={k}");
                Assert.GreaterOrEqual(c.r, 0f); Assert.LessOrEqual(c.r, 1f);
                Assert.GreaterOrEqual(c.g, 0f); Assert.LessOrEqual(c.g, 1f);
                Assert.GreaterOrEqual(c.b, 0f); Assert.LessOrEqual(c.b, 1f);
            }
        }

        // ---- 接地影のにじみ ----

        [Test]
        public void BlobFeather_IsRatioOfRadius()
        {
            // にじみは m 指定、シェーダは半径比で受ける。半径が変われば比も変わる。
            Assert.AreEqual(0.4f, ShowCgLayer.BlobFeatherFromSoftM(0.12f, 0.3f), 1e-4f);
            Assert.AreEqual(0.24f, ShowCgLayer.BlobFeatherFromSoftM(0.12f, 0.5f), 1e-4f);
        }

        [Test]
        public void BlobFeather_IsClamped()
        {
            Assert.AreEqual(1f, ShowCgLayer.BlobFeatherFromSoftM(10f, 0.3f), 1e-4f, "全域ぼけで頭打ち");
            Assert.AreEqual(0.05f, ShowCgLayer.BlobFeatherFromSoftM(0f, 0.3f), 1e-4f, "0 でも縁は少し落とす");
            Assert.IsFalse(float.IsNaN(ShowCgLayer.BlobFeatherFromSoftM(0.1f, 0f)), "半径 0 で NaN を出さない");
        }

        [Test]
        public void Light_ExtremePitch_IsClamped()
        {
            // 真上（90°超）を渡しても縮退して NaN にならない。
            Vector3 d = ShowCgLayer.CourseLightDirToWorld(0f, 180f, 0f);
            Assert.IsFalse(float.IsNaN(d.x) || float.IsNaN(d.y) || float.IsNaN(d.z));
            Assert.AreEqual(1f, d.magnitude, 1e-3f);
        }

        // ---- 平面投影シャドウ（ShowShadowProjector.shader と同じ式）----
        //
        // ⚠ ここの期待値とシェーダの頂点計算は**必ず対で直す**。片方だけ直すとテストは通るのに
        //    実機の影だけがずれる（このプロジェクトが何度も踏んでいる形）。

        [Test]
        public void Shadow_LightStraightUp_DropsVertically()
        {
            Assert.IsTrue(ShadowProjectionLogic.TryProjectToPlane(
                new Vector3(1f, 2f, 3f), 0f, Vector3.up, out Vector3 p));
            Assert.AreEqual(1f, p.x, 1e-4f);
            Assert.AreEqual(0f, p.y, 1e-4f);
            Assert.AreEqual(3f, p.z, 1e-4f);
        }

        [Test]
        public void Shadow_SlantedLight_FallsAwayFromTheLight()
        {
            // 光が -Z 側の高さ 45° から来る → 影は +Z 側へ、高さと同じだけ伸びる。
            Vector3 l = new Vector3(0f, 1f, -1f).normalized;
            Assert.IsTrue(ShadowProjectionLogic.TryProjectToPlane(
                new Vector3(0f, 1f, 0f), 0f, l, out Vector3 p));
            Assert.AreEqual(0f, p.x, 1e-4f);
            Assert.AreEqual(0f, p.y, 1e-4f);
            Assert.AreEqual(1f, p.z, 1e-4f, "45° なら高さ 1m の点の影は 1m 先");
        }

        [Test]
        public void Shadow_UsesCourseLightDirection()
        {
            // ApplyLight が渡すのと同じベクトルで動くこと（course 相対の向きがそのまま影の向きになる）。
            Vector3 l = ShowCgLayer.CourseLightDirToWorld(0f, 45f, 0f);   // course +Z の 45° 上から
            Assert.IsTrue(ShadowProjectionLogic.TryProjectToPlane(
                new Vector3(0f, 1f, 0f), 0f, l, out Vector3 p));
            Assert.Less(p.z, -0.9f, "光が +Z から来るなら影は -Z へ落ちる");
        }

        [Test]
        public void Shadow_RaisedFloor_ShortensTheProjection()
        {
            // 床が高いほど影は短い（机の上に立つケース）。plane を無視すると影が床下へ抜ける。
            Vector3 l = new Vector3(0f, 1f, -1f).normalized;
            Assert.IsTrue(ShadowProjectionLogic.TryProjectToPlane(
                new Vector3(0f, 1f, 0f), 0.5f, l, out Vector3 p));
            Assert.AreEqual(0.5f, p.y, 1e-4f);
            Assert.AreEqual(0.5f, p.z, 1e-4f);
        }

        [Test]
        public void Shadow_PointOnPlane_StaysPut()
        {
            // 床に接している足の頂点は動かない（＝影と足が必ず繋がる）。
            Vector3 l = new Vector3(0.4f, 1f, -0.2f).normalized;
            Assert.IsTrue(ShadowProjectionLogic.TryProjectToPlane(
                new Vector3(0.7f, 0f, -0.3f), 0f, l, out Vector3 p));
            Assert.AreEqual(0.7f, p.x, 1e-4f);
            Assert.AreEqual(-0.3f, p.z, 1e-4f);
        }

        [Test]
        public void Shadow_HorizontalLight_IsRejected()
        {
            // 真横から来る光では床との交点が無限遠。**そのまま描くと画面いっぱいの黒帯**になるので
            // シェーダ側も同じ閾値で三角形を潰す（影を出さない）。
            Assert.IsFalse(ShadowProjectionLogic.TryProjectToPlane(
                new Vector3(0f, 1f, 0f), 0f, new Vector3(1f, 0f, 0f), out _));
        }

        [Test]
        public void Shadow_LightFromBelow_IsRejected()
        {
            Assert.IsFalse(ShadowProjectionLogic.TryProjectToPlane(
                new Vector3(0f, 1f, 0f), 0f, new Vector3(0f, -1f, 0f), out _));
        }

        [Test]
        public void Shadow_ZeroLightVector_IsRejected()
        {
            // 未設定のベクトル（0,0,0）を「真上」と誤解しない。誤解すると影の向きが黙って変わる。
            Assert.IsFalse(ShadowProjectionLogic.TryProjectToPlane(
                new Vector3(0f, 1f, 0f), 0f, Vector3.zero, out _));
        }

        [Test]
        public void Shadow_UnnormalizedLight_GivesSameResult()
        {
            // マテリアルに入る値は正規化済みとは限らない（卓の著作値・MPB 経由）。
            Vector3 l = new Vector3(0f, 1f, -1f);
            Assert.IsTrue(ShadowProjectionLogic.TryProjectToPlane(
                new Vector3(0f, 1f, 0f), 0f, l, out Vector3 a));
            Assert.IsTrue(ShadowProjectionLogic.TryProjectToPlane(
                new Vector3(0f, 1f, 0f), 0f, l * 7.3f, out Vector3 b));
            Assert.AreEqual(a.z, b.z, 1e-4f);
        }

        // ---- 接地影の大きさ（人間用の定数を人形へ流用しない）----

        [Test]
        public void Blob_人形の大きさに比例する()
        {
            // ⚠ 下限 0.15m（人間用）を人形にも掛けていたのが 2026-08-06 まで残っていた:
            //    全高 0.40m の市松人形で本来 0.088m のところ 0.15m へ持ち上がり、
            //    **背丈の 3/4 の黒い円盤**が足元に敷かれて「切り抜きを板に貼った」ように見えていた。
            Assert.AreEqual(0.088f, ShowCgLayer.GroundBlobRadiusM(0.40f), 1e-3f, "人形 0.40m");
            Assert.AreEqual(0.352f, ShowCgLayer.GroundBlobRadiusM(1.60f), 1e-3f, "成人 1.6m");
        }

        [Test]
        public void Blob_極端な大きさでも上下限に収まる()
        {
            Assert.AreEqual(0.03f, ShowCgLayer.GroundBlobRadiusM(0.02f), 1e-4f, "下限");
            Assert.AreEqual(0.6f, ShowCgLayer.GroundBlobRadiusM(9f), 1e-4f, "上限");
            Assert.AreEqual(0.03f, ShowCgLayer.GroundBlobRadiusM(0f), 1e-4f, "0 でも破綻しない");
        }

        // ---- 人形の明るさを映像へ寄せる ----

        [Test]
        public void 明るさ倍率_測れていなければ著作値のまま()
        {
            Assert.AreEqual(1f, ShowCgLayer.LumaGainFor(-1f, 0f), 1e-5f);
        }

        [Test]
        public void 明るさ倍率_基準の明るさでは変えない()
        {
            // 基準 0.20 = 「人が立つ床あたりの明るさ」。ここでは著作した光量をそのまま使う。
            Assert.AreEqual(1f, ShowCgLayer.LumaGainFor(0.20f, 0f), 1e-3f);
        }

        [Test]
        public void 明るさ倍率_暗い映像では人形も暗くする()
        {
            float dark = ShowCgLayer.LumaGainFor(0.08f, 0f);
            float bright = ShowCgLayer.LumaGainFor(0.70f, 0f);
            Assert.Less(dark, 1f, "暗い区間で人形だけ明るいと必ず浮く");
            Assert.Greater(bright, 1f, "明るい区間で人形だけ暗いと沈む");
            // 真っ暗で人形が消える / 白飛びで焼ける、を防ぐ clamp。
            Assert.Greater(ShowCgLayer.LumaGainFor(0.001f, 0f), 0.2f, "完全には消さない");
            Assert.Less(ShowCgLayer.LumaGainFor(1f, 0f), 1.9f);
        }

        [Test]
        public void 明るさ倍率_暗所でも下限に張り付かず消えもしない()
        {
            // 実測（2026-08-07・カメラ B/C の無人プレート）で人形が立つ場所は 0.06 台、
            // 自動露出が持ち上げた後で 0.086。旧実装は下限 0.35（→ 倍率 0.545）で頭打ちになり、
            // 白い顔が周囲の 3 倍の明るさで出ていた。一方で下げすぎると絵から人形が消える
            // （実測: 基準 0.35 のまま局所輝度へ切り替えたら周囲の 0.75〜0.86 倍まで沈んだ）。
            float veryDark = ShowCgLayer.LumaGainFor(0.086f, 0f);
            Assert.Less(veryDark, 0.75f, "暗所で頭打ちになると顔だけが浮く");
            Assert.Greater(veryDark, 0.35f, "3 周目に自分だと読めなくなる");
        }

        [Test]
        public void 明るさ倍率_自動露出が持ち上げたぶんは差し引く()
        {
            // 装置の自動露出が画全体を +1EV したなら、体験者に見えている明るさは 2 倍。
            // そこへ生の輝度で合わせてから露出を浴びると二重補正になり、暗い区間ほど人形が浮く。
            float raw = ShowCgLayer.LumaGainFor(0.10f, 0f);
            float withAgc = ShowCgLayer.LumaGainFor(0.10f, 1f);
            Assert.Greater(withAgc, raw, "露出で持ち上がるぶん、人形はそこまで暗くしなくてよい");
            Assert.AreEqual(ShowCgLayer.LumaGainFor(0.20f, 0f), withAgc, 1e-4f,
                            "+1EV は輝度 2 倍と同じ扱いになる");
        }
    }
}
