#nullable enable
using System;
using FixedCamVr.Diagnostics;
using FixedCamVr.Streaming;
using UnityEngine;
using UnityEngine.XR;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
using System.Text;
#endif

namespace FixedCamVr.OvrBridge
{
    /// <summary>Development ビルドで Halt / Prompt が実際に見えている間だけ性能を記録する。</summary>
    public sealed class CommsPerformanceProbe : MonoBehaviour
    {
        private CommsPanel? _panel;
        private bool _active;
        private bool _nativeUnavailable;
        private int _sequence;
        private int _windowFrames;
        private int _totalFrames;
        private double _windowSec;
        private double _totalSec;
        private float _windowMinDt = float.PositiveInfinity;
        private float _windowMaxDt;
        private float _totalMinDt = float.PositiveInfinity;
        private float _totalMaxDt;
        private int _beginDropped = -1;
        private int _previousDropped = -1;
        private CommsNotice _notice;
        private CommsStage _stage;
        private float _recoverySec;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private long _loadCount;
        private long _tickCount;
        private long _lumaCount;
        private double _loadMs;
        private double _tickMs;
        private double _lumaMs;
        private double _previousFrameLoadMs;
        private int _previousFrameLoadCount;
        private int _slowFrames;
        private double _slowPreviousLoadMaxMs;
        private int _slowPreviousLoadCallsAtMax;
        private int _slowLogCount;
        private CameraStreamRegistry? _registry;
        private long[] _cameraDecoded = Array.Empty<long>();
        private long[] _cameraSkipped = Array.Empty<long>();
        private readonly StringBuilder _cameraStats = new StringBuilder(256);
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize()
        {
            if (!Debug.isDebugBuild) return;
            new GameObject("[CommsPerformanceProbe]").AddComponent<CommsPerformanceProbe>();
        }

        private void Start()
        {
            _panel = FindObjectOfType<CommsPanel>(true);
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            _registry = FindObjectOfType<CameraStreamRegistry>(true);
            int count = _registry != null ? _registry.Count : 0;
            _cameraDecoded = new long[count];
            _cameraSkipped = new long[count];
#endif
            if (_panel == null) enabled = false;
        }

        private void LateUpdate()
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            // unscaledDeltaTime は前フレームからの経過。今回の decode と同じ時刻とは扱わない。
            double previousLoadMs = _previousFrameLoadMs;
            int previousLoadCount = _previousFrameLoadCount;
            _previousFrameLoadMs = CameraStream.LoadImageTiming.CurrentFrameMs;
            _previousFrameLoadCount = CameraStream.LoadImageTiming.CurrentFrameCount;
#endif
            bool visible = _panel != null && _panel.isActiveAndEnabled
                && (_panel.LastNotice == CommsNotice.Halt || _panel.LastNotice == CommsNotice.Prompt)
                && _panel.AppliedPanelAlpha > 0.002f;
            if (!visible)
            {
                EndWindow();
                return;
            }

            _notice = _panel!.LastNotice;
            _stage = _panel.Stage;
            _recoverySec = _panel.RecoverySec;
            if (!_active) BeginWindow();

            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f || float.IsNaN(dt) || float.IsInfinity(dt)) return;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            if (dt > 0.040f && _slowLogCount < 4)
            {
                _slowLogCount++;
                Debug.Log($"[CommsPerfSlow] seq={_sequence} frame={Time.frameCount} dtms={dt * 1000f:F3} " +
                          $"previousLoadMs={previousLoadMs:F3} previousLoadCount={previousLoadCount} " +
                          $"now={Time.realtimeSinceStartup:F3} RecoverySec={_recoverySec:F3}");
            }
            if (dt > 0.020f)
            {
                _slowFrames++;
                if (previousLoadMs > _slowPreviousLoadMaxMs)
                {
                    _slowPreviousLoadMaxMs = previousLoadMs;
                    _slowPreviousLoadCallsAtMax = previousLoadCount;
                }
            }
#endif
            _windowFrames++;
            _totalFrames++;
            _windowSec += dt;
            _totalSec += dt;
            _windowMinDt = Mathf.Min(_windowMinDt, dt);
            _windowMaxDt = Mathf.Max(_windowMaxDt, dt);
            _totalMinDt = Mathf.Min(_totalMinDt, dt);
            _totalMaxDt = Mathf.Max(_totalMaxDt, dt);
            if (_windowSec < 1.0) return;

