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

        // HMD を外す / システムメニューで Quest ランタイムが描画ループ(=この Update)を凍結する。
        // 凍結中は各 stream を suspend し、復帰時に計測ウィンドウをリセットする
        // （凍結ぶんの実時間ギャップで lag-detect が誤発火 → 不要な強制再接続を防ぐ）。
        //
        // 設計上の注意（重要）: pause と focus のコールバックは Quest / Link で
        // 非対称に発火しうる（外す時 pause(true)+focus(false)、戻す時 focus(true) のみ等）。
        // _paused || _unfocused の OR ラッチだと片方が立ちっぱなしで永久 suspend → 復帰不能に
        // なる。そのため「どの resume 信号でも確実に解除」する非ラッチ方式にする。
        private bool _suspended;

        private void OnApplicationPause(bool pause)
        {
            Debug.Log($"[HmdLife] OnApplicationPause({pause})");
            if (pause) Suspend("pause"); else Resume("unpause");
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            Debug.Log($"[HmdLife] OnApplicationFocus({hasFocus})");
            if (hasFocus) Resume("focusGained"); else Suspend("focusLost");
        }

        private void Suspend(string why)
        {
            if (_suspended) return;
            _suspended = true;
            Debug.Log($"[HmdLife] -> SUSPEND ({why}) streams={_streams.Length}");
            foreach (var s in _streams) s?.SetSuspended(true);
        }

        private void Resume(string why)
        {
            if (!_suspended) return;
            _suspended = false;
            Debug.Log($"[HmdLife] -> RESUME ({why}) streams={_streams.Length}");
            foreach (var s in _streams) s?.SetSuspended(false);
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

        /// <summary>登録 CameraSource の数（show.json cameras[] との対応付けに使う）。</summary>
        public int SourceCount => sources.Length;

        /// <summary>
        /// index 番カメラの接続先（host/port/Basic 認証）を show.json / 端末キャッシュ値で実行時上書きする。
        /// 接続パラメータが実際に変わった時だけ MJPEG を張り直す（無変化なら no-op で再接続コストを払わない）。
        /// host 空文字は「Web 未設定」とみなし焼き込み .asset 値へ戻す。
        /// </summary>
        public void ApplyEndpoint(int index, string host, int port, string user, string pass)
        {
            if (index < 0 || index >= sources.Length) return;
            var src = sources[index];
            if (src == null) return;
            string before = src.ConnectionKey;
            src.ApplyRuntimeEndpoint(host, port, user, pass);
            if (src.ConnectionKey != before)
                Get(index)?.ReapplyConnection();
        }

        /// <summary>
        /// 絶対指定でアクティブカメラを切り替える（ゾーン連動・オペレータ卓など）。
        /// 範囲外はラップせず clamp + 警告する。ラップは「カメラ番号や cameraIndex の
        /// ズレで無言で別カメラに切り替わる」事故（実害）を招くため、絶対指定では禁じる。
        /// 巡回切替（コントローラの Next/Prev）が欲しい場合は <see cref="Next"/>/<see cref="Prev"/> を使う。
        /// </summary>
        public void SetActive(int index)
        {
            if (_streams.Length == 0) return;
            if (index < 0 || index >= _streams.Length)
            {
                Debug.LogWarning($"[CameraStreamRegistry] SetActive({index}) は範囲 [0,{_streams.Length}) 外。" +
                                 "clamp します（cameraIndex / show.json のカメラ並びと sources の不一致を疑え）。");
                index = Mathf.Clamp(index, 0, _streams.Length - 1);
            }
            if (index == _activeIndex) return;
            _activeIndex = index;
            ActiveChanged?.Invoke(_activeIndex);
        }

        public void Next() => SetActiveWrapped(_activeIndex + 1);
        public void Prev() => SetActiveWrapped(_activeIndex - 1);

        // 巡回切替用。Next/Prev は端で反対側へラップするのが期待挙動。
        private void SetActiveWrapped(int index)
        {
            if (_streams.Length == 0) return;
            int wrapped = WrapIndex(index, _streams.Length);
            if (wrapped == _activeIndex) return;
            _activeIndex = wrapped;
            ActiveChanged?.Invoke(_activeIndex);
        }

        /// <summary>負値を含めた wrap-around。count==0 では 0 を返す。</summary>
        public static int WrapIndex(int index, int count)
        {
            if (count <= 0) return 0;
            return ((index % count) + count) % count;
        }
    }
}
