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

        /// <summary>いま録画中か（HUD / 診断用）。</summary>
        public bool IsRecording => _writer != null;

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

        /// <summary>ラン開始（体験者交代・卓の runEpoch 由来）。走行中の録画を閉じ、前ランのファイルを消す。</summary>
        public void ResetRun(int runEpoch)
        {
            StopSegment();
            _runEpoch = runEpoch;
            PurgeOtherRuns(runEpoch);
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

            string path = SegmentPath(CurrentEpoch, lap, camera);
            long maxBytes = (long)Mathf.Max(1, cfg.maxTotalMB) * 1024 * 1024;
            var limits = new SegmentRecordWriter.Limits(maxBytes, cfg.fpsCap);

            try { _writer = new SegmentRecordWriter(path, limits); }
            catch (Exception e)
            {
                Debug.LogWarning($"[SegmentRecorder] 録画を開始できない: {e.Message}");
                _writer = null;
                return;
            }

            _segmentStart = Time.realtimeSinceStartup;
            _tapped = stream;
            float maxSec = cfg.maxSegmentSec > 0f ? cfg.maxSegmentSec : 60f;
            SegmentRecordWriter writer = _writer;
            float start = _segmentStart;
            stream.FrameTap = (buf, len) =>
            {
                float t = Time.realtimeSinceStartup - start;
                if (t > maxSec) return;   // 区間が長引いても上限で止める（体験は止めない）
                writer.TryAppend(buf, len, Mathf.RoundToInt(t * 1000f));
            };
            Debug.Log($"[SegmentRecorder] 録画開始 lap={lap} camera={camera} → {path}");
        }

        private void StopSegment()
        {
            if (_tapped != null) { _tapped.FrameTap = null; _tapped = null; }
            if (_writer == null) return;
            bool capped = _writer.Capped;
            string path = _writer.Path;
            _writer.Dispose();
            _writer = null;
            Debug.Log($"[SegmentRecorder] 録画終了{(capped ? "（容量上限で打ち切り）" : "")} → {path}");
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

        // 現ラン以外のディレクトリを消す。体験者の映像を端末に残さないための運用要件でもある。
        private static void PurgeOtherRuns(int keepEpoch)
        {
            try
            {
                string root = RootDir();
                if (!Directory.Exists(root)) return;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    if (Path.GetFileName(dir) == keepEpoch.ToString()) continue;
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
            catch (Exception e) { Debug.LogWarning($"[SegmentRecorder] 旧ランの掃除に失敗: {e.Message}"); }
        }
    }
}
