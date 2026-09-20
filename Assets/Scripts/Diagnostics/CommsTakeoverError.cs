#nullable enable

using System.Globalization;
using FixedCamVr.Streaming;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// Comms の乗っ取り表示と主画面の不正アクセス表示を同じ時計で開始・終了する。
    /// </summary>
    public sealed class CommsTakeoverError : MonoBehaviour
    {
        private CommsPanel? _panel;
        private GameObject? _prefab;
        private Transform? _screenAnchor;
        private Vector2 _screenSize;
        private bool _screenExplicit;
        private GameObject? _instance;
        private UnauthorizedAccessEffect? _effect;
        private bool _active;
        private bool _blockFailedLogged;
        private bool _warnedMissingPrefab;
        private bool _warnedMissingScreen;

        public void Configure(CommsPanel panel, GameObject? prefab)
        {
            if (_active) StopEffect();
            ReleaseInstance();
            _panel = panel;
            _prefab = prefab;
            _blockFailedLogged = false;
            _warnedMissingPrefab = false;
            if (!_screenExplicit)
            {
                _screenAnchor = null;
                _screenSize = Vector2.zero;
            }
        }

        /// <summary>独立プレビューとテストが本番と同じ時計を使うための画面指定。</summary>
        public void ConfigureScreen(Transform anchor, Vector2 size)
        {
            if (anchor == null) throw new System.ArgumentNullException(nameof(anchor));
            if (size.x <= 0f || size.y <= 0f)
                throw new System.ArgumentOutOfRangeException(nameof(size), "Screen size must be positive.");
            if (_active) StopEffect();
            _screenAnchor = anchor;
            _screenSize = size;
            _screenExplicit = true;
            _warnedMissingScreen = false;
        }

        private void LateUpdate() =>
            Tick(_panel != null && _panel.TakeoverVisible, Time.unscaledDeltaTime);

        /// <summary>乗っ取り表示の実時間を進める。プレビューと EditMode テストもこの入口を使う。</summary>
        public void Tick(bool visible, float dt)
        {
            _ = dt;
            if (visible && !_active) StartEffect();
            if (!visible && _active)
            {
                StopEffect();
                return;
            }
            if (!_active || _effect == null) return;
            float elapsed = _panel != null ? _panel.TakeoverElapsedSec : 0f;
            bool blockFailed = _panel != null && _panel.TakeoverBlockFailed;
            float opacity = _panel != null ? _panel.TakeoverErrorOpacity : 1f;
            _effect.SampleTakeover(elapsed, blockFailed, opacity);
            if (blockFailed && !_blockFailedLogged)
            {
                _blockFailedLogged = true;
                Debug.Log("[XP] ev=commsError phase=BlockFailed sec="
                    + elapsed.ToString("0.000", CultureInfo.InvariantCulture)
                    + " opacity=" + opacity.ToString("0.000", CultureInfo.InvariantCulture));
            }
        }

        private void StartEffect()
        {
            if (_prefab == null)
            {
                WarnOnce(ref _warnedMissingPrefab,
                    "[CommsTakeoverError] UnauthorizedAccess prefab がありません。主画面の乗っ取りエラーを表示できません。");
                return;
            }
            // 初期化後に映像のアスペクト比が確定するため、開始時点の実寸を取り直す。
            if (!_screenExplicit)
                ResolveScreen();
            if (_screenAnchor == null || _screenSize.x <= 0f || _screenSize.y <= 0f)
            {
                WarnOnce(ref _warnedMissingScreen,
                    "[CommsTakeoverError] MjpegScreen の実画面と寸法を解決できません。主画面の乗っ取りエラーを表示できません。");
                return;
            }

            ReleaseInstance();
            _instance = Instantiate(_prefab);
            _instance.name = _prefab.name + " (Comms takeover)";
            if (gameObject.scene.IsValid() && _instance.scene != gameObject.scene)
                SceneManager.MoveGameObjectToScene(_instance, gameObject.scene);
            _effect = _instance.GetComponent<UnauthorizedAccessEffect>();
            if (_effect == null)
            {
                Debug.LogWarning("[CommsTakeoverError] UnauthorizedAccess prefab に UnauthorizedAccessEffect がありません。");
                ReleaseInstance();
                return;
            }

            // UnauthorizedAccessEffect 自身の 7 秒時計は使わない。Comms の時計を SampleTakeover へ渡す。
            _effect.enabled = false;
            _effect.Play(_screenAnchor, _screenSize);
            _active = true;
            _blockFailedLogged = false;
            Debug.Log("[XP] ev=commsError active=1 screen=1 spatial=1");
        }

        private void StopEffect()
        {
            _effect?.Stop();
            if (_active) Debug.Log("[XP] ev=commsError active=0 screen=0 spatial=0");
            _active = false;
            _blockFailedLogged = false;
        }

        private void ResolveScreen()
        {
            var screen = FindObjectOfType<MjpegScreen>();
            if (screen == null) return;
            var filter = screen.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) return;
            Vector3 meshSize = filter.sharedMesh.bounds.size;
            Vector3 scale = screen.transform.lossyScale;
            var size = new Vector2(Mathf.Abs(meshSize.x * scale.x), Mathf.Abs(meshSize.y * scale.y));
            if (size.x <= 0f || size.y <= 0f) return;
            _screenAnchor = screen.transform;
            _screenSize = size;
            _warnedMissingScreen = false;
        }

        private void OnDisable()
        {
            StopEffect();
            ReleaseInstance();
        }

        private void OnDestroy()
        {
            StopEffect();
            ReleaseInstance();
        }

        private void ReleaseInstance()
        {
            if (_instance == null)
            {
                _effect = null;
                return;
            }
            if (Application.isPlaying) Destroy(_instance);
            else DestroyImmediate(_instance);
            _instance = null;
            _effect = null;
        }

        private static void WarnOnce(ref bool warned, string message)
        {
            if (warned) return;
            warned = true;
            Debug.LogWarning(message);
        }
    }
}
