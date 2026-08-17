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
    /// <b>AIエージェントからの連絡（<see cref="CommsPanel"/>）の出方を 1 コマずつ焼く。</b>
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
        /// <summary>
        /// 撮る文面。⚠ <b>全部を並べる。</b> 1 つでも漏らすと、その文面だけ枠から溢れていても
        /// 誰も気づけない（0079 で⓪a / ⓪b を足したときに漏らしかけた）。
        /// </summary>
        private static readonly CommsNotice[] Notices =
        {
            CommsNotice.Greeting, CommsNotice.Walk, CommsNotice.Arrived, CommsNotice.Begin,
            CommsNotice.MarkLogged, CommsNotice.MarkNothing, CommsNotice.Prompt,
        };

        private const string MainScenePath = "Assets/Scenes/Main.unity";
        private const string OutDirRel = "Screenshots/comms-preview";

        /// <summary>撮影台。他の scene 幾何が写り込まないよう、誰も居ない高さへ。</summary>
        private static readonly Vector3 Stage = new Vector3(0f, 1000f, 0f);

        private const int W = 1280, H = 720;
        private const int Fps = 30;

        /// <summary>
        /// 寄りの画角（垂直・度）。
        /// ⚠ <b>26 → 30 へ広げた</b>（2026-08-17・<c>canon/LEDGER.md</c> 0071）。顔の枠のぶん
        /// 面が左へ 0.194m 伸びて全幅 0.954m ＝ 見かけ <b>35°</b> になり、26 では左端が枠外へ出る。
        /// 文面を画面中心へ運んで撮る（<see cref="PlaceStraightAhead"/>）ので、要るのは
        /// 左へ 0.574m ＝ 21° ぶん。16:9 の横画角は垂直 30° で片側 25.5° なので余裕を持って入る。
        /// </summary>
        private const float CloseFovDeg = 30f;

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

            // ---- 周回の壊れ（`canon/LEDGER.md` 0068）------------------------------------
            // ⚠⚠ **指定が無ければ 4 段階まとめて焼く。** Unity の起動は 1 回 8 分かかるので、
            //    周ごとの見え方を比べるのに 4 回起こしていられない。
            float[] decays = ParseDecayList();

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
                // ⚠⚠ **壊れは発作の刻みで跳ねる**ので、適当な時刻で撮ると「何も起きていない」絵になり、
                //    実装が死んでいても気づけない。**発作が立っている刻みを探してそこで撮る。**
                foreach (float decay in decays)
                {
                    string suffix = $"_d{Mathf.RoundToInt(decay * 100f):000}";
                    // 3 周目は 3 字以上化けた刻み、手前の周は 1 字でも化けた刻みを撮る。
                    int wantMin = decay >= 0.9f ? 3 : 1;

                    // ⚠ **全部の文面を撮る。** 1 つでも漏らすと、その文面だけ枠から溢れていても
                    //   誰も気づけない（0079 で⓪a / ⓪b を足したときに漏らしかけた）。
                    foreach (CommsNotice notice in Notices)
                    {
                        Disable(logic);
                        ApplyNow(apply, panel, logic);
                        panel.Deliver(notice);
                        // ⚠ **Deliver の後**（素の文面が入ってから壊しにいく）。
                        SeekCorruption(panel, decay, wantMin);
                        PlaceStraightAhead(root, tmp, dist);
                        Step(logic, apply, panel, inSec);
                        Step(logic, apply, panel, TypeSec(logic));
                        Shoot(cam, Path.Combine(dir, $"notice_{notice}{suffix}.png"));
                    }

                    // ---- 1 字も化けていない刻み（③でだけ撮る）------------------------------
                    // ⚠ 面は**ほとんどの時間こちらの姿**で立っている。化けた絵だけ見て強さを決めると、
                    //    体験のほとんどの時間に何も起きていない、という判断ミスをする。
                    if (decay > 0f)
                    {
                        Disable(logic);
                        ApplyNow(apply, panel, logic);
                        panel.Deliver(CommsNotice.Prompt);
                        SeekQuiet(panel, decay);
                        PlaceStraightAhead(root, tmp, dist);
                        Step(logic, apply, panel, inSec);
                        Step(logic, apply, panel, TypeSec(logic));
                        Shoot(cam, Path.Combine(dir, $"notice_Prompt{suffix}_quiet.png"));
                    }
                }
                // コマ送りの動画は 1 周目の姿で撮る（壊れは静止画で見る）。
                panel.SetDecayForPreview(0f, 0f);

                // ---- 報告の長押し中（下段が 2 行になる）----
                // ⚠ **これは絵でしか確かめられない。** 見出しがゲージの左上に小さく座っているか
                //    （2026-08-15 の赤入れ）と、2 行が下段の帯に収まっているか。
                //    連絡が無いときの姿（`Guide`）もここで一緒に見える。
                Disable(logic);
                panel.SetControllerState(true, true);
                panel.SetMarkState(0.6f, confirming: false);
                logic.GetType().GetMethod("SetGuideWanted")!.Invoke(logic, new object[] { true });
                PlaceStraightAhead(root, tmp, dist);
                // ⚠ **開き切るまで進める。** 0.1 秒では枠がまだ開いている途中で、下段も
                //    出ていない（`HintInAt` は開きの 75% から）＝ 空の細い枠が写るだけになる。
                Step(logic, apply, panel, inSec + 0.1f);
                Shoot(cam, Path.Combine(dir, "mark_holding.png"));

                // ---- 上下段が同時に出る**最悪の姿**（上段 2 行 ＋ 下段 2 行）----
                // ⚠⚠ **2026-08-16 まで 1 枚も撮っていなかった。** `canon/LEDGER.md` 0058 が
                //    「最悪は上段 2 行 ＋ 下段 2 行で、収まるかは絵でしか分からない」と書いた当の姿。
                //    枠の高さを中身から解くようにした（0065）ので、ここが崩れると誰も気づけない。
                //    ⚠ 押しっぱなしのまま③を届ける（`SetGuideWanted` は下げない）。
                panel.Deliver(CommsNotice.Prompt);
                Step(logic, apply, panel, inSec);
                Step(logic, apply, panel, TypeSec(logic));
                Shoot(cam, Path.Combine(dir, "both_bands.png"));

                panel.SetMarkState(0f, confirming: false);
                logic.GetType().GetMethod("SetGuideWanted")!.Invoke(logic, new object[] { false });

                // ---- 出て、読ませて、引くまでを 1 コマずつ（いちばん重い ③ で撮る）----
                // ⚠ **229 コマ ＝ 撮影だけで 20 分。** 壊れの見え方を詰めるときは要らないので
                //    `-Set frames=0` で飛ばせる（動画を作るときだけ撮る）。
                bool wantFrames = EditorCliArgs.Get("frames") != "0";
                if (!wantFrames)
                {
                    Debug.Log("[CommsPreview] コマ送りは飛ばした（-Set frames=0）。静止画だけ焼いた");
                    return;
                }
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

                Debug.Log($"[CommsPreview] {n} コマ + place.png + 文面 {decays.Length * Notices.Length} 枚"
                        + $"（周回の壊れ {string.Join(" / ", System.Array.ConvertAll(decays, d => d.ToString("0.00")))}）"
                        + $" → Assets/{OutDirRel}/\n"
                        + $"  枠が開く {inSec:0.00}s → 打つ {typeSec:0.00}s"
                        + $" → 読ませる {holdSec:0.0}s → 引く {outSec:0.00}s\n"
                        + $"  置き場所: 頭から {dist:0.0}m・左へ {-yawOff:0}°・下へ {pitchOff:0}°");
            }
            finally { Object.DestroyImmediate(camGo); }
        }

        private static string Frame(string dir, int i) => Path.Combine(dir, $"f{i:0000}.png");

        /// <summary>
        /// <c>-Set decay=0..1</c>（周回の進み）。既定 0 ＝ 1 周目の頭 ＝ <b>壊れが 1 画素も出ない</b>。
        /// 周の境目は 0 / 0.33 / 0.67 / 1.0（<see cref="ScreenDecayLogic"/>）。
        /// ⚠ 形は <c>ShowCompositePreview.ParseDecayArg</c> と同じ（あちらは映像、こちらは連絡の面）。
        /// </summary>
        /// <summary>
        /// 焼く進みの並び。<b>指定が無ければ 4 段階</b>（1 周目の頭 / 2 周目の頭 / 2 周目の終わり /
        /// 3 周目 A 以降）。周の境目は <see cref="ScreenDecayLogic"/> の
        /// <c>(lap-1 + 経過/目安) / (totalLaps-1)</c> から。
        /// </summary>
        private static float[] ParseDecayList()
        {
            string? raw = EditorCliArgs.Get("decay");
            if (string.IsNullOrEmpty(raw)) return new[] { 0f, 0.33f, 0.66f, 1f };
            return new[] { ParseDecayArg() };
        }

        private static float ParseDecayArg()
        {
            string? raw = EditorCliArgs.Get("decay");
            if (string.IsNullOrEmpty(raw)) return 0f;
            if (!float.TryParse(raw, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float v))
            {
                Debug.LogWarning($"[CommsPreview] decay の値を読めない: '{raw}'（0 として扱う）");
                return 0f;
            }
            return Mathf.Clamp01(v);
        }

        /// <summary>
        /// <b>字が <paramref name="wantMin"/> 個以上化けている刻みへ合わせる。</b>
        /// ⚠⚠ 適当な時刻で撮ると「何も起きていない」絵になり、**実装が死んでいても気づけない**。
        /// ⚠ 文面ごとに化ける刻みが違うので、**面へ実際に掛けてから数える**
        /// （プレビュー側が文面を知らなくて済む）。
        /// ⚠ <b>必ず <c>Deliver</c> の後に呼ぶ</b> — 素の文面が入っていないと 1 字も化けない。
        /// </summary>
        private static void SeekCorruption(CommsPanel panel, float decay, int wantMin)
        {
            for (int t = 0; t < 240; t++)
            {
                panel.SetDecayForPreview(decay, t * CommsGlitchLogic.TickSec + 0.01f);
                if (panel.CorruptedChars >= wantMin) return;
            }
        }

        /// <summary>1 字も化けていない刻みへ合わせる（面が立っている時間の大半はこちらの姿）。</summary>
        private static void SeekQuiet(CommsPanel panel, float decay)
        {
            for (int t = 0; t < 240; t++)
            {
                panel.SetDecayForPreview(decay, t * CommsGlitchLogic.TickSec + 0.01f);
                if (panel.CorruptedChars == 0) return;
            }
        }

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
