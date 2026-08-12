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

        /// <summary>
        /// 実際にテクスチャへ載せたフレーム数。**ファイルを開けたことと画に出たことは別**なので、
        /// 「録画が再生された」を証明できるのはこちら（暗所で目視できない現場ではこれが唯一の証拠）。
        /// </summary>
        public int PresentedCount { get; private set; }

        /// <summary>読み出しに失敗したフレーム数（壊れ JPEG・途中で切れたファイル）。</summary>
        public int FailedCount { get; private set; }

        /// <summary>直近に載せたフレームの平均輝度 0..1（暗所で「黒しか出ていない」を見分ける）。</summary>
        public float LastLuma { get; private set; }

        private RecordedFramePlayer(FileStream fs, RecordedSegmentFormat.FrameRef[] index)
        {
            _fs = fs;
            _index = index;
            DurationSec = RecordedSegmentFormat.DurationSec(index);
            _buf = new byte[128 * 1024];
            // ⚠ mipChain は**ライブと同じだけ必要**。3 周目に流れるのはこの録画で、ライブだけ痩せて
            //   録画が鮮明だと、切り替わった瞬間に画の素性が変わって差し替えがばれる。
            _tex = new Texture2D(2, 2, TextureFormat.RGB24, mipChain: true)
            {
                name = "RecordedFrame",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
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
                if (got != f.length) { FailedCount++; return; }             // 途中で切れている → 前のフレームを保つ
                if (!_tex.LoadImage(_buf, markNonReadable: false)) { FailedCount++; return; }  // 壊れ JPEG は無視
                if (_tex.height > 0) Aspect = (float)_tex.width / _tex.height;
                PresentedCount++;
                // 最初の 1 枚だけ輝度を測る（毎フレーム GetPixels32 すると重い）。暗所での
                // 「開けたが真っ黒しか出ていない」を、目視ではなくログで見分けるため。
                if (PresentedCount == 1) LastLuma = SampleLuma();
            }
            catch (Exception e)
            {
                FailedCount++;
                Debug.LogWarning($"[RecordedFrame] フレーム読み出し失敗: {e.Message}");
            }
        }

        // 16x16 に縮めた画素の平均輝度。RGB24 の Texture2D は CPU から読めるので
        // GetPixels の矩形読みで済む（RenderTexture の読み戻しは要らない）。
        private float SampleLuma()
        {
            try
            {
                int w = _tex.width, h = _tex.height;
                if (w <= 0 || h <= 0) return 0f;
                int stepX = Mathf.Max(1, w / 16), stepY = Mathf.Max(1, h / 16);
                float sum = 0f;
                int n = 0;
                for (int y = 0; y < h; y += stepY)
                    for (int x = 0; x < w; x += stepX)
                    {
                        Color c = _tex.GetPixel(x, y);
                        sum += 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
                        n++;
                    }
                return n > 0 ? sum / n : 0f;
            }
            catch { return 0f; }
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
