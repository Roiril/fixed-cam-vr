#nullable enable
using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// <b>スクリーン左上の時刻表示（OSD）を状態ごとに焼く</b>（<c>canon/LEDGER.md</c> 0108）。
    ///
    /// ⚠ <b>実シェーダ <c>FixedCamVr/ScreenComposite</c> と実実装</b>（<see cref="ScreenOsd"/> ＋
    /// <see cref="OsdClockLogic"/>）を通す。矩形の式もセルの敷き直しも模写していないので、
    /// ここで見える大きさ・位置・沈み方は実機と同じ式。
    ///
    /// <b>見るのは 2 つ</b>:
    /// <list type="number">
    /// <item><b>装置に起きることでは一緒に沈み、映像に起きることでは変わらない</b>
    ///       — 砂嵐・乱れ・劣化の上でも鮮明なまま／切替の黒と終幕の電力では一緒に落ちる。</item>
    /// <item><b>左上の余白が管の縁の暗さ・角の丸みに食われていないか</b>
    ///       （<c>_CrtEdgeWidth</c> は枠の 17% もある）。</item>
    /// </list>
    ///
    /// ⚠ <b>実機の見え方そのものではない</b> — 両眼視差・レンズ・管の曲面は入っていない。
    /// <b>1 字 1.0° が実際に読めるかは被って確かめる</b>（見かけ角の机上見積もりは
    /// 2 回続けて外した前歴がある — <c>memory/hmd_text_style.md</c>）。
    ///
    /// 使い方: <c>.\tools\unity.ps1 menu osd</c>
    /// </summary>
    public static class OsdPreview
    {
        private const string OutDirRel = "Screenshots/osd-preview";
        private const int W = 960, H = 540;

        /// <summary>撮影台。他の scene 幾何が写り込まないよう、誰も居ない高さへ。</summary>
        private static readonly Vector3 Stage = new Vector3(0f, 2000f, 0f);

        /// <summary>
        /// 焼く時刻。**固定**（決定論のため）。⚠ 実機は端末の実時刻を使う
        /// （<c>canon/OPEN.md</c> Q12）。ここへ実時刻を持ち込むと、同じ版が毎回違う絵になる。
        /// </summary>
        private static readonly DateTime Fixed = new DateTime(2026, 9, 6, 14, 23, 45);

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Screen OSD", priority = 234)]
        public static void Run()
        {
            string dir = Path.Combine(Application.dataPath, OutDirRel);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);

            Shader? sh = Shader.Find("FixedCamVr/ScreenComposite");
            if (sh == null)
            {
                Debug.LogError("[OsdPreview] シェーダ FixedCamVr/ScreenComposite が見つかりません");
                return;
            }

            Texture2D? plate = LoadNewestPlate(out string plateLabel);
            if (plate == null)
            {
                Debug.LogError("[OsdPreview] カメラ A のプレートが見つかりません"
                             + "（tools/web-compositor/captures/plate_A_*.jpg）");
                return;
            }

            var camGo = new GameObject("[OsdPreview] Camera");
            var quadGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quadGo.name = "[OsdPreview] Screen";
            var mat = new Material(sh) { name = "ScreenComposite (osd preview)" };
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
                // 管の面（本編と同じ値）。**左上の余白がここに食われないか**を見るのが主目的の 1 つ。
                mat.SetFloat("_CrtRound", CameraFeelFx.CrtRound);
                mat.SetFloat("_CrtEdge", CameraFeelFx.CrtEdge);
                mat.SetFloat("_CrtEdgeWidth", CameraFeelFx.CrtEdgeWidth);
                ApplyGlobalPost(mat, out string postLabel);

                quadGo.transform.SetPositionAndRotation(Stage, Quaternion.identity);
                quadGo.transform.localScale = new Vector3(frameAspect, 1f, 1f);
                quadGo.GetComponent<Renderer>().sharedMaterial = mat;

                // ⚠ 実装をそのまま通す（矩形の式も敷き直しも模写しない）。
                //   Edit モードでは Awake が走らないので、材質を直接渡す口を使う。
                var osd = quadGo.AddComponent<ScreenOsd>();
                osd.BindForPreview(mat, new Vector2(liveScale.x, liveScale.y));
                osd.SetShowState(1, ShowRunDefaults.TotalLaps, otherworld: false);
                osd.Tick(Fixed);
                if (!osd.Built)
                {
                    Debug.LogError("[OsdPreview] 版を掴めませんでした"
                                 + "（py -3.11 tools/make-osd-font.py で焼く）");
                    return;
                }

                var cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;   // 曲面もパースも入れない（大きさと位置だけを見る）
                cam.orthographicSize = 0.5f;
                cam.nearClipPlane = 0.01f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.transform.SetPositionAndRotation(Stage + new Vector3(0f, 0f, -1f),
                                                     Quaternion.identity);

                // ⚠⚠ **状態は「装置に起きること」と「映像に起きること」を必ず両方入れる。**
                //   時計が正しい層に入っているかは、片方だけ見ても判定できない。
                var shots = new (string name, string desc, Action<Material> set,
                                 int lap, bool otherworld, ShowLang lang)[]
                {
                    ("0_plain", "素（本編・1 周目）", m => { }, 1, false, ShowLang.Ja),
                    ("1_static", "信号断（砂嵐）— 映像側。時計は鮮明なまま",
                        m => m.SetFloat("_SignalLost", 1f), 1, false, ShowLang.Ja),
                    ("2_glitch", "乱れ最大 0.60 — 映像側。時計は微動もしない",
                        m => { m.SetFloat("_Glitch", 0.60f); m.SetFloat("_GlitchSeed", 3.1f); },
                        2, false, ShowLang.Ja),
                    ("3_decay", "3 周目相当（粗い・夜間モード）— 映像側。時計は鮮明なまま",
                        m => { m.SetFloat("_CoarseBlocks", 96f); m.SetFloat("_Mono", 1f); },
                        3, false, ShowLang.Ja),
                    ("4_dip", "切替の黒 0.6 — 装置側。時計も一緒に沈む",
                        m => m.SetFloat("_SwitchDim", 0.6f), 1, false, ShowLang.Ja),
                    ("5_outro", "終幕の電力 0.35 — 装置側。時計も一緒に落ちる",
                        m => m.SetFloat("_ScreenPower", 0.35f), 4, false, ShowLang.Ja),
                    ("6_intro_frame", "導入 段 4（映像が出てくる途中）",
                        m => m.SetFloat("_IntroLive", 0.5f), -1, false, ShowLang.Ja),
                    ("7_intro_dark", "導入 段 0〜3（まだ何も映していない管）— **時計は出ない**",
                        m => m.SetFloat("_IntroLive", 0f), -1, false, ShowLang.Ja),
                    // 周回（canon/LEDGER.md 0167）。ここから先は**表示の側**を切り替える。
                    ("8_lap_last", "帰りの区間（4-A）＝「最後」。4 周目とは書かない",
                        m => { }, 4, false, ShowLang.Ja),
                    ("9_lap_none", "区間がまだ確定していない（導入）＝ 周回は空のまま",
                        m => { }, -1, false, ShowLang.Ja),
                    ("10_otherworld", "別の場所（バックルームズ）が映っている — 時刻も周回も ?",
                        m => { }, 2, true, ShowLang.Ja),
                    // 言語（canon/LEDGER.md 0127 の表に周回を足した）。⚠ 時刻は訳さない（数字）。
                    ("11_en", "English — LAP 1", m => { }, 1, false, ShowLang.En),
                    ("12_en_last", "English — LAST", m => { }, 4, false, ShowLang.En),
                    ("13_fr", "Français — TOUR 1（欄いっぱいの 6 セル）",
                        m => { }, 1, false, ShowLang.Fr),
                    ("14_fr_last", "Français — FIN", m => { }, 4, false, ShowLang.Fr),
                };

                foreach (var (name, desc, set, lap, otherworld, lang) in shots)
                {
                    ResetState(mat);
                    set(mat);
                    osd.SetShowState(lap, ShowRunDefaults.TotalLaps, otherworld, lang);
                    osd.Tick(Fixed);
                    Shoot(cam, Path.Combine(dir, $"osd_{name}.png"));
                    Debug.Log($"[OsdPreview] {name}  {desc}");
                }
                ResetState(mat);

                // 矩形を数値でも出す（絵だけだと「少しずれている」を追えない）。
                Vector4 rect = mat.GetVector("_OsdRect");
                // スクリーンの見かけ: 水平 ±30.6° / 垂直 ±18.4°（本編の実測）
                const float vFovDeg = 36.8f, hFovDeg = 61.2f;
                float cellDeg = rect.w * vFovDeg;
                Debug.Log($"[OsdPreview] {shots.Length} 枚 → Assets/{OutDirRel}/\n"
                        + $"  文字列 {OsdClockLogic.Format(Fixed)}  {OsdClockLogic.LapLabel(1, 3)}"
                        + $"（時刻 {OsdClockLogic.TextLength} ＋ 空き {OsdClockLogic.GapCells}"
                        + $" ＋ 周回 {OsdClockLogic.LabelCells} ＝ {OsdClockLogic.CellCount} セル）\n"
                        + $"  矩形 x={rect.x:F3} y={rect.y:F3} w={rect.z:F3} h={rect.w:F3}（枠 UV）\n"
                        + $"  1 セル {cellDeg:F2}° ＝ 字の高さ 約 {cellDeg / 1.19f:F2}°"
                        + $" / 全幅 {rect.z * hFovDeg:F1}°（枠幅の {rect.z * 100f:F0}%）\n"
                        + $"  余白 上 {ScreenOsd.MarginK * vFovDeg:F2}° — 管の縁は枠の"
                        + $" {CameraFeelFx.CrtEdgeWidth * 100f:F0}% まで暗い\n"
                        + $"  プレート {plateLabel} / post {postLabel}\n"
                        + $"  ⚠ 両眼視差・レンズ・管の曲面は入っていない。読めるかは実機で確かめる");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(camGo);
                UnityEngine.Object.DestroyImmediate(quadGo);
                UnityEngine.Object.DestroyImmediate(mat);
            }
        }

        /// <summary>別系統の uniform を既定へ戻す（前の 1 枚が次へ漏れない）。</summary>
        private static void ResetState(Material m)
        {
            m.SetFloat("_SignalLost", 0f);
            // この 8 枚はプレート（＝ 最後のフレーム）の上に描くので、砂の下に画がある。
            // ⚠ 材質の既定は 1（画が無い側）なので、明示的に 0 へ戻さないと地が持ち上がる。
            m.SetFloat("_SignalFloor", 0f);
            m.SetFloat("_Glitch", 0f);
            m.SetFloat("_CoarseBlocks", 0f);
            m.SetFloat("_Mono", 0f);
            m.SetFloat("_SwitchDim", 0f);
            m.SetFloat("_ScreenPower", 1f);
            m.SetFloat("_IntroLive", 1f);
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
        /// ⚠ 素の絵で見ると明るすぎて、暗い実機での読みやすさを判断できない。
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
