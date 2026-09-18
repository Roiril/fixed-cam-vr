#nullable enable
using System.IO;
using System.Reflection;
using FormattableString = System.FormattableString;
using FixedCamVr.Diagnostics;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// <b>AIエージェントからの連絡（<see cref="CommsPanel"/>）の出方を 1 コマずつ焼く。</b>
    /// <b>文面 8 通を通しで</b> — 枠が開く 0.45 秒・打つ（文字数 ÷ 12）・読ませる 2 秒・
    /// 引く 0.9 秒の全部を、実装と同じ重みで撮る。
    /// ⚠ 順序と間は <see cref="CommsCueLogic"/> に決めさせている（尺を写して並べていない）ので、
    /// ⓪a → ⓪b と① → ①b が<b>同じ面のまま繋がる</b>のもそのまま写る。
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
    ///
    /// ⚠⚠ <b>言語を選べる</b>（<c>-Set lang=ja|en|fr</c>・既定は日本語）。
    /// 2026-09-06 まで日本語しか焼けなかったので、<b>English / Français の面は一度も
    /// 描かれた絵で見られていなかった</b>（字数と行数は <c>CommsNoticeTextTests</c> が
    /// 3 言語ぶん測っているが、**測るのと見るのは別**）。
    /// 出し先は日本語だけ従来どおりで、他は <c>-en</c> / <c>-fr</c> が付く。
    /// </summary>
    public static class CommsPreview
    {
        /// <summary>嘘の一文（3 周目 A）を憑依の出し方（0230）で 3 言語ぶん焼く。呼ぶのは <c>menu raw:…CommsPreview.RunPossession</c>。</summary>
        [MenuItem("Tools/FixedCamVr/Preview/Comms Possession (frames)", priority = 85)]
        public static void RunPossession() => CommsPossessionPreview.Run();

        /// <summary>
        /// 撮る文面。⚠ <b>全部を並べる。</b> 1 つでも漏らすと、その文面だけ枠から溢れていても
        /// 誰も気づけない（0079 で⓪a / ⓪b を足したときに漏らしかけた）。
        /// </summary>
        private static readonly CommsNotice[] Notices =
        {
            CommsNotice.Greeting, CommsNotice.Walk, CommsNotice.Arrived,
            CommsNotice.Begin, CommsNotice.BeginHow,
            CommsNotice.MarkLogged, CommsNotice.MarkNothing,
            CommsNotice.Halt, CommsNotice.Prompt,
        };

        private const string MainScenePath = "Assets/Scenes/Main.unity";
        private const string OutDirRel = "Screenshots/comms-preview";

        /// <summary>
        /// <c>-Set lang=ja|en|fr</c>（既定は日本語）。出し先は日本語だけ従来どおりで、
        /// 他は <c>-en</c> / <c>-fr</c> が付く（同じ所へ焼くと前の言語のコマを消す）。
        /// ⚠ 読み違えても落とさない（<see cref="ShowLanguage.Parse"/> と同じ流儀）。
        /// </summary>
        private static string OutDirFor(ShowLang lang)
            => lang == ShowLang.Ja ? OutDirRel : OutDirRel + "-" + ShowLanguage.Code(lang);

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

            // ⚠⚠ **言語は面を組む前に決める。** 後から替えると、`SetNotice` が測った字数・重心・
            //    枠の高さが前の言語のまま焼き付く（`HmdTextAudit` と同じ順序）。
            ShowLang lang = ShowLanguage.Parse(EditorCliArgs.Get("lang"));
            ShowLang restoreLang = ShowLanguage.Current;
            ShowLanguage.Select(lang);
            try
            {
                RunFor(lang);
            }
            finally { ShowLanguage.Select(restoreLang); }
        }

        private static void RunFor(ShowLang lang)
        {

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
            // 塗り替わりの頭の乱れの音（0230）も同じ理由で自分で起こす（起こさないと音源を掴まず 0 発のまま）。
            var sweepSfx = panel.GetComponent<CurseSweepAudioCue>();
            if (sweepSfx != null) Invoke(sweepSfx, "Awake");
            TMP_Text? tmp = null;
            foreach (TMP_Text candidate in panel.GetComponentsInChildren<TMP_Text>(includeInactive: true))
                if (candidate.name == "CommsText") { tmp = candidate; break; }
            if (tmp == null)
            {
                Debug.LogError("[CommsPreview] CommsText を組めませんでした（日本語フォントが解決できない？）");
                return;
            }
            object? logic = GetField(panel, "_logic");
            MethodInfo? apply = panel.GetType().GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic);
            if (logic == null || apply == null)
            {
                Debug.LogError("[CommsPreview] CommsPanel の _logic / Apply を取れません");
                return;
            }

            // ---- イベントで進む通信侵食 -----------------------------------------------
            // 指定が無ければ 0 / 0.25 / 0.75 / 1 をまとめて焼く。
            float[] decays = ParseDecayList();

            string outDirRel = OutDirFor(lang);
            string dir = Path.Combine(Application.dataPath, outDirRel);
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
                float holdSec = ConstF(typeof(CommsPanelLogic), "HoldSec", 2f);
                float outSec = ConstF(typeof(CommsPanelLogic), "OutSec", 0.9f);
                float previewTime = PreviewTimeSec();

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
                        // ⚠ 侵食度は**届く前**に立てる — 出し方（打つ／憑依・0230）は届いた瞬間の侵食度で決まる。
                        SetLevelForPreview(panel, decay, previewTime);
                        panel.Deliver(notice);
                        PlaceStraightAhead(root, tmp, dist);
                        Step(logic, apply, panel, inSec);
                        Step(logic, apply, panel, TypeSec(logic));
                        // 実描画の欠落数を測るため、全文を出して Apply した後に刻みを探す。
                        SeekCorruption(panel, logic, apply, decay, wantMin, previewTime);
                        Shoot(cam, Path.Combine(dir, $"notice_{notice}{suffix}.png"));
                    }

                }
                ShootReviewFrames(panel, logic, apply, root, tmp, cam, dir, dist, previewTime);
                // コマ送りの動画は 1 周目の姿で撮る（壊れは静止画で見る）。
                panel.SetInvasionForPreview(0f, 0f);

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

                // ---- 1 回の体験ぶんを通しで撮る（実装の尺そのまま）--------------------------
                // ⚠⚠ **文面 8 通を全部、実装が決めた尺で並べる**（2026-08-19・ユーザー依頼
                //    「全部のテキストを表示 / 2s 残存とか、アニメーションを実装されているもので
                //    再現した動画をください」）。それまでは③ 1 通だけを 229 コマ撮っていた。
                //
                // ⚠ **順序と間は <see cref="CommsCueLogic"/> に決めさせる**（尺を写して並べない）。
                //   ここが書くのは「体験者が何をしたか」だけ — 導入に居る / 円へ着いた /
                //   本編へ入った / ボタンを長押しした / 締めのカットが待ち始めた。
                //   ⓪a → ⓪b と① → ①b が**同じ面のまま繋がる**のも、実装がそう返すからそう写る。
                bool wantFrames = EditorCliArgs.Get("frames") == "1";
                if (!wantFrames)
                {
                    Debug.Log("[CommsPreview] 55 秒の全通知連番は飛ばした（必要なら -Set frames=1）");
                    if (EditorCliArgs.Get("motion") == "1")
                        ShootMotion(panel, logic, apply, root, tmp, cam, dir, dist);
                    return;
                }
                Disable(logic);
                panel.SetMarkState(0f, confirming: false);
                PlaceStraightAhead(root, tmp, dist);
                ApplyNow(apply, panel, logic);

                float dt = 1f / Fps;
                // 体験者が何をするか（秒）。**実装が決める尺はここに 1 つも無い。**
                const float ArrivedAt = 11.0f;   // 円へ着いた ＝ 段 0 を抜けた（⓪c）
                const float RunAt = 17.0f;       // 導入演出が明けた（①）
                const float Press1At = 28.0f;    // 1 回目の報告（通る ＝ ②a）
                const float Press2At = 34.5f;    // 2 回目の報告（通らない ＝ ②b）
                const float WaitAt = 41.0f;      // **締めのカットに入った**（③a は 5 秒後・0178）
                // ⚠⚠ **③a を挟んだぶん伸びた**（2026-09-06・0168）。③a が 1.35 秒画に居て、
                //    そこから③b が打ち始めるので、49 のままだと**③b が引き切る前に切れる**
                //    （＝ 出来上がった動画の最後だけが無い、という気づきにくい形）。
                const float EndAt = 55.0f;
                float markHoldSec = ConstF(typeof(FixedCamVr.Input.VisitorMarkHoldLogic),
                                           "DefaultHoldSec", 1.0f);

                var cue = new CommsCueLogic();
                MethodInfo guideWanted = logic.GetType().GetMethod("SetGuideWanted")!;
                PropertyInfo doneReading = logic.GetType().GetProperty("DoneReading")!;
                // ⚠ **打鍵が鳴るコマを書き出す**（`canon/LEDGER.md` 0056）。音を後から Python 側で
                //    数え直すと、実機と違う所で鳴る動画ができて判断が狂う（`menu glitch` の
                //    `frames.tsv` と同じ流儀 — 数えるのは Unity、並べるのが Python）。
                var taps = new System.Text.StringBuilder("frame\tchars\thit\n");
                int lastTyped = panel.TypedCount;
                float holdT = -1f;               // 長押しの経過（負 ＝ 押していない）
                int presses = 0;

                for (float t = 0f; t < EndAt; t += dt)
                {
                    // 体験者の長押し（1 秒）。押しているあいだ面は開き、走っている連絡は片づく。
                    bool release = false;
                    if ((presses == 0 && t >= Press1At) || (presses == 1 && t >= Press2At))
                    {
                        if (holdT < 0f) holdT = 0f;
                        holdT += dt;
                        panel.SetMarkState(Mathf.Clamp01(holdT / markHoldSec), confirming: false);
                        guideWanted.Invoke(logic, new object[] { true });
                        if (holdT >= markHoldSec)
                        {
                            release = true;
                            holdT = -1f;
                            presses++;
                            panel.SetMarkState(0f, confirming: false);
                        }
                    }

                    CommsNotice next = cue.Tick(new CommsCueInput
                    {
                        inIntro = t < RunAt,
                        inRun = t >= RunAt,
                        startAuthorized = true,
                        introWaiting = t < ArrivedAt,
                        panelDoneReading = (bool)doneReading.GetValue(logic),
                        closingSec = t >= WaitAt ? t - WaitAt : -1f,
                        markPressed = release,
                        markDetected = presses == 1,   // 1 回目は異常表示中 / 2 回目は平常時
                        dt = dt,
                    });
                    if (next != CommsNotice.None) panel.Deliver(next);
                    if (release) guideWanted.Invoke(logic, new object[] { false });

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
                        + $"（通信侵食 {string.Join(" / ", System.Array.ConvertAll(decays, d => d.ToString("0.00")))}）"
                        + $" → Assets/{outDirRel}/（言語 {ShowLanguage.Code(lang)}）\n"
                        + $"  通し: {EndAt:0} 秒 ＝ 文面 {Notices.Length} 通"
                        + "（⓪a→⓪b / ①→①b / ③a→③b は同じ面のまま繋がる）\n"
                        + $"  枠が開く {inSec:0.00}s → 打つ 文字数÷{ConstF(typeof(CommsPanelLogic), "CharsPerSec", 12f):0}"
                        + $" → 読ませる {holdSec:0.0}s → 引く {outSec:0.00}s\n"
                        + $"  置き場所: 頭から {dist:0.0}m・左へ {-yawOff:0}°・下へ {pitchOff:0}°");
            }
            finally { Object.DestroyImmediate(camGo); }
        }

        private static string Frame(string dir, int i) => Path.Combine(dir, $"f{i:0000}.png");

        /// <summary>
        /// <c>-Set invasion=0..1</c>。旧 <c>decay=</c> も同じ意味で受け付ける。
        /// </summary>
        /// <summary>
        /// 焼く侵食度。指定が無ければ物語上の4段階を使う。
        /// </summary>
        private static float[] ParseDecayList()
        {
            string? raw = EditorCliArgs.Get("invasion") ?? EditorCliArgs.Get("decay");
            if (string.IsNullOrEmpty(raw)) return new[] { 0f, 0.25f, 0.75f, 1f };
            return new[] { ParseDecayArg() };
        }

        private static float ParseDecayArg()
        {
            string? raw = EditorCliArgs.Get("invasion") ?? EditorCliArgs.Get("decay");
            if (string.IsNullOrEmpty(raw)) return 0f;
            if (!float.TryParse(raw, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float v))
            {
                Debug.LogWarning($"[CommsPreview] invasion の値を読めない: '{raw}'（0 として扱う）");
                return 0f;
            }
            return Mathf.Clamp01(v);
        }

        /// <summary>
        /// <b>斑が目標まで重なった所で撮る。</b> 斑は面が開いてから
        /// <see cref="CommsCurseLogic.RampSec"/> で立ち上がる（0229）ので、届く前に撮ると
        /// 「何も起きていない」絵になり、**実装が死んでいても気づけない**。
        /// ⚠ <b>必ず <c>Deliver</c> の後に呼ぶ</b> — 素の文面が入っていないと 1 字も切れない。
        /// ⚠ 呼ぶのは読ませている段（Hold 2 秒）なので、1 秒進めても引き始めない。
        /// </summary>
        private static void SeekCorruption(CommsPanel panel, object logic, MethodInfo apply,
                                           float decay, int wantMin, float startSec)
        {
            if (decay <= 0f) return;
            panel.SetInvasionForPreview(decay, startSec);
            // 斑は 1 秒で立ち上がり（0229）、憑依の出し方（0230）は 出る → 読ませる → 塗り替わる の後で全面になる。
            // どちらも「届いた所」で撮る。届かなければ 6 秒で諦めて警告する。
            float waited = 0f;
            while (waited < 6f && panel.CurseTarget > 0.001f && panel.AppliedCurse < panel.CurseTarget * 0.99f)
            {
                Step(logic, apply, panel, 1f / Fps);
                waited += 1f / Fps;
            }
            // 「止まってください！」と続く警告は斑の目標が 0（読める状態で出す）。切られなくて正しい。
            if (panel.CurseTarget <= 0.001f) return;
            if (panel.AppliedCurse < panel.CurseTarget * 0.99f)
                Debug.LogWarning($"[CommsPreview] 斑が目標へ届いていない: {panel.AppliedCurse:F2} / {panel.CurseTarget:F2}");
            if (panel.CorruptedChars < wantMin)
                Debug.LogWarning($"[CommsPreview] 侵食度 {decay:F2} で切られた字が {panel.CorruptedChars} 字（{wantMin} 字以上のはず）");
        }

        private static float PreviewTimeSec()
        {
            string? raw = EditorCliArgs.Get("time");
            if (string.IsNullOrEmpty(raw)) return 0.01f;
            if (!float.TryParse(raw, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float value))
            {
                Debug.LogWarning($"[CommsPreview] time の値を読めない: '{raw}'（0.01 として扱う）");
                return 0.01f;
            }
            return Mathf.Max(0f, value);
        }

        private static void SetLevelForPreview(CommsPanel panel, float level, float timeSec)
            => panel.SetInvasionForPreview(Mathf.Clamp01(level), timeSec);

        private static void ShootReviewFrames(CommsPanel panel, object logic, MethodInfo apply,
                                              Transform root, TMP_Text tmp, Camera cam, string dir,
                                              float dist, float timeSec)
        {
            float inSec = ConstF(typeof(CommsPanelLogic), "InSec", 0.45f);
            float outSec = ConstF(typeof(CommsPanelLogic), "OutSec", 0.9f);

            float[] levels = { 0f, 0.25f, 0.75f, 1f };
            string[] names =
            {
                "state_000.png", "state_025.png", "state_075.png", "state_100.png",
            };
            for (int i = 0; i < levels.Length; i++)
            {
                Disable(logic);
                ApplyNow(apply, panel, logic);
                SetLevelForPreview(panel, levels[i], timeSec);
                panel.Deliver(CommsNotice.BeginHow);
                PlaceStraightAhead(root, tmp, dist);
                Step(logic, apply, panel, inSec);
                Step(logic, apply, panel, TypeSec(logic));
                ApplyNow(apply, panel, logic);
                Shoot(cam, Path.Combine(dir, names[i]));
            }

            Disable(logic);
            ApplyNow(apply, panel, logic);
            panel.Deliver(CommsNotice.BeginHow);
            SetLevelForPreview(panel, 0f, timeSec);
            PlaceStraightAhead(root, tmp, dist);
            Step(logic, apply, panel, inSec * 0.5f);
            Shoot(cam, Path.Combine(dir, "transition_in.png"));
            Step(logic, apply, panel, inSec * 0.5f);

            float typeSec = TypeSec(logic);
            Step(logic, apply, panel, typeSec * 0.18f);
            Shoot(cam, Path.Combine(dir, "type_early.png"));
            Step(logic, apply, panel, typeSec * 0.34f);
            Shoot(cam, Path.Combine(dir, "type_middle.png"));
            Step(logic, apply, panel, typeSec * 0.34f);
            Shoot(cam, Path.Combine(dir, "type_late.png"));
            Step(logic, apply, panel, typeSec * 0.14f);
            Step(logic, apply, panel, ConstF(typeof(CommsPanelLogic), "HoldSec", 2f));
            Step(logic, apply, panel, outSec * 0.55f);
            Shoot(cam, Path.Combine(dir, "transition_out.png"));
        }

        /// <summary>
        /// <b>呪いの動き（<c>canon/LEDGER.md</c> 0229 / 0230）。</b> 同じ文面（①）を侵食度 0 / 0.25 / 0.75 / 1 で、
        /// 開く → 出す → 読ませる → 引く まで通しで焼く。<c>motion/lv000|025|075|100/</c> に
        /// 連番と <c>type.tsv</c>（打鍵）と <c>curse.tsv</c>（斑・前線・切られた字数・段）、
        /// <c>motion/geometry.json</c> に矩形と本文の帯の画面座標（Python の画素検査が読む）。
        ///
        /// 0.25 は打つ ＋ 1 秒で斑が重なる（0229）。0.75 と 1 は憑依の出し方（0230）:
        /// 全文が一気に出て（打鍵 0）→ 読ませて → 上から前線が降りて塗り替わる。
        /// ⚠ 判定は Python（<c>tools/render-comms-curse-preview.py</c>）が画素でする。ここは数値の側を落とす。
        /// </summary>
        private static void ShootMotion(CommsPanel panel, object logic, MethodInfo apply,
                                        Transform root, TMP_Text tmp, Camera cam, string dir, float dist)
        {
            string motionDir = Path.Combine(dir, "motion");
            Directory.CreateDirectory(motionDir);
            float dt = 1f / Fps;
            float[] levels = { 0f, 0.25f, 0.75f, 1f };
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var summary = new System.Text.StringBuilder(
                "level\tframes\tcurse_open\tcurse_1_2s\ttarget\tcx_1_2s\tcx_max\tramp_frame\tpossessed\tshown_frame\tshown_chars\tsweep_start_frame\tsweep_end_frame\thits\tsweep_sfx\n");
            PropertyInfo active = logic.GetType().GetProperty("Active")!;
            PropertyInfo stage = logic.GetType().GetProperty("Stage")!;

            foreach (float level in levels)
            {
                string sub = Path.Combine(motionDir, $"lv{Mathf.RoundToInt(level * 100f):000}");
                Directory.CreateDirectory(sub);
                Disable(logic);
                ApplyNow(apply, panel, logic);
                SetLevelForPreview(panel, level, 0f);
                panel.Deliver(CommsNotice.BeginHow);
                bool possessed = level >= CommsCurseLogic.PossessedLevel;
                PlaceStraightAhead(root, tmp, dist);
                var taps = new System.Text.StringBuilder("frame\tchars\thit\n");
                var curse = new System.Text.StringBuilder("frame\tsec\tstage\tcurse\ttarget\tcx\tglyph\tpanel\tface\tsweep\tphase\tsfx\ttear\ttorn\n");
                int lastTyped = panel.TypedCount, lastSfx = panel.SweepSfxCount;
                int frame = 0, cxMax = 0, cxAt12 = -1, rampFrame = -1, hits = 0, sfxHits = 0;
                int shownFrame = Mathf.RoundToInt((CommsPanelLogic.InSec + CommsPossessionLogic.ShowSec + 0.05f) * Fps);
                int shownChars = -1, shownCx = -1, sweepStart = -1, sweepEnd = -1;
                float curseOpen = -1f, curseAt12 = -1f, curseShown = -1f;
                int total = tmp.textInfo.characterCount;
                while ((bool)active.GetValue(logic) && frame < Fps * 12)
                {
                    ApplyNow(apply, panel, logic);
                    Shoot(cam, Frame(sub, frame));
                    int hit = panel.TypedCount - lastTyped;
                    lastTyped = panel.TypedCount;
                    hits += hit;
                    int sfx = panel.SweepSfxCount - lastSfx;
                    lastSfx = panel.SweepSfxCount;
                    sfxHits += sfx;
                    taps.Append(frame).Append('\t').Append(panel.VisibleChars).Append('\t').Append(hit).Append('\n');
                    curse.Append(frame).Append('\t')
                         .Append((frame * dt).ToString("F3", inv)).Append('\t')
                         .Append(stage.GetValue(logic)).Append('\t')
                         .Append(panel.AppliedCurse.ToString("F3", inv)).Append('\t')
                         .Append(panel.CurseTarget.ToString("F3", inv)).Append('\t')
                         .Append(panel.CorruptedChars).Append('\t')
                         .Append(panel.AppliedGlyph.ToString("F3", inv)).Append('\t')
                         .Append(panel.AppliedPanelAlpha.ToString("F3", inv)).Append('\t')
                         .Append(panel.AppliedFaceMix.ToString("F3", inv)).Append('\t')
                         .Append(panel.AppliedSweep.ToString("F3", inv)).Append('\t')
                         .Append(panel.PossessionPhase).Append('\t')
                         .Append(sfx).Append('\t')
                         .Append(panel.AppliedTear.ToString("F3", inv)).Append('\t')
                         .Append(panel.TornBands).Append('\n');
                    if (frame == 0) curseOpen = panel.AppliedCurse;
                    if (frame == Mathf.RoundToInt(1.2f * Fps))
                    {
                        curseAt12 = panel.AppliedCurse;
                        cxAt12 = panel.CorruptedChars;
                        // 画面座標は**面が開いて置かれている最中**に測る（畳んだ後に測ると quad が潰れている）。
                        if (level <= 0.01f)
                            File.WriteAllText(Path.Combine(motionDir, "geometry.json"), GeometryJson(panel, cam, tmp, root));
                    }
                    if (frame == shownFrame)
                    {
                        shownChars = panel.VisibleChars;
                        shownCx = panel.CorruptedChars;
                        curseShown = panel.AppliedCurse;
                    }
                    if (sweepStart < 0 && panel.PossessionPhase == CommsPossessionPhase.Sweep) sweepStart = frame;
                    if (sweepEnd < 0 && panel.PossessionPhase == CommsPossessionPhase.Cursed) sweepEnd = frame;
                    if (rampFrame < 0 && panel.CurseTarget > 0.01f && panel.AppliedCurse >= panel.CurseTarget * 0.99f)
                        rampFrame = frame;
                    cxMax = Mathf.Max(cxMax, panel.CorruptedChars);
                    Step(logic, apply, panel, dt);
                    frame++;
                }
                File.WriteAllText(Path.Combine(sub, "type.tsv"), taps.ToString());
                File.WriteAllText(Path.Combine(sub, "curse.tsv"), curse.ToString());
                summary.Append(level.ToString("F2", inv)).Append('\t')
                       .Append(frame).Append('\t')
                       .Append(curseOpen.ToString("F3", inv)).Append('\t')
                       .Append(curseAt12.ToString("F3", inv)).Append('\t')
                       .Append(panel.CurseTarget.ToString("F3", inv)).Append('\t')
                       .Append(cxAt12).Append('\t').Append(cxMax).Append('\t').Append(rampFrame).Append('\t')
                       .Append(possessed ? 1 : 0).Append('\t').Append(shownFrame).Append('\t').Append(shownChars).Append('\t')
                       .Append(sweepStart).Append('\t').Append(sweepEnd).Append('\t').Append(hits).Append('\t').Append(sfxHits).Append('\n');
                // 数値の側を落とす（画素は Python が見る）。
                if (curseOpen > 0.001f)
                    throw new System.InvalidOperationException($"侵食度 {level}: 開いた縁で斑が {curseOpen:F3}（0 のはず）");
                if (level <= 0.01f && cxMax != 0)
                    throw new System.InvalidOperationException($"侵食度 0 で字が切られた（{cxMax}）");
                if (level > 0.01f && !possessed)
                {
                    // 0229: 打ちながら 1 秒で斑が重なる。
                    if (rampFrame < 0 || rampFrame > Mathf.RoundToInt(1.3f * Fps))
                        throw new System.InvalidOperationException($"侵食度 {level}: 斑が 1.3 秒までに目標へ届かない（{rampFrame} コマ）");
                    if (cxAt12 <= 0)
                        throw new System.InvalidOperationException($"侵食度 {level}: 1.2 秒で切られた字が 0");
                    if (hits <= 0)
                        throw new System.InvalidOperationException($"侵食度 {level}: 打鍵が 0（打つ出し方のはず）");
                }
                if (possessed)
                {
                    // 0230: 一気に出て（打鍵 0・全文）→ 読ませて（斑 0・切られた字 0）→ 上から塗り替わる（0.45 秒）→ 全面。
                    if (hits != 0)
                        throw new System.InvalidOperationException($"侵食度 {level}: 打鍵が {hits} 発（一気に出るので 0 のはず）");
                    if (shownChars != total || shownCx != 0 || curseShown > 0.001f)
                        throw new System.InvalidOperationException(
                            $"侵食度 {level}: 出た直後に 全文 {shownChars}/{total}・切られた字 {shownCx}・斑 {curseShown:F3}（全文・0・0 のはず）");
                    if (sweepStart < 0 || sweepEnd < 0)
                        throw new System.InvalidOperationException($"侵食度 {level}: 塗り替わりが始まらない／終わらない（{sweepStart}〜{sweepEnd}）");
                    int sweepFrames = sweepEnd - sweepStart;
                    if (Mathf.Abs(sweepFrames - CommsPossessionLogic.SweepSec * Fps) > 2f)
                        throw new System.InvalidOperationException($"侵食度 {level}: 塗り替わりが {sweepFrames} コマ（{CommsPossessionLogic.SweepSec * Fps:0} のはず）");
                    if (sfxHits != 1)
                        throw new System.InvalidOperationException($"侵食度 {level}: 塗り替わりの音が {sfxHits} 発（1 発のはず）");
                    if (rampFrame < 0 || rampFrame != sweepEnd)
                        throw new System.InvalidOperationException($"侵食度 {level}: 斑が全面になる縁（{rampFrame}）が塗り替わり切った縁（{sweepEnd}）と違う");
                }
            }
            File.WriteAllText(Path.Combine(motionDir, "summary.tsv"), summary.ToString());
            Debug.Log($"[CommsPreview] 呪いの動き 4 段 → {motionDir}\n{summary}");
        }

        /// <summary>
        /// 画素検査のための画面座標（px・左上原点）: 地の矩形（毛羽立ちの余白を除く）と本文の帯と顔の枠。
        /// ⚠ 地の quad は毛羽立ちのぶん広い（<c>CommsPanel.PlateMarginM</c>）。矩形は余白を引いて出す。
        /// </summary>
        private static string GeometryJson(CommsPanel panel, Camera cam, TMP_Text tmp, Transform root)
        {
            float margin = ConstF(typeof(CommsPanel), "PlateMarginM", 0.05f) * root.localScale.x;
            var plate = panel.transform.Find("CommsRoot/CommsPanelQuad");
            var face = panel.transform.Find("CommsRoot/CommsAvatar");
            string Rect(Transform? t, float inset)
            {
                if (t == null) return "null";
                Vector3 s = t.lossyScale;
                Vector3 a = cam.WorldToScreenPoint(t.position + t.rotation * new Vector3(-s.x * 0.5f + inset, -s.y * 0.5f + inset, 0f));
                Vector3 b = cam.WorldToScreenPoint(t.position + t.rotation * new Vector3(s.x * 0.5f - inset, s.y * 0.5f - inset, 0f));
                return FormattableString.Invariant(
                    $"{{\"x0\":{Mathf.Min(a.x, b.x):0.0},\"x1\":{Mathf.Max(a.x, b.x):0.0},\"y0\":{H - Mathf.Max(a.y, b.y):0.0},\"y1\":{H - Mathf.Min(a.y, b.y):0.0}}}");
            }
            Bounds tb = tmp.textBounds;
            Vector3 ta = cam.WorldToScreenPoint(tmp.transform.TransformPoint(tb.min));
            Vector3 tz = cam.WorldToScreenPoint(tmp.transform.TransformPoint(tb.max));
            string text = FormattableString.Invariant(
                $"{{\"x0\":{Mathf.Min(ta.x, tz.x):0.0},\"x1\":{Mathf.Max(ta.x, tz.x):0.0},\"y0\":{H - Mathf.Max(ta.y, tz.y):0.0},\"y1\":{H - Mathf.Min(ta.y, tz.y):0.0}}}");
            return "{\"width\":" + W + ",\"height\":" + H + ",\"fps\":" + Fps
                   + ",\"plate\":" + Rect(plate, margin) + ",\"plateQuad\":" + Rect(plate, 0f)
                   + ",\"face\":" + Rect(face, 0f) + ",\"text\":" + text + "}";
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
