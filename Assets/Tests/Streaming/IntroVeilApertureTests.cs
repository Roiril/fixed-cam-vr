#nullable enable
using System.Reflection;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// <b>導入の枠が、本編のスクリーンにちょうど重なることを固定する。</b>
    ///
    /// 2026-08-01 の実害: 覆い（<see cref="IntroVeil"/>）は head-lock で開口を uv 中心に固定しており、
    /// 本編のスクリーンが頭の約 8° 下（<c>ScreenAnchor.heightOffset = -0.28m</c> / 距離 2.0m）に
    /// 置かれることを勘定していなかった。半画角 18.4° に対して 8° ＝ <b>43% の縦ずれ</b>。
    /// 「枠は現れるだけで、既にそこにある」（計画 2026-07-30 §4）が成立していなかった。
    ///
    /// 判定は <see cref="IntroVeil.SignedDistance"/>（シェーダと同じ式）に**スクリーンの実点**を
    /// 食わせて行う。開口の内部表現ではなく<b>「その点が枠の中か」</b>を見るので、表現を変えても壊れない。
    /// </summary>
    public sealed class IntroVeilApertureTests
    {
        /// <summary>本編スクリーンの実寸（MjpegScreenStage.prefab の localScale）。</summary>
        private static readonly Vector2 ScreenScale = new(2.3704f, 1.3333f);

        /// <summary>ScreenAnchor の既定配置（距離 2.0m・頭から 0.28m 下）。</summary>
        private const float ScreenDistance = 2.0f;
        private const float ScreenHeightOffset = -0.28f;

        /// <summary>枠が閉じ切った状態。</summary>
        private const float Closed = 1f;

        /// <summary>
        /// 縁の判定に使う余裕。<see cref="IntroVeil.SignedDistance"/> は「辺の平面からの角度の sin」なので、
        /// 0.0009 ≒ 0.05°。2m 先で 1.7mm ＝ feather のぼけ幅よりずっと細かい。
        /// </summary>
        private const float Eps = 0.0009f;

        private GameObject? _eye;
        private GameObject? _screen;
        private IntroVeil? _veil;

        [SetUp]
        public void SetUp()
        {
            // 眼（CenterEyeAnchor 相当）とその子の覆い。
            _eye = new GameObject("Eye");
            var veilGo = new GameObject("IntroVeil");
            veilGo.transform.SetParent(_eye.transform, worldPositionStays: false);
            _veil = veilGo.AddComponent<IntroVeil>();

            _screen = new GameObject("Screen");
            _screen.transform.localScale = new Vector3(ScreenScale.x, ScreenScale.y, 1f);
            SetField(_veil, "screenQuad", _screen.transform);
        }

        [TearDown]
        public void TearDown()
        {
            if (_screen != null) Object.DestroyImmediate(_screen);
            if (_eye != null) Object.DestroyImmediate(_eye);
        }

        private static void SetField(object target, string name, object? value)
            => target.GetType()
                .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(target, value);

        /// <summary>ScreenAnchor と同じ規則でスクリーンを置く（ヨーだけ追従・高さオフセット・水平）。</summary>
        private void PlaceScreen(float screenYawDeg)
        {
            Quaternion face = Quaternion.Euler(0f, screenYawDeg, 0f);
            Vector3 eye = _eye!.transform.position;
            Vector3 pos = eye + face * Vector3.forward * ScreenDistance;
            pos.y = eye.y + ScreenHeightOffset;
            _screen!.transform.SetPositionAndRotation(pos, face);
        }

        /// <summary>スクリーン面上の点（-1..1 の正規化座標）。</summary>
        private Vector3 ScreenPoint(float u, float v)
            => _screen!.transform.position
               + _screen.transform.right * (u * ScreenScale.x * 0.5f)
               + _screen.transform.up * (v * ScreenScale.y * 0.5f);

        /// <summary>枠の縁とスクリーンの縁が一致していること（＝四隅と辺の中点が縁の上に乗る）。</summary>
        private void AssertFrameMatchesScreen(string what)
        {
            // 四隅 + 各辺の中点。辺の中点まで見るのは、四隅だけだと「四隅を通る別の四角形」を
            // 見逃すため（透視投影では矩形が台形になるので、実際に起こりうる）。
            foreach ((float u, float v) in new[]
                     {
                         (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f),
                         (0f, -1f), (0f, 1f), (-1f, 0f), (1f, 0f),
                     })
            {
                float d = _veil!.SignedDistance(ScreenPoint(u, v), Closed);
                Assert.That(Mathf.Abs(d), Is.LessThan(Eps),
                    $"{what}: スクリーンの縁 ({u}, {v}) が枠の縁から {Mathf.Asin(Mathf.Clamp(d, -1f, 1f)) * Mathf.Rad2Deg:F2}° " +
                    "ずれている。段 4 で枠の縁とスクリーンの縁のあいだに帯が露出する。");
            }

            // 中はしっかり中、外はしっかり外（縁だけ合っていて内外が反転していないこと）。
            Assert.That(_veil!.SignedDistance(ScreenPoint(0f, 0f), Closed), Is.LessThan(-Eps),
                $"{what}: スクリーンの中心が枠の外になっている");
            Assert.That(_veil.SignedDistance(ScreenPoint(1.2f, 0f), Closed), Is.GreaterThan(Eps),
                $"{what}: スクリーンの外（横）が枠の中になっている");
            Assert.That(_veil.SignedDistance(ScreenPoint(0f, 1.2f), Closed), Is.GreaterThan(Eps),
                $"{what}: スクリーンの外（縦）が枠の中になっている");
        }

        [Test]
        public void Frame_MatchesScreen_WhenLookingStraightAhead()
        {
            // **これが 2026-08-01 まで落ちていたケース**。開口が uv 中心固定だと縦に 8° ずれる。
            PlaceScreen(screenYawDeg: 0f);
            AssertFrameMatchesScreen("正面");
        }

        [Test]
        public void Frame_MatchesScreen_WhenScreenLagsBehindHead()
        {
            // 追従が遅れてスクリーンが頭の正面に無い瞬間。スクリーン面が視線に対して傾くので、
            // 覆いの面へ矩形として投影する方式ではここで四隅が数度ずれた。
            _eye!.transform.rotation = Quaternion.Euler(0f, 25f, 0f);
            PlaceScreen(screenYawDeg: 10f);
            AssertFrameMatchesScreen("追従の遅れ");
        }

        [Test]
        public void Frame_MatchesScreen_WhenHeadIsPitchedDown()
        {
            // **スクリーンは水平を保つ（ヨーだけ追従）ので、頭を上下に振ると必ず面が傾く。**
            // 矩形近似では 20° の見下ろしで四隅が約 2.8° ずれていた。
            PlaceScreen(screenYawDeg: 0f);
            _eye!.transform.rotation = Quaternion.Euler(20f, 0f, 0f);
            AssertFrameMatchesScreen("見下ろし");
        }

        [Test]
        public void Frame_MatchesScreen_WhenHeadIsRolled()
        {
            PlaceScreen(screenYawDeg: 0f);
            _eye!.transform.rotation = Quaternion.Euler(0f, 0f, 15f);   // 首を傾げる
            AssertFrameMatchesScreen("首の傾き");
        }

        [Test]
        public void Frame_MatchesScreen_WhenEyeIsNotAtOrigin()
        {
            // 体験者は歩く。眼がワールド原点に居ることに依存していないことを確かめる。
            _eye!.transform.position = new Vector3(1.3f, 1.6f, -0.7f);
            _eye.transform.rotation = Quaternion.Euler(8f, -40f, 3f);
            PlaceScreen(screenYawDeg: -40f);
            AssertFrameMatchesScreen("原点から離れた眼");
        }

        [Test]
        public void ScreenFadeIntersection_UsesOnlyTheScreenRectangle()
        {
            // _ScreenFade は開口全体ではなく、本編スクリーンの実矩形だけへ掛ける。
            // 頭とスクリーンが平行でない場合も、中心は内側、すぐ外は外側でなければならない。
            _eye!.transform.position = new Vector3(0.8f, 1.6f, -0.4f);
            _eye.transform.rotation = Quaternion.Euler(18f, 22f, 4f);
            PlaceScreen(screenYawDeg: 8f);

            Assert.That(_veil!.ScreenSignedDistance(ScreenPoint(0f, 0f)), Is.LessThan(-Eps),
                "スクリーン中心が映像クロスフェードの外になっている");
            Assert.That(_veil.ScreenSignedDistance(ScreenPoint(1.2f, 0f)), Is.GreaterThan(Eps),
                "スクリーンの横外まで映像クロスフェードが掛かっている");
            Assert.That(_veil.ScreenSignedDistance(ScreenPoint(0f, 1.2f)), Is.GreaterThan(Eps),
                "スクリーンの縦外まで映像クロスフェードが掛かっている");
        }

        [Test]
        public void Frame_FullyOpen_CoversNothing()
        {
            PlaceScreen(screenYawDeg: 0f);
            // frameClose=0 は「覆いが無い」状態。覆いの面の隅まで全部が枠の中でなければ、
            // 段 1〜3（現実がそのまま見えているべき区間）の四隅が黒く欠ける。
            Vector2 size = _veil!.PlaneSize;
            float z = _veil.PlaneDistance;
            // 縁のぼけ帯より外＝完全に「中」でなければならない。帯に掛かるだけでも四隅が翳る。
            float band = -_veil.FeatherAngle(0f);
            foreach (float sx in new[] { -1f, 1f })
            foreach (float sy in new[] { -1f, 1f })
            {
                Vector3 corner = _veil.transform.TransformPoint(
                    new Vector3(sx * size.x * 0.5f, sy * size.y * 0.5f, z));
                Assert.That(_veil.SignedDistance(corner, 0f), Is.LessThan(band),
                    "枠が開き切っているのに覆いの隅が翳る（現実が四隅で欠ける）");
            }
        }

        /// <summary>
        /// 点 <c>中心 + 軸 × 半径 × s</c> が枠の縁に乗る s を二分探索で求める。
        /// 「枠はスクリーンを何倍したものか」を軸ごとに測るための道具。
        /// </summary>
        private float EdgeScale(Vector3 axis, float half, float frameClose)
        {
            float lo = 0.05f, hi = 40f;
            for (int i = 0; i < 60; i++)
            {
                float mid = 0.5f * (lo + hi);
                Vector3 p = _screen!.transform.position + axis * (half * mid);
                if (_veil!.SignedDistance(p, frameClose) < 0f) lo = mid; else hi = mid;
            }
            return 0.5f * (lo + hi);
        }

        [Test]
        public void VeilPlane_SitsAtTheScreenDistance()
        {
            // **両眼視差を消すための条件**（2026-08-01 実害・ユーザーが録画で発見）。
            // 穴は中央眼から解くが描画は左右それぞれの眼から行うので、覆いの面がスクリーンより
            // 手前にあると、同じ穴が左眼では右へ・右眼では左へ寄り、枠の外にスクリーンがはみ出す
            // （実測: 左眼は右側だけ 33,616px / 右眼は左側だけ 27,948px の漏れ）。
            // 面をスクリーンと同じ距離へ置けば、穴の縁のワールド位置がスクリーンの縁と一致する。
            PlaceScreen(screenYawDeg: 0f);
            _veil!.SignedDistance(_screen!.transform.position, Closed);   // 解かせる
            float eye = Vector3.Distance(_veil.transform.position, _screen.transform.position);
            Assert.That(_veil.PlaneDistanceResolved, Is.EqualTo(eye).Within(0.01f),
                $"覆いの面が {_veil.PlaneDistanceResolved:F2}m にある（スクリーンは {eye:F2}m）。" +
                "手前に置くと両眼で枠がずれ、スクリーンが枠からはみ出す。");
        }

        [Test]
        public void FractureAndBaseQuad_UseTheSamePlaneForBothEyes()
        {
            PlaceScreen(screenYawDeg: 0f);
            typeof(IntroVeil).GetMethod("Awake", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(_veil, null);
            _veil!.Apply(new IntroWeights { passthrough = 1f, shatter = 0.4f, ignite = 1f });

            Transform? baseQuad = _veil.transform.Find("IntroVeilQuad");
            Transform? fracture = _veil.transform.Find("IntroVeilFracture");
            Assert.IsNotNull(baseQuad, "スクリーン窓の base quad が無い");
            Assert.IsNotNull(fracture, "破片面が無い");
            Assert.That(fracture!.localPosition.z, Is.EqualTo(baseQuad!.localPosition.z).Within(1e-5f),
                "破片だけ手前にあり、左右眼でスクリーン辺との位置がずれる");
            Assert.That(fracture.localScale.x, Is.EqualTo(baseQuad.localScale.x).Within(1e-5f));
            Assert.That(fracture.localScale.y, Is.EqualTo(baseQuad.localScale.y).Within(1e-5f));
            Assert.AreEqual(_veil.ApertureRectDesc, _veil.ShatterRectDesc,
                "破片の行き先と映像の交差判定が別の矩形を使っている");
        }

        [Test]
        public void Frame_KeepsScreenAspect_WhileClosing()
        {
            PlaceScreen(screenYawDeg: 0f);
            // 閉じている**途中**でも、枠はスクリーンを縦横同じ倍率で拡大した形であること。
            // 旧実装は幅と高さを同じ絶対値から別々に補間していたため、途中は正方形に近く
            // （倍率が横 1.4 倍に対し縦 2.5 倍などになり）、最後だけ 16:9 へ変形していた。
            foreach (float k in new[] { 0.25f, 0.5f, 0.75f, 1f })
            {
                float sx = EdgeScale(_screen!.transform.right, ScreenScale.x * 0.5f, k);
                float sy = EdgeScale(_screen.transform.up, ScreenScale.y * 0.5f, k);
                Assert.That(sx, Is.EqualTo(sy).Within(0.02f * sy),
                    $"閉じ具合 {k:F2} で枠の形がスクリーンと違う（横 {sx:F2} 倍 / 縦 {sy:F2} 倍）。" +
                    "迫ってくる枠が画面と別の形に見える。");
            }
        }

        [Test]
        public void Frame_Closing_ShrinksMonotonically()
        {
            PlaceScreen(screenYawDeg: 0f);
            // スクリーンのすぐ外の点は、閉じるにつれて「外」へ移っていく（枠は縮む一方）。
            Vector3 p = ScreenPoint(1.6f, 0f);
            float prev = float.NegativeInfinity;
            for (float k = 0f; k <= 1.001f; k += 0.1f)
            {
                float d = _veil!.SignedDistance(p, k);
                Assert.That(d, Is.GreaterThanOrEqualTo(prev - 1e-5f), $"閉じ具合 {k:F1} で枠が広がった");
                prev = d;
            }
            Assert.That(prev, Is.GreaterThan(0f), "閉じ切ってもスクリーンの外が枠の中のまま");
        }

        [Test]
        public void Frame_FallsBackToHalfAngles_WhenScreenIsMissing()
        {
            // screenQuad 未配線でも枠は出す（真っ黒で視界を塞ぐより既定の大きさで出す方が安全）。
            SetField(_veil!, "screenQuad", null);
            float z = _veil!.PlaneDistance;
            // 既定は半画角 30.6° / 18.4°。その内側は中・外側は外。
            float inX = z * Mathf.Tan(29f * Mathf.Deg2Rad);
            float outX = z * Mathf.Tan(32f * Mathf.Deg2Rad);
            Assert.That(_veil.SignedDistance(_veil.transform.TransformPoint(new Vector3(inX, 0f, z)), Closed),
                Is.LessThan(0f));
            Assert.That(_veil.SignedDistance(_veil.transform.TransformPoint(new Vector3(outX, 0f, z)), Closed),
                Is.GreaterThan(0f));
        }

        [Test]
        public void Frame_FallsBack_WhenScreenIsBehindTheEye()
        {
            // 背後のスクリーンをそのまま使うと枠が真後ろに現れる。既定へ落ちること。
            _screen!.transform.position = _veil!.transform.position + Vector3.back * 2f;
            float z = _veil.PlaneDistance;
            Assert.That(_veil.SignedDistance(_veil.transform.TransformPoint(new Vector3(0f, 0f, z)), Closed),
                Is.LessThan(0f), "正面が枠の外になっている（背後のスクリーンを使ってしまった）");
        }

        [Test]
        public void Feather_IsConstantAcrossTheClose()
        {
            PlaceScreen(screenYawDeg: 0f);
            // ぼけ幅が 0 になると smoothstep の上下限が一致して縁が壊れる。
            Assert.That(_veil!.FeatherAngle(Closed), Is.GreaterThan(0f));
            // 閉じ切った枠を基準にする（いまの大きさに比例させると、開いているときぼけ幅が
            // 6° 以上に膨らんで覆いの四隅が翳る）。
            Assert.That(_veil.FeatherAngle(0f), Is.EqualTo(_veil.FeatherAngle(Closed)).Within(1e-6f));
            // 2m 先のスクリーン（半画角 18.4°）に対して 8% ＝ 約 1.5°。
            Assert.That(Mathf.Asin(_veil.FeatherAngle(Closed)) * Mathf.Rad2Deg,
                Is.EqualTo(1.47f).Within(0.2f));
        }

        // ---------------- 全開のとき、どの頭の向きでも視界を塞がない ----------------

        /// <summary>Quest 3 の片眼の視野（左右 ±55° / 上下 ±48°・公称からの安全側）。</summary>
        private const float FovHalfHDeg = 55f;
        private const float FovHalfVDeg = 48f;

        /// <summary>枠が無い状態（段 0〜3）。</summary>
        private const float Open = 0f;

        /// <summary>頭のローカル方向（右+ / 上+ の角度）にある点をワールドで返す。</summary>
        private Vector3 EyeDirPoint(float yawDeg, float pitchDeg, float dist = 3f)
        {
            Vector3 dir = Quaternion.Euler(-pitchDeg, yawDeg, 0f) * Vector3.forward;
            return _veil!.transform.TransformPoint(dir * dist);
        }

        private void AssertWholeFovIsOpen(string what, float frameClose)
        {
            foreach (float yaw in new[] { -FovHalfHDeg, 0f, FovHalfHDeg })
            foreach (float pitch in new[] { -FovHalfVDeg, 0f, FovHalfVDeg })
            {
                float d = _veil!.SignedDistance(EyeDirPoint(yaw, pitch), frameClose);
                Assert.That(d, Is.LessThan(0f),
                    $"{what}: 視界の ({yaw}°, {pitch}°) が枠の外にある。" +
                    "実機ではそこから黒帯が出て、現実が途中で途切れて見える。");
            }
        }

        /// <summary>
        /// <b>2026-08-09 の実害を固定する。</b> 全開でも開口は 77.9° の有限の矩形で、しかも中心が
        /// スクリーン（頭から 8° 下・ピッチに追従しない）に固定されていた。頭を 30° 以上下げると
        /// 下辺が視界へ入り、<b>視界の下端から黒帯が出て、下を向くほど広がった</b>
        /// （ユーザー報告「下を向くとパススルーが途中で途切れており、そこには黒い空間が広がっている」）。
        /// </summary>
        [Test]
        public void Open_CoversWholeFov_AtEveryHeadPitch()
        {
            foreach (float headPitch in new[] { 0f, -15f, -30f, -45f, -60f, -80f, 15f, 30f, 45f, 60f })
            {
                _eye!.transform.rotation = Quaternion.Euler(-headPitch, 0f, 0f);
                PlaceScreen(screenYawDeg: 0f);   // スクリーンは水平のまま（ヨーだけ追従）
                AssertWholeFovIsOpen($"頭のピッチ {headPitch}°", Open);
                // 閉じ始めも見る。**全開だけを特別扱いして繋ぎ目で飛ぶ実装**（開口の中心が
                // スクリーンに固定されたまま巨大化する形）だと、ここが落ちる。
                AssertWholeFovIsOpen($"頭のピッチ {headPitch}°・閉じ始め", 0.02f);
            }
        }

        /// <summary>ヨーの追従が遅れて開口が横へずれても、開いている間は視界を塞がないこと。</summary>
        [Test]
        public void Open_CoversWholeFov_WhenScreenYawLagsBehind()
        {
            foreach (float lag in new[] { 0f, 30f, 60f, 90f, 180f })
            {
                _eye!.transform.rotation = Quaternion.identity;
                PlaceScreen(screenYawDeg: lag);
                AssertWholeFovIsOpen($"スクリーンのヨー遅れ {lag}°", Open);
            }
        }

        /// <summary>
        /// 閉じ始めの側も、まだ視界より大きいあいだは塞がないこと。段 4 は静止前提だが、
        /// <b>「全開だけ特別扱いして繋ぎ目で飛ぶ」実装になっていないこと</b>を固定する。
        /// </summary>
        [Test]
        public void JustAfterOpen_StillCoversWholeFov()
        {
            _eye!.transform.rotation = Quaternion.identity;
            PlaceScreen(screenYawDeg: 0f);
            AssertWholeFovIsOpen("閉じ始め", 0.02f);
        }
    }
}
