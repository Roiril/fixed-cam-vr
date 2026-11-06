#nullable enable
using System;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>1枚のスクリーンが所有するGPU信号処理。生Liveは借り、生成RTだけを所有する。</summary>
    public sealed class CrtSignalProcessor : IDisposable
    {
        private static readonly int RuntimeId = Shader.PropertyToID("_CrtSignalRuntime"),
            ProfileSelectedId = Shader.PropertyToID("_CrtSignalProfileSelected"),
            LiveTexId = Shader.PropertyToID("_LiveTex"), LiveScaleId = Shader.PropertyToID("_LiveScale"),
            PsfSigmaId = Shader.PropertyToID("_CrtPsfSigma"), CoarseBlocksId = Shader.PropertyToID("_CoarseBlocks"),
            PixelateId = Shader.PropertyToID("_Pixelate"), SignalGainId = Shader.PropertyToID("_CrtSignalGain"),
            SignalBandwidthId = Shader.PropertyToID("_CrtSignalBandwidth"), PreGlassEncodedId = Shader.PropertyToID("_CrtPreGlassEncoded"),
            CrtCenterEvId = Shader.PropertyToID("_CrtCenterEv"), CrtCenterRadiusId = Shader.PropertyToID("_CrtCenterRadius"),
            CrtCenterFalloffId = Shader.PropertyToID("_CrtCenterFalloff"), CrtTintStrengthId = Shader.PropertyToID("_CrtTintStrength"),
            CrtTintCoolnessId = Shader.PropertyToID("_CrtTintCoolness"), CrtApertureId = Shader.PropertyToID("_CrtAperture"),
            ProcessedTexId = Shader.PropertyToID("_CrtProcessedSignalTex"), SignalReadyId = Shader.PropertyToID("_CrtSignalReady"),
            PsfReadyId = Shader.PropertyToID("_CrtRuntimePsfReady"), PsfTexId = Shader.PropertyToID("_CrtRuntimePsfTex"),
            PsfRedTexId = Shader.PropertyToID("_CrtRuntimePsfRedTex"), PsfBlueTexId = Shader.PropertyToID("_CrtRuntimePsfBlueTex"),
            PsfUvShiftId = Shader.PropertyToID("_CrtRuntimePsfUVShift"), KernelTexelId = Shader.PropertyToID("_KernelTexel"),
            PhaseId = Shader.PropertyToID("_Phase"), SigmaId = Shader.PropertyToID("_Sigma"), ActiveLodId = Shader.PropertyToID("_ActiveLod"),
            GainId = Shader.PropertyToID("_Gain"), BandwidthId = Shader.PropertyToID("_Bandwidth"), SourceEncodedId = Shader.PropertyToID("_SourceEncoded"),
            CenterEvId = Shader.PropertyToID("_CenterEv"), CenterRadiusId = Shader.PropertyToID("_CenterRadius"), CenterFalloffId = Shader.PropertyToID("_CenterFalloff"),
            TintStrengthId = Shader.PropertyToID("_TintStrength"), TintCoolnessId = Shader.PropertyToID("_TintCoolness"), ApertureId = Shader.PropertyToID("_Aperture");

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
        public long MipBuilds { get; private set; }
        public long ProcessedFrames { get; private set; }
        public long DisplayBindings { get; private set; }
        public long ExplicitCaptures { get; private set; }
        public long PeakOwnedTextureBytes { get; private set; }
        public RenderTexture? LinearSource => _linear;
        public RenderTexture? PsfSource => _psf;
        /// <summary>帯域フィルタが有効な間（_CrtSignalBandwidth &gt; .5）は sRGB の値を持つ。無効なら linear。</summary>
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
            if (!state.HasProperty(RuntimeId) || state.GetFloat(RuntimeId) < .5f) return false;
            _busy = true;
            RenderTexture? previous = RenderTexture.active;
            try
            {
                if (_bound != null && _bound != state) UnbindOwned();
                // CameraFeel writes its legacy glare in LateUpdate. The selected profile owns only
                // its fixed optical values; synchronous captures still use current mono/AGC/CG/OSD.
                if (state.GetFloat(ProfileSelectedId) > .5f) CrtSignalProfile.ApplySelected(state);
                _composition.CopyPropertiesFromMaterial(state);
                // The copy carries last frame's output. The pre-glass pass must not read it as ready.
                // state keeps its bindings until the end: toggling its keyword every frame only costs a variant lookup.
                InvalidateBindings(_composition);
                // Stable player passes never borrow an Editor diagnostic variant or its auxiliary source.
                _composition.shaderKeywords = Array.Empty<string>();
                _composition.SetFloat(RuntimeId, 1);
                float bandwidth = _composition.GetFloat(SignalBandwidthId);
                float encoded = bandwidth > .5f ? 1 : 0;
                _composition.SetFloat(PreGlassEncodedId, encoded);
                Texture live = state.GetTexture(LiveTexId) ?? Texture2D.blackTexture;
                Vector3 sigma = _composition.GetVector(PsfSigmaId); Vector4 scale = state.GetVector(LiveScaleId);
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
                    _linear!.DiscardContents();
                    Graphics.Blit(live, _linear, _source, 0);
                    // Only a coarse Gaussian reads below the base level; a change of lod always lands here again.
                    if (lod > 0) { _linear.GenerateMips(); MipBuilds++; }
                    _source.SetVector(KernelTexelId, new Vector4(1f / live.width, 1f / live.height, 0, 0)); _source.SetVector(PhaseId, phase);
                    Gaussian(sigma.y, lod, _psf!);
                    if (sigma.x != sigma.y) Gaussian(sigma.x, lod, _psfRed!);
                    if (_psfBlue != null) Gaussian(sigma.z, lod, _psfBlue);
                    _cachedSource = live; _cachedGeneration = generation; _cachedUpdate = live.updateCount; _cachedSigma = sigma; _cachedScale = scale; _cachedLod = lod; _cachedSrgb = srgb;
                    InputUpdates++; PsfUpdates++;
                }
                BindPsf(_composition, sigma, phase);
                // Every pass here writes all pixels without blending, so a tile GPU need not load the old contents.
                _preGlass!.DiscardContents();
                Graphics.Blit(Texture2D.blackTexture, _preGlass, _composition, _preGlassPass);
                _filter.SetFloat(GainId, _composition.GetFloat(SignalGainId)); _filter.SetFloat(BandwidthId, bandwidth); _filter.SetFloat(SourceEncodedId, encoded);
                _filter.SetFloat(CenterEvId, _composition.GetFloat(CrtCenterEvId)); _filter.SetFloat(CenterRadiusId, _composition.GetFloat(CrtCenterRadiusId)); _filter.SetFloat(CenterFalloffId, _composition.GetFloat(CrtCenterFalloffId));
                _filter.SetFloat(TintStrengthId, _composition.GetFloat(CrtTintStrengthId)); _filter.SetFloat(TintCoolnessId, _composition.GetFloat(CrtTintCoolnessId)); _filter.SetVector(ApertureId, _composition.GetVector(CrtApertureId));
                _processed!.DiscardContents();
                Graphics.Blit(_preGlass, _processed, _filter, 0);
                ProcessedFrames++;
                BindPsf(state, sigma, phase);
                state.SetTexture(ProcessedTexId, _processed); state.SetFloat(SignalReadyId, 1);
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
            float coarse = state.GetFloat(CoarseBlocksId), pixelate = state.GetFloat(PixelateId);
            float cb = coarse > .5f ? Mathf.Max(6, Mathf.Floor(coarse)) : 0;
            float pix = pixelate > .001f ? Mathf.Max(6, Mathf.Floor(Mathf.Lerp(400, 18, Mathf.Clamp01(pixelate)))) : 0;
            if (cb <= 0 || pix > 0 && pix <= cb) return 0;
            float across = Mathf.Max(cb * Mathf.Max(state.GetVector(LiveScaleId).x, .001f), 1);
            return Mathf.Max(Mathf.Log(Mathf.Max(sourceWidth, 2) / across, 2), 0);
        }

        public static void InvalidateBindings(Material state)
        {
            state.DisableKeyword("CRT_SIGNAL_RUNTIME");
            state.SetFloat(SignalReadyId, 0); state.SetFloat(PsfReadyId, 0);
            state.SetTexture(ProcessedTexId, null); state.SetTexture(PsfTexId, null);
            state.SetTexture(PsfRedTexId, null); state.SetTexture(PsfBlueTexId, null);
        }

        private void Gaussian(float sigma, float lod, RenderTexture destination)
        {
            _source.SetVector(SigmaId, new Vector3(sigma, sigma, sigma)); _source.SetFloat(ActiveLodId, lod);
            _horizontal!.DiscardContents();
            Graphics.Blit(_linear, _horizontal, _source, 1); _source.SetFloat(ActiveLodId, 0);
            destination.DiscardContents();
            Graphics.Blit(_horizontal, destination, _source, 2);
        }

        private void BindPsf(Material state, Vector3 sigma, Vector4 phase)
        {
            state.SetTexture(PsfTexId, _psf); state.SetTexture(PsfRedTexId, sigma.x == sigma.y ? _psf : _psfRed);
            state.SetTexture(PsfBlueTexId, sigma.z == sigma.y ? _psf : sigma.z == sigma.x ? _psfRed : _psfBlue);
            state.SetVector(PsfUvShiftId, phase); state.SetFloat(PsfReadyId, 1);
        }

        private void EnsureTargets(int width, int height, int psfScale, bool separateBlue)
        {
            width = Mathf.Max(width, 1); height = Mathf.Max(height, 1);
            int psfWidth = width * psfScale, psfHeight = height * psfScale;
            bool linearOk = Alive(_linear) && _linear!.width == width && _linear.height == height;
            bool psfOk = Alive(_horizontal) && _horizontal!.width == psfWidth && _horizontal.height == psfHeight
                && Alive(_psf) && Alive(_psfRed) && (_psfBlue != null) == separateBlue && (_psfBlue == null || _psfBlue.IsCreated());
            bool canonicalOk = Alive(_preGlass) && Alive(_processed);
            if (linearOk && psfOk && canonicalOk) return;
            UnbindOwned();
            // 粗さが 0 と非 0 をまたぐと PSF だけ倍の寸法になる。体験の途中で起きるので、要らない物は作り直さない。
            if (!linearOk)
            {
                Release(ref _linear);
                _linear = Target(width, height, true, "CRT Raw Linear Mips");
            }
            if (!psfOk)
            {
                Release(ref _horizontal); Release(ref _psf); Release(ref _psfRed); Release(ref _psfBlue);
                _horizontal = Target(psfWidth, psfHeight, false, "CRT Source Gaussian H"); _psf = Target(psfWidth, psfHeight, false, "CRT Green-sigma RGB Gaussian");
                _psfRed = Target(psfWidth, psfHeight, false, "CRT Red-sigma RGB Gaussian");
                if (separateBlue) _psfBlue = Target(psfWidth, psfHeight, false, "CRT Blue-sigma RGB Gaussian");
            }
            if (!canonicalOk)
            {
                Release(ref _preGlass); Release(ref _processed);
                _preGlass = Target(CrtSignalProfile.Width, CrtSignalProfile.Height, false, "CRT Canonical PreGlass");
                _processed = Target(CrtSignalProfile.Width, CrtSignalProfile.Height, false, "CRT Canonical Processed");
            }
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

        private static bool Alive(RenderTexture? texture) => texture != null && texture.IsCreated();

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
            if (_bound != null && (_bound.GetTexture(ProcessedTexId) == _processed || _bound.GetTexture(PsfTexId) == _psf)) InvalidateBindings(_bound);
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
