#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace FixedCamVr.Streaming.Recording
{
    /// <summary>
    /// 区間 1 つぶんの <c>.mjr</c> を書き出す。**残すのは切り替えの前後だけ**
    /// （切り替えの <c>tailSec</c> 秒前 〜 切り替えの <c>postSec</c> 秒後）。
    ///
    /// <b>なぜ末尾か</b>（2026-08-06 に頭から録る方式を置き換えた）:
    /// 3 周目に流す録画は「その区間へ入った瞬間」に始まる。区間の頭から録ると、映像の中の過去の自分も
    /// 入口に居るので、**体験者の現在位置に立つ CG 人形と重なる**。末尾＝区間を出る直前を残せば、
    /// 過去の自分は出口側に居て、現在の自分（＝人形）と画面内で位置が分かれる。
    /// 副次的に、容量が滞在時間に比例しなくなる（ゆっくり歩く体験者でも一定）ので、
    /// ラン全体の上限に当たって**後半の区間が録れなくなる**事故も消える。
    ///
    /// <b>なぜ切り替えの後まで録るか</b>（2026-08-14 追加・<see cref="BeginPostRoll"/>）:
    /// 切り替えの瞬間で切ると、**過去の自分が曲がり切る前に映像が終わる**（角を曲がる動きは
    /// カメラが切り替わってからも 1〜2 秒続く）。切り替え後も同じカメラを録り続ければ、
    /// 過去の自分は画面の外まで歩いて出ていく。
    ///
    /// 積むのはメインスレッド（<see cref="CameraStream.FrameTap"/>）でリングへのコピーだけ。
    /// ファイル書き込みは <see cref="Dispose"/>（＝区間の切れ目）で背景スレッドが一度に行う。
    ///
    /// **録画は体験を止めない**（不変条件 1）: 容量上限・書き込み失敗はすべて「捨てて続行」で処理し、
    /// 例外を呼び出し側へ投げない。バッファはリング内で使い回す（毎フレーム new すると 1MB/s の GC ゴミ）。
    /// </summary>
    public sealed class SegmentRecordWriter : IDisposable
    {
        /// <summary>末尾の既定尺 (秒)。show.json <c>record.tailSec</c> が未指定 / 0 以下のとき。</summary>
        public const float DefaultTailSec = 3f;

        /// <summary>
        /// 切り替え後に録り続ける既定尺 (秒)。show.json <c>record.postSec</c> が未指定 / 0 以下のとき。
        ///
        /// ⚠ 0 を「追い録りなし」にしない。JsonUtility はキーの無い show.json でも 0 を書くので、
        /// 既存の焼き込み・端末キャッシュでは**必ず 0 が入る**。0 を無効と読むと、
        /// この機能は設定を書き直した現場でしか効かない。
        /// </summary>
        public const float DefaultPostSec = 2f;

        /// <summary>
        /// 書き出しの待ち上限 (ms)。超えたら諦めて体験へ戻る（背景スレッドは書き続ける）。
        /// **これはメインスレッドの停止時間の上限**なので短い（VR で数百 ms を超えると酔いに出る）。
        /// 実測は数十 ms なので、通常は 1 度も当たらない。
        /// </summary>
        private const int FlushTimeoutMs = 300;

        /// <summary>
        /// 録り始めの合図（<c>record.startLineId</c>）が効いた区間で、**起点から先に残す上限** (ms)。
        /// 起点だけで切ると、長く留まった体験者ぶんが RAM に積み続ける（落とすのが容量上限だけになる）。
        /// 実測滞在は 7 秒前後なので、30 秒は著作の意図（線 → 区間の終わり）を切らない余裕がある。
        /// </summary>
        private const int StartWindowMs = 30_000;

        /// <summary>録画の上限。0 以下は「無制限」ではなく既定値へ倒す（暴走させない）。</summary>
        public readonly struct Limits
        {
            public readonly long maxBytes;
            public readonly float minFrameIntervalSec;   // fpsCap の逆数。0 なら間引きなし
            public readonly float tailSec;               // 残す末尾の長さ

            public Limits(long maxBytes, float fpsCap, float tailSec)
            {
                this.maxBytes = maxBytes > 0 ? maxBytes : 64L * 1024 * 1024;
                minFrameIntervalSec = fpsCap > 0f ? 1f / fpsCap : 0f;
                this.tailSec = tailSec > 0f ? tailSec : DefaultTailSec;
            }
        }

        private readonly struct Item
        {
            public readonly byte[] buf;
            public readonly int length;
            public readonly int ptsMs;
            public Item(byte[] buf, int length, int ptsMs) { this.buf = buf; this.length = length; this.ptsMs = ptsMs; }
        }

        private readonly Queue<Item> _ring = new(64);
        private readonly ConcurrentBag<byte[]> _pool = new();
        private readonly Limits _limits;
        private readonly string _path;
        private readonly int _tailMs;

        private long _ringBytes = RecordedSegmentFormat.HeaderBytes;
        private long _written;
        private int _writtenFrames;
        private bool _stopped;
        private volatile bool _flushDone;   // 背景スレッドが書き終えたか（実績が確定したか）
        private int _queuedFrames;          // 書き出しへ渡した枚数（Dispose で確定）
        private volatile bool _capped;
        // 書き出しの失敗を報告したか（`_capped` と混ぜない — あちらは「尺が縮んだ」の意味）。
        private volatile bool _writeFailed;
        private int _lastPtsMs = -1;

        // 切り替えの瞬間の pts（-1 = まだ切り替わっていない）。ここから先は**古い側を落とさない**
        // ＝ 残る窓が [切替 - tailSec, 切替 + postSec] に固定される。
        private int _anchorPtsMs = -1;
        private int _postEndPtsMs = int.MaxValue;

        // 録り始めの pts（-1 = 指定なし ＝ 末尾方式）。立つと **tailSec を見ずにここから全部残す**。
        private int _startPtsMs = -1;

        /// <summary>
        /// 末尾を丸ごと残せなかったか（容量で古い側を落とした / 書き込みに失敗した）。HUD・ログ用。
        /// </summary>
        public bool Capped => _capped;

        /// <summary>
        /// 実際にファイルへ書けたバイト数。**<see cref="Dispose"/> の後に読むこと**
        /// （書き込みは背景スレッドなので、それ以前は 0）。ラン全体の容量配分に使う。
        /// </summary>
        public long WrittenBytes => Interlocked.Read(ref _written);

        /// <summary>
        /// 実際にファイルへ書けたフレーム数。**バイト数だけでは足りない** — ヘッダだけの空ファイルでも
        /// バイト数は 0 にならないので、「録れたか」を判定できるのはこちら。
        /// <see cref="Dispose"/> の後に読むこと。
        /// </summary>
        public int WrittenFrames => Interlocked.CompareExchange(ref _writtenFrames, 0, 0);

        /// <summary>
        /// 書き出しが終わっているか。<b>false のあいだ <see cref="WrittenBytes"/> /
        /// <see cref="WrittenFrames"/> は未確定（0 のことがある）。</b>
        /// <see cref="Dispose"/> の待ちを打ち切ったときに false のまま返る。
        /// </summary>
        public bool FlushCompleted => _flushDone;

        /// <summary>
        /// 書き出しへ渡した枚数（<see cref="Dispose"/> で確定・以後不変）。
        /// <see cref="FlushCompleted"/> が false のときに「録れているはずの枚数」を言える唯一の値。
        /// </summary>
        public int QueuedFrames => _queuedFrames;

        /// <summary>いまリングに載っている枚数（診断用）。</summary>
        public int BufferedFrames => _ring.Count;

        /// <summary>
        /// いまリングに載っているバイト数（＝閉じたときに書くであろう量）。
        /// ラン全体の容量配分で「まだ閉じていない区間のぶん」を差し引くのに使う。
        /// </summary>
        public long BufferedBytes => _ringBytes;

        /// <summary>切り替えが済んで追い録り中か。</summary>
        public bool PostRolling => _anchorPtsMs >= 0;

        /// <summary>
        /// 切り替えの瞬間を記録し、**追い録り**へ入る。以後 <see cref="Trim"/> の基準は
        /// 現在時刻ではなく <paramref name="atPtsMs"/> になるので、
        /// 残る窓は <c>[atPtsMs - tailSec, atPtsMs + postSec]</c> に固定される
        /// （基準を凍らせないと、追い録りしたぶんだけ切り替え前が押し出されて短くなる）。
        ///
        /// <paramref name="postSec"/> は保険の上限で、実際に閉じるのは呼び出し側
        /// （<see cref="SegmentRecorder"/>）。2 度目の呼び出しは無視する。
        /// </summary>
        public void BeginPostRoll(int atPtsMs, float postSec)
        {
            if (_stopped || _anchorPtsMs >= 0) return;
            _anchorPtsMs = Math.Max(0, atPtsMs);
            int postMs = (int)MathF.Round(Math.Max(0f, postSec) * 1000f);
            _postEndPtsMs = _anchorPtsMs > int.MaxValue - postMs ? int.MaxValue : _anchorPtsMs + postMs;
        }

        /// <summary>録り始めの合図（線の横断）が来たか。診断・観測用。</summary>
        public bool HasStartMark => _startPtsMs >= 0;

        /// <summary>
        /// <b>ここから録る</b>（<c>record.startLineId</c> の線を横切った瞬間）。
        /// 以後 <see cref="Trim"/> は <see cref="Limits.tailSec"/> を見ず、
        /// <paramref name="atPtsMs"/> より前だけを落とす ＝ <b>横断から区間の終わりまで全部残る</b>。
        ///
        /// ⚠⚠ <b>横切ったその瞬間に打つこと。</b> 残せるのは<b>いまリングに載っているぶんだけ</b>で、
        /// それより前は末尾方式の <see cref="Trim"/> が既に捨てている。後から遡って打っても戻らない
        /// （テストで実際に踏んだ — 20 秒積んでから 5 秒の所を指しても、残るのは末尾 3 秒 ＋ その後）。
        /// ⚠ <b>2 度目の呼び出しは無視する。</b> 1 区間で何度も横切ることはある（行ったり来たり）が、
        /// 起点が動くと「どこから録れているか」が走行ごとに変わって著作できない。
        /// ⚠ <b>容量の上限（<see cref="Limits.maxBytes"/>）は依然として効く。</b>
        /// 長すぎる区間では古い側から落ちて <see cref="Capped"/> が立つ（＝起点が後ろへずれる）。
        /// </summary>
        public void MarkStart(int atPtsMs)
        {
            if (_stopped || _startPtsMs >= 0) return;
            _startPtsMs = Math.Max(0, atPtsMs);
            Trim(_lastPtsMs >= 0 ? _lastPtsMs : _startPtsMs);
        }

        /// <summary>このセグメントのファイルパス。</summary>
        public string Path => _path;

        public SegmentRecordWriter(string path, Limits limits)
        {
            _path = path;
            _limits = limits;
            _tailMs = (int)MathF.Round(limits.tailSec * 1000f);
        }

        /// <summary>
        /// フレームを 1 枚積む。<paramref name="ptsMs"/> は区間先頭からの経過 (ms)。
        /// fpsCap による間引きで捨てたときは false（リングに載れば true）。
        /// 載せた結果あふれた古いフレームは黙って落ちる（それがこのクラスの仕事）。
        /// </summary>
        public bool TryAppend(byte[] jpeg, int length, int ptsMs)
        {
            if (_stopped || length <= 0 || length > RecordedSegmentFormat.MaxFrameBytes) return false;
            // 追い録りの上限を過ぎたら受けない（呼び出し側が閉じ忘れても窓は伸びない）。
            if (_anchorPtsMs >= 0 && ptsMs > _postEndPtsMs) return false;
            if (_limits.minFrameIntervalSec > 0f && _lastPtsMs >= 0
                && (ptsMs - _lastPtsMs) < _limits.minFrameIntervalSec * 1000f - 1f)
                return false;

            if (!_pool.TryTake(out byte[]? buf) || buf.Length < length) buf = new byte[Math.Max(length, 96 * 1024)];
            Buffer.BlockCopy(jpeg, 0, buf, 0, length);
            _ring.Enqueue(new Item(buf, length, ptsMs));
            _ringBytes += RecordedSegmentFormat.FrameHeaderBytes + length;
            _lastPtsMs = ptsMs;

            Trim(ptsMs);
            return true;
        }

        // 末尾 tailSec 秒ぶんへ切り詰める。時間と容量の 2 条件で古い側から落とす。
        // **最新の 1 枚は必ず残す**（1 枚しか無い状態で容量が足りなくても空ファイルにはしない）。
        //
        // 基準は「いま」だが、切り替えが済んだら**切り替えの瞬間**に凍る（BeginPostRoll）。
        // 凍らせないと、追い録りした秒数だけ切り替え前が押し出されて消える。
        private void Trim(int nowPtsMs)
        {
            int anchor = _anchorPtsMs >= 0 ? _anchorPtsMs : nowPtsMs;
            while (_ring.Count > 1)
            {
                Item head = _ring.Peek();
                // 録り始めの合図が来ていれば、**そこより前だけ**を落とす（末尾の窓は見ない）。
                // 来ていなければ従来どおり「末尾 tailSec 秒」。
                // ⚠ 起点の合図が来ていれば「そこより前」を落とす。**ただし時間の上限は残す**
                //    （2026-08-30）。起点だけで切ると、その区間に長く留まった体験者ぶんが
                //    丸ごと RAM に積み続ける — 落とす条件が容量上限だけになり、
                //    **ラン全体の残量（maxTotalMB・既定 200MB）いっぱいまで managed byte[] で載る**。
                //    実測滞在は 7 秒前後なので 30 秒あれば著作の意図（線から区間の終わりまで）は満たす。
                bool tooOld = _startPtsMs >= 0
                    ? (head.ptsMs < _startPtsMs || anchor - head.ptsMs > StartWindowMs)
                    : anchor - head.ptsMs > _tailMs;
                bool tooBig = _ringBytes > _limits.maxBytes;
                if (!tooOld && !tooBig) break;
                if (tooBig) _capped = true;   // 末尾を丸ごと残せていない（尺が縮む）
                _ring.Dequeue();
                _ringBytes -= RecordedSegmentFormat.FrameHeaderBytes + head.length;
                _pool.Add(head.buf);
            }
        }

        /// <summary>
        /// 区間の終わり。リングの中身を <c>.mjr</c> へ書き出す（背景スレッド + 有界待ち）。
        /// 1 枚も無ければファイルを作らない（＝再生側は「録れていない」として飛ばす）。
        /// </summary>
        public void Dispose()
        {
            if (_stopped) return;
            _stopped = true;

            Item[] items = _ring.ToArray();
            _ring.Clear();
            _ringBytes = RecordedSegmentFormat.HeaderBytes;
            if (items.Length == 0) return;

            _queuedFrames = items.Length;
            var thread = new Thread(() => Flush(items)) { IsBackground = true, Name = "SegmentRecordWriter" };
            thread.Start();
            // 2.7MB 程度なので実測は数十 ms。万一詰まっても体験を止めない（待ちは打ち切る）。
            //
            // ⚠⚠ **ここはメインスレッドで、しかも体験者が区間を跨いだその瞬間に走る**
            //    （`SegmentRecorder.Close` ← `OnCameraEntered` / `Update`）。VR で数百 ms を超える
            //    フリーズは実害（酔い）なので、待ちは短く切って背景へ逃がす（2026-08-30 に 2000ms から）。
            //    打ち切ったときは実績（WrittenBytes / WrittenFrames）がまだ 0 なので、
            //    **呼び手は `FlushCompleted` を見てから数値を読むこと** — 見ないと
            //    「録れているのに 1 枚も録れていない」と警告し、`SegmentClosed` に frames=0 を配って
            //    解析器（analyze-xp-log.py）が偽の FAIL を出す。
            try { thread.Join(FlushTimeoutMs); } catch { }
        }

        // pts を **先頭 0 起点へ振り直して**書く。振り直さないと RecordedFramePlayer が
        // 頭の数秒を空回りする（再生開始から実際に絵が出るまで無音の間ができる）。
        private void Flush(Item[] items)
        {
            FileStream? fs = null;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                fs = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024);
                RecordedSegmentFormat.WriteHeader(fs, 0, 0);
                long written = RecordedSegmentFormat.HeaderBytes;
                int frames = 0;
                int basePts = items[0].ptsMs;

                foreach (Item item in items)
                {
                    RecordedSegmentFormat.WriteFrame(fs, item.buf, item.length, Math.Max(0, item.ptsMs - basePts));
                    written += RecordedSegmentFormat.FrameHeaderBytes + item.length;
                    frames++;
                }
                fs.Flush();
                Interlocked.Exchange(ref _written, written);
                Interlocked.Exchange(ref _writtenFrames, frames);
            }
            catch (Exception e)
            {
                // ディスク満杯・権限・端末側の都合。録画は諦めるが体験は続ける（不変条件 1）。
                // ⚠⚠ **黙って諦めない**（2026-09-04）。ここを握りつぶすと、`_capped` は
                //    「容量で尺が縮んだ」と同じ顔になる。3 周目に映像が出ない当日、原因が
                //    ディスクなのか台本なのか切り分けられない。1 度だけ出す（背景スレッドから
                //    毎フレーム吠えると logcat のリングバッファを食う）。
                if (!_writeFailed)
                {
                    _writeFailed = true;
                    // ⚠ このファイルは `using UnityEngine;` を持たない（`System.IO.Path` と
                    //   自前の `Path` プロパティの衝突を避けている）。完全修飾で呼ぶ。
                    UnityEngine.Debug.LogError($"[SegmentRecordWriter] 書き出しに失敗（この区間は諦める） {_path}: {e}");
                }
                _capped = true;
            }
            finally
            {
                try { fs?.Dispose(); } catch { }
                _flushDone = true;   // 失敗経路でも「もう待っても増えない」を伝える
            }
        }
    }
}
