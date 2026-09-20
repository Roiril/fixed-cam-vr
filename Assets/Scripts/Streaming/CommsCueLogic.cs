#nullable enable

namespace FixedCamVr.Streaming
{
    /// <summary>AIエージェントから届く連絡の種類。<b>文面は <c>CommsPanel</c> が持つ</b>（ここは判断だけ）。</summary>
    public enum CommsNotice
    {
        /// <summary>何も出さない。</summary>
        None,
        /// <summary>導入。左コントローラーを起こす。</summary>
        ControllerDisconnected,
        /// <summary>導入。接続済みだが位置を認識できない。</summary>
        ControllerUntracked,
        /// <summary>導入。15秒以上確認できないためスタッフへ知らせる。</summary>
        ControllerStaff,
        /// <summary>導入。左コントローラーの接続と位置認識が安定した。</summary>
        ControllerConfirmed,
        /// <summary>導入。報告操作を練習する。</summary>
        Tutorial,
        /// <summary>導入。短く離した人へ長押しを補足する。</summary>
        TutorialShort,
        /// <summary>導入。練習の報告を受け取った。</summary>
        TutorialAccepted,
        /// <summary>導入中の切断から戻す。</summary>
        TutorialReconnect,
        /// <summary>導入。練習した操作を本編でも使うと伝える。</summary>
        TutorialReminder,
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
        /// <summary>
        /// ①b <b>①「異変を見つけたら…」を読ませ終わった縁</b>。「調査を開始してください。」
        ///
        /// ⚠⚠ <b>2026-09-06 に①b へ回った</b>（<c>canon/LEDGER.md</c> 0174・ユーザー指定
        /// 「調査を開始してくださいと、異変を見つけたらボタンを長押ししてくださいの順番を逆にしよう」）。
        /// それまでは本編に入った直後の 1 通目だった（0097）。
        /// ⚠ 名前は <c>Begin</c> のまま — <b>順番ではなく文面の名前</b>で、
        /// 走行ログ（<c>ev=comms id=</c>）と解析器がこの綴りで繋がっている。
        /// </summary>
        Begin,
        /// <summary>
        /// ① <b>導入が明けて、映像だけになった直後</b>。「異変を見つけたら／ボタンを長押ししてください／
        /// 装置が解析して対処を試みます」（2026-08-19・<c>canon/LEDGER.md</c> 0097・ユーザー指定
        /// 「調査を開始してください→異変をみつけたら〜と、表示は時間的に分けて。その間を切り詰める」）。
        ///
        /// ⚠⚠ <b>2026-09-06 に 1 通目へ回った</b>（<c>canon/LEDGER.md</c> 0174・ユーザー指定
        /// 「調査を開始してくださいと、異変を見つけたらボタンを長押ししてくださいの順番を逆にしよう」）。
        /// ⚠ 名前は <c>BeginHow</c> のまま — <b>順番ではなく文面の名前</b>で、
        /// 走行ログ（<c>ev=comms id=</c>）と解析器がこの綴りで繋がっている。
        ///
        /// ⚠ <b>1 通に戻さない。</b> 4 行を一度に出すと、読み手は「押し方の説明」と「開始の合図」を
        /// 同時に読むことになる。分けたぶんは<b>間を 0 にして同じ面のまま繋ぐ</b>ので、
        /// 画としては「文面が入れ替わる」1 続きに見える。
        /// </summary>
        BeginHow,
        /// <summary>② 報告時に異常演出が表示されていた。「異常を検出しました」。</summary>
        MarkLogged,
        /// <summary>② 報告した瞬間、<b>演出が 1 本も走っていなかった</b>。「異状は検出されませんでした」。侵食度 0.75 未満だけ。</summary>
        MarkNothing,
        /// <summary>
        /// ② 侵食度 0.75 以上で、報告した瞬間に演出が走っていなかった。判定を持たない一文「装置が解析しています」
        /// （<c>canon/LEDGER.md</c> 0232 — 「異常なし」は人形に乗っ取られた結果なので、乗っ取られた装置は
        /// 正直に「異常なし」を言わない。「異常なし」は嘘の一文が塗り替えた行にしか出ない）。出し方は憑依（走り書きに消える）。
        /// </summary>
        MarkAnalyzing,
        /// <summary>
        /// 侵食度 1 の嘘の一文。**ラン 1 回に 1 度だけ**。読ませる段は<b>当たりの報告の返事そのもの</b>
        /// 「異常を検出しました」で、前線が行を渡り切ったコマに<b>その最小編集</b>「異常を検出しませんでした」
        /// （<c>CommsPanel.TakeoverLieText</c>）へ書き換わる（<c>canon/LEDGER.md</c> 0232）。
        /// 出し方は <see cref="CommsDelivery.Possessed"/>（0230 / 0231）— 一気に出て、読ませて、上から乱れとともに塗り替わる。
        /// </summary>
        Takeover,
        /// <summary>
        /// ③a <b>4 周目 A の締めで、押さないまま時間が過ぎた最初の一言</b>「止まってください！」
        /// （2026-09-06・<c>canon/LEDGER.md</c> 0168・ユーザー指定
        /// 「"止まってください！"を短くその前に差し込む」「カタカタ音無しにすっと出てくる感じで」）。
        ///
        /// ⚠⚠ <b>これだけは打たない</b>（<see cref="CommsDelivery.Fade"/>）。
        /// 1 字ずつ印字している間も惜しい、という一言なので、<b>文面ごとすっと浮かんで
        /// 打鍵は 1 発も鳴らない</b>。<c>rules/sound-design.md</c> の「1 文字 1 発」の唯一の例外。
        /// ⚠ 読ませ終わったら<b>同じ面のまま</b> <see cref="Prompt"/> へ替わる（枠は開いたまま・0096）。
        /// </summary>
        Halt,
        /// <summary>
        /// ③b <see cref="Halt"/> を読ませ終わった縁。
        /// 「異常があなたを取り込もうとしています。排除してください。」
        /// </summary>
        Prompt,
    }

