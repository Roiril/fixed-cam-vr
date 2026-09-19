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
        /// <summary>
        /// ステータス表示（右 B）。⚠⚠ <b>画にも音にも出ない</b>ので、体験者の走行中に業務表示が
        /// 出ていたかは <c>ev=status</c> と <c>ev=sum</c> の <c>hud*</c> にしか残らない。
        /// </summary>
        private StatusHud? _statusHud;
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
        /// <summary>ホラー軽減モードの音（2026-09-05・<c>canon/LEDGER.md</c> 0154）。</summary>
        private HorrorReliefAudio? _relief;
        private SwitchAudioCue? _switchSfx;
        private ShowCgLayer? _cg;
        private SwapMorphFx? _swap;

        /// <summary>持続の覆い（0102）の縁を出すためのラッチ。</summary>
        private int _lastWrapCount;
        private bool _lastWrapHolding;

        // 入れ替わりのノイズ（canon/LEDGER.md 0089）。回数の変化で 1 行出す。
        private int _lastSwapCount;
        private bool _lastSwapActive;
        private AnomalyEyes? _eyes;
        private ScreenOsd? _osd;
        private TakeRunner? _takes;

        // --- 購読状態（多重購読を防ぐ）---
        private bool _subSwitch, _subRun, _subCues, _subRegistry, _subTakes, _subRec, _subClip;

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

        /// <summary>電源断が画へ出た最大値（<c>clMax</c>）。<b>段の縁だけでは取れない</b>。</summary>
        private float _collapseMax;
        private bool _collapseSeen;

        /// <summary>
        /// タイトルの段（Off/Wait/In/Hold/Out/Done）。<b>2026-08-14 まで 1 つも観測していなかった</b> —
        /// 体験の入口そのものなのに、出たかどうかがログから分からなかった。
        /// </summary>
        private TitleStage _lastTitleStage = TitleStage.Off;
        private bool _titleSeen;

        /// <summary>直近に出した記録ボタンの回数（体験者の左のどれか）。</summary>
        private int _lastMarkCount;
        private int _lastCommsPulse;
        private int _lastCommsCurseRamp;
        private string _lastCommsPossessPhase = "Off";
        // 塗り替わりのあいだに画へ書いた乱れの最大（`canon/LEDGER.md` 0231）。段の縁で出し、読ませる段で 0 へ戻す。
        private float _commsTearMax;
        private int _commsTornMax;
        private float _lastCommsInvasion = -1f;
        // 締めの線を締めのカットの中で踏んだ縁（③a の引き金・`canon/LEDGER.md` 0233）。
        private bool _lastClosingLine;
        private string _lastTakeId = "";
        // 目の視界ジャックの縁検出（canon/LEDGER.md 0099）。
        private bool _lastJackActive;
        private int _jackShownAtBegin;
        private string _lastCtrlMode = "";

        // ステータス表示（右 B）の縁。`sec` を出すために「出た時刻」も持つ。
        private bool _lastHudVisible;
        private float _hudShownAt;
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
            // id = この機の端末 ID の頭 6 桁（SystemInfo.deviceUniqueIdentifier）。卓の visitor.html で
            //   役 α/β と結ぶときに「どの機がどれか」を見分ける唯一の手掛かり（0185）。
            //   heartbeat は全桁を名乗るが、logcat には頭だけで足りる。
            string devId = SystemInfo.deviceUniqueIdentifier ?? "";
            Emit($"ev=boot build=dev dev={SystemInfo.deviceModel} id={(devId.Length > 6 ? devId.Substring(0, 6) : devId)} " +
                 $"rate={DisplayRateInfo.CurrentHz:F0} " +
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

            // 電源断は 0.9 秒で終わるうえ `ev=outro` は段の縁でしか出ないので、
            // **画へ出た最大値をここで拾う**（`clMax`・`canon/LEDGER.md` 0111）。
            if (_outro != null && _outro.CollapseWritten >= 0f)
            {
                _collapseSeen = true;
                if (_outro.CollapseWritten > _collapseMax) _collapseMax = _outro.CollapseWritten;
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
            if (_statusHud == null) _statusHud = FindObjectOfType<StatusHud>();
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
            if (_swap == null) _swap = FindObjectOfType<SwapMorphFx>();
            if (_eyes == null) _eyes = FindObjectOfType<AnomalyEyes>();
            if (_osd == null) _osd = FindObjectOfType<ScreenOsd>();
            if (_takes == null) _takes = FindObjectOfType<TakeRunner>();
            if (_sound == null) _sound = FindObjectOfType<ShowSoundDirector>();
            if (_relief == null) _relief = FindObjectOfType<HorrorReliefAudio>();
            if (_switchSfx == null) _switchSfx = FindObjectOfType<SwitchAudioCue>();

            if (!_subTakes && _takes != null)
            {
                _takes.StepResolved += OnStepResolved;
                _subTakes = true;
            }
            if (!_subClip && _overlay != null)
            {
                _overlay.ClipLatency += OnClipLatency;
                _subClip = true;
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
            if (_subClip && _overlay != null) _overlay.ClipLatency -= OnClipLatency;
            _subClip = false;
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

        /// <summary>
        /// 動画カットが発火してから画に出るまでの内訳。<b>短いカットを連続で差し替える演出の要</b>。
        ///
        /// カットの尺は発火時刻から数える（<c>TakeRunnerLogic.StepEndTime</c>）ので、ここが尺に
        /// 近づくとその分だけ画に出る時間が減る。<c>ev=step played=1</c> は「画面を取った」しか
        /// 言わないので、停滞はこのイベントでしか観測できない。
        /// </summary>
        private void OnClipLatency(string cueId, float dlSec, float prepSec, bool cached)
            => Emit($"ev=clip id={(string.IsNullOrEmpty(cueId) ? "?" : cueId)} " +
                    $"dl={dlSec * 1000f:F0} prep={prepSec * 1000f:F0} " +
                    $"tot={(dlSec + prepSec) * 1000f:F0} cache={(cached ? 1 : 0)}");

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
        /// <summary>スクリーンの表示ゲート <c>_IntroLive</c> に実際に書いた値（0225 で reveal でも開く）。</summary>
        private string LiveGateState => _intro == null
            ? "-"
            : (_intro.LiveGateWritten < 0f ? "nc" : _intro.LiveGateWritten.ToString("F2"));

        /// <summary>
        /// スクリーンの電力（<c>_ScreenPower</c>）に<b>実際に書いた値</b>。
        ///
        /// <c>-</c> = 終幕の実行体がシーンに居ない / <c>nc</c> = <b>書く先の材質を掴めていない</b>
        /// （＝ 装置が死ぬ過程は一生画に出ない）。
        /// ⚠ 2026-08-23 から <b>Collapse のあいだは 1.00 のまま</b>で、Dark 以降 0 になる
        /// （消え方そのものは <see cref="CollapseState"/> が持つ）。
        /// </summary>
        private string PowerState => _outro == null
            ? "-"
            : (_outro.PowerWritten < 0f ? "nc" : _outro.PowerWritten.ToString("F2"));

        /// <summary>
        /// <b>電源断が実際に画へ出たか</b>（<c>_ScreenCollapse</c>・<c>canon/LEDGER.md</c> 0111）。
        ///
        /// ⚠⚠ <b>これが無いと「潰れなかった」を誰も検出できない。</b> 段は正しく進み、
        /// <c>pw</c> も Dark 以降 0 になるので、**書けていなくても既存の判定は全部 PASS する**
        /// （画は「0.9 秒ふつうに映ってから黒へ瞬断」になり、暗い現場では目視でも区別できない）。
        /// 段が Collapse を通っているのにずっと 0.00 なら、進みは配っているのに 1 画素も潰れていない。
        /// </summary>
        private string CollapseState => _outro == null
            ? "-"
            : (_outro.CollapseWritten < 0f ? "nc" : _outro.CollapseWritten.ToString("F2"));

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

        /// <summary>段 4 の基底面へ実際に配った閉じ具合の最大値。</summary>
        private string ApertureState => _veil == null ? "-" : _veil.ApertureClosePeak.ToString("F2");

        /// <summary>覆いの quad 数。新しい開口は全期間 1 枚で、0 は実体を組めていない。</summary>
        private string ApertureQuadState => _veil == null ? "-" : _veil.ApertureQuads.ToString();

        /// <summary>開口の終端と映像交差に使ったスクリーン矩形（半幅,半高,眼からの距離）。</summary>
        private string ApertureRectState => _veil == null ? "-" : _veil.ApertureRectDesc;

        private string ShatterState => _veil == null ? "-" : _veil.ShatterPeak.ToString("F2");
        private string ShatterPieceState => _veil == null ? "-" : _veil.ShatterPieces.ToString();
        private string ShatterDrawState => _veil == null ? "-" : (_veil.ShatterDrawn ? "1" : "0");
        private string ShatterFrozenState => _veil == null ? "-" : (_veil.HasFrozenFrame ? "1" : "0");
        private string ShatterFrozenCount => _veil == null ? "-" : _veil.FrozenFrameCount.ToString();
        /// <summary>静止画を取れなかった理由（ok / none / unsupported / denied / nocam / noimage / stale / stereo）。</summary>
        private string ShatterCameraState => _veil == null ? "-" : _veil.FrozenFrameStatus;
        /// <summary>割れ始めの頭の姿勢を固定したか。0 のまま Frame が進むと破片が頭についてくる。</summary>
        private string ShatterAnchorState => _veil == null ? "-" : (_veil.ShatterAnchored ? "1" : "0");
        /// <summary>静止画の投影の検算（0236）。頭の正面 1.6m の uv 左/右、または behind。</summary>
        private string ShatterProjectionState => _veil == null ? "-" : _veil.FrozenFrameProjection;
        /// <summary>この走行で光の粒（0235）を一度でも描いたか。0 ならシェーダかメッシュが剥がれている。</summary>
        private string SparkState => _veil == null ? "-" : (_veil.SparkEverDrawn ? "1" : "0");
        /// <summary>組めた光の粒の数（流れ ＋ 代表）。0 はメッシュを組めていない。</summary>
        private string SparkCountState => _veil == null ? "-" : _veil.SparkCount.ToString();

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

        // ⚠ 周ごとの環境音（旧 <c>sndAmb</c>）は 2026-08-23 に観測から外した
        //   （`canon/LEDGER.md` 0115）。3 本の環境音ごと退役して背景は劇伴 1 本になったので、
        //   見るのは <c>sndScore</c> 側になった。

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

        /// <summary>
        /// CG の層 — <c>&lt;層が描かれているか&gt;/&lt;それが人形か&gt;</c>。<c>-</c>=シーンに居ない。
        ///
        /// ⚠⚠ <b>2 つ組にしたのは、1 つ目だけでは「人形が立っている」と読めないから</b>
        /// （2026-08-22）。覆いの形を出す<b>人の代役</b>でも 1 つ目は 1 になるので、
        /// <c>analyze-xp-log.py</c> が「人形が立っているのに笑っていない」と誤判定していた。
        /// 人形かを訊く側は<b>必ず 2 つ目を読む</b>（C# の <see cref="Cg.ShowCgLayer.DollVisible"/> と対）。
        /// </summary>
        private string CgState => _cg == null
            ? "-"
            : $"{(_cg.IsVisible ? 1 : 0)}/{(_cg.DollVisible ? 1 : 0)}";

        /// <summary>
        /// 入れ替わりのノイズ —
        /// <c>&lt;走っているか&gt;/&lt;被覆&gt;/&lt;背丈 m&gt;/&lt;矩形&gt;/&lt;累計&gt;/&lt;差分マスク&gt;/
        /// &lt;山の振幅&gt;/&lt;エネルギー&gt;/&lt;手の点&gt;</c>。
        /// 6 つ目（2026-08-21〜）: 1 = 無人プレートの差分で覆えている / 0 = CG の形へ縮退
        /// （映像の中の人と位置がずれうる）。
        ///
        /// 7〜9 つ目は<b>黒い波</b>（設計 A〜F）。⚠⚠ <b>「段が進んだ」ではなく「効果が出た」を出す</b> —
        /// 山の振幅は**画へ書いた値**なので、走っているのに 0 なら段だけが進んでいる。
        /// エネルギーと手の点は<b>立ち止まっている体験者では 0 が正しい</b>（判定に使わない）。
        /// `analyze-xp-log.py` と**対**。
        /// </summary>
        private string SwapState => _swap == null
            ? "-"
            : $"{(_swap.Active ? 1 : 0)}/{_swap.Cover:F2}/{_swap.HeightM:F2}/" +
              $"{(_swap.RectResolved ? 1 : 0)}/{_swap.Count}/{(_swap.MaskPlateBound ? 1 : 0)}/" +
              $"{_swap.CrestAmp:F2}/{_swap.Energy01:F2}/{_swap.HotPoints}";

        /// <summary>
        /// <b>持続の覆い</b>（<c>canon/LEDGER.md</c> 0102）—
        /// <c>&lt;包んだままか&gt;/&lt;覆いの左端&gt;/&lt;立てた回数&gt;/&lt;矩形を書けたか&gt;/&lt;差分で覆えたか&gt;</c>。
        ///
        /// ⚠⚠ <b><see cref="SwapState"/> とは別に出す。</b> 包んでいるあいだ入れ替わりは
        /// <c>Active</c> だが段は進まないので、<c>swap=</c> だけを見ると
        /// 「被覆 1.00 のまま何分も止まっている壊れた入れ替わり」に読める。
        /// ⚠ 左端が 0 のまま包んでいたら<b>左半分の鏡映しの人物まで包んでいる</b>
        /// （3 周目 A では 0.50 が正）。<c>analyze-xp-log.py</c> と<b>対</b>。
        /// </summary>
        private string WrapState => _swap == null
            ? "-"
            : $"{(_swap.Holding ? 1 : 0)}/{_swap.MinX:F2}/{_swap.HoldCount}/" +
              $"{(_swap.RectResolved ? 1 : 0)}/{(_swap.MaskPlateBound ? 1 : 0)}";

        /// <summary>
        /// 闇に開く目 — <c>&lt;組めたか&gt;/&lt;開いている数&gt;/&lt;不透明度&gt;/&lt;区間の進み&gt;/&lt;速さ&gt;/&lt;カットの指示&gt;/&lt;流しきり中か&gt;/&lt;版を掴めたか&gt;</c>。
        /// 6・7 つ目は 2026-08-19（<c>canon/LEDGER.md</c> 0093）、8 つ目は 2026-09-19（0238）に足した。
        /// <b>進み -1 = 位置では測っていない</b>（未登録・layout 不在）。速さ 2.00 = 追い上げ中。
        /// ⚠ <b>8 つ目が 0 なら、組めていて数も進んでいるのに 1 画素も出ていない</b>（版はシェーダの既定が黒）。
        /// </summary>
        private string EyesState => _eyes == null
            ? "-"
            : $"{(_eyes.IsBuilt ? 1 : 0)}/{_eyes.OpenCount}/{_eyes.AppliedFade:F2}/" +
              $"{_eyes.SpanProgress01:F2}/{_eyes.Rate:F2}/" +
              $"{_eyes.WantedLevel:F2}/{(_eyes.CueRunning ? 1 : 0)}/{(_eyes.TextureLoaded ? 1 : 0)}";

        /// <summary>
        /// 目が開く音（<c>canon/LEDGER.md</c> 0131）。<c>&lt;一撃の累計&gt;/&lt;大きい目を鳴らせたか&gt;</c>。
        ///
        /// ⚠⚠ <b>音は録画に映らない。</b> 目が開いているのに一撃が 0 なら、
        /// 音源を掴めていないか（大きい目の側は 0 で出る）、間引きが壊れている。
        /// </summary>
        private string EyeSfxState => _eyes == null
            ? "-"
            : $"{_eyes.EyeSfxCount}/{(_eyes.EyeBigSfxFired ? 1 : 0)}";

        /// <summary>
        /// 装置が打っている時計（<c>canon/LEDGER.md</c> 0108）—
        /// <c>&lt;組めたか&gt;/&lt;刻んだ回数&gt;/&lt;不透明度&gt;</c>。
        ///
        /// ⚠ <b>3 つとも要る。</b> 版か材質を掴めていない（0/…）／組めたのに 1 度も刻んでいない
        /// （1/0/… ＝ <see cref="ScreenOsd.Tick"/> が回っていない）／刻んでいるのに画へ書いていない
        /// （…/1..N/0.00）は**別の壊れ方**で、どれも<b>画には「時計が無い」としか出ない</b>。
        /// ⚠ 刻んだ回数は<b>秒が変わった回数</b>なので、走行秒とおおむね一致する。
        /// 大きく足りなければ、途中で敷き直しが止まっている。
        /// </summary>
        private string OsdState => _osd == null
            ? "-"
            : $"{(_osd.Built ? 1 : 0)}/{_osd.Ticks}/{_osd.Opacity:F2}";

        /// <summary>
        /// 時計の右に出している<b>周回</b>（<c>canon/LEDGER.md</c> 0167）—
        /// <c>-</c>（区間が未確定）/ <c>1</c> <c>2</c> <c>3</c> / <c>last</c>（帰りの区間）/
        /// <c>mask</c>（別の場所が映っていて時刻ごと <c>?</c>）。
        ///
        /// ⚠ <b>敷いた結果</b>を出す（状態ではない）。時計を組めていなければ <c>-</c> のまま。
        /// ⚠ <c>osd=</c> と分けてある — あちらは 3 値で <c>analyze-xp-log.py</c> が
        /// スラッシュの数を見ている。増やすと解析が黙って落ちる。
        /// </summary>
        private string OsdLapState => _osd == null ? "-" : _osd.LabelToken;

        /// <summary>
        /// 目の視界ジャック（<c>canon/LEDGER.md</c> 0099）—
        /// <c>&lt;面を組めたか&gt;/&lt;端末に用意できた写真&gt;/&lt;画に出した累計&gt;/&lt;いま乗っ取り中か&gt;</c>。
        ///
        /// ⚠ 4 つとも要る — シェーダが剥がれた（0/…）／写真が届いていない（1/0/…＝ **当日フォルダが
        /// 空のまま**）／発火したのに 1 枚も画に出ていない（…/0/1）は**別の壊れ方**。
        /// 2 つ目は「show.json が言う枚数」ではなく**この端末がデコードまで済ませた枚数**（画に出せる側）。
        /// </summary>
        private string JackState => _eyes == null
            ? "-"
            : $"{(_eyes.JackBuilt ? 1 : 0)}/{_eyes.JackPhotoCount}/{_eyes.JackShownTotal}/" +
              $"{(_eyes.JackActive ? 1 : 0)}";

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
                // veil / veilBuilt / wire / pt / aper は「重みが動いた」ではなく「画に出た」の側。
                // 段だけ見て OK と判定した 2026-07-31 の事故を繰り返さないため必ず一緒に出す。
                Emit($"ev=intro stage={_lastStage} hold={(_intro.Holding ? 1 : 0)} " +
                     $"fresh={(_intro.LiveFresh ? 1 : 0)} centered={(_intro.FrameCentered ? 1 : 0)} " +
                     $"pass={w.passthrough:F2} live={w.live:F2} frame={w.frame:F2} edge={w.edge:F2} " +
                     $"veil={VeilState} veilBuilt={VeilBuiltState} wire={WireState} pt={PassthroughState} " +
                     $"shell={ShellState} shellBuilt={ShellBuiltState} shellBox={ShellBoxState} " +
                     $"shellRev={ShellRevealState} " +
                     // 連続開口は段 4 のあいだだけ動く。Frame → Swap の行に閉じ切った実測が載る。
                     $"aper={ApertureState} aperQ={ApertureQuadState} aperRect={ApertureRectState} " +
                     $"shatStyle=frozen shat={ShatterState} shatC={ShatterPieceState} shatDraw={ShatterDrawState} " +
                     $"shatFrozen={ShatterFrozenState} shatCopies={ShatterFrozenCount} " +
                     $"shatCam={ShatterCameraState} shatAnchor={ShatterAnchorState} shatProj={ShatterProjectionState} " +
                     // 0235: 光の粒。spark は「画に出た」側で、sparkN は組めた粒の数。
                     $"spark={SparkState} sparkN={SparkCountState} " +
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

            // 終幕の段（Off/Collapse/Dark/Report/Done）。**画に出た側**を必ず一緒に出す —
            // cl は実際に材質へ書いた電源断の進み（0 のままなら 1 画素も潰れていない）、
            // pw は電力（nc なら材質を掴めていない ＝ 装置が死ぬ過程が一生画に出ない）、
            // rep / repBuilt は報告の面（built=0 なら最後の 4 行が 1 文字も出ない）。
            // marks は報告の数そのもの（○○ に入る値が正しいかは、これでしか確かめられない）。
            if (_outro != null && (!_outroSeen || _outro.Stage != _lastOutroStage))
            {
                _outroSeen = true;
                _lastOutroStage = _outro.Stage;
                Emit($"ev=outro stage={_lastOutroStage} pw={PowerState} cl={CollapseState} " +
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
                     // anomalies = 報告した異変の数（同じ演出は 1・`canon/LEDGER.md` 0234）。
                     // 終幕の「報告した怪異の数」に出るのは marks（押した回数）ではなくこちら。
                     $"anomalies={(_show != null ? _show.ReportedAnomalyCount : -1)} " +
                     // 合図（run.outro.afterTakeId）が武装したか / 撃ったか。
                     // 著作していなければ両方 0 のままで、終わり方は従来どおり。
                     $"armed={(_run != null && _run.EndingArmed ? 1 : 0)} " +
                     $"cue={(_run != null && _run.EndingFired ? 1 : 0)}");
            }

            // 体験者の記録ボタン（左のどれか）。**押した時刻が残ると、3 周目の反転に気づいたかが
            // 訊かずに分かる**（初見は消耗品なので、誘導せずに取れる観測の価値が高い）。
            // 区間を併記するのは「どこで押したか」が無いと後から読めないため。
            if (_show != null && _show.VisitorMarkCount != _lastMarkCount)
            {
                _lastMarkCount = _show.VisitorMarkCount;
                Emit($"ev=mark n={_lastMarkCount} lap={(_run != null ? _run.Lap : -1)} " +
                     $"cam={(_switch != null && _switch.TryGetCurrentZoneCamera(out int mc) ? mc : -1)}"
                     // det = 報告時に異常演出が表示されていたか。通信面の返答はこれで分かれる。
                     // res = 解除処理が通ったか。主映像の解除と進行の診断に残す。
                   + $" det={(_show.LastMarkDetected ? 1 : 0)}"
                   + $" res={(_show.LastMarkResolved ? 1 : 0)}"
                   + $" invasion={(_comms != null ? _comms.InvasionProgress : 0f):F2}"
                     // take = 報告が乗った演出の id（走っていなければ -）。new = この報告で異変の数が増えたか。
                     // anom = 報告した異変の数の累計（`canon/LEDGER.md` 0234・同じ演出は 1）。
                     // ⚠ n（押した回数）とは別物 — 解析器が「det=1 の相異なる take の数 == 最後の anom」を突き合わせる。
                   + $" take={(string.IsNullOrEmpty(_show.LastMarkTakeId) ? "-" : _show.LastMarkTakeId)}"
                   + $" new={(_show.LastMarkCounted ? 1 : 0)}"
                   + $" anom={_show.ReportedAnomalyCount}");
            }

            // AIエージェントからの連絡（`canon/LEDGER.md` 0054）。**1 通ごとに 1 行**。
            // ⚠ id だけでは足りない — 「配った」と「画に出た」は別物なので glyph / open を必ず添える
            //   （2026-07-31 の「段は進んだのに画は空だった」と同じ型）。built=0 なら一生出ない。
            if (_comms != null && Mathf.Abs(_comms.InvasionProgress - _lastCommsInvasion) > 0.001f)
            {
                _lastCommsInvasion = _comms.InvasionProgress;
                Emit($"ev=commsInvasion v={_lastCommsInvasion:F2} " +
                     $"lap={(_timeline != null ? _timeline.CurrentLap : -1)} " +
                     $"cam={(_timeline != null ? _timeline.CurrentCamera : -1)} " +
                     $"cue={(_timeline != null ? _timeline.ActiveStepCueId : "")} " +
                     $"doll={(_timeline != null && _timeline.DollReplacementShowing ? 1 : 0)}");
            }
            // 憑依の出し方（`canon/LEDGER.md` 0230）の段の縁: Shown（一気に出て読ませている）→ Sweep（上から
            // 塗り替わっている）→ Cursed（塗り替わり切った）。**画に出た側**（AppliedSweep / cx / red）で出す。
            // `sfx` は塗り替わりの頭の乱れの音の累計（nc = 音源を掴めていない ＝ 画は変わるのに無音）。
            if (_comms != null)
            {
                // 乱れ（0231）は前線が降りているあいだだけ立つ。**画へ書いた側**（AppliedTear / 飛んだ帯の数）の最大を持つ。
                if (_comms.PossessionPhase == FixedCamVr.Streaming.CommsPossessionPhase.Sweep)
                {
                    _commsTearMax = Mathf.Max(_commsTearMax, _comms.AppliedTear);
                    _commsTornMax = Mathf.Max(_commsTornMax, _comms.TornBands);
                }
                else if (_comms.PossessionPhase == FixedCamVr.Streaming.CommsPossessionPhase.Shown)
                {
                    _commsTearMax = 0f;
                    _commsTornMax = 0;
                }
            }
            if (_comms != null && _comms.PossessionPhase.ToString() != _lastCommsPossessPhase)
            {
                _lastCommsPossessPhase = _comms.PossessionPhase.ToString();
                Emit($"ev=commsPossess phase={_lastCommsPossessPhase} sweep={_comms.AppliedSweep:F3} " +
                     $"tearMax={_commsTearMax:F2} tornMax={_commsTornMax} " +
                     $"cx={_comms.CorruptedChars} face={_comms.AppliedFaceMix:F3} " +
                     $"invasion={_comms.InvasionProgress:F2} lie={_comms.LieChars} " +
                     $"glyph={_comms.AppliedGlyph:F3} faceInk={_comms.AppliedFace:F3} " +
                     $"shown={_comms.VisibleChars} id={_comms.LastNotice} n={_comms.PossessedCount} " +
                     $"lies={_comms.LieCount} sweeps={_comms.SweepCount} " +
                     $"sfx={(_comms.SweepSfxBuilt ? _comms.SweepSfxCount.ToString() : "nc")}");
            }
            // 呪いの斑が目標へ届いた縁（`canon/LEDGER.md` 0229「出た初めは通常 → 1s ほどで重なる」）。
            // sec = 面が開いてから届くまでの秒。**画に出た側**（AppliedCurse）で数えている。
            if (_comms != null && _comms.CurseRampCount != _lastCommsCurseRamp)
            {
                _lastCommsCurseRamp = _comms.CurseRampCount;
                Emit($"ev=commsCurse n={_lastCommsCurseRamp} sec={_comms.LastCurseRampSec:F2} " +
                     $"v={_comms.AppliedCurse:F2} target={_comms.CurseTarget:F2} " +
                     $"cx={_comms.CorruptedChars} id={_comms.LastNotice} " +
                     $"invasion={_comms.InvasionProgress:F2} plate={(_comms.PlateBuilt ? 1 : 0)}");
            }
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
                     $"decay={_comms.DecayProgress:F2} invasion={_comms.InvasionProgress:F2} " +
                     // curse = 届いた瞬間に画へ書いていた斑の量。**開いた縁なら 0 のはず**（0229）。
                     $"curse={_comms.AppliedCurse:F2} " +
                     $"wait={(_timeline != null && _timeline.IsWaitingForVisitorMark ? 1 : 0)} " +
                     // closing = 締めのカットに入ってからの秒（外なら負）。cline = 締めの線を踏んでいたか。
                     // ③a は線を踏んだ縁で出る（0233）ので、Halt の行は cline=1 のはず。
                     // cline=0 で Halt が出ていたら時計の退避路（線が解決できていない）。
                     $"closing={(_timeline != null ? _timeline.ClosingTakeSec : -1f):F2} " +
                     $"cline={(_timeline != null && _timeline.ClosingLineCrossed ? 1 : 0)}");
            }

            // 締めの線（3 周目 A の凍結点）を締めのカットの中で踏んだ縁（③a の引き金・0233）。
            // sec = 締めのカットに入ってからの秒。**「線を踏んだのに③a が出ない」と「踏んでいない」を
            // 分ける唯一の行**（TakeRunner の横断ログは締めの中かどうかを言わない）。
            bool closingLine = _timeline != null && _timeline.ClosingLineCrossed;
            if (closingLine && !_lastClosingLine)
                Emit($"ev=closingLine id={(_timeline != null ? _timeline.ClosingLineId : "-")} " +
                     $"sec={(_timeline != null ? _timeline.ClosingLineCrossedSec : -1f):F2} " +
                     $"wait={(_timeline != null && _timeline.IsWaitingForVisitorMark ? 1 : 0)}");
            _lastClosingLine = closingLine;

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

            // ステータス表示（右 B）の出入り。
            // ⚠⚠ **画にも音にも出ない。** 体験者の視線前方に日本語の業務表示が出ていた走行を、
            //    卓でも解析器でも当日パネルでも検出できなかった（2026-09-14 まで観測が 1 ビットも無かった）。
            //    `sec` は「その 1 回で出ていた秒数」。走行全体の累計は `ev=sum` の `hudSec`。
            if (_statusHud != null)
            {
                bool hudNow = _statusHud.IsVisible;
                if (hudNow != _lastHudVisible)
                {
                    string src = StatusHud.SourceTag(_statusHud.LastShowSource);
                    if (hudNow)
                    {
                        _hudShownAt = Time.unscaledTime;
                        Emit($"ev=status st=on src={src} sec=0.0");
                    }
                    else
                    {
                        float sec = Mathf.Max(0f, Time.unscaledTime - _hudShownAt);
                        Emit($"ev=status st=off src={src} sec={sec:F1}");
                    }
                    _lastHudVisible = hudNow;
                }
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

            // 目の視界ジャックの出入り（canon/LEDGER.md 0099）。
            // ⚠ **終わり方の理由（why）を必ず添える** — done（写真が尽きた・止まっている体験者）と
            //    cut（区間の畳み・歩き続ける体験者）は 0099 の分岐そのもので、
            //    ここが無いと「どちらの終わり方だったか」を機械で確かめられない。
            bool jackActive = _eyes != null && _eyes.JackActive;
            if (jackActive != _lastJackActive)
            {
                if (jackActive)
                {
                    _jackShownAtBegin = _eyes!.JackShownTotal;
                    Emit($"ev=jack st=begin n={_eyes.JackShowCount} per={_eyes.JackPerSec:F2} " +
                         $"photos={_eyes.JackPhotoCount}");
                }
                else
                {
                    Emit($"ev=jack st=end why={(_eyes != null ? _eyes.JackLastEndWhy : "?")} " +
                         $"shown={(_eyes != null ? _eyes.JackShownTotal - _jackShownAtBegin : 0)}");
                }
                _lastJackActive = jackActive;
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

            // 入れ替わりのノイズ（`canon/LEDGER.md` 0089）。**始まりと終わりの 2 行**を出す。
            //   ⚠ 終わりの行に出す `cover` / `h` / `rect` が要る — 段が 3 つとも進んでも、
            //     人形がカメラの後ろに居れば砂は 1 画素も出ない（`rect=0`）。「進んだ」と
            //     「出た」を分けるのがこの 3 つ（`rules/visual-verification.md` の計装の規律）。
            if (_swap != null)
            {
                // 持続の覆い（0102）。**立てた / 畳んだの 2 行**。入れ替わりの begin / end とは
                // 別の系統なので、`ev=swap` の対応（begin と end の数）を崩さない。
                bool holding = _swap.Holding;
                if (_swap.HoldCount != _lastWrapCount)
                {
                    _lastWrapCount = _swap.HoldCount;
                    Emit($"ev=wrap st=on n={_swap.HoldCount} minx={_swap.MinX:F2} " +
                         $"rect={(_swap.RectResolved ? 1 : 0)} mask={(_swap.MaskPlateBound ? 1 : 0)}");
                }
                else if (_lastWrapHolding && !holding)
                {
                    // 引き継いだ（入れ替わりへ）のか、畳まれた（演出の中止・カットが変わった）のか。
                    Emit($"ev=wrap st=off n={_swap.HoldCount} " +
                         $"why={(_swap.Active ? "swap" : "drop")}");
                }
                _lastWrapHolding = holding;

                bool act = _swap.Active;
                if (_swap.Count != _lastSwapCount)
                {
                    _lastSwapCount = _swap.Count;
                    Emit($"ev=swap st=begin n={_swap.Count} " +
                         $"dir={(_swap.Direction == SwapMorphLogic.Dir.ToDoll ? "toDoll" : "toHuman")} " +
                         $"h={_swap.HeightM:F2} rect={(_swap.RectResolved ? 1 : 0)} " +
                         $"mask={(_swap.MaskPlateBound ? 1 : 0)} vis={(_swap.HumanActorShown ? 1 : 0)}");
                }
                else if (_lastSwapActive && !act)
                {
                    // ⚠ 黒い波の 3 つは**この回の最大**（走り終わってから出る行なので、
                    //   生の値を読むと必ず 0 になる）。
                    Emit($"ev=swap st=end n={_swap.Count} " +
                         $"dir={(_swap.Direction == SwapMorphLogic.Dir.ToDoll ? "toDoll" : "toHuman")} " +
                         $"h={_swap.HeightM:F2} rect={(_swap.RectResolved ? 1 : 0)} " +
                         $"mask={(_swap.MaskPlateBound ? 1 : 0)} vis={(_swap.HumanActorShown ? 1 : 0)} " +
                         $"crest={_swap.CrestAmpPeak:F2} e={_swap.EnergyPeak:F2} hot={_swap.HotPeak}");
                }
                _lastSwapActive = act;
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
                // 連続開口も同じ理由。段 4 の途中の到達点はここでしか取れない。
                _sb.Append(" aper=").Append(ApertureState);
                _sb.Append(" aperQ=").Append(ApertureQuadState);
                _sb.Append(" shatStyle=frozen shat=").Append(ShatterState);
                _sb.Append(" shatC=").Append(ShatterPieceState);
                _sb.Append(" shatDraw=").Append(ShatterDrawState);
                _sb.Append(" shatFrozen=").Append(ShatterFrozenState);
                _sb.Append(" shatCopies=").Append(ShatterFrozenCount);
                _sb.Append(" shatCam=").Append(ShatterCameraState);
                _sb.Append(" shatAnchor=").Append(ShatterAnchorState);
                _sb.Append(" shatProj=").Append(ShatterProjectionState);
                _sb.Append(" spark=").Append(SparkState);
                _sb.Append(" sparkN=").Append(SparkCountState);
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
            // 表示ゲート。導入の段 4 で reveal（.68〜）か live（.895〜）が立てば 1。ここが 0 のまま
            // 砂嵐も映像も出ない（0225 の実機検証で、保持中に画が黒だった原因の切り分けに要る）。
            _sb.Append(" ilive=").Append(LiveGateState);
            // 砂嵐の実値。**導入中にも出す**（0225 の実機検証で、段 Frame の保持中に画が黒だった原因の
            // 切り分けに要る — 後半の `storm=` は本編でしか書かれない）。`-` は SignalLostFx を掴めていない。
            _sb.Append(" sig=").Append(_signal != null ? _signal.Level.ToString("F2") : "-");
            // 終幕の電力。**演出の外では 1.00** なので、本編中にこれが下がっていたら画が暗い理由がここ。
            _sb.Append(" pw=").Append(PowerState);
            _sb.Append(" cg=").Append(CgState);
            //   swap = 入れ替わりのノイズ（`canon/LEDGER.md` 0089）。
            //   **`<走っているか>/<砂の被覆>/<人型の背丈 m>/<矩形を書けたか>/<累計>`** の 5 つ組。
            //   ⚠ 5 つとも要る — 段は進んだのに矩形が書けない（.../0/...＝ **画に 1 画素も出ない**）／
            //     被覆が 0 のまま（人形のシルエットが取れていない）／背丈が動かない（縮んでいない）は
            //     **別の壊れ方**。全画面の砂嵐と違って、これは「出ていない」が画で分かりにくい。
            //   ⚠ 累計は 1 体験で **2**（3 周目 A の人 → 人形、4 周目 A の人形 → 人）。
            _sb.Append(" swap=").Append(SwapState);
            //   wrap = 持続の覆い（`canon/LEDGER.md` 0102。3 周目 A の入り）。
            //   **`<包んだままか>/<覆いの左端>/<立てた回数>/<矩形>/<差分マスク>`**。
            //   ⚠ `swap=` と対で読む — 包んでいるあいだ入れ替わりは走っている（Active）が
            //     段は進まないので、`swap=` だけでは「止まった入れ替わり」と区別できない。
            //   ⚠ 左端が 0.00 のまま包んでいたら、左半分の鏡映しの人物まで包んでいる。
            _sb.Append(" wrap=").Append(WrapState);
            //   eyes = 闇に開く目（`canon/LEDGER.md` 0075）。
            //   **`<実体を組めたか>/<いま開いている目の数>/<画に出た不透明度>/<区間の進み>/<速さ>`**。
            //   ⚠ 後ろ 2 つは 0093（位置で開閉する）で足した。**進みが -1 のまま動かない走行は、
            //     位置を測れていない**（未登録 / layout 不在）＝ 開閉はカットの尺で起きている。
            //     速さが 1.00 のまま終わったなら、追い上げは 1 度も要らなかった（歩くのが遅い人）。
            //   ⚠ 3 つとも要る — シェーダが剥がれた（0/…）／カットが 1 度も指していない（1/0/0.00）／
            //     指したのに 1 画素も出ていない（1/0/1.00 ＝ 座席表を組めていない）は**別の壊れ方**。
            //   ⚠ 数は「重みを配った」ではなく**何個ぶんの目が実際に開いているか**（画に出た側）。
            _sb.Append(" eyes=").Append(EyesState);
            //   osd = 装置が打っている時計（`canon/LEDGER.md` 0108）。組めたか/刻んだ回数/不透明度。
            //   ⚠ **本編で 1/0/… なら時計が止まっている**（秒が変わっても敷き直していない）。
            //     画には「時計が無い」としか出ないので、ここが唯一の手掛かり。
            _sb.Append(" osd=").Append(OsdState);
            //   osdLap = 時計の右の周回（`canon/LEDGER.md` 0167）。-/1/2/3/last/mask。
            //   ⚠ **別の場所（バックルームズ）が映っているあいだは mask**。異世界のカットが出た
            //     走行でここが 1 度も mask にならなければ、時計だけが現実を主張し続けている。
            _sb.Append(" osdLap=").Append(OsdLapState);
            //   jack = 目の視界ジャック（`canon/LEDGER.md` 0099）。組めたか/写真/出した累計/乗っ取り中。
            _sb.Append(" jack=").Append(JackState);
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
            //   sndDolls = 人形の笑いの音量（`canon/LEDGER.md` 0066）。**報告を押すまでループ**
            //              するので、一撃のログ（ev=sfx）には出ない。鳴ったかはここにしか出ない。
            _sb.Append(" sndDolls=").Append(_sound == null ? "-" : _sound.DollsGain.ToString("F2"));
            //   sndSwap  = 入れ替わった人形の笑いの音量（`canon/LEDGER.md` 0086）。3 周目 A の
            //              入れ替わりで立ち、C を出るまで鳴り続ける。**これも一撃のログには出ない**
            //   sndSwell = 笑う人形の増え具合 0..1（C に居るあいだ増える）。**音量とは別に出す** —
            //              合計だけ見ても「増えた」のか「大きくなった」のか分けられない
            _sb.Append(" sndSwap=").Append(_sound == null ? "-" : _sound.DollSwapGain.ToString("F2"));
            _sb.Append(" sndSwell=").Append(_sound == null ? "-" : _sound.DollSwellNow.ToString("F2"));
            //   sndScore = 劇伴（HorrBGM）の取り分（`canon/LEDGER.md` 0115）。**黒から 3 周目の
            //              終わりまで 1** で、終幕で退く。BgmDirector が鳴らす音なので
            //              `sndAud`（敷く音の合計）には 1 ビットも出ない — 本編で背景が鳴っている
            //              証拠はこのキーだけ（0115 で環境音を退役させたので、ここが 0 なら無音）。
            _sb.Append(" sndScore=").Append(_sound == null ? "-" : _sound.ScoreGain.ToString("F2"));
            //   snd3d = 音の定位（`canon/LEDGER.md` 0130）。
            //           <名簿で掴めた本数>/<ステレオのままの本数>/<いま 3D で鳴っている敷く音>
            //   ⚠⚠ **真ん中が 0 でなければ定位していない。** spatializer はモノしか処理しないので、
            //      ステレオのクリップは `spatialBlend=1` でも頭の中で鳴る ＝ **音は鳴っている**。
            //      画にも録画にも一撃のログにも出ないので、ここが唯一の証拠。
            //   ⚠ 右が 0 のまま本編が進むなら、頭かスクリーンの Transform を掴めていない
            //      （掴めないときは 2D へ落とす設計なので、無音にはならず黙って頭の中で鳴る）。
            _sb.Append(" snd3d=").Append(_sound == null
                                         ? "-"
                                         : $"{_sound.SpatialClipCount}/{_sound.StereoInSpatial}"
                                           + $"/{_sound.SpatialBedsAudible}");
            //   sndAz = 人形の笑いの方角（度・ワールド）。**体験者ごとに引き直す**ので走行ごとに違う。
            //           ⚠⚠ **2026-09-04 から 8 個**（体ごとに 1 つ・`canon/LEDGER.md` 0139）。
            //           360° を等分した区画へ 1 つずつ入るので、**どの 2 つも区画の 1/3 より
            //           近づかない**（8 体なら 15°。近ければ引き直しが壊れている）。
            _sb.Append(" sndAz=").Append(_sound == null ? "-" : _sound.LaughBearingsText);
            //   sndLaugh = **いま何か所から笑いが鳴っているか**（0139）。
            //   ⚠⚠ 「周囲に大勢いる」が成立したかの唯一の証拠。**画にも録画にも出ない。**
            //      4 周目 A の群れで 8・3 周目 C で 7（1 ＋ 2 ＋ 4）で、
            //      1 なら体ごとに分けた意味が消えている（焼き忘れ／取り込み忘れ）。
            //   ⚠⚠ **満点は 15**（入れ替わる 2 秒は両方が実際に鳴る。実測 t=121.4 で
            //      群れ 0.11 / 入替 2.26）。**16 以上なら数え方が壊れている。**
            //   ⚠ 数えるのは**聞こえる大きさで鳴っている体**だけ
            //      （`ShowSoundDirector.LaughAudibleGain` = -34dB）。層の出し入れは半減期で
            //      寄せるので、切り替わった後も消えかけの声が長く 0 に届かない
            //      （「置けているか」の閾値だと 40dB 下の尾まで数えた）。
            _sb.Append(" sndLaugh=").Append(_sound == null ? "-" : _sound.LaughPoints.ToString());
            //   sndWind  = 別の場所（バックルームズ）の風（`canon/LEDGER.md` 0131）。
            //   ⚠⚠ **異世界が映っているあいだだけ立ち、そのあいだ `sndScore` は 0**。
            //      画は 4 秒の演出として出るが、**音が入れ替わったかは画に 1 ビットも出ない**。
            //   sndCurse = 3 周目 B から呪いが排除されるまでの 2 本（**2 本とも同じ値**）。
            //   sndWhite = 呪いが排除された後のホワイトノイズ。
            //   ⚠ 3 つとも `sndAud`（敷く音の合計）にも乗るが、**どれが鳴っているかは分けないと出ない**。
            _sb.Append(" sndWind=").Append(_sound == null ? "-" : _sound.WindGain.ToString("F2"));
            _sb.Append(" sndCurse=").Append(_sound == null ? "-" : _sound.CurseGain.ToString("F2"));
            _sb.Append(" sndWhite=").Append(_sound == null ? "-" : _sound.WhiteGain.ToString("F2"));
            //   sndHeart = 心音（`canon/LEDGER.md` 0175）。2 周目 C の追いつきから、
            //   3 周目 A の入れ替わりの再生が終わるまで立つ。
            //   ⚠⚠ **画にも録画にも一撃のログにも 1 ビットも出ない。** ここが唯一の証拠。
            //      しかも素材は正体が 150Hz より下にあるので、**実機の内蔵スピーカーでも聞こえない**
            //      （内蔵SP -21.1dB）。耳でも確かめられない ＝ この数字しか無い。
            _sb.Append(" sndHeart=").Append(_sound == null ? "-" : _sound.HeartGain.ToString("F2"));
            //   sndEye = 目が開く音（0131）。<一撃の累計>/<大きい目を鳴らせたか>。
            //   ⚠ **目が開いた数（`eyes` の 2 つ目）と対で見る** — 開いているのに 0 なら鳴っていない。
            _sb.Append(" sndEye=").Append(EyeSfxState);
            //   sndCall = 人形の呼びかけ（あーそーぼー）。<鳴らした回数>/<見かけの方角（度）>。
            //   ⚠ 方角は 0 が正面・180 が真後ろ。**145〜215 の外なら「後ろから」が成立していない。**
            //      -1 は頭の Transform を掴めずに 2D で鳴らしたということ。
            _sb.Append(" sndCall=").Append(_sound == null
                                           ? "-"
                                           : $"{_sound.CallCount}/"
                                             + Mathf.RoundToInt(_sound.LastCallAzimuthDeg));
            _sb.Append(" sfxN=").Append(_sound == null ? "-" : _sound.SpotCount.ToString());
            _sb.Append(" swN=").Append(_switchSfx == null
                                       ? "-"
                                       : (_switchSfx.HasClips ? _switchSfx.PlayedCount.ToString() : "nc"));
            //   swAlert = そのうち**警告音つき**で鳴った回数（`canon/LEDGER.md` 0106）。
            //             人形視点が差し込まれるカット（カットの `switchSfx`）だけがここに乗る。
            //             `nc` は警告つきの音源を掴めていない ＝ その差し込みも素の切替音で鳴っている。
            //             ⚠ **画にも動画にも違いが出ない**（音は録画に映らない）ので、
            //                「警告音が混ざったか」の証拠はこのキーだけ。
            //   ⚠⚠ **2026-09-04 から 3 つ組**（`canon/LEDGER.md` 0145）:
            //      &lt;回数&gt;/&lt;直前に警告へ掛けた倍率&gt;/&lt;警告だけの音源を掴めたか 1|0&gt;。
            //      回数だけでは「回を重ねて大きくなったか」が分からない（0134 で
            //      「鳴った回数は正しいのに聞こえない」を踏んだのと同じ形）。
            //      3 つ目が 0 なら混ぜた 1 本へ落ちている ＝ **大きさは一定のまま**。
            _sb.Append(" swAlert=").Append(_switchSfx == null
                                           ? "-"
                                           : (_switchSfx.HasAlertClip || _switchSfx.HasWarnClip
                                              ? _switchSfx.AlertCount.ToString()
                                              : "nc"));
            if (_switchSfx != null && (_switchSfx.HasAlertClip || _switchSfx.HasWarnClip))
            {
                _sb.Append('/').Append(_switchSfx.AlertGain.ToString("F2"))
                   .Append('/').Append(_switchSfx.HasWarnClip ? '1' : '0');
            }
            //   swVar = 焼けている変種の本数 / この走行で実際に鳴った異なり数
            //           （`canon/LEDGER.md` 0112「毎回同じではなく」）。
            //           ⚠ **累計（swN）だけでは「6 本焼いたのに 1 本しか鳴っていない」を見分けられない。**
            //              焼き忘れ・`menu sound-import` 忘れは左が落ちる形で出る（6 → 1）。
            //              ⚠ 画にも動画にも違いが出ないので、変種が効いた証拠はこのキーだけ。
            _sb.Append(" swVar=").Append(_switchSfx == null
                                         ? "-"
                                         : $"{_switchSfx.ClipCount}/{_switchSfx.DistinctUsedCount}");
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
            //   commsPossess / commsSweep = 憑依の出し方（`canon/LEDGER.md` 0230）の段と、画へ書いた前線の進み。
            //   ⚠ 斑（commsCurse）とは別の観測 — 憑依の出し方では斑は塗り替わる前 0・後 1 で、
            //     その間の「上から降りている」は commsSweep にしか出ない。
            _sb.Append(" commsPossess=").Append(_comms == null ? "-" : _comms.PossessionPhase.ToString());
            _sb.Append(" commsSweep=").Append(_comms == null ? "-" : _comms.AppliedSweep.ToString("F3"));
            //   commsTear = いま画へ書いている乱れの強さ（0231）。降りているあいだだけ 0.6。
            _sb.Append(" commsTear=").Append(_comms == null ? "-" : _comms.AppliedTear.ToString("F2"));
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
            //   commsCurse = 呪いの斑（`canon/LEDGER.md` 0229）。
            //   **`<地の双子のシェーダを引けたか>/<画へ書いた斑の量>/<斑の目標>`** の 3 つ組。
            //   ⚠ 1 つ目が 0 なら毛羽立ち・走り書き・文字の切断が実機で 1 画素も出ていない
            //     （Editor では出るので、この 1 ビットが無いと永久に気づけない — `Unlit/Color` と同じ穴）。
            //   ⚠ 2 つ目は面が開いてから 1 秒で 3 つ目へ寄る。開いた直後の標本で 0 なのは正常。
            _sb.Append(" commsCurse=").Append(_comms == null
                ? "-"
                : $"{(_comms.PlateBuilt ? 1 : 0)}/{_comms.AppliedCurse:F2}/{_comms.CurseTarget:F2}");
            //   repTypeN / repShown = 終幕の報告の打鍵の累計と、いま画に出ている文字数
            //           （`canon/LEDGER.md` 0063）。**対で出す** — 片方だけだと
            //           「字は出たのに無音」と「音は鳴ったのに字が出ていない」を区別できない。
            //           打ち切るまでの尺は段 Report より長いので、到達の判定はここでしか取れない。
            _sb.Append(" repTypeN=").Append(_report == null
                                            ? "-"
                                            : (_report.TypeSfxBuilt ? _report.TypedCount.ToString() : "nc"));
            _sb.Append(" repShown=").Append(_report == null ? "-" : _report.VisibleChars.ToString());
            //   clMax = **電源断が画へ出た最大値**（`canon/LEDGER.md` 0111）。
            //   ⚠⚠ `ev=outro` は段の縁でしか出ないので、そこの `cl` は必ず頭の値（≒0）になる。
            //     潰れ切ったかは**走行全体の最大値**でしか取れない（敷く音を最大値で見るのと同じ理屈・
            //     `rules/sound-design.md` §7）。0.9 未満なら、進みを配っているのに画が潰れ切っていない。
            _sb.Append(" clMax=").Append(_outro == null
                                         ? "-"
                                         : (_collapseSeen ? _collapseMax.ToString("F2") : "nc"));
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
            //   hud / hudSec / hudN = スタッフのステータス表示（右 B）。
            //   ⚠⚠ **画にも音にも出ない。** 体験者の走行中に業務表示が出ていたかはここにしか残らない
            //     （2026-09-14 まで観測が 1 ビットも無く、卓でも解析器でも当日パネルでも検出できなかった）。
            //   ⚠ 自動走行（`--walk`）では誰も押さないので **`hudSec=0.0` が普通**。
            //     0 でなければ、押したか・ピン留めが残っているか・プローブ（`-e xpstatus 1`）を指したか。
            //   ⚠ `-` は StatusHud がシーンに居ない（`menu scene` の焼き直し漏れ）。
            _sb.Append(" hud=").Append(_statusHud == null ? "-" : (_statusHud.IsVisible ? "1" : "0"));
            _sb.Append(" hudSec=").Append(_statusHud == null
                                          ? "-"
                                          : _statusHud.VisibleSecSinceRun.ToString("F1"));
            _sb.Append(" hudN=").Append(_statusHud == null
                                        ? "-"
                                        : _statusHud.ShowCountSinceRun.ToString());
            //   lang / langN = 体験者が読んでいる言語と、この走行で変わった回数（2026-09-03）。
            //   ⚠⚠ **画にも音にも出ない。** 注意書き・AIエージェントの連絡・終幕の報告は
            //     どれも「文字が出ている」ことしか外から見えないので、**どの言語で出ていたかは
            //     ここにしか残らない**（走行の PNG を開いても、英語で出すつもりが日本語だったことは
            //     読めるが、日本語で出すつもりが英語だったことは気づかれずに通る）。
            //   ⚠ **2 つで 1 組**。`lang` だけだと「選ばれなかった」と「切り替えが効いていない」が
            //     区別できない（どちらも ja のまま）。`langN` が 0 なら押しても変わっていない。
            _sb.Append(" lang=").Append(ShowLanguage.Code(ShowLanguage.Current));
            _sb.Append(" langN=").Append(ShowLanguage.ChangeCount);
            //   relief = ホラー軽減モード（2026-09-05・`canon/LEDGER.md` 0154）。
            //   **`<入っているか>/<切り替えた回数>/<既存の音の倍率>/<陽気な曲の音量>/<曲の再生位置>`**
            //   ⚠⚠ **5 つとも要る。** 音は録画に映らないので、ここが唯一の証拠になる:
            //     ・1/1/1.00/… → 入ったのに **AudioListener に書けていない**（[Sound] に
            //       `HorrorReliefAudio` が焼かれていない ＝ `menu scene` の忘れ）
            //     ・1/1/0.50/0.00/0.00 → 音量は半分になったが**曲を掴めていない**
            //       （`bed_relief` の焼き忘れ）
            //     ・1/1/0.50/1.00/0.00 → 音量を書いたのに**再生位置が進んでいない** ＝ 鳴っていない
            //       （`rules/work-style.md` §2-3「初期化されたかは通し番号か経過時間で見る」）
            //   ⚠ **倍率は自分が書いた値ではなく engine から読み直したもの**（「書いたつもり」を出さない）。
            //   ⚠ 走行の頭で `0/0/…` でなければ、**前の体験者から持ち越している**
            //     （`TitleScreen.BeginTitle` の `HorrorRelief.Reset` が効いていない）。
            _sb.Append(" relief=").Append(HorrorRelief.Enabled ? 1 : 0)
               .Append('/').Append(HorrorRelief.ChangeCount)
               .Append('/').Append(_relief == null ? "-" : _relief.ListenerVolume.ToString("F2"))
               .Append('/').Append(_relief == null ? "-" : _relief.BgmGain.ToString("F2"))
               .Append('/').Append(_relief == null ? "-" : _relief.BgmTimeSec.ToString("F1"));
            //   visitor = タブレットの口（0185 / 0187）。
            //     **`<口が開いているか>/<受けた累計>/<枠の受理番号>/<書いた受理番号>/<書いた回数>`**
            //   ⚠ 1 つ目が 0 なら :8090 を開けていない ＝ タブレットは繋げない（卓は関わらないので他に手掛かりが無い）。
            //   ⚠ 2 つ目は入力の累計（work-style §2-2）。タブレットで押したのに増えないなら届いていない。
            //   ⚠ 枠の受理番号が書いた受理番号より大きいまま本編（RUN）なら、それは次の人の分（正常）。
            //     注意書きの段（Wait）で大きいままなら TitleScreen の書き込みが効いていない。
            //   ⚠ `lang` / `relief`（上）が実際の値。ここは経路の証拠で、値は上を見る。
            VisitorPortal? portal = _show != null ? _show.Portal : null;
            _sb.Append(" visitor=").Append(portal != null && portal.IsListening ? 1 : 0)
               .Append('/').Append(portal != null ? portal.Received : 0)
               .Append('/').Append(VisitorPrefs.PendingSeq)
               .Append('/').Append(VisitorPrefs.AppliedSeq)
               .Append('/').Append(VisitorPrefs.ApplyCount);
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
                    // thr=1 は「配信端末が熱い」（v0.11.0 以降アプリは絞っていない）。
                    if (h != null && h.IsHot) _sb.Append(" thr=1");
                }
            }
            Emit(_sb.ToString());
        }

        private static void Emit(string body) => Debug.Log($"{Tag} t={Now:F2} {body}");
    }
}
