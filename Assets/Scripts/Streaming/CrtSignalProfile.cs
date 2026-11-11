#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>Web採用23値の正本。動的mono/暗部ノイズと演出の時計はDirectorが持つ。</summary>
    public static class CrtSignalProfile
    {
        public const int Width = 1280, Height = 1030;
        public const float Gain = .85f, LensWarp = .155f, Mono = .5f, Bandwidth = 1f;
        public const float CenterEv = 3.63f, CenterRadius = 1.68f, CenterFalloff = 3, TintStrength = 1, TintCoolness = 1;
        public const float Exposure = -1.75f, Contrast = 1.12f, Saturation = .83f, Temperature = .38f, Tint = 0, Lift = 0;
        public const float Vignette = 1, Grain = .02f, Scanline = .68f, ConvergenceMultiplier = 10;
        public const float NoiseDark = .05f, NoiseFixed = .098f, EndNoiseDark = .2f;
        public static readonly Vector3 Sigma = new Vector3(2.25f, .65f, .65f);
        public const float DollMono = .56f, DollNoiseDark = .34f, DollNoiseFixed = .241f;
        public const float DollGrain = 0, DollScanline = 1.47f;
        public static readonly Vector3 DollSigma = new Vector3(5.26f, 3.28f, 3.28f);

        private static readonly int DollBlendId = Shader.PropertyToID("_CrtDollProfileBlend"),
            ImageAdjustmentId = Shader.PropertyToID("_CrtImageAdjustment"), OpticalConvergenceId = Shader.PropertyToID("_CrtOpticalConvergence"),
            DisplayContrastId = Shader.PropertyToID("_CrtDisplayContrast"), GlareId = Shader.PropertyToID("_Glare"),
            ExposureId = Shader.PropertyToID("_Exposure"), ContrastId = Shader.PropertyToID("_Contrast"),
            SaturationId = Shader.PropertyToID("_Saturation"), TemperatureId = Shader.PropertyToID("_Temperature"),
            TintId = Shader.PropertyToID("_Tint"), LiftId = Shader.PropertyToID("_Lift"), VignetteId = Shader.PropertyToID("_Vignette"),
            GrainId = Shader.PropertyToID("_Grain"), ScanlineId = Shader.PropertyToID("_Scanline"), NoiseFixedId = Shader.PropertyToID("_NoiseFixed"),
            LensWarpId = Shader.PropertyToID("_CrtLensWarp"), PsfSigmaId = Shader.PropertyToID("_CrtPsfSigma"),
            SignalGainId = Shader.PropertyToID("_CrtSignalGain"), SignalBandwidthId = Shader.PropertyToID("_CrtSignalBandwidth"),
            SignalRuntimeId = Shader.PropertyToID("_CrtSignalRuntime"), ProfileSelectedId = Shader.PropertyToID("_CrtSignalProfileSelected"),
            CenterEvId = Shader.PropertyToID("_CrtCenterEv"), CenterRadiusId = Shader.PropertyToID("_CrtCenterRadius"), CenterFalloffId = Shader.PropertyToID("_CrtCenterFalloff"),
            TintStrengthId = Shader.PropertyToID("_CrtTintStrength"), TintCoolnessId = Shader.PropertyToID("_CrtTintCoolness"),
            ScreenMaskTexId = Shader.PropertyToID("_CrtScreenMaskTex"), ScreenOnlyId = Shader.PropertyToID("_CrtScreenOnly");
        private static Texture2D? s_screenMask;

        /// <summary>採用値で動く材質だと印を付ける。値そのものは <see cref="CrtSignalProcessor.PrepareDisplay"/> が描く直前に書く。</summary>
        public static void EnableRuntime(Material material)
        {
            material.SetFloat(SignalRuntimeId, 1);
            material.SetFloat(ProfileSelectedId, 1);
        }

        public static void ApplySelected(Material material)
        {
            float blend = Mathf.Clamp01(material.GetFloat(DollBlendId));
            Vector4 adjustment = CrtReferenceScreen.ImageAdjustment; adjustment.w = -.32f;
            material.SetVector(ImageAdjustmentId, adjustment);
            material.SetVector(OpticalConvergenceId, CrtReferenceScreen.OpticalConvergence * ConvergenceMultiplier);
            material.SetFloat(DisplayContrastId, 1.16f);
            material.SetFloat(GlareId, 0);
            material.SetFloat(ExposureId, Exposure); material.SetFloat(ContrastId, Contrast);
            material.SetFloat(SaturationId, Saturation); material.SetFloat(TemperatureId, Temperature);
            material.SetFloat(TintId, Tint); material.SetFloat(LiftId, Lift);
            material.SetFloat(VignetteId, Vignette); material.SetFloat(GrainId, Mathf.Lerp(Grain, DollGrain, blend)); material.SetFloat(ScanlineId, Mathf.Lerp(Scanline, DollScanline, blend));
            material.SetFloat(NoiseFixedId, Mathf.Lerp(NoiseFixed, DollNoiseFixed, blend));
            material.SetFloat(LensWarpId, LensWarp);
            material.SetVector(PsfSigmaId, Vector3.Lerp(Sigma, DollSigma, blend));
            material.SetFloat(SignalGainId, Gain);
            material.SetFloat(SignalBandwidthId, Bandwidth);
            EnableRuntime(material);
            material.SetFloat(CenterEvId, CenterEv); material.SetFloat(CenterRadiusId, CenterRadius); material.SetFloat(CenterFalloffId, CenterFalloff);
            material.SetFloat(TintStrengthId, TintStrength); material.SetFloat(TintCoolnessId, TintCoolness);
            if (s_screenMask == null)
                s_screenMask = Resources.Load<Texture2D>("CrtReference/ScreenMask")
                    ?? throw new System.InvalidOperationException("Selected CRT screen contour is missing");
            material.SetTexture(ScreenMaskTexId, s_screenMask);
            material.SetFloat(ScreenOnlyId, 1);
        }
    }
}
