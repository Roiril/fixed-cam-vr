#nullable enable
using System;
using System.Text;
using FixedCamVr.Streaming;
using FixedCamVr.Streaming.Cg;
using FixedCamVr.Streaming.Recording;
using FixedCamVr.Tracking;
using UnityEngine;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// 体験 1 回ぶんを外から観測して <c>[XP]</c> タグの 1 行 1 イベントで吐く**観測専用**コンポーネント。
    /// 実機（Quest）で走らせたログを親エージェントが adb logcat で拾い、
    /// 「体験が著作どおりに起きたか」「映像が安定していたか」「砂嵐が多すぎないか」を機械判定するための一次証拠。
    ///
    /// <b>体験の挙動には一切干渉しない。</b> 既存コンポーネントの公開イベント購読と読み取り専用の
    /// ポーリングだけで組み立ててあり、状態を書き換える呼び出しは 1 つも無い。だから本番ビルドに
    /// 残しても害が無く、次の現地テストでもそのまま使える（計装のために本番コードを汚さない）。
    ///
    /// 出す行は 2 種類:
    ///   - <b>遷移</b>: 起きた瞬間に 1 行（phase / zone / screen / seg / take / step / cue / intro / storm / rec / bgm）
    ///   - <b>サマリ</b>: <see cref="SummaryIntervalSec"/> 秒ごとに 1 行（fps・遅延・受信 fps・砂嵐の累計）
    ///
    /// <b>「状態が進んだ」ではなく「効果が出た」を観測する。</b> 2026-07-31、この計装が
    /// 「FAIL ゼロ・演出 7 本すべて OK」と判定した走行の画を録ったら、<b>導入演出が 1 段も画に
    /// 出ていなかった</b>（パススルーの初期化失敗 / シェーダのビルド剥がれ / カメラ背景が不透明が
    /// 同時に起きていた）。状態機械の遷移は完璧に進んでいて、「その段で画に何かが実際に出たか」を
    /// 1 つも観測していなかったのが原因。以後、遷移だけの観測項目を足さない —
    /// <c>veil</c> / <c>pt</c> / <c>bg</c> / <c>wire</c> / <c>ovl</c> / <c>cg</c> / <c>bgm</c> /
    /// <c>font</c> / <c>ev=step</c> はすべてこの「効果の実在」を出すための項目。
    ///
    /// 形式は <c>key=value</c> の空白区切りに固定する。解析スクリプトが正規表現ではなく
    /// 素直な split で読めるようにするため（現地で崩れたログを手で読む時にも効く）。
    ///
    /// <b>Development ビルドでのみ動く</b>（<see cref="Debug.isDebugBuild"/>）。Release では
    /// GameObject すら作らないので、体験本番のフレーム予算には 1 命令も乗らない。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShowTelemetryHost : MonoBehaviour
    {
        /// <summary>ログの先頭タグ。解析側（tools/analyze-xp-log.py）と対。</summary>
        public const string Tag = "[XP]";

        /// <summary>サマリ行の間隔 (秒)。短くすると logcat が溢れ、長くすると一過性の乱れを取り逃す。</summary>
        private const float SummaryIntervalSec = 2f;

        /// <summary>参照解決のリトライ間隔 (秒)。ShowControlClient が実行時に生む物（録画係）を拾い直す。</summary>
        private const float ResolveRetrySec = 1f;

        /// <summary>「強い砂嵐」と数える <see cref="SignalLostFx.Level"/> の閾値。</summary>
        private const float StormOnLevel = 0.5f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!Debug.isDebugBuild) return;
            // logcat のリングバッファを食い潰していた主犯は本文ではなく **Debug.Log に付くスタックトレース**
            // （実測: 265 秒の走行で 3,941 行 179KB が別行として出ていた）。情報量はほぼ無いのに、
            // これが原因で走行の前半が丸ごと落ちた回がある。Warning / Error のスタックは原因究明に
            // 要るので残す。Development ビルドでしか通らないので本番の挙動は変わらない。
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
            var go = new GameObject("[XPTelemetry]");
            DontDestroyOnLoad(go);
            go.AddComponent<ShowTelemetryHost>();
        }

        // --- 観測対象（すべて読み取り専用で触る）---
        private CameraStreamRegistry? _registry;
        private CameraSwitchDirector? _switch;
        private ShowRunDirector? _run;
        private IntroDirector? _intro;
        private WalkGuide? _guide;
        private OutroDirector? _outro;
        private OutroReport? _report;
        private CommsPanel? _comms;
        // **コントローラの位置が取れているか**の唯一の観測点（下の ctrlL / ctrlR）。
        // 左は連絡の面が受け取っている（報告の押し方をそこへ出すため）。
        private ControllerGuidePanel? _guidePanel;
        private TitleScreen? _title;
        private TimelineDirector? _timeline;
        private SignalLostFx? _signal;
        private GlitchFx? _glitch;
        private CameraFeelFx? _feel;
        private CueScheduler? _cues;
        private SegmentRecorder? _recorder;
        private ScreenOverlayController? _overlay;
        private LapCounter? _lap;
        private CourseFrame? _frame;
        private ShowControlClient? _show;
        private IntroVeil? _veil;
        private IntroStructureWire? _wire;
        private ContainmentShell? _shell;
        private BgmDirector? _bgm;
        private ShowSoundDirector? _sound;
        private SwitchAudioCue? _switchSfx;
        private ShowCgLayer? _cg;
        private AnomalyEyes? _eyes;
        private TakeRunner? _takes;

        // --- 購読状態（多重購読を防ぐ）---
        private bool _subSwitch, _subRun, _subCues, _subRegistry, _subTakes, _subRec;

        // --- 遷移検出のための前回値 ---
        private IntroStage _lastStage = IntroStage.Off;
        private WalkGuideStage _lastGuideStage = WalkGuideStage.Off;

        /// <summary>
        /// 終幕の段（Off/Flicker/Dark/Report/Done）。<b>2026-08-15 まで 1 行も出していなかった。</b>
        /// 終幕は「装置が力尽きて報告を出す」だけの演出なので、<b>画に出たかどうかを見る手が
        /// テレメトリしか無い</b>（消えていく過程は録画では「暗い」としか読めない）。
        /// </summary>
        private OutroStage _lastOutroStage = OutroStage.Off;
        private bool _outroSeen;

        /// <summary>
        /// タイトルの段（Off/Wait/In/Hold/Out/Done）。<b>2026-08-14 まで 1 つも観測していなかった</b> —
        /// 体験の入口そのものなのに、出たかどうかがログから分からなかった。
        /// </summary>
        private TitleStage _lastTitleStage = TitleStage.Off;
        private bool _titleSeen;

        /// <summary>直近に出した記録ボタンの回数（体験者の左 X）。</summary>
        private int _lastMarkCount;
        private int _lastCommsPulse;
        private string _lastTakeId = "";
        private string _lastCtrlMode = "";
        private string _lastCueId = "";
        private bool _lastStormOn;
        private bool _lastTrackingFrozen;
        private bool _lastGateOpen = true;
        private string _lastConfig = "";
        private string _lastBgmTrack = "";
        private bool _lastBgmPlaying;
        private SoundCue _lastSoundCue = SoundCue.None;
        private int _lastSpotCount;
        private bool _soundWarned;

        // 再生中の録画（記録側とは別系統）。閉じるときに「画に出た枚数」を出すために参照を持つ。
        private RecordedFramePlayer? _lastRecPlayer;
        private int _recPlayLap = -1;
        private int _recPlayCam = -1;

        // --- サマリ集計 ---
        private float _resolveAccum;
        private float _summaryAccum;
        private int _frames;
        private float _dtSum;
        private float _dtMax;
        private float _stormSec;      // 強い砂嵐（配信断）の累計
        private float _weakSec;       // 弱い砂嵐（トラッキング/pause 明け）の累計
        private float _liveSec;       // 観測している総時間
        private readonly StringBuilder _sb = new StringBuilder(512);

        private static float Now => Time.realtimeSinceStartup;

        private void Start()
        {
            Resolve();
            // bg / font は**シーンとアセットに焼かれた値**なので、コードを直しても走行のたびに変わらない。
            // 起動時に 1 回出せば足りるし、ここで出さないと「実機でだけ画が出ない」原因に辿り着けない。
            Emit($"ev=boot build=dev dev={SystemInfo.deviceModel} rate={DisplayRateInfo.CurrentHz:F0} " +
                 $"bg={DescribeCameraBackgrounds()} font={(JapaneseHudFont.TryGet() != null ? 1 : 0)}");
        }

        private void OnDisable() => Unsubscribe();

        private void Update()
        {
            float udt = Time.unscaledDeltaTime;
            _frames++;
            _dtSum += udt;
            if (udt > _dtMax) _dtMax = udt;
            _liveSec += udt;

            // 参照は遅延解決（ShowControlClient が record.enabled を見て録画係を後から生む）。
            _resolveAccum += udt;
            if (_resolveAccum >= ResolveRetrySec)
            {
                _resolveAccum = 0f;
                Resolve();
                PollConfig();  // 毎フレームだと DescribeConfig の文字列生成が無駄になるのでここで
            }

            PollTransitions(udt);

            _summaryAccum += udt;
            if (_summaryAccum >= SummaryIntervalSec)
            {
                _summaryAccum = 0f;
                EmitSummary();
            }
        }

        // ---------------------------------------------------------------- 参照解決 / 購読

        private void Resolve()
        {
            if (_registry == null) _registry = FindObjectOfType<CameraStreamRegistry>();
            if (_switch == null) _switch = FindObjectOfType<CameraSwitchDirector>();
            if (_run == null) _run = FindObjectOfType<ShowRunDirector>();
            if (_intro == null) _intro = FindObjectOfType<IntroDirector>();
            if (_guide == null) _guide = FindObjectOfType<WalkGuide>();
            if (_outro == null) _outro = FindObjectOfType<OutroDirector>();
            if (_report == null) _report = FindObjectOfType<OutroReport>();
            if (_comms == null) _comms = FindObjectOfType<CommsPanel>();
            if (_guidePanel == null) _guidePanel = FindObjectOfType<ControllerGuidePanel>();
            if (_title == null) _title = FindObjectOfType<TitleScreen>();
            if (_timeline == null) _timeline = FindObjectOfType<TimelineDirector>();
            if (_signal == null) _signal = FindObjectOfType<SignalLostFx>();
            if (_glitch == null) _glitch = FindObjectOfType<GlitchFx>();
            if (_feel == null) _feel = FindObjectOfType<CameraFeelFx>();
            if (_cues == null) _cues = FindObjectOfType<CueScheduler>();
            if (_recorder == null) _recorder = FindObjectOfType<SegmentRecorder>();
            if (_overlay == null) _overlay = FindObjectOfType<ScreenOverlayController>();
            if (_lap == null) _lap = FindObjectOfType<LapCounter>();
            if (_frame == null) _frame = FindObjectOfType<CourseFrame>();
            if (_show == null) _show = FindObjectOfType<ShowControlClient>();
            if (_veil == null) _veil = FindObjectOfType<IntroVeil>();
            if (_wire == null) _wire = FindObjectOfType<IntroStructureWire>();
            if (_shell == null) _shell = FindObjectOfType<ContainmentShell>();
            if (_bgm == null) _bgm = FindObjectOfType<BgmDirector>();
            if (_cg == null) _cg = FindObjectOfType<ShowCgLayer>();
            if (_eyes == null) _eyes = FindObjectOfType<AnomalyEyes>();
            if (_takes == null) _takes = FindObjectOfType<TakeRunner>();
            if (_sound == null) _sound = FindObjectOfType<ShowSoundDirector>();
            if (_switchSfx == null) _switchSfx = FindObjectOfType<SwitchAudioCue>();

            if (!_subTakes && _takes != null)
            {
                _takes.StepResolved += OnStepResolved;
                _subTakes = true;
            }
            if (!_subSwitch && _switch != null)
            {
                _switch.ZoneCommitted += OnZoneCommitted;
                _switch.SwitchCommitted += OnSwitchCommitted;
                _subSwitch = true;
            }
            if (!_subRun && _run != null)
            {
                _run.PhaseChanged += OnPhaseChanged;
                _subRun = true;
                Emit($"ev=phase v={_run.Phase} laps={_run.TotalLaps} target={_run.TargetSec:F0}");
            }
            if (!_subCues && _cues != null)
            {
                _cues.CameraEntered += OnCameraEntered;
                _subCues = true;
            }
            if (!_subRegistry && _registry != null)
            {
                _registry.ActiveChanged += OnActiveChanged;
                _subRegistry = true;
            }
            if (!_subRec && _recorder != null)
            {
                _recorder.SegmentOpened += OnRecSegmentOpened;
                _recorder.SegmentClosed += OnRecSegmentClosed;
                _subRec = true;
                // 録画係は show.json 適用時に生まれるので、解決が遅れて最初の開始を取り逃すことがある。
                if (_recorder.IsRecording) OnRecSegmentOpened(_recorder.CurrentLap, _recorder.CurrentCamera);
            }
        }

        private void Unsubscribe()
        {
            if (_subSwitch && _switch != null)
            {
                _switch.ZoneCommitted -= OnZoneCommitted;
                _switch.SwitchCommitted -= OnSwitchCommitted;
            }
            if (_subRun && _run != null) _run.PhaseChanged -= OnPhaseChanged;
            if (_subCues && _cues != null) _cues.CameraEntered -= OnCameraEntered;
            if (_subRegistry && _registry != null) _registry.ActiveChanged -= OnActiveChanged;
            if (_subTakes && _takes != null) _takes.StepResolved -= OnStepResolved;
            if (_subRec && _recorder != null)
            {
                _recorder.SegmentOpened -= OnRecSegmentOpened;
                _recorder.SegmentClosed -= OnRecSegmentClosed;
            }
            _subSwitch = _subRun = _subCues = _subRegistry = _subTakes = _subRec = false;
        }

        // ---------------------------------------------------------------- イベント

        // ショーの時計（人の居場所）。画面が演出で塞がっていても進むのが正しい挙動。
        private void OnZoneCommitted(int camera)
            => Emit($"ev=zone cam={camera} lap={(_lap != null ? _lap.CurrentLap : -1)}");

        // 画面の切替（誰が画面を握ったか）。src=Zone/Manual/Override/Insert/External。
        private void OnSwitchCommitted(int camera, CameraSwitchDirector.SwitchSource src)
            => Emit($"ev=screen cam={camera} src={src}");

        // 区間の進入（周回つき）。演出の武装・区間 post / BGM・録画の駆動点と同じ首。
        //
        // ⚠ **周は 2 つ出す。** `lap` = 区間の周（逆走で戻る・演出の区間キー）／
        //   `plap` = 進行の周（単調増加・終了判定）。食い違っていたら体験者が引き返している。
        //   1 つしか出さないと「引き返して演出が再演された」と「著作が二重に置かれている」を
        //   走行のログから区別できない。
        private void OnCameraEntered(int camera, int lap, int progressLap)
            => Emit($"ev=seg lap={lap} cam={camera} plap={progressLap}");

        private void OnPhaseChanged(ShowPhase phase)
            => Emit($"ev=phase v={phase} lap={(_run != null ? _run.Lap : -1)} " +
                    $"elapsed={(_run != null ? _run.RunElapsedSec : 0f):F1}");

        private void OnActiveChanged(int index) => Emit($"ev=active cam={index}");

        /// <summary>
        /// 演出のカット 1 つが画面を取ったか、飛ばされたか。<b>この計装の要</b>。
        ///
        /// §6.4 の規約で全カットが飛んだ演出は画面を掴まずに終わるが、<c>ActiveTakeId</c> は立つので
        /// <c>ev=take st=begin</c> → <c>st=end</c> が正常に出て、解析は「演出 OK」と判定する。
        /// 実際には何も起きていない。2026-07-31 の事故と完全に同型なので、カットの粒度で出す。
        /// </summary>
        private void OnStepResolved(string takeId, int stepIndex, string source, int camera,
                                    bool played, string why)
            => Emit($"ev=step take={(string.IsNullOrEmpty(takeId) ? "?" : takeId)} i={stepIndex} " +
                    $"src={source} cam={camera} played={(played ? 1 : 0)} why={why}");

        // ---------------------------------------------------------------- 効果の実在（読み取りだけ）

        /// <summary>
        /// OVRCameraRig のカメラ背景が透明か（<c>透明なカメラ数/全カメラ数</c>）。
        ///
        /// Underlay パススルーは「アプリが描かない画素」(alpha 0) にしか出ない。既定の不透明な黒のままだと
        /// パススルーを有効にしても<b>アプリが上から塗り潰して何も見えない</b>（2026-07-31 実害）。
        /// <b>シーンに焼かれた値なのでコードを直しても変わらない</b>類の事故なので、起動時に必ず出す。
        /// </summary>
        private static string DescribeCameraBackgrounds()
        {
            Camera[] cams = Camera.allCameras;
            int clear = 0;
            for (int i = 0; i < cams.Length; i++)
            {
                Camera c = cams[i];
                if (c == null) continue;
                if (c.clearFlags == CameraClearFlags.SolidColor && c.backgroundColor.a <= 0.004f) clear++;
            }
            return $"{clear}/{cams.Length}";
        }

        /// <summary>導入の覆いが描画状態か。<c>-</c>=シーンに居ない / <c>0</c>=非描画 / <c>1</c>=描画中。</summary>
        private string VeilState => _veil == null ? "-" : (_veil.IsActive ? "1" : "0");

        // 頭の高さ（床基準）を出すためだけの参照。Camera.main は毎回タグ検索するのでキャッシュする。
        private Transform? _head;
        private Transform? HeadTransform
        {
            get
            {
                if (_head == null)
                {
                    Camera? c = Camera.main;
                    _head = c != null ? c.transform : null;
                }
                return _head;
            }
        }

        /// <summary>覆いの実体を組めたか。<c>0</c> なら <c>Shader.Find</c> が null ＝ 枠は一生出ない。</summary>
        private string VeilBuiltState => _veil == null ? "-" : (_veil.IsBuilt ? "1" : "0");

        /// <summary>
        /// スクリーンの管の点灯（<c>_CrtIgnite</c>）に<b>実際に書いた値</b>。
        ///
        /// <c>-</c> = 導入の実行体がシーンに居ない / <c>nc</c> = <b>書く先の材質を掴めていない</b>
        /// （＝ 段 3 の「闇の中で管が点く」は一生出ない）。段 Ignite / Live を通っているのに
        /// 0.00 のままなら、進みは配っているのに画だけが変わっていない。
        ///
        /// ⚠ 既定は 1（点いている）。演出を出していない間ここが 0 なら画がまるごと消えている。
        /// </summary>
        private string IgniteState => _intro == null
            ? "-"
            : (_intro.IgniteWritten < 0f ? "nc" : _intro.IgniteWritten.ToString("F2"));

        /// <summary>
        /// スクリーンの電力（<c>_ScreenPower</c>）に<b>実際に書いた値</b>。
        ///
        /// <c>-</c> = 終幕の実行体がシーンに居ない / <c>nc</c> = <b>書く先の材質を掴めていない</b>
        /// （＝ ちかちかしながら消える過程は一生画に出ない）。段が Flicker を通っているのに
        /// ずっと 1.00 なら、重みは動いているのに画は明るいまま。
        /// </summary>
        private string PowerState => _outro == null
            ? "-"
            : (_outro.PowerWritten < 0f ? "nc" : _outro.PowerWritten.ToString("F2"));

        /// <summary>
        /// 報告の面が<b>組めたか</b>（<c>-</c> = 面がシーンに居ない）。false なら日本語フォントか
        /// TMP の実体を組めていて<b>いない</b> ＝ 終幕の最後に 1 文字も出ない。
        /// <b>気づける口がここしかない</b>（黒の中で何も出ないだけなので画では区別できない）。
        /// </summary>
        private string ReportBuiltState => _report == null ? "-" : (_report.IsBuilt ? "1" : "0");

        /// <summary>
        /// AIエージェントからの連絡が<b>いまどう画に出ているか</b>。<c>off</c> ＝ 出していない、
        /// それ以外は <c>&lt;段&gt;/&lt;文字の濃さ&gt;/&lt;枠の開き&gt;</c>。
        /// ⚠ 段だけを出すと「進んでいるのに 1 画素も出ていない」を見逃す。
        /// </summary>
        /// <summary>
        /// コントローラの <c>&lt;繋がっている&gt;/&lt;位置が取れている&gt;</c>。面が居なければ <c>-</c>。
        /// <b>2 つ目が 0 なら手元の面は出ていない</b>（アンカーが原点へ飛ぶので出す方が害）。
        /// </summary>
        private static string ControllerState(bool? connected, bool? tracked) =>
            connected == null || tracked == null
                ? "-"
                : (connected.Value ? "1" : "0") + "/" + (tracked.Value ? "1" : "0");

        private string CommsState => _comms == null ? "-"
            : _comms.Stage == CommsStage.Off ? "off"
            : $"{_comms.Stage}/{_comms.AppliedGlyph:F2}/{_comms.AppliedOpen:F2}";

        /// <summary>
        /// 演出が終わった理由（<c>ev=take st=end why=</c>）。
        /// <c>done</c> = 著作どおり流し切った / <c>wd</c> = watchdog の強制終了 /
        /// <c>yield</c> = 体験者が区間を移って打ち切った / <b><c>mark</c> = 体験者が報告して消した</b>。
        /// </summary>
        private string TakeEndReason => _timeline == null ? "-" : _timeline.LastEndReason switch
        {
            TakeRunnerLogic.EndReason.Watchdog => "wd",
            TakeRunnerLogic.EndReason.Yielded => "yield",
            TakeRunnerLogic.EndReason.VisitorDismissed => "mark",
            _ => "done",
        };

        /// <summary>報告の面に<b>実際に書いた不透明度</b>（<c>nc</c> = 組めていない）。</summary>
        private string ReportAlphaState => _report == null
            ? "-"
            : (_report.AppliedAlpha < 0f ? "nc" : _report.AppliedAlpha.ToString("F2"));

        /// <summary>
        /// 破砕（段 4）が<b>画に出たか</b>。2026-08-13 に段ごと廃止して観測から外していたが、
        /// 2026-08-15 に段が戻ったので戻した（<c>canon/LEDGER.md</c> 0044）。
        ///
        /// ⚠ 箱の側（旧 <c>shatB</c> / <c>shatBC</c> / <c>shatBMesh</c>）は戻していない —
        /// 封印の箱を退避したので、割れるのは覆いだけ。
        ///
        /// <c>-</c>=覆いがシーンに居ない / 0.00 のまま = 進みは配っているのに画が割れていない。
        /// </summary>
        private string ShatterState => _veil == null ? "-" : _veil.ShatterPeak.ToString("F2");

        /// <summary>破砕のセル数。<c>0</c> ならセル格子を組めていない ＝ 割れようがない。</summary>
        private string ShatterCellState => _veil == null ? "-" : _veil.ShatterCells.ToString();

        /// <summary>吸い込み先の矩形（半幅,半高,奥行,吸い込み半径）。全部 0 なら行き先が解けていない。</summary>
        private string ShatterRectState => _veil == null ? "-" : _veil.ShatterRectDesc;

        /// <summary>段 3 の構造の線の本数。<c>-</c>=シーンに居ない。</summary>
        private string WireState => _wire == null ? "-" : _wire.LineCount.ToString();

        /// <summary>隔離殻が実際に黒を書いているか。<c>-</c>=シーンに居ない / 0=非描画 / 1=描画中。</summary>
        private string ShellState => _shell == null ? "-" : (_shell.IsActive ? "1" : "0");

        /// <summary>殻の実体を組めたか。<c>0</c> なら <c>Shader.Find</c> が null ＝ 隔離は一生出ない。</summary>
        private string ShellBuiltState => _shell == null ? "-" : (_shell.IsBuilt ? "1" : "0");

        /// <summary>「見てよいもの」の箱の数。<c>0</c> なら幾何が未著作 ＝ 隔離を出しようがない。</summary>
        private string ShellBoxState => _shell == null ? "-" : _shell.BoxCount.ToString();

        /// <summary>隔離が実物の壁と床を見せているか（0 = 真っ黒）。導入は 0 が正常。</summary>
        /// <summary>掴めた音源 / 掴めなかった音源。**右が 0 でなければ音は設計どおりに出ていない。**</summary>
        private string SoundBuiltState =>
            _sound == null ? "-" : $"{_sound.ClipsResolved}/{_sound.ClipsMissing}";

        /// <summary>いま実際に書いている音量の合計。**0 なら無音**（指示がいくら正しくても）。</summary>
        private string SoundAudibleState =>
            _sound == null ? "-" : _sound.AudibleSum.ToString("F2");

        /// <summary>部屋の帯域（kHz）。隔離が閉じると下がる ＝ 隔離の「効果の実在」。</summary>
        private string SoundCutoffState =>
            _sound == null ? "-" : (_sound.RoomCutoffHz / 1000f).ToString("F1");

        /// <summary>
        /// 周ごとの環境音の取り分（`0.00/1.00/0.00`）。**入れ替わりはここにしか出ない** —
        /// 二乗の和が常に 1 なので、合計を見ている <c>sndAud</c> は 1 ビットも動かない。
        /// </summary>
        private string SoundAmbientState
        {
            get
            {
                if (_sound == null) return "-";
                SoundBedGains g = _sound.Bed;
                return $"{g.roomLap1:F2}/{g.roomLap2:F2}/{g.roomLap3:F2}";
            }
        }

        private string ShellRevealState => _shell == null ? "-" : (_shell.Revealing ? "1" : "0");

        // ⚠ 封印の箱（旧 <c>box</c> / <c>boxBuilt</c>）は 2026-08-15 に観測から外した。
        //   箱を退避して重み `sealBox` を全段 0 にしたので**常に 0 が並ぶだけ**になり、
        //   「出るはずのものが出ない」と誤検出させる材料にしかならない
        //   （2026-08-13 に破砕を外したときと同じ判断）。実装は Attic に残っている。

        /// <summary>パススルーをアプリから有効化できているか。<c>-</c>=読み口が無い / -1=判定不能 / 0=無効 / 1=有効。</summary>
        private string PassthroughState
        {
            get
            {
                // ⚠ `_show?.` は C# の null（Unity の破棄済みオブジェクトを見逃す）。
                //    破棄後にフィールドを触ると MissingReferenceException になるので Unity の == で見る。
                if (_show == null) return "-";
                Func<int>? f = _show.PassthroughStateProvider;
                return f == null ? "-" : f().ToString();
            }
        }

        /// <summary>いま画面へ書いている合成の重み（素材が実際に混ざっているか）。</summary>
        private float OverlayStrength => _overlay != null ? _overlay.Strength : 0f;

        /// <summary>合成のマテリアルを掴めているか。<c>-</c>=シーンに居ない / 0=未解決 / 1=解決済み。</summary>
        private string OverlayMaterial => _overlay == null ? "-" : (_overlay.HasMaterial ? "1" : "0");

        /// <summary>CG 人形が実際に描画されているか。<c>-</c>=シーンに居ない。</summary>
        private string CgState => _cg == null ? "-" : (_cg.IsVisible ? "1" : "0");

        /// <summary>闇に開く目 — <c>&lt;組めたか&gt;/&lt;開いている数&gt;/&lt;不透明度&gt;</c>。</summary>
        private string EyesState => _eyes == null
            ? "-"
            : $"{(_eyes.IsBuilt ? 1 : 0)}/{_eyes.OpenCount}/{_eyes.AppliedFade:F2}";

        /// <summary>
        /// 歩行誘導（<c>canon/LEDGER.md</c> 0079）。<b>組めたか / 山形の数 / 矢印 / 輪</b>。
        ///
        /// ⚠ <b>「段が進んだ」ではなく「画に出た」の側。</b> 組めていなければ（シェーダがビルドから
        /// 剥がれていれば）1 画素も出ないのに段だけは進む。⚠ <b>山形が 0</b> なら円だけが出ている
        /// （道筋が短すぎた ＝ 矢印が引けていない）。
        /// </summary>
        private string GuideState => _guide == null
            ? "-"
            : $"{(_guide.IsBuilt ? 1 : 0)}/{_guide.ChevronCount}/" +
              $"{_guide.AppliedArrow:F2}/{_guide.AppliedRing:F2}";

        /// <summary>
        /// 解像度の劣化を書く先（スクリーンの Renderer のマテリアル）を掴めているか。
        /// <c>-</c>=CameraFeelFx がシーンに居ない / 0=掴めていない ＝ **進みが動いても画は変わらない** / 1=書ける。
        /// </summary>
        private string CoarseMaterialState => _feel == null ? "-" : (_feel.HasMaterial ? "1" : "0");

        // ---------------------------------------------------------------- 設定の出所

        /// <summary>実機がどの設定で走っているかを出す。**これが無いと解析が嘘をつく。**
        ///
        /// 解析（tools/analyze-xp-log.py）は PC の show.json を期待値にして「出るはずで出なかった演出」を
        /// 引き算で見つける。ところが実機が読むのは端末キャッシュ（persistentDataPath/show_config.json）で、
        /// これは**焼き込みより優先される**。実測（2026-07-31）で 2 台の Quest のキャッシュを比べたら
        /// 片方にだけ <c>run.intro.startLineId</c> が無く、導入の始まり方が機ごとに違っていた。
        /// その状態で解析すると「導入の演出が出なかった」と報告されるが、原因はコードではなく設定のずれ。
        ///
        /// 設定が変わるのは起動直後の数秒（焼き込み → キャッシュ → ライブ）なので、変化した時だけ出す。</summary>
        private void PollConfig()
        {
            if (_show == null) return;
            string cfg = _show.DescribeConfig();
            if (cfg == _lastConfig) return;
            _lastConfig = cfg;
            Emit($"ev=config {cfg}");
        }

        // ---------------------------------------------------------------- ポーリング遷移

        private void PollTransitions(float udt)
        {
            // 導入演出の段（Off/Black/Real/Degrade/Structure/Frame/Swap/Done）
            if (_intro != null && _intro.Stage != _lastStage)
            {
                _lastStage = _intro.Stage;
                IntroWeights w = _intro.Weights;
                // fresh / centered は段 4 → 段 5 の進行条件。false のまま足踏みすると
                // 最後の段（枠の中がカメラ映像へ変わる）が出ないので、必ず一緒に出す。
                // veil / veilBuilt / wire / pt / shat は「重みが動いた」ではなく「画に出た」の側。
                // 段だけ見て OK と判定した 2026-07-31 の事故を繰り返さないため必ず一緒に出す。
                Emit($"ev=intro stage={_lastStage} hold={(_intro.Holding ? 1 : 0)} " +
                     $"fresh={(_intro.LiveFresh ? 1 : 0)} centered={(_intro.FrameCentered ? 1 : 0)} " +
                     $"pass={w.passthrough:F2} live={w.live:F2} frame={w.frame:F2} edge={w.edge:F2} " +
                     $"veil={VeilState} veilBuilt={VeilBuiltState} wire={WireState} pt={PassthroughState} " +
                     $"shell={ShellState} shellBuilt={ShellBuiltState} shellBox={ShellBoxState} " +
                     $"shellRev={ShellRevealState} " +
                     // 破砕は段 4 のあいだしか動かない。**遷移の瞬間の値**なので、
                     // 意味を持つのは Frame → Swap の行（そこに段 4 の到達点が載る）。
                     $"shat={ShatterState} shatC={ShatterCellState} shatRect={ShatterRectState} " +
                     // 管は導入の全段で 1（点いていて、まだ何も映していない）。0 が出たら画が消えている。
                     $"ignite={IgniteState} " +
                     // 開始の門。**auth=0 のまま段 0 に居るのは正常**（人がまだ A を押していない）。
                     // 段 1 以降の行に auth=0 が出たら、卓の ⏭ で始まったということ。
                     $"auth={(_show != null && _show.StartAuthorized ? 1 : 0)}");
            }

            // 歩行誘導の段（Off/SpotIn/Trail/Hold/Arrive/Out/Done。canon/LEDGER.md 0079）。
            // ⚠ **画に出た側を必ず一緒に出す** — built=0 なら 1 画素も出ていないのに段は進む。
            //   spot は円の course 座標（導出か著作かで場所が変わるので、実際に使った値を残す）。
            //   ⚠ st=Out で to=1 なら「着かないまま諦めて従来の開始判定へ戻した」。
            if (_guide != null && _guide.Stage != _lastGuideStage)
            {
                _lastGuideStage = _guide.Stage;
                Emit($"ev=guide st={_lastGuideStage} built={(_guide.IsBuilt ? 1 : 0)} " +
                     $"path={(_guide.HasPath ? 1 : 0)} chev={_guide.ChevronCount} " +
                     $"arrow={_guide.AppliedArrow:F2} ring={_guide.AppliedRing:F2} " +
                     $"spot={_guide.Spot.x:F2},{_guide.Spot.y:F2} r={_guide.RadiusM:F2} " +
                     $"auth={(_guide.SpotAuthored ? 1 : 0)} to={(_guide.TimedOut ? 1 : 0)} " +
                     // told=0 のまま矢印が出ていたら、**保険（12 秒）で出た** ＝ 説明と対になっていない
                     // （連絡の面が組めない現場で起きる。画からは区別できないのでここにしか出ない）。
                     $"told={(_guide.Told ? 1 : 0)}");
            }

            // 終幕の段（Off/Flicker/Dark/Report/Done）。**画に出た側**を必ず一緒に出す —
            // pw は実際に材質へ書いた電力（nc なら掴めていない ＝ 一生ちかちかしない）、
            // rep / repBuilt は報告の面（built=0 なら最後の 4 行が 1 文字も出ない）。
            // marks は報告の数そのもの（○○ に入る値が正しいかは、これでしか確かめられない）。
            if (_outro != null && (!_outroSeen || _outro.Stage != _lastOutroStage))
            {
                _outroSeen = true;
                _lastOutroStage = _outro.Stage;
                Emit($"ev=outro stage={_lastOutroStage} pw={PowerState} " +
                     $"rep={ReportAlphaState} repBuilt={ReportBuiltState} " +
                     // repChars = 報告を打ち切るまでに鳴る打鍵の数（改行を除く字数）。
                     // 解析器が `ev=sum` の `repTypeN` と突き合わせる（`canon/LEDGER.md` 0063）。
                     // ⚠ **打鍵は段 Done より後まで続く**（打つ尺 > reportFadeSec）ので、
                     //   到達したかを見るのは段の行ではなく `ev=sum` の側。
                     $"repChars={(_report == null ? -1 : _report.ReportChars)} " +
                     $"repSfx={(_report == null ? "-" : _report.TypeSfxBuilt ? "1" : "0")} " +
                     // comms はAIエージェントからの連絡。built=0 なら一生出ない。段が Off 以外のあいだの
                     // glyph / open は**実際に書いた値** ＝ 画に出た側（`ev=comms` は縁しか持たない）。
                     $"comms={CommsState} commsBuilt={(_comms == null ? "-" : _comms.IsBuilt ? "1" : "0")} " +
                     // ⚠ 周回の壊れ（commsGl / commsGlMat）は **`ev=sum` の側**に出す。
                     //   ここ（`ev=outro`）は終幕の縁でしか出ないので、判定の材料にならない。
                     $"marks={(_show != null ? _show.VisitorMarkCount : -1)} " +
                     // 合図（run.outro.afterTakeId）が武装したか / 撃ったか。
                     // 著作していなければ両方 0 のままで、終わり方は従来どおり。
                     $"armed={(_run != null && _run.EndingArmed ? 1 : 0)} " +
                     $"cue={(_run != null && _run.EndingFired ? 1 : 0)}");
            }

            // 体験者の記録ボタン（左 X）。**押した時刻が残ると、3 周目の反転に気づいたかが
            // 訊かずに分かる**（初見は消耗品なので、誘導せずに取れる観測の価値が高い）。
            // 区間を併記するのは「どこで押したか」が無いと後から読めないため。
            if (_show != null && _show.VisitorMarkCount != _lastMarkCount)
            {
                _lastMarkCount = _show.VisitorMarkCount;
                Emit($"ev=mark n={_lastMarkCount} lap={(_run != null ? _run.Lap : -1)} " +
                     $"cam={(_switch != null && _switch.TryGetCurrentZoneCamera(out int mc) ? mc : -1)}"
                     // ⚠⚠ **その報告で解除が通ったか**（2026-08-17・`canon/LEDGER.md` 0082）。
                     //    連絡の面の文面がこれで分かれる。2026-08-17 まではキーが `take=`
                     //    ＝「演出が走っていたか」で、3 周目の入れ替わりでも 1 が立っていた。
                   + $" res={(_show.LastMarkResolved ? 1 : 0)}");
            }

            // AIエージェントからの連絡（`canon/LEDGER.md` 0054）。**1 通ごとに 1 行**。
            // ⚠ id だけでは足りない — 「配った」と「画に出た」は別物なので glyph / open を必ず添える
            //   （2026-07-31 の「段は進んだのに画は空だった」と同じ型）。built=0 なら一生出ない。
            if (_comms != null && _comms.PulseCount != _lastCommsPulse)
            {
                _lastCommsPulse = _comms.PulseCount;
                Emit($"ev=comms id={_comms.LastNotice} n={_lastCommsPulse} " +
                     $"built={(_comms.IsBuilt ? 1 : 0)} lap={(_run != null ? _run.Lap : -1)} " +
                     // chars = この文面で鳴るはずの打鍵の数（改行を除く）。
                     // 解析器が合計と `typeN` を突き合わせる（`canon/LEDGER.md` 0056）。
                     $"chars={_comms.NoticeChars} " +
                     $"sfx={(_comms.TypeSfxBuilt ? 1 : 0)} " +
                     // この 1 通が届いたときの周回の進み（`canon/LEDGER.md` 0068）。
                     // ⚠ 強さではなく**進み**を出す — 強さは発作で跳ねるので、
                     //   1 通ごとの比較には使えない（3 周目の連絡が軽く見えることがある）。
                     $"decay={_comms.DecayProgress:F2} " +
                     $"wait={(_timeline != null && _timeline.IsWaitingForVisitorMark ? 1 : 0)}");
            }

            // タイトルの段。**体験の入口なのに 2026-08-14 まで 1 行も出していなかった。**
            // built は「実体を組めたか」、veil / glyph は**実際に書いた不透明度** ＝ 画に出た側。
            // built=0 だと A を押しても何も起きず、導入だけが素通しで始まる（気づける口がここしかない）。
            if (_title != null && (!_titleSeen || _title.Stage != _lastTitleStage))
            {
                _titleSeen = true;
                _lastTitleStage = _title.Stage;
                Emit($"ev=title stage={_lastTitleStage} built={(_title.IsBuilt ? 1 : 0)} " +
                     $"veil={_title.AppliedVeil:F2} glyph={_title.AppliedGlyph:F2} " +
                     // 譲っている間（位置合わせ・ステータス表示）は段が Wait のままでも黒は 0。
                     // これが無いと判定が「Wait なのに黒が立っていない」と誤検出する。
                     $"yield={(_title.Yielding ? 1 : 0)}");
            }

            // コントローラの操作モード（NORMAL / REG）。**位置合わせが起きたかの唯一の観測点**。
            // `pt` を併記するのは「モードが変わった」だけでなく「現実が実際に出たか」まで見るため
            // （状態の遷移だけ出して画が死んでいた 2026-07-31 の事故と同型を作らない）。
            string ctrlMode = _show != null ? _show.ControllerMode : "";
            if (ctrlMode != _lastCtrlMode)
            {
                _lastCtrlMode = ctrlMode;
                Emit($"ev=ctrlmode v={(string.IsNullOrEmpty(ctrlMode) ? "?" : ctrlMode)} pt={PassthroughState}");
            }

            // 演出（Take）の出入り。
            // ⚠⚠ **終わり方の理由を必ず添える。** 理由が無いと「著作どおり終わった」「体験者が報告して
            //    消した」「壊れて打ち切られた」が尺の長短でしか区別できず、**報告で畳む機構が
            //    効かなくても効きすぎてもログから判別できない**（2026-08-17 監査）。
            string takeId = _timeline != null ? _timeline.ActiveTakeId : "";
            if (takeId != _lastTakeId)
            {
                if (!string.IsNullOrEmpty(_lastTakeId))
                    Emit($"ev=take id={_lastTakeId} st=end why={TakeEndReason}");
                if (!string.IsNullOrEmpty(takeId)) Emit($"ev=take id={takeId} st=begin");
                _lastTakeId = takeId;
            }

            // 画面に出ている素材（cue）。演出のカットが素材へ切り替わった瞬間が見える。
            // ⚠ Current は動画の Prepare 完了**前**に代入されるので、これだけでは「素材が来た」証明にならない。
            //    合成の重み ovl（シェーダの _OverlayStrength そのもの）を必ず添える。
            string cueId = _overlay != null && _overlay.Current != null ? (_overlay.Current.id ?? "?") : "";
            if (cueId != _lastCueId)
            {
                if (!string.IsNullOrEmpty(_lastCueId))
                    Emit($"ev=cue id={_lastCueId} st=off ovl={OverlayStrength:F2} ovlMat={OverlayMaterial}");
                if (!string.IsNullOrEmpty(cueId))
                    Emit($"ev=cue id={cueId} st=on ovl={OverlayStrength:F2} ovlMat={OverlayMaterial}");
                _lastCueId = cueId;
            }

            // BGM が実際に鳴っているか。クリップ取得に失敗すると BgmDirector は**無音のまま黙って戻る**ので、
            // 指示（区間 / 演出）の側をいくら見ても音の不在は分からない。
            if (_bgm != null)
            {
                string trk = _bgm.CurrentTrackId;
                bool playing = _bgm.IsPlaying;
                if (trk != _lastBgmTrack || playing != _lastBgmPlaying)
                {
                    _lastBgmTrack = trk;
                    _lastBgmPlaying = playing;
                    Emit($"ev=bgm trk={(string.IsNullOrEmpty(trk) ? "-" : trk)} play={(playing ? 1 : 0)}");
                }
            }

            // 節目の音。**画と違って録画には映らない**ので、鳴らしたことをログにしか残せない。
            // ⚠ ここは「鳴らした」であって「聞こえた」ではない。音量 0 でも通る点に注意
            //    （音量そのものは ev=sum の sndAud が持つ）。
            if (_sound != null)
            {
                int spots = _sound.SpotCount;
                if (spots != _lastSpotCount || _sound.LastCue != _lastSoundCue)
                {
                    if (spots > _lastSpotCount)
                        Emit($"ev=sfx id={_sound.LastCue} n={spots} " +
                             $"bed={_sound.Bed.device + _sound.Bed.deviceWorn:F2} " +
                             $"duck={_sound.Bed.duck:F2}");
                    _lastSpotCount = spots;
                    _lastSoundCue = _sound.LastCue;
                }
                // 音源を 1 本でも掴めていなければ **1 回だけ** 名指しで言う。
                // 黙って無音になるのがこの層でいちばん起きやすく、いちばん気づけない壊れ方。
                if (!_soundWarned && _sound.ClipsMissing > 0)
                {
                    _soundWarned = true;
                    Emit($"ev=sfx id=MISSING n={_sound.ClipsMissing} bed=0.00 duck=0.00");
                }
            }

            // 砂嵐（配信断＝強 / トラッキング明け＝弱）。累計は「多すぎないか」の直接の答えになる。
            if (_signal != null)
            {
                float lv = _signal.Level;
                if (lv > StormOnLevel) _stormSec += udt;
                else if (lv > 0.01f) _weakSec += udt;

                bool on = lv > StormOnLevel;
                if (on != _lastStormOn)
                {
                    _lastStormOn = on;
                    Emit($"ev=storm v={(on ? "on" : "off")} lvl={lv:F2} " +
                         $"cam={(_registry != null ? _registry.ActiveIndex : -1)}");
                }

                bool frozen = _signal.TrackingFrozen;
                if (frozen != _lastTrackingFrozen)
                {
                    _lastTrackingFrozen = frozen;
                    Emit($"ev=track v={(frozen ? "lost" : "ok")}");
                }
            }

            // 区間進行のゲート（導入・終了で閉じる single choke point）。
            if (_cues != null && _cues.ShowGateOpen != _lastGateOpen)
            {
                _lastGateOpen = _cues.ShowGateOpen;
                Emit($"ev=gate v={(_lastGateOpen ? "open" : "closed")}");
            }

            TickRecordingPlayback();
        }

        // 端末内録画（3 周目の素材はここが録れていないと黙って飛ぶ）。
        //
        // ⚠ 出すのは**録画対象の (周, カメラ)** で、画面に映っているカメラではない
        //    （演出中は食い違う。旧実装は registry.ActiveIndex を出しており誤りだった）。
        // ⚠ ポーリングでは取れない（2026-08-14）。切り替え後も postSec 秒は前の区間を録り続けるので
        //    **開始と終了が入れ子になり**、しかも枚数・バイト数は閉じた瞬間にしか確定しない。
        //    ポーリングのままだと閉じた区間が frames=0 に見えて、解析器が偽の FAIL を出す。
        private void OnRecSegmentOpened(int lap, int cam)
            => Emit($"ev=rec v=start lap={lap} cam={cam} bytes=0 mb={RecMB():F1}");

        // start= は**録り始めの線が効いたか**（1 = 線の横断から / 0 = 末尾方式）。
        // ⚠ 枚数では区別できない — 末尾方式でも枚数は出るので、
        //   「線を指したのに効いていない」はここでしか分からない（`canon/LEDGER.md` 0061）。
        private void OnRecSegmentClosed(int lap, int cam, int frames, long bytes)
            => Emit($"ev=rec v=stop lap={lap} cam={cam} bytes={bytes} frames={frames} "
                  + $"start={(_recorder != null && _recorder.LastSegmentStarted ? 1 : 0)} "
                  + $"mb={RecMB():F1}");

        private double RecMB() => _recorder != null ? _recorder.RunBytes / 1048576.0 : 0.0;

        // 録画の**再生**（記録側の ev=rec とは別系統）。
        //
        // 「録れた」と「画に出た」は別で、暗い現場では目視で区別できない。ファイルを開けただけの
        // カットは絵が 1 枚も出ないまま尺を消費する（ログ上は演出が走ったように見える）ので、
        // 閉じるときに **実際にテクスチャへ載せた枚数**（presented）と輝度を出す。
        private void TickRecordingPlayback()
        {
            if (_timeline == null) return;
            RecordedFramePlayer? cur = _timeline.ActiveRecording;
            bool active = cur != null;

            if (active && !ReferenceEquals(cur, _lastRecPlayer))
            {
                // 直前の録画を閉じずに別のを開いた（カットが連続している）。先に閉じる。
                if (_lastRecPlayer != null) EmitRecPlayClose();
                _lastRecPlayer = cur;
                _recPlayLap = _timeline.ActiveRecordingLap;
                _recPlayCam = _timeline.ActiveRecordingCamera;
                Emit($"ev=recplay v=open lap={_recPlayLap} cam={_recPlayCam} " +
                     $"frames={cur!.FrameCount} dur={cur.DurationSec:F1}");
            }
            else if (!active && _lastRecPlayer != null)
            {
                EmitRecPlayClose();
            }
        }

        private void EmitRecPlayClose()
        {
            RecordedFramePlayer? p = _lastRecPlayer;
            _lastRecPlayer = null;
            if (p == null) return;
            // Dispose 済みでもマネージドなカウンタは読める（テクスチャには触らない）。
            Emit($"ev=recplay v=close lap={_recPlayLap} cam={_recPlayCam} " +
                 $"presented={p.PresentedCount} failed={p.FailedCount} luma={p.LastLuma:F2}");
        }

        // ---------------------------------------------------------------- サマリ

        private void EmitSummary()
        {
            float fps = _dtSum > 0f ? _frames / _dtSum : 0f;
            float worstMs = _dtMax * 1000f;
            _frames = 0; _dtSum = 0f; _dtMax = 0f;

            _sb.Clear();
            _sb.Append("ev=sum");
            _sb.Append(" fps=").Append(fps.ToString("F1"));
            _sb.Append(" worst=").Append(worstMs.ToString("F0"));

            if (_run != null)
            {
                _sb.Append(" phase=").Append(_run.Phase);
                _sb.Append(" lap=").Append(_run.Lap);
                _sb.Append(" run=").Append(_run.RunElapsedSec.ToString("F0"));
                if (_run.Phase == ShowPhase.Intro)
                    _sb.Append(" intro=").Append(_run.IntroElapsedSec.ToString("F0"));
                if (_run.EndHolding) _sb.Append(" endhold=1");
            }
            if (_intro != null && _intro.Active)
            {
                _sb.Append(" istage=").Append(_intro.Stage);
                // 段の遷移（ev=intro）は 1 瞬の値しか持たない。線は Apply の中で組まれるので、
                // 遷移の瞬間はまだ 0 本のことがある。段に居るあいだの実数はここでしか取れない。
                _sb.Append(" wire=").Append(WireState);
                // 破砕も同じ理由。段 4 の途中の到達点はここでしか取れない。
                _sb.Append(" shat=").Append(ShatterState);
            }

            int active = _registry != null ? _registry.ActiveIndex : -1;
            _sb.Append(" cam=").Append(active);
            if (_switch != null)
            {
                _sb.Append(" dip=").Append(_switch.Dipping ? 1 : 0);
                // 画面の状態: i=演出/インサートが占有 o=卓が固定 p=ゾーン切替が保留中
                _sb.Append(" frz=");
                _sb.Append(_switch.InsertActive ? "i" : "-");
                _sb.Append(_switch.OverrideActive ? "o" : "-");
                _sb.Append(_switch.SwitchSuppressed ? "p" : "-");
            }
            if (!string.IsNullOrEmpty(_lastTakeId)) _sb.Append(" take=").Append(_lastTakeId);
            if (!string.IsNullOrEmpty(_lastCueId)) _sb.Append(" cue=").Append(_lastCueId);
            if (_glitch != null && _glitch.Level > 0.005f)
                _sb.Append(" glitch=").Append(_glitch.Level.ToString("F2"));
            // 乱れが起きるたびに大きくなる（`canon/LEDGER.md` 0055）。
            // ⚠ glN は「起きた回数」、glE は「いまの大きくなり具合 0..1」。
            //   level だけ見ても**台本が強いのか回数で育ったのかを区別できない**ので対で出す。
            //   0 回のあいだは出さない（本編前は常に 0 で、行が伸びるだけ）。
            if (_glitch != null && _glitch.Count > 0)
                _sb.Append(" glN=").Append(_glitch.Count)
                   .Append(" glE=").Append(_glitch.Escalation01.ToString("F2"));
            // **報告で実際に演出が消えた回数**（`canon/LEDGER.md` 0050「報告したらそれらが消え」）。
            // ⚠ 押した回数（`marks=`）とは別物 — 消せる演出の方が少ないので、
            //   marks だけ見ても「機構が効いたか」は 1 ビットも分からない。0 のあいだは出さない。
            if (_timeline != null && _timeline.DismissCount > 0)
                _sb.Append(" disN=").Append(_timeline.DismissCount);
            // **途中で切れた演出を出し直した回数**（体験者が引き返して同じ区間へ戻った・2026-08-17）。
            // ⚠ 引き返しそのものは `ev=seg` の `lap` と `plap` の食い違いに出るが、
            //   **それが再演へ繋がったか**はここにしか出ない（画では「演出が出た」としか見えない）。
            if (_timeline != null && _timeline.ReplayCount > 0)
                _sb.Append(" reN=").Append(_timeline.ReplayCount);

            // --- 効果の実在（「段が進んだ」ではなく「画・音に出たか」）---
            // ここが全部揃っていても遷移は正常に見える、という壊れ方を 2026-07-31 に踏んだ。
            _sb.Append(" veil=").Append(VeilState);
            _sb.Append(" veilBuilt=").Append(VeilBuiltState);
            _sb.Append(" pt=").Append(PassthroughState);
            // 隔離は「重みが動いた」だけでは画に出ない（幾何が無い / 未登録 / シェーダが剥がれた の 3 経路で
            // 黙って消える）。**実際に黒を書いたか**と**箱の数**を対で出す。
            _sb.Append(" shell=").Append(ShellState);
            _sb.Append(" shellBuilt=").Append(ShellBuiltState);
            _sb.Append(" shellBox=").Append(ShellBoxState);
            _sb.Append(" shellRev=").Append(ShellRevealState);
            // 管の点灯は**導入の外でも必ず出す**。既定は 1（点いている）で、演出が終わった後に
            // 0 が残っていたら画がまるごと消えている ＝ ここでしか気づけない。
            _sb.Append(" ignite=").Append(IgniteState);
            // 終幕の電力。**演出の外では 1.00** なので、本編中にこれが下がっていたら画が暗い理由がここ。
            _sb.Append(" pw=").Append(PowerState);
            _sb.Append(" cg=").Append(CgState);
            //   eyes = 闇に開く目（`canon/LEDGER.md` 0075）。
            //   **`<実体を組めたか>/<いま開いている目の数>/<画に出た不透明度>`** の 3 つ組。
            //   ⚠ 3 つとも要る — シェーダが剥がれた（0/…）／カットが 1 度も指していない（1/0/0.00）／
            //     指したのに 1 画素も出ていない（1/0/1.00 ＝ 座席表を組めていない）は**別の壊れ方**。
            //   ⚠ 数は「重みを配った」ではなく**何個ぶんの目が実際に開いているか**（画に出た側）。
            _sb.Append(" eyes=").Append(EyesState);
            //   guide = 歩行誘導（canon/LEDGER.md 0079）。組めたか/山形の数/矢印/輪。
            //   ⚠ 段 0 のあいだしか動かない。**本編で 0 以外が出たら畳み忘れ**。
            _sb.Append(" guide=").Append(GuideState);
            // 音は**録画にも映らない**ので、実在の観測はここにしか無い。
            //   sndBuilt = 掴めた音源 / 掴めなかった音源（0 でなければ設計どおりに鳴っていない）
            //   sndAud   = いま AudioSource へ書いている音量の合計（**0 なら無音**）
            //   sndLpf   = 部屋の帯域（隔離が閉じると下がる。隔離の実在の観測）
            //   sfxN/swN = 一撃と切替の累計
            _sb.Append(" sndBuilt=").Append(SoundBuiltState);
            _sb.Append(" sndAud=").Append(SoundAudibleState);
            _sb.Append(" sndLpf=").Append(SoundCutoffState);
            //   sndAmb = 周ごとの環境音の取り分（1 周目/2 周目/3 周目）。**入れ替わったかの唯一の証拠**
            //            — 合計は常に一定なので `sndAud` には出ない（canon/LEDGER.md 0049）
            _sb.Append(" sndAmb=").Append(SoundAmbientState);
            //   sndDolls = 人形の笑いの音量（`canon/LEDGER.md` 0066）。**報告を押すまでループ**
            //              するので、一撃のログ（ev=sfx）には出ない。鳴ったかはここにしか出ない。
            _sb.Append(" sndDolls=").Append(_sound == null ? "-" : _sound.DollsGain.ToString("F2"));
            //   sndSwap  = 入れ替わった人形の笑いの音量（`canon/LEDGER.md` 0086）。3 周目 A の
            //              入れ替わりで立ち、C を出るまで鳴り続ける。**これも一撃のログには出ない**
            //   sndSwell = 笑う人形の増え具合 0..1（C に居るあいだ増える）。**音量とは別に出す** —
            //              合計だけ見ても「増えた」のか「大きくなった」のか分けられない
            _sb.Append(" sndSwap=").Append(_sound == null ? "-" : _sound.DollSwapGain.ToString("F2"));
            _sb.Append(" sndSwell=").Append(_sound == null ? "-" : _sound.DollSwellNow.ToString("F2"));
            //   sndScore = 劇伴（HorrBGM）の取り分（`canon/LEDGER.md` 0088）。**リセット後の黒だけ 1**で
            //              題字が立つと退く。BgmDirector が鳴らす音なので `sndAud`（敷く音の合計）には
            //              1 ビットも出ない — 本編で劇伴が黙っている証拠はこのキーだけ。
            _sb.Append(" sndScore=").Append(_sound == null ? "-" : _sound.ScoreGain.ToString("F2"));
            _sb.Append(" sfxN=").Append(_sound == null ? "-" : _sound.SpotCount.ToString());
            _sb.Append(" swN=").Append(_switchSfx == null
                                       ? "-"
                                       : (_switchSfx.HasClips ? _switchSfx.PlayedCount.ToString() : "nc"));
            //   typeN = 連絡の面の打鍵の累計（`canon/LEDGER.md` 0056）。**出た文字数と対で見る** —
            //           `nc` は音源を掴めていない ＝ 字は出るのに無音。
            //           解析器が「連絡 n 通ぶんの字数の合計」と突き合わせて FAIL にする。
            _sb.Append(" typeN=").Append(_comms == null
                                         ? "-"
                                         : (_comms.TypeSfxBuilt ? _comms.TypedCount.ToString() : "nc"));
            //   commsGl / commsCx = 連絡の面の周回の壊れ（`canon/LEDGER.md` 0068 / 0069）。
            //   **強さ**と、**いま実際に化けている字の数**を対で出す。
            //   ⚠ 0069 で壊れ方を作り直した（レイヤを貼る → 印字そのものが壊れる）ので、
            //     「書く先を掴めたか（commsGlMat）」は無くなった。**画に出た側の観測は commsCx**。
            //   ⚠⚠ **ここは `ev=sum` でなければならない。** 2026-08-17 に `ev=outro` へ足してしまい、
            //   終幕の 5 標本にしか出ず、解析器の判定に 1 度も入らなかった（走行で気づいた）。
            _sb.Append(" commsGl=").Append(_comms == null ? "-" : _comms.GlitchLevel.ToString("F2"));
            _sb.Append(" commsCx=").Append(_comms == null ? "-" : _comms.CorruptedChars.ToString());
            //   commsBg = 地と縁を組めたか。**0 なら文字と壊れだけが宙に浮く。**
            //   ⚠⚠ 2026-08-17 まで実機がまさにこれだった（`Unlit/Color` がビルドから剥がれていた）。
            //   Editor では出るので、この 1 ビットが無いと永久に気づけない。
            _sb.Append(" commsBg=").Append(_comms == null ? "-" : (_comms.PanelBuilt ? "1" : "0"));
            //   commsFace = AIエージェントの顔（`canon/LEDGER.md` 0071 / 0073）。
            //   **`<枠を組めたか>/<版の枚数 0..2>/<画に出た濃さ>/<侵食の進み>`** の 4 つ組。
            //   ⚠ 4 つとも要る — 枠のシェーダが剥がれた（0/…）／版が Resources に無い（1/0/…）／
            //     人形の版だけ無い（1/1/…＝ **3 周目でも顔が変わらない**）／
            //     どれも在るのに 1 度も濃さが乗らない（1/2/0.00/…）は**別の壊れ方**で、直し方も違う。
            //   ⚠⚠ 4 つ目は `commsGl` と**同じ値のはず**。食い違ったら配線が壊れている
            //     （画に出た側の観測なので、掛け違えるとここだけ 0 のまま残る）。
            _sb.Append(" commsFace=").Append(_comms == null
                ? "-"
                : $"{(_comms.AvatarBuilt ? 1 : 0)}/{_comms.FaceArtCount}/" +
                  $"{_comms.AppliedFace:F2}/{_comms.AppliedFaceMix:F2}");
            //   repTypeN / repShown = 終幕の報告の打鍵の累計と、いま画に出ている文字数
            //           （`canon/LEDGER.md` 0063）。**対で出す** — 片方だけだと
            //           「字は出たのに無音」と「音は鳴ったのに字が出ていない」を区別できない。
            //           打ち切るまでの尺は段 Report より長いので、到達の判定はここでしか取れない。
            _sb.Append(" repTypeN=").Append(_report == null
                                            ? "-"
                                            : (_report.TypeSfxBuilt ? _report.TypedCount.ToString() : "nc"));
            _sb.Append(" repShown=").Append(_report == null ? "-" : _report.VisibleChars.ToString());
            //   ctrlL / ctrlR = コントローラの <繋がっている>/<位置が取れている>。
            //   ⚠⚠ **2 つ目が 0 のとき、手元の面（報告の押し方・操作早見表）は出ない。**
            //   2026-08-16 まで接続しか見ておらず、位置が無効なコントローラのアンカーが
            //   トラッキング原点（床の中心）へ飛ぶせいで、面が「遠くに小さく」出ていた。
            //   実機でしか起きず、画にも音にも出ないので、**ここが唯一の手掛かり**。
            _sb.Append(" ctrlL=").Append(ControllerState(_comms == null ? null
                                                         : (bool?)_comms.LeftConnected,
                                                         _comms == null ? null
                                                         : (bool?)_comms.LeftTracked));
            _sb.Append(" ctrlR=").Append(ControllerState(_guidePanel == null ? null
                                                         : (bool?)_guidePanel.ControllerConnected,
                                                         _guidePanel == null ? null
                                                         : (bool?)_guidePanel.ControllerTracked));
            // 周回で進む解像度の劣化（canon/LEDGER.md 0012）。
            // **進みだけ出しても意味が無い** — 書く先を掴めていなければ画は 1 画素も変わらないので、
            // 「実際に書いたブロック数」と「書く先があるか」を対で出す。
            if (_run != null)
            {
                // ⚠⚠ **生（coarse）と画に出た側（coarseShown）は別物**（`canon/LEDGER.md` 0083）。
                //    呪いが解けると画だけが 0 へ戻り、生は単調のまま — 音（装置の声の痩せ）と
                //    AI の侵食が生を読んでいるので、そちらを戻すと「直った」を音で宣言してしまう。
                //    **対で出さないと「解けたのか / そもそも劣化していないのか」が走行から読めない。**
                _sb.Append(" coarse=").Append(_run.ScreenDecay.ToString("F2"));
                _sb.Append(" coarseShown=").Append(_run.ScreenDecayShown.ToString("F2"));
                _sb.Append(" cbx=").Append(_run.ScreenDecayBlocks.ToString("F0"));
                _sb.Append(" coarseMat=").Append(CoarseMaterialState);
                if (_run.ScreenDecayReleased) _sb.Append(" coarseRel=1");
            }
            if (_overlay != null)
                _sb.Append(" ovl=").Append(OverlayStrength.ToString("F2"))
                   .Append(" ovlMat=").Append(OverlayMaterial);
            // 左右分割と第 2 の差し替え層（canon/LEDGER.md 0050）。**カットの中でしか書かれない**ので、
            // 「出たか」と「残っていないか」の両方をここで見る。演出が終わった後に spl が 0 でなければ
            // 画が割れたまま次の体験者へ持ち越されている（この codebase が 4 回踏んだ固着の型）。
            // 第 2 層は素材を非同期で読むので、カットが指しただけでは載った証拠にならない。
            if (_feel != null)
                _sb.Append(" spl=").Append(_feel.SplitX.ToString("F2"))
                   .Append(" ovl2=").Append(_feel.Overlay2Strength.ToString("F2"));
            if (_bgm != null)
                _sb.Append(" bgm=").Append(_bgm.IsPlaying ? 1 : 0)
                   .Append(" bgmTrk=").Append(string.IsNullOrEmpty(_bgm.CurrentTrackId) ? "-" : _bgm.CurrentTrackId);

            // 砂嵐は「いまのレベル」と「累計の割合」を両方出す。体感の判定は割合の方が効く。
            if (_signal != null)
            {
                _sb.Append(" storm=").Append(_signal.Level.ToString("F2"));
                _sb.Append(" stormPct=").Append((_liveSec > 0f ? _stormSec / _liveSec * 100f : 0f).ToString("F1"));
                _sb.Append(" weakPct=").Append((_liveSec > 0f ? _weakSec / _liveSec * 100f : 0f).ToString("F1"));
            }
            if (_recorder != null)
                // recPost=1 は「カメラが切り替わった後も前の区間を録り続けている」＝ 追い録りが実際に走った証拠。
                // 見ないと、Update が回っていない / postSec が 0 に化けている等で黙って無くなる。
                _sb.Append(" rec=").Append(_recorder.IsRecording ? 1 : 0)
                   .Append(" recPost=").Append(_recorder.IsPostRolling ? 1 : 0)
                   .Append(" recMB=").Append((_recorder.RunBytes / 1048576.0).ToString("F1"));
            // 録画を再生中なら、いま何枚目まで画に出したか（尺だけ進んで絵が出ていないのを見分ける）。
            if (_lastRecPlayer != null)
                _sb.Append(" recPlay=").Append(_lastRecPlayer.PresentedCount)
                   .Append('/').Append(_lastRecPlayer.FrameCount);
            if (_frame != null)
            {
                _sb.Append(" reg=").Append(_frame.HasRegistration ? 1 : 0);
                // 床の高さ。regv<2 は「測っていない登録」で、ワイヤーや人形が沈む原因になる。
                _sb.Append(" regv=").Append(_frame.RegSchema);
                _sb.Append(" floorY=").Append(_frame.FloorY.ToString("F2"));
                // 頭が床から何 m にあるか。1.2〜2.0 の外なら床の基準がおかしい。
                Transform? head = HeadTransform;
                if (head != null)
                    _sb.Append(" headY=").Append(_frame.HeightAboveFloor(head.position.y).ToString("F2"));
            }

            // 画像加工が実際に画面へ効いているか。mat=0 なら material 未解決＝加工は 1 つも出ていない。
            if (_show != null)
            {
                _sb.Append(" mat=").Append(_show.HasScreenMaterial ? 1 : 0);
                PostParams? p = _show.AppliedPost;
                if (p == null) _sb.Append(" post=none");
                else
                    _sb.Append(" post=exp").Append(p.exposure.ToString("F2"))
                       .Append("/con").Append(p.contrast.ToString("F2"))
                       .Append("/sat").Append(p.saturation.ToString("F2"))
                       .Append("/vig").Append(p.vignette.ToString("F2"))
                       .Append("/scan").Append(p.scanline.ToString("F2"))
                       .Append("/grain").Append(p.grain.ToString("F2"));
            }

            // カメラごとの受信品質。ここが体験の土台（映像が安定しているか）。
            if (_registry != null)
            {
                for (int i = 0; i < _registry.Count; i++)
                {
                    CameraStream? s = _registry.Get(i);
                    if (s == null) continue;
                    _sb.Append(" |c").Append(i);
                    _sb.Append(" con=").Append(s.IsConnected ? 1 : 0);
                    _sb.Append(" rx=").Append(s.ReceivedFps.ToString("F1"));
                    StreamHealth? h = s.Health;
                    _sb.Append(" tx=").Append(h != null ? h.fps.ToString("F1") : "-");
                    _sb.Append(" drop=").Append(s.DroppedFrames);
                    LatencyEstimatorLogic lat = s.Latency;
                    _sb.Append(" jit=").Append(lat.ArrivalJitterMs.ToString("F0"));
                    _sb.Append(" dec=").Append(lat.DecodeMs.ToString("F0"));
                    _sb.Append(" age=").Append(lat.SourceAgeMs.ToString("F0"));
                    if (h != null && h.IsThrottling) _sb.Append(" thr=1");
                }
            }
            Emit(_sb.ToString());
        }

        private static void Emit(string body) => Debug.Log($"{Tag} t={Now:F2} {body}");
    }
}
