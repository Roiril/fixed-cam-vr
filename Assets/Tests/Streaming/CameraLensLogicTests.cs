#nullable enable
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    public sealed class CameraLensLogicTests
    {
        private const float SourceAspect = 4f / 3f;

        [TestCase("A", false, false)]
        [TestCase("B", false, false)]
        [TestCase("C", false, true)]
        [TestCase("C", true, false)]
        [TestCase(null, false, false)]
        public void OnlyCameraC_UsesTheFisheye_AndNotDuringDollPov(string? camera, bool pov, bool expected)
        {
            Assert.That(CameraLensLogic.UsesFisheye(camera, pov), Is.EqualTo(expected));
            Assert.That(CameraLensLogic.Uniform(camera, pov).x > 0, Is.EqualTo(expected));
        }

        [Test]
        public void Version3_MatchesTheStudy()
        {
            Assert.That(CameraLensLogic.CenterScale, Is.EqualTo(1.365f).Within(.005f));
            Vector2 edge = CameraLensLogic.SourceFromScreen(new Vector2(.5f, 0));
            Assert.That(edge.x, Is.EqualTo(.5f).Within(1e-4f), "ソースの右端は画面の右端の中央に来る");
            Assert.That(edge.y, Is.EqualTo(0).Within(1e-6f));
        }

        [Test]
        public void CornersOfTheScreen_ReadOutsideTheSource()
        {
            Vector2 corner = CameraLensLogic.SourceFromScreen(new Vector2(.5f, .5f / SourceAspect));
            Assert.That(corner.x, Is.GreaterThan(.5f));
            Assert.That(corner.y, Is.GreaterThan(.5f / SourceAspect));
        }

        [TestCase(.1f, .05f)]
        [TestCase(.3f, -.2f)]
        [TestCase(-.48f, .1f)]
        public void Forward_And_Inverse_RoundTrip(float x, float y)
        {
            var source = new Vector2(x, y);
            Vector2 back = CameraLensLogic.SourceFromScreen(CameraLensLogic.ScreenFromSource(source));
            Assert.That((back - source).magnitude, Is.LessThan(1e-4f));
        }

        [Test]
        public void StraightLine_BendsLikeVersion3()
        {
            // 画面の上から 1/4 を中央で通る、ソースで水平な線。右端でどれだけ動くかを画面高さで見る。
            float height = 1f / SourceAspect;
            float yAtCentre = height * .25f;
            float line = CameraLensLogic.SourceFromScreen(new Vector2(0, yAtCentre)).y;
            float low = 0, high = 5;
            for (int i = 0; i < 40; i++)
            {
                float middle = (low + high) * .5f;
                if (CameraLensLogic.ScreenFromSource(new Vector2(middle, line)).x < .5f) low = middle;
                else high = middle;
            }
            float yAtEdge = CameraLensLogic.ScreenFromSource(new Vector2(low, line)).y;
            Assert.That((yAtCentre - yAtEdge) / height, Is.EqualTo(.065f).Within(.015f));
        }

        [Test]
        public void Shader_ReadsTheSamePointAsTheLogic()
        {
            bool oldAsync = ShaderUtil.allowAsyncCompilation;
            var material = new Material(CrtSignalResources.Load().Composite);
            var normal = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            var live = new Texture2D(640, 480, TextureFormat.RGB24, false);
            var target = new RenderTexture(64, 48, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var pixel = new Texture2D(64, 48, TextureFormat.RGBAFloat, false, true);
            RenderTexture? previous = RenderTexture.active;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                normal.SetPixel(0, 0, new Color(.5f, .5f, 1, 1)); normal.Apply();
                material.SetTexture("_CrtNormalTex", normal);
                // 映像の寸法が読む範囲を決める。未設定だと全点が中央へ丸められる。
                material.SetTexture("_LiveTex", live);
                material.SetFloat("_CrtReference", 1);
                material.SetVector("_CrtAperture", new Vector4(0, 0, 1, 1));
                material.SetVector("_CrtLogicalCrop", new Vector4(.75f, 1, 0, 0));
                material.SetVector("_LiveScale", new Vector4(.75f, 1, 0, 0));
                material.SetFloat("_FrameAspect", 16f / 9f);
                material.SetFloat("_CrtLensWarp", CrtSignalProfile.LensWarp);
                material.EnableKeyword("CRT_STYLE_DIAGNOSTIC");
                material.SetFloat("_CrtStyleProbe", 1);
                CameraLensLogic.Apply(material, "C", false);
                Graphics.Blit(Texture2D.blackTexture, target, material, 0);
                RenderTexture.active = target;
                pixel.ReadPixels(new Rect(0, 0, 64, 48), 0, 0, false);
                foreach ((int x, int y) in new[] { (32, 24), (48, 24), (40, 36), (20, 12), (58, 24) })
                {
                    Color probe = pixel.GetPixel(x, y);
                    var screen = new Vector2((probe.b - .5f) / .75f, (probe.a - .5f) / SourceAspect);
                    var read = new Vector2((probe.r - .5f) / .75f, (probe.g - .5f) / SourceAspect);
                    Vector2 expected = CameraLensLogic.SourceFromScreen(screen);
                    Assert.That((read - expected).magnitude, Is.LessThan(.002f), $"pixel {x},{y} probe {probe.r:F5},{probe.g:F5},{probe.b:F5},{probe.a:F5} read {read.x:F5},{read.y:F5} expected {expected.x:F5},{expected.y:F5}");
                }
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = oldAsync;
                RenderTexture.active = previous;
                Object.DestroyImmediate(material); Object.DestroyImmediate(normal); Object.DestroyImmediate(live);
                Object.DestroyImmediate(target); Object.DestroyImmediate(pixel);
            }
        }
    }
}
