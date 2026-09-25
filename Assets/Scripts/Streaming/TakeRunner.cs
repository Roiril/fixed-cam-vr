#nullable enable
using System;
using System.Collections.Generic;
using FixedCamVr.Streaming.Cg;
using FixedCamVr.Streaming.Recording;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>終幕用のスクリーン写真を更新してよい安定区間。</summary>
    public enum EndingShotWindow
    {
        None = 0,
        Trapped = 1,
        Released = 2,
    }

    /// <summary>
    /// 演出（Take）の実行体。<see cref="TakeRunnerLogic"/> の判定を実際の画面へ写す。
    ///   - カット の source（live / inherit / clip / still）→ <see cref="CameraSwitchDirector"/>（dip 付き切替 + 占有）
    ///   - カット の overlay（cueId / assetUrl）→ <see cref="ScreenOverlayController"/>
    ///   - カット の post → <see cref="ShowControlClient.SetInsertPostOverride"/>（演出中の最優先層）
    ///
    /// <see cref="InsertController"/>（v2 の単一カット insert）の後継。show.json が v3 のときだけ動く
    /// （切替は <see cref="ShowControlClient"/> 側。v2 の show.json は従来経路のまま＝後方互換の退避路）。
    ///
    /// 駆動は <see cref="TimelineDirector"/>（区間確定 (lap,camera) を <see cref="NotifyZoneCommitted"/> で forward）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TakeRunner : MonoBehaviour
    {
        [Tooltip("dip-to-black 付き切替と占有（ゾーン自動切替の凍結）を担う CameraSwitchDirector。null なら演出は動かない。")]
        [SerializeField] private CameraSwitchDirector? director;

        [Tooltip("カットのオーバーレイ / 全面差し替えを担う ScreenOverlayController。null なら映像なしで切替のみ。")]
        [SerializeField] private ScreenOverlayController? overlay;

        [Tooltip("演出中の post 層を掛け外しする ShowControlClient。null なら post 上書きなし。")]
        [SerializeField] private ShowControlClient? showControl;

        [Tooltip("演出中だけ BGM を差し替える BgmDirector。null ならシーンから探す。無ければ BGM は区間のまま。")]
        [SerializeField] private BgmDirector? bgmDirector;

        private readonly TakeRunnerLogic _logic = new();

        // 通過ライン（床の線分）の横断検出。show.json layout.lines から作る。
        private readonly LineCrossLogic _lineCross = new();

        // lineId → スロット index の割当。**session 内で不変**（layout が更新されても並べ替えない）。
        // Def.lineIndex は SetTakes 時に確定するので、ここが動くと走行中の once 状態と食い違う。
        private readonly List<string> _lineSlots = new();

        // ライントリガーを持つ演出があるか（無ければ HMD 位置を引きに行かない）。
        private bool _hasLineTakes;
        private bool _warnedNoHeadProvider;
        private string _warnedMissingLines = "";

        // 締めの線（3 周目 A の凍結点・`TakeSchema.ResolveClosingLineId`）。id は台本から、
        // スロットは _lineSlots から、実体の有無は layout から。③a「止まってください！」の引き金（0233）。
        private string _closingLineId = "";
        private int _closingLineSlot = -1;
        private bool _closingLineDefined;
        private bool _lastClosingLineCrossed;

        // 横断判定用の前回時刻（dt は Now の差分で作る＝テストの時刻源差し替えでも動く）。
        private bool _hasLastNow;
        private float _lastNow;

        // 実行に必要な定義本体（_logic の Def と同じ並び）。
        private ShowTakeDef[] _takes = Array.Empty<ShowTakeDef>();

        // cueId → 素材定義 / URL 解決（ShowControlClient から注入）。
        private Func<string, OverlayCueData?>? _cueResolver;
        private Func<string, string>? _urlResolver;

        // 現カットでオーバーレイを出したか（演出終了時に自分が出した分だけ止める）。
        private bool _stepOverlayPlayed;
        // 現カットが untilClipEnd で、オーバーレイの終了を待っているか。
        private bool _awaitingClipEnd;
        // 待っている発火のトークン（ScreenOverlayController.PlayCue の戻り）。
        // Current==null を直接見るとロード中を「終わった」と誤判定する（2026-07-26 監査 HIGH）。
        private int _clipToken = -1;

        // 報告開始から復帰までの動画。途中で指を離してもこのトークンは止めない。
        private int _markResponseToken = -1;
        private bool _markResponsePlaying;
        private bool _markResponseReported;
        private bool _markResponseZoneExit;

        // 現カットが開いている端末内録画（source:"rec"）。所有はここ — カットが変わったら必ず閉じる。
        private RecordedFramePlayer? _stepFrames;

        // その録画が指す (周, カメラ)。テレメトリが「どの区間の録画が再生されたか」を言うのに要る
        // （player 自身はファイルパスしか知らない）。
        private int _stepFramesLap = -1;
        private int _stepFramesCam = -1;

        /// <summary>
        /// いま再生している端末内録画（テレメトリ用・所有はこのクラス）。null なら録画カットではない。
        /// <c>PresentedCount</c> が「開けた」ではなく「**画に出た**」の証拠になる。
        /// </summary>
        public RecordedFramePlayer? ActiveRecording => _stepFrames;

        /// <summary>再生中の録画が指す周（1 始まり）。無ければ -1。</summary>
        public int ActiveRecordingLap => _stepFrames != null ? _stepFramesLap : -1;

        /// <summary>再生中の録画が指すカメラ index。無ければ -1。</summary>
        public int ActiveRecordingCamera => _stepFrames != null ? _stepFramesCam : -1;

        // この演出が BGM を占有したか（占有した時だけ終了時にレーンへ返す）。
        private bool _bgmOverrideActive;

        // 端末内録画の在り処を引く（source:"rec" の解決）。null なら rec カットは飛ばす。
        private SegmentRecorder? _recorder;

        // 映像の上に人形を描く層（step.cg）。null なら CG は出ない（機能未配置でも演出は動く）。
        private ShowCgLayer? _cgLayer;
        private CameraFeelFx? _feelFx;
        private EndingFrameCapture? _endingCapture;
        private string _endingTakeId = "";
        private bool _endingReleased;

        /// <summary>
        /// 入れ替わりのノイズが覆い切るのを待っている素材。覆い切る前に演出が畳まれたら
        /// 誰も引き取らないので、<see cref="ReleaseStepState"/> が閉じる。
        /// </summary>
        private OverlayCueData? _pendingSwapCue;

        // スクリーンの外の闇で目が開く異変（step.eyes）。null なら目は出ない（同上）。
        private AnomalyEyes? _eyes;

        // 人形の呼びかけ（step.dollCall・canon/LEDGER.md 0109）。null なら鳴らないだけで画は進む。
        private ShowSoundDirector? _sound;

        // 時刻源。既定は Time.time。EditMode テストは時間が進まないため差し替える
        //（純ロジックは既に時刻を引数で受けており、束縛しているのはこの実行体だけ）。
        private Func<float>? _timeSource;
        private float Now => _timeSource != null ? _timeSource() : Time.time;

        /// <summary>演出が画面を占有中か（HUD・診断用）。</summary>
        public bool IsActive => _logic.IsActive;

        /// <summary>
        /// 走行中の演出の id（卓のモニタ用。走っていなければ空）。
        /// 本番中に「いま画面を握っているのは誰か」を人が知る唯一の手段なので公開する。
        /// </summary>
        public string ActiveTakeId => _logic.IsActive ? TakeId(_logic.ActiveTakeIndex) : "";

        /// <summary>
        /// 終幕写真の更新区間。台本の段だけではなく、素材と CG が material へ実際に届き、
        /// 遷移が終わっていることまで確認してから開く。
        /// </summary>
        public EndingShotWindow EndingShotWindow
        {
            get
            {
                bool endingTakeActive = _logic.IsActive
                                        && !string.IsNullOrEmpty(_endingTakeId)
                                        && ActiveTakeId == _endingTakeId;
                ShowStepDef? step = GetStep(_logic.ActiveTakeIndex, _logic.ActiveStepIndex);
                bool liveSource = step != null
                                  && TakeSchema.NormalizeSource(step.source, out _) == TakeSchema.SourceLive;
                bool primaryActive = overlay != null && overlay.Strength > 0.001f;
                bool primaryMatches = step != null && !string.IsNullOrEmpty(step.cueId)
                                      && primaryActive && overlay!.AppliedCueId == step.cueId;
                bool secondaryActive = _feelFx != null && _feelFx.Overlay2Strength > 0.001f;
                bool secondaryMatches = step != null && !string.IsNullOrEmpty(step.overlay2CueId)
                                        && secondaryActive;
                bool cgActive = _cgLayer != null && _cgLayer.IsVisible;
                bool trappedCgVisible = _cgLayer != null && _cgLayer.DollVisible;
                bool swapActive = director != null && director.SwapActive;
                bool transitionActive = director != null && director.Dipping;
                return ResolveEndingShotWindow(endingTakeActive, _logic.IsWaitingForMark,
                    _endingReleased, liveSource, primaryMatches, secondaryMatches, trappedCgVisible,
                    primaryActive, secondaryActive, cgActive,
                    swapActive, transitionActive);
            }
        }

        /// <summary>終幕写真の窓判定。Unity の描画状態を引数へ分離し、EditMode で固定する。</summary>
        public static EndingShotWindow ResolveEndingShotWindow(
            bool endingTakeActive, bool waitingForMark, bool reportReleased, bool liveSource,
            bool trappedPrimaryVisible, bool trappedSecondaryVisible, bool trappedCgVisible,
            bool primaryActive, bool secondaryActive, bool cgActive,
            bool swapActive, bool transitionActive)
        {
            if (!endingTakeActive || swapActive || transitionActive) return EndingShotWindow.None;
            if (!reportReleased && waitingForMark
                && trappedPrimaryVisible && trappedSecondaryVisible && trappedCgVisible)
                return EndingShotWindow.Trapped;
            if (reportReleased && liveSource && !primaryActive && !secondaryActive && !cgActive)
                return EndingShotWindow.Released;
            return EndingShotWindow.None;
        }

        /// <summary>
        /// <b>いま画面を取っているカットの素材 id</b>（<c>steps[].cueId</c>）。演出が走っていなければ空。
        ///
        /// 音が「いま何が映っているか」を知るための唯一の口（2026-09-03・<c>canon/LEDGER.md</c> 0131）。
        /// ⚠ <b>飛ばしたカットでは書かない</b>（画面を取っていないので）。
        /// </summary>
        public string ActiveStepCueId => _logic.IsActive ? _activeStepCueId : "";

        private string _activeStepCueId = "";

        /// <summary>
        /// <b>人形の呼びかけ（<c>steps[].dollCall</c>）を持つカットが、いま画面を取っているか</b>
        /// （2026-09-06・<c>canon/LEDGER.md</c> 0175）。
        ///
        /// 心音（<see cref="SoundBedLogic"/>）が<b>始まる縁を作るためだけ</b>に読む。ユーザー指定は
        /// 「2-C で連続する人形視点が終わった後から」で、その連なりの最後のカットが
        /// <b>呼びかけを持つカット</b>（＝ 人形が追いつく所）。ここが立ってから降りた瞬間が
        /// 「連なりが終わった」。
        ///
        /// ⚠ <b>素材 id の頭（<c>pov</c>）では判じられない。</b> 同じ 2 周目 C の予備動作
        /// （<c>pov_0</c>・低い視点が 1 度だけ割り込む）も同じ頭を持つので、
        /// そちらで心音が始まってしまう（**連なりではない**）。呼びかけは接近の最後のカットに
        /// 1 つだけ付いているので、これが連なりの終わりの唯一の目印になる。
        /// </summary>
        public bool DollCallShowing => _logic.IsActive && _activeStepDollCall;

        private bool _activeStepDollCall;
        private bool _activeStepDollReplacement;

        /// <summary>
        /// <b>人形の追いつき（<c>steps[].dollCall</c>）を最後まで表示した回数</b>。
        /// 次カットへの自然遷移か、演出の自然完了だけを数える。離脱・watchdog・中止・未表示カットは数えない。
        /// ラン開始（<see cref="ResetRun"/>）で 0 に戻る。
        /// </summary>
        public int DollCatchUpCompletedCount { get; private set; }

        /// <summary>
        /// <b>走行中の演出を外部要因で中止した回数</b>。
        /// 通信面が途中の表示を即座に畳むための観測値。ラン開始（<see cref="ResetRun"/>）で 0 に戻る。
        /// </summary>
        public int PresentationAbortCount { get; private set; }

        /// <summary>人形の差し替えカットが実際に CG を表示しているか。</summary>
        public bool DollReplacementShowing => IsDollReplacementShowing(
            _logic.IsActive, _activeStepDollReplacement, _cgLayer != null && _cgLayer.DollVisible);

        /// <summary>
        /// 最後の ending take で、報告前の CG 人形化を実際に描いた足位置を返す。
        /// 通常 take、人の代役、乱れ遷移中、報告後は対象外。
        /// </summary>
        public bool TryGetEndingDollFootUv(out Vector2 uv)
        {
            uv = new Vector2(0.5f, 0.5f);
            bool endingTakeActive = _logic.IsActive
                                    && !string.IsNullOrEmpty(_endingTakeId)
                                    && ActiveTakeId == _endingTakeId;
            if (!CanUseEndingDollFoot(endingTakeActive, _endingReleased,
                                      DollReplacementShowing, DollReplacementTransitioning))
                return false;
            return _cgLayer != null && _cgLayer.TryGetLastRenderedDollFootUv(out uv);
        }

        public static bool CanUseEndingDollFoot(bool endingTakeActive, bool reportReleased,
                                                bool dollReplacementShowing, bool transitionActive)
            => endingTakeActive && !reportReleased && dollReplacementShowing && !transitionActive;

        /// <summary>
        /// 人形視点の素材が実際に画面へ混ざっているか。
        /// cue id だけでは動画の Prepare 前にも立つため、overlay の実効強度と対で見る。
        /// </summary>
        public bool DollPovShowing => IsDollPovShowing(
            _logic.IsActive, _activeStepCueId,
            overlay != null ? overlay.AppliedCueId : "",
            overlay != null ? overlay.Strength : 0f);

        /// <summary>CG 人形を出すカットが、乱れ遷移でまだ覆われているか。</summary>
        public bool DollReplacementTransitioning => _logic.IsActive && _activeStepDollReplacement
                                                     && director != null && director.Dipping;

        /// <summary>CG 人形化の乱れ遷移の進み。遷移外は 1。</summary>
        public float DollReplacementTransition01 => director != null ? director.TransitionProgress01 : 1f;

        public static bool IsDollReplacementShowing(bool active, bool stepWantsDoll, bool dollVisible)
            => active && stepWantsDoll && dollVisible;

        public static bool IsDollPovCue(string cueId)
            => cueId == "pov_0" || cueId == "pov_1" || cueId == "pov_2"
               || cueId == "pov_3" || cueId == "pov_4";

        public static bool IsDollPovShowing(bool active, string activeCueId, string appliedCueId, float strength)
            => active && strength > 0.001f && activeCueId == appliedCueId && IsDollPovCue(appliedCueId);

        public bool Suppressed => _logic.Suppressed;

        /// <summary>著作された演出が抑止されずに画面を取っているか。</summary>
        public bool AnomalyShowing => IsAnomalyShowing(_logic.IsActive, _logic.Suppressed);

        public static bool IsAnomalyShowing(bool active, bool suppressed)
            => active && !suppressed;

        /// <summary>
        /// <b>別の場所（異世界）の素材 id の頭</b>（2026-09-03・<c>canon/LEDGER.md</c> 0131）。
        ///
        /// ⚠⚠ <b>現場の著作（<c>show.json</c>）に踏み込んでいる唯一の場所。</b>
        /// 卓で cue の名前を変えると<b>音が黙って止まり、時計も ? にならない</b>（画は 1 画素も
        /// 変わらない）。だから <c>analyze-xp-log.py</c> が「異世界のカットが出たのに風が鳴って
        /// いない」を FAIL にする。
        /// ⚠ 素材の側に「これは異世界だ」と書ける欄が無いので名前で判じている。
        /// 欄を足すなら <c>rules/streaming.md</c> の契約・卓の編集面・端末キャッシュを対で直すこと。
        /// </summary>
        public const string OtherworldCuePrefix = "backrooms";

        /// <summary>
        /// <b>いま画面を取っているカットが別の場所（異世界）か</b>。
        ///
        /// 読む側は 2 つ — 音（<see cref="ShowSoundDirector"/>：風が鳴って劇伴が切れる）と
        /// 時計（<see cref="ScreenOsd"/>：時刻も周回も <c>?</c> になる・<c>canon/LEDGER.md</c> 0167）。
        /// ⚠ <b>判定はこの 1 本だけ。</b> 2 か所に書くと、片方だけ直したときに
        /// 「風は鳴っているのに時計は出たまま」が黙って起きる。
        /// </summary>
        public bool OtherworldActive => IsOtherworldCue(ActiveStepCueId);

        /// <summary>素材 id が異世界のものか（純判定）。</summary>
        public static bool IsOtherworldCue(string cueId)
            => !string.IsNullOrEmpty(cueId)
               && cueId.StartsWith(OtherworldCuePrefix, System.StringComparison.Ordinal);

        /// <summary>
        /// カット 1 つを解決した瞬間に上がる<b>観測専用</b>イベント。
        /// 引数は (演出 id, カット index, source, camera, 画面を取ったか, 理由トークン)。
        ///
        /// <b>これが無いと「演出は走ったのに画に何も出ていない」を検出できない。</b>
        /// §6.4 で全カットが飛んだ演出は画面を掴まずに終わるが、<see cref="ActiveTakeId"/> は立つので
        /// 外からは <c>begin</c> → <c>end</c> が正常に見え、解析は「演出 OK」と判定してしまう
        /// （2026-07-31 の事故と同型）。
        ///
        /// 理由トークンは <see cref="StepOkReason"/> / <see cref="StepSkipCameraRange"/> /
        /// <see cref="StepSkipNoAsset"/> / <see cref="StepSkipNoRecording"/>。
        /// </summary>
        public event Action<string, int, string, int, bool, string>? StepResolved;

        /// <summary>カットが画面を取れた（<see cref="StepResolved"/> の理由トークン）。</summary>
        public const string StepOkReason = "ok";

        /// <summary>live のカメラ index が registry の範囲外だったので飛ばした。</summary>
        public const string StepSkipCameraRange = "camrange";

        /// <summary>clip / still の素材が 1 つも解決できなかったので飛ばした。</summary>
        public const string StepSkipNoAsset = "noasset";

        /// <summary>指定の (周, カメラ) の端末内録画が無かったので飛ばした。</summary>
        public const string StepSkipNoRecording = "norec";

        /// <summary>時刻源を差し替える（EditMode テスト用。null で <c>Time.time</c> に戻る）。</summary>
        public void SetTimeSource(Func<float>? source) => _timeSource = source;

        private void Awake()
        {
            // 演出を捨てたら必ず言う（黙って消さない）。著作した山場が出ないのは事故なので警告で出す。
            _logic.TakeDropped = (index, reason) => Debug.LogWarning(
                $"[TakeRunner] 演出が出ないまま終わった: take={TakeId(index)}（{DropReasonText(reason)}）");

            // 実配置では本コンポーネントは [Tracker]、Director / overlay / showControl は Screen に居る
            // （InsertController と同じ構図）。SerializeField 未割当のシーンでも動くよう、同 GameObject →
            // シーン全体の順で解決する（CameraSwitchDirector が registry を解決するのと同じ流儀）。
            if (director == null) director = GetComponent<CameraSwitchDirector>();
            if (director == null) director = FindObjectOfType<CameraSwitchDirector>();
            if (overlay == null) overlay = GetComponent<ScreenOverlayController>();
            if (overlay == null) overlay = FindObjectOfType<ScreenOverlayController>();
            if (showControl == null) showControl = GetComponent<ShowControlClient>();
            if (showControl == null) showControl = FindObjectOfType<ShowControlClient>();
            _recorder = FindObjectOfType<SegmentRecorder>();
            if (bgmDirector == null) bgmDirector = FindObjectOfType<BgmDirector>();
            // CG レイヤはスクリーンのマテリアルを共有する必要があるので overlay と同じ GameObject に置く。
            // シーン未再生成でも効くよう、無ければ自分で載せる（prefab の SerializeField 欠落で
            // 機能が全死した過去の事故を繰り返さない）。
            _cgLayer = FindObjectOfType<ShowCgLayer>();
            if (_cgLayer == null && overlay != null) _cgLayer = overlay.gameObject.AddComponent<ShowCgLayer>();
            _feelFx = overlay != null ? overlay.GetComponent<CameraFeelFx>() : FindObjectOfType<CameraFeelFx>();

            // 終幕写真もスクリーンの material と同居させる。旧シーンに配線が無くても自動で載る。
            _endingCapture = FindObjectOfType<EndingFrameCapture>();
            if (_endingCapture == null && overlay != null)
                _endingCapture = overlay.gameObject.AddComponent<EndingFrameCapture>();
            _endingCapture?.Bind(this);

            // 闇の目（canon/LEDGER.md 0075）。**シーンに居なければ自分で載せる** — 群れは
            // 頭に付いて動くだけで親を選ばないので、どこに載っていても同じ絵になる。
            // prefab / シーンの配線漏れで機能が全死した過去の事故を繰り返さない。
            _eyes = FindObjectOfType<AnomalyEyes>();
            if (_eyes == null)
            {
                // ⚠ **自分の GameObject には載せない。** AnomalyEyes は自分の transform を
                //    頭の位置へ運ぶので、[Tracker] に載せると周回・ゾーンの居場所ごと動く。
                var go = new GameObject("[Eyes]");
                go.transform.SetParent(transform, worldPositionStays: false);
                _eyes = go.AddComponent<AnomalyEyes>();
            }

            // 人形の呼びかけ（canon/LEDGER.md 0109）。**無ければ自分で載せない** — 音の器は
            // `[Sound]` が丸ごと持っており（敷く音 11 本 + 一撃の声 6 本）、ここで作ると
            // 二重に鳴る。`menu scene` が焼いていない構成では、呼びかけだけが黙る。
            _sound = FindObjectOfType<ShowSoundDirector>();

            // 遷移層でカットが画面に出ないまま上書きされたら報告する（演出層と同じ規律を通す）。
            if (director != null) director.TransitionPreempted += OnTransitionPreempted;
        }

        private static string DropReasonText(TakeRunnerLogic.DropReason reason) => reason switch
        {
            TakeRunnerLogic.DropReason.ScreenBusyAtExit => "別の演出 / ライブ卓が画面を使用中のまま区間が終わった",
            TakeRunnerLogic.DropReason.LostToAnotherTake => "同じ区間の別の演出が先に選ばれた",
            TakeRunnerLogic.DropReason.CarryExpired => "待ち続けたが上限（本数 / 時間）に掛かった",
            _ => "待っているあいだに、塞いでいた演出が人の操作で消えた",
        };

        private void OnTransitionPreempted() => Debug.LogWarning(
            "[TakeRunner] カットが一度も画面に出ないまま次のカットに上書きされた（尺が遷移より短い）");

        private void OnDestroy()
        {
            if (director != null) director.TransitionPreempted -= OnTransitionPreempted;
        }

        private void OnEnable()
        {
            // 通過ラインは layout（ライブ / 端末キャッシュ / 焼き込み）から来る。
            if (showControl != null) showControl.LayoutChanged += ApplyLinesFromLayout;
            ApplyLinesFromLayout();
        }

        private void OnDisable()
        {
            if (showControl != null) showControl.LayoutChanged -= ApplyLinesFromLayout;
        }

        /// <summary>cueId → 素材定義の解決関数を注入する（ShowControlClient に集約）。</summary>
        public void SetCueResolver(Func<string, OverlayCueData?> resolver) => _cueResolver = resolver;

        /// <summary>素材 URL（sa:// / 相対）の解決関数を注入する（ShowControlClient に集約）。</summary>
        public void SetUrlResolver(Func<string, string> resolver) => _urlResolver = resolver;

        /// <summary>終幕の合図が指す take を設定する。空なら写真の窓は開かない。</summary>
        public void ConfigureEndingCapture(string? afterTakeId)
        {
            _endingTakeId = afterTakeId ?? "";
            _endingCapture?.Bind(this);
        }

        /// <summary>終了処理が表示状態を畳む前に、終幕写真を固定する。</summary>
        public void FreezeEndingShots(bool forceTrappedCapture = false)
        {
            if (_endingCapture == null) return;
            if (forceTrappedCapture) _endingCapture.Freeze(EndingShotWindow.Trapped);
            else _endingCapture.Freeze();
        }

        /// <summary>
        /// ライブ卓の抑止（activeCue 非空 / cameraOverride 非 null）を通知する。
        ///
        /// **走行中の演出は即畳む**（§6.3-6「ライブ卓が最優先」）。抑止フラグだけ立てて走らせ続けると、
        /// 演出のカット終了が卓の cue を <c>StopOverlay</c> で消し、演出の復帰 dip が卓の cameraOverride を
        /// 外す（オペレータが介入した瞬間ほど壊れる。2026-07-26 監査 MED）。
        /// 卓がカメラも握っている（override 中）ならカメラは返さず占有だけ解く。
        /// </summary>
        public void SetSuppressed(bool suppressed)
        {
            if (suppressed && _logic.IsActive)
                CleanupActive(releaseScreen: director == null || !director.OverrideActive);
            _logic.SetSuppressed(suppressed);
        }

        /// <summary>
        /// 走行中の演出だけを畳んで画面をライブへ返す（緊急の出口）。
        ///
        /// ⚠ <see cref="SetSuppressed"/> では代替できない。卓の「■ 画面を取り返す」が送るのは
        /// control の書き換えだが、**タイムラインが自動発火した演出は activeCue を使わず空のまま走る**ため
        /// 「空を空にする」stopCue は状態変化ゼロ＝ no-op になり、suppressed も false→false で畳まれなかった
        /// （2026-07-28 まで卓の停止ボタンが本番の演出に効いていなかった実バグ）。
        ///
        /// 抑止フラグは立てない — 中止した直後から次の演出は通常どおり武装する（ラン全体を殺さない）。
        /// once 発火済み・周回はそのまま（体験をやり直しにしない。全部戻すのは <see cref="ResetRun"/>）。
        /// </summary>
        public void AbortActive()
        {
            // この直後に overlay / CG / split が畳まれる。既に取れた最終フレームを先に固定する。
            _endingCapture?.FreezeIfCaptured();
            // 演出が自然終了した直後でも、卓の「■ 画面を取り返す」は通信表示を止める明示信号になる。
            // active / chain 中は CleanupActive が数えるため、入口では非 active 時だけ数えて二重加算を避ける。
            if (!_logic.IsActive && !_chainPending)
            {
                PresentationAbortCount++;
                return;
            }
            CleanupActive(releaseScreen: director == null || !director.OverrideActive);
            Debug.Log("[TakeRunner] 走行中の演出を中止した（卓からの緊急停止）");
        }

        /// <summary>タイムライン区間から演出定義を（再）構築する。走行中の演出は先に畳む（不変条件 8）。</summary>
        public void SetTakes(ShowTimelineSegmentDef[]? segments)
        {
            CleanupActive();
            if (segments == null)
            {
                _takes = Array.Empty<ShowTakeDef>();
                _logic.SetDefs(Array.Empty<TakeRunnerLogic.Def>());
                return;
            }

            var takes = new List<ShowTakeDef>();
            var defs = new List<TakeRunnerLogic.Def>();
            bool anyLine = false;

            // ⚠⚠ **終幕の合図が指す演出は、旗が立っていても報告では畳めない。**
            //    EndingCueLogic は「指した演出が走らなくなった」で撃つので、報告で畳むと
            //    著作された残りのカット（現行の台本なら「現実へ戻る 3 秒」）を飛ばして終幕が始まる。
            //    データで上書きできない構造ガードとして、ここで旗ごと落とす。
            //    ⚠ `ShowRunDirector` は参照しない（ShowControlClient が既に run を持っている）。
            string outroAnchorId = showControl?.RunConfig?.outro?.afterTakeId ?? "";
            foreach (ShowTimelineSegmentDef? seg in segments)
            {
                if (seg?.takes == null) continue;
                foreach (ShowTakeDef? t in seg.takes)
                {
                    if (t == null) continue;
                    takes.Add(t);
                    // ライントリガーは lineId → スロット（未知 id も枠を取る = 線が後から来ても index が動かない）。
                    // **ラインが未指定でも onLine は下ろさない**。下ろすと時刻トリガー扱いになり
                    // offsetSec(=0) で区間進入と同時に発火する ＝ 契約「空 lineId は発火しない」の真逆になる
                    // （2026-07-27 監査。lineIndex=-1 は TakeRunnerLogic.IsDue が常に false を返す枠）。
                    bool onLine = t.IsLine;
                    bool hasLine = onLine && !string.IsNullOrEmpty(t.lineId);
                    anyLine |= hasLine;
                    // カット側の線待ち（durKind:"untilLine"）も横断検出を要求する。**ここを落とすと
                    // TickLines が回らず、線待ちのカットは watchdog まで永久に終わらない。**
                    int[] stepLines = BuildStepLineIndices(t);
                    foreach (int sl in stepLines) if (sl >= 0) { anyLine = true; break; }
                    bool isOutroAnchor = !string.IsNullOrEmpty(outroAnchorId) && t.id == outroAnchorId;
                    if (isOutroAnchor && t.dismissible)
                        Debug.LogWarning($"[TakeRunner] 「報告で消える」を指定した演出が終幕の合図" +
                                         $"（run.outro.afterTakeId）なので無効にした（take={t.id}）。" +
                                         $"畳むと残りのカットを飛ばして終幕が早撃ちされる");
                    defs.Add(new TakeRunnerLogic.Def
                    {
                        dismissible = t.dismissible && !isOutroAnchor,
                        stepLineIndex = stepLines,
                        lap = seg.lap,
                        camera = seg.camera,
                        onExit = t.IsExit,
                        offsetSec = t.offsetSec,
                        skipWhenMissed = t.SkipWhenMissed,
                        once = t.once,
                        maxDurationSec = t.maxDurationSec,
                        yieldOnZoneChange = t.IsYield,
                        chainWait = t.IsChainWait,
                        stepDurSec = BuildStepDurations(t),
                        onLine = onLine,
                        lineIndex = hasLine ? LineSlot(t.lineId) : -1,
                    });
                    if (t.IsLine && string.IsNullOrEmpty(t.lineId))
                        Debug.LogWarning($"[TakeRunner] ライントリガーの演出にラインが未指定 → 発火しない" +
                                         $"（take={(string.IsNullOrEmpty(t.id) ? "?" : t.id)}）");
                }
            }
            _takes = takes.ToArray();
            // 締めの線は台本から導く（3 周目 A の凍結点）。**枠は必ず取る** — 演出の線と同じ理由で、
            // layout が後から来ても index が動かないようにする。線があれば横断検出も回す。
            _closingLineId = TakeSchema.ResolveClosingLineId(_takes);
            _closingLineSlot = string.IsNullOrEmpty(_closingLineId) ? -1 : LineSlot(_closingLineId);
            anyLine |= _closingLineSlot >= 0;
            _hasLineTakes = anyLine;
            _logic.SetDefs(defs.ToArray());
            // 新しく取った枠も含めてラインを貼り直す（layout が既に来ていれば geometry が入る）。
            ApplyLinesFromLayout();
        }

        /// <summary>
        /// 締めの線の id（3 周目 A の凍結点。<c>TakeSchema.ResolveClosingLineId</c>）。台本に無ければ空。
        /// 自動走行（<c>ShowWalkDebugDriver</c>）が締めのカットの中でこの線を踏みに行く。
        /// </summary>
        public string ClosingLineId => _closingLineId;

        /// <summary>締めの線が layout に実体を持つか。false なら③a は時計の退避路で出る。</summary>
        public bool ClosingLineDefined => _closingLineDefined;

        /// <summary>締めのカットの中で締めの線を踏んだ（③a「止まってください！」の引き金・0233）。</summary>
        public bool ClosingLineCrossed => _logic.ClosingLineCrossed;

        /// <summary>締めの線を踏んだ時刻（締めのカットに入ってからの秒・踏んでいなければ負）。</summary>
        public float ClosingLineCrossedSec => _logic.ClosingLineCrossedSec;

        // lineId のスロットを引く（無ければ末尾へ追加）。並べ替え・削除はしない。
        private int LineSlot(string id)
        {
            int i = _lineSlots.IndexOf(id);
            if (i >= 0) return i;
            _lineSlots.Add(id);
            return _lineSlots.Count - 1;
        }

        /// <summary>
        /// show.json <c>layout.lines</c> を通過ラインへ取り込む。スロット割当（<see cref="_lineSlots"/>）は
        /// 保ったまま geometry だけ差し替えるので、走行中に卓が線を動かしても once 状態は壊れない。
        /// 演出が参照しているのに layout に無い id は「実体の無い枠」＝決して横断しない（発火しない）。
        /// </summary>
        private void ApplyLinesFromLayout()
        {
            ShowLineDef[] src = showControl != null && showControl.Layout != null && showControl.Layout.lines != null
                ? showControl.Layout.lines
                : Array.Empty<ShowLineDef>();

            foreach (ShowLineDef? l in src)
                if (l != null && !string.IsNullOrEmpty(l.id)) LineSlot(l.id);

            var lines = new LineCrossLogic.Line[_lineSlots.Count];
            for (int i = 0; i < lines.Length; i++) lines[i] = LineCrossLogic.Line.Undefined;
            foreach (ShowLineDef? l in src)
            {
                if (l == null || string.IsNullOrEmpty(l.id)) continue;
                int slot = _lineSlots.IndexOf(l.id);
                if (slot < 0) continue;
                int dir = LineCrossLogic.ParseDir(l.dir, out bool known);
                if (!known)
                    Debug.LogWarning($"[TakeRunner] 未知の通過方向 '{l.dir}' → 両方向として扱う（line={l.id}）");
                lines[slot] = LineCrossLogic.Line.Between(l.x1, l.z1, l.x2, l.z2, dir, l.camera);
            }
            _lineCross.SetLines(lines);
            // 締めの線は実体があるときだけ効かせる。実体の無い枠を渡すと決して横切られず、
            // ③a が黙って出なくなる（時計の退避路へ倒す方が安全）。欠けの警告は WarnMissingLines が
            // 同じ id（3 周目 A の untilLine）で出す。
            _closingLineDefined = _closingLineSlot >= 0 && _closingLineSlot < lines.Length
                                  && lines[_closingLineSlot].defined;
            _logic.SetClosingLine(_closingLineDefined ? _closingLineSlot : -1);
            WarnMissingLines(lines);
        }

        // 演出が参照しているのに layout にラインが無い id を 1 回だけ列挙して警告する
        //（黙って発火しない状態を作らない。卓側は保存時に警告を出す）。
        private void WarnMissingLines(LineCrossLogic.Line[] lines)
        {
            var missing = new List<string>();
            void Check(string id)
            {
                if (string.IsNullOrEmpty(id)) return;
                int slot = _lineSlots.IndexOf(id);
                if (slot < 0 || slot >= lines.Length || !lines[slot].defined)
                    if (!missing.Contains(id)) missing.Add(id);
            }
            foreach (ShowTakeDef t in _takes)
            {
                if (t.IsLine) Check(t.lineId);
                // カット側の線待ち（durKind:"untilLine"）も同じ警告に乗せる。実体の無い線を待つと
                // **watchdog まで画が固まる**ので、演出側より沈黙が痛い。
                foreach (ShowStepDef? st in t.steps ?? Array.Empty<ShowStepDef>())
                    if (st != null && st.IsUntilLine) Check(st.lineId);
            }
            string key = string.Join(",", missing);
            if (key == _warnedMissingLines) return;
            _warnedMissingLines = key;
            if (missing.Count > 0)
                Debug.LogWarning($"[TakeRunner] layout.lines に無いラインを参照している演出がある → 発火しない: {key}");
        }

        /// <summary>
        /// 体験者が異変を報告した（左 X / Y の 1 秒長押し）。効き方は 2 つで**排他**
        /// （判断は <see cref="TakeRunnerLogic.NotifyMarkPressed"/>）:
        ///
        ///   ①現カットが <c>durKind:"untilMark"</c> なら、そのカットが畳まれて次のカットへ進む（4 周目 A の締め）
        ///   ②走行中の演出が <c>dismissible</c> なら、**演出ごと畳まれて乱れとともに現実へ戻る**
        ///
        /// どちらにも当たらなければ 1 ビットも変わらない。
        /// </summary>
        /// <returns>
        /// <b>解除が通ったか</b>（2026-08-17・<c>canon/LEDGER.md</c> 0082）。
        /// 連絡の面の文面がこれで分かれる。⚠ <b>「演出が走っていたか」ではない</b> —
        /// 3 周目の録画は走っているが通らないので false になり、
        /// 「異常は検出されませんでした」が返る（それが正しい返事）。
        /// </returns>
        public TakeRunnerLogic.MarkResult NotifyVisitorMark()
        {
            if (_markResponseReported) return TakeRunnerLogic.MarkResult.None;
            TakeRunnerLogic.MarkResult result = _logic.NotifyMarkPressed(Now,
                deferDismiss: _markResponsePlaying);
            if (result == TakeRunnerLogic.MarkResult.Dismissed && _markResponsePlaying)
                _markResponseReported = true;
            if (result == TakeRunnerLogic.MarkResult.Released && ActiveTakeId == _endingTakeId)
                _endingReleased = true;
            return result;
        }

        /// <summary>受付可能な長押しの開始。応答動画を待機姿勢から一度だけ往復再生する。</summary>
        public void NotifyVisitorMarkStarted()
        {
            if (!_logic.IsActive || _markResponsePlaying || _markResponseReported || overlay == null) return;
            int index = _logic.ActiveTakeIndex;
            if (index < 0 || index >= _takes.Length) return;
            string id = _takes[index].markStartCueId;
            if (string.IsNullOrEmpty(id)) return;
            OverlayCueData? cue = _cueResolver?.Invoke(id);
            if (cue == null || !cue.SourceIsVideo)
            {
                Debug.LogWarning($"[TakeRunner] 報告開始の動画 cue が解決できません: {id}");
                return;
            }
            // 一周の終端から先頭姿勢へ戻ったあと、待機画が読めるまで動画の先頭を保つ。
            cue.loop = true;
            cue.fadeInSeconds = 0f;
            cue.fadeOutSeconds = 0f;
            int token = overlay.PlayCue(cue);
            if (token < 0) return;
            _markResponseToken = token;
            _markResponsePlaying = true;
            Debug.Log($"[TakeRunner] 報告開始の応答動画 take={TakeId(index)} cue={id} token={token}");
        }

        /// <summary>走行中のカットが体験者の報告を待っているか（自動走行が押す真似をするのに読む）。</summary>
        public bool IsWaitingForVisitorMark => _logic.IsWaitingForMark;

        /// <summary>走行中のカットが指定した線の横断を待っているか。未登録の id は登録しない。</summary>
        public bool IsWaitingForLine(string id) => _logic.IsWaitingForLine(_lineSlots.IndexOf(id));

        /// <summary>締めのカットに入ってからの秒数（走っていなければ負）。③の時計。</summary>
        public float ClosingTakeSec => _logic.ClosingTakeSec(Now);

        /// <summary>いま押しても受け付けない（締めに入って <c>MarkGraceSec</c> 未満）。</summary>
        public bool IsMarkTooEarly => _logic.IsMarkTooEarly(Now);

        /// <summary>
        /// <b>報告で実際に演出が消えた回数</b>（テレメトリ用）。押した回数（<c>VisitorMarkCount</c>）とは別物 —
        /// 消えない演出の方が多いので、混ぜると「効いたか」がログから分からなくなる。
        /// </summary>
        public int DismissCount => _logic.DismissCount;

        /// <summary>
        /// <b>途中で切れた演出を出し直した回数</b>（テレメトリ用）。体験者が引き返して同じ区間へ戻り、
        /// 報告していなかったので頭からやり直した回数。0 のままなら引き返しが起きていないか、
        /// 引き返しても再演の判定が効いていない — <b>画からはどちらとも見分けが付かない</b>。
        /// </summary>
        public int ReplayCount => _logic.ReplayCount;

        /// <summary>直近に演出が終わった理由（テレメトリ用）。走行前は <c>Completed</c>。</summary>
        public TakeRunnerLogic.EndReason LastEndReason => _lastEndReason;

        private TakeRunnerLogic.EndReason _lastEndReason = TakeRunnerLogic.EndReason.Completed;

        /// <summary>ラン開始（体験者交代）。走行中の演出を畳み、once をクリアする。</summary>
        public void ResetRun()
        {
            CleanupActive();
            // ⚠⚠ CleanupActive は「走行中の演出が無ければ」何もしないで返る。ところが闇の目は
            //    カットより長生きする（0093 の流しきり）ので、**演出が終わった後のラン開始**が
            //    そこをすり抜ける。体験者の交代でだけは、走行中かどうかに関わらず落とす。
            _eyes?.Abort();
            _logic.ResetRun();
            // 前の体験者の位置・横断状態を持ち越さない（ラン開始直後に幽霊の横断を作らない）。
            _lineCross.Reset();
            _hasLastNow = false;
            DollCatchUpCompletedCount = 0;
            PresentationAbortCount = 0;
            _endingReleased = false;
        }

        /// <summary>ゾーン確定（TimelineDirector 経由の deterministic (lap,camera)）を受ける。</summary>
        public void NotifyZoneCommitted(int newLap, int newCam, bool hadPrev, int prevLap, int prevCam)
        {
            // 「次にカメラが切り替わるまで」のカットはここで終わる。**新しい区間の判定より先に**
            // 畳まないと、同じフレームで武装された次の演出とどちらが画面を取るかが順序依存になる。
            // 畳むのは尺だけで、演出を終わらせるのは次の Tick（そこで chainNext が繋ぎ目の黒を省く）。
            if (_markResponsePlaying) _markResponseZoneExit = true;
            else _logic.NotifyZoneChanged(Now);

            TakeRunnerLogic.Decision d =
                _logic.OnZoneCommitted(newLap, newCam, hadPrev, prevLap, prevCam, Now);
            Apply(d, exitAnchored: true);
        }

        private void Update()
        {
            if (_markResponsePlaying && overlay != null &&
                (overlay.HasLooped(_markResponseToken) || overlay.IsFinished(_markResponseToken)))
            {
                _markResponsePlaying = false;
                _markResponseToken = -1;
                if (_markResponseReported) _logic.CompleteDeferredDismiss();
                else
                {
                    OverlayCueData? idle = _cueResolver?.Invoke(_activeStepCueId);
                    if (idle != null) overlay.PlayCue(idle);
                    if (_markResponseZoneExit) _logic.NotifyZoneChanged(Now);
                }
                Debug.Log($"[TakeRunner] 報告開始の応答動画が復帰した reported={_markResponseReported}");
            }
            // untilClipEnd のカットは、オーバーレイが自然終端 / 中止で決着した時点を「終わり」とする。
            // 判定は必ずトークン経由（Current==null はロード中と区別が付かない）。
            if (_awaitingClipEnd && overlay != null && overlay.IsFinished(_clipToken))
            {
                _awaitingClipEnd = false;
                _clipToken = -1;
                _logic.NotifyCurrentStepFinished(Now);
            }

            LineCrossLogic.State[]? lines = TickLines();
            int latest = ResolveLatestZoneCamera();
            TakeRunnerLogic.Decision d = _logic.Tick(Now, latest, lines);
            Apply(d, exitAnchored: false);

            // 締めの線を踏んだ縁を 1 行出す（③a の引き金・0233）。「線は踏まれたのに③a が出ない」と
            // 「そもそも踏んでいない」を実機で切り分けるのはこの行だけ（線の横断そのものは上の
            // TickLines が出すが、締めのカットの中かどうかまでは言わない）。
            bool crossedNow = _logic.ClosingLineCrossed;
            if (crossedNow && !_lastClosingLineCrossed)
                Debug.Log($"[TakeRunner] 締めの線を踏んだ id={_closingLineId} " +
                          $"closing={_logic.ClosingLineCrossedSec:F2}s");
            _lastClosingLineCrossed = crossedNow;

            // 連続の渡し（chainNext）で画面を返さなかったのに、次の演出が始まらなかった場合の安全網。
            // 判定と発火のあいだ（1 フレーム）に持ち越しが期限切れになる等で「次」が消えると、
            // 占有が降りないまま画面が固まる。**凍結が解けない事故を新しく作らない**ため、
            // 次のフレームで必ず決着させる。
            // ⚠ 必ず「次のフレーム以降」で判定する。EndTake は上の Apply(d) の中で呼ばれて
            // _chainPending を立てるので、同じフレームで見ると次の演出は**まだ始まりようがない**。
            // フレーム境界を跨がないと、chainNext は毎回ここで取り消され、避けたかった復帰の暗転が
            // 必ず入る（実測 2026-08-03: 2 回とも「渡す」と「取り消し」が同じ ms に出て、
            // 次の演出はその 9ms 後＝次フレームに始まっていた）。
            if (_chainPending && Time.frameCount > _chainPendingFrame)
            {
                _chainPending = false;
                // 次が始まっていれば渡しは成立（IsActive は d.takeStarted を含む上位条件）。
                if (!_logic.IsActive && director != null && director.InsertActive)
                {
                    Debug.LogWarning("[TakeRunner] 次の演出が始まらなかったので画面を返す（連続の渡しを取り消し）");
                    director.InsertReturn(ResolveLatestZoneCamera());
                }
            }
        }

        // 連続の渡しで画面を返さずに待っている状態。次のフレームで必ず決着させる（上の安全網）。
        private bool _chainPending;
        // _chainPending を立てたフレーム。同一フレームでの誤った取り消しを防ぐ番人。
        private int _chainPendingFrame = -1;

        /// <summary>
        /// 通過ラインの横断検出を体験者の course 空間 XZ で進める。位置が取れない
        /// （<see cref="ShowControlClient.HeadCourseXZProvider"/> 未注入 = 未登録 / HMD 参照なし）間は
        /// null を返し、at=line の演出は発火しない（従来の時刻トリガーは無影響）。
        /// </summary>
        private LineCrossLogic.State[]? TickLines()
        {
            if (!_hasLineTakes || _lineCross.Count == 0) return null;

            Func<Vector2>? head = showControl != null ? showControl.HeadCourseXZProvider : null;
            if (head == null)
            {
                if (!_warnedNoHeadProvider)
                {
                    _warnedNoHeadProvider = true;
                    Debug.LogWarning("[TakeRunner] 体験者の位置が取れないため通過ラインは発火しない" +
                                     "（ZoneLayoutApplier の HMD 参照 / 位置合わせを確認）");
                }
                _lineCross.Reset();
                _hasLastNow = false;
                return null;
            }

            float now = Now;
            float dt = _hasLastNow ? now - _lastNow : 0f;
            _lastNow = now;
            _hasLastNow = true;

            Vector2 xz = head();
            _lineCross.Tick(now, xz.x, xz.y, dt);
            // 横断は事象なので、起きた瞬間に 1 行出す。**これが無いと「線が踏まれたのか、
            // 踏まれたのに効かなかったのか」を実機で切り分けられない**（2026-08-15 に実際に詰まった）。
            LineCrossLogic.State[] st = _lineCross.StateView;
            for (int i = 0; i < st.Length; i++)
            {
                if (!st[i].crossed) continue;
                Debug.Log($"[TakeRunner] 線を横切った id={(i < _lineSlots.Count ? _lineSlots[i] : "?")} " +
                          $"cam={st[i].camera} pos=({xz.x:F2},{xz.y:F2})");
            }
            return st;
        }

        // 復帰先 = 時計が確定している「いま体験者が居るゾーン」> 演出開始時のゾーン（未確定時のみ）。
        private int ResolveLatestZoneCamera()
        {
            if (director != null && director.TryGetCurrentZoneCamera(out int nowZone)) return nowZone;
            return _logic.BaseZoneCamera;
        }

        private void Apply(TakeRunnerLogic.Decision d, bool exitAnchored)
        {
            switch (d.action)
            {
                case TakeRunnerLogic.Action.BeginStep:
                    BeginStep(d, exitAnchored);
                    break;
                case TakeRunnerLogic.Action.EndTake:
                    EndTake(d);
                    break;
            }
        }

        private void BeginStep(TakeRunnerLogic.Decision d, bool exitAnchored)
        {
            _markResponseToken = -1;
            _markResponsePlaying = false;
            _markResponseReported = false;
            _markResponseZoneExit = false;
            // 同じ演出の次カットへ進んだ事実は、直前のカットが著作どおり終わったことを意味する。
            // playable 判定を通って画面を取った dollCall だけが _activeStepDollCall を立てるため、
            // 素材不足などで飛ばされたカットはここへ混ざらない。
            if (!d.takeStarted) CompleteDollCatchUpIfActive();

            if (director == null)
            {
                Debug.LogWarning("[TakeRunner] director 未配線のため演出を実行できない。");
                return;
            }
            ShowStepDef? step = GetStep(d.takeIndex, d.stepIndex);
            if (step == null) return;

            // 音は演出の単位（カットではない）。指示が無ければ何も起きず、区間の曲が鳴り続ける。
            if (d.takeStarted) BeginTakeBgm(d.takeIndex);

            string source = TakeSchema.NormalizeSource(step.source, out bool known);
            if (!known)
                Debug.LogWarning($"[TakeRunner] 未知の source '{step.source}' → live として扱う（take={TakeId(d.takeIndex)}）");

            // オーバーレイ（cue / 全面差し替え素材）を先に組む。§6.4 の「飛ばす」判定に素材の有無が要るため、
            // 画面（カメラ・post）を触る前に決める。
            OverlayCueData? cue = BuildStepOverlay(step, source, d.takeIndex, d.stepIndex);

            // --- §6.4 不正値: このカットは実行できない → 画面も post も触らずに即次のカットへ送る ---
            // 「範囲外カメラを registry の clamp 任せで無言に別カメラへ」「素材無しで数秒画面が固まる」を作らない。
            bool playable = IsStepPlayable(step, source, cue, d.takeIndex, d.stepIndex, out string why);
            // 飛ばしたことも取れたことも外へ出す（観測専用。全カットが飛んだ演出を解析が名指しできる）。
            StepResolved?.Invoke(TakeId(d.takeIndex), d.stepIndex, source, step.camera, playable, why);
            if (!playable)
            {
                cue?.frames?.Dispose();   // 開いた録画をリークさせない
                _logic.SetCurrentStepEnd(Now);
                return;
            }

            // 3 周目 A の凍結に入った瞬間で心音同期の画を止める。
            // ラッチは CameraFeelFx が持つため、引き返して以前のカットを再演しても戻らない。
            if (step.splitFreeze) _feelFx?.NotifyHeartbeatFreeze();

            // いま画面を取っているカットの素材 id（音が読む・`canon/LEDGER.md` 0131）。
            // ⚠ **飛ばす判定の後**（画面を取れなかったカットの id を立てない）。
            _activeStepCueId = step.cueId ?? "";
            // 呼びかけのカットが画面を取っているか（心音の始まりを決める・0175）。
            _activeStepDollCall = step.dollCall;
            _activeStepDollReplacement = false;

            // このカットから劇伴を差し替える（`canon/LEDGER.md` 0119）。
            // ⚠ **演出の bgm（占有）とは別の口。** こちらはレーンそのものを書き換えるので、
            //   演出が終わっても区間を移っても鳴り続ける。
            // ⚠ **飛ばすカットでは掛けない**（上の !playable で return 済み）。素材が無くて
            //   画が出なかったカットで曲だけ変わると、走行から「なぜ変わったか」が読めなくなる。
            BeginStepBgm(step);

            // post 層はカットごとに掛け替える（無指定のカットでは解除して区間 / カメラ / global へ戻す）。
            showControl?.SetInsertPostOverride(step.hasPost && step.post != null, step.hasPost ? step.post : null);

            // CG 人形もカットごとに掛け替える。構図を決めるのは「その映像を撮った実カメラ」なので、
            // live でも rec でも step.camera を渡す（未指定なら今映しているカメラ）。
            // **素材カット（clip / still）に人形は重ねない。**
            // 素材は「いつどこで撮ったか分からない画」で、step.camera の較正とはパースが一致しない
            // （camera 未指定なら直前のゾーンのカメラへ落ちるので、なおさら無関係な構図になる）。
            // 重ねれば必ず浮いた絵になるので、出さずに理由を言う方がよい。
            // rec（端末内録画）は step.camera で撮った画なので、そのカメラの較正がそのまま効く。
            // 素材のうち、**そのカメラで撮った画**（rec / plate）だけは人形を重ねてよい。
            // 一般の素材（clip / still）は撮影条件が分からないので、重ねれば必ず浮く。
            bool cgBlocked = TakeSchema.IsAssetSource(step.source)
                             && !TakeSchema.MatchesCameraPerspective(step.source);
            bool toDoll = step.HasCg && !cgBlocked;

            // ---- 入れ替わりのノイズ（`canon/LEDGER.md` 0089）--------------------------------
            //
            // ⚠ **CG を触る前に向きを決める。** 向きはデータに無く、「直前に人形が出ていたか」と
            //   「このカットが人形を出すか」から導く（<see cref="TakeSchema.TransSwap"/> の説明）。
            //   前後で同じなら入れ替わりではないので、乱れ遷移へ倒して理由を言う。
            bool wantSwap = TakeSchema.IsSwapTransition(step.transition);
            // ⚠⚠ **DollVisible を読む（IsVisible ではなく）。** 持続の覆い（swapHold）は人の代役を
            //   立てたまま swap のカットへ渡すので、IsVisible だと fromDoll=true に化けて
            //   「人形 → 人形 ＝ 成立しない」へ倒れ、**保持からの引き継ぎが 1 度も走らない**
            //   （2026-08-22。プレビューは TakeRunner を通らないので絵では出ず、ここでしか捕まらない）。
            bool fromDoll = _cgLayer != null && _cgLayer.DollVisible;
            bool swapUsable = wantSwap && _cgLayer != null && fromDoll != toDoll;
            if (wantSwap && !swapUsable)
                Debug.LogWarning($"[TakeRunner] カット {d.stepIndex + 1} の入れ替わりは成立しない" +
                                 $"（人形 前={(fromDoll ? 1 : 0)} 後={(toDoll ? 1 : 0)}）→ 乱れ遷移へ倒す" +
                                 $"（take={TakeId(d.takeIndex)}）");
            var swapDir = toDoll ? SwapMorphLogic.Dir.ToDoll : SwapMorphLogic.Dir.ToHuman;

            // ---- 持続の覆い（`canon/LEDGER.md` 0102）-----------------------------------------
            //
            // このカットのあいだ、覆いを包み切った状態で保持する（3 周目 A の入り）。
            // ⚠ 覆いの形の供給元として**人の代役**を立てるので、CG を消す側の分岐（下の Hide）を
            //   通してはいけない。通すと HoldForSwap が Hide を保留し、**入れ替わりが終わった
            //   瞬間にその保留が走って人形が消える**（次のカットのプレート＋人形が空になる）。
            // ⚠ 立てられなかった（較正未着・actors[] に visitor が無い）なら false へ倒す ＝
            //   覆いは出ないが CG は従来どおり畳まれ、鏡映し → 凍結 → 録画 の筋はそのまま通る。
            bool holdVeil = step.swapHold && !wantSwap
                && director.TakeVeilHoldBegin(step.camera >= 0 ? step.camera : ResolveLatestZoneCamera(),
                                              step.swapMinX);
            // 覆いを引き継がないカットへ移ったら畳む（画が割れたまま・包まれたまま残さない）。
            if (!holdVeil && !wantSwap && director.SwapHolding) director.CancelSwap();

            if (_cgLayer != null)
            {
                // ⚠⚠ 「人形 → 人」の入れ替わりは、**人形を出さないカットへ移るのと同時に**始まる。
                //    先に掴んでおかないと、この直後の Hide が人形を消して砂が 1 コマも映らない
                //    （砂の形は人形のシルエットそのものなので、人形が居なければ何も出ない）。
                if (swapUsable && !toDoll) _cgLayer.HoldForSwap(true);
                if (step.HasCg && cgBlocked)
                {
                    Debug.LogWarning($"[TakeRunner] カット {d.stepIndex + 1} は素材（{step.source}）なので " +
                                     $"CG 人形 '{step.cg}' は出さない（素材の構図と人形のパースが合わないため）");
                }
                if (toDoll)
                    _cgLayer.Apply(step.cg, step.cgMode,
                                   step.camera >= 0 ? step.camera : ResolveLatestZoneCamera(),
                                   step.hasPlacement ? step.placement : null);
                // ⚠ 持続の覆いが立てた代役は畳まない（畳むと覆いの形が消える）。
                //   この代役を消す責任は SwapMorphFx.Cancel が持つ。
                else if (!holdVeil) _cgLayer.Hide();
                // 人形に付き従う劣化。人形を出さないカットでは必ず 0 へ戻す
                // （残ると「何も居ない所の画だけが荒れている」という説明の付かない絵になる）。
                _cgLayer.SetAura(toDoll ? step.aura : 0f);
                // 表示意図を保持する。DollVisible は非同期の準備後に次フレームで立つ場合があるので、
                // ここで一度だけ標本を取ると、実際に出た後も false のままになる。
                _activeStepDollReplacement = toDoll;
            }

            // カット遷移（cut / dip / fade / glitch）。**source によって効かせ方が違う**:
            //   live   … 画面のライブ層は 1 枚しかないのでクロスフェードできない。cut=瞬時 / dip・fade=黒経由
            //   素材   … cut=瞬時に差し替え / dip=黒経由 / fade=素材のクロスフェード（overlay の fadeIn。従来どおり）
            //   glitch … 黒の代わりに「映像の乱れ」で覆い、その最中に差し替える（live / 素材とも同じ）
            // 旧実装は素材カットに遷移を一切効かせず、卓は 4 カット全部に遷移欄を出していた（嘘の UI）。
            // 入れ替わりのノイズ。**画面を差し替える仕事は覆い切った瞬間まで待つ** —
            // 素材も左右分割も第 2 層も、砂が体験者を隠し切ってから一斉に入れ替える。
            // 早いと体験者が砂の下ではなく画の中で消え、遅いと砂の中で背景が動く。
            bool swapping = false;
            if (swapUsable)
            {
                float swapSec = TakeSchema.ResolveTransitionMs(step.transition, step.transitionMs) / 1000f;
                _pendingSwapCue = cue;
                // ⚠⚠ **1 層目（左半分の生成素材）だけ、覆いを待たずに畳む**（2026-08-23・ユーザー指定
                //   「抹消したら生成の人形は消える」→ 赤入れ「右半分は以前のまま、左半分だけ
                //   生成を乱れとともに抹消したらすぐ消す」）。
                //   4 周目 A の締めは 1 層目が生成画像の人形（マスクで左半分）・
                //   **第 2 層が右半分の無人プレート**。覆いは `swapMinX` 未指定 ＝ 人型の周りだけなので
                //   1 層目を 1 画素も隠さず、待つと報告から **1.95 秒**（実測）人形が残る。
                //   乱れは同じフレームの `step.glitch` が撃つ（下の「カット頭の単発の乱れ」）。
                //
                //   ⚠⚠ **第 2 層はここで畳まない。** 一度そうして人形 → 体験者の入れ替わりを壊した —
                //   右半分が覆いより先にライブへ戻ると、**入れ替わる前から本物の体験者が写っている**
                //   ので「黒い波が育って人になる」が読めなくなる。第 2 層は従来どおり onCovered。
                //   ⚠ **素材を出す側も従来どおり onCovered**（早く出すと体験者が砂の下ではなく
                //   画の中で入れ替わる）。ここで畳んでよいのは「もう出さないと決まっている素材」だけ。
                //
                //   ⚠⚠ **畳んだ跡へライブを出さない**（2026-09-05・ユーザー報告「4-A って確か、
                //   体験者がひだりはんぶんに手を伸ばしちゃうと結構序盤から手が見えちゃってたよね」）。
                //   覆いは CG の人型の形でしか育たないので、**横へ伸ばした手は覆いの外に残る** —
                //   畳んだ跡がライブだと、育ち切るまでの 2.6 秒（`DefaultSwapMs`）ずっと生身が見える。
                //   右半分は第 2 層が onCovered まで保持されるので、**穴は左半分にだけ開いていた**。
                //   ⇒ 素材だけ**無人プレート**へ差し替え、マスク（左半分）はそのまま保つ。
                //   生成の人形は消え（ユーザー指定は守る）、左右とも無人プレート ＝ 画面全体が
                //   無人の部屋になり、そこへ黒い波が育つ。掴めなければ従来どおり畳む。
                if (cue == null && !CoverWithPlate(step)) PlayStepOverlay(null, step);
                swapping = director.TakeSwapBegin(
                    source == TakeSchema.SourceLive ? step.camera : -1, swapSec, swapDir,
                    () =>
                    {
                        _pendingSwapCue = null;
                        PlayStepOverlay(cue, step);
                        director.ApplySplit(step.splitX, step.splitFlip, step.splitFreeze);
                        ApplyStepOverlay2(step, takeIndex: d.takeIndex, stepIndex: d.stepIndex);
                    }, step.swapMinX);
                if (!swapping)
                {
                    _pendingSwapCue = null;
                    // 掴みを解く（「人形 → 人」で保留していた Hide がここで走る）。
                    _cgLayer?.HoldForSwap(false);
                    Debug.LogWarning($"[TakeRunner] 入れ替わりのノイズを出せなかった → 乱れ遷移で差し替える" +
                                     $"（take={TakeId(d.takeIndex)} step={d.stepIndex}）");
                }
            }

            // ⚠ 入れ替わりに倒れたときの遷移は **glitch の既定尺**へ落とす。
            //   swap の尺（既定 2.6 秒）をそのまま dip へ渡すと 2.6 秒の暗転になる。
            string transKind = swapping || !wantSwap ? step.transition : TakeSchema.TransGlitch;
            float transMs = swapping || !wantSwap ? step.transitionMs : 0f;
            TakeSchema.SplitTransition(TakeSchema.ResolveTransitionMs(transKind, transMs),
                out float downSec, out float upSec);
            bool glitchTrans = TakeSchema.IsGlitchTransition(transKind);

            // 画面の占有とカメラ。live のときだけカメラを動かす。
            // ⚠ 入れ替わりのノイズが走っているときは**ここで画面に触らない**（差し替えは onCovered）。
            if (swapping)
            {
                // 何もしない（画面は SwapMorphFx が持っている）。
            }
            else if (source == TakeSchema.SourceLive)
            {
                PlayStepOverlay(cue, step);   // live カットでも cue 重ねは即時（dip の黒で隠れる）
                // 演出の 1 カット目が exit アンカー由来なら、離脱の dip の黒中に差し替える（中間カメラを見せない）。
                if (d.takeStarted && exitAnchored) director.InsertExitRedirect(step.camera, downSec, upSec, glitchTrans);
                else director.InsertBegin(step.camera, downSec, upSec, glitchTrans);
            }
            else
            {
                // dip と glitch は「覆いの最中に差し替える」点で同じ扱い。fade / cut は覆いを作らない。
                bool throughCover = transKind == TakeSchema.TransDip || glitchTrans;
                if (transKind == TakeSchema.TransCut && cue != null && step.fadeInSec < 0f)
                    cue.fadeInSeconds = 0f;   // 「瞬時」はフェードも掛けない（明示指定があればそれを尊重）
                // カメラは変えないが画面は演出が持つ。覆いがあるときだけ素材の差し替えをその最中に行う。
                director.TakeHoldBegin(throughCover ? downSec : 0f, throughCover ? upSec : 0f,
                    () => PlayStepOverlay(cue, step), glitchTrans);
            }

            // 視点が急に切り替わった音（カットの `switchSfx`・`canon/LEDGER.md` 0102）。
            // ⚠ **画を差し替えるのと同じ行から鳴らす** — 別の層（`SoundCueLogic` の毎フレーム観測）に
            //   置くと、0.5〜1.2 秒刻みで連打される 2 周目 C の接近では取りこぼす。
            // ⚠ カメラが実際に変わるカットは Director が既に鳴らすので、そこで立てると二重に鳴る。
            if (step.switchSfx && !(source == TakeSchema.SourceLive && step.camera >= 0
                                    && director.ActiveCameraIndex != step.camera))
                director.PlaySwitchSfx();

            // 人形の呼びかけ（カットの `dollCall`・`canon/LEDGER.md` 0109）。
            // ⚠ **切替音と同じ行から撃つ。** 毎フレーム状態を見る `SoundCueLogic` に置くと、
            //   0.8〜1.4 秒刻みで進む 2 周目 C の接近では頭を取りこぼす。
            // ⚠ 音（1.60 秒）はカット（1.4 秒）より長いが**画は待たない**。はみ出した 0.2 秒は
            //   次のカットへ被る（ユーザー指示・0109「映像はこの音を無視してそのまま先に進んで ok」）。
            // ⚠ 引き返して同じ演出が頭から再演されたら**もう 1 度鳴る**。画も再演されるので、
            //   ここだけ黙ると「追いついたのに声がしない」になる（`memory/backtrack_and_replay.md`）。
            if (step.dollCall && _sound != null && !_sound.PlaySpot(SoundCue.DollCall))
                Debug.LogWarning("[TakeRunner] 人形の呼びかけの音源がありません"
                                 + "（Resources/Sound/sfx_doll_call）。カットは無言で進みます。"
                                 + "`py -3.11 tools/ingest-sounds.py --only sfx_doll_call` の後に "
                                 + "`.\\tools\\unity.ps1 menu sound-import` を走らせること。");

            // カット頭の単発の乱れ（遷移とは別物。企画書 2.3 の「注意・移動の誘導」に使う）。
            if (step.glitch > 0.001f)
                director.PulseGlitch(step.glitch, step.glitchSec > 0f ? step.glitchSec : 0.25f);

            // 画のホールド / 焼き付き。どちらも「その瞬間の 1 枚」を凍らせる同じ機構で、
            // 強さと保持時間が違うだけ（hold = 完全に止まる / burn = 薄く残る）。
            if (step.hold > 0.001f) director.HoldFrame(step.hold);
            else if (step.burn > 0.001f) director.BurnFrame(step.burn, step.burnSec);

            // 左右分割と第 2 の差し替え層（canon/LEDGER.md 0050）。**カットごとに毎回書く** —
            // 前のカットの分割・素材を引き継がせない（引き継ぐと「指定していないカット」で
            // 画が割れたまま・左半分が凍ったままになる）。
            // ⚠ 入れ替わりのノイズでは**ここで書かない**（onCovered が持っている）。
            //   ここで書くと、砂が湧いている最中に左半分の凍結が解け、人形の群れが消える ＝
            //   「体験者が入れ替わった」ではなく「画面が切り替わった」に読まれる。
            if (!swapping)
            {
                director.ApplySplit(step.splitX, step.splitFlip, step.splitFreeze);
                ApplyStepOverlay2(step, takeIndex: d.takeIndex, stepIndex: d.stepIndex);
            }

            // スクリーンの外の闇で目が開く異変（canon/LEDGER.md 0075）。**画面には触らない**ので
            // どの source のカットにも足せる。同じ値を続けて言い直しても進みは巻き戻らないので、
            // カットをまたいでも 1 つの出来事として続く。
            _eyes?.Apply(step.eyes, step.eyeJack);

            Debug.Log($"[TakeRunner] {(d.takeStarted ? "演出開始" : "カット")} take={TakeId(d.takeIndex)} " +
                      $"step={d.stepIndex} source={source}" +
                      $"{(source == TakeSchema.SourceLive ? $" camera={step.camera}" : "")}");
        }

        /// <summary>
        /// カットの第 2 差し替え層（<c>overlay2CueId</c>）を出す。空なら畳む。
        ///
        /// **1 層目と違って遷移の黒を待たない** — 第 2 層は左右分割とセットで使い、切り替えは
        /// 乱れが覆う（canon/LEDGER.md 0050「向きが逆になるのは一瞬なので、映像の乱れでごまかそう」）。
        /// 素材のロードは非同期なので、載ったかどうかは <c>ovl2</c>（テレメトリ）でしか分からない。
        /// </summary>
        private void ApplyStepOverlay2(ShowStepDef step, int takeIndex, int stepIndex)
        {
            if (overlay == null) return;
            if (string.IsNullOrEmpty(step.overlay2CueId)) { overlay.ClearSecondLayer(); return; }

            OverlayCueData? cue = _cueResolver?.Invoke(step.overlay2CueId);
            if (cue == null)
            {
                // カットごと飛ばさないのは、第 2 層が**添え物**だから（1 層目と分割は成立している）。
                // 飛ばすと画面の所有者が変わって演出の筋が丸ごと消える。
                Debug.LogWarning($"[TakeRunner] 第 2 層の cue 未解決: {step.overlay2CueId}" +
                                 $"（take={TakeId(takeIndex)} step={stepIndex}）→ 第 2 層は出さない");
                overlay.ClearSecondLayer();
                return;
            }
            overlay.ShowSecondLayer(cue);
        }

        /// <summary>
        /// カットのオーバーレイを実際に出す（遷移が dip なら黒の瞬間に呼ばれる）。
        /// <c>untilClipEnd</c> の待ちはここで確定させる — 静止画は終端イベントを持たないため
        /// §6.4 のとおり <c>durSec&gt;0 ? durSec : 4s</c> で畳む（watchdog 任せにすると 45 秒画面が固まる）。
        /// </summary>
        /// <summary>
        /// 入れ替わりの覆いが育つあいだ、1 層目の素材を<b>そのカメラの無人プレート</b>へ差し替える
        /// （2026-09-05）。マスクは触らないので、左半分だけを覆っていた cue は左半分だけがプレートになる。
        ///
        /// ⚠ プレートは <see cref="ShowControlClient"/> が起動時に先読みしてある
        /// （<see cref="SwapMorphFx"/> が覆いの形を引く相手と同じ 1 枚）。掴めなければ <c>false</c> を返し、
        /// 呼び出し側は従来どおり畳む — **覆いが育つまで実写が出る**が、素材が残り続けるよりはよい。
        /// </summary>
        private bool CoverWithPlate(ShowStepDef step)
        {
            if (overlay == null || showControl == null) return false;
            int cam = step.camera >= 0 ? step.camera : ResolveLatestZoneCamera();
            Texture? plate = showControl.SwapPlateFor(cam);
            if (plate == null)
            {
                Debug.LogWarning($"[TakeRunner] 覆いのあいだ 1 層目へ出す無人プレートがありません" +
                                 $"（camera={cam}）→ 素材を畳みます。覆いが育つ間その部分に実写が出ます。" +
                                 $"卓の 📷 無人プレートで plate_<カメラid> を用意すること。");
                return false;
            }
            if (!overlay.ReplaceStillSource(plate)) return false;
            // ⚠ **画からはほとんど区別が付かない**（塞いだ跡も無人の部屋なので、実写と絵の差しか出ない）。
            //   体験者が写っているかどうかで判定するしかないが、自動走行には人が居ない。
            //   だからこの 1 行が「塞げたか」の唯一の証拠になる（走行ログを grep して確かめる）。
            Debug.Log($"[TakeRunner] 覆いのあいだ 1 層目を無人プレートで塞いだ（camera={cam}）");
            return true;
        }

        private void PlayStepOverlay(OverlayCueData? cue, ShowStepDef step)
        {
            if (cue != null && overlay != null)
            {
                // 前のカットの録画を閉じ、このカットの録画を引き取る（所有はこのクラス）。
                if (!ReferenceEquals(_stepFrames, cue.frames))
                {
                    _stepFrames?.Dispose();
                    _stepFrames = cue.frames as RecordedFramePlayer;
                }
                // 「終端イベントを持つ素材」= 動画 / 録画フレーム列。静止画は持たない。
                bool hasNaturalEnd = cue.SourceIsVideo || cue.SourceIsFrames;
                _clipToken = overlay.PlayCue(cue);
                _stepOverlayPlayed = true;
                _awaitingClipEnd = step.IsUntilClipEnd && hasNaturalEnd;
                if (step.IsUntilClipEnd && !hasNaturalEnd)
                {
                    float sec = step.durSec > 0f ? step.durSec : TakeSchema.FallbackStepDurSec;
                    _logic.SetCurrentStepEnd(Now + sec);
                }
                return;
            }

            // 前カットのオーバーレイを引きずらない（live カットへ戻る等）。
            if (_stepOverlayPlayed) overlay?.StopOverlay();
            _stepOverlayPlayed = false;
            _awaitingClipEnd = false;
            _clipToken = -1;
            _stepFrames?.Dispose();
            _stepFrames = null;
            if (step.IsUntilClipEnd)
            {
                // 素材が無いのに untilClipEnd → 尺が決まらない。watchdog 任せにせず既定尺で畳む。
                _logic.SetCurrentStepEnd(Now + TakeSchema.FallbackStepDurSec);
            }
        }

        /// <summary>
        /// §6.4 の不正値判定。false のカットは飛ばす（全 step が飛べば演出は実質発火しない）。
        ///   - live: camera が registry の範囲外
        ///   - clip / still: 出せる素材が 1 つも解決できない
        ///   - inherit: 常に有効（「そのままの画を保つ」カットは素材が無くて当然）
        ///
        /// <paramref name="reason"/> には <see cref="StepResolved"/> へ流す理由トークンを返す。
        /// </summary>
        private bool IsStepPlayable(ShowStepDef step, string source, OverlayCueData? cue,
                                    int takeIndex, int stepIndex, out string reason)
        {
            reason = StepOkReason;
            if (source == TakeSchema.SourceLive)
            {
                int count = director != null ? director.CameraCount : 0;
                if (step.camera < 0 || (count > 0 && step.camera >= count))
                {
                    Debug.LogWarning($"[TakeRunner] live のカメラ {step.camera} が範囲 [0,{count}) 外 → " +
                                     $"このカットを飛ばす（take={TakeId(takeIndex)} step={stepIndex}）");
                    reason = StepSkipCameraRange;
                    return false;
                }
                return true;
            }
            if ((TakeSchema.IsAssetSource(source) || TakeSchema.IsRecSource(source)) && cue == null)
            {
                Debug.LogWarning($"[TakeRunner] {source} だが素材が解決できない → " +
                                 $"このカットを飛ばす（take={TakeId(takeIndex)} step={stepIndex}）");
                // rec だけは理由が違う（「録れていない」＝ 3 周目の素材が無い）。混ぜると原因を取り違える。
                reason = TakeSchema.IsRecSource(source) ? StepSkipNoRecording : StepSkipNoAsset;
                return false;
            }
            return true;
        }

        // 演出の BGM 指示を掛ける（占有できた時だけ終了時に返す）。
        private void BeginTakeBgm(int takeIndex)
        {
            if (takeIndex < 0 || takeIndex >= _takes.Length) return;
            ShowTakeDef take = _takes[takeIndex];
            if (bgmDirector == null) bgmDirector = FindObjectOfType<BgmDirector>();
            if (bgmDirector == null) return;
            _bgmOverrideActive = bgmDirector.BeginTakeOverride(take.bgm, take.hasBgm && take.bgm != null);
        }

        // カットの BGM 指示を掛ける（`canon/LEDGER.md` 0119）。
        // ⚠⚠ **占有ではなくレーンの書き換え**なので、`ApplySegment` を通す。`BeginTakeOverride` を
        //   使うと演出の終わりに前の曲へ戻ってしまい、「ここから先はこの曲」が書けない。
        // ⚠ 演出が音を占有中なら鳴らず、戻り先だけが変わる（`ApplySegment` の契約）。
        private void BeginStepBgm(ShowStepDef step)
        {
            if (!step.hasBgm || step.bgm == null) return;
            if (bgmDirector == null) bgmDirector = FindObjectOfType<BgmDirector>();
            bgmDirector?.ApplySegment(step.bgm, present: true);
        }

        // 演出が終わった / 畳まれた。占有していたならレーン（区間の曲）へ返す。
        private void EndTakeBgm()
        {
            if (!_bgmOverrideActive) return;
            _bgmOverrideActive = false;
            bgmDirector?.EndTakeOverride();
        }

        private void EndTake(TakeRunnerLogic.Decision d)
        {
            _lastEndReason = d.reason;
            if (d.reason == TakeRunnerLogic.EndReason.Completed) CompleteDollCatchUpIfActive();
            else _activeStepDollCall = false;
            // 音は director の有無に関係なく必ず返す（画面が無くても占有だけ残さない）。
            EndTakeBgm();
            // ⚠ カット単位の資源（オーバーレイ・第 2 層・左右分割・録画・CG）は **画面の有無と無関係**。
            //   これを director の null チェックより後ろに置いていた版は、画面を組めていない構成で
            //   演出が走ると資源を掴んだまま終わっていた（中の呼び先はどれも null 安全）。
            ReleaseStepState();
            showControl?.SetInsertPostOverride(false, null);
            if (director == null) return;

            // **次の演出が控えているなら画面を返さない**（連続の繋ぎ目に黒を挟まない）。
            // 返してしまうと、復帰の暗転（既定 70/100ms）と次の演出の入りの遷移が二重に出るうえ、
            // 次の演出の 1 カット目が必ずその暗転へ割り込むことになり、作者が選んだ遷移が消える。
            // 占有（InsertActive）は保ったままなので、画面の所有者が空白になる瞬間は無い。
            // watchdog の強制終了だけは素直に返す（壊れて止まったので仕切り直す）。
            if (d.chainNext && !d.forced && director.InsertActive)
            {
                _chainPending = true;
                _chainPendingFrame = Time.frameCount;
                Debug.Log($"[TakeRunner] 演出終了 → 次の演出へそのまま渡す（復帰の暗転を挟まない）");
                return;
            }
            _chainPending = false;

            // 画面を実際に持っていた時だけ返す。全 step が §6.4 で飛ばされた演出は画面に触っていないので、
            // ここで dip を掛けると「何も起きていないのに暗転する」ことになる。
            if (director.InsertActive)
            {
                if (d.dismissed) DismissReturn(d.returnCamera);
                else director.InsertReturn(d.returnCamera);
            }
            Debug.Log($"[TakeRunner] 演出終了（{d.reason}） → 復帰 camera={d.returnCamera}");
        }

        /// <summary>
        /// <b>報告で畳んだ演出を、黒ではなく「映像の乱れ」で現実へ返す。</b>
        /// <c>canon/LEDGER.md</c> 0050「推したら乱れたのちに元に戻って」。
        ///
        /// ⚠ 黒の dip で返すと「カメラが切り替わった」の語彙になり、押した行為と画の変化が
        ///   因果として結ばれない。クロスフェードは「作者が消した」に、砂嵐は「信号が切れた」に読まれる。
        /// ⚠ <b>乱れの育ち（<see cref="GlitchEscalationLogic"/>）には数えさせない。</b> 数えると
        ///   終盤の乱れの強さが体験者の押下回数の関数になり、著作した曲線
        ///   （<c>canon/LEDGER.md</c> 0055「最後にかけて粗く」）が人によって別物になる ＝ 走行の再現性が消える。
        /// </summary>
        private void DismissReturn(int returnCamera)
        {
            if (director == null) return;
            TakeSchema.SplitTransition(TakeSchema.DismissGlitchMs, out float down, out float up);
            director.InsertReturn(returnCamera, down, up, glitch: true, countEscalation: false);
        }

        // 走行中の演出を安全に畳む（SetTakes / ResetRun / ライブ卓の介入の前に呼ぶ。凍結ストランドを残さない）。
        // releaseScreen=false なら「画面の占有だけ解いてカメラは動かさない」（ライブ卓が既に画面を取っている場合）。
        private void CleanupActive(bool releaseScreen = true)
        {
            // ⚠⚠ **連続の渡し（chainNext）の最中は `_logic.IsActive` が false** — 前の演出は
            //    EndTakeDecision で終わっていて、次の演出はまだ始まっていない。ところが
            //    **画面の占有だけは保ったまま**なので、ここで早期 return すると
            //    「占有が降りない ＋ Update の安全網（_chainPending）も消える」で画面が固まる。
            //    渡しの最中かどうかを先に読んでから落とす。
            bool handingOver = _chainPending;
            _chainPending = false;
            if (!_logic.IsActive && !handingOver) return;
            PresentationAbortCount++;
            _activeStepCueId = "";   // 音が「まだ異世界が映っている」と読まない（0131）
            _activeStepDollCall = false;   // 同・呼びかけのカットが降りた縁を作る（0175）
            _activeStepDollReplacement = false;
            EndTakeBgm();
            ReleaseStepState();
            if (director != null && director.InsertActive)
            {
                showControl?.SetInsertPostOverride(false, null);
                if (releaseScreen) director.InsertReturn(ResolveLatestZoneCamera());
                else director.TakeHoldEnd();
            }
            // 中止では凍結を必ず畳む。ホールドは秒で必ず明けるので原理的に固着しないが、
            // 「画が止まったまま戻らない」はこの codebase が 4 回踏んだ事故の型なので二重に閉じる。
            // ⚠ null 許容にする（同じメソッドの上と ReleaseStepState は既に `director?.`）。
            //   director が解決できない構成で演出が走ると、中止のたびに NRE で止まっていた。
            director?.ClearFeelFx();
            // ⚠⚠ 闇の目は Release では消えない（流しきる）。**中止・ラン開始・卓の緊急停止は
            //    「無かったことにする」側**なので、ここだけは 1 フレームで落とす。
            //    落とさないと、次の体験者が被った直後に前の人の目が閉じ残っている。
            _eyes?.Abort();
            _logic.AbortActive();
        }

        private void CompleteDollCatchUpIfActive()
        {
            if (!_activeStepDollCall) return;
            _activeStepDollCall = false;
            DollCatchUpCompletedCount++;
        }

        // カット単位の状態（オーバーレイ・クリップ待ち・開いている録画）を落とす。
        private void ReleaseStepState()
        {
            _markResponseToken = -1;
            _markResponsePlaying = false;
            _markResponseReported = false;
            _markResponseZoneExit = false;
            if (_stepOverlayPlayed) overlay?.StopOverlay();
            _stepOverlayPlayed = false;
            _awaitingClipEnd = false;
            _clipToken = -1;
            _stepFrames?.Dispose();
            _stepFrames = null;
            _activeStepDollReplacement = false;
            // ⚠ 入れ替わりのノイズを先に畳む。畳まないと ShowCgLayer が人形を掴んだままで、
            //   この直後の Hide が保留され、**砂の人形が次の体験者へ持ち越される**。
            director?.CancelSwap();
            // 覆い切る前に畳まれたら、待たせていた素材（開いた録画）は誰も引き取らない。ここで閉じる。
            _pendingSwapCue?.frames?.Dispose();
            _pendingSwapCue = null;
            _cgLayer?.Hide();
            _cgLayer?.SetAura(0f);
            // ⚠⚠ 左右分割と第 2 層も**カット単位の状態**なので、ここで必ず畳む。
            //    書いているのはカットの中だけなので、演出が終わった後は誰も上書きしない ＝
            //    残すと**画が割れたまま・左半分が凍ったまま次の体験者へ持ち越される**。
            //    畳む経路を CleanupActive（中止）だけに置いていた版は、**正常終了で必ず残った**。
            //    連続の渡し（chainNext）では次の演出の 1 カット目まで 1 フレームだけ素へ戻るが、
            //    割れたまま固着するより桁違いに軽い。
            director?.ApplySplit(0f, false, false);
            overlay?.ClearSecondLayer();
            // 闇の目も**カット単位の状態**。ここを通らない終わり方は無い（正常終了・中止・
            // ラン開始・watchdog・報告で畳む、のすべてが ReleaseStepState を通る）。
            // ⚠⚠ **ここは切らない**（2026-08-19・canon/LEDGER.md 0093）。開いている途中で
            //    区間が変わったら、目は倍速で開き切ってから閉じる ＝ 数秒はカットより長生きする。
            //    体験者を替える側（CleanupActive）だけが Abort で本当に消す。
            _eyes?.Release();
        }

        private ShowStepDef? GetStep(int takeIndex, int stepIndex)
        {
            if (takeIndex < 0 || takeIndex >= _takes.Length) return null;
            ShowStepDef[] steps = _takes[takeIndex].steps;
            if (steps == null || stepIndex < 0 || stepIndex >= steps.Length) return null;
            return steps[stepIndex];
        }

        private string TakeId(int takeIndex)
            => takeIndex >= 0 && takeIndex < _takes.Length && !string.IsNullOrEmpty(_takes[takeIndex].id)
                ? _takes[takeIndex].id
                : $"#{takeIndex}";

        /// <summary>
        /// カットのオーバーレイを組み立てる。素材は <c>assetUrl</c>（clip / still）> <c>cueId</c> の順で決まり、
        /// マスク・既定値は cueId の素材定義から継承する（step 側の <c>-1</c> は「継承」）。
        /// 出すものが無ければ null。
        /// </summary>
        private OverlayCueData? BuildStepOverlay(ShowStepDef step, string source, int takeIndex, int stepIndex)
        {
            OverlayCueData? cue = null;
            if (!string.IsNullOrEmpty(step.cueId))
            {
                cue = _cueResolver?.Invoke(step.cueId);
                if (cue == null)
                    Debug.LogWarning($"[TakeRunner] cue 未解決: {step.cueId}（take={TakeId(takeIndex)} step={stepIndex}）");
            }

            // 端末内録画（source:"rec"）。録れていなければ null を返し、このカットは飛ばされる。
            if (TakeSchema.IsRecSource(source))
            {
                RecordedFramePlayer? rec = OpenRecording(step, takeIndex, stepIndex);
                if (rec == null) return null;
                return new OverlayCueData
                {
                    id = $"{TakeId(takeIndex)}#{stepIndex}",
                    displayName = _takes[takeIndex].name ?? "",
                    frames = rec,
                    maskTexture = cue?.maskTexture,
                    maskUrl = cue?.maskUrl ?? "",
                    strength = TakeSchema.Inherit(step.strength, cue?.strength ?? 1f),
                    loop = false,
                    fadeInSeconds = TakeSchema.Inherit(step.fadeInSec, cue?.fadeInSeconds ?? 0.5f),
                    fadeOutSeconds = TakeSchema.Inherit(step.fadeOutSec, cue?.fadeOutSeconds ?? 0.5f),
                };
            }

            string assetUrl = "";
            if (TakeSchema.IsAssetSource(source) && !string.IsNullOrEmpty(step.assetUrl))
                assetUrl = _urlResolver != null ? _urlResolver(step.assetUrl) : step.assetUrl;

            bool hasAsset = !string.IsNullOrEmpty(assetUrl);
            bool cueHasSource = cue != null &&
                                (!string.IsNullOrEmpty(cue.sourceUrl) || cue.clip != null || cue.stillImage != null);
            // 出すものが無い（警告は IsStepPlayable が 1 回だけ出す）。
            if (!hasAsset && !cueHasSource) return null;

            return new OverlayCueData
            {
                id = $"{TakeId(takeIndex)}#{stepIndex}",
                displayName = _takes[takeIndex].name ?? "",
                clip = hasAsset ? null : cue?.clip,
                stillImage = hasAsset ? null : cue?.stillImage,
                maskTexture = cue?.maskTexture,
                sourceUrl = hasAsset ? assetUrl : (cue?.sourceUrl ?? ""),
                maskUrl = cue?.maskUrl ?? "",
                strength = TakeSchema.Inherit(step.strength, cue?.strength ?? 1f),
                // 演出のカットは必ず終わるものとして扱う（ループさせない。尺は logic が持つ）。
                loop = false,
                fadeInSeconds = TakeSchema.Inherit(step.fadeInSec, cue?.fadeInSeconds ?? 0.5f),
                fadeOutSeconds = TakeSchema.Inherit(step.fadeOutSec, cue?.fadeOutSeconds ?? 0.5f),
                trimStart = TakeSchema.Inherit(step.trimStartSec, cue?.trimStart ?? 0f),
                trimEnd = TakeSchema.Inherit(step.trimEndSec, cue?.trimEnd ?? 0f),
            };
        }

        /// <summary>
        /// <c>source:"rec"</c> のカットが指す端末内録画を開く。
        /// 「1 周目を録っていない」「ランを途中で開始した」等で録れていなければ null
        /// （§6.4 の扱いでそのカットを飛ばす。演出が無い分には体験は壊れない）。
        /// </summary>
        private RecordedFramePlayer? OpenRecording(ShowStepDef step, int takeIndex, int stepIndex)
        {
            if (_recorder == null) _recorder = FindObjectOfType<SegmentRecorder>();
            if (_recorder == null)
            {
                Debug.LogWarning($"[TakeRunner] SegmentRecorder が居ないので録画カットを飛ばす（take={TakeId(takeIndex)} step={stepIndex}）");
                return null;
            }
            if (step.recLap <= 0 || step.camera < 0)
            {
                Debug.LogWarning($"[TakeRunner] rec の周 / カメラが未指定（recLap={step.recLap} camera={step.camera}）→ 飛ばす");
                return null;
            }
            string path = _recorder.ResolveRecorded(step.recLap, step.camera);
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogWarning($"[TakeRunner] 録画が無い（lap={step.recLap} camera={step.camera}）→ このカットを飛ばす");
                return null;
            }
            RecordedFramePlayer? player = RecordedFramePlayer.Open(path);
            if (player != null)
            {
                _stepFramesLap = step.recLap;
                _stepFramesCam = step.camera;
                Debug.Log($"[TakeRunner] 録画を開いた lap={step.recLap} camera={step.camera} " +
                          $"frames={player.FrameCount} dur={player.DurationSec:F1}s");
            }
            return player;
        }

        // カットの尺配列（負値＝外部通知待ち。-1 は素材の終端、-2 は次の区間確定を待つ）。
        private static float[] BuildStepDurations(ShowTakeDef take)
        {
            ShowStepDef[] steps = take.steps ?? Array.Empty<ShowStepDef>();
            var durs = new float[steps.Length];
            for (int i = 0; i < steps.Length; i++)
            {
                ShowStepDef s = steps[i];
                if (s == null) { durs[i] = TakeSchema.FallbackStepDurSec; continue; }
                if (s.IsUntilClipEnd) { durs[i] = TakeRunnerLogic.WaitClipEnd; continue; }
                if (s.IsUntilZoneChange) { durs[i] = TakeRunnerLogic.WaitZoneChange; continue; }
                // 線待ちは**線が指定されているときだけ**。空 id を線待ちにすると watchdog まで固まる
                //（演出側の「空 lineId は発火しない」と同じ流儀で、無効な指定は無害な側へ倒す）。
                if (s.IsUntilLine && !string.IsNullOrEmpty(s.lineId))
                { durs[i] = TakeRunnerLogic.WaitLine; continue; }
                if (s.IsUntilLine)
                    Debug.LogWarning("[TakeRunner] untilLine のカットにラインが未指定 → 既定尺で畳む");
                if (s.IsUntilMark) { durs[i] = TakeRunnerLogic.WaitMark; continue; }
                durs[i] = s.durSec > 0f ? s.durSec : TakeSchema.FallbackStepDurSec;
            }
            return durs;
        }

        // カットごとに待つ線の slot index（-1 = 待たない）。**枠は必ず取る** — 線が layout より後から
        // 来ても index が動かないようにするため（演出側の LineSlot と同じ理由）。
        private int[] BuildStepLineIndices(ShowTakeDef take)
        {
            ShowStepDef[] steps = take.steps ?? Array.Empty<ShowStepDef>();
            var idx = new int[steps.Length];
            for (int i = 0; i < steps.Length; i++)
            {
                ShowStepDef s = steps[i];
                idx[i] = (s != null && s.IsUntilLine && !string.IsNullOrEmpty(s.lineId))
                    ? LineSlot(s.lineId)
                    : -1;
            }
            return idx;
        }
    }
}
