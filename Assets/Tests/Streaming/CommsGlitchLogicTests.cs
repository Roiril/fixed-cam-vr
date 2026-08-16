#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 連絡の面が周回とともに壊れていく（`canon/LEDGER.md` 0068）。
    ///
    /// ⚠ シェーダの誤りはここに 1 件も出ない（この codebase は同じ型で 3 回踏んでいる）。
    /// ここが固定するのは<b>いつ・どれだけ壊れるか</b>だけで、
    /// <b>どう見えるか</b>は `menu comms-preview -Set decay=` の絵でしか判定できない。
    /// </summary>
    public sealed class CommsGlitchLogicTests
    {
        /// <summary>
        /// ⚠⚠ <b>1 周目は 1 画素も変わらない。</b> 進み 0 は本編の頭そのもので、
        /// そこで壊れが出ると<b>映像がまだ綺麗なのに連絡の面だけ先に壊れる</b>。
        /// </summary>
        [Test]
        public void Progress0_ChangesNothing()
        {
            Assert.AreEqual(0f, CommsGlitchLogic.LevelFor(0f), "進み 0 では強さ 0");
            for (float t = 0f; t < 12f; t += 0.037f)
            {
                Assert.AreEqual(0f, CommsGlitchLogic.OffsetXAt(t, 0f), 0f, "横へも飛ばない");
                Assert.IsFalse(CommsGlitchLogic.BurstAt(t, 0f), "発作も起きない");
            }
        }

        /// <summary>進み 1（3 周目 A 以降）で強さが 1 に着く。</summary>
        [Test]
        public void Progress1_ReachesFullLevel()
        {
            Assert.AreEqual(1f, CommsGlitchLogic.LevelFor(1f), 0.0001f);
            Assert.AreEqual(1f, CommsGlitchLogic.LevelFor(1.7f), 0.0001f, "範囲外は 1 で頭打ち");
            Assert.AreEqual(0f, CommsGlitchLogic.LevelFor(-3f), "負も 0 で止まる");
        }

        /// <summary>
        /// ⚠ <b>序盤は線形より軽い。</b> 2 周目の頭（進み 0.33）で線形なら 0.33 だが、
        /// 指数 2 なので 0.11 — 映像の劣化（そこではまだ 1.6 画素／ブロック）と足並みが揃う。
        /// </summary>
        [Test]
        public void EarlyLaps_AreLighterThanLinear()
        {
            Assert.Less(CommsGlitchLogic.LevelFor(0.33f), 0.33f * 0.5f, "2 周目の頭は線形の半分より軽い");
            Assert.Less(CommsGlitchLogic.LevelFor(0.5f), 0.5f, "中ほども線形より軽い");
            Assert.Greater(CommsGlitchLogic.LevelFor(0.9f), 0.75f, "終盤はちゃんと効く");
        }

        /// <summary>単調（下がらない）。下がると「直った」に見えて装置が壊れていく筋が崩れる。</summary>
        [Test]
        public void Level_IsMonotonic()
        {
            float prev = -1f;
            for (float p = 0f; p <= 1f; p += 0.01f)
            {
                float lv = CommsGlitchLogic.LevelFor(p);
                Assert.GreaterOrEqual(lv, prev, $"p={p:0.00} で下がった");
                prev = lv;
            }
        }

        /// <summary>
        /// ⚠⚠ <b>発作は「たまに」でなければならない。</b> ずっと立っていると面が壊れっぱなしになり、
        /// 周が進んだことが伝わらない（壊れは<b>差</b>でしか見えない）。
        /// ⚠ 2026-08-17 の初版は強さを 1 本へ畳んでシェーダへ渡していたため、
        /// <b>発作が立っていない刻みでもブロックが出ていた</b>（絵で見つけた）。
        /// いまは <see cref="CommsGlitchLogic.BurstAt"/> が唯一の判定で、シェーダは 0/1 で受け取る。
        /// </summary>
        [Test]
        public void Burst_IsOccasional_NotConstant()
        {
            int burst = 0, total = 0;
            for (float t = 0f; t < 60f; t += CommsGlitchLogic.BurstTickSec)
            {
                total++;
                if (CommsGlitchLogic.BurstAt(t, 1f)) burst++;
            }
            Assert.Greater(burst, 0, "60 秒で 1 度も発作が起きないのは薄すぎる");
            Assert.Less(burst / (float)total, 0.5f, "半分以上が発作なら『たまに』ではない");
        }

        /// <summary>発作の頻度は進みで増える（1 周目は起きず、3 周目でよく起きる）。</summary>
        [Test]
        public void BurstChance_GrowsWithProgress()
        {
            Assert.Less(CountBursts(0.25f), CountBursts(1f), "進むほど発作が増える");
            Assert.AreEqual(0, CountBursts(0f), "進み 0 では 1 度も起きない");
        }

        private static int CountBursts(float progress)
        {
            float lv = CommsGlitchLogic.LevelFor(progress);
            int n = 0;
            for (float t = 0f; t < 120f; t += CommsGlitchLogic.BurstTickSec)
                if (CommsGlitchLogic.BurstAt(t, lv)) n++;
            return n;
        }

        /// <summary>
        /// ⚠ <b>横飛びの上限を超えない。</b> 面の幅は 0.76m なので、これ以上飛ぶと
        /// 飛んでいるあいだ文字が追えない（③が読まれないと締めのカットが進まない）。
        /// </summary>
        [Test]
        public void OffsetX_StaysWithinLimit()
        {
            for (float t = 0f; t < 120f; t += 0.05f)
            {
                float dx = CommsGlitchLogic.OffsetXAt(t, 1f);
                Assert.LessOrEqual(System.Math.Abs(dx), CommsGlitchLogic.MaxOffsetM + 1e-5f,
                                   $"t={t:0.00} で {dx:0.000}m 飛んだ");
            }
        }

        /// <summary>
        /// ⚠ <b>乱数を使っていない。</b> 同じ時刻・同じ進みなら必ず同じ絵になる
        /// （走行を並べて比べられなくなるのを防ぐ）。
        /// </summary>
        [Test]
        public void SameInput_GivesSameOutput()
        {
            for (float t = 0.1f; t < 20f; t += 1.3f)
            {
                Assert.AreEqual(CommsGlitchLogic.BurstAt(t, 0.7f), CommsGlitchLogic.BurstAt(t, 0.7f));
                Assert.AreEqual(CommsGlitchLogic.SeedAt(t), CommsGlitchLogic.SeedAt(t));
                Assert.AreEqual(CommsGlitchLogic.OffsetXAt(t, 0.7f),
                                CommsGlitchLogic.OffsetXAt(t, 0.7f));
            }
        }

        /// <summary>
        /// ⚠ <b>種は刻みの中では動かない。</b> 毎フレーム動かすとブロックが砂嵐になり、
        /// 「矩形が貼り付いている」というデジタル破損の顔が消える。
        /// </summary>
        [Test]
        public void Seed_HoldsWithinTick_AndChangesAcrossTicks()
        {
            float tick = CommsGlitchLogic.BurstTickSec;
            Assert.AreEqual(CommsGlitchLogic.SeedAt(tick * 3f + 0.01f),
                            CommsGlitchLogic.SeedAt(tick * 3f + tick * 0.9f),
                            "同じ刻みの中では変わらない");
            Assert.AreNotEqual(CommsGlitchLogic.SeedAt(tick * 3f + 0.01f),
                               CommsGlitchLogic.SeedAt(tick * 4f + 0.01f),
                               "刻みが変われば変わる");
        }

        /// <summary>種は 0..1 に収まる（シェーダが frac で使うので外れても壊れはしないが、契約として）。</summary>
        [Test]
        public void Seed_IsNormalized()
        {
            for (float t = 0f; t < 30f; t += 0.07f)
            {
                float s = CommsGlitchLogic.SeedAt(t);
                Assert.GreaterOrEqual(s, 0f);
                Assert.LessOrEqual(s, 1f);
            }
        }
    }
}
