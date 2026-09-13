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
                var rows = new StringBuilder("frame\tsec\tphase\terase\tlie\tface\tmissing\thit\tfull\tdrawn\tx\ty\ttext\n");
                var taps = new StringBuilder("frame\tchars\thit\n");
                int totalHits = 0, truthFrames = 0, lieFrames = 0;
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
                    rows.AppendFormat(CultureInfo.InvariantCulture,
                        "{0}\t{1:F6}\t{2}\t{3:F4}\t{4:F4}\t{5:F4}\t{6}\t{7}\t{8}\t{9}\t{10:F3}\t{11:F3}\t{12}\n",
                        i, i / (float)Fps, phase, panel.AppliedTakeoverErase, panel.AppliedTakeoverLie,
                        panel.AppliedFaceMix, panel.CorruptedChars, hit, full, drawn, screen.x, screen.y, body);
                    taps.Append(i).Append('\t').Append(panel.VisibleChars).Append('\t').Append(hit).Append('\n');
                    byte[] png = Capture(cam, rt, image);
                    File.WriteAllBytes(Path.Combine(dir, $"f{i:0000}.png"), png);
                    if (phase != lastPhase)
                    {
                        File.WriteAllBytes(Path.Combine(dir, "phase-" + phase + ".png"), png);
                        lastPhase = phase;
                    }
                    if (phase == "Truth" && full == intended)
                    {
                        truthFrames++;
                        if (truthFrames == 20) File.WriteAllBytes(Path.Combine(dir, "truth-readable.png"), png);
                    }
                    if (phase == "Erase" && panel.AppliedTakeoverErase > 0.46f && panel.AppliedTakeoverErase < 0.52f)
                        File.WriteAllBytes(Path.Combine(dir, "erasing.png"), png);
                    if (phase == "LieHold")
                    {
                        lieFrames++;
                        if (lieFrames == 10) File.WriteAllBytes(Path.Combine(dir, "lie-readable.png"), png);
                        if (lieFrames == 40) File.WriteAllBytes(Path.Combine(dir, "lie-still.png"), png);
                        if (hit != 0) throw new InvalidOperationException("The concealed report produced a keystroke");
                    }
                    count++;
                    if (!logic.Active) break;
                    logic.Tick(1f / Fps);
                }
                File.WriteAllText(Path.Combine(dir, "frames.tsv"), rows.ToString(), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(dir, "type.tsv"), taps.ToString(), new UTF8Encoding(false));
                if (logic.Active || truthFrames < Fps || lieFrames < Fps)
                    throw new InvalidOperationException("Takeover did not finish with readable truth and lie");
                if (totalHits != intended)
                    throw new InvalidOperationException($"Keystrokes {totalHits}, expected visible truth glyphs {intended}");
                Debug.Log($"[CommsTakeoverPreview] {ShowLanguage.Code(lang)} frames={count} "
                    + $"typed={totalHits}/{intended} truthFrames={truthFrames} lieFrames={lieFrames} "
                    + $"completed={panel.TakeoverCompletedCount}");
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
