#nullable enable
using System;
using System.IO;
using FixedCamVr.Streaming.Cg;
using FixedCamVr.Tracking;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// <b>入れ替わりのほどけ</b>（`canon/LEDGER.md` 0089 / 0090）を連番 PNG へ焼く。Play しない。
    ///
    /// 音と違って画は撮れるが、**この演出は 2.6 秒の時間の形そのもの**なので静止画では判定できない
    /// （砂が湧く速さ・縮む速さ・晴れ方は 1 枚では 1 ビットも読めない）。だから連番で焼いて
    /// <c>tools/make-preview-video.py</c> で動画にする。
    ///
    /// <b>本番と同じものを使う</b>（写経した式は必ずいつか食い違う）:
    ///   - 進み方は <see cref="SwapMorphLogic"/>（実機の <see cref="SwapMorphFx"/> が回すのと同じ純ロジック）
    ///   - 糸の形は Stage が描く**人形のシルエットそのもの**（実機と同じ RT・同じ縮尺）
    ///   - 合成は実シェーダ <c>FixedCamVr/ScreenComposite</c> の
    ///     <c>_SwapRect</c> / <c>_SwapCover</c> / <c>_SwapKnot</c> / <c>_SwapThread</c> /
    ///     <c>_SwapReal</c> / <c>_SwapFromTop</c> / <c>_SwapSeed</c>
    ///
    /// ⚠⚠ <b>1 つだけ実機と違う</b>: 実機の「体験者」は<b>映像の中</b>に居る（ライブ / 録画の実写）。
    ///   Editor には体験者が居ないので、**人の姿を CG の人体（Remy）で代役**にしてある。
    ///   ほどけるのが実写か CG かの違いだけで、ほどく側（シルエット・糸・段の進み）は同一。
    ///   キャプションに毎回そう書く（何の絵か分からない PNG は証拠にならない）。
    ///
    /// 出力は <c>Assets/Screenshots/swap/</c>。
    /// </summary>
    public static partial class ShowCompositePreview
    {
        private const string SwapOutDirRel = "Screenshots/swap";

        /// <summary>連番の刻み（<c>tools/make-preview-video.py</c> の FPS と揃える）。</summary>
        private const int SwapFps = 30;

        /// <summary>入れ替わりの前後に置く「ふつうの画」のコマ数（何が入れ替わったのか読ませる間）。</summary>
        private const int SwapHeadFrames = 12;
        private const int SwapTailFrames = 24;

        /// <summary>
        /// 持続の覆い（0102）を見せるコマ数。<b>実機の凍結の尺（1.2 秒）より長く取る</b> —
        /// 「止まっているあいだに薄まらないか」は短い区間では読めない。
        /// </summary>
        private const int SwapHoldFrames = 45;

        /// <summary>プレビューで使う覆いの左端（実機の 3 周目 A の著作値と同じ）。</summary>
        private const float SwapHoldMinX = 0.5f;

        /// <summary>体験者の代役に使う人体プレハブ（Resources 配下）。</summary>
        private const string SwapHumanPrefab = "ShowActors/Remy";

        /// <summary>代役の背丈 (m)。実機は HMD の高さから解く（<see cref="SwapMorphLogic.HumanHeightFrom"/>）。</summary>
        private const float SwapHumanHeightM = SwapMorphLogic.FallbackHumanHeightM;

        /// <summary>
        /// 黒い波の層の切り替え（<c>-Set layers=ABDEF</c>）。
        ///
        /// ⚠⚠ <b>層は 1 つずつ入れて焼く</b>（設計 5 節）。まとめて入れると「どれが効いたか」が消える。
        /// 既定（指定なし / <c>all</c>）は全部 on。<c>none</c> で 1 つも入れない ＝ 前の版の絵。
        /// </summary>
        private readonly struct SwapLayers
        {
            /// <summary>A 走る波 / B 細いほつれ / C 稀に跳ぶ帯 / D キメの一拍。</summary>
            public readonly bool Crest, Needle, Spike, Beat;

            /// <summary>E 全身のエネルギー（歩速） / F 局所のホットスポット（手） / H 余韻（晴れ方）。</summary>
            public readonly bool Energy, Hot, Afterglow;

            public SwapLayers(bool crest, bool needle, bool spike, bool beat,
                              bool energy, bool hot, bool afterglow)
            {
                Crest = crest; Needle = needle; Spike = spike; Beat = beat;
                Energy = energy; Hot = hot; Afterglow = afterglow;
            }

            public static SwapLayers Parse(string? spec)
            {
                if (string.IsNullOrEmpty(spec) || spec == "all")
                    return new SwapLayers(true, true, true, true, true, true, true);
                string s = spec!.ToUpperInvariant();
                if (s == "NONE") return new SwapLayers(false, false, false, false, false, false, false);
                return new SwapLayers(s.Contains("A"), s.Contains("B"), s.Contains("C"),
                                      s.Contains("D"), s.Contains("E"), s.Contains("F"),
                                      s.Contains("H"));
            }

            /// <summary>絵に焼くラベル（何が入った絵なのか分からない PNG は証拠にならない）。</summary>
            public string Label =>
                (Crest ? "A" : "-") + (Needle ? "B" : "-") + (Spike ? "C" : "-") +
                (Beat ? "D" : "-") + (Energy ? "E" : "-") + (Hot ? "F" : "-") +
                (Afterglow ? "H" : "-");

            /// <summary>切った層の成分を 0 に倒す（シェーダ側は 0 で従来の絵に戻る）。</summary>
            public SwapWaveLogic.Wave Apply(in SwapWaveLogic.Wave w)
                => new SwapWaveLogic.Wave(
                    Crest ? w.crestY : 0f,
                    Crest ? w.crestAmp : 0f,
                    Needle ? w.needle : 0f,
                    Spike ? w.spike : 0f,
                    w.elapsed,
                    Beat ? w.beat : 0f,
                    Afterglow ? w.clear01 : 0f,
                    Energy ? w.ampMul : 1f);
        }

        /// <summary>
        /// 代役の体の入力を<b>台本</b>で作る（設計 5 節「E / F はプレビューに台本の動きが要る」）。
        /// <b>静止 → 歩行 → 手を振る</b>を入れ替わりの尺の中で演じさせる。これが無いと
        /// 応答の層（E / F）は絵で判定できず、実機頼みになる。
        ///
        /// ⚠⚠ <b>実機との 2 つ目の違い</b>（1 つ目は「体験者が CG の代役」）:
        ///   台本が作るのは<b>体の入力だけ</b>で、代役の絵は歩かないし手も振らない
        ///   （絵を動かすと、映像へ焼き込んだ人と CG がずれて別の判定不能が生まれる）。
        ///   ⇒ ここで判定できるのは「波がエネルギーに応えるか」「手の周りが毛羽立つか」の 2 つだけ。
        ///   <b>絵と体の一致は実機で被って歩くまで分からない。</b>
        /// </summary>
        private static ShowBodyInput ScriptedBody(float t, float total, Vector3 foot,
                                                  float heightM, float yawDeg)
        {
            float standSec = 0.35f * total;
            float walkSec = 0.35f * total;
            Quaternion rot = Quaternion.Euler(0f, yawDeg, 0f);
            Vector3 right = rot * Vector3.right;
            Vector3 fwd = rot * Vector3.forward;

            // 歩行は**半径 0.25m の円を 1.1 m/s で回る**。sin の往復にすると速さが脈打って
            // エネルギーが 0 と全開を往復する（＝ 何に反応しているのか絵から読めない）。
            Vector3 sway = Vector3.zero;
            if (t > standSec)
            {
                const float r = 0.25f, v = 1.1f;
                float w = v / r;
                float tw = Mathf.Min(t - standSec, walkSec);   // 手を振る段では歩みを止める
                sway = right * (r * (Mathf.Cos(w * tw) - 1f)) + fwd * (r * Mathf.Sin(w * tw));
            }

            // 手を振るのは**右だけ**（左と比べられないと「手に反応した」が読めない）。
            float waveT = Mathf.Max(0f, t - (standSec + walkSec));
            float swing = waveT > 0f ? Mathf.Sin(waveT * 2f * Mathf.PI * 1.6f) * 0.30f : 0f;
            Vector3 shoulder = foot + Vector3.up * (heightM * 0.80f) + sway;
            return new ShowBodyInput(
                true, foot + Vector3.up * heightM + sway, yawDeg,
                true, shoulder - right * 0.26f,
                true, shoulder + right * (0.26f + swing) + Vector3.up * (swing * 0.5f));
        }

        // public なのは CLI（`unity.ps1 menu swap`）が -executeMethod で直接呼ぶため。
        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Swap Morph", priority = 253)]
        public static void RunSwap() => ExecuteSwap(EditorCliArgs.Get("show"));

        private static void ExecuteSwap(string? explicitShowJsonPath)
        {
            // CLI（batchmode）は空シーンで始まる。枠のアスペクトはシーンの MjpegScreen が正。
            if (!EditorCliArgs.EnsureScene("Assets/Scenes/Main.unity")) return;

            string outDir = Path.Combine(Application.dataPath, SwapOutDirRel);
            if (Directory.Exists(outDir))
                foreach (string old in Directory.GetFiles(outDir, "f*.png")) File.Delete(old);
            Directory.CreateDirectory(outDir);

            Stage? stage = null;
            int written = 0;
            try
            {
                stage = Stage.Create(outDir);

                ShowJson? show = LoadShow(explicitShowJsonPath, out string showPath);
                if (show == null)
                {
                    Debug.LogError("[SwapViz] show.json が見つからない → 入れ替わりの絵は出せない");
                    return;
                }
                Debug.Log($"[SwapViz] show.json: {showPath.Replace('\\', '/')}");

                TimelinePresentFlags.Reconcile(show.timeline);
                TimelineMigration.EnsureTakes(show.timeline);
                stage.ApplyLayout(show.layout);
                stage.SetFeel(show.feel);

                // 入れ替わりが起きるのはカメラ A（course の順路の頭）。3 周目 A / 4 周目 A の両方がここ。
                const int cameraIndex = 0;
                PreviewCameraDef? cam = show.CameraAt(cameraIndex);
                // `-Set plate=white` で一様な明るい地に差し替える。**帯の端が読めるかの判定用**
                // （現場の映像は右半分が真っ黒で、黒い覆いが背景と同化して絵から判定できない）。
                Plate plate = EditorCliArgs.Get("plate") == "white"
                    ? stage.MakeFlatPlate(0.90f)
                    : stage.LoadPlate(cam != null ? cam.id : "");
                Geometry geom = stage.AimVirtualCamera(cam, plate.width, plate.height);
                if (!geom.usable)
                {
                    Debug.LogError("[SwapViz] カメラ A の姿勢も較正も未著作 → 人形が立てないので絵にならない");
                    return;
                }
                stage.SetPlateLuma(plate);

                ShowActorDef? doll = show.actors != null && show.actors.Length > 0 ? show.actors[0] : null;
                if (doll == null)
                {
                    Debug.LogError("[SwapViz] show.json の actors[] に人形が 1 体も無い");
                    return;
                }
                // 人の姿は **show.json の actors[] から引く**（実機と同じもの）。
                // 無い show.json でも絵が出るよう、Remy で代用する。
                ShowActorDef human = show.FindActor(TakeSchema.SwapHumanActorId) ?? new ShowActorDef
                {
                    id = TakeSchema.SwapHumanActorId,
                    name = "体験者の代役",
                    prefab = SwapHumanPrefab,
                    heightM = SwapHumanHeightM,
                };

                // 立ち位置は「そのカメラが担当するゾーンの重心」＝ その映像が出ているとき体験者が居るはずの場所。
                Vector2 standXz = new Vector2(doll.fixedX, doll.fixedZ);
                float standYaw = doll.fixedYawDeg;
                if (TryStandInZone(show, cameraIndex, cam, out Vector2 zXz, out float zYaw))
                {
                    standXz = zXz;
                    standYaw = zYaw;
                }

                // 3 周目 A の粗さ（解像度の劣化が最大）で焼く。**入れ替わりが起きるのはその周**なので、
                // 1 周目の鮮明な画で判定すると砂の粒の大きさを見誤る。
                stage.DecayProgress = 1f;
                PostParams post = ShowCompositePreviewPlan.ResolvePost(
                    null, show.CameraPost(cameraIndex), show.post);

                // 黒い波の層（`-Set layers=A` で 1 層だけ、`none` で前の版の絵）。
                SwapLayers layers = SwapLayers.Parse(EditorCliArgs.Get("layers"));
                Debug.Log($"[SwapViz] 黒い波の層: {layers.Label}" +
                          "（A 走る波 / B 針 / C 跳び / D キメ / E 歩速 / F 手 / H 余韻）");

                // `-Set hold=1` で **3 周目 A の入り**（0102）だけを焼く。
                // 包まれたまま → 縮む段から引き継ぐ、という新しい形は 2 本の swap では出ない。
                if (EditorCliArgs.Get("hold") == "1")
                {
                    written += RenderVeilHold(stage, plate, geom, post, doll, human,
                                              standXz, standYaw, layers, written);
                }
                else
                {
                    written += RenderSwap(stage, plate, geom, post, doll, human, standXz, standYaw,
                                          SwapMorphLogic.Dir.ToDoll, layers, written);
                    written += RenderSwap(stage, plate, geom, post, doll, human, standXz, standYaw,
                                          SwapMorphLogic.Dir.ToHuman, layers, written);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SwapViz] 失敗: {e}");
            }
            finally
            {
                stage?.Dispose();
                AssetDatabase.Refresh();
            }

            if (written > 0)
                Debug.Log($"[SwapViz] {written} コマを保存: Assets/{SwapOutDirRel}/f0000.png …\n" +
                          $"  動画にする: py -3.11 tools/make-preview-video.py Assets/{SwapOutDirRel} swap");
        }

        /// <summary>
        /// <b>3 周目 A の入り</b>（`canon/LEDGER.md` 0102）を焼く。
        /// 包まれたまま（段は止まって波だけ走る）→ そこから縮む段へ引き継ぐ、の 1 本。
        ///
        /// ⚠⚠ <b>ここでしか判定できないものが 3 つある</b>:
        ///   ①包んだまま止まっているあいだ、覆いが薄まったり晴れたりしないか
        ///   ②<c>_SwapMinX</c> が効いて<b>左半分の鏡映しの人物を包まない</b>か
        ///   ③引き継いだ 1 フレーム目に「1 度晴れてまた包まれる」が出ないか
        /// ⚠ 凍結（<c>splitFreeze</c>）は静止画のプレートでは絵に出ないので掛けない。
        /// </summary>
        private static int RenderVeilHold(Stage stage, Plate plate, Geometry geom, PostParams post,
                                          ShowActorDef doll, ShowActorDef human,
                                          Vector2 standXz, float standYaw,
                                          SwapLayers layers, int frameBase)
        {
            const string title = "L3C0  wrapped from the start, then drawn in";
            const string note = "** the person here is a CG stand-in; on the device it is the live video";
            const float minX = SwapHoldMinX;
            float humanH = SwapHumanHeightM;
            float dollH = Mathf.Max(0.05f, doll.heightM);

            // 映像の中に人を焼き込む（覆いの相手）。左半分は鏡映しなので同じ 1 枚から出る。
            stage.UseActor(human);
            stage.PlaceActor(human, standXz, standYaw, geom);
            stage.SetActorHeight(human, humanH, geom);
            stage.SetSwap(false, SwapMorphLogic.Sample.Idle, true, 0f);
            Plate platePerson = stage.BakeActorIntoPlate(plate, " + person");
            stage.SetSwapMaskPlate(plate.texture);

            int n = 0;
            float capH = humanH, capCover = 0f, capKnot = 0f, capThread = 0f, capReal = 0f;
            SwapWaveLogic.Wave capWave = SwapWaveLogic.Wave.Idle;
            string capPhase = "";
            float capT = 0f;
            Plate shown = platePerson;
            bool cgOn = false;

            void Shoot()
            {
                string cap = $"{title}   {capPhase}   t={capT:0.00}s\n" +
                             $"figure height {capH:0.00}m   unravel {capCover:0.00}   " +
                             $"knot {capKnot:0.00}   thread {capThread:0.00}   real {capReal:0.00}\n" +
                             $"wave {layers.Label}   crest y {capWave.crestY:+0.00;-0.00}   " +
                             $"amp {capWave.crestAmp:0.00}   " +
                             $"split 0.50 mirrored   veil right of x={minX:0.00}\n{note}";
                stage.Composite(shown, cgVisible: cgOn, post: post, caption: cap);
                stage.Save($"f{frameBase + n:0000}.png");
                n++;
            }

            // 分割（鏡映し）はこの 1 本のあいだ掛け続ける。段 2 の差し替えでも切らない
            // （実機は splitX=0 へ戻すが、覆いの制限は `swapMinX` が別に持っている）。
            stage.SetSplit(0.5f, flipLeft: true);

            var logic = new SwapMorphLogic();
            var wave = new SwapWaveLogic();
            var energy = new SwapEnergyLogic();
            const float dt = 1f / SwapFps;

            // ---- 包まれたまま（実機の `BeginHold`）----
            logic.BeginHold(humanH);
            wave.Begin(SwapMorphLogic.Dir.ToDoll, SwapMorphLogic.DefaultTotalSec);
            stage.UseActor(human);
            stage.PlaceActor(human, standXz, standYaw, geom);
            stage.SetSwapHold(minX, diffHold: true);
            capPhase = "0 wrapped (held)";
            for (int i = 0; i < SwapHoldFrames; i++)
            {
                SwapMorphLogic.Sample s = logic.Tick(dt);
                capT += dt;
                stage.SetActorHeight(human, s.heightM, geom);
                stage.SetGroundMul(s.ground);
                stage.SetSwap(true, s, true, capT, -1f, logic.MaskHeightM);
                ShowBodyInput body = ScriptedBody(capT, SwapMorphLogic.DefaultTotalSec,
                                                  stage.ActorFootWorld, humanH, standYaw);
                energy.Tick(dt, body);
                capWave = layers.Apply(wave.Tick(dt, logic.Progress01,
                    layers.Energy ? energy.AmpMul : SwapEnergyLogic.NeutralEnergy,
                    layers.Energy ? energy.SpeedMul : SwapEnergyLogic.NeutralEnergy));
                stage.SetSwapWave(capWave, body.LeftHandPos, layers.Hot ? energy.LeftHot01 : 0f,
                                  body.RightHandPos, layers.Hot ? energy.RightHot01 : 0f);
                capH = s.heightM; capCover = s.cover; capKnot = s.knot;
                capThread = s.thread; capReal = s.real;
                Shoot();
            }

            // ---- 引き継ぎ（実機の `Begin(..., startCovered: true)`）----
            logic.Begin(SwapMorphLogic.Dir.ToDoll, SwapMorphLogic.DefaultTotalSec,
                        humanH, dollH, startCovered: true);
            wave.Begin(SwapMorphLogic.Dir.ToDoll, SwapMorphLogic.DefaultTotalSec, keepTravel: true);
            ShowActorDef cur = human;
            bool settled = false;
            for (int guard = 0; guard < 600 && logic.Active; guard++)
            {
                SwapMorphLogic.Sample s = logic.Tick(dt);
                capT += dt;
                if (s.justSwapScreen)
                {
                    // 覆いは既に立っているので、この 1 フレームで画が無人へ差し替わる。
                    shown = plate;
                    stage.SetSwapHold(minX, diffHold: false);   // 以後は CG の形へ渡す（実機と同じ）
                }
                if (s.justSettling && !settled)
                {
                    settled = true;
                    cur = doll;
                    stage.UseActor(doll);
                    stage.PlaceActor(doll, standXz, standYaw, geom);
                }
                stage.SetActorHeight(cur, s.heightM, geom);
                stage.SetGroundMul(s.ground);
                cgOn = settled;
                stage.SetSwap(true, s, true, capT, -1f, logic.MaskHeightM);
                if (s.justSwapScreen && layers.Beat) wave.NotifyBeat();
                ShowBodyInput body = ScriptedBody(capT, SwapMorphLogic.DefaultTotalSec,
                                                  stage.ActorFootWorld, humanH, standYaw);
                energy.Tick(dt, body);
                capWave = layers.Apply(wave.Tick(dt, logic.Progress01,
                    layers.Energy ? energy.AmpMul : SwapEnergyLogic.NeutralEnergy,
                    layers.Energy ? energy.SpeedMul : SwapEnergyLogic.NeutralEnergy));
                stage.SetSwapWave(capWave, body.LeftHandPos, layers.Hot ? energy.LeftHot01 : 0f,
                                  body.RightHandPos, layers.Hot ? energy.RightHot01 : 0f);
                capH = s.heightM; capCover = s.cover; capKnot = s.knot;
                capThread = s.thread; capReal = s.real;
                capPhase = settled ? "3 thread settles into the doll" : "2 drawn in, down to doll size";
                Shoot();
            }

            // ---- 尾: 無人プレート ＋ 人形 ----
            stage.SetSwap(false, SwapMorphLogic.Sample.Idle, true, 0f);
            stage.SetSwapWave(SwapWaveLogic.Wave.Idle);
            stage.SetSwapHold(0f, diffHold: false);
            stage.SetGroundMul(1f);
            stage.SetActorHeight(doll, dollH, geom);
            shown = plate;
            cgOn = true;
            capH = dollH; capCover = 0f; capKnot = 0f; capThread = 0f; capReal = 1f;
            capWave = SwapWaveLogic.Wave.Idle;
            capPhase = "after";
            for (int i = 0; i < SwapTailFrames; i++) { capT += dt; Shoot(); }

            stage.SetSplit(0f, flipLeft: false);
            return n;
        }

        /// <summary>1 方向ぶんを焼く。戻り値は書いたコマ数。</summary>
        private static int RenderSwap(Stage stage, Plate plate, Geometry geom, PostParams post,
                                      ShowActorDef doll, ShowActorDef human,
                                      Vector2 standXz, float standYaw,
                                      SwapMorphLogic.Dir dir, SwapLayers layers, int frameBase)
        {
            bool toDoll = dir == SwapMorphLogic.Dir.ToDoll;
            string title = toDoll ? "L3C0  visitor -> doll" : "L4C0  doll -> visitor";
            const string note = "** the person here is a CG stand-in; on the device it is the live video";

            ShowActorDef first = toDoll ? human : doll;
            ShowActorDef last = toDoll ? doll : human;
            float fromH = toDoll ? SwapHumanHeightM : Mathf.Max(0.05f, doll.heightM);
            float toH = toDoll ? Mathf.Max(0.05f, doll.heightM) : SwapHumanHeightM;

            // ⚠⚠ **映像の中に人を焼き込む**（2026-08-20）。入れ替わりの対象は
            //   「映像の中に写っている人」で、CG の人形ではない。プレビューは人を CG の代役で
            //   演じているので、焼き込まないと**映像の中には誰も居ない** ＝
            //   映像から人を拾う経路（`SwapDiffSil`）を絵で確かめられない。
            //   実機の構造（録画 / ライブに人が写っていて、覚いの下で画面が差し替わる）と揃える。
            stage.UseActor(human);
            stage.PlaceActor(human, standXz, standYaw, geom);
            stage.SetActorHeight(human, SwapHumanHeightM, geom);
            stage.SetSwap(false, SwapMorphLogic.Sample.Idle, toDoll, 0f);
            Plate platePerson = stage.BakeActorIntoPlate(plate, " + person");

            stage.UseActor(doll);
            stage.PlaceActor(doll, standXz, standYaw, geom);
            stage.SetActorHeight(doll, Mathf.Max(0.05f, doll.heightM), geom);
            Plate plateDoll = stage.BakeActorIntoPlate(plate, " + doll");

            // 差分の相手は**無人のプレート**（実機は卓が撮った `plate_<ID>_<時刻>.jpg`）。
            stage.SetSwapMaskPlate(plate.texture);

            int n = 0;
            float capH = fromH, capCover = 0f, capKnot = 0f, capThread = 0f;
            float capReal = toDoll ? 0f : 1f;
            // 黒い波（設計 A〜F）。**絵に焼く** — 何の層が入った絵なのか分からない PNG は証拠にならない。
            SwapWaveLogic.Wave capWave = SwapWaveLogic.Wave.Idle;
            float capEnergy = 0f, capHotL = 0f, capHotR = 0f;
            ShowActorDef stageActor = first;
            Plate shown = toDoll ? platePerson : plateDoll;   // いま映像に写っている画
            bool cgOn = false;                                // CG は入れ替わった後の姿だけ

            void Shoot(string phase, float sec)
            {
                string cap = $"{title}   {phase}   t={sec:0.00}s\n" +
                             $"figure height {capH:0.00}m   unravel {capCover:0.00}   " +
                             $"knot {capKnot:0.00}   thread {capThread:0.00}   real {capReal:0.00}\n" +
                             $"wave {layers.Label}   crest y {capWave.crestY:+0.00;-0.00}   " +
                             $"amp {capWave.crestAmp:0.00}   needle {capWave.needle:0.00}   " +
                             $"spike {capWave.spike:0.00}   beat {capWave.beat:0.00}   " +
                             $"clear {capWave.clear01:0.00}   energy {capEnergy:0.00}   " +
                             $"hot L{capHotL:0.00} R{capHotR:0.00}\n{note}";
                stage.Composite(shown, cgVisible: cgOn, post: post, caption: cap);
                stage.Save($"f{frameBase + n:0000}.png");
                n++;
            }

            // ---- 頭: ふつうの画（何が入れ替わるのかを読ませる）----
            stageActor = first;
            stage.UseActor(first);
            stage.PlaceActor(first, standXz, standYaw, geom);
            stage.SetActorHeight(first, fromH, geom);
            stage.SetGroundMul(1f);
            cgOn = false;   // 頭では人も人形も**映像の中**に写っている（CG は出さない）
            stage.SetSwap(false, SwapMorphLogic.Sample.Idle, toDoll, 0f);
            for (int i = 0; i < SwapHeadFrames; i++)
                Shoot("before", -(SwapHeadFrames - i) / (float)SwapFps);

            // ---- 入れ替わり ----
            var logic = new SwapMorphLogic();
            logic.Begin(dir, SwapMorphLogic.DefaultTotalSec, fromH, toH);
            // **本番と同じ純ロジック**（`SwapMorphFx` が回すのと同じもの）。写経した式は必ずいつか食い違う。
            var wave = new SwapWaveLogic();
            wave.Begin(dir, SwapMorphLogic.DefaultTotalSec);
            var energy = new SwapEnergyLogic();
            const float dt = 1f / SwapFps;
            float t = 0f;
            bool covered = false;         // もう砂が覆い切ったか（段の名前に要る）
            ShowActorDef cur = first;     // いま立っている姿
            for (int guard = 0; guard < 600 && logic.Active; guard++)
            {
                SwapMorphLogic.Sample s = logic.Tick(dt);
                t += dt;
                if (s.justCovered) covered = true;
                if (s.justSwapScreen)
                {
                    // ⚠⚠ **画面が差し替わる縁は向きで違う**（実機と同じ `justSwapScreen`）。
                    //   人 → 人形は覆い切った縁で録画（人あり）→ 無人プレート。
                    //   人形 → 人は**育ち切った縁**で無人プレート → 人ありへ（覆いが人の
                    //   大きさになるまで当人を隠せないため）。
                    shown = toDoll ? plate : platePerson;
                }

                // 砂に覆われている間だけ姿を替えられる（替わったことが 1 画素も見えない）。
                // **背丈が動いている間の形は、どちらの向きでも人**（人形を拡大しても人型に見えない）。
                // 実機（`SwapMorphFx`）とまったく同じ縁で替える。
                if (s.justCovered && !toDoll) cur = human;
                else if (s.justSettling && toDoll) cur = doll;
                if (!ReferenceEquals(cur, stageActor))
                {
                    stageActor = cur;
                    stage.UseActor(cur);
                    stage.PlaceActor(cur, standXz, standYaw, geom);
                }

                stage.SetActorHeight(cur, s.heightM, geom);
                stage.SetGroundMul(s.ground);
                // CG が要るのは**入れ替わった後の人形**だけ（人はもう映像の中に居る）。
                cgOn = toDoll && covered;
                // ⚠ マスクは映像の中の人を拾う（`SwapDiffSil`）。人型は縮む / 育つが
                //   映像の中の人は動かないので、引く枠の背丈を別に渡す。
                stage.SetSwap(true, s, toDoll, t, -1f, logic.MaskHeightM);

                // ---- 黒い波（設計 A〜F）。実機（`SwapMorphFx.LateUpdate`）と同じ順で解く ----
                if (s.justSwapScreen && layers.Beat) wave.NotifyBeat();
                // 手と頭は台本。⚠ 手の位置は**実寸の人の背丈**で組む（映像の中の人は縮まない）。
                ShowBodyInput body = ScriptedBody(t, SwapMorphLogic.DefaultTotalSec,
                                                  stage.ActorFootWorld, SwapHumanHeightM, standYaw);
                energy.Tick(dt, body);
                float ampMul = layers.Energy ? energy.AmpMul : SwapEnergyLogic.NeutralEnergy;
                float speedMul = layers.Energy ? energy.SpeedMul : SwapEnergyLogic.NeutralEnergy;
                capWave = layers.Apply(wave.Tick(dt, logic.Progress01, ampMul, speedMul));
                capEnergy = layers.Energy ? energy.Energy01 : 0f;
                capHotL = layers.Hot ? energy.LeftHot01 : 0f;
                capHotR = layers.Hot ? energy.RightHot01 : 0f;
                stage.SetSwapWave(capWave, body.LeftHandPos, capHotL, body.RightHandPos, capHotR);

                capH = s.heightM; capCover = s.cover; capKnot = s.knot;
                capThread = s.thread; capReal = s.real;
                Shoot(PhaseLabel(s, dir, covered), t);
            }

            // ---- 尾: 入れ替わった後のふつうの画 ----
            // 人 → 人形は無人の映像に CG の人形が立つ。人へ戻る向きは映像の中に人が写っている。
            shown = toDoll ? plate : platePerson;
            cgOn = toDoll;
            if (!ReferenceEquals(last, stageActor))
            {
                stageActor = last;
                stage.UseActor(last);
                stage.PlaceActor(last, standXz, standYaw, geom);
            }
            stage.SetSwap(false, SwapMorphLogic.Sample.Idle, toDoll, 0f);
            stage.SetSwapWave(SwapWaveLogic.Wave.Idle);
            stage.SetGroundMul(1f);
            stage.SetActorHeight(last, toH, geom);
            capH = toH; capCover = 0f; capKnot = 0f; capThread = 0f;
            capReal = toDoll ? 1f : 0f;
            capWave = SwapWaveLogic.Wave.Idle;
            capEnergy = 0f; capHotL = 0f; capHotR = 0f;
            for (int i = 0; i < SwapTailFrames; i++) Shoot("after", t + (i + 1) / (float)SwapFps);

            return n;
        }

        /// <summary>
        /// 段の名前。<paramref name="covered"/>（もう覆い切ったか）が要る —
        /// <c>cover &lt; 1</c> は「まだ湧いている」と「もう晴れている」の両方で立つので、
        /// 値だけでは 1 段目と 3 段目を区別できない。
        /// </summary>
        private static string PhaseLabel(SwapMorphLogic.Sample s, SwapMorphLogic.Dir dir, bool covered)
        {
            if (!covered) return "1 unravels into thread";
            if (s.real > 0.001f) return "3 thread settles into the doll";
            if (s.thread < SwapMorphLogic.ThreadAtPull - 1e-3f) return "3 thread fades";
            return dir == SwapMorphLogic.Dir.ToDoll
                ? "2 drawn in, down to doll size" : "2 drawn out, up to human size";
        }

        /// <summary>
        /// そのカメラが担当するゾーンの重心（＝ その映像が出ているとき体験者が居るはずの場所）。
        /// <see cref="TryFollowStand"/> と同じ式だが、区間（Shot）ではなくカメラ index から引く。
        /// </summary>
        private static bool TryStandInZone(ShowJson show, int cameraIndex, PreviewCameraDef? cam,
                                           out Vector2 standXz, out float yawDeg)
        {
            standXz = Vector2.zero;
            yawDeg = 0f;
            ShowGridDef? g = show.layout?.grid;
            if (g == null || g.rows <= 0 || g.cols <= 0 || g.tileM <= 0f) return false;

            int[] cells = ZoneLayoutSolver.ParseGridCells(g.cells, g.rows, g.cols);
            double sx = 0, sz = 0;
            int n = 0;
            for (int r = 0; r < g.rows; r++)
            {
                for (int c = 0; c < g.cols; c++)
                {
                    if (cells[r * g.cols + c] != cameraIndex) continue;
                    ZoneLayoutSolver.CellRect(r, c, g.rows, g.cols, g.tileM,
                        out float xLo, out float xHi, out float zLo, out float zHi);
                    sx += (xLo + xHi) * 0.5;
                    sz += (zLo + zHi) * 0.5;
                    n++;
                }
            }
            if (n == 0) return false;
            standXz = new Vector2((float)(sx / n), (float)(sz / n));

            float camX, camZ;
            if (cam != null && cam.hasCalib && cam.calib != null && cam.calib.IsUsable())
            { camX = cam.calib.x; camZ = cam.calib.z; }
            else if (cam?.pose != null) { camX = cam.pose.x; camZ = cam.pose.z; }
            else return true;

            Vector2 toCam = new Vector2(camX - standXz.x, camZ - standXz.y);
            if (toCam.sqrMagnitude > 1e-6f) yawDeg = Mathf.Atan2(toCam.x, toCam.y) * Mathf.Rad2Deg;
            return true;
        }
    }
}
