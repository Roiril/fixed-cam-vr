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
    /// <summary>実物の面を同じ時計で進め、字形の描画と音の発火を各コマに記録する。</summary>
    public static class CommsTakeoverPreview
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
                Debug.Log("[CommsTakeoverPreview] rendered actual glyphs and audio events: " + dir);
            }
            finally { ShowLanguage.Select(restore); }
        }

        private static void RenderLanguage(ShowLang lang, string dir)
        {
            Directory.CreateDirectory(dir);
            ShowLanguage.Select(lang);
            var go = new GameObject("[CommsTakeoverPreview] Panel");
            var cameraGo = new GameObject("[CommsTakeoverPreview] Camera");
            RenderTexture? rt = null;
            Texture2D? image = null;
            try
            {
                var panel = go.AddComponent<CommsPanel>();
                typeof(CommsPanel).GetMethod("Awake", Private)!.Invoke(panel, null);
                var sound = go.GetComponent<TypeAudioCue>();
                if (sound != null) typeof(TypeAudioCue).GetMethod("Awake", Private)!.Invoke(sound, null);
                var logic = (CommsPanelLogic)typeof(CommsPanel).GetField("_logic", Private)!.GetValue(panel);
                var apply = typeof(CommsPanel).GetMethod("Apply", Private)!;
                var text = (TMP_Text)typeof(CommsPanel).GetField("_text", Private)!.GetValue(panel);
                if (!panel.IsBuilt || !panel.PanelBuilt || !panel.AvatarBuilt || panel.FaceArtCount != 2)
                    throw new InvalidOperationException("Panel, font or face art missing");
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
                int intended = panel.NoticeChars;
                int previousHits = panel.TypedCount;
                var rows = new StringBuilder("frame\tsec\tprogress\tphase\terase\treveal\tstrain\tresistance\tcollapse\tface\tmissing\tdeformed\tbottom_y\tpanel_alpha\thit\tfull\tdrawn\tgenerated\ttotal\texpected\tcut\tx\ty\ttext\n");
                var taps = new StringBuilder("frame\tchars\thit\n");
                int totalHits = 0, overlapFrames = 0, blankFrames = 0;
                int lastGenerated = 0, overlapGrowth = 0;
                byte[]? resistanceA = null, resistanceB = null;
                bool midSaved = false;
                string original = text.text;
                int total = text.textInfo.characterCount;
                string lastPhase = "";
                int count = 0;
                for (int i = 0; i < Fps * 14; i++)
                {
                    panel.SetDecayForPreview(1f, i / (float)Fps);
                    apply.Invoke(panel, new object[] { logic.Weights });
                    int hit = panel.TypedCount - previousHits;
                    previousHits = panel.TypedCount;
                    totalHits += hit;
                    string phase = panel.TakeoverPhase.ToString();
                    CountInk(text, out int full, out int drawn);
                    string body = text.text.Replace("\n", " / ").Replace("\t", " ");
                    Vector3 screen = cam.WorldToScreenPoint(text.transform.position);
                    float progress = phase == "Off" || logic.TypeSec <= 0f ? 0f
                        : Mathf.Clamp01(logic.StageElapsedSec / logic.TypeSec);
                    float bottomY = MeasureBottom(text, panel.VisibleChars);
                    rows.AppendFormat(CultureInfo.InvariantCulture,
                        "{0}\t{1:F6}\t{2:F6}\t{3}\t{4:F4}\t{5:F4}\t{6:F4}\t{7}\t{8:F4}\t{9:F4}\t{10}\t{11}\t{12:F6}\t{13:F4}\t{14}\t{15}\t{16}\t{17}\t{18}\t{19}\t{20}\t{21:F3}\t{22:F3}\t{23}\n",
                        i, i / (float)Fps, progress, phase, panel.AppliedTakeoverErase,
                        panel.AppliedTakeoverReveal, panel.AppliedTakeoverStrain,
                        panel.TakeoverResistance ? 1 : 0, panel.AppliedTakeoverCollapse,
                        panel.AppliedFace, panel.CorruptedChars, panel.TakeoverDeformedChars,
                        bottomY, panel.AppliedPanelAlpha, hit, full, drawn, panel.VisibleChars,
                        total, intended, panel.TakeoverCutCount, screen.x, screen.y, body);
                    taps.Append(i).Append('\t').Append(panel.VisibleChars).Append('\t').Append(hit).Append('\n');
                    byte[] png = Capture(cam, rt, image);
                    File.WriteAllBytes(Path.Combine(dir, $"f{i:0000}.png"), png);
                    if (phase != lastPhase)
                    {
                        File.WriteAllBytes(Path.Combine(dir, "phase-" + phase + ".png"), png);
                        lastPhase = phase;
                    }
                    if (phase == "Output" && full >= 3)
                        File.WriteAllBytes(Path.Combine(dir, "output.png"), png);
                    if (phase == "Pursuit" && panel.CorruptedChars > 0 && drawn > 0)
                    {
                        overlapFrames++;
                        if (panel.VisibleChars > lastGenerated) overlapGrowth++;
                        if (overlapFrames == 3) File.WriteAllBytes(Path.Combine(dir, "overlap-early.png"), png);
                        File.WriteAllBytes(Path.Combine(dir, "overlap-late.png"), png);
                    }
                    if (!midSaved && phase == "Pursuit" && !panel.TakeoverResistance
                        && panel.AppliedTakeoverStrain >= 0.30f)
                    {
                        File.WriteAllBytes(Path.Combine(dir, "mid-deformation.png"), png);
                        midSaved = true;
                    }
                    if (panel.TakeoverResistance && progress >= 0.75f && resistanceA == null)
                    {
                        resistanceA = png;
                        File.WriteAllBytes(Path.Combine(dir, "resistance-a.png"), png);
                    }
                    if (panel.TakeoverResistance && progress >= 0.81f && resistanceB == null)
                    {
                        resistanceB = png;
                        File.WriteAllBytes(Path.Combine(dir, "resistance-b.png"), png);
                    }
                    if (phase == "Seized")
                    {
                        blankFrames++;
                        if (blankFrames == 4) File.WriteAllBytes(Path.Combine(dir, "seized.png"), png);
                        if (hit != 0 || drawn != 0)
                            throw new InvalidOperationException("The seized output resumed");
                    }
                    if (text.text != original || panel.VisibleChars >= total)
                        throw new InvalidOperationException("The source finished or a second sentence was introduced");
                    lastGenerated = panel.VisibleChars;
                    count++;
                    if (!logic.Active) break;
                    logic.Tick(1f / Fps);
                }
                File.WriteAllText(Path.Combine(dir, "frames.tsv"), rows.ToString(), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(dir, "type.tsv"), taps.ToString(), new UTF8Encoding(false));
                if (logic.Active || overlapFrames < 6 || overlapGrowth < 2 || blankFrames < 4
                    || !midSaved || resistanceA == null || resistanceB == null)
                    throw new InvalidOperationException("Output did not coexist with erasure or fail irreversibly");
                if (!SameBytes(resistanceA!, resistanceB!))
                    throw new InvalidOperationException("Resistance frames changed while the output clock was stopped");
                if (totalHits != intended)
                    throw new InvalidOperationException($"Keystrokes {totalHits}, expected generated glyphs {intended}");
                Debug.Log($"[CommsTakeoverPreview] {ShowLanguage.Code(lang)} frames={count} "
                    + $"typed={totalHits}/{intended} overlapFrames={overlapFrames} overlapGrowth={overlapGrowth} "
                    + $"blankFrames={blankFrames} cuts={panel.TakeoverCutCount} completed={panel.TakeoverCompletedCount}");
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

        private static float MeasureBottom(TMP_Text text, int generated)
        {
            float bottom = float.PositiveInfinity;
            var info = text.textInfo;
            for (int i = 0; i < info.characterCount && i < generated; i++)
            {
                var ch = info.characterInfo[i];
                if (!ch.isVisible) continue;
                Vector3[] vertices = info.meshInfo[ch.materialReferenceIndex].vertices;
                for (int k = 0; k < 4; k++)
                    bottom = Mathf.Min(bottom, text.transform.localPosition.y
                        + vertices[ch.vertexIndex + k].y * text.transform.localScale.y);
            }
            return float.IsPositiveInfinity(bottom) ? 0f : bottom;
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }
}
