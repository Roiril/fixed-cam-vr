#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// Web オペレータ卓（show.json）の状態を long-poll で受けて Unity 側へ適用するクライアント。
    /// Screen GameObject（MjpegScreen / ScreenOverlayController と同居）に付ける。
    ///
    /// 適用対象:
    ///   - control.activeCue      → ScreenOverlayController.PlayCue / StopOverlay
    ///   - control.cameraOverride → registry.SetActive + ゾーン Tracker の無効化
    ///   - post.*                 → ScreenComposite マテリアルのポスト FX プロパティ
    /// 逆方向: /unity/heartbeat へ 2s ごとに現状（アクティブカメラ・fps・発火中 cue）を報告。
    ///
    /// サーバ不在でも体験は成立する（接続失敗はリトライし続けるだけ）。
    /// カメラ override 解除後、ゾーン自律切替は次の境界跨ぎから復帰する。
    /// </summary>
    public sealed class ShowControlClient : MonoBehaviour
    {
        private static readonly int ExposureId = Shader.PropertyToID("_Exposure");
        private static readonly int ContrastId = Shader.PropertyToID("_Contrast");
        private static readonly int SaturationId = Shader.PropertyToID("_Saturation");
        private static readonly int TemperatureId = Shader.PropertyToID("_Temperature");
        private static readonly int VignetteId = Shader.PropertyToID("_Vignette");
        private static readonly int GrainId = Shader.PropertyToID("_Grain");
        private static readonly int ScanlineId = Shader.PropertyToID("_Scanline");

        [SerializeField] private ShowServerSource? server;
        [SerializeField] private CameraStreamRegistry? registry;

        [Tooltip("カメラ手動 override 中に無効化するゾーン Tracker（PlayerZoneTracker）。" +
                 "asmdef 循環回避のため Behaviour 参照で持つ。null なら override 時も Tracker は動き続ける。")]
        [SerializeField] private Behaviour? zoneTrackerToDisable;

        [Tooltip("heartbeat 送信間隔 (秒)。")]
        [SerializeField, Min(0.5f)] private float heartbeatInterval = 2f;

        private ScreenOverlayController? _overlay;
        private Material? _material;
        private int _rev = -1;
        private string _appliedCue = "";
        private string _appliedOverride = "";

        [Serializable] private class ShowState
        {
            public int rev;
            public CameraDef[] cameras = Array.Empty<CameraDef>();
            public CueDef[] cues = Array.Empty<CueDef>();
            public PostParams? post;
            public ControlState? control;
        }
        [Serializable] private class CameraDef { public string id = ""; public string sourceId = ""; }
        [Serializable] private class CueDef
        {
            public string id = "";
            public string name = "";
            public string maskUrl = "";
            public string sourceUrl = "";
            public float strength = 1f;
            public bool loop = true;
            public float fadeIn = 0.5f;
            public float fadeOut = 0.5f;
        }
        [Serializable] private class PostParams
        {
            public float exposure;
            public float contrast = 1f;
            public float saturation = 1f;
            public float temperature;
            public float vignette;
            public float grain;
            public float scanline;
        }
        [Serializable] private class ControlState { public string? activeCue; public string? cameraOverride; }

        private void Awake()
        {
            _overlay = GetComponent<ScreenOverlayController>();
            var renderer = GetComponent<Renderer>();
            _material = renderer != null ? renderer.material : null;
        }

        private void OnEnable()
        {
            if (server == null)
            {
                Debug.LogWarning("[ShowControl] server 未設定。オペレータ卓なしで続行。");
                enabled = false;
                return;
            }
            _ = PollLoopAsync(destroyCancellationToken);
            _ = HeartbeatLoopAsync(destroyCancellationToken);
        }

        // ---- state long-poll ----

        private async Task PollLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    string url = server!.BuildUrl($"/state?rev={_rev}");
                    using var req = UnityWebRequest.Get(url);
                    req.timeout = 35; // サーバ側 long-poll 上限 25s より長く
                    var op = req.SendWebRequest();
                    while (!op.isDone)
                    {
                        ct.ThrowIfCancellationRequested();
                        await Task.Yield();
                    }
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        await Task.Delay(2000, ct); // サーバ不在。静かにリトライ
                        continue;
                    }
                    var state = JsonUtility.FromJson<ShowState>(req.downloadHandler.text);
                    if (state == null) continue;
                    if (state.rev != _rev)
                    {
                        _rev = state.rev;
                        Apply(state);
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ShowControl] poll error: {e.Message}");
                    try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return; }
                }
            }
        }

        private void Apply(ShowState state)
        {
            // 1) ポスト FX → マテリアル直書き
            if (state.post != null && _material != null)
            {
                var p = state.post;
                _material.SetFloat(ExposureId, p.exposure);
                _material.SetFloat(ContrastId, p.contrast);
                _material.SetFloat(SaturationId, p.saturation);
                _material.SetFloat(TemperatureId, p.temperature);
                _material.SetFloat(VignetteId, p.vignette);
                _material.SetFloat(GrainId, p.grain);
                _material.SetFloat(ScanlineId, p.scanline);
            }

            // 2) カメラ手動 override（show.cameras の並び = registry sources の並びが前提）
            string ovr = state.control?.cameraOverride ?? "";
            if (ovr != _appliedOverride)
            {
                _appliedOverride = ovr;
                bool hasOverride = !string.IsNullOrEmpty(ovr);
                if (zoneTrackerToDisable != null) zoneTrackerToDisable.enabled = !hasOverride;
                if (hasOverride && registry != null)
                {
                    int idx = Array.FindIndex(state.cameras, c => c.id == ovr);
                    if (idx >= 0) registry.SetActive(idx);
                    else Debug.LogWarning($"[ShowControl] unknown camera id: {ovr}");
                }
            }

            // 3) cue 発火 / 停止
            string cueId = state.control?.activeCue ?? "";
            if (cueId != _appliedCue)
            {
                _appliedCue = cueId;
                if (_overlay == null) return;
                if (string.IsNullOrEmpty(cueId))
                {
                    _overlay.StopOverlay();
                }
                else
                {
                    var def = Array.Find(state.cues, c => c.id == cueId);
                    if (def == null)
                    {
                        Debug.LogWarning($"[ShowControl] unknown cue id: {cueId}");
                        return;
                    }
                    _overlay.PlayCue(new OverlayCueData
                    {
                        id = def.id,
                        displayName = string.IsNullOrEmpty(def.name) ? def.id : def.name,
                        sourceUrl = server!.Absolute(def.sourceUrl),
                        maskUrl = server.Absolute(def.maskUrl),
                        strength = def.strength,
                        loop = def.loop,
                        fadeInSeconds = def.fadeIn,
                        fadeOutSeconds = def.fadeOut,
                    });
                }
            }
        }

        // ---- heartbeat ----

        [Serializable] private class Heartbeat
        {
            public string activeCamera = "";
            public int activeIndex = -1;
            public float recvFps;
            public string playingCue = "";
            public string cameraOverride = "";
            // ここまで適用した show.json の rev。UI / 自動検証が「Unity 反映済み」を機械判定する。
            public int appliedRev = -1;
        }

        private async Task HeartbeatLoopAsync(CancellationToken ct)
        {
            var hb = new Heartbeat();
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var active = registry != null ? registry.GetActive() : null;
                    hb.activeCamera = active?.DisplayName ?? "";
                    hb.activeIndex = registry != null ? registry.ActiveIndex : -1;
                    hb.recvFps = active?.ReceivedFps ?? 0f;
                    hb.playingCue = _overlay?.Current?.id ?? "";
                    hb.cameraOverride = _appliedOverride;
                    hb.appliedRev = _rev;

                    string json = JsonUtility.ToJson(hb);
                    using var req = UnityWebRequest.Post(
                        server!.BuildUrl("/unity/heartbeat"), json, "application/json");
                    req.timeout = 3;
                    var op = req.SendWebRequest();
                    while (!op.isDone)
                    {
                        ct.ThrowIfCancellationRequested();
                        await Task.Yield();
                    }
                }
                catch (OperationCanceledException) { return; }
                catch { /* heartbeat はベストエフォート */ }

                try { await Task.Delay((int)(heartbeatInterval * 1000), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
