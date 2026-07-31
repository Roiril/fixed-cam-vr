#nullable enable
using System;
using System.Text;
using FixedCamVr.Streaming;
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
    ///   - <b>遷移</b>: 起きた瞬間に 1 行（phase / zone / screen / seg / take / cue / intro / storm / rec）
    ///   - <b>サマリ</b>: <see cref="SummaryIntervalSec"/> 秒ごとに 1 行（fps・遅延・受信 fps・砂嵐の累計）
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
        private TimelineDirector? _timeline;
        private SignalLostFx? _signal;
        private GlitchFx? _glitch;
        private CueScheduler? _cues;
        private SegmentRecorder? _recorder;
        private ScreenOverlayController? _overlay;
        private LapCounter? _lap;
        private CourseFrame? _frame;
        private ShowControlClient? _show;

        // --- 購読状態（多重購読を防ぐ）---
        private bool _subSwitch, _subRun, _subCues, _subRegistry;

        // --- 遷移検出のための前回値 ---
        private IntroStage _lastStage = IntroStage.Off;
        private string _lastTakeId = "";
        private string _lastCueId = "";
        private bool _lastStormOn;
        private bool _lastTrackingFrozen;
        private bool _lastRecording;
        private bool _lastGateOpen = true;
        private string _lastConfig = "";

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
            Emit($"ev=boot build=dev dev={SystemInfo.deviceModel} rate={DisplayRateInfo.CurrentHz:F0}");
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
            if (_timeline == null) _timeline = FindObjectOfType<TimelineDirector>();
            if (_signal == null) _signal = FindObjectOfType<SignalLostFx>();
            if (_glitch == null) _glitch = FindObjectOfType<GlitchFx>();
            if (_cues == null) _cues = FindObjectOfType<CueScheduler>();
            if (_recorder == null) _recorder = FindObjectOfType<SegmentRecorder>();
            if (_overlay == null) _overlay = FindObjectOfType<ScreenOverlayController>();
            if (_lap == null) _lap = FindObjectOfType<LapCounter>();
            if (_frame == null) _frame = FindObjectOfType<CourseFrame>();
            if (_show == null) _show = FindObjectOfType<ShowControlClient>();

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
            _subSwitch = _subRun = _subCues = _subRegistry = false;
        }

        // ---------------------------------------------------------------- イベント

        // ショーの時計（人の居場所）。画面が演出で塞がっていても進むのが正しい挙動。
        private void OnZoneCommitted(int camera)
            => Emit($"ev=zone cam={camera} lap={(_lap != null ? _lap.CurrentLap : -1)}");

        // 画面の切替（誰が画面を握ったか）。src=Zone/Manual/Override/Insert/External。
        private void OnSwitchCommitted(int camera, CameraSwitchDirector.SwitchSource src)
            => Emit($"ev=screen cam={camera} src={src}");

        // 区間の進入（周回つき）。演出の武装・区間 post / BGM・録画の駆動点と同じ首。
        private void OnCameraEntered(int camera, int lap)
            => Emit($"ev=seg lap={lap} cam={camera}");

        private void OnPhaseChanged(ShowPhase phase)
            => Emit($"ev=phase v={phase} lap={(_run != null ? _run.Lap : -1)} " +
                    $"elapsed={(_run != null ? _run.RunElapsedSec : 0f):F1}");

        private void OnActiveChanged(int index) => Emit($"ev=active cam={index}");

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
            // 導入演出の段（Off/Black/Real/Degrade/Structure/Frame/Swap/Settle …）
            if (_intro != null && _intro.Stage != _lastStage)
            {
                _lastStage = _intro.Stage;
                IntroWeights w = _intro.Weights;
                // fresh / centered は段 4 → 段 5 の進行条件。false のまま足踏みすると
                // 最後の段（枠の中がカメラ映像へ変わる）が出ないので、必ず一緒に出す。
                Emit($"ev=intro stage={_lastStage} hold={(_intro.Holding ? 1 : 0)} " +
                     $"fresh={(_intro.LiveFresh ? 1 : 0)} centered={(_intro.FrameCentered ? 1 : 0)} " +
                     $"pass={w.passthrough:F2} live={w.live:F2} frame={w.frame:F2} edge={w.edge:F2}");
            }

            // 演出（Take）の出入り。TakeRunner のログと突き合わせると理由まで分かる。
            string takeId = _timeline != null ? _timeline.ActiveTakeId : "";
            if (takeId != _lastTakeId)
            {
                if (!string.IsNullOrEmpty(_lastTakeId)) Emit($"ev=take id={_lastTakeId} st=end");
                if (!string.IsNullOrEmpty(takeId)) Emit($"ev=take id={takeId} st=begin");
                _lastTakeId = takeId;
            }

            // 画面に出ている素材（cue）。演出のカットが素材へ切り替わった瞬間が見える。
            string cueId = _overlay != null && _overlay.Current != null ? (_overlay.Current.id ?? "?") : "";
            if (cueId != _lastCueId)
            {
                if (!string.IsNullOrEmpty(_lastCueId)) Emit($"ev=cue id={_lastCueId} st=off");
                if (!string.IsNullOrEmpty(cueId)) Emit($"ev=cue id={cueId} st=on");
                _lastCueId = cueId;
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

            // 端末内録画（3 周目の素材はここが録れていないと黙って飛ぶ）。
            if (_recorder != null)
            {
                bool rec = _recorder.IsRecording;
                if (rec != _lastRecording)
                {
                    _lastRecording = rec;
                    Emit($"ev=rec v={(rec ? "start" : "stop")} mb={_recorder.RunBytes / 1048576.0:F1} " +
                         $"cam={(_registry != null ? _registry.ActiveIndex : -1)}");
                }
            }

            // 区間進行のゲート（導入・終了で閉じる single choke point）。
            if (_cues != null && _cues.ShowGateOpen != _lastGateOpen)
            {
                _lastGateOpen = _cues.ShowGateOpen;
                Emit($"ev=gate v={(_lastGateOpen ? "open" : "closed")}");
            }
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
            if (_intro != null && _intro.Active) _sb.Append(" istage=").Append(_intro.Stage);

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

            // 砂嵐は「いまのレベル」と「累計の割合」を両方出す。体感の判定は割合の方が効く。
            if (_signal != null)
            {
                _sb.Append(" storm=").Append(_signal.Level.ToString("F2"));
                _sb.Append(" stormPct=").Append((_liveSec > 0f ? _stormSec / _liveSec * 100f : 0f).ToString("F1"));
                _sb.Append(" weakPct=").Append((_liveSec > 0f ? _weakSec / _liveSec * 100f : 0f).ToString("F1"));
            }
            if (_recorder != null)
                _sb.Append(" rec=").Append(_recorder.IsRecording ? 1 : 0)
                   .Append(" recMB=").Append((_recorder.RunBytes / 1048576.0).ToString("F1"));
            if (_frame != null)
                _sb.Append(" reg=").Append(_frame.HasRegistration ? 1 : 0);

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
