#nullable enable
using System;
using System.IO;
using UnityEngine;

namespace FixedCamVr.Streaming.Recording
{
    /// <summary>
    /// オーバーレイに「フレーム列」を供給するソース（動画 / 静止画に並ぶ 3 種目）。
    /// <see cref="ScreenOverlayController"/> が毎フレーム <see cref="Tick"/> を呼ぶ。
    /// </summary>
    public interface IFrameSequence : IDisposable
    {
        /// <summary>現在のフレームを保持するテクスチャ。</summary>
        Texture Texture { get; }

        /// <summary>映像のアスペクト比（幅 / 高さ）。初回フレーム反映後に確定する。</summary>
        float Aspect { get; }

        /// <summary>総尺 (秒)。</summary>
        float DurationSec { get; }

        /// <summary>経過秒に対応するフレームを反映する。終端に達したら false（＝オーバーレイを畳む）。</summary>
        bool Tick(float elapsedSec);
    }

    /// <summary>
    /// 端末内録画 <c>.mjr</c> を pts どおりに再生する。
    ///
    /// **JPEG のまま持っているので、デコード経路はライブ映像とまったく同じ**
    /// （<c>Texture2D.LoadImage</c>）＝再エンコード劣化がなく、絵が完全に一致する。
    /// ファイルは開いたまま seek + 部分読みするので、20 秒ぶんを丸ごとメモリに載せない。
    /// </summary>
    public sealed class RecordedFramePlayer : IFrameSequence
    {
        private readonly FileStream _fs;
        private readonly RecordedSegmentFormat.FrameRef[] _index;
        private readonly Texture2D _tex;
        private byte[] _buf;
        private int _cursor = -1;
        private bool _disposed;

        public Texture Texture => _tex;
        public float Aspect { get; private set; } = 16f / 9f;
        public float DurationSec { get; }

        /// <summary>フレーム数（0 なら再生するものが無い）。</summary>
        public int FrameCount => _index.Length;

        private RecordedFramePlayer(FileStream fs, RecordedSegmentFormat.FrameRef[] index)
        {
            _fs = fs;
            _index = index;
            DurationSec = RecordedSegmentFormat.DurationSec(index);
            _buf = new byte[128 * 1024];
            _tex = new Texture2D(2, 2, TextureFormat.RGB24, false)
            {
                name = "RecordedFrame",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
        }

        /// <summary>
        /// 録画ファイルを開く。存在しない / 壊れている / フレーム 0 なら null
        /// （呼び出し側はそのカットを飛ばす）。
        /// </summary>
        public static RecordedFramePlayer? Open(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
                RecordedSegmentFormat.FrameRef[] index = RecordedSegmentFormat.BuildIndex(fs);
                if (index.Length == 0) { fs.Dispose(); return null; }
                return new RecordedFramePlayer(fs, index);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RecordedFrame] 録画を開けない: {path} ({e.Message})");
                return null;
            }
        }

        public bool Tick(float elapsedSec)
        {
            if (_disposed || _index.Length == 0) return false;
            int elapsedMs = Mathf.Max(0, Mathf.RoundToInt(elapsedSec * 1000f));
            int i = RecordedSegmentFormat.SeekIndex(_index, elapsedMs, _cursor);
            if (i < 0) i = 0;                       // 先頭より前 → 1 枚目を出す
            if (i != _cursor) { _cursor = i; Present(i); }
            // 終端: 最後のフレームの pts を過ぎたら畳む。
            return elapsedMs <= _index[_index.Length - 1].ptsMs;
        }

        private void Present(int i)
        {
            RecordedSegmentFormat.FrameRef f = _index[i];
            try
            {
                if (_buf.Length < f.length) _buf = new byte[f.length];
                _fs.Position = f.offset;
                int got = 0;
                while (got < f.length)
                {
                    int n = _fs.Read(_buf, got, f.length - got);
                    if (n <= 0) break;
                    got += n;
                }
                if (got != f.length) return;                 // 途中で切れている → 前のフレームを保つ
                if (!_tex.LoadImage(_buf, markNonReadable: false)) return;  // 壊れ JPEG は無視
                if (_tex.height > 0) Aspect = (float)_tex.width / _tex.height;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RecordedFrame] フレーム読み出し失敗: {e.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _fs.Dispose(); } catch { }
            if (_tex != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(_tex);
                else UnityEngine.Object.DestroyImmediate(_tex);
            }
        }
    }
}
