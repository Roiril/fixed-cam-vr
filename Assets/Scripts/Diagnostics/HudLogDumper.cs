#nullable enable
using System.Text;
using FixedCamVr.Streaming;
using FixedCamVr.Tracking;
using UnityEngine;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// HUD と同じ値を Unity Console に <c>[HudDump]</c> プレフィックス付きで吐く。
    ///
    /// 設計:
    /// - 状態変化（CONN / CAM index / ZONE label）があった瞬間に 1 行吐く
    /// - 加えて <see cref="periodicIntervalSec"/> 秒ごとに 1 行だけ定期吐き（動いている事の確認）
    /// - これでログが埋もれずに済み、シュビーは MCP <c>read_console filter_text="HudDump"</c> で時系列が取れる
    /// </summary>
    public sealed class HudLogDumper : MonoBehaviour
    {
        [Tooltip("接続 / カメラ / ゾーン取得元（HUD と同じ参照を刺す）。")]
        [SerializeField] private CameraStreamRegistry? registry;

        [Tooltip("現在ゾーン取得元。")]
        [SerializeField] private PlayerZoneTracker? tracker;

        [Tooltip("HMD 位置取得用 Transform。CenterEyeAnchor。")]
        [SerializeField] private Transform? hmd;

        [Tooltip("FPS 計測ウィンドウ（秒）。HUD と同じ値を推奨。")]
        [SerializeField] private float fpsWindowSec = 1.0f;

        [Tooltip("変化が無い時に最低 1 行は吐く間隔（秒）。長めに取って良い。")]
        [SerializeField] private float periodicIntervalSec = 30f;

        [Tooltip("起動時のみ 1 行吐く initial dump を有効化。")]
        [SerializeField] private bool initialDump = true;

        private readonly StringBuilder _sb = new(256);

        private float _fpsAccum;
        private int _fpsFrames;
        private float _fps;

        private float _sincePeriodic;

        private int _seq;

        // 直前の値（変化検知用）
        private bool _hasPrev;
        private bool _prevConnected;
        private int _prevCamIndex = -1;
        private string _prevZone = "";

        private void OnEnable()
        {
            _sincePeriodic = 0f;
            _hasPrev = false;
            if (initialDump)
            {
                Dump("init");
                StampPrev();
            }
        }

        private void Update()
        {
            // FPS 集計
            _fpsAccum += Time.unscaledDeltaTime;
            _fpsFrames++;
            if (_fpsAccum >= fpsWindowSec && _fpsFrames > 0)
            {
                _fps = _fpsFrames / _fpsAccum;
                _fpsAccum = 0f;
                _fpsFrames = 0;
            }

            // 変化検知
            string reason = DetectChange();
            if (reason != null)
            {
                Dump(reason);
                StampPrev();
                _sincePeriodic = 0f;
                return;
            }

            // 定期 ping
            _sincePeriodic += Time.unscaledDeltaTime;
            if (_sincePeriodic >= periodicIntervalSec)
            {
                Dump("periodic");
                StampPrev();
                _sincePeriodic = 0f;
            }
        }

        private string? DetectChange()
        {
            bool conn = false;
            int camIdx = -1;
            string zone = "";

            if (registry != null)
            {
                var active = registry.GetActive();
                conn = active != null && active.IsConnected;
                camIdx = registry.ActiveIndex;
            }
            if (tracker != null && tracker.CurrentZone != null)
            {
                zone = tracker.CurrentZone.Label ?? "";
            }

            if (!_hasPrev) return null;
            if (conn != _prevConnected) return "conn";
            if (camIdx != _prevCamIndex) return "cam";
            if (zone != _prevZone) return "zone";
            return null;
        }

        private void StampPrev()
        {
            _hasPrev = true;
            _prevConnected = registry?.GetActive()?.IsConnected ?? false;
            _prevCamIndex = registry?.ActiveIndex ?? -1;
            _prevZone = tracker?.CurrentZone?.Label ?? "";
        }

        private void Dump(string reason)
        {
            _seq++;
            _sb.Clear();
            _sb.Append("[HudDump #");
            _sb.Append(_seq);
            _sb.Append(" t=");
            _sb.Append(Time.unscaledTime.ToString("F1"));
            _sb.Append(" why=");
            _sb.Append(reason);
            _sb.Append("] FPS=");
            _sb.Append(_fps.ToString("F1"));

            // CONN / CAM
            _sb.Append(" CONN=");
            CameraStream? activeForHealth = null;
            if (registry == null)
            {
                _sb.Append("noreg");
            }
            else
            {
                var active = registry.GetActive();
                activeForHealth = active;
                _sb.Append(active != null && active.IsConnected ? "1" : "0");
                _sb.Append(" CAM=");
                if (active == null)
                {
                    _sb.Append('-');
                }
                else
                {
                    _sb.Append(registry.ActiveIndex + 1);
                    _sb.Append('/');
                    _sb.Append(registry.Count);
                    _sb.Append(' ');
                    _sb.Append(active.DisplayName);
                }
            }

            // 配信側 fps / uptime / Unity 受信 fps（あれば）
            var health = activeForHealth?.Health;
            if (health != null)
            {
                _sb.Append(" PHONE_FPS=");
                _sb.Append(health.fps.ToString("F1"));
                _sb.Append(" RECV_FPS=");
                _sb.Append(activeForHealth!.ReceivedFps.ToString("F1"));
                _sb.Append(" UP=");
                _sb.Append((health.uptimeMs / 1000L).ToString());
                _sb.Append("s");
            }
            // Metadata（向き）が取れていれば付ける（向き変更検知用）
            var meta = activeForHealth?.Metadata;
            if (meta != null)
            {
                _sb.Append(" ROT=");
                _sb.Append(meta.rotationDeg);
                _sb.Append(" ");
                _sb.Append(meta.widthPx);
                _sb.Append("x");
                _sb.Append(meta.heightPx);
            }

            // ZONE
            _sb.Append(" ZONE=");
            var z = tracker?.CurrentZone;
            if (z == null) _sb.Append('-');
            else { _sb.Append(z.Label); _sb.Append('@'); _sb.Append(z.Priority); }

            // HMD
            if (hmd != null)
            {
                Vector3 p = hmd.position;
                _sb.Append(" HMD=");
                _sb.Append(p.x.ToString("F2")); _sb.Append(',');
                _sb.Append(p.y.ToString("F2")); _sb.Append(',');
                _sb.Append(p.z.ToString("F2"));
            }

            Debug.Log(_sb.ToString());
        }
    }
}