    /// <summary>
    /// 連絡の<b>出方</b>。1 通ごとに決まっていて、面（<c>CommsPanelLogic</c>）と
    /// 打鍵（<c>CommsPanel.Apply</c>）と観測（<c>NoticeChars</c>）は<b>これに従うだけ</b>。
    /// </summary>
    public enum CommsDelivery
    {
        /// <summary>
        /// 1 字ずつ打つ（<b>既定</b>）。<b>1 文字 1 発</b>の打鍵音が鳴る
        /// （<c>rules/sound-design.md</c>「印字の打鍵」）。
        /// </summary>
        Typed,
        /// <summary>
        /// <b>文面ごとすっと浮かぶ。</b> 打鍵は 1 発も鳴らない。
        /// ⚠ 濃さだけが上がる — 字は最初から全部そこに在る（<c>CommsPanelLogic.FadeInSec</c>）。
        /// </summary>
        Fade,
        /// <summary>
        /// <b>憑依の出し方</b>（<c>canon/LEDGER.md</c> 0230・侵食度 0.75 以降）。
        /// 全文が<b>一気に</b>出て（打鍵なし）、読ませてから、<b>上から前線が降りて呪われた双子に塗り替わる</b>
        /// （時計は <see cref="CommsPossessionLogic"/>）。
        /// ⚠ 旧「印字へ侵食が追いつく」（<c>Takeover</c>）は 0230 で捨てた。
        /// </summary>
        Possessed,
        /// <summary>乗っ取り後。最初から人形と否定文だけを表示する。</summary>
        Cursed,
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

        /// <summary>
        /// <b>締めのカット（<c>durKind:"untilMark"</c> の段を持つ take）に入ってからの秒数</b>。
        /// 走っていなければ<b>負</b>。供給は <c>TimelineDirector.ClosingTakeSec</c>。
        ///
        /// ⚠⚠ <b>2026-09-19 から、③a の引き金は時計ではなく場所</b>（<see cref="closingLineCrossed"/>・0233）。
        /// この時計で③a が出るのは<b>締めの線が無いとき</b>（<see cref="closingLineDefined"/> = false）だけ。
        /// ⚠ 2026-09-06 に「報告待ちが立ってからの秒数」から替えた（<c>canon/LEDGER.md</c> 0178）。
        /// 前は報告待ちの段（人形の動画が終わったあと）から数えていたので、
        /// <b>その段のあいだに押した人は③を一度も見なかった</b>。
        /// </summary>
        public float closingSec;

