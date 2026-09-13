#nullable enable
using System.Reflection;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 段 4 の実破砕とスクリーンへの収束を固定する。
    /// <c>shatter</c> は描画と既存音が共有する同じ時計でなければならない。
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
        public void ShatterClock_EqualsFrameProgress_ForDrawingAndSound()
        {
            var l = AtFrame();
            l.Tick(IntroTiming.Default.frameSec * 0.4f, Ready());
            Assert.AreEqual(0.4f, l.Weights.shatter, 1e-4f,
                "破片描画と SoundCueLogic / SoundBedLogic が読む時計を変えている");
            Assert.AreEqual(0f, l.Weights.grain, 1e-5f);
            Assert.AreEqual(0f, l.Weights.glitch, 1e-5f);
        }

        [Test]
        public void FractureAndTerminalFrame_AreContinuousAndMonotonic()
        {
            var l = AtFrame();
            float previousShatter = l.Weights.shatter;
            float previousFrame = l.Weights.frame;
            for (int i = 1; i < 20; i++)
            {
                l.Tick(IntroTiming.Default.frameSec / 20f, Ready());
                Assert.GreaterOrEqual(l.Weights.shatter, previousShatter, $"標本 {i} で破砕が戻った");
                Assert.GreaterOrEqual(l.Weights.frame, previousFrame, $"標本 {i} で終端矩形が戻った");
                previousShatter = l.Weights.shatter;
                previousFrame = l.Weights.frame;
            }
            Assert.AreEqual(IntroStage.Frame, l.Stage);
            l.RequestAdvance();
            l.Tick(IntroTiming.Default.frameSec / 20f, Ready());
            Assert.AreEqual(IntroStage.Swap, l.Stage);
            Assert.AreEqual(0f, l.Weights.shatter, "Swap では破片の描画を終了する");
            Assert.AreEqual(1f, l.Weights.frame, 1e-5f, "終端がスクリーン矩形まで確定していない");
        }

        [Test]
        public void ScreenCrossfade_StaysOffThenCompletesBeforeFrameEnd()
        {
            var l = AtFrame();
            l.Tick(IntroTiming.Default.frameSec * 0.70f, Ready());
            Assert.AreEqual(0f, l.Weights.live, 1e-5f);

            l.Tick(IntroTiming.Default.frameSec * 0.10f, Ready());
            Assert.That(l.Weights.live, Is.InRange(0.01f, 0.99f));

            l.Tick(IntroTiming.Default.frameSec * 0.10f, Ready());
            Assert.AreEqual(1f, l.Weights.live, 1e-5f);
            Assert.AreEqual(1f, l.Weights.frame, 1e-5f, "映像の終端とスクリーン矩形が揃っていない");
        }

        [Test]
        public void IntroVeil_DrawsRealShardsAndLeavesNoTerminalLeak()
        {
            var root = new GameObject("IntroVeilTest");
            try
            {
                var veil = root.AddComponent<IntroVeil>();
                typeof(IntroVeil).GetMethod("Awake", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(veil, null);
                Assert.AreEqual(1, veil.ApertureQuads, "正確なスクリーン窓の base quad が 1 枚でない");
                Assert.Greater(veil.ShatterPieces, 0, "破片メッシュが生成されていない");
                Assert.IsFalse(veil.ShatterDrawn, "段 4 の開始前から破片が描かれている");

                var w = new IntroWeights { passthrough = 1f, shatter = 0.02f, ignite = 1f };
                veil.Apply(w);
                Assert.IsTrue(veil.ShatterDrawn, "割れ始めに破片 Renderer が描かれていない");
                Assert.AreEqual(1, veil.ApertureQuads, "破砕中に base quad を差し替えている");

                w.shatter = 0.83f;
                w.frame = 0.9f;
                veil.Apply(w);
                Assert.IsTrue(veil.ShatterDrawn, "継ぎ目へ収束する前に破片が消えた");
                Assert.That(veil.ShatterPeak, Is.EqualTo(0.83f).Within(1e-4f));

                w.shatter = 0.84f;
                w.frame = 1f;
                w.live = 1f;
                veil.Apply(w);
                Assert.IsTrue(veil.ShatterDrawn,
                    "見かけが継ぎ目へ閉じた Frame 終端まで実配布の記録経路を保っていない");

                w = new IntroWeights { passthrough = 0f, frame = 1f, live = 1f, ignite = 1f };
                veil.Apply(w);
                Assert.IsFalse(veil.ShatterDrawn, "収束終端で破片 Renderer が残り、現実が漏れる");
                Assert.That(veil.ShatterPeak, Is.EqualTo(0.84f).Within(1e-4f),
                    "終端記録より前に破砕の実測最大値を消した");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
