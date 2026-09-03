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
        /// ⚠⚠ <b>いちばん大きいところが「見開く瞬間」に来ている。</b>
        /// 兆しの段の進み <see cref="AnomalyEyesLogic.HintSnapAt"/> ×
        /// <see cref="AnomalyEyesLogic.HintSec"/> ＝ 頭から 1.234 秒。
        ///
        /// ⚠ 素材の頭に無音を足して焼いてある（<c>tools/ingest-sounds.py</c> の <c>ALIGN</c>）ので、
        /// <b>実行時に足し引きしない</b>。ここが崩れたら焼き直しが要る。
        /// </summary>
        [Test]
        public void EyeBig_LoudestPointLandsWhenTheBigEyeSnapsOpen()
        {
            var clip = Resources.Load<AudioClip>($"{ShowSoundDirector.ResourceDir}sfx_eye_big");
            if (clip == null)
            {
                Assert.Ignore("sfx_eye_big が無い（先に焼く）");
                return;
            }
            float want = AnomalyEyesLogic.HintSnapAt * AnomalyEyesLogic.HintSec;
            Assert.That(want, Is.EqualTo(1.234f).Within(0.01f), "見開く時刻（焼く側と対の値）");

            var data = new float[clip.samples * clip.channels];
            clip.GetData(data, 0);
            // 0.4 秒窓の実効値がいちばん大きい所（焼く側の `loudest_sec` と同じ物差し）。
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
                        $"いちばん大きいところが {at:F3}s（狙い {want:F3}s）— "
                        + "`tools/ingest-sounds.py` の ALIGN で焼き直す");
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
