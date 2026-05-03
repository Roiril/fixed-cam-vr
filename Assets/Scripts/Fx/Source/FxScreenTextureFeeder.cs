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
        [SerializeField] private FxSourceBinder? binder;
        [SerializeField] private Renderer? targetRenderer;

        private Texture? _last;

        private void Awake()
        {
            if (targetRenderer == null) targetRenderer = GetComponent<Renderer>();
        }

        private void Update()
        {
            if (binder == null || targetRenderer == null) return;
            var tex = binder.Current;
            if (!ReferenceEquals(tex, _last))
            {
                targetRenderer.material.mainTexture = tex;
                _last = tex;
            }
        }
    }
}
