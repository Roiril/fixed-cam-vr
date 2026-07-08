#nullable enable
using System.Collections.Generic;
using System.IO;
using TableDuoVr.Net;
using UnityEditor;
using UnityEngine;

namespace TableDuoVr.EditorTools
{
    /// <summary>
    /// 開いている TableDuoMain の卓上（海底探検の盤面）を複数角度でスクショする診断ツール。
    /// Play 不要・現シーンをそのまま撮る。配置・スケール調整の視覚検証用
    /// （visual-verification ルール: 判断は 1 角度でしない → 斜め/両席/真上を一括出力）。
    /// 出力: Temp/TablePreview/*.png
    ///
    /// Remy 着座つき（2026-07-08）: 両席へ座位 Remy を一時生成して寸法感（体 vs 机の高さ・
    /// リーチ）を確認できる。ランタイム生成物ではなくプレビュー内だけ（撮影後に破棄）。
    /// </summary>
    public static class TableDuoTablePreview
    {
        private const int Size = 1100;

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Table (screenshot)", priority = 214)]
        public static void Capture() => CaptureInternal(withRemy: false);

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Table + Remy seated", priority = 215)]
        public static void CaptureWithRemy() => CaptureInternal(withRemy: true);

        private static void CaptureInternal(bool withRemy)
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
            var remyInstances = new List<GameObject>();
            try
            {
                if (withRemy) SeatRemy(remyInstances);

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

                if (withRemy)
                {
                    // 寸法感用: 座位 Remy 全身と机を一緒に真横〜斜めから（体高 vs 天板高・肘/手の届き）
                    var bodyAim = new Vector3(center.x, topY + 0.15f, center.z);
                    Shot(cam, dir, "remy_side.png", bodyAim + new Vector3(2.4f, 0.15f, 0f), bodyAim);
                    Shot(cam, dir, "remy_front_full.png", bodyAim + new Vector3(0f, 0.10f, -2.6f), bodyAim);
                    Shot(cam, dir, "remy_diag.png", bodyAim + new Vector3(1.8f, 0.55f, -1.8f), bodyAim);
                }

                Debug.Log($"[TablePreview] {(withRemy ? "Remy 着座つき 7" : "4")} 枚保存 → {dir}（天板 y={topY:F3}）");
            }
            finally
            {
                foreach (var go in remyInstances) if (go != null) Object.DestroyImmediate(go);
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(lightGo);
            }
        }

        /// <summary>両席に座位 Remy を一時生成する（撮影後に呼び出し側が破棄）。</summary>
        private static void SeatRemy(List<GameObject> outInstances)
        {
            var prefab = Resources.Load<GameObject>("RemyFullAvatar");
            if (prefab == null)
            {
                Debug.LogWarning("[TablePreview] RemyFullAvatar prefab が無いため Remy 着座をスキップ");
                return;
            }
            foreach (var name in new[] { "[TableDuo]/Seats/Seat0", "[TableDuo]/Seats/Seat1" })
            {
                var seat = GameObject.Find(name);
                if (seat == null) continue;
                int before = seat.transform.childCount;
                // コンストラクタが座位ポーズ + 休めポーズまで組む。firstPerson:false で頭も残す（全身確認）
                _ = new RemyAvatarRig(seat.transform, prefab, firstPerson: false);
                for (int i = before; i < seat.transform.childCount; i++)
                {
                    outInstances.Add(seat.transform.GetChild(i).gameObject);
                }
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
