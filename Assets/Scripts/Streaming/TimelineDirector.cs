#nullable enable
using System;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// show.json の timeline（区間 = (lap, camera)）を実行体へ分配する薄いアダプタ。
    ///   - 区間 takes[] → <see cref="TakeRunner"/>（演出・カットの唯一の実行体）
    ///   - 区間 post → <see cref="ShowControlClient.SetPostOverride"/>（区間滞在中のみ・segment &gt; camera &gt; global）
    ///   - 区間 bgm → <see cref="BgmDirector.ApplySegment"/>（指示のある区間だけが状態を変える）
    ///
    /// **2026-07-25 に v3（演出・カット）へ一本化した。** v2（cues[] / insert）の show.json は
    /// <see cref="TimelineMigration.EnsureTakes"/> が takes[] へ決定的に変換してから渡ってくるので、
    /// ここには版の分岐が無い（旧 InsertController / 区間 cues[] の CueScheduler 供給は廃止）。
    /// <see cref="CueScheduler"/> は「もっと古い schedule.entries を焼き込みから読む」経路と
    /// 区間追跡の信号（<see cref="CueScheduler.CameraEntered"/>）としてだけ残っている。
    ///
    /// 現在区間は CameraEntered（LapCounter 由来の deterministic (camera, lap)）で追跡する。
    /// 演出の画面切替はこの信号に来ないため、区間追跡は体験者のゾーン進行だけを見る。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TimelineDirector : MonoBehaviour
    {
        [Tooltip("区間進入（CameraEntered）の購読元。区間 takes[] の供給先ではない（v3 は TakeRunner が実行する）。")]
        [SerializeField] private CueScheduler? cueScheduler;

        [Tooltip("区間 takes[] を受け取る TakeRunner。null なら同 GameObject から取得（無ければ自分で載せる）。")]
        [SerializeField] private TakeRunner? takeRunner;

        [Tooltip("区間 post 上書き層を掛け外しする ShowControlClient。")]
        [SerializeField] private ShowControlClient? showControl;

        [Tooltip("区間 bgm 指示を受け取る BgmDirector。null ならシーンから探す。無ければ BGM 制御なし（後方互換）。")]
        [SerializeField] private BgmDirector? bgmDirector;

        private ShowTimelineSegmentDef[] _segments = Array.Empty<ShowTimelineSegmentDef>();
        private bool _hasCurrent;
        private int _curLap, _curCam;
        private bool _subscribed;

        private void Awake()
        {
            if (cueScheduler == null) cueScheduler = GetComponent<CueScheduler>();
            // 演出の実行体。既存シーン / prefab に未配置でも動くよう、無ければ自分で載せる
            //（TakeRunner.Awake が Director / overlay / showControl をシーンから解決する）。
            // シーン配線を必須にしないのは、prefab の SerializeField 欠落で実機機能が全死した実績があるため。
            if (takeRunner == null) takeRunner = GetComponent<TakeRunner>();
            if (takeRunner == null) takeRunner = gameObject.AddComponent<TakeRunner>();
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

        /// <summary>cueId 解決関数を実行体へ中継する（ShowControlClient.ResolveCue）。</summary>
        public void SetCueResolver(Func<string, OverlayCueData?> resolver) => takeRunner?.SetCueResolver(resolver);

        /// <summary>素材 URL（sa:// / 相対）の解決関数を TakeRunner へ中継する。</summary>
        public void SetUrlResolver(Func<string, string> resolver) => takeRunner?.SetUrlResolver(resolver);

        /// <summary>ライブ抑止（activeCue 非空 / cameraOverride 非 null）を実行体へ中継する。</summary>
        public void SetSuppressed(bool suppressed) => takeRunner?.SetSuppressed(suppressed);

        /// <summary>走行中の演出だけを畳んで画面をライブへ返す（卓の ■ 画面を取り返す）。</summary>
        public void AbortActive() => takeRunner?.AbortActive();

        /// <summary>体験者の報告を演出へ中継する（untilMark のカットだけが反応する）。</summary>
        public void NotifyVisitorMark() => takeRunner?.NotifyVisitorMark();

        /// <summary>走行中のカットが体験者の報告を待っているか（自動走行の検証用）。</summary>
        public bool IsWaitingForVisitorMark => takeRunner != null && takeRunner.IsWaitingForVisitorMark;

        /// <summary>
        /// <b>報告で実際に演出が消えた回数</b>（テレメトリ用）。押した回数とは別物 —
        /// 消えない演出の方が多いので、混ぜると「効いたか」がログから分からなくなる。
        /// </summary>
        public int DismissCount => takeRunner != null ? takeRunner.DismissCount : 0;

        /// <summary>
        /// <b>途中で切れた演出を出し直した回数</b>（テレメトリ用）。引き返しが実際に再演へ繋がったかの唯一の証拠。
        /// </summary>
        public int ReplayCount => takeRunner != null ? takeRunner.ReplayCount : 0;

        /// <summary>直近に演出が終わった理由（テレメトリ用）。</summary>
        public TakeRunnerLogic.EndReason LastEndReason =>
            takeRunner != null ? takeRunner.LastEndReason : TakeRunnerLogic.EndReason.Completed;

        /// <summary>走行中の演出の id（卓のモニタ用。走っていなければ空）。</summary>
        public string ActiveTakeId => takeRunner != null ? takeRunner.ActiveTakeId : "";

        /// <summary>再生中の端末内録画（テレメトリ用）。null なら録画カットではない。</summary>
        public Recording.RecordedFramePlayer? ActiveRecording => takeRunner?.ActiveRecording;

        /// <summary>再生中の録画が指す周（1 始まり）。無ければ -1。</summary>
        public int ActiveRecordingLap => takeRunner != null ? takeRunner.ActiveRecordingLap : -1;

        /// <summary>再生中の録画が指すカメラ index。無ければ -1。</summary>
        public int ActiveRecordingCamera => takeRunner != null ? takeRunner.ActiveRecordingCamera : -1;

        /// <summary>
        /// タイムラインを分配する。区間 takes[] を <see cref="TakeRunner"/> へ流し込み、
        /// 区間 post は次の <see cref="OnCameraEntered"/> で貼り直す（ここでは一旦解除する）。
        /// 渡される segments は **v3（takes[] 済み）**であること（変換は ShowControlClient が行う）。
        /// </summary>
        public void SetTimeline(ShowTimelineSegmentDef[]? segments)
        {
            _segments = segments ?? Array.Empty<ShowTimelineSegmentDef>();
            // もっと古い schedule.entries が残っていても二重発火しないよう、区間経路では常に空にする
            //（画面の所有者を 1 人に保つ）。
            cueScheduler?.SetEntries(Array.Empty<CueScheduleLogic.Entry>());
            takeRunner?.SetTakes(_segments);
            _hasCurrent = false;
            showControl?.SetPostOverride(null);
        }

        /// <summary>タイムライン supersede を解除する（timeline 不在時）。legacy schedule は ShowControlClient が供給する。</summary>
        public void Clear()
        {
            _segments = Array.Empty<ShowTimelineSegmentDef>();
            takeRunner?.SetTakes(null);
            _hasCurrent = false;
            showControl?.SetPostOverride(null);
            // cue エントリは触らない（ShowControlClient が legacy schedule を CueScheduler へ直接供給する）。
        }

        /// <summary>ラン開始（runEpoch 変化 / 現地手動）で区間追跡と once をリセットする。</summary>
        public void ResetRun()
        {
            _hasCurrent = false;
            takeRunner?.ResetRun();
        }

        // ゾーン進入（deterministic post-Feed lap）。離脱区間の exit 演出即時 + 進入区間の
        // 演出武装を TakeRunner へ forward し、区間 post / bgm を貼り替える。
        //
        // ⚠ 読むのは **区間の周**（lap・逆走で戻る）。進行の周（progressLap）は使わない —
        //   引き返した先は「前にそこに居たときの区間」として演出・post・BGM を貼るのが正しい
        //   （進行の周で貼ると、まだ通っていない先の周の演出が消費される）。
        private void OnCameraEntered(int camera, int lap, int progressLap)
        {
            bool hadPrev = _hasCurrent;
            int prevLap = _curLap, prevCam = _curCam;

            takeRunner?.NotifyZoneCommitted(lap, camera, hadPrev, prevLap, prevCam);

            // 区間 post 上書き（segment > camera > global）。区間が変わったら貼り替え。無い区間は解除。
            ShowTimelineSegmentDef? seg = FindSegment(lap, camera);
            showControl?.SetPostOverride(seg != null && seg.hasPost ? seg.post : null);

            // 区間 BGM 指示。post と違い「解除」は無く、指示のある区間だけが状態を変える
            // （指示の無い区間では鳴っている曲がそのまま続く＝動画編集のオーディオトラックと同じ感覚）。
            if (seg != null && seg.hasBgm) ResolveBgm()?.ApplySegment(seg.bgm, true);

            _hasCurrent = true;
            _curLap = lap;
            _curCam = camera;
        }

        // 既存シーンで未配線でも動くよう遅延解決する（ShowControlClient の ResolveBgmDirector と同流儀）。
        private BgmDirector? ResolveBgm()
        {
            if (bgmDirector != null) return bgmDirector;
            bgmDirector = FindObjectOfType<BgmDirector>();
            return bgmDirector;
        }

        private ShowTimelineSegmentDef? FindSegment(int lap, int camera)
        {
            foreach (var s in _segments)
                if (s != null && s.lap == lap && s.camera == camera) return s;
            return null;
        }
    }
}
