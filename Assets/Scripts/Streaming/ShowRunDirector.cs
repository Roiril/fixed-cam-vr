#nullable enable
using System;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 体験 1 回の骨格を回す実行体。企画書 3 章「経路を 3 周する／導入を含め 3 分以内／
    /// 導入で固定視点に慣れてから追跡体験を開始する」の実装側。
    ///
    /// 判定はすべて純ロジック <see cref="ShowRunLogic"/> にあり、ここは配線だけを持つ:
    ///   - 導入・終了のあいだ <see cref="CueScheduler.SetShowGate"/> を閉じる
    ///     （演出の武装・端末内録画・区間 post / BGM・実測滞在がまとめて止まる単一の首）
    ///   - 導入 → 本編 で <see cref="ShowControlClient.BeginMainRun"/>（周回と演出だけを初期化。**録画は消さない**）
    ///   - 終了で 走行中の演出を畳む / BGM を落とす / 乱れを畳む → <see cref="PhaseChanged"/> を配る
    ///
    /// <b>凍結ラッチは増やさない。</b> 終了しても画面のカメラ切替は裏で回ったままで、見えなくなるのは
    /// 暗転（<c>ShowEndingFader</c>）のおかげ。凍結を足すと「解除されずに残る」事故を新しく作る。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShowRunDirector : MonoBehaviour
    {
        [Header("References (未配線でも実行時に自己解決する)")]
        [SerializeField] private CueScheduler? cueScheduler;
        [SerializeField] private TimelineDirector? timelineDirector;
        [SerializeField] private ShowControlClient? showControl;
        [SerializeField] private CameraSwitchDirector? switchDirector;

        private readonly ShowRunLogic _logic = new ShowRunLogic();

        /// <summary>
        /// 周を重ねるごとに落ちていく解像度（<c>canon/LEDGER.md</c> 0012）。
        /// 相と周回を両方持っているのはここだけなので、回すのもここ。
        /// </summary>
        private readonly ScreenDecayLogic _decay = new ScreenDecayLogic();

        /// <summary>
        /// 終幕の合図（<c>canon/LEDGER.md</c> 0048）。「この演出が終わったら終わる」の 1 ビットで、
        /// <b>落ちるのは <see cref="BeginRun"/>（周回リセット）だけ</b>。
        /// 相と走行中の演出 id を両方持っているのはここだけなので、判定もここで回す。
        /// </summary>
        private readonly EndingCueLogic _ending = new EndingCueLogic();

        private BgmDirector? _bgm;
        private GlitchFx? _glitch;
        /// <summary>警告音の育ち（0145）を落とすため。⚠ 落とさないと 2 人目が最大から始まる。</summary>
        private SwitchAudioCue? _switchCue;
        private CameraFeelFx? _feel;
        private IntroDirector? _intro;
        private bool _subscribed;
        private ShowPhase _lastPhase = ShowPhase.Intro;
        private float _endFadeSec = ShowRunDefaults.EndFadeSec;
        private float _targetSec = ShowRunDefaults.TargetSec;

        /// <summary>
        /// 相が変わったときに発火する（StatusHud / heartbeat / IntroDirector が読む）。
        /// ⚠ <c>ShowEndingFader</c> はこれを購読しない — <b>導入の中止は相の変化ではない</b>ので届かず、
        /// あちらは <see cref="ShouldBlackout"/> を毎フレーム読む 1 系統にしてある。
        /// </summary>
        public event Action<ShowPhase>? PhaseChanged;

        public ShowPhase Phase => _logic.Phase;
        public int Lap => _logic.Lap;

        /// <summary>
        /// 走り切る周数（<c>run.totalLaps</c>）。
        /// ⚠ <b>これを超えた周は「帰りの区間」</b>（`lap = totalLaps + 1` の `order[0]` は構造的に必ず踏む）。
        /// 連絡の面の侵食が回復するのはそこ（<c>canon/LEDGER.md</c> 0070）。
        /// </summary>
        public int TotalLaps => _logic.TotalLaps;
        public float RunElapsedSec => _logic.RunElapsedSec;
        public float LapElapsedSec => _logic.LapElapsedSec;
        public float IntroElapsedSec => _logic.IntroElapsedSec;

        /// <summary>目安の尺 (秒)。超過は卓に出すだけで体験は止めない。</summary>
        public float TargetSec => _targetSec;

        /// <summary>終了時に黒へ落とす時間 (秒)。<c>ShowEndingFader</c> が読む。</summary>
        public float EndFadeSec => _endFadeSec;

        /// <summary>終了条件は満たしたが走行中の演出を見せ切るために待っているか（卓表示用）。</summary>
        public bool EndHolding => _logic.EndHolding;

        /// <summary>
        /// 終幕の合図の演出が走っているのを見たか（＝ 周回リセットで落ちるフラグ・テレメトリ用）。
        /// <b>著作していなければ一生 false</b>（<see cref="ShowOutroDef.afterTakeId"/> が空）。
        /// </summary>
        public bool EndingArmed => _ending.Armed;

        /// <summary>終幕の合図が既に撃たれたか（テレメトリ用）。</summary>
        public bool EndingFired => _ending.Fired;

        /// <summary>
        /// 装置の劣化そのものの進み 0..1。<b>呪いが解けても下がらない</b>（単調）。
        /// 読むのは<b>音</b>（装置の声の痩せ）と <c>CommsGlitchLogic</c>（AI の侵食の入力）。
        /// ⚠ <b>画はこれを読まない</b> → <see cref="ScreenDecayShown"/>。
        /// </summary>
        public float ScreenDecay => _decay.Progress;

        /// <summary>
        /// <b>いま画に出ている劣化の進み 0..1</b>（テレメトリ用）。呪いが解けると 0 へ戻る。
        /// 「装置は使い込まれたまま、呪いだけが解けた」を分けて観測するために <see cref="ScreenDecay"/> と対で出す。
        /// </summary>
        public float ScreenDecayShown => _decay.Shown;

        /// <summary>いま画へ書いている「枠を横切るブロック数」（0 = 量子化していない・テレメトリ用）。</summary>
        public float ScreenDecayBlocks => _decay.ShownBlocks;

        /// <summary>視界の劣化が「呪いが解けた」で戻り始めたか（テレメトリ用）。</summary>
        public bool ScreenDecayReleased => _decay.Released;

        /// <summary>
        /// <b>解除の進み 0..1</b>（0 = まだ呪われている / 1 = 戻り切った）。
        /// 画だけでなく <b>AI の侵食（<c>CommsGlitchLogic</c>）もこの 1 本で消える</b>
        /// （2026-09-03・<c>canon/LEDGER.md</c> 0129）。呪いが解けた瞬間は 1 つなので時計も 1 つ。
        /// </summary>
        public float ScreenDecayReleaseK => _decay.ReleaseK;

        /// <summary>
        /// <b>呪いが解けた。視界の悪さを元へ戻す</b>（<c>canon/LEDGER.md</c> 0083）。
        /// 呼ぶのは <c>ShowControlClient.RecordVisitorMark</c> — <b>締めのカット
        /// （<c>durKind:"untilMark"</c>）が報告で進んだときだけ</b>。
        ///
        /// ⚠ 1〜2 周目で異変を消したとき（<c>MarkResult.Dismissed</c>）には呼ばない。
        /// あれは怪異を 1 つ消しただけで、呪いそのものは解けていない。
        /// </summary>
        public void ReleaseScreenDecay() => _decay.Release();

        /// <summary>
        /// 視界を黒で閉じるべきか。<b>終了</b>と<b>導入の中止</b>の 2 つで立つ。
        ///
        /// 中止で閉じるのは、トラッキング原点がずれた状態の世界を体験者に見せないため。黙って本編の
        /// 画へ切り替わると、体験者は「始まった」と思って歩き出し、<b>壁の位置が違う世界を手でたどる</b>
        /// ことになる（実物に手をぶつける）。
        ///
        /// 新しいラッチは持たない（毎フレーム導出する）。中止の唯一のラッチは
        /// <see cref="IntroDirector.Aborted"/> で、それが落ちるのは BeginIntro だけ。
        /// </summary>
        /// ⚠ <b>終幕が有効なら終了で黒は出さない。</b> 終幕は本編の画から直に始まり、
        /// <b>装置の電力を落とすことで自分で黒くなる</b>（2026-08-15・<c>canon/LEDGER.md</c> 0048）。
        /// ここで黒を重ねると、ちかちかしながら消えていく過程も、最後に出す報告の 4 行も
        /// まとめて隠れる（この面は 0.3m ＝ 全部の面のうち最も手前）。
        /// ⚠ <b>位置合わせ中は黒を出さない</b>（2026-08-09）。中止の唯一の直し方が再登録なので、
        /// 黒が居座ると<b>直す作業そのものが黒に隠れて詰む</b>（線を実物に重ねる作業なのに何も見えない）。
        /// 位置合わせを起こせるのはコントローラを持つスタッフだけなので、体験者の視界へ現実が
        /// 勝手に漏れることはない。抜ければ黒は同じ導出でそのまま戻る（ラッチを足していない）。
        public bool ShouldBlackout =>
            !(showControl != null && showControl.CourseRegistrationActive)
            && ((_logic.Phase == ShowPhase.Finished && !OutroDef.enabled)
                || (_logic.Phase == ShowPhase.Intro && _intro != null && _intro.Aborted));

        /// <summary>
        /// 黒の上に出す 1 行（空なら文字なし）。<c>ShowEndingFader</c> が描く。
        ///
        /// 終了は文字なし — 黒だけで「終わった」が伝わる。中止は「待ってよい」と伝える必要がある。
        /// 黒だけだと体験者は終わったと誤解して HMD を外し、スタッフが直す前に立ち去る。
        /// <b>理由は書かない</b>（機器の言葉を体験の中に持ち込まない）。スタッフ向けの復帰手順は StatusHud。
        /// </summary>
        public string BlackoutMessage =>
            // ⚠ 出す相手はスタッフだけ（体験者には黒しか見えない）。だから「少しお待ちください」では
            //   なく**何が起きたか**を言う。語は RecoveryGuidance と揃える（同じ事象を 2 つの
            //   名前で呼ぶと現場で照合できない）。
            _logic.Phase == ShowPhase.Intro && _intro != null && _intro.Aborted
                ? "部屋の位置がずれたので止めました" : "";

        /// <summary>
        /// 黒へ落とす時間 (秒)。終了は <see cref="EndFadeSec"/>（既定 1.5s ＝ 余韻）、中止は素早く閉じる
        /// （ずれた世界を見せている時間を短くしたい）。中止側を const にしてあるのは、SerializeField に
        /// すると既存シーンに焼かれた 0 が読まれて「一瞬で真っ黒」になるため。
        /// </summary>
        public float BlackoutFadeSec => _logic.Phase == ShowPhase.Finished ? _endFadeSec : AbortFadeSec;

        /// <summary>導入を中止したときに黒へ落とす時間 (秒)。</summary>
        private const float AbortFadeSec = 0.4f;

        /// <summary>show.json <c>run</c> を反映する（欠落ならコード既定）。</summary>
        public void Configure(ShowRunDef? def)
        {
            bool introEnabled = def?.introEnabled ?? true;
            float introMin = def != null ? def.introMinSec : ShowRunDefaults.IntroMinSec;
            bool introAuto = def?.introAutoAdvance ?? true;
            int laps = def?.ResolveTotalLaps() ?? ShowRunDefaults.TotalLaps;
            float hard = def != null ? def.hardLimitSec : ShowRunDefaults.HardLimitSec;
            float grace = def?.ResolveEndGraceSec() ?? ShowRunDefaults.EndGraceSec;
            float hold = def?.ResolveEndHoldMaxSec() ?? ShowRunDefaults.EndHoldMaxSec;
            _targetSec = def != null && def.targetSec > 0f ? def.targetSec : ShowRunDefaults.TargetSec;
            _endFadeSec = def != null && def.endFadeSec >= 0f ? def.endFadeSec : ShowRunDefaults.EndFadeSec;
            _logic.Configure(introEnabled, introMin, introAuto, laps, hard, grace, hold);
            // JsonUtility は `intro` キーが無くても「全部 0」の実体を作るので、それは未設定として扱う
            // （そのまま渡すと enabled=false に化けて導入演出が黙って出なくなる）。
            var i = def?.intro;
            IntroDef = (i == null || i.LooksUnset) ? new ShowIntroDef() : i;
            IntroDefChanged?.Invoke(IntroDef);
            // 終幕も同じ罠を踏む（キー欠落で enabled=false に化け、黙って黒で終わる）。
            var o = def?.outro;
            OutroDef = (o == null || o.LooksUnset()) ? new ShowOutroDef() : o;
            _ending.Configure(OutroDef.afterTakeId);
            OutroDefChanged?.Invoke(OutroDef);
        }

        /// <summary>show.json の終幕設定（<c>OutroDirector</c> が読む）。未設定ならコード既定。</summary>
        public ShowOutroDef OutroDef { get; private set; } = new ShowOutroDef();

        /// <summary>終幕設定が更新された（<c>OutroDirector</c> が購読して尺を入れ替える）。</summary>
        public event Action<ShowOutroDef>? OutroDefChanged;

        /// <summary>導入設定が更新された（<c>IntroDirector</c> が購読して尺を入れ替える）。</summary>
        public event Action<ShowIntroDef>? IntroDefChanged;

        /// <summary>
        /// ランを頭から始め直した（体験者交代）。**相が変わらなくても必ず発火する。**
        /// 導入演出はこれを購読して武装し直す — 慣らし歩行の途中で ▶ ラン開始を押すと
        /// 相は Intro → Intro で <see cref="PhaseChanged"/> が発火せず、
        /// **次の体験者に演出が一度も出なかった**（2026-07-30 の監査 high 7）。
        /// </summary>
        public event Action? RunRestarted;

        /// <summary>新しい体験者のランを頭から始める（導入があれば導入から）。</summary>
        public void BeginRun()
        {
            ShowRunEvent ev = _logic.BeginRun();
            // 解像度の劣化を落とす**唯一の場所**。次の体験者は今の解像度から始める。
            // 終了では落とさない（落とすと演出を見せ切っている最中に画が急に鮮明になる）。
            _decay.Reset();
            // ⚠⚠ **画の状態も落とす**（2026-08-14）。凍結（hold）・焼き付き（burn）・
            //    人形に付き従う劣化（aura）・乱れは、それまで**体験の終了でしか戻していなかった**。
            //    現場は途中で打ち切って交代することがある（気分が悪くなった / 時間が押している /
            //    演出が固まった）ので、その経路では**前の体験者の画の状態が次のランへ持ち越される**。
            _feel?.ResetAll();
            _glitch?.ResetAll();
            // ⚠⚠ **警告音の育ちも落とす**（2026-09-04・0145）。乱れの育ちと同じ理由で、
            //    落とさないと次の体験者は 1 発目から最大の警告を聞く。
            //    画にも録画にも出ないので、走行の `swAlert` の 2 つ目でしか気づけない。
            _switchCue?.ResetRun();
            // 終幕の合図も落とす。**ユーザーが「周回リセットのときにリセットされるフラグ」と
            // 名指ししたもの**（canon/LEDGER.md 0048）。落とす場所はここ 1 つ。
            _ending.ResetRun();
            ApplyGate();
            // ⚠⚠ **ゲートを閉じた「後」にもう一度武装を落とす**（2026-08-30）。
            //    ラン開始の号令元（`ShowControlClient.TriggerRunReset` / `BeginNewVisitorRunLocal`）は
            //    `timelineDirector.ResetRun()` → `RunReset?.Invoke()` → ここ、の順で走る。
            //    中央の `RunReset` は `LapCounter.ResetRun` → `SeedCurrentZone` →
            //    `CueScheduler.NotifyCameraEntered` → `TakeRunnerLogic.ArmEnterTakes` まで届くが、
            //    **その時点ではまだ前の相のゲートが開いている**（閉じるのは直上の `ApplyGate`）。
            //    ＝ 前の体験者が本編を走っている最中にラン開始を押すと、シード先のカメラに
            //    lap=1 の演出があれば**導入中に本編の演出が武装され、次フレームで画面を奪う**。
            //    カットが `untilZoneChange` ならゲートが閉じているぶんゾーン変化も届かず、
            //    watchdog（既定 45 秒）まで導入の裏で居座る。
            //    号令元の順序を入れ替える手もあるが、**導入を挟まない設定**（`introEnabled:false`）では
            //    `_logic.BeginRun()` が即 `RunBegan` を返して `OnRunBegan` → `BeginMainRun` →
            //    `RunReset` が走るので、号令元の残りと二重リセットになる。ここで落とす方が安全。
            timelineDirector?.ResetRun();
            if (ev == ShowRunEvent.RunBegan) OnRunBegan();
            NotifyPhaseIfChanged();
            RunRestarted?.Invoke();
        }

        /// <summary>導入を今すぐ終える（卓 / 現地のスタッフ操作）。</summary>
        public void RequestAdvanceIntro() => _logic.RequestAdvance();

        /// <summary>
        /// <b>人が走行中の演出を止めた</b>（卓の 📺 カメラ固定 / 手動 cue / ■ 画面を取り返す）。
        /// 終幕の合図の武装だけ落とす — 詳しい機序は <see cref="EndingCueLogic.NotifyInterrupted"/>。
        /// </summary>
        public void NotifyTakeInterrupted() => _ending.NotifyInterrupted();

        /// <summary>
        /// 慣らし歩行の計時を今から始める（導入演出が終わった合図）。<c>IntroDirector</c> が呼ぶ。
        /// これが無いと、演出の秒数が <c>introMinSec</c> を食って慣らし歩行が短くなる。
        /// </summary>
        public void RestartIntroClock() => _logic.RestartIntroClock();

        /// <summary>show.json の導入設定（<c>IntroDirector</c> が読む）。未設定ならコード既定。</summary>
        public ShowIntroDef IntroDef { get; private set; } = new ShowIntroDef();

        /// <summary>体験を今すぐ終える（卓のスタッフ操作）。</summary>
        public void RequestFinish() => _logic.RequestFinish();

        private void Awake()
        {
            ResolveRefs();
            ApplyGate();
        }

        private void OnEnable()
        {
            ResolveRefs();
            if (cueScheduler != null && !_subscribed)
            {
                cueScheduler.CameraEntered += OnCameraEntered;
                _subscribed = true;
            }
            ApplyGate();
        }

        private void OnDisable()
        {
            if (cueScheduler != null && _subscribed) cueScheduler.CameraEntered -= OnCameraEntered;
            _subscribed = false;
            // 自分が閉じたゲートを開けたまま去る（このコンポーネントを外したら従来どおり常に流れる）。
            cueScheduler?.SetShowGate(true);
        }

        private void ResolveRefs()
        {
            if (cueScheduler == null) cueScheduler = FindObjectOfType<CueScheduler>();
            if (timelineDirector == null) timelineDirector = FindObjectOfType<TimelineDirector>();
            if (showControl == null) showControl = FindObjectOfType<ShowControlClient>();
            if (switchDirector == null) switchDirector = FindObjectOfType<CameraSwitchDirector>();
            if (_bgm == null) _bgm = FindObjectOfType<BgmDirector>();
            if (_glitch == null) _glitch = FindObjectOfType<GlitchFx>();
            if (_switchCue == null) _switchCue = FindObjectOfType<SwitchAudioCue>();
            if (_feel == null) _feel = FindObjectOfType<CameraFeelFx>();
            if (_intro == null) _intro = FindObjectOfType<IntroDirector>();
        }

        // 周回は「本編の区間進行」からだけ受ける（ゲートが閉じている導入・終了では来ない）。
        //
        // ⚠⚠ 読むのは **進行の周**（progressLap・単調増加）。区間の周（lap）は逆走で戻るので、
        //   ここへ渡すと「帰りの A に着いた体験者が 1 区間引き返した瞬間に周回が 3 へ落ちて、
        //   体験が終わらなくなる」。終了判定は `lap > totalLaps` の 1 本しか無いので、
        //   ここを間違えると出口が丸ごと消える。
        private void OnCameraEntered(int camera, int lap, int progressLap) => _logic.NotifyLap(progressLap);

        private void Update()
        {
            bool atStart = AtStartZone();
            string activeTakeId = timelineDirector != null ? timelineDirector.ActiveTakeId : "";
            bool takeRunning = !string.IsNullOrEmpty(activeTakeId);

            // 終幕の合図。著作した演出（run.outro.afterTakeId）が走って、そして終わったら終わる。
            // ⚠ **これは出口を増やすだけ**で、周を走り切ったときの従来の終わり方（endGraceSec /
            //    endHoldMaxSec / hardLimitSec）は 1 つも外していない。指した演出が最後まで走らない
            //    現場でも体験は必ず終わる。
            if (_ending.Tick(_logic.Phase == ShowPhase.Run, activeTakeId))
            {
                Debug.Log($"[ShowRun] 終幕の合図（演出 {OutroDef.afterTakeId} が終わった）");
                _logic.RequestFinish();
            }
            // 演出が「終わったか」。⚠ **「進行中でない」で判定してはいけない**（2026-08-06 置き換え）。
            // 旧実装は `Active && Stage != Black` の否定＝「進行中でない」を渡していた。段 0（開始待ち）は
            // そこに含まれないので、**演出が始まる前でも「進行中でない」が成立**していた。慣らし歩行
            // （introMinSec = 20 秒）が猶予を兼ねていたため露見しなかったが、慣らしを 0 にした瞬間に
            // 体験者がスタート区間に立った時点で本編へ飛び、演出が 1 度も出なくなる。
            //
            // 併せて 2026-07-30 の実害（`Holding` を使うと段 3・段 4 の条件待ちで false へ落ち、
            // 枠が出た直後に本編へ飛んで Swap が一度も出なかった）も、「終わった」判定なら構造的に起きない。
            if (_intro == null) _intro = FindObjectOfType<IntroDirector>();
            bool introCompleted = _intro == null || _intro.Completed;

            // 中止は「終わった」ではないので Completed は立たない。別引数にしてあるのは、
            // 中止と「まだ終わっていない」を区別して現場へ出すため（StatusHud の異常 1 件・黒の 1 行）。
            bool introAborted = _intro != null && _intro.Aborted;

            ShowRunEvent ev = _logic.Tick(Time.unscaledDeltaTime, atStart, takeRunning, introCompleted,
                                          introAborted);

            // ⚠ ゲートは**イベントを配る前に**合わせる。RunBegan の処理は
            // BeginMainRun → LapCounter.ResetRun → SeedCurrentZone → CueScheduler.NotifyCameraEntered
            // を通る。ここが後だと、その進入が**まだ閉じているゲートに捨てられる**。
            // 周回は進行ポインタ方式なので、捨てられた (lap 1, course.order[0]) は二度と来ない
            //（次に order[0] へ入る時は lap 2）。結果、**スタート区間だけが 1 周目に録画されず**、
            // それを背景に使う 3 周目の録画カットが実機で無言で飛ぶ（2026-07-29 監査）。
            ApplyGate();

            if (ev == ShowRunEvent.RunBegan) OnRunBegan();
            else if (ev == ShowRunEvent.RunFinished) OnRunFinished();

            TickScreenDecay();
            NotifyPhaseIfChanged();
        }

        /// <summary>
        /// 周を重ねるごとに映像の解像度を落とす（<c>canon/LEDGER.md</c> 0012）。
        ///
        /// ⚠ <b>進めるのは本編だけ。導入と終了では値を保持する</b>（0 へ戻さない）。
        /// 導入で進めると「1 周目の最初は今くらいの解像度」が破れ、終了で戻すと
        /// 走行中の演出を見せ切っている猶予のあいだに画が急に鮮明になって「直った」ように見える。
        ///
        /// ⚠ 押す先（<see cref="CameraFeelFx"/>）は <see cref="ResolveRefs"/> で 1 回だけ引く。
        /// ここで毎フレーム <c>FindObjectOfType</c> を撃つと 90Hz でシーン全走査になる。
        /// シーンの焼き直し前の APK では居ないことがあるが、そのときは何も起きないだけ
        /// （＝従来どおりの画）で、居ないことは実機ログの <c>coarseMat=-</c> に出る。
        /// </summary>
        private void TickScreenDecay()
        {
            _decay.Tick(Time.unscaledDeltaTime, _logic.Phase == ShowPhase.Run,
                        _logic.Lap, _logic.TotalLaps, _logic.LapElapsedSec);
            // ⚠⚠ 画へ書くのは **Shown**（呪いが解けたら 0 へ戻る側）。生の Progress は
            //    音と AI の侵食が読む — そちらまで戻すと「直った」を音で宣言することになる。
            _feel?.SetCoarseBlocks(_decay.ShownBlocks);
            // 色が抜けるのも**同じ進み**。1 周目は暖色、3 周目の A で完全な無彩
            // （`canon/LEDGER.md` 0019）。別の時計で動かすと、装置として説明の付かない絵になる。
            // ⚠ 戻すときも同じ 1 本なので、解像度と色は必ず一緒に戻る。
            _feel?.SetMono(_decay.Shown);
        }

        private bool AtStartZone()
        {
            if (switchDirector == null || !switchDirector.TryGetCurrentZoneCamera(out int cam)) return false;
            int[]? order = showControl != null ? showControl.CourseOrder : null;
            // 順路が未著作なら「どこに居ても導入は終われる」（現地で order を書き忘れて詰むのを避ける）。
            if (order == null || order.Length == 0) return true;
            return cam == order[0];
        }

        private void ApplyGate() => cueScheduler?.SetShowGate(_logic.GateOpen);

        private void OnRunBegan()
        {
            Debug.Log("[ShowRun] 導入を終えて本編へ（周回 1 から）");
            // **録画の世代は切り替えない。** ここを録画のリセット系統に載せると、ラン開始が複数系統に増え、
            // 遅れて届いた runEpoch が 1 周目の録画を消す経路ができる（3 周目の素材が消える）。
            showControl?.BeginMainRun();
        }

        private void OnRunFinished()
        {
            Debug.Log($"[ShowRun] 体験の終了（{_logic.Lap - 1} 周 / 経過 {_logic.RunElapsedSec:F0} 秒）");
            timelineDirector?.AbortActive();
            _glitch?.ResetAll();
            // 凍結・焼き付きも畳む。終わったのに画が止まったままだと、暗転が始まっても
            // 「まだ何か起きるのか」に見えて終わりが伝わらない。
            _feel?.ResetAll();
            if (_bgm != null) _bgm.StopAll(_endFadeSec > 0f ? _endFadeSec : ShowRunDefaults.EndFadeSec);
        }

        private void NotifyPhaseIfChanged()
        {
            if (_logic.Phase == _lastPhase) return;
            _lastPhase = _logic.Phase;
            PhaseChanged?.Invoke(_lastPhase);
        }
    }
}
