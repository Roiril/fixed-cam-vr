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
        /// ⓪c <b>円へ着いて導入演出が始まった瞬間</b>。到着を告げ、観測カメラへ接続すると言う
        /// （直後にテレビが点いてカメラの映像が来る）。文面は <c>CommsPanel</c>。
        /// ⚠ <b>段 0 を抜けた縁で出す</b>ので、⓪b（歩行の指示）が出ていればそれを押しのけて上書きする。
        /// </summary>
        Arrived,
        /// <summary>
        /// ① <b>導入が明けて、映像だけになった直後</b>の開始の合図。一周目だと告げ、手すりを持って廻り始めるよう言う。
        /// 周の節目を告げる 3 通（これ・<see cref="SecondLap"/>・<see cref="ThirdLap"/>）の 1 通目。文面は <c>CommsPanel</c>。
        /// ⚠ 名前は <c>Begin</c> のまま — 走行ログ（<c>ev=comms id=</c>）と解析器がこの綴りで繋がっている。
        /// </summary>
        Begin,
        /// <summary>
        /// 押し方と、押すと何が起きるか。<b>本編では出さない</b>（導入の練習で実際に押させ、
        /// <see cref="TutorialReminder"/> が同じことを言う。<see cref="ResetRun"/> が出し済みにする）。
        /// 導入を持たない設定のために文面だけ残してある。
        /// ⚠ 名前は <c>BeginHow</c> のまま — 走行ログ（<c>ev=comms id=</c>）と解析器がこの綴りで繋がっている。
        /// </summary>
        BeginHow,
        /// <summary>
        /// ② 報告時に異常演出が表示されていて、<b>報告で消せる演出だった</b>。検出したことと、遮断することを返す。
        /// どの演出への報告かが分かれば、あいだに解析の中身を 1 行挟む（<c>CommsPanel.FindingLine</c>）。
        /// 消せない演出への報告は <see cref="MarkStuck"/>。
        /// </summary>
        MarkLogged,
        /// <summary>
        /// ② 報告した瞬間、<b>演出が 1 本も走っていなかった</b>。侵食度 0.75 未満だけ。
        /// 文面は乗っ取り後の赤い嘘（<c>CommsPanel.TakeoverLieText</c>）と 1 字も違わない。
        /// </summary>
        MarkNothing,
        /// <summary>
        /// ② 侵食度 0.75 以上で、報告した瞬間に演出が走っていなかった。判定を持たない一文「解析が、終わりません。」
        /// （<c>canon/LEDGER.md</c> 0232 — 「異常なし」は人形に乗っ取られた結果なので、乗っ取られた装置は
        /// 正直に「異常なし」を言わない。「異常なし」は嘘の一文が塗り替えた行にしか出ない）。出し方は憑依（走り書きに消える）。
        /// </summary>
        MarkAnalyzing,
        /// <summary>
        /// 乗っ取りの一文。**ラン 1 回に 1 度だけ**。読ませる段は<b>当たりの報告の返事の 1 行目そのもの</b>で、
        /// 前線が行を渡り切ったコマに、空振りの返事と同じ文（<c>CommsPanel.TakeoverLieText</c>）へ書き換わる
        /// （<c>canon/LEDGER.md</c> 0232）。
        /// 出し方は <see cref="CommsDelivery.Possessed"/>（0230 / 0231）— 一気に出て、読ませて、乱れとともに塗り替わる。
        /// </summary>
        Takeover,
        /// <summary>
        /// ③a <b>最後の A の締めで、線を踏むか 3 秒が過ぎた最初の一言</b>。止まれと言い、何を止めるのか（廻ること）を言う。
        /// 文面は <c>CommsPanel</c>。
        ///
        /// ⚠⚠ <b>これだけは打たない</b>（<see cref="CommsDelivery.Fade"/>）。
        /// 1 字ずつ印字している間も惜しい、という一言なので、<b>文面ごとすっと浮かんで
        /// 打鍵は 1 発も鳴らない</b>。<c>rules/sound-design.md</c> の「1 文字 1 発」の唯一の例外。
        /// ⚠ 画に居るのは 1.8 秒ほど。文面を長くしない（<c>CommsNoticeTextTests.TheStopCall_SaysWhatToStop_AndIsShort</c>）。
        /// ⚠ 読ませ終わったら<b>同じ面のまま</b> <see cref="Prompt"/> へ替わる（枠は開いたまま・0096）。
        /// </summary>
        Halt,
        /// <summary>
        /// ③b <see cref="Halt"/> を読ませ終わった縁。止まる理由（廻り続ければ、体験者もスイも人形になる）と、
        /// 報告すればスイが遮断することを言う。導入の約束（<see cref="TutorialReminder"/>）の言い直し。文面は <c>CommsPanel</c>。
        /// </summary>
        Prompt,
        /// <summary>
        /// ④ 最後の A の締めの報告が通って<b>呪いが解けた</b>ときの返事（遮断したこと・おかえりなさい）。
        /// （2026-10-21・<c>canon/LEDGER.md</c> 0284。それまでは②の返事をそのまま返していて急だった）。
        /// 選ぶのは <c>CommsPanel.Deliver</c>（②a が届いた同じフレームに呪いが解けていれば差し替える）。
        /// ⚠ 侵食度 1 のままでも出方はスイの通常の印字（<see cref="CommsDelivery.Typed"/>）。
        /// </summary>
        MarkCleared,
        /// <summary>
        /// ⑤ 帰還の別れで、<b>人形が後ろを向き始めた縁</b>に 1 度だけ出す別れの一言。
        /// 去っていく人形がいま言っていることを、スイが引用で伝える（判定も内面の推測も言わない）。文面は <c>CommsPanel</c>。
        /// ⚠ <b>この文はシュビーの案で、ユーザーの判定はまだ無い</b>
        /// （<c>canon/OPEN.md</c>）。外すときは <see cref="CommsCueLogic.Tick"/> の 1 か所を止める。
        /// 出方は <see cref="CommsDelivery.Fade"/>（オルゴールが鳴っている場面なので打鍵を鳴らさない）。
        /// 面は終幕の電源断まで出したまま（<see cref="CommsCueLogic.FarewellFindingStays"/>）。
        /// </summary>
        FarewellFinding,
        /// <summary>
        /// ② 報告時に異常演出が表示されていたが、<b>報告では消せない演出だった</b>
        /// （乗っ取りの前の 2 周目 C の人形視点など）。検出したこと・観測カメラの映像ではないこと・遮断できないことを返す。
        /// 消せなかったのに「遮断します」と返すと、乗っ取りの前にスイが嘘をつくことになる。
        /// 同じ理由を既に言っていれば（<see cref="CommsCueLogic.StuckReasonAlreadySaid"/>）、面は別の 1 行を返す。
        /// ⚠ 末尾に足してある（<see cref="CommsCueLogic.IsOnboarding"/> は並びの範囲で判定する）。
        /// </summary>
        MarkStuck,
        /// <summary>
        /// 区間の周が 2 に入った後、面が空いた縁に 1 度だけ出す。二周目だと告げ、廻るたびに異常が強まっているようだと言う。
        /// 人形の視点が始まった後には出さない。文面は <c>CommsPanel</c>。
        /// ⚠ シュビーの案で、ユーザーの判定はまだ無い（<c>canon/OPEN.md</c>）。
        /// </summary>
        SecondLap,
        /// <summary>
        /// 区間の周が 3 に入った後、面が空いた縁に 1 度だけ出す。周の節目を告げる 3 通目
        /// （一周目 ＝ <see cref="Begin"/>・二周目 ＝ <see cref="SecondLap"/>）。
        /// <b>乗っ取りの後は人形の顔と赤い字で、同じ型の嘘が出る</b>（<c>CommsPanel.CursedBodyFor</c>）—
        /// 「異常はありません。そのまま、廻ってください。」。スイ自身の「廻り始めてください」の真似で、
        /// 締めの「止まってください！」と「廻り続ければ…取り込みます」が打ち消す。
        /// 乗っ取りが起きなかった走行では、スイのまま正直な文が出る。
        /// ⚠ シュビーの案で、ユーザーの判定はまだ無い（<c>canon/OPEN.md</c>）。
        /// </summary>
        ThirdLap,
        /// <summary>
        /// 2 周目 C で人形の視点の映像が混ざり始めた後、面が空いた縁に 1 度だけ、<b>スイが報告を待たずに</b>出す警告。
        /// 「観測カメラ以外の映像が、混ざっています。遮断できません。」
        /// 同じことは報告の返事（<see cref="MarkStuck"/>）も言うが、あちらは報告した人しか読まない。
        /// これが無いと、報告しなかった人には乗っ取りの「遮断失敗」が前触れなしに来る。
        /// 先に <see cref="MarkStuck"/> を返していれば出さない（同じことを 2 度言わない）。
        /// ⚠ シュビーの案で、ユーザーの判定はまだ無い（<c>canon/OPEN.md</c>）。
        /// </summary>
        ForeignFeed,
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
        /// <b>最終 A の確定進入からの秒数</b>。まだ着いていなければ負。
        /// 供給は <c>TimelineDirector.ClosingSegmentSec</c>。締めの演出が始まる前も進む。
        /// 線を踏めば早く出る。踏まなくても 3 秒で出る。
        /// </summary>
        public float closingSec;

        /// <summary>
        /// <b>締めの線（3 周目 A の凍結点）が layout に実体を持つか。</b> 供給は <c>TimelineDirector.ClosingLineDefined</c>。
        /// ③a は線の有無に関わらず時計でも出る。
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
        /// その報告時に画面を取っていた演出が、<b>報告では消せないものだった</b>か。
        /// 供給は <c>ShowControlClient.LastMarkStuck</c>。<see cref="markDetected"/> が立っているときだけ読む。
        /// ⚠ 否定形で持つ — 既定（false）は従来どおり <see cref="CommsNotice.MarkLogged"/>。
        /// </summary>
        public bool markStuck;

        /// <summary>区間の周が 2 に居るか（<c>TimelineDirector.SegmentLap == 2</c>）。<see cref="CommsNotice.SecondLap"/> の材料。</summary>
        public bool secondLap;

        /// <summary>区間の周が 3 に居るか（<c>TimelineDirector.SegmentLap == 3</c>）。<see cref="CommsNotice.ThirdLap"/> の材料。</summary>
        public bool thirdLap;

        /// <summary>
        /// 人形の視点の映像が 1 度でも画に出たか（通信画面の侵食度が 0 を超えた）。<see cref="CommsNotice.ForeignFeed"/> の材料。
        /// ⚠ <see cref="invasionProgress"/> とは別に持つ。侵食度だけを入れて回す既存の検証が、警告を拾わないようにする。
        /// </summary>
        public bool dollViewStarted;

        /// <summary>
        /// 通信画面の侵食度。0 / 0.25 / 0.75 / 1 の段階値で、1 のあいだは
        /// 通常の検出結果を維持できず、ひと続きの書き換えへ入る。
        /// </summary>
        public float invasionProgress;

        /// <summary>嘘の一文（<see cref="CommsNotice.Takeover"/>）が出ている最中か。再報告で頭へ戻さないために使う。</summary>
        public bool takeoverPlaying;

        /// <summary>現在も本編の通常周に居るか。報告時のスナップショットとは別に中止を優先する。</summary>
        public bool takeoverAllowed;

        /// <summary>追いつきの最後の人形視点が表示中。既存の表示観測との互換用で、乗っ取り開始には使わない。</summary>
        public bool dollCatchUpShowing;

        /// <summary>
        /// 追いつきの最後の人形視点が成立した累計（自然完了か、人形視点が出た後に前へ抜けた yield）。供給は
        /// <c>TimelineDirector.DollCatchUpCompletedCount</c>。増加したフレームで乗っ取りを開始する。
        /// </summary>
        public int dollCatchUpCompletedCount;

        /// <summary>
        /// ライブ卓が画面へ介入中か。介入中に届いた完了は観測済みにして捨て、解除後へ持ち越さない。
        /// </summary>
        public bool takeoverSuppressed;

        /// <summary>
        /// 帰還の別れの人形が<b>後ろを向き始めた</b>（<c>EndingFarewellLogic.Phase.Turn</c> か <c>Retreat</c>）。
        /// 供給は <c>TimelineDirector.FarewellPhase</c>。⚠ Retreat も含めるのは、1 フレームが長いと
        /// Turn（0.75 秒）を飛ばして Retreat へ入ることがあるため。Done・Unavailable は含めない（人形が画に居ない）。
        /// </summary>
        public bool farewellTurned;

        /// <summary>呪いが解けたか（<c>ShowRunDirector.ScreenDecayReleased</c>）。⑤は帰還の別れでしか出さない。</summary>
        public bool curseReleased;

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
    /// （2026-09-19・0233）。線を踏まない場合は 3 秒の時計で出す。
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
        /// ③a の時計: 最終 A の確定進入から「止まってください！」まで (秒)。
        ///
        /// 2026-09-21・0247: 線を踏む OR 最終 A に入って 3 秒。線があっても時計は有効。
        ///
        /// ⚠ ③b（異常があなたを…）は<b>③a を読ませ終わった縁</b>で続く（間は持たない）。
        /// ⚠ 場所で出すようになったので、<see cref="TakeRunnerLogic.MarkGraceSec"/>（4 秒）より前に
        ///   ③a → ③b が来ることがある（線を早く踏む人）。③a は「止まれ」で「押せ」ではないので
        ///   矛盾はしないが、③b「排除してください」が受付前に届く形はある（`OPEN.md` に問）。
        /// </summary>
        public const float HaltAfterClosingSec = 3f;

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
        private int _dollCatchUpCompletedObserved;
        private bool _catchUpFinished;
        private bool _takeoverDelivered;
        private bool _farewellFindingFired;
        private bool _secondLapFired;
        private bool _thirdLapFired;
        private bool _foreignFeedFired;

        public bool TakeoverDelivered => _takeoverDelivered;

        /// <summary>⑤（別れの解析結果）を出したか（診断・テスト用）。</summary>
        public bool FarewellFindingFired => _farewellFindingFired;

        /// <summary>2 周目の連絡を出したか（診断・テスト用）。</summary>
        public bool SecondLapFired => _secondLapFired;

        /// <summary>人形の視点の映像への警告を出した（か、同じ中身の返事を返した）か（診断・テスト用）。</summary>
        public bool ForeignFeedFired => _foreignFeedFired;

        /// <summary>
        /// 直近の <see cref="CommsNotice.MarkStuck"/> を返した時点で、理由（観測カメラの映像ではない）を
        /// 既に 1 度言っていたか。立っていれば面は理由の代わりに別の 1 行を返す（警告の直後に同じ文を繰り返すと、くどい）。
        /// </summary>
        public bool StuckReasonAlreadySaid { get; private set; }

        /// <summary>3 周目の連絡を出したか（診断・テスト用）。</summary>
        public bool ThirdLapFired => _thirdLapFired;

        /// <summary>Update 順序によらず、追いつき完了から強制表示までの入力も止める。</summary>
        public bool HasPendingTakeover(int completedCount)
            => !_takeoverDelivered && (_catchUpFinished
                || completedCount > _dollCatchUpCompletedObserved);

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
            _dollCatchUpCompletedObserved = 0;
            _catchUpFinished = false;
            _takeoverDelivered = false;
            _farewellFindingFired = false;
            _secondLapFired = false;
            _thirdLapFired = false;
            _foreignFeedFired = false;
            StuckReasonAlreadySaid = false;
        }

        /// <summary>開始の合図（<see cref="CommsNotice.Begin"/>）まで出したか（診断・テスト用）。</summary>
        public bool BeginFired => _beginFired;

        /// <summary>押し方（<see cref="CommsNotice.BeginHow"/>）を出したか（診断・テスト用）。</summary>
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
             : notice == CommsNotice.MarkCleared ? CommsDelivery.Typed
             : notice == CommsNotice.FarewellFinding ? CommsDelivery.Fade
             : notice == CommsNotice.Takeover ? CommsDelivery.Possessed
             : invasionProgress >= CommsCurseLogic.PossessedLevel ? CommsDelivery.Cursed
             : CommsDelivery.Typed;

        /// <summary>侵食度 0 での出方（文面の規約を測るテスト用）。</summary>
        public static CommsDelivery DeliveryOf(CommsNotice notice) => DeliveryOf(notice, 0f);

        public static bool IsOnboarding(CommsNotice notice)
            => notice >= CommsNotice.ControllerDisconnected
               && notice <= CommsNotice.TutorialReminder;

        /// <summary>
        /// ⑤（別れの解析結果）を出したままにしてよいか。本編のあいだ（別れが続いている）と、
        /// 終幕の砂嵐のあいだ（電源断の前）は残す。電源断・終幕を出さない設定・ラン開始・導入では畳む。
        /// 結果画面（Report）は電源断より後なので、そこには残らない。
        /// </summary>
        /// <param name="inRun">本編に居る。</param>
        /// <param name="finished">終幕の相に居る。</param>
        /// <param name="outroPresenting">終幕が画に何かを出している（<c>OutroDirector.Presenting</c>）。</param>
        /// <param name="outroElapsedSec">終幕の頭からの秒（<c>OutroDirector.TotalElapsedSec</c>）。</param>
        /// <param name="powerCutAtSec">管が潰れ始めるまでの秒（<c>OutroDirector.PowerCutAtSec</c>）。</param>
        public static bool FarewellFindingStays(bool inRun, bool finished, bool outroPresenting,
                                                float outroElapsedSec, float powerCutAtSec)
            => inRun || (finished && outroPresenting && outroElapsedSec < powerCutAtSec);

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

            int completed = inp.dollCatchUpCompletedCount > 0 ? inp.dollCatchUpCompletedCount : 0;
            if (completed < _dollCatchUpCompletedObserved)
            {
                // TakeRunner.ResetRun の 0 戻し。非 Run の Tick を挟まない構成でも前ランの候補を消す。
                _dollCatchUpCompletedObserved = completed;
                _catchUpFinished = false;
            }
            else if (completed > _dollCatchUpCompletedObserved)
            {
                _dollCatchUpCompletedObserved = completed;
                // 卓が画面を握っているあいだの完了は、その場で消費する。解除後に遅れて嘘を出さない。
                if (!inp.takeoverSuppressed) _catchUpFinished = true;
            }
            if (inp.takeoverSuppressed) _catchUpFinished = false;

            // 追いつき終了は報告や前の通知より優先する。侵食度1への到達だけでは発火しない。
            if (inp.takeoverAllowed && !inp.takeoverSuppressed && !_takeoverDelivered && _catchUpFinished)
                return CommsNotice.Takeover;
            if (inp.takeoverPlaying) return CommsNotice.None;

            // ⑤ 別れの解析結果は面を待たずに、人形が後ろを向き始めた縁で 1 度だけ出す。
            // ⚠ 出した後はこのランで何も出さない。報告が届いても返事（②）で上書きしない —
            //    面は終幕の電源断まで⑤を出したまま（CommsPanel が畳む）。
            if (_farewellFindingFired) return CommsNotice.None;
            if (inp.farewellTurned && inp.curseReleased)
            {
                _farewellFindingFired = true;
                return CommsNotice.FarewellFinding;
            }

            // ③a は締めの線を踏むか、最終 A に入ってから `HaltAfterClosingSec` 秒で出す。
            //    線が著作されていても、体験者が踏まない場合は時計が有効。
            // ⚠⚠ **消費しない。面が空くまで待って、必ず出す。** ユーザー指定が
            //    「止まってください！以降の流れは全員に見せる」なので、報告に押しのけられて
            //    消えてよい連絡ではない（時間で立つ①を消費するのとはここが違う）。
            //    面が塞がっていれば `panelDoneReading` が false のあいだ待つ ＝ 割り込まない。
            //    線の記録は締めのカットのあいだ立ちっぱなし（TakeRunnerLogic）なので、待っても消えない。
            bool haltTriggered = inp.closingLineCrossed
                || inp.closingSec >= HaltAfterClosingSec;
            bool haltDue = !_haltFired && haltTriggered && inp.panelDoneReading;

            // ③b は③a を**読ませ終わった縁**で、間を置かずに続ける（①→①b と同じ形）。
            // ⚠⚠ **報告したかは見ない**（0178）。③a を見せた以上、続きも見せる。
            bool promptDue = !haltDue && _haltFired && !_promptFired && inp.panelDoneReading;

            bool beginDue = !_beginFired && _runSec >= BeginDelaySec && inp.panelDoneReading;

            // 締めでは Halt → Prompt を先に全文見せる。ここで報告を優先すると、同じフレームの
            // Halt / Prompt が押し流され、ShowControl 側の受付解除も永久に来なくなる。
            if (haltDue) { _haltFired = true; return CommsNotice.Halt; }
            if (promptDue) { _promptFired = true; return CommsNotice.Prompt; }
            bool closingLocked = (inp.closingSec >= 0f || inp.closingLineCrossed) && !_promptFired;
            if (inp.markPressed && !closingLocked)
            {
                // 嘘の一文が出ている最中の再報告は頭へ戻さない（1 回だけ・最後まで見せる）。
                // 消せる演出なら「遮断します」、消せない演出なら「遮断できません」。
                if (inp.markDetected)
                {
                    if (!inp.markStuck) return CommsNotice.MarkLogged;
                    // 理由（観測カメラの映像ではない）を既に言っていれば、返事は結果だけにする（面が `StuckReasonAlreadySaid` を読む）。
                    // ここで理由を言うなら、頼まれずに出す警告はもう要らない。
                    StuckReasonAlreadySaid = _foreignFeedFired;
                    _foreignFeedFired = true;
                    return CommsNotice.MarkStuck;
                }
                // 0232: 乗っ取られた装置（侵食度 0.75 以上）は正直に「異常なし」を言わない — 判定を持たない一文へ。
                return inp.invasionProgress >= CommsCurseLogic.PossessedLevel
                    ? CommsNotice.MarkAnalyzing : CommsNotice.MarkNothing;
            }
            // 開始の合図は押しのけられても消費しない。報告を読ませ終わってから改めて出す。
            //   ②や③に割り込まれた回では**その連絡を読ませ終わってから**改めて出す。
            if (beginDue) { _beginFired = true; return CommsNotice.Begin; }
            // 2 周目の連絡。開始の合図より後、面が空いた縁に 1 度だけ。
            // ⚠ 消費しない — 報告の返事に押しのけられたら、読ませ終わってから出す（2 周目のあいだだけ）。
            // ⚠ 人形視点が始まった後（侵食度 > 0）と締めには出さない。乗っ取りの因果（0245）へ割り込まない。
            if (!_secondLapFired && _beginFired && inp.secondLap && inp.panelDoneReading
                && inp.invasionProgress <= 0f && !_takeoverDelivered && inp.closingSec < 0f)
            {
                _secondLapFired = true;
                return CommsNotice.SecondLap;
            }
            // 人形の視点の映像が混ざり始めたら、報告を待たずに 1 度だけ警告する。
            // ⚠ 消費しない — 面が空くまで待つ。乗っ取りが先に来たら出さない（乗っ取りの後は嘘しか出ない）。
            // ⚠ 侵食度 1（3 周目の録画と人形）では出さない。そこで混ざっているのは人形の視点ではない。
            if (!_foreignFeedFired && _beginFired && inp.dollViewStarted && inp.panelDoneReading
                && !_takeoverDelivered && inp.invasionProgress < 1f && inp.closingSec < 0f)
            {
                _foreignFeedFired = true;
                return CommsNotice.ForeignFeed;
            }
            // 3 周目の連絡。乗っ取りの後なら赤い嘘として出る（出方は DeliveryOf と CommsPanel が決める）。
            // ⚠ 消費しない — 乗っ取りや報告の返事に押しのけられたら、読ませ終わってから出す（3 周目のあいだだけ）。
            // ⚠ 締めには出さない。③a「止まってください！」がこの嘘を打ち消す側なので、順序を逆にしない。
            if (!_thirdLapFired && _beginFired && inp.thirdLap && inp.panelDoneReading && inp.closingSec < 0f)
            {
                _thirdLapFired = true;
                return CommsNotice.ThirdLap;
            }
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
