#nullable enable

namespace FixedCamVr.Streaming
{
    /// <summary>AIエージェントから届く連絡の種類。<b>文面は <c>CommsPanel</c> が持つ</b>（ここは判断だけ）。</summary>
    public enum CommsNotice
    {
        /// <summary>何も出さない。</summary>
        None,
        /// <summary>
        /// ⓪a <b>タイトルが焼け切った直後</b>。スイが名乗る（<c>canon/LEDGER.md</c> 0079）。
        /// ⚠ 0073 では①の 1 行目だったものを<b>ここへ移した</b> — 名乗るのは会う前で、
        /// 調査の指示は装置に入ってからの方が順序として正しい。
        /// </summary>
        Greeting,
        /// <summary>
        /// ⓪b 名乗った後。<b>「矢印の方向から、指定されたポイントへ移動してください。」</b>
        /// 床の矢印と円（<see cref="WalkGuide"/>）はこの連絡と対で出る。
        /// </summary>
        Walk,
        /// <summary>
        /// ⓪c <b>円へ着いて導入演出が始まった瞬間</b>（2026-08-17・ユーザー指定
        /// 「演出が始まったら『ポイントに到着しました。観測装置を起動します』とエージェントに言わせよう。
        /// 演出は長いから、エージェントスクリーンが出るのと演出開始は同時でいい」）。
        /// ⚠ <b>段 0 を抜けた縁で出す</b>ので、⓪b（歩行の指示）が出ていればそれを押しのけて上書きする。
        /// </summary>
        Arrived,
        /// <summary>① 導入が明けて、映像だけになった直後。「調査を開始してください。」</summary>
        Begin,
        /// <summary>
        /// ①b <b>①を読ませ終わった縁</b>。「異変を見つけたら／ボタンを長押ししてください／
        /// 装置が解析して解呪します」（2026-08-19・<c>canon/LEDGER.md</c> 0097・ユーザー指定
        /// 「調査を開始してください→異変をみつけたら〜と、表示は時間的に分けて。その間を切り詰める」）。
        ///
        /// ⚠ <b>1 通に戻さない。</b> 4 行を一度に出すと、読み手は「開始の合図」と「押し方の説明」を
        /// 同時に読むことになる。分けたぶんは<b>間を 0 にして同じ面のまま繋ぐ</b>ので、
        /// 画としては「文面が入れ替わる」1 続きに見える。
        /// </summary>
        BeginHow,
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
        /// <summary>本編（<see cref="ShowPhase.Run"/>）に居るか。終幕では連絡を出さない。</summary>
        public bool inRun;

        /// <summary>
        /// 導入（<see cref="ShowPhase.Intro"/>）に居るか。
        /// ⚠⚠ <b>2026-08-17 まで、導入では 1 通も出していなかった</b>（<c>canon/LEDGER.md</c> 0079）。
        /// 歩行誘導はタイトルと導入演出のあいだで起きるので、ここを開けないと指示を出す面が無い。
        /// </summary>
        public bool inIntro;

        /// <summary>
        /// タイトルが画面を手放したか（<c>ShowControlClient.StartAuthorized</c>）。
        /// <b>立つまでは 1 文字も出さない</b> — 題字の上に連絡が重なる。
        /// </summary>
        public bool startAuthorized;

        /// <summary>
        /// 導入がまだ段 0（開始待ち）に居るか。演出が走り出したら誘導の連絡は用済み。
        /// </summary>
        public bool introWaiting;

        /// <summary>
        /// 前の連絡を<b>読ませ終わった</b>か（<c>CommsPanelLogic.DoneReading</c>）。
        ///
        /// ⚠⚠ <b>「畳み終わった」ではない</b>（2026-08-19・<c>canon/LEDGER.md</c> 0096）。
        /// ここが立った瞬間はまだ枠が開いているので、次の連絡を出せば
        /// <b>同じ面のまま文面だけが替わる</b>（ユーザー指定「毎回消して表示しなおすのはしない」）。
        /// 畳み終わりを待つと、枠が左へ畳まれてから開き直す ＝ 毎回かならず吃る。
        /// ⚠ それでも<b>読ませ終わりは待つ</b> — 待たないと自己紹介が読まれないまま指示に上書きされる。
        /// </summary>
        public bool panelDoneReading;

