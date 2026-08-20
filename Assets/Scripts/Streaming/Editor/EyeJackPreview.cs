#nullable enable
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// <b>目の視界ジャックを 1 コマずつ焼く</b>（<c>canon/LEDGER.md</c> 0099）。
    ///
    /// 2 通りの体験者を通しで焼く:
    /// <list type="bullet">
    ///   <item><b>stationary</b> — 足を止めた人。全開 + 1 拍で乗っ取り → 写真が尽きて返す → 目が閉じる</item>
    ///   <item><b>walker</b> — 歩き続ける人。区間の半分（約 3.5 秒）の縁で乗っ取り →
    ///     目は裏で追い上げて閉じる → 写真が尽きて返すと目はもう居ない</item>
    /// </list>
    ///
    /// ⚠ <b>実シェーダ・実ロジック</b>（<see cref="EyeJackLogic"/> ＋ <see cref="EyesCueLogic"/> ＋
    /// <see cref="AnomalyEyesLogic"/>）を通す。配線は <c>AnomalyEyes.DriveJack</c> の写し —
    /// 本番の配る値を変えたらここも直す（EyesPreview の <c>Write</c> と同じ罠）。
    ///
    /// 写真: <c>-Set photos=&lt;dir&gt;</c> か <c>tools/web-compositor/eyejack/norm/</c> があれば実物、
    /// 無ければ番号付きの合成写真 5 枚（決定的）。
    ///
    /// 使い方: <c>.\tools\unity.ps1 menu eyejack</c> → <c>tools/make-preview-video.py</c> で mp4 へ。
    /// </summary>
    public static class EyeJackPreview
    {
        private const string OutDirRel = "Screenshots/eyejack-preview";
        private const int W = 960, H = 540;
        private const int Fps = 30;
        private const float FovDeg = 70f;

        private static readonly Vector3 Stage = new(0f, 2000f, 0f);

        // 置き換えのスクリーン（EyesPreview と同じ実測値）。
        private const float ScreenDistM = 2.0f;
        private const float ScreenHalfYawDeg = 30.6f;
        private const float ScreenHalfPitchDeg = 18.4f;
        private const float ScreenDropDeg = 8f;

        // walker の歩み。半分（EyesCueLogic.FinishAt=0.5）へ実測どおり約 3.5 秒で達する速さ
        //（3:2 の平均滞在 7.04 秒 — dwell_stats.json n=98）。
        private const float WalkerProgressPerSec = 0.5f / 3.5f;

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Eye Jack", priority = 234)]
        public static void Run()
        {
            string dir = Path.Combine(Application.dataPath, OutDirRel);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);

            Shader? eyeSh = Shader.Find(AnomalyEyes.ShaderName);
            Shader? jackSh = Shader.Find(AnomalyEyes.JackShaderName);
            if (eyeSh == null || jackSh == null)
            {
                Debug.LogError($"[EyeJackPreview] シェーダが見つかりません" +
                               $"（eyes={(eyeSh != null)} jack={(jackSh != null)}）");
                return;
            }

            Texture2D[] photos = LoadPhotos();
            var camGo = new GameObject("[EyeJackPreview] Camera");
            var eyesGo = new GameObject("[EyeJackPreview] Eyes");
            var jackGo = new GameObject("[EyeJackPreview] Jack");
            var screenGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            screenGo.name = "[EyeJackPreview] Screen (placeholder)";
            var eyeMat = new Material(eyeSh) { name = "AnomalyEyes (preview)" };
            var jackMat = new Material(jackSh) { name = "EyeJack (preview)" };
            Material? screenMat = null;
            Mesh? eyeMesh = null;
            Mesh? jackMesh = null;

            try
            {
                EyeSeat[] seats = AnomalyEyesMesh.BuildSeats();
                eyeMesh = AnomalyEyesMesh.Build(seats);
                eyesGo.transform.SetPositionAndRotation(Stage, Quaternion.identity);
                eyesGo.AddComponent<MeshFilter>().sharedMesh = eyeMesh;
                var mr = eyesGo.AddComponent<MeshRenderer>();
                mr.sharedMaterial = eyeMat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

                // ジャックの面。実機と同じ画角の quad（AnomalyEyes.BuildJack と同じ式）。
                float hw = 1.8f * Mathf.Tan(55f * Mathf.Deg2Rad) * 1.35f;
                float hh = 1.8f * Mathf.Tan(50f * Mathf.Deg2Rad) * 1.35f;
                jackMesh = new Mesh { name = "EyeJackQuad (preview)" };
                jackMesh.vertices = new[]
                {
                    new Vector3(-hw, -hh, 0f), new Vector3(hw, -hh, 0f),
                    new Vector3(-hw, hh, 0f), new Vector3(hw, hh, 0f),
                };
                jackMesh.uv = new[]
                {
                    new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f),
                };
                jackMesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
                jackMesh.RecalculateBounds();
                jackGo.transform.SetPositionAndRotation(Stage + new Vector3(0f, 0f, 1.8f), Quaternion.identity);
                jackGo.AddComponent<MeshFilter>().sharedMesh = jackMesh;
                var jr = jackGo.AddComponent<MeshRenderer>();
                jr.sharedMaterial = jackMat;
                jr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                jr.enabled = false;

                Shader? plain = Shader.Find("Universal Render Pipeline/Unlit");
                if (plain != null)
                {
                    screenMat = new Material(plain) { name = "Screen (preview)" };
                    screenMat.SetColor("_BaseColor", new Color(0.10f, 0.09f, 0.08f, 1f));
                    screenMat.SetColor("_Color", new Color(0.10f, 0.09f, 0.08f, 1f));
                    screenGo.GetComponent<Renderer>().sharedMaterial = screenMat;
                }
                float shw = ScreenDistM * Mathf.Tan(ScreenHalfYawDeg * Mathf.Deg2Rad);
                float shh = ScreenDistM * Mathf.Tan(ScreenHalfPitchDeg * Mathf.Deg2Rad);
                float drop = ScreenDistM * Mathf.Tan(ScreenDropDeg * Mathf.Deg2Rad);
                screenGo.transform.position = Stage + new Vector3(0f, -drop, ScreenDistM);
                screenGo.transform.localScale = new Vector3(shw * 2f, shh * 2f, 1f);

                var cam = camGo.AddComponent<Camera>();
                cam.fieldOfView = FovDeg;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 100f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.transform.SetPositionAndRotation(Stage, Quaternion.identity);

                Bake(cam, eyeMat, jackMat, jr, seats, photos, dir, "stationary", walking: false);
                Bake(cam, eyeMat, jackMat, jr, seats, photos, dir, "walker", walking: true);

                Debug.Log($"[EyeJackPreview] stationary + walker を焼いた → Assets/{OutDirRel}/\n" +
                          $"  写真 {photos.Length} 枚 / 総尺 {EyeJackLogic.TotalSec}s" +
                          $"（1 枚 {EyeJackLogic.PerMinSec}〜{EyeJackLogic.PerMaxSec}s）/" +
                          $" 全開 + {EyeJackLogic.HoldBeatSec}s or 半分の縁で発火\n" +
                          "  ⚠ 両眼視差・レンズ・パススルーは入っていない。スクリーンは置き換えの暗い板");
            }
            finally
            {
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(eyesGo);
                Object.DestroyImmediate(jackGo);
                Object.DestroyImmediate(screenGo);
                Object.DestroyImmediate(eyeMat);
                Object.DestroyImmediate(jackMat);
                if (screenMat != null) Object.DestroyImmediate(screenMat);
                if (eyeMesh != null) Object.DestroyImmediate(eyeMesh);
                if (jackMesh != null) Object.DestroyImmediate(jackMesh);
                foreach (var p in photos) if (p != null) Object.DestroyImmediate(p);
            }
        }

        /// <summary>1 通りの体験者を通しで焼く（配線は AnomalyEyes.DriveJack の写し）。</summary>
        private static void Bake(Camera cam, Material eyeMat, Material jackMat, MeshRenderer jackRenderer,
                                 EyeSeat[] seats, Texture2D[] photos, string dir, string name, bool walking)
        {
            var eyes = new AnomalyEyesLogic();
            var cue = new EyesCueLogic();
            var jack = new EyeJackLogic();
            float dt = 1f / Fps;
            float clock = 0f;
            float progress = 0f;
            int frames = 0;
            int lastShot = -1;
            var ledger = new System.Text.StringBuilder();
            ledger.AppendLine("frame\tsec\tstage\tprogress\tjackActive\tphotoIndex\topen");

            // 発火 → 写真 → 目の閉じ切りまで + 1 秒。stationary は全開 4.92 + 拍 + 2.4 + 閉じ 1.6。
            float total = 11f;
            for (float t = 0f; t < total; t += dt)
            {
                if (walking) progress = Mathf.Min(1f, progress + WalkerProgressPerSec * dt);
                var span = new ZoneSpan(true, 2, 7, progress);
                cue.Tick(armed: true, eyes.Stage, span);
                // ⚠ 本番（AnomalyEyes.LateUpdate）と同じ式。ジャックが覆っているあいだは閉じさせない
                //   （覆いの裏で閉じるのは「閉じた」ではなく「消えた」）。
                eyes.Tick(dt, cue.Wanted || jack.Active, 1f, cue.Rate);
                jack.Tick(dt, armed: true, eyes.Stage, cue.HalfReached, photos.Length);
                if (jack.FinishEyesRequested) cue.RequestFinish();

                WriteEyes(eyeMat, eyes, clock);
                if (jack.Active && photos.Length > 0)
                {
                    int idx = Mathf.Min(jack.PhotoIndex, photos.Length - 1);
                    if (idx != lastShot)
                    {
                        lastShot = idx;
                        jackMat.SetTexture("_JackTex", photos[idx]);
                        jackMat.SetVector("_JackUv", AnomalyEyes.CoverUv(photos[idx]));
                    }
                    jackMat.SetFloat("_JackOn", 1f);
                    jackRenderer.enabled = true;
                }
                else
                {
                    lastShot = -1;
                    jackRenderer.enabled = false;
                }

                clock += dt;
                Shoot(cam, Path.Combine(dir, $"{name}_f{frames:0000}.png"));
                int open = AnomalyEyesMesh.CountOpen(seats, eyes.Big, eyes.Field, eyes.Density);
                ledger.Append(frames).Append('\t').Append(F(t)).Append('\t')
                      .Append(eyes.Stage).Append('\t').Append(F(progress)).Append('\t')
                      .Append(jack.Active ? 1 : 0).Append('\t')
                      .Append(jack.Active ? jack.PhotoIndex : -1).Append('\t')
                      .Append(open).Append('\n');
                frames++;

                // 全部終わったら 1 秒だけ余韻を焼いて切り上げる（stationary の尻の無駄を削る）。
                if (eyes.Stage == EyesStage.Off && jack.Spent && t < total - 1.5f) total = t + 1f;
            }
            File.WriteAllText(Path.Combine(dir, $"{name}_frames.tsv"), ledger.ToString());
        }

        private static void WriteEyes(Material mat, AnomalyEyesLogic l, float clock)
        {
            mat.SetFloat("_EyeBig", l.Big);
            mat.SetFloat("_EyeField", l.Field);
            mat.SetFloat("_EyeSpan", l.Span);
            mat.SetFloat("_EyeDensity", l.Density);
            mat.SetFloat("_EyeFade", l.Fade);
            mat.SetFloat("_EyeIntensity", l.Intensity);
            mat.SetFloat("_EyeTime", clock);
            mat.SetFloat("_EyeGaze", l.Gaze);
            mat.SetFloat("_EyeSmile", l.Smile);
            mat.SetFloat("_EyeClosing", l.Closing);
        }

        /// <summary>実物（norm/ の正規化済み）か、無ければ番号付きの合成写真（決定的）。</summary>
        private static Texture2D[] LoadPhotos()
        {
            string dir = EditorCliArgs.Get("photos") ?? "";
            if (string.IsNullOrEmpty(dir))
                dir = Path.GetFullPath(Path.Combine(Application.dataPath, "../tools/web-compositor/eyejack/norm"));
            if (Directory.Exists(dir))
            {
                var files = Directory.GetFiles(dir);
                System.Array.Sort(files, System.StringComparer.OrdinalIgnoreCase);
                var list = new System.Collections.Generic.List<Texture2D>();
                foreach (string f in files)
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".jpg" && ext != ".jpeg" && ext != ".png") continue;
                    var tex = new Texture2D(2, 2, TextureFormat.RGB24, mipChain: false);
                    if (tex.LoadImage(File.ReadAllBytes(f))) list.Add(tex);
                    else Object.DestroyImmediate(tex);
                    if (list.Count >= EyeJackPhotoStore.MaxPhotos) break;
                }
                if (list.Count > 0)
                {
                    Debug.Log($"[EyeJackPreview] 実物の写真 {list.Count} 枚（{dir}）");
                    return list.ToArray();
                }
            }
            // 合成 5 枚 — 色相の違う勾配 + 左上に枚数ぶんの白い角（何枚目かが画で読める）。
            var photos = new Texture2D[5];
            for (int i = 0; i < photos.Length; i++)
            {
                var tex = new Texture2D(640, 480, TextureFormat.RGB24, mipChain: false)
                { name = $"jack-test-{i + 1}" };
                var px = new Color32[640 * 480];
                Color a = Color.HSVToRGB(i / 5f, 0.55f, 0.55f);
                Color b = Color.HSVToRGB((i / 5f + 0.12f) % 1f, 0.35f, 0.18f);
                for (int y = 0; y < 480; y++)
                for (int x = 0; x < 640; x++)
                    px[y * 640 + x] = Color.Lerp(a, b, (x / 640f + y / 480f) * 0.5f);
                // 左上の白い角（i+1 個）。
                for (int m = 0; m <= i; m++)
                for (int y = 448; y < 472; y++)
                for (int x = 16 + m * 36; x < 40 + m * 36; x++)
                    px[y * 640 + x] = Color.white;
                tex.SetPixels32(px);
                tex.Apply(false, false);
                photos[i] = tex;
            }
            Debug.Log("[EyeJackPreview] 実物が無いので合成写真 5 枚（左上の白い角が枚数）");
            return photos;
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
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(tex);
            }
        }
    }
}
