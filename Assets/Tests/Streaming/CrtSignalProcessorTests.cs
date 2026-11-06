#nullable enable
using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FixedCamVr.Streaming.Tests
{
    public sealed class CrtSignalProcessorTests
    {
        private readonly List<Object> _garbage = new();

        [TearDown]
        public void TearDown()
        {
            foreach (Object item in _garbage) if (item != null) Object.DestroyImmediate(item);
            _garbage.Clear();
        }

        private T Track<T>(T item) where T : Object
        {
            _garbage.Add(item);
            return item;
        }

        private Material State(Texture source)
        {
            var material = Track(new Material(CrtSignalResources.Load().Composite));
            material.SetTexture("_LiveTex", source);
            material.SetVector("_LiveScale", new Vector4(1, 1, 0, 0));
            CrtSignalProfile.ApplySelected(material);
            return material;
        }

        private Texture2D Solid(Color color, int width = 16, int height = 12)
        {
            var texture = Track(new Texture2D(width, height, TextureFormat.RGBA32, true, true)
            {
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp,
            });
            var pixels = new Color[width * height];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        private static Color Center(RenderTexture target)
        {
            RenderTexture? previous = RenderTexture.active;
            var pixel = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            try
            {
                RenderTexture.active = target;
                pixel.ReadPixels(new Rect(target.width / 2, target.height / 2, 1, 1), 0, 0, false);
                return pixel.GetPixel(0, 0);
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(pixel);
            }
        }

        [TestCase(0f, 0f, 1f, 1600, 0f)]
        [TestCase(800f, 0f, 1f, 1600, 1f)]
        [TestCase(266.9f, 0f, 1f, 1064, 2f)]
        [TestCase(400f, 0f, .75f, 1200, 2f)]
        [TestCase(300f, .5f, 1f, 800, 0f)]
        [TestCase(200f, .5f, 1f, 800, 2f)]
        [TestCase(.6f, 0f, 1f, 24, 2f)]
        [TestCase(32f, 0f, 1f, 16, 0f)]
        public void CoarseLod_UsesFlooredBlocksAndContainScale_AndDefersToCoarserPixelate(
            float blocks, float pixelate, float scale, int width, float expected)
        {
            Material material = State(Texture2D.blackTexture);
            material.SetFloat("_CoarseBlocks", blocks);
            material.SetFloat("_Pixelate", pixelate);
            material.SetVector("_LiveScale", new Vector4(scale, 1, 0, 0));

            Assert.That(CrtSignalProcessor.CoarseLod(material, width), Is.EqualTo(expected).Within(1e-5));
        }

        [Test]
        public void SelectedProfile_DoesNotOverwriteTheCurrentRunOrBorrowedLive()
        {
            Texture2D live = Solid(Color.gray);
            Material material = State(live);
            material.SetFloat("_Mono", .37f);
            material.SetFloat("_CoarseBlocks", 267);
            material.SetFloat("_SrcFrame", 123);
            material.SetFloat("_ExposureBias", -.6f);
            material.SetFloat("_OverlayStrength", .42f);
            material.SetFloat("_NoiseDark", .175f);

            CrtSignalProfile.ApplySelected(material);

            Assert.That(material.GetFloat("_Mono"), Is.EqualTo(.37f));
            Assert.That(material.GetFloat("_CoarseBlocks"), Is.EqualTo(267));
            Assert.That(material.GetFloat("_SrcFrame"), Is.EqualTo(123));
            Assert.That(material.GetFloat("_ExposureBias"), Is.EqualTo(-.6f));
            Assert.That(material.GetFloat("_OverlayStrength"), Is.EqualTo(.42f));
            Assert.That(material.GetFloat("_NoiseDark"), Is.EqualTo(.175f));
            Assert.That(material.GetFloat("_Exposure"), Is.EqualTo(-1.75f));
            Assert.That(material.GetFloat("_Contrast"), Is.EqualTo(1.12f));
            Assert.That(material.GetTexture("_LiveTex"), Is.SameAs(live));
        }

        [Test]
        public void PrepareDisplay_AppliesFinalSelectedPostAndPreservesDynamicMonoAndNoise()
        {
            Material material = State(Solid(Color.gray));
            material.SetFloat("_Exposure", 4); material.SetFloat("_Glare", .55f);
            material.SetFloat("_Mono", 1); material.SetFloat("_NoiseDark", .2f);
            material.SetFloat("_CoarseBlocks", ScreenDecayLogic.BlocksFor(1));
            using var processor = new CrtSignalProcessor();
            Assert.IsTrue(processor.PrepareDisplay(material, 1));
            Assert.AreEqual(CrtSignalProfile.Exposure, material.GetFloat("_Exposure"));
            Assert.AreEqual(0, material.GetFloat("_Glare"));
            Assert.AreEqual(1, material.GetFloat("_Mono"));
            Assert.AreEqual(.2f, material.GetFloat("_NoiseDark"));
            Assert.AreEqual(ScreenDecayLogic.BlocksFor(1), material.GetFloat("_CoarseBlocks"));
        }

        [Test]
        public void DollProfile_ReapplicationAndSynchronousClonePreserveBlendAndDynamics()
        {
            Material material = State(Solid(Color.gray));
            material.SetFloat("_CrtDollProfileBlend", 1);
            material.SetFloat("_Mono", CrtSignalProfile.DollMono);
            material.SetFloat("_NoiseDark", CrtSignalProfile.DollNoiseDark);
            material.SetFloat("_CoarseBlocks", ScreenDecayLogic.BlocksFor(CrtSignalProfile.DollMono));
            foreach (float blend in new[] { 0f, .5f, 1f })
            {
                material.SetFloat("_CrtDollProfileBlend", blend);
                material.SetFloat("_Grain", 99); material.SetFloat("_Scanline", 99);
                CrtSignalProfile.ApplySelected(material);
                using var processor = new CrtSignalProcessor();
                Assert.IsTrue(processor.PrepareDisplay(material, 1));
                Assert.AreEqual(Mathf.Lerp(CrtSignalProfile.Grain, CrtSignalProfile.DollGrain, blend), material.GetFloat("_Grain"));
                Assert.AreEqual(Mathf.Lerp(CrtSignalProfile.Scanline, CrtSignalProfile.DollScanline, blend), material.GetFloat("_Scanline"));
                Assert.AreEqual(Mathf.Lerp(CrtSignalProfile.NoiseFixed, CrtSignalProfile.DollNoiseFixed, blend), material.GetFloat("_NoiseFixed"));
                Assert.AreEqual((Vector4)Vector3.Lerp(CrtSignalProfile.Sigma, CrtSignalProfile.DollSigma, blend), material.GetVector("_CrtPsfSigma"));
                Assert.AreEqual(CrtSignalProfile.DollMono, material.GetFloat("_Mono"));
                Assert.AreEqual(CrtSignalProfile.DollNoiseDark, material.GetFloat("_NoiseDark"));
                var clone = Track(new Material(material));
                CrtSignalProfile.ApplySelected(clone);
                Assert.AreEqual(blend, clone.GetFloat("_CrtDollProfileBlend"));
                Assert.AreEqual(material.GetVector("_CrtPsfSigma"), clone.GetVector("_CrtPsfSigma"));
                var target = Track(new RenderTexture(64, 36, 0, RenderTextureFormat.ARGB32));
                Assert.IsTrue(target.Create());
                Assert.IsTrue(CrtSignalProcessor.Capture(clone, target));
                Assert.AreEqual(blend, clone.GetFloat("_CrtDollProfileBlend"));
                Assert.AreEqual(CrtSignalProfile.DollMono, clone.GetFloat("_Mono"));
                Assert.AreEqual(CrtSignalProfile.DollNoiseDark, clone.GetFloat("_NoiseDark"));
                Assert.AreEqual(material.GetFloat("_Scanline"), clone.GetFloat("_Scanline"));
            }
        }

        private static Color[] AllPixels(RenderTexture target)
        {
            RenderTexture? previous = RenderTexture.active;
            var read = new Texture2D(target.width, target.height, TextureFormat.RGBAFloat, false, true);
            try
            {
                RenderTexture.active = target;
                read.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
                return read.GetPixels();
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(read);
            }
        }

        private Texture2D Pattern(int width, int height)
        {
            var texture = Track(new Texture2D(width, height, TextureFormat.RGBA32, true, true)
            {
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp,
            });
            var pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    pixels[y * width + x] = new Color((x * 7 % 16) / 15f, (y * 5 % 12) / 11f, (x + y) % 2, 1);
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        // pre-glass で 1 度だけ符号化する経路と、タップごとに符号化する元の経路が同じ画を出すこと。
        [Test]
        public void EncodedPreGlass_ProducesTheSameProcessedImageAsEncodingEachTap()
        {
            Material material = State(Pattern(64, 48));
            using var processor = new CrtSignalProcessor();
            Assert.IsTrue(processor.PrepareDisplay(material, 1));
            Color[] encoded = AllPixels(processor.Processed!);

            material.SetFloat("_CrtSignalProfileSelected", 0);
            material.SetFloat("_CrtSignalBandwidth", 0);
            Assert.IsTrue(processor.PrepareDisplay(material, 1));
            var filter = Track(new Material(CrtSignalResources.Load().SignalFilter));
            filter.SetFloat("_Gain", CrtSignalProfile.Gain);
            filter.SetFloat("_Bandwidth", CrtSignalProfile.Bandwidth);
            filter.SetFloat("_SourceEncoded", 0);
            filter.SetFloat("_CenterEv", CrtSignalProfile.CenterEv);
            filter.SetFloat("_CenterRadius", CrtSignalProfile.CenterRadius);
            filter.SetFloat("_CenterFalloff", CrtSignalProfile.CenterFalloff);
            filter.SetFloat("_TintStrength", CrtSignalProfile.TintStrength);
            filter.SetFloat("_TintCoolness", CrtSignalProfile.TintCoolness);
            filter.SetVector("_Aperture", material.GetVector("_CrtAperture"));
            RenderTexture target = RenderTexture.GetTemporary(CrtSignalProfile.Width, CrtSignalProfile.Height, 0,
                RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            try
            {
                Graphics.Blit(processor.PreGlass, target, filter, 0);
                Color[] eachTap = AllPixels(target);
                Assert.That(eachTap.Length, Is.EqualTo(encoded.Length));
                float worst = 0;
                for (int i = 0; i < encoded.Length; i++)
                {
                    worst = Mathf.Max(worst, Mathf.Abs(encoded[i].r - eachTap[i].r));
                    worst = Mathf.Max(worst, Mathf.Abs(encoded[i].g - eachTap[i].g));
                    worst = Mathf.Max(worst, Mathf.Abs(encoded[i].b - eachTap[i].b));
                }
                Assert.That(worst, Is.LessThan(1e-5f));
            }
            finally { RenderTexture.ReleaseTemporary(target); }
        }

        [Test]
        public void Mips_AreBuiltOnlyWhileTheGaussianReadsACoarserLevel()
        {
            Material material = State(Solid(Color.gray));
            material.SetFloat("_CoarseBlocks", 0);
            using var processor = new CrtSignalProcessor();
            Assert.IsTrue(processor.PrepareDisplay(material, 1));
            Assert.That(processor.MipBuilds, Is.Zero);

            material.SetFloat("_CoarseBlocks", 8);
            Assert.IsTrue(processor.PrepareDisplay(material, 1));
            Assert.That(processor.MipBuilds, Is.EqualTo(1), "粗さが付いた瞬間に作る");
            Assert.IsTrue(processor.PrepareDisplay(material, 1));
            Assert.That(processor.MipBuilds, Is.EqualTo(1), "同じ入力なら作り直さない");
            Assert.IsTrue(processor.PrepareDisplay(material, 2));
            Assert.That(processor.MipBuilds, Is.EqualTo(2), "新しいコマでは作り直す");
        }

        [Test]
        public void CoarseLodFlip_ReplacesOnlyThePsfTargets_AndRebindsThem()
        {
            Material material = State(Solid(Color.gray));
            material.SetFloat("_CoarseBlocks", 0);
            using var processor = new CrtSignalProcessor();
            Assert.IsTrue(processor.PrepareDisplay(material, 1));
            RenderTexture linear = processor.LinearSource!, preGlass = processor.PreGlass!, processed = processor.Processed!;
            int width = processor.PsfSource!.width;

            material.SetFloat("_CoarseBlocks", 8);
            Assert.IsTrue(processor.PrepareDisplay(material, 1));
            Assert.That(processor.LinearSource, Is.SameAs(linear));
            Assert.That(processor.PreGlass, Is.SameAs(preGlass));
            Assert.That(processor.Processed, Is.SameAs(processed));
            Assert.That(processor.PsfSource!.width, Is.EqualTo(width * 2));
            Assert.That(material.GetTexture("_CrtRuntimePsfTex"), Is.SameAs(processor.PsfSource));
            Assert.That(material.GetTexture("_CrtProcessedSignalTex"), Is.SameAs(processed));
            Assert.That(material.GetFloat("_CrtSignalReady"), Is.EqualTo(1f));
        }

        [Test]
        public void HeldSource_ReusesOnlyPsf_WhileAgcAndOverlayStillChangeThePreGlassImage()
        {
            Material material = State(Solid(new Color(.08f, .04f, .02f, 1)));
            // Cache/AGC is measured with an unclipped post response, independently of the adopted dark grade.
            material.SetFloat("_CrtSignalProfileSelected", 0);
            // The band filter off keeps PreGlass linear, so the AGC ratio reads directly.
            material.SetFloat("_CrtSignalBandwidth", 0);
            material.SetFloat("_Exposure", 0); material.SetFloat("_Contrast", 1);
            material.SetFloat("_Saturation", 1); material.SetFloat("_Temperature", 0);
            material.SetFloat("_Vignette", 0); material.SetFloat("_Grain", 0);
            material.SetFloat("_Scanline", 0); material.SetFloat("_NoiseFixed", 0);
            material.SetTexture("_OverlayTex", Solid(new Color(.02f, .3f, .02f, 1)));
            material.SetTexture("_MaskTex", Texture2D.whiteTexture);
            using var processor = new CrtSignalProcessor();

            Assert.IsTrue(processor.PrepareDisplay(material, 7));
            Color initial = Center(processor.PreGlass!);
            Assert.IsTrue(processor.PrepareDisplay(material, 7));
            Assert.That(processor.PsfUpdates, Is.EqualTo(1));
            Assert.That(processor.InputUpdates, Is.EqualTo(1));
            Assert.That(processor.ProcessedFrames, Is.EqualTo(2));

            material.SetFloat("_ExposureBias", 1);
            Assert.IsTrue(processor.PrepareDisplay(material, 7));
            Color brighter = Center(processor.PreGlass!);
            Assert.That(brighter.r, Is.GreaterThan(initial.r * 1.5f), "同じ入力でもAGCを描き直す");

            material.SetFloat("_ExposureBias", 0);
            material.SetFloat("_OverlayStrength", 1);
            Assert.IsTrue(processor.PrepareDisplay(material, 7));
            Color overlay = Center(processor.PreGlass!);
            Assert.That(overlay.g, Is.GreaterThan(overlay.r + .1f), "同じ入力でも現在の素材が現れる");
            Assert.That(processor.PsfUpdates, Is.EqualTo(1));
            Assert.That(processor.ProcessedFrames, Is.EqualTo(4));
            Assert.That(processor.DisplayBindings, Is.EqualTo(4));
            Assert.That(processor.PreGlass!.width, Is.EqualTo(1280));
            Assert.That(processor.PreGlass.height, Is.EqualTo(1030));
        }

        [Test]
        public void PsfCache_RefreshesForSourceUpdatesReplacementResizeAndGeneration()
        {
            Texture2D live = Solid(Color.gray);
            Material material = State(live);
            using var processor = new CrtSignalProcessor();

            Assert.IsTrue(processor.PrepareDisplay(material, 1));
            // Apply alone does not advance Texture.updateCount in2022.3. Procedural producers
            // explicitly notify content updates; streamed producers advance the generation argument.
            live.SetPixel(0, 0, Color.blue); live.Apply(); live.IncrementUpdateCount();
            Assert.IsTrue(processor.PrepareDisplay(material, 1));
            Assert.That(processor.PsfUpdates, Is.EqualTo(2), "同じTextureでも更新された画素は読む");

            material.SetTexture("_LiveTex", Solid(Color.red));
            Assert.IsTrue(processor.PrepareDisplay(material, 1));
            Assert.That(processor.PsfUpdates, Is.EqualTo(3), "同寸法のカメラ切替でも更新する");

            material.SetTexture("_LiveTex", Solid(Color.green, 24, 16));
            Assert.IsTrue(processor.PrepareDisplay(material, 1));
            Assert.That(processor.PsfUpdates, Is.EqualTo(4));
            Assert.That(processor.LinearSource!.width, Is.EqualTo(24));
            Assert.That(processor.LinearSource.height, Is.EqualTo(16));
            Assert.That(processor.PsfSource!.width, Is.EqualTo(24));
            Assert.That(processor.PsfSource.height, Is.EqualTo(16));

            Assert.IsTrue(processor.PrepareDisplay(material, 2));
            Assert.That(processor.PsfUpdates, Is.EqualTo(5), "再生世代が変わったら同じTextureも読み直す");
            Assert.That(processor.InputUpdates, Is.EqualTo(5));
        }

        [Test]
        public void Dispose_ReleasesOwnedTargetsAndBindings_AndKeepsBorrowedObjectsAlive()
        {
            Texture2D live = Solid(Color.gray);
            Material material = State(live);
            using var processor = new CrtSignalProcessor();
            Assert.IsTrue(processor.PrepareDisplay(material, 1));
            RenderTexture linear = processor.LinearSource!;
            RenderTexture psf = processor.PsfSource!;
            RenderTexture preGlass = processor.PreGlass!;
            RenderTexture processed = processor.Processed!;
            Assert.That(material.GetTexture("_CrtProcessedSignalTex"), Is.SameAs(processed));

            processor.Dispose();
            processor.Dispose();

            Assert.IsTrue(processor.IsDisposed);
            Assert.IsTrue(linear == null && psf == null && preGlass == null && processed == null,
                "所有RTはReleaseだけでなく破棄する");
            Assert.IsNull(processor.LinearSource);
            Assert.IsNull(processor.PsfSource);
            Assert.IsNull(processor.PreGlass);
            Assert.IsNull(processor.Processed);
            Assert.That(material.GetFloat("_CrtSignalReady"), Is.Zero);
            Assert.That(material.GetFloat("_CrtRuntimePsfReady"), Is.Zero);
            Assert.IsNull(material.GetTexture("_CrtProcessedSignalTex"));
            Assert.IsNull(material.GetTexture("_CrtRuntimePsfTex"));
            Assert.IsTrue(live != null && material != null);
            Assert.That(material.GetTexture("_LiveTex"), Is.SameAs(live));
            Assert.Throws<ObjectDisposedException>(() => processor.PrepareDisplay(material, 1));
        }

        [Test]
        public void SynchronousCapture_UsesTheLatestOverlayWithoutChangingDisplayBindings()
        {
            Texture2D live = Solid(new Color(.3f, .02f, .02f, 1));
            Material material = State(live);
            material.SetTexture("_OverlayTex", Solid(new Color(.02f, .3f, .02f, 1)));
            material.SetTexture("_MaskTex", Texture2D.whiteTexture);
            using var display = new CrtSignalProcessor();
            Assert.IsTrue(display.PrepareDisplay(material, 12));
            Texture psf = material.GetTexture("_CrtRuntimePsfTex");
            Texture processed = material.GetTexture("_CrtProcessedSignalTex");
            var target = Track(new RenderTexture(64, 36, 0, RenderTextureFormat.ARGB32));
            Assert.IsTrue(target.Create());

            Assert.IsTrue(CrtSignalProcessor.Capture(material, target));
            Color before = Center(target);
            material.SetFloat("_OverlayStrength", 1);
            Assert.IsTrue(CrtSignalProcessor.Capture(material, target));
            Color after = Center(target);

            Assert.That(before.r, Is.GreaterThan(before.g + .05f));
            Assert.That(after.g, Is.GreaterThan(after.r + .05f), "同じsource frame内の撮影も素材変更を反映する");
            Assert.That(material.GetTexture("_LiveTex"), Is.SameAs(live));
            Assert.That(material.GetTexture("_CrtRuntimePsfTex"), Is.SameAs(psf));
            Assert.That(material.GetTexture("_CrtProcessedSignalTex"), Is.SameAs(processed));
            Assert.That(material.GetFloat("_CrtSignalReady"), Is.EqualTo(1));
            Assert.That(material.GetFloat("_CrtRuntimePsfReady"), Is.EqualTo(1));
            Assert.That(display.ProcessedFrames, Is.EqualTo(1));
        }
    }
}
