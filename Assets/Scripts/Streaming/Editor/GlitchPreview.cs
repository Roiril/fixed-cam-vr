#nullable enable
using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// <b>乱れが起きるたびにどれだけ大きくなるかを 1 コマずつ焼く</b>（`canon/LEDGER.md` 0055）。
    ///
    /// ⚠ <b>実シェーダ <c>FixedCamVr/ScreenComposite</c> と実ロジック</b>
    /// （<see cref="GlitchEscalationLogic"/> ＋ <see cref="GlitchEnvelopeLogic"/>）を通す。
    /// 模写ではないので、ここで見える揺れ幅・帯の数・白の混ざり方は実機と同じ式。
    ///
    /// ⚠ <b>実機の見え方そのものではない</b> — 両眼視差・レンズ・ブラウン管の曲面は入っていない。
    /// 音も出ない（<c>glE</c> に対応する音量だけ画面下の帯に数値で出す）。
    ///
    /// 使い方: <c>.\tools\unity.ps1 menu glitch</c> → <c>tools/make-preview-video.py</c> で mp4 へ。
    /// </summary>
    public static class GlitchPreview
    {
        private const string OutDirRel = "Screenshots/glitch-preview";
        private const int W = 960, H = 540;
        private const int Fps = 30;

        /// <summary>撮影台。他の scene 幾何が写り込まないよう、誰も居ない高さへ。</summary>
        private static readonly Vector3 Stage = new Vector3(0f, 2000f, 0f);

        /// <summary>見せる回数。頭打ち（12）を跨いで、育ち切ったところまで写す。</summary>
        private const int Occurrences = 14;

        /// <summary>台本の代表値。2 周目の差し替えの頭（`show.json` の L2C0#0 step0）。</summary>
        private const float ScriptLevel = 0.70f;
        private const float ScriptHoldSec = 0.40f;

        /// <summary>1 回ぶんを撮る尺（秒）。減衰まで見えるように尺 + 余韻。</summary>
        private const float TailSec = 0.45f;

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Glitch Escalation", priority = 232)]
        public static void Run()
        {
            string dir = Path.Combine(Application.dataPath, OutDirRel);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);

            Shader? sh = Shader.Find("FixedCamVr/ScreenComposite");
            if (sh == null)
            {
                Debug.LogError("[GlitchPreview] シェーダ FixedCamVr/ScreenComposite が見つかりません");
                return;
            }

            Texture2D? plate = LoadNewestPlate(out string plateLabel);
            if (plate == null)
            {
                Debug.LogError("[GlitchPreview] カメラ A のプレートが見つかりません"
                             + "（tools/web-compositor/captures/plate_A_*.jpg）");
                return;
            }

            var camGo = new GameObject("[GlitchPreview] Camera");
            var quadGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quadGo.name = "[GlitchPreview] Screen";
            var mat = new Material(sh) { name = "ScreenComposite (glitch preview)" };
            int frames = 0;
            try
            {
                // スクリーンは 16:9 の枠。プレートは 4:3 なので contain-fit で letterbox する
                //   （`MjpegScreen.ContainScale` と同じ考え。ここは静止画なので定数で足りる）。
                const float frameAspect = 16f / 9f;
                float srcAspect = plate.width / (float)plate.height;
                Vector4 liveScale = srcAspect > frameAspect
                    ? new Vector4(1f, frameAspect / srcAspect, 0f, 0f)
                    : new Vector4(srcAspect / frameAspect, 1f, 0f, 0f);

                mat.SetTexture("_LiveTex", plate);
                mat.SetVector("_LiveScale", liveScale);
                ApplyGlobalPost(mat, out string postLabel);

                quadGo.transform.SetPositionAndRotation(Stage, Quaternion.identity);
                quadGo.transform.localScale = new Vector3(frameAspect, 1f, 1f);
                quadGo.GetComponent<Renderer>().sharedMaterial = mat;

                var cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;              // 曲面もパースも入れない（乱れの量だけを見る）
                cam.orthographicSize = 0.5f;
                cam.nearClipPlane = 0.01f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.transform.SetPositionAndRotation(Stage + new Vector3(0f, 0f, -1f), Quaternion.identity);

                // ---- 実ロジックをそのまま回す（模写しない）----
                var esc = new GlitchEscalationLogic();
                var env = new GlitchEnvelopeLogic();
                float dt = 1f / Fps;
                float seed = 0f;
                // ⚠ コマごとの値を台帳へ出す。**Python 側で計算し直さない** —
                //    二重に持つと、定数を直したときに帯だけ古い値を出す（沈黙して食い違う）。
                var ledger = new System.Text.StringBuilder();
                // ⚠ progress（回数の直線）と curve（実際に掛かる値）は**別物**。
                //   ゲージに progress を出すと、7 回目で半分以上進んで見えるのに画はまだ軽い
                //   ＝ 帯が嘘をつく（実際に 1 度そうなった）。
                ledger.AppendLine("frame\tn\tprogress\tcurve\tlevelApplied\tholdSec\tvolume\tlevelNow");

                for (int n = 1; n <= Occurrences; n++)
                {
                    esc.Notify();
                    float lv = esc.ApplyLevel(ScriptLevel);
                    float hold = esc.ApplyHold(ScriptHoldSec);
                    env.Pulse(lv, hold);

                    float total = hold + TailSec;
                    for (float t = 0f; t < total; t += dt)
                    {
                        float level = env.Tick(dt);
                        seed += dt;
                        mat.SetFloat("_Glitch", level);
                        mat.SetFloat("_GlitchSeed", seed);
                        Shoot(cam, Path.Combine(dir, $"f{frames:0000}.png"));
                        ledger.Append(frames).Append('\t').Append(n).Append('\t')
                              .Append(esc.Progress01.ToString("F3", CultureInfo.InvariantCulture)).Append('\t')
                              .Append(esc.Curve01.ToString("F3", CultureInfo.InvariantCulture)).Append('\t')
                              .Append(lv.ToString("F3", CultureInfo.InvariantCulture)).Append('\t')
                              .Append(hold.ToString("F3", CultureInfo.InvariantCulture)).Append('\t')
                              .Append(esc.VolumeGain.ToString("F3", CultureInfo.InvariantCulture)).Append('\t')
                              .Append(level.ToString("F3", CultureInfo.InvariantCulture)).Append('\n');
                        frames++;
                    }
                    // 次の回まで少し空ける（連続して見えると「1 回ぶん」が読めない）。
                    env.Tick(0.2f);
                    mat.SetFloat("_Glitch", 0f);

                    Debug.Log($"[GlitchPreview] {n,2} 回目  進み {esc.Progress01:F2}  "
                            + $"強さ {ScriptLevel:F2}→{lv:F2}  尺 {ScriptHoldSec:F2}→{hold:F2}s  "
                            + $"音量 {esc.VolumeGain:F2}");
                }

                File.WriteAllText(Path.Combine(dir, "frames.tsv"), ledger.ToString());
                Debug.Log($"[GlitchPreview] {frames} コマ + frames.tsv → Assets/{OutDirRel}/\n"
                        + $"  台本の値 強さ {ScriptLevel} / 尺 {ScriptHoldSec}s を {Occurrences} 回\n"
                        + $"  プレート {plateLabel} / post {postLabel}\n"
                        + $"  ⚠ 両眼視差・レンズ・管の曲面は入っていない。音も出ない");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(camGo);
                UnityEngine.Object.DestroyImmediate(quadGo);
                UnityEngine.Object.DestroyImmediate(mat);
            }
        }

        /// <summary>いちばん新しいカメラ A の無人プレート。</summary>
        private static Texture2D? LoadNewestPlate(out string label)
        {
            label = "(none)";
            string root = Path.Combine(Directory.GetParent(Application.dataPath)!.FullName,
                                       "tools", "web-compositor", "captures");
            if (!Directory.Exists(root)) return null;
            string? newest = null;
            DateTime best = DateTime.MinValue;
            foreach (string f in Directory.GetFiles(root, "plate_A*.*"))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".jpg" && ext != ".jpeg" && ext != ".png") continue;
                DateTime t = File.GetLastWriteTimeUtc(f);
                if (t > best) { best = t; newest = f; }
            }
            if (newest == null) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true);
            if (!tex.LoadImage(File.ReadAllBytes(newest))) return null;
            tex.filterMode = FilterMode.Trilinear;
            label = Path.GetFileName(newest);
            return tex;
        }

        /// <summary>
        /// show.json のトップレベル <c>post</c>（全体グレーディング）を材質へ。
        /// ⚠ 素の絵で見ると明るすぎて乱れが読めない。実機に近い暗さで見るために掛ける。
        /// キーが 1 つでも欠けたら**その項目だけ**シェーダ既定のままにする。
        /// </summary>
        private static void ApplyGlobalPost(Material mat, out string label)
        {
            label = "(shader defaults)";
            string root = Directory.GetParent(Application.dataPath)!.FullName;
            string path = Path.Combine(root, "tools", "web-compositor", "show.json");
            if (!File.Exists(path)) return;
            string text;
            try { text = File.ReadAllText(path); }
            catch (IOException) { return; }

            int i = text.IndexOf("\"post\"", StringComparison.Ordinal);
            if (i < 0) return;
            int open = text.IndexOf('{', i);
            int close = open >= 0 ? text.IndexOf('}', open) : -1;
            if (open < 0 || close < 0) return;
            string body = text.Substring(open, close - open + 1);

            int applied = 0;
            foreach (string key in new[]
            {
                "exposure", "contrast", "saturation", "temperature", "vignette",
                "grain", "scanline", "lift", "tint", "aberration", "pixelate",
            })
            {
                if (!TryReadNumber(body, key, out float v)) continue;
                mat.SetFloat("_" + char.ToUpperInvariant(key[0]) + key.Substring(1), v);
                applied++;
            }
            label = applied > 0 ? $"show.json global ({applied} 項目)" : "(shader defaults)";
        }

        private static bool TryReadNumber(string body, string key, out float value)
        {
            value = 0f;
            int k = body.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (k < 0) return false;
            int c = body.IndexOf(':', k);
            if (c < 0) return false;
            int e = c + 1;
            while (e < body.Length && (body[e] == ' ' || body[e] == '\t')) e++;
            int s = e;
            while (e < body.Length && (char.IsDigit(body[e]) || body[e] == '-' || body[e] == '+'
                                       || body[e] == '.' || body[e] == 'e' || body[e] == 'E')) e++;
            return e > s && float.TryParse(body.Substring(s, e - s), NumberStyles.Float,
                                           CultureInfo.InvariantCulture, out value);
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
