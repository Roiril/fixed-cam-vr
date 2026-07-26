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
    }
}
