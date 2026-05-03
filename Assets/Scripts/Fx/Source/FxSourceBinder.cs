#nullable enable
using UnityEngine;

namespace FixedCamVr.Fx.Source
{
    /// <summary>
    /// 映像入力を取り出す薄いブリッジ。MjpegScreen を改変せずに、
    /// 1) 既存 Renderer の sharedMaterial.mainTexture（Main 経由を読む場合）
    /// 2) 明示的に渡された Texture（FxSandbox で VideoPlayer / RenderTexture を割り当てる用途）
    /// のどちらかを Texture プロパティとして公開する。
    ///
    /// 既存 Streaming 系には書き込まない。読み取り専用。
    /// </summary>
    public sealed class FxSourceBinder : MonoBehaviour
    {
        public enum Mode { ExplicitTexture, RendererMainTexture }

        [SerializeField] private Mode mode = Mode.ExplicitTexture;
        [SerializeField] private Texture? explicitTexture;
        [SerializeField] private Renderer? sourceRenderer;

        public Texture? Current
        {
            get
            {
                switch (mode)
                {
                    case Mode.ExplicitTexture:
                        return explicitTexture;
                    case Mode.RendererMainTexture:
                        if (sourceRenderer == null) return null;
                        var mat = sourceRenderer.sharedMaterial;
                        return mat == null ? null : mat.mainTexture;
                    default:
                        return null;
                }
            }
        }

        public void SetExplicitTexture(Texture? tex)
        {
            mode = Mode.ExplicitTexture;
            explicitTexture = tex;
        }
    }
}
