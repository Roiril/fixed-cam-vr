#nullable enable
using UnityEngine;

namespace FixedCamVr.Fx.RendererFeature
{
    /// <summary>
    /// CRT ポストエフェクト用 Material のパラメータをランタイム調整するコントローラ。
    /// URP の FullScreenPassRendererFeature が参照する Material を直接編集することで
    /// インスペクタや動的演出からスキャンライン強度などを調整可能にする。
    ///
    /// 注意: このマテリアルは新規 Renderer Asset (FxRenderer.asset) のみが参照する。
    /// 既存 Main の URP Renderer は触らない。
    /// </summary>
    public sealed class FxCrtPostFxController : MonoBehaviour
    {
        [SerializeField] private Material? targetMaterial;
        [Range(0, 1)] [SerializeField] private float scanlineIntensity = 0.25f;
        [Range(60, 800)] [SerializeField] private float scanlineCount = 240f;
        [Range(0, 0.4f)] [SerializeField] private float grainIntensity = 0.08f;
        [Range(0, 1)] [SerializeField] private float vignetteIntensity = 0.6f;

        private static readonly int IdScanInt = Shader.PropertyToID("_ScanlineIntensity");
        private static readonly int IdScanCount = Shader.PropertyToID("_ScanlineCount");
        private static readonly int IdGrain = Shader.PropertyToID("_GrainIntensity");
        private static readonly int IdVignette = Shader.PropertyToID("_VignetteIntensity");
        private static readonly int IdTime = Shader.PropertyToID("_Time01");

        private void Update()
        {
            if (targetMaterial == null) return;
            targetMaterial.SetFloat(IdScanInt, scanlineIntensity);
            targetMaterial.SetFloat(IdScanCount, scanlineCount);
            targetMaterial.SetFloat(IdGrain, grainIntensity);
            targetMaterial.SetFloat(IdVignette, vignetteIntensity);
            targetMaterial.SetFloat(IdTime, Time.time);
        }
    }
}
