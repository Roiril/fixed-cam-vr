#nullable enable
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 題字の書き出し（2026-10-14・Claude の案）。筆の届く時刻は版ごとの図（<c>tools/make-title-writeon.py</c>）が持ち、
    /// 状態機械は出の進みを時間に比例させ、書き上がってから墨を乾かす。
    /// </summary>
    public sealed class TitleWriteOnTests
    {
        private const float Dt = 1f / 72f;
        private static readonly TitleInput Ready = new TitleInput { concealReady = true };

        private static TitleLogic Summoned()
        {
            var l = new TitleLogic();
            l.Begin();
            l.RequestAdvance();
            return l;
        }

        private static void Run(TitleLogic l, float sec)
        {
            for (float t = 0f; t < sec; t += Dt) l.Tick(Dt, Ready);
        }

        [Test]
        public void Reveal_IsProportionalToTime_TheMapCarriesTheBrushPace()
        {
            var l = Summoned();
            Run(l, TitleLogic.InDelaySec + TitleLogic.InSec * 0.5f);
            Assert.AreEqual(0.5f, l.Weights.reveal, 0.03f, "ease を掛けると図の緩急と二重になる");
        }

        [Test]
        public void Ink_IsWetWhileWriting_ThenDriesDuringTheSoundsSustain()
        {
            var l = Summoned();
            Run(l, TitleLogic.InSec * 0.5f);
            Assert.AreEqual(1f, l.Weights.wet, 1e-6f, "書いている最中は濡れている");
            Run(l, TitleLogic.InSec * 0.5f + 0.05f);
            Assert.AreEqual(TitleStage.Hold, l.Stage);
            float prev = l.Weights.wet;
            Assert.Greater(prev, 0.9f, "書き上がった瞬間に乾いた（濡れが段で切れる）");
            for (float t = 0f; t < TitleLogic.SettleSec + 0.2f; t += Dt)
            {
                l.Tick(Dt, Ready);
                Assert.LessOrEqual(l.Weights.wet, prev + 1e-6f, "乾く途中で濡れが戻った");
                prev = l.Weights.wet;
            }
            Assert.AreEqual(0f, prev, 1e-6f, "乾き切っていない（現行の版と同じ画に戻らない）");
            Assert.LessOrEqual(TitleLogic.InSec + TitleLogic.SettleSec, 2.6f, "音が張っている間（〜2.4 秒）に乾かす");
        }

        [Test]
        public void Dismissing_WhileWet_DriesQuickly_WithoutAJump()
        {
            var l = Summoned();
            Run(l, TitleLogic.InSec + 0.2f);
            float wet = l.Weights.wet;
            Assert.Greater(wet, 0.5f);
            Run(l, TitleLogic.DismissLockoutSec);
            wet = l.Weights.wet;
            l.RequestDismiss();
            Assert.AreEqual(wet, l.Weights.wet, 0.02f, "押した瞬間に濡れが飛んだ");
            Run(l, TitleLogic.WetOutSec + Dt);
            Assert.AreEqual(0f, l.Weights.wet, 1e-6f, "閉じる演出（焼け）に濡れが残っている");
        }

        // ---- 書き出しの図 ----------------------------------------------------------------

        private static Color32[] LoadPng(string name, out int w, out int h)
        {
            string path = Path.Combine(Application.dataPath, "Resources", "Title", name);
            Assert.IsTrue(File.Exists(path), $"{name} が無い（py -3.11 tools/make-title-writeon.py）");
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, linear: true);
            Assert.IsTrue(tex.LoadImage(File.ReadAllBytes(path)));
            w = tex.width; h = tex.height;
            Color32[] px = tex.GetPixels32();
            Object.DestroyImmediate(tex);
            return px;
        }

        [Test]
        public void WriteMap_GivesEveryInkPixelATime_InStrokeOrder()
        {
            Color32[] write = LoadPng("MawarimiTitleWrite.png", out int w, out int h);
            Color32[] art = LoadPng("MawarimiTitle.png", out int aw, out int ah);
            Assert.AreEqual(1024, w);
            Assert.AreEqual(512, h);
            Assert.AreEqual(aw * h, ah * w, "図と版の縦横比が違う");
            int sx = aw / w, sy = ah / h;
            double mawari = 0, ri = 0, mi = 0, deco = 0;
            int nMawari = 0, nRi = 0, nMi = 0, nDeco = 0, untimed = 0;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                // ⚠ PNG の行は下から（LoadImage は Unity の向き）。版も同じ向きで読むので揃う。
                Color32 a = art[(y * sy) * aw + x * sx];
                Color32 t = write[y * w + x];
                if (a.r > 160)
                {
                    if (t.r >= 254) untimed++;
                    if (x < 440) { mawari += t.r; nMawari++; } else { ri += t.r; nRi++; }
                }
                if (a.g > 160) { if (t.g >= 254) untimed++; mi += t.g; nMi++; }
                if (a.a > 160) { if (t.a >= 254) untimed++; deco += t.a; nDeco++; }
            }
            Assert.Greater(nMawari, 1000); Assert.Greater(nRi, 500); Assert.Greater(nMi, 1000); Assert.Greater(nDeco, 300);
            Assert.AreEqual(0, untimed, "筆の届く時刻を持たない墨がある（版を焼き直して図を忘れた？）");
            Assert.Less(mawari / nMawari, ri / nRi, "廻 → リ の順になっていない");
            Assert.Less(ri / nRi, mi / nMi, "リ → 視 の順になっていない");
            Assert.Greater(deco / nDeco, 0.75 * 255, "添え（払い・ルビ）が主の字より先に出る");
        }
    }
}
