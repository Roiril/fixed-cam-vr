#nullable enable
using System;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 撮影の PNG 書き出し。Unity 自身の PNG 読み込みへ食わせて往復を確かめる
    /// （CRC / Adler / zlib ヘッダのどれかが壊れていれば LoadImage が false を返す）。
    /// </summary>
    public sealed class ShotPngWriterTests
    {
        private static Texture2D Load(byte[] png)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            Assert.IsTrue(tex.LoadImage(png), "Unity が PNG として読めない（チャンクか圧縮が壊れている）");
            return tex;
        }

        // 2x2。下の行（バッファの先頭）が赤・緑、上の行が青・白（bottomUp の並び）。
        private static byte[] BottomUp2x2() => new byte[]
        {
            255, 0, 0, 255,     0, 255, 0, 255,      // 下の行
            0, 0, 255, 255,     255, 255, 255, 255,  // 上の行
        };

        [Test]
        public void Encode_BottomUpBuffer_ComesBackUpright()
        {
            byte[] png = ShotPngWriter.Encode(BottomUp2x2(), 2, 2, bottomUp: true, opaque: true);
            Texture2D tex = Load(png);
            try
            {
                Assert.AreEqual(2, tex.width);
                Assert.AreEqual(2, tex.height);
                // Texture2D は左下原点。bottomUp で渡した並びがそのまま復元されるはず。
                Assert.AreEqual((Color32)new Color32(255, 0, 0, 255), (Color32)tex.GetPixel(0, 0), "左下=赤");
                Assert.AreEqual((Color32)new Color32(0, 255, 0, 255), (Color32)tex.GetPixel(1, 0), "右下=緑");
                Assert.AreEqual((Color32)new Color32(0, 0, 255, 255), (Color32)tex.GetPixel(0, 1), "左上=青");
                Assert.AreEqual((Color32)new Color32(255, 255, 255, 255), (Color32)tex.GetPixel(1, 1), "右上=白");
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }

        [Test]
        public void Encode_TopDownBuffer_IsNotFlipped()
        {
            // 同じバッファを上から下として書けば、上下が入れ替わって復元される。
            byte[] png = ShotPngWriter.Encode(BottomUp2x2(), 2, 2, bottomUp: false, opaque: true);
            Texture2D tex = Load(png);
            try
            {
                Assert.AreEqual((Color32)new Color32(0, 0, 255, 255), (Color32)tex.GetPixel(0, 0), "左下=青");
                Assert.AreEqual((Color32)new Color32(255, 0, 0, 255), (Color32)tex.GetPixel(0, 1), "左上=赤");
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }

        [Test]
        public void Encode_Opaque_ForcesAlpha255_WithoutTouchingTheSource()
        {
            var src = new byte[] { 10, 20, 30, 0 };
            byte[] png = ShotPngWriter.Encode(src, 1, 1, bottomUp: true, opaque: true);
            Texture2D tex = Load(png);
            try
            {
                Assert.AreEqual((Color32)new Color32(10, 20, 30, 255), (Color32)tex.GetPixel(0, 0));
                Assert.AreEqual(0, src[3], "入力を書き換えていない");
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }

        [Test]
        public void Encode_KeepsAlpha_WhenNotOpaque()
        {
            var src = new byte[] { 200, 100, 50, 64 };
            byte[] png = ShotPngWriter.Encode(src, 1, 1, bottomUp: true, opaque: false);
            Texture2D tex = Load(png);
            try
            {
                Color32 c = tex.GetPixel(0, 0);
                Assert.AreEqual(64, c.a, 1, "透過 PNG の alpha が残る");
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }

        [Test]
        public void Encode_LargeImage_RoundTripsEveryPixel()
        {
            // Adler32 の NMAX（5552）と deflate の窓をまたぐ大きさ。勾配で全画素が違う値を持つ。
            const int W = 97, H = 61;
            var src = new byte[W * H * 4];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = (y * W + x) * 4;
                    src[i] = (byte)(x * 2);
                    src[i + 1] = (byte)(y * 4);
                    src[i + 2] = (byte)((x + y) & 0xFF);
                    src[i + 3] = 255;
                }
            byte[] png = ShotPngWriter.Encode(src, W, H, bottomUp: true, opaque: false);
            Texture2D tex = Load(png);
            try
            {
                Assert.AreEqual(W, tex.width);
                Assert.AreEqual(H, tex.height);
                Color32[] px = tex.GetPixels32();   // 左下原点・行は下から上
                for (int i = 0; i < px.Length; i++)
                {
                    Assert.AreEqual(src[i * 4], px[i].r, $"r @{i}");
                    Assert.AreEqual(src[i * 4 + 1], px[i].g, $"g @{i}");
                    Assert.AreEqual(src[i * 4 + 2], px[i].b, $"b @{i}");
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }

        [Test]
        public void Encode_RejectsBadSizes()
        {
            Assert.Throws<ArgumentException>(() => ShotPngWriter.Encode(new byte[7], 1, 2, true, true));
            Assert.Throws<ArgumentOutOfRangeException>(() => ShotPngWriter.Encode(new byte[4], 0, 1, true, true));
        }

        [Test]
        public void Unpremultiply_RestoresStraightAlpha()
        {
            // 半透明の赤（straight=255,0,0 a=128）を premultiplied にすると (128,0,0,128)。
            var px = new byte[] { 128, 0, 0, 128,   0, 0, 0, 0,   10, 20, 30, 255 };
            ShotPngWriter.UnpremultiplyInPlace(px);
            Assert.AreEqual(255, px[0], 1, "premultiplied を割り戻す");
            Assert.AreEqual(0, px[1]);
            Assert.AreEqual(128, px[3], "alpha は変えない");
            Assert.AreEqual(0, px[4], "alpha 0 は rgb 0 のまま");
            Assert.AreEqual(10, px[8], "不透明は触らない");
        }

        [Test]
        public void HasCoverage_IsFalseForAnEmptyLayer()
        {
            Assert.IsFalse(ShotPngWriter.HasCoverage(new byte[16]));
            Assert.IsTrue(ShotPngWriter.HasCoverage(new byte[] { 0, 0, 0, 0,  0, 0, 0, 9 }));
        }
    }
}
