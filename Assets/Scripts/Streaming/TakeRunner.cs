#nullable enable
using System;
using System.Collections.Generic;
using FixedCamVr.Streaming.Cg;
using FixedCamVr.Streaming.Recording;
using UnityEngine;

namespace FixedCamVr.Streaming
{
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
            if (!_logic.IsActive) return;
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
                    defs.Add(new TakeRunnerLogic.Def
                    {
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
            _hasLineTakes = anyLine;
            _logic.SetDefs(defs.ToArray());
            // 新しく取った枠も含めてラインを貼り直す（layout が既に来ていれば geometry が入る）。
            ApplyLinesFromLayout();
        }

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

        /// <summary>ラン開始（体験者交代）。走行中の演出を畳み、once をクリアする。</summary>
        public void ResetRun()
        {
            CleanupActive();
            _logic.ResetRun();
            // 前の体験者の位置・横断状態を持ち越さない（ラン開始直後に幽霊の横断を作らない）。
            _lineCross.Reset();
            _hasLastNow = false;
        }

        /// <summary>ゾーン確定（TimelineDirector 経由の deterministic (lap,camera)）を受ける。</summary>
        public void NotifyZoneCommitted(int newLap, int newCam, bool hadPrev, int prevLap, int prevCam)
        {
            // 「次にカメラが切り替わるまで」のカットはここで終わる。**新しい区間の判定より先に**
            // 畳まないと、同じフレームで武装された次の演出とどちらが画面を取るかが順序依存になる。
            // 畳むのは尺だけで、演出を終わらせるのは次の Tick（そこで chainNext が繋ぎ目の黒を省く）。
            _logic.NotifyZoneChanged(Now);

            TakeRunnerLogic.Decision d =
                _logic.OnZoneCommitted(newLap, newCam, hadPrev, prevLap, prevCam, Now);
            Apply(d, exitAnchored: true);
        }

        private void Update()
        {
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
            return _lineCross.StateView;
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

            // post 層はカットごとに掛け替える（無指定のカットでは解除して区間 / カメラ / global へ戻す）。
            showControl?.SetInsertPostOverride(step.hasPost && step.post != null, step.hasPost ? step.post : null);

            // CG 人形もカットごとに掛け替える。構図を決めるのは「その映像を撮った実カメラ」なので、
            // live でも rec でも step.camera を渡す（未指定なら今映しているカメラ）。
            if (_cgLayer != null)
            {
                // **素材カット（clip / still）に人形は重ねない。**
                // 素材は「いつどこで撮ったか分からない画」で、step.camera の較正とはパースが一致しない
                // （camera 未指定なら直前のゾーンのカメラへ落ちるので、なおさら無関係な構図になる）。
                // 重ねれば必ず浮いた絵になるので、出さずに理由を言う方がよい。
                // rec（端末内録画）は step.camera で撮った画なので、そのカメラの較正がそのまま効く。
                // 素材のうち、**そのカメラで撮った画**（rec / plate）だけは人形を重ねてよい。
                // 一般の素材（clip / still）は撮影条件が分からないので、重ねれば必ず浮く。
                bool cgBlocked = TakeSchema.IsAssetSource(step.source)
                                 && !TakeSchema.MatchesCameraPerspective(step.source);
                if (step.HasCg && cgBlocked)
                {
                    Debug.LogWarning($"[TakeRunner] カット {d.stepIndex + 1} は素材（{step.source}）なので " +
                                     $"CG 人形 '{step.cg}' は出さない（素材の構図と人形のパースが合わないため）");
                }
                if (step.HasCg && !cgBlocked)
                    _cgLayer.Apply(step.cg, step.cgMode,
                                   step.camera >= 0 ? step.camera : ResolveLatestZoneCamera(),
                                   step.hasPlacement ? step.placement : null);
                else _cgLayer.Hide();
                // 人形に付き従う劣化。人形を出さないカットでは必ず 0 へ戻す
                // （残ると「何も居ない所の画だけが荒れている」という説明の付かない絵になる）。
                _cgLayer.SetAura(step.HasCg && !cgBlocked ? step.aura : 0f);
            }

            // カット遷移（cut / dip / fade / glitch）。**source によって効かせ方が違う**:
            //   live   … 画面のライブ層は 1 枚しかないのでクロスフェードできない。cut=瞬時 / dip・fade=黒経由
            //   素材   … cut=瞬時に差し替え / dip=黒経由 / fade=素材のクロスフェード（overlay の fadeIn。従来どおり）
            //   glitch … 黒の代わりに「映像の乱れ」で覆い、その最中に差し替える（live / 素材とも同じ）
            // 旧実装は素材カットに遷移を一切効かせず、卓は 4 カット全部に遷移欄を出していた（嘘の UI）。
            TakeSchema.SplitTransition(TakeSchema.ResolveTransitionMs(step.transition, step.transitionMs),
                out float downSec, out float upSec);
            bool glitchTrans = TakeSchema.IsGlitchTransition(step.transition);

            // 画面の占有とカメラ。live のときだけカメラを動かす。
            if (source == TakeSchema.SourceLive)
            {
                PlayStepOverlay(cue, step);   // live カットでも cue 重ねは即時（dip の黒で隠れる）
                // 演出の 1 カット目が exit アンカー由来なら、離脱の dip の黒中に差し替える（中間カメラを見せない）。
                if (d.takeStarted && exitAnchored) director.InsertExitRedirect(step.camera, downSec, upSec, glitchTrans);
                else director.InsertBegin(step.camera, downSec, upSec, glitchTrans);
            }
            else
            {
                // dip と glitch は「覆いの最中に差し替える」点で同じ扱い。fade / cut は覆いを作らない。
                bool throughCover = step.transition == TakeSchema.TransDip || glitchTrans;
                if (step.transition == TakeSchema.TransCut && cue != null && step.fadeInSec < 0f)
                    cue.fadeInSeconds = 0f;   // 「瞬時」はフェードも掛けない（明示指定があればそれを尊重）
                // カメラは変えないが画面は演出が持つ。覆いがあるときだけ素材の差し替えをその最中に行う。
                director.TakeHoldBegin(throughCover ? downSec : 0f, throughCover ? upSec : 0f,
                    () => PlayStepOverlay(cue, step), glitchTrans);
            }

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
            director.ApplySplit(step.splitX, step.splitFlip, step.splitFreeze);
            ApplyStepOverlay2(step, takeIndex: d.takeIndex, stepIndex: d.stepIndex);

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

        // 演出が終わった / 畳まれた。占有していたならレーン（区間の曲）へ返す。
        private void EndTakeBgm()
        {
            if (!_bgmOverrideActive) return;
            _bgmOverrideActive = false;
            bgmDirector?.EndTakeOverride();
        }

        private void EndTake(TakeRunnerLogic.Decision d)
        {
            // 音は director の有無に関係なく必ず返す（画面が無くても占有だけ残さない）。
            EndTakeBgm();
            if (director == null) return;
            ReleaseStepState();
            showControl?.SetInsertPostOverride(false, null);

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
            if (director.InsertActive) director.InsertReturn(d.returnCamera);
            Debug.Log($"[TakeRunner] 演出終了{(d.forced ? "（watchdog 強制）" : "")} → 復帰 camera={d.returnCamera}");
        }

        // 走行中の演出を安全に畳む（SetTakes / ResetRun / ライブ卓の介入の前に呼ぶ。凍結ストランドを残さない）。
        // releaseScreen=false なら「画面の占有だけ解いてカメラは動かさない」（ライブ卓が既に画面を取っている場合）。
        private void CleanupActive(bool releaseScreen = true)
        {
            _chainPending = false;
            if (!_logic.IsActive) return;
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
            director.ClearFeelFx();
            _logic.AbortActive();
        }

        // カット単位の状態（オーバーレイ・クリップ待ち・開いている録画）を落とす。
        private void ReleaseStepState()
        {
            if (_stepOverlayPlayed) overlay?.StopOverlay();
            _stepOverlayPlayed = false;
            _awaitingClipEnd = false;
            _clipToken = -1;
            _stepFrames?.Dispose();
            _stepFrames = null;
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
