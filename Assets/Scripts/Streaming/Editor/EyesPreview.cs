#nullable enable
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// <b>闇に目が開く異変を 1 コマずつ焼く</b>（<c>canon/LEDGER.md</c> 0072）。
    ///
    /// ⚠ <b>実シェーダ <c>FixedCamVr/AnomalyEyes</c> と実ロジック</b>（<see cref="AnomalyEyesLogic"/> ＋
    /// <see cref="AnomalyEyesMesh"/>）を通す。模写ではないので、開く順・大きさ・虹彩の彫りは実機と同じ式。
    ///
    /// ⚠ <b>実機の見え方そのものではない</b> — 両眼視差・レンズ・パススルーは入っていない。
    /// スクリーンは<b>置き換えの暗い板</b>（本編の映像は出さない）。目がその外に出ることだけを見る絵。
    ///
    /// 使い方: <c>.\tools\unity.ps1 menu eyes</c> → <c>tools/make-preview-video.py</c> で mp4 へ。
    /// </summary>
    public static class EyesPreview
    {
        private const string OutDirRel = "Screenshots/eyes-preview";
        private const int W = 960, H = 540;
        private const int Fps = 30;

        /// <summary>撮影台。他の scene 幾何が写り込まないよう、誰も居ない高さへ。</summary>
        private static readonly Vector3 Stage = new(0f, 2000f, 0f);

        /// <summary>持続を見せる尺 (秒)。全部開いてからも少し回す（瞬き・震えが見える）。</summary>
        private const float HoldTailSec = 2.0f;

        /// <summary>縦画角 (度)。16:9 で水平 約 102° ＝ Quest 3 の表示画角に近い。</summary>
        private const float FovDeg = 70f;

        // 置き換えのスクリーン（本編の実測: 水平 ±30.6° / 垂直 ±18.4° / 8° 下）。
        private const float ScreenDistM = 2.0f;
        private const float ScreenHalfYawDeg = 30.6f;
        private const float ScreenHalfPitchDeg = 18.4f;
        private const float ScreenDropDeg = 8f;

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Anomaly Eyes", priority = 233)]
        public static void Run()
        {
            string dir = Path.Combine(Application.dataPath, OutDirRel);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);

            Shader? sh = Shader.Find(AnomalyEyes.ShaderName);
            if (sh == null)
            {
                Debug.LogError($"[EyesPreview] シェーダ {AnomalyEyes.ShaderName} が見つかりません");
                return;
            }

            float density = ParseArg("density", 1f);

            var camGo = new GameObject("[EyesPreview] Camera");
            var eyesGo = new GameObject("[EyesPreview] Eyes");
            var screenGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            screenGo.name = "[EyesPreview] Screen (placeholder)";
            var mat = new Material(sh) { name = "AnomalyEyes (preview)" };
            Material? screenMat = null;
            Mesh? mesh = null;
            int frames = 0;

            try
            {
                EyeSeat[] seats = AnomalyEyesMesh.BuildSeats();
                mesh = AnomalyEyesMesh.Build(seats);
                eyesGo.transform.SetPositionAndRotation(Stage, Quaternion.identity);
                eyesGo.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = eyesGo.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

                // 置き換えのスクリーン。**目がこの外に出ること**を見るためだけの板。
                Shader? plain = Shader.Find("Universal Render Pipeline/Unlit");
                if (plain != null)
                {
                    screenMat = new Material(plain) { name = "Screen (preview)" };
                    screenMat.SetColor("_BaseColor", new Color(0.10f, 0.09f, 0.08f, 1f));
                    screenMat.SetColor("_Color", new Color(0.10f, 0.09f, 0.08f, 1f));
                    screenGo.GetComponent<Renderer>().sharedMaterial = screenMat;
                }
                float hw = ScreenDistM * Mathf.Tan(ScreenHalfYawDeg * Mathf.Deg2Rad);
                float hh = ScreenDistM * Mathf.Tan(ScreenHalfPitchDeg * Mathf.Deg2Rad);
                float drop = ScreenDistM * Mathf.Tan(ScreenDropDeg * Mathf.Deg2Rad);
                screenGo.transform.position = Stage + new Vector3(0f, -drop, ScreenDistM);
                screenGo.transform.rotation = Quaternion.identity;
                screenGo.transform.localScale = new Vector3(hw * 2f, hh * 2f, 1f);

                var cam = camGo.AddComponent<Camera>();
                cam.fieldOfView = FovDeg;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 100f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.transform.SetPositionAndRotation(Stage, Quaternion.identity);

                var logic = new AnomalyEyesLogic();
                float dt = 1f / Fps;
                float clock = 0f;
                var ledger = new System.Text.StringBuilder();
                ledger.AppendLine("frame\tsec\tstage\tbig\tfield\tintensity\topen");

                float total = AnomalyEyesLogic.HintSec + AnomalyEyesLogic.StareSec
                              + AnomalyEyesLogic.SwarmSec + HoldTailSec;
                for (float t = 0f; t < total; t += dt)
                {
                    logic.Tick(dt, wanted: true, density: density);
                    clock += dt;
                    Write(mat, logic, clock);
                    Shoot(cam, Path.Combine(dir, $"f{frames:0000}.png"));
                    int open = AnomalyEyesMesh.CountOpen(seats, logic.Big, logic.Field, logic.Density);
                    ledger.Append(frames).Append('\t').Append(F(t + dt)).Append('\t')
                          .Append(logic.Stage).Append('\t')
                          .Append(F(logic.Big)).Append('\t').Append(F(logic.Field)).Append('\t')
                          .Append(F(logic.Intensity)).Append('\t').Append(open).Append('\n');
                    frames++;
                }

                // 段ごとの静止画（数値が緑でも絵は必ず開く — rules/visual-verification.md §7）。
                Shot(cam, mat, dir, "stage_1_hint_quarter", AnomalyEyesLogic.HintSec * 0.25f, density, 0f);
                Shot(cam, mat, dir, "stage_1_hint_end", AnomalyEyesLogic.HintSec, density, 0f);
                float stareEnd = AnomalyEyesLogic.HintSec + AnomalyEyesLogic.StareSec;
                Shot(cam, mat, dir, "stage_2_stare", stareEnd - 0.2f, density, 0f);
                Shot(cam, mat, dir, "stage_3_swarm_half",
                     stareEnd + AnomalyEyesLogic.SwarmSec * 0.5f, density, 0f);
                float full = stareEnd + AnomalyEyesLogic.SwarmSec + 0.5f;
                Shot(cam, mat, dir, "stage_4_hold", full, density, 0f);
                // 360 度いちめん（振り返っても目が居ることを見る）。
                Shot(cam, mat, dir, "hold_yaw090", full, density, 90f);
                Shot(cam, mat, dir, "hold_yaw180", full, density, 180f);
                Shot(cam, mat, dir, "hold_yaw270", full, density, 270f);
                // 割合を下げた版（カットの eyes が 1 未満のとき）。
                Shot(cam, mat, dir, "hold_density040", full, 0.40f, 0f);

                File.WriteAllText(Path.Combine(dir, "frames.tsv"), ledger.ToString());
                Debug.Log($"[EyesPreview] {frames} コマ + 静止画 9 枚 + frames.tsv → Assets/{OutDirRel}/\n"
                        + $"  目 {seats.Length} 個（大きい目 1 ＋ {AnomalyEyesMesh.EyeCount}）/ 割合 {density:F2}\n"
                        + $"  兆し {AnomalyEyesLogic.HintSec}s → 凝視 {AnomalyEyesLogic.StareSec}s → "
                        + $"開眼 {AnomalyEyesLogic.SwarmSec}s（全開まで "
                        + $"{AnomalyEyesLogic.HintSec + AnomalyEyesLogic.StareSec + AnomalyEyesLogic.SwarmSec}s）\n"
                        + "  ⚠ 両眼視差・レンズ・パススルーは入っていない。スクリーンは置き換えの暗い板");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(camGo);
                UnityEngine.Object.DestroyImmediate(eyesGo);
                UnityEngine.Object.DestroyImmediate(screenGo);
                UnityEngine.Object.DestroyImmediate(mat);
                if (screenMat != null) UnityEngine.Object.DestroyImmediate(screenMat);
                if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        /// <summary>その時刻まで実ロジックを回して 1 枚撮る（頭の向きを <paramref name="yawDeg"/> へ）。</summary>
        private static void Shot(Camera cam, Material mat, string dir,
                                 string name, float sec, float density, float yawDeg)
        {
            var logic = new AnomalyEyesLogic();
            float dt = 1f / Fps;
            float clock = 0f;
            for (float t = 0f; t < sec; t += dt) { logic.Tick(dt, true, density); clock += dt; }
            Write(mat, logic, clock);
            cam.transform.rotation = Quaternion.Euler(0f, yawDeg, 0f);
            Shoot(cam, Path.Combine(dir, name + ".png"));
            cam.transform.rotation = Quaternion.identity;
        }

        private static void Write(Material mat, AnomalyEyesLogic l, float clock)
        {
            mat.SetFloat("_EyeBig", l.Big);
            mat.SetFloat("_EyeField", l.Field);
            mat.SetFloat("_EyeSpan", AnomalyEyesLogic.SwarmSpan);
            mat.SetFloat("_EyeDensity", l.Density);
            mat.SetFloat("_EyeFade", l.Fade);
            mat.SetFloat("_EyeIntensity", l.Intensity);
            mat.SetFloat("_EyeTime", clock);
            mat.SetFloat("_EyeAspect", AnomalyEyesMesh.AspectHeight);
        }

        private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);

        private static float ParseArg(string key, float fallback)
        {
            string raw = EditorCliArgs.Get(key) ?? "";
            return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                ? v : fallback;
        }

        private static void Shoot(Camera cam, string path)
        {
            var rt = new RenderTexture(W, H, 24);
            var tex = new Texture2D(W, H, TextureFormat.RGB24, mipChain: false);
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply();
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally
            {
                cam.targetTexture = null;
                RenderTexture.active = null;
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }
    }
}
