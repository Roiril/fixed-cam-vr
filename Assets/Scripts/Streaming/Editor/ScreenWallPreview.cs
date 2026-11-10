#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// <b>増えるスクリーンと黒い霧を 1 コマずつ焼く</b>（3-C・<c>canon/LEDGER.md</c> 0317）。
    ///
    /// 実シェーダ（<c>Resources/ScreenMultiply</c>・<c>ScreenMultiplyGlow</c>）と実ロジック
    /// （<see cref="AnomalyEyesLogic"/>・<see cref="ScreenMultiply"/> の点いた時刻の表）を通す。
    /// 版は 2 つ撮る — <c>fog</c>（本番の見え）と <c>flat</c>（霧なし・その場で点く・奥の段なし ＝ 前の版に近い形）。
    ///
    /// ⚠ 実機の見え方そのものではない。両眼視差・レンズは入っていない。メインスクリーンは無人写真を貼った板で、
    /// 面に映る画もその写真（本番はメインスクリーンの取り込み）。奥行きの効き方と霧の濃さを見るための絵。
    ///
    /// 使い方: <c>.\tools\unity.ps1 menu screen-wall</c>
    /// </summary>
    public static class ScreenWallPreview
    {
        private const string OutDirRel = "Screenshots/screen-wall";
        private const int StillW = 1600, StillH = 900;
        private const int SeqW = 960, SeqH = 540;
        private const int Fps = 30;
        private const float FovDeg = 70f;
        private static readonly Vector3 Stage = new(0f, 2000f, 0f);

        // 置き換えのメインスクリーン（本編の実測: 水平 ±30.6° / 垂直 ±18.4° / 8° 下 / 2m）。
        private const float ScreenDistM = 2.0f;
        private const float ScreenHalfYawDeg = 30.6f;
        private const float ScreenHalfPitchDeg = 18.4f;
        private const float ScreenDropDeg = 8f;

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Screen Wall And Fog", priority = 234)]
        public static void Run()
        {
            string dir = Path.Combine(Application.dataPath, OutDirRel);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);

            Texture2D? plate = LoadPlate(out string plateName);
            if (plate == null)
            {
                Debug.LogError("[ScreenWallPreview] 無人写真（tools/web-compositor/captures/plate_*.jpg）が見つかりません");
                return;
            }
            bool sequence = (EditorCliArgs.Get("frames") ?? "1") != "0";

            var camGo = new GameObject("[ScreenWallPreview] Camera");
            var wallGo = new GameObject("[ScreenWallPreview] Wall");
            var screenGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Material? screenMat = null;
            Texture2DArray? feed = null;
            try
            {
                var cam = camGo.AddComponent<Camera>();
                cam.fieldOfView = FovDeg;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 100f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.allowHDR = false;
                cam.transform.SetPositionAndRotation(Stage, Quaternion.identity);

                Shader? plain = Shader.Find("Universal Render Pipeline/Unlit");
                if (plain == null)
                {
                    Debug.LogError("[ScreenWallPreview] URP の Unlit が見つかりません");
                    return;
                }
                screenMat = new Material(plain) { name = "Main screen (preview)" };
                screenMat.SetTexture("_BaseMap", plate);
                screenMat.SetColor("_BaseColor", new Color(0.62f, 0.74f, 0.86f, 1f));
                screenGo.name = "[ScreenWallPreview] Main screen (plate)";
                screenGo.GetComponent<Renderer>().sharedMaterial = screenMat;
                float hw = ScreenDistM * Mathf.Tan(ScreenHalfYawDeg * Mathf.Deg2Rad);
                float hh = ScreenDistM * Mathf.Tan(ScreenHalfPitchDeg * Mathf.Deg2Rad);
                float drop = ScreenDistM * Mathf.Tan(ScreenDropDeg * Mathf.Deg2Rad);
                screenGo.transform.position = Stage + new Vector3(0f, -drop, ScreenDistM);
                screenGo.transform.localScale = new Vector3(hw * 2f, hh * 2f, 1f);

                wallGo.transform.SetPositionAndRotation(Stage, Quaternion.identity);
                var wall = wallGo.AddComponent<ScreenMultiply>();
                wall.Build();
                if (!wall.IsBuilt)
                {
                    Debug.LogError("[ScreenWallPreview] 増えるスクリーンを組めません（シェーダか取り込みの器）");
                    return;
                }
                feed = BuildFeed(plate);
                wall.SetFeedOverride(feed);

                float open0 = AnomalyEyesLogic.HintSec + AnomalyEyesLogic.StareSec;
                float full = open0 + AnomalyEyesLogic.SwarmSec;
                var report = new StringBuilder();
                report.AppendLine("variant\tshot\tsec\topen\tarriving\tnearLuma\tfarLuma\tfarOverNear");

                foreach (string variant in new[] { "fog", "flat" })
                {
                    string vdir = Path.Combine(dir, variant);
                    Directory.CreateDirectory(vdir);
                    bool fog = variant == "fog";
                    wall.SetLook(fog ? ScreenFogLogic.Density : 0f, fog ? ScreenMultiply.EmergeM : 0f, showFar: fog);

                    (string name, float sec, float yaw)[] shots =
                    {
                        ("1_first", open0 - 0.15f, 0f),
                        ("2_ripple", open0 + AnomalyEyesLogic.SwarmSec * 0.14f, 0f),
                        ("3_pause", open0 + AnomalyEyesLogic.SwarmSec * 0.55f, 0f),
                        ("4_rush", open0 + AnomalyEyesLogic.SwarmSec * 0.88f, 0f),
                        ("5_just_full", full + 0.25f, 0f),
                        ("6_settled", full + 3.0f, 0f),
                        ("7_settled_left", full + 3.0f, -55f),
                        ("8_settled_right", full + 3.0f, 55f),
                        ("9_settled_up", full + 3.0f, 0f),
                    };
                    foreach (var shot in shots)
                    {
                        DriveTo(wall, shot.sec);
                        float pitch = shot.name == "9_settled_up" ? -32f : 0f;
                        cam.transform.rotation = Quaternion.Euler(pitch, shot.yaw, 0f);
                        Texture2D img = Shoot(cam, StillW, StillH);
                        File.WriteAllBytes(Path.Combine(vdir, shot.name + ".png"), img.EncodeToPNG());
                        (float near, float far) = MeasureTiers(cam, img);
                        UnityEngine.Object.DestroyImmediate(img);
                        report.Append(variant).Append('\t').Append(shot.name).Append('\t').Append(F(shot.sec)).Append('\t')
                              .Append(wall.OpenCount).Append('\t').Append(wall.ArrivingCount).Append('\t')
                              .Append(F(near)).Append('\t').Append(F(far)).Append('\t')
                              .Append(near > 1e-5f ? F(far / near) : "-").Append('\n');
                    }
                    cam.transform.rotation = Quaternion.identity;

                    if (!sequence) continue;
                    string sdir = Path.Combine(vdir, "seq");
                    Directory.CreateDirectory(sdir);
                    var logic = new AnomalyEyesLogic();
                    float dt = 1f / Fps, clock = 0f;
                    bool begun = false;
                    int n = 0;
                    for (float t = 0f; t < full + 3.0f; t += dt)
                    {
                        Step(wall, logic, dt, ref clock, ref begun);
                        if (t < AnomalyEyesLogic.HintSec * 0.6f) continue;
                        Texture2D img = Shoot(cam, SeqW, SeqH);
                        File.WriteAllBytes(Path.Combine(sdir, $"f{n:0000}.jpg"), img.EncodeToJPG(92));
                        UnityEngine.Object.DestroyImmediate(img);
                        n++;
                    }
                }

                File.WriteAllText(Path.Combine(dir, "report.tsv"), report.ToString());
                int farSeats = ScreenMultiplyLayout.Seats.Count(s => ScreenMultiplyLayout.IsFar(s));
                Debug.Log($"[ScreenWallPreview] fog / flat の静止画 各 9 枚{(sequence ? " + 連番" : "")} + report.tsv → Assets/{OutDirRel}/\n"
                        + $"  席 {ScreenMultiplyLayout.Seats.Length}（霧の奥の段 {farSeats}）/ 写真 {plateName}\n"
                        + $"  霧 {ScreenFogLogic.Density}/m・{ScreenFogLogic.ClearM}m まで無し / 奥から {ScreenMultiply.EmergeM}m・{ScreenMultiply.ArriveSec}s\n"
                        + "  ⚠ 両眼視差・レンズは入っていない。メインスクリーンは写真を貼った板");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(camGo);
                UnityEngine.Object.DestroyImmediate(wallGo);
                UnityEngine.Object.DestroyImmediate(screenGo);
                if (screenMat != null) UnityEngine.Object.DestroyImmediate(screenMat);
                if (feed != null) UnityEngine.Object.DestroyImmediate(feed);
                UnityEngine.Object.DestroyImmediate(plate);
            }
        }

        /// <summary>頭から <paramref name="sec"/> 秒まで、本番と同じ刻みで回す（点いた時刻の表も進む）。</summary>
        private static void DriveTo(ScreenMultiply wall, float sec)
        {
            var logic = new AnomalyEyesLogic();
            float dt = 1f / Fps, clock = 0f;
            bool begun = false;
            for (float t = 0f; t < sec; t += dt) Step(wall, logic, dt, ref clock, ref begun);
        }

        private static void Step(ScreenMultiply wall, AnomalyEyesLogic logic, float dt, ref float clock, ref bool begun)
        {
            logic.Tick(dt, wanted: true, density: 1f);
            clock += dt;
            if (!begun) { wall.BeginRun(); begun = true; }
            wall.Drive(dt, logic.Big, logic.Field, logic.Span, logic.Closing, clock);
        }

        /// <summary>
        /// 画の上で、手前の段と霧の奥の段の明るさ（linear の輝度の平均）を測る。席の中心の周り 7×7 画素。
        /// メインスクリーンの後ろと画の外の席は数えない。
        /// </summary>
        private static (float near, float far) MeasureTiers(Camera cam, Texture2D img)
        {
            double nearSum = 0, farSum = 0;
            int nearN = 0, farN = 0;
            for (int i = 0; i < ScreenMultiplyLayout.Seats.Length; i++)
            {
                ScreenSeat seat = ScreenMultiplyLayout.Seats[i];
                Vector3 world = Stage + seat.Center;
                Vector3 vp = cam.WorldToViewportPoint(world);
                if (vp.z <= 0f || vp.x < 0.03f || vp.x > 0.97f || vp.y < 0.03f || vp.y > 0.97f) continue;
                Vector3 local = cam.transform.InverseTransformPoint(world);
                float az = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
                float el = Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg;
                bool behindMain = Mathf.Abs(az) < ScreenHalfYawDeg + 1f
                                  && el > -ScreenDropDeg - ScreenHalfPitchDeg - 1f && el < -ScreenDropDeg + ScreenHalfPitchDeg + 1f;
                if (behindMain) continue;
                int cx = Mathf.RoundToInt(vp.x * img.width), cy = Mathf.RoundToInt(vp.y * img.height);
                double sum = 0;
                int count = 0;
                for (int y = cy - 3; y <= cy + 3; y++)
                for (int x = cx - 3; x <= cx + 3; x++)
                {
                    if (x < 0 || y < 0 || x >= img.width || y >= img.height) continue;
                    Color c = img.GetPixel(x, y).linear;
                    sum += 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b;
                    count++;
                }
                if (count == 0) continue;
                if (ScreenMultiplyLayout.IsFar(seat)) { farSum += sum / count; farN++; }
                else { nearSum += sum / count; nearN++; }
            }
            return (nearN > 0 ? (float)(nearSum / nearN) : 0f, farN > 0 ? (float)(farSum / farN) : 0f);
        }

        private static Texture2D? LoadPlate(out string name)
        {
            name = "";
            string capDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "tools", "web-compositor", "captures"));
            if (!Directory.Exists(capDir)) return null;
            string? path = Directory.GetFiles(capDir, "plate_C_*.jpg").OrderBy(p => p, StringComparer.Ordinal).LastOrDefault()
                           ?? Directory.GetFiles(capDir, "plate_*.jpg").OrderBy(p => p, StringComparer.Ordinal).LastOrDefault();
            if (path == null) return null;
            name = Path.GetFileName(path);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true);
            if (!tex.LoadImage(File.ReadAllBytes(path))) { UnityEngine.Object.DestroyImmediate(tex); return null; }
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Trilinear;
            return tex;
        }

        /// <summary>取り込みのリングの代わり。全段に同じ写真を入れ、mip を作る。</summary>
        private static Texture2DArray BuildFeed(Texture2D plate)
        {
            int w = ScreenMultiply.TileW, h = ScreenMultiply.TileH;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var small = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: true);
            var arr = new Texture2DArray(w, h, ScreenMultiplyLayout.DelaySlots, TextureFormat.RGBA32, mipChain: true)
            { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            try
            {
                Graphics.Blit(plate, rt);
                RenderTexture.active = rt;
                small.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                Color32[] px = small.GetPixels32();
                for (int i = 0; i < px.Length; i++) px[i].a = 255;
                for (int s = 0; s < ScreenMultiplyLayout.DelaySlots; s++) arr.SetPixels32(px, s, 0);
                arr.Apply(updateMipmaps: true);
            }
            finally
            {
                RenderTexture.active = null;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(small);
            }
            return arr;
        }

        private static Texture2D Shoot(Camera cam, int w, int h)
        {
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var tex = new Texture2D(w, h, TextureFormat.RGB24, mipChain: false);
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
            }
            finally
            {
                cam.targetTexture = null;
                RenderTexture.active = null;
                UnityEngine.Object.DestroyImmediate(rt);
            }
            return tex;
        }

        private static string F(float v) => v.ToString("F4", CultureInfo.InvariantCulture);
    }
}
