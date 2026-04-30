#nullable enable
using TMPro;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// CameraStreamRegistry のアクティブ stream の名前と接続状態を TMP_Text に流す。
    /// 切替直後 fadeSeconds 秒だけ表示し、それ以外はフェードアウト。fadeSeconds<=0 で常時表示。
    /// 接続状態（● Connected / ○ Disconnected）も表示する。
    /// </summary>
    [RequireComponent(typeof(TMP_Text))]
    public sealed class CurrentSourceLabel : MonoBehaviour
    {
        [SerializeField] private CameraStreamRegistry? registry;
        [SerializeField] private float fadeSeconds = 2.0f;
        [SerializeField] private float fadeOutDuration = 0.4f;
        [SerializeField] private bool alwaysShowOnDisconnect = true;

        private TMP_Text? _label;
        private float _showUntil;
        private string _lastText = "";

        private void Awake()
        {
            _label = GetComponent<TMP_Text>();
        }

        private void OnEnable()
        {
            if (registry != null) registry.ActiveChanged += OnActiveChanged;
            _showUntil = (fadeSeconds > 0f) ? Time.time + fadeSeconds : float.PositiveInfinity;
        }

        private void OnDisable()
        {
            if (registry != null) registry.ActiveChanged -= OnActiveChanged;
        }

        private void OnActiveChanged(int index)
        {
            _showUntil = (fadeSeconds > 0f) ? Time.time + fadeSeconds : float.PositiveInfinity;
        }

        private void Update()
        {
            if (registry == null || _label == null) return;

            var stream = registry.GetActive();
            if (stream == null) return;

            string indicator = stream.IsConnected ? "●" : "○";
            string text = $"{indicator} [{registry.ActiveIndex + 1}/{registry.Count}] {stream.DisplayName}";
            if (text != _lastText)
            {
                _label.text = text;
                _lastText = text;
            }

            float alpha;
            if (fadeSeconds <= 0f)
            {
                alpha = 1f;
            }
            else if (alwaysShowOnDisconnect && !stream.IsConnected)
            {
                alpha = 1f;
            }
            else
            {
                float t = _showUntil - Time.time;
                alpha = (t >= 0f) ? 1f : Mathf.Clamp01(1f + t / Mathf.Max(0.01f, fadeOutDuration));
            }
            var c = _label.color;
            if (!Mathf.Approximately(c.a, alpha))
            {
                c.a = alpha;
                _label.color = c;
            }
        }
    }
}
