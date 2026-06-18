#nullable enable
using System;
using UnityEngine;
using UnityEngine.Video;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// オーバーレイ演出 1 つ分のプレーンデータ。
    /// OverlayCue (ScriptableObject) からも、Web オペレータ卓の show.json からも作れる。
    /// ソースはローカル参照（clip / stillImage / maskTexture）と URL（sourceUrl / maskUrl）の
    /// どちらでも指定可。URL は ScreenOverlayController がロードする。
    /// </summary>
    public sealed class OverlayCueData
    {
        public string id = "";
        public string displayName = "";

        // ローカル参照（OverlayCue SO 経由）
        public VideoClip? clip;
        public Texture2D? stillImage;
        public Texture2D? maskTexture;

        // URL 参照（オペレータ卓サーバ経由）。拡張子で動画/静止画を判別する。
        public string sourceUrl = "";
        public string maskUrl = "";

        public float strength = 1f;
        public bool loop = true;
        public float fadeInSeconds = 0.5f;
        public float fadeOutSeconds = 0.5f;

        // 動画の再生区間（秒）。trimEnd<=0 は「最後まで」。区間終端 or 自然終端で自動フェードアウトする。
        public float trimStart = 0f;
        public float trimEnd = 0f;

        public bool SourceIsVideo
        {
            get
            {
                if (clip != null) return true;
                if (stillImage != null) return false;
                // ToLowerInvariant() は毎回 string を確保するので、StringComparison 付き EndsWith で
                // ノーアロケート判定する（拡張子の大小無視はこれで足りる）。
                return sourceUrl.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                    || sourceUrl.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
                    || sourceUrl.EndsWith(".mov", StringComparison.OrdinalIgnoreCase);
            }
        }

        public static OverlayCueData From(OverlayCue so) => new()
        {
            id = so.name,
            displayName = so.name,
            clip = so.clip,
            stillImage = so.stillImage,
            maskTexture = so.mask,
            strength = so.strength,
            loop = so.loop,
            fadeInSeconds = so.fadeInSeconds,
            fadeOutSeconds = so.fadeOutSeconds,
        };
    }
}
