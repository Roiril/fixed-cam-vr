#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// **音の設計そのものを固定するテスト。** 数値の良し悪しではなく、
    /// 「この作品の音はこうでなければならない」という決めごとを機械で守る。
    /// 設計の正本は <c>.claude/rules/sound-design.md</c>。ここを変えるならそちらを先に直す。
    /// </summary>
    public class SoundBedLogicTests
    {
        private static SoundShowState Intro(IntroStage stage, float shell = 0f,
                                            float degrade = 0f, float shatter = 0f, float live = 0f)
        {
            var s = SoundShowState.Idle;
            s.introActive = true;
            s.introStage = stage;
            s.introWeights = IntroWeights.Inactive;
            s.introWeights.shell = shell;
            s.introWeights.degrade = degrade;
            s.introWeights.shatter = shatter;
            s.introWeights.live = live;
            return s;
        }

        [Test]
        public void Title_IsSilent()
        {
            // タイトルは黒。世界の手前なので何も鳴らない。
            // ⚠ 2026-08-15 まで、ここで封印の箱の唸りを先に鳴らして J カットにしていた
            //   （`canon/LEDGER.md` 0044 で箱を退避したので定位する先が無くなった）。
            var s = SoundShowState.Idle;
            s.titleVisible = true;
            var g = SoundBedLogic.Target(s);
            Assert.AreEqual(0f, g.seal, 1e-6f, "退避した箱が鳴っている");
            Assert.AreEqual(0f, g.room, 1e-6f);
            Assert.AreEqual(0f, g.device, 1e-6f);
        }

        [Test]
        public void Device_ArrivesBeforeThePicture_AtStageDegrade()
        {
            // ⚠ **この 1 本が導入の音設計の核心。** 装置の声は「画が映像になる」より前、
            // 段 2（色が抜ける）で入り始める。これが 13 秒を 1 つの出来事として繋ぐ。
            Assert.AreEqual(0f, SoundBedLogic.Target(Intro(IntroStage.Black)).device, 1e-6f,
                            "段 0 で装置が鳴っている（まだ現実のはず）");
            Assert.AreEqual(0f, SoundBedLogic.Target(Intro(IntroStage.Real)).device, 1e-6f,
                            "段 1 は素のパススルー（比較対象なので何も足さない）");
            float mid = SoundBedLogic.Target(Intro(IntroStage.Degrade, degrade: 0.5f)).device;
            Assert.Greater(mid, 0f, "段 2 で装置が入り始めていない");
            Assert.Less(mid, SoundBedLogic.Target(Intro(IntroStage.Swap, live: 1f)).device);
        }

        [Test]
        public void Device_ReachesFull_OnlyAfterTheVideoArrives()
        {
            var swap = SoundBedLogic.Target(Intro(IntroStage.Swap, live: 1f));
            Assert.AreEqual(1f, swap.device, 1e-3f);
            var run = SoundShowState.Idle;
            run.phase = ShowPhase.Run;
            Assert.AreEqual(1f, SoundBedLogic.Target(run).device, 1e-6f);
        }

        [Test]
        public void Isolation_ClosesTheBand_NotTheVolume()
        {
            // ⚠ 隔離は「音量を下げる」ではなく「帯域を閉じる」で表す。
            //    音量を下げると **遠ざかった** に聞こえ、閉じ込められた感じにならない。
            var open = SoundBedLogic.Target(Intro(IntroStage.Swap, shell: 0f));
            var shut = SoundBedLogic.Target(Intro(IntroStage.Swap, shell: 1f));
            Assert.AreEqual(open.room, shut.room, 1e-6f, "隔離で部屋の音量が変わっている");
            Assert.Less(shut.roomOpen, open.roomOpen, "隔離で帯域が閉じていない");
            Assert.AreEqual(SoundBedLogic.RoomOpenSealed, shut.roomOpen, 1e-4f);
            Assert.Greater(shut.roomOpen, 0f, "完全に閉じると無響になって不自然");
        }

        [Test]
        public void SealedBox_NeverHums_InAnyStage()
        {
            // ⚠⚠ 封印の箱は 2026-08-15 に退避した（`canon/LEDGER.md` 0044）。
            //    `bed_seal` は 3D で箱の面に置いていたので、定位する先が無い。
            //    音源は残してあるので、重みが 1 フレームでも立つと**黙って唸りが戻る**。
            foreach (IntroStage st in System.Enum.GetValues(typeof(IntroStage)))
                Assert.AreEqual(0f, SoundBedLogic.Target(Intro(st, shell: 1f, live: 1f)).seal, 1e-6f,
                                $"段 {st} で退避した箱が鳴っている");
        }

        [Test]
        public void SignalLost_DucksTheDeviceAndRoom()
        {
            var s = SoundShowState.Idle;
            s.phase = ShowPhase.Run;
            s.signalLost = 1f;
            var g = SoundBedLogic.Target(s);
            Assert.AreEqual(1f, g.noise, 1e-6f);
            Assert.AreEqual(SoundBedLogic.NoiseDuckScale, g.device, 1e-4f);
            Assert.Less(g.room, SoundBedLogic.RoomInRun);
        }

        [Test]
        public void Registration_PullsEverythingDown_SoStaffCanWork()
        {
            // 位置合わせは**現実に線を重ねる作業**。視界から覆いを全部どけるのと同じ理屈で、
            // 音も引く（rules/show-design.md「位置合わせ中は現実を隠すものを全部どける」）。
            var s = SoundShowState.Idle;
            s.phase = ShowPhase.Run;
            s.registrationActive = true;
            var g = SoundBedLogic.Target(s);
            Assert.AreEqual(SoundBedLogic.RegistrationDuckScale, g.device, 1e-4f);
            Assert.AreEqual(1f, g.roomOpen, 1e-6f, "作業中に部屋を閉じない");
            Assert.AreEqual(1f, g.duck, 1e-6f, "作業中は劇伴を止める");
        }

        [Test]
        public void Finished_EndsInSilence()
        {
            // ⚠ 終わりに音を残さない。最後に鳴っているのは**無音**が正しい
            // （work-style.md 規約4「決め台詞で締めない」の音版）。
            var s = SoundShowState.Idle;
            s.phase = ShowPhase.Finished;
            var g = SoundBedLogic.Target(s);
            Assert.AreEqual(0f, g.seal + g.room + g.device + g.noise, 1e-6f);
        }

        [Test]
        public void Outro_NeverRisesAboveTheIntro()
        {
            // 終幕は導入の逆をたどるが、**山を作らない**。
            float introPeak = SoundBedLogic.Target(Intro(IntroStage.Swap, live: 1f)).device;
            foreach (OutroStage st in System.Enum.GetValues(typeof(OutroStage)))
            {
                var s = SoundShowState.Idle;
                s.outroActive = true;
                s.outroStage = st;
                var g = SoundBedLogic.Target(s);
                Assert.LessOrEqual(g.device, introPeak + 1e-4f, $"終幕 {st} で装置が導入を超えた");
            }
        }

        [Test]
        public void Decay_CrossfadesDeviceEqualPower_NoDipInTheMiddle()
        {
            // 周回で装置の声が痩せる。2 本の録り分けは無相関なので、**線形に混ぜると途中で凹む**。
            var logic = new SoundBedLogic();
            var s = SoundShowState.Idle;
            s.phase = ShowPhase.Run;
            // ⚠ 立ち上がりの半減期が 1.6 秒なので、**十分に収束させてから**測る。
            //    途中で測ると「まだ上がりきっていない」を「谷」と読んでしまう。
            for (int i = 0; i < 2400; i++) logic.Tick(1f / 60f, s);

            for (int step = 0; step <= 10; step++)
            {
                s.decay = step / 10f;
                var g = logic.Tick(1f / 60f, s);
                float power = g.device * g.device + g.deviceWorn * g.deviceWorn;
                Assert.AreEqual(1f, power, 0.02f,
                                $"decay={s.decay} で装置の合成パワーが凹んだ / 膨らんだ");
            }
        }

        [Test]
        public void SpotDuck_Decays_AndNeverSticks()
        {
            // 引いたまま居座る事故を作らない（この codebase は「凍結が解けない」を 4 回踏んでいる）。
            var logic = new SoundBedLogic();
            var s = SoundShowState.Idle;
            s.phase = ShowPhase.Run;
            logic.PushSpotDuck(0.9f);
            SoundBedGains g = default;
            for (int i = 0; i < 12; i++) g = logic.Tick(1f / 60f, s);   // 0.2 秒
            Assert.Greater(g.duck, 0.5f, "一撃で劇伴が引けていない");
            for (int i = 0; i < 600; i++) g = logic.Tick(1f / 60f, s);
            Assert.Less(g.duck, 0.01f, "引きが戻っていない");
        }

        [Test]
        public void SpotDuck_AlsoPullsTheBeds_SoTheEventIsNotBuried()
        {
            // ⚠ 劇伴だけ引いて地の音を残すと、隔離が閉じる音も割れる音も地に埋もれる。
            //    引きは**出力にだけ**掛かり、状態には残らない（残すと地が静かに痩せていく）。
            var logic = new SoundBedLogic();
            var s = SoundShowState.Idle;
            s.phase = ShowPhase.Run;
            SoundBedGains flat = default;
            for (int i = 0; i < 2400; i++) flat = logic.Tick(1f / 60f, s);

            logic.PushSpotDuck(1f);
            SoundBedGains ducked = default;
            for (int i = 0; i < 12; i++) ducked = logic.Tick(1f / 60f, s);
            Assert.Less(ducked.device + ducked.deviceWorn, flat.device + flat.deviceWorn,
                        "一撃で地が退いていない");
            Assert.Greater(ducked.device + ducked.deviceWorn, 0.05f,
                           "地ごと消えている（装置が生きている前提が壊れる）");

            // 引きが戻れば元の高さへ戻る（引いた状態が居座らない）
            SoundBedGains back = default;
            for (int i = 0; i < 600; i++) back = logic.Tick(1f / 60f, s);
            Assert.AreEqual(flat.device + flat.deviceWorn, back.device + back.deviceWorn, 1e-2f);
        }

        // ---- 周ごとの環境音（2026-08-15・canon/LEDGER.md 0049）--------------------

        private static SoundShowState Run(int lap)
        {
            var s = SoundShowState.Idle;
            s.phase = ShowPhase.Run;
            s.lap = lap;
            return s;
        }

        private static SoundBedGains Settle(SoundBedLogic l, in SoundShowState s, float sec = 30f)
        {
            SoundBedGains g = default;
            for (int i = 0; i < (int)(sec * 90f); i++) g = l.Tick(1f / 90f, s);
            return g;
        }

        [Test]
        public void Ambient_SwapsPerLap()
        {
            var l = new SoundBedLogic();

            var g1 = Settle(l, Run(1));
            Assert.Greater(g1.roomLap1, 0.97f, "1 周目は合成の環境音");
            Assert.Less(g1.roomLap2 + g1.roomLap3, 0.05f);

            var g2 = Settle(l, Run(2));
            Assert.Greater(g2.roomLap2, 0.97f, "2 周目で入れ替わっていない");

            var g3 = Settle(l, Run(3));
            Assert.Greater(g3.roomLap3, 0.97f, "3 周目で入れ替わっていない");

            // 帰りの A（lap 4）は 3 周目の続き。**ここで 1 周目へ戻ると終わりで空気が変わる。**
            var g4 = Settle(l, Run(4));
            Assert.Greater(g4.roomLap3, 0.97f, "帰りの区間で環境音が戻ってしまった");
        }

        /// <summary>
        /// ⚠⚠ <b>入れ替えの最中に音の密度が凹まない。</b> ユーザー指示は
        /// 「差し替えを気づかれないようにクロスフェード」（`canon/LEDGER.md` 0049）で、
        /// 線形に混ぜると真ん中で -3dB の谷ができ、その谷こそが「切り替わった」の合図になる。
        /// </summary>
        [Test]
        public void Ambient_CrossfadeKeepsPowerConstant()
        {
            var l = new SoundBedLogic();
            Settle(l, Run(1));

            var s = Run(2);
            float minPower = 1f, maxPower = 1f;
            for (int i = 0; i < 90 * 20; i++)
            {
                var g = l.Tick(1f / 90f, s);
                float p = g.roomLap1 * g.roomLap1 + g.roomLap2 * g.roomLap2 + g.roomLap3 * g.roomLap3;
                if (p < minPower) minPower = p;
                if (p > maxPower) maxPower = p;
            }
            Assert.AreEqual(1f, minPower, 1e-3f, "入れ替えの最中に密度が凹んでいる");
            Assert.AreEqual(1f, maxPower, 1e-3f, "入れ替えの最中に密度が膨らんでいる");
        }

        [Test]
        public void Ambient_TakesSeveralSeconds_NotOneFrame()
        {
            // **1 フレームで切り替わると「差し替えた」と分かる。**
            var l = new SoundBedLogic();
            Settle(l, Run(1));

            var s = Run(2);
            var oneFrame = l.Tick(1f / 90f, s);
            Assert.Greater(oneFrame.roomLap1, 0.9f, "1 フレームで入れ替わっている");

            float t = 1f / 90f;
            while (t < 30f)
            {
                var g = l.Tick(1f / 90f, s);
                t += 1f / 90f;
                if (g.roomLap2 > 0.99f) break;
            }
            Assert.Greater(t, 4f, "入れ替えが速すぎる（気づかれる）");
            Assert.Less(t, 20f, "入れ替えが遅すぎる（区間を跨いでしまう）");
        }

        [Test]
        public void Ambient_ResetsToFirstLap_ForTheNextVisitor()
        {
            var l = new SoundBedLogic();
            Settle(l, Run(3));

            l.Reset();
            var g = l.Tick(1f / 90f, Run(1));
            Assert.Greater(g.roomLap1, 0.99f, "前の体験者の 3 周目の環境音から始まっている");
        }

        // ---------------------------------------------------------------- 人形の笑い

        /// <summary>締めのカットが報告を待っている状態（人形がたくさん出てくる所）。</summary>
        private static SoundShowState Closing(bool waiting, bool registering = false)
        {
            var s = Run(4);
            s.markWaiting = waiting;
            s.registrationActive = registering;
            return s;
        }

        [Test]
        public void Dolls_LaughWhileTheClosingCutWaits_AndStopWhenTheVisitorReports()
        {
            // ⚠ **一撃ではなくループ**（2026-08-16・`canon/LEDGER.md` 0066）。
            //    報告を押すまで鳴り続け、押したら止まる。
            var l = new SoundBedLogic();
            Assert.AreEqual(0f, Settle(l, Run(4)).dolls, 1e-3f, "待っていないのに笑っている");

            var g = Settle(l, Closing(true), sec: 3f);
            Assert.Greater(g.dolls, 0.99f, "締めのカットが待っているのに笑わない");

            // 40 秒待たされても鳴り続ける（一撃なら消えている）。
            Assert.Greater(Settle(l, Closing(true), sec: 40f).dolls, 0.99f, "途中で止まった");

            // 報告した → 止まる。
            Assert.Less(Settle(l, Closing(false), sec: 3f).dolls, 0.01f, "押しても止まらない");
        }

        [Test]
        public void Dolls_DuckTheScoreWhileTheyLaugh()
        {
            // ⚠ 一撃の退き（`PushSpotDuck`）と違い、**鳴っているあいだずっと**引く。
            var l = new SoundBedLogic();
            var g = Settle(l, Closing(true), sec: 5f);
            Assert.GreaterOrEqual(g.duck, SoundBedLogic.DollsDuck - 1e-2f, "劇伴が退いていない");
            // 笑いそのものは退かせない（自分で自分を引いたら意味が無い）。
            Assert.Greater(g.dolls, 0.99f);
        }

        [Test]
        public void Dolls_AreSilentDuringRegistration_AndAfterTheShowEnds()
        {
            var l = new SoundBedLogic();
            Assert.Less(Settle(l, Closing(true, registering: true), sec: 5f).dolls, 0.01f,
                        "位置合わせ中に人形が笑っている");

            var done = Closing(true);
            done.phase = ShowPhase.Finished;
            Assert.Less(Settle(new SoundBedLogic(), done, sec: 5f).dolls, 0.01f,
                        "体験が終わったのに笑っている");
        }

        // ------------------------------------------- 入れ替わった人形の笑い（3 周目）

        /// <summary>映像の中で体験者の場所に人形が立っている状態（<c>canon/LEDGER.md</c> 0086）。</summary>
        private static SoundShowState Swapped(int camera, bool present = true)
        {
            var s = Run(3);
            s.dollPresent = present;
            s.camera = camera;
            return s;
        }

        [Test]
        public void Swap_TheDollStartsLaughing_TheMomentItReplacesTheVisitor()
        {
            // ⚠ 立った縁から鳴り始める（入れ替わりの瞬間に 1 声目が来る）。
            var l = new SoundBedLogic();
            Assert.AreEqual(0f, Settle(l, Run(3)).dollOne, 1e-3f, "人形が居ないのに笑っている");

            // 0.5 秒（半減期 0.25 秒 × 2）で 3/4 は立っている。
            var s = Swapped(camera: 0);
            SoundBedGains g = default;
            for (int i = 0; i < 45; i++) g = l.Tick(1f / 90f, s);
            Assert.Greater(g.dollOne, 0.7f, "入れ替わったのに笑い出さない");
        }

        [Test]
        public void Swap_OnlyOneDollLaughs_InZonesAandB()
        {
            // ユーザー指定「3-A,3-Bでは一人の女の子が笑ってる感じ」。
            foreach (int cam in new[] { 0, 1 })
            {
                var l = new SoundBedLogic();
                var g = Settle(l, Swapped(cam), sec: 30f);
                Assert.Greater(g.dollOne, 0.99f, $"カメラ {cam} で一人ぶんが鳴っていない");
                Assert.Less(g.dollsGrowA, 0.01f, $"カメラ {cam} で人形が増えている");
                Assert.Less(g.dollsGrowB, 0.01f, $"カメラ {cam} で人形が増えている");
            }
        }

        [Test]
        public void Swell_TheDollsIncreaseOverTime_OnlyInZoneC()
        {
            // ユーザー指定「3-Cでは徐々に増えていく感じ」。**単調に増える**ことまで見る
            //（途中で減ると「増えていく」ではなくなる）。
            var l = new SoundBedLogic();
            var s = Swapped(camera: SoundBedLogic.SwellCamera);
            float prev = -1f;
            SoundBedGains g = default;
            for (int i = 0; i < (int)(SoundBedLogic.SwellRiseSec * 90f); i++)
            {
                g = l.Tick(1f / 90f, s);
                float sum = g.dollsGrowA + g.dollsGrowB;
                Assert.GreaterOrEqual(sum + 1e-4f, prev, "増える途中で減っている");
                prev = sum;
            }
            Assert.Greater(g.dollsGrowA, 0.99f, "C に居続けたのに 2 体目が入り切らない");
            Assert.Greater(g.dollsGrowB, 0.99f, "C に居続けたのに 3 枚目が入り切らない");

            // 2 枚目が先、3 枚目が後（一度に全部来ない ＝ 段が付かない）。
            var l2 = new SoundBedLogic();
            var half = Settle(l2, s, sec: SoundBedLogic.SwellRiseSec * 0.4f);
            Assert.Greater(half.dollsGrowA, half.dollsGrowB, "2 枚目と 3 枚目が同時に来ている");
        }

        [Test]
        public void Swell_FallsBack_WhenTheVisitorLeavesZoneC()
        {
            // 引き返しても、増えた人形がその場に残らない。
            var l = new SoundBedLogic();
            Settle(l, Swapped(camera: SoundBedLogic.SwellCamera), sec: 30f);
            var g = Settle(l, Swapped(camera: 1), sec: SoundBedLogic.SwellFallSec + 2f);
            Assert.Less(g.dollsGrowA, 0.01f, "C を出たのに増えた人形が残っている");
            Assert.Greater(g.dollOne, 0.99f, "一人ぶんまで消えている（笑うのは C だけではない）");
        }

        [Test]
        public void Swap_SurvivesTheGapBetweenSegments()
        {
            // ⚠⚠ **区間の継ぎ目で人形が数フレーム消えても、笑いに穴を開けない。**
            //    3 周目は区間ごとに別の演出が走るので、カットの入れ替わりで CG が一瞬消える。
            var l = new SoundBedLogic();
            Settle(l, Swapped(camera: 0), sec: 5f);

            var gone = Swapped(camera: 1, present: false);
            SoundBedGains g = default;
            for (int i = 0; i < (int)(0.5f * 90f); i++) g = l.Tick(1f / 90f, gone);
            Assert.Greater(g.dollOne, 0.99f, "区間の継ぎ目で笑いが途切れた");

            // 保持を過ぎたら止まる（人形が本当に居なくなったら黙る）。
            g = Settle(l, gone, sec: SoundBedLogic.DollHoldSec + 8f);
            Assert.Less(g.dollOne, 0.01f, "人形が消えたのに笑い続けている");
        }

        [Test]
        public void Swap_AndTheClosingCrowd_NeverSoundTogether()
        {
            // 4 周目 A も人形は立っているが、あちらは「たくさん出てくる」場面。
            // 一人ぶんが混ざると数が濁る。
            var s = Swapped(camera: 0);
            s.lap = 4;
            s.markWaiting = true;
            var g = SoundBedLogic.Target(s);
            Assert.AreEqual(1f, g.dolls, 1e-6f, "締めの群れが鳴っていない");
            Assert.AreEqual(0f, g.dollOne, 1e-6f, "群れと入れ替わりの笑いが同時に鳴っている");
        }

        [Test]
        public void Swap_DoesNotComeBack_AfterTheVisitorReports()
        {
            // ⚠⚠ 実機で踏んだ（2026-08-18）。4 周目 A は「人形が立っている ＋ 報告待ち」なので
            //    一人ぶんは黙るが、**報告を押した瞬間に報告待ちが降りる**。人形はまだ画に残って
            //    いるので、素直に書くと一人ぶんが戻って 3 秒鳴る（実測 0.78）。
            //    報告のあとに笑い声が戻るのは、現実へ返す所の逆。
            var l = new SoundBedLogic();
            Settle(l, Swapped(camera: SoundBedLogic.SwellCamera), sec: 20f);

            var closing = Swapped(camera: 0);
            closing.lap = 4;
            closing.markWaiting = true;
            var g = Settle(l, closing, sec: 5f);
            Assert.Greater(g.dolls, 0.99f, "締めの群れが鳴っていない");
            Assert.Less(g.dollOne, 0.01f, "群れと同時に一人ぶんが鳴っている");

            // 報告した（群れが止まる）。人形はまだ画に残っている。
            var reported = Swapped(camera: 0);
            reported.lap = 4;
            g = Settle(l, reported, sec: 4f);
            Assert.Less(g.dollOne, 0.01f, "報告のあとに笑い声が戻ってきた");
            Assert.Less(g.dollsGrowA, 0.01f);

            // 人形が画から消えれば解ける（次の体験者・別の周では普通に鳴る）。
            var gone = Swapped(camera: 0, present: false);
            Settle(l, gone, sec: SoundBedLogic.DollHoldSec + 4f);
            g = Settle(l, Swapped(camera: 0), sec: 3f);
            Assert.Greater(g.dollOne, 0.99f, "人形が出直しても二度と笑わない");
        }

        [Test]
        public void Swap_IsSilentDuringRegistration_AndAfterTheShowEnds()
        {
            var l = new SoundBedLogic();
            var reg = Swapped(camera: 0);
            reg.registrationActive = true;
            Assert.Less(Settle(l, reg, sec: 5f).dollOne, 0.01f, "位置合わせ中に人形が笑っている");

            var done = Swapped(camera: 0);
            done.phase = ShowPhase.Finished;
            Assert.Less(Settle(new SoundBedLogic(), done, sec: 5f).dollOne, 0.01f,
                        "体験が終わったのに笑っている");
        }

        [Test]
        public void Swap_DucksTheScore_LessDeeplyThanTheClosingCrowd()
        {
            var l = new SoundBedLogic();
            var g = Settle(l, Swapped(camera: 0), sec: 5f);
            Assert.GreaterOrEqual(g.duck, SoundBedLogic.DollSwapDuck - 1e-2f, "劇伴が退いていない");
            Assert.Less(SoundBedLogic.DollSwapDuck, SoundBedLogic.DollsDuck,
                        "入れ替わりの退きが締めの群れより深い（山の順序が崩れる）");
            // 笑いそのものは退かせない（切替の一撃が 9 回以上入る区間なので、引くと毎回凹む）。
            Assert.Greater(g.dollOne, 0.99f);
        }

        [Test]
        public void Swap_DoesNotCarryOver_ToTheNextVisitor()
        {
            var l = new SoundBedLogic();
            Settle(l, Swapped(camera: SoundBedLogic.SwellCamera), sec: 30f);

            l.Reset();
            var g = l.Tick(1f / 90f, Swapped(camera: SoundBedLogic.SwellCamera));
            Assert.Less(g.dollsGrowA, 0.01f, "前の体験者の C で増えた人形を持ち越している");
            // ⚠ 1 フレームぶんは進んでいる（C に居るので）。見るのは「1 から始まっていない」こと。
            Assert.Less(l.Swell01, 0.01f, "増え具合が前の体験者の値から続いている");
        }

        // ---- 劇伴（`HorrBGM`）・`canon/LEDGER.md` 0088 ---------------------------
        //
        // ⚠ ここが固定しているのは「どこで鳴るか」だけではない。**題字が焼け始める前に
        //   渡し終える**という尺の約束と、**聴感直線で退く**という形の約束も含む
        //   （振幅直線で落とすと「後半だけ急に消えた」に聞こえる — `SoundFade` の注意書き）。

        /// <summary>リセット後の黒（字も光も無く、A を待っている）。</summary>
        private static SoundShowState DarkWait()
        {
            var s = SoundShowState.Idle;
            s.titleVisible = true;
            return s;
        }

        /// <summary>A を押して題字が立っている。</summary>
        private static SoundShowState TitleGlyph()
        {
            var s = DarkWait();
            s.titleGlyphShowing = true;
            return s;
        }

        [Test]
        public void AfterTheReport_OnlyTheLapAmbientRemains()
        {
            // 4 周目 A。**報告するまでは笑い声、押したあとは 3 周目の背景音だけ**
            //（`canon/LEDGER.md` 0088）。押したあとに何かが残ると「まだ続く」に聞こえる。
            var l = new SoundBedLogic();
            var closing = Run(4);
            closing.dollPresent = true;      // 締めのカットに人形が立っている
            closing.markWaiting = true;      // まだ報告していない
            var g = Settle(l, closing, sec: 10f);
            Assert.Greater(g.dolls, 0.9f, "報告を待っているあいだ人形が笑っていない");

            var after = closing;
            after.markWaiting = false;       // 報告を押した（人形はまだ画に居る）
            g = Settle(l, after, sec: 5f);
            Assert.Less(g.dolls, 0.01f, "報告のあとも群れが笑っている");
            Assert.Less(g.dollOne + g.dollsGrowA + g.dollsGrowB, 0.01f,
                        "報告のあとに一人ぶんの笑いが戻っている");
            Assert.Less(g.score, 0.01f, "本編で HorrBGM が鳴っている");
            Assert.Greater(g.room * g.roomLap3, SoundBedLogic.RoomInRun * 0.9f,
                           "3 周目の背景音が鳴っていない");
        }

        [Test]
        public void Score_PlaysOnlyInTheDark_BeforeTheTitle()
        {
            Assert.AreEqual(1f, SoundBedLogic.Target(DarkWait()).score, 1e-6f,
                            "黒で A を待つあいだに劇伴が鳴っていない");
            Assert.AreEqual(0f, SoundBedLogic.Target(TitleGlyph()).score, 1e-6f,
                            "題字が立ったのに劇伴が残っている");
            Assert.AreEqual(0f, SoundBedLogic.Target(Intro(IntroStage.Black)).score, 1e-6f,
                            "導入へ入っても劇伴が鳴っている");
            Assert.AreEqual(0f, SoundBedLogic.Target(Run(1)).score, 1e-6f,
                            "本編で劇伴が鳴っている（装置と部屋の音だけになるはず）");
        }

        [Test]
        public void Score_HandsOver_WhileTheTitleIsStillStanding()
        {
            var l = new SoundBedLogic();
            Settle(l, DarkWait(), sec: 6f);
            Assert.Greater(l.Gains.score, 0.99f, "黒のあいだに劇伴が満ちていない");

            // 題字が立ってから焼け始めるまでの尺で渡し終える（字が燃える所には残らない）。
            var g = Settle(l, TitleGlyph(), sec: SoundBedLogic.ScoreFadeOutSec);
            Assert.Less(g.score, 0.01f, "題字が焼け始める時刻になっても劇伴が残っている");
        }

        [Test]
        public void Score_FadesByLoudness_NotByAmplitude()
        {
            // 半分の時刻で振幅が半分なら、聴感では 8 割残って聞こえる。聴感直線ならもっと落ちている。
            var l = new SoundBedLogic();
            Settle(l, DarkWait(), sec: 6f);
            var g = Settle(l, TitleGlyph(), sec: SoundBedLogic.ScoreFadeOutSec * 0.5f);
            Assert.Less(g.score, 0.40f, "振幅が直線で落ちている（後半だけ急に消えたように聞こえる）");
            Assert.Greater(g.score, 0.20f, "落ちるのが速すぎる（半分の時刻でもう消えている）");
        }

        [Test]
        public void Score_ReturnsForTheNextVisitor()
        {
            var l = new SoundBedLogic();
            Settle(l, Run(1), sec: 30f);
            Assert.Less(l.Gains.score, 0.01f, "本編で劇伴が鳴っている");

            l.Reset();
            var g = Settle(l, DarkWait(), sec: 6f);
            Assert.Greater(g.score, 0.99f, "次の体験者の黒で劇伴が戻っていない");
        }

        [Test]
        public void Score_IsSilent_WhileStaffAreCalibrating()
        {
            var s = DarkWait();
            s.registrationActive = true;
            Assert.AreEqual(0f, SoundBedLogic.Target(s).score, 1e-6f,
                            "位置合わせ中に劇伴が鳴っている（スタッフの声が通らない）");
        }

        [Test]
        public void Tick_ApproachesTarget_WithoutOvershoot()
        {
            var logic = new SoundBedLogic();
            var s = SoundShowState.Idle;
            s.phase = ShowPhase.Run;
            SoundBedGains g = default;
            for (int i = 0; i < 1200; i++) g = logic.Tick(1f / 90f, s);
            Assert.AreEqual(1f, g.device + g.deviceWorn, 1e-2f);
            Assert.AreEqual(SoundBedLogic.RoomInRun, g.room, 1e-2f);
            Assert.LessOrEqual(g.device, 1f);
            Assert.LessOrEqual(g.room, 1f);
        }
    }
}