            Metrics metrics = ReadMetrics();
            WriteLog("window", metrics);
            _previousDropped = metrics.Dropped;
            ResetSample();
        }

        private void OnDisable() => EndWindow();

        private void BeginWindow()
        {
            _active = true;
            _sequence++;
            _totalFrames = 0;
            _totalSec = 0.0;
            _totalMinDt = float.PositiveInfinity;
            _totalMaxDt = 0f;
            ResetSample();
            Metrics metrics = ReadMetrics();
            _beginDropped = metrics.Dropped;
            _previousDropped = metrics.Dropped;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            _slowLogCount = 0;
            _loadCount = CameraStream.LoadImageTiming.Count;
            _loadMs = CameraStream.LoadImageTiming.TotalMs;
            _tickCount = CameraStream.TickTiming.Count;
            _tickMs = CameraStream.TickTiming.TotalMs;
            _lumaCount = MjpegScreen.SampleLumaTiming.Count;
            _lumaMs = MjpegScreen.SampleLumaTiming.TotalMs;
            CameraStream.LoadImageTiming.ReadAndResetFrameMaxMs();
            CameraStream.TickTiming.ReadAndResetFrameMaxMs();
            MjpegScreen.SampleLumaTiming.ReadAndResetFrameMaxMs();
            CaptureCameraBaseline();
#endif
            WriteLog("begin", metrics);
        }

        private void EndWindow()
        {
            if (!_active) return;
            _active = false;
            WriteLog("end", ReadMetrics());
        }

        private void ResetSample()
        {
            _windowFrames = 0;
            _windowSec = 0.0;
            _windowMinDt = float.PositiveInfinity;
            _windowMaxDt = 0f;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            _slowFrames = 0;
            _slowPreviousLoadMaxMs = 0.0;
            _slowPreviousLoadCallsAtMax = 0;
#endif
        }

        private struct Metrics
        {
            public float CpuRaw;
            public float GpuRaw;
            public int Dropped;
            public float ActualHz;
        }

        private Metrics ReadMetrics()
        {
            var result = new Metrics { CpuRaw = -1f, GpuRaw = -1f, Dropped = -1, ActualHz = -1f };
            if (_nativeUnavailable) return result;
            try
            {
                result.CpuRaw = ValidMetric(OVRPlugin.GetPerfMetricsFloat(OVRPlugin.PerfMetrics.App_CpuTime_Float));
                result.GpuRaw = ValidMetric(OVRPlugin.GetPerfMetricsFloat(OVRPlugin.PerfMetrics.App_GpuTime_Float));
                int? dropped = OVRPlugin.GetPerfMetricsInt(OVRPlugin.PerfMetrics.Compositor_DroppedFrameCount_Int);
                result.Dropped = dropped.HasValue && dropped.Value >= 0 ? dropped.Value : -1;
                float hz = OVRPlugin.systemDisplayFrequency;
                result.ActualHz = hz > 0f && !float.IsInfinity(hz) ? hz : -1f;
            }
            catch (DllNotFoundException)
            {
                _nativeUnavailable = true;
            }
            catch (EntryPointNotFoundException)
            {
                _nativeUnavailable = true;
            }
            return result;
        }

        private static float ValidMetric(float? value)
            => value.HasValue && value.Value >= 0f && !float.IsInfinity(value.Value) ? value.Value : -1f;

        private static int DroppedSince(int current, int previous)
            => current >= 0 && previous >= 0 && current >= previous ? current - previous : -1;

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private void CaptureCameraBaseline()
        {
            for (int i = 0; i < _cameraDecoded.Length; i++)
            {
                var stream = _registry != null ? _registry.Get(i) : null;
                _cameraDecoded[i] = stream?.SuccessfulDecodeCount ?? 0;
                _cameraSkipped[i] = stream?.SkippedOpportunityCount ?? 0;
            }
        }

        private string CameraStats()
        {
            _cameraStats.Clear();
            _cameraStats.Append("cameras=").Append(_cameraDecoded.Length)
                .Append(" activeCamera=").Append(_registry != null ? _registry.ActiveIndex : -1);
            for (int i = 0; i < _cameraDecoded.Length; i++)
            {
                var stream = _registry != null ? _registry.Get(i) : null;
                long decoded = stream?.SuccessfulDecodeCount ?? 0;
                long skipped = stream?.SkippedOpportunityCount ?? 0;
                _cameraStats.Append(" cam").Append(i).Append("Decoded=").Append(decoded - _cameraDecoded[i])
                    .Append(" cam").Append(i).Append("SkipOpp=").Append(skipped - _cameraSkipped[i])
                    .Append(" cam").Append(i).Append("AgeMs=").Append((stream?.DecodedImageAgeMs ?? -1f).ToString("F1"));
                _cameraDecoded[i] = decoded;
                _cameraSkipped[i] = skipped;
            }
            return _cameraStats.ToString();
        }

        private static void ReadWorkTiming(StreamWorkTiming timing, ref long previousCount, ref double previousMs,
            out long count, out double ms, out double frameMaxMs)
        {
            long currentCount = timing.Count;
            double currentMs = timing.TotalMs;
            count = currentCount - previousCount;
            ms = currentMs - previousMs;
            frameMaxMs = timing.ReadAndResetFrameMaxMs();
            previousCount = currentCount;
            previousMs = currentMs;
        }