        /// <summary>「報告するまで」のカット（<c>durKind:"untilMark"</c>）が待っているか。</summary>
        public bool waitingForMark;

        /// <summary>このフレームに報告が届いたか（縁）。</summary>
        public bool markPressed;

        /// <summary>
        /// ⚠⚠ <b>その報告で怪異の解除が通ったか</b>（2026-08-17・<c>canon/LEDGER.md</c> 0082）。
        /// 通れば <see cref="CommsNotice.MarkLogged"/>（異常が記録されました）、
        /// 通らなければ <see cref="CommsNotice.MarkNothing"/>（異常は検出されませんでした）。
        ///
        /// ⚠⚠ <b>「演出が走っていたか」ではない。</b> 2026-08-17 まではそれを見ていたので、
        /// <b>3 周目の入れ替わりに押しても「記録されました」と返っていた</b>。
        /// いま false が返るのは、装置が本当に検出できていないから
        /// （解除を実行するエージェントが侵食されている周 ＝ 0070）。
        ///
        /// 供給は <c>ShowControlClient.LastMarkResolved</c>
        /// ＝ <c>TimelineDirector.NotifyVisitorMark</c> の戻り値。
        /// </summary>
        public bool markResolved;

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

        /// <summary>
        /// ③ 締めのカットが待ち始めてから促すまで (秒)。
        /// ⚠ <b>3 → 2 へ</b>（2026-08-19・<c>canon/LEDGER.md</c> 0096）。当初のユーザー指定は「3s ほど」。
        /// ⚠ <c>ShowWalkDebugDriver.ReportHesitateSec</c>（4.5 秒）より短く保つ —
        ///   自動走行が先に押してしまうと③は実機で一度も走らない。
        /// </summary>
        public const float PromptAfterWaitSec = 2f;

        /// <summary>
        /// ⓪a タイトルが焼け切ってから名乗るまで (秒)。<b>一拍おく</b> —
        /// 題字が消えたその瞬間に別の面が開くと、タイトルの余韻ごと上書きされる。
        /// </summary>
        public const float GreetDelaySec = 0.6f;

        /// <summary>
        /// ⓪b 自己紹介を<b>読ませ終わってから</b>指示を出すまで (秒)。
        /// ⚠⚠ <b>0.5 → 0</b>（2026-08-19・<c>canon/LEDGER.md</c> 0096）。0 でないと、
        /// 読ませ終わった面が畳まれ始めてから次が届く ＝ <b>同じ面のまま繋がらない</b>
        /// （<see cref="CommsCueInput.panelDoneReading"/>）。
        /// </summary>
        public const float WalkGapSec = 0f;

        /// <summary>
        /// ⓪b を出し直すまで (秒)。<b>床の矢印と円は出っぱなし</b>なので、文字は繰り返さなくても
        /// 指示は画に残る。それでも動けない体験者のために、間を置いてもう一度だけ出す。
        /// </summary>
        public const float WalkRepeatSec = 18f;

        /// <summary>
        /// ⓪b を出し直せる回数。<b>無制限にしない</b> — 同じ文が何度も来ると、
        /// 装置が壊れているように見える。
        /// </summary>
        public const int WalkRepeatMax = 2;

        private bool _beginFired;
        private bool _beginHowFired;
        private bool _promptFired;
        private float _runSec;
        private float _waitSec;
        private bool _greetFired;
        private bool _walkFired;
        private bool _arrivedFired;
        private int _walkRepeats;
        private float _introSec;
        private float _idleSec;

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
            _beginHowFired = false;
            _promptFired = false;
            _runSec = 0f;
            _waitSec = 0f;
            _greetFired = false;
            _walkFired = false;
            _arrivedFired = false;
            _walkRepeats = 0;
            _introSec = 0f;
            _idleSec = 0f;
        }

        /// <summary>①b（押し方）まで出したか（診断・テスト用）。</summary>
        public bool BeginHowFired => _beginHowFired;

        /// <summary>導入で名乗ったか（診断・テスト用）。</summary>
        public bool GreetFired => _greetFired;

        /// <summary>導入で歩行の指示を出したか（診断・テスト用）。</summary>
        public bool WalkFired => _walkFired;

        /// <summary>演出の始まりを告げたか（診断・テスト用）。</summary>
        public bool ArrivedFired => _arrivedFired;

