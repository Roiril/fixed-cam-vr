#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FixedCamVr.Streaming.Cg;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// CG 人形の腕が体験者の手に**違和感なく追従するか**を、Play せずに絵と数値で確かめる。
    ///
    /// [`ShowActorVizPreview`] とは見たいものが違う。あちらは「人形が出るか」（静止 4 ポーズ）。
    /// こちらは「**動きが自然か**」で、体験者の姿勢を時系列で合成して毎フレーム駆動する。
    ///
    /// ## 数値を出す理由
    /// 腕が伸び切っているのか、たまたまその角度なのかは**絵では見分けられない**。
    /// 「届かなかった距離」と「腕の伸び率」を出せば、違和感の原因が
    /// 「写像がずれている」のか「そもそも腕が短い」のかを切り分けられる。
    ///
    /// ## 体験者の姿勢の値
    /// 頭（HMD ＝ 目の高さ）からの相対で与える。身長 1.72m・目 1.61m の人体計測に基づく:
    /// 肩は目から 0.26m 下・横 0.18m、腕を下ろした手首は目から 0.78m 下。
    /// **この値は人形とは無関係**（体験者の側の事実）なので、人形を差し替えても変えない。
    /// </summary>
    public static class ShowActorMotionPreview
    {
        private const int Width = 720;
        private const int Height = 720;
        private const string OutDirRel = "Screenshots/actormotion";
        private const string DefaultActorResource = "ShowActors/Remy";
        private const string CgLayerName = "ShowCg";
        private const float ActorHeightM = 1.6f;   // show.json actors[].heightM の既定
        private const float HeadHeightM = 1.6f;    // 体験者の HMD 高さ
        private const float ActorYawDeg = 180f;    // カメラ（-Z 側）を向く
        private const float Fps = 30f;
        private const float ShotIntervalSec = 0.25f;
        private const int SheetCols = 6;
        private const int SheetCell = 240;

        /// <summary>体験者の手の位置（頭からの相対・体験者は +Z を向いている）。</summary>
        private readonly struct Key
        {
            public readonly string Name;
            public readonly Vector3 Left, Right;
            public readonly float HeadYawDeg;
            public readonly float HoldSec, MoveSec;
            public Key(string name, Vector3 left, Vector3 right, float holdSec = 1f,
                       float moveSec = 0.5f, float headYawDeg = 0f)
            {
                Name = name; Left = left; Right = right;
                HoldSec = holdSec; MoveSec = moveSec; HeadYawDeg = headYawDeg;
            }
        }

        // 自然に立って腕を下ろした状態。目から 0.70m 下・横 0.19m。
        // ⚠ **人は立っているとき肘を伸ばし切らない**（5〜15° 曲がる）。ここを「真下へ伸ばし切り」の
        //    値にすると、写像が正しくても腕が棒になって、実装の良し悪しが絵に出なくなる。
        //    伸ばし切りは下の reachDown で別に見る。**いちばん長く続く姿勢がこれ**。
        private static readonly Vector3 RestL = new(-0.19f, -0.70f, 0.03f);
        private static readonly Vector3 RestR = new(+0.19f, -0.70f, 0.03f);

        private static readonly Key[] ArmScenario =
        {
            new("rest",      RestL, RestR, holdSec: 1.0f, moveSec: 0f),
            new("front",     new(-0.12f, -0.42f, 0.28f), new(+0.12f, -0.42f, 0.28f)),
            new("wide",      new(-0.62f, -0.28f, 0.04f), new(+0.62f, -0.28f, 0.04f)),
            new("raise",     new(-0.28f, +0.12f, 0.18f), new(+0.28f, +0.12f, 0.18f)),
            // 導入演出の「右手を上げてみてください」。片手だけ動く形が正しく出るか。
            new("rightUp",   RestL,                      new(+0.26f, +0.20f, 0.14f)),
            // 意識的に真下へ伸ばし切る。**ここは伸び切って正しい**（人間もそうなる）。
            new("reachDown", new(-0.20f, -0.78f, 0.02f), new(+0.20f, -0.78f, 0.02f)),
            new("rest2",     RestL, RestR, holdSec: 0.5f),
        };

        // 手は下ろしたまま、頭だけ振る。いまの follow は体の向き＝頭の向きなので、
        // **首を振ると人形の体ごと回る**。それがどう見えるかを撮る。
        private static readonly Key[] TurnScenario =
        {
            new("face0",   RestL, RestR, holdSec: 0.8f, moveSec: 0f,  headYawDeg: 0f),
            new("turn45",  RestL, RestR, holdSec: 0.6f, moveSec: 0.6f, headYawDeg: 45f),
            new("turn90",  RestL, RestR, holdSec: 0.8f, moveSec: 0.6f, headYawDeg: 90f),
            new("back0",   RestL, RestR, holdSec: 0.8f, moveSec: 0.8f, headYawDeg: 0f),
        };

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Actor Motion", priority = 252)]
        private static void Run()
        {
            int layer = LayerMask.NameToLayer(CgLayerName);
            if (layer < 0) { Debug.LogError($"[ActorMotion] レイヤ '{CgLayerName}' が未定義です。"); return; }

            GameObject? prefab = ResolveActorPrefab(out string actorOrigin);
            if (prefab == null)
            {
                Debug.LogError($"[ActorMotion] Resources/{DefaultActorResource} が読めません。");
                return;
            }
            Debug.Log($"[ActorMotion] 対象: {prefab.name}（{actorOrigin}）");

            GameObject? root = null;
            RenderTexture? rt = null;
            Texture2D? tex = null;
            var report = new StringBuilder();
            try
            {
                root = new GameObject("[ActorMotionPreview]") { hideFlags = HideFlags.HideAndDontSave };
                GameObject actor = UnityEngine.Object.Instantiate(prefab, root.transform);
                actor.transform.localPosition = Vector3.zero;
                actor.transform.localRotation = Quaternion.Euler(0f, ActorYawDeg, 0f);
                // ⚠ Edit Mode は骨を動かしてもスキニングがキャッシュされ、全フレームが同じ絵になる。
                foreach (var smr in actor.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    smr.forceMatrixRecalculationPerRender = true;
                    smr.updateWhenOffscreen = true;
                }
                var rig = actor.GetComponent<ShowActorRig>();
                if (rig == null) { Debug.LogError("[ActorMotion] ShowActorRig がありません。"); return; }
                rig.Prepare();
                if (!rig.HasRig)
                {
                    Debug.LogError($"[ActorMotion] {prefab.name} は腕のボーンを解決できません（腕は動きません）。");
                    return;
                }
                float k = Mathf.Clamp(ActorHeightM / Mathf.Max(0.1f, rig.MeasuredHeightM), 0.05f, 20f);
                actor.transform.localScale = Vector3.one * k;
                SetLayerRecursive(root.transform, layer);

                var camGo = new GameObject("MotionCam") { hideFlags = HideFlags.HideAndDontSave };
                camGo.transform.SetParent(root.transform, false);
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.08f, 0.08f, 0.10f, 1f);
                cam.fieldOfView = 45f;
                cam.nearClipPlane = 0.03f;
                cam.farClipPlane = 30f;
                cam.cullingMask = 1 << layer;

                rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32);
                tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);

                string outDir = Path.Combine(Application.dataPath, OutDirRel);
                if (Directory.Exists(outDir)) Directory.Delete(outDir, true);   // 前回の残りと混ぜない
                Directory.CreateDirectory(outDir);

                SetupFullFraming(actor.transform);
                report.AppendLine($"# 腕の駆動 実測  {prefab.name}（{actorOrigin}）");
                report.AppendLine();
                AppendRigFacts(report, rig, k);

                RunScenario("arms", ArmScenario, rig, actor.transform, cam, rt, tex, outDir, report);
                RunScenario("turn", TurnScenario, rig, actor.transform, cam, rt, tex, outDir, report);
                RunWalk(rig, actor.transform, cam, rt, tex, outDir, report);

                string reportPath = Path.Combine(outDir, "report.md");
                File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
                Debug.Log($"[ActorMotion] 完了 → Assets/{OutDirRel}/（report.md に数値）");
            }
            catch (Exception e)
            {
                Debug.LogError($"[ActorMotion] 失敗: {e}");
            }
            finally
            {
                if (rt != null) { RenderTexture.active = null; rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                AssetDatabase.Refresh();
            }
        }

        /// <summary>人形そのものの寸法。写像が合っているかを読むための土台。</summary>
        private static void AppendRigFacts(StringBuilder sb, ShowActorRig rig, float scale)
        {
            ShowActorRig.ArmDiag d = rig.LeftDiag;   // Drive 前は既定値なので 1 回空回しして測る
            rig.Drive(new ShowBodyInput(true, Vector3.up * HeadHeightM, 0f, false, Vector3.zero, false, Vector3.zero),
                      ActorYawDeg, 1f / Fps);
            d = rig.LeftDiag;
            Vector3 headW = rig.HeadWorldPosition;
            sb.AppendLine($"- 実寸 {rig.MeasuredHeightM:F3} m → 縮尺 {scale:F3}（表示 {ActorHeightM:F2} m）");
            sb.AppendLine($"- 腕長 {d.ArmLength:F3} m（身長比 {d.ArmLength / ActorHeightM * 100f:F1}%）");
            sb.AppendLine($"- 肩は頭から下 {headW.y - d.Shoulder.y:F3} m・横 {Mathf.Abs(d.Shoulder.x):F3} m");
            // ⚠ 体験者側は推定値。ここに数字を直書きすると比率を変えたときに黙って食い違う。
            Vector3 ps = ActorArmLogic.EstimateShoulder(Vector3.up * HeadHeightM, 0f, +1f, HeadHeightM);
            sb.AppendLine($"- 体験者（頭 {HeadHeightM:F2} m）は肩が頭から下 {HeadHeightM - ps.y:F3} m・" +
                          $"横 {ps.x:F3} m / 腕長 {ActorArmLogic.EstimateArmLengthM(HeadHeightM):F3} m（推定）");
            sb.AppendLine($"- 腕長の比（人形 / 体験者）= {ActorArmLogic.ArmScale(d.ArmLength, HeadHeightM):F3}");
            // ⚠ 縮尺は「ボーンの最高点」を身長とみなして決まる。頭頂までボーンが無い人形では
            //    実寸を小さく見積もって**拡大しすぎる**。見た目の高さで気づけるように出す。
            // ⚠ 閾値は 1.25。renderer bounds は膨らむので、Remy でも 1.11 倍に出る（誤検出しない値にする）。
            float over = _fullHeight / ActorHeightM;
            sb.AppendLine($"- 実際に描かれる高さ **{_fullHeight:F2} m**" +
                          (over > 1.25f
                              ? $"（目標 {ActorHeightM:F2} m の {over:F2} 倍。**この人形はボーンが頭頂まで無いので拡大されすぎている** — "
                                + "show.json の heightM を下げるか、プレハブのボーンを直す）"
                              : $"（目標 {ActorHeightM:F2} m）"));
            sb.AppendLine();
            rig.ResetPose();
        }

        private static void RunScenario(string id, Key[] keys, ShowActorRig rig, Transform actor, Camera cam,
                                        RenderTexture rt, Texture2D tex, string outDir, StringBuilder sb)
        {
            rig.ResetPose();
            float dt = 1f / Fps;
            var shots = new List<Texture2D>();
            var close = new List<Texture2D>();
            var rows = new List<string>();
            float sinceShot = ShotIntervalSec;   // 先頭で 1 枚撮る
            int index = 0;

            Vector3 prevL = keys[0].Left, prevR = keys[0].Right;
            float prevYaw = keys[0].HeadYawDeg;
            // 体の向き。実行時（ShowCgLayer）と同じく、頭の向きを鈍らせたものを人形の向きに使う。
            float bodyYaw = keys[0].HeadYawDeg;

            foreach (Key key in keys)
            {
                int moveFrames = Mathf.RoundToInt(key.MoveSec * Fps);
                int holdFrames = Mathf.RoundToInt(key.HoldSec * Fps);
                // 各キーの最後のフレームでの実測を代表値として残す（遷移の途中は絵で見る）。
                ShowActorRig.ArmDiag lastL = default, lastR = default;

                for (int f = 0; f < moveFrames + holdFrames; f++)
                {
                    float t = moveFrames > 0 ? Mathf.Clamp01((f + 1f) / moveFrames) : 1f;
                    // なめらかに動かす（等速だと止まり際が機械的で、平滑化の効きが読めない）。
                    float s = t * t * (3f - 2f * t);
                    Vector3 l = Vector3.Lerp(prevL, key.Left, s);
                    Vector3 r = Vector3.Lerp(prevR, key.Right, s);
                    float yaw = Mathf.Lerp(prevYaw, key.HeadYawDeg, s);

                    Vector3 head = new Vector3(0f, HeadHeightM, 0f);
                    Quaternion q = Quaternion.Euler(0f, yaw, 0f);
                    var body = new ShowBodyInput(true, head, yaw, true, head + q * l, true, head + q * r);
                    // follow と同じ条件で測る: 人形の体の向き＝体験者の体の向き（頭の向きを鈍らせたもの）。
                    // ActorYawDeg はカメラを向いた基準なので、体の向きをそこへ足す。
                    bodyYaw = ActorArmLogic.SmoothYawDeg(bodyYaw, yaw, dt);
                    // ⚠ **人形の Transform も回す**。実行時は ShowCgLayer.PlaceActor がこれをやる。
                    //    回さないと体が正面のまま腕だけ回った絵になり、idle 位置・肘の向き・
                    //    「手の前後」の測定まで全部ずれる（実際そうなっていた）。
                    actor.localRotation = Quaternion.Euler(0f, ActorYawDeg + bodyYaw, 0f);
                    rig.Drive(body, ActorYawDeg + bodyYaw, dt, bodyYaw);
                    lastL = rig.LeftDiag; lastR = rig.RightDiag;

                    sinceShot += dt;
                    if (sinceShot >= ShotIntervalSec)
                    {
                        sinceShot = 0f;
                        // ⚠ 全身だけだと肘の曲がりが数 px にしかならず、**改善しても絵が同じに見える**
                        //    （実際そうなった）。腕を判定するための寄りを必ず一緒に撮る。
                        AimFull(cam);
                        shots.Add(Shoot(cam, rt, tex, outDir, $"{id}_{index:D3}"));
                        AimUpper(cam, lastL.Shoulder.y);
                        close.Add(Shoot(cam, rt, tex, outDir, $"{id}_{index:D3}_arm"));
                        index++;
                    }
                }

                string fwd = lastL.ForwardM.ToString("+0.00;-0.00", CultureInfo.InvariantCulture);
                string twist = Mathf.DeltaAngle(bodyYaw, key.HeadYawDeg)
                                    .ToString("+0;-0", CultureInfo.InvariantCulture);
                rows.Add($"| {key.Name} | {lastL.TargetReachRatio:F2} | {lastL.DeficitM:F3} | {lastL.Extension:F2} " +
                         $"| {lastR.TargetReachRatio:F2} | {lastR.DeficitM:F3} | {lastR.Extension:F2} " +
                         $"| {fwd} | {twist}° |");
                prevL = key.Left; prevR = key.Right; prevYaw = key.HeadYawDeg;
            }

            sb.AppendLine($"## {id}");
            sb.AppendLine();
            sb.AppendLine("目標到達率 = 肩から目標までの距離 / 腕長（**1.00 を超えると届かない**）。");
            sb.AppendLine("届かない = 目標と実際の手首の距離 (m)。伸び率 = 肩から手首 / 腕長（1.00 が伸び切り）。");
            sb.AppendLine("手の前後 = 手首が体の前(+)か後ろ(−)か (m)。**大きく負なら手が背中へ回っている**。");
            sb.AppendLine("ねじれ = 頭の向き − 体の向き（人の首は 50° まで）。");
            sb.AppendLine();
            sb.AppendLine("| ポーズ | 左 到達率 | 左 届かない | 左 伸び率 | 右 到達率 | 右 届かない | 右 伸び率 " +
                          "| 手の前後 | ねじれ |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
            foreach (string r in rows) sb.AppendLine(r);
            sb.AppendLine();

            SaveContactSheet(shots, Path.Combine(outDir, $"{id}_sheet.png"));
            SaveContactSheet(close, Path.Combine(outDir, $"{id}_arm_sheet.png"));
            foreach (Texture2D t in shots) UnityEngine.Object.DestroyImmediate(t);
            foreach (Texture2D t in close) UnityEngine.Object.DestroyImmediate(t);
            sb.AppendLine($"絵: 全身 `{id}_000.png` / 腕の寄り `{id}_000_arm.png` … 各 {shots.Count} 枚。");
            sb.AppendLine($"一覧 `{id}_sheet.png` `{id}_arm_sheet.png`。" +
                          "**肘が曲がっているかの判定は寄りのフル解像度で**（全身では数 px にしかならない）。");
            sb.AppendLine();
        }

        /// <summary>
        /// 体験者の位置に人形が追従する（cgMode="follow"）ときの挙動。
        ///
        /// **スクリーンに映っているのは 0.15 秒前の絵**なので、人形も同じ時刻の体験者を写す。
        /// 実行時は <c>ShowCgLayer</c> が <see cref="CourseTrack"/> で同じことをする — ここでは
        /// その遅れが数値どおりに効いているか（歩行中は遅れ、止まれば一致する）を確かめる。
        /// </summary>
        private static void RunWalk(ShowActorRig rig, Transform actor, Camera cam, RenderTexture rt,
                                    Texture2D tex, string outDir, StringBuilder sb)
        {
            rig.ResetPose();
            float dt = 1f / Fps;
            var track = new CourseTrack();
            var shots = new List<Texture2D>();
            var rows = new List<string>();
            float sinceShot = ShotIntervalSec;
            int index = 0;
            float maxLag = 0f;
            // 記録する時刻。**1 回ずつ**（「その時刻の近く」で拾うと同じ場面が数行に増える）。
            float[] marks = { 0.4f, 1.2f, 2.6f };
            int mark = 0;

            // 立ち止まる(0.5s) → 右へ 1.0m/s で歩く(1.5s) → 立ち止まる(1.0s)。
            const float total = 3.0f;
            for (int f = 0; f * dt < total; f++)
            {
                float t = f * dt;
                float x = t < 0.5f ? -0.6f
                        : t < 2.0f ? -0.6f + (t - 0.5f) * 0.8f
                        : 0.6f;
                var now = new Vector2(x, 0f);
                track.Push(t, now);
                // 人形は「映像に映っている時刻」の体験者の場所へ立つ。
                Vector2 shown = track.TrySample(t - 0.15f, out Vector2 s) ? s : now;
                actor.localPosition = new Vector3(shown.x, 0f, 0f);
                actor.localRotation = Quaternion.Euler(0f, ActorYawDeg, 0f);

                Vector3 head = new Vector3(now.x, HeadHeightM, 0f);
                var body = new ShowBodyInput(true, head, 0f, true, head + RestL, true, head + RestR);
                rig.Drive(body, ActorYawDeg, dt, 0f);

                float lag = Mathf.Abs(now.x - shown.x);
                if (t > 0.7f && t < 2.0f) maxLag = Mathf.Max(maxLag, lag);
                if (mark < marks.Length && t >= marks[mark])
                {
                    mark++;
                    string scene = t < 0.5f ? "止まっている" : t < 2.0f ? "歩いている（0.8 m/s）" : "止まった後";
                    rows.Add($"| {scene} " +
                             $"| {now.x.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)} " +
                             $"| {shown.x.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)} " +
                             $"| {lag * 100f:F0} cm |");
                }

                sinceShot += dt;
                if (sinceShot >= ShotIntervalSec)
                {
                    sinceShot = 0f;
                    AimFull(cam);
                    shots.Add(Shoot(cam, rt, tex, outDir, $"walk_{index:D3}"));
                    index++;
                }
            }
            actor.localPosition = Vector3.zero;

            sb.AppendLine("## walk（体験者の位置への追従）");
            sb.AppendLine();
            sb.AppendLine("スクリーンに映っているのは 0.15 秒前の絵なので、人形も同じ時刻の体験者の場所に立つ。");
            sb.AppendLine("**歩行中だけ遅れ、止まれば一致する**のが正しい（止まっているのにずれていたら壊れている）。");
            sb.AppendLine();
            sb.AppendLine("| 場面 | 体験者 X | 人形 X | ずれ |");
            sb.AppendLine("|---|---|---|---|");
            foreach (string r in rows) sb.AppendLine(r);
            sb.AppendLine();
            sb.AppendLine($"歩行中（0.8 m/s）の最大の遅れ **{maxLag * 100f:F0} cm**" +
                          $"（0.15 秒 × 0.8 m/s = 12cm が理論値）。");
            SaveContactSheet(shots, Path.Combine(outDir, "walk_sheet.png"));
            foreach (Texture2D t2 in shots) UnityEngine.Object.DestroyImmediate(t2);
            sb.AppendLine($"絵: `walk_000.png` … 計 {shots.Count} 枚 / 一覧 `walk_sheet.png`");
            sb.AppendLine();
        }

        // 全身ショットの画枠。人形ごとに実際の見た目の大きさから 1 回決めて固定する
        // （フレームごとに合わせ直すと、腕を動かすたびに画枠が動いて比較できない）。
        private static float _fullDist = 3f, _fullCenterY = 0.95f, _fullHeight = 1.6f;

        /// <summary>
        /// 全身の画枠を人形の実測から決める。
        /// ⚠ 固定値にすると頭身の違う人形で頭が切れる（市松人形で実際に切れた）。
        /// ボーンではなく**描かれるメッシュの範囲**で測る — 髪や着物はボーンより外へ広がる。
        /// </summary>
        private static void SetupFullFraming(Transform actor)
        {
            bool any = false;
            Bounds b = new Bounds(actor.position, Vector3.zero);
            foreach (Renderer r in actor.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            float h = any ? Mathf.Max(0.2f, b.size.y) : 1.6f;
            _fullHeight = h;
            _fullCenterY = any ? b.center.y : 0.8f;
            // 縦に人形の 1.25 倍が入る距離（上下に余白を残す）。
            _fullDist = (h * 0.625f) / Mathf.Tan(45f * 0.5f * Mathf.Deg2Rad);
        }

        /// <summary>全身。立ち姿と体の向きを見る。</summary>
        private static void AimFull(Camera cam)
        {
            cam.fieldOfView = 45f;
            cam.transform.position = new Vector3(0f, _fullCenterY + _fullDist * 0.06f, -_fullDist);
            cam.transform.LookAt(new Vector3(0f, _fullCenterY, 0f), Vector3.up);
        }

        /// <summary>
        /// 上半身の寄り。**肘が曲がっているか**はここでしか判定できない。
        ///
        /// ⚠ 注視点は**人形の実際の肩の高さ**から決める。固定値にすると頭身の違う人形で外れる
        /// （市松人形は 3.5 頭身で肩が 1.56m にあり、人体用の 1.15m を見ると帯しか写らなかった）。
        /// </summary>
        private static void AimUpper(Camera cam, float shoulderY)
        {
            cam.fieldOfView = 38f;
            float y = Mathf.Clamp(shoulderY, 0.3f, 3f);
            cam.transform.position = new Vector3(0.35f, y + 0.20f, -1.55f);
            cam.transform.LookAt(new Vector3(0f, y - 0.22f, 0f), Vector3.up);
        }

        private static Texture2D Shoot(Camera cam, RenderTexture rt, Texture2D tex, string dir, string name)
        {
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0f, 0f, Width, Height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), tex.EncodeToPNG());

            var copy = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            copy.SetPixels32(tex.GetPixels32());
            copy.Apply();
            return copy;
        }

        /// <summary>一覧。**どこを見るかを決めるため**のもので、判定はフル解像度の連番で行う。</summary>
        private static void SaveContactSheet(List<Texture2D> shots, string path)
        {
            if (shots.Count == 0) return;
            int cols = Mathf.Min(SheetCols, shots.Count);
            int rows = Mathf.CeilToInt(shots.Count / (float)cols);
            var sheet = new Texture2D(cols * SheetCell, rows * SheetCell, TextureFormat.RGB24, false);
            var clear = new Color32[cols * SheetCell * rows * SheetCell];
            for (int i = 0; i < clear.Length; i++) clear[i] = new Color32(12, 12, 16, 255);
            sheet.SetPixels32(clear);

            for (int i = 0; i < shots.Count; i++)
            {
                int cx = (i % cols) * SheetCell;
                // 画像は左下原点。1 行目が上に来るように行を反転する。
                int cy = (rows - 1 - i / cols) * SheetCell;
                Texture2D src = shots[i];
                for (int y = 0; y < SheetCell; y++)
                {
                    for (int x = 0; x < SheetCell; x++)
                    {
                        int sx = Mathf.Min(src.width - 1, x * src.width / SheetCell);
                        int sy = Mathf.Min(src.height - 1, y * src.height / SheetCell);
                        sheet.SetPixel(cx + x, cy + y, src.GetPixel(sx, sy));
                    }
                }
            }
            sheet.Apply();
            File.WriteAllBytes(path, sheet.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(sheet);
        }

        [Serializable]
        private sealed class ShowActorsOnly
        {
            public ShowActorDef[] actors = Array.Empty<ShowActorDef>();
        }

        /// <summary>
        /// 見る人形を決める。**Project ウィンドウの選択 > show.json の actors[0] > 既定**。
        /// show.json を見るのは、**現場で出る人形と違うものを測っても意味が無い**から
        /// （show.json は 2026-08-05 に市松人形へ差し替わった）。
        /// </summary>
        private static GameObject? ResolveActorPrefab(out string origin)
        {
            foreach (UnityEngine.Object obj in Selection.objects)
            {
                string p = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(p) || !p.Contains("/Resources/ShowActors/")) continue;
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                if (go != null) { origin = "Project の選択"; return go; }
            }

            string showPath = Path.Combine(Directory.GetParent(Application.dataPath)!.FullName,
                                           "tools", "web-compositor", "show.json");
            if (File.Exists(showPath))
            {
                try
                {
                    var s = JsonUtility.FromJson<ShowActorsOnly>(File.ReadAllText(showPath));
                    string res = s?.actors != null && s.actors.Length > 0 ? s.actors[0].prefab : "";
                    if (!string.IsNullOrEmpty(res))
                    {
                        var go = Resources.Load<GameObject>(res);
                        if (go != null) { origin = $"show.json actors[0] ({res})"; return go; }
                        Debug.LogWarning($"[ActorMotion] show.json の '{res}' が Resources から読めません。");
                    }
                }
                catch (Exception e) { Debug.LogWarning($"[ActorMotion] show.json を読めません: {e.Message}"); }
            }
            origin = $"既定 ({DefaultActorResource})";
            return Resources.Load<GameObject>(DefaultActorResource);
        }

        private static void SetLayerRecursive(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayerRecursive(t.GetChild(i), layer);
        }

        private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);
    }
}
