#nullable enable
using UnityEngine;
using UnityEngine.Video;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// スクリーン合成の 1 演出 = 「事前撮影クリップ（or 静止画）+ マスク + フェード」の定義。
    /// 固定視点なのでマスクは事前に撮ったフレームから作ればそのまま位置が合う
    /// （web-compositor で検証済みのモデル）。
    /// マスクはスクリーン枠空間の R チャンネル。白 = オーバーレイ表示、黒 = ライブ維持。
    /// null なら全面（白）扱い。
    /// </summary>
    [CreateAssetMenu(menuName = "FixedCamVr/Overlay Cue", fileName = "OverlayCue")]
    public sealed class OverlayCue : ScriptableObject
    {
        [Tooltip("差し替え映像。null なら stillImage を使う。")]
        public VideoClip? clip;

        [Tooltip("clip が null のときに使う静止画（血の手形など）。")]
        public Texture2D? stillImage;

        [Tooltip("合成マスク（R チャンネル、スクリーン枠空間）。null なら全面差し替え。")]
        public Texture2D? mask;

        [Range(0f, 1f)]
        [Tooltip("合成強度の最大値。")]
        public float strength = 1f;

        public bool loop = true;

        [Min(0f)] public float fadeInSeconds = 0.5f;
        [Min(0f)] public float fadeOutSeconds = 0.5f;
    }
}
