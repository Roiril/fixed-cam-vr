#nullable enable
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

        public bool SourceIsVideo
        {
            get
            {
                if (clip != null) return true;
                if (stillImage != null) return false;
                string u = sourceUrl.ToLowerInvariant();
                return u.EndsWith(".mp4") || u.EndsWith(".webm") || u.EndsWith(".mov");
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