        /// <summary>
        /// <b>締めの線（3 周目 A の凍結点）が layout に実体を持つか。</b> 供給は <c>TimelineDirector.ClosingLineDefined</c>。
        /// true なら③a は <see cref="closingLineCrossed"/> だけで出る（時計は見ない）。
        /// false なら時計（<see cref="closingSec"/> ≥ <see cref="CommsCueLogic.HaltAfterClosingSec"/>）の退避路。
        /// ⚠ 線が解決できないことを黙って③の欠落にしない — 0178「止まってください！以降の流れは全員に見せる」。
        /// </summary>
        public bool closingLineDefined;

        /// <summary>
        /// <b>締めのカットの中で、締めの線を踏んだ</b>（2026-09-19・<c>canon/LEDGER.md</c> 0233・ユーザー指定
        /// 「4-A で止まって！というタイミングを、時間指定で 4s ではなく、場所指定にし、その場所を、
        /// 左右反転の演出のときのフリーズされる位置にしてほしい」）。供給は <c>TimelineDirector.ClosingLineCrossed</c>。
        /// 締めのカットのあいだ立ちっぱなし（ラッチ）。締めが終われば下りる。
        /// </summary>
        public bool closingLineCrossed;

        /// <summary>このフレームに報告が届いたか（縁）。</summary>
        public bool markPressed;

        /// <summary>
        /// その報告時に、著作された異常演出が画面を取っていたか。
        /// 侵食度 1 未満では、解除できる演出かどうかに関わらず表示事実を検出結果として返す。
        /// 供給は <c>ShowControlClient.LastMarkDetected</c>。
        /// </summary>
        public bool markDetected;

        /// <summary>
        /// 通信画面の侵食度。0 / 0.25 / 0.75 / 1 の段階値で、1 のあいだは
        /// 通常の検出結果を維持できず、ひと続きの書き換えへ入る。
        /// </summary>
        public float invasionProgress;

        /// <summary>嘘の一文（<see cref="CommsNotice.Takeover"/>）が出ている最中か。再報告で頭へ戻さないために使う。</summary>
        public bool takeoverPlaying;

        /// <summary>現在も本編の通常周に居るか。報告時のスナップショットとは別に中止を優先する。</summary>
        public bool takeoverAllowed;

        /// <summary>追いつきの最後の人形視点が表示中。下降縁で一度だけ乗っ取りを開始する。</summary>
        public bool dollCatchUpShowing;

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
    /// ⚠ <b>①①b③はラン 1 回につき 1 度だけ。②は押すたび。</b>
    /// ⚠⚠ <b>③a は締めのカットの中で締めの線（3 周目 A の凍結点）を踏んだ瞬間に出る</b>
    /// （2026-09-19・0233）。時計（0178 の 5 秒）は<b>線が無いときの退避路</b>。
    /// 報告したかは見ないので、<b>線を踏めば押しても押さなくても③a →③b は流れる</b>（0178）。
    /// ⚠ <b>本編の進行は 1 ビットも変わらない。</b> 連絡は読まなくても勝手に引く
    /// （既読の操作を作らない — <see cref="CommsPanelLogic"/>）。
    /// </summary>
    public sealed class CommsCueLogic
    {
        /// <summary>
        /// 本編に入ってから「調査を開始してください」が届くまで (秒)。
        ///
        /// ⚠⚠ <b>1.5 → 0</b>（2026-08-16・<c>canon/LEDGER.md</c> 0058・ユーザー指示
        /// 「終わったらそのまま鈴を鳴らし、すぐに調査を開始してくださいを表示する。3s またなくていい」）。
        /// 旧コメントは「段 5 の最後は『枠の中に自分が居る』を読む時間だから 0 にしない」だったが、
        /// <b>その読ませる時間そのものを外した</b>（段 5 を 4.5 → 1.6 秒）。鈴が鳴った所が
        /// 「完全にスクリーンになった」で、そこが読ませ終わりなので待つ理由が無い。
        /// </summary>
        public const float BeginDelaySec = 0f;

