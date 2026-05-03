#nullable enable
using FixedCamVr.Fx.Source;
using UnityEngine;

namespace FixedCamVr.Fx.Blit
{
    /// <summary>
    /// FxSourceBinder の入力 Texture に色収差シェーダを適用し、
    /// 結果 RT を Renderer.material.mainTexture に流し込む MonoBehaviour 駆動 Blit。
    ///
    /// URP のパイプライン外（OnPreRender 相当のタイミング）でフレーム単位に Blit する設計。
    /// Phase 1 の Renderer Feature 方式との比較対象として用意している。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FxBlitChromaticAberration : MonoBehaviour
    {
        [SerializeField] private Shader? blitShader;
        [SerializeField] private FxSourceBinder? binder;
        [SerializeField] private Renderer? targetRenderer;
        [Range(0, 0.05f)] [SerializeField] private float strength = 0.012f;
        [Range(0, 4f)] [SerializeField] private float falloff = 1.5f;

        private Material? _mat;
        private RenderTexture? _rt;
        private Texture? _lastSource;

        private static readonly int IdStrength = Shader.PropertyToID("_Strength");
        private static readonly int IdFalloff = Shader.PropertyToID("_Falloff");

        private void OnEnable()
        {
            if (blitShader != null)
            {
                _mat = new Material(blitShader) { hideFlags = HideFlags.HideAndDontSave };
            }
        }

        private void OnDisable()
        {
            if (_mat != null) { Destroy(_mat); _mat = null; }
            ReleaseRT();
            if (targetRenderer != null) targetRenderer.material.mainTexture = null;
        }

        private void Update()
        {
            if (_mat == null || binder == null || targetRenderer == null) return;
            var src = binder.Current;
            if (src == null) return;

            EnsureRT(src.width, src.height);
            if (_rt == null) return;

            _mat.SetFloat(IdStrength, strength);
            _mat.SetFloat(IdFalloff, falloff);
            Graphics.Blit(src, _rt, _mat);

            if (!ReferenceEquals(_rt, _lastSource))
            {
                targetRenderer.material.mainTexture = _rt;
                _lastSource = _rt;
            }
        }

        private void EnsureRT(int w, int h)
        {
            if (_rt != null && _rt.width == w && _rt.height == h) return;
            ReleaseRT();
            _rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
            {
                name = "Fx_ChromaticAb_RT",
                filterMode = FilterMode.Bilinear,
            };
            _rt.Create();
            _lastSource = null;
        }

        private void ReleaseRT()
        {
            if (_rt != null) { _rt.Release(); Destroy(_rt); _rt = null; }
        }
    }
}
