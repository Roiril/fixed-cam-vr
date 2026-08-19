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
    /// <b>入れ替わりのノイズ</b>（`canon/LEDGER.md` 0089）を連番 PNG へ焼く。Play しない。
    ///
    /// 音と違って画は撮れるが、**この演出は 2.6 秒の時間の形そのもの**なので静止画では判定できない
    /// （砂が湧く速さ・縮む速さ・晴れ方は 1 枚では 1 ビットも読めない）。だから連番で焼いて
    /// <c>tools/make-preview-video.py</c> で動画にする。
    ///
    /// <b>本番と同じものを使う</b>（写経した式は必ずいつか食い違う）:
    ///   - 進み方は <see cref="SwapMorphLogic"/>（実機の <see cref="SwapMorphFx"/> が回すのと同じ純ロジック）
    ///   - 砂の形は Stage が描く**人形のシルエットそのもの**（実機と同じ RT・同じ縮尺）
    ///   - 合成は実シェーダ <c>FixedCamVr/ScreenComposite</c> の
    ///     <c>_SwapRect</c> / <c>_SwapCover</c> / <c>_SwapSolid</c> / <c>_SwapSeed</c>
    ///
    /// ⚠⚠ <b>1 つだけ実機と違う</b>: 実機の「体験者」は<b>映像の中</b>に居る（ライブ / 録画の実写）。
    ///   Editor には体験者が居ないので、**人の姿を CG の人体（Remy）で代役**にしてある。
    ///   砂に覆われるのが実写か CG かの違いだけで、覆う側（シルエット・砂・段の進み）は同一。
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

        /// <summary>体験者の代役に使う人体プレハブ（Resources 配下）。</summary>
        private const string SwapHumanPrefab = "ShowActors/Remy";

        /// <summary>代役の背丈 (m)。実機は HMD の高さから解く（<see cref="SwapMorphLogic.HumanHeightFrom"/>）。</summary>
        private const float SwapHumanHeightM = SwapMorphLogic.FallbackHumanHeightM;

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
                Plate plate = stage.LoadPlate(cam != null ? cam.id : "");
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

                written += RenderSwap(stage, plate, geom, post, doll, human, standXz, standYaw,
                                      SwapMorphLogic.Dir.ToDoll, written);
                written += RenderSwap(stage, plate, geom, post, doll, human, standXz, standYaw,
                                      SwapMorphLogic.Dir.ToHuman, written);
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

        /// <summary>1 方向ぶんを焼く。戻り値は書いたコマ数。</summary>
        private static int RenderSwap(Stage stage, Plate plate, Geometry geom, PostParams post,
                                      ShowActorDef doll, ShowActorDef human,
                                      Vector2 standXz, float standYaw,
                                      SwapMorphLogic.Dir dir, int frameBase)
        {
            bool toDoll = dir == SwapMorphLogic.Dir.ToDoll;
            string title = toDoll ? "L3C0  visitor -> doll" : "L4C0  doll -> visitor";
            const string note = "** the person here is a CG stand-in; on the device it is the live video";

            ShowActorDef first = toDoll ? human : doll;
            ShowActorDef last = toDoll ? doll : human;
            float fromH = toDoll ? SwapHumanHeightM : Mathf.Max(0.05f, doll.heightM);
            float toH = toDoll ? Mathf.Max(0.05f, doll.heightM) : SwapHumanHeightM;

            int n = 0;
            float capH = fromH, capCover = 0f, capSolid = 0f;
            ShowActorDef stageActor = first;

            void Shoot(string phase, float sec)
            {
                string cap = $"{title}   {phase}   t={sec:0.00}s\n" +
                             $"figure height {capH:0.00}m   sand cover {capCover:0.00}   " +
                             $"solid {capSolid:0.00}\n{note}";
                stage.Composite(plate, cgVisible: true, post: post, caption: cap);
                stage.Save($"f{frameBase + n:0000}.png");
                n++;
            }

            // ---- 頭: ふつうの画（何が入れ替わるのかを読ませる）----
            stageActor = first;
            stage.UseActor(first);
            stage.PlaceActor(first, standXz, standYaw, geom);
            stage.SetActorHeight(first, fromH, geom);
            stage.SetGroundMul(1f);
            stage.SetSwap(false, 0f, 0f, 0f);
            for (int i = 0; i < SwapHeadFrames; i++)
                Shoot("before", -(SwapHeadFrames - i) / (float)SwapFps);

            // ---- 入れ替わり ----
            var logic = new SwapMorphLogic();
            logic.Begin(dir, SwapMorphLogic.DefaultTotalSec, fromH, toH);
            const float dt = 1f / SwapFps;
            float t = 0f;
            bool covered = false;         // もう砂が覆い切ったか（段の名前に要る）
            ShowActorDef cur = first;     // いま立っている姿
            for (int guard = 0; guard < 600 && logic.Active; guard++)
            {
                SwapMorphLogic.Sample s = logic.Tick(dt);
                t += dt;
                if (s.justCovered) covered = true;

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
                stage.SetSwap(true, s.cover, s.solid, t);
                capH = s.heightM; capCover = s.cover; capSolid = s.solid;
                Shoot(PhaseLabel(s, dir, covered), t);
            }

            // ---- 尾: 入れ替わった後のふつうの画 ----
            // 人 → 人形は人形が残る。人へ戻る向きは実機なら CG が消えて実写の体験者が出る所なので、
            // ここでだけ代役を出して「戻った先に人が居る」を絵にする。
            if (!ReferenceEquals(last, stageActor))
            {
                stageActor = last;
                stage.UseActor(last);
                stage.PlaceActor(last, standXz, standYaw, geom);
            }
            stage.SetSwap(false, 0f, 0f, 0f);
            stage.SetGroundMul(1f);
            stage.SetActorHeight(last, toH, geom);
            capH = toH; capCover = 0f; capSolid = toDoll ? 1f : 0f;
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
            if (!covered) return "1 sand rises over the figure";
            if (s.solid > 0.001f) return "3 sand settles into the doll";
            if (s.cover < 0.999f) return "3 sand drains away";
            return dir == SwapMorphLogic.Dir.ToDoll ? "2 shrinks to doll size" : "2 grows to human size";
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
