#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using FixedCamVr.Streaming.Cg;
using FixedCamVr.Tracking;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// 演出のカットごとに「**実写プレート × CG 人形**」の合成結果を **Play せずに** PNG へ焼く。
    ///
    /// これが合成品質の**一次証拠**になる。理由は 2 つ:
    ///   - 実機確認はユーザーの手作業に依存していて、頻繁には回せない
    ///   - 卓（Web）は意図的に人形を描かない（glTF + スキニング + IK + 影を素の WebGL2 で二重実装すると
    ///     「どちらが正しいか」を確かめる手段が無くなる。設計 2026-07-27_cg-compositing-rebuild.md）
    /// つまり **Unity Editor が唯一「本番と同じ機構で絵を出せる場所」**なので、ここで見えないものは
    /// 現地で HMD を被るまで誰も気づけない。
    ///
    /// **本番と同じものを使う**のが絶対条件（写経した式は必ずいつか食い違い、そうなると証拠として無価値）:
    ///   - 合成は実シェーダ <c>FixedCamVr/ScreenComposite</c>（premultiplied over + post FX 9 項目）
    ///   - 射影・画角・光の向きは <see cref="ShowCgLayer"/> の public static
    ///   - 部屋プロキシは実コンポーネント <see cref="ShowRoomProxy"/>、影 / 接地影は Resources の実マテリアル
    ///   - 撮る対象・カメラ・プレート・post・ファイル名の決め方は <see cref="ShowCompositePreviewPlan"/>（テストあり）
    ///
    /// 出力は <c>Assets/Screenshots/cgviz/</c>。**1 枚ごとに条件を画面下の帯へ焼き込む** —
    /// 何を見ている絵か分からない PNG は証拠にならない（後から並べたときに区別できない）。
    ///
    /// 各カメラ 1 枚の <c>calibcheck_&lt;camera&gt;.png</c> も出す（プレート + 部屋ワイヤー + 床格子のみ・post なし）。
    /// 較正が合っているかを人が判定するための絵で、卓のワイヤー重畳の Unity 版にあたる。
    /// </summary>
    public static class ShowCompositePreview
    {
        private const string OutDirRel = "Screenshots/cgviz";
        private const string CgLayerName = "ShowCg";

        /// <summary>
        /// 合成ステージ（枠 Quad + キャプション）だけを写すための隔離レイヤ。
        /// 組み込みの TransparentFX を一時借用する（RegistrationVizPreview と同じ手口・恒久変更はしない）。
        /// さらにステージ自体を <see cref="StageOriginX"/> だけ離して置くので、開いているシーンの
        /// 同レイヤのオブジェクトが紛れ込むこともない。
        /// </summary>
        private const int PreviewLayer = 1;
        private const float StageOriginX = 1000f;

        /// <summary>出力 PNG の映像部分の縦解像度。キャプション帯はこの下に足される。</summary>
        private const int OutImageHeight = 720;

        /// <summary>
        /// キャプション帯の高さ（映像高に対する比）。映像を隠さないよう**下に足す**。
        /// 0.18（4 行）→ 0.24 は浮き具合の 1 行を足したぶん（折り返しを含めて 6 行まで入る）。
        /// </summary>
        private const float CaptionHeightRatio = 0.24f;

        /// <summary>
        /// キャプションの <c>fontSize</c>。
        ///
        /// ⚠ TMP の **3D テキスト**の fontSize は世界単位ではない（UGUI の TMP は canvas 単位と 1:1 なので、
        ///   そちらの感覚で「行高 = 0.03 世界単位だから 0.03」と置くと 1px 未満の線になり、帯が空に見える）。
        ///   実測で決めた値: **この画づくり（帯 = 映像高の 18% / 出力 850px）で 0.5 が 1 行 39px**。
        ///   0.35 → 1 行約 27px・1 文字約 13px ⇒ 1280px 幅に約 95 文字入り、いちばん長い行（約 90 文字）が
        ///   折り返さずに収まる。4 行に膨らんでも帯（130px）を超えない。
        ///   ⇒ <see cref="CaptionHeightRatio"/> や <see cref="OutImageHeight"/> を変えたら**この値も measure し直す**。
        /// オートサイズは使わない — 行が伸びると際限なく縮み、読めない帯（＝証拠にならない）になる。
        /// </summary>
        private const float CaptionFontSize = 0.35f;

        /// <summary>
        /// スクリーン枠のアスペクト。シーンに <see cref="MjpegScreen"/> が居ればそれを正とし、
        /// 居なければこの値（authored な Quad の localScale 2.3704 x 1.3333 = 16:9）を使う。
        /// </summary>
        private const float FallbackFrameAspect = 16f / 9f;

        /// <summary>プレートが 1 枚も無いカメラで代用する単色板の寸法（streamer / IP Camera Lite と同じ 4:3）。</summary>
        private const int FallbackPlateW = 640;
        private const int FallbackPlateH = 480;

        /// <summary>CG の RT 縦解像度の上限。<c>ShowCgLayer.renderHeightPx</c> の既定と同じ値にしてある。</summary>
        private const int CgRenderHeightMax = 720;

        /// <summary>人形を idle（体側）へ収束させるために回す Drive の回数。実行時の 0.35s 合流ぶん。</summary>
        private const int ActorSettleFrames = 60;

        // プレートの置き場。卓の保存先（captures/）を先に見て、無ければデモ撮影（recordings/）から拾う。
        // どちらも命名規約が同じ（cam<ID>_<stamp>.<ext>）なので、同じ規則で選べる。
        private static readonly string[] PlateDirsRel =
        {
            "tools/web-compositor/captures",
            "tools/web-compositor/recordings",
        };

        private static readonly string[] ShowJsonCandidatesRel =
        {
            "tools/web-compositor/show.json",
            "Assets/StreamingAssets/show/show.json",
        };

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Show Composite", priority = 252)]
        private static void Run() => Execute(null);

        /// <summary>
        /// show.json を明示して同じ処理を回す（batchmode / 検証用エントリ）。
        /// 実データを触らずに「この姿勢なら人形はどこに立つか」を試すのに使う
        /// — 卓の show.json は演出中に書き換わるので、試しの値を書き込む場所ではない。
        /// </summary>
        public static void RunFor(string showJsonPath) => Execute(showJsonPath);

        private static void Execute(string? explicitShowJsonPath)
        {
            var saved = new List<string>();
            string outDir = Path.Combine(Application.dataPath, OutDirRel);
            Directory.CreateDirectory(outDir);

            Stage? stage = null;
            try
            {
                stage = Stage.Create(outDir);

                ShowJson? show = LoadShow(explicitShowJsonPath, out string showPath);
                if (show == null)
                {
                    // 「何も出ない」で終わらせない。足りないものを PNG にも書いて残す。
                    stage.WriteMessageCard("missing_show_json.png",
                        "show.json NOT FOUND",
                        "looked for: " + (explicitShowJsonPath ?? string.Join(" , ", ShowJsonCandidatesRel)),
                        saved);
                    return;
                }
                Debug.Log($"[CgViz] show.json: {showPath.Replace('\\', '/')}");

                // 本番のロード経路と同じ順で正規化する。present-flag を先に確定させてから v2→v3 変換
                // （逆順にすると、幽霊の既定オブジェクトが take へ変換されて出力に湧く）。
                TimelinePresentFlags.Reconcile(show.timeline);
                TimelineMigration.EnsureTakes(show.timeline);

                stage.ApplyLayout(show.layout);
                stage.SetFeel(show.feel);

                List<ShowCompositePreviewPlan.Shot> shots =
                    ShowCompositePreviewPlan.CollectShots(show.timeline);
                Debug.Log($"[CgViz] CG 人形が出るカット: {shots.Count} 件 / カメラ {show.cameras.Length} 台");

                foreach (ShowCompositePreviewPlan.Shot shot in shots)
                    RenderShot(stage, show, shot, saved);

                // 較正確認は「人形が出るカメラ」ではなく**全カメラ**に対して出す。
                // 姿勢がまだ無いカメラこそ、何が足りないかを見せる必要がある。
                for (int i = 0; i < show.cameras.Length; i++)
                    RenderCalibCheck(stage, show, i, saved);

                if (shots.Count == 0)
                    Debug.LogWarning("[CgViz] CG 人形を指定したカット（steps[].cg）が show.json に 1 つも無い" +
                                     " → 合成の絵は出せない（卓のカット編集で「CG 人形」を選ぶと出る）");
            }
            catch (Exception e)
            {
                Debug.LogError($"[CgViz] 失敗: {e}");
            }
            finally
            {
                stage?.Dispose();
                AssetDatabase.Refresh();
            }

            if (saved.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[CgViz] {saved.Count} 枚を保存:");
                foreach (string p in saved) sb.AppendLine("  " + p.Replace('\\', '/'));
                Debug.Log(sb.ToString());
            }
        }

        // ---- 1 カット分の合成 ----

        private static void RenderShot(Stage stage, ShowJson show,
                                       ShowCompositePreviewPlan.Shot shot, List<string> saved)
        {
            ShowStepDef step = shot.step;
            PreviewCameraDef? cam = show.CameraAt(shot.camera);
            string camId = cam != null ? cam.id : "?";

            Plate plate = stage.LoadPlate(camId);
            Geometry geom = stage.AimVirtualCamera(cam, plate.width, plate.height);
            stage.SetAura(step.aura);

            // 人形（actors[] に無ければ出せない）。
            ShowActorDef? actor = show.FindActor(step.cg);
            var notes = new List<string>();
            bool dollShown = false;

            if (actor == null)
            {
                notes.Add($"NO DOLL: actor '{Ascii(step.cg)}' not in show.json actors[]");
            }
            else if (!geom.usable)
            {
                // 本番（ShowCgLayer.Apply）が「出さない」を選ぶのと同じ条件。当てずっぽうのパースで
                // 出すと「浮いている / 床に埋まっている」絵になり、合っているのか判断できなくなる。
                notes.Add("NO DOLL: camera pose/calib not authored");
            }
            else
            {
                dollShown = true;
            }

            Vector2 standXz = Vector2.zero;
            float standYaw = 0f;
            if (dollShown && actor != null)
            {
                // 立ち位置は本番と同じ「カットの placement が優先 / 無ければ actor の既定」。
                bool hasPlacement = step.hasPlacement && step.placement != null;
                bool follow = TakeSchema.NormalizeCgMode(step.cgMode, out _) != TakeSchema.CgFixed;

                if (hasPlacement)
                {
                    standXz = new Vector2(step.placement!.x, step.placement.z);
                    standYaw = step.placement.yawDeg;
                }
                else if (follow && TryFollowStand(show, shot, cam, out Vector2 fXz, out float fYaw))
                {
                    // follow は体験者の HMD 位置に立つ。Editor に体験者は居ないが、**その区間のゾーンの重心**
                    // なら「その映像が出ているとき体験者が居るはずの場所」になる（区間とゾーンは同義なので）。
                    // 著作位置 (0,0) へ置くより実際に近く、大きさ・接地・遮蔽の判定に使える。
                    // 向きはカメラの方へ向ける。実機は体験者の頭の向きなので**ここだけは本番と違う** —
                    // 顔が見える向きの方が、人形の大きさとパースを確かめる目的に合う。
                    standXz = fXz;
                    standYaw = fYaw;
                    notes.Add("cgMode=follow -> zone centroid (where the wearer would stand), facing camera");
                }
                else
                {
                    standXz = new Vector2(actor.fixedX, actor.fixedZ);
                    standYaw = actor.fixedYawDeg;
                    // ゾーンが読めないときだけ著作位置で代用する。黙って別の場所へ立たせると
                    // 「ずれている」と誤診するので、代用したことを明示する。
                    if (follow) notes.Add("cgMode=follow -> stands at authored pos (no zone grid)");
                }

                // 人形の光量を実写へ寄せる倍率は、**人形を置く前**に実写側の明るさから決まる
                // （ShowCgLayer では MjpegScreen.SourceLuma が同じ役をする）。
                stage.SetPlateLuma(plate);
                stage.PlaceActor(actor, standXz, standYaw, geom);
            }
            else
            {
                stage.HideActor();
            }

            if (plate.missing) notes.Add("NO PLATE: flat gray substitute");

            // 足元が枠から出ていないか。立ち位置は演出の管轄（cgMode=follow なら体験者次第）なので
            // コードでは直さないが、黙って出すと「なぜか浮いて見える」の原因へ到達できない。
            float footV = stage.FootViewportV();
            if (dollShown && !float.IsNaN(footV) && (footV < 0f || footV > 1f))
            {
                notes.Add($"FEET OUT OF FRAME (v={footV:0.00}) no ground contact visible");
                Debug.LogWarning($"[CgViz] {ShowCompositePreviewPlan.ShotFileName(shot)}: " +
                                 "人形の足元が映像の外（接地の手掛かりが画に無い）" +
                                 " — 立ち位置かカメラの画角を見直す");
            }

            PostParams post = ShowCompositePreviewPlan.ResolvePost(step, show.CameraPost(shot.camera), show.post);

            // 3 行に割る。1 行へ詰め込むと折り返しで読み順が崩れ、警告（** で始まる行）を見落とす。
            string caption =
                $"lap{shot.lap} cam{shot.camera}({Ascii(camId)}) take {ShowCompositePreviewPlan.SanitizeToken(shot.takeId)}" +
                $" step{shot.stepIndex} src={Ascii(step.source)}\n" +
                $"plate {plate.label} | geom {geom.label}\n" +
                $"doll '{Ascii(step.cg)}' {Ascii(step.cgMode)} " +
                (dollShown ? $"at({standXz.x:0.00},{standXz.y:0.00}) yaw{standYaw:0}" : "(not shown)") +
                (notes.Count > 0 ? "   ** " + string.Join("  ** ", notes) : "");

            stage.Composite(plate, cgVisible: dollShown, post: post, caption: caption);

            // 浮き具合は**合成した後の絵**からしか測れない。測って、その 1 行を足して描き直す。
            // Editor の 1 枚あたり数十 ms なので、証拠が 1 行増えるほうが安い。
            string blend = dollShown ? stage.MeasureBlend() : "";
            if (blend.Length > 0)
            {
                stage.Composite(plate, cgVisible: true, post: post, caption: caption + "\n" + blend);
                // 絵を 1 枚ずつ開かなくても並べて読めるよう、ログにも出す（比較はここが一番速い）。
                Debug.Log($"[CgViz] {ShowCompositePreviewPlan.ShotFileName(shot)}  {blend}");
            }

            saved.Add(stage.Save(ShowCompositePreviewPlan.ShotFileName(shot)));
        }

        // ---- 較正確認（プレート + 部屋ワイヤー + 床格子。人形も post も無し）----

        private static void RenderCalibCheck(Stage stage, ShowJson show, int index, List<string> saved)
        {
            PreviewCameraDef? cam = show.CameraAt(index);
            string camId = cam != null ? cam.id : "?";
            Plate plate = stage.LoadPlate(camId);
            Geometry geom = stage.AimVirtualCamera(cam, plate.width, plate.height);

            stage.HideActor();

            string note;
            if (!geom.usable)
            {
                note = "** cannot draw: no pose/calib for this camera";
                stage.ShowWire(false);
            }
            else
            {
                note = "wire=room proxy + floor grid (post OFF: plate shown raw)";
                stage.ShowWire(true);
            }

            // post は掛けない。較正の判定はプレートの素の絵に対して行うもので、グレーディングを乗せると
            // 「ずれ」と「色の効き」が混ざって読めなくなる（かつワイヤーの視認性も落ちる）。
            // 撮像の質も掛けない。較正のずれを見る絵に粒と露出の揺れを足すと、
            // 「ずれ」と「装置の癖」が混ざって読めなくなる（post を掛けないのと同じ理由）。
            stage.Composite(plate, cgVisible: geom.usable, post: new PostParams(),
                            caption: $"CALIB CHECK cam{index}({Ascii(camId)})\n" +
                                     $"plate {plate.label} | geom {geom.label}\n{note}",
                            applyFeel: false);
            saved.Add(stage.Save(ShowCompositePreviewPlan.CalibCheckFileName(index)));
            stage.ShowWire(false);
        }

        /// <summary>
        /// <c>cgMode=follow</c> のカットで、体験者が居ると想定される立ち位置と向きを解く。
        ///
        /// 体験者は**その区間のゾーン**に居る（区間 = (lap, camera) で、camera はゾーンのカメラ）。
        /// だから重心を取れば「その映像が出ているとき体験者が立っているはずの場所」になる。
        /// ⚠ 見る視点は <c>shot.camera</c>（そのカットを撮ったカメラ）で、立ち位置は
        ///   <c>shot.segmentCamera</c>（体験者が居る区間）から取る。インサートでは両者が食い違う。
        ///
        /// 向きはカメラの方（顔が見える向き）。実機は体験者の頭の向きなので、ここは意図的に本番と違う。
        /// </summary>
        private static bool TryFollowStand(ShowJson show, ShowCompositePreviewPlan.Shot shot,
            PreviewCameraDef? cam, out Vector2 standXz, out float yawDeg)
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
                    if (cells[r * g.cols + c] != shot.segmentCamera) continue;
                    ZoneLayoutSolver.CellRect(r, c, g.rows, g.cols, g.tileM,
                        out float xLo, out float xHi, out float zLo, out float zHi);
                    sx += (xLo + xHi) * 0.5;
                    sz += (zLo + zHi) * 0.5;
                    n++;
                }
            }
            if (n == 0) return false;
            standXz = new Vector2((float)(sx / n), (float)(sz / n));

            // カメラの XZ は較正が正（無ければ概算姿勢）。どちらも無ければ向きは 0 のままにする。
            float camX, camZ;
            if (cam != null && cam.hasCalib && cam.calib != null && cam.calib.IsUsable())
            { camX = cam.calib.x; camZ = cam.calib.z; }
            else if (cam?.pose != null) { camX = cam.pose.x; camZ = cam.pose.z; }
            else return true;

            Vector2 toCam = new Vector2(camX - standXz.x, camZ - standXz.y);
            if (toCam.sqrMagnitude > 1e-6f) yawDeg = Mathf.Atan2(toCam.x, toCam.y) * Mathf.Rad2Deg;
            return true;
        }

        // ---- 合成ステージ（生成物一式。finally で必ず畳む）----

        /// <summary>仮想カメラの構え方の結果。<see cref="usable"/> が false なら人形もワイヤーも出さない。</summary>
        private struct Geometry
        {
            public bool usable;
            public bool fromCalib;
            public string label;     // キャプションへ焼く 1 行（ASCII）
        }

        private struct Plate
        {
            public Texture2D texture;
            public int width;
            public int height;
            public bool missing;
            public string label;
        }

        private sealed class Stage : IDisposable
        {
            private readonly GameObject _root;
            private readonly string _outDir;
            private readonly int _cgLayer;
            private readonly float _frameAspect;

            private readonly Camera _cgCam;
            private readonly Camera _outCam;
            private readonly Material _compositeMat;
            private readonly Transform _quad;
            private readonly TextMeshPro _caption;

            private readonly ShowControlClient _showControl;
            private readonly ShowRoomProxy _roomProxy;

            private readonly GameObject _wireRoot;
            private Mesh? _gridMesh;
            private Mesh? _boxMesh;

            private GameObject? _actor;
            private ShowActorRig? _rig;
            private Transform? _blob;
            private Material? _shadowMat;
            private Material? _blobMat;

            private RenderTexture? _cgRt;
            /// MSAA の RT は直接 ReadPixels できないので、測定用に非 MSAA へ解決してから読む。
            private RenderTexture? _cgResolve;
            private Texture2D? _cgRead;
            private RenderTexture? _outRt;
            private Texture2D? _readback;
            private Texture2D? _grayPlate;
            private readonly Dictionary<string, Texture2D?> _plateCache = new();

            private int _outW, _outH;
            /// 直近の Composite で使った contain-fit。測定が同じ枠で座標を戻すのに要る。
            private Vector2 _contain = Vector2.one;

            private Stage(GameObject root, string outDir, int cgLayer, float frameAspect,
                          Camera cgCam, Camera outCam, Material compositeMat, Transform quad,
                          TextMeshPro caption, ShowControlClient showControl, ShowRoomProxy roomProxy,
                          GameObject wireRoot)
            {
                _root = root; _outDir = outDir; _cgLayer = cgLayer; _frameAspect = frameAspect;
                _cgCam = cgCam; _outCam = outCam; _compositeMat = compositeMat; _quad = quad;
                _caption = caption; _showControl = showControl; _roomProxy = roomProxy; _wireRoot = wireRoot;
            }

            public static Stage Create(string outDir)
            {
                int cgLayer = LayerMask.NameToLayer(CgLayerName);
                if (cgLayer < 0)
                    throw new InvalidOperationException(
                        $"レイヤ '{CgLayerName}' が未定義（Project Settings > Tags and Layers に追加する）");

                // 枠のアスペクトは authored な Quad が正。シーンが開いていればそこから読む
                // （ここを取り違えると letterbox 帯の幅が実機と変わり、post の見え方も変わる）。
                var screen = UnityEngine.Object.FindObjectOfType<MjpegScreen>();
                float frameAspect = screen != null && screen.ScreenAspect > 0.01f
                    ? screen.ScreenAspect : FallbackFrameAspect;
                Debug.Log($"[CgViz] 枠アスペクト {frameAspect:0.###}" +
                          (screen != null ? "（シーンの MjpegScreen から取得）" : "（既定 16:9）"));

                var root = new GameObject("[CgVizPreview]") { hideFlags = HideFlags.HideAndDontSave };
                root.transform.position = new Vector3(StageOriginX, 0f, 0f);

                // --- CG を描く仮想カメラ。設定は ShowCgLayer.EnsureCamera と同一にする ---
                var cgCamGo = new GameObject("CgVirtualCamera");
                cgCamGo.transform.SetParent(root.transform, worldPositionStays: false);
                var cgCam = cgCamGo.AddComponent<Camera>();
                cgCam.clearFlags = CameraClearFlags.SolidColor;
                cgCam.backgroundColor = new Color(0f, 0f, 0f, 0f);   // 透明背景 = 被覆率がアルファに出る
                cgCam.cullingMask = 1 << cgLayer;
                cgCam.nearClipPlane = 0.05f;
                cgCam.farClipPlane = 30f;
                cgCam.allowHDR = false;
                cgCam.allowMSAA = true;  // 本番と同じ（人形の輪郭だけギザギザだと実写のなまった縁と食い違う）
                cgCam.enabled = false;   // 手動 Render のみ（Edit Mode で勝手に描かせない）

                // --- 合成ステージ（枠 Quad + 直交カメラ + キャプション帯）---
                var stageGo = new GameObject("CompositeStage");
                stageGo.transform.SetParent(root.transform, worldPositionStays: false);

                var quadGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quadGo.name = "ScreenQuad";
                quadGo.transform.SetParent(stageGo.transform, worldPositionStays: false);
                // 世界 1 単位 = 映像 1 枚分の高さ。映像はキャプション帯のぶん上へ寄せる。
                quadGo.transform.localPosition = new Vector3(0f, CaptionHeightRatio * 0.5f, 0f);
                quadGo.transform.localScale = new Vector3(frameAspect, 1f, 1f);
                Collider? quadCol = quadGo.GetComponent<Collider>();
                if (quadCol != null) DestroyImmediate(quadCol);

                Shader? compositeShader = Shader.Find("FixedCamVr/ScreenComposite");
                if (compositeShader == null)
                    throw new InvalidOperationException("シェーダ 'FixedCamVr/ScreenComposite' が見つからない");
                // ⚠ _CgSoften は**設定しない**。シェーダ側の既定 0.7 が ShowCgLayer.CgSoftenTexels と
                //    同値なので、ここで書くと二重定義になり片方だけ変えたときに黙って食い違う。
                var compositeMat = new Material(compositeShader) { name = "ScreenComposite (cgviz)" };
                quadGo.GetComponent<Renderer>().sharedMaterial = compositeMat;

                var outCamGo = new GameObject("CompositeCamera");
                outCamGo.transform.SetParent(stageGo.transform, worldPositionStays: false);
                outCamGo.transform.localPosition = new Vector3(0f, 0f, -1f);
                var outCam = outCamGo.AddComponent<Camera>();
                outCam.orthographic = true;
                outCam.orthographicSize = (1f + CaptionHeightRatio) * 0.5f;
                outCam.nearClipPlane = 0.1f;
                outCam.farClipPlane = 5f;
                outCam.clearFlags = CameraClearFlags.SolidColor;
                outCam.backgroundColor = Color.black;   // キャプション帯の地
                outCam.cullingMask = 1 << PreviewLayer;
                outCam.allowHDR = false;
                outCam.allowMSAA = false;
                outCam.enabled = false;

                // キャプションは **ASCII のみ**。Edit Mode の TMP は静的アトラスに焼かれていない字を
                // 豆腐で組む（日本語 HUD フォントは HUD の文言ぶんしか焼いていない・[[hud_font_and_preview]]）。
                // 読めない帯を出すくらいなら英数字で確実に読ませる。
                // 帯は映像の**下**（世界 y ∈ [-0.57, -0.43] / 中心 -0.50）。映像を隠さないための配置で、
                // 地の色は outCam の clear（黒）がそのまま出るので背景板は要らない。
                var capGo = new GameObject("Caption");
                capGo.transform.SetParent(stageGo.transform, worldPositionStays: false);
                capGo.transform.localPosition = new Vector3(
                    0f, -(1f + CaptionHeightRatio) * 0.5f + CaptionHeightRatio * 0.5f, -0.5f);
                var caption = capGo.AddComponent<TextMeshPro>();
                var capRt = (RectTransform)caption.transform;
                capRt.sizeDelta = new Vector2(frameAspect - 0.02f, CaptionHeightRatio);
                caption.alignment = TextAlignmentOptions.TopLeft;
                caption.richText = false;
                caption.enableWordWrapping = true;   // 長い行は折り返す（切り落とさない）
                caption.fontSize = CaptionFontSize;
                caption.color = new Color(0.95f, 0.95f, 0.7f, 1f);

                var showGo = new GameObject("ShowControlClient");
                showGo.transform.SetParent(root.transform, worldPositionStays: false);
                // Edit Mode では Awake / Start が走らないので long-poll もキャッシュ読みも起きない。
                // 使うのは「layout を配る器」としての面だけ（SetLayoutForPreview → Room）。
                var showControl = showGo.AddComponent<ShowControlClient>();

                var proxyGo = new GameObject("[CgRoomProxy]");
                proxyGo.transform.SetParent(root.transform, worldPositionStays: false);
                proxyGo.layer = cgLayer;
                var roomProxy = proxyGo.AddComponent<ShowRoomProxy>();
                roomProxy.Initialize(showControl, cgLayer);

                var wireRoot = new GameObject("Wire");
                wireRoot.transform.SetParent(root.transform, worldPositionStays: false);
                wireRoot.SetActive(false);

                SetLayerRecursive(stageGo.transform, PreviewLayer);

                return new Stage(root, outDir, cgLayer, frameAspect, cgCam, outCam, compositeMat,
                                 quadGo.transform, caption, showControl, roomProxy, wireRoot);
            }

            // ---- layout（部屋プロキシ + 較正確認のワイヤー）----

            public void ApplyLayout(ShowLayoutDef? layout)
            {
                _showControl.SetLayoutForPreview(layout);
                _roomProxy.Sync();
                BuildWire(layout);
                Debug.Log($"[CgViz] 部屋プロキシ: room={( _roomProxy.HasRoom ? "あり" : "なし（床は course y=0）")}" +
                          $" / オクルーダ {_roomProxy.OccluderCount} 個");
            }

            /// <summary>
            /// 較正確認用のワイヤー（床格子 + 部屋プロキシの稜線）を組む。線分トポロジのメッシュ 1 枚ずつ
            /// なので、ボックスが何個あっても描画は 2 回で済む。course→world は identity（未登録のため）。
            /// </summary>
            private void BuildWire(ShowLayoutDef? layout)
            {
                ShowRoomDef? room = _showControl.Room;
                float floorY = room != null ? room.floorY : 0f;
                float w = room != null ? room.floorW : (layout?.floor != null ? layout.floor.w : 1.8f);
                float d = room != null ? room.floorD : (layout?.floor != null ? layout.floor.d : 1.8f);

                var gridVerts = new List<Vector3>();
                const float step = 0.3f;
                int nx = Mathf.Max(1, Mathf.RoundToInt(w / step));
                int nz = Mathf.Max(1, Mathf.RoundToInt(d / step));
                float hw = w * 0.5f, hd = d * 0.5f;
                for (int i = 0; i <= nx; i++)
                {
                    float x = -hw + w * i / nx;
                    gridVerts.Add(new Vector3(x, floorY, -hd));
                    gridVerts.Add(new Vector3(x, floorY, hd));
                }
                for (int i = 0; i <= nz; i++)
                {
                    float z = -hd + d * i / nz;
                    gridVerts.Add(new Vector3(-hw, floorY, z));
                    gridVerts.Add(new Vector3(hw, floorY, z));
                }

                var boxVerts = new List<Vector3>();
                foreach (ShowRoomProxyLogic.Box b in ShowRoomProxyLogic.Build(room))
                    AppendBoxEdges(boxVerts, b);

                _gridMesh = ReplaceLineMesh(_gridMesh, gridVerts, "CgVizGrid");
                _boxMesh = ReplaceLineMesh(_boxMesh, boxVerts, "CgVizBoxes");

                foreach (Transform child in _wireRoot.transform) DestroyImmediate(child.gameObject);
                AddWireObject(_gridMesh, new Color(0.25f, 0.85f, 1f, 1f));
                AddWireObject(_boxMesh, new Color(1f, 0.55f, 0.15f, 1f));
            }

            private static void AppendBoxEdges(List<Vector3> into, ShowRoomProxyLogic.Box b)
            {
                Quaternion rot = Quaternion.Euler(0f, b.yawDeg, 0f);
                Vector3 h = b.size * 0.5f;
                var c = new Vector3[8];
                for (int i = 0; i < 8; i++)
                {
                    var local = new Vector3((i & 1) == 0 ? -h.x : h.x,
                                            (i & 2) == 0 ? -h.y : h.y,
                                            (i & 4) == 0 ? -h.z : h.z);
                    c[i] = b.center + rot * local;
                }
                // 立方体の 12 稜線（頂点 index の 1 bit 違い = 隣接）。
                for (int i = 0; i < 8; i++)
                    foreach (int bit in new[] { 1, 2, 4 })
                        if ((i & bit) == 0) { into.Add(c[i]); into.Add(c[i | bit]); }
            }

            private Mesh ReplaceLineMesh(Mesh? old, List<Vector3> verts, string name)
            {
                if (old != null) DestroyImmediate(old);
                var mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
                var indices = new int[verts.Count];
                for (int i = 0; i < indices.Length; i++) indices[i] = i;
                mesh.SetVertices(verts);
                mesh.SetIndices(indices, MeshTopology.Lines, 0);
                mesh.RecalculateBounds();
                return mesh;
            }

            private void AddWireObject(Mesh mesh, Color color)
            {
                if (mesh.vertexCount == 0) return;
                var go = new GameObject("WireMesh") { layer = _cgLayer };
                go.transform.SetParent(_wireRoot.transform, worldPositionStays: false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"))
                { name = "CgVizWire", hideFlags = HideFlags.HideAndDontSave };
                mat.SetColor("_BaseColor", color);
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }

            public void ShowWire(bool on)
            {
                _wireRoot.SetActive(on);
                // 部屋プロキシは色を書かない（深度だけ）オクルーダなので、ワイヤーを隠してしまう。
                // 較正確認のあいだは畳む。
                _roomProxy.gameObject.SetActive(!on);
            }

            // ---- プレート ----

            public Plate LoadPlate(string cameraId)
            {
                Texture2D? tex = LoadPlateTexture(cameraId, out string label);
                if (tex != null)
                    return new Plate { texture = tex, width = tex.width, height = tex.height,
                                       missing = false, label = label };

                _grayPlate ??= MakeGrayPlate();
                return new Plate { texture = _grayPlate, width = FallbackPlateW, height = FallbackPlateH,
                                   missing = true, label = $"(none) {FallbackPlateW}x{FallbackPlateH} gray" };
            }

            private Texture2D? LoadPlateTexture(string cameraId, out string label)
            {
                label = "(none)";
                if (string.IsNullOrEmpty(cameraId)) return null;
                if (_plateCache.TryGetValue(cameraId, out Texture2D? cached))
                {
                    if (cached == null) return null;
                    label = $"{Ascii(cached.name)} {cached.width}x{cached.height}";
                    return cached;
                }

                string? repoRoot = Directory.GetParent(Application.dataPath)?.FullName;
                Texture2D? loaded = null;
                if (repoRoot != null)
                {
                    foreach (string rel in PlateDirsRel)
                    {
                        string dir = Path.Combine(repoRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                        if (!Directory.Exists(dir)) continue;
                        string[] names = Array.ConvertAll(Directory.GetFiles(dir), Path.GetFileName);
                        string? pick = ShowCompositePreviewPlan.PickPlate(names, cameraId);
                        if (pick == null) continue;

                        // sRGB / bilinear は実行時の MJPEG テクスチャと同じ扱い（Linear 空間プロジェクト）。
                        var tex = new Texture2D(2, 2, TextureFormat.RGB24, mipChain: false)
                        { name = pick, hideFlags = HideFlags.HideAndDontSave };
                        if (tex.LoadImage(File.ReadAllBytes(Path.Combine(dir, pick))))
                        {
                            loaded = tex;
                            break;
                        }
                        Debug.LogWarning($"[CgViz] プレートを読めない: {pick}");
                        DestroyImmediate(tex);
                    }
                }

                _plateCache[cameraId] = loaded;
                if (loaded == null) return null;
                label = $"{Ascii(loaded.name)} {loaded.width}x{loaded.height}";
                return loaded;
            }

            private static Texture2D MakeGrayPlate()
            {
                var tex = new Texture2D(FallbackPlateW, FallbackPlateH, TextureFormat.RGB24, mipChain: false)
                { name = "gray", hideFlags = HideFlags.HideAndDontSave };
                var px = new Color32[FallbackPlateW * FallbackPlateH];
                for (int i = 0; i < px.Length; i++) px[i] = new Color32(96, 96, 100, 255);
                tex.SetPixels32(px);
                tex.Apply();
                return tex;
            }

            // ---- 仮想カメラ ----

            /// <summary>
            /// 較正（あれば）または概算姿勢で仮想カメラを構える。**式は自前で持たず
            /// <see cref="ShowCgLayer"/> の public static を呼ぶ** — 本番とずれた射影で撮った絵は証拠にならない。
            /// course→world は identity（プレビューは未登録＝course 座標がそのままワールド）。
            /// </summary>
            public Geometry AimVirtualCamera(PreviewCameraDef? cam, int srcW, int srcH)
            {
                EnsureRenderTargets(srcW, srcH);
                float aspect = (float)srcW / Mathf.Max(1, srcH);

                ShowCameraCalibDef? calib = cam != null && cam.hasCalib && cam.calib != null
                                            && cam.calib.IsUsable() ? cam.calib : null;
                // 較正はレンズ・解像度に従属する。プレートの実寸と合わなければ本番と同じく無効へ倒す
                // （lensId はプレートに付いてこないので空で照合＝両方非空のときだけ効く既存規則に乗る）。
                if (calib != null && !calib.MatchesSource(srcW, srcH, null))
                {
                    Debug.LogWarning($"[CgViz] カメラ '{cam!.id}' の較正は撮り方が違う" +
                                     $"（較正時 {calib.srcW}x{calib.srcH} / プレート {srcW}x{srcH}）→ 概算姿勢を使う");
                    calib = null;
                }

                if (calib != null)
                {
                    Transform t = _cgCam.transform;
                    t.position = CourseToWorld(new Vector2(calib.x, calib.z), calib.y);
                    t.rotation = Quaternion.Euler(-calib.pitchDeg, calib.yawDeg, calib.rollDeg);
                    _cgCam.aspect = (float)calib.srcW / Mathf.Max(1, calib.srcH);
                    _cgCam.projectionMatrix = ShowCgLayer.BuildProjectionMatrix(
                        calib.fxPx, calib.fyPx, calib.cxPx, calib.cyPx, calib.srcW, calib.srcH,
                        _cgCam.nearClipPlane, _cgCam.farClipPlane);
                    SetLens(calib.k1, calib.cxPx / calib.srcW, calib.cyPx / calib.srcH,
                            calib.fxPx / calib.srcW, calib.fyPx / calib.srcH);
                    return new Geometry
                    {
                        usable = true,
                        fromCalib = true,
                        label = $"calib rms{calib.rmsPx:0.0}px pts{calib.pointCount} k1={calib.k1:0.000}",
                    };
                }

                bool hasPose = cam != null && cam.hasPose && cam.pose != null;
                if (!hasPose)
                {
                    _cgCam.ResetProjectionMatrix();
                    SetLens(0f, 0.5f, 0.5f, 1f, 1f);
                    // 「値はあるのに flag が false」は卓の未移行でしか起きない。ここを黙って
                    // 「未著作」とだけ言うと、著作者は何を直せばいいのか分からない。
                    string why = cam != null && LooksAuthored(cam.pose)
                        ? "pose:PRESENT-BUT-hasPose=false (console not migrated)"
                        : "pose:NONE calib:NONE";
                    return new Geometry { usable = false, fromCalib = false, label = why };
                }

                ShowCameraPoseDef pose = cam!.pose!;
                _cgCam.ResetProjectionMatrix();
                SetLens(0f, 0.5f, 0.5f, 1f, 1f);   // 概算に歪み補正は無い
                Transform pt = _cgCam.transform;
                pt.position = CourseToWorld(new Vector2(pose.x, pose.z), pose.y);
                pt.rotation = Quaternion.Euler(-pose.pitchDeg, pose.yawDeg, 0f);
                float hfov = Mathf.Clamp(pose.hfovDeg > 0f ? pose.hfovDeg : 70f, 10f, 170f);
                _cgCam.aspect = aspect;
                _cgCam.fieldOfView = ShowCgLayer.HorizontalToVerticalFovDeg(hfov, aspect);
                return new Geometry
                {
                    usable = true,
                    fromCalib = false,
                    label = $"pose approx hfov{hfov:0}deg" + (pose.hfovDeg > 0f ? "" : " (default: hfovDeg unset)"),
                };
            }

            /// <summary>
            /// JsonUtility は JSON にキーが無くても入れ子オブジェクトを既定値で作る。「本当に書かれているか」は
            /// present-flag が正だが、**値だけ入っていて flag が false** のときに理由を言えるようにする。
            /// </summary>
            private static bool LooksAuthored(ShowCameraPoseDef? p)
                => p != null && (Mathf.Abs(p.x) > 1e-4f || Mathf.Abs(p.z) > 1e-4f
                                 || Mathf.Abs(p.yawDeg) > 1e-4f || Mathf.Abs(p.pitchDeg) > 1e-4f
                                 || p.hfovDeg > 0f);

            private void SetLens(float k1, float cxN, float cyN, float fxN, float fyN)
            {
                _compositeMat.SetVector("_CgLens", new Vector4(k1, cxN, cyN, 0f));
                _compositeMat.SetVector("_CgFocalN",
                    new Vector4(Mathf.Max(1e-4f, fxN), Mathf.Max(1e-4f, fyN), 0f, 0f));
            }

            // 位置合わせ（登録）が無いプレビューでは course 空間 = ワールド。ステージを遠くへ逃がしている
            // ぶんだけ平行移動する（隔離のためのオフセットが人形とカメラで食い違わないよう 1 箇所で足す）。
            private Vector3 CourseToWorld(Vector2 xz, float y)
                => new Vector3(StageOriginX + xz.x, y, xz.y);

            private void EnsureRenderTargets(int srcW, int srcH)
            {
                // CG の RT は**ソース映像の実寸**（枠ではない）。解像度もそこまで落とす —
                // 映像より鮮明な CG は「貼り付けた絵」に見える（ShowCgLayer.EnsureRenderTexture と同じ判断）。
                int h = Mathf.Max(64, Mathf.Min(srcH, CgRenderHeightMax));
                int w = Mathf.Max(64, Mathf.RoundToInt(h * ((float)srcW / Mathf.Max(1, srcH))));
                if (_cgRt == null || _cgRt.width != w || _cgRt.height != h)
                {
                    ReleaseCgRt();
                    // depth 24（+stencil8）。16 だとステンシルが無く、平面投影シャドウの「1 画素 1 回」が
                    // 効かずに腕と胴の重なりが二重に暗くなる（本番と同じ理由でここも 24）。
                    _cgRt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32)
                    {
                        name = "CgVizCgRT", useMipMap = false, wrapMode = TextureWrapMode.Clamp,
                        antiAliasing = 4,   // 本番（ShowCgLayer.EnsureRenderTexture）と同じ
                    };
                    _cgRt.Create();
                    _cgCam.targetTexture = _cgRt;
                    _compositeMat.SetTexture("_CgTex", _cgRt);

                    _cgResolve = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
                    { name = "CgVizCgResolve" };
                    _cgResolve.Create();
                    _cgRead = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false)
                    { hideFlags = HideFlags.HideAndDontSave };
                }

                if (_outRt != null) return;
                _outW = Mathf.RoundToInt(OutImageHeight * _frameAspect);
                _outH = Mathf.RoundToInt(OutImageHeight * (1f + CaptionHeightRatio));
                _outRt = new RenderTexture(_outW, _outH, 24, RenderTextureFormat.ARGB32)
                { name = "CgVizOutRT" };
                _outRt.Create();
                _outCam.targetTexture = _outRt;
                _outCam.aspect = (float)_outW / _outH;
                _readback = new Texture2D(_outW, _outH, TextureFormat.RGB24, mipChain: false)
                { hideFlags = HideFlags.HideAndDontSave };
            }

            // ---- 人形 ----

            public void PlaceActor(ShowActorDef def, Vector2 courseXz, float yawDeg, Geometry geom)
            {
                EnsureActor(def);
                if (_actor == null) return;
                _actor.SetActive(true);

                Transform t = _actor.transform;
                Vector3 pos = CourseToWorld(courseXz, 0f);
                if (_rig == null || !_rig.HasRig)
                    pos.y += Mathf.Max(0.2f, def.heightM) * 0.5f;   // 代用カプセルは中心が原点
                t.position = pos;
                t.rotation = Quaternion.Euler(0f, yawDeg, 0f);

                // 手が来ない状態（＝体側へ降ろした idle）へ収束させてから撮る。実行時の合流と同じ Drive を回す。
                if (_rig != null && _rig.HasRig)
                {
                    _rig.ResetPose();
                    for (int i = 0; i < ActorSettleFrames; i++)
                        _rig.Drive(ShowBodyInput.None, yawDeg, 1f / 30f);
                }

                ApplyLightAndGround(def, geom);
            }

            public void HideActor()
            {
                if (_actor != null) _actor.SetActive(false);
                if (_blob != null) _blob.gameObject.SetActive(false);
            }

            /// <summary>
            /// 人形の足元が映像のどこに来るか（ビューポート v・0 = 下端 / 1 = 上端）。
            /// 人形が居ない・カメラの後ろなら NaN。
            ///
            /// 0..1 の外なら**接地の手掛かりが画から丸ごと消えている**（床との接点も、影の落ち先も
            /// 映らない）。人形が浮いて見える原因の中で、これだけは合成の精度をいくら上げても直らない。
            /// </summary>
            public float FootViewportV()
            {
                if (_actor == null || !_actor.activeSelf) return float.NaN;
                Vector3 vp = _cgCam.WorldToViewportPoint(_actor.transform.position);
                return vp.z <= 0.01f ? float.NaN : vp.y;
            }

            private void EnsureActor(ShowActorDef def)
            {
                if (_actor != null) return;

                GameObject? prefab = string.IsNullOrEmpty(def.prefab)
                    ? null : Resources.Load<GameObject>(def.prefab);
                if (prefab == null)
                {
                    Debug.LogWarning($"[CgViz] actor '{def.id}' のプレハブ '{def.prefab}' が Resources に無い" +
                                     " → 代用の箱で出す（本番と同じフェイルソフト）");
                    _actor = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                    _actor.transform.localScale =
                        new Vector3(0.35f, Mathf.Max(0.2f, def.heightM) * 0.5f, 0.35f);
                    Collider? col = _actor.GetComponent<Collider>();
                    if (col != null) DestroyImmediate(col);
                }
                else
                {
                    _actor = UnityEngine.Object.Instantiate(prefab);
                    _rig = _actor.GetComponent<ShowActorRig>();
                    if (_rig != null)
                    {
                        _rig.Prepare();
                        float k = Mathf.Clamp(Mathf.Max(0.2f, def.heightM)
                                              / Mathf.Max(0.1f, _rig.MeasuredHeightM), 0.05f, 20f);
                        _actor.transform.localScale = Vector3.one * k;
                    }
                }

                _actorHeightM = Mathf.Max(0.05f, def.heightM);
                _actor.name = $"[CgVizActor:{def.id}]";
                _actor.transform.SetParent(_root.transform, worldPositionStays: true);
                SetLayerRecursive(_actor.transform, _cgLayer);

                // ⚠ Edit Mode では骨を動かしてもスキニング結果が更新されず、**全カットが同じ絵になる**
                //    （ShowActorVizPreview が実測で踏んだ罠。バイト単位で同一の PNG が出た）。
                foreach (var smr in _actor.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    smr.forceMatrixRecalculationPerRender = true;
                    smr.updateWhenOffscreen = true;
                }

                AttachShadowMaterial();
            }

            /// <summary>
            /// 影マテリアルを各 Renderer へ 1 枚足す（本体 + 影 = 同じメッシュを 2 回描く）。
            /// <c>ShowCgLayer.AttachShadowMaterial</c> と同じことをしている（あちらは private なので呼べない）。
            /// </summary>
            private void AttachShadowMaterial()
            {
                if (_actor == null) return;
                _shadowMat ??= LoadRuntimeMaterial("ShowCg/ShowShadowProjector", "FixedCamVr/ShowShadowProjector", "影");
                if (_shadowMat == null) return;

                foreach (Renderer r in _actor.GetComponentsInChildren<Renderer>(true))
                {
                    Material[] mats = r.sharedMaterials;
                    if (mats.Length > 0 && mats[mats.Length - 1] == _shadowMat) continue;
                    var next = new Material[mats.Length + 1];
                    for (int i = 0; i < mats.Length; i++) next[i] = mats[i];
                    next[mats.Length] = _shadowMat;
                    r.sharedMaterials = next;
                }
            }

            /// 実写プレートの平均輝度（0..1・まだ測っていなければ -1）。人形の光量をここへ寄せる。
            private float _plateLuma = -1f;

            /// <summary>
            /// 撮像の質（show.json <c>feel</c>）。**実機は既定値だけでも効く**ので、ここで書かないと
            /// プレビューだけ暗部ノイズも自動露出も無い絵になり、**人形が実機より浮いて見える**
            /// （＝馴染ませの判断が過剰になる）。解決も uniform の書き方も
            /// <see cref="CameraFeelFx"/> の口を通す — 写すと片方だけ直したときに黙って食い違う。
            /// </summary>
            private CameraFeelFx.Settings _feel = CameraFeelFx.Settings.Resolve(null);
            private readonly CameraFeelLogic _feelLogic = new CameraFeelLogic();

            public void SetFeel(ShowFeelDef? def)
            {
                _feel = CameraFeelFx.Settings.Resolve(def);
                _feelLogic.TargetLuma = _feel.TargetLuma;
                _feelLogic.FollowHalfLifeSec = _feel.FollowSec;
                Debug.Log($"[CgViz] 撮像の質: 暗部ノイズ {_feel.NoiseDark:0.###} / 固定ノイズ " +
                          $"{_feel.NoiseFixed:0.###} / 自動露出 {_feel.Agc:0.##}" +
                          (def == null || def.LooksUnset() ? "（show.json に feel が無いのでコード既定）" : ""));
            }

            /// <summary>人形に付き従う劣化（カットの <c>aura</c>）。0 なら書かない。</summary>
            private float _aura;
            private float _actorHeightM = 0.4f;

            public void SetAura(float aura) => _aura = Mathf.Clamp01(aura);

            /// <summary>
            /// 実写側の明るさを測る（本番の <c>MjpegScreen.SourceLuma</c> と同じ役）。
            /// **人形を置く前に呼ぶこと** — <see cref="ApplyLightAndGround"/> がこの値を読む。
            /// </summary>
            public void SetPlateLuma(Plate plate) => _plateLuma = AverageLuma(plate.texture);

            /// 場所別の明るさ。**実機と同じ器**（MjpegScreen も同じ <see cref="SourceLumaMap"/> を回す）。
            private readonly SourceLumaMap _plateLumaMap = new SourceLumaMap();

            // 16x16 の疎サンプル（MjpegScreen.SampleLuma と同じ手）。読めないテクスチャなら -1。
            private float AverageLuma(Texture2D? tex)
            {
                if (tex == null || tex.width <= 4 || tex.height <= 4) return -1f;
                try
                {
                    int stepX = Mathf.Max(1, tex.width / 16), stepY = Mathf.Max(1, tex.height / 16);
                    _plateLumaMap.BeginFrame();
                    for (int y = 0; y < tex.height; y += stepY)
                        for (int x = 0; x < tex.width; x += stepX)
                        {
                            Color c = tex.GetPixel(x, y);
                            _plateLumaMap.Add((float)x / tex.width, (float)y / tex.height,
                                              0.299f * c.r + 0.587f * c.g + 0.114f * c.b);
                        }
                    _plateLumaMap.EndFrame();
                    return _plateLumaMap.Mean;
                }
                catch { return -1f; }
            }

            /// <summary>
            /// 人形が立っている**その辺り**のプレートの明るさ（実機の
            /// <c>ShowCgLayer.LocalLumaAtActor</c> と同じ役）。取れなければ全画面平均へ落ちる。
            /// </summary>
            private float LocalPlateLumaAtActor()
            {
                if (_actor == null) return _plateLuma;
                Vector3 vp = _cgCam.WorldToViewportPoint(
                    _actor.transform.position + Vector3.up * (_actorHeightM * 0.5f));
                if (vp.z <= 0.01f) return _plateLuma;
                float local = _plateLumaMap.Sample(vp.x, vp.y);
                return local >= 0f ? local : _plateLuma;
            }

            /// <summary>光の向き（course 相対）と接地（投影シャドウ + 接地影 blob）を本番と同じ式で決める。</summary>
            private void ApplyLightAndGround(ShowActorDef def, Geometry geom)
            {
                ShowRoomDef? room = _showControl.Room;
                ShowRoomLightDef? light = room != null && room.hasLight && room.light != null ? room.light : null;
                float lightYaw = light != null ? light.yawDeg : 30f;
                float lightPitch = light != null ? light.pitchDeg : 55f;
                float density = light != null ? Mathf.Clamp01(light.shadowDensity) : 0.55f;
                float softM = light != null ? Mathf.Max(0f, light.shadowSoftM) : 0.12f;

                // course 相対の光。ワールド固定にすると、トラッキング原点の向き次第で部屋に対する
                // 陰影が変わる（＝毎回違う絵になる）。プレビューでも同じ式を通す。
                Vector3 dir = ShowCgLayer.CourseLightDirToWorld(lightYaw, lightPitch, 0f);

                if (_actor != null)
                {
                    // 光の色・強さ・環境光も本番（ShowCgLayer.ApplyLight）と同じ式で入れる。
                    // ここを省くとシェーダ既定の白色光で描かれ、**プレビューだけ人形が明るい**。
                    // 映像の明るさへ寄せる倍率も同じ関数から取る（実写が暗い区間で人形だけ浮くのを消す）。
                    float tempK = light != null ? light.tempK : 4000f;
                    float intensity = light != null ? Mathf.Max(0f, light.intensity) : 1f;
                    float ambient = light != null ? Mathf.Clamp01(light.ambient) : 0.35f;
                    // 合わせる先は「画に出た後の、人形が立つ場所の明るさ」。実機と同じく
                    // 局所輝度 + 自動露出のバイアスを食わせる（本番は ShowCgLayer.LumaGain）。
                    float gain = ShowCgLayer.LumaGainFor(
                        LocalPlateLumaAtActor(), _feelLogic.SteadyBiasFor(_plateLuma) * _feel.Agc);
                    Color lc = ShowCgLayer.KelvinToLinearColor(tempK) * (intensity * gain);

                    var mpb = new MaterialPropertyBlock();
                    foreach (Renderer r in _actor.GetComponentsInChildren<Renderer>(true))
                    {
                        r.GetPropertyBlock(mpb);
                        mpb.SetVector("_LightDir", new Vector4(dir.x, dir.y, dir.z, 0f));
                        mpb.SetVector("_LightColor", new Vector4(lc.r, lc.g, lc.b, 1f));
                        mpb.SetFloat("_Ambient", ambient);
                        r.SetPropertyBlock(mpb);
                    }
                }

                float floorWorldY = CourseToWorld(Vector2.zero, _roomProxy.FloorCourseY).y;
                if (_shadowMat != null)
                {
                    _shadowMat.SetFloat("_ShadowPlaneY", floorWorldY);
                    _shadowMat.SetVector("_ShadowLightDir", new Vector4(dir.x, dir.y, dir.z, 0f));
                    _shadowMat.SetFloat("_ShadowDensity", density);
                    _shadowMat.SetFloat("_ShadowSoftM", softM);
                }
                PlaceBlob(def, floorWorldY, density, softM);
            }

            private void PlaceBlob(ShowActorDef def, float floorWorldY, float density, float softM)
            {
                _blobMat ??= LoadRuntimeMaterial("ShowCg/ShowGroundBlob", "FixedCamVr/ShowGroundBlob", "接地影");
                if (_blobMat == null || _actor == null) return;

                if (_blob == null)
                {
                    var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    go.name = "[CgVizGroundBlob]";
                    go.layer = _cgLayer;
                    go.transform.SetParent(_root.transform, worldPositionStays: true);
                    Collider? col = go.GetComponent<Collider>();
                    if (col != null) DestroyImmediate(col);
                    var r = go.GetComponent<MeshRenderer>();
                    r.sharedMaterial = _blobMat;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    r.receiveShadows = false;
                    _blob = go.transform;
                }

                _blob.gameObject.SetActive(true);
                Vector3 p = _actor.transform.position;
                // **式は本番（ShowCgLayer）から取る。**ここへ数値を写すと、片方だけ直したときに
                // 「プレビューでは自然なのに実機では足元に円盤」という食い違いが黙って起きる。
                float radius = ShowCgLayer.GroundBlobRadiusM(def.heightM);
                _blob.position = new Vector3(p.x, floorWorldY + 0.004f, p.z);
                _blob.rotation = Quaternion.Euler(90f, 0f, 0f);
                _blob.localScale = new Vector3(radius * 2f, radius * 2f, 1f);
                _blobMat.SetFloat("_BlobDensity", density);
                _blobMat.SetFloat("_BlobFeather", ShowCgLayer.BlobFeatherFromSoftM(softM, radius));
            }

            private static Material? LoadRuntimeMaterial(string resourcePath, string shaderName, string label)
            {
                var loaded = Resources.Load<Material>(resourcePath);
                Shader? shader = loaded != null ? loaded.shader : Shader.Find(shaderName);
                if (shader == null)
                {
                    Debug.LogWarning($"[CgViz] {label}のマテリアル '{resourcePath}' もシェーダ '{shaderName}' も" +
                                     $" 見つからない → {label}なしで続行");
                    return null;
                }
                var mat = loaded != null ? new Material(loaded) : new Material(shader);
                mat.name = $"{shaderName} (cgviz)";
                mat.hideFlags = HideFlags.HideAndDontSave;
                return mat;
            }

            // ---- 合成 + 保存 ----

            public void Composite(Plate plate, bool cgVisible, PostParams post, string caption,
                                  bool applyFeel = true)
            {
                EnsureRenderTargets(plate.width, plate.height);
                _plateLuma = AverageLuma(plate.texture);

                // CG レイヤを描く（人形もワイヤーもここに乗る）。透明背景なので被覆率がアルファに出る。
                if (_cgRt != null) _cgCam.Render();

                // ライブ層 = プレート。contain-fit は MjpegScreen と同じ式（枠に収めて残りは黒帯）。
                float srcAspect = (float)plate.width / Mathf.Max(1, plate.height);
                Vector2 contain = srcAspect < _frameAspect
                    ? new Vector2(srcAspect / _frameAspect, 1f)
                    : new Vector2(1f, _frameAspect / srcAspect);

                _contain = contain;
                _compositeMat.SetTexture("_LiveTex", plate.texture);
                _compositeMat.SetVector("_LiveScale", new Vector4(contain.x, contain.y, 0f, 0f));
                // CG は**ライブと同じ contain 枠**。ここを生の screenUv にすると、4:3 の映像が 16:9 の枠へ
                // letterbox されている分（0.75）だけ人形が水平 1.33 倍外側へずれる（2026-07-27 監査 CRITICAL 1）。
                _compositeMat.SetVector("_CgScale", new Vector4(contain.x, contain.y, 0f, 0f));
                _compositeMat.SetFloat("_UvRotSteps", 0f);
                _compositeMat.SetFloat("_OverlayStrength", 0f);
                _compositeMat.SetFloat("_CgStrength", cgVisible ? 1f : 0f);

                _compositeMat.SetFloat("_Exposure", post.exposure);
                _compositeMat.SetFloat("_Contrast", post.contrast);
                _compositeMat.SetFloat("_Saturation", post.saturation);
                _compositeMat.SetFloat("_Temperature", post.temperature);
                _compositeMat.SetFloat("_Vignette", post.vignette);
                _compositeMat.SetFloat("_Grain", post.grain);
                _compositeMat.SetFloat("_Scanline", post.scanline);
                _compositeMat.SetFloat("_Lift", post.lift);
                _compositeMat.SetFloat("_Tint", post.tint);
                _compositeMat.SetFloat("_SwitchDim", 0f);
                _compositeMat.SetFloat("_SignalLost", 0f);

                // 4) 撮像の質。**実機は show.json に feel が無くても既定値で効く**ので、ここを書かないと
                //    プレビューだけ暗部ノイズも自動露出も無い絵になり、人形が実機より浮いて見える。
                //    静止画 1 枚なので自動露出は収束値を使う（時間で追う意味が無く、同じ値になる）。
                float steadyBias = applyFeel ? _feelLogic.SteadyBiasFor(_plateLuma) : 0f;
                CameraFeelFx.WriteUniforms(_compositeMat,
                    applyFeel ? _feel.NoiseDark : 0f,
                    applyFeel ? _feel.NoiseFixed : 0f,
                    steadyBias * _feel.Agc,
                    _feelLogic.VignetteBiasFor(steadyBias) * _feel.Agc,
                    echo: 0f);   // ホールド / 焼き付きは時間の表現なので静止画には出さない

                // 人形に付き従う劣化（カットの aura）。人形が出ないカットでは必ず 0 へ戻す。
                Vector4 focus = new Vector4(0.5f, 0.5f, 0.2f, 0f);
                if (applyFeel && cgVisible && _aura > 0f && _actor != null)
                    ShowCgLayer.TryActorFocus(_cgCam, _actor.transform.position, _actorHeightM,
                                              contain, _aura, out focus);
                _compositeMat.SetVector("_ActorFocus", focus);

                SetCaption(caption);
                _outCam.Render();
            }

            private void SetCaption(string caption)
            {
                _caption.text = Ascii(caption);
                // Edit Mode では TMP の遅延メッシュ生成が走らない。2 回叩く（1 回目でグリフ要求・
                // 2 回目で焼けたグリフを含めて再レイアウト。HudPreviewScreenshot と同じ対処）。
                _caption.ForceMeshUpdate(ignoreActiveState: true, forceTextReparsing: true);
                _caption.ForceMeshUpdate(ignoreActiveState: true, forceTextReparsing: true);
            }

            public string Save(string fileName)
            {
                if (_outRt == null || _readback == null)
                    throw new InvalidOperationException("出力 RT が未初期化");
                RenderTexture.active = _outRt;
                _readback.ReadPixels(new Rect(0f, 0f, _outW, _outH), 0, 0);
                _readback.Apply();
                RenderTexture.active = null;

                string path = Path.Combine(_outDir, fileName);
                File.WriteAllBytes(path, _readback.EncodeToPNG());
                return path;
            }

            /// <summary>show.json すら無い等、絵を作れないときに「何が足りないか」だけを書いた 1 枚を出す。</summary>
            public void WriteMessageCard(string fileName, string line1, string line2, List<string> saved)
            {
                Plate plate = LoadPlate("");
                Composite(plate, cgVisible: false, post: new PostParams(), caption: line1 + "\n" + line2);
                saved.Add(Save(fileName));
                Debug.LogError($"[CgViz] {line1} / {line2}");
            }

            // ---- 後片付け ----

            private void ReleaseCgRt()
            {
                if (_cgResolve != null)
                {
                    _cgResolve.Release();
                    DestroyImmediate(_cgResolve);
                    _cgResolve = null;
                }
                if (_cgRead != null) { DestroyImmediate(_cgRead); _cgRead = null; }
                if (_cgRt == null) return;
                _cgCam.targetTexture = null;
                _cgRt.Release();
                DestroyImmediate(_cgRt);
                _cgRt = null;
            }

            // ---- 浮き具合の測定 ----

            /// <summary>
            /// 人形が周囲に対してどれだけ浮いているかを 1 行で返す（人形が出ていなければ空）。
            ///
            /// <b>なぜ数値が要るか</b>: 目視は当てにならない。2026-08-07 の見直しでは、人形の彩度が
            /// 周囲の 3〜4 倍あることを絵からは誰も指摘できず、数値にした瞬間に一目で分かった。
            /// <b>なぜ数値だけでは足りないか</b>: 同じ日に、数値を全部そろえたら**絵から人形が消えた**
            /// （3 周目に「自分がそこに立っている」と読めなければ演出が成立しない）。
            /// この行と絵は必ず並べて見ること。
            ///
            /// 人形の範囲は CG レイヤの alpha が正（マスクを別に作らない）。周囲は人形を囲む
            /// 1.8 倍の矩形から人形を除いた部分で、比較は**同じ 1 枚の中**で完結する
            /// （プレートと合成を比べると post のぶんがまるごと差として出て読めない）。
            /// </summary>
            public string MeasureBlend()
            {
                Vector2 contain = _contain;
                if (_cgRt == null || _cgResolve == null || _cgRead == null
                    || _outRt == null || _readback == null)
                {
                    Debug.LogWarning($"[CgViz] 浮き具合を測れない（RT 未初期化）: cgRt={_cgRt != null} " +
                                     $"resolve={_cgResolve != null} read={_cgRead != null} " +
                                     $"out={_outRt != null} readback={_readback != null}");
                    return "";
                }

                Graphics.Blit(_cgRt, _cgResolve);
                RenderTexture.active = _cgResolve;
                _cgRead.ReadPixels(new Rect(0f, 0f, _cgRt.width, _cgRt.height), 0, 0);
                _cgRead.Apply();
                RenderTexture.active = _outRt;
                _readback.ReadPixels(new Rect(0f, 0f, _outW, _outH), 0, 0);
                _readback.Apply();
                RenderTexture.active = null;

                Color32[] cg = _cgRead.GetPixels32();
                Color32[] px = _readback.GetPixels32();
                int cw = _cgRead.width, ch = _cgRead.height;

                // 出力画素 → 枠 UV → CG UV（逆に辿ると 1 CG 画素が複数出力画素に散って穴が開く）。
                bool[] isDoll = new bool[_outW * _outH];
                int minX = _outW, maxX = -1, minY = _outH, maxY = -1;
                for (int oy = 0; oy < _outH; oy++)
                {
                    // 映像部分は世界 y ∈ [r/2-0.5, r/2+0.5]、カメラは高さ (1+r)。
                    float fv = (oy + 0.5f) / _outH * (1f + CaptionHeightRatio) - CaptionHeightRatio;
                    if (fv < 0f || fv > 1f) continue;
                    float v = (fv - 0.5f) / Mathf.Max(contain.y, 1e-4f) + 0.5f;
                    if (v < 0f || v > 1f) continue;
                    int cy = Mathf.Clamp((int)(v * ch), 0, ch - 1);
                    for (int ox = 0; ox < _outW; ox++)
                    {
                        float u = ((ox + 0.5f) / _outW - 0.5f) / Mathf.Max(contain.x, 1e-4f) + 0.5f;
                        if (u < 0f || u > 1f) continue;
                        int cx = Mathf.Clamp((int)(u * cw), 0, cw - 1);
                        if (cg[cy * cw + cx].a < 128) continue;
                        isDoll[oy * _outW + ox] = true;
                        if (ox < minX) minX = ox;
                        if (ox > maxX) maxX = ox;
                        if (oy < minY) minY = oy;
                        if (oy > maxY) maxY = oy;
                    }
                }
                if (maxX < minX) return "";   // 人形が 1 画素も映っていない（＝測る対象が無い）

                var doll = new Sample();
                var around = new Sample();
                int padX = Mathf.RoundToInt((maxX - minX + 1) * 0.4f);
                int padY = Mathf.RoundToInt((maxY - minY + 1) * 0.4f);
                for (int oy = Mathf.Max(0, minY - padY); oy <= Mathf.Min(_outH - 1, maxY + padY); oy++)
                {
                    float fv = (oy + 0.5f) / _outH * (1f + CaptionHeightRatio) - CaptionHeightRatio;
                    if (fv < 0f || fv > 1f) continue;   // キャプション帯は混ぜない
                    for (int ox = Mathf.Max(0, minX - padX); ox <= Mathf.Min(_outW - 1, maxX + padX); ox++)
                    {
                        int i = oy * _outW + ox;
                        // ⚠ `(cond ? doll : around).Add(...)` と書くと **struct のコピー**に足して
                        //    元が変わらない（実際それで doll=0 / around=0 になった）。
                        if (isDoll[i]) doll.Add(px, i, _outW);
                        else around.Add(px, i, _outW);
                    }
                }
                if (doll.N == 0 || around.N == 0)
                {
                    Debug.LogWarning($"[CgViz] 浮き具合を測れない（標本不足）: doll={doll.N} around={around.N}");
                    return "";
                }

                return $"blend doll/around: luma {doll.Luma:0.0}/{around.Luma:0.0} " +
                       $"(x{Ratio(doll.Luma, around.Luma):0.00})  " +
                       $"sat {doll.Sat:0.0}/{around.Sat:0.0} (x{Ratio(doll.Sat, around.Sat):0.00})  " +
                       $"grain {doll.Grain:0.0}/{around.Grain:0.0} " +
                       $"(x{Ratio(doll.Grain, around.Grain):0.00})";
            }

            private static float Ratio(float a, float b) => b > 0.01f ? a / b : 0f;

            /// 明るさ / 彩度 / 粒（近傍との差）を貯める。3 つとも「同じ画の中で人形が浮くか」を測る軸。
            private struct Sample
            {
                private double _luma, _sat, _grain;
                public int N;

                public float Luma => N > 0 ? (float)(_luma / N) : 0f;
                public float Sat => N > 0 ? (float)(_sat / N * 100.0) : 0f;
                public float Grain => N > 0 ? (float)(_grain / N) : 0f;

                public void Add(Color32[] px, int i, int w)
                {
                    Color32 c = px[i];
                    float l = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
                    int mx = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                    int mn = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
                    _luma += l;
                    _sat += mx > 12 ? (mx - mn) / (float)mx : 0f;
                    // 粒 = 左右の画素との差。周囲 8 近傍まで取ると境界の扱いが増えるだけで結論は変わらない。
                    int x = i % w;
                    if (x > 0 && x < w - 1)
                    {
                        Color32 a = px[i - 1], b = px[i + 1];
                        float la = 0.299f * a.r + 0.587f * a.g + 0.114f * a.b;
                        float lb = 0.299f * b.r + 0.587f * b.g + 0.114f * b.b;
                        _grain += Mathf.Abs(l - (la + lb) * 0.5f);
                    }
                    N++;
                }
            }

            public void Dispose()
            {
                RenderTexture.active = null;
                ReleaseCgRt();
                if (_outRt != null)
                {
                    _outCam.targetTexture = null;
                    _outRt.Release();
                    DestroyImmediate(_outRt);
                }
                if (_readback != null) DestroyImmediate(_readback);
                if (_grayPlate != null) DestroyImmediate(_grayPlate);
                foreach (Texture2D? t in _plateCache.Values)
                    if (t != null) DestroyImmediate(t);
                _plateCache.Clear();
                if (_gridMesh != null) DestroyImmediate(_gridMesh);
                if (_boxMesh != null) DestroyImmediate(_boxMesh);
                if (_compositeMat != null) DestroyImmediate(_compositeMat);
                if (_shadowMat != null) DestroyImmediate(_shadowMat);
                if (_blobMat != null) DestroyImmediate(_blobMat);
                if (_root != null) DestroyImmediate(_root);
            }

            private static void DestroyImmediate(UnityEngine.Object o) => UnityEngine.Object.DestroyImmediate(o);
        }

        private static void SetLayerRecursive(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayerRecursive(t.GetChild(i), layer);
        }

        /// <summary>
        /// キャプションへ焼く前に非 ASCII を落とす。Edit Mode の TMP は静的アトラスに焼いていない字を
        /// 豆腐で組むので、日本語の cue 名や人形名がそのまま入ると帯が □□□ になる。
        /// </summary>
        private static string Ascii(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s!.Length);
            // 改行は残す。潰すと 3 行が 1 行に繋がり、幅で勝手に折り返されて読み順が崩れる
            // （警告行が前の行の途中から始まって見落とす）。
            foreach (char c in s) sb.Append(c == '\n' || (c >= ' ' && c <= '~') ? c : '?');
            return sb.ToString();
        }

        // ---- show.json のパース ----
        //
        // ShowControlClient の cameras / actors は private なので、実機と同じ「読んだ結果」を作るには
        // ここで同じ形の器へ読み直すしかない。**present-flag（hasPose 等）を必ず AND で見る**のが要点で、
        // JsonUtility は JSON にキーが無くても入れ子オブジェクトを既定値で作る（幽霊が湧く）。

        private static ShowJson? LoadShow(string? explicitPath, out string usedPath)
        {
            usedPath = "";
            string? repoRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (repoRoot == null) return null;

            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(explicitPath)) candidates.Add(explicitPath!);
            foreach (string rel in ShowJsonCandidatesRel)
                candidates.Add(Path.Combine(repoRoot, rel.Replace('/', Path.DirectorySeparatorChar)));

            foreach (string path in candidates)
            {
                if (!File.Exists(path)) continue;
                try
                {
                    var parsed = JsonUtility.FromJson<ShowJson>(File.ReadAllText(path));
                    if (parsed == null) continue;
                    ReconcileCameraFlags(parsed);
                    usedPath = path;
                    return parsed;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[CgViz] show.json を読めない（次の候補へ）: {path} — {e.Message}");
                }
            }
            return null;
        }

        /// <summary>
        /// カメラの present-flag を**本番と同じ規則で再導出**する。
        ///
        /// ⚠ ここを飛ばすと「実機では人形が出るのにプレビューでは出ない」という**嘘の証拠**が焼ける。
        /// 卓は `pose` / `calib` を書くが `hasPose` / `hasCalib` は書かない（JSON にキー自体が無い）。
        /// 実機の <see cref="ShowControlClient"/> はライブ受信・焼き込みのフレッシュパース直後に
        /// `hasPose = pose != null` / `hasCalib = calib != null && calib.IsUsable()` で立て直しており、
        /// このプレビューも同じ土俵に立たなければ判定が食い違う。
        ///
        /// 一方 `layout.hasRoom` / `step.hasPlacement` は**宣言 bool が正**（卓が明示的に書く）ので
        /// 再導出しない — そちらは幽霊の入れ子を弾くのが目的で、規約が逆向き。
        /// </summary>
        private static void ReconcileCameraFlags(ShowJson show)
        {
            if (show.cameras == null) return;
            foreach (PreviewCameraDef? c in show.cameras)
            {
                if (c == null) continue;
                c.hasPose = c.pose != null;
                c.hasCalib = c.calib != null && c.calib.IsUsable();
            }
        }

        [Serializable]
        private sealed class ShowJson
        {
            public PreviewCameraDef[] cameras = Array.Empty<PreviewCameraDef>();
            public PostParams? post;
            public ShowLayoutDef? layout;
            public ShowTimelineDef? timeline;
            public ShowActorDef[] actors = Array.Empty<ShowActorDef>();

            /// <summary>撮像の質（暗部ノイズ・固定パターンノイズ・自動露出の追従）。**欠落 = コード既定で効く**。</summary>
            public ShowFeelDef? feel;

            public PreviewCameraDef? CameraAt(int i)
                => cameras != null && i >= 0 && i < cameras.Length ? cameras[i] : null;

            /// <summary>カメラ個別 post（present-flag AND 実体）。無ければ null で global へ落とす。</summary>
            public PostParams? CameraPost(int i)
            {
                PreviewCameraDef? c = CameraAt(i);
                return c != null && c.hasPost && c.post != null ? c.post : null;
            }

            public ShowActorDef? FindActor(string? id)
            {
                if (string.IsNullOrEmpty(id) || actors == null) return null;
                foreach (ShowActorDef? a in actors)
                    if (a != null && a.id == id) return a;
                return null;
            }
        }

        /// <summary>ShowControlClient の private CameraDef のうち、プレビューに要る面だけを写した器。</summary>
        [Serializable]
        private sealed class PreviewCameraDef
        {
            public string id = "";
            public string role = CameraRoles.Zone;
            public PostParams? post;
            public bool hasPost;
            public ShowCameraPoseDef? pose;
            public bool hasPose;
            public ShowCameraCalibDef? calib;
            public bool hasCalib;
        }
    }
}
