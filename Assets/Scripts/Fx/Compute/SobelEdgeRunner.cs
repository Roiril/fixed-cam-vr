#nullable enable
using FixedCamVr.Fx.Source;
using UnityEngine;

namespace FixedCamVr.Fx.Compute
{
    /// <summary>
    /// FxSourceBinder の入力を Compute Shader で畳み込み処理し、結果を Renderer に流し込む。
    /// 3 種類のカーネルを切替可能:
    ///   - Sobel: グレースケール輪郭抽出
    ///   - SobelOverlay: 元映像 + 赤輪郭強調（バイオハザード固定カメラ向き）
    ///   - Tonemap: Reinhard
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SobelEdgeRunner : MonoBehaviour
    {
        public enum Kernel { Sobel, SobelOverlay, Tonemap }

        [SerializeField] private ComputeShader? compute;
        [SerializeField] private FxSourceBinder? binder;
        [SerializeField] private Renderer? targetRenderer;
        [SerializeField] private Kernel kernel = Kernel.SobelOverlay;
        [Range(0.1f, 8f)] [SerializeField] private float edgeIntensity = 2.0f;
        [Range(0.1f, 4f)] [SerializeField] private float exposure = 1.0f;

        private RenderTexture? _rt;
        private Texture? _last;

        private static readonly int IdSource = Shader.PropertyToID("_Source");
        private static readonly int IdResult = Shader.PropertyToID("_Result");
        private static readonly int IdSize = Shader.PropertyToID("_Size");
        private static readonly int IdEdge = Shader.PropertyToID("_EdgeIntensity");
        private static readonly int IdExposure = Shader.PropertyToID("_Exposure");

        private void OnDisable()
        {
            ReleaseRT();
            if (targetRenderer != null) targetRenderer.material.mainTexture = null;
        }

        private void Update()
        {
            if (compute == null || binder == null || targetRenderer == null) return;
            var src = binder.Current;
            if (src == null) return;

            EnsureRT(src.width, src.height);
            if (_rt == null) return;

            int kid = compute.FindKernel(KernelName(kernel));
            compute.SetTexture(kid, IdSource, src);
            compute.SetTexture(kid, IdResult, _rt);
            compute.SetInts(IdSize, src.width, src.height);
            compute.SetFloat(IdEdge, edgeIntensity);
            compute.SetFloat(IdExposure, exposure);

            int gx = Mathf.CeilToInt(src.width / 8f);
            int gy = Mathf.CeilToInt(src.height / 8f);
            compute.Dispatch(kid, gx, gy, 1);

            if (!ReferenceEquals(_rt, _last))
            {
                targetRenderer.material.mainTexture = _rt;
                _last = _rt;
            }
        }

        private static string KernelName(Kernel k) => k switch
        {
            Kernel.Sobel => "SobelEdge",
            Kernel.SobelOverlay => "SobelEdgeOverlay",
            Kernel.Tonemap => "ReinhardTonemap",
            _ => "SobelEdge",
        };

        private void EnsureRT(int w, int h)
        {
            if (_rt != null && _rt.width == w && _rt.height == h) return;
            ReleaseRT();
            _rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
            {
                name = "Fx_Sobel_RT",
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
            };
            _rt.Create();
            _last = null;
        }

        private void ReleaseRT()
        {
            if (_rt != null) { _rt.Release(); Destroy(_rt); _rt = null; }
        }
    }
}
