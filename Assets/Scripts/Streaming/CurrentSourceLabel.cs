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

        // GC アロケ削減用: 文字列補間は state が変化したときのみ実行する。
        private bool _lastConnected;
        private int _lastIndex = -1;
        private int _lastCount = -1;
        private string? _lastDisplayName;

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

            // 毎フレ string 構築を避けるため、state が変化したときだけ補間する。
            bool connected = stream.IsConnected;
            int index = registry.ActiveIndex;
            int count = registry.Count;
            string displayName = stream.DisplayName;
            if (connected != _lastConnected
                || index != _lastIndex
                || count != _lastCount
                || !ReferenceEquals(displayName, _lastDisplayName))
            {
                string indicator = connected ? "●" : "○";
                string text = $"{indicator} [{index + 1}/{count}] {displayName}";
                if (text != _lastText)
                {
                    _label.text = text;
                    _lastText = text;
                }
                _lastConnected = connected;
                _lastIndex = index;
                _lastCount = count;
                _lastDisplayName = displayName;
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
