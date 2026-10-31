#nullable enable
using System;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>1枚のスクリーンが所有するGPU信号処理。生Liveは借り、生成RTだけを所有する。</summary>
    public sealed class CrtSignalProcessor : IDisposable
    {
        private readonly Material _composition, _source, _filter;
        private readonly int _preGlassPass;
        private RenderTexture? _linear, _horizontal, _psf, _psfRed, _psfBlue, _preGlass, _processed;
        private Material? _bound;
        private Texture? _cachedSource;
        private long _cachedGeneration = long.MinValue;
        private uint _cachedUpdate;
        private Vector3 _cachedSigma;
        private Vector4 _cachedScale;
        private float _cachedLod = -1;
        private bool _cachedSrgb, _busy, _disposed;

        public long InputUpdates { get; private set; }
        public long PsfUpdates { get; private set; }
        public long ProcessedFrames { get; private set; }
        public long DisplayBindings { get; private set; }
        public long ExplicitCaptures { get; private set; }
        public long PeakOwnedTextureBytes { get; private set; }
        public RenderTexture? LinearSource => _linear;
        public RenderTexture? PsfSource => _psf;
        public RenderTexture? PreGlass => _preGlass;
        public RenderTexture? Processed => _processed;
        public bool IsDisposed => _disposed;

        public CrtSignalProcessor()
        {
            CrtSignalResources resources = CrtSignalResources.Load();
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat)) throw new NotSupportedException("CRT linear Float render targets are unsupported");
            Shader composite = resources.Composite, psf = resources.SourcePsf, filter = resources.SignalFilter;
            _composition = NewMaterial(composite);
            _source = NewMaterial(psf);
            _filter = NewMaterial(filter);
            _preGlassPass = _composition.FindPass("CanonicalPreGlass");
            if (_preGlassPass < 0) { Dispose(); throw new InvalidOperationException("Missing CRT canonical pre-glass pass"); }
        }

        /// <summary>mono/AGC/CG/OSD/Timeは毎回現在値を読む。PSFだけは元frameと条件が同じなら再利用する。</summary>
        public bool PrepareDisplay(Material state, long generation, bool forceSource = false)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CrtSignalProcessor));
            if (_busy) return false;
            if (!state.HasProperty("_CrtSignalRuntime") || state.GetFloat("_CrtSignalRuntime") < .5f) return false;
            _busy = true;
            RenderTexture? previous = RenderTexture.active;
            try
            {
                if (_bound != null && _bound != state) UnbindOwned();
                InvalidateBindings(state);
                _composition.CopyPropertiesFromMaterial(state);
                // CameraFeel writes its legacy glare in LateUpdate. The selected profile owns only
                // its fixed optical values; synchronous captures still use current mono/AGC/CG/OSD.
                if (state.GetFloat("_CrtSignalProfileSelected") > .5f) CrtSignalProfile.ApplySelected(_composition);
                // Stable player passes never borrow an Editor diagnostic variant or its auxiliary source.
                _composition.shaderKeywords = Array.Empty<string>();
                _composition.SetFloat("_CrtSignalRuntime", 1);
                Texture live = state.GetTexture("_LiveTex") ?? Texture2D.blackTexture;
                Vector3 sigma = _composition.GetVector("_CrtPsfSigma"); Vector4 scale = state.GetVector("_LiveScale");
                float lod = CoarseLod(state, live.width); bool srgb = IsSrgb(live);
                // At nonzero mip LOD the piecewise-linear knots include half source pixels.
                // A2x grid with quarter-pixel phase represents all knots, including each integer tap shift.
                int psfScale = lod > 0 ? 2 : 1;
                EnsureTargets(live.width, live.height, psfScale, sigma.z != sigma.y && sigma.z != sigma.x);
                Vector4 phase = psfScale == 2 ? new Vector4(.25f / live.width, .25f / live.height, 0, 0) : Vector4.zero;
                bool changed = forceSource || _cachedSource != live || _cachedGeneration != generation || _cachedUpdate != live.updateCount
                    || _cachedSigma != sigma || _cachedScale != scale || _cachedLod != lod || _cachedSrgb != srgb;
                if (changed)
                {
                    Graphics.Blit(live, _linear!, _source, 0);
                    _linear!.GenerateMips();
                    _source.SetVector("_KernelTexel", new Vector4(1f / live.width, 1f / live.height, 0, 0)); _source.SetVector("_Phase", phase);
                    Gaussian(sigma.y, lod, _psf!);
                    if (sigma.x != sigma.y) Gaussian(sigma.x, lod, _psfRed!);
                    if (_psfBlue != null) Gaussian(sigma.z, lod, _psfBlue);
                    _cachedSource = live; _cachedGeneration = generation; _cachedUpdate = live.updateCount; _cachedSigma = sigma; _cachedScale = scale; _cachedLod = lod; _cachedSrgb = srgb;
                    InputUpdates++; PsfUpdates++;
                }
                BindPsf(_composition, sigma, phase);
                Graphics.Blit(Texture2D.blackTexture, _preGlass!, _composition, _preGlassPass);
                _filter.SetFloat("_Gain", _composition.GetFloat("_CrtSignalGain")); _filter.SetFloat("_Bandwidth", _composition.GetFloat("_CrtSignalBandwidth"));
                _filter.SetFloat("_CenterEv", _composition.GetFloat("_CrtCenterEv")); _filter.SetFloat("_CenterRadius", _composition.GetFloat("_CrtCenterRadius")); _filter.SetFloat("_CenterFalloff", _composition.GetFloat("_CrtCenterFalloff"));
                _filter.SetFloat("_TintStrength", _composition.GetFloat("_CrtTintStrength")); _filter.SetFloat("_TintCoolness", _composition.GetFloat("_CrtTintCoolness")); _filter.SetVector("_Aperture", _composition.GetVector("_CrtAperture"));
                Graphics.Blit(_preGlass, _processed!, _filter, 0);
                ProcessedFrames++;
                BindPsf(state, sigma, phase);
                state.SetTexture("_CrtProcessedSignalTex", _processed); state.SetFloat("_CrtSignalReady", 1);
                state.EnableKeyword("CRT_SIGNAL_RUNTIME");
                _bound = state; DisplayBindings++;
                return true;
            }
            catch { InvalidateBindings(state); throw; }
            finally { RenderTexture.active = previous != null && previous.IsCreated() ? previous : null; _busy = false; }
        }

        /// <summary>LateUpdate途中のFreezeにも対応する。frame番号で前段の再構成を省略しない。</summary>
        public bool Render(Material state, RenderTexture destination, long generation = 0)
        {
            if (destination == null || !destination.IsCreated()) return false;
            if (!PrepareDisplay(state, generation, true)) return false;
            RenderTexture? previous = RenderTexture.active;
            try
            {
                if (state.GetFloat("_CrtScreenOnly") > .5f) { RenderTexture.active = destination; GL.Clear(true, true, Color.clear); }
                Graphics.Blit(Texture2D.blackTexture, destination, state, 0); ExplicitCaptures++; return true;
            }
            finally { RenderTexture.active = previous; }
        }

        /// <summary>cloneのLive差し替えを含め、引数の現在値だけから同期撮影する。引数の所有権は取らない。</summary>
        public static bool Capture(Material state, RenderTexture destination)
        {
            var snapshot = new Material(state) { hideFlags = HideFlags.HideAndDontSave };
            try { using (var processor = new CrtSignalProcessor()) return processor.Render(snapshot, destination); }
            catch (Exception e) { Debug.LogError("[CrtSignal] 同期撮影に失敗: " + e); return false; }
            finally { DestroyOwned(snapshot); }
        }

        public static float CoarseLod(Material state, int sourceWidth)
        {
            float coarse = state.GetFloat("_CoarseBlocks"), pixelate = state.GetFloat("_Pixelate");
            float cb = coarse > .5f ? Mathf.Max(6, Mathf.Floor(coarse)) : 0;
            float pix = pixelate > .001f ? Mathf.Max(6, Mathf.Floor(Mathf.Lerp(400, 18, Mathf.Clamp01(pixelate)))) : 0;
            if (cb <= 0 || pix > 0 && pix <= cb) return 0;
            float across = Mathf.Max(cb * Mathf.Max(state.GetVector("_LiveScale").x, .001f), 1);
            return Mathf.Max(Mathf.Log(Mathf.Max(sourceWidth, 2) / across, 2), 0);
        }

        public static void InvalidateBindings(Material state)
        {
            state.DisableKeyword("CRT_SIGNAL_RUNTIME");
            state.SetFloat("_CrtSignalReady", 0); state.SetFloat("_CrtRuntimePsfReady", 0);
            state.SetTexture("_CrtProcessedSignalTex", null); state.SetTexture("_CrtRuntimePsfTex", null);
            state.SetTexture("_CrtRuntimePsfRedTex", null); state.SetTexture("_CrtRuntimePsfBlueTex", null);
        }

        private void Gaussian(float sigma, float lod, RenderTexture destination)
        {
            _source.SetVector("_Sigma", new Vector3(sigma, sigma, sigma)); _source.SetFloat("_ActiveLod", lod);
            Graphics.Blit(_linear, _horizontal!, _source, 1); _source.SetFloat("_ActiveLod", 0);
            Graphics.Blit(_horizontal, destination, _source, 2);
        }

        private void BindPsf(Material state, Vector3 sigma, Vector4 phase)
        {
            state.SetTexture("_CrtRuntimePsfTex", _psf); state.SetTexture("_CrtRuntimePsfRedTex", sigma.x == sigma.y ? _psf : _psfRed);
            state.SetTexture("_CrtRuntimePsfBlueTex", sigma.z == sigma.y ? _psf : sigma.z == sigma.x ? _psfRed : _psfBlue);
            state.SetVector("_CrtRuntimePsfUVShift", phase); state.SetFloat("_CrtRuntimePsfReady", 1);
        }

        private void EnsureTargets(int width, int height, int psfScale, bool separateBlue)
        {
            width = Mathf.Max(width, 1); height = Mathf.Max(height, 1);
            if (_linear != null && _linear.width == width && _linear.height == height && _linear.IsCreated()
                && _horizontal != null && _horizontal.IsCreated() && _horizontal.width == width * psfScale && _horizontal.height == height * psfScale
                && _psf != null && _psf.IsCreated() && _psfRed != null && _psfRed.IsCreated() && (_psfBlue != null) == separateBlue
                && _preGlass != null && _preGlass.IsCreated() && _processed != null && _processed.IsCreated()) return;
            UnbindOwned();
            Release(ref _linear); Release(ref _horizontal); Release(ref _psf); Release(ref _psfRed); Release(ref _psfBlue); Release(ref _preGlass); Release(ref _processed);
            _linear = Target(width, height, true, "CRT Raw Linear Mips");
            _horizontal = Target(width * psfScale, height * psfScale, false, "CRT Source Gaussian H"); _psf = Target(width * psfScale, height * psfScale, false, "CRT Green-sigma RGB Gaussian");
            _psfRed = Target(width * psfScale, height * psfScale, false, "CRT Red-sigma RGB Gaussian");
            if (separateBlue) _psfBlue = Target(width * psfScale, height * psfScale, false, "CRT Blue-sigma RGB Gaussian");
            _preGlass = Target(CrtSignalProfile.Width, CrtSignalProfile.Height, false, "CRT Canonical PreGlass");
            _processed = Target(CrtSignalProfile.Width, CrtSignalProfile.Height, false, "CRT Canonical Processed");
            long allocated = 0;
            foreach (RenderTexture? rt in new[] { _linear, _horizontal, _psf, _psfRed, _psfBlue, _preGlass, _processed })
            {
                if (rt == null) continue;
                int w = rt.width, h = rt.height;
                do { allocated += (long)w * h * 16; if (!rt.useMipMap || w == 1 && h == 1) break; w = Mathf.Max(1, w / 2); h = Mathf.Max(1, h / 2); } while (true);
            }
            PeakOwnedTextureBytes = Math.Max(PeakOwnedTextureBytes, allocated);
            _cachedSource = null;
        }

        private static RenderTexture Target(int width, int height, bool mip, string name)
        {
            var rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            { name = name, hideFlags = HideFlags.HideAndDontSave, antiAliasing = 1, useMipMap = mip, autoGenerateMips = false, wrapMode = TextureWrapMode.Clamp, filterMode = mip ? FilterMode.Trilinear : FilterMode.Bilinear };
            if (!rt.Create()) { DestroyOwned(rt); throw new InvalidOperationException("Could not create " + name); }
            return rt;
        }

        private static bool IsSrgb(Texture texture) => UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat);
        private static Material NewMaterial(Shader shader) => new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        private static void Release(ref RenderTexture? texture)
        {
            if (texture == null) return;
            if (RenderTexture.active == texture) RenderTexture.active = null;
            texture.Release(); DestroyOwned(texture); texture = null;
        }
        private static void DestroyOwned(UnityEngine.Object value) { if (Application.isPlaying) UnityEngine.Object.Destroy(value); else UnityEngine.Object.DestroyImmediate(value); }

        private void UnbindOwned()
        {
            if (_bound != null && (_bound.GetTexture("_CrtProcessedSignalTex") == _processed || _bound.GetTexture("_CrtRuntimePsfTex") == _psf)) InvalidateBindings(_bound);
            _bound = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            UnbindOwned();
            Release(ref _linear); Release(ref _horizontal); Release(ref _psf); Release(ref _psfRed); Release(ref _psfBlue); Release(ref _preGlass); Release(ref _processed);
            DestroyOwned(_composition); DestroyOwned(_source); DestroyOwned(_filter);
        }
    }
}
