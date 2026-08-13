#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using FixedCamVr.Streaming.Cg;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// 導入演出（2026-08-13 の作り直し版・段 0〜4）を PNG で出す。<b>Play も HMD もビルドも要らない。</b>
    ///
    /// ⚠⚠ <b>これが導入の唯一の安い門。</b> 覆い・隔離殻・封印の箱・管の点灯はすべてシェーダで、
    /// <c>unity.ps1 test</c> には 1 件も出ない（コンパイルが通っても全面マゼンタ・真っ黒になりうる）。
    /// 段を触ったら必ずここを通して<b>焼いた PNG を開くこと</b>
    /// （<c>rules/show-design.md</c>「シェーダを書いたら絵を出す」）。
    ///
    /// <b>本番と同じものを使う</b>のが絶対条件（写経した式は必ずいつか食い違い、証拠として無価値になる）:
    ///   - 段と重みは<b>本物の <see cref="IntroLogic"/> を実際に回して</b>取る（重みを手で作らない）
    ///   - 描くのは実コンポーネント <see cref="IntroVeil"/> / <see cref="ContainmentShell"/> /
    ///     <see cref="SealedBox"/> と、実シェーダ <c>FixedCamVr/ScreenComposite</c>
    ///   - スクリーンの材質は<b>シーンのものを複製する</b>（authored な post がそのまま乗る）。
    ///     撮像の質（粒・自動露出・管の面）は <see cref="CameraFeelFx.WriteUniforms"/> が書く
    ///     — ここを省くと<b>プレビューだけ実機より綺麗な絵</b>になる（2026-08-07 の実害と同じ型）
    ///
    /// <b>パススルーの合成まで真似る。</b> 覆いは <c>Blend Zero SrcAlpha</c> で
    /// 「現実を出す＝ alpha を 0 にする」だけなので、素直にカメラで撮ると
    /// <b>現実が出るはずの所が真っ黒</b>になって判断が丸ごと逆になる。ここでは実機の合成と同じ
    ///   最終 = アプリの rgb + 現実 × (1 - アプリの alpha)
    /// を CPU で解き、「現実」の代わりに <c>tools/web-compositor/captures/</c> の実写プレートを敷いてある。
    ///
    /// ⚠ <b>これは段の進み方と形を見るための絵で、現場の見えではない。</b> 「現実」は明るい実写プレートで、
    /// 実際の会場（暗い）とは違う。ブラウン管の<b>曲面</b>（<see cref="CrtScreenMesh"/>）も出ない
    /// （平らな Quad を正投影で撮るため。角の丸みと縁の暗さは出る）。合否はここで出さない
    /// （<c>.claude/reference/why.md</c>「安い門は落とすためだけに使う」）。
    /// </summary>
    public static class IntroPreview
    {
        private const string OutDirRel = "Screenshots/intro";

        /// <summary>映像部分の解像度。キャプション帯はこの下に足される。</summary>
        private const int Width = 1280;
        private const int Height = 960;

        /// <summary>キャプション帯の高さ（映像高に対する比）。映像を隠さないよう<b>下に足す</b>。</summary>
        private const float CaptionHeightRatio = 0.20f;

        /// <summary>
        /// キャプションの <c>fontSize</c>。TMP の <b>3D テキスト</b>の fontSize は世界単位ではないので、
        /// <see cref="ShowCompositePreview"/> の実測（幅 1.778 世界単位に約 95 文字 = 0.35）から
        /// この画づくり（幅 = 4/3 世界単位）へ比例で移した値。1 行あたり約 100 文字入る。
        /// ⚠ <see cref="Width"/> / <see cref="Height"/> / <see cref="CaptionHeightRatio"/> を変えたら
        ///   この値も measure し直す。オートサイズは使わない（行が伸びると読めない帯になる）。
        /// </summary>
        private const float CaptionFontSize = 0.26f;

        /// <summary>
        /// 導入の面だけを写すための隔離レイヤ（組み込みの TransparentFX を一時借用・恒久変更はしない）。
        /// <b>Main.unity を開くので、これが無いとシーンの壁や Rig が絵に紛れ込む。</b>
        /// ⚠ course 空間 = ワールド（<c>CourseToWorldProvider</c> 不在の identity）なので、
        ///   <see cref="ShowCompositePreview"/> のように座標で逃がすことはできない。分けるのはレイヤだけ。
        /// </summary>
        private const int IntroLayer = 1;

        /// <summary>キャプション帯を組む合成ステージのレイヤ（Ignore Raycast の一時借用）。</summary>
        private const int CaptionLayer = 2;

        /// <summary>合成ステージの置き場。導入の面（原点付近）と混ざらないよう遠くへ逃がす。</summary>
        private const float CaptionStageOriginX = 1000f;

        private const float EyeH = 1.6f;

        /// <summary>
        /// 段 0 / 段 1 で箱の面からどれだけ離れて立つか (m)。
        ///
        /// ⚠ <b>これは絵の都合であって、現場の立ち位置ではない。</b> 実際の武装距離は
        /// <see cref="IntroLogic.ApproachNearM"/> (1.0m) で、そこでは高さ 2.4m の箱が視界を埋める。
        /// 「箱が 1 個の物として読めるか」を見るために、上下が画角 90° に収まる 1.8m まで下げてある
        /// （<see cref="SealedBoxPreview"/> の <c>far</c> と同じ判断）。
        /// </summary>
        private const float OutsideStandM = 1.8f;

        /// <summary>床が著作されていないときに使う footprint の半寸 (m)。<b>キャプションに明示する。</b></summary>
        private const float FallbackHalfM = 0.9f;

        // 本編のスクリーンの構え（ScreenAnchor の authored 値）。頭からの距離と高さのオフセット。
        private const float ScreenDistanceM = 2.0f;
        private const float ScreenHeightOffsetM = -0.28f;
        private const float FallbackScreenW = 2.3704f;
        private const float FallbackScreenH = 1.3333f;

        // 「現実」の代わりに敷く実写プレート。選び方は ShowCompositePreview と同じ（plate_ を先に見る）。
        private static readonly string[] PlateDirsRel =
        {
            "tools/web-compositor/captures",
            "tools/web-compositor/recordings",
        };

        private static readonly string[] PlateCameraIds = { "A", "B", "C", "D" };

        private static readonly string[] ShowJsonCandidatesRel =
        {
            "tools/web-compositor/show.json",
            "Assets/StreamingAssets/show/show.json",
        };

        private static readonly int CrtIgniteId = Shader.PropertyToID("_CrtIgnite");
        private static readonly int IntroLiveId = Shader.PropertyToID("_IntroLive");
        private static readonly int GlitchId = Shader.PropertyToID("_Glitch");
        private static readonly int GlitchSeedId = Shader.PropertyToID("_GlitchSeed");
        private static readonly int SignalLostId = Shader.PropertyToID("_SignalLost");

        // public なのは CLI（`unity.ps1 menu intro`）が -executeMethod で直接呼ぶため。
        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Intro", priority = 238)]
        public static void Run()
        {
            // CLI（batchmode）は空シーンで始まる。スクリーンの材質と枠の大きさはシーンが正なので、
            // **本番と同じ絵にするため** Main を開いてから撮る。
            if (!EditorCliArgs.EnsureScene("Assets/Scenes/Main.unity")) return;

            string outDir = Path.Combine(Application.dataPath, OutDirRel);
            Directory.CreateDirectory(outDir);
            // ⚠ 古い絵を残さない。段や刻みを変えると名前が変わるので、消さないと**前の版の 1 枚が
            //    新しい絵の隣に並んで証拠として読まれる**（この codebase が繰り返し踏んでいる型）。
            foreach (string old in Directory.GetFiles(outDir, "intro_*.png")) File.Delete(old);

            var saved = new List<string>();
            Stage? stage = null;
            try
            {
                stage = Stage.Create(outDir);
                foreach (Shot shot in BuildShots()) stage.Render(shot, saved);
            }
            catch (Exception e)
            {
                Debug.LogError($"[IntroViz] 失敗: {e}");
            }
            finally
            {
                stage?.Dispose();
                AssetDatabase.Refresh();
            }

            if (saved.Count == 0)
            {
                Debug.LogError("[IntroViz] 1 枚も出せませんでした");
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[IntroViz] {saved.Count} 枚を保存:");
            foreach (string p in saved) sb.AppendLine("  " + p.Replace('\\', '/'));
            sb.Append("（段の進み方と形を見るための絵。現場の見えではない）");
            Debug.Log(sb.ToString());
        }

        // ---- 撮る対象 --------------------------------------------------------

        /// <summary>1 枚ぶんの条件。</summary>
        private readonly struct Shot
        {
            public readonly IntroStage stage;
            public readonly int index;      // 段番号（ファイル名と帯に出る）
            public readonly string name;    // 段の名前（ASCII）
            public readonly float p;        // 段の中の進み 0..1
            public readonly int label;      // ファイル名に入れる進み（000 / 050 / 100 …）
            /// <summary>カメラが 1 台も繋がっていない現場（<c>_SignalLost = 1</c>）。</summary>
            public readonly bool noSignal;

            public Shot(IntroStage stage, int index, string name, float p, int label,
                        bool noSignal = false)
            {
                this.stage = stage; this.index = index; this.name = name; this.p = p; this.label = label;
                this.noSignal = noSignal;
            }

            public string File =>
                $"intro_{index}_{name}_{label:000}{(noSignal ? "_nosignal" : "")}.png";
        }

        /// <summary>
        /// 段の頭・中・終わりを撮る。<b>段 3（管が点く）は点き方が本題なので 5 枚</b>。
        ///
        /// ⚠ 段 0 は 1 枚だけ。<b>時間で進まない段</b>（体験者が近づくのを待つだけ）で、
        /// <see cref="IntroLogic.Weights"/> が定数を返すので 3 枚撮っても同じ絵が 3 つ並ぶ。
        ///
        /// ⚠ 終わりは <c>0.999</c>。ちょうど 1.0 を渡すと <see cref="IntroLogic.Tick"/> が次の段へ
        /// 送ってしまい、「段の終わり」ではなく「次の段の頭」が撮れる。
        /// </summary>
        private static IEnumerable<Shot> BuildShots()
        {
            yield return new Shot(IntroStage.Black, 0, "black", 0f, 0);

            foreach ((float p, int label) in new[] { (0f, 0), (0.5f, 50), (0.999f, 100) })
                yield return new Shot(IntroStage.Seal, 1, "seal", p, label);

            foreach ((float p, int label) in new[] { (0f, 0), (0.5f, 50), (0.999f, 100) })
                yield return new Shot(IntroStage.Dark, 2, "dark", p, label);

            foreach ((float p, int label) in
                     new[] { (0f, 0), (0.25f, 25), (0.5f, 50), (0.75f, 75), (0.999f, 100) })
                yield return new Shot(IntroStage.Ignite, 3, "ignite", p, label);

            // ⚠ 段 4 の「中」は 0.50 ではなく **0.25**。段の尺は 2.4 秒だが、映像へのクロスフェードは
            //    <see cref="IntroLogic.LiveCrossfadeSec"/>（1.2 秒）で終わり、継ぎ目を隠す乱れも
            //    そこが山になる。0.50 で撮ると終わりと同じ絵が 2 枚並ぶだけで、**継ぎ目が 1 枚も写らない**。
            foreach ((float p, int label) in new[] { (0f, 0), (0.25f, 25), (0.999f, 100) })
                yield return new Shot(IntroStage.Live, 4, "live", p, label);

            // ⚠⚠ **カメラが 1 台も繋がっていない現場**（canon/LEDGER.md 0025）。
            //    段 4 は飛ばさず、映像の代わりに砂嵐が出る。ここで見るのは 2 つ:
            //      - 段 3（管が点く途中）に砂嵐が**乗っていない**こと
            //        （砂嵐は post の最後なので、切らないと点灯の過程をまるごと上書きする）
            //      - 段 4 で映像と同じ進みで砂嵐が**入ってくる**こと
            yield return new Shot(IntroStage.Ignite, 3, "ignite", 0.5f, 50, noSignal: true);
            yield return new Shot(IntroStage.Live, 4, "live", 0.25f, 25, noSignal: true);
            yield return new Shot(IntroStage.Live, 4, "live", 0.999f, 100, noSignal: true);
        }

        // ---- 段の駆動（重みは本物の状態機械から取る）--------------------------

        /// <summary>
        /// <b>本物の <see cref="IntroLogic"/> を実際に回して</b>目的の段・進みまで持っていく。
        ///
        /// 重みを手で作らないのが要点。手で作ると<b>段の遷移が実装とずれても絵からは気づけない</b>
        /// （このリポジトリが「状態は進んでいるのに画には何も出ていない」を 2026-07-31 に踏んでいる）。
        ///
        /// 尺は show.json の <c>run.intro</c>（無ければ <see cref="IntroTiming.Default"/>）。
        /// 段の中の進みは正規化してあるので尺を変えても絵は変わらないが、<b>段 4 だけは違う</b> —
        /// <see cref="IntroLogic.LiveCrossfadeSec"/> は絶対秒なので、<c>liveSec</c> を変えると
        /// 「クロスフェードが段のどこで終わるか」が動く。
        /// </summary>
        private static IntroLogic DriveTo(IntroStage target, float p, IntroTiming t)
        {
            var logic = new IntroLogic();
            logic.Configure(t);
            logic.Begin();

            // 段 0。現実が見えていて、体験者はまだ箱の外に立っている。
            logic.Tick(0f, Observed(outsideM: OutsideStandM, atStartSpot: false));
            if (target == IntroStage.Black) return logic;

            // 段 1。開始の合図（体験エリアへの接近）で入る。閉じ切るまで箱の外のまま。
            logic.Tick(1e-3f, Observed(OutsideStandM, atStartSpot: true));
            if (target == IntroStage.Seal)
            {
                logic.Tick(p * t.sealSec, Observed(OutsideStandM, true));
                return logic;
            }

            logic.Tick(t.sealSec, Observed(OutsideStandM, true));      // → 段 2
            if (target == IntroStage.Dark)
            {
                // 段 2 の全黒のあいだに、体験者は封印の箱の中へ歩いて入る。
                logic.Tick(p * t.darkSec, Observed(0f, true));
                return logic;
            }

            logic.Tick(t.darkSec, Observed(0f, true));                 // 中に入った → 段 3
            if (target == IntroStage.Ignite)
            {
                logic.Tick(p * t.igniteSec, Observed(0f, true));
                return logic;
            }

            logic.Tick(t.igniteSec, Observed(0f, true));               // → 段 4
            logic.Tick(p * t.liveSec, Observed(0f, true));
            return logic;
        }

        /// <summary>
        /// 段送りの条件を全部満たした観測値。<b>足踏みの条件は見ない</b>
        /// （頭を振っていない・スクリーンを見ている・映像が届いている）— ここで見たいのは
        /// 「段が進んだときに何が描かれるか」であって、進む / 進まないの判定ではない。
        /// </summary>
        private static IntroInput Observed(float outsideM, bool atStartSpot) => new IntroInput
        {
            blackCleared = true,
            atStartSpot = atStartSpot,
            headTurnDegPerSec = 0f,
            frameCentered = true,
            liveFresh = true,
            recentered = false,
            outsideBoxM = outsideM,
        };

        // ---- 合成ステージ（生成物一式。finally で必ず畳む）--------------------

        private sealed class Stage : IDisposable
        {
            private readonly string _outDir;

            // 導入の面（原点付近・course = world）
            private readonly GameObject _root;
            private readonly Transform _head;
            private readonly Camera _cam;
            private readonly Material _screenMat;
            private readonly IntroVeil _veil;
            private readonly ContainmentShell _shell;
            private readonly SealedBox _box;
            private readonly float _halfM;

            // キャプション帯を足す合成ステージ（遠くの別レイヤ）
            private readonly GameObject _capRoot;
            private readonly Camera _outCam;
            private readonly Material _stageMat;
            private readonly TextMeshPro _caption;

            private readonly Texture2D _reality;
            private readonly Texture2D? _plate;
            private readonly Texture2D _sceneTex;
            private readonly RenderTexture _outRt;
            private readonly Texture2D _readback;
            private readonly int _outW, _outH;

            private readonly string _plateLabel;
            private readonly string _screenMatLabel;
            private readonly string _showLabel;
            private readonly IntroTiming _timing;
            /// <summary>段 4 の乱れに掛かる係数（<c>run.intro.glitchOnSwap</c>）。実機と同じ倍率を掛ける。</summary>
            private readonly float _glitchOnSwap;
            private float _igniteWritten = -1f;

            private Stage(string outDir, GameObject root, Transform head, Camera cam, Material screenMat,
                          IntroVeil veil, ContainmentShell shell, SealedBox box, float halfM,
                          GameObject capRoot, Camera outCam, Material stageMat, TextMeshPro caption,
                          Texture2D reality, Texture2D? plate, Texture2D sceneTex,
                          RenderTexture outRt, Texture2D readback, int outW, int outH,
                          string plateLabel, string screenMatLabel, string showLabel,
                          IntroTiming timing, float glitchOnSwap)
            {
                _outDir = outDir; _root = root; _head = head; _cam = cam; _screenMat = screenMat;
                _veil = veil; _shell = shell; _box = box; _halfM = halfM;
                _capRoot = capRoot; _outCam = outCam; _stageMat = stageMat; _caption = caption;
                _reality = reality; _plate = plate; _sceneTex = sceneTex;
                _outRt = outRt; _readback = readback; _outW = outW; _outH = outH;
                _plateLabel = plateLabel; _screenMatLabel = screenMatLabel; _showLabel = showLabel;
                _timing = timing; _glitchOnSwap = glitchOnSwap;
            }

            public static Stage Create(string outDir)
            {
                // --- 「現実」の代わりに敷く実写プレート（スクリーンの映像にも同じものを使う）---
                Texture2D? plate = LoadPlate(out string plateLabel);
                Texture2D reality = BuildReality(plate, Width, Height);

                // --- show.json。footprint（封印の箱の大きさ）も尺も乱れの倍率も現場の値が正 ---
                ShowJsonSubset? show = LoadShow(out string showLabel);
                ShowLayoutDef? layout = show?.layout;

                // 実機（ShowRunDirector → IntroDirector）と同じ扱い: キーが無ければ JsonUtility が
                // 既定値の実体を作るので、LooksUnset で弾いてコード既定へ落とす。
                ShowIntroDef? introDef = show?.run?.intro;
                if (introDef != null && introDef.LooksUnset) introDef = null;
                IntroTiming timing = introDef != null ? introDef.ToTiming() : IntroTiming.Default;
                float glitchOnSwap = introDef != null ? Mathf.Clamp01(introDef.glitchOnSwap) : 0.8f;
                showLabel += introDef != null
                    ? $" | intro {timing.sealSec:0.0}/{timing.darkSec:0.0}/{timing.igniteSec:0.0}/" +
                      $"{timing.liveSec:0.0}s glitch x{glitchOnSwap:0.00}"
                    : " | intro NOT authored -> code defaults";

                float halfM = FallbackHalfM;
                {
                    ShowRoomDef? room = layout != null && layout.hasRoom
                                        && layout.room != null && layout.room.HasData() ? layout.room : null;
                    if (ContainmentShellLogic.TryFootprint(layout, room, out Vector2 half))
                        halfM = Mathf.Max(half.x, half.y);
                    else
                        showLabel += $" | footprint NOT authored: box at fallback {FallbackHalfM * 2f:0.0}m sq";
                }

                var root = new GameObject("[IntroPreview]") { hideFlags = HideFlags.HideAndDontSave };

                // --- 頭（覆い・殻・スクリーンがぶら下がる。実機と同じ head-lock）---
                var head = new GameObject("Head").transform;
                head.SetParent(root.transform, worldPositionStays: false);

                var cam = new GameObject("Cam").AddComponent<Camera>();
                cam.transform.SetParent(head, worldPositionStays: false);
                cam.clearFlags = CameraClearFlags.SolidColor;
                // ⚠ alpha は 1 で始める。**アプリが描かない所は現実が出ない**のが実機の規約で、
                //    覆いが alpha を 0 へ落とした所にだけ現実が出る。
                cam.backgroundColor = new Color(0f, 0f, 0f, 1f);
                cam.fieldOfView = 90f;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 50f;
                cam.cullingMask = 1 << IntroLayer;   // 開いている Main.unity の中身を写さない
                cam.allowHDR = false;
                cam.allowMSAA = true;
                cam.enabled = false;                 // 手動 Render のみ

                // --- 本編のスクリーン。**材質はシーンのものを複製する**（authored な post が乗る）---
                var sceneScreen = UnityEngine.Object.FindObjectOfType<MjpegScreen>(true);
                Renderer? sceneRenderer = sceneScreen != null ? sceneScreen.GetComponent<Renderer>() : null;
                Material? sceneMat = sceneRenderer != null ? sceneRenderer.sharedMaterial : null;
                string screenMatLabel;
                Material screenMat;
                if (sceneMat != null)
                {
                    screenMat = new Material(sceneMat) { name = "ScreenComposite (introviz)" };
                    screenMatLabel = $"{Ascii(sceneMat.name)} (copied from scene)";
                }
                else
                {
                    Shader? s = Shader.Find("FixedCamVr/ScreenComposite");
                    if (s == null)
                        throw new InvalidOperationException("シェーダ 'FixedCamVr/ScreenComposite' が見つからない");
                    screenMat = new Material(s) { name = "ScreenComposite (introviz)" };
                    screenMatLabel = "shader defaults (no MjpegScreen in scene)";
                }
                screenMat.hideFlags = HideFlags.HideAndDontSave;

                Vector3 screenScale = new Vector3(FallbackScreenW, FallbackScreenH, 1f);
                if (sceneScreen != null)
                {
                    Vector3 ls = sceneScreen.transform.localScale;
                    if (ls.x > 0.01f && ls.y > 0.01f) screenScale = new Vector3(ls.x, ls.y, 1f);
                }

                var screen = GameObject.CreatePrimitive(PrimitiveType.Quad).transform;
                screen.name = "ScreenQuad";
                DestroyIfPresent(screen.GetComponent<Collider>());
                screen.SetParent(head, worldPositionStays: false);
                screen.localPosition = new Vector3(0f, ScreenHeightOffsetM, ScreenDistanceM);
                screen.localScale = screenScale;
                var screenRenderer = screen.GetComponent<MeshRenderer>();
                screenRenderer.sharedMaterial = screenMat;
                screenRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                screenRenderer.receiveShadows = false;

                ConfigureScreenMaterial(screenMat, plate, reality, screenScale.x / screenScale.y);

                // --- 覆い（本物の IntroVeil。開口の平面と global の配布まで実機と同じ）---
                var veil = AddIntroComponent<IntroVeil>(head, "IntroVeil", null);
                var veilSo = new SerializedObject(veil);
                veilSo.FindProperty("screenQuad").objectReferenceValue = screen;
                veilSo.ApplyModifiedPropertiesWithoutUndo();
                if (!veil.IsBuilt)
                    throw new InvalidOperationException(
                        "覆いの実体を組めなかった（シェーダ FixedCamVr/IntroVeil が見つからない）");

                // --- layout の供給元。**自前で持つ**（シーンの ShowControlClient を掴むと、
                //     プレビューが片付いた後にイベント購読が残りうる）---
                var showGo = new GameObject("ShowControlClient");
                showGo.transform.SetParent(root.transform, worldPositionStays: false);
                var showControl = showGo.AddComponent<ShowControlClient>();
                showControl.SetLayoutForPreview(layout);

                // --- 隔離殻（head-lock の全画面）と封印の箱（world 配置）---
                var shell = AddIntroComponent<ContainmentShell>(head, "ContainmentShell", showControl);
                var box = AddIntroComponent<SealedBox>(root.transform, "SealedBox", showControl);

                // --- キャプション帯を足す合成ステージ（遠くの別レイヤ）---
                float quadAspect = (float)Width / Height;
                var capRoot = new GameObject("[IntroPreviewCaption]") { hideFlags = HideFlags.HideAndDontSave };
                capRoot.transform.position = new Vector3(CaptionStageOriginX, 0f, 0f);

                var capQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                capQuad.name = "SceneQuad";
                capQuad.transform.SetParent(capRoot.transform, worldPositionStays: false);
                capQuad.transform.localPosition = new Vector3(0f, CaptionHeightRatio * 0.5f, 0f);
                capQuad.transform.localScale = new Vector3(quadAspect, 1f, 1f);
                DestroyIfPresent(capQuad.GetComponent<Collider>());

                Shader? unlit = Shader.Find("Universal Render Pipeline/Unlit");
                if (unlit == null) throw new InvalidOperationException("シェーダ 'Universal Render Pipeline/Unlit' が無い");
                var stageMat = new Material(unlit)
                { name = "IntroVizStage", hideFlags = HideFlags.HideAndDontSave };
                stageMat.SetColor("_BaseColor", Color.white);
                capQuad.GetComponent<MeshRenderer>().sharedMaterial = stageMat;

                var outCamGo = new GameObject("CaptionCamera");
                outCamGo.transform.SetParent(capRoot.transform, worldPositionStays: false);
                outCamGo.transform.localPosition = new Vector3(0f, 0f, -1f);
                var outCam = outCamGo.AddComponent<Camera>();
                outCam.orthographic = true;
                outCam.orthographicSize = (1f + CaptionHeightRatio) * 0.5f;
                outCam.nearClipPlane = 0.1f;
                outCam.farClipPlane = 5f;
                outCam.clearFlags = CameraClearFlags.SolidColor;
                outCam.backgroundColor = Color.black;   // キャプション帯の地
                outCam.cullingMask = 1 << CaptionLayer;
                outCam.allowHDR = false;
                outCam.allowMSAA = false;
                outCam.enabled = false;

                // キャプションは **ASCII のみ**。Edit Mode の TMP は静的アトラスに焼いていない字を
                // 豆腐で組む（日本語 HUD フォントは HUD の文言ぶんしか焼いていない）。
                var capGo = new GameObject("Caption");
                capGo.transform.SetParent(capRoot.transform, worldPositionStays: false);
                capGo.transform.localPosition = new Vector3(
                    0f, -(1f + CaptionHeightRatio) * 0.5f + CaptionHeightRatio * 0.5f, -0.5f);
                var caption = capGo.AddComponent<TextMeshPro>();
                var capRt = (RectTransform)caption.transform;
                capRt.sizeDelta = new Vector2(quadAspect - 0.02f, CaptionHeightRatio);
                caption.alignment = TextAlignmentOptions.TopLeft;
                caption.richText = false;
                caption.enableWordWrapping = true;   // 長い行は折り返す（切り落とさない）
                caption.fontSize = CaptionFontSize;
                caption.color = new Color(0.95f, 0.95f, 0.7f, 1f);

                SetLayerRecursive(capRoot.transform, CaptionLayer);

                // --- 出力バッファ ---
                var sceneTex = new Texture2D(Width, Height, TextureFormat.RGBA32, mipChain: false)
                { name = "IntroVizScene", hideFlags = HideFlags.HideAndDontSave };
                stageMat.SetTexture("_BaseMap", sceneTex);

                int outW = Width;
                int outH = Mathf.RoundToInt(Height * (1f + CaptionHeightRatio));
                var outRt = new RenderTexture(outW, outH, 24, RenderTextureFormat.ARGB32,
                                              RenderTextureReadWrite.sRGB)
                { name = "IntroVizOutRT" };
                outRt.Create();
                outCam.targetTexture = outRt;
                outCam.aspect = (float)outW / outH;
                var readback = new Texture2D(outW, outH, TextureFormat.RGB24, mipChain: false)
                { hideFlags = HideFlags.HideAndDontSave };

                Debug.Log($"[IntroViz] 現実 = {plateLabel} / スクリーンの材質 = {screenMatLabel} / {showLabel}");

                return new Stage(outDir, root, head, cam, screenMat, veil, shell, box, halfM,
                                 capRoot, outCam, stageMat, caption,
                                 reality, plate, sceneTex, outRt, readback, outW, outH,
                                 plateLabel, screenMatLabel, showLabel, timing, glitchOnSwap);
            }

            // ---- 1 枚ぶん ----

            public void Render(Shot shot, List<string> saved)
            {
                IntroLogic logic = DriveTo(shot.stage, shot.p, _timing);
                IntroWeights w = logic.Weights;
                bool inside = logic.InsideBox;

                PlaceHead(inside);

                // 実機（IntroDirector.Update）と同じ順で配る。覆い → 殻 → 箱 の順序には意味がある
                // （覆いが開口の平面を global へ配り、後から描かれる箱がそれで切られる）。
                _veil.Apply(w);
                _shell.Apply(w);
                _box.Apply(w);

                // 管の点灯と、**映像そのものの出方**。実機は IntroDirector が MjpegScreen の材質へ
                // 両方書く（既定はどちらも 1）。⚠ **2 つは別物** — 管が点くのは段 3、映像が出るのは段 4。
                // 分けていないと、重み live が 0 のままなのに画に映像が出る（2026-08-13 にこの絵で見つけた）。
                _igniteWritten = Mathf.Clamp01(w.ignite);
                _screenMat.SetFloat(CrtIgniteId, _igniteWritten);
                _screenMat.SetFloat(IntroLiveId, Mathf.Clamp01(w.live));
                // ⚠ 乱れの唯一の writer は本来 GlitchFx だが、あれは Update で書くので Edit Mode では
                //    1 度も走らない。段 4 の継ぎ目は乱れが乗った絵でしか判定できないので、ここだけ直接書く。
                //    ⚠⚠ **倍率 glitchOnSwap を忘れない。** 実機は
                //    `SetSustain(w.glitch * _def.glitchOnSwap)` なので、生の重みを書くと
                //    プレビューだけ乱れが強くなる（現行 show.json では 1.00 対 0.80）。
                _screenMat.SetFloat(GlitchId, Mathf.Clamp01(w.glitch) * _glitchOnSwap);
                _screenMat.SetFloat(GlitchSeedId, shot.index * 3.1f + shot.p * 7.3f);
                // 配信断（＝ カメラが繋がっていない）。実機では SignalLostFx が書く。
                _screenMat.SetFloat(SignalLostId, shot.noSignal ? 1f : 0f);

                // 覆い・殻・箱は自分で子 GameObject を作る（レイヤは継がない）。撮る直前に揃える。
                SetLayerRecursive(_root.transform, IntroLayer);

                CaptureScene();
                SetCaption(BuildCaption(shot, w, inside));
                saved.Add(SaveWithCaption(shot.File));
            }

            private void PlaceHead(bool inside)
            {
                // 中に居るときは箱の中央。外に居るときは手前の面から OutsideStandM 離れて箱を向く。
                _head.position = inside
                    ? new Vector3(0f, EyeH, 0f)
                    : new Vector3(0f, EyeH, -(_halfM + OutsideStandM));
                _head.rotation = Quaternion.identity;   // +Z（箱の中心）を向く
            }

            /// <summary>
            /// 導入の面を撮って、<b>実機と同じパススルーの合成</b>を CPU で解く。
            /// </summary>
            private void CaptureScene()
            {
                // ⚠ ステンシル（隔離殻の「印の無い所だけ塗る」）が要るので depth は 24。16 だと
                //    ステンシルが無く、殻の判定が黙って壊れる。
                var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32,
                                           RenderTextureReadWrite.sRGB)
                { antiAliasing = 4 };
                _cam.targetTexture = rt;
                _cam.Render();

                // ⚠ MSAA の RT から直接 ReadPixels しない（解決前の面を読むとまだらになる）。
                var resolved = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGB32,
                                                 RenderTextureReadWrite.sRGB);
                Graphics.Blit(rt, resolved);

                RenderTexture.active = resolved;
                _sceneTex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                RenderTexture.active = null;
                _cam.targetTexture = null;

                // ---- パススルーの合成（Passthrough Windows と同じ式）----
                Color32[] a = _sceneTex.GetPixels32();
                Color32[] r = _reality.GetPixels32();
                for (int i = 0; i < a.Length; i++)
                {
                    float inv = 1f - a[i].a / 255f;
                    a[i] = new Color32(
                        (byte)Mathf.Min(255f, a[i].r + r[i].r * inv),
                        (byte)Mathf.Min(255f, a[i].g + r[i].g * inv),
                        (byte)Mathf.Min(255f, a[i].b + r[i].b * inv),
                        255);
                }
                _sceneTex.SetPixels32(a);
                _sceneTex.Apply();

                resolved.Release();
                UnityEngine.Object.DestroyImmediate(resolved);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }

            private string BuildCaption(Shot shot, in IntroWeights w, bool inside)
            {
                string eye = inside
                    ? "inside (box centre)"
                    : $"outside ({OutsideStandM:0.0}m from the face; the real trigger range is " +
                      $"{IntroLogic.ApproachNearM:0.0}m)";

                return
                    $"INTRO stage {shot.index} {shot.name}  p={shot.p:0.00}  eye {eye}\n" +
                    $"w: passthrough {w.passthrough:0.00}  frame {w.frame:0.00}  shell {w.shell:0.00}  " +
                    $"ignite {w.ignite:0.00}  live {w.live:0.00}  sealBox {w.sealBox:0.00}   " +
                    $"(glitch {w.glitch:0.00} shellReveal {w.shellReveal:0.00})\n" +
                    $"drawn: veil built={B(_veil.IsBuilt)} on={B(_veil.IsActive)} | " +
                    $"shell built={B(_shell.IsBuilt)} s={_shell.AppliedStrength:0.00} " +
                    $"reveal={B(_shell.Revealing)} | " +
                    $"box built={B(_box.IsBuilt)} a={_box.AppliedOpacity:0.00} " +
                    $"footprint={B(_box.HasFootprint)} | " +
                    // ⚠ **床の影も「画に出た」の側で出す**（canon/LEDGER.md 0024）。
                    //    最初この 2 つを出していなかったので、影が 1 画素も無い絵を見て
                    //    「向きが裏側なのか、そもそも組めていないのか」が判別できなかった。
                    $"shadow built={B(_box.ShadowIsBuilt)} a={_box.ShadowAppliedOpacity:0.00} | " +
                    $"crtIgnite={_igniteWritten:0.00}" +
                    (shot.noSignal ? "  signalLost=1.00 (no camera connected)" : "") + "\n" +
                    $"reality {_plateLabel} | screen mat {_screenMatLabel} | {_showLabel}";
            }

            private static string B(bool v) => v ? "1" : "0";

            private void SetCaption(string caption)
            {
                _caption.text = Ascii(caption);
                // Edit Mode では TMP の遅延メッシュ生成が走らない。2 回叩く（1 回目でグリフ要求・
                // 2 回目で焼けたグリフを含めて再レイアウト）。
                _caption.ForceMeshUpdate(ignoreActiveState: true, forceTextReparsing: true);
                _caption.ForceMeshUpdate(ignoreActiveState: true, forceTextReparsing: true);
            }

            private string SaveWithCaption(string fileName)
            {
                _outCam.Render();
                RenderTexture.active = _outRt;
                _readback.ReadPixels(new Rect(0f, 0f, _outW, _outH), 0, 0);
                _readback.Apply();
                RenderTexture.active = null;

                string path = Path.Combine(_outDir, fileName);
                File.WriteAllBytes(path, _readback.EncodeToPNG());
                return path;
            }

            public void Dispose()
            {
                RenderTexture.active = null;
                if (_outRt != null)
                {
                    _outCam.targetTexture = null;
                    _outRt.Release();
                    UnityEngine.Object.DestroyImmediate(_outRt);
                }
                UnityEngine.Object.DestroyImmediate(_readback);
                UnityEngine.Object.DestroyImmediate(_sceneTex);
                UnityEngine.Object.DestroyImmediate(_reality);
                if (_plate != null) UnityEngine.Object.DestroyImmediate(_plate);
                UnityEngine.Object.DestroyImmediate(_stageMat);
                UnityEngine.Object.DestroyImmediate(_screenMat);
                UnityEngine.Object.DestroyImmediate(_capRoot);
                UnityEngine.Object.DestroyImmediate(_root);
            }
        }

        // ---- 部品 ------------------------------------------------------------

        /// <summary>
        /// 導入の実コンポーネントを 1 つ足して、Edit Mode でも実体が組まれた状態にする。
        ///
        /// ⚠ <b>GameObject を無効にしてから AddComponent する。</b> 参照（<c>showControl</c>）を
        /// <c>Awake</c> / <c>OnEnable</c> より<b>先に</b>入れないと、コンポーネントが
        /// <c>FindObjectOfType</c> で<b>開いているシーンの ShowControlClient</b> を掴んで購読し、
        /// プレビューを畳んだ後にその購読だけが残る。無効な GameObject なら Awake は走らないので、
        /// 参照を入れてから起こせば、Editor が Awake をどう扱っても正しい相手に繋がる。
        ///
        /// ⚠ <b>Edit Mode では Awake / OnEnable が走らない</b>ので自分で呼ぶ。ただし
        /// <c>SendMessage</c> は使わない — Edit Mode で呼ぶと Unity が
        /// <c>Assertion failed on expression: 'ShouldRunBehaviour()'</c> を毎回吐き、
        /// <b>実機ログの警告を数える検証（<c>analyze-xp-log.py</c> と同じ流儀）でノイズになる</b>。
        /// リフレクションなら黙って正確に 1 回だけ呼べる。
        /// </summary>
        private static T AddIntroComponent<T>(Transform parent, string name, ShowControlClient? showControl)
            where T : MonoBehaviour
        {
            var go = new GameObject(name);
            go.SetActive(false);
            go.transform.SetParent(parent, worldPositionStays: false);
            var comp = go.AddComponent<T>();
            if (showControl != null)
            {
                var so = new SerializedObject(comp);
                SerializedProperty? prop = so.FindProperty("showControl");
                if (prop != null)
                {
                    prop.objectReferenceValue = showControl;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
            }
            go.SetActive(true);
            InvokeLifecycle(comp, "Awake");
            InvokeLifecycle(comp, "OnEnable");
            return comp;
        }

        /// <summary>持っていれば呼ぶ（無ければ何もしない）。二度組みは各コンポーネントが自分で弾く。</summary>
        private static void InvokeLifecycle(MonoBehaviour comp, string method)
        {
            System.Reflection.MethodInfo? m = comp.GetType().GetMethod(
                method,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public,
                null, Type.EmptyTypes, null);
            m?.Invoke(comp, null);
        }

        /// <summary>
        /// スクリーンの材質を本編と同じ状態にする。
        ///
        /// ⚠ <b>撮像の質（粒・自動露出・管の面）は必ず書く。</b> 実機は show.json に <c>feel</c> が
        /// 無くてもコード既定で効くので、ここを省くと<b>プレビューだけ粒も露出の揺れも無い絵</b>になる
        /// （2026-08-07 に合成プレビューで踏んだのと同じ穴）。書き方は
        /// <see cref="CameraFeelFx.WriteUniforms"/> の口を通す — 写すと片方だけ直したときに黙って食い違う。
        /// </summary>
        private static void ConfigureScreenMaterial(Material mat, Texture2D? plate, Texture2D fallback,
                                                    float frameAspect)
        {
            Texture2D live = plate != null ? plate : fallback;
            float srcAspect = (float)live.width / Mathf.Max(1, live.height);
            Vector2 contain = srcAspect < frameAspect
                ? new Vector2(srcAspect / frameAspect, 1f)
                : new Vector2(1f, frameAspect / srcAspect);

            mat.SetTexture("_LiveTex", live);
            mat.SetVector("_LiveScale", new Vector4(contain.x, contain.y, 0f, 0f));
            mat.SetFloat("_UvRotSteps", 0f);
            mat.SetFloat("_OverlayStrength", 0f);
            mat.SetFloat("_CgStrength", 0f);
            mat.SetFloat("_SwitchDim", 0f);
            mat.SetFloat("_SignalLost", 0f);

            CameraFeelFx.Settings feel = CameraFeelFx.Settings.Resolve(null);
            var feelLogic = new CameraFeelLogic
            {
                TargetLuma = feel.TargetLuma,
                FollowHalfLifeSec = feel.FollowSec,
            };
            // 静止画 1 枚なので自動露出は収束値を使う（時間で追っても同じ値になる）。
            CameraFeelFx.WriteUniforms(mat,
                feel.NoiseDark, feel.NoiseFixed,
                feelLogic.SteadyBiasFor(AverageLuma(live)) * feel.Agc,
                echo: 0f, coarseBlocks: 0f, srcFrame: 0f, mono: 0f);
        }

        /// <summary>16x16 の疎サンプルで平均輝度（読めなければ -1）。<c>MjpegScreen.SampleLuma</c> と同じ手。</summary>
        private static float AverageLuma(Texture2D? tex)
        {
            if (tex == null || tex.width <= 4 || tex.height <= 4) return -1f;
            try
            {
                int stepX = Mathf.Max(1, tex.width / 16), stepY = Mathf.Max(1, tex.height / 16);
                double sum = 0;
                int n = 0;
                for (int y = 0; y < tex.height; y += stepY)
                    for (int x = 0; x < tex.width; x += stepX)
                    {
                        Color c = tex.GetPixel(x, y);
                        sum += 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
                        n++;
                    }
                return n > 0 ? (float)(sum / n) : -1f;
            }
            catch { return -1f; }
        }

        /// <summary>
        /// パススルーの代わりに敷く「現実」。実写プレートを画面いっぱいへ cover-fit する
        /// （<b>アスペクトを崩さない</b> — 崩すと「窓が動いた」のか「絵が歪んだ」のか分からなくなる）。
        /// プレートが 1 枚も無ければ一様な灰。
        /// </summary>
        private static Texture2D BuildReality(Texture2D? plate, int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false)
            { name = "IntroVizReality", hideFlags = HideFlags.HideAndDontSave };
            var px = new Color32[w * h];

            if (plate == null)
            {
                for (int i = 0; i < px.Length; i++) px[i] = new Color32(96, 96, 100, 255);
            }
            else
            {
                float scale = Mathf.Max((float)w / plate.width, (float)h / plate.height);
                float drawW = plate.width * scale;
                float drawH = plate.height * scale;
                float offX = (drawW - w) * 0.5f;
                float offY = (drawH - h) * 0.5f;
                for (int y = 0; y < h; y++)
                {
                    float v = (y + offY) / drawH;
                    for (int x = 0; x < w; x++)
                    {
                        float u = (x + offX) / drawW;
                        px[y * w + x] = plate.GetPixelBilinear(u, v);
                    }
                }
            }

            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// 実写プレートを 1 枚拾う。選び方は <see cref="ShowCompositePreviewPlan.PickPlate"/>
        /// （<c>plate_</c> を先に見る）で、カメラは A→D の順に最初に見つかったもの。
        /// </summary>
        private static Texture2D? LoadPlate(out string label)
        {
            label = "(none) flat gray substitute";
            string? repoRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (repoRoot == null) return null;

            foreach (string camId in PlateCameraIds)
            {
                foreach (string rel in PlateDirsRel)
                {
                    string dir = Path.Combine(repoRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (!Directory.Exists(dir)) continue;
                    string[] names = Array.ConvertAll(Directory.GetFiles(dir), Path.GetFileName);
                    string? pick = ShowCompositePreviewPlan.PickPlate(names, camId);
                    if (pick == null) continue;

                    // sRGB / trilinear + mipChain は実行時の MJPEG テクスチャと同じ扱い（Linear 空間）。
                    var tex = new Texture2D(2, 2, TextureFormat.RGB24, mipChain: true)
                    {
                        name = pick, hideFlags = HideFlags.HideAndDontSave,
                        wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear,
                    };
                    if (tex.LoadImage(File.ReadAllBytes(Path.Combine(dir, pick))))
                    {
                        label = $"{Ascii(pick)} {tex.width}x{tex.height} (cam {camId})";
                        return tex;
                    }
                    Debug.LogWarning($"[IntroViz] プレートを読めない: {pick}");
                    UnityEngine.Object.DestroyImmediate(tex);
                }
            }
            Debug.LogWarning("[IntroViz] 実写プレートが 1 枚も無い → 「現実」は一様な灰で代用する"
                             + "（卓の 📸 無人プレートを撮ると本物の絵で見られる）");
            return null;
        }

        /// <summary>
        /// show.json のうちこのプレビューが要る 2 つだけを読む —
        /// <c>layout</c>（封印の箱と隔離殻の footprint）と <c>run.intro</c>（尺と乱れの倍率）。
        ///
        /// present-flag（<c>hasRoom</c>）は<b>宣言 bool が正</b>なので再導出しない。
        /// <c>run.intro</c> の「キーが無いのに実体がある」は呼び出し側が
        /// <see cref="ShowIntroDef.LooksUnset"/> で弾く（実機と同じ扱い）。
        /// </summary>
        private static ShowJsonSubset? LoadShow(out string label)
        {
            label = "show.json NOT FOUND";
            string? repoRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (repoRoot == null) return null;

            foreach (string rel in ShowJsonCandidatesRel)
            {
                string path = Path.Combine(repoRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) continue;
                try
                {
                    var parsed = JsonUtility.FromJson<ShowJsonSubset>(File.ReadAllText(path));
                    if (parsed?.layout == null) continue;
                    float w = parsed.layout.floor != null ? parsed.layout.floor.w : 0f;
                    float d = parsed.layout.floor != null ? parsed.layout.floor.d : 0f;
                    label = $"{Ascii(rel)} floor {w:0.0}x{d:0.0}";
                    return parsed;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[IntroViz] show.json を読めない（次の候補へ）: {path} — {e.Message}");
                }
            }
            Debug.LogWarning("[IntroViz] show.json が見つからない → 封印の箱は既定の footprint、"
                             + "尺と乱れはコード既定で出す");
            return null;
        }

        [Serializable]
        private sealed class ShowJsonSubset
        {
            public ShowLayoutDef? layout;
            public ShowRunDef? run;
        }

        private static void SetLayerRecursive(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayerRecursive(t.GetChild(i), layer);
        }

        private static void DestroyIfPresent(UnityEngine.Object? o)
        {
            if (o != null) UnityEngine.Object.DestroyImmediate(o);
        }

        /// <summary>
        /// キャプションへ焼く前に非 ASCII を落とす。Edit Mode の TMP は静的アトラスに焼いていない字を
        /// 豆腐で組むので、日本語がそのまま入ると帯が □□□ になる。改行は残す（潰すと読み順が崩れる）。
        /// </summary>
        private static string Ascii(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s!.Length);
            foreach (char c in s) sb.Append(c == '\n' || (c >= ' ' && c <= '~') ? c : '?');
            return sb.ToString();
        }
    }
}
