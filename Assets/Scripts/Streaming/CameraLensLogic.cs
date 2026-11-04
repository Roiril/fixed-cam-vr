#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// カメラごとのレンズ（LEDGER 0308）。C の映像だけ等立体角の魚眼へ写し直す。
    /// 座標はソースの幅を 1 とした中心原点。式はシェーダの <c>_CrtFisheye</c> の枝と同じ。
    /// </summary>
    public static class CameraLensLogic
    {
        public const string FisheyeCameraId = "C";

        /// <summary>C の無人画像のピンホール焦点距離（幅 640 で 288.33px。tools/twin/rig.json）。</summary>
        public const float SourceFocal = 288.33f / 640f;

        /// <summary>版 3。ソースの左右の端が、画面の左右の端の中央へちょうど来る魚眼の焦点距離。</summary>
        public static readonly float ScreenFocal = .5f / (2f * Mathf.Sin(Mathf.Atan(.5f / SourceFocal) * .5f));

        /// <summary>中央の拡大率。</summary>
        public static float CenterScale => ScreenFocal / SourceFocal;

        public static readonly int PropertyId = Shader.PropertyToID("_CrtFisheye");

        /// <summary>人形視点は C の映像ではないので掛けない。</summary>
        public static bool UsesFisheye(string? cameraId, bool dollPovShowing)
            => cameraId == FisheyeCameraId && !dollPovShowing;

        public static Vector4 Uniform(string? cameraId, bool dollPovShowing)
            => UsesFisheye(cameraId, dollPovShowing) ? new Vector4(SourceFocal, ScreenFocal, 0, 0) : Vector4.zero;

        public static void Apply(Material material, string? cameraId, bool dollPovShowing)
        {
            if (material.HasProperty(PropertyId)) material.SetVector(PropertyId, Uniform(cameraId, dollPovShowing));
        }

        /// <summary>画面の点を、ソースのどの点で読むか。</summary>
        public static Vector2 SourceFromScreen(Vector2 screen)
        {
            float radius = screen.magnitude;
            if (radius < 1e-5f) return screen * (SourceFocal / ScreenFocal);
            // 90 度の手前で止める。tan が裏返ると映像の反対側を読む。
            float half = Mathf.Min(radius / (2f * ScreenFocal), .7f);
            float theta = 2f * Mathf.Asin(half);
            return screen * (SourceFocal * Mathf.Tan(theta) / radius);
        }

        /// <summary>ソースの点が、画面のどこに映るか。</summary>
        public static Vector2 ScreenFromSource(Vector2 source)
        {
            float radius = source.magnitude;
            if (radius < 1e-5f) return source * (ScreenFocal / SourceFocal);
            float theta = Mathf.Atan(radius / SourceFocal);
            return source * (2f * ScreenFocal * Mathf.Sin(theta * .5f) / radius);
        }
    }
}
