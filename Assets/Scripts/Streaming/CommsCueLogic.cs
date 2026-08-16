#nullable enable

namespace FixedCamVr.Streaming
{
    /// <summary>AIエージェントから届く連絡の種類。<b>文面は <c>CommsPanel</c> が持つ</b>（ここは判断だけ）。</summary>
    public enum CommsNotice
    {
        /// <summary>何も出さない。</summary>
        None,
        /// <summary>① 導入が明けて、映像だけになった直後。「調査を開始してください」。</summary>
        Begin,
        /// <summary>② 報告した瞬間、<b>演出が走っていた</b>。「異常が記録されました」。</summary>
        MarkLogged,
        /// <summary>② 報告した瞬間、<b>演出が 1 本も走っていなかった</b>。「異常は検出されませんでした」。</summary>
        MarkNothing,
        /// <summary>③ 4 周目 A の締めで、押さないまま時間が過ぎた。「異常が検出されました。記録してください。」</summary>
        Prompt,
    }

    /// <summary>1 フレーム分の入力。<b>UnityEngine 非依存・dt 注入</b>。</summary>
    public struct CommsCueInput
    {
        /// <summary>本編（<see cref="ShowPhase.Run"/>）に居るか。導入・終幕では連絡を出さない。</summary>
        public bool inRun;

        /// <summary>「報告するまで」のカット（<c>durKind:"untilMark"</c>）が待っているか。</summary>
        public bool waitingForMark;

        /// <summary>このフレームに報告が届いたか（縁）。</summary>
        public bool markPressed;

        /// <summary>
        /// ⚠⚠ <b>その報告が届いた瞬間に演出が走っていたか。</b>
        ///
        /// <b>「いま走っているか」を後から見てはいけない。</b> 報告は
        /// <c>ShowControlClient.RecordVisitorMark</c> → <c>TimelineDirector.NotifyVisitorMark</c> と
        /// 流れて、<b>untilMark のカットをその場で畳む</b>。だから次のフレームに演出の有無を見ると、
        /// 4 周目 A の締めで押したときだけ「演出が無かった」に化けて
        /// <see cref="CommsNotice.MarkNothing"/> ＝ <b>意味が真逆の連絡</b>が返る。
        /// 供給は <c>ShowControlClient.LastMarkHadTake</c>（中継の<b>前</b>に確定させてある）。
        /// </summary>
        public bool markHadTake;

        /// <summary>経過（秒）。</summary>
        public float dt;
    }

    /// <summary>
    /// <b>AIエージェントからの連絡を、いつ・どれで出すかを決める。</b>
    /// UnityEngine 非依存・dt 注入。配るのは <c>CommsPanel</c>。
    ///
    /// 判定は <c>canon/LEDGER.md</c> 0054（ユーザー指定の 3 点）:
    /// ①パススルーから 2D スクリーンへの遷移が終わった後 ②報告を送信したとき（演出の有無で文面が変わる）
    /// ③4 周目 A で体験者がボタンを押さずに 3 秒ほど経過。
    ///
    /// ⚠ <b>①③はラン 1 回につき 1 度だけ。②は押すたび。</b>
    /// ⚠ <b>本編の進行は 1 ビットも変わらない。</b> 連絡は読まなくても勝手に引く
    /// （既読の操作を作らない — <see cref="CommsPanelLogic"/>）。
    /// </summary>
    public sealed class CommsCueLogic
    {
        /// <summary>
        /// ① 本編に入ってから連絡が届くまで (秒)。
        ///
        /// ⚠⚠ <b>1.5 → 0</b>（2026-08-16・<c>canon/LEDGER.md</c> 0058・ユーザー指示
        /// 「終わったらそのまま鈴を鳴らし、すぐに調査を開始してくださいを表示する。3s またなくていい」）。
        /// 旧コメントは「段 5 の最後は『枠の中に自分が居る』を読む時間だから 0 にしない」だったが、
        /// <b>その読ませる時間そのものを外した</b>（段 5 を 4.5 → 1.6 秒）。鈴が鳴った所が
        /// 「完全にスクリーンになった」で、そこが読ませ終わりなので待つ理由が無い。
        /// </summary>
        public const float BeginDelaySec = 0f;

        /// <summary>③ 締めのカットが待ち始めてから促すまで (秒)。ユーザー指定「3s ほど」。</summary>
        public const float PromptAfterWaitSec = 3f;

        private bool _beginFired;
        private bool _promptFired;
        private float _runSec;
        private float _waitSec;

        /// <summary>本編に入ってからの経過（診断用）。</summary>
        public float RunSec => _runSec;

        /// <summary>締めのカットが待っている時間（診断用）。</summary>
        public float WaitSec => _waitSec;

        /// <summary>
        /// 体験 1 回ぶんの状態を落とす。
        /// ⚠ <b>呼び出し元は 2 つある</b>（ラン開始の号令 と 本編を出た縁）。
        /// 片方だけに頼ると、導入を持たない設定で 2 人目に連絡が出なくなる
        /// （2026-08-15 に音で踏んだのと同じ型 — `rules/sound-design.md` §7）。冪等。
        /// </summary>
        public void ResetRun()
        {
            _beginFired = false;
            _promptFired = false;
            _runSec = 0f;
            _waitSec = 0f;
        }

        /// <summary>時間を進め、このフレームに出す連絡を返す（無ければ <see cref="CommsNotice.None"/>）。</summary>
        public CommsNotice Tick(in CommsCueInput inp)
        {
            if (!inp.inRun)
            {
                // 本編の外（導入・終幕・中止）では連絡を出さないし、状態も持ち越さない。
                ResetRun();
                return CommsNotice.None;
            }

            float dt = inp.dt > 0f ? inp.dt : 0f;
            _runSec += dt;

            // ⚠ 締めの待ちは「立っているあいだ」だけ数える。押された / 畳まれた時点で 0 へ戻す。
            if (inp.waitingForMark) _waitSec += dt;
            else _waitSec = 0f;

            // ⚠⚠ **ラッチは「出したもの」ではなく「このフレームに条件が揃ったもの」全部を消費する。**
            //     1 フレームに 2 つ揃ったとき、出せるのは 1 通だけ（面が 1 つしかない）。
            //     消費しないと、押しのけられた方が次のフレームに遅れて出て、
            //     体験者から見ると「報告したのに関係ない連絡が来た」になる。
            bool beginDue = !_beginFired && _runSec >= BeginDelaySec;
            bool promptDue = !_promptFired && inp.waitingForMark && _waitSec >= PromptAfterWaitSec;
            if (beginDue) _beginFired = true;
            if (promptDue) _promptFired = true;

            // 優先は 報告 > 締めの催促 > 開始。**報告は体験者が起こした出来事**なので必ず勝つ
            // （押した手応えが返らないと、装置が壊れているように見える）。
            if (inp.markPressed) return inp.markHadTake ? CommsNotice.MarkLogged : CommsNotice.MarkNothing;
            if (promptDue) return CommsNotice.Prompt;
            if (beginDue) return CommsNotice.Begin;
            return CommsNotice.None;
        }
    }
}
