#nullable enable
using System.Reflection;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 段 4 の連続光学開口と、旧 <c>shatter</c> 時計の音声互換を固定する。
    /// 破片メッシュと <see cref="IntroShatterCurve"/> は廃止互換資産として残るが、描画には使わない。
    /// </summary>
    public sealed class IntroShatterTests
    {
        private static IntroInput Ready() => new IntroInput
        {
            blackCleared = true,
            startAuthorized = true,
            outsideValid = true,
            atStartSpot = true,
            frameCentered = true,
            liveFresh = true,
            outsideBoxM = 2f,
        };

        private static IntroLogic AtFrame()
        {
            var l = new IntroLogic();
            l.Configure(IntroTiming.Default);
            l.Begin();
            for (int i = 0; i < 4; i++)
            {
                l.RequestAdvance();
                l.Tick(0f, Ready());
            }
            Assert.AreEqual(IntroStage.Frame, l.Stage);
            return l;
        }

        [Test]
        public void LegacyShatterClock_EqualsFrameProgress_ForSoundCompatibility()
        {
            var l = AtFrame();
            l.Tick(IntroTiming.Default.frameSec * 0.4f, Ready());
            Assert.AreEqual(0.4f, l.Weights.shatter, 1e-4f,
                "SoundCueLogic と SoundBedLogic が読む既存時計を変えている");
            Assert.AreEqual(0f, l.Weights.grain, 1e-5f);
            Assert.AreEqual(0f, l.Weights.glitch, 1e-5f);
        }

        [Test]
        public void ApertureClose_IsContinuousAndMonotonic()
        {
            var l = AtFrame();
            float previous = l.Weights.frame;
            for (int i = 1; i <= 20; i++)
            {
                if (i == 20) l.RequestAdvance();
                l.Tick(IntroTiming.Default.frameSec / 20f, Ready());
                float current = l.Weights.frame;
                Assert.GreaterOrEqual(current, previous, $"標本 {i} で開口が広がった");
                previous = current;
            }
            Assert.AreEqual(IntroStage.Swap, l.Stage);
            Assert.AreEqual(1f, l.Weights.frame, 1e-5f, "終端がスクリーンの開口まで閉じていない");
        }

        [Test]
        public void ScreenCrossfade_StaysOffThenCompletesBeforeFrameEnd()
        {
            var l = AtFrame();
            l.Tick(IntroTiming.Default.frameSec * 0.75f, Ready());
            Assert.AreEqual(0f, l.Weights.live, 1e-5f);

            l.Tick(IntroTiming.Default.frameSec * 0.12f, Ready());
            Assert.That(l.Weights.live, Is.InRange(0.01f, 0.99f));

            l.Tick(IntroTiming.Default.frameSec * 0.11f, Ready());
            Assert.AreEqual(1f, l.Weights.live, 1e-5f);
            Assert.Less(l.Weights.frame, 1f, "開口の終端より前にクロスフェードが終わっていない");
        }

        [Test]
        public void IntroVeil_KeepsOneQuadWhileApertureCloses()
        {
            var root = new GameObject("IntroVeilTest");
            try
            {
                var veil = root.AddComponent<IntroVeil>();
                typeof(IntroVeil).GetMethod("Awake", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(veil, null);
                Assert.AreEqual(1, veil.ApertureQuads, "覆いが単一 quad で組まれていない");

                var w = new IntroWeights { passthrough = 1f, frame = 0.5f, ignite = 1f };
                veil.Apply(w);
                Assert.AreEqual(1, veil.ApertureQuads, "開口の途中でメッシュを差し替えている");
                Assert.IsTrue(veil.ApertureDrawn);
                Assert.Greater(veil.ApertureClosePeak, 0f);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
