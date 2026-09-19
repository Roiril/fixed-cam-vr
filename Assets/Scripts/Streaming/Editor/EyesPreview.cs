#nullable enable
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// <b>闇に目が開く異変を 1 コマずつ焼く</b>（<c>canon/LEDGER.md</c> 0075）。
    ///
    /// ⚠ <b>実シェーダ <c>FixedCamVr/AnomalyEyes</c> と実ロジック</b>（<see cref="AnomalyEyesLogic"/> ＋
    /// <see cref="AnomalyEyesMesh"/>）を通す。模写ではないので、開く順・大きさ・虹彩の彫りは実機と同じ式。
    ///
    /// ⚠ <b>実機の見え方そのものではない</b> — 両眼視差・レンズ・パススルーは入っていない。
    /// スクリーンは<b>置き換えの暗い板</b>（本編の映像は出さない）。目がその外に出ることだけを見る絵。
    ///
    /// 使い方: <c>.\tools\unity.ps1 menu eyes</c> → <c>tools/make-preview-video.py</c> で mp4 へ。
    /// </summary>
    public static class EyesPreview
    {
        private const string OutDirRel = "Screenshots/eyes-preview";
        private const int BaseW = 960, BaseH = 540;
        private const int Fps = 30;

        /// <summary>
        /// 出力の画素数。既定 960×540。<c>-Set scale=2</c> で 1920×1080 ＝ Quest 3 の片眼の密度
        /// （約 19 画素/度）に並ぶ。⚠ 帯の目（0237）は帯 1 本が 2〜8 画素なので、
        /// 既定の密度では帯の継ぎ目が AA に溶けて「実機より疎ら」に見える。細部の判定は scale=2 で。
        /// </summary>
        private static int W = BaseW, H = BaseH;

        /// <summary>撮影台。他の scene 幾何が写り込まないよう、誰も居ない高さへ。</summary>
        private static readonly Vector3 Stage = new(0f, 2000f, 0f);

        /// <summary>
        /// 持続を見せる尺 (秒)。全部開いてからも回す（瞬き・震え・<b>待機中の視線</b>が見える）。
        ///
        /// ⚠⚠ <b>2.0 秒では視線の動きが 1 度も写らない</b>（2026-08-17・<c>canon/LEDGER.md</c> 0084）。
        /// 視線は開き切って <see cref="AnomalyEyesLogic.GazeRiseSec"/>（0.9 秒）後に立ち上がり、
        /// そこから <b>1.25 秒に 1 度</b>飛ぶ。5 秒あれば 3〜4 回の飛びが写る。
        /// </summary>
        private const float HoldTailSec = 5.0f;

        /// <summary>縦画角 (度)。16:9 で水平 約 102° ＝ Quest 3 の表示画角に近い。</summary>
        private const float FovDeg = 70f;

        // 置き換えのスクリーン（本編の実測: 水平 ±30.6° / 垂直 ±18.4° / 8° 下）。
        private const float ScreenDistM = 2.0f;
        private const float ScreenHalfYawDeg = 30.6f;
        private const float ScreenHalfPitchDeg = 18.4f;
        private const float ScreenDropDeg = 8f;

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Anomaly Eyes", priority = 233)]
        public static void Run()
        {
            string dir = Path.Combine(Application.dataPath, OutDirRel);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);

            Shader? sh = Shader.Find(AnomalyEyes.ShaderName);
            if (sh == null)
            {
                Debug.LogError($"[EyesPreview] シェーダ {AnomalyEyes.ShaderName} が見つかりません");
                return;
            }

            float density = ParseArg("density", 1f);
            float scale = Mathf.Clamp(ParseArg("scale", 1f), 0.5f, 4f);
            W = Mathf.RoundToInt(BaseW * scale);
            H = Mathf.RoundToInt(BaseH * scale);
            // ⚠ **プレビュー限定の早回し**。出荷する尺（AnomalyEyesLogic の const）は 1 つも変えない。
            //   止まっている 3 つ（闇 / 断片のまま静止 / 凝視）だけを、この秒数へ詰めて見せる。
            float trim = ParseArg("trim", 0f);
            float holdTail = ParseArg("hold", HoldTailSec);

            var camGo = new GameObject("[EyesPreview] Camera");
            var eyesGo = new GameObject("[EyesPreview] Eyes");
            var screenGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            screenGo.name = "[EyesPreview] Screen (placeholder)";
            var mat = new Material(sh) { name = "AnomalyEyes (preview)" };
            Material? screenMat = null;
            Mesh? mesh = null;
            int frames = 0;

            try
            {
                EyeSeat[] seats = AnomalyEyesMesh.BuildSeats();
                mesh = AnomalyEyesMesh.Build(seats);
                eyesGo.transform.SetPositionAndRotation(Stage, Quaternion.identity);
                eyesGo.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = eyesGo.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

                // 置き換えのスクリーン。**目がこの外に出ること**を見るためだけの板。
                Shader? plain = Shader.Find("Universal Render Pipeline/Unlit");
                if (plain != null)
                {
                    screenMat = new Material(plain) { name = "Screen (preview)" };
                    screenMat.SetColor("_BaseColor", new Color(0.10f, 0.09f, 0.08f, 1f));
                    screenMat.SetColor("_Color", new Color(0.10f, 0.09f, 0.08f, 1f));
                    screenGo.GetComponent<Renderer>().sharedMaterial = screenMat;
                }
                float hw = ScreenDistM * Mathf.Tan(ScreenHalfYawDeg * Mathf.Deg2Rad);
                float hh = ScreenDistM * Mathf.Tan(ScreenHalfPitchDeg * Mathf.Deg2Rad);
                float drop = ScreenDistM * Mathf.Tan(ScreenDropDeg * Mathf.Deg2Rad);
                screenGo.transform.position = Stage + new Vector3(0f, -drop, ScreenDistM);
                screenGo.transform.rotation = Quaternion.identity;
                screenGo.transform.localScale = new Vector3(hw * 2f, hh * 2f, 1f);

                var cam = camGo.AddComponent<Camera>();
                cam.fieldOfView = FovDeg;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 100f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.transform.SetPositionAndRotation(Stage, Quaternion.identity);

                var logic = new AnomalyEyesLogic();
                float dt = 1f / Fps;
                float clock = 0f;
                var ledger = new System.Text.StringBuilder();
                ledger.AppendLine("frame\tsec\tstage\tbig\tfield\tintensity\topen");

                // 早回しでは「実時間で何秒回すか」が変わる。段の中をどこまで進めたかは logicT が持つ。
                float logicT = 0f;
                float total = TrimmedOpenSec(trim) + holdTail;
                for (float t = 0f; t < total; t += dt)
                {
                    float step = dt * SpeedAt(logicT, trim);
                    logicT += step;
                    logic.Tick(step, wanted: true, density: density);
                    clock += dt;
                    Write(mat, logic, clock);
                    Shoot(cam, Path.Combine(dir, $"f{frames:0000}.png"));
                    int open = AnomalyEyesMesh.CountOpen(seats, logic.Big, logic.Field, logic.Density);
                    ledger.Append(frames).Append('\t').Append(F(t + dt)).Append('\t')
                          .Append(logic.Stage).Append('\t')
                          .Append(F(logic.Big)).Append('\t').Append(F(logic.Field)).Append('\t')
                          .Append(F(logic.Intensity)).Append('\t').Append(open).Append('\n');
                    frames++;
                }

                // ⚠⚠ **閉じる段も撮る**（2026-08-17・`canon/LEDGER.md` 0084）。
                //    ここを撮っていなかったので、0.45 秒の一様なフェードが
                //    「閉じた」ではなく「電源が落ちた」に見えることに誰も気づけなかった。
                for (float t = 0f; t < AnomalyEyesLogic.CloseSec + dt; t += dt)
                {
                    logic.Tick(dt, wanted: false, density: density);
                    clock += dt;
                    Write(mat, logic, clock);
                    Shoot(cam, Path.Combine(dir, $"f{frames:0000}.png"));
                    int open = AnomalyEyesMesh.CountOpen(seats, logic.Big, logic.Field, logic.Density);
                    ledger.Append(frames).Append('\t').Append(F(total + t + dt)).Append('\t')
                          .Append(logic.Stage).Append('\t')
                          .Append(F(logic.Big)).Append('\t').Append(F(logic.Field)).Append('\t')
                          .Append(F(logic.Intensity)).Append('\t').Append(open).Append('\n');
                    frames++;
                }

                // 段ごとの静止画（数値が緑でも絵は必ず開く — rules/visual-verification.md §7）。
                // ⚠ 早回しでは撮らない。ここは著作の尺で刻んでいるので、混ぜると
                //   「どの尺の絵なのか」が分からない 1 組ができる。
                if (trim > 0f)
                {
                    File.WriteAllText(Path.Combine(dir, "frames.tsv"), ledger.ToString());
                    Debug.Log($"[EyesPreview] 早回し {frames} コマ → Assets/{OutDirRel}/\n"
                            + $"  止まる 3 つを {trim:F2}s へ（闇 / 断片のまま静止 / 凝視）\n"
                            + $"  開き切るまで {TrimmedOpenSec(trim):F2}s → 開けっ放し {holdTail:F2}s"
                            + $" → 閉じ {AnomalyEyesLogic.CloseSec}s\n"
                            + "  ⚠ 出荷する尺は 1 つも変えていない（プレビュー限定の早回し）");
                    return;
                }
                Shot(cam, mat, dir, "stage_1_hint_quarter", AnomalyEyesLogic.HintSec * 0.25f, density, 0f);
                Shot(cam, mat, dir, "stage_1_hint_end", AnomalyEyesLogic.HintSec, density, 0f);
                float stareEnd = AnomalyEyesLogic.HintSec + AnomalyEyesLogic.StareSec;
                Shot(cam, mat, dir, "stage_2_stare", stareEnd - 0.2f, density, 0f);
                Shot(cam, mat, dir, "stage_3_swarm_half",
                     stareEnd + AnomalyEyesLogic.SwarmSec * 0.5f, density, 0f);
                float full = stareEnd + AnomalyEyesLogic.SwarmSec + 0.5f;
                Shot(cam, mat, dir, "stage_4_hold", full, density, 0f);
                // 360 度いちめん（振り返っても目が居ることを見る）。
                Shot(cam, mat, dir, "hold_yaw090", full, density, 90f);
                Shot(cam, mat, dir, "hold_yaw180", full, density, 180f);
                Shot(cam, mat, dir, "hold_yaw270", full, density, 270f);
                // 割合を下げた版（カットの eyes が 1 未満のとき）。
                Shot(cam, mat, dir, "hold_density040", full, 0.40f, 0f);

                // ⚠⚠ **閉じる 3 段**（`canon/LEDGER.md` 0084）。開いた順の逆 —
                //    いちめんが閉じる → 大きい目だけ残って止まる → 最後にすっと閉じる。
                //    ここを並べて見ないと「間」が間になっているか分からない。
                float closeMid = 0.5f * (AnomalyEyesLogic.CloseFieldAt + AnomalyEyesLogic.CloseHoldAt);
                Shot(cam, mat, dir, "close_1_field", full,
                     density, 0f, AnomalyEyesLogic.CloseSec * AnomalyEyesLogic.CloseFieldAt * 0.5f);
                Shot(cam, mat, dir, "close_2_pause", full,
                     density, 0f, AnomalyEyesLogic.CloseSec * closeMid);
                Shot(cam, mat, dir, "close_3_last", full,
                     density, 0f, AnomalyEyesLogic.CloseSec * (AnomalyEyesLogic.CloseHoldAt + 0.13f));

                File.WriteAllText(Path.Combine(dir, "frames.tsv"), ledger.ToString());
                Debug.Log($"[EyesPreview] {frames} コマ + 静止画 12 枚 + frames.tsv → Assets/{OutDirRel}/\n"
                        + $"  目 {seats.Length} 個（候補 {AnomalyEyesMesh.CandidateCount} から重ならないものを詰めた）"
                        + $" / 割合 {density:F2}\n"
                        + $"  兆し {AnomalyEyesLogic.HintSec}s → 凝視 {AnomalyEyesLogic.StareSec}s → "
                        + $"開眼 {AnomalyEyesLogic.SwarmSec}s（全開まで "
                        + $"{AnomalyEyesLogic.HintSec + AnomalyEyesLogic.StareSec + AnomalyEyesLogic.SwarmSec}s）\n"
                        + "  ⚠ 両眼視差・レンズ・パススルーは入っていない。スクリーンは置き換えの暗い板");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(camGo);
                UnityEngine.Object.DestroyImmediate(eyesGo);
                UnityEngine.Object.DestroyImmediate(screenGo);
                UnityEngine.Object.DestroyImmediate(mat);
                if (screenMat != null) UnityEngine.Object.DestroyImmediate(screenMat);
                if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        /// <summary>
        /// <b>止まっている 3 つを <paramref name="trim"/> 秒へ詰めたときの、開き切るまでの実時間</b>。
        /// 0 以下なら著作どおり（4.92 秒）。
        /// </summary>
        private static float TrimmedOpenSec(float trim)
        {
            float open = AnomalyEyesLogic.HintSec + AnomalyEyesLogic.StareSec + AnomalyEyesLogic.SwarmSec;
            if (trim <= 0f) return open;
            // 止まっている 3 つ ＝ 闇 / 開き切ってからの静止 / 凝視（0138 で「断片のまま静止」は無くなった）。
            float dark = AnomalyEyesLogic.HintOnsetSec;
            float held = AnomalyEyesLogic.HintSec - AnomalyEyesLogic.HintFullSec;
            return open - dark - held - AnomalyEyesLogic.StareSec + trim * 3f;
        }

        /// <summary>
        /// いま段のどこに居るかで決まる<b>早回しの倍率</b>（プレビュー限定）。
        ///
        /// 詰めるのは<b>止まっている 3 つだけ</b> — 闇 / 開き切ってからの静止 / 凝視。
        /// 動いている所（音に沿って開く・さざめき・間・一気に）は<b>1 倍のまま</b>で、
        /// そこを速めると 0076 の緩急（止まる と 一気に）と 0138 の音との一致が消える。
        ///
        /// ⚠ 凝視を詰めると、その中の瞬き（尺の 11%）も一緒に縮む。
        /// ⚠ 早回しでは音と合わない（音は詰められない）。音との一致を見るなら trim を渡さないこと。
        /// </summary>
        private static float SpeedAt(float logicT, float trim)
        {
            if (trim <= 0f) return 1f;
            float hint = AnomalyEyesLogic.HintSec;
            if (logicT < hint)
            {
                if (logicT < AnomalyEyesLogic.HintOnsetSec)
                    return AnomalyEyesLogic.HintOnsetSec / trim;
                if (logicT < AnomalyEyesLogic.HintFullSec) return 1f;
                return (hint - AnomalyEyesLogic.HintFullSec) / trim;
            }
            if (logicT < hint + AnomalyEyesLogic.StareSec) return AnomalyEyesLogic.StareSec / trim;
            return 1f;
        }

        /// <summary>
        /// その時刻まで実ロジックを回して 1 枚撮る（頭の向きを <paramref name="yawDeg"/> へ）。
        /// <paramref name="closeSec"/> を渡すと、開き切ったあと**その秒数ぶん閉じてから**撮る。
        /// </summary>
        private static void Shot(Camera cam, Material mat, string dir,
                                 string name, float sec, float density, float yawDeg,
                                 float closeSec = 0f)
        {
            var logic = new AnomalyEyesLogic();
            float dt = 1f / Fps;
            float clock = 0f;
            for (float t = 0f; t < sec; t += dt) { logic.Tick(dt, true, density); clock += dt; }
            for (float t = 0f; t < closeSec; t += dt) { logic.Tick(dt, false, density); clock += dt; }
            Write(mat, logic, clock);
            cam.transform.rotation = Quaternion.Euler(0f, yawDeg, 0f);
            Shoot(cam, Path.Combine(dir, name + ".png"));
            cam.transform.rotation = Quaternion.identity;
        }

        private static void Write(Material mat, AnomalyEyesLogic l, float clock)
        {
            mat.SetFloat("_EyeBig", l.Big);
            mat.SetFloat("_EyeField", l.Field);
            mat.SetFloat("_EyeSpan", l.Span);
            mat.SetFloat("_EyeDensity", l.Density);
            mat.SetFloat("_EyeFade", l.Fade);
            mat.SetFloat("_EyeIntensity", l.Intensity);
            mat.SetFloat("_EyeTime", clock);
            // ⚠⚠ **本番（AnomalyEyes.cs）が配る値をここでも全部配る。**
            //    2026-08-17 に _EyeGaze を足したとき、ここへ足し忘れて
            //    **プレビューだけ視線が動かないまま**「動きが小さい」と誤診した。
            mat.SetFloat("_EyeGaze", l.Gaze);
            mat.SetFloat("_EyeSmile", l.Smile);
            mat.SetFloat("_EyeClosing", l.Closing);
            // ⚠⚠ **明るさ・瞬き・色も配る。** 2026-08-23 まで書いておらず、プレビューだけ
            //    シェーダ既定（明るさ 1.0・生成りに近い白）で描かれていた ＝
            //    **明るさを触っても絵が 1 画素も変わらない**計器だった。
            //    ⚠ 足し忘れは `AnomalyEyesPreviewParityTests` が落とす（2 度目なので機械で守る）。
            mat.SetFloat("_EyeGain", AnomalyEyes.DefaultGain);
            mat.SetFloat("_EyeBlink", AnomalyEyes.DefaultBlink);
            mat.SetColor("_EyeColor", AnomalyEyes.DefaultColor);
            // 版（0238）。本番と同じ Resources から引く。無ければ本番と同じく 1 画素も出ない（警告は本番と同じ文言）。
            if (!mat.HasProperty("_EyeTex") || mat.GetTexture("_EyeTex") == null)
            {
                var tex = Resources.Load<Texture2D>(AnomalyEyes.TexResourcePath);
                if (tex != null) mat.SetTexture("_EyeTex", tex);
                else Debug.LogWarning($"[EyesPreview] 目の版 Resources/{AnomalyEyes.TexResourcePath} が見つかりません。"
                                      + "絵は真っ黒になります（`py -3.11 tools/make-eye-glitch.py` で焼く）。");
            }
        }

        private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);

        private static float ParseArg(string key, float fallback)
        {
            string raw = EditorCliArgs.Get(key) ?? "";
            return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                ? v : fallback;
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
