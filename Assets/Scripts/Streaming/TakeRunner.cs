#nullable enable
using System;
using System.Collections.Generic;
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

        private readonly TakeRunnerLogic _logic = new();

        // 実行に必要な定義本体（_logic の Def と同じ並び）。
        private ShowTakeDef[] _takes = Array.Empty<ShowTakeDef>();

        // cueId → 素材定義 / URL 解決（ShowControlClient から注入）。
        private Func<string, OverlayCueData?>? _cueResolver;
        private Func<string, string>? _urlResolver;

        // 現カットでオーバーレイを出したか（演出終了時に自分が出した分だけ止める）。
        private bool _stepOverlayPlayed;
        // 現カットが untilClipEnd で、オーバーレイの終了を待っているか。
        private bool _awaitingClipEnd;

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
            if (director == null) director = GetComponent<CameraSwitchDirector>();
            if (overlay == null) overlay = GetComponent<ScreenOverlayController>();
            if (showControl == null) showControl = GetComponent<ShowControlClient>();
        }

        /// <summary>cueId → 素材定義の解決関数を注入する（ShowControlClient に集約）。</summary>
        public void SetCueResolver(Func<string, OverlayCueData?> resolver) => _cueResolver = resolver;

        /// <summary>素材 URL（sa:// / 相対）の解決関数を注入する（ShowControlClient に集約）。</summary>
        public void SetUrlResolver(Func<string, string> resolver) => _urlResolver = resolver;

        /// <summary>ライブ卓の抑止（activeCue 非空 / cameraOverride 非 null）を通知する。</summary>
        public void SetSuppressed(bool suppressed) => _logic.SetSuppressed(suppressed);

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
            foreach (ShowTimelineSegmentDef? seg in segments)
            {
                if (seg?.takes == null) continue;
                foreach (ShowTakeDef? t in seg.takes)
                {
                    if (t == null) continue;
                    takes.Add(t);
                    defs.Add(new TakeRunnerLogic.Def
                    {
                        lap = seg.lap,
                        camera = seg.camera,
                        onExit = t.IsExit,
                        offsetSec = t.offsetSec,
                        skipWhenMissed = t.SkipWhenMissed,
                        once = t.once,
                        maxDurationSec = t.maxDurationSec,
                        stepDurSec = BuildStepDurations(t),
                    });
                }
            }
            _takes = takes.ToArray();
            _logic.SetDefs(defs.ToArray());
        }

        /// <summary>ラン開始（体験者交代）。走行中の演出を畳み、once をクリアする。</summary>
        public void ResetRun()
        {
            CleanupActive();
            _logic.ResetRun();
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
            // untilClipEnd のカットは、オーバーレイが自然終端 / 中止で消えた時点を「終わり」とする。
            if (_awaitingClipEnd && overlay != null && overlay.Current == null)
            {
                _awaitingClipEnd = false;
                _logic.NotifyCurrentStepFinished(Now);
            }

            int latest = ResolveLatestZoneCamera();
            TakeRunnerLogic.Decision d = _logic.Tick(Now, latest);
            Apply(d, exitAnchored: false);
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

            string source = TakeSchema.NormalizeSource(step.source, out bool known);
            if (!known)
                Debug.LogWarning($"[TakeRunner] 未知の source '{step.source}' → live として扱う（take={TakeId(d.takeIndex)}）");

            // post 層はカットごとに掛け替える（無指定のカットでは解除して区間 / カメラ / global へ戻す）。
            showControl?.SetInsertPostOverride(step.hasPost && step.post != null, step.hasPost ? step.post : null);

            // 画面の占有とカメラ。live のときだけカメラを動かす。
            if (source == TakeSchema.SourceLive && step.camera >= 0)
            {
                // 演出の 1 カット目が exit アンカー由来なら、離脱の dip の黒中に差し替える（中間カメラを見せない）。
                if (d.takeStarted && exitAnchored) director.InsertExitRedirect(step.camera);
                else director.InsertBegin(step.camera);
            }
            else if (d.takeStarted)
            {
                director.TakeHoldBegin(); // カメラは変えないが画面は演出が持つ
            }

            // オーバーレイ（cue / 全面差し替え素材）。
            OverlayCueData? cue = BuildStepOverlay(step, source, d.takeIndex, d.stepIndex);
            if (cue != null && overlay != null)
            {
                overlay.PlayCue(cue);
                _stepOverlayPlayed = true;
                _awaitingClipEnd = step.IsUntilClipEnd;
            }
            else
            {
                // 前カットのオーバーレイを引きずらない（live カットへ戻る等）。
                if (_stepOverlayPlayed) overlay?.StopOverlay();
                _stepOverlayPlayed = false;
                _awaitingClipEnd = false;
                if (step.IsUntilClipEnd)
                {
                    // 素材が無いのに untilClipEnd → 尺が決まらない。watchdog 任せにせず既定尺で畳む。
                    Debug.LogWarning($"[TakeRunner] untilClipEnd だが素材が無い（take={TakeId(d.takeIndex)} step={d.stepIndex}）→ " +
                                     $"{TakeSchema.FallbackStepDurSec}s で進める");
                    _logic.SetCurrentStepEnd(Now + TakeSchema.FallbackStepDurSec);
                }
            }

            Debug.Log($"[TakeRunner] {(d.takeStarted ? "演出開始" : "カット")} take={TakeId(d.takeIndex)} " +
                      $"step={d.stepIndex} source={source}" +
                      $"{(step.camera >= 0 && source == TakeSchema.SourceLive ? $" camera={step.camera}" : "")}");
        }

        private void EndTake(TakeRunnerLogic.Decision d)
        {
            if (director == null) return;
            if (_stepOverlayPlayed) overlay?.StopOverlay();
            _stepOverlayPlayed = false;
            _awaitingClipEnd = false;
            showControl?.SetInsertPostOverride(false, null);
            director.InsertReturn(d.returnCamera);
            Debug.Log($"[TakeRunner] 演出終了{(d.forced ? "（watchdog 強制）" : "")} → 復帰 camera={d.returnCamera}");
        }

        // 走行中の演出を安全に畳む（SetTakes / ResetRun の前に呼ぶ。凍結ストランドを残さない）。
        private void CleanupActive()
        {
            if (!_logic.IsActive) return;
            if (_stepOverlayPlayed) overlay?.StopOverlay();
            _stepOverlayPlayed = false;
            _awaitingClipEnd = false;
            if (director != null && director.InsertActive)
            {
                showControl?.SetInsertPostOverride(false, null);
                director.InsertReturn(ResolveLatestZoneCamera());
            }
            _logic.AbortActive();
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

            string assetUrl = "";
            if (TakeSchema.IsAssetSource(source) && !string.IsNullOrEmpty(step.assetUrl))
                assetUrl = _urlResolver != null ? _urlResolver(step.assetUrl) : step.assetUrl;

            bool hasAsset = !string.IsNullOrEmpty(assetUrl);
            bool cueHasSource = cue != null &&
                                (!string.IsNullOrEmpty(cue.sourceUrl) || cue.clip != null || cue.stillImage != null);
            if (!hasAsset && !cueHasSource)
            {
                if (TakeSchema.IsAssetSource(source))
                    Debug.LogWarning($"[TakeRunner] {source} だが素材が無い（take={TakeId(takeIndex)} step={stepIndex}）→ このカットは映像なし");
                return null;
            }

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
