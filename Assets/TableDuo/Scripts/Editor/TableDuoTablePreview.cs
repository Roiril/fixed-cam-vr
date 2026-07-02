#nullable enable
using System.IO;
using UnityEditor;
using UnityEngine;

namespace TableDuoVr.EditorTools
{
    /// <summary>
    /// 開いている TableDuoMain の卓上（海底探検の盤面）を複数角度でスクショする診断ツール。
    /// Play 不要・現シーンをそのまま撮る。配置・スケール調整の視覚検証用
    /// （visual-verification ルール: 判断は 1 角度でしない → 斜め/両席/真上を一括出力）。
    /// 出力: Temp/TablePreview/*.png
    /// </summary>
    public static class TableDuoTablePreview
    {
        private const int Size = 1100;

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Table (screenshot)", priority = 214)]
        public static void Capture()
        {
            var table = GameObject.Find("[TableDuo]/Table") ?? GameObject.Find("Table");
            Vector3 center;
            float topY;
            if (table != null)
            {
                var b = CalcBounds(table);
                center = new Vector3(b.center.x, b.max.y, b.center.z);
                topY = b.max.y;
            }
            else
            {
                center = new Vector3(0f, 0.75f, 0f);
                topY = 0.75f;
                Debug.LogWarning("[TablePreview] Table が見つからないため既定位置で撮影");
            }

            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "../Temp/TablePreview"));
            Directory.CreateDirectory(dir);

            var camGo = new GameObject("TablePreviewCam");
            var lightGo = new GameObject("TablePreviewLight");
            try
            {
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.16f, 0.17f, 0.20f);
                cam.fieldOfView = 50f;
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.1f;
                lightGo.transform.rotation = Quaternion.Euler(55f, -25f, 0f);

                // 斜め上（人役側/手役側）・低め斜め・真上
                Shot(cam, dir, "diag_fullside.png", center + new Vector3(0.55f, 0.75f, -0.85f), center);
                Shot(cam, dir, "diag_handside.png", center + new Vector3(-0.55f, 0.75f, 0.85f), center);
                Shot(cam, dir, "low_fullside.png", center + new Vector3(0.0f, 0.35f, -0.75f), center + new Vector3(0f, 0.02f, 0f));
                Shot(cam, dir, "top.png", center + new Vector3(0f, 1.1f, 0.001f), center);

                Debug.Log($"[TablePreview] 4 枚保存 → {dir}（天板 y={topY:F3}）");
            }
            finally
            {
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(lightGo);
            }
        }

        private static Bounds CalcBounds(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>();
            var b = rs.Length > 0 ? rs[0].bounds : new Bounds(go.transform.position, Vector3.one);
            foreach (var r in rs) b.Encapsulate(r.bounds);
            return b;
        }

        private static void Shot(Camera cam, string dir, string file, Vector3 camPos, Vector3 aim)
        {
            cam.transform.position = camPos;
            cam.transform.rotation = Quaternion.LookRotation(aim - camPos, Vector3.up);

            var rt = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            cam.targetTexture = rt;
            cam.Render();

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;

            File.WriteAllBytes(Path.Combine(dir, file), tex.EncodeToPNG());

            cam.targetTexture = null;
            Object.DestroyImmediate(tex);
            rt.Release();
            Object.DestroyImmediate(rt);
        }
    }
}
