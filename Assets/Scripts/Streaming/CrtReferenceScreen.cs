#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>参照画像から抽出した筐体。著作とCGは16:9の論理座標を保持する。</summary>
    public static class CrtReferenceScreen
    {
        public const float LogicalAspect = 16f / 9f;
        public const float CaseAspect = 1280f / 1030f;
        // 透過素材の開口を実測。PNGは上原点、Unity UVは下原点。
        public static readonly Rect Aperture = new Rect(101f / 1398f, 87f / 1125f, 1224f / 1398f, 962f / 1125f);
        public static readonly Vector2 LogicalCrop = new Vector2(0.75f, 1f);
        public static readonly Vector4 ImageAdjustment = new Vector4(1.90f, -0.10f, 0.35f, -0.52f);
        // Reference JPEGの白い襖と暗い縦枠の60走査線から推定したRGBのedge spread。
        // 被写体の枠幅も含む推定値であり、レンズPSFそのものの実測ではない。
        public static readonly Vector4 OpticalSpread = new Vector4(1.4f, 1.1f, 1.1f, 0);
        public static readonly Vector4 OpticalConvergence = new Vector4(0.97588f, 1.78615f, 0, 0);
        // 元参照の室内照明を別室へ焼き込まない。fieldは診断の除去比較だけで使う。
        public const float ImageFieldStrength = 0;

        public static bool Apply(Material material)
        {
            Texture2D? frame = Resources.Load<Texture2D>("CrtReference/Frame");
            Texture2D? caseColor = Resources.Load<Texture2D>("CrtReference/CaseColor");
            Texture2D? reflectance = Resources.Load<Texture2D>("CrtReference/Reflectance");
            Texture2D? glass = Resources.Load<Texture2D>("CrtReference/Glass");
            Texture2D? normal = Resources.Load<Texture2D>("CrtReference/Normal");
            Texture2D? grade = Resources.Load<Texture2D>("CrtReference/Grade");
            if (frame == null || caseColor == null || reflectance == null || glass == null || normal == null || grade == null
                || !material.HasProperty("_CrtReference") || !material.shader.isSupported)
            {
                Debug.LogError("[CrtReference] 筐体または反射マップが無い。参照スクリーンを構成できません。");
                return false;
            }
            material.SetTexture("_CrtFrameTex", frame);
            material.SetTexture("_CrtCaseColorTex", caseColor);
            material.SetTexture("_CrtReflectanceTex", reflectance);
            material.SetTexture("_CrtGlassTex", glass);
            material.SetTexture("_CrtNormalTex", normal);
            material.SetTexture("_CrtGradeTex", grade);
            material.SetFloat("_CrtReference", 1f);
            material.SetVector("_CrtAperture", new Vector4(Aperture.x, Aperture.y, Aperture.width, Aperture.height));
            material.SetVector("_CrtLogicalCrop", new Vector4(LogicalCrop.x, LogicalCrop.y, 0, 0));
            material.SetVector("_CrtImageAdjustment", ImageAdjustment);
            material.SetVector("_CrtOpticalSpread", OpticalSpread);
            material.SetVector("_CrtOpticalConvergence", OpticalConvergence);
            material.SetFloat("_CrtFieldStrength", ImageFieldStrength);
            material.SetFloat("_CrtDisplayContrast", 1.16f);
            material.SetFloat("_CrtGradeBypass", 0);
            material.SetVector("_CrtView", new Vector4(0, 0, -1, 0));
            return true;
        }

        public static Vector2 ToLogicalUv(Vector2 surfaceUv)
        {
            Vector2 apertureUv = new Vector2((surfaceUv.x - Aperture.x) / Aperture.width,
                                             (surfaceUv.y - Aperture.y) / Aperture.height);
            return Vector2.Scale(apertureUv - Vector2.one * 0.5f, LogicalCrop) + Vector2.one * 0.5f;
        }

        public static Vector2 VisibleScale(Vector2 logicalContain)
            => new Vector2(Aperture.width * Mathf.Min(1, logicalContain.x / LogicalCrop.x),
                           Aperture.height * Mathf.Min(1, logicalContain.y / LogicalCrop.y));

        public static Vector3 ApertureCenter(Transform surface)
            => surface.TransformPoint(new Vector3(Aperture.center.x - 0.5f, Aperture.center.y - 0.5f, 0));
    }
}
