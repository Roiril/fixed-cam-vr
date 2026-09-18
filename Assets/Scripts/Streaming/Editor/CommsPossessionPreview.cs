#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using FixedCamVr.Diagnostics;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// 嘘の一文（3 周目 A・侵食度 1）を<b>実物の面</b>で同じ時計で進め、各コマの絵と状態を記録する
    /// （<c>canon/LEDGER.md</c> 0230・憑依の出し方）。3 言語ぶん。
    ///
    /// 出す先は <c>Logs/comms-takeover-20260914/render-&lt;日時&gt;/&lt;ja|en|fr&gt;/</c>（履歴の置き場をそのまま使う）。
    /// 動画と画素検査は <c>tools/render-comms-possession-preview.py</c>。
    /// ここでは状態の側（打鍵 0・全文が出る・赤 4 字・前線の尺・塗り替わった後は全字が切れる）を落とす。
    /// </summary>
    public static class CommsPossessionPreview
    {
        private const int Fps = 30, Width = 1280, Height = 720;
        private static readonly Vector3 Stage = new Vector3(0f, 1000f, 0f);
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        public static void Run()
        {
            if (!EditorCliArgs.EnsureScene("Assets/Scenes/Main.unity"))
                throw new InvalidOperationException("Main scene could not be opened");
            string root = Path.GetFullPath(Path.Combine(Application.dataPath,
                "../Logs/comms-takeover-20260914"));
            string dir = Path.Combine(root, "render-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(dir);
            var restore = ShowLanguage.Current;
            try
            {
                foreach (ShowLang lang in ShowLanguage.All)
                    RenderLanguage(lang, Path.Combine(dir, ShowLanguage.Code(lang)));
                File.WriteAllText(Path.Combine(root, "latest-render.txt"), dir, new UTF8Encoding(false));
                Debug.Log("[CommsPossessionPreview] rendered the possessed lie: " + dir);
            }
            finally { ShowLanguage.Select(restore); }
        }

        private static void RenderLanguage(ShowLang lang, string dir)
        {
            Directory.CreateDirectory(dir);
            ShowLanguage.Select(lang);
            var go = new GameObject("[CommsPossessionPreview] Panel");
            var cameraGo = new GameObject("[CommsPossessionPreview] Camera");
            RenderTexture? rt = null;
            Texture2D? image = null;
            try
            {
                var panel = go.AddComponent<CommsPanel>();
                typeof(CommsPanel).GetMethod("Awake", Private)!.Invoke(panel, null);
                var sound = go.GetComponent<TypeAudioCue>();
                if (sound != null) typeof(TypeAudioCue).GetMethod("Awake", Private)!.Invoke(sound, null);
                var sweepSound = go.GetComponent<CurseSweepAudioCue>();
                if (sweepSound != null) typeof(CurseSweepAudioCue).GetMethod("Awake", Private)!.Invoke(sweepSound, null);
                var logic = (CommsPanelLogic)typeof(CommsPanel).GetField("_logic", Private)!.GetValue(panel);
                var apply = typeof(CommsPanel).GetMethod("Apply", Private)!;
                var text = (TMP_Text)typeof(CommsPanel).GetField("_text", Private)!.GetValue(panel);
                if (!panel.IsBuilt || !panel.PanelBuilt || !panel.AvatarBuilt || panel.FaceArtCount != 2 || !panel.PlateBuilt)
                    throw new InvalidOperationException("Panel, plate shader, font or face art missing");
                if (!panel.SweepSfxBuilt) throw new InvalidOperationException("sfx_glitch clips missing");
                var cam = cameraGo.AddComponent<Camera>();
                cam.nearClipPlane = 0.05f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.055f, 0.052f, 0.050f, 1f);
                cam.fieldOfView = 30f;
                cam.transform.SetPositionAndRotation(Stage, Quaternion.identity);
                rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32);
                image = new Texture2D(Width, Height, TextureFormat.RGB24, false);
                cam.targetTexture = rt;
                panel.SetControllerState(false, false);
                panel.SetDecayForPreview(1f, 0f);
                panel.Deliver(CommsNotice.Takeover);
                go.transform.rotation = Quaternion.identity;
                go.transform.position = Stage + new Vector3(0f, 0f, 1.5f)
                    - (text.transform.position - go.transform.position);
                int previousHits = panel.TypedCount;
                int previousSfx = panel.SweepSfxCount;
                int total = text.textInfo.characterCount;
                int visibleTotal = 0;
                for (int i = 0; i < total; i++) if (text.textInfo.characterInfo[i].isVisible) visibleTotal++;
                // ⚠ 赤くする範囲は**原文の字数**だが、画に出て数えられるのは**絵を持つ字**だけ
                //   （`ApplyGlyphMesh` は `isVisible` の字しか数えない）。English「No anomaly」は
                //   10 字だが空白を除くと 9 字。期待値もそちらで作る。
                int redExpected = 0;
                for (int i = 0; i < CommsPanel.TakeoverDenialPrefixLength(lang) && i < total; i++)
                    if (text.textInfo.characterInfo[i].isVisible) redExpected++;
                var rows = new StringBuilder("frame\tsec\tphase\tsweep\tcurse\tface\tcx\tred\tglyph\tpanel_alpha\thit\tsfx\tfull\tdrawn\tshown\ttotal\tvisible\tx\ty\ttext\ttear\ttorn\n");
                var taps = new StringBuilder("frame\tchars\thit\n");
                int totalHits = 0, totalSfx = 0, count = 0;
                int shownFrames = 0, sweepFrames = 0, cursedFrames = 0;
                int sweepStart = -1, sweepEnd = -1;
                bool shownSaved = false, cursedSaved = false;
                string original = text.text;
                string lastPhase = "";
                for (int i = 0; i < Fps * 14; i++)
                {
                    panel.SetDecayForPreview(1f, i / (float)Fps);
                    apply.Invoke(panel, new object[] { logic.Weights });
                    int hit = panel.TypedCount - previousHits;
                    previousHits = panel.TypedCount;
                    int sfx = panel.SweepSfxCount - previousSfx;
                    previousSfx = panel.SweepSfxCount;
                    totalHits += hit;
                    totalSfx += sfx;
                    string phase = panel.PossessionPhase.ToString();
                    CountInk(text, out int full, out int drawn);
                    string body = text.text.Replace("\n", " / ").Replace("\t", " ");
                    Vector3 screen = cam.WorldToScreenPoint(text.transform.position);
                    rows.AppendFormat(CultureInfo.InvariantCulture,
                        "{0}\t{1:F6}\t{2}\t{3:F4}\t{4:F4}\t{5:F4}\t{6}\t{7}\t{8:F4}\t{9:F4}\t{10}\t{11}\t{12}\t{13}\t{14}\t{15}\t{16}\t{17:F3}\t{18:F3}\t{19}\t{20:F3}\t{21}\n",
                        i, i / (float)Fps, phase, panel.AppliedSweep, panel.AppliedCurse, panel.AppliedFaceMix,
                        panel.CorruptedChars, panel.RedChars, panel.AppliedGlyph, panel.AppliedPanelAlpha,
                        hit, sfx, full, drawn, panel.VisibleChars, total, visibleTotal, screen.x, screen.y, body,
                        panel.AppliedTear, panel.TornBands);
                    taps.Append(i).Append('\t').Append(panel.VisibleChars).Append('\t').Append(hit).Append('\n');
                    byte[] png = Capture(cam, rt, image);
                    File.WriteAllBytes(Path.Combine(dir, $"f{i:0000}.png"), png);
                    if (phase != lastPhase)
                    {
                        File.WriteAllBytes(Path.Combine(dir, "phase-" + phase + ".png"), png);
                        lastPhase = phase;
                    }
                    if (phase == "Shown")
                    {
                        shownFrames++;
                        if (panel.AppliedGlyph >= 0.99f)
                        {
                            if (panel.VisibleChars != total)
                                throw new InvalidOperationException($"Not all characters shown while Shown: {panel.VisibleChars}/{total}");
                            if (panel.RedChars != redExpected)
                                throw new InvalidOperationException($"Red prefix {panel.RedChars}, expected {redExpected}");
                            if (panel.CorruptedChars != 0 || panel.AppliedCurse > 0f || panel.AppliedFaceMix > 0f)
                                throw new InvalidOperationException("The face or the text was cursed before the sweep");
                            if (!shownSaved && shownFrames >= 6)
                            {
                                File.WriteAllBytes(Path.Combine(dir, "shown.png"), png);
                                shownSaved = true;
                            }
                        }
                    }
                    if (phase == "Sweep")
                    {
                        if (sweepStart < 0) sweepStart = i;
                        sweepFrames++;
                        if (panel.AppliedSweep >= 0.20f && !File.Exists(Path.Combine(dir, "sweep-early.png")))
                            File.WriteAllBytes(Path.Combine(dir, "sweep-early.png"), png);
                        if (panel.AppliedSweep >= 0.50f && !File.Exists(Path.Combine(dir, "sweep-mid.png")))
                            File.WriteAllBytes(Path.Combine(dir, "sweep-mid.png"), png);
                        if (panel.AppliedSweep >= 0.78f && !File.Exists(Path.Combine(dir, "sweep-late.png")))
                            File.WriteAllBytes(Path.Combine(dir, "sweep-late.png"), png);
                    }
                    if (phase == "Cursed")
                    {
                        if (sweepEnd < 0) sweepEnd = i;
                        cursedFrames++;
                        // ⚠ 引いている最中は文字の濃さが 0 へ落ちるので、切られた字の数え上げも 0 になる
                        //    （`ApplyGlyphMesh` は「画に出ている字」だけを数える）。判定は**まだ点いているコマ**に絞る。
                        if (panel.AppliedGlyph > 0.5f
                            && (panel.CorruptedChars != visibleTotal || panel.RedChars != 0 || panel.AppliedCurse < 0.999f
                                || panel.AppliedFaceMix < 0.999f || drawn != 0))
                            throw new InvalidOperationException(
                                $"Cursed frame {i}: cx={panel.CorruptedChars}/{visibleTotal} red={panel.RedChars} curse={panel.AppliedCurse} drawn={drawn}");
                        if (!cursedSaved && cursedFrames >= 4 && panel.AppliedGlyph > 0.5f)
                        {
                            File.WriteAllBytes(Path.Combine(dir, "cursed.png"), png);
                            cursedSaved = true;
                        }
                    }
                    if (text.text != original)
                        throw new InvalidOperationException("A second sentence was introduced");
                    count++;
                    if (!logic.Active) break;
                    logic.Tick(1f / Fps);
                }
                File.WriteAllText(Path.Combine(dir, "frames.tsv"), rows.ToString(), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(dir, "type.tsv"), taps.ToString(), new UTF8Encoding(false));
                if (logic.Active) throw new InvalidOperationException("The panel never closed");
                if (totalHits != 0) throw new InvalidOperationException($"Keystrokes {totalHits}, expected 0 (shown all at once)");
                if (totalSfx != 1) throw new InvalidOperationException($"Sweep sound {totalSfx}, expected exactly 1");
                if (!shownSaved || sweepStart < 0 || sweepEnd < 0 || !cursedSaved)
                    throw new InvalidOperationException("Shown → Sweep → Cursed did not all happen");
                float sweepSec = sweepFrames / (float)Fps;
                if (Math.Abs(sweepSec - CommsPossessionLogic.SweepSec) > 2f / Fps)
                    throw new InvalidOperationException($"Sweep took {sweepSec:F2}s, expected {CommsPossessionLogic.SweepSec:F2}s");
                Debug.Log($"[CommsPossessionPreview] {ShowLanguage.Code(lang)} frames={count} typed={totalHits} "
                    + $"shownFrames={shownFrames} ({shownFrames / (float)Fps:F2}s) sweepFrames={sweepFrames} ({sweepSec:F2}s) "
                    + $"cursedFrames={cursedFrames} red={redExpected} visible={visibleTotal} sfx={totalSfx}");
            }
            finally
            {
                if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
                if (image != null) Object.DestroyImmediate(image);
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(go);
            }
        }

        private static byte[] Capture(Camera cam, RenderTexture rt, Texture2D image)
        {
            RenderTexture previous = RenderTexture.active;
            try
            {
                cam.Render();
                RenderTexture.active = rt;
                image.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                image.Apply();
                return image.EncodeToPNG();
            }
            finally { RenderTexture.active = previous; }
        }

        private static void CountInk(TMP_Text text, out int full, out int drawn)
        {
            full = drawn = 0;
            var info = text.textInfo;
            for (int i = 0; i < info.characterCount; i++)
            {
                var ch = info.characterInfo[i];
                if (!ch.isVisible) continue;
                var colors = info.meshInfo[ch.materialReferenceIndex].colors32;
                int minimum = 255, maximum = 0;
                for (int k = 0; k < 4; k++)
                {
                    int alpha = colors[ch.vertexIndex + k].a;
                    minimum = Math.Min(minimum, alpha);
                    maximum = Math.Max(maximum, alpha);
                }
                if (minimum >= 250) full++;
                if (maximum > 2) drawn++;
            }
        }
    }
}
