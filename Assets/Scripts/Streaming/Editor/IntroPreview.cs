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
    /// 導入演出（段 0〜5）を PNG で出す。<b>Play も HMD もビルドも要らない。</b>
    ///
    /// ⚠⚠ <b>これが導入の唯一の安い門。</b> 覆い・隔離殻・実破砕・管の面はすべてシェーダで、
    /// <c>unity.ps1 test</c> には 1 件も出ない（コンパイルが通っても全面マゼンタ・真っ黒になりうる）。
    /// 段を触ったら必ずここを通して<b>焼いた PNG を開くこと</b>
    /// （<c>rules/show-design.md</c>「シェーダを書いたら絵を出す」）。
    ///
    /// <b>本番と同じものを使う</b>のが絶対条件（写経した式は必ずいつか食い違い、証拠として無価値になる）:
    ///   - 段と重みは<b>本物の <see cref="IntroLogic"/> を実際に回して</b>取る（重みを手で作らない）
    ///   - 描くのは実コンポーネント <see cref="IntroVeil"/> / <see cref="ContainmentShell"/> と、
    ///     実シェーダ <c>FixedCamVr/ScreenComposite</c>
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
    /// ⚠⚠ <b>段 2「格下げ」と段 3「輪郭」はここに出ない。</b> あの 2 段は
    /// <c>PassthroughStyler</c>（Assembly-CSharp 側）が<b>実機のパススルー層</b>へ当てるもので、
    /// ここが敷いているのは静止した実写プレートだから。<b>絵が同じでも「効いていない」の証拠にはならない</b> —
    /// 重み（帯の <c>degrade</c> / <c>edge</c>）が動いていることだけを見て、見え方は実機で確かめる。
    /// ここで判定できるのは<b>段 0 / 1（素通し）・段 4（連続開口）・段 5（静止）</b>。
    ///
    /// ⚠ <b>これは段の進み方と形を見るための絵で、現場の見えではない。</b> 「現実」は明るい実写プレートで、
    /// 実際の会場（暗い）とは違う。ブラウン管の<b>曲面</b>（<see cref="CrtScreenMesh"/>）も出ない
    /// （平らな Quad を正投影で撮るため。角の丸みと縁の暗さは出る）。合否はここで出さない
    /// （<c>.claude/reference/why.md</c>「安い門は落とすためだけに使う」）。
    /// </summary>
    public static class IntroPreview
    {
        private const string OutDirRel = "Screenshots/intro";
        private const string FramesDirName = "frames";
        private const string CleanDirName = "clean";
        private const int SequenceFps = 30;

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
        /// 体験エリアの境界からどれだけ離れて立つか (m)。
        ///
        /// ⚠ <b>これは絵の都合であって、現場の立ち位置ではない。</b> 実際の武装距離は
        /// <see cref="IntroLogic.ApproachNearM"/> (1.0m)。導入は最後まで境界の外で流れるので、
        /// 全段この位置から撮る。
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
        private static readonly int SignalLostId = Shader.PropertyToID("_SignalLost");
        private static readonly int SignalFloorId = Shader.PropertyToID("_SignalFloor");
        private static readonly int LiveTexId = Shader.PropertyToID("_LiveTex");

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
            string cleanDir = Path.Combine(outDir, CleanDirName);
            Directory.CreateDirectory(cleanDir);
            foreach (string old in Directory.GetFiles(cleanDir, "intro_*.png")) File.Delete(old);
            bool renderFrames = string.Equals(EditorCliArgs.Get("frames"), "1", StringComparison.Ordinal);
            string framesDir = Path.Combine(outDir, FramesDirName);
            if (renderFrames)
            {
                Directory.CreateDirectory(framesDir);
                foreach (string old in Directory.GetFiles(framesDir, "intro_*.png")) File.Delete(old);
                string cleanFramesDir = Path.Combine(cleanDir, FramesDirName);
                Directory.CreateDirectory(cleanFramesDir);
                foreach (string old in Directory.GetFiles(cleanFramesDir, "intro_*.png")) File.Delete(old);
                foreach (string old in new[] { "frames.tsv", "audio-cues.tsv" })
                {
                    string path = Path.Combine(cleanFramesDir, old);
                    if (File.Exists(path)) File.Delete(path);
                }
            }

            var saved = new List<string>();
            Stage? stage = null;
            try
            {
                stage = Stage.Create(outDir);
                foreach (Shot shot in BuildShots()) stage.Render(shot, saved);
                if (string.Equals(EditorCliArgs.Get("peripheral"), "1", StringComparison.Ordinal))
                    stage.VerifyPeripheralFracture(saved);
                if (renderFrames)
                {
                    var framesTsv = new System.Text.StringBuilder(
                        "index\tstage\tstageProgress\tlive\tshatter\tShatterDrawn\tShatterPieces\tFrozenFrame\tFrozenCount\n");
                    var cuesTsv = new System.Text.StringBuilder("tSec\tcue\tResourceName\n");
                    var cues = new SoundCueLogic();
                    cues.ResetRun();
                    foreach (Shot shot in BuildFrameSequence(stage.Timing))
                        stage.Render(shot, saved, framesTsv, cuesTsv, cues);

                    string cleanFramesDir = Path.Combine(cleanDir, FramesDirName);
                    File.WriteAllText(Path.Combine(cleanFramesDir, "frames.tsv"), framesTsv.ToString());
                    File.WriteAllText(Path.Combine(cleanFramesDir, "audio-cues.tsv"), cuesTsv.ToString());
                    stage.VerifyFrozenFrame(saved);
                    stage.VerifyEdgeDissolve(saved);
                    WriteMeshEvidence(outDir);
                }
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

        /// <summary>
        /// 破片の形の分布を JSON で残す（0222「割れ方が美しくない」への答え合わせ用）。
        /// 1 片 1 行: 重心（覆いのローカル・m）/ 面積 / 枠を閉じる片か。起点からの距離で
        /// 面積がどう変わるかを `Logs/` の検査スクリプトが数える。Build は決定的なので撮った絵と同じ形。
        /// </summary>
        private static void WriteMeshEvidence(string outDir)
        {
            Mesh mesh = IntroFractureMesh.Build();
            try
            {
                var pieces = new List<Vector4>();
                var surfaces = new List<Vector4>();
                mesh.GetUVs(1, pieces);
                mesh.GetUVs(3, surfaces);
                var seen = new HashSet<Vector4>();
                var sb = new System.Text.StringBuilder();
                sb.Append("{\"pieces\":").Append(IntroFractureMesh.LastPieceCount)
                  .Append(",\"triangles\":").Append(IntroFractureMesh.LastTrianglePieceCount)
                  .Append(",\"quads\":").Append(IntroFractureMesh.LastQuadPieceCount)
                  .Append(",\"impactAngular\":[").Append(IntroFractureMesh.ImpactX.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(',').Append(IntroFractureMesh.ImpactY.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
                  .Append("],\"halfExtentLocal\":").Append(IntroFractureMesh.HalfExtentLocal.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(",\"shards\":[");
                bool first = true;
                for (int i = 0; i < pieces.Count; i++)
                {
                    if (surfaces[i].x != IntroFractureMesh.FrontSurface) continue;
                    if (!seen.Add(pieces[i])) continue;
                    Vector4 piece = pieces[i];
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(FormattableString.Invariant(
                        $"{{\"cx\":{piece.x:0.00000},\"cy\":{piece.y:0.00000},\"area\":{piece.z * piece.z:0.000000000},\"closer\":{(piece.w > 0.5f ? 1 : 0)}}}"));
                }
                sb.Append("]}");
                File.WriteAllText(Path.Combine(outDir, "mesh-evidence.json"), sb.ToString());
                Debug.Log($"[IntroViz] mesh evidence: {seen.Count} pieces");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
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
            public readonly int sequenceIndex;

            public Shot(IntroStage stage, int index, string name, float p, int label,
                        bool noSignal = false, int sequenceIndex = -1)
            {
                this.stage = stage; this.index = index; this.name = name; this.p = p; this.label = label;
                this.noSignal = noSignal;
                this.sequenceIndex = sequenceIndex;
            }

            public string File =>
                sequenceIndex >= 0
                    ? $"{FramesDirName}/intro_{sequenceIndex:0000}.png"
                    : $"intro_{index}_{name}_{label:000}{(noSignal ? "_nosignal" : "")}.png";
        }

        /// <summary>
        /// 段の頭・中・終わりを撮る。段 4 は破砕と収束を読むため細かく撮る。
        ///
        /// ⚠ 段 0 と段 1 は 1 枚だけ。どちらも <see cref="IntroLogic.Weights"/> が定数を返す
        /// （素通しのパススルー）ので、3 枚撮っても同じ絵が 3 つ並ぶ。
        ///
        /// ⚠ 終わりは <c>0.999</c>。ちょうど 1.0 を渡すと <see cref="IntroLogic.Tick"/> が次の段へ
        /// 送ってしまい、「段の終わり」ではなく「次の段の頭」が撮れる。
        /// </summary>
        private static IEnumerable<Shot> BuildShots()
        {
            yield return new Shot(IntroStage.Black, 0, "black", 0f, 0);
            yield return new Shot(IntroStage.Real, 1, "real", 0.5f, 50);

            foreach ((float p, int label) in new[] { (0f, 0), (0.5f, 50), (0.999f, 100) })
                yield return new Shot(IntroStage.Degrade, 2, "degrade", p, label);

            yield return new Shot(IntroStage.Structure, 3, "structure", 0.5f, 50);

            foreach ((float p, int label) in
                     // 0221 の時計: 予兆 .00-.06 / 一撃 .06-.10 / スロー .10-.52 / 集結 .52-.90 / 面 .90-.94 / 混合 .94-.99
                     new[]
                     {
                         (0f, 0), (0.04f, 4), (0.065f, 6), (0.08f, 8), (0.10f, 10), (0.14f, 14),
                         (0.25f, 25), (0.40f, 40), (0.52f, 52), (0.65f, 65), (0.78f, 78),
                         (0.86f, 86), (0.90f, 90), (0.92f, 92), (0.96f, 96), (0.999f, 100),
                     })
                yield return new Shot(IntroStage.Frame, 4, "frame", p, label);

            // 段 5 は映像を静止して見せる。3 枚が同じであること自体が乱れの無い契約になる。
            foreach ((float p, int label) in new[] { (0f, 0), (0.25f, 25), (0.999f, 100) })
                yield return new Shot(IntroStage.Swap, 5, "swap", p, label);

            // ⚠⚠ **カメラが 1 台も繋がっていない現場**（canon/LEDGER.md 0025）。
            //    段 5 は飛ばさず、映像の代わりに砂嵐が出る。ここで見るのは 2 つ:
            //      - 段 4（現実が割れている途中）に砂嵐が**乗っていない**こと
            //        （砂嵐は post の最後なので、切らないと割れの過程をまるごと上書きする）
            //      - 段 5 で映像と同じ進みで砂嵐が**入ってくる**こと
            yield return new Shot(IntroStage.Frame, 4, "frame", 0.5f, 50, noSignal: true);
            // 0225: 着地した破片の下に砂嵐が出るか（カメラ無し）と、枠を閉じた後（Frame の保持中）の画。
            yield return new Shot(IntroStage.Frame, 4, "frame", 0.8f, 80, noSignal: true);
            yield return new Shot(IntroStage.Frame, 4, "frame", 0.999f, 100, noSignal: true);
            yield return new Shot(IntroStage.Swap, 5, "swap", 0.25f, 25, noSignal: true);
            yield return new Shot(IntroStage.Swap, 5, "swap", 0.999f, 100, noSignal: true);
        }

        /// <summary>
        /// <c>-Set frames=1</c> のときに出す段 4 → 5 の 30fps 連番。
        /// 最終標本は各段の終了直前に置き、状態機械が次段へ送った値を誤って前段として保存しない。
        /// </summary>
        private static IEnumerable<Shot> BuildFrameSequence(IntroTiming timing)
        {
            int n = 0;
            int frameFrames = Mathf.Max(1, Mathf.CeilToInt(timing.frameSec * SequenceFps));
            for (int i = 0; i < frameFrames; i++)
            {
                float p = Mathf.Min(0.999f, (float)i / frameFrames);
                yield return new Shot(IntroStage.Frame, 4, "frame", p,
                    Mathf.RoundToInt(p * 100f), sequenceIndex: n++);
            }

            int swapFrames = Mathf.Max(1, Mathf.CeilToInt(timing.swapSec * SequenceFps));
            for (int i = 0; i < swapFrames; i++)
            {
                float p = Mathf.Min(0.999f, (float)i / swapFrames);
                yield return new Shot(IntroStage.Swap, 5, "swap", p,
                    Mathf.RoundToInt(p * 100f), sequenceIndex: n++);
            }
        }

        // ---- 段の駆動（重みは本物の状態機械から取る）--------------------------

        /// <summary>
        /// <b>本物の <see cref="IntroLogic"/> を実際に回して</b>目的の段・進みまで持っていく。
        ///
        /// 重みを手で作らないのが要点。手で作ると<b>段の遷移が実装とずれても絵からは気づけない</b>
        /// （このリポジトリが「状態は進んでいるのに画には何も出ていない」を 2026-07-31 に踏んでいる）。
        ///
        /// 尺は show.json の <c>run.intro</c>（無ければ <see cref="IntroTiming.Default"/>）。
        /// 段の中の進みは正規化してあるので、尺を変えても各標本の絵は変わらない。
        /// </summary>
        private static IntroLogic DriveTo(IntroStage target, float p, IntroTiming t)
        {
            var logic = new IntroLogic();
            logic.Configure(t);
            logic.Begin();

            // 段 0。現実が見えていて、体験者はまだ体験エリアの外に立っている。
            logic.Tick(0f, Observed(outsideM: OutsideStandM, atStartSpot: false));
            if (target == IntroStage.Black) return logic;

            // 段 1。開始の合図（体験エリアへの接近）で入る。以後は素通しのまま格下げされていく。
            logic.Tick(1e-3f, Observed(OutsideStandM, atStartSpot: true));
            if (target == IntroStage.Real)
            {
                logic.Tick(p * t.realSec, Observed(OutsideStandM, true));
                return logic;
            }

            logic.Tick(t.realSec, Observed(OutsideStandM, true));       // → 段 2
            if (target == IntroStage.Degrade)
            {
                logic.Tick(p * t.degradeSec, Observed(OutsideStandM, true));
                return logic;
            }

            logic.Tick(t.degradeSec, Observed(OutsideStandM, true));    // → 段 3
            // 段 3 の尺は段 2 と重なるぶんを引いたもの（IntroTiming.TotalSec と同じ式）。
            float ownSec = Mathf.Max(
                t.structureSec - t.degradeSec * (1f - IntroLogic.StructureOverlapAt),
                IntroLogic.StructureMinOwnSec);
            if (target == IntroStage.Structure)
            {
                logic.Tick(p * ownSec, Observed(OutsideStandM, true));
                return logic;
            }

            logic.Tick(ownSec, Observed(OutsideStandM, true));          // → 段 4
            if (target == IntroStage.Frame)
            {
                logic.Tick(p * t.frameSec, Observed(OutsideStandM, true));
                return logic;
            }

            // → 段 5。この 1 tick で「枠を見ている」条件（FrameCenteredHoldSec）も満ちる
            // （`Observed` は足踏みの条件を全部満たしてある）。
            logic.Tick(t.frameSec, Observed(OutsideStandM, true));
            logic.Tick(p * t.swapSec, Observed(OutsideStandM, true));
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
            // 絵を焼くのが目的なので、開始の門と位置の信用は満たしている扱いにする
            // （実機では A の押下と位置合わせがこれを立てる）。
            startAuthorized = true,
            outsideValid = true,
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
            /// <summary>本編の映像として敷いてあるプレート（<see cref="Shot.noSignal"/> で外す）。</summary>
            private readonly Texture? _liveTex;
            // 0225 の探針: 映像のテクスチャを差し替えて「着地した破片の場所に映像が出ているか」を測る。
            private Texture? _liveTexOverride;
            private readonly IntroVeil _veil;
            private readonly ContainmentShell _shell;
            private readonly float _halfM;

            // キャプション帯を足す合成ステージ（遠くの別レイヤ）
            private readonly GameObject _capRoot;
            private readonly Camera _outCam;
            private readonly Material _stageMat;
            private readonly TextMeshPro _caption;

            private readonly Texture2D _reality;
            private readonly Texture2D? _plate;
            private readonly Texture2D _sceneTex;
            private Texture2D? _alphaMask;
            private Color32[]? _alphaPixels;
            private Texture2D? _freezeSourceOverride;
            private readonly RenderTexture _outRt;
            private readonly Texture2D _readback;
            private readonly int _outW, _outH;

            private readonly string _plateLabel;
            private readonly string _screenMatLabel;
            private readonly string _showLabel;
            private readonly IntroTiming _timing;
            private float _igniteWritten = -1f;
            /// <summary>頭のヨー（度）。割れた実景がその場に残るかを測る探針だけが動かす。</summary>
            private float _headYawDeg;
            private float _headPitchDeg;
            private bool _peripheralProbe;
            private bool _peripheralOtherEyeWide;
            private bool _suppressPeripheral;

            public IntroTiming Timing => _timing;

            private Stage(string outDir, GameObject root, Transform head, Camera cam, Material screenMat,
                          IntroVeil veil, ContainmentShell shell, float halfM,
                          GameObject capRoot, Camera outCam, Material stageMat, TextMeshPro caption,
                          Texture2D reality, Texture2D? plate, Texture2D sceneTex,
                          RenderTexture outRt, Texture2D readback, int outW, int outH,
                          string plateLabel, string screenMatLabel, string showLabel,
                          IntroTiming timing)
            {
                _outDir = outDir; _root = root; _head = head; _cam = cam; _screenMat = screenMat;
                _veil = veil; _shell = shell; _halfM = halfM;
                _capRoot = capRoot; _outCam = outCam; _stageMat = stageMat; _caption = caption;
                _reality = reality; _plate = plate; _sceneTex = sceneTex;
                _outRt = outRt; _readback = readback; _outW = outW; _outH = outH;
                _plateLabel = plateLabel; _screenMatLabel = screenMatLabel; _showLabel = showLabel;
                _timing = timing;
                _liveTex = screenMat.GetTexture(LiveTexId);
                _veil.FrozenFrameProvider = CaptureProxySource;
            }

            private IntroFrozenFrameSource? CaptureProxySource()
            {
                // Editor の代理画像も本体と同じ GPU 複製を通す。動く窓へ後から絵を足さない。
                var viewport = Matrix4x4.identity;
                viewport.m00 = viewport.m11 = 0.5f;
                viewport.m03 = viewport.m13 = 0.5f;
                // 周辺の探針は、表示視野より狭い正方形の撮影範囲を明示して再現する。
                Matrix4x4 projection = _peripheralProbe
                    ? Matrix4x4.Perspective(70f, 1f, _cam.nearClipPlane, _cam.farClipPlane)
                    : _cam.projectionMatrix;
                Matrix4x4 worldToUv = viewport * projection * _cam.worldToCameraMatrix;
                Texture source = _freezeSourceOverride != null ? _freezeSourceOverride : _reality;
                Matrix4x4 otherEyeUv = _peripheralOtherEyeWide
                    ? viewport * Matrix4x4.Perspective(110f, 1f, _cam.nearClipPlane, _cam.farClipPlane)
                        * _cam.worldToCameraMatrix
                    : worldToUv;
                return new IntroFrozenFrameSource(source, source, worldToUv, otherEyeUv);
            }

            public static Stage Create(string outDir)
            {
                // --- 「現実」の代わりに敷く実写プレート（スクリーンの映像にも同じものを使う）---
                Texture2D? plate = LoadPlate(out string plateLabel);
                Texture2D reality = BuildReality(plate, Width, Height);

                // --- show.json。footprint（封印の箱の大きさ）と尺は現場の値が正 ---
                ShowJsonSubset? show = LoadShow(out string showLabel);
                ShowLayoutDef? layout = show?.layout;

                // 実機（ShowRunDirector → IntroDirector）と同じ扱い: キーが無ければ JsonUtility が
                // 既定値の実体を作るので、LooksUnset で弾いてコード既定へ落とす。
                ShowIntroDef? introDef = show?.run?.intro;
                if (introDef != null && introDef.LooksUnset) introDef = null;
                IntroTiming timing = introDef != null ? introDef.ToTiming() : IntroTiming.Default;
                showLabel += introDef != null
                    ? $" | intro {timing.realSec:0.0}/{timing.degradeSec:0.0}/{timing.structureSec:0.0}/" +
                      $"{timing.frameSec:0.0}/{timing.swapSec:0.0}s = {timing.TotalSec:0.0}s"
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
                cam.aspect = (float)Width / Height;
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

                // --- 隔離殻（head-lock の全画面）---
                // ⚠ 封印の箱は 2026-08-15 に退避したのでここでも組まない
                //   （箱だけを見る絵は `menu sealedbox` が持っている）。
                var shell = AddIntroComponent<ContainmentShell>(head, "ContainmentShell", showControl);

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

                return new Stage(outDir, root, head, cam, screenMat, veil, shell, halfM,
                                 capRoot, outCam, stageMat, caption,
                                 reality, plate, sceneTex, outRt, readback, outW, outH,
                                 plateLabel, screenMatLabel, showLabel, timing);
            }

            // ---- 1 枚ぶん ----

            public void Render(Shot shot, List<string> saved,
                               System.Text.StringBuilder? framesTsv = null,
                               System.Text.StringBuilder? cuesTsv = null,
                               SoundCueLogic? cueLogic = null)
            {
                IntroLogic logic = DriveTo(shot.stage, shot.p, _timing);
                IntroWeights w = logic.Weights;
                bool inside = logic.InsideBox;

                PlaceHead(inside);

                // 実機（IntroDirector.Update）と同じ順で配る。
                _veil.Apply(w);
                _shell.Apply(w);

                // 実機と同じく _IntroLive は 0/1 の表示ゲート。混合量は IntroVeil の
                // _ScreenFade だけが持つ。ここへ w.live を直接入れると二重に暗くなる。
                _igniteWritten = Mathf.Clamp01(w.ignite);
                _screenMat.SetFloat(CrtIgniteId, _igniteWritten);
                // 0225: 着地した破片がその場所の映像を見せる区間（reveal）も出してよい（IntroDirector と同じ式）。
                _screenMat.SetFloat(IntroLiveId, (w.live > 0f || w.reveal > 0f) ? 1f : 0f);
                _screenMat.SetFloat(GlitchId, 0f);
                // 配信断（＝ カメラが繋がっていない）。実機では SignalLostFx が書く。
                _screenMat.SetFloat(SignalLostId, shot.noSignal ? 1f : 0f);
                // 砂は掛け算で乗るので、下に画があるかで地の持ち上げ方が変わる。
                // この枚は「カメラが繋がっていない」＝ 砂の下は 1 枚も無い。
                _screenMat.SetFloat(SignalFloorId, shot.noSignal ? 1f : 0f);
                // ⚠⚠ **映像そのものを外す。** 「カメラが繋がっていない」は画が 1 枚も来ていない
                //   状態なので、プレートを敷いたままだと砂の下に部屋が残り、
                //   `canon/LEDGER.md` 0025 の現場（砂だけで体験が流れる）を 1 枚も映さない。
                _screenMat.SetTexture(LiveTexId, shot.noSignal ? Texture2D.blackTexture : (_liveTexOverride ?? _liveTex));

                // 覆い・殻は自分で子 GameObject を作る（レイヤは継がない）。撮る直前に揃える。
                SetLayerRecursive(_root.transform, IntroLayer);

                if (shot.sequenceIndex >= 0 && framesTsv != null)
                {
                    float span = logic.Stage == IntroStage.Frame ? _timing.frameSec
                               : logic.Stage == IntroStage.Swap ? _timing.swapSec : 0f;
                    float progress = span > 0f ? Mathf.Clamp01(logic.StageElapsedSec / span) : 0f;
                    framesTsv.AppendLine(FormattableString.Invariant(
                        $"{shot.sequenceIndex}\t{logic.Stage}\t{progress:0.000000}\t{w.live:0.000000}\t{w.shatter:0.000000}\t{(_veil.ShatterDrawn ? 1 : 0)}\t{_veil.ShatterPieces}\t{(_veil.HasFrozenFrame ? 1 : 0)}\t{_veil.FrozenFrameCount}"));

                    if (cuesTsv != null && cueLogic != null)
                    {
                        var soundState = SoundShowState.Idle;
                        soundState.introActive = true;
                        soundState.introStage = logic.Stage;
                        soundState.introWeights = w;
                        float dt = shot.sequenceIndex == 0 ? 0f : 1f / SequenceFps;
                        ReadOnlySpan<SoundCue> fired = cueLogic.Tick(dt, soundState, 0f, out int count);
                        float tSec = shot.sequenceIndex / (float)SequenceFps;
                        for (int i = 0; i < count; i++)
                        {
                            SoundCue cue = fired[i];
                            if (cue != SoundCue.Shatter && cue != SoundCue.ScreenOn && cue != SoundCue.Bell)
                                continue;
                            cuesTsv.AppendLine(FormattableString.Invariant(
                                $"{tSec:0.000000}\t{cue}\t{SoundCueLogic.ResourceName(cue)}"));
                        }
                    }
                }

                CaptureScene();
                SaveClean(shot.File);
                SetCaption(BuildCaption(shot, w, inside));
                saved.Add(SaveWithCaption(shot.File));
            }

            private void SaveClean(string fileName)
            {
                string path = Path.Combine(_outDir, CleanDirName, fileName);
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, _sceneTex.EncodeToPNG());
                // 合成前の alpha も保存する。静止画像を描く間に現実が漏れていないか測る。
                if (_alphaMask != null)
                {
                    string maskPath = Path.Combine(_outDir, "alpha", fileName);
                    string? maskDir = Path.GetDirectoryName(maskPath);
                    if (!string.IsNullOrEmpty(maskDir)) Directory.CreateDirectory(maskDir);
                    File.WriteAllBytes(maskPath, _alphaMask.EncodeToPNG());
                }
            }

            /// <summary>縁のON/OFFを同じ画像で比較し、中心から消える順序を画素で測る。</summary>
            public void VerifyEdgeDissolve(List<string> saved)
            {
                var materials = new List<Material>();
                foreach (Renderer renderer in _veil.GetComponentsInChildren<Renderer>(true))
                    if (renderer.sharedMaterial != null && renderer.sharedMaterial.HasProperty("_EdgeEmphasis"))
                        materials.Add(renderer.sharedMaterial);
                if (materials.Count == 0)
                    throw new InvalidOperationException("縁の描画材質が見つからない");
                Render(new Shot(IntroStage.Frame, 4, "probe_edge_reset", 0f, 0), saved);
                var screen = _head.Find("ScreenQuad");
                var plane = new Plane(screen.forward, screen.position);
                float halfDiagonal = new Vector2(screen.lossyScale.x, screen.lossyScale.y).magnitude * 0.5f;
                var radius = new float[Width * Height];
                for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    Ray ray = _cam.ViewportPointToRay(new Vector3((x + 0.5f) / Width, (y + 0.5f) / Height, 0f));
                    Vector3 local = plane.Raycast(ray, out float distance)
                        ? screen.InverseTransformPoint(ray.GetPoint(distance)) : Vector3.one;
                    radius[y * Width + x] = Mathf.Abs(local.x) > 0.5f || Mathf.Abs(local.y) > 0.5f
                        ? -1f : new Vector2(local.x * screen.lossyScale.x, local.y * screen.lossyScale.y).magnitude / halfDiagonal;
                }
                var proof = new System.Text.StringBuilder("{\"samples\":[");
                float[] progress = { 0.30f, 0.91f, 0.93f, 0.955f, 0.97f, 0.985f, 0.995f, 1f };
                try
                {
                    for (int n = 0; n < progress.Length; n++)
                    {
                        float p = progress[n];
                        foreach (Material material in materials) material.SetFloat("_EdgeEmphasis", 0f);
                        Render(new Shot(IntroStage.Frame, 4, "probe_edge_off", p, n), saved);
                        Color32[] baseline = _sceneTex.GetPixels32();
                        foreach (Material material in materials) material.SetFloat("_EdgeEmphasis", 1f);
                        Render(new Shot(IntroStage.Frame, 4, "probe_edge_on", p, n), saved);
                        Color32[] emphasized = _sceneTex.GetPixels32();
                        double center = 0, outer = 0;
                        int centerCount = 0, outerCount = 0;
                        for (int i = 0; i < radius.Length; i++)
                        {
                            float d = (Mathf.Abs(emphasized[i].r - baseline[i].r)
                                + Mathf.Abs(emphasized[i].g - baseline[i].g)
                                + Mathf.Abs(emphasized[i].b - baseline[i].b)) / 3f;
                            if (radius[i] >= 0f && radius[i] < 0.25f) { center += d; centerCount++; }
                            if (radius[i] > 0.75f) { outer += d; outerCount++; }
                        }
                        center /= Math.Max(1, centerCount);
                        outer /= Math.Max(1, outerCount);
                        float all = MeanPixelDifference(baseline, emphasized);
                        if (n > 0) proof.Append(',');
                        proof.Append(FormattableString.Invariant(
                            $"{{\"p\":{p:0.000},\"allDelta\":{all:0.000000},\"centerDelta\":{center:0.000000},\"outerDelta\":{outer:0.000000}}}"));
                        if (p == 0.91f && (center < 0.5 || outer < 0.5))
                            throw new InvalidOperationException($"着地後の縁が残っていない: center={center} outer={outer}");
                        if (p == 0.97f && (center > 0.02 || outer < 0.5))
                            throw new InvalidOperationException($"中心から外へ消えていない: center={center} outer={outer}");
                        if (p >= 0.995f && all > 0.01f)
                            throw new InvalidOperationException($"縁を消した後の画が元の映像と違う: delta={all}");
                    }
                    proof.Append("]}");
                    File.WriteAllText(Path.Combine(_outDir, "edge-proof.json"), proof.ToString());
                    Debug.Log("[IntroViz] edge dissolve: retained after landing, center clears before perimeter, final image unchanged");
                }
                finally
                {
                    foreach (Material material in materials) material.SetFloat("_EdgeEmphasis", 1f);
                }
            }

            public void VerifyFrozenFrame(List<string> saved)
            {
                // 状態値だけでは速度変化を証明できない。同じ入力で別時刻の実画素を照合する（0221）。
                // 一撃直後の 100ms はスロー中の 100ms の 5 倍以上動き、スロー中は止まらず（0 ではなく）、
                // 着地後は止まる。「止まるはず」と「動くはず」の両方を流して計器を校正する。
                Render(new Shot(IntroStage.Frame, 4, "probe_motion_reset", 0f, 0), saved);
                Render(new Shot(IntroStage.Frame, 4, "probe_burst_a", 0.07f, 7), saved);
                Color32[] burst = _sceneTex.GetPixels32();
                Render(new Shot(IntroStage.Frame, 4, "probe_burst_b", 0.09f, 9), saved);
                float burstDelta = MeanPixelDifference(burst, _sceneTex.GetPixels32());
                Render(new Shot(IntroStage.Frame, 4, "probe_slow_a", 0.30f, 30), saved);
                Color32[] slow = _sceneTex.GetPixels32();
                Render(new Shot(IntroStage.Frame, 4, "probe_slow_b", 0.32f, 32), saved);
                float slowDelta = MeanPixelDifference(slow, _sceneTex.GetPixels32());
                Render(new Shot(IntroStage.Frame, 4, "probe_landed_a", 0.91f, 91), saved);
                Color32[] landed = _sceneTex.GetPixels32();
                Render(new Shot(IntroStage.Frame, 4, "probe_landed_b", 0.93f, 93), saved);
                float landedDelta = MeanPixelDifference(landed, _sceneTex.GetPixels32());
                if (slowDelta < 0.02f)
                    throw new InvalidOperationException(
                        $"スロー中に破片が止まっている（0216 の「止まって逆回転に見える」に戻る）: slow={slowDelta}");
                if (burstDelta < slowDelta * 5f)
                    throw new InvalidOperationException(
                        $"一撃がスローより速くない（速度変化が出ていない）: burst={burstDelta} slow={slowDelta}");
                if (landedDelta > 0.01f)
                    throw new InvalidOperationException(
                        $"着地した実景の面が動いた: landed={landedDelta}");
                Debug.Log($"[IntroViz] speed ramp: burst={burstDelta:F4} slow={slowDelta:F4} landed={landedDelta:F4}");
                File.WriteAllText(Path.Combine(_outDir, "motion-proof.json"), FormattableString.Invariant(
                    $"{{\"burstPixelDelta\":{burstDelta:0.000000},\"slowPixelDelta\":{slowDelta:0.000000},\"landedPixelDelta\":{landedDelta:0.000000}}}"));
                VerifyShatterAnchor(saved);
                VerifyPerPieceReveal(saved);
                _freezeSourceOverride = UnityEngine.Object.Instantiate(_reality);
                try
                {
                    Render(new Shot(IntroStage.Frame, 4, "probe_reset", 0f, 0), saved);
                    Render(new Shot(IntroStage.Frame, 4, "probe_frozen_before", 0.50f, 50), saved);
                    if (!_veil.HasFrozenFrame)
                        throw new InvalidOperationException("フリーズ画像を作れていない");
                    int captured = _veil.FrozenFrameCount;
                    Color32[] before = _sceneTex.GetPixels32();

                    // 入力画像を変えても凍結した破片は変わらないことを実描画で検証する。
                    var changedInput = new Color32[Width * Height];
                    for (int i = 0; i < changedInput.Length; i++) changedInput[i] = new Color32(0, 255, 0, 255);
                    _freezeSourceOverride.SetPixels32(changedInput);
                    _freezeSourceOverride.Apply();
                    Render(new Shot(IntroStage.Frame, 4, "probe_frozen_after", 0.50f, 50), saved);
                    float frozenDelta = MeanPixelDifference(before, _sceneTex.GetPixels32());
                    if (_veil.FrozenFrameCount != captured || frozenDelta > 0.1f)
                        throw new InvalidOperationException($"静止画が入力へ追従した: delta={frozenDelta}");

                    // 新しく凍結すれば変えた入力が現れる。上の検査が常に同じ絵を返していないか校正。
                    Render(new Shot(IntroStage.Frame, 4, "probe_new_reset", 0f, 0), saved);
                    Render(new Shot(IntroStage.Frame, 4, "probe_new_capture", 0.50f, 50), saved);
                    float recaptureDelta = MeanPixelDifference(before, _sceneTex.GetPixels32());
                    if (recaptureDelta < 5f)
                        throw new InvalidOperationException($"再取得しても画像が変わらない: delta={recaptureDelta}");

                    // 視点だけを左右へ動かして、凍結した空間からの視差を保存する。
                    _freezeSourceOverride.SetPixels32(_reality.GetPixels32());
                    _freezeSourceOverride.Apply();
                    Render(new Shot(IntroStage.Frame, 4, "probe_depth_reset", 0f, 0), saved);
                    Render(new Shot(IntroStage.Frame, 4, "probe_depth_center", 0.50f, 50), saved);
                    _cam.transform.localPosition = Vector3.left * 0.032f;
                    Render(new Shot(IntroStage.Frame, 4, "probe_depth_left", 0.50f, 50), saved);
                    Color32[] left = _sceneTex.GetPixels32();
                    _cam.transform.localPosition = Vector3.right * 0.032f;
                    Render(new Shot(IntroStage.Frame, 4, "probe_depth_right", 0.50f, 50), saved);
                    float parallaxDelta = MeanPixelDifference(left, _sceneTex.GetPixels32());
                    if (parallaxDelta < 0.5f)
                        throw new InvalidOperationException($"視点を移動しても破片の像が変わらない: delta={parallaxDelta}");
                    File.WriteAllText(Path.Combine(_outDir, "frozen-proof.json"), FormattableString.Invariant(
                        $"{{\"frozenPixelDelta\":{frozenDelta:0.000000},\"recapturePixelDelta\":{recaptureDelta:0.000000},\"parallaxPixelDelta\":{parallaxDelta:0.000000}}}"));
                    Debug.Log($"[IntroViz] frozen proof: retained={frozenDelta:F4} recaptured={recaptureDelta:F4} parallax={parallaxDelta:F4}");
                }
                finally
                {
                    _cam.transform.localPosition = Vector3.zero;
                    UnityEngine.Object.DestroyImmediate(_freezeSourceOverride);
                    _freezeSourceOverride = null;
                }
            }

            /// <summary>
            /// 着地した破片から、その場所の映像が現れるか（0225）を実画素で測る。映像のテクスチャを緑へ差し替えて
            /// 同じコマを描き、差が出る画素があれば映像が見えている。「止まるはず」＝着地前（p=.60）は差 0。
            /// 「通るはず」＝着地の途中（p=.78）は差が出て、全面が映像の p=.999 ではさらに大きい。
            /// </summary>
            private void VerifyPerPieceReveal(List<string> saved)
            {
                var green = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                var px = new Color32[16];
                for (int i = 0; i < px.Length; i++) px[i] = new Color32(0, 255, 0, 255);
                green.SetPixels32(px);
                green.Apply();
                var proof = new System.Text.StringBuilder("{");
                try
                {
                    float[] probes = { 0.60f, 0.78f, 0.999f };
                    string[] names = { "before", "landing", "closed" };
                    var deltas = new float[probes.Length];
                    for (int k = 0; k < probes.Length; k++)
                    {
                        int label = Mathf.RoundToInt(probes[k] * 100f);
                        _liveTexOverride = null;
                        Render(new Shot(IntroStage.Frame, 4, $"probe_reveal_{names[k]}_reset", 0f, 0), saved);
                        Render(new Shot(IntroStage.Frame, 4, $"probe_reveal_{names[k]}_plate", probes[k], label), saved);
                        Color32[] plate = _sceneTex.GetPixels32();
                        _liveTexOverride = green;
                        Render(new Shot(IntroStage.Frame, 4, $"probe_reveal_{names[k]}_green", probes[k], label), saved);
                        deltas[k] = MeanPixelDifference(plate, _sceneTex.GetPixels32());
                        _liveTexOverride = null;
                        proof.Append(FormattableString.Invariant($"\"{names[k]}PixelDelta\":{deltas[k]:0.000000},"));
                    }
                    if (deltas[0] > 0.01f)
                        throw new InvalidOperationException($"着地前から映像が見えている: before={deltas[0]}");
                    if (deltas[1] < 0.2f)
                        throw new InvalidOperationException($"着地した破片の場所に映像が現れていない: landing={deltas[1]}");
                    if (deltas[2] < deltas[1] * 2f)
                        throw new InvalidOperationException($"枠を閉じても全面が映像になっていない: closed={deltas[2]} landing={deltas[1]}");
                    Debug.Log($"[IntroViz] per-piece reveal: before={deltas[0]:F4} landing={deltas[1]:F4} closed={deltas[2]:F4}");
                    proof.Append("\"ok\":true}");
                    File.WriteAllText(Path.Combine(_outDir, "reveal-proof.json"), proof.ToString());
                }
                finally
                {
                    _liveTexOverride = null;
                    UnityEngine.Object.DestroyImmediate(green);
                    Render(new Shot(IntroStage.Frame, 4, "probe_reveal_done", 0f, 0), saved);
                }
            }

            /// <summary>
            /// 割れた実景が<b>その地点に残る</b>か（ワールド固定）を実画素で測る。静止画あり／なしの両経路。
            ///
            /// 計器の校正を先にやる。「通るはず」＝割れ始めの後に頭を 12° 振ると、破片は世界に残るので
            /// 画は大きく変わる。「止まるはず」＝頭を 12° 振った姿勢で割り始めれば、頭から見た破片の
            /// 並びは振らない場合と同じなので、画は変わらない。片方だけでは対象と計器のどちらが
            /// 悪いか分からない（2026-09-14。以前はプレビューの頭が一度も動かず、頭固定の破片を測れなかった）。
            /// </summary>
            private void VerifyShatterAnchor(List<string> saved)
            {
                const float yawDeg = 12f;
                const float p = 0.36f;
                var proof = new System.Text.StringBuilder("{");
                Func<IntroFrozenFrameSource?>? provider = _veil.FrozenFrameProvider;
                try
                {
                    foreach (bool frozen in new[] { true, false })
                    {
                        string mode = frozen ? "frozen" : "fallback";
                        _veil.FrozenFrameProvider = frozen ? CaptureProxySource : () => null;

                        // 正面で割り始め、そのまま撮る。
                        _headYawDeg = 0f;
                        Render(new Shot(IntroStage.Frame, 4, $"probe_anchor_{mode}_reset", 0f, 0), saved);
                        Render(new Shot(IntroStage.Frame, 4, $"probe_anchor_{mode}_straight", p, 36), saved);
                        if (_veil.HasFrozenFrame != frozen)
                            throw new InvalidOperationException($"{mode} の経路になっていない（HasFrozenFrame={_veil.HasFrozenFrame}）");
                        if (!_veil.ShatterAnchored)
                            throw new InvalidOperationException($"{mode} で割れ始めの姿勢を固定していない");
                        Color32[] straight = _sceneTex.GetPixels32();

                        // 同じ走行のまま頭を振る。破片は世界に残るので画が変わる（通るはず）。
                        _headYawDeg = yawDeg;
                        Render(new Shot(IntroStage.Frame, 4, $"probe_anchor_{mode}_turned", p, 36), saved);
                        float turnedDelta = MeanPixelDifference(straight, _sceneTex.GetPixels32());

                        // 振った姿勢で割り始める。頭から見た並びは正面と同じ（止まるはず）。
                        Render(new Shot(IntroStage.Frame, 4, $"probe_anchor_{mode}_turned_reset", 0f, 0), saved);
                        Render(new Shot(IntroStage.Frame, 4, $"probe_anchor_{mode}_turned_start", p, 36), saved);
                        float restartDelta = MeanPixelDifference(straight, _sceneTex.GetPixels32());
                        _headYawDeg = 0f;

                        if (turnedDelta < 0.5f)
                            throw new InvalidOperationException(
                                $"{mode}: 頭を {yawDeg}° 振っても割れた実景が同じ位置に見える（頭についてきている）: delta={turnedDelta}");
                        if (restartDelta > 0.05f)
                            throw new InvalidOperationException(
                                $"{mode}: 振った姿勢で割り始めた画が正面と違う（計器が壊れている）: delta={restartDelta}");
                        Debug.Log($"[IntroViz] shatter anchor ({mode}): turned={turnedDelta:F4} restart={restartDelta:F4}");
                        proof.Append(FormattableString.Invariant(
                            $"\"{mode}TurnedPixelDelta\":{turnedDelta:0.000000},\"{mode}RestartPixelDelta\":{restartDelta:0.000000},"));
                    }
                    proof.Append(FormattableString.Invariant($"\"yawDeg\":{yawDeg:0.0},\"p\":{p:0.00}}}"));
                    File.WriteAllText(Path.Combine(_outDir, "anchor-proof.json"), proof.ToString());
                }
                finally
                {
                    _headYawDeg = 0f;
                    _veil.FrozenFrameProvider = provider;
                    // 探針の走行を捨て、後続の探針が新しい走行から始まるようにする。
                    Render(new Shot(IntroStage.Frame, 4, "probe_anchor_done", 0f, 0), saved);
                }
            }

            /// <summary>狭い撮影範囲の外を、正面・左右・背後・上下から実画素で確認する。</summary>
            public void VerifyPeripheralFracture(List<string> saved)
            {
                string proofPath = Path.Combine(_outDir, "peripheral-proof.json");
                if (File.Exists(proofPath)) File.Delete(proofPath);
                var proof = new System.Text.StringBuilder("{\"captureFovDeg\":70,\"views\":[");
                string[] names = { "front", "right", "back", "left", "up", "down" };
                float[] yaws = { 0f, 90f, 180f, -90f, 0f, 0f };
                float[] pitches = { 0f, 0f, 0f, 0f, -75f, 75f };
                float[] phases = { 0.04f, 0.14f, 0.30f, 0.65f, 0.86f };
                _peripheralProbe = true;
                try
                {
                    for (int view = 0; view < names.Length; view++)
                    {
                        _headYawDeg = _headPitchDeg = 0f;
                        Render(new Shot(IntroStage.Frame, 4, "peripheral_reset_" + names[view], 0f, 0), saved);
                        Render(new Shot(IntroStage.Frame, 4, "peripheral_anchor_" + names[view], 0.01f, 1), saved);
                        if (!_veil.ShatterAnchored || !_veil.HasFrozenFrame)
                            throw new InvalidOperationException("周辺の探針が正面で撮影姿勢を固定できていない");
                        _headYawDeg = yaws[view];
                        _headPitchDeg = pitches[view];
                        if (view > 0) proof.Append(',');
                        proof.Append("{\"name\":\"").Append(names[view]).Append("\",\"samples\":[");
                        for (int sample = 0; sample < phases.Length; sample++)
                        {
                            float p = phases[sample];
                            int label = Mathf.RoundToInt(p * 100f);
                            _suppressPeripheral = false;
                            Render(new Shot(IntroStage.Frame, 4, "peripheral_" + names[view], p, label), saved);
                            Color32[] withPeripheral = _sceneTex.GetPixels32();
                            long alphaSum = 0;
                            foreach (Color32 pixel in _alphaPixels!) alphaSum += pixel.r;
                            float alpha = (float)((double)alphaSum / (_alphaPixels!.Length * 255));
                            _suppressPeripheral = true;
                            Render(new Shot(IntroStage.Frame, 4, "peripheral_off_" + names[view], p, label), saved);
                            float delta = MeanPixelDifference(withPeripheral, _sceneTex.GetPixels32());
                            _suppressPeripheral = false;
                            if (sample == 0 && view > 0 && alpha > 0.25f)
                                throw new InvalidOperationException($"{names[view]}: 破断前に周囲の現実が消えている alpha={alpha}");
                            if (sample >= 1 && alpha < 0.999f)
                                throw new InvalidOperationException($"{names[view]} p={p}: 破断後にライブ実景が漏れる alpha={alpha}");
                            if (sample == 2 && view > 0 && delta < 0.2f)
                                throw new InvalidOperationException($"{names[view]}: 撮影範囲外に破片が描かれていない delta={delta}");
                            if (sample == 4 && delta > 0.01f)
                                throw new InvalidOperationException($"{names[view]}: 周辺破片が退場していない delta={delta}");
                            if (sample > 0) proof.Append(',');
                            proof.Append(FormattableString.Invariant(
                                $"{{\"p\":{p:0.00},\"alpha\":{alpha:0.000000},\"pixelDelta\":{delta:0.000000}}}"));
                        }
                        proof.Append("]}");
                    }
                    // Monoでは左眼を描く。右眼の撮影範囲だけ広げても左眼の窓に欠けを作らない。
                    _headYawDeg = _headPitchDeg = 0f;
                    Render(new Shot(IntroStage.Frame, 4, "peripheral_eye_reset", 0f, 0), saved);
                    Render(new Shot(IntroStage.Frame, 4, "peripheral_eye_anchor", 0.01f, 1), saved);
                    Render(new Shot(IntroStage.Frame, 4, "peripheral_eye_base", 0.04f, 4), saved);
                    Color32[] eyeBaseline = _sceneTex.GetPixels32();
                    _peripheralOtherEyeWide = true;
                    Render(new Shot(IntroStage.Frame, 4, "peripheral_eye_wide_reset", 0f, 0), saved);
                    Render(new Shot(IntroStage.Frame, 4, "peripheral_eye_wide_anchor", 0.01f, 1), saved);
                    Render(new Shot(IntroStage.Frame, 4, "peripheral_eye_wide", 0.04f, 4), saved);
                    float otherEyeDelta = MeanPixelDifference(eyeBaseline, _sceneTex.GetPixels32());
                    if (otherEyeDelta > 0.01f)
                        throw new InvalidOperationException($"別の眼の撮影範囲が表示中の眼に欠けを作る delta={otherEyeDelta}");
                    proof.Append(FormattableString.Invariant($"],\"otherEyeOverscanDelta\":{otherEyeDelta:0.000000},\"ok\":true}}"));
                    File.WriteAllText(proofPath, proof.ToString());
                    Debug.Log("[IntroViz] peripheral proof: all six directions retain reality, fracture, then retire");
                }
                finally
                {
                    _headYawDeg = _headPitchDeg = 0f;
                    _peripheralProbe = _suppressPeripheral = _peripheralOtherEyeWide = false;
                    Render(new Shot(IntroStage.Frame, 4, "peripheral_done", 0f, 0), saved);
                }
            }

            private static float MeanPixelDifference(Color32[] a, Color32[] b)
            {
                if (a.Length != b.Length) throw new InvalidOperationException("比較画像の画素数が一致しない");
                long difference = 0;
                for (int i = 0; i < a.Length; i++)
                    difference += Math.Abs(a[i].r - b[i].r) + Math.Abs(a[i].g - b[i].g) + Math.Abs(a[i].b - b[i].b);
                return (float)((double)difference / (a.Length * 3));
            }

            private void PlaceHead(bool inside)
            {
                // 中に居るときは体験エリアの中央。外に居るときは境界から OutsideStandM 離れて中を向く。
                _head.position = inside
                    ? new Vector3(0f, EyeH, 0f)
                    : new Vector3(0f, EyeH, -(_halfM + OutsideStandM));
                // +Z（エリアの中心）を向く。探針だけがヨーを足す。
                _head.rotation = Quaternion.Euler(_headPitchDeg, _headYawDeg, 0f);
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
                // 実描画から追加分だけを外す校正。同時刻の差で存在と退場を測る。
                var suppressed = new List<Renderer>();
                if (_suppressPeripheral)
                    foreach (Renderer renderer in _root.GetComponentsInChildren<Renderer>())
                        if (renderer.enabled && renderer.sharedMaterial != null
                            && renderer.sharedMaterial.shader.name == "FixedCamVr/IntroPeripheralFracture")
                        {
                            renderer.enabled = false;
                            suppressed.Add(renderer);
                        }
                _cam.Render();
                foreach (Renderer renderer in suppressed) renderer.enabled = true;

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
                if (_alphaPixels == null || _alphaPixels.Length != a.Length) _alphaPixels = new Color32[a.Length];
                for (int i = 0; i < a.Length; i++)
                {
                    _alphaPixels[i] = new Color32(a[i].a, a[i].a, a[i].a, 255);
                    float inv = 1f - a[i].a / 255f;
                    a[i] = new Color32(
                        (byte)Mathf.Min(255f, a[i].r + r[i].r * inv),
                        (byte)Mathf.Min(255f, a[i].g + r[i].g * inv),
                        (byte)Mathf.Min(255f, a[i].b + r[i].b * inv),
                        255);
                }
                _sceneTex.SetPixels32(a);
                _sceneTex.Apply();
                if (_alphaMask == null) _alphaMask = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
                _alphaMask.SetPixels32(_alphaPixels);
                _alphaMask.Apply();

                resolved.Release();
                UnityEngine.Object.DestroyImmediate(resolved);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }

            private string BuildCaption(Shot shot, in IntroWeights w, bool inside)
            {
                string eye = inside
                    ? "inside (area centre)"
                    : $"outside ({OutsideStandM:0.0}m from the boundary; the real trigger range is " +
                      $"{IntroLogic.ApproachNearM:0.0}m)";

                return
                    $"INTRO stage {shot.index} {shot.name}  p={shot.p:0.00}  eye {eye}\n" +
                    $"w: passthrough {w.passthrough:0.00}  degrade {w.degrade:0.00}  edge {w.edge:0.00}  " +
                    $"fracture {w.shatter:0.00}  terminal frame {w.frame:0.00}  shell {w.shell:0.00}  " +
                    $"live {w.live:0.00}   (glitch {w.glitch:0.00} grain {w.grain:0.00})\n" +
                    $"drawn: veil built={B(_veil.IsBuilt)} on={B(_veil.IsActive)} " +
                    $"shards drawn={B(_veil.ShatterDrawn)} peak={_veil.ShatterPeak:0.00} " +
                    $"pieces={_veil.ShatterPieces} rect={_veil.ShatterRectDesc} " +
                    $"frozen={B(_veil.HasFrozenFrame)} copies={_veil.FrozenFrameCount} " +
                    $"base quads={_veil.ApertureQuads} | " +
                    $"shell built={B(_shell.IsBuilt)} s={_shell.AppliedStrength:0.00} " +
                    $"reveal={B(_shell.Revealing)} | " +
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
                if (_alphaMask != null) UnityEngine.Object.DestroyImmediate(_alphaMask);
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
            mat.SetFloat("_SignalFloor", 0f);

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
