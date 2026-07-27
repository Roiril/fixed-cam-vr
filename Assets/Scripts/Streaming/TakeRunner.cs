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

        /// <summary>時刻源を差し替える（EditMode テスト用。null で <c>Time.time</c> に戻る）。</summary>
        public void SetTimeSource(Func<float>? source) => _timeSource = source;

        private void Awake()
        {
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
                    bool onLine = t.IsLine && !string.IsNullOrEmpty(t.lineId);
                    anyLine |= onLine;
                    defs.Add(new TakeRunnerLogic.Def
                    {
                        lap = seg.lap,
                        camera = seg.camera,
                        onExit = t.IsExit,
                        offsetSec = t.offsetSec,
                        skipWhenMissed = t.SkipWhenMissed,
                        once = t.once,
                        maxDurationSec = t.maxDurationSec,
                        yieldOnZoneChange = t.IsYield,
                        stepDurSec = BuildStepDurations(t),
                        onLine = onLine,
                        lineIndex = onLine ? LineSlot(t.lineId) : -1,
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
            foreach (ShowTakeDef t in _takes)
            {
                if (!t.IsLine || string.IsNullOrEmpty(t.lineId)) continue;
                int slot = _lineSlots.IndexOf(t.lineId);
                if (slot < 0 || slot >= lines.Length || !lines[slot].defined)
                    if (!missing.Contains(t.lineId)) missing.Add(t.lineId);
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
        }

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
            if (!IsStepPlayable(step, source, cue, d.takeIndex, d.stepIndex))
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
                if (step.HasCg) _cgLayer.Apply(step.cg, step.cgMode, step.camera >= 0 ? step.camera : ResolveLatestZoneCamera());
                else _cgLayer.Hide();
            }

            // カット遷移（cut / dip / fade）。**source によって効かせ方が違う**:
            //   live   … 画面のライブ層は 1 枚しかないのでクロスフェードできない。cut=瞬時 / dip・fade=黒経由
            //   素材   … cut=瞬時に差し替え / dip=黒経由 / fade=素材のクロスフェード（overlay の fadeIn。従来どおり）
            // 旧実装は素材カットに遷移を一切効かせず、卓は 4 カット全部に遷移欄を出していた（嘘の UI）。
            TakeSchema.SplitTransition(TakeSchema.ResolveTransitionMs(step.transition, step.transitionMs),
                out float downSec, out float upSec);

            // 画面の占有とカメラ。live のときだけカメラを動かす。
            if (source == TakeSchema.SourceLive)
            {
                PlayStepOverlay(cue, step);   // live カットでも cue 重ねは即時（dip の黒で隠れる）
                // 演出の 1 カット目が exit アンカー由来なら、離脱の dip の黒中に差し替える（中間カメラを見せない）。
                if (d.takeStarted && exitAnchored) director.InsertExitRedirect(step.camera, downSec, upSec);
                else director.InsertBegin(step.camera, downSec, upSec);
            }
            else
            {
                bool throughBlack = step.transition == TakeSchema.TransDip;
                if (step.transition == TakeSchema.TransCut && cue != null && step.fadeInSec < 0f)
                    cue.fadeInSeconds = 0f;   // 「瞬時」はフェードも掛けない（明示指定があればそれを尊重）
                // カメラは変えないが画面は演出が持つ。dip のときだけ素材の差し替えを**黒の瞬間**に行う。
                director.TakeHoldBegin(throughBlack ? downSec : 0f, throughBlack ? upSec : 0f,
                    () => PlayStepOverlay(cue, step));
            }

            Debug.Log($"[TakeRunner] {(d.takeStarted ? "演出開始" : "カット")} take={TakeId(d.takeIndex)} " +
                      $"step={d.stepIndex} source={source}" +
                      $"{(source == TakeSchema.SourceLive ? $" camera={step.camera}" : "")}");
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
        /// </summary>
        private bool IsStepPlayable(ShowStepDef step, string source, OverlayCueData? cue, int takeIndex, int stepIndex)
        {
            if (source == TakeSchema.SourceLive)
            {
                int count = director != null ? director.CameraCount : 0;
                if (step.camera < 0 || (count > 0 && step.camera >= count))
                {
                    Debug.LogWarning($"[TakeRunner] live のカメラ {step.camera} が範囲 [0,{count}) 外 → " +
                                     $"このカットを飛ばす（take={TakeId(takeIndex)} step={stepIndex}）");
                    return false;
                }
                return true;
            }
            if ((TakeSchema.IsAssetSource(source) || TakeSchema.IsRecSource(source)) && cue == null)
            {
                Debug.LogWarning($"[TakeRunner] {source} だが素材が解決できない → " +
                                 $"このカットを飛ばす（take={TakeId(takeIndex)} step={stepIndex}）");
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
            // 画面を実際に持っていた時だけ返す。全 step が §6.4 で飛ばされた演出は画面に触っていないので、
            // ここで dip を掛けると「何も起きていないのに暗転する」ことになる。
            if (director.InsertActive) director.InsertReturn(d.returnCamera);
            Debug.Log($"[TakeRunner] 演出終了{(d.forced ? "（watchdog 強制）" : "")} → 復帰 camera={d.returnCamera}");
        }

        // 走行中の演出を安全に畳む（SetTakes / ResetRun / ライブ卓の介入の前に呼ぶ。凍結ストランドを残さない）。
        // releaseScreen=false なら「画面の占有だけ解いてカメラは動かさない」（ライブ卓が既に画面を取っている場合）。
        private void CleanupActive(bool releaseScreen = true)
        {
            if (!_logic.IsActive) return;
            EndTakeBgm();
            ReleaseStepState();
            if (director != null && director.InsertActive)
            {
                showControl?.SetInsertPostOverride(false, null);
                if (releaseScreen) director.InsertReturn(ResolveLatestZoneCamera());
                else director.TakeHoldEnd();
            }
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
            return RecordedFramePlayer.Open(path);
        }

        // カットの尺配列（untilClipEnd は負値＝外部通知待ち）。
        private static float[] BuildStepDurations(ShowTakeDef take)
        {
            ShowStepDef[] steps = take.steps ?? Array.Empty<ShowStepDef>();
            var durs = new float[steps.Length];
            for (int i = 0; i < steps.Length; i++)
            {
                ShowStepDef s = steps[i];
                if (s == null) { durs[i] = TakeSchema.FallbackStepDurSec; continue; }
                if (s.IsUntilClipEnd) { durs[i] = -1f; continue; }
                durs[i] = s.durSec > 0f ? s.durSec : TakeSchema.FallbackStepDurSec;
            }
            return durs;
        }
    }
}
