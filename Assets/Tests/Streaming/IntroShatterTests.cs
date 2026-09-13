#nullable enable
using System.Reflection;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 段 4 の全面破砕とスクリーン矩形への再構成を固定する。
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
        public void ScreenCrossfade_HoldsTheReconstructedRealityThenCompletes()
        {
            var l = AtFrame();
            // p=.82 で全片が矩形へ再構成された後も、短く現実を保つ。
            l.Tick(IntroTiming.Default.frameSec * 0.88f, Ready());
            Assert.AreEqual(0f, l.Weights.live, 1e-5f);

            l.Tick(IntroTiming.Default.frameSec * 0.05f, Ready());
            Assert.That(l.Weights.live, Is.InRange(0.01f, 0.99f));

            l.Tick(IntroTiming.Default.frameSec * 0.05f, Ready());
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
                Assert.AreEqual(1, veil.ApertureQuads, "破砕の背面を塞ぐ base quad が 1 枚でない");
                Assert.Greater(veil.ShatterPieces, 0, "破片メッシュが生成されていない");
                Assert.IsFalse(veil.ShatterDrawn, "段 4 の開始前から破片が描かれている");

                var w = new IntroWeights { passthrough = 1f, shatter = 0.02f, ignite = 1f };
                veil.Apply(w);
                Assert.IsTrue(veil.ShatterDrawn, "割れ始めに破片 Renderer が描かれていない");
                Assert.AreEqual(1, veil.ApertureQuads, "破砕中に base quad を差し替えている");

                w.shatter = 0.81f;
                w.frame = 0.9f;
                veil.Apply(w);
                Assert.IsTrue(veil.ShatterDrawn, "矩形へ再構成される前に破片が消えた");
                Assert.That(veil.ShatterPeak, Is.EqualTo(0.81f).Within(1e-4f));

                w.shatter = 0.82f;
                w.frame = 1f;
                w.live = 0f;
                veil.Apply(w);
                Assert.IsTrue(veil.ShatterDrawn,
                    "全片が矩形へ再構成された Frame 終端まで実配布の記録経路を保っていない");

                w = new IntroWeights { passthrough = 0f, frame = 1f, live = 1f, ignite = 1f };
                veil.Apply(w);
                Assert.IsFalse(veil.ShatterDrawn, "収束終端で破片 Renderer が残り、現実が漏れる");
                Assert.That(veil.ShatterPeak, Is.EqualTo(0.82f).Within(1e-4f),
                    "終端記録より前に破砕の実測最大値を消した");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void FrozenFrameSource_ExposesConstructorValuesWithoutMutation()
        {
            var left = new Texture2D(2, 2);
            var right = new Texture2D(3, 3);
            try
            {
                Matrix4x4 leftProjection = Matrix4x4.Translate(new Vector3(1f, 2f, 3f));
                Matrix4x4 rightProjection = Matrix4x4.Scale(new Vector3(2f, 3f, 4f));
                var source = new IntroFrozenFrameSource(left, right, leftProjection, rightProjection);
                Assert.That(source.Left, Is.SameAs(left));
                Assert.That(source.Right, Is.SameAs(right));
                Assert.That(source.LeftWorldToUv, Is.EqualTo(leftProjection));
                Assert.That(source.RightWorldToUv, Is.EqualTo(rightProjection));
            }
            finally
            {
                Object.DestroyImmediate(left);
                Object.DestroyImmediate(right);
            }
        }

        [Test]
        public void IntroVeil_CopiesFrozenFrameOnceAndReleasesItAtTheRunBoundary()
        {
            var root = new GameObject("IntroFrozenFrameTest");
            var left = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            var right = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            try
            {
                var veil = root.AddComponent<IntroVeil>();
                typeof(IntroVeil).GetMethod("Awake", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(veil, null);
                int calls = 0;
                veil.FrozenFrameProvider = () =>
                {
                    calls++;
                    return new IntroFrozenFrameSource(
                        left, right, Matrix4x4.identity, Matrix4x4.identity);
                };

                veil.Apply(new IntroWeights { passthrough = 1f, shatter = 0.02f, ignite = 1f });
                Assert.That(calls, Is.EqualTo(1));
                Assert.That(veil.FrozenFrameAttempted, Is.True);
                Assert.That(veil.HasFrozenFrame, Is.True);
                Assert.That(veil.FrozenFrameCount, Is.EqualTo(1));
                var depthRenderer = (MeshRenderer)typeof(IntroVeil)
                    .GetField("_fractureDepthRenderer", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(veil)!;
                var colorRenderer = (MeshRenderer)typeof(IntroVeil)
                    .GetField("_fractureRenderer", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(veil)!;
                var baseMaterial = (Material)typeof(IntroVeil)
                    .GetField("_mat", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(veil)!;
                Assert.That(baseMaterial.renderQueue, Is.EqualTo(4900));
                Assert.That(baseMaterial.GetInt("_ZWrite"), Is.EqualTo(1));
                Assert.That(depthRenderer.enabled, Is.True);
                Assert.That(depthRenderer.sharedMaterial.renderQueue, Is.EqualTo(4901));
                Assert.That(depthRenderer.sharedMaterial.GetInt("_ColorMask"), Is.Zero);
                Assert.That(depthRenderer.sharedMaterial.GetInt("_ZWrite"), Is.EqualTo(1));
                Assert.That(depthRenderer.sharedMaterial.GetInt("_ZTest"),
                    Is.EqualTo((int)CompareFunction.LessEqual));
                Assert.That(colorRenderer.sharedMaterial.renderQueue, Is.EqualTo(4902));
                Assert.That(colorRenderer.sharedMaterial.GetInt("_ZWrite"), Is.Zero);
                Assert.That(colorRenderer.sharedMaterial.GetInt("_ZTest"),
                    Is.EqualTo((int)CompareFunction.Equal));
                Assert.That(colorRenderer.sharedMaterial.GetInt("_SrcBlend"),
                    Is.EqualTo((int)BlendMode.SrcAlpha));
                Assert.That(colorRenderer.sharedMaterial.GetInt("_DstBlend"),
                    Is.EqualTo((int)BlendMode.OneMinusSrcAlpha));
                Assert.That(colorRenderer.sharedMaterial.GetInt("_SrcBlendAlpha"),
                    Is.EqualTo((int)BlendMode.Zero));
                Assert.That(colorRenderer.sharedMaterial.GetInt("_DstBlendAlpha"),
                    Is.EqualTo((int)BlendMode.One));

                veil.Apply(new IntroWeights { passthrough = 1f, shatter = 0.40f, ignite = 1f });
                Assert.That(calls, Is.EqualTo(1), "同じ Frame 中に実景を再取得した");
                Assert.That(veil.FrozenFrameCount, Is.EqualTo(1));

                veil.Apply(new IntroWeights { passthrough = 1f, shatter = 0f, frame = 0f });
                Assert.That(veil.HasFrozenFrame, Is.False);
                Assert.That(veil.FrozenFrameAttempted, Is.False);
                Assert.That(veil.FrozenFrameCount, Is.Zero);

                veil.Apply(new IntroWeights { passthrough = 1f, shatter = 0.02f, ignite = 1f });
                Assert.That(calls, Is.EqualTo(2), "次の Frame で新しい実景を取得しなかった");
                Assert.That(veil.FrozenFrameCount, Is.EqualTo(1));
                veil.SetHidden();
                Assert.That(veil.HasFrozenFrame, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(left);
                Object.DestroyImmediate(right);
            }
        }

        [Test]
        public void IntroVeil_NullFrozenFrameFallsBackWithoutRetrying()
        {
            var root = new GameObject("IntroFrozenFallbackTest");
            try
            {
                var veil = root.AddComponent<IntroVeil>();
                typeof(IntroVeil).GetMethod("Awake", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(veil, null);
                int calls = 0;
                veil.FrozenFrameProvider = () =>
                {
                    calls++;
                    return null;
                };
                veil.Apply(new IntroWeights { passthrough = 1f, shatter = 0.02f, ignite = 1f });
                veil.Apply(new IntroWeights { passthrough = 1f, shatter = 0.60f, ignite = 1f });
                Assert.That(calls, Is.EqualTo(1));
                Assert.That(veil.FrozenFrameAttempted, Is.True);
                Assert.That(veil.HasFrozenFrame, Is.False);
                Assert.That(veil.FrozenFrameCount, Is.Zero);
                Assert.That(veil.ShatterDrawn, Is.True, "静止画なしの旧 alpha 破片経路を失った");
                var depthRenderer = (MeshRenderer)typeof(IntroVeil)
                    .GetField("_fractureDepthRenderer", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(veil)!;
                var colorRenderer = (MeshRenderer)typeof(IntroVeil)
                    .GetField("_fractureRenderer", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(veil)!;
                Assert.That(depthRenderer.enabled, Is.False);
                Assert.That(colorRenderer.sharedMaterial.GetInt("_ZTest"),
                    Is.EqualTo((int)CompareFunction.Always));
                Assert.That(colorRenderer.sharedMaterial.GetInt("_SrcBlend"),
                    Is.EqualTo((int)BlendMode.Zero));
                Assert.That(colorRenderer.sharedMaterial.GetInt("_DstBlend"),
                    Is.EqualTo((int)BlendMode.SrcAlpha));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
