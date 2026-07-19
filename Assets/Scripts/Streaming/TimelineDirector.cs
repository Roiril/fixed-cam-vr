#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// show.json timeline（スキーマ v2）を既存の実行体へ分配する薄いアダプタ。
    ///   - 区間 cues[] → <see cref="CueScheduler"/>（override 付きの flat エントリ）
    ///   - 区間 insert → <see cref="InsertController"/>
    ///   - 区間 post → <see cref="ShowControlClient.SetPostOverride"/>（区間滞在中のみ・segment > camera > global）
    ///
    /// 現在区間は <see cref="CueScheduler.CameraEntered"/>（LapCounter 由来の deterministic (camera, lap)）で追跡する。
    /// Insert source の切替はこの信号に来ないため、区間追跡は体験者のゾーン進行だけを見る。
    /// timeline supersede の判定と <see cref="SetTimeline"/> / <see cref="Clear"/> の呼び出しは
    /// <see cref="ShowControlClient"/> が行う（本アダプタは分配のみ）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TimelineDirector : MonoBehaviour
    {
        [Tooltip("区間 cues[] を flatten して供給する CueScheduler。CameraEntered の購読元でもある。")]
        [SerializeField] private CueScheduler? cueScheduler;

        [Tooltip("区間 insert を受け取る InsertController。")]
        [SerializeField] private InsertController? insertController;

        [Tooltip("区間 post 上書き層を掛け外しする ShowControlClient。")]
        [SerializeField] private ShowControlClient? showControl;

        private ShowTimelineSegmentDef[] _segments = Array.Empty<ShowTimelineSegmentDef>();
        private bool _hasCurrent;
        private int _curLap, _curCam;
        private bool _subscribed;

        private void Awake()
        {
            if (cueScheduler == null) cueScheduler = GetComponent<CueScheduler>();
            if (insertController == null) insertController = GetComponent<InsertController>();
        }

        private void OnEnable()
        {
            if (cueScheduler != null && !_subscribed)
            {
                cueScheduler.CameraEntered += OnCameraEntered;
                _subscribed = true;
            }
        }

        private void OnDisable()
        {
            if (cueScheduler != null && _subscribed) cueScheduler.CameraEntered -= OnCameraEntered;
            _subscribed = false;
        }

        /// <summary>cueId 解決関数を InsertController へ中継する（ShowControlClient.ResolveCue）。</summary>
        public void SetCueResolver(Func<string, OverlayCueData?> resolver) => insertController?.SetCueResolver(resolver);

        /// <summary>ライブ抑止（activeCue 非空 / cameraOverride 非 null）を InsertController へ中継する。</summary>
        public void SetSuppressed(bool suppressed) => insertController?.SetSuppressed(suppressed);

        /// <summary>
        /// タイムラインを分配する。区間 cues[] を CueScheduler へ、insert を InsertController へ流し込む。
        /// 区間 post は次の <see cref="OnCameraEntered"/>（seed 含む）で貼り直す（ここでは一旦解除する）。
        /// </summary>
        public void SetTimeline(ShowTimelineSegmentDef[]? segments)
        {
            _segments = segments ?? Array.Empty<ShowTimelineSegmentDef>();
            cueScheduler?.SetEntries(FlattenCues(_segments));
            insertController?.SetInserts(_segments);
            _hasCurrent = false;
            showControl?.SetPostOverride(null);
        }

        /// <summary>タイムライン supersede を解除する（timeline 不在時）。legacy schedule は ShowControlClient が供給する。</summary>
        public void Clear()
        {
            _segments = Array.Empty<ShowTimelineSegmentDef>();
            insertController?.SetInserts(_segments);
            _hasCurrent = false;
            showControl?.SetPostOverride(null);
            // cue エントリは触らない（ShowControlClient が legacy schedule を CueScheduler へ直接供給する）。
        }

        /// <summary>ラン開始（runEpoch 変化 / 現地手動）で区間追跡と once をリセットする。</summary>
        public void ResetRun()
        {
            _hasCurrent = false;
            insertController?.ResetRun();
        }

        // ゾーン進入（deterministic post-Feed lap）。離脱区間の exit インサート即時 + 進入区間の
        // enter インサート武装を InsertController へ forward し、区間 post を貼り替える。
        private void OnCameraEntered(int camera, int lap)
        {
            bool hadPrev = _hasCurrent;
            int prevLap = _curLap, prevCam = _curCam;

            insertController?.NotifyZoneCommitted(lap, camera, hadPrev, prevLap, prevCam);

            // 区間 post 上書き（segment > camera > global）。区間が変わったら貼り替え。無い区間は解除。
            ShowTimelineSegmentDef? seg = FindSegment(lap, camera);
            showControl?.SetPostOverride(seg != null && seg.hasPost ? seg.post : null);

            _hasCurrent = true;
            _curLap = lap;
            _curCam = camera;
        }

        private ShowTimelineSegmentDef? FindSegment(int lap, int camera)
        {
            foreach (var s in _segments)
                if (s != null && s.lap == lap && s.camera == camera) return s;
            return null;
        }

        // 全区間の cues[] を CueScheduler の評価エントリへ平坦化する（override を載せる）。
        private static CueScheduleLogic.Entry[] FlattenCues(ShowTimelineSegmentDef[] segments)
        {
            var list = new List<CueScheduleLogic.Entry>();
            foreach (var seg in segments)
            {
                if (seg == null || seg.cues == null) continue;
                foreach (var c in seg.cues)
                {
                    if (c == null) continue;
                    var e = new CueScheduleLogic.Entry
                    {
                        lap = seg.lap,
                        camera = seg.camera,
                        cueId = c.cueId ?? "",
                        delaySec = c.delaySec,
                        once = c.once,
                    };
                    if (c.hasOverride && c.@override != null)
                    {
                        e.ov = new CueScheduleLogic.CueOverride
                        {
                            has = true,
                            strength = c.@override.strength,
                            fadeIn = c.@override.fadeIn,
                            fadeOut = c.@override.fadeOut,
                            trimStart = c.@override.trimStart,
                            trimEnd = c.@override.trimEnd,
                        };
                    }
                    list.Add(e);
                }
            }
            return list.ToArray();
        }
    }
}