        /// <summary>時間を進め、このフレームに出す連絡を返す（無ければ <see cref="CommsNotice.None"/>）。</summary>
        public CommsNotice Tick(in CommsCueInput inp)
        {
            float dtAll = inp.dt > 0f ? inp.dt : 0f;

            // ⚠⚠ **導入の分岐が先。** 旧実装は「本編に居なければ ResetRun」だったので、
            //    ここで落ちると導入のラッチも毎フレーム落ちて⓪が延々と出続ける。
            if (inp.inIntro) return TickIntro(inp, dtAll);

            if (!inp.inRun)
            {
                // 本編でも導入でもない（終幕・中止・停止）。連絡を出さないし、状態も持ち越さない。
                ResetRun();
                return CommsNotice.None;
            }

            float dt = dtAll;
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

            // ①b は①を**読ませ終わった縁**で、間を置かずに続ける（`canon/LEDGER.md` 0097）。
            // ⚠ **①を出したそのフレームには立たない**（`!beginDue`）。面はまだ Deliver されておらず
            //   「読ませ終わった」が true のままなので、見ないと 2 通が同じフレームに揃って片方が消える。
            bool howDue = !beginDue && _beginFired && !_beginHowFired && inp.panelDoneReading;

            // 優先は 報告 > 締めの催促 > 開始。**報告は体験者が起こした出来事**なので必ず勝つ
            // （押した手応えが返らないと、装置が壊れているように見える）。
            if (inp.markPressed) return inp.markResolved ? CommsNotice.MarkLogged : CommsNotice.MarkNothing;
            if (promptDue) return CommsNotice.Prompt;
            if (beginDue) return CommsNotice.Begin;
            // ⚠ ①b だけは**押しのけられても消費しない**（上の 2 つと違う）。押し方の説明なので、
            //   ②や③に割り込まれた回では**その連絡を読ませ終わってから**改めて出す。
            if (howDue) { _beginHowFired = true; return CommsNotice.BeginHow; }
            return CommsNotice.None;
        }

        /// <summary>
        /// 導入（段 0）で出す 2 通（<c>canon/LEDGER.md</c> 0079）。
        /// <b>名乗る → 引く → 指示</b>の順で、重ねない。
        ///
        /// ⚠ <b>タイトルが立っているあいだは何も出さない。</b> 題字の上に受信票が重なる。
        /// ⚠ <b>演出が走り出したら止める。</b> 段 0 を抜けたら誘導の指示は用済みで、
        /// パススルーが割れていく最中に文字が浮いていると世界が壊れる。
        /// </summary>
        private CommsNotice TickIntro(in CommsCueInput inp, float dt)
        {
            if (!inp.startAuthorized) { _idleSec = 0f; return CommsNotice.None; }

            // ⓪c 段 0 を抜けた ＝ 演出が始まった（`canon/LEDGER.md` 0079 の赤入れ 3）。
            // ⚠ **面を引くのではなく、上書きする。** ⓪b の指示が出ている最中でも、
            //    `CommsPanel.Deliver` は「すでに出ていれば頭から出し直す」ので枠は開いたまま
            //    文面だけが替わる（畳んでから開き直すと必ず吃る — 0058）。
            if (!inp.introWaiting)
            {
                _idleSec = 0f;
                if (_arrivedFired) return CommsNotice.None;
                _arrivedFired = true;
                return CommsNotice.Arrived;
            }
            _introSec += dt;

            if (!_greetFired)
            {
                if (_introSec < GreetDelaySec) return CommsNotice.None;
                _greetFired = true;
                _idleSec = 0f;
                return CommsNotice.Greeting;
            }

            // ⚠ 前の連絡が引き切るまで数え始めない（面は 1 つしか無い）。
            if (!inp.panelDoneReading) { _idleSec = 0f; return CommsNotice.None; }
            _idleSec += dt;

            if (!_walkFired)
            {
                if (_idleSec < WalkGapSec) return CommsNotice.None;
                _walkFired = true;
                _idleSec = 0f;
                return CommsNotice.Walk;
            }

            if (_walkRepeats >= WalkRepeatMax || _idleSec < WalkRepeatSec) return CommsNotice.None;
            _walkRepeats++;
            _idleSec = 0f;
            return CommsNotice.Walk;
        }
    }
}
