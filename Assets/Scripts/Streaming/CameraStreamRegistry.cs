#nullable enable
using System;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 複数 CameraSource を常時受信し、現在のアクティブを保持する。
    /// 表示側 (MjpegScreen) はここから ActiveStream の Texture を取得する。
    /// </summary>
    public sealed class CameraStreamRegistry : MonoBehaviour
    {
        [SerializeField] private CameraSource[] sources = Array.Empty<CameraSource>();
        [SerializeField] private int initialIndex = 0;

        private CameraStream[] _streams = Array.Empty<CameraStream>();
        private int _activeIndex;

        public int Count => _streams.Length;
        public int ActiveIndex => _activeIndex;

        public event Action<int>? ActiveChanged;

        private void Awake()
        {
            int n = sources.Length;
            _streams = new CameraStream[n];
            for (int i = 0; i < n; i++)
            {
                if (sources[i] == null)
                {
                    Debug.LogError($"[CameraStreamRegistry] sources[{i}] is null.");
                    continue;
                }
                _streams[i] = new CameraStream(sources[i]);
            }
            _activeIndex = (n == 0) ? 0 : Mathf.Clamp(initialIndex, 0, n - 1);
        }

        private void OnEnable()
        {
            foreach (var s in _streams) s?.Start();
        }

        private void Update()
        {
            foreach (var s in _streams) s?.Tick();
        }

        private void OnDestroy()
        {
            foreach (var s in _streams) s?.Dispose();
            _streams = Array.Empty<CameraStream>();
        }

        public CameraStream? Get(int index)
        {
            if (index < 0 || index >= _streams.Length) return null;
            return _streams[index];
        }

        public CameraStream? GetActive() => Get(_activeIndex);

        public void SetActive(int index)
        {
            if (_streams.Length == 0) return;
            int wrapped = WrapIndex(index, _streams.Length);
            if (wrapped == _activeIndex) return;
            _activeIndex = wrapped;
            ActiveChanged?.Invoke(_activeIndex);
        }

        public void Next() => SetActive(_activeIndex + 1);
        public void Prev() => SetActive(_activeIndex - 1);

        /// <summary>負値を含めた wrap-around。count==0 では 0 を返す。</summary>
        public static int WrapIndex(int index, int count)
        {
            if (count <= 0) return 0;
            return ((index % count) + count) % count;
        }
    }
}
