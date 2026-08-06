#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Tests.Streaming
{
    /// <summary>
    /// 場所別の明るさ（<see cref="SourceLumaMap"/>）。CG 人形の光量はここを基準に決まるので、
    /// 「全画面平均に潰れていないか」「測れていないときに 0 を返さないか」が要点。
    /// </summary>
    public sealed class SourceLumaMapTests
    {
        // 左半分だけ明るい絵を作る（黒いカーテンと明るい床、のような画）。
        private static SourceLumaMap HalfBright()
        {
            var m = new SourceLumaMap();
            m.BeginFrame();
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                    m.Add(x / 32f, y / 32f, x < 16 ? 0.8f : 0.05f);
            m.EndFrame();
            return m;
        }

        [Test]
        public void 測る前は未測定を返す()
        {
            var m = new SourceLumaMap();
            Assert.AreEqual(-1f, m.Mean, 1e-5f);
            Assert.AreEqual(-1f, m.Sample(0.5f, 0.5f), 1e-5f, "0 を返すと人形が真っ黒になる");
        }

        [Test]
        public void 場所ごとに違う明るさを返す()
        {
            SourceLumaMap m = HalfBright();
            Assert.AreEqual(0.425f, m.Mean, 0.02f, "全画面平均は明暗の中間");
            Assert.Greater(m.Sample(0.1f, 0.5f), 0.7f, "明るい側");
            Assert.Less(m.Sample(0.9f, 0.5f), 0.15f, "暗い側");
        }

        [Test]
        public void 全画面平均だけでは人形の明るさを決められない()
        {
            // これが局所を持つ理由そのもの。暗い側に立つ人形を平均 0.42 に合わせると明るく浮く。
            SourceLumaMap m = HalfBright();
            Assert.Greater(m.Mean / m.Sample(0.9f, 0.5f), 3f);
        }

        [Test]
        public void 境界では段にならず滑らかに変わる()
        {
            // タイル境界で段になると、人形が少し歩いただけで明るさが跳ねる。
            SourceLumaMap m = HalfBright();
            float a = m.Sample(0.44f, 0.5f);
            float b = m.Sample(0.50f, 0.5f);
            float c = m.Sample(0.56f, 0.5f);
            Assert.Greater(a, b);
            Assert.Greater(b, c);
        }

        [Test]
        public void 範囲外は端の値へ丸める()
        {
            SourceLumaMap m = HalfBright();
            Assert.AreEqual(m.Sample(0f, 0.5f), m.Sample(-2f, 0.5f), 1e-4f);
            Assert.AreEqual(m.Sample(1f, 0.5f), m.Sample(3f, 0.5f), 1e-4f);
        }

        [Test]
        public void 一点も測れなかったフレームは前回の値を残す()
        {
            // 読めないテクスチャに当たったフレームで 0 に塗ると、その 1 フレームだけ人形が消える。
            SourceLumaMap m = HalfBright();
            float before = m.Sample(0.1f, 0.5f);
            m.BeginFrame();
            m.EndFrame();
            Assert.AreEqual(before, m.Sample(0.1f, 0.5f), 1e-4f);
        }
    }
}
