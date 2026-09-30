#nullable enable
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 撮影の GPU 側の経路（RT → CPU → PNG）が向きも色も保つこと、
    /// 「ライブを黒に置き換えた合成」と「単眼カメラの描画」が実際に絵を作ることを、
    /// 実際に描いて確かめる（純ロジックのテストでは通ってしまう領域）。
    /// </summary>
    public sealed class ExperienceShotGpuTests
    {
        private readonly System.Collections.Generic.List<Object> _garbage = new();

        [TearDown]
        public void TearDown()
        {
            foreach (Object o in _garbage) if (o != null) Object.DestroyImmediate(o);
            _garbage.Clear();
        }

        private T Track<T>(T o) where T : Object
        {
            _garbage.Add(o);
            return o;
        }

        private static Color32 PixelAt(byte[] rgbaBottomUp, int width, int x, int yFromBottom)
        {
            int i = (yFromBottom * width + x) * 4;
            return new Color32(rgbaBottomUp[i], rgbaBottomUp[i + 1], rgbaBottomUp[i + 2], rgbaBottomUp[i + 3]);
        }

        [Test]
        public void ReadRgba_ThenPng_KeepsTheOrientationOfTheSource()
        {
            // 左下=赤 / 右下=緑 / 左上=青 / 右上=白（Texture2D は左下原点）。
            var src = Track(new Texture2D(2, 2, TextureFormat.RGBA32, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            });
            src.SetPixels32(new[]
            {
                new Color32(255, 0, 0, 255), new Color32(0, 255, 0, 255),
                new Color32(0, 0, 255, 255), new Color32(255, 255, 255, 255),
            });
            src.Apply();

            var rt = Track(new RenderTexture(8, 8, 0, RenderTextureFormat.ARGB32));
            rt.Create();
            Graphics.Blit(src, rt);

            byte[]? rgba = ExperienceShotCapture.ReadRgba(rt);
            Assert.IsNotNull(rgba);
            Assert.AreEqual(8 * 8 * 4, rgba!.Length);
            Assert.AreEqual(new Color32(255, 0, 0, 255), PixelAt(rgba, 8, 1, 1), "左下=赤");
            Assert.AreEqual(new Color32(0, 255, 0, 255), PixelAt(rgba, 8, 6, 1), "右下=緑");
            Assert.AreEqual(new Color32(0, 0, 255, 255), PixelAt(rgba, 8, 1, 6), "左上=青");

            // PNG に書いて読み戻しても、上下が入れ替わらない。
            byte[] png = ShotPngWriter.Encode(rgba, 8, 8, bottomUp: true, opaque: true);
            var back = Track(new Texture2D(2, 2, TextureFormat.RGBA32, false));
            Assert.IsTrue(back.LoadImage(png));
            Assert.AreEqual(new Color32(255, 0, 0, 255), (Color32)back.GetPixel(1, 1), "PNG の左下=赤");
            Assert.AreEqual(new Color32(0, 0, 255, 255), (Color32)back.GetPixel(1, 6), "PNG の左上=青");
        }

        [Test]
        public void CompositeWithoutLive_KeepsTheOverlay_DropsTheLive_AndLeavesTheOriginalUntouched()
        {
            Shader? shader = Shader.Find("FixedCamVr/ScreenComposite");
            if (shader == null) Assert.Ignore("ScreenComposite が見つからない（シェーダ未取り込み）");

            Texture2D Solid(Color32 c)
            {
                var tex = Track(new Texture2D(4, 4, TextureFormat.RGBA32, false, true)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                });
                var px = new Color32[16];
                for (int i = 0; i < px.Length; i++) px[i] = c;
                tex.SetPixels32(px);
                tex.Apply();
                return tex;
            }

            // マスクは右半分だけ白（スクリーン枠空間）。左はライブ、右は素材（緑）が出る。
            var mask = Track(new Texture2D(4, 4, TextureFormat.RGBA32, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            });
            var mp = new Color32[16];
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                    mp[y * 4 + x] = x >= 2 ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 255);
            mask.SetPixels32(mp);
            mask.Apply();

            Texture2D live = Solid(new Color32(200, 20, 20, 255));
            var material = Track(new Material(shader));
            material.SetTexture("_LiveTex", live);
            material.SetVector("_LiveScale", new Vector4(1, 1, 0, 0));
            material.SetTexture("_OverlayTex", Solid(new Color32(20, 200, 20, 255)));
            material.SetVector("_OverlayScale", new Vector4(1, 1, 0, 0));
            material.SetTexture("_MaskTex", mask);
            material.SetFloat("_OverlayStrength", 1f);

            var shown = Track(new RenderTexture(64, 36, 0, RenderTextureFormat.ARGB32));
            var layers = Track(new RenderTexture(64, 36, 0, RenderTextureFormat.ARGB32));
            shown.Create();
            layers.Create();

            Assert.IsTrue(EndingFrameCapture.CaptureComposite(material, shown));
            Assert.IsTrue(ExperienceShotCapture.CaptureCompositeWithoutLive(material, layers));

            byte[] a = ExperienceShotCapture.ReadRgba(shown)!;
            byte[] b = ExperienceShotCapture.ReadRgba(layers)!;
            Color32 shownLeft = PixelAt(a, 64, 16, 18), shownRight = PixelAt(a, 64, 48, 18);
            Color32 layersLeft = PixelAt(b, 64, 16, 18), layersRight = PixelAt(b, 64, 48, 18);

            Assert.Greater(shownLeft.r, shownLeft.g + 40, $"① 左はライブの赤（{shownLeft.r},{shownLeft.g},{shownLeft.b}）");
            Assert.Greater(shownRight.g, shownRight.r + 40, $"① 右は素材の緑（{shownRight.r},{shownRight.g},{shownRight.b}）");
            Assert.Less(layersLeft.r, 30, $"③ 左はライブが消えて黒（{layersLeft.r},{layersLeft.g},{layersLeft.b}）");
            Assert.Greater(layersRight.g, layersRight.r + 40,
                $"③ 右は素材の緑が残る＝描けている（{layersRight.r},{layersRight.g},{layersRight.b}）");
            Assert.AreSame(live, material.GetTexture("_LiveTex"), "画面の材質は書き換えていない");
        }

        [Test]
        public void RenderView_DrawsWhatTheCameraSees()
        {
            var go = Track(new GameObject("shot-test-camera"));
            var cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(1f, 0f, 0f, 1f);
            cam.enabled = false;

            RenderTexture? rt = ExperienceShotCapture.RenderView(
                cam, go.transform, cam.projectionMatrix, 32, 32, out GameObject? shotCamera);
            Assert.IsNotNull(rt);
            Track(rt!);
            Assert.IsNotNull(shotCamera, "描いたカメラは呼び出し側へ渡される（読み終えるまで壊さない）");

            byte[]? rgba = ExperienceShotCapture.ReadRgba(rt!);
            Assert.IsNotNull(rgba);
            Color32 c = PixelAt(rgba!, 32, 16, 16);
            Assert.Greater(c.r, 200, $"背景色の赤が描かれている（実際 {c.r},{c.g},{c.b}）");
            Assert.Less(c.g, 40);

            Object.DestroyImmediate(shotCamera);
            Assert.IsNull(GameObject.Find("[ShotCamera]"), "読み終えたら一時カメラは残らない");
        }
    }
}