        /// <summary>
        /// ③a <b>締めの線が無いときだけ</b>の時計: 締めのカットに入ってから「止まってください！」まで (秒)。
        ///
        /// ⚠⚠ <b>2026-09-19 から③a の引き金は場所</b>（<see cref="CommsCueInput.closingLineCrossed"/>・
        /// <c>canon/LEDGER.md</c> 0233・ユーザー指定「時間指定で 4s ではなく、場所指定にし、その場所を、
        /// 左右反転の演出のときのフリーズされる位置に」）。台本から締めの線が引けないとき
        /// （<c>TakeSchema.ResolveClosingLineId</c> が空・layout に実体が無い）だけ、ここへ倒れる。
        /// 値は 0178（ユーザー指定「押してないとき一律 5s」）のまま。
        ///
        /// ⚠ ③b（異常があなたを…）は<b>③a を読ませ終わった縁</b>で続く（間は持たない）。
        /// ⚠ 場所で出すようになったので、<see cref="TakeRunnerLogic.MarkGraceSec"/>（4 秒）より前に
        ///   ③a → ③b が来ることがある（線を早く踏む人）。③a は「止まれ」で「押せ」ではないので
        ///   矛盾はしないが、③b「排除してください」が受付前に届く形はある（`OPEN.md` に問）。
        /// </summary>
        public const float HaltAfterClosingSec = 5f;

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
        private bool _beginHowFired = true;
        private bool _haltFired;
        private bool _promptFired;
        private float _runSec;
        private bool _greetFired;
        private bool _walkFired;
        private bool _arrivedFired;
        private int _walkRepeats;
        private float _introSec;
        private float _idleSec;
        private bool _catchUpWasShowing;
        private bool _catchUpFinished;
        private bool _takeoverDelivered;

        public bool TakeoverDelivered => _takeoverDelivered;

        /// <summary>本編に入ってからの経過（診断用）。</summary>
        public float RunSec => _runSec;

        /// <summary>
        /// 体験 1 回ぶんの状態を落とす。
        /// ⚠ <b>呼び出し元は 2 つある</b>（ラン開始の号令 と 本編を出た縁）。
        /// 片方だけに頼ると、導入を持たない設定で 2 人目に連絡が出なくなる
        /// （2026-08-15 に音で踏んだのと同じ型 — `rules/sound-design.md` §7）。冪等。
        /// </summary>
        public void ResetRun()
        {
            _beginFired = false;
            // 押し方はHMD導入で実際に練習した。ここでは再掲しない。
            _beginHowFired = true;
            _haltFired = false;
            _promptFired = false;
            _runSec = 0f;
            _greetFired = false;
            _walkFired = false;
            _arrivedFired = false;
            _walkRepeats = 0;
            _introSec = 0f;
            _idleSec = 0f;
            _catchUpWasShowing = false;
            _catchUpFinished = false;
            _takeoverDelivered = false;
        }

        /// <summary>①b（開始の合図「調査を開始してください。」）まで出したか（診断・テスト用）。</summary>
        public bool BeginFired => _beginFired;

        /// <summary>①（押し方「異変を見つけたら…」）を出したか（診断・テスト用）。</summary>
        public bool BeginHowFired => _beginHowFired;

        /// <summary>③a（止まってください！）を出したか（診断・テスト用）。</summary>
        public bool HaltFired => _haltFired;

        /// <summary>
        /// その連絡の出方。<b>ここが唯一の窓口</b> — 面も打鍵も観測もここを読む
        /// （散らすと、音だけ / 観測だけが黙って食い違う）。
        ///
        /// ⚠⚠ <b>侵食度 0.75 以降は憑依の出し方</b>（<c>canon/LEDGER.md</c> 0230・
        /// <see cref="CommsCurseLogic.PossessedLevel"/>）。実際にそこで届くのは報告の返事（②）と嘘の一文だけ。
        /// ⚠ 「止まってください！」（浮かぶ）と続く警告（打つ）は<b>侵食度に関わらず従来のまま</b> —
        /// エージェントがなんとか復帰して助ける段（0070）なので、呪いの形では出さない。
        /// </summary>
        public static CommsDelivery DeliveryOf(CommsNotice notice, float invasionProgress)
            => IsOnboarding(notice) ? CommsDelivery.Fade
             : notice == CommsNotice.Halt ? CommsDelivery.Fade
             : notice == CommsNotice.Prompt ? CommsDelivery.Typed
             : notice == CommsNotice.Takeover ? CommsDelivery.Possessed
             : invasionProgress >= CommsCurseLogic.PossessedLevel ? CommsDelivery.Cursed
             : CommsDelivery.Typed;

