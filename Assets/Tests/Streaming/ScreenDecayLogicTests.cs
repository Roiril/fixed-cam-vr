#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 周回で進む解像度の劣化（<see cref="ScreenDecayLogic"/>）。
    ///
    /// ここが固定するのは**演出の効き目そのもの**なので、値を変えるときは意図を持って変えること:
    ///   - 1 周目の入口では**何も起きない**（今と同じ画）
    ///   - 周の変わり目に**段差が出ない**（LEDGER 0012「急に落ちるではなくばれないように」）
    ///   - **最後の周へ入った瞬間に落ち切り、そこから先は動かない**
    ///     （LEDGER 0013「3周目のAで粗さがマックスになるようにして、そこからは変わらない」）
    ///   - **後戻りしない**（下がると「直った」に見える）
    ///   - 導入・終了では**進まないが保持する**（終了で戻すと画が急に鮮明になる）
    /// </summary>
    public sealed class ScreenDecayLogicTests
    {
        private const float Dt = 1f / 72f;

        /// <summary>本編を <paramref name="sec"/> 秒進める（周の経過も一緒に進む）。</summary>
        private static void Run(ScreenDecayLogic l, float sec, int lap, int totalLaps, float lapElapsedAtStart)
        {
            float t = 0f;
            while (t < sec)
            {
                t += Dt;
                l.Tick(Dt, running: true, lap, totalLaps, lapElapsedAtStart + t);
            }
        }

        [Test]
        public void 始まりは量子化しない()
        {
            var l = new ScreenDecayLogic();
            Assert.AreEqual(0f, l.Progress);
            Assert.AreEqual(0f, l.Blocks, "進み 0 は「今までと 1 ビットも変わらない画」でなければならない");
        }

        [Test]
        public void 進み0の対応表は映像の実寸とほぼ1対1()
        {
            // 枠 900 ブロック × 映像の領域 0.75 = 675 ＝ ソース 640px に対して 0.95 画素／ブロック。
            // ここが崩れると「1 周目の最初は今くらいの解像度」が守れない。
            Assert.AreEqual(900f, ScreenDecayLogic.BlocksFor(0f), 0.01f);
            Assert.AreEqual(110f, ScreenDecayLogic.BlocksFor(1f), 0.01f);
        }

        [Test]
        public void 対応表は等比なので周ごとの落ち幅の比が揃う()
        {
            float b0 = ScreenDecayLogic.BlocksFor(0f);
            float b1 = ScreenDecayLogic.BlocksFor(1f / 3f);
            float b2 = ScreenDecayLogic.BlocksFor(2f / 3f);
            float b3 = ScreenDecayLogic.BlocksFor(1f);
            // 等差だと最初の 1 周で見た目が全部落ちて、後の 2 周が動かない。
            Assert.AreEqual(b0 / b1, b1 / b2, 0.01f);
            Assert.AreEqual(b1 / b2, b2 / b3, 0.01f);
        }

        [Test]
        public void 目標は周の中でも連続に進む()
        {
            // 周の番号だけで決めると変わり目が段差になる。周の中の経過も混ぜる。
            float head = ScreenDecayLogic.TargetFor(lap: 2, totalLaps: 3, lapElapsedSec: 0f);
            float mid = ScreenDecayLogic.TargetFor(lap: 2, totalLaps: 3, lapElapsedSec: 15f);
            float tail = ScreenDecayLogic.TargetFor(lap: 2, totalLaps: 3, lapElapsedSec: 30f);
            Assert.AreEqual(0.5f, head, 0.001f);
            Assert.AreEqual(0.75f, mid, 0.001f);
            Assert.AreEqual(1f, tail, 0.001f);
            // 周の終わりの目標と、次の周の頭の目標が一致する＝境目に段差が無い。
            Assert.AreEqual(tail, ScreenDecayLogic.TargetFor(3, 3, 0f), 0.001f);
        }

        [Test]
        public void 落ち切るのは最後の周へ入った瞬間()
        {
            // canon/LEDGER.md 0013「3周目のAで粗さがマックスになるようにして、そこからは変わらない」。
            // 3 周目は録画が流れる周なので、その最中に画が動き続けてはいけない。
            Assert.Less(ScreenDecayLogic.TargetFor(lap: 2, totalLaps: 3, lapElapsedSec: 29f), 1f);
            Assert.AreEqual(1f, ScreenDecayLogic.TargetFor(lap: 3, totalLaps: 3, lapElapsedSec: 0f), 0.001f);
            Assert.AreEqual(1f, ScreenDecayLogic.TargetFor(lap: 3, totalLaps: 3, lapElapsedSec: 40f), 0.001f);
        }

        [Test]
        public void 最後の周へ入ったら画は動かない()
        {
            var l = new ScreenDecayLogic();
            Run(l, 30f, lap: 1, totalLaps: 3, lapElapsedAtStart: 0f);
            Run(l, 30f, lap: 2, totalLaps: 3, lapElapsedAtStart: 0f);
            Assert.AreEqual(1f, l.Progress, 0.001f, "3 周目の A へ入る時点で落ち切っている");

            float blocks = l.Blocks;
            Run(l, 40f, lap: 3, totalLaps: 3, lapElapsedAtStart: 0f);
            Run(l, 20f, lap: 4, totalLaps: 3, lapElapsedAtStart: 0f);
            Assert.AreEqual(blocks, l.Blocks, 1e-4f, "3 周目と帰りの A では 1 ブロックも動かない");
            Assert.AreEqual(ScreenDecayLogic.EndBlocks, l.Blocks, 0.01f);
        }

        [Test]
        public void 周が目安より短くても最後の周に入る時点で落ち切っている()
        {
            // 2026-08-12 の実機: 目安 30 秒に対し実際の 1 周は 24 秒で、周の中の進みが 1 に届かず、
            // **不足が最後の境目へ持ち越されて 3 周目に入ってから 2 秒ぶん落ち続けた**。
            // 前の周の実測を目安に採ると周の中で届き切るので、境目の不足が 0 になる。
            const float lapSec = 24f;
            var l = new ScreenDecayLogic();
            Run(l, lapSec, lap: 1, totalLaps: 3, lapElapsedAtStart: 0f);
            Run(l, lapSec, lap: 2, totalLaps: 3, lapElapsedAtStart: 0f);

            l.Tick(Dt, running: true, 3, 3, 0f);   // 3 周目の A へ入った最初のフレーム
            Assert.AreEqual(1f, l.Progress, 0.001f, "3 周目に入った時点で落ち切っていること");
            Assert.AreEqual(ScreenDecayLogic.EndBlocks, l.Blocks, 0.01f);
        }

        [Test]
        public void 目安は前の周の実測へ差し替わる()
        {
            var l = new ScreenDecayLogic();
            Assert.AreEqual(ScreenDecayLogic.LapRefSec, l.LapRef, 0.01f, "1 周目は企画書の目安から始める");

            Run(l, 24f, lap: 1, totalLaps: 3, lapElapsedAtStart: 0f);
            l.Tick(Dt, running: true, 2, 3, 0f);
            Assert.AreEqual(24f, l.LapRef, 0.2f);

            // 走り抜け・立ち止まりで暴れさせない（下限・上限で切る）。
            var fast = new ScreenDecayLogic();
            Run(fast, 3f, lap: 1, totalLaps: 3, lapElapsedAtStart: 0f);
            fast.Tick(Dt, running: true, 2, 3, 0f);
            Assert.AreEqual(ScreenDecayLogic.MinLapRefSec, fast.LapRef, 0.2f);
        }

        [Test]
        public void 周が1つしか無い設定でも落ち切る()
        {
            // totalLaps-1 が 0 になる割り算を踏まない（その 1 周の中で落とす）。
            Assert.AreEqual(0f, ScreenDecayLogic.TargetFor(lap: 1, totalLaps: 1, lapElapsedSec: 0f), 0.001f);
            Assert.AreEqual(1f, ScreenDecayLogic.TargetFor(lap: 1, totalLaps: 1, lapElapsedSec: 30f), 0.001f);
        }

        [Test]
        public void 帰りのAでは頭打ちになる()
        {
            // 周回は order[0] へ戻った時に上がるので lap = totalLaps + 1 は構造的に必ず踏む。
            Assert.AreEqual(1f, ScreenDecayLogic.TargetFor(lap: 4, totalLaps: 3, lapElapsedSec: 0f), 0.001f);
            Assert.AreEqual(1f, ScreenDecayLogic.TargetFor(lap: 9, totalLaps: 3, lapElapsedSec: 99f), 0.001f);
        }

        [Test]
        public void 導入では進まない()
        {
            var l = new ScreenDecayLogic();
            for (int i = 0; i < 3000; i++) l.Tick(Dt, running: false, 1, 3, 40f);
            Assert.AreEqual(0f, l.Progress, "導入で進むと「1 周目の最初は今くらいの解像度」が破れる");
        }

        [Test]
        public void 終了では進まないが値は保持する()
        {
            var l = new ScreenDecayLogic();
            Run(l, 30f, lap: 3, totalLaps: 3, lapElapsedAtStart: 0f);
            float held = l.Progress;
            Assert.Greater(held, 0.2f);

            // 終了（running=false）。走行中の演出を見せ切る猶予のあいだ、画が急に鮮明にならない。
            for (int i = 0; i < 600; i++) l.Tick(Dt, running: false, 4, 3, 0f);
            Assert.AreEqual(held, l.Progress, 1e-6f);
        }

        [Test]
        public void 後戻りしない()
        {
            var l = new ScreenDecayLogic();
            Run(l, 20f, lap: 2, totalLaps: 3, lapElapsedAtStart: 20f);
            float peak = l.Progress;
            // 周が戻る（ランのやり直し以外では起きないが、起きても画は「直らない」）。
            for (int i = 0; i < 600; i++) l.Tick(Dt, running: true, 1, 3, 0f);
            Assert.AreEqual(peak, l.Progress, 1e-6f);
        }

        [Test]
        public void 交代で頭から始め直す()
        {
            var l = new ScreenDecayLogic();
            Run(l, 30f, lap: 3, totalLaps: 3, lapElapsedAtStart: 0f);
            Assert.Greater(l.Progress, 0f);
            l.Reset();
            Assert.AreEqual(0f, l.Progress);
            Assert.AreEqual(0f, l.Blocks, "次の体験者は今の解像度から始める");
        }

        [Test]
        public void 周が飛んでも段差にならない()
        {
            // 1 周 15 秒で駆け抜けると、周の頭で目標が 0.167 飛ぶ。頭打ちが無いと 1 フレームで落ちる。
            var l = new ScreenDecayLogic();
            Run(l, 15f, lap: 1, totalLaps: 3, lapElapsedAtStart: 0f);

            float before = l.Progress;
            // 周が上がった直後の 1 フレーム。
            l.Tick(Dt, running: true, 2, 3, 0f);
            Assert.LessOrEqual(l.Progress - before, ScreenDecayLogic.MaxRisePerSec * Dt + 1e-6f);

            // 2 秒（実機のサマリ 1 標本ぶん）でも解析の段差判定 0.09 を超えない。
            float t0 = l.Progress;
            Run(l, 2f, lap: 2, totalLaps: 3, lapElapsedAtStart: 0f);
            Assert.Less(l.Progress - t0, 0.09f, "解析（analyze-xp-log.py）が段差と判定する幅");
        }

        [Test]
        public void 本編を通すと終端まで届く()
        {
            var l = new ScreenDecayLogic();
            Run(l, 30f, lap: 1, totalLaps: 3, lapElapsedAtStart: 0f);
            Run(l, 30f, lap: 2, totalLaps: 3, lapElapsedAtStart: 0f);
            Run(l, 30f, lap: 3, totalLaps: 3, lapElapsedAtStart: 0f);
            Run(l, 10f, lap: 4, totalLaps: 3, lapElapsedAtStart: 0f);
            Assert.AreEqual(1f, l.Progress, 0.02f, "3 周 + 帰りの A を歩き切ったら終端に届く");
            Assert.AreEqual(ScreenDecayLogic.EndBlocks, l.Blocks, 2f);
        }
    }
}
