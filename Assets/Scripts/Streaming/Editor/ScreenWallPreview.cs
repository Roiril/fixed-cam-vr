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
    /// メインスクリーンは本番と同じ面で組む（シーンの材質の複製・参照の筐体・曲面・前段・本人の採用値。
    /// <see cref="IntroPreview"/> のテレビ方式と同じ手順）。増える面の画は、本番と同じくその面の取り込み。
    /// ⚠ 置き換えの板を使わない。最初の版は 16:9 の平らな板に写真を貼り、面にもその写真を直接渡していたので、
    /// メインの形・縦横比・角の丸みと、面の外形が全部本番と違う絵になった（LEDGER 0319 / 0320）。
    ///
    /// ⚠ 実機の見え方そのものではない。両眼視差・レンズは入っていない。映像は無人写真 1 枚（動かない）。
    ///
    /// 使い方: <c>.\tools\unity.ps1 menu screen-wall</c>
    /// </summary>
    public static class ScreenWallPreview
    {
        private const string OutDirRel = "Screenshots/screen-wall";
        private const int StillW = 1600, StillH = 900;
        private const int SeqW = 1600, SeqH = 900;
        private const int Fps = 30;
        private const float FovDeg = 70f;
        private static readonly Vector3 Stage = new(0f, 2000f, 0f);

        // メインスクリーンの置き方（ScreenAnchor の distance / heightOffset、シーンの高さ）。
        private const float ScreenDistM = 2.0f;
        private const float ScreenHeightOffsetM = -0.28f;
        private const float ScreenHeightM = 1.3333f;
        /// <summary>目の高さ（床から）。床の映り込みはこの下に映る。</summary>
        private const float EyeHeightM = 1.55f;
        /// <summary>シーンの物を写さないための撮影用レイヤ（<see cref="IntroPreview"/> と同じ番号）。</summary>
        private const int PreviewLayer = 1;

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Screen Wall And Fog", priority = 234)]
        public static void Run()
        {
            if (!EditorCliArgs.EnsureScene("Assets/Scenes/Main.unity")) return;
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
            Mesh? curved = null;
            CrtSignalProcessor? signal = null;
            long generation = 0;
            try
            {
                var cam = camGo.AddComponent<Camera>();
                cam.fieldOfView = FovDeg;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 100f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.allowHDR = false;
                cam.cullingMask = 1 << PreviewLayer;
                cam.enabled = false;
                cam.transform.SetPositionAndRotation(Stage, Quaternion.identity);

                // --- メインスクリーン。本番と同じ面（IntroPreview のテレビ方式と同じ手順）---
                var sceneScreen = UnityEngine.Object.FindObjectOfType<MjpegScreen>(true);
                Material? sceneMat = sceneScreen != null ? sceneScreen.GetComponent<Renderer>().sharedMaterial : null;
                if (sceneMat == null)
                {
                    Debug.LogError("[ScreenWallPreview] シーンに MjpegScreen の材質がありません（`menu scene` を先に）");
                    return;
                }
                screenMat = new Material(sceneMat) { name = "ScreenComposite (screen-wall)" };
                IntroPreview.ConfigureScreenMaterial(screenMat, plate, plate, CrtReferenceScreen.LogicalAspect);
                if (!CrtReferenceScreen.Apply(screenMat))
                {
                    Debug.LogError("[ScreenWallPreview] 参照の筐体を組めません");
                    return;
                }
                CrtSignalProfile.ApplySelected(screenMat);
                CameraFeelFx.WriteUniforms(screenMat, CrtSignalProfile.NoiseDark, CrtSignalProfile.NoiseFixed,
                    0, 0, ScreenDecayLogic.BlocksFor(CrtSignalProfile.Mono), 0, CrtSignalProfile.Mono);
                CrtSignalProfile.ApplySelected(screenMat);
                screenMat.SetFloat("_CrtIgnite", 1f);
                screenMat.SetFloat("_IntroLive", 1f);
                // 本編のメインスクリーンはテレビの枠つき（既定のテレビ方式・0321）。IntroDirector.ResetIgnite と同じ値。
                screenMat.SetFloat("_CrtTvCase", IntroLogic.MainShowTvCase);
                screenMat.SetFloat("_CrtTvDark", 0f);
                screenMat.SetFloat("_Glitch", 0f);

                screenGo.name = "[ScreenWallPreview] Main screen";
                UnityEngine.Object.DestroyImmediate(screenGo.GetComponent<Collider>());
                var screenRenderer = screenGo.GetComponent<MeshRenderer>();
                screenRenderer.sharedMaterial = screenMat;
                curved = CrtScreenMesh.BuildMesh(48, 0.055f);
                screenGo.GetComponent<MeshFilter>().sharedMesh = curved;
                screenGo.transform.position = Stage + new Vector3(0f, ScreenHeightOffsetM, ScreenDistM);
                screenGo.transform.localScale = new Vector3(ScreenHeightM * CrtReferenceScreen.CaseAspect, ScreenHeightM, 1f);
                signal = new CrtSignalProcessor();
                Vector3 view = screenGo.transform.InverseTransformPoint(cam.transform.position);
                screenMat.SetVector("_CrtView", new Vector4(view.x, view.y, view.z, 0f));
                if (!signal.PrepareDisplay(screenMat, ++generation, forceSource: true))
                {
                    Debug.LogError("[ScreenWallPreview] スクリーンの前段を構成できません");
                    return;
                }

                // --- 増える面。本番と同じく、メインスクリーンを取り込んで映す ---
                wallGo.transform.SetPositionAndRotation(Stage, Quaternion.identity);
                var wall = wallGo.AddComponent<ScreenMultiply>();
                wall.Build();
                if (!wall.IsBuilt)
                {
                    Debug.LogError("[ScreenWallPreview] 増えるスクリーンを組めません（シェーダか取り込みの器）");
                    return;
                }
                wall.SetCaptureSource(screenRenderer);
                wall.SetFloorWorldY(Stage.y - EyeHeightM);
                SetLayer(screenGo.transform);
                SetLayer(wallGo.transform);

                float open0 = AnomalyEyesLogic.HintSec + AnomalyEyesLogic.StareSec;
                float full = open0 + AnomalyEyesLogic.SwarmSec;
                // 閉じの段の頭（区間の半ば・録画が尽きる所）。ここから目の時計の閉じ方で消える。
                float close = full + 3.0f;
                var report = new StringBuilder();
                report.AppendLine("variant\tshot\tsec\topen\tafterglow\tarriving\tnearLuma\tfarLuma\tfarOverNear");

                foreach (string variant in new[] { "fog", "flat" })
                {
                    string vdir = Path.Combine(dir, variant);
                    Directory.CreateDirectory(vdir);
                    bool fog = variant == "fog";
                    wall.SetLook(fog ? ScreenFogLogic.Density : 0f, fog ? ScreenMultiply.EmergeM : 0f, showFar: fog);

                    // 時刻は目の時計の頭から。pitch は正で下を向く。
                    // closeAt はその撮影で閉じの段に入る時刻（帯の回転を見る 12 番だけ閉じない）。
                    (string name, float sec, float yaw, float pitch, float closeAt)[] shots =
                    {
                        ("1_first", open0 - 0.15f, 0f, 0f, close),
                        ("2_ripple", open0 + AnomalyEyesLogic.SwarmSec * 0.14f, 0f, 0f, close),
                        ("3_pause", open0 + AnomalyEyesLogic.SwarmSec * 0.55f, 0f, 0f, close),
                        ("4_rush", open0 + AnomalyEyesLogic.SwarmSec * 0.88f, 0f, 0f, close),
                        ("5_just_full", full + 0.25f, 0f, 0f, close),
                        ("6_settled", full + 3.0f, 0f, 0f, close),
                        ("7_settled_left", full + 3.0f, -55f, 0f, close),
                        ("8_settled_right", full + 3.0f, 55f, 0f, close),
                        ("9_settled_up", full + 3.0f, 0f, -32f, close),
                        // 波の先頭（橙）が横を渡っていく所。
                        ("10_rush_left", open0 + AnomalyEyesLogic.SwarmSec * 0.40f, -70f, 0f, close),
                        ("11_rush_back", open0 + AnomalyEyesLogic.SwarmSec * 0.80f, 180f, 0f, close),
                        // 帯の回転。6_settled と同じ向きで 12 秒後（目の高さの帯 +10.8°・上の帯 −14.4°）。
                        ("12_settled_late", full + 15.0f, 0f, 0f, float.MaxValue),
                        // 床の映り込み。
                        ("13_settled_down", full + 3.0f, 0f, 28f, close),
                        ("14_settled_down_left", full + 3.0f, -60f, 22f, close),
                        // 閉じ。波が逆向きに戻り（後ろから）、1 枚ずつ電源が落ちる。0 番が最後。
                        ("15_closing_back", close + 0.18f, 180f, 0f, close),
                        ("16_closing_left", close + 0.30f, -70f, 0f, close),
                        ("17_closing_front", close + 0.45f, 0f, 0f, close),
                        ("18_afterglow_front", close + 0.75f, 0f, 0f, close),
                        ("19_last_one", close + AnomalyEyesLogic.CloseSec * 0.93f, 0f, 0f, close),
                    };
                    foreach (var shot in shots)
                    {
                        DriveTo(wall, shot.sec, shot.closeAt);
                        SetLayer(wallGo.transform);
                        cam.transform.rotation = Quaternion.Euler(shot.pitch, shot.yaw, 0f);
                        Texture2D img = Shoot(cam, StillW, StillH);
                        File.WriteAllBytes(Path.Combine(vdir, shot.name + ".png"), img.EncodeToPNG());
                        (float near, float far) = MeasureTiers(cam, img, wall.RunSec);
                        UnityEngine.Object.DestroyImmediate(img);
                        report.Append(variant).Append('\t').Append(shot.name).Append('\t').Append(F(shot.sec)).Append('\t')
                              .Append(wall.OpenCount).Append('\t').Append(wall.AfterglowCount).Append('\t')
                              .Append(wall.ArrivingCount).Append('\t')
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
                    for (float t = 0f; t < close + AnomalyEyesLogic.CloseSec + 0.3f; t += dt)
                    {
                        Step(wall, logic, dt, ref clock, ref begun, wanted: t < close);
                        SetLayer(wallGo.transform);
                        if (t < AnomalyEyesLogic.HintSec * 0.6f) continue;
                        Texture2D img = Shoot(cam, SeqW, SeqH);
                        File.WriteAllBytes(Path.Combine(sdir, $"f{n:0000}.jpg"), img.EncodeToJPG(92));
                        UnityEngine.Object.DestroyImmediate(img);
                        n++;
                    }
                }

                File.WriteAllText(Path.Combine(dir, "report.tsv"), report.ToString());
                int farSeats = ScreenMultiplyLayout.Seats.Count(s => ScreenMultiplyLayout.IsFar(s));
                Debug.Log($"[ScreenWallPreview] fog / flat の静止画 各 19 枚{(sequence ? " + 連番" : "")} + report.tsv → Assets/{OutDirRel}/\n"
                        + $"  席 {ScreenMultiplyLayout.Seats.Length}（霧の奥の段 {farSeats}）/ 写真 {plateName}\n"
                        + $"  霧 {ScreenFogLogic.Density}/m・{ScreenFogLogic.ClearM}m まで無し / 奥から {ScreenMultiply.EmergeM}m・{ScreenMultiply.ArriveSec}s\n"
                        + $"  取り込み {wall.CaptureCount} 回（本番と同じ経路でメインスクリーンを写した）\n"
                        + "  ⚠ 両眼視差・レンズは入っていない。映像は無人写真 1 枚");
                if (wall.CaptureCount == 0)
                    Debug.LogError("[ScreenWallPreview] メインスクリーンを 1 度も取り込めていません。面は何も映していません");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(camGo);
                UnityEngine.Object.DestroyImmediate(wallGo);
                UnityEngine.Object.DestroyImmediate(screenGo);
                if (screenMat != null) UnityEngine.Object.DestroyImmediate(screenMat);
                if (curved != null) UnityEngine.Object.DestroyImmediate(curved);
                signal?.Dispose();
                UnityEngine.Object.DestroyImmediate(plate);
            }
        }

        /// <summary>
        /// 頭から <paramref name="sec"/> 秒まで、本番と同じ刻みで回す（点いた時刻の表も進む）。
        /// <paramref name="closeAt"/> 秒からはカットが「出せ」と言わなくなる（閉じの段）。
        /// </summary>
        private static void DriveTo(ScreenMultiply wall, float sec, float closeAt)
        {
            var logic = new AnomalyEyesLogic();
            float dt = 1f / Fps, clock = 0f;
            bool begun = false;
            for (float t = 0f; t < sec; t += dt) Step(wall, logic, dt, ref clock, ref begun, wanted: t < closeAt);
        }

        private static void Step(ScreenMultiply wall, AnomalyEyesLogic logic, float dt, ref float clock, ref bool begun,
                                 bool wanted)
        {
            logic.Tick(dt, wanted, density: 1f);
            clock += dt;
            if (!begun) { wall.BeginRun(); begun = true; }
            wall.Drive(dt, logic.Big, logic.Field, logic.Span, logic.Closing, clock);
        }

        /// <summary>
        /// 画の上で、手前の段と霧の奥の段の明るさ（linear の輝度の平均）を測る。席の中心の周り 7×7 画素。
        /// 席の中心は帯の回転を当てた位置。メインスクリーンの後ろと画の外の席は数えない。
        /// </summary>
        private static (float near, float far) MeasureTiers(Camera cam, Texture2D img, float runSec)
        {
            double nearSum = 0, farSum = 0;
            int nearN = 0, farN = 0;
            for (int i = 0; i < ScreenMultiplyLayout.Seats.Length; i++)
            {
                ScreenSeat seat = ScreenMultiplyLayout.Seats[i];
                Vector3 world = Stage + ScreenMultiplyLayout.DriftedCenter(seat, runSec);
                Vector3 vp = cam.WorldToViewportPoint(world);
                if (vp.z <= 0f || vp.x < 0.03f || vp.x > 0.97f || vp.y < 0.03f || vp.y > 0.97f) continue;
                Vector3 local = cam.transform.InverseTransformPoint(world);
                float az = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
                float el = Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg;
                float halfYaw = Mathf.Atan(ScreenHeightM * CrtReferenceScreen.CaseAspect * 0.5f / ScreenDistM) * Mathf.Rad2Deg;
                float top = Mathf.Atan((ScreenHeightOffsetM + ScreenHeightM * 0.5f) / ScreenDistM) * Mathf.Rad2Deg;
                float bottom = Mathf.Atan((ScreenHeightOffsetM - ScreenHeightM * 0.5f) / ScreenDistM) * Mathf.Rad2Deg;
                bool behindMain = Mathf.Abs(az) < halfYaw + 1f && el > bottom - 1f && el < top + 1f;
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

        private static void SetLayer(Transform root)
        {
            root.gameObject.layer = PreviewLayer;
            for (int i = 0; i < root.childCount; i++) SetLayer(root.GetChild(i));
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
