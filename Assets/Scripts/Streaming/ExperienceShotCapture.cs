#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FixedCamVr.Streaming.Cg;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 体験中の画を説明資料へ載せるための撮影。左グリップ（<c>OvrControllerBridge</c> が押下を渡す）で
    /// <b>同じ瞬間</b>の 4 種類を 1 フォルダへ PNG で保存する。
    ///
    /// <list type="number">
    /// <item><c>1_screen.png</c> 表示されているスクリーン映像（ライブ・素材・CG・加工を含む最終合成）</item>
    /// <item><c>2_raw.png</c> 加工前の生映像（画面へ出ているカメラの受信フレーム）</item>
    /// <item><c>3_layers.png</c> 合成しているものだけ（ライブを黒へ置き換え、同じ加工で描く）。
    ///       合成が無い瞬間は撮らない。<c>3_cg_alpha.png</c> は CG 層そのもの（透過）</item>
    /// <item><c>4_hmd.png</c> 体験者が見ている左眼の視界（アプリが描いたもの）</item>
    /// </list>
    ///
    /// <b>体験を止めない作り。</b>GPU 側の写し（Blit・カメラ描画）は 1 フレームで終え、
    /// CPU への読み出しは<b>1 枚ずつ別のフレームへ</b>散らし、PNG の圧縮は背景スレッドへ逃がす。
    ///
    /// ⚠ <b>④ にはパススルー（現実）が入らない。</b>パススルーは OS が合成するので、アプリの描画バッファには
    ///   存在しない。本編は背景が黒なので実害は小さいが、導入（現実が見えている段）では現実の所が黒になる。
    /// ⚠ <b>体験者は左グリップを握り込む。</b>展示本番の機では必ず OFF にする（<see cref="ExperienceShotLogic.IsEnabled"/>）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ExperienceShotCapture : MonoBehaviour
    {
        // 出力寸法。① と ③ は EndingFrameCapture と同じ 1280x720（スクリーン枠は 16:9）。
        private const int ScreenWidth = EndingFrameCapture.Width;
        private const int ScreenHeight = EndingFrameCapture.Height;

        // ④ の幅。高さは左眼の投影行列の縦横比から決める（Quest 3 の片眼は 1.0 前後）。
        private const int HmdWidth = 1600;
        private const int HmdMinHeight = 512;
        private const int HmdMaxHeight = 2048;

        private static readonly int LiveTexId = Shader.PropertyToID("_LiveTex");
        private static readonly int OverlayStrengthId = Shader.PropertyToID("_OverlayStrength");
        private static readonly int Overlay2StrengthId = Shader.PropertyToID("_Overlay2Strength");
        private static readonly int CgStrengthId = Shader.PropertyToID("_CgStrength");

        /// <summary>④ の描画に使う視点。Bridge（OVR を知っている側）が入れる。</summary>
        public readonly struct HmdView
        {
            public HmdView(Camera camera, Transform eye)
            {
                Camera = camera;
                Eye = eye;
            }

            public Camera Camera { get; }

            /// <summary>左眼の位置と向き（<c>OVRCameraRig.leftEyeAnchor</c>）。</summary>
            public Transform Eye { get; }
        }

        /// <summary>④ の視点の供給元。null を返す・未設定のときは <see cref="Camera.main"/> の中心から撮る。</summary>
        public Func<HmdView?>? HmdViewProvider { get; set; }

        /// <summary>この起動で受理した撮影の回数。</summary>
        public int AcceptedCount => _accepted;

        /// <summary>直近に書いたフォルダ（無ければ空）。</summary>
        public string LastSetFolder { get; private set; } = "";

        private static bool? _enabled;
        private int _accepted;
        private float _lastAcceptedAt = float.NegativeInfinity;
        private bool _busy;

        // ---- 有効化 ----------------------------------------------------------------------

        /// <summary>撮影の保存先（端末の外から <c>adb pull</c> できる場所）。</summary>
        public static string OutputRoot => Path.Combine(Application.persistentDataPath, "shots");

        /// <summary>この機で撮影が有効か。起動中は 1 度だけ決める。</summary>
        public static bool Enabled
        {
            get
            {
                _enabled ??= ExperienceShotLogic.IsEnabled(
                    Debug.isDebugBuild,
                    MarkerExists("ON"),
                    MarkerExists("OFF"));
                return _enabled.Value;
            }
        }

        private static bool MarkerExists(string name)
        {
            try { return File.Exists(Path.Combine(OutputRoot, name)); }
            catch { return false; }
        }

        /// <summary>撮影の実行体を作る（無ければ）。シーンへは焼かず、実行時に生やす。</summary>
        public static ExperienceShotCapture Ensure()
        {
            var existing = FindObjectOfType<ExperienceShotCapture>();
            if (existing != null) return existing;
            var go = new GameObject("[ExperienceShots]");
            DontDestroyOnLoad(go);
            var created = go.AddComponent<ExperienceShotCapture>();
            Debug.Log($"[Shot] 撮影機能 enabled={Enabled} root={OutputRoot}");
            return created;
        }

        // ---- 要求 ------------------------------------------------------------------------

        /// <summary>
        /// 撮影を要求する。受理したら true（呼び出し側が手応えの振動を返す）。
        /// 受理しなかった理由はログに 1 行残す（画にも音にも出ないので、ここが唯一の手掛かり）。
        /// </summary>
        public bool Request()
        {
            ExperienceShotLogic.Verdict verdict = _busy
                ? ExperienceShotLogic.Verdict.Cooldown
                : ExperienceShotLogic.Decide(Enabled, Time.unscaledTime, _lastAcceptedAt, _accepted);
            if (verdict != ExperienceShotLogic.Verdict.Accepted)
            {
                if (verdict != ExperienceShotLogic.Verdict.Cooldown)
                    Debug.Log($"[Shot] 撮影を受けなかった: {verdict}");
                return false;
            }

            _accepted++;
            _lastAcceptedAt = Time.unscaledTime;
            _busy = true;
            StartCoroutine(CaptureSet(_accepted));
            return true;
        }

        // ---- 撮影 ------------------------------------------------------------------------

        /// <summary>1 枚ぶんの仕事。GPU 側に写し終えた RT を、あとで 1 枚ずつ CPU へ読む。</summary>
        private sealed class Job
        {
            public string file = "";
            public RenderTexture? source;
            /// <summary>
            /// 描いたカメラ。**読み出しが終わるまで壊さない**（描いた直後に壊すと、
            /// 描いたはずの絵が RT から消える。EditMode で実測: 壊す前に読めば赤 / 壊した後は黒）。
            /// </summary>
            public GameObject? owner;
            public bool premultiplied;   // CG 層（straight alpha へ戻して透過 PNG にする）
            public bool opaque = true;   // alpha を 255 で書く
            public int width;
            public int height;
        }

        private IEnumerator CaptureSet(int sequence)
        {
            // 画が組み上がってから写す（Blit は EndOfFrame でないと、そのフレームの material の値を拾えない）。
            yield return new WaitForEndOfFrame();

            var jobs = new List<Job>(5);
            var skipped = new List<string>(4);
            var info = new ExperienceShotInfo();
            string setName = ExperienceShotLogic.SetFolderName(DateTime.Now, sequence);
            string setDir = Path.Combine(OutputRoot, setName);

            try
            {
                info.setName = setName;
                info.capturedAtIso = DateTime.Now.ToString("o");
                info.appTimeSec = Time.realtimeSinceStartup;
                var screen = FindObjectOfType<MjpegScreen>();
                Material? material = ResolveMaterial(screen);
                FillState(info, screen, material);

                CaptureScreen(material, jobs, skipped, info);
                CaptureRaw(screen, jobs, skipped, info);
                CaptureLayers(material, jobs, skipped, info);
                CaptureCgAlpha(material, jobs, skipped);
                CaptureHmd(jobs, skipped, info);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Shot] 撮影の写しに失敗: {e}");
            }

            if (jobs.Count == 0)
            {
                Debug.LogWarning("[Shot] 撮れたものが 1 枚も無い — 保存しない");
                _busy = false;
                yield break;
            }

            try { Directory.CreateDirectory(setDir); }
            catch (Exception e)
            {
                Debug.LogError($"[Shot] 保存先を作れない {setDir}: {e.Message}");
                foreach (Job j in jobs) ReleaseJobTexture(j);
                _busy = false;
                yield break;
            }

            // CPU へ読むのは 1 枚ずつ別のフレーム。まとめて読むと GPU 待ちが 1 フレームに積み上がって
            // 体験者の視界が止まる。
            var tasks = new List<KeyValuePair<Job, Task<bool>>>(jobs.Count);
            foreach (Job job in jobs)
            {
                yield return null;
                byte[]? rgba = ReadBack(job);
                ReleaseJobTexture(job);
                if (rgba == null)
                {
                    skipped.Add(job.file + ": 読み出しに失敗");
                    continue;
                }
                string path = Path.Combine(setDir, job.file);
                Job captured = job;
                tasks.Add(new KeyValuePair<Job, Task<bool>>(job, Task.Run(() => Write(path, rgba, captured))));
            }

            // 背景スレッドの書き出しを待つ（フレームは止めない）。
            while (true)
            {
                bool done = true;
                foreach (var t in tasks)
                    if (!t.Value.IsCompleted) { done = false; break; }
                if (done) break;
                yield return null;
            }

            var written = new List<string>(tasks.Count + 1);
            foreach (var t in tasks)
            {
                if (t.Value.Status == TaskStatus.RanToCompletion && t.Value.Result) written.Add(t.Key.file);
                else skipped.Add(t.Key.file + ": 書き出しに失敗");
            }

            info.files = written.ToArray();
            info.skipped = skipped.ToArray();
            try
            {
                File.WriteAllText(Path.Combine(setDir, ExperienceShotLogic.InfoFile),
                                  JsonUtility.ToJson(info, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Shot] info.json を書けない: {e.Message}");
            }

            LastSetFolder = setDir;
            Debug.Log($"[Shot] 保存 set={setName} files={written.Count} skipped={skipped.Count} " +
                      $"phase={info.phase} lap={info.progressLap} cam={info.screenCameraName} " +
                      $"take={info.activeTake} dir={setDir}");
            foreach (string s in skipped) Debug.Log($"[Shot]   撮らなかった: {s}");
            _busy = false;
        }

        private static bool Write(string path, byte[] rgba, Job job)
        {
            try
            {
                if (job.premultiplied) ShotPngWriter.UnpremultiplyInPlace(rgba);
                if (job.premultiplied && !ShotPngWriter.HasCoverage(rgba)) return false;
                byte[] png = ShotPngWriter.Encode(rgba, job.width, job.height, bottomUp: true, opaque: job.opaque);
                File.WriteAllBytes(path, png);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Shot] {job.file} の書き出しに失敗: {e.Message}");
                return false;
            }
        }

        // ---- ① スクリーン ----------------------------------------------------------------

        private static Material? ResolveMaterial(MjpegScreen? screen)
        {
            if (screen == null) return null;
            var renderer = screen.GetComponent<Renderer>();
            return renderer != null ? renderer.sharedMaterial : null;
        }

        private static void CaptureScreen(Material? material, List<Job> jobs, List<string> skipped,
                                          ExperienceShotInfo info)
        {
            if (material == null)
            {
                skipped.Add(ExperienceShotLogic.ScreenFile + ": スクリーンの材質が見つからない");
                return;
            }
            RenderTexture rt = NewTarget(ScreenWidth, ScreenHeight, "Shot Screen");
            if (!EndingFrameCapture.CaptureComposite(material, rt))
            {
                Destroy(rt);
                skipped.Add(ExperienceShotLogic.ScreenFile + ": 合成の写しに失敗");
                return;
            }
            info.screenWidth = ScreenWidth;
            info.screenHeight = ScreenHeight;
            jobs.Add(new Job { file = ExperienceShotLogic.ScreenFile, source = rt, width = ScreenWidth, height = ScreenHeight });
        }

        // ---- ② 生映像 --------------------------------------------------------------------

        private static void CaptureRaw(MjpegScreen? screen, List<Job> jobs, List<string> skipped,
                                       ExperienceShotInfo info)
        {
            if (screen == null)
            {
                skipped.Add(ExperienceShotLogic.RawFile + ": スクリーンが見つからない");
                return;
            }
            Texture? live = screen.LiveTexture;
            int w = screen.SourceWidth, h = screen.SourceHeight;
            if (live == null || w <= 0 || h <= 0)
            {
                skipped.Add(ExperienceShotLogic.RawFile + ": 映像がまだ届いていない");
                return;
            }
            RenderTexture rt = NewTarget(w, h, "Shot Raw");
            RenderTexture? previous = RenderTexture.active;
            try { Graphics.Blit(live, rt); }
            finally { RenderTexture.active = previous; }
            info.rawWidth = w;
            info.rawHeight = h;
            jobs.Add(new Job { file = ExperienceShotLogic.RawFile, source = rt, width = w, height = h });
        }

        // ---- ③ 合成しているものだけ ------------------------------------------------------

        private static void CaptureLayers(Material? material, List<Job> jobs, List<string> skipped,
                                          ExperienceShotInfo info)
        {
            if (material == null) return;
            bool composing = info.overlayStrength > 0.001f || info.overlay2Strength > 0.001f
                             || info.cgStrength > 0.001f;
            if (!composing)
            {
                skipped.Add(ExperienceShotLogic.LayersFile + ": 合成しているものが無い瞬間");
                return;
            }

            RenderTexture rt = NewTarget(ScreenWidth, ScreenHeight, "Shot Layers");
            if (!CaptureCompositeWithoutLive(material, rt))
            {
                Destroy(rt);
                skipped.Add(ExperienceShotLogic.LayersFile + ": 合成の写しに失敗");
                return;
            }
            jobs.Add(new Job { file = ExperienceShotLogic.LayersFile, source = rt, width = ScreenWidth, height = ScreenHeight });
        }

        /// <summary>
        /// スクリーンの合成を、<b>ライブだけ黒へ置き換えて</b>描く。素材・マスク・CG・加工は同じ値のまま、
        /// 「合成しているものだけ」が黒の上に乗った絵になる。
        /// 画面の材質は書き換えない（複製して描く）。
        /// </summary>
        public static bool CaptureCompositeWithoutLive(Material material, RenderTexture destination)
        {
            var clone = new Material(material);
            try
            {
                clone.SetTexture(LiveTexId, Texture2D.blackTexture);
                return EndingFrameCapture.CaptureComposite(clone, destination);
            }
            finally
            {
                if (Application.isPlaying) Destroy(clone);
                else DestroyImmediate(clone);
            }
        }

        private static void CaptureCgAlpha(Material? material, List<Job> jobs, List<string> skipped)
        {
            var cg = FindObjectOfType<ShowCgLayer>();
            if (cg == null || !cg.IsVisible || cg.Layer == null) return;
            if (material != null && material.GetFloat(CgStrengthId) <= 0.001f) return;

            RenderTexture src = cg.Layer;
            // CG 層の RT は毎フレーム描き直されるので、同じ瞬間の複製を取る（MSAA の解決も兼ねる）。
            RenderTexture copy = NewTarget(src.width, src.height, "Shot CG");
            RenderTexture? previous = RenderTexture.active;
            try { Graphics.Blit(src, copy); }
            finally { RenderTexture.active = previous; }
            jobs.Add(new Job
            {
                file = ExperienceShotLogic.CgAlphaFile,
                source = copy,
                premultiplied = true,
                opaque = false,
                width = copy.width,
                height = copy.height,
            });
        }

        // ---- ④ 体験者の視界 --------------------------------------------------------------

        private void CaptureHmd(List<Job> jobs, List<string> skipped, ExperienceShotInfo info)
        {
            HmdView? provided = null;
            try { provided = HmdViewProvider?.Invoke(); }
            catch (Exception e) { Debug.LogWarning($"[Shot] 視点の供給元が例外: {e.Message}"); }

            Camera? cameraSource = provided?.Camera != null ? provided.Value.Camera : Camera.main;
            if (cameraSource == null)
            {
                skipped.Add(ExperienceShotLogic.HmdFile + ": 視点のカメラが見つからない");
                return;
            }
            Transform eye = provided != null && provided.Value.Eye != null
                ? provided.Value.Eye
                : cameraSource.transform;

            Matrix4x4 projection = ResolveEyeProjection(cameraSource);
            float aspect = projection.m11 / projection.m00;
            int h = Mathf.Clamp(Mathf.RoundToInt(HmdWidth / Mathf.Max(0.25f, aspect)), HmdMinHeight, HmdMaxHeight);

            RenderTexture? rt = RenderView(cameraSource, eye, projection, HmdWidth, h, out GameObject? owner);
            if (rt == null)
            {
                skipped.Add(ExperienceShotLogic.HmdFile + ": 描画に失敗");
                return;
            }

            info.hmdWidth = HmdWidth;
            info.hmdHeight = h;
            info.hmdNote = "アプリの描画のみ（パススルーの現実は含まない。黒＝現実が見える場所か、元から黒）";
            jobs.Add(new Job { file = ExperienceShotLogic.HmdFile, source = rt, owner = owner, width = HmdWidth, height = h });
        }

        /// <summary>
        /// <paramref name="source"/> と同じ設定の単眼カメラを、<paramref name="eye"/> の位置・向きと
        /// <paramref name="projection"/> で 1 度だけ描く。失敗したら null。
        /// 体験者の視界と同じ幾何・同じ描画範囲を、XR を通さずに取り出す。
        ///
        /// ⚠ <paramref name="cameraObject"/> は**呼び出し側が、RT を読み終えてから**壊す。
        ///   描いた直後に壊すと RT の中身が消える（下のテストが固定している）。
        /// </summary>
        public static RenderTexture? RenderView(Camera source, Transform eye, Matrix4x4 projection,
                                                int width, int height, out GameObject? cameraObject)
        {
            cameraObject = null;
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
            {
                name = "Shot Hmd",
                antiAliasing = 4,
                useMipMap = false,
                wrapMode = TextureWrapMode.Clamp,
            };
            rt.Create();

            var go = new GameObject("[ShotCamera]") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var cam = go.AddComponent<Camera>();
                cam.CopyFrom(source);
                cam.stereoTargetEye = StereoTargetEyeMask.None;
                cam.enabled = false;
                cam.targetTexture = rt;
                cam.projectionMatrix = projection;
                cam.transform.SetPositionAndRotation(eye.position, eye.rotation);
                cam.Render();
                cameraObject = go;
                return rt;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Shot] 視界の描画に失敗: {e.Message}");
                rt.Release();
                if (Application.isPlaying) Destroy(rt); else DestroyImmediate(rt);
                if (Application.isPlaying) Destroy(go); else DestroyImmediate(go);
                return null;
            }
        }

        /// <summary>
        /// 左眼の投影行列。XR が動いていなければ（Editor など）カメラ自身の行列へ落とす。
        /// 画角が「実際に見えている範囲」になるので、体験者の視界そのままの切り取りになる。
        /// </summary>
        private static Matrix4x4 ResolveEyeProjection(Camera camera)
        {
            try
            {
                Matrix4x4 stereo = camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left);
                if (IsUsableProjection(stereo)) return stereo;
            }
            catch { /* XR が無い環境では投げることがある */ }
            return camera.projectionMatrix;
        }

        private static bool IsUsableProjection(Matrix4x4 m)
            => !float.IsNaN(m.m00) && !float.IsNaN(m.m11) && m.m00 > 0.05f && m.m11 > 0.05f;

        // ---- 状態の記録 ------------------------------------------------------------------

        private static void FillState(ExperienceShotInfo info, MjpegScreen? screen, Material? material)
        {
            var run = FindObjectOfType<ShowRunDirector>();
            if (run != null)
            {
                info.phase = run.Phase.ToString();
                info.progressLap = run.Lap;
            }
            var timeline = FindObjectOfType<TimelineDirector>();
            if (timeline != null)
            {
                info.segmentLap = timeline.CurrentLap;
                info.activeTake = timeline.ActiveTakeId;
                info.activeStepCue = timeline.ActiveStepCueId;
            }
            var registry = FindObjectOfType<CameraStreamRegistry>();
            if (registry != null)
            {
                info.screenCamera = registry.ActiveIndex;
                info.screenCameraName = registry.GetActive()?.DisplayName ?? "";
            }
            var overlay = FindObjectOfType<ScreenOverlayController>();
            if (overlay != null) info.overlayCue = overlay.AppliedCueId;
            if (material != null)
            {
                info.overlayStrength = material.GetFloat(OverlayStrengthId);
                info.overlay2Strength = material.GetFloat(Overlay2StrengthId);
                info.cgStrength = material.GetFloat(CgStrengthId);
            }
            var cg = FindObjectOfType<ShowCgLayer>();
            info.cgVisible = cg != null && cg.IsVisible;
        }

        // ---- RT と読み出し ---------------------------------------------------------------

        private static RenderTexture NewTarget(int width, int height, string name)
        {
            var rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
            };
            rt.Create();
            return rt;
        }

        private static byte[]? ReadBack(Job job)
        {
            byte[]? rgba = job.source != null ? ReadRgba(job.source) : null;
            if (rgba == null) Debug.LogWarning($"[Shot] {job.file} の読み出しに失敗");
            return rgba;
        }

        /// <summary>
        /// RT の画素を CPU へ読む（RGBA・1 画素 4 バイト・<b>行は下から上</b>）。
        /// <see cref="ShotPngWriter.Encode"/> へは <c>bottomUp: true</c> で渡す。
        /// </summary>
        public static byte[]? ReadRgba(RenderTexture rt)
        {
            RenderTexture? previous = RenderTexture.active;
            Texture2D? tex = null;
            try
            {
                RenderTexture.active = rt;
                tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
                return tex.GetRawTextureData();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Shot] RT の読み出しに失敗: {e.Message}");
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                if (tex != null)
                {
                    if (Application.isPlaying) Destroy(tex); else DestroyImmediate(tex);
                }
            }
        }

        private static void ReleaseJobTexture(Job job)
        {
            if (job.owner != null)
            {
                Destroy(job.owner);
                job.owner = null;
            }
            if (job.source == null) return;
            job.source.Release();
            Destroy(job.source);
            job.source = null;
        }
    }
}
