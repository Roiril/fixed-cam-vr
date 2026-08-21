#nullable enable
using System.Collections.Generic;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// <b>黒い波の形と時間</b>の決めごとを機械で守る（設計 A〜D）。
    ///
    /// ここで固定するのは「かっこいいか」ではなく、**設計が言い切ったこと** —
    /// 山は一方向へ走る / 前線より 1.5 倍速い / 体の外から入る /
    /// キメの一拍は画面が差し替わる縁にだけ立つ / 段の進みには 1 ビットも触らない。
    /// </summary>
    public class SwapWaveLogicTests
    {
        private const float Dt = 1f / 90f;
        private const float Total = SwapMorphLogic.DefaultTotalSec;

        private sealed class Run
        {
            public readonly List<SwapWaveLogic.Wave> waves = new List<SwapWaveLogic.Wave>();
            public readonly List<float> progress = new List<float>();
            public int beatAt = -1;
        }

        /// <summary>実機（<see cref="SwapMorphFx"/>）と同じ順で 1 本まわす。</summary>
        private static Run RunAll(SwapMorphLogic.Dir dir, float ampMul = 1f, float speedMul = 1f)
        {
            var morph = new SwapMorphLogic();
            bool toDoll = dir == SwapMorphLogic.Dir.ToDoll;
            morph.Begin(dir, Total, toDoll ? 1.70f : 0.40f, toDoll ? 0.40f : 1.70f);
            var wave = new SwapWaveLogic();
            wave.Begin(dir, Total);

            var run = new Run();
            for (int i = 0; i < 10000 && morph.Active; i++)
            {
                SwapMorphLogic.Sample s = morph.Tick(Dt);
                if (s.justSwapScreen)
                {
                    wave.NotifyBeat();
                    run.beatAt = run.waves.Count;
                }
                run.progress.Add(morph.Progress01);
                run.waves.Add(wave.Tick(Dt, morph.Progress01, ampMul, speedMul));
            }
            return run;
        }

        private static readonly SwapMorphLogic.Dir[] BothWays =
            { SwapMorphLogic.Dir.ToDoll, SwapMorphLogic.Dir.ToHuman };

        [Test]
        public void Crest_RunsOneWay_TowardTheDestination()
        {
            foreach (SwapMorphLogic.Dir dir in BothWays)
            {
                Run r = RunAll(dir);
                float want = dir == SwapMorphLogic.Dir.ToDoll ? -1f : 1f;
                int forward = 0, wraps = 0;
                for (int i = 1; i < r.waves.Count; i++)
                {
                    float d = r.waves[i].crestY - r.waves[i - 1].crestY;
                    if (Mathf.Abs(d) > SwapWaveLogic.CrestIntervalFig * 0.5f) { wraps++; continue; }
                    if (d * want > 0f) forward++;
                }
                Assert.Greater(forward, (r.waves.Count - wraps - 1) * 0.99f,
                               $"{dir}: 山は 1 本の swap で**向きを変えない**");
                Assert.Greater(wraps, 0, $"{dir}: 体の外へ抜けたら反対から入り直す");
            }
        }

        [Test]
        public void Crest_IsFasterThanTheUnravelFront()
        {
            // 設計 A「前線と同じ向きに、前線より約 1.5 倍速く」＝ 波が前線の先触れになる。
            Assert.AreEqual(1.5f, SwapWaveLogic.CrestSpeedRatio, 1e-6f);
            Assert.AreEqual(SwapWaveLogic.FrontSpeedFig(Total) * 1.5f,
                            SwapWaveLogic.CrestSpeedFig(Total), 1e-3f);
            // ほどける段（0.83 秒）のあいだに山は体（figure 2.0）を 1 度以上通り抜ける。
            Assert.Greater(SwapWaveLogic.CrestSpeedFig(Total) * SwapMorphLogic.RiseFrac * Total, 2.0f,
                           "ほどける段で少なくとも 1 度は体を掃く");
        }

        [Test]
        public void Crest_EntersFromOutsideTheBody()
        {
            // 0 から始めると**山が頭（足元）に全振幅で湧いて出る**。外から入ってこさせる。
            foreach (SwapMorphLogic.Dir dir in BothWays)
            {
                var wave = new SwapWaveLogic();
                wave.Begin(dir, Total);
                SwapWaveLogic.Wave w = wave.Tick(0f, 0f, 1f, 1f);
                Assert.Greater(Mathf.Abs(w.crestY), 1f,
                               $"{dir}: 1 フレーム目の山は体（|y| <= 1）の外");
            }
        }

        [Test]
        public void Beat_StandsOnlyAtTheFrameTheScreenSwaps()
        {
            foreach (SwapMorphLogic.Dir dir in BothWays)
            {
                Run r = RunAll(dir);
                Assert.Greater(r.beatAt, 0, $"{dir}: 画面が差し替わる縁が要る");
                Assert.AreEqual(0f, r.waves[r.beatAt - 1].beat, 1e-4f, "縁の前は 0");
                Assert.AreEqual(1f, r.waves[r.beatAt].beat, 0.05f, "縁で 1");

                // 0.25 秒で 0 へ戻る（既存の乱れパルス 0.20 秒と同じ縁・同じ桁）。
                int after = r.beatAt + Mathf.CeilToInt(SwapWaveLogic.BeatSec / Dt) + 1;
                if (after < r.waves.Count)
                    Assert.AreEqual(0f, r.waves[after].beat, 1e-4f, "0.25 秒で戻る");
            }
        }

        [Test]
        public void Beat_DoublesTheWaveAndSweepsTheWholeBody()
        {
            Run r = RunAll(SwapMorphLogic.Dir.ToDoll);
            SwapWaveLogic.Wave at = r.waves[r.beatAt];
            Assert.AreEqual(SwapWaveLogic.CrestAmpFig * 2f, at.crestAmp, 1e-3f, "振幅 2 倍");
            Assert.AreEqual(SwapWaveLogic.NeedleGain * 2f, at.needle, 1e-3f, "針 2 倍");

            // キメのあいだに山が全身（figure 2.6）を 1 度掃く（＝ 最大の山が走り抜ける）。
            int end = Mathf.Min(r.beatAt + Mathf.CeilToInt(SwapWaveLogic.BeatSec / Dt),
                                r.waves.Count - 1);
            int wraps = 0;
            for (int i = r.beatAt + 1; i <= end; i++)
                if (Mathf.Abs(r.waves[i].crestY - r.waves[i - 1].crestY)
                    > SwapWaveLogic.CrestIntervalFig * 0.5f) wraps++;
            Assert.GreaterOrEqual(wraps, 1, "キメのあいだに山が 1 度は走り抜ける");
        }

        [Test]
        public void ClearProgress_FollowsTheStageSplit()
        {
            float start = SwapMorphLogic.RiseFrac + SwapMorphLogic.MorphFrac;
            Assert.AreEqual(0f, SwapWaveLogic.ClearProgress(start), 1e-4f);
            Assert.AreEqual(0f, SwapWaveLogic.ClearProgress(start * 0.5f), 1e-4f,
                            "ほどける・縮む段では晴れていない");
            Assert.AreEqual(1f, SwapWaveLogic.ClearProgress(1f), 1e-4f);
            Assert.AreEqual(0.5f, SwapWaveLogic.ClearProgress(start + (1f - start) * 0.5f), 1e-3f);
        }

        [Test]
        public void Spike_FadesOutWhileClearing()
        {
            // 晴れながら事故（跳び）が起きると「壊れた」に読み替わる。
            Run r = RunAll(SwapMorphLogic.Dir.ToDoll);
            Assert.Greater(r.waves[0].spike, 0f);
            Assert.AreEqual(0f, r.waves[r.waves.Count - 1].spike, 0.02f, "晴れ切りで跳びは 0");
        }

        [Test]
        public void Energy_ScalesAmplitudeAndSpeed_ButNotTheStages()
        {
            Run loud = RunAll(SwapMorphLogic.Dir.ToDoll);
            Run quiet = RunAll(SwapMorphLogic.Dir.ToDoll,
                               SwapEnergyLogic.AmpFloor, SwapEnergyLogic.SpeedFloor);

            // ⚠⚠ **総尺・段の進みは 1 ミリも動かない**（設計 E の規律）。
            Assert.AreEqual(loud.waves.Count, quiet.waves.Count, "コマ数（＝尺）が変わってはいけない");
            for (int i = 0; i < loud.progress.Count; i++)
                Assert.AreEqual(loud.progress[i], quiet.progress[i], 1e-6f, $"段の進み i={i}");
            Assert.AreEqual(loud.beatAt, quiet.beatAt, "差し替えの縁も動かない");

            Assert.AreEqual(SwapEnergyLogic.AmpFloor, quiet.waves[0].ampMul, 1e-4f);
            Assert.AreEqual(loud.waves[0].crestAmp * SwapEnergyLogic.AmpFloor,
                            quiet.waves[0].crestAmp, 1e-4f, "振幅に掛かる");
            // 速さの方は周回で折り返すので crestY では比べられない（`TravelFig` の試験で見る）。
        }

        [Test]
        public void Travel_IsMonotonicAndSlowerWhenStandingStill()
        {
            var fast = new SwapWaveLogic();
            var slow = new SwapWaveLogic();
            fast.Begin(SwapMorphLogic.Dir.ToDoll, Total);
            slow.Begin(SwapMorphLogic.Dir.ToDoll, Total);
            float last = -1f;
            for (int i = 0; i < 90; i++)
            {
                fast.Tick(Dt, i * Dt / Total, 1f, 1f);
                slow.Tick(Dt, i * Dt / Total, SwapEnergyLogic.AmpFloor, SwapEnergyLogic.SpeedFloor);
                Assert.Greater(fast.TravelFig, last, "山の進んだ距離は単調に増える");
                last = fast.TravelFig;
            }
            Assert.Less(slow.TravelFig, fast.TravelFig,
                        "立ち止まっていると山はゆっくり走る（0.70 倍）");
        }

        [Test]
        public void Cancel_StopsEverything()
        {
            var wave = new SwapWaveLogic();
            wave.Begin(SwapMorphLogic.Dir.ToDoll, Total);
            wave.Tick(Dt, 0.1f, 1f, 1f);
            wave.Cancel();
            SwapWaveLogic.Wave w = wave.Tick(Dt, 0.2f, 1f, 1f);
            Assert.AreEqual(0f, w.crestAmp, 1e-6f);
            Assert.AreEqual(0f, w.needle, 1e-6f);
            Assert.AreEqual(0f, w.spike, 1e-6f);
            // ⚠ ampMul の既定は **1**。0 だと基本の波（従来の絵）まで消える。
            Assert.AreEqual(1f, w.ampMul, 1e-6f);
        }

        [Test]
        public void Idle_IsTheOldPicture()
        {
            SwapWaveLogic.Wave w = SwapWaveLogic.Wave.Idle;
            Assert.AreEqual(0f, w.crestAmp, 1e-6f, "層が 1 つも無い ＝ 前の版の絵");
            Assert.AreEqual(0f, w.needle, 1e-6f);
            Assert.AreEqual(0f, w.spike, 1e-6f);
            Assert.AreEqual(0f, w.clear01, 1e-6f);
            Assert.AreEqual(1f, w.ampMul, 1e-6f);
        }

        [Test]
        public void FrontBand_MatchesTheShader()
        {
            // ⚠⚠ シェーダの `SwapFront` の `band` と対の値。片方だけ直すと 1.5 倍の関係が狂う。
            Assert.AreEqual(0.30f, SwapWaveLogic.FrontBand, 1e-6f,
                            "ScreenComposite.shader の SwapFront の band と同じ値であること");
        }

        [Test]
        public void CrestInterval_KeepsOnlyOneCrestOnTheBody()
        {
            // 設計 A「山の間隔は体高の 1.2 倍（画面にいつも 1 つだけ山がある）」。体高 = figure 2.0。
            Assert.Greater(SwapWaveLogic.CrestIntervalFig, 2.0f);
            Assert.AreEqual(2.4f, SwapWaveLogic.CrestIntervalFig, 1e-6f);
        }
    }
}
