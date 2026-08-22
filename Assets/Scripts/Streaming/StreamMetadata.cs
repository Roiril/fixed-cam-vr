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

        // ---- 発見プロトコル用（streamer v0.3.0 で /info 末尾に追記。旧版は空/既定）----
        /// <summary>端末内在カメラ ID（"A"/"B"/"C"）。discovery の切替前照合に使う。空 = 未設定。</summary>
        public string cameraId = "";
        /// <summary>install 毎 UUID。roam / conflict 判別用。</summary>
        public string uuid = "";
        /// <summary>show トークン（隣ブース混線対策）。切替前に自分の showToken と照合する。</summary>
        public string show = "";

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

        // ---- 発熱（streamer v0.7.0〜。v0.11.0 で意味が変わった）----
        // 熱で fps が落ちている間は「経路が詰まった」わけではないので、
        // lag 判定（recvFps/phoneFps 比）で MJPEG を張り直してはいけない。
        // 張り直すと黒 or 砂嵐が出るうえ再接続でさらに熱が上がり、事態を悪化させる。
        //
        // ⚠⚠ streamer v0.11.0 で**アプリ側の throttle は全廃**した（本番の体験中に映像が
        // 劣化するのが致命的なため）。それでも lag 抑止は要る — アプリが絞らなくなった分
        // 熱の上がり方は速くなり、**OS が強制的に絞る**からで、そのときも経路は詰まっていない。

        /// <summary>Android の熱ステータス（0=none … 6=shutdown）。</summary>
        public int thermalStatus;

        /// <summary>熱の余裕（0-1・1 に近いほど余裕がない）。</summary>
        public float thermalHeadroom;

        /// <summary>
        /// 配信側の熱の段（0=S0 / 1=S1 / 2=S2）。
        /// ⚠ v0.11.0 以降は「絞っている段」ではなく**熱の段**（アプリは絞っていない）。
        /// キー名は wire 契約なので配信側と揃えたまま変えていない。
        /// </summary>
        public int throttleStage;

        /// <summary>電池温度 (℃)。現場で「熱いから落ちた」を人が確認するための表示用。</summary>
        public float batteryTempC;

        /// <summary>配信端末が熱いか（= fps が落ちうるので lag 判定を抑止する条件）。</summary>
        public bool IsHot => throttleStage > 0;
    }
}
