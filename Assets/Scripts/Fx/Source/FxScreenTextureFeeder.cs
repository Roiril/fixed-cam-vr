#nullable enable
using UnityEngine;

namespace FixedCamVr.Fx.Source
{
    /// <summary>
    /// FxSourceBinder の Texture を Renderer.material.mainTexture へ流し込むだけの薄いフィーダー。
    /// 既存 MjpegScreen のロジックを真似ているが、こちらは MJPEG とは無関係に
    /// Binder（テストパターン or 任意 Texture）を表示する。
    /// </summary>
    [RequireComponent(typeof(Renderer))]
    public sealed class FxScreenTextureFeeder : MonoBehaviour
    {
        [Tooltip("流し込む Texture を提供するブリッジ")]
        [SerializeField] private FxSourceBinder? binder;
        [Tooltip("mainTexture の書き込み先 Renderer (未設定時は同 GameObject の Renderer)")]
        [SerializeField] private Renderer? targetRenderer;

        private Texture? _last;
        // Renderer.material は呼ぶたびに sharedMaterial の複製を生成するので
        // 1 度だけ取得して保持し、OnDestroy で破棄する。
        private Material? _matInstance;

        private void Awake()
        {
            if (targetRenderer == null) targetRenderer = GetComponent<Renderer>();
        }

        private void OnDestroy()
        {
            if (_matInstance != null)
            {
                if (Application.isPlaying) Destroy(_matInstance);
                else DestroyImmediate(_matInstance);
                _matInstance = null;
            }
        }

        private void Update()
        {
            if (binder == null || targetRenderer == null) return;
            var tex = binder.Current;
            if (!ReferenceEquals(tex, _last))
            {
                if (_matInstance == null) _matInstance = targetRenderer.material;
                _matInstance.mainTexture = tex;
                _last = tex;
            }
        }
    }
}
