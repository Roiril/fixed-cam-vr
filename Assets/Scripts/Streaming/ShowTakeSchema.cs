#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// show.json v3 の「演出（Take）」1 本。区間 <c>(lap, camera)</c> に付き、開始規則と占有ポリシーを持つ
    /// **剛体**（尺が決まっている）。中に <see cref="ShowStepDef"/>（カット）を順に持つ。
    ///
    /// v2 の <c>cues[]</c>（オーバーレイ）と <c>insert</c>（カメラ差し込み）を 1 語彙へ畳んだもの。
    /// 契約の正本は <c>.claude/plans/2026-07-25_shot-timeline-foundation.md</c> §6。
    ///
    /// JsonUtility の「null 入れ子を既定値で書く」罠を避けるため、<c>start</c> / <c>source</c> / <c>dur</c> は
    /// オブジェクトにせず**フラットな文字列判別子 + 値**で持つ。present-flag が要るのは post だけ。
    /// </summary>
    [Serializable] public sealed class ShowTakeDef
    {
        public string id = "";              // 区間内で一意。空なら実行時に "L<lap>C<cam>#<i>" で補う
        public string name = "";            // UI 表示のみ（実行に影響しない）

        public string at = TakeSchema.AtEnter;              // "enter" | "exit"
        public float offsetSec;                             // at=enter のみ（区間進入からの遅延）
        public string ifMissed = TakeSchema.MissedFireOnExit; // at=enter のみ。"fireOnExit" | "skip"

        public string policy = TakeSchema.PolicyHold;       // "hold" | "yield"
        public bool once = true;                            // ラン内 1 回
        public float maxDurationSec;                        // watchdog。0 / 未指定 = コード既定 45

        public ShowStepDef[] steps = Array.Empty<ShowStepDef>();

        public bool IsExit => TakeSchema.IsExit(at);
        public bool SkipWhenMissed => TakeSchema.SkipWhenMissed(ifMissed);
        public bool IsYield => TakeSchema.IsYield(policy);
    }

    /// <summary>
    /// 演出の 1 カット。「何を映すか（source）＋ 何を重ねるか（cueId）＋ どれだけ（dur）＋ どう繋ぐか（transition）」。
    /// <c>-1</c> は「<c>cueId</c> の素材定義の値をそのまま使う（継承）」（<see cref="ShowBgmDef"/> と同流儀）。
    /// </summary>
    [Serializable] public sealed class ShowStepDef
    {
        public string source = TakeSchema.SourceLive;   // "live" | "inherit" | "clip" | "still"
        public int camera = -1;                         // source=live のみ有効
        public string assetUrl = "";                    // source=clip/still のみ有効（sa:// 可）

        public string cueId = "";                       // オーバーレイ素材。空 = 重ねない
        public float strength = -1f;                    // 以下 -1 = 素材定義から継承
        public float fadeInSec = -1f;
        public float fadeOutSec = -1f;
        public float trimStartSec = -1f;
        public float trimEndSec = -1f;

        public string durKind = TakeSchema.DurSec;      // "sec" | "untilClipEnd"
        public float durSec;

        public string transition = TakeSchema.TransDip; // "cut" | "dip" | "fade"
        public float transitionMs;                      // 0 = 種別ごとのコード既定

        public PostParams? post;
        public bool hasPost;

        public bool IsUntilClipEnd => TakeSchema.IsUntilClipEnd(durKind);
    }

    /// <summary>
    /// v3 スキーマの文字列判別子とコード既定値。**未知の値は既定へ倒す**（例外にしない = 既存流儀）。
    /// 判別が既定へ倒れたかは <c>known</c> out 引数で分かるので、呼び出し側が 1 回だけ警告ログを出せる。
    /// 純粋（UnityEngine 非依存）。
    /// </summary>
    public static class TakeSchema
    {
        public const string AtEnter = "enter";
        public const string AtExit = "exit";

        public const string MissedFireOnExit = "fireOnExit";
        public const string MissedSkip = "skip";

        public const string PolicyHold = "hold";
        public const string PolicyYield = "yield";

        public const string SourceLive = "live";
        public const string SourceInherit = "inherit";
        public const string SourceClip = "clip";
        public const string SourceStill = "still";

        public const string DurSec = "sec";
        public const string DurUntilClipEnd = "untilClipEnd";

        public const string TransCut = "cut";
        public const string TransDip = "dip";
        public const string TransFade = "fade";

        /// <summary>演出の最大長 (秒)。超えたらランタイムが強制終了する（不変条件 2）。</summary>
        public const float DefaultMaxDurationSec = 45f;

        /// <summary>尺が決まらないカットの最終フォールバック (秒)。</summary>
        public const float FallbackStepDurSec = 4f;

        /// <summary>dip 遷移**全体**の既定 (ms)。落とし + 立ち上げの合計（既存の 70+100ms 相当）。</summary>
        public const float DefaultDipMs = 170f;

        /// <summary>fade 遷移全体の既定 (ms)。</summary>
        public const float DefaultFadeMs = 300f;

        /// <summary>遷移全体の長さを「黒へ落とす / 黒から立ち上げる」へ配分する比（既存 70:100 に合わせる）。</summary>
        public const float DipDownRatio = 0.4f;

        /// <summary>遷移全体 (ms) を落とし・立ち上げの秒数へ分ける。cut（0）は 0/0。</summary>
        public static void SplitTransition(float totalMs, out float downSec, out float upSec)
        {
            float total = totalMs > 0f ? totalMs : 0f;
            downSec = total * DipDownRatio / 1000f;
            upSec = total * (1f - DipDownRatio) / 1000f;
        }

        public static bool IsExit(string? at) => at == AtExit;

        public static bool SkipWhenMissed(string? ifMissed) => ifMissed == MissedSkip;

        public static bool IsYield(string? policy) => policy == PolicyYield;

        public static bool IsUntilClipEnd(string? durKind) => durKind == DurUntilClipEnd;

        /// <summary>source 判別子を正規化する。未知は <see cref="SourceLive"/> へ倒し known=false。</summary>
        public static string NormalizeSource(string? source, out bool known)
        {
            known = source == SourceLive || source == SourceInherit
                    || source == SourceClip || source == SourceStill;
            return known ? source! : SourceLive;
        }

        /// <summary>「このカットは映像素材（動画 / 静止画）を全面に出すか」。</summary>
        public static bool IsAssetSource(string? source)
            => source == SourceClip || source == SourceStill;

        /// <summary>watchdog の上限を解決する（0 / 負値 = コード既定）。</summary>
        public static float ResolveMaxDuration(float v)
            => v > 0f ? v : DefaultMaxDurationSec;

        /// <summary>遷移時間 (ms) を解決する（0 / 負値 = 種別ごとのコード既定・cut は常に 0）。</summary>
        public static float ResolveTransitionMs(string? transition, float ms)
        {
            if (transition == TransCut) return 0f;
            if (ms > 0f) return ms;
            return transition == TransFade ? DefaultFadeMs : DefaultDipMs;
        }

        /// <summary>
        /// 素材定義の値と step の上書きを合成する（<c>-1</c> = 継承）。
        /// </summary>
        public static float Inherit(float stepValue, float cueValue)
            => stepValue < 0f ? cueValue : stepValue;
    }
}