        /// <summary>侵食度 0 での出方（文面の規約を測るテスト用）。</summary>
        public static CommsDelivery DeliveryOf(CommsNotice notice) => DeliveryOf(notice, 0f);

        public static bool IsOnboarding(CommsNotice notice)
            => notice >= CommsNotice.ControllerDisconnected
               && notice <= CommsNotice.TutorialReminder;

        /// <summary>候補ではなく面へ実際に渡せた時だけ自動提示の一回を消費する。</summary>
        public void NotifyDelivered(CommsNotice notice)
        {
            if (notice == CommsNotice.Takeover) _takeoverDelivered = true;
        }

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

            if (_catchUpWasShowing && !inp.dollCatchUpShowing) _catchUpFinished = true;
            _catchUpWasShowing = inp.dollCatchUpShowing;

            // 追いつき終了は報告や前の通知より優先する。侵食度1への到達だけでは発火しない。
            if (inp.takeoverAllowed && !_takeoverDelivered && _catchUpFinished)
                return CommsNotice.Takeover;
            if (inp.takeoverPlaying) return CommsNotice.None;

            // ③a は**締めのカットの中で締めの線（3 周目 A の凍結点）を踏んだ瞬間**（0233）。
            //    線が無い台本・実体の無い線では、締めに入ってから `HaltAfterClosingSec` 秒の時計（0178）へ倒す。
            // ⚠⚠ **消費しない。面が空くまで待って、必ず出す。** ユーザー指定が
            //    「止まってください！以降の流れは全員に見せる」なので、報告に押しのけられて
            //    消えてよい連絡ではない（時間で立つ①を消費するのとはここが違う）。
            //    面が塞がっていれば `panelDoneReading` が false のあいだ待つ ＝ 割り込まない。
            //    線の記録は締めのカットのあいだ立ちっぱなし（TakeRunnerLogic）なので、待っても消えない。
            bool haltTriggered = inp.closingLineDefined
                ? inp.closingLineCrossed
                : inp.closingSec >= HaltAfterClosingSec;
            bool haltDue = !_haltFired && haltTriggered && inp.panelDoneReading;

            // ③b は③a を**読ませ終わった縁**で、間を置かずに続ける（①→①b と同じ形）。
            // ⚠⚠ **報告したかは見ない**（0178）。③a を見せた以上、続きも見せる。
            bool promptDue = !haltDue && _haltFired && !_promptFired && inp.panelDoneReading;

            bool beginDue = !_beginFired && _runSec >= BeginDelaySec && inp.panelDoneReading;

            // 優先は 報告 > 締めの催促 > 開始。**報告は体験者が起こした出来事**なので必ず勝つ
            // （押した手応えが返らないと、装置が壊れているように見える）。
            if (inp.markPressed)
            {
                // 嘘の一文が出ている最中の再報告は頭へ戻さない（1 回だけ・最後まで見せる）。
                if (inp.markDetected) return CommsNotice.MarkLogged;
                // 0232: 乗っ取られた装置（侵食度 0.75 以上）は正直に「異常なし」を言わない — 判定を持たない一文へ。
                return inp.invasionProgress >= CommsCurseLogic.PossessedLevel
                    ? CommsNotice.MarkAnalyzing : CommsNotice.MarkNothing;
            }
            if (haltDue) { _haltFired = true; return CommsNotice.Halt; }
            if (promptDue) { _promptFired = true; return CommsNotice.Prompt; }
            // 開始の合図は押しのけられても消費しない。報告を読ませ終わってから改めて出す。
            //   ②や③に割り込まれた回では**その連絡を読ませ終わってから**改めて出す。
            if (beginDue) { _beginFired = true; return CommsNotice.Begin; }
            return CommsNotice.None;
        }

        /// <summary>
        /// タイトル後の段0で歩行指示を出す。名乗りは報告練習の前に済ませる。
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

            if (!_greetFired) _greetFired = true;

            if (_introSec < GreetDelaySec) return CommsNotice.None;
            // 前の連絡が引き切るまで数え始めない（面は1つしか無い）。
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
