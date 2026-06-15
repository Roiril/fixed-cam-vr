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
        [Tooltip("FullScreenPassRendererFeature が参照する CRT 用 Material (FxCrtMaterial)")]
        [SerializeField] private Material? targetMaterial;
        [Tooltip("スキャンライン暗化の強度 (0=無効, 1=黒帯)")]
        [Range(0, 1)] [SerializeField] private float scanlineIntensity = 0.25f;
        [Tooltip("画面 Y 方向のスキャンライン本数")]
        [Range(60, 800)] [SerializeField] private float scanlineCount = 240f;
        [Tooltip("フィルムグレインの振幅")]
        [Range(0, 0.4f)] [SerializeField] private float grainIntensity = 0.08f;
        [Tooltip("ビネット (周辺減光) の強度")]
        [Range(0, 1)] [SerializeField] private float vignetteIntensity = 0.6f;

        private static readonly int IdScanInt = Shader.PropertyToID("_ScanlineIntensity");
        private static readonly int IdScanCount = Shader.PropertyToID("_ScanlineCount");
        private static readonly int IdGrain = Shader.PropertyToID("_GrainIntensity");
        private static readonly int IdVignette = Shader.PropertyToID("_VignetteIntensity");
        private static readonly int IdTime = Shader.PropertyToID("_Time01");

        private void OnEnable() => PushStaticParams();

        // インスペクタ変更時に即反映（Editor のみ）。
        private void OnValidate() => PushStaticParams();

        // スキャンライン強度等は静的。毎フレーム書くとマテリアルを無駄に dirty 化するので、
        // 変化時（OnEnable / OnValidate）だけ push し、Update では時間項だけ更新する。
        private void PushStaticParams()
        {
            if (targetMaterial == null) return;
            targetMaterial.SetFloat(IdScanInt, scanlineIntensity);
            targetMaterial.SetFloat(IdScanCount, scanlineCount);
            targetMaterial.SetFloat(IdGrain, grainIntensity);
            targetMaterial.SetFloat(IdVignette, vignetteIntensity);
        }

        private void Update()
        {
            if (targetMaterial == null) return;
            targetMaterial.SetFloat(IdTime, Time.time);
        }
    }
}
