#nullable enable

using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// 題字の出（A で呼び出してから 3 秒）を 60fps の連番で撮る（2026-10-14）。
    /// 実物の <see cref="TitleScreen"/> と <see cref="TitleLogic"/> をそのまま回す。
    ///
    /// 使い方: <c>.\tools\unity.ps1 menu titlereveal -Set tag=after</c>
    /// → <c>Logs/title-reveal/&lt;時刻&gt;_&lt;tag&gt;/f0000.png…</c> と <c>timeline.tsv</c>（刻みごとの reveal / wet）。
    /// 動画は <c>py -3.11 tools/title-reveal-video.py &lt;dir&gt; [&lt;before&gt;]</c>（音 <c>sfx_title_in</c> を呼び出しの縁に重ねる）。
    ///
    /// ⚠ 公開されている口だけで組む（修正前のコードでも同じ条件で撮れるように）。
    /// ⚠ 画角は題字を大きく見るために狭めてある（縦 46°）。大きさの判断は <see cref="TitlePreview"/>（縦 82°）で。
    /// </summary>
    public static class TitleRevealPreview
    {
        private const int Width = 1600;
        private const int Height = 900;
        private const float Fps = 60f;
        private const float PreSec = 0.15f;
        private const float PostSec = 3.0f;
        private const float FovDeg = 46f;

        [MenuItem("Tools/FixedCamVr/Diagnostics/Title Reveal Sequence", priority = 238)]
        public static void Run()
        {
            string tag = EditorCliArgs.Get("tag") ?? "";
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string dir = Path.Combine("Logs", "title-reveal", tag.Length > 0 ? $"{stamp}_{tag}" : stamp);
            Directory.CreateDirectory(dir);

            var camGo = new GameObject("[TitleRevealCam]") { hideFlags = HideFlags.HideAndDontSave };
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.14f, 0.14f, 0.135f, 1f);
            cam.fieldOfView = FovDeg;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 50f;
            cam.transform.SetPositionAndRotation(new Vector3(0f, 1.6f, 0f), Quaternion.identity);

            var titleGo = new GameObject("[TitleReveal]") { hideFlags = HideFlags.HideAndDontSave };
            titleGo.transform.SetPositionAndRotation(cam.transform.position, cam.transform.rotation);
            var title = titleGo.AddComponent<TitleScreen>();
            title.EditorBuild();
            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            { antiAliasing = 4 };
            var resolved = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            try
            {
                if (!title.IsBuilt)
                {
                    Debug.LogError("[TitleReveal] タイトルの実体を組めない（シェーダか版が無い）");
                    return;
                }
                var l = new TitleLogic();
                l.Begin();
                var input = new TitleInput { concealReady = true };
                float dt = 1f / Fps;
                int frames = Mathf.RoundToInt((PreSec + PostSec) * Fps);
                var tsv = new System.Text.StringBuilder("frame\tt\tstage\treveal\tglyph\tpeak\n");
                int peakMax = 0;
                for (int f = 0; f < frames; f++)
                {
                    float t = f * dt - PreSec;
                    if (f == Mathf.RoundToInt(PreSec * Fps)) l.RequestAdvance();   // A で呼び出す縁（音もここ）
                    TitleWeights w = l.Weights;
                    title.Apply(w, Mathf.Max(0f, t));
                    cam.targetTexture = rt;
                    cam.Render();
                    Graphics.Blit(rt, resolved);
                    RenderTexture prev = RenderTexture.active;
                    RenderTexture.active = resolved;
                    tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                    tex.Apply();
                    RenderTexture.active = prev;
                    cam.targetTexture = null;
                    File.WriteAllBytes(Path.Combine(dir, $"f{f:D4}.png"), tex.EncodeToPNG());
                    int peak = 0;
                    Color32[] px = tex.GetPixels32();
                    for (int k = 0; k < px.Length; k += 11) peak = Mathf.Max(peak, Mathf.Max(px[k].r, Mathf.Max(px[k].g, px[k].b)));
                    peakMax = Mathf.Max(peakMax, peak);
                    tsv.Append(f).Append('\t').Append(t.ToString("F4", CultureInfo.InvariantCulture)).Append('\t')
                       .Append(l.Stage).Append('\t').Append(w.reveal.ToString("F4", CultureInfo.InvariantCulture)).Append('\t')
                       .Append(w.glyph.ToString("F3", CultureInfo.InvariantCulture)).Append('\t').Append(peak).Append('\n');
                    l.Tick(dt, input);
                }
                File.WriteAllText(Path.Combine(dir, "timeline.tsv"), tsv.ToString());
                if (peakMax < 120)
                    Debug.LogError($"[TitleReveal] 題字が写っていない（最大 {peakMax}）。絵を信用しないこと");
                Debug.Log($"[TitleReveal] {frames} コマ（{Fps:0}fps）→ {dir.Replace('\\', '/')}（最大の明るさ {peakMax}）");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
                rt.Release(); UnityEngine.Object.DestroyImmediate(rt);
                resolved.Release(); UnityEngine.Object.DestroyImmediate(resolved);
                UnityEngine.Object.DestroyImmediate(titleGo);
                UnityEngine.Object.DestroyImmediate(camGo);
            }
        }
    }
}
