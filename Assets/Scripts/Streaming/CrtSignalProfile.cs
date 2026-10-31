#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>0298の選択を本編へ適用する。通常劣化の進みと時計は各Directorが持つ。</summary>
    public static class CrtSignalProfile
    {
        public const int Width = 1280, Height = 1030;
        public const float Gain = .85f, LensWarp = .06f;
        public const float CenterEv = .12f, CenterRadius = .8f, CenterFalloff = 2, TintStrength = .025f, TintCoolness = .55f;
        public static readonly Vector3 Sigma = new Vector3(.85f, .65f, .65f);

        public static void ApplySelected(Material material)
        {
            Vector4 adjustment = CrtReferenceScreen.ImageAdjustment; adjustment.w = -.32f;
            material.SetVector("_CrtImageAdjustment", adjustment);
            material.SetVector("_CrtOpticalConvergence", CrtReferenceScreen.OpticalConvergence * 1.8f);
            material.SetFloat("_CrtDisplayContrast", 1.16f);
            material.SetFloat("_Glare", 0);
            material.SetFloat("_CrtLensWarp", LensWarp);
            material.SetVector("_CrtPsfSigma", Sigma);
            material.SetFloat("_CrtSignalGain", Gain);
            material.SetFloat("_CrtSignalBandwidth", 1);
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
