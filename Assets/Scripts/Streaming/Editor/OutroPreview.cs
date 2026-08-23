#nullable enable
using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// <b>終幕の電源断（ブラウン管が潰れて線 → 点 → 消える）を焼く</b>（<c>canon/LEDGER.md</c> 0111）。
    ///
    /// ⚠⚠ <b>シェーダの誤りは <c>unity.ps1 test</c> に 1 件も出ない。</b> 進みを配る側
    /// （<see cref="OutroLogic"/>）はテストで固定できるが、<b>その進みが画に何を起こすかは
    /// ここでしか見られない</b>。0111 の設計はまさに「消え方が読めるか」の話なので、
    /// 数値が緑でもこの絵を必ず開くこと。
    ///
    /// <b>見るのは 4 つ</b>:
    /// <list type="number">
    /// <item><b>潰れが一方向か</b> — 明るくなって戻る瞬間が 1 コマも無い（往復は
    ///       「起動しかけ」に反転し、0111 が廃した「ちかちか」へ戻る）。</item>
    /// <item><b>線が眩しすぎないか</b> — 潰れた瞬間の輝度は上限つきで、縮む段では落ちる。
    ///       絶対値はログの実測を見る（<c>COLLAPSE_GAIN_MAX</c> の妥当性はここで決める）。</item>
    /// <item><b>管のガラスが潰れていないか</b> — 角の丸みと縁の暗さは<b>元の枠のまま</b>。
    ///       一緒に潰れていたら <c>CrtSdf</c> に潰れた uv を渡している。</item>
    /// <item><b>時計が灰色の帯として残っていないか</b> — OSD は潰れの頭で消える。</item>
    /// </list>
    ///
    /// ⚠ <b>実機の見え方そのものではない</b> — 両眼視差・レンズ・管の曲面は入っていない。
    /// <b>線の明るさが暗所で眩しくないかは被って確かめる</b>（見かけの明るさの机上見積もりは
    /// 2 回続けて外した前歴がある — <c>memory/hmd_text_style.md</c>）。
    ///
    /// 使い方: <c>.\tools\unity.ps1 menu outro</c>
    /// </summary>
    public static class OutroPreview
    {
        private const string OutDirRel = "Screenshots/outro";
        private const int W = 960, H = 540;

        /// <summary>連番の枚数。0.9 秒を刻むので、1 枚あたり約 30ms（実機の 90fps に近い粒）。</summary>
        private const int Frames = 30;

        /// <summary>撮影台。他の scene 幾何が写り込まないよう、誰も居ない高さへ。</summary>
        private static readonly Vector3 Stage = new Vector3(0f, 2000f, 0f);

        /// <summary>OSD に焼く時刻。**固定**（決定論のため）。</summary>
        private static readonly DateTime FixedTime = new DateTime(2026, 9, 6, 14, 23, 45);

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Outro Collapse", priority = 235)]
        public static void Run()
        {
            string dir = Path.Combine(Application.dataPath, OutDirRel);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);

            Shader? sh = Shader.Find("FixedCamVr/ScreenComposite");
            if (sh == null)
            {
                Debug.LogError("[OutroPreview] シェーダ FixedCamVr/ScreenComposite が見つかりません");
                return;
            }

            Texture2D? plate = LoadNewestPlate(out string plateLabel);
            if (plate == null)
            {
                Debug.LogError("[OutroPreview] カメラ A のプレートが見つかりません"
                             + "（tools/web-compositor/captures/plate_A_*.jpg）");
                return;
            }

            var camGo = new GameObject("[OutroPreview] Camera");
            var quadGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quadGo.name = "[OutroPreview] Screen";
            var mat = new Material(sh) { name = "ScreenComposite (outro preview)" };
            try
            {
                const float frameAspect = 16f / 9f;
                float srcAspect = plate.width / (float)plate.height;
                Vector4 liveScale = srcAspect > frameAspect
                    ? new Vector4(1f, frameAspect / srcAspect, 0f, 0f)
                    : new Vector4(srcAspect / frameAspect, 1f, 0f, 0f);

                mat.SetTexture("_LiveTex", plate);
                mat.SetVector("_LiveScale", liveScale);
                mat.SetFloat("_FrameAspect", frameAspect);
                // 管の面（本編と同じ値）。**これが潰れていないか**を見るのが目的の 1 つ。
                mat.SetFloat("_CrtRound", CameraFeelFx.CrtRound);
                mat.SetFloat("_CrtEdge", CameraFeelFx.CrtEdge);
                mat.SetFloat("_CrtEdgeWidth", CameraFeelFx.CrtEdgeWidth);
                ApplyGlobalPost(mat, out string postLabel);

                quadGo.transform.SetPositionAndRotation(Stage, Quaternion.identity);
                quadGo.transform.localScale = new Vector3(frameAspect, 1f, 1f);
                quadGo.GetComponent<Renderer>().sharedMaterial = mat;

                // 時計も実装をそのまま通す（潰れの頭で消えることを絵で確かめる）。
                var osd = quadGo.AddComponent<ScreenOsd>();
                osd.BindForPreview(mat, new Vector2(liveScale.x, liveScale.y));
                osd.Tick(FixedTime);
                bool osdBuilt = osd.Built;
                if (!osdBuilt)
                {
                    Debug.LogWarning("[OutroPreview] OSD の版を掴めていません"
                                   + "（py -3.11 tools/make-osd-font.py）— 時計抜きの絵になります");
                }

                var cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;   // 曲面もパースも入れない（形と明るさだけを見る）
                cam.orthographicSize = 0.5f;
                cam.nearClipPlane = 0.01f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.transform.SetPositionAndRotation(Stage + new Vector3(0f, 0f, -1f),
                                                     Quaternion.identity);

                // ⚠ **実ロジックを通す**（進みの式をここで模写しない）。
                var logic = new OutroLogic();
                logic.Configure(OutroTiming.Default);
                logic.Begin();
                OutroTiming t = OutroTiming.Default;
                float dt = t.collapseSec / Frames;

                float prevCollapse = -1f;
                float peakLuma = 0f;
                int peakFrame = -1;
                var lumas = new float[Frames + 1];
                var peaks = new float[Frames + 1];
                var hots = new int[Frames + 1];
                int hotMax = 0, hotFrame = -1;

                for (int i = 0; i <= Frames; i++)
                {
                    float c = logic.Stage == OutroStage.Collapse ? logic.ScreenCollapse : 1f;
                    // ⚠ 一方向であることは絵の前に数値で確かめる（進みが戻ったら即わかる）。
                    if (c < prevCollapse - 1e-4f)
                    {
                        Debug.LogError($"[OutroPreview] 潰れの進みが戻った（{prevCollapse:F3} → {c:F3}）"
                                     + " — 0111 が廃した往復の動きが復活している");
                    }
                    prevCollapse = c;

                    mat.SetFloat("_ScreenCollapse", c);
                    mat.SetFloat("_ScreenPower", logic.ScreenPower);
                    // 時計は実装と同じ縁で消す（OutroDirector の OsdCutAt と対）。
                    osd.Suppressed = c > 0.12f;
                    osd.Tick(FixedTime);

                    string path = Path.Combine(dir, $"outro_{i:D2}.png");
                    (float mean, float peak, int hot) = ShootAndMeasure(cam, path);
                    lumas[i] = mean;
                    peaks[i] = peak;
                    hots[i] = hot;
                    if (peak > peakLuma) { peakLuma = peak; peakFrame = i; }
                    if (hot > hotMax) { hotMax = hot; hotFrame = i; }

                    logic.Tick(dt);
                }

                // 素の画（潰れる前）の明るさ。線の尖頭がこの何倍かが「眩しさ」の目安になる。
                mat.SetFloat("_ScreenCollapse", 0f);
                mat.SetFloat("_ScreenPower", 1f);
                osd.Suppressed = false;
                osd.Tick(FixedTime);
                (float plainMean, float plainPeak, int plainHot) =
                    ShootAndMeasure(cam, Path.Combine(dir, "outro_plain.png"));

                Debug.Log($"[OutroPreview] {Frames + 1} 枚 + 素 1 枚 → Assets/{OutDirRel}/");
                Debug.Log($"[OutroPreview] 尺 潰れ {t.collapseSec:F2}s / 黒 {t.darkSec:F2}s /"
                        + $" 報告 {t.reportFadeSec:F2}s（合計 {t.TotalSec:F2}s）／"
                        + $" 報告の 1 文字目まで {t.collapseSec + t.darkSec:F2}s（旧構成は 7.2s）");
                const float TotalPx = W * (float)H;
                Debug.Log($"[OutroPreview] 素の画 平均 {plainMean:F4} / 尖頭 {plainPeak:F4}"
                        + $" / 白飛び {plainHot} px（{plainHot / TotalPx * 100f:F3}%）");
                Debug.Log($"[OutroPreview] いちばん明るいコマ #{peakFrame} 尖頭 {peakLuma:F4}"
                        + $" ＝ 素の尖頭の {(plainPeak > 1e-5f ? peakLuma / plainPeak : 0f):F2} 倍");
                Debug.Log($"[OutroPreview] 白飛びが最大のコマ #{hotFrame} {hotMax} px"
                        + $"（画面の {hotMax / TotalPx * 100f:F3}% / 素は {plainHot} px）"
                        + "  ⚠ **ここが眩しさの実測** — 素より大きく増えるなら COLLAPSE_GAIN_MAX を下げる");
                Debug.Log($"[OutroPreview] プレート {plateLabel} / post {postLabel}"
                        + $" / OSD {(osdBuilt ? "あり" : "なし")}");
                Debug.Log("[OutroPreview] ⚠ 線の明るさが暗所の VR で眩しくないかは、被って 1 度見るまで"
                        + "確定しない（両眼視差・レンズ・管の曲面は入っていない）");

                // 推移を出す（絵を 31 枚開かずに「一方向か」を追える）。
                // ⚠ **尖頭と平均を並べる** — 平均は面積が減るぶん必ず下がるので、
                //   輝度補償が効いているかは尖頭でしか見えない。
                var sb = new System.Text.StringBuilder("[OutroPreview] 尖頭  : ");
                for (int i = 0; i <= Frames; i += 3) sb.Append($"#{i}={peaks[i]:F3} ");
                sb.Append("\n[OutroPreview] 白飛び: ");
                for (int i = 0; i <= Frames; i += 3) sb.Append($"#{i}={hots[i]} ");
                sb.Append("\n[OutroPreview] 平均  : ");
                for (int i = 0; i <= Frames; i += 3) sb.Append($"#{i}={lumas[i]:F3} ");
                Debug.Log(sb.ToString());
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
                DateTime tm = File.GetLastWriteTimeUtc(f);
                if (tm > best) { best = tm; newest = f; }
            }
            if (newest == null) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true);
            if (!tex.LoadImage(File.ReadAllBytes(newest))) return null;
            tex.filterMode = FilterMode.Trilinear;
            label = Path.GetFileName(newest);
            return tex;
        }

        /// <summary>show.json のトップレベル <c>post</c>（全体グレーディング）を材質へ。</summary>
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

        /// <summary>
        /// 1 枚焼いて、平均輝度・<b>いちばん明るい画素</b>・<b>白飛びした画素の数</b>を返す。
        ///
        /// ⚠⚠ <b>眩しさは平均では測れない。</b> 潰れると光る面積が 1/167 まで減るので、
        /// 輝度補償が何倍掛かっていても<b>平均は必ず下がる</b>（実測: 素 0.169 → 線 0.002）。
        /// 判定に使うのは<b>尖頭と白飛びの画素数</b>で、平均は「一方向に消えていくか」を追うためのもの。
        ///
        /// ⚠ 白飛びの画素数を数えるのは、尖頭だけでは「1 画素だけ飽和」と「線が丸ごと真っ白」を
        /// 区別できないため。暗所の VR で効くのは<b>光っている面積</b>の方。
        /// </summary>
        private static (float mean, float peak, int hot) ShootAndMeasure(Camera cam, string path)
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

                Color32[] px = tex.GetPixels32();
                double sum = 0.0;
                double peak = 0.0;
                int hot = 0;
                for (int i = 0; i < px.Length; i++)
                {
                    double y = (0.299 * px[i].r + 0.587 * px[i].g + 0.114 * px[i].b) / 255.0;
                    sum += y;
                    if (y > peak) peak = y;
                    if (y >= 0.98) hot++;
                }
                return ((float)(sum / px.Length), (float)peak, hot);
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
