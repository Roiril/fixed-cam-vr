#nullable enable
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 目が開く音（<c>canon/LEDGER.md</c> 0131・ユーザー提供の太鼓）。
    ///
    /// ⚠⚠ <b>1 つの目に 1 発は鳴らせない。</b> 開くのは 189 個で、開眼の 3 秒に収まる
    /// （実測で最大 188 個/秒）。だから <see cref="AnomalyEyes.EyeSfxMinIntervalSec"/> で間引く。
    /// ここが固定するのは「間引いても<b>開き方の形</b>が残ること」と、音源の名前の対応。
    /// </summary>
    public sealed class EyeSoundTests
    {
        /// <summary>変種は 6 本（切替音と同じ仕組み・ユーザー指定「全部同じ音ではなく少しずつ変えること」）。</summary>
        [Test]
        public void EyeOpen_HasSixVariants()
        {
            Assert.That(SoundCueLogic.ResourceName(SoundCue.EyeOpen), Is.EqualTo("sfx_eye"));
            Assert.That(SoundCueLogic.VariantCount(SoundCue.EyeOpen), Is.EqualTo(6));
        }

        /// <summary>大きい目は 1 本きり（1 回の体験で 1 度しか鳴らない）。</summary>
        [Test]
        public void EyeBig_HasOneClip()
        {
            Assert.That(SoundCueLogic.ResourceName(SoundCue.EyeBig), Is.EqualTo("sfx_eye_big"));
            Assert.That(SoundCueLogic.VariantCount(SoundCue.EyeBig), Is.EqualTo(1));
        }

        /// <summary>
        /// ⚠⚠ <b>目の一撃は劇伴を退かせない。</b> 3 秒に 20 発近く並ぶので、1 発ごとに引くと
        /// 背景が波打つ（人形の笑いを退かせないのと同じ理由 —— 事件そのものだから）。
        /// </summary>
        [Test]
        public void EyeOpen_DoesNotDuckTheScore()
        {
            Assert.That(SoundCueLogic.DuckFor(SoundCue.EyeOpen), Is.EqualTo(0f));
            Assert.Greater(SoundCueLogic.DuckFor(SoundCue.EyeBig), 0.5f,
                           "大きい目は 3 周目 C の山なので深く退かせる");
        }

        /// <summary>
        /// ⚠ 焼いた後に <c>tools/unity.ps1 menu sound-import</c> を通していないと
        /// Resources から引けない。
        /// </summary>
        [Test]
        public void Clips_AreInResources()
        {
            for (int i = 1; i <= SoundCueLogic.VariantCount(SoundCue.EyeOpen); i++)
            {
                var c = Resources.Load<AudioClip>($"{ShowSoundDirector.ResourceDir}sfx_eye_{i}");
                Assert.IsNotNull(c, $"sfx_eye_{i} が無い — "
                                    + "`py -3.11 tools/ingest-sounds.py --only sfx_eye` の後に "
                                    + "`tools/unity.ps1 menu sound-import`");
                Assert.That(c!.channels, Is.EqualTo(1), "その目の方角から鳴るのでモノ（0130）");
            }
            var big = Resources.Load<AudioClip>($"{ShowSoundDirector.ResourceDir}sfx_eye_big");
            Assert.IsNotNull(big, "sfx_eye_big が無い");
            Assert.That(big!.channels, Is.EqualTo(1));
        }

        /// <summary>
        /// <b>音は動かしていない</b>（0138 は「音にアニメーションを合わせる」）。
        /// 焼いた <c>sfx_eye_big</c> のいちばん大きいところ（0.4 秒窓の実効値）は
        /// 0131 のとおり頭から 1.234 秒のまま（<c>tools/ingest-sounds.py</c> の <c>ALIGN</c>）。
        /// ここが動いたら、下の表（<see cref="AnomalyEyesLogic.HintOpenKnots"/>）を測り直すこと。
        /// </summary>
        [Test]
        public void EyeBig_ClipIsUnchanged_LoudestPointStaysAt1234()
        {
            var clip = Resources.Load<AudioClip>($"{ShowSoundDirector.ResourceDir}sfx_eye_big");
            if (clip == null)
            {
                Assert.Ignore("sfx_eye_big が無い（先に焼く）");
                return;
            }
            const float want = 1.234f;
            var data = new float[clip.samples * clip.channels];
            clip.GetData(data, 0);
            int win = (int)(0.4f * clip.frequency) * clip.channels;
            Assert.Greater(data.Length, win, "素材が窓より長いこと");
            double run = 0;
            for (int i = 0; i < win; i++) run += data[i] * (double)data[i];
            double best = run;
            int bestAt = 0;
            for (int i = win; i < data.Length; i++)
            {
                run += data[i] * (double)data[i] - data[i - win] * (double)data[i - win];
                if (run > best) { best = run; bestAt = i - win + 1; }
            }
            float at = (bestAt / (float)clip.channels + win / (2f * clip.channels)) / clip.frequency;
            Assert.That(at, Is.EqualTo(want).Within(0.06f),
                        $"いちばん大きいところが {at:F3}s（0131 の {want:F3}s から動いている）— "
                        + "音を焼き直したなら AnomalyEyesLogic.HintOpenKnots を測り直す");
        }

        /// <summary>
        /// ⚠⚠ <b>大きい目の開き方は、音の聴感の形そのもの</b>（0138・ユーザー逐語
        /// 「音にアニメーションを合わせてほしい。自然な感じに」）。
        ///
        /// 焼いたファイルを測って <see cref="AnomalyEyesLogic.HintOpenKnots"/> と突き合わせる:
        /// 10ms 窓の実効値を持続の高さ（1.0〜1.5 秒の平均）で割り、0.6 乗して、最大値で保持し、1 で止める。
        /// 音の頭（-30dB を越える所）が <see cref="AnomalyEyesLogic.HintOnsetSec"/> から
        /// 先読み（<see cref="SfxPlayer.ScheduleLeadSec"/>）を引いた所に来ていること。
        /// ⚠ 音を焼き直すとここが落ちる ＝ 表を測り直す合図。手で整えて通さない。
        /// </summary>
        [Test]
        public void EyeBig_OpeningFollowsTheSoundsLoudness()
        {
            var clip = Resources.Load<AudioClip>($"{ShowSoundDirector.ResourceDir}sfx_eye_big");
            if (clip == null)
            {
                Assert.Ignore("sfx_eye_big が無い（先に焼く）");
                return;
            }
            var data = new float[clip.samples * clip.channels];
            clip.GetData(data, 0);
            int ch = clip.channels;
            int win = (int)(0.010f * clip.frequency);
            int frames = clip.samples / win;
            var rms = new float[frames];
            for (int f = 0; f < frames; f++)
            {
                double acc = 0;
                for (int s = 0; s < win; s++)
                {
                    double m = 0;
                    for (int c = 0; c < ch; c++) m += data[((f * win) + s) * ch + c];
                    m /= ch;
                    acc += m * m;
                }
                rms[f] = (float)System.Math.Sqrt(acc / win);
            }
            // 持続の高さ（1.0〜1.5 秒）。
            double plateau = 0; int np = 0;
            for (int f = 100; f < 150 && f < frames; f++) { plateau += rms[f]; np++; }
            plateau /= System.Math.Max(1, np);
            Assert.Greater(plateau, 1e-4, "持続の高さが測れない（無音か）");

            // 音の頭。
            float thr = (float)(plateau * System.Math.Pow(10.0, -30.0 / 20.0));
            int onsetF = -1;
            for (int f = 0; f < frames; f++) { if (rms[f] > thr) { onsetF = f; break; } }
            Assert.GreaterOrEqual(onsetF, 0, "音の頭が見つからない");
            float onset = onsetF * 0.010f;
            float wantOnset = AnomalyEyesLogic.HintOnsetSec - SfxPlayer.ScheduleLeadSec;
            Assert.That(onset, Is.EqualTo(wantOnset).Within(0.03f),
                        $"音の頭 {onset:F3}s に対し、絵は {wantOnset:F3}s から動く（AnomalyEyesLogic.HintOnsetSec）");

            // 聴感の形（最大値で保持・1 で止める）と表を突き合わせる。
            float hold = 0f;
            var loud = new float[frames];
            for (int f = 0; f < frames; f++)
            {
                float v = Mathf.Min(1f, Mathf.Pow((float)(rms[f] / plateau), 0.6f));
                hold = Mathf.Max(hold, v);
                loud[f] = hold;
            }
            int n = AnomalyEyesLogic.HintOpenKnots.GetLength(0);
            for (int i = 0; i < n; i++)
            {
                float u = AnomalyEyesLogic.HintOpenKnots[i, 0];
                float want = AnomalyEyesLogic.HintOpenKnots[i, 1];
                int f = onsetF + Mathf.RoundToInt(u / 0.010f);
                Assert.Less(f, frames);
                Assert.That(want, Is.EqualTo(loud[f]).Within(0.12f),
                            $"音の頭から {u:F2}s: 表 {want:F2} / 音 {loud[f]:F2} — 表を測り直すこと");
            }
        }

        /// <summary>
        /// <b>間引いても開き方の形が残る。</b> 開眼は「さざめき → 間 → 一気に」なので、
        /// 一撃も同じ形（前半に数発 → <b>無音の間</b> → 後半に連なる）にならなければ、
        /// 音は演出を運んでいない。
        ///
        /// ⚠ 実際の目の開き方（<see cref="AnomalyEyesLogic.SwarmCurve"/>）と
        /// 実際の間引き（<see cref="AnomalyEyes.EyeSfxMinIntervalSec"/>）で数える。
        /// </summary>
        [Test]
        public void EyeOpen_KeepsTheShapeOfTheSwarm()
        {
            const int seats = 189;
            const float dt = 1f / 72f;
            int last = 0;
            float cool = 0f;
            int hits = 0, before = 0, during = 0, after = 0;

            for (float t = 0f; t <= AnomalyEyesLogic.SwarmSec; t += dt)
            {
                if (cool > 0f) cool -= dt;
                float field = AnomalyEyesLogic.SwarmCurve(t / AnomalyEyesLogic.SwarmSec);
                int open = 0;
                for (int k = 0; k < seats; k++)
                {
                    float rank = k / (float)(seats - 1);
                    if (AnomalyEyesLogic.EyeOpen(field, rank) > AnomalyEyesLogic.OpenEpsilon) open++;
                }
                if (open > last)
                {
                    if (cool <= 0f)
                    {
                        cool = AnomalyEyes.EyeSfxMinIntervalSec;
                        hits++;
                        float u = t / AnomalyEyesLogic.SwarmSec;
                        if (u < AnomalyEyesLogic.SwarmPauseAt) before++;
                        else if (u < AnomalyEyesLogic.SwarmRushAt) during++;
                        else after++;
                    }
                    last = open;
                }
            }

            Assert.That(hits, Is.InRange(10, 34),
                        $"間引いた一撃が {hits} 発 —— 少なすぎると演出を運ばず、多すぎると雑音になる");
            Assert.Greater(before, 0, "さざめきで数発鳴ること");
            Assert.Greater(during, before, "一気に開く所でいちばん多く鳴ること");
        }

        /// <summary>
        /// <b>間（ポーズ）では鳴らない。</b> 開き方の「止まる」がそのまま音の空白になる
        /// （等間隔の連打にしない）。
        /// </summary>
        [Test]
        public void EyeOpen_IsSilentDuringThePause()
        {
            float a = AnomalyEyesLogic.SwarmRippleAt * AnomalyEyesLogic.SwarmSec;
            float b = AnomalyEyesLogic.SwarmPauseAt * AnomalyEyesLogic.SwarmSec;
            Assert.Greater(b - a, AnomalyEyes.EyeSfxMinIntervalSec * 2f,
                           "間は最短間隔より十分に長い（＝ そこに空白が出る）");
            // 間のあいだ、開く目は 1 つも増えない（＝ 鳴らす縁が無い）。
            float f1 = AnomalyEyesLogic.SwarmCurve(AnomalyEyesLogic.SwarmRippleAt + 1e-4f);
            float f2 = AnomalyEyesLogic.SwarmCurve(AnomalyEyesLogic.SwarmPauseAt - 1e-4f);
            Assert.That(f2, Is.EqualTo(f1).Within(1e-3f), "間のあいだ進みが動かないこと");
        }
    }
}
