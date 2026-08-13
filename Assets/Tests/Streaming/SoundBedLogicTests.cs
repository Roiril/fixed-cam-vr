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
                                            float frame = 0f, float ignite = 0f, float live = 0f)
        {
            var s = SoundShowState.Idle;
            s.introActive = true;
            s.introStage = stage;
            s.introWeights = IntroWeights.Inactive;
            s.introWeights.shell = shell;
            s.introWeights.frame = frame;
            s.introWeights.ignite = ignite;
            s.introWeights.live = live;
            return s;
        }

        [Test]
        public void Title_IsAlmostSilent_ButTheBoxIsAlreadyHumming()
        {
            // タイトルは黒。世界の手前なので部屋も装置も鳴らない。
            // ただし封印の箱の唸りだけは**先に**鳴らす（J カット）。A を押して黒が開いたとき、
            // 箱の声がもう鳴っているので「場面が切り替わった」ではなく「幕が上がった」になる。
            var s = SoundShowState.Idle;
            s.titleVisible = true;
            var g = SoundBedLogic.Target(s);
            Assert.Greater(g.seal, 0f, "タイトルの下で箱が鳴っていない（J カットが成立しない）");
            Assert.AreEqual(0f, g.room, 1e-6f);
            Assert.AreEqual(0f, g.device, 1e-6f);
        }

        [Test]
        public void Device_ArrivesBeforeThePicture_AtStageSeal()
        {
            // ⚠ **この 1 本が導入の音設計の核心。** 装置の声は「画が映像になる」より前、
            // 段 1（開口が閉じる）で入り始める。これが 6.2 秒を 1 つの出来事として繋ぐ。
            Assert.AreEqual(0f, SoundBedLogic.Target(Intro(IntroStage.Black)).device, 1e-6f,
                            "段 0 で装置が鳴っている（まだ現実のはず）");
            float mid = SoundBedLogic.Target(Intro(IntroStage.Seal, frame: 0.5f)).device;
            Assert.Greater(mid, 0f, "段 1 で装置が入り始めていない");
            Assert.Less(mid, SoundBedLogic.Target(Intro(IntroStage.Live, live: 1f)).device);
        }

        [Test]
        public void Device_ReachesFull_OnlyAfterTheVideoArrives()
        {
            var swap = SoundBedLogic.Target(Intro(IntroStage.Live, live: 1f));
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
            var open = SoundBedLogic.Target(Intro(IntroStage.Seal, shell: 0f));
            var shut = SoundBedLogic.Target(Intro(IntroStage.Seal, shell: 1f));
            Assert.AreEqual(open.room, shut.room, 1e-6f, "隔離で部屋の音量が変わっている");
            Assert.Less(shut.roomOpen, open.roomOpen, "隔離で帯域が閉じていない");
            Assert.AreEqual(SoundBedLogic.RoomOpenSealed, shut.roomOpen, 1e-4f);
            Assert.Greater(shut.roomOpen, 0f, "完全に閉じると無響になって不自然");
        }

        [Test]
        public void SealedBox_FadesWithTheClosingAperture_AndIsGoneInTheDark()
        {
            // 箱の声は段 1 で開口が閉じるのと**同じ進み**で引く。
            // 箱だけ鳴り続けると「閉じたのにまだ外に箱がある」と食い違う。
            Assert.AreEqual(1f, SoundBedLogic.Target(Intro(IntroStage.Black)).seal, 1e-6f);
            Assert.AreEqual(0.25f, SoundBedLogic.Target(Intro(IntroStage.Seal, frame: 0.75f)).seal, 1e-4f);
            Assert.AreEqual(0f, SoundBedLogic.Target(Intro(IntroStage.Dark)).seal, 1e-6f);
            Assert.AreEqual(0f, SoundBedLogic.Target(Intro(IntroStage.Live, live: 1f)).seal, 1e-6f);
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
            float introPeak = SoundBedLogic.Target(Intro(IntroStage.Live, live: 1f)).device;
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
