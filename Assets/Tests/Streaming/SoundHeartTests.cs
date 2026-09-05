#nullable enable
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 心音（<c>canon/LEDGER.md</c> 0175）。ユーザー指定は
    /// 「2-C で連続する人形視点が終わった後から、3-A で左右反転 → 自分が人形になる演出が
    /// 終わるまで、これを流すようにしてほしい」。
    ///
    /// | 縁 | 何を見るか |
    /// |---|---|
    /// | 始点 | 呼びかけのカット（人形視点の連なりの最後）が画面から**降りた**所 |
    /// | 始点（保険） | 3 周目 A へ入った所 — 接近は卓の合図待ちなので走らないことがある |
    /// | 終点 | 入れ替わりの再生（3 周目 A の録画カット）が**終わった**所 |
    /// | 終点（保険） | 呪いの 2 本が立った所（3 周目 B） |
    ///
    /// ⚠⚠ <b>画にも録画にも一撃のログにも 1 ビットも出ない。</b> しかも素材は正体が 150Hz より
    /// 下にあるので（内蔵SP -21.1dB）、実機で耳を当てても確かめられない。走行の
    /// <c>sndHeart</c> と、ここだけが証拠。
    /// </summary>
    public sealed class SoundHeartTests
    {
        private const float Dt = 1f / 72f;

        /// <summary>本編のひとこま。⚠ 3 周目 A より手前を作るときは lap を 3 未満にすること。</summary>
        private static SoundShowState Run(int lap, int camera)
        {
            var s = SoundShowState.Idle;
            s.phase = ShowPhase.Run;
            s.lap = lap;
            s.camera = camera;
            return s;
        }

        private static SoundBedGains Settle(SoundBedLogic logic, SoundShowState s, float sec)
        {
            var g = default(SoundBedGains);
            for (int i = 0; i < (int)(sec / Dt); i++) g = logic.Tick(Dt, s);
            return g;
        }

        // ---- 始点 -------------------------------------------------------------

        /// <summary>
        /// <b>人形視点の連なりが終わった所から鳴り始める。</b> 呼びかけのカットが出ているあいだは
        /// まだ鳴らない（「終わった<b>後</b>から」）。
        /// </summary>
        [Test]
        public void Heart_StartsAfterTheCallCutLeavesTheScreen()
        {
            var logic = new SoundBedLogic();
            var s = Run(2, 2);
            var g = Settle(logic, s, 4f);
            Assert.That(g.heart, Is.EqualTo(0f).Within(1e-3f), "接近の前は鳴らない");

            s.dollCallShowing = true;                 // 連なりの最後のカット（あーそぼー）
            g = Settle(logic, s, 1.4f);
            Assert.That(g.heart, Is.EqualTo(0f).Within(1e-3f), "まだ人形視点が出ているあいだは鳴らない");
            Assert.IsFalse(logic.HeartArmed);

            s.dollCallShowing = false;                // 追いつき（live へ戻る）
            g = Settle(logic, s, 2f);
            Assert.IsTrue(logic.HeartArmed);
            Assert.Greater(g.heart, 0.9f, "追いついた所から心音が立つ");
        }

        /// <summary>
        /// ⚠ <b>立ち上がりは 1.2 秒。</b> 追いつきは事件なので、劇伴の入り（2.5 秒）より速い。
        /// </summary>
        [Test]
        public void Heart_RisesWithinTheFadeInTime()
        {
            var logic = new SoundBedLogic();
            var s = Run(2, 2);
            s.dollCallShowing = true;
            Settle(logic, s, 1f);
            s.dollCallShowing = false;

            var g = Settle(logic, s, SoundBedLogic.HeartFadeInSec * 0.5f);
            Assert.Greater(g.heart, 0.05f, "半ばでは鳴り始めている");
            Assert.Less(g.heart, 0.9f, "半ばで鳴り切ってはいない（点いたように聞こえる）");
            g = Settle(logic, s, SoundBedLogic.HeartFadeInSec);
            Assert.Greater(g.heart, 0.99f);
        }

        /// <summary>
        /// ⚠⚠ <b>接近が走らなくても 3 周目 A では鳴る。</b> 接近は <c>at:"line"</c>（卓の合図待ち・
        /// <c>ifMissed:"skip"</c>）なので、合図が出なければ 1 度も走らない。それでもユーザー指定の
        /// 区間の後半は鳴らす約束なので、区間へ入った縁を保険の始点にしてある。
        /// </summary>
        [Test]
        public void Heart_StartsAtLapThreeAEvenWhenTheApproachWasSkipped()
        {
            var logic = new SoundBedLogic();
            var s = Run(2, 2);
            Settle(logic, s, 4f);                     // 呼びかけは 1 度も出ない

            var g = Settle(logic, Run(3, 0), 2f);
            Assert.IsTrue(logic.HeartArmed);
            Assert.Greater(g.heart, 0.9f);
        }

        // ---- 続き（引き返し）--------------------------------------------------

        /// <summary>
        /// ⚠⚠ <b>引き返しても止まらない。</b> 区間の条件を毎フレーム見る作りだと、体験者が
        /// 2 周目 C から B へ戻った瞬間に脈が止まる（引き返しは実際に起きる —
        /// <c>rules/show-design.md</c>「体験者は引き返す」）。始点は縁で latch してある。
        /// </summary>
        [Test]
        public void Heart_KeepsPlayingWhenTheVisitorWalksBack()
        {
            var logic = new SoundBedLogic();
            var s = Run(2, 2);
            s.dollCallShowing = true;
            Settle(logic, s, 1f);
            s.dollCallShowing = false;
            Settle(logic, s, 2f);

            var g = Settle(logic, Run(2, 1), 4f);     // 2 周目 B へ引き返した
            Assert.IsTrue(logic.HeartArmed);
            Assert.Greater(g.heart, 0.9f, "引き返しても脈は続く");
        }

        // ---- 終点 -------------------------------------------------------------

        /// <summary>
        /// <b>入れ替わりの再生が終わったら退く。</b> 3 周目 A の演出の最後は
        /// 「2 周目 A の録画に人形が立っている再生」で、それが終わって静止画へ落ち着いた所が
        /// 「自分が人形になる演出が終わった」。
        /// </summary>
        [Test]
        public void Heart_StopsWhenTheSwapPlaybackEnds()
        {
            var logic = new SoundBedLogic();
            var s = Run(3, 0);
            var g = Settle(logic, s, 3f);
            Assert.Greater(g.heart, 0.9f, "3 周目 A では鳴っている");

            s.recPlaying = true;                      // 左右反転 → 凍結 → 録画（入れ替わり）
            g = Settle(logic, s, 4f);
            Assert.Greater(g.heart, 0.9f, "見せているあいだは鳴り続ける");

            s.recPlaying = false;                     // 静止画へ落ち着いた ＝ 演出の終わり
            g = Settle(logic, s, SoundBedLogic.HeartFadeOutSec + 0.5f);
            Assert.IsFalse(logic.HeartArmed);
            Assert.That(g.heart, Is.EqualTo(0f).Within(1e-3f));
        }

        /// <summary>
        /// ⚠ <b>1 度退いたら戻らない。</b> 呼びかけの latch は残っているので、素朴に書くと
        /// 次のフレームで鳴り直す（3 相にしてある理由）。
        /// </summary>
        [Test]
        public void Heart_DoesNotComeBackAfterTheSwap()
        {
            var logic = new SoundBedLogic();
            var s = Run(3, 0);
            s.dollCallShowing = true;
            Settle(logic, s, 1f);
            s.dollCallShowing = false;
            s.recPlaying = true;
            Settle(logic, s, 3f);
            s.recPlaying = false;

            var g = Settle(logic, s, 20f);
            Assert.That(g.heart, Is.EqualTo(0f).Within(1e-3f), "終わった後は静かなまま");
        }

        /// <summary>
        /// ⚠⚠ <b>遅くとも 3 周目 B では降りる。</b> そこから背景が呪いの 2 本へ入れ替わるので、
        /// 心音がその上に残ってはいけない（録画が無くて入れ替わりの再生が飛んだ走行の保険でもある）。
        /// </summary>
        [Test]
        public void Heart_StopsAtLapThreeBAtTheLatest()
        {
            var logic = new SoundBedLogic();
            var s = Run(3, 0);
            var g = Settle(logic, s, 3f);
            Assert.Greater(g.heart, 0.9f);

            // 録画が無くて入れ替わりの再生が飛んだ（recPlaying は 1 度も立たない）。
            g = Settle(logic, Run(3, SoundBedLogic.CurseCamera), SoundBedLogic.HeartFadeOutSec + 1f);
            Assert.IsFalse(logic.HeartArmed);
            Assert.That(g.heart, Is.EqualTo(0f).Within(1e-3f));
            Assert.Greater(g.beat, 0.5f, "入れ替わりに呪いの 2 本が立っている");
        }

        // ---- 混ざり方 ---------------------------------------------------------

        /// <summary>
        /// ⚠⚠ <b>置き換えではなく足す。</b> ユーザーは「流すようにしてほしい」としか言っていないので、
        /// 劇伴も装置の声も切らない（<c>bed_relief</c> と同じ扱い）。0131 の 3 つとはそこが違う。
        /// </summary>
        [Test]
        public void Heart_DoesNotReplaceTheScore()
        {
            var logic = new SoundBedLogic();
            var s = Run(3, 0);
            // ⚠ 装置の声は半減期 1.6 秒で寄るので、8 秒まわして 0.97 まで来る。
            var g = Settle(logic, s, 8f);
            Assert.Greater(g.heart, 0.9f);
            Assert.Greater(g.score, 0.99f, "劇伴はそのまま鳴っている");
            Assert.Greater(g.device, 0.9f, "装置の声もそのまま");
        }

        /// <summary>
        /// ⚠ <b>一撃で退かせない。</b> 鳴っている区間には切替の一撃が何度も入るので、
        /// 退かせると<b>脈が切替のたびに飛ぶ</b>（人形の笑いを退かせないのと同じ理由）。
        /// </summary>
        [Test]
        public void Heart_IsNotDuckedByASpotCue()
        {
            var logic = new SoundBedLogic();
            var s = Run(3, 0);
            var before = Settle(logic, s, 8f);

            logic.PushSpotDuck(1f);
            var g = logic.Tick(Dt, s);
            Assert.Greater(g.heart, 0.99f, "一撃が鳴っても脈は凹まない");
            // ⚠ 計器の校正: 退きの仕組み自体は効いていること（地の音は同じ 1 フレームで下がる）。
            //    ここが下がらないなら、上の「凹まない」は何も言っていない。
            Assert.Less(g.device, before.device * 0.95f, "地の音は退いている");
        }

        /// <summary>位置合わせ中は黙る（スタッフが実物へ線を重ねているあいだ脈は要らない）。</summary>
        [Test]
        public void Heart_IsSilentDuringRegistration()
        {
            var logic = new SoundBedLogic();
            var s = Run(3, 0);
            Settle(logic, s, 4f);

            s.registrationActive = true;
            var g = Settle(logic, s, SoundBedLogic.HeartFadeOutSec + 0.5f);
            Assert.That(g.heart, Is.EqualTo(0f).Within(1e-3f));
        }

        // ---- 体験者の交代 -----------------------------------------------------

        /// <summary>
        /// ⚠⚠ <b>次の体験者でもう 1 度鳴る。</b> 3 相を戻し忘れると <c>After</c> のまま残り、
        /// <b>2 人目以降は 1 度も鳴らない</b>（<c>memory/visitor_sound_reset.md</c> と同じ形の事故 —
        /// 画にも録画にも出ない）。
        /// </summary>
        [Test]
        public void Heart_RingsAgainForTheNextVisitor()
        {
            var logic = new SoundBedLogic();
            var s = Run(3, 0);
            s.recPlaying = true;
            Settle(logic, s, 3f);
            s.recPlaying = false;
            Settle(logic, s, 4f);
            Assert.IsFalse(logic.HeartArmed);

            logic.Reset();
            var g = Settle(logic, Run(3, 0), 3f);
            Assert.IsTrue(logic.HeartArmed);
            Assert.Greater(g.heart, 0.9f);
        }
    }
}
