#nullable enable
using System;
using System.IO;
using System.Reflection;
using System.Text;
using FixedCamVr.Diagnostics;
using TMPro;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>現行の通信面と空間エラーを実描画し、途中解除も同じメッシュで確かめる。</summary>
    public static class CommsRevisionPreview
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private const int Fps = 30;
        public static void Run()
        {
            string root = Path.GetFullPath(EditorCliArgs.Get("out") ?? "Logs/comms-story-20260921");
            Directory.CreateDirectory(root);
            var previous = ShowLanguage.Current;
            try
            {
                foreach (ShowLang lang in ShowLanguage.All) Render(lang, Path.Combine(root, ShowLanguage.Code(lang)));
            }
            finally { ShowLanguage.Select(previous); }
            Debug.Log("[CommsRevisionPreview] " + root);
        }

        private static void Render(ShowLang lang, string dir)
        {
            Directory.CreateDirectory(dir);
            ShowLanguage.Select(lang);
            var go = new GameObject("Revision preview panel");
            var camGo = new GameObject("Revision preview camera");
            var anchor = new GameObject("Revision preview main screen");
            var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32);
            var pixels = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            try
            {
                var panel = go.AddComponent<CommsPanel>();
                typeof(CommsPanel).GetMethod("Awake", Hidden)!.Invoke(panel, null);
                var logic = (CommsPanelLogic)typeof(CommsPanel).GetField("_logic", Hidden)!.GetValue(panel);
                var apply = typeof(CommsPanel).GetMethod("Apply", Hidden)!;
                var text = (TMP_Text)typeof(CommsPanel).GetField("_text", Hidden)!.GetValue(panel);
                if (!panel.IsBuilt || !panel.AvatarBuilt || !panel.PlateBuilt)
                    throw new InvalidOperationException("Panel assets are missing");
                var camera = camGo.AddComponent<Camera>();
                camera.transform.position = new Vector3(0, 1000, 0);
                camera.nearClipPlane = .05f;
                camera.fieldOfView = 45f;
                camera.aspect = 1280f / 720f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.045f, .045f, .05f, 1f);
                camera.targetTexture = rt;
                panel.SetControllerState(false, false);
                void Apply() => apply.Invoke(panel, new object[] { logic.Weights });
                void Step(float seconds)
                {
                    for (int i = 0; i < Mathf.CeilToInt(seconds * Fps); i++)
                    { logic.Tick(1f / Fps); Apply(); }
                }
                void Place()
                {
                    go.transform.rotation = Quaternion.identity;
                    go.transform.position = camera.transform.position + new Vector3(0, 0, 1.5f)
                        - (text.transform.position - go.transform.position);
                }
                void Shot(string name)
                {
                    var old = RenderTexture.active;
                    try
                    {
                        camera.Render(); RenderTexture.active = rt;
                        pixels.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); pixels.Apply();
                        File.WriteAllBytes(Path.Combine(dir, name + ".png"), pixels.EncodeToPNG());
                    }
                    finally { RenderTexture.active = old; }
                }
                panel.SetDecayForPreview(0f, 0f);
                panel.Deliver(CommsNotice.TutorialAccepted); Place();
                Step(CommsPanelLogic.InSec + CommsPanelLogic.FadeInSec + .1f); Shot("intro-success");
                logic.Disable();
                panel.SetDecayForPreview(1f, 0f);
                panel.Deliver(CommsNotice.MarkLogged); Place();
                Step(CommsPanelLogic.InSec + .2f); Shot("after-takeover");
                panel.SetControllerState(true, true);
                panel.SetMarkState(.6f, false);
                logic.SetGuideWanted(true); Step(.5f); Shot("holding");
                if (panel.LieChars != 0 || panel.VisibleChars != 0)
                    throw new InvalidOperationException("Holding revived the old report");
                panel.SetMarkState(0f, false);
                logic.SetGuideWanted(false); Apply(); Shot("cancel-immediate");
                if (logic.Weights.reveal > .001f) throw new InvalidOperationException("Cancelled hold revived the old report");
                if (panel.LieChars != 0 || panel.VisibleChars != 0)
                    throw new InvalidOperationException("Cancelled hold rendered the old report");
                Step(CommsPanelLogic.OutSec + .1f); Shot("cancel-closed");
                if (logic.Active) throw new InvalidOperationException("Cancelled panel did not close");
                panel.SetControllerState(false, false);

                panel.Deliver(CommsNotice.Takeover); Place();
                anchor.transform.position = camera.transform.position + new Vector3(0, 0, 3f);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Effects/UnauthorizedAccess.prefab");
                var sync = go.GetComponent<CommsTakeoverError>();
                if (sync == null) sync = go.AddComponent<CommsTakeoverError>();
                sync.Configure(panel, prefab);
                sync.ConfigureScreen(anchor.transform, new Vector2(2.7f, 1.51875f));
                sync.Tick(panel.TakeoverVisible, 0f);
                var error = (UnauthorizedAccessEffect)typeof(CommsTakeoverError)
                    .GetField("_effect", Hidden)!.GetValue(sync);
                var data = new StringBuilder("frame,sec,stage,sweep,lie,glyph,error,panel,errorOpacity,blockFailed\n");
                bool savedIntrusion = false, savedFailure = false;
                bool savedTruth = false, savedMid = false, savedFinal = false;
                int frames = 0;
                for (int i = 0; i < Fps * 12; i++)
                {
                    Apply();
                    sync.Tick(panel.TakeoverVisible, 1f / Fps);
                    Shot($"f{i:0000}"); frames++;
                    data.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                        "{0},{1:F3},{2},{3:F3},{4},{5:F3},{6},{7:F3},{8:F3},{9}\n", i, i / (float)Fps,
                        panel.Stage, panel.AppliedSweep, panel.LieChars, panel.AppliedGlyph, error.IsPlaying ? 1 : 0,
                        panel.AppliedPanelAlpha, panel.TakeoverErrorOpacity, panel.TakeoverBlockFailed ? 1 : 0);
                    if (!savedIntrusion && i / (float)Fps >= .75f)
                    {
                        if (panel.AppliedPanelAlpha > .001f || panel.VisibleChars != 0)
                            throw new InvalidOperationException("Sui appeared before the intrusion warning");
                        Shot("intrusion"); savedIntrusion = true;
                    }
                    if (!savedFailure && panel.TakeoverBlockFailed)
                    { Shot("block-failed"); savedFailure = true; }
                    if (!savedTruth && panel.PossessionPhase == CommsPossessionPhase.Shown && panel.AppliedGlyph > .99f)
                    { Shot("truth-with-error"); error.Stop(); Shot("truth"); error.Play(anchor.transform, new Vector2(2.7f, 1.51875f)); savedTruth = true; }
                    if (!savedMid && panel.AppliedSweep >= .65f && panel.AppliedSweep < 1f)
                    { Shot("wipe-with-error"); error.Stop(); Shot("wipe"); error.Play(anchor.transform, new Vector2(2.7f, 1.51875f)); savedMid = true; }
                    if (!savedFinal && panel.AppliedSweep >= .999f && panel.AppliedGlyph > .99f)
                    { Shot("doll-with-error"); error.Stop(); Shot("doll"); error.Play(anchor.transform, new Vector2(2.7f, 1.51875f)); savedFinal = true; }
                    if (!logic.Active) { Shot("finished"); break; }
                    logic.Tick(1f / Fps);
                }
                File.WriteAllText(Path.Combine(dir, "frames.csv"), data.ToString(), new UTF8Encoding(false));
                if (!savedIntrusion || !savedFailure || !savedTruth || !savedMid || !savedFinal || logic.Active || error.IsPlaying)
                    throw new InvalidOperationException("Takeover did not render and close completely");
                Debug.Log($"[CommsRevisionPreview] lang={lang} frames={frames} truth=1 wipe=1 doll=1 closed=1");
            }
            finally
            {
                Object.DestroyImmediate(anchor); Object.DestroyImmediate(go); Object.DestroyImmediate(camGo);
                rt.Release(); Object.DestroyImmediate(rt); Object.DestroyImmediate(pixels);
            }
        }
    }
}
