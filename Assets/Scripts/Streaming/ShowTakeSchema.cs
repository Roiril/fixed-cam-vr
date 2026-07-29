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

        public string at = TakeSchema.AtEnter;              // "enter" | "exit" | "line"
        public float offsetSec;                             // at=enter のみ（区間進入からの遅延）
        public string ifMissed = TakeSchema.MissedFireOnExit; // at=enter / line。"fireOnExit" | "skip"

        /// <summary>
        /// at=line のみ。<c>layout.lines[].id</c>（床の通過ライン）。空 / 未定義 id は発火しない。
        /// ラインの担当カメラがこの演出の区間のカメラと違う場合も発火しない（区間紐づけ）。
        /// </summary>
        public string lineId = "";

        public string policy = TakeSchema.PolicyHold;       // "hold" | "yield"

        /// <summary>
        /// 画面が塞がっていて出られなかったときの待ちの寿命。"segment"（既定）| "chain"。
        ///
        /// <b>chain = 「自分を塞いでいた演出が終わるまで待つ」</b>。区間を出ても待ち続ける。
        /// 「前の区間の離脱時演出が 8 秒あって、この区間の滞在が 5 秒しかない」ときに、
        /// 著作した演出が黙って消えるのを防ぐためのもの。
        ///
        /// 持ち越すのは**因果がはっきりしている場合だけ** — 離脱の瞬間に実際に別の演出（かライブ卓）が
        /// 画面を持っていて、かつ開始条件は既に満たしていた（Ready だった）演出に限る。
        /// 「歩くのが速くて時刻に届かなかった」ものは持ち越さない（それは ifMissed の担当）。
        /// at=exit には指定できない（離脱時の演出を別の区間で出すと文脈が最も壊れる）。
        /// </summary>
        public string wait = TakeSchema.WaitSegment;

        public bool once = true;                            // ラン内 1 回
        public float maxDurationSec;                        // watchdog。0 / 未指定 = コード既定 45

        public ShowStepDef[] steps = Array.Empty<ShowStepDef>();

        /// <summary>
        /// 演出中だけの BGM 指示（省略 = 区間で鳴っている曲がそのまま続く）。
        /// 画面と同じ規則で扱う: <b>演出は音も一時的に占有し、終わったら「いまの区間の音」へ戻る</b>。
        /// 型は区間 BGM と同じ <see cref="ShowBgmDef"/>（-1 = トラック既定を継承）。
        /// </summary>
        public ShowBgmDef? bgm;
        public bool hasBgm;                 // present-flag（宣言 bool が正。TimelinePresentFlags 参照）

        /// <summary>「自分を塞いでいた演出が終わるまで、区間を出ても待つ」か。at=exit では常に false。</summary>
        public bool IsChainWait => !IsExit && TakeSchema.IsChainWait(wait);

        public bool IsExit => TakeSchema.IsExit(at);
        /// <summary>開始規則が「このラインを通過したら」か（<see cref="lineId"/> の線分を横切ったら発火）。</summary>
        public bool IsLine => TakeSchema.IsLine(at);
        public bool SkipWhenMissed => TakeSchema.SkipWhenMissed(ifMissed);
        public bool IsYield => TakeSchema.IsYield(policy);
    }

    /// <summary>
    /// 演出の 1 カット。「何を映すか（source）＋ 何を重ねるか（cueId）＋ どれだけ（dur）＋ どう繋ぐか（transition）」。
    /// <c>-1</c> は「<c>cueId</c> の素材定義の値をそのまま使う（継承）」（<see cref="ShowBgmDef"/> と同流儀）。
    /// </summary>
    [Serializable] public sealed class ShowStepDef
    {
        public string source = TakeSchema.SourceLive;   // "live" | "inherit" | "clip" | "still" | "rec"
        public int camera = -1;                         // source=live / rec で有効（rec は録画元カメラ）
        public string assetUrl = "";                    // source=clip/still のみ有効（sa:// / slot:// 可）
        public int recLap;                              // source=rec のみ。録画元の周（0 = 未指定 → 飛ばす）

        public string cueId = "";                       // オーバーレイ素材。空 = 重ねない
        public float strength = -1f;                    // 以下 -1 = 素材定義から継承
        public float fadeInSec = -1f;
        public float fadeOutSec = -1f;
        public float trimStartSec = -1f;
        public float trimEndSec = -1f;

        public string durKind = TakeSchema.DurSec;      // "sec" | "untilClipEnd"
        public float durSec;

        public string transition = TakeSchema.TransDip; // "cut" | "dip" | "fade" | "glitch"
        public float transitionMs;                      // 0 = 種別ごとのコード既定

        // カット頭で 1 回だけ走らせる「映像の乱れ」（企画書 2.3 の注意・移動の誘導）。
        // 遷移の glitch とは別物で、こちらはカットが始まってから単発で走る。0 = 出さない。
        public float glitch;
        public float glitchSec;

        // CG レイヤ（映像の上に立つ人形）。空 = 出さない。actors[] の id を指す。
        public string cg = "";
        public string cgMode = TakeSchema.CgFollow;     // "follow"(体験者 XZ に追従) | "fixed"(下の placement)

        // cgMode="fixed" のときの立ち位置。**人形ではなくカットが持つ**のが要点 —
        // actor 側に位置を持たせていた旧設計では「同じ人形を別のカットで別の場所に立たせる」ができず、
        // 人形を複製する羽目になっていた（2026-07-27 設計批評）。hasPlacement が present-flag。
        // 未指定なら actor の fixedX/fixedZ/fixedYawDeg へフォールバックする（後方互換）。
        public ShowPlacementDef? placement;
        public bool hasPlacement;

        public PostParams? post;
        public bool hasPost;

        public bool IsUntilClipEnd => TakeSchema.IsUntilClipEnd(durKind);
        public bool HasCg => !string.IsNullOrEmpty(cg);
    }

    /// <summary>CG 人形の立ち位置（course 空間）。カット（<see cref="ShowStepDef"/>）が持つ。</summary>
    [Serializable] public sealed class ShowPlacementDef
    {
        public float x;
        public float z;
        public float yawDeg;   // course +Z を 0 とする向き
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
        /// <summary>
        /// 「体験者が床の通過ライン（<c>layout.lines</c>）を横切ったら」。時刻ではなく場所の開始規則。
        /// ラインは担当カメラに紐づくので、別の区間で踏んでも発火しない。
        /// </summary>
        public const string AtLine = "line";

        public const string MissedFireOnExit = "fireOnExit";
        public const string MissedSkip = "skip";

        public const string PolicyHold = "hold";
        public const string PolicyYield = "yield";

        /// <summary>待ちは自分の区間まで（既定・従来の挙動）。</summary>
        public const string WaitSegment = "segment";
        /// <summary>自分を塞いでいた演出が終わるまで、区間を出ても待つ。</summary>
        public const string WaitChain = "chain";

        /// <summary>wait 判別子を正規化する。未知は <see cref="WaitSegment"/> へ倒し known=false。</summary>
        public static string NormalizeWait(string? w, out bool known)
        {
            known = w == WaitSegment || w == WaitChain;
            return known ? w! : WaitSegment;
        }

        public static bool IsChainWait(string? w) => w == WaitChain;

        public const string SourceLive = "live";
        public const string SourceInherit = "inherit";
        public const string SourceClip = "clip";
        public const string SourceStill = "still";
        /// <summary>端末内に録っておいた区間の映像（<c>camera</c> + <c>recLap</c> で指す）。</summary>
        public const string SourceRec = "rec";

        public const string CgFollow = "follow";
        public const string CgFixed = "fixed";

        /// <summary>ラン中に卓が実体を差し替える素材の URL スキーム（<c>slot://&lt;name&gt;</c>）。</summary>
        public const string SlotScheme = "slot://";

        public const string DurSec = "sec";
        public const string DurUntilClipEnd = "untilClipEnd";

        public const string TransCut = "cut";
        public const string TransDip = "dip";
        public const string TransFade = "fade";
        /// <summary>
        /// 黒ではなく「映像の乱れ」で継ぎ目を覆う遷移。dip と同じ状態機械（落とし → 差し替え → 立ち上げ）を
        /// 通り、黒の代わりに <see cref="GlitchFx"/> の持続成分を上げ下げする。企画書 2.3 の
        /// 「差し替えの前後に映像の乱れを挿入し、伝送の劣化を装って継ぎ目を隠す」がこれ。
        /// </summary>
        public const string TransGlitch = "glitch";

        /// <summary>演出の最大長 (秒)。超えたらランタイムが強制終了する（不変条件 2）。</summary>
        public const float DefaultMaxDurationSec = 45f;

        /// <summary>尺が決まらないカットの最終フォールバック (秒)。</summary>
        public const float FallbackStepDurSec = 4f;

        /// <summary>dip 遷移**全体**の既定 (ms)。落とし + 立ち上げの合計（既存の 70+100ms 相当）。</summary>
        public const float DefaultDipMs = 170f;

        /// <summary>fade 遷移全体の既定 (ms)。</summary>
        public const float DefaultFadeMs = 300f;

        /// <summary>glitch 遷移全体の既定 (ms)。黒より少し長く取らないと「壊れた」に見えない。</summary>
        public const float DefaultGlitchMs = 220f;

        /// <summary>glitch 遷移で到達する乱れの強さ。</summary>
        public const float GlitchTransitionLevel = 0.85f;

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

        public static bool IsLine(string? at) => at == AtLine;

        public static bool SkipWhenMissed(string? ifMissed) => ifMissed == MissedSkip;

        public static bool IsYield(string? policy) => policy == PolicyYield;

        public static bool IsUntilClipEnd(string? durKind) => durKind == DurUntilClipEnd;

        /// <summary>source 判別子を正規化する。未知は <see cref="SourceLive"/> へ倒し known=false。</summary>
        public static string NormalizeSource(string? source, out bool known)
        {
            known = source == SourceLive || source == SourceInherit
                    || source == SourceClip || source == SourceStill || source == SourceRec;
            return known ? source! : SourceLive;
        }

        /// <summary>cgMode 判別子を正規化する。未知は <see cref="CgFollow"/> へ倒し known=false。</summary>
        public static string NormalizeCgMode(string? mode, out bool known)
        {
            known = mode == CgFollow || mode == CgFixed;
            return known ? mode! : CgFollow;
        }

        /// <summary>「このカットは映像素材（動画 / 静止画）を全面に出すか」。</summary>
        public static bool IsAssetSource(string? source)
            => source == SourceClip || source == SourceStill;

        /// <summary>「このカットは端末内録画を映すか」。</summary>
        public static bool IsRecSource(string? source) => source == SourceRec;

        /// <summary>素材スロット URL（<c>slot://name</c>）ならスロット名を返す。違えば空文字。</summary>
        public static string SlotName(string? url)
            => !string.IsNullOrEmpty(url) && url!.StartsWith(SlotScheme, StringComparison.Ordinal)
                ? url.Substring(SlotScheme.Length)
                : "";

        /// <summary>watchdog の上限を解決する（0 / 負値 = コード既定）。</summary>
        public static float ResolveMaxDuration(float v)
            => v > 0f ? v : DefaultMaxDurationSec;

        /// <summary>遷移時間 (ms) を解決する（0 / 負値 = 種別ごとのコード既定・cut は常に 0）。</summary>
        public static float ResolveTransitionMs(string? transition, float ms)
        {
            if (transition == TransCut) return 0f;
            if (ms > 0f) return ms;
            if (transition == TransFade) return DefaultFadeMs;
            return transition == TransGlitch ? DefaultGlitchMs : DefaultDipMs;
        }

        /// <summary>遷移の見た目が「黒」ではなく「乱れ」か。</summary>
        public static bool IsGlitchTransition(string? transition) => transition == TransGlitch;

        /// <summary>
        /// 素材定義の値と step の上書きを合成する（<c>-1</c> = 継承）。
        /// </summary>
        public static float Inherit(float stepValue, float cueValue)
            => stepValue < 0f ? cueValue : stepValue;
    }
}
