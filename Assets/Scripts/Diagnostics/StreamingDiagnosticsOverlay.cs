#nullable enable
using FixedCamVr.Streaming;
using UnityEngine;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// Quest を被らずにストリーミング状態を Editor Play / Flat ビルドで確認するための
    /// IMGUI オーバーレイ。FlatStreaming.unity 等の非 VR デモシーンに 1 個だけ置く想定。
    ///
    /// 表示:
    /// - 描画 FPS / 受信 FPS / 配信 FPS / その比率（lag 検知の閾値と並べて表示）
    /// - 現在カメラ index / 接続状態 / メタ情報（rotationDeg / isPortrait / 解像度）
    /// - 直近 5s に強制再接続が走った時刻
    ///
    /// ホットキー:
    /// - R: 強制再接続
    /// - N / P: 次 / 前 のカメラへ切替（CameraStreamRegistry 経由）
    /// - T: オーバーレイ表示 ON/OFF
    /// </summary>
    public sealed class StreamingDiagnosticsOverlay : MonoBehaviour
    {
        [Tooltip("登録済みカメラの取得元。null なら scene を探す。")]
        [SerializeField] private CameraStreamRegistry? registry;

        [Tooltip("起動時に表示するか。")]
        [SerializeField] private bool startVisible = true;

        [Tooltip("画面左上からのオフセット (px)。")]
        [SerializeField] private Vector2 anchor = new(12, 12);

        [Tooltip("オーバーレイの幅 (px)。")]
        [SerializeField] private int width = 360;

        private bool _visible;
        private float _renderFpsAccum;
        private int _renderFpsFrames;
        private float _renderFps;
        private float _lastForcedReconnectTime = -999f;
        private GUIStyle? _boxStyle;
        private GUIStyle? _labelStyle;

        private void OnEnable()
        {
            _visible = startVisible;
            if (registry == null) registry = FindObjectOfType<CameraStreamRegistry>();
        }

        private void Update()
        {
            // 描画 FPS（1s 平均）
            _renderFpsAccum += Time.unscaledDeltaTime;
            _renderFpsFrames++;
            if (_renderFpsAccum >= 1f)
            {
                _renderFps = _renderFpsFrames / _renderFpsAccum;
                _renderFpsAccum = 0f;
                _renderFpsFrames = 0;
            }

            // ホットキー
            if (Input.GetKeyDown(KeyCode.T)) _visible = !_visible;
            if (registry != null)
            {
                if (Input.GetKeyDown(KeyCode.R))
                {
                    var active = registry.GetActive();
                    if (active != null)
                    {
                        active.ForceReconnect();
                        _lastForcedReconnectTime = Time.unscaledTime;
                        Debug.Log("[Overlay] 'R' hotkey: forced reconnect requested.");
                    }
                }
                if (Input.GetKeyDown(KeyCode.N)) registry.Next();
                if (Input.GetKeyDown(KeyCode.P)) registry.Prev();
            }
        }

        private void OnGUI()
        {
            if (!_visible) return;

            EnsureStyles();

            GUILayout.BeginArea(new Rect(anchor.x, anchor.y, width, Screen.height - anchor.y - 12), _boxStyle);

            GUILayout.Label("<b>FixedCam Streaming Diagnostics</b>  [T to toggle]", _labelStyle);
            GUILayout.Space(4);

            if (registry == null)
            {
                GUILayout.Label("registry: <not assigned, no scene match>", _labelStyle);
                GUILayout.EndArea();
                return;
            }

            var active = registry.GetActive();

            // FPS パネル
            float renderFps = _renderFps;
            float recvFps = active?.ReceivedFps ?? 0f;
            float phoneFps = active?.Health?.fps ?? 0f;
            float ratio = phoneFps > 0.1f ? recvFps / phoneFps : 0f;
            string lagTag = ratio > 0f && ratio < 0.6f ? " <color=#ff6060>LAG</color>" : "";

            GUILayout.Label(
                $"render={renderFps:F1}fps  recv={recvFps:F1}  phone={phoneFps:F1}  ratio={ratio:F2}{lagTag}",
                _labelStyle);

            // 接続 / カメラ
            string connTag = (active?.IsConnected == true) ? "<color=#80ff80>OK</color>" : "<color=#ff8080>--</color>";
            int idx = registry.ActiveIndex;
            int count = registry.Count;
            string camName = active?.DisplayName ?? "-";
            GUILayout.Label($"conn={connTag}  cam={idx + 1}/{count} {camName}", _labelStyle);

            // メタ情報
            var meta = active?.Metadata;
            if (meta != null)
            {
                GUILayout.Label(
                    $"rot={meta.rotationDeg}  port={(meta.isPortrait ? 1 : 0)}  res={meta.widthPx}x{meta.heightPx}  fov={meta.lensFovDeg:F1}",
                    _labelStyle);
            }
            else
            {
                GUILayout.Label("meta: (none)", _labelStyle);
            }

            // /health
            var health = active?.Health;
            if (health != null)
            {
                GUILayout.Label(
                    $"up={health.uptimeMs / 1000}s  frames={health.totalFrames}  bytes={health.totalBytes / 1024 / 1024}MB",
                    _labelStyle);
            }

            // 直近の reconnect マーカ
            if (Time.unscaledTime - _lastForcedReconnectTime < 3f)
            {
                GUILayout.Label("<color=#ffd060>(R) forced reconnect requested</color>", _labelStyle);
            }

            GUILayout.Space(4);
            GUILayout.Label("<i>R=reconnect (auto on lag)  N/P=cam  T=toggle</i>", _labelStyle);

            GUILayout.EndArea();
        }

        private void EnsureStyles()
        {
            if (_boxStyle != null && _labelStyle != null) return;
            _boxStyle = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(8, 8, 6, 6),
            };
            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                richText = true,
                fontSize = 13,
                wordWrap = true,
            };
        }
    }
}
