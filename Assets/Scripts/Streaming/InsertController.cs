#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// インサートショット（区間から別カメラを差し込む演出）の実行体。<see cref="InsertLogic"/> の判定を
    /// <see cref="CameraSwitchDirector"/>（dip-to-black 付き切替 + ゾーン凍結）・
    /// <see cref="ScreenOverlayController"/>（cueId）・<see cref="ShowControlClient"/>（insert 中の post 層）へ写す。
    ///
    /// 駆動は <see cref="TimelineDirector"/>（区間確定 (lap,camera) を <see cref="NotifyZoneCommitted"/> で forward）。
    /// 自身はイベント購読しない。exit インサートは黒転換中（同期）に呼ばれ
    /// <see cref="CameraSwitchDirector.InsertExitRedirect"/> で黒のまま insert カメラへ差し替える。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InsertController : MonoBehaviour
    {
        [Tooltip("dip-to-black 付き切替を担う CameraSwitchDirector。null ならインサートは動かない。")]
        [SerializeField] private CameraSwitchDirector? director;

        [Tooltip("insert.cueId の再生に使う ScreenOverlayController。null なら cue なしで切替のみ。")]
        [SerializeField] private ScreenOverlayController? overlay;

        [Tooltip("insert 中の post 層を掛け外しする ShowControlClient。null なら post 上書きなし。")]
        [SerializeField] private ShowControlClient? showControl;

        private readonly InsertLogic _logic = new();

        // cueId → OverlayCueData の解決関数（ShowControlClient.ResolveCue を注入）。
        private Func<string, OverlayCueData?>? _cueResolver;

        // defIndex 並びの insert post（insert.hasPost ? insert.post : null）。null は「insert 先カメラ post / global」。
        private PostParams?[] _postByDef = Array.Empty<PostParams?>();

        // 現インサートが cue を再生したか（復帰時にその cue だけを止めるため）。
        private bool _insertPlayedCue;

        /// <summary>タイムライン区間からインサート定義を（再）構築する（hasInsert を持つ区間のみ）。</summary>
        public void SetInserts(ShowTimelineSegmentDef[]? segments)
        {
            // 進行中インサートを畳んでから定義を差し替える。畳まないと director の insert 凍結
            // （_insertActive）が InsertReturn 単一経路でしか解除されないため残り続け、ゾーン自動切替が
            // 恒久凍結する（ショー中のタイムライン編集で踏む）。CleanupActiveInsert は _logic をリセットする前に呼ぶ。
            CleanupActiveInsert();
            if (segments == null)
            {
                _logic.SetDefs(Array.Empty<InsertLogic.Def>());
                _postByDef = Array.Empty<PostParams?>();
                return;
            }
            var defs = new List<InsertLogic.Def>();
            var posts = new List<PostParams?>();
            foreach (var seg in segments)
            {
                if (seg == null || !seg.hasInsert || seg.insert == null) continue;
                var ins = seg.insert;
                defs.Add(new InsertLogic.Def
                {
                    lap = seg.lap,
                    camera = seg.camera,
                    onExit = ins.IsExit,
                    insertCamera = ins.camera,
                    delaySec = ins.delaySec,
                    durationSec = ins.durationSec,
                    cueId = ins.cueId ?? "",
                    once = ins.once,
                });
                posts.Add(ins.hasPost ? ins.post : null);
            }
            _logic.SetDefs(defs.ToArray());
            _postByDef = posts.ToArray();
        }

        /// <summary>cueId → OverlayCueData の解決関数を注入する（ShowControlClient に集約）。</summary>
        public void SetCueResolver(Func<string, OverlayCueData?> resolver) => _cueResolver = resolver;

        /// <summary>ライブ抑止（activeCue 非空 / cameraOverride 非 null）を通知する。</summary>
        public void SetSuppressed(bool suppressed) => _logic.SetSuppressed(suppressed);

        /// <summary>体験の明示リセット（once 発火済みと進行を全消去）。</summary>
        public void ResetRun()
        {
            // 進行中インサートを畳んでから once / 進行を消す（SetInserts と同理由で凍結ストランドを防ぐ）。
            CleanupActiveInsert();
            _logic.ResetRun();
        }

        /// <summary>
        /// 進行中インサート（EnterDelay / Showing）を安全に後片付けする。表示中（director 側で
        /// insert 凍結が立っている＝BeginInsert 済み）なら insert cue 停止・insert post 解除・
        /// <see cref="CameraSwitchDirector.InsertReturn"/> で凍結解除まで行う。ResetRun / SetInserts が
        /// <see cref="InsertLogic"/> をリセットする前に呼ぶ（リセットは _logic を Idle へ戻すため、
        /// 復帰カメラの算出は畳む前に行う必要がある）。非進行時は no-op。
        /// </summary>
        private void CleanupActiveInsert()
        {
            if (!_logic.IsActive) return;
            // 自分が出した insert cue を止める（区間 cue を巻き込まない）。
            if (_insertPlayedCue) { overlay?.StopOverlay(); _insertPlayedCue = false; }
            // director 側の凍結（Showing = BeginInsert 済み）だけ後片付けする。EnterDelay は未表示で
            // 凍結も post も立っていないため、余計な dip を出さないよう InsertReturn は呼ばない。
            if (director != null && director.InsertActive)
            {
                showControl?.SetInsertPostOverride(false, null);
                int latest = _logic.BaseZoneCamera;
                if (director.TryGetPendingZone(out int pending)) latest = pending;
                director.InsertReturn(latest);   // 復帰は Insert source（リセット中は周回へ数えない）
            }
        }

        /// <summary>
        /// ゾーン確定（TimelineDirector 経由の deterministic (lap,camera)）を受ける。
        /// exit インサートはこの同期呼び中に <see cref="CameraSwitchDirector.InsertExitRedirect"/> を叩く
        /// （黒転換中の即差し替え）。enter インサートは武装のみ（<see cref="Update"/> が delay 後に発火）。
        /// </summary>
        public void NotifyZoneCommitted(int newLap, int newCam, bool hadPrev, int prevLap, int prevCam)
        {
            InsertLogic.Decision d = _logic.OnZoneCommitted(newLap, newCam, hadPrev, prevLap, prevCam, Time.time);
            Apply(d);
        }

        private void Update()
        {
            // 復帰先 = インサート中に体験者が移動した先（Director の保留ゾーン）> 開始時ゾーン。
            int latest = _logic.BaseZoneCamera;
            if (director != null && director.TryGetPendingZone(out int pending)) latest = pending;

            InsertLogic.Decision d = _logic.Tick(Time.time, latest);
            Apply(d);
        }

        private void Apply(InsertLogic.Decision d)
        {
            switch (d.action)
            {
                case InsertLogic.Action.BeginExit: BeginInsert(d, exit: true); break;
                case InsertLogic.Action.BeginEnter: BeginInsert(d, exit: false); break;
                case InsertLogic.Action.End: EndInsert(d.camera); break;
            }
        }

        private void BeginInsert(InsertLogic.Decision d, bool exit)
        {
            if (director == null)
            {
                Debug.LogWarning("[InsertController] director 未配線のためインサートを実行できない。");
                return;
            }
            // insert 中の post 層を先に立てる（切替で ActiveChanged → ApplyPostForActive が読む）。
            PostParams? post = (d.defIndex >= 0 && d.defIndex < _postByDef.Length) ? _postByDef[d.defIndex] : null;
            showControl?.SetInsertPostOverride(true, post);

            if (exit) director.InsertExitRedirect(d.camera);
            else director.InsertBegin(d.camera);

            // insert.cueId があれば表示に合わせて再生（cue 評価より後に呼ばれるので最後の命令が勝つ）。
            _insertPlayedCue = false;
            if (!string.IsNullOrEmpty(d.cueId) && overlay != null)
            {
                OverlayCueData? data = _cueResolver?.Invoke(d.cueId);
                if (data != null) { overlay.PlayCue(data); _insertPlayedCue = true; }
                else Debug.LogWarning($"[InsertController] insert cue 未解決: {d.cueId}");
            }
            Debug.Log($"[InsertController] インサート開始 ({(exit ? "exit" : "enter")}) camera={d.camera}" +
                      $"{(string.IsNullOrEmpty(d.cueId) ? "" : $" cue={d.cueId}")}");
        }

        private void EndInsert(int returnCamera)
        {
            if (director == null) return;
            // 自分が出した insert cue だけ止める（区間 cue を巻き込まない）。
            if (_insertPlayedCue) { overlay?.StopOverlay(); _insertPlayedCue = false; }
            // insert 中に体験者が実ゾーンを移動していた（復帰先 != 開始時ゾーン = BaseZoneCamera）なら、
            // 復帰 commit を Zone 相当で通知して LapCounter / TimelineDirector に実ゾーン移動を反映する
            // （insert 復帰が SwitchSource.Insert のままだと Zone ゲートで Feed されず lap under-count・
            //  区間追跡ズレ・exit insert の誤遷移を起こす）。移動が無ければ従来どおり Insert（周回に数えない）。
            // 進行ポインタは順方向一致でしか進まないため、この Zone 通知で二重カウントは起きない。
            bool movedDuringInsert = returnCamera != _logic.BaseZoneCamera;
            director.InsertReturn(returnCamera, asZone: movedDuringInsert);
            showControl?.SetInsertPostOverride(false, null);
            Debug.Log($"[InsertController] インサート終了 → 復帰 camera={returnCamera}" +
                      $"{(movedDuringInsert ? "（実ゾーン移動を反映＝Zone commit）" : "")}");
        }
    }
}
