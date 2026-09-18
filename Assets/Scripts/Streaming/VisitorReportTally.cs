#nullable enable
using System.Collections.Generic;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>体験者の報告を数える。</b> 押した回数と、報告した異変の数を<b>分けて</b>持つ純ロジック
    /// （UnityEngine 非依存）。持ち主は <c>ShowControlClient</c>（ラン 1 回ぶんの状態）。
    ///
    /// 判定は <c>canon/LEDGER.md</c> 0234（2026-09-19・ユーザー逐語）:
    /// 「1つの異変に対して報告したら内部では1だけカウントするようにしてほしい」。
    /// それまでは押した回数（左 X / Y の 1 秒長押しの回数）をそのまま終幕の
    /// 「報告した怪異の数」（0048）に出していた — 同じ人形に 3 回押せば ３ と出る、お飾りの数だった。
    ///
    /// <b>異変の単位は、報告が乗った演出（take）の id。</b>
    /// <list type="bullet">
    ///   <item>同じ演出のあいだに何度押しても <see cref="AnomalyCount"/> は 1 だけ増える</item>
    ///   <item>演出が走っていない所で押しても増えない（<see cref="PressCount"/> には残る）</item>
    ///   <item>締めのカット（4 周目 A・<c>untilMark</c>）で報告が通った後、現実へ戻る 3 秒のあいだに
    ///         押し直しても増えない（同じ演出がまだ走っている）</item>
    /// </list>
    ///
    /// ⚠ <b>侵食度 1 の嘘の一文</b>（<c>canon/LEDGER.md</c> 0232「異常を検出しませんでした」）を読んだ
    /// 体験者が押し直しても増えない。スイの言葉が読めるようになった（0230〜0232）ことで
    /// 押し直しが起きるようになった — それがこの数え方を要る理由。
    ///
    /// ⚠ 報告された演出は <c>once</c> の決着が付いて再演しない（<c>TakeRunnerLogic.EndTakeDecision</c>）ので、
    /// id で数えることと「走った回ごと」に数えることは同じになる。
    ///
    /// ⭐ 後々の分岐（報告数によるエンディング・0234「まだしないが」）の材料は
    /// <see cref="AnomalyCount"/>。<see cref="PressCount"/> は使わない。
    /// </summary>
    public sealed class VisitorReportTally
    {
        private readonly HashSet<string> _reported = new();

        /// <summary>
        /// 押した回数（受け付けた報告の数）。締めの猶予（<c>TakeRunnerLogic.MarkGraceSec</c>）で
        /// 捨てた分は<b>呼ばれない</b>ので入らない。スタッフの面（<c>StatusHud</c>）と
        /// <c>ev=mark n=</c> が読む。
        /// </summary>
        public int PressCount { get; private set; }

        /// <summary>
        /// 報告した異変の数（同じ演出は 1 回だけ）。<b>終幕の「報告した怪異の数」はこれ。</b>
        /// </summary>
        public int AnomalyCount => _reported.Count;

        /// <summary>直近の報告が乗った演出の id（演出が走っていなければ空）。</summary>
        public string LastTakeId { get; private set; } = "";

        /// <summary>直近の報告で <see cref="AnomalyCount"/> が増えたか（<c>ev=mark new=</c>）。</summary>
        public bool LastCounted { get; private set; }

        /// <summary>体験 1 回ぶんの状態を落とす（ラン開始）。冪等。</summary>
        public void Reset()
        {
            _reported.Clear();
            PressCount = 0;
            LastTakeId = "";
            LastCounted = false;
        }

        /// <summary>
        /// 報告を 1 回受ける。
        /// </summary>
        /// <param name="takeId">報告した瞬間に走っていた演出の id（<c>TimelineDirector.ActiveTakeId</c>）。
        /// 走っていなければ空。⚠ 報告で段が進む<b>前</b>に凍らせた値を渡す。</param>
        /// <param name="detected">報告した瞬間に異常演出が画面を取っていたか
        /// （<c>ShowControlClient.LastMarkDetected</c>）。スイの返事「異常を検出しました」と同じ判定を
        /// 使う — 画で「検出した」と言ったものだけを数える。</param>
        /// <returns>この報告で <see cref="AnomalyCount"/> が増えたか。</returns>
        public bool Record(string? takeId, bool detected)
        {
            PressCount++;
            LastTakeId = takeId ?? "";
            LastCounted = detected && LastTakeId.Length > 0 && _reported.Add(LastTakeId);
            return LastCounted;
        }

        /// <summary>その演出はもう報告されているか（診断・テスト用）。</summary>
        public bool HasReported(string takeId) => _reported.Contains(takeId);
    }
}
