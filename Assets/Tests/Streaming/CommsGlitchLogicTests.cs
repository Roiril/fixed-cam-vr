#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 連絡の面が周回とともに壊れていく（`canon/LEDGER.md` 0068 / <b>0069</b>）。
    ///
    /// 0069 で**壊れ方そのものを作り直した** — 面の上にレイヤを貼るのをやめ、
    /// **印字そのものが壊れる**形にした。ここが固定するのは「いつ・どれだけ壊れるか」と
    /// 「**文面の骨格を壊していないか**」で、<b>どう見えるか</b>は
    /// `menu comms-preview -Set decay=` の絵でしか判定できない。
    /// </summary>
    public sealed class CommsGlitchLogicTests
    {
        private const string Body = "異常が検出されました。\n記録してください。";

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
                Assert.AreEqual(1f, CommsGlitchLogic.PanelFlickerAt(t, 0f), 0f, "明滅もしない");
                Assert.AreSame(Body, CommsGlitchLogic.Corrupt(Body, 0f, (int)(t * 5f)),
                               "文面は同じ実体のまま返る（割り当てすら作らない）");
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
        /// 指数 2 なので 0.11 — 映像の劣化と足並みが揃う。
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
        /// ⚠⚠ <b>文字数を変えない。</b> 変えると打鍵の数え（`NoticeChars`）と食い違い、
        /// 枠の高さと重心も測り直しになる。<b>置換のみ・挿入も削除もしない。</b>
        /// </summary>
        [Test]
        public void Corrupt_KeepsLength_AndNewlines()
        {
            for (int tick = 0; tick < 400; tick++)
            {
                string s = CommsGlitchLogic.Corrupt(Body, 1f, tick);
                Assert.AreEqual(Body.Length, s.Length, $"tick={tick} で長さが変わった");
                for (int i = 0; i < Body.Length; i++)
                    if (Body[i] == '\n')
                        Assert.AreEqual('\n', s[i], $"tick={tick} で改行が壊れた（行が繋がる）");
            }
        }

        /// <summary>
        /// ⚠⚠ <b>化け先は意味を持たない記号だけ。</b> 別の言葉になると
        /// <b>装置が嘘をついた</b>ことになり、3 周目の反転が乗っている
        /// 「装置は正直に映している」が壊れる。
        /// </summary>
        [Test]
        public void Corrupt_OnlyProducesMeaninglessMarks()
        {
            for (int tick = 0; tick < 400; tick++)
            {
                string s = CommsGlitchLogic.Corrupt(Body, 1f, tick);
                for (int i = 0; i < s.Length; i++)
                {
                    if (s[i] == Body[i]) continue;
                    bool ok = s[i] == CommsGlitchLogic.Blank
                              || CommsGlitchLogic.Marks.IndexOf(s[i]) >= 0;
                    Assert.IsTrue(ok, $"tick={tick} i={i} で '{s[i]}' へ化けた（記号でも空白でもない）");
                }
            }
        }

        /// <summary>
        /// ⚠⚠ <b>読めなくならない。</b> ③「異常が検出されました。記録してください。」は
        /// <b>4 周目 A の締め</b>で出て、<b>読まれないと締めのカットが進まない</b>。
        /// </summary>
        [Test]
        public void Corrupt_LeavesTextReadable()
        {
            int worst = 0;
            for (int tick = 0; tick < 600; tick++)
            {
                string s = CommsGlitchLogic.Corrupt(Body, 1f, tick);
                int n = 0;
                for (int i = 0; i < s.Length; i++) if (s[i] != Body[i]) n++;
                if (n > worst) worst = n;
            }
            // ⚠⚠ **上限は確率ではなく字数で守る**（`Corrupt` が budget で打ち切る）。
            //    字ごとに独立の確率だけで決めていた初版は、まれに 8/20 字 ＝ 4 割が化けた。
            //    余裕は端数の抽選（+1）のぶんだけ。
            int cap = (int)((Body.Length - 1) * CommsGlitchLogic.MaxCorruptShare) + 1;
            Assert.LessOrEqual(worst, cap,
                               $"最悪の刻みで {worst} 字が化けた（上限 {cap}）— 読めない");
            Assert.Greater(worst, 0, "600 刻みで 1 字も化けないのは薄すぎる");
        }

        /// <summary>進みが増えるほど化ける字も増える（1 周目は 0、3 周目でよく化ける）。</summary>
        [Test]
        public void Corrupt_GrowsWithProgress()
        {
            Assert.AreEqual(0, TotalCorrupted(0f), "進み 0 では 1 字も化けない");
            Assert.Less(TotalCorrupted(0.33f), TotalCorrupted(1f), "進むほど化ける");
        }

        private static int TotalCorrupted(float progress)
        {
            float lv = CommsGlitchLogic.LevelFor(progress);
            int n = 0;
            for (int tick = 0; tick < 300; tick++)
            {
                string s = CommsGlitchLogic.Corrupt(Body, lv, tick);
                for (int i = 0; i < s.Length; i++) if (s[i] != Body[i]) n++;
            }
            return n;
        }

        /// <summary>
        /// ⚠ <b>明滅は沈むだけ。</b> 明るくすると「光った」に見えて、赤入れの
        /// 「明るすぎる」へ戻る（`canon/LEDGER.md` 0069）。
        /// </summary>
        [Test]
        public void Flicker_OnlyDims_NeverBrightens()
        {
            float low = 1f;
            int dips = 0, total = 0;
            for (float t = 0f; t < 60f; t += 0.02f)
            {
                float f = CommsGlitchLogic.PanelFlickerAt(t, 1f);
                Assert.LessOrEqual(f, 1f, $"t={t:0.00} で 1 を超えた（明るくなっている）");
                Assert.Greater(f, 0.5f, $"t={t:0.00} で沈みすぎ（面が消える）");
                if (f < 0.999f) dips++;
                if (f < low) low = f;
                total++;
            }
            Assert.Greater(dips, 0, "60 秒で 1 度も沈まないのは薄すぎる");
            Assert.Less(dips / (float)total, 0.5f, "半分以上沈んでいたら『点滅する看板』になる");
            Assert.Less(low, 0.95f, "いちばん沈んだ瞬間でも気づけない深さ");
        }

        /// <summary>
        /// ⚠ <b>横飛びの上限を超えない。</b> 面の幅は 0.76m なので、これ以上飛ぶと
        /// 飛んでいるあいだ文字が追えない。
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

        /// <summary>発作は「たまに」でなければならない（ずっと飛んでいると読めない）。</summary>
        [Test]
        public void Burst_IsOccasional_NotConstant()
        {
            int burst = 0, total = 0;
            for (float t = 0f; t < 60f; t += CommsGlitchLogic.TickSec)
            {
                total++;
                if (CommsGlitchLogic.BurstAt(t, 1f)) burst++;
            }
            Assert.Greater(burst, 0, "60 秒で 1 度も発作が起きないのは薄すぎる");
            Assert.Less(burst / (float)total, 0.5f, "半分以上が発作なら『たまに』ではない");
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
                Assert.AreEqual(CommsGlitchLogic.OffsetXAt(t, 0.7f), CommsGlitchLogic.OffsetXAt(t, 0.7f));
                Assert.AreEqual(CommsGlitchLogic.PanelFlickerAt(t, 0.7f),
                                CommsGlitchLogic.PanelFlickerAt(t, 0.7f));
                Assert.AreEqual(CommsGlitchLogic.Corrupt(Body, 0.7f, (int)t),
                                CommsGlitchLogic.Corrupt(Body, 0.7f, (int)t));
            }
        }

        /// <summary>
        /// ⚠ <b>刻みの中では化けの組み合わせが動かない。</b> 毎フレーム変えると
        /// 字がざわついて「読めない砂」になる。
        /// </summary>
        [Test]
        public void Corrupt_HoldsWithinTick_AndChangesAcrossTicks()
        {
            Assert.AreEqual(CommsGlitchLogic.Corrupt(Body, 1f, 5),
                            CommsGlitchLogic.Corrupt(Body, 1f, 5), "同じ刻みなら同じ");
            bool anyDiff = false;
            for (int t = 0; t < 40; t++)
                if (CommsGlitchLogic.Corrupt(Body, 1f, t) != CommsGlitchLogic.Corrupt(Body, 1f, t + 1))
                    anyDiff = true;
            Assert.IsTrue(anyDiff, "刻みが変わっても 1 度も組み合わせが変わらない");
        }

        /// <summary>刻みは時刻から決まる（時刻の写し先が 1 つであることの確認）。</summary>
        [Test]
        public void TickAt_AdvancesWithTime()
        {
            Assert.AreEqual(0, CommsGlitchLogic.TickAt(0f));
            Assert.AreEqual(0, CommsGlitchLogic.TickAt(-5f), "負の時刻でも落ちない");
            Assert.AreEqual(1, CommsGlitchLogic.TickAt(CommsGlitchLogic.TickSec * 1.5f));
            Assert.Less(CommsGlitchLogic.TickAt(1f), CommsGlitchLogic.TickAt(2f));
        }
    }
}
