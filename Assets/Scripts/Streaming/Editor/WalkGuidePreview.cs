#nullable enable
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// <b>歩行誘導（床の矢印と円）を 1 コマずつ焼く</b>（<c>canon/LEDGER.md</c> 0079）。
    ///
    /// ⚠ <b>実シェーダ</b>（<c>FixedCamVr/WalkGuideArrow</c> / <c>WalkGuideRing</c>）と
    /// <b>実ロジック</b>（<see cref="WalkGuideLogic"/> ＋ <see cref="WalkGuidePath"/>）を通す。
    /// 道筋も <c>show.json</c> の <c>layout</c> から実際に解く ＝ <b>現場と同じ場所に出る</b>。
    ///
    /// ⚠ <b>実機の見え方そのものではない</b> — 両眼視差・レンズ・パススルーは入っていない。
    /// 床と壁は<b>置き換えの暗い板</b>（実物の床の色でも明るさでもない）。
    /// <b>加算合成なので、実機の明るい床の上では相対的に薄く見える</b>ことに注意する。
    ///
    /// 使い方: <c>.\tools\unity.ps1 menu walkguide</c>（<c>-Set show=&lt;show.json&gt;</c> で差し替え）
    /// → <c>tools/make-preview-video.py</c> で mp4 へ。
    /// </summary>
    public static class WalkGuidePreview
    {
        private const string OutDirRel = "Screenshots/walkguide-preview";
        private const int W = 960, H = 540;
        private const int Fps = 30;

        /// <summary>
        /// 撮影台は<b>原点</b>。<see cref="WalkGuide"/> は course→world を<b>ワールド座標で</b>置くので、
        /// 台をずらすと誘導だけが取り残される（床と壁だけが浮いた絵になる）。
        /// batchmode の空シーンで撮るので、他の幾何は写り込まない。
        /// </summary>
        private static readonly Vector3 Stage = Vector3.zero;

        private const float EyeHeightM = 1.6f;
        private const float FovDeg = 70f;

        /// <summary>矢印の起点からどれだけ後ろに立って撮るか (m)。</summary>
        private const float StandBackM = 0.6f;

        /// <summary>出し切ってから回す尺 (秒)。流れが見える。</summary>
        private const float HoldTailSec = 3.0f;

        private static readonly string[] ShowCandidates =
        {
            "tools/web-compositor/show.json",
            "Assets/StreamingAssets/show/show.json",
        };

        [System.Serializable]
        private sealed class ShowRoot { public ShowLayoutDef? layout; }

        /// <summary>後片づけ用（Editor で回すたびに材質が漏れないように）。</summary>
        private static readonly System.Collections.Generic.List<Material> _previewMats = new();

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Walk Guide", priority = 234)]
        public static void Run()
        {
            string dir = Path.Combine(Application.dataPath, OutDirRel);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);

            ShowLayoutDef? layout = LoadLayout(out string showPath);
            WalkGuidePath.Path path = WalkGuidePath.Solve(layout, layout?.room);
            if (!path.valid)
            {
                Debug.LogError("[WalkGuide] 道筋が解けないのでプレビューを焼けません — "
                             + $"床も壁の角も開始位置も無い（{showPath}）。"
                             + "卓の 🧱 部屋で壁を、または 🎬 開始位置で円を置くこと");
                return;
            }
            Debug.Log($"[WalkGuide] show.json: {showPath.Replace('\\', '/')}\n"
                    + $"  円 ({path.spot.x:F2},{path.spot.y:F2}) 半径 {path.radiusM:F2}m"
                    + $" / {(path.spotAuthored ? "卓で著作" : "壁の角から導出")}\n"
                    + (path.hasArrow
                        ? $"  矢印 ({path.from.x:F2},{path.from.y:F2}) → 円（{Vector2.Distance(path.from, path.spot):F2}m）"
                        : "  ⚠ 道筋が短すぎて矢印は出ない（円だけ）"));

            var camGo = new GameObject("[WalkGuidePreview] Camera");
            var guideGo = new GameObject("[WalkGuidePreview] Guide");
            var props = new GameObject("[WalkGuidePreview] Room");
            int frames = 0;

            try
            {
                BuildRoom(props.transform, layout, path.floorY);
                guideGo.transform.position = Stage;
                props.transform.position = Stage;
                var guide = guideGo.AddComponent<WalkGuide>();

                var cam = camGo.AddComponent<Camera>();
                cam.fieldOfView = FovDeg;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 100f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.03f, 0.03f, 0.035f, 1f);

                var logic = new WalkGuideLogic();
                float dt = 1f / Fps;
                float clock = 0f;
                var ledger = new System.Text.StringBuilder();
                ledger.AppendLine("frame\tsec\tstage\tspot\tring\tarrow\treveal\tflow\tarrive");

                // 立って見る絵（連番）。円へ着く手前までを通しで焼く。
                SetEye(cam, path);
                float total = WalkGuideLogic.SpotInSec + WalkGuideLogic.TrailSec + HoldTailSec;
                for (float t = 0f; t < total; t += dt)
                {
                    logic.Tick(Input(path, dt, wanted: true, dist: 9f));
                    clock += dt;
                    WalkGuideWeights w = logic.Weights;
                    guide.PreviewFrame(path, w, clock);
                    Shoot(cam, Path.Combine(dir, $"f{frames:0000}.png"));
                    ledger.Append(frames).Append('\t').Append(F(t + dt)).Append('\t')
                          .Append(logic.Stage).Append('\t')
                          .Append(F(w.spot)).Append('\t').Append(F(w.ring)).Append('\t')
                          .Append(F(w.arrow)).Append('\t').Append(F(w.reveal)).Append('\t')
                          .Append(F(w.flow)).Append('\t').Append(F(w.arrive)).Append('\n');
                    frames++;
                }

                // 段ごとの静止画（数値が緑でも絵は必ず開く — rules/visual-verification.md §7）。
                float spotIn = WalkGuideLogic.SpotInSec;
                float trailEnd = spotIn + WalkGuideLogic.TrailSec;
                Shot(cam, guide, path, dir, "eye_1_spot_half", spotIn * 0.5f, false, false);
                Shot(cam, guide, path, dir, "eye_2_trail_half", spotIn + WalkGuideLogic.TrailSec * 0.5f, false, false);
                Shot(cam, guide, path, dir, "eye_3_hold", trailEnd + 1.4f, false, false);
                Shot(cam, guide, path, dir, "eye_4_arrive", trailEnd + 1.4f, true, false);
                // 真上から（道筋と壁の関係を 1 枚で見る）。
                Shot(cam, guide, path, dir, "top_hold", trailEnd + 1.4f, false, true);
                Shot(cam, guide, path, dir, "top_arrive", trailEnd + 1.4f, true, true);

                File.WriteAllText(Path.Combine(dir, "frames.tsv"), ledger.ToString());
                Debug.Log($"[WalkGuide] {frames} コマ + 静止画 6 枚 + frames.tsv → Assets/{OutDirRel}/\n"
                        + $"  山形 {guide.ChevronCount} 個 / 組めた {(guide.IsBuilt ? "はい" : "いいえ")}\n"
                        + "  ⚠ 両眼視差・レンズ・パススルーは入っていない。床と壁は置き換えの暗い板。"
                        + "加算合成なので実機の明るい床では相対的に薄く見える");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(camGo);
                UnityEngine.Object.DestroyImmediate(guideGo);
                UnityEngine.Object.DestroyImmediate(props);
                foreach (Material m in _previewMats) UnityEngine.Object.DestroyImmediate(m);
                _previewMats.Clear();
            }
        }

        /// <summary>その時刻まで実ロジックを回して 1 枚撮る。</summary>
        private static void Shot(Camera cam, WalkGuide guide, in WalkGuidePath.Path path,
                                 string dir, string name, float sec, bool arrive, bool top)
        {
            var logic = new WalkGuideLogic();
            float dt = 1f / Fps;
            float clock = 0f;
            for (float t = 0f; t < sec; t += dt)
            {
                logic.Tick(Input(path, dt, wanted: true, dist: 9f));
                clock += dt;
            }
            if (arrive)
            {
                // 円の中へ入って留まる → 着いた段の途中まで進める。
                for (float t = 0f; t < WalkGuideLogic.ArriveHoldSec + WalkGuideLogic.ArriveSec * 0.6f; t += dt)
                {
                    logic.Tick(Input(path, dt, wanted: true, dist: 0f));
                    clock += dt;
                }
            }
            guide.PreviewFrame(path, logic.Weights, clock);
            if (top) SetTop(cam, path);
            else SetEye(cam, path);
            Shoot(cam, Path.Combine(dir, name + ".png"));
        }

        private static WalkGuideInput Input(in WalkGuidePath.Path path, float dt, bool wanted, float dist)
            => new WalkGuideInput
            {
                wanted = wanted, posValid = true, distM = dist, radiusM = path.radiusM, dt = dt,
            };

        /// <summary>矢印の起点の少し後ろに立って、円の方を向く。</summary>
        private static void SetEye(Camera cam, in WalkGuidePath.Path path)
        {
            Vector2 from = path.hasArrow ? path.from : path.spot + new Vector2(0f, 1.2f);
            Vector2 dir = (path.spot - from);
            if (dir.sqrMagnitude < 1e-6f) dir = new Vector2(0f, -1f);
            dir.Normalize();
            Vector2 eye2 = from - dir * StandBackM;
            Vector3 eye = Stage + new Vector3(eye2.x, path.floorY + EyeHeightM, eye2.y);
            // 円の少し上を見る（真下を見ると床しか写らない）。
            Vector3 look = Stage + new Vector3(path.spot.x, path.floorY + 0.35f, path.spot.y);
            cam.orthographic = false;
            cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(look - eye, Vector3.up));
        }

        /// <summary>真上から。壁と道筋の関係が 1 枚で読める。</summary>
        private static void SetTop(Camera cam, in WalkGuidePath.Path path)
        {
            cam.orthographic = true;
            cam.orthographicSize = 1.1f;
            cam.transform.SetPositionAndRotation(Stage + new Vector3(0f, path.floorY + 4f, 0f),
                                                 Quaternion.Euler(90f, 0f, 0f));
        }

        /// <summary>床と壁の置き換え（暗い板）。<b>実物の色ではない</b> — 位置関係を見るためだけ。</summary>
        private static void BuildRoom(Transform parent, ShowLayoutDef? layout, float floorY)
        {
            Shader? plain = Shader.Find("Universal Render Pipeline/Unlit");
            if (plain == null) return;
            var floorMat = new Material(plain) { name = "Floor (preview)" };
            _previewMats.Add(floorMat);
            floorMat.SetColor("_BaseColor", new Color(0.10f, 0.10f, 0.11f, 1f));
            floorMat.SetColor("_Color", new Color(0.10f, 0.10f, 0.11f, 1f));
            var wallMat = new Material(plain) { name = "Wall (preview)" };
            _previewMats.Add(wallMat);
            wallMat.SetColor("_BaseColor", new Color(0.19f, 0.18f, 0.17f, 1f));
            wallMat.SetColor("_Color", new Color(0.19f, 0.18f, 0.17f, 1f));

            if (IntroStructureWireLogic.TryFloorExtents(layout, layout?.room, out float fw, out float fd))
            {
                var floor = GameObject.CreatePrimitive(PrimitiveType.Quad);
                floor.name = "Floor";
                floor.transform.SetParent(parent, worldPositionStays: false);
                floor.transform.localPosition = new Vector3(0f, floorY, 0f);
                floor.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                floor.transform.localScale = new Vector3(fw, fd, 1f);
                floor.GetComponent<Renderer>().sharedMaterial = floorMat;
            }

            ShowRoomWallDef[]? walls = layout?.room?.walls;
            if (walls == null) return;
            foreach (ShowRoomWallDef? wd in walls)
            {
                if (wd == null || !wd.IsUsable()) continue;
                var a = new Vector2(wd.x1, wd.z1);
                var b = new Vector2(wd.x2, wd.z2);
                Vector2 mid = (a + b) * 0.5f;
                float len = Vector2.Distance(a, b);
                var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                box.name = "Wall " + wd.id;
                box.transform.SetParent(parent, worldPositionStays: false);
                box.transform.localPosition = new Vector3(mid.x, floorY + wd.h * 0.5f, mid.y);
                box.transform.localRotation =
                    Quaternion.Euler(0f, Mathf.Atan2(b.x - a.x, b.y - a.y) * Mathf.Rad2Deg, 0f);
                box.transform.localScale = new Vector3(Mathf.Max(wd.thick, 0.02f), wd.h, len);
                box.GetComponent<Renderer>().sharedMaterial = wallMat;
            }
        }

        private static ShowLayoutDef? LoadLayout(out string usedPath)
        {
            usedPath = "(なし)";
            string? explicitPath = EditorCliArgs.Get("show");
            string root = Directory.GetParent(Application.dataPath)!.FullName;
            var candidates = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(explicitPath)) candidates.Add(explicitPath!);
            foreach (string rel in ShowCandidates) candidates.Add(Path.Combine(root, rel));
            foreach (string p in candidates)
            {
                if (!File.Exists(p)) continue;
                try
                {
                    var parsed = JsonUtility.FromJson<ShowRoot>(File.ReadAllText(p));
                    if (parsed?.layout == null) continue;
                    usedPath = p;
                    return parsed.layout;
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[WalkGuide] {p} を読めない: {e.Message}");
                }
            }
            return null;
        }

        private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);

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
