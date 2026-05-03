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

        [Tooltip("Sobel / Tonemap カーネルを含む Compute Shader (FxSobel.compute)")]
        [SerializeField] private ComputeShader? compute;
        [Tooltip("Compute の入力 Texture を提供するブリッジ")]
        [SerializeField] private FxSourceBinder? binder;
        [Tooltip("加工後 RT を mainTexture として渡す描画先 Renderer")]
        [SerializeField] private Renderer? targetRenderer;
        [Tooltip("実行カーネル種別 (Sobel: 単純輪郭 / SobelOverlay: 元映像 + 赤輪郭 / Tonemap: Reinhard)")]
        [SerializeField] private Kernel kernel = Kernel.SobelOverlay;
        [Tooltip("Sobel エッジ抽出強度 (saturate 直前に乗算)")]
        [Range(0.1f, 8f)] [SerializeField] private float edgeIntensity = 2.0f;
        [Tooltip("Tonemap カーネル時の入力露出倍率")]
        [Range(0.1f, 4f)] [SerializeField] private float exposure = 1.0f;

        private RenderTexture? _rt;
        private Texture? _last;
        // targetRenderer.material は毎回複製を返すので 1 度だけ取得して保持する。
        private Material? _targetMatInstance;
        // FindKernel は文字列ハッシュなので毎フレ呼ばずキャッシュ。
        private Kernel _cachedKernelKind;
        private int _cachedKernelId = -1;

        private static readonly int IdSource = Shader.PropertyToID("_Source");
        private static readonly int IdResult = Shader.PropertyToID("_Result");
        private static readonly int IdSize = Shader.PropertyToID("_Size");
        private static readonly int IdEdge = Shader.PropertyToID("_EdgeIntensity");
        private static readonly int IdExposure = Shader.PropertyToID("_Exposure");

        private void OnDisable()
        {
            ReleaseRT();
            if (_targetMatInstance != null)
            {
                _targetMatInstance.mainTexture = null;
                Destroy(_targetMatInstance);
                _targetMatInstance = null;
            }
            _cachedKernelId = -1;
        }

        private void Update()
        {
            if (compute == null || binder == null || targetRenderer == null) return;
            var src = binder.Current;
            if (src == null) return;

            EnsureRT(src.width, src.height);
            if (_rt == null) return;

            // カーネル種別が変わった時だけ FindKernel を呼ぶ。
            if (_cachedKernelId < 0 || _cachedKernelKind != kernel)
            {
                _cachedKernelId = compute.FindKernel(KernelName(kernel));
                _cachedKernelKind = kernel;
            }
            int kid = _cachedKernelId;

            compute.SetTexture(kid, IdSource, src);
            compute.SetTexture(kid, IdResult, _rt);
            compute.SetInts(IdSize, src.width, src.height);
            compute.SetFloat(IdEdge, edgeIntensity);
            compute.SetFloat(IdExposure, exposure);

            // 8x8 numthreads に合わせてグループ数を切り上げ。Z 次元は 1 固定。
            int gx = Mathf.CeilToInt(src.width / 8f);
            int gy = Mathf.CeilToInt(src.height / 8f);
            compute.Dispatch(kid, gx, gy, 1);

            if (!ReferenceEquals(_rt, _last))
            {
                if (_targetMatInstance == null) _targetMatInstance = targetRenderer.material;
                _targetMatInstance.mainTexture = _rt;
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
