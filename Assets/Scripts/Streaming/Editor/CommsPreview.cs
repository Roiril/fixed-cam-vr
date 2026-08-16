#nullable enable
using System.IO;
using System.Reflection;
using FixedCamVr.Diagnostics;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// <b>上司からの連絡（<see cref="CommsPanel"/>）の出方を 1 コマずつ焼く。</b>
    /// 出るまで 0.45 秒・読ませる 7 秒・引くまで 0.9 秒の全部を、実装と同じ重みで撮る。
    ///
    /// ⚠ <b>Unity が実際に描いた絵</b>（CPU の模写ではない）。地・縁・文字の色も、
    /// 面が先で文字が後という遅れも、<see cref="CommsPanelLogic"/> と
    /// <c>CommsPanel.Apply</c> をそのまま通している。
    /// <c>tools/preview-visitor-mark.py</c> は模写だが、こちらは実物。
    ///
    /// ⚠ 実機の見え方そのものではない — <b>両眼視差・レンズ・パススルーの明るさは入っていない</b>。
    /// 地の濃さ（<c>Unlit/Color</c> は alpha を持たないので明るさで濃さを出している）は、
    /// 背景が明るい現場では印象が変わる。
    ///
    /// 使い方: <c>.\tools\unity.ps1 menu comms-preview</c> → <c>tools/make-preview-video.py</c> で mp4 へ。
    /// </summary>
    public static class CommsPreview
    {
        private const string MainScenePath = "Assets/Scenes/Main.unity";
        private const string OutDirRel = "Screenshots/comms-preview";

        /// <summary>撮影台。他の scene 幾何が写り込まないよう、誰も居ない高さへ。</summary>
        private static readonly Vector3 Stage = new Vector3(0f, 1000f, 0f);

        private const int W = 1280, H = 720;
        private const int Fps = 30;

        /// <summary>寄りの画角（垂直・度）。1.5m の面（見かけ 28.7°）が画面いっぱいに入る。</summary>
        private const float CloseFovDeg = 26f;

        /// <summary>視界の中の座りを見る画角（垂直・度）。左下へ振ってある位置関係が読める。</summary>
        private const float WideFovDeg = 52f;

        [MenuItem("Tools/FixedCamVr/Preview/Comms Panel (frames)", priority = 84)]
        public static void Run()
        {
            if (!EditorCliArgs.EnsureScene(MainScenePath)) return;

            var panel = Object.FindObjectOfType<CommsPanel>(includeInactive: true);
            if (panel == null)
            {
                Debug.LogError("[CommsPreview] シーンに [Comms] が居ません（menu scene を先に）");
                return;
            }

            // Edit モードでは Awake が走っていないので、自分で起こして面を組ませる。
            Invoke(panel, "Awake");
            // 打鍵も起こす（音は鳴らないが、**鳴らしたはずの数**が数えられる ＝ type.tsv の材料）。
            var typeSfx = panel.GetComponent<TypeAudioCue>();
            if (typeSfx != null) Invoke(typeSfx, "Awake");
            TMP_Text? tmp = panel.GetComponentInChildren<TMP_Text>(includeInactive: true);
            if (tmp == null)
            {
                Debug.LogError("[CommsPreview] 面を組めませんでした（日本語フォントが解決できない？）");
                return;
            }
            var jp = JapaneseHudFont.TryGet();
            if (jp != null) tmp.font = jp;

            object? logic = GetField(panel, "_logic");
            MethodInfo? apply = panel.GetType().GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic);
            if (logic == null || apply == null)
            {
                Debug.LogError("[CommsPreview] CommsPanel の _logic / Apply を取れません");
                return;
            }

            string dir = Path.Combine(Application.dataPath, OutDirRel);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);

            Transform root = panel.transform;
            root.SetParent(null, worldPositionStays: true);
            root.gameObject.SetActive(true);

            var camGo = new GameObject("[CommsPreview] Camera");
            int n = 0;
            try
            {
                var cam = camGo.AddComponent<Camera>();
                cam.nearClipPlane = 0.05f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                // 実機の暗い現場に近い無地。⚠ 現場のパススルーはここより明るいことがあり、
                //    そのとき地（暗い漆の面）は相対的に沈んで見える。
                cam.backgroundColor = new Color(0.055f, 0.052f, 0.050f, 1f);

                float dist = ConstF(typeof(CommsPanel), "DistanceM", 1.5f);
                float yawOff = ConstF(typeof(CommsPanel), "YawOffsetDeg", -20f);
                float pitchOff = ConstF(typeof(CommsPanel), "PitchOffsetDeg", 10f);
                float inSec = ConstF(typeof(CommsPanelLogic), "InSec", 0.45f);
                float holdSec = ConstF(typeof(CommsPanelLogic), "HoldSec", 7f);
                float outSec = ConstF(typeof(CommsPanelLogic), "OutSec", 0.9f);

                // ---- 1 枚目: 視界の中の座り（正面を向いた頭から見て、面がどこに立つか）----
                PlaceAuthored(root, dist, yawOff, pitchOff);
                panel.Deliver(CommsNotice.Begin);
                Step(logic, apply, panel, inSec);          // 枠を開き切る
                Step(logic, apply, panel, TypeSec(logic));
                cam.transform.SetPositionAndRotation(Stage, Quaternion.identity);
                cam.fieldOfView = WideFovDeg;
                Shoot(cam, Path.Combine(dir, "place.png"));

                cam.fieldOfView = CloseFovDeg;

                // ---- 文面ごとに 1 枚（打ち終わった状態）----
                // ⚠ **4 通とも撮る。** 枠に収まるかは机上の文字数勘定では決まらず、しかも
                //    ③ だけが 2 行なので、縦の座り（`CommsPanel.SetNotice` の重心運び）は
                //    ここでしか見られない（`menu text-audit` は幅しか測らない）。
                foreach (CommsNotice notice in new[]
                {
                    CommsNotice.Begin, CommsNotice.MarkLogged,
                    CommsNotice.MarkNothing, CommsNotice.Prompt,
                })
                {
                    Disable(logic);
                    ApplyNow(apply, panel, logic);
                    panel.Deliver(notice);
                    PlaceStraightAhead(root, tmp, dist);
                    Step(logic, apply, panel, inSec);
                    Step(logic, apply, panel, TypeSec(logic));
                    Shoot(cam, Path.Combine(dir, $"notice_{notice}.png"));
                }

                // ---- 出て、読ませて、引くまでを 1 コマずつ（いちばん重い ③ で撮る）----
                Disable(logic);
                ApplyNow(apply, panel, logic);
                panel.Deliver(CommsNotice.Prompt);
                PlaceStraightAhead(root, tmp, dist);
                Disable(logic);
                ApplyNow(apply, panel, logic);

                float dt = 1f / Fps;
                // ⚠ **打鍵が鳴るコマを書き出す**（`canon/LEDGER.md` 0056）。音を後から Python 側で
                //    数え直すと、実機と違う所で鳴る動画ができて判断が狂う（`menu glitch` の
                //    `frames.tsv` と同じ流儀 — 数えるのは Unity、並べるのが Python）。
                var taps = new System.Text.StringBuilder("frame\tchars\thit\n");
                int lastTyped = panel.TypedCount;
                for (int i = 0; i < Fps * 0.3f; i++)
                {
                    taps.Append(n).Append("\t").Append(panel.VisibleChars).Append("\t0\n");
                    Shoot(cam, Frame(dir, n++));   // 出る前の間
                }

                panel.Deliver(CommsNotice.Prompt);
                float typeSec = TypeSec(logic);
                float total = inSec + typeSec + holdSec + outSec + 0.2f;
                for (float t = 0f; t < total; t += dt)
                {
                    Step(logic, apply, panel, dt);
                    // ⚠ 実際に鳴らした数（`TypedCount`）の増分で見る ＝ **改行で鳴らない規則も
                    //    そのまま入る**（コマ数から数え直すと、そこだけ実機と違う動画になる）。
                    int hit = panel.TypedCount > lastTyped ? 1 : 0;
                    lastTyped = panel.TypedCount;
                    taps.Append(n).Append("\t").Append(panel.VisibleChars).Append("\t")
                        .Append(hit).Append("\n");
                    Shoot(cam, Frame(dir, n++));
                }
                File.WriteAllText(Path.Combine(dir, "type.tsv"), taps.ToString());
                if (!panel.TypeSfxBuilt)
                {
                    Debug.LogWarning("[CommsPreview] 打鍵の音源を掴めていないので type.tsv は空になります"
                                     + "（`py -3.11 tools/ingest-sounds.py --only sfx_type`）");
                }

                Debug.Log($"[CommsPreview] {n} コマ + place.png + 文面 4 枚 → Assets/{OutDirRel}/\n"
                        + $"  枠が開く {inSec:0.00}s → 打つ {typeSec:0.00}s"
                        + $" → 読ませる {holdSec:0.0}s → 引く {outSec:0.00}s\n"
                        + $"  置き場所: 頭から {dist:0.0}m・左へ {-yawOff:0}°・下へ {pitchOff:0}°");
            }
            finally { Object.DestroyImmediate(camGo); }
        }

        private static string Frame(string dir, int i) => Path.Combine(dir, $"f{i:0000}.png");

        /// <summary>著作どおりの位置（左へ振って下げて、面は頭へ正対）へ置く。</summary>
        private static void PlaceAuthored(Transform root, float dist, float yawOffDeg, float pitchOffDeg)
        {
            Quaternion yaw = Quaternion.Euler(0f, yawOffDeg, 0f);
            Vector3 dir = yaw * Quaternion.Euler(pitchOffDeg, 0f, 0f) * Vector3.forward;
            root.position = Stage + dir * dist;
            root.rotation = Quaternion.LookRotation(root.position - Stage, Vector3.up);
        }

        /// <summary>寄りの画のために、面を眼の正面 <paramref name="dist"/> へ持ってくる。</summary>
        private static void PlaceStraightAhead(Transform root, TMP_Text tmp, float dist)
        {
            root.rotation = Quaternion.identity;
            Vector3 offset = tmp.transform.position - root.position;
            root.position = Stage + new Vector3(0f, 0f, dist) - offset;
        }

        /// <summary>この文面を打ち終わるまでの秒（`CommsPanel.Deliver` が決めた実測値）。</summary>
        private static float TypeSec(object logic) =>
            (float)logic.GetType().GetProperty("TypeSec")!.GetValue(logic);

        private static void Disable(object logic) =>
            logic.GetType().GetMethod("Disable")!.Invoke(logic, null);

        /// <summary>ロジックを進めて、その重みを面へ配る（実行時と同じ 1 本道）。</summary>
        private static void Step(object logic, MethodInfo apply, CommsPanel panel, float dt)
        {
            logic.GetType().GetMethod("Tick")!.Invoke(logic, new object[] { dt });
            ApplyNow(apply, panel, logic);
        }

        private static void ApplyNow(MethodInfo apply, CommsPanel panel, object logic)
        {
            object w = logic.GetType().GetProperty("Weights")!.GetValue(logic);
            apply.Invoke(panel, new[] { w });
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
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(tex);
            }
        }

        private static object? GetField(object target, string name) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target);

        /// <summary>private const を実装から読む（数値を写して食い違わせない）。</summary>
        private static float ConstF(System.Type t, string name, float fallback)
        {
            FieldInfo? f = t.GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (f == null || !f.IsLiteral) return fallback;
            return System.Convert.ToSingle(f.GetRawConstantValue());
        }

        private static void Invoke(object target, string method)
        {
            MethodInfo? m = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            m?.Invoke(target, null);
        }
    }
}
