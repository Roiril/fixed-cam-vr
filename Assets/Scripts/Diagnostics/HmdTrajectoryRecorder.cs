#nullable enable
using System;
using System.Globalization;
using System.Text;
using FixedCamVr.Streaming;
using FixedCamVr.Tracking;
using UnityEngine;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// HMD 位置と各 PlayerZone の含有判定を <c>[HmdTrace]</c> プレフィックスで Unity Console に流す。
    /// シュビーが MCP <c>read_console filter_text="HmdTrace"</c> で時系列を取得する用途。
    /// 調査が終わったら GameObject を消すか enabled=false にするだけで止まる軽量計測。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HmdTrajectoryRecorder : MonoBehaviour
    {
        [Tooltip("HMD 位置取得用 Transform（CenterEyeAnchor 推奨）。null なら Camera.main を使う。")]
        [SerializeField] private Transform? hmd;

        [Tooltip("含有判定対象のゾーン一覧（Tracker と同じ並びを推奨）。")]
        [SerializeField] private PlayerZone[] zones = Array.Empty<PlayerZone>();

        [Tooltip("現在ゾーン取得元。null でも動く（zone 列が '-' になるだけ）。")]
        [SerializeField] private PlayerZoneTracker? tracker;

        [Tooltip("アクティブカメラ取得元。null なら cam 列が '-'。")]
        [SerializeField] private CameraStreamRegistry? registry;

        [Tooltip("ログ出力間隔（秒）。ゆっくり歩く前提なら 1.0 で十分。")]
        [SerializeField, Min(0.1f)] private float sampleInterval = 1.0f;

        [Tooltip("Tracker と揃える hysteresis shrink (m)。inShrunk 列の判定に使用。")]
        [SerializeField, Min(0f)] private float hysteresisShrink = 0.15f;

        private float _accum;
        private int _seq;
        private readonly StringBuilder _sb = new(256);

        private void OnEnable()
        {
            _accum = sampleInterval; // 起動直後に 1 行出す
            _seq = 0;
            Debug.Log("[HmdTrace] start (filter: 'HmdTrace')");
        }

        private void OnDisable()
        {
            Debug.Log($"[HmdTrace] stop samples={_seq}");
        }

        private void Update()
        {
            _accum += Time.unscaledDeltaTime;
            if (_accum < sampleInterval) return;
            _accum = 0f;
            EmitSample();
        }

        private void EmitSample()
        {
            Transform? head = hmd;
            if (head == null)
            {
                var cam = Camera.main;
                if (cam != null) head = cam.transform;
            }
            if (head == null) return;

            Vector3 p = head.position;
            string zoneLabel = tracker?.CurrentZone?.Label ?? "-";

            string camStr = "-";
            string connStr = "-";
            if (registry != null)
            {
                camStr = (registry.ActiveIndex + 1).ToString(CultureInfo.InvariantCulture);
                var active = registry.GetActive();
                connStr = (active != null && active.IsConnected) ? "1" : "0";
            }

            _seq++;
            _sb.Clear();
            _sb.Append("[HmdTrace #").Append(_seq);
            _sb.Append(" t=").Append(Time.unscaledTime.ToString("F1", CultureInfo.InvariantCulture));
            _sb.Append("] pos=(");
            _sb.Append(p.x.ToString("F2", CultureInfo.InvariantCulture)).Append(',');
            _sb.Append(p.y.ToString("F2", CultureInfo.InvariantCulture)).Append(',');
            _sb.Append(p.z.ToString("F2", CultureInfo.InvariantCulture)).Append(')');
            _sb.Append(" zone=").Append(zoneLabel);
            _sb.Append(" cam=").Append(camStr);
            _sb.Append(" conn=").Append(connStr);

            // 各ゾーンの含有判定（in / inShrunk）。デッドゾーンを波形で見つけるため両方出す。
            for (int i = 0; i < zones.Length; i++)
            {
                var z = zones[i];
                if (z == null) continue;
                bool inFull = z.Contains(p);
                bool inShr = z.Contains(p, hysteresisShrink);
                _sb.Append(' ').Append(z.Label).Append('=');
                _sb.Append(inFull ? '1' : '0');
                _sb.Append('/');
                _sb.Append(inShr ? '1' : '0'); // 形式: in/inShrunk
            }

            Debug.Log(_sb.ToString());
        }
    }
}
