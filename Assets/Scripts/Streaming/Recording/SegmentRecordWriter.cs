#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace FixedCamVr.Streaming.Recording
{
    /// <summary>
    /// 区間 1 つぶんの <c>.mjr</c> を書き出す。**メインスレッドは JPEG のコピーとキュー投入だけ**を行い、
    /// 実際のファイル書き込みはバックグラウンドスレッドが担う（15fps × 60KB ≒ 0.9MB/s。
    /// メインスレッドで write すると 90Hz の描画が落ちる）。
    ///
    /// **録画は体験を止めない**（不変条件 1）: 容量上限・書き込み失敗・キュー溢れはすべて
    /// 「そのフレームを捨てて続行」で処理し、例外を呼び出し側へ投げない。
    ///
    /// バッファはプールして使い回す（毎フレーム new すると 1MB/s の GC ゴミになる）。
    /// </summary>
    public sealed class SegmentRecordWriter : IDisposable
    {
        /// <summary>録画の上限。0 以下は「無制限」ではなく既定値へ倒す（暴走させない）。</summary>
        public readonly struct Limits
        {
            public readonly long maxBytes;
            public readonly float minFrameIntervalSec;   // fpsCap の逆数。0 なら間引きなし

            public Limits(long maxBytes, float fpsCap)
            {
                this.maxBytes = maxBytes > 0 ? maxBytes : 64L * 1024 * 1024;
                minFrameIntervalSec = fpsCap > 0f ? 1f / fpsCap : 0f;
            }
        }

        private readonly struct Item
        {
            public readonly byte[] buf;
            public readonly int length;
            public readonly int ptsMs;
            public Item(byte[] buf, int length, int ptsMs) { this.buf = buf; this.length = length; this.ptsMs = ptsMs; }
        }

        // キューの上限。溢れたら**新しいフレームを捨てる**（古いものを捨てると時系列が飛ぶ）。
        private const int MaxQueued = 32;

        private readonly BlockingCollection<Item> _queue = new(new ConcurrentQueue<Item>(), MaxQueued);
        private readonly ConcurrentBag<byte[]> _pool = new();
        private readonly Limits _limits;
        private readonly Thread _thread;
        private readonly string _path;

        private long _written;
        private int _writtenFrames;
        private volatile bool _stopped;
        private volatile bool _capped;
        private int _lastPtsMs = -1;

        /// <summary>書き込み中に容量上限へ達したか（HUD / ログ用）。</summary>
        public bool Capped => _capped;

        /// <summary>
        /// 実際にファイルへ書けたバイト数。**<see cref="Dispose"/> の後に読むこと**
        /// （書き込みは背景スレッドなので、それ以前は途中経過）。ラン全体の容量配分に使う。
        /// </summary>
        public long WrittenBytes => Interlocked.Read(ref _written);

        /// <summary>
        /// 実際にファイルへ書けたフレーム数。**バイト数だけでは足りない** — ヘッダだけの空ファイルでも
        /// バイト数は 0 にならないので、「録れたか」を判定できるのはこちら。
        /// <see cref="Dispose"/> の後に読むこと。
        /// </summary>
        public int WrittenFrames => Interlocked.CompareExchange(ref _writtenFrames, 0, 0);

        /// <summary>このセグメントのファイルパス。</summary>
        public string Path => _path;

        public SegmentRecordWriter(string path, Limits limits)
        {
            _path = path;
            _limits = limits;
            _thread = new Thread(WriteLoop) { IsBackground = true, Name = "SegmentRecordWriter" };
            _thread.Start();
        }

        /// <summary>
        /// フレームを 1 枚積む。<paramref name="ptsMs"/> は区間先頭からの経過 (ms)。
        /// fpsCap による間引き・容量上限・キュー溢れで捨てたときは false。
        /// </summary>
        public bool TryAppend(byte[] jpeg, int length, int ptsMs)
        {
            if (_stopped || _capped || length <= 0) return false;
            if (_limits.minFrameIntervalSec > 0f && _lastPtsMs >= 0
                && (ptsMs - _lastPtsMs) < _limits.minFrameIntervalSec * 1000f - 1f)
                return false;

            if (!_pool.TryTake(out byte[]? buf) || buf.Length < length) buf = new byte[Math.Max(length, 96 * 1024)];
            Buffer.BlockCopy(jpeg, 0, buf, 0, length);
            if (!_queue.TryAdd(new Item(buf, length, ptsMs)))
            {
                _pool.Add(buf);   // 書き込みが追いつかない → このフレームは捨てる（体験は止めない）
                return false;
            }
            _lastPtsMs = ptsMs;
            return true;
        }

        private void WriteLoop()
        {
            FileStream? fs = null;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                fs = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024);
                RecordedSegmentFormat.WriteHeader(fs, 0, 0);
                Interlocked.Exchange(ref _written, RecordedSegmentFormat.HeaderBytes);

                foreach (Item item in _queue.GetConsumingEnumerable())
                {
                    if (Interlocked.Read(ref _written) + RecordedSegmentFormat.FrameHeaderBytes + item.length > _limits.maxBytes)
                    {
                        _capped = true;   // 以降の TryAppend は即 false（体験は止めない）
                        _pool.Add(item.buf);
                        continue;
                    }
                    RecordedSegmentFormat.WriteFrame(fs, item.buf, item.length, item.ptsMs);
                    Interlocked.Add(ref _written, RecordedSegmentFormat.FrameHeaderBytes + item.length);
                    Interlocked.Increment(ref _writtenFrames);
                    _pool.Add(item.buf);
                }
                fs.Flush();
            }
            catch (Exception)
            {
                // ディスク満杯・権限・端末側の都合。録画は諦めるが体験は続ける（不変条件 1）。
                _capped = true;
            }
            finally
            {
                try { fs?.Dispose(); } catch { }
            }
        }

        public void Dispose()
        {
            if (_stopped) return;
            _stopped = true;
            try { _queue.CompleteAdding(); } catch { }
            // 書き切るのを待つ。上限があるので有界（万一固まっても体験を止めないよう待ちは打ち切る）。
            try { _thread.Join(2000); } catch { }
            try { _queue.Dispose(); } catch { }
        }
    }
}
