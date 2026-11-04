#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>本編と同じGPU描画を使うローカル調整卓。設定やシーンは保存しない。</summary>
    public static partial class CrtTuningServer
    {
        private const int MaxBody = 64 * 1024;
        [Serializable] private sealed class Config { public PostParams post = new PostParams(); public ShowFeelDef? feel = null; }
        [Serializable] private sealed class Control
        {
            public string key = "", label = "", unit = "";
            public float min, max, step, @default, @base;
        }
        [Serializable] private sealed class Group { public string id = "", label = ""; public Control[] controls = Array.Empty<Control>(); }
        [Serializable] private sealed class Source { public string id = "", label = ""; }
        [Serializable] private sealed class Metadata
        {
            public int version = 1;
            public string mode = "normal", defaultSource = "live";
            public Source[] sources = Array.Empty<Source>();
            public string[] cameras = { "A", "B", "C" };
            public string baseLabel = "本編採用版・枠なし（通常加工0.5）";
            public string unknownParameters = "ignore";
            public Group[] groups = Array.Empty<Group>();
            public ParameterValues defaults = new ParameterValues(), baseline = new ParameterValues();
        }
        [Serializable] private sealed class ErrorBody { public string error = ""; }
        [Serializable] private sealed class ParameterValues
        {
            public float centerEv = 0, centerRadius = 0, centerFalloff = 0, tintStrength = 0, tintCoolness = 0;
            public float signalGain = 0, mono = 0, bandwidth = 0;
            public float exposure = 0, contrast = 0, saturation = 0, temperature = 0, tint = 0, lift = 0, vignette = 0, grain = 0, scanline = 0;
            public float lensWarp = 0, sigmaR = 0, sigmaGB = 0, convergenceMultiplier = 0, noiseDark = 0, noiseFixed = 0;
        }
        [Serializable] private sealed class RenderRequest { public string camera = "", mode = "normal", source = "live"; public ParameterValues? parameters; }

        public static void Run()
        {
            int port = 8107;
            string? requestedPort = EditorCliArgs.Get("port");
            if (requestedPort != null && (!int.TryParse(requestedPort, out port) || port < 1024 || port > 65535))
                throw new ArgumentException("port must be 1024..65535");
            bool oldAsync = ShaderUtil.allowAsyncCompilation;
            RenderTexture? previous = RenderTexture.active;
            int exitCode = 0;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                using var renderer = new Tuner();
                using var listener = new HttpListener();
                string origin = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
                listener.Prefixes.Add(origin + "/"); listener.Start();
                Debug.Log("[CrtTuning] Ready " + origin + "/ GPU=" + SystemInfo.graphicsDeviceName);
                bool stop = false;
                // Only the listener wait uses an asynchronous IO handle. All Unity calls stay here.
                while (!stop)
                {
                    IAsyncResult pending = listener.BeginGetContext(null, null);
                    using (var ready = pending.AsyncWaitHandle)
                    {
                        if (!ready.WaitOne(TimeSpan.FromHours(6))) { listener.Stop(); break; }
                    }
                    HttpListenerContext context = listener.EndGetContext(pending);
                    try { stop = Serve(context, renderer, origin); }
                    catch (ArgumentException e) { Error(context.Response, 400, e.Message); }
                    catch (InvalidDataException e) { Error(context.Response, 400, e.Message); }
                    catch (Exception e)
                    {
                        Debug.LogError("[CrtTuning] Request failed: " + e);
                        Error(context.Response, 500, "描画に失敗しました。Unityのログを確認してください。");
                    }
                    finally
                    {
                        try { context.Response.Close(); }
                        catch (IOException) { }
                        catch (HttpListenerException) { }
                    }
                }
                listener.Stop();
            }
            catch (Exception e) { exitCode = 1; Debug.LogError("[CrtTuning] " + e); }
            finally
            {
                RenderTexture.active = previous != null && previous.IsCreated() ? previous : null;
                ShaderUtil.allowAsyncCompilation = oldAsync;
                if (Application.isBatchMode) EditorApplication.Exit(exitCode);
            }
        }

        private static bool Serve(HttpListenerContext context, Tuner tuner, string origin)
        {
            HttpListenerRequest request = context.Request; HttpListenerResponse response = context.Response;
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' blob:; style-src 'self'; script-src 'self'; frame-ancestors 'none'";
            bool entryNavigation = request.HttpMethod == "GET" && request.Url?.AbsolutePath == "/";
            if (request.RemoteEndPoint == null || !IPAddress.IsLoopback(request.RemoteEndPoint.Address)
                || request.Url == null || request.Url.GetLeftPart(UriPartial.Authority) != origin
                || (!entryNavigation && ((request.Headers["Origin"] != null && request.Headers["Origin"] != origin)
                    || request.Headers["Sec-Fetch-Site"] == "cross-site")))
            { Error(response, 403, "同じローカル画面から操作してください。"); return false; }
            string path = request.Url.AbsolutePath;
            if (request.HttpMethod == "GET")
            {
                string? staticFile = path == "/" ? "index.html" : path == "/app.js" ? "app.js" : path == "/style.css" ? "style.css" : path == "/screen-mask.png" ? "screen-mask.png" : null;
                if (staticFile != null)
                {
                    string file = Path.Combine(tuner.Root, "tools/crt-tuner", staticFile);
                    if (!File.Exists(file)) { Error(response, 404, "画面ファイルがありません。"); return false; }
                    Send(response, File.ReadAllBytes(file), staticFile.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : staticFile.EndsWith(".js", StringComparison.Ordinal) ? "text/javascript; charset=utf-8" : staticFile.EndsWith(".css", StringComparison.Ordinal) ? "text/css; charset=utf-8" : "text/html; charset=utf-8");
                }
                else if (path == "/reference.jpg") Send(response, File.ReadAllBytes(Path.Combine(tuner.Root, "Assets/Resources/CrtReference/CaseColor.jpg")), "image/jpeg");
                else if (path == "/api/config") Send(response, Encoding.UTF8.GetBytes(tuner.Configuration(request.QueryString["mode"] ?? "normal")), "application/json; charset=utf-8");
                else if (path == "/api/health") Send(response, Encoding.UTF8.GetBytes("{\"app\":\"crt-tuner\",\"ready\":true,\"renderer\":\"Unity GPU\",\"sequence\":" + tuner.Sequence + "}"), "application/json; charset=utf-8");
                else if (path == "/baseline/A.png" || path == "/baseline/B.png" || path == "/baseline/C.png") Send(response, tuner.Baseline(path.Substring(10, 1), request.QueryString["mode"] ?? "normal", request.QueryString["source"] ?? "live"), "image/png");
                else Error(response, 404, "見つかりません。");
                return false;
            }
            if (request.HttpMethod != "POST") { Error(response, 405, "GETまたはPOSTを使ってください。"); return false; }
            if (path == "/api/stop") { Send(response, Encoding.UTF8.GetBytes("{\"stopping\":true}"), "application/json; charset=utf-8"); return true; }
            if (path != "/api/render") { Error(response, 404, "見つかりません。"); return false; }
            if (request.ContentLength64 > MaxBody) { Error(response, 413, "リクエストは64KB以内にしてください。"); return false; }
            if (request.ContentType == null || !request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            { Error(response, 415, "JSONで送信してください。"); return false; }
            using var body = new MemoryStream(); var buffer = new byte[4096]; int read;
            while ((read = request.InputStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (body.Length + read > MaxBody) { Error(response, 413, "リクエストは64KB以内にしてください。"); return false; }
                body.Write(buffer, 0, read);
            }
            var parsed = new RenderRequest { parameters = tuner.Values(false) };
            string json = new UTF8Encoding(false, true).GetString(body.ToArray());
            JsonUtility.FromJsonOverwrite(json, parsed);
            Tuner.ValidateSelection(parsed.mode, parsed.source);
            // Partial requests inherit the selected mode's values, including the doll noise level.
            parsed = new RenderRequest { parameters = tuner.Values(false, parsed.mode) };
            JsonUtility.FromJsonOverwrite(json, parsed);
            if (!Tuner.ValidCamera(parsed.camera)) throw new ArgumentException("camera must be A, B or C");
            if (parsed.parameters == null) throw new ArgumentException("parameters must be an object of numbers");
            var watch = Stopwatch.StartNew(); byte[] png = tuner.Render(parsed.camera, tuner.Resolve(parsed.parameters), source: parsed.source); watch.Stop();
            response.Headers["X-Render-Ms"] = watch.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture);
            response.Headers["X-Render-Sequence"] = tuner.Sequence.ToString(CultureInfo.InvariantCulture);
            Send(response, png, "image/png"); return false;
        }

        private static void Send(HttpListenerResponse response, byte[] bytes, string contentType)
        {
            try { response.ContentType = contentType; response.ContentLength64 = bytes.Length; response.OutputStream.Write(bytes, 0, bytes.Length); }
            catch (IOException) { }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        }
        private static void Error(HttpListenerResponse response, int status, string message)
        {
            try { response.StatusCode = status; Send(response, Encoding.UTF8.GetBytes(JsonUtility.ToJson(new ErrorBody { error = message })), "application/json; charset=utf-8"); }
            catch (IOException) { }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        }

        private sealed class Tuner : IDisposable
        {
            public readonly string Root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            public string ConfigJson => Configuration("normal");
            public long Sequence { get; private set; }
            private readonly List<UnityEngine.Object> _objects = new List<UnityEngine.Object>();
            private readonly Dictionary<string, Control> _controls = new Dictionary<string, Control>(StringComparer.Ordinal);
            private readonly Dictionary<string, float> _baseline = new Dictionary<string, float>(StringComparer.Ordinal);
            private readonly Dictionary<string, byte[]> _baselineImages = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            private readonly Texture2D[] _photos = new Texture2D[3];
            private readonly Dictionary<string, Texture2D> _pov = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
            private readonly Group[] _groups;
            private readonly float[] _bias = new float[3];
            private readonly Material _material, _adjustment;
            private readonly Camera _camera;
            private readonly RenderTexture _final, _adjusted;
            private readonly Texture2D _readback;
            private readonly CrtSignalProcessor _processor;
            private ShowCompositePreview.CrtTuningDollSource? _dollSource;
            public Material Material => _material;

            public Tuner()
            {
                try
                {
                    Config config = JsonUtility.FromJson<Config>(File.ReadAllText(Path.Combine(Root, "tools/web-compositor/show.json"))) ?? throw new InvalidDataException("Missing show settings");
                    CameraFeelFx.Settings feel = CameraFeelFx.Settings.Resolve(config.feel);
                    _groups = BuildControls(config.post, feel);
                    _material = Own(new Material(CrtSignalResources.Load().Composite) { hideFlags = HideFlags.HideAndDontSave });
                    if (!CrtReferenceScreen.Apply(_material)) throw new InvalidOperationException("Missing reference maps");
                    _material.SetVector("_LiveScale", new Vector4(.75f, 1, 0, 0)); _material.SetVector("_CgScale", new Vector4(.75f, 1, 0, 0));
                    _material.SetFloat("_FrameAspect", CrtReferenceScreen.LogicalAspect);
                    _material.SetFloat("_Aberration", config.post.aberration); _material.SetFloat("_Pixelate", config.post.pixelate);
                    CameraFeelFx.WriteUniforms(_material, feel.NoiseDark, feel.NoiseFixed, 0, 0, ScreenDecayLogic.BlocksFor(.5f), 0, .5f);
                    CrtSignalProfile.ApplySelected(_material); _material.SetFloat("_CrtSignalProfileSelected", 0);
                    // Web adjustment remains a single Editor pass; the CSS mask controls its framing.
                    _material.SetFloat("_CrtCenterEv", 0); _material.SetFloat("_CrtTintStrength", 0); _material.SetFloat("_CrtScreenOnly", 0);
                    Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Art/Shaders/Streaming/Editor/CrtTuningAdjustment.shader");
                    if (shader == null || !shader.isSupported) throw new InvalidOperationException("Tuning adjustment shader is unsupported");
                    _adjustment = Own(new Material(shader) { hideFlags = HideFlags.HideAndDontSave });
                    _adjustment.SetVector("_Aperture", _material.GetVector("_CrtAperture"));
                    Compile(_adjustment);
                    var screen = Own(new GameObject("CrtTuningScreen") { hideFlags = HideFlags.HideAndDontSave, layer = 1 });
                    screen.transform.position = new Vector3(1000, 0, 0); screen.transform.localScale = new Vector3(CrtReferenceScreen.CaseAspect, 1, 1);
                    screen.AddComponent<MeshFilter>().sharedMesh = Own(CrtScreenMesh.BuildMesh(48, .055f));
                    screen.AddComponent<MeshRenderer>().sharedMaterial = _material;
                    var cameraObject = Own(new GameObject("CrtTuningCamera") { hideFlags = HideFlags.HideAndDontSave });
                    _camera = cameraObject.AddComponent<Camera>(); _camera.enabled = false;
                    _camera.transform.SetPositionAndRotation(new Vector3(1000, 0, -2), Quaternion.identity);
                    _camera.orthographic = true; _camera.orthographicSize = .5f; _camera.aspect = CrtReferenceScreen.CaseAspect;
                    _camera.nearClipPlane = .1f; _camera.farClipPlane = 4; _camera.cullingMask = 1 << 1;
                    _camera.clearFlags = CameraClearFlags.SolidColor; _camera.backgroundColor = Color.black; _camera.allowHDR = false; _camera.allowMSAA = false;
                    _final = Target(RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB, 24);
                    _adjusted = Target(RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear, 0); _camera.targetTexture = _final;
                    _readback = Own(new Texture2D(1280, 1030, TextureFormat.RGB24, false, false) { hideFlags = HideFlags.HideAndDontSave });
                    string[] names = { "plate_A_20260924_172209_b8e1f375.jpg", "plate_B_20260924_172210_4abb5bae.jpg", "plate_C_20260924_172211_a1c7c02f.jpg" };
                    for (int i = 0; i < names.Length; i++)
                    {
                        Texture2D photo = Own(new Texture2D(2, 2, TextureFormat.RGB24, true, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear });
                        if (!photo.LoadImage(File.ReadAllBytes(Path.Combine(Root, "tools/web-compositor/captures", names[i]))) || photo.width != 640 || photo.height != 480 || photo.format != TextureFormat.RGB24)
                            throw new InvalidDataException("Expected original 640x480 RGB24 plate: " + names[i]);
                        _photos[i] = photo; float mean = 0; int count = 0;
                        for (int y = 0; y < photo.height; y += photo.height / 16) for (int x = 0; x < photo.width; x += photo.width / 16)
                        { Color p = photo.GetPixel(x, y); mean += p.r * .299f + p.g * .587f + p.b * .114f; count++; }
                        var logic = new CameraFeelLogic { TargetLuma = feel.TargetLuma, FollowHalfLifeSec = feel.FollowSec };
                        _bias[i] = logic.SteadyBiasFor(mean / count) * feel.Agc;
                    }
                    _processor = new CrtSignalProcessor();
                }
                catch { Dispose(); throw; }
            }

            private Group[] BuildControls(PostParams post, CameraFeelFx.Settings feel)
            {
                return new[]
                {
                    new Group { id = "center", label = "中心の明るさ", controls = new[] {
                        Add("centerEv", "中心の明るさ", -6, 6, .01f, CrtSignalProfile.CenterEv, "EV"),
                        Add("centerRadius", "明るくする範囲", .05f, 4, .01f, CrtSignalProfile.CenterRadius),
                        Add("centerFalloff", "外側へのなだらかさ", .1f, 16, .1f, CrtSignalProfile.CenterFalloff) } },
                    new Group { id = "color", label = "薄い色づけ", controls = new[] {
                        Add("tintStrength", "色づけの強さ", 0, 1, .001f, CrtSignalProfile.TintStrength),
                        Add("tintCoolness", "中性色から寒色へ", 0, 1, .01f, CrtSignalProfile.TintCoolness) } },
                    new Group { id = "signal", label = "画面と通常加工", controls = new[] {
                        Add("signalGain", "画面の明るさ", 0, 4, .01f, CrtSignalProfile.Gain),
                        Add("mono", "通常加工の量", 0, 1, .01f, CrtSignalProfile.Mono),
                        Add("bandwidth", "輝度と色の帯域制限", 0, 1, 1, CrtSignalProfile.Bandwidth) } },
                    new Group { id = "post", label = "映像の調整", controls = new[] {
                        Add("exposure", "露出", -10, 10, .01f, CrtSignalProfile.Exposure, "EV"), Add("contrast", "コントラスト", -4, 8, .01f, CrtSignalProfile.Contrast),
                        Add("saturation", "彩度", -4, 8, .01f, CrtSignalProfile.Saturation), Add("temperature", "色温度", -5, 5, .01f, CrtSignalProfile.Temperature),
                        Add("tint", "緑とマゼンタ", -5, 5, .01f, CrtSignalProfile.Tint), Add("lift", "黒の明るさ", -1, 1, .001f, CrtSignalProfile.Lift),
                        Add("vignette", "周辺の暗さ", 0, 1, .01f, CrtSignalProfile.Vignette), Add("grain", "粒の強さ", 0, 4, .01f, CrtSignalProfile.Grain),
                        Add("scanline", "走査線の強さ", 0, 4, .01f, CrtSignalProfile.Scanline) } },
                    new Group { id = "optics", label = "レンズ", controls = new[] {
                        Add("lensWarp", "レンズのゆがみ", -.2f, 1, .001f, CrtSignalProfile.LensWarp),
                        Add("sigmaR", "赤のぼけ", 0, 8, .01f, CrtSignalProfile.Sigma.x, "px"),
                        Add("sigmaGB", "緑と青のぼけ", 0, 8, .01f, CrtSignalProfile.Sigma.y, "px"),
                        Add("convergenceMultiplier", "色の位置ずれ", -10, 10, .01f, CrtSignalProfile.ConvergenceMultiplier) } },
                    new Group { id = "noise", label = "カメラのノイズ", controls = new[] {
                        Add("noiseDark", "暗部のノイズ", 0, 2, .001f, CrtSignalProfile.NoiseDark), Add("noiseFixed", "固定ノイズ", 0, 2, .001f, CrtSignalProfile.NoiseFixed) } }
                };
            }

            private Control Add(string key, string label, float min, float max, float step, float baseline, string unit = "", float? initial = null)
            {
                var control = new Control { key = key, label = label, min = min, max = max, step = step, @base = baseline, @default = initial ?? baseline, unit = unit };
                _controls.Add(key, control); _baseline.Add(key, baseline); return control;
            }
            public ParameterValues Values(bool baseline, string mode = "normal")
            {
                ValidateMode(mode);
                var result = new ParameterValues();
                foreach (Control c in _controls.Values)
                    typeof(ParameterValues).GetField(c.key)!.SetValue(result, baseline ? c.@base : c.@default);
                if (mode == "doll" || mode == "transformed")
                {
                    result.mono = mode == "transformed" ? CrtSignalProfile.DollMono : 1;
                    result.noiseDark = mode == "transformed" ? CrtSignalProfile.DollNoiseDark
                        : ScreenDecayLogic.NoiseTargetFor(2, 2, new[] { 0, 1, 2 });
                }
                if (mode == "transformed")
                {
                    result.grain = CrtSignalProfile.DollGrain;
                    result.scanline = CrtSignalProfile.DollScanline;
                    result.sigmaR = CrtSignalProfile.DollSigma.x;
                    result.sigmaGB = CrtSignalProfile.DollSigma.y;
                    result.noiseFixed = CrtSignalProfile.DollNoiseFixed;
                }
                return result;
            }
            public string Configuration(string mode)
            {
                ValidateMode(mode);
                // Copy controls so one mode cannot change the metadata cached by the other.
                Group[] groups = JsonUtility.FromJson<Metadata>(JsonUtility.ToJson(new Metadata { groups = _groups })).groups;
                ParameterValues defaults = Values(false, mode), baseline = Values(true, mode);
                foreach (Group group in groups) foreach (Control control in group.controls)
                {
                    control.@default = (float)typeof(ParameterValues).GetField(control.key)!.GetValue(defaults);
                    control.@base = (float)typeof(ParameterValues).GetField(control.key)!.GetValue(baseline);
                }
                var sources = new List<Source> { new Source { id = "live", label = "無人写真" } };
                if (mode == "doll")
                {
                    string[] labels = { "遠景", "①遠い", "②近い", "③すぐそこ", "④追いつき" };
                    for (int i = 0; i < labels.Length; i++) sources.Add(new Source { id = "pov_" + i, label = labels[i] });
                }
                if (mode == "transformed") sources.Add(new Source { id = "cg_doll", label = "無人写真＋CG人形" });
                return JsonUtility.ToJson(new Metadata { mode = mode, defaultSource = mode == "doll" ? "pov_0" : mode == "transformed" ? "cg_doll" : "live", sources = sources.ToArray(),
                    groups = groups, defaults = defaults, baseline = baseline, baseLabel = mode == "doll" ? "人形視点（2-C）" : mode == "transformed" ? "人形化後（3-A〜4-A）" : "本編採用版・枠なし（通常加工0.5）" });
            }
            private static void ValidateMode(string mode)
            {
                if (mode != "normal" && mode != "doll" && mode != "transformed") throw new ArgumentException("mode must be normal, doll or transformed");
            }
            public static void ValidateSelection(string mode, string source)
            {
                ValidateMode(mode);
                if (source == "live") return;
                if (mode == "doll" && TakeRunner.IsDollPovCue(source)) return;
                if (mode == "transformed" && source == "cg_doll") return;
                throw new ArgumentException("source is not available in this mode");
            }
            public static bool ValidCamera(string camera) => camera == "A" || camera == "B" || camera == "C";
            public Dictionary<string, float> Resolve(ParameterValues requested)
            {
                var result = new Dictionary<string, float>(StringComparer.Ordinal);
                foreach (Control control in _controls.Values)
                {
                    float value = (float)typeof(ParameterValues).GetField(control.key)!.GetValue(requested);
                    if (float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentException("Parameter must be a finite number: " + control.key);
                    result.Add(control.key, Mathf.Clamp(value, control.min, control.max));
                }
                return result;
            }
            public byte[] Baseline(string camera, string mode = "normal", string source = "live")
            {
                ValidateSelection(mode, source);
                string key = mode + "/" + source + "/" + camera;
                if (!_baselineImages.TryGetValue(key, out byte[] image)) { image = Render(camera, Resolve(Values(true, mode)), source: source); _baselineImages.Add(key, image); }
                return image;
            }
            public byte[] Render(string camera, Dictionary<string, float> p, bool adoptedRuntime = false, string source = "live", float dollProfileBlend = 0)
            {
                if (!ValidCamera(camera)) throw new ArgumentException("camera must be A, B or C");
                if (source != "live" && source != "cg_doll" && !TakeRunner.IsDollPovCue(source)) throw new ArgumentException("Unknown source");
                _material.SetFloat("_CrtSignalProfileSelected", adoptedRuntime ? 1 : 0);
                _material.SetFloat("_CrtDollProfileBlend", dollProfileBlend);
                _material.SetFloat("_CrtScreenOnly", adoptedRuntime ? 1 : 0);
                _material.SetFloat("_CrtCenterEv", 0); _material.SetFloat("_CrtTintStrength", 0);
                _camera.backgroundColor = adoptedRuntime ? Color.clear : Color.black;
                int index = camera[0] - 'A'; _material.SetTexture("_LiveTex", _photos[index]);
                float bias = SetSource(source, camera, _bias[index]);
                CameraFeelFx.WriteUniforms(_material, p["noiseDark"], p["noiseFixed"], bias, 0, p["mono"] > 0 ? ScreenDecayLogic.BlocksFor(p["mono"]) : 0, 0, p["mono"]);
                _material.SetFloat("_Glare", 0);
                foreach (string key in new[] { "exposure", "contrast", "saturation", "temperature", "tint", "lift", "vignette", "grain", "scanline" })
                    _material.SetFloat("_" + char.ToUpperInvariant(key[0]) + key.Substring(1), p[key]);
                _material.SetFloat("_CrtSignalGain", p["signalGain"]); _material.SetFloat("_CrtSignalBandwidth", p["bandwidth"] >= .5f ? 1 : 0);
                _material.SetFloat("_CrtLensWarp", p["lensWarp"]); _material.SetVector("_CrtPsfSigma", new Vector3(p["sigmaR"], p["sigmaGB"], p["sigmaGB"]));
                _material.SetVector("_CrtOpticalConvergence", CrtReferenceScreen.OpticalConvergence * p["convergenceMultiplier"]);
                if (adoptedRuntime) CrtSignalProfile.ApplySelected(_material);
                CameraLensLogic.Apply(_material, camera, TakeRunner.IsDollPovCue(source));
                if (!_processor.PrepareDisplay(_material, index)) throw new InvalidOperationException("Runtime CRT signal could not be prepared");
                if (!adoptedRuntime)
                {
                    _adjustment.SetFloat("_CenterEv", p["centerEv"]); _adjustment.SetFloat("_CenterRadius", p["centerRadius"]); _adjustment.SetFloat("_CenterFalloff", p["centerFalloff"]);
                    _adjustment.SetFloat("_TintStrength", p["tintStrength"]); _adjustment.SetFloat("_TintCoolness", p["tintCoolness"]);
                    Graphics.Blit(_processor.Processed, _adjusted, _adjustment, 0);
                    _material.SetTexture("_CrtProcessedSignalTex", _adjusted);
                }
                // Render the curved screen once, after replacing only its processed signal.
                Compile(_material); _camera.Render(); Sequence++;
                RenderTexture? previous = RenderTexture.active;
                try
                {
                    RenderTexture.active = _final;
                    if (adoptedRuntime)
                    {
                        var rgba = new Texture2D(1280, 1030, TextureFormat.RGBA32, false, false);
                        try { rgba.ReadPixels(new Rect(0, 0, 1280, 1030), 0, 0); rgba.Apply(); return rgba.EncodeToPNG(); }
                        finally { UnityEngine.Object.DestroyImmediate(rgba); }
                    }
                    _readback.ReadPixels(new Rect(0, 0, _final.width, _final.height), 0, 0); _readback.Apply(); return _readback.EncodeToPNG();
                }
                finally { RenderTexture.active = previous; }
            }
            private float SetSource(string source, string camera, float bias)
            {
                _material.SetTexture("_CgTex", Texture2D.blackTexture); _material.SetFloat("_CgStrength", 0);
                _material.SetVector("_LiveScale", new Vector4(.75f, 1, 0, 0)); _material.SetVector("_CgScale", new Vector4(.75f, 1, 0, 0));
                _material.SetVector("_CgLens", new Vector4(0, .5f, .5f, 0)); _material.SetVector("_CgFocalN", new Vector4(1, 1, 0, 0));
                _material.SetVector("_ActorFocus", new Vector4(.5f, .5f, .2f, 0)); _material.SetVector("_ActorLight", new Vector4(.5f, .5f, .2f, 0));
                _material.SetVector("_RedKeep", Vector4.zero);
                bool overlay = TakeRunner.IsDollPovCue(source);
                Texture texture = Texture2D.blackTexture;
                Vector4 scale = new Vector4(1, 1, 0, 0);
                if (overlay)
                {
                    if (!_pov.TryGetValue(source, out Texture2D frame))
                    {
                        string path = Path.Combine("G:/マイドライブ/shubie/reports/2026-11-01_crt-pov-tuner/frames", source + ".png");
                        frame = Own(new Texture2D(2, 2, TextureFormat.RGB24, true, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear });
                        if (!frame.LoadImage(File.ReadAllBytes(path))) throw new InvalidDataException("Cannot load POV frame: " + source);
                        _pov.Add(source, frame);
                    }
                    texture = frame;
                    float aspect = (float)frame.width / frame.height;
                    float screenAspect = CrtReferenceScreen.LogicalAspect;
                    scale = aspect < screenAspect ? new Vector4(aspect / screenAspect, 1, 0, 0) : new Vector4(1, screenAspect / aspect, 0, 0);
                }
                _material.SetTexture("_OverlayTex", texture); _material.SetTexture("_OverlayTexB", Texture2D.blackTexture);
                _material.SetTexture("_MaskTex", overlay ? Texture2D.whiteTexture : Texture2D.blackTexture);
                _material.SetVector("_OverlayScale", scale); _material.SetFloat("_OverlayStrength", overlay ? 1 : 0); _material.SetFloat("_OverlayBlend", 0);
                _material.SetVector("_OverlayGain", new Vector4(1, 1, 1, 0)); _material.SetVector("_OverlayOffset", Vector4.zero);
                _material.SetFloat("_Overlay2Strength", 0);
                if (source == "cg_doll")
                {
                    _dollSource ??= new ShowCompositePreview.CrtTuningDollSource();
                    bias = _dollSource.WriteTo(_material, camera);
                }
                return bias;
            }
            private T Own<T>(T value) where T : UnityEngine.Object { _objects.Add(value); return value; }
            private RenderTexture Target(RenderTextureFormat format, RenderTextureReadWrite color, int depth)
            {
                var target = Own(new RenderTexture(1280, 1030, depth, format, color) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear });
                if (!target.Create()) throw new InvalidOperationException("Cannot create tuning render target"); return target;
            }
            public void Dispose()
            {
                _processor?.Dispose();
                _dollSource?.Dispose(); _dollSource = null;
                for (int i = _objects.Count - 1; i >= 0; i--) { if (_objects[i] is RenderTexture target) target.Release(); UnityEngine.Object.DestroyImmediate(_objects[i]); }
                _objects.Clear();
            }
        }

        private static void Compile(Material material)
        {
            ShaderUtil.CompilePass(material, 0, true);
            foreach (var message in ShaderUtil.GetShaderMessages(material.shader))
                if (message.severity == ShaderCompilerMessageSeverity.Error) throw new InvalidOperationException(message.message);
        }

    }
}