#endif

        private void WriteLog(string kind, Metrics metrics)
        {
            // SDK の C# 定義は単位を明記しない。秒として raw * 1000 した値と raw を両方残す。
            float cpuMs = metrics.CpuRaw >= 0f ? metrics.CpuRaw * 1000f : -1f;
            float gpuMs = metrics.GpuRaw >= 0f ? metrics.GpuRaw * 1000f : -1f;
            double fps = _windowSec > 0.0 ? _windowFrames / _windowSec : -1.0;
            double totalFps = _totalSec > 0.0 ? _totalFrames / _totalSec : -1.0;
            float minFps = _windowMaxDt > 0f ? 1f / _windowMaxDt : -1f;
            float totalMinFps = _totalMaxDt > 0f ? 1f / _totalMaxDt : -1f;
            float minDtMs = _windowFrames > 0 ? _windowMinDt * 1000f : -1f;
            float maxDtMs = _windowFrames > 0 ? _windowMaxDt * 1000f : -1f;
            float totalMinDtMs = _totalFrames > 0 ? _totalMinDt * 1000f : -1f;
            float totalMaxDtMs = _totalFrames > 0 ? _totalMaxDt * 1000f : -1f;
            int eyeWidth = XRSettings.enabled ? XRSettings.eyeTextureWidth : -1;
            float viewportScale = XRSettings.enabled ? XRSettings.renderViewportScale : -1f;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            int loadFrameMaxCalls = CameraStream.LoadImageTiming.PeriodMaxFrameCount;
            ReadWorkTiming(CameraStream.LoadImageTiming, ref _loadCount, ref _loadMs,
                out long loadCount, out double loadMs, out double loadFrameMaxMs);
            ReadWorkTiming(CameraStream.TickTiming, ref _tickCount, ref _tickMs,
                out long tickCount, out double tickMs, out double tickFrameMaxMs);
            ReadWorkTiming(MjpegScreen.SampleLumaTiming, ref _lumaCount, ref _lumaMs,
                out long lumaCount, out double lumaMs, out double lumaFrameMaxMs);
#endif
            Debug.Log(
                $"[CommsPerf] {kind} seq={_sequence} notice={_notice} RecoverySec={_recoverySec:F3} Stage={_stage} " +
                $"sec={_windowSec:F3} frames={_windowFrames} fps={fps:F2} minFps={minFps:F2} minDtMs={minDtMs:F3} maxDtMs={maxDtMs:F3} " +
                $"totalSec={_totalSec:F3} totalFrames={_totalFrames} totalFps={totalFps:F2} totalMinFps={totalMinFps:F2} totalMinDtMs={totalMinDtMs:F3} totalMaxDtMs={totalMaxDtMs:F3} " +
                $"cpuMs={cpuMs:F3} gpuMs={gpuMs:F3} cpuRaw={metrics.CpuRaw:F6} gpuRaw={metrics.GpuRaw:F6} " +
                $"dropped={metrics.Dropped} droppedDelta={DroppedSince(metrics.Dropped, _previousDropped)} totalDropped={DroppedSince(metrics.Dropped, _beginDropped)} " +
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                $"loadCount={loadCount} loadMs={loadMs:F3} loadFrameMaxMs={loadFrameMaxMs:F3} " +
                $"loadFrameMaxCalls={loadFrameMaxCalls} {CameraStats()} " +
                $"tickCount={tickCount} tickMs={tickMs:F3} tickFrameMaxMs={tickFrameMaxMs:F3} " +
                $"lumaCount={lumaCount} lumaMs={lumaMs:F3} lumaFrameMaxMs={lumaFrameMaxMs:F3} " +
                $"slowFrames20ms={_slowFrames} slowPrevLoadFrameMaxMs={_slowPreviousLoadMaxMs:F3} slowPrevLoadCallsAtMax={_slowPreviousLoadCallsAtMax} " +
#endif
                $"desiredHz={DisplayRateRequester.DesiredHz:F1} actualHz={metrics.ActualHz:F1} eyeTextureWidth={eyeWidth} renderViewportScale={viewportScale:F3}");
        }
    }
}
