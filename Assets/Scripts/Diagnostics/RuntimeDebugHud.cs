#nullable enable
using System.Text;
using FixedCamVr.Streaming;
using FixedCamVr.Tracking;
using TMPro;
using UnityEngine;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// 実機 Quest 3 用ランタイム HUD。FPS / 接続 / 現在カメラ / ゾーン / HMD 位置 / FX 状態を
    /// 1 個の TMP_Text にまとめて吐き出す。GC アロケを抑えるため StringBuilder + TMP_Text.SetText を使用。
    /// </summary>
    public sealed class RuntimeDebugHud : MonoBehaviour
    {
        [Tooltip("出力先 TMP_Text (1 個)。")]
        [SerializeField] private TMP_Text? text;

        [Tooltip("アクティブカメラ / 接続状態の取得元。")]
        [SerializeField] private CameraStreamRegistry? registry;

        [Tooltip("現在ゾーン取得元。")]
        [SerializeField] private PlayerZoneTracker? tracker;

        [Tooltip("HMD 位置取得用 Transform。OVRCameraRig の CenterEyeAnchor を割り当てる。")]
        [SerializeField] private Transform? hmd;

        [Tooltip("HUD 更新間隔 (秒)。Update 毎フレーム文字列構築を避けて 90Hz を維持するためのスロットリング。")]
        [SerializeField] private float updateInterval = 0.25f;

        [Tooltip("起動時に HUD を表示するか。")]
        [SerializeField] private bool startVisible = true;

        private readonly StringBuilder _sb = new(256);
        private float _accum;
        private float _fpsAccum;
        private int _fpsFrames;
        private float _fps;
        private bool _visible;

        /// <summary>HUD の表示・非表示を外部から切り替える。</summary>
        public void SetVisible(bool v)
        {
            _visible = v;
            if (text != null) text.enabled = v;
        }

        /// <summary>現在の表示状態。</summary>
        public bool IsVisible => _visible;

        private void OnEnable()
        {
            SetVisible(startVisible);
        }

        private void Update()
        {
            // FPS は毎フレーム積算（更新間隔単位で平均化）。
            _fpsAccum += Time.unscaledDeltaTime;
            _fpsFrames++;

            _accum += Time.unscaledDeltaTime;
            if (_accum < updateInterval) return;

            _fps = (_fpsFrames > 0 && _fpsAccum > 0f) ? (_fpsFrames / _fpsAccum) : 0f;
            _fpsAccum = 0f;
            _fpsFrames = 0;
            _accum = 0f;

            if (text == null || !_visible) return;

            _sb.Clear();
            BuildFpsLine(_sb);
            _sb.Append('\n');
            BuildConnLine(_sb);
            _sb.Append('\n');
            BuildZoneLine(_sb);
            _sb.Append('\n');
            BuildHmdLine(_sb);
            _sb.Append('\n');
            BuildFxLine(_sb);

            if (registry == null && tracker == null && hmd == null)
            {
                _sb.Clear();
                _sb.Append("(no refs)");
            }

            text.SetText(_sb);
        }

        private void BuildFpsLine(StringBuilder sb)
        {
            sb.Append("FPS  ");
            AppendFloat1(sb, _fps);
            sb.Append("  GPU --  CPU --");
        }

        private void BuildConnLine(StringBuilder sb)
        {
            if (registry == null)
            {
                sb.Append("CONN -  CAM -/-  (no registry)");
                return;
            }
            var active = registry.GetActive();
            int count = registry.Count;
            int idx = registry.ActiveIndex;
            if (active == null)
            {
                sb.Append("CONN -  CAM ");
                sb.Append(count == 0 ? 0 : idx + 1);
                sb.Append('/');
                sb.Append(count);
                return;
            }
            sb.Append("CONN ");
            sb.Append(active.IsConnected ? '●' : '○');
            sb.Append("  CAM ");
            sb.Append(idx + 1);
            sb.Append('/');
            sb.Append(count);
            sb.Append(' ');
            sb.Append(active.DisplayName);
        }

        private void BuildZoneLine(StringBuilder sb)
        {
            sb.Append("ZONE ");
            if (tracker == null)
            {
                sb.Append("(no tracker)");
                return;
            }
            var z = tracker.CurrentZone;
            if (z == null)
            {
                sb.Append("-          pri --");
                return;
            }
            sb.Append(z.Label);
            sb.Append("  pri ");
            sb.Append(z.Priority);
        }

        private void BuildHmdLine(StringBuilder sb)
        {
            sb.Append("HMD  ");
            if (hmd == null)
            {
                sb.Append("(no hmd)");
                return;
            }
            Vector3 p = hmd.position;
            AppendSignedFixed2(sb, p.x); sb.Append(' ');
            AppendSignedFixed2(sb, p.y); sb.Append(' ');
            AppendSignedFixed2(sb, p.z);
        }

        private void BuildFxLine(StringBuilder sb)
        {
            // Fx 系の有効状態は現状 FxSourceBinder からは公開 API として読めないので NA。
            // Fx 側に取得 API が追加され次第ここを実装する。
            sb.Append("FX   CRT NA   Dust NA   Sobel NA");
        }

        // 小数 1 桁を非アロケで append。負値は - を付与。
        private static void AppendFloat1(StringBuilder sb, float v)
        {
            if (v < 0f) { sb.Append('-'); v = -v; }
            int whole = (int)v;
            int frac = (int)((v - whole) * 10f + 0.5f);
            if (frac >= 10) { whole += 1; frac = 0; }
            sb.Append(whole);
            sb.Append('.');
            sb.Append(frac);
        }

        // 符号 + 小数 2 桁固定（HMD 位置用）。
        private static void AppendSignedFixed2(StringBuilder sb, float v)
        {
            if (v < 0f) { sb.Append('-'); v = -v; }
            else { sb.Append('+'); }
            int whole = (int)v;
            int frac = (int)((v - whole) * 100f + 0.5f);
            if (frac >= 100) { whole += 1; frac = 0; }
            sb.Append(whole);
            sb.Append('.');
            if (frac < 10) sb.Append('0');
            sb.Append(frac);
        }
    }
}
