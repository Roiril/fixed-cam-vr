#nullable enable
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// BgmPlanLogic（区間 BGM 指示 → 遷移）の純判定と、ループ窓の正規化を固定する。
    /// 「指示の無い区間では曲が途切れない」「同一トラックは Retune で位置を保つ」が体験上の要。
    /// </summary>
    public sealed class BgmPlanLogicTests
    {
        private const string Play = BgmPlanLogic.ActionPlay;
        private const string Stop = BgmPlanLogic.ActionStop;
        private const string Cont = BgmPlanLogic.ActionContinue;

        // ---- Decide ----------------------------------------------------------

        [Test]
        public void 指示なしの区間は何もしない()
        {
            Assert.AreEqual(BgmPlanLogic.BgmChange.None,
                BgmPlanLogic.Decide(false, Play, "t1", false, playing: true, currentTrackId: "t0"));
        }

        [Test]
        public void continue_は鳴っていても止めない()
        {
            Assert.AreEqual(BgmPlanLogic.BgmChange.None,
                BgmPlanLogic.Decide(true, Cont, "", false, playing: true, currentTrackId: "t0"));
        }

        [Test]
        public void 無音から_play_で開始する()
        {
            Assert.AreEqual(BgmPlanLogic.BgmChange.Start,
                BgmPlanLogic.Decide(true, Play, "t1", false, playing: false, currentTrackId: ""));
        }

        [Test]
        public void 別トラックの_play_はクロスフェード開始()
        {
            Assert.AreEqual(BgmPlanLogic.BgmChange.Start,
                BgmPlanLogic.Decide(true, Play, "t2", false, playing: true, currentTrackId: "t1"));
        }

        [Test]
        public void 同一トラックの_play_は位置を保つ_Retune()
        {
            Assert.AreEqual(BgmPlanLogic.BgmChange.Retune,
                BgmPlanLogic.Decide(true, Play, "t1", false, playing: true, currentTrackId: "t1"));
        }

        [Test]
        public void 同一トラックでも_restart_なら頭出しし直す()
        {
            Assert.AreEqual(BgmPlanLogic.BgmChange.Start,
                BgmPlanLogic.Decide(true, Play, "t1", restart: true, playing: true, currentTrackId: "t1"));
        }

        [Test]
        public void stop_は鳴っている時だけ止める()
        {
            Assert.AreEqual(BgmPlanLogic.BgmChange.Stop,
                BgmPlanLogic.Decide(true, Stop, "", false, playing: true, currentTrackId: "t1"));
            Assert.AreEqual(BgmPlanLogic.BgmChange.None,
                BgmPlanLogic.Decide(true, Stop, "", false, playing: false, currentTrackId: ""));
        }

        [Test]
        public void trackId_が空の_play_は不成立で無視する()
        {
            Assert.AreEqual(BgmPlanLogic.BgmChange.None,
                BgmPlanLogic.Decide(true, Play, "", false, playing: false, currentTrackId: ""));
        }

        [Test]
        public void 未知の_action_は_continue_扱い()
        {
            Assert.AreEqual(BgmPlanLogic.BgmChange.None,
                BgmPlanLogic.Decide(true, "pause", "t1", false, playing: true, currentTrackId: "t1"));
            Assert.AreEqual(BgmPlanLogic.BgmChange.None,
                BgmPlanLogic.Decide(true, null, "t1", false, playing: true, currentTrackId: "t1"));
        }

        // ---- NormalizeWindow -------------------------------------------------

        [Test]
        public void ループ終端未設定はクリップ末尾になる()
        {
            var (st, ls, le) = BgmPlanLogic.NormalizeWindow(120f, 0f, 0f, 0f);
            Assert.AreEqual(0f, st, 1e-4f);
            Assert.AreEqual(0f, ls, 1e-4f);
            Assert.AreEqual(120f, le, 1e-4f);
        }

        [Test]
        public void ループ範囲はクリップ長にクランプされる()
        {
            var (_, ls, le) = BgmPlanLogic.NormalizeWindow(30f, 0f, 5f, 999f);
            Assert.AreEqual(5f, ls, 1e-4f);
            Assert.AreEqual(30f, le, 1e-4f);
        }

        [Test]
        public void ループ範囲の逆転は末尾までに開く()
        {
            var (_, ls, le) = BgmPlanLogic.NormalizeWindow(60f, 0f, 40f, 10f);
            Assert.AreEqual(40f, ls, 1e-4f);
            Assert.AreEqual(60f, le, 1e-4f);
            Assert.Less(ls, le);
        }

        [Test]
        public void 潰れたループ範囲は全長へ戻す()
        {
            // loopStart がクリップ長ちょうど（= 窓が作れない）→ 全長へ
            var (_, ls, le) = BgmPlanLogic.NormalizeWindow(60f, 0f, 60f, 0f);
            Assert.AreEqual(0f, ls, 1e-4f);
            Assert.AreEqual(60f, le, 1e-4f);
        }

        [Test]
        public void 開始位置が窓の外なら窓の頭へ丸める()
        {
            var (st, ls, _) = BgmPlanLogic.NormalizeWindow(120f, 2f, 30f, 90f);
            Assert.AreEqual(ls, st, 1e-4f);   // 窓より前 → 窓頭
            var (st2, _, _) = BgmPlanLogic.NormalizeWindow(120f, 100f, 30f, 90f);
            Assert.AreEqual(30f, st2, 1e-4f); // 窓より後 → 窓頭
        }

        [Test]
        public void 開始位置が窓の中ならそのまま()
        {
            var (st, _, _) = BgmPlanLogic.NormalizeWindow(120f, 45f, 30f, 90f);
            Assert.AreEqual(45f, st, 1e-4f);
        }

        [Test]
        public void クリップ長ゼロは全部ゼロ()
        {
            var (st, ls, le) = BgmPlanLogic.NormalizeWindow(0f, 5f, 1f, 2f);
            Assert.AreEqual(0f, st, 1e-4f);
            Assert.AreEqual(0f, ls, 1e-4f);
            Assert.AreEqual(0f, le, 1e-4f);
        }

        // ---- DecideRestore（演出が音を占有した後、区間の音へ戻る）-----------------

        [Test]
        public void 演出明けは区間の曲へ戻す()
        {
            // 演出が別の曲を鳴らしていた → レーンの曲へクロスフェードで戻る
            Assert.AreEqual(BgmPlanLogic.BgmChange.Start,
                BgmPlanLogic.DecideRestore(playing: true, currentTrackId: "scare", laneTrackId: "amb"));
        }

        [Test]
        public void 演出が曲を止めていたら区間の曲を鳴らし直す()
        {
            Assert.AreEqual(BgmPlanLogic.BgmChange.Start,
                BgmPlanLogic.DecideRestore(playing: false, currentTrackId: "", laneTrackId: "amb"));
        }

        [Test]
        public void 区間が無音なら演出明けは止める()
        {
            Assert.AreEqual(BgmPlanLogic.BgmChange.Stop,
                BgmPlanLogic.DecideRestore(playing: true, currentTrackId: "scare", laneTrackId: ""));
            Assert.AreEqual(BgmPlanLogic.BgmChange.None,
                BgmPlanLogic.DecideRestore(playing: false, currentTrackId: "", laneTrackId: ""),
                "元から無音なら何もしない");
        }

        [Test]
        public void 演出が同じ曲のままなら戻す必要はない()
        {
            Assert.AreEqual(BgmPlanLogic.BgmChange.None,
                BgmPlanLogic.DecideRestore(playing: true, currentTrackId: "amb", laneTrackId: "amb"));
        }

        // ---- 折り返しのクロスフェード（LEDGER 0049）---------------------------------

        [Test]
        public void 折り返しの秒は窓の四分の一で_上限と下限に丸める()
        {
            Assert.AreEqual(1.5f, BgmPlanLogic.LoopCrossfadeSec(0f, 120f), 1e-5f, "長い窓は上限 1.5 秒");
            Assert.AreEqual(1.5f, BgmPlanLogic.LoopCrossfadeSec(10f, 16f), 1e-5f, "窓 6 秒 = 1.5 秒ちょうど");
            Assert.AreEqual(0.5f, BgmPlanLogic.LoopCrossfadeSec(0f, 2f), 1e-5f, "短い窓は 1/4");
            Assert.AreEqual(0.05f, BgmPlanLogic.LoopCrossfadeSec(0f, 0.1f), 1e-5f, "極端に短い窓は下限 0.05 秒");
            Assert.AreEqual(0.05f, BgmPlanLogic.LoopCrossfadeSec(5f, 5f), 1e-5f, "潰れた窓も下限");
        }

        [Test]
        public void 折り返しは終端から秒を引いた位置で始まる()
        {
            // 窓 10〜110（100 秒）→ 秒は上限 1.5 → 108.5 から
            Assert.IsFalse(BgmPlanLogic.ShouldStartLoopCrossfade(true, 108.49f, 10f, 110f));
            Assert.IsTrue(BgmPlanLogic.ShouldStartLoopCrossfade(true, 108.5f, 10f, 110f));
            Assert.IsTrue(BgmPlanLogic.ShouldStartLoopCrossfade(true, 109.9f, 10f, 110f));
            // 全長ループ（0〜120）
            Assert.IsFalse(BgmPlanLogic.ShouldStartLoopCrossfade(true, 0f, 0f, 120f));
            Assert.IsTrue(BgmPlanLogic.ShouldStartLoopCrossfade(true, 118.5f, 0f, 120f));
        }

        [Test]
        public void loop_false_は折り返しをしない()
        {
            Assert.IsFalse(BgmPlanLogic.ShouldStartLoopCrossfade(false, 119.9f, 0f, 120f));
            Assert.IsFalse(BgmPlanLogic.ShouldStartLoopCrossfade(false, 108.5f, 10f, 110f));
        }

        [Test]
        public void 短い窓では秒が割合で縮み_窓の外へ出るほど短いなら始めない()
        {
            // 窓 2 秒 → 秒 0.5。1.5 秒の位置から。
            Assert.IsFalse(BgmPlanLogic.ShouldStartLoopCrossfade(true, 1.49f, 0f, 2f));
            Assert.IsTrue(BgmPlanLogic.ShouldStartLoopCrossfade(true, 1.5f, 0f, 2f));
            // 窓 0.1 秒 → 秒 0.05（下限）= 窓の半分。ちょうど始められる。
            Assert.IsTrue(BgmPlanLogic.ShouldStartLoopCrossfade(true, 0.05f, 0f, 0.1f));
            // 窓 0.09 秒 → 2 × 0.05 に満たない。受け側が窓から出るので始めない（従来の即時巻き戻し）。
            Assert.IsFalse(BgmPlanLogic.ShouldStartLoopCrossfade(true, 0.089f, 0f, 0.09f));
            Assert.IsFalse(BgmPlanLogic.ShouldStartLoopCrossfade(true, 5f, 5f, 5f), "潰れた窓");
        }

        [Test]
        public void 開始が遅れた分は重なり済みの秒として返る()
        {
            Assert.AreEqual(0f, BgmPlanLogic.LoopCrossfadeOverlap(108.5f, 110f, 1.5f), 1e-5f);
            Assert.AreEqual(0.4f, BgmPlanLogic.LoopCrossfadeOverlap(108.9f, 110f, 1.5f), 1e-4f);
            Assert.AreEqual(1.5f, BgmPlanLogic.LoopCrossfadeOverlap(111f, 110f, 1.5f), 1e-5f, "窓を越えても秒で頭打ち");
            Assert.AreEqual(0f, BgmPlanLogic.LoopCrossfadeOverlap(100f, 110f, 1.5f), 1e-5f, "手前は 0");
        }

        [Test]
        public void 折り返しの倍率は全域で二乗の和が_1_になる()
        {
            for (int i = 0; i <= 100; i++)
            {
                float t = i / 100f;
                BgmPlanLogic.LoopCrossfadeGains(t, out float gOut, out float gIn);
                Assert.AreEqual(1f, gOut * gOut + gIn * gIn, 1e-5f, $"t={t}");
            }
            BgmPlanLogic.LoopCrossfadeGains(0f, out float o0, out float i0);
            Assert.AreEqual(1f, o0, 1e-6f);
            Assert.AreEqual(0f, i0, 1e-6f);
            BgmPlanLogic.LoopCrossfadeGains(1f, out float o1, out float i1);
            Assert.AreEqual(0f, o1, 1e-6f);
            Assert.AreEqual(1f, i1, 1e-6f);
        }
    }
}
