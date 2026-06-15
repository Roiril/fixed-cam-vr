#nullable enable
using System;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// fixed-cam-streamer の <c>GET /info</c> レスポンスを表す DTO。
    /// JsonUtility でデシリアライズするため、フィールド名はサーバ側 JSON キーと一致させる。
    /// </summary>
    [Serializable]
    public sealed class StreamMetadata
    {
        public string deviceName = "";
        public string lensId = "";
        public double lensFovDeg;
        public int widthPx;
        public int heightPx;
        public int rotationDeg;
        public bool isPortrait;

        /// <summary>
        /// rotationDeg を加味した「表示時の論理サイズ」を返す。
        /// 例: 1080x1920 / 90deg → (1920, 1080) になる（向きを正立させた後の見え方）。
        /// </summary>
        public Vector2Int EffectiveSize()
        {
            bool swap = rotationDeg == 90 || rotationDeg == 270;
            return swap ? new Vector2Int(heightPx, widthPx) : new Vector2Int(widthPx, heightPx);
        }

        /// <summary>表示時の論理アスペクト (W/H)。</summary>
        public float EffectiveAspect()
        {
            var e = EffectiveSize();
            return e.y == 0 ? 1f : (float)e.x / e.y;
        }
    }

    /// <summary>fixed-cam-streamer の <c>GET /health</c> レスポンス。</summary>
    [Serializable]
    public sealed class StreamHealth
    {
        public long uptimeMs;
        public long totalFrames;
        public long totalBytes;
        public float fps;

        /// <summary>配信済みフレーム数。<see cref="totalFrames"/> との差が広がる時は HTTP ワーカ詰まり。</summary>
        public long sentFrames;

        /// <summary>最新フレームの経過時間 (ms)。大きい時はカメラ stall（配信側で映像が更新されていない）。</summary>
        public long latestFrameAgeMs;
    }
}
