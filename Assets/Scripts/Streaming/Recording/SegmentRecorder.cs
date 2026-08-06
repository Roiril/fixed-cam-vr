#nullable enable
using System;
using System.IO;
using UnityEngine;

namespace FixedCamVr.Streaming.Recording
{
    /// <summary>
    /// **1 周目の映像を端末内に録っておき、3 周目の演出で流す**ための録画係。
    ///
    /// 録画の単位は**区間**（<c>(lap, camera)</c>）で、駆動は<b>ショーの時計</b>
    /// （<see cref="CueScheduler.CameraEntered"/> = 体験者のゾーン進行）。
    /// 画面が何を映しているかとは無関係に録る — 演出が画面を横取りしている間も、
    /// その区間のカメラのライブを録り続ける。
    ///
    /// 保存先は <c>Application.temporaryCachePath/rec/&lt;runEpoch&gt;/L&lt;lap&gt;C&lt;cam&gt;.mjr</c>。
    /// **ラン開始で前ランの録画を消す**（次の体験者に前の人の映像を出さない・端末に残さない）。
    ///
    /// 設計の正本: <c>.claude/plans/2026-07-26_show-sources-and-cg-layer.md</c> F2。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SegmentRecorder : MonoBehaviour
    {
        [Tooltip("区間進入（CameraEntered）の購読元。null なら同 GameObject → シーンから探す。")]
        [SerializeField] private CueScheduler? cueScheduler;

        [Tooltip("録画対象のカメラ受信ユニット。null なら同 GameObject → シーンから探す。")]
        [SerializeField] private CameraStreamRegistry? registry;

        [Tooltip("record 設定（show.json）の供給元。null なら同 GameObject → シーンから探す。")]
        [SerializeField] private ShowControlClient? showControl;

        private SegmentRecordWriter? _writer;
        private CameraStream? _tapped;
        private float _segmentStart;
        private int _runEpoch = -1;
        private bool _subscribed;

        // このランで書いた総バイト数（確定した区間の合計）。maxTotalMB は**ラン全体**の上限なので、
        // 区間ごとに使い切らせず残量を配る（旧実装は各区間へ満額を渡しており、区間数だけ端末を食えた）。
        private long _runBytes;
        private bool _budgetWarned;

        // 最後に開いた区間の (周, カメラ)。**閉じても消さない** — 録画は区間の切れ目で
        // stop→start が同一フレームに起きるので、消すと閉じた区間が何だったのか観測側から辿れない。
        private int _curLap = -1;
        private int _curCamera = -1;

        /// <summary>いま録画中か（HUD / 診断用）。</summary>
        public bool IsRecording => _writer != null;

        /// <summary>このランで録画に使った総バイト数（診断用）。</summary>
        public long RunBytes => _runBytes;

        /// <summary>
        /// 最後に開いた区間の周（1 始まり・まだ 1 度も開いていなければ -1）。
        /// <b>画面に映っているカメラとは別物</b>（演出中は食い違う）。診断・テレメトリ用。
        /// </summary>
        public int CurrentLap => _curLap;

        /// <summary>最後に開いた区間の録画対象カメラ index（まだ開いていなければ -1）。</summary>
        public int CurrentCamera => _curCamera;

        // 最後に閉じた区間の実績。**バイト数だけでは「録れた」を判定できない**
        // （ヘッダだけの空ファイルでもバイト数は 0 にならない）ので、フレーム数を対で持つ。
        private int _lastFrames;
        private long _lastBytes;

        /// <summary>最後に閉じた区間に書けたフレーム数（0 = 1 枚も録れていない）。</summary>
        public int LastSegmentFrames => _lastFrames;

        /// <summary>最後に閉じた区間に書けたバイト数。</summary>
        public long LastSegmentBytes => _lastBytes;

        private void Awake()
        {
            if (cueScheduler == null) cueScheduler = GetComponent<CueScheduler>();
            if (cueScheduler == null) cueScheduler = FindObjectOfType<CueScheduler>();
            if (registry == null) registry = GetComponent<CameraStreamRegistry>();
            if (registry == null) registry = FindObjectOfType<CameraStreamRegistry>();
            if (showControl == null) showControl = GetComponent<ShowControlClient>();
            if (showControl == null) showControl = FindObjectOfType<ShowControlClient>();
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
            StopSegment();
        }

        private void OnDestroy() => StopSegment();

        /// <summary>
        /// ラン開始（体験者交代・卓の runEpoch 由来）。走行中の録画を閉じ、**端末に残っている録画を全部消す**。
        ///
        /// 現ランの epoch だけ残す実装だと、現地リセット（<see cref="ResetRunLocal"/> が epoch を自分で +1 する）で
        /// 進んだ番号に卓の runEpoch が後から追いつくと、前の体験者のファイルが「このランの録画」として
        /// 再生される。ラン開始時点でこのランの録画はまだ 1 本も無いので、全部消して困る場面は無い。
        /// </summary>
        public void ResetRun(int runEpoch)
        {
            StopSegment();
            _runEpoch = runEpoch;
            _runBytes = 0;
            _budgetWarned = false;
            _curLap = -1;
            _curCamera = -1;
            PurgeAllRuns();
        }

        /// <summary>
        /// 現地のランリセット（右グリップ長押し）。卓が居ないので epoch は自分で 1 つ進める
        /// （0 に戻すと前の体験者の録画を上書きしつつ混ざる）。
        /// </summary>
        public void ResetRunLocal() => ResetRun(CurrentEpoch + 1);

        // ---- 録画 ----

        private void OnCameraEntered(int camera, int lap)
        {
            StopSegment();

            ShowRecordDef? cfg = showControl != null ? showControl.RecordConfig : null;
            if (cfg == null || !cfg.RecordsLap(lap)) return;

            CameraStream? stream = registry != null ? registry.Get(camera) : null;
            if (stream == null) return;

            // ラン全体の残量を配る。使い切ったら以降の区間は録らない（体験は止めない）。
            long budget = (long)Mathf.Max(1, cfg.maxTotalMB) * 1024 * 1024;
            long remaining = budget - _runBytes;
            if (remaining <= RecordedSegmentFormat.HeaderBytes)
            {
                if (!_budgetWarned)
                {
                    _budgetWarned = true;
                    Debug.LogWarning($"[SegmentRecorder] ラン全体の録画容量 {cfg.maxTotalMB}MB を使い切った。以降の区間は録らない");
                }
                return;
            }

            string path = SegmentPath(CurrentEpoch, lap, camera);
            var limits = new SegmentRecordWriter.Limits(remaining, cfg.fpsCap, cfg.TailSec);

            try { _writer = new SegmentRecordWriter(path, limits); }
            catch (Exception e)
            {
                Debug.LogWarning($"[SegmentRecorder] 録画を開始できない: {e.Message}");
                _writer = null;
                return;
            }

            _segmentStart = Time.realtimeSinceStartup;
            _curLap = lap;
            _curCamera = camera;
            _tapped = stream;
            SegmentRecordWriter writer = _writer;
            float start = _segmentStart;
            // 区間の間ずっと積む。**残るのは末尾 tailSec 秒だけ**（古い側は writer が落とす）ので、
            // 体験者がどれだけ長く留まってもファイルは一定サイズで、しかも「出る直前」が残る。
            stream.FrameTap = (buf, len) =>
                writer.TryAppend(buf, len, Mathf.RoundToInt((Time.realtimeSinceStartup - start) * 1000f));
            Debug.Log($"[SegmentRecorder] 録画開始 lap={lap} camera={camera} 末尾{limits.tailSec:0.#}s → {path}");
        }

        private void StopSegment()
        {
            if (_tapped != null) { _tapped.FrameTap = null; _tapped = null; }
            if (_writer == null) return;
            string path = _writer.Path;
            _writer.Dispose();          // 末尾を書き出す（この後で WrittenBytes / Capped が確定する）
            _lastBytes = _writer.WrittenBytes;
            _lastFrames = _writer.WrittenFrames;
            _runBytes += _writer.WrittenBytes;
            bool capped = _writer.Capped;
            _writer = null;
            Debug.Log($"[SegmentRecorder] 録画終了{(capped ? "（容量が足りず末尾が縮んだ）" : "")} " +
                      $"lap={_curLap} camera={_curCamera} frames={_lastFrames} bytes={_lastBytes} → {path}");
            if (_lastFrames == 0)
                Debug.LogWarning($"[SegmentRecorder] 1 枚も録れていない lap={_curLap} camera={_curCamera} " +
                                 $"— この区間を指す録画カットは無言で飛びます");
        }

        // ---- 参照（再生側）----

        private int CurrentEpoch => _runEpoch >= 0 ? _runEpoch : 0;

        /// <summary>録画ファイルのパス（存在するとは限らない）。</summary>
        public static string SegmentPath(int runEpoch, int lap, int camera)
            => Path.Combine(RunDir(runEpoch), $"L{lap}C{camera}.mjr");

        /// <summary>現在のランで録れている区間のパス。無ければ空文字。</summary>
        public string ResolveRecorded(int lap, int camera)
        {
            string p = SegmentPath(CurrentEpoch, lap, camera);
            return File.Exists(p) ? p : "";
        }

        private static string RootDir() => Path.Combine(Application.temporaryCachePath, "rec");

        private static string RunDir(int runEpoch) => Path.Combine(RootDir(), runEpoch.ToString());

        // 端末に残っている録画を全部消す。体験者の映像を端末に残さないための運用要件でもある。
        private static void PurgeAllRuns()
        {
            try
            {
                string root = RootDir();
                if (!Directory.Exists(root)) return;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
            catch (Exception e) { Debug.LogWarning($"[SegmentRecorder] 旧ランの掃除に失敗: {e.Message}"); }
        }
    }
}
