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

        // フレーム列ソース（端末内録画）。非 null なら clip / stillImage / sourceUrl より優先する。
        // ScreenOverlayController が毎フレーム Tick し、終端で自動的に畳む。
        public Recording.IFrameSequence? frames;

        // フレーム列を流す速さ（1 = 等速）。途中で変えるのは ScreenOverlayController.SetFramesRate
        // （3 周目 A の早送り・canon/LEDGER.md 0272）。
        public float framesRate = 1f;

        // フレーム列を時間ではなく体験者の進み具合で選ぶ（ScreenOverlayController.SetFramesProgress。
        // 3 周目 A の位置合わせ・canon/LEDGER.md 0274）。位置で録っていない録画では時間どおりに流れる。
        public bool framesFollow;

        public float strength = 1f;
        public bool loop = true;
        public float fadeInSeconds = 0.5f;
        public float fadeOutSeconds = 0.5f;

        // 動画の再生区間（秒）。trimEnd<=0 は「最後まで」。区間終端 or 自然終端で自動フェードアウトする。
        public float trimStart = 0f;
        public float trimEnd = 0f;

        // 色統計マッチング（企画書 2.3）。卓が「差し替え素材の統計を実写プレートへ合わせる」Reinhard を解き、
        // per-channel の gain/offset に落として配る。実機は out = src*gain + offset を掛けるだけ。
        // hasMatch=false なら恒等（従来どおりのハード合成）。
        public bool hasMatch;
        public Vector3 matchGain = Vector3.one;
        public Vector3 matchOffset = Vector3.zero;

        // 掛け算（cue の blend:"multiply"・0349）。素材は「シミの写真 / シミの無い写真」の線形の比で、
        // ライブに live * lerp(1, ratio, a) で掛ける。体験者がシミの前に立つと、体にも薄く乗る。
        public bool multiply;

        // 滲んで現れる（0349）。revealUrl は枠空間 640x360 の灰色の順の画（0 が最初・1 が最後・外は 1）。
        // 空なら従来どおり strength のフェードで出入りする。入っている cue は strength を即 1 にし、
        // 出入りは reveal だけで動かす（ScreenOverlayController.TickReveal）。
        public string revealUrl = "";
        public float revealSec;
        public float revealOutSec = OverlayRevealLogic.DefaultOutSec;
        public float revealDelaySec;

        // 長押しで布が膨らむ（0349）。この cue が take の markStartCueId に名指しされたとき、
        // 切り替えではなく、待機の cue を残したまま膨らみの素材（sourceUrl）を press マスクの中へ混ぜる。
        // pressCenter は枠 uv（v は下から）。道具（tools/gen-plate/bleed_maps.py）が重心から出す。
        public string pressMaskUrl = "";
        public Vector2 pressCenter = new(0.5f, 0.5f);

        /// <summary>滲んで現れる cue か（順の画を持つ）。</summary>
        public bool HasReveal => revealUrl.Length > 0;

        /// <summary>長押しで膨らむ cue か（press マスクを持つ）。</summary>
        public bool HasPress => pressMaskUrl.Length > 0;

        /// <summary>フレーム列ソース（端末内録画）か。動画・静止画より優先する。</summary>
        public bool SourceIsFrames => frames != null;

        public bool SourceIsVideo
        {
            get
            {
                if (frames != null) return false;
                if (clip != null) return true;
                if (stillImage != null) return false;
                // クエリ/フラグメント（?v=2 等のキャッシュバスター）を除いた末尾で判定する。
                // これを剥がさないと "clip.mp4?v=2" が拡張子不一致で静止画に誤判定される。
                int end = sourceUrl.Length;
                int q = sourceUrl.IndexOf('?');
                if (q >= 0) end = q;
                int hash = sourceUrl.IndexOf('#');
                if (hash >= 0 && hash < end) end = hash;
                // ToLowerInvariant() は毎回 string を確保するので、範囲付き比較でノーアロケート判定する。
                return EndsWithExt(sourceUrl, end, ".mp4")
                    || EndsWithExt(sourceUrl, end, ".webm")
                    || EndsWithExt(sourceUrl, end, ".mov");
            }
        }

        // s の [0, end) 範囲が ext（小文字前提の拡張子）で終わるか。大小無視・ノーアロケート。
        private static bool EndsWithExt(string s, int end, string ext)
        {
            int n = ext.Length;
            if (end < n) return false;
            int off = end - n;
            for (int i = 0; i < n; i++)
            {
                char c = s[off + i];
                if (c >= 'A' && c <= 'Z') c = (char)(c + 32);
                if (c != ext[i]) return false;
            }
            return true;
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
