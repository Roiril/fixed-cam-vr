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

        public static void ApplySelected(Material material)
        {
            Vector4 adjustment = CrtReferenceScreen.ImageAdjustment; adjustment.w = -.32f;
            material.SetVector("_CrtImageAdjustment", adjustment);
            material.SetVector("_CrtOpticalConvergence", CrtReferenceScreen.OpticalConvergence * ConvergenceMultiplier);
            material.SetFloat("_CrtDisplayContrast", 1.16f);
            material.SetFloat("_Glare", 0);
            material.SetFloat("_Exposure", Exposure); material.SetFloat("_Contrast", Contrast);
            material.SetFloat("_Saturation", Saturation); material.SetFloat("_Temperature", Temperature);
            material.SetFloat("_Tint", Tint); material.SetFloat("_Lift", Lift);
            material.SetFloat("_Vignette", Vignette); material.SetFloat("_Grain", Grain); material.SetFloat("_Scanline", Scanline);
            material.SetFloat("_NoiseFixed", NoiseFixed);
            material.SetFloat("_CrtLensWarp", LensWarp);
            material.SetVector("_CrtPsfSigma", Sigma);
            material.SetFloat("_CrtSignalGain", Gain);
            material.SetFloat("_CrtSignalBandwidth", Bandwidth);
            material.SetFloat("_CrtSignalRuntime", 1);
            material.SetFloat("_CrtSignalProfileSelected", 1);
            material.SetFloat("_CrtCenterEv", CenterEv); material.SetFloat("_CrtCenterRadius", CenterRadius); material.SetFloat("_CrtCenterFalloff", CenterFalloff);
            material.SetFloat("_CrtTintStrength", TintStrength); material.SetFloat("_CrtTintCoolness", TintCoolness);
            material.SetTexture("_CrtScreenMaskTex", Resources.Load<Texture2D>("CrtReference/ScreenMask")
                ?? throw new System.InvalidOperationException("Selected CRT screen contour is missing"));
            material.SetFloat("_CrtScreenOnly", 1);
        }
    }
}
