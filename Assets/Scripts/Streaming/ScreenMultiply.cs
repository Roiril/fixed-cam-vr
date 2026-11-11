#nullable enable

using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>目の前にメインスクリーンが増える</b>演出の実体（参考 <c>mul.jpg</c>）。
    /// 時計と出番は <see cref="AnomalyEyes"/> が持つ（<see cref="AnomalyEyesLogic"/> ＋ <see cref="EyesCueLogic"/>）。
    /// ここがやるのは 3 つ — メインスクリーンの画を取り込む / 面を組む / 数を材質へ書く。
    ///
    /// 画は本番のメインスクリーンを専用カメラで正面から写したもの。人形・乱れ・外形がそのまま付いて来る。
    /// 取り込みはリングへ順に積み、席ごとに少し前の画を映す。
    ///
    /// メインスクリーンは<b>テレビの筐体（<c>_CrtTvCase</c>）ごと</b>写す。面の枠はメインと同じ筐体になる（0321 / 0324）。
    /// 面は自前の枠を描かない。筐体と管の境は、メインと同じ輪郭のマスク（<c>_CrtScreenMaskTex</c>）を面へ渡して決める。
    /// 色味・電源の入り切り・乱れは管の中だけに掛け、筐体は取り込んだままの色で出す。
    ///
    /// ⚠ 親（[Eyes]）は頭の<b>位置</b>にだけ付いて動く。向きはワールド固定なので、首を振るとメインスクリーンだけが
    /// 面の前を滑り、後ろにあった面が出てくる。
    ///
    /// 闇には黒い霧がある（<see cref="ScreenFogLogic"/>）。遠い面ほど暗く、ぼけ、光がにじむ。
    /// 点いた面は霧の奥から席まで寄ってくる（<see cref="EmergeM"/> / <see cref="ArriveSec"/>）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ScreenMultiply : MonoBehaviour
    {
        public const string SurfaceShaderResource = "ScreenMultiply";
        public const string GlowShaderResource = "ScreenMultiplyGlow";

        // 取り込みの大きさ。メインの細かい走査線が折り返さないよう、倍で写してから縮める。
        public const int TileW = 320;
        public const int TileH = 258;
        private const int CaptureScale = 2;

        /// <summary>取り込みの間隔（秒）。リングは 1.6 秒ぶん。ビデオの時間軸で古い機材のコマ落ちに見える。</summary>
        public const float CaptureIntervalSec = 0.1f;

        // 写すカメラの奥行き。メインスクリーンの曲面だけが入る厚みにする（連絡の面などを巻き込まない）。
        private const float CaptureBackoffM = 0.2f;
        private const float CaptureDepthM = 0.4f;

        /// <summary>メインの色をどれだけ抜いて面の色へ寄せるか。少し残すと人形の赤だけが透ける。</summary>
        public const float Desaturate = 0.72f;
        public const float SurfaceGain = 1.25f;
        // ⚠ どちらも linear。sRGB では 0.05 が 0.25 の灰色に見える（最初の版で部屋じゅうが霧と灰色の枠になった）。
        public const float BodyGain = 0.006f;
        public const float GlowGain = 0.018f;

        /// <summary>点いた面が席より何 m 奥から寄ってくるか。席ごとに 0.75〜1.25 倍。</summary>
        public const float EmergeM = 3.4f;
        /// <summary>席に着くまでの秒。時計は自前（目の時計の「間」で止まらない）。</summary>
        public const float ArriveSec = 1.5f;
        /// <summary>取り込みの mip の段数。霧のぼけに使う。</summary>
        private const int FeedMips = 5;
        private const float NeverOpened = 1e9f;

        private static readonly int FeedTexId = Shader.PropertyToID("_FeedTex");
        private static readonly int BigId = Shader.PropertyToID("_WallBig");
        private static readonly int FieldId = Shader.PropertyToID("_WallField");
        private static readonly int SpanId = Shader.PropertyToID("_WallSpan");
        private static readonly int ClosingId = Shader.PropertyToID("_WallClosing");
        private static readonly int TimeId = Shader.PropertyToID("_WallTime");
        private static readonly int RingHeadId = Shader.PropertyToID("_WallRingHead");
        private static readonly int RingFilledId = Shader.PropertyToID("_WallRingFilled");
        private static readonly int RingCountId = Shader.PropertyToID("_WallRingCount");
        private static readonly int DesatId = Shader.PropertyToID("_WallDesat");
        private static readonly int GainId = Shader.PropertyToID("_WallGain");
        private static readonly int BodyGainId = Shader.PropertyToID("_WallBodyGain");
        private static readonly int GlowGainId = Shader.PropertyToID("_WallGlowGain");
        private static readonly int NowId = Shader.PropertyToID("_WallNow");
        private static readonly int OpenAtId = Shader.PropertyToID("_WallOpenAt");
        private static readonly int ArriveSecId = Shader.PropertyToID("_WallArriveSec");
        private static readonly int EmergeId = Shader.PropertyToID("_WallEmergeM");
        private static readonly int FogDensityId = Shader.PropertyToID("_WallFogDensity");
        private static readonly int FogClearId = Shader.PropertyToID("_WallFogClearM");
        private static readonly int FogBlurId = Shader.PropertyToID("_WallFogBlur");
        private static readonly int FarShowId = Shader.PropertyToID("_WallFarShow");
        private static readonly int ScreenMaskId = Shader.PropertyToID("_CrtScreenMaskTex");
        private static readonly int CrtReferenceId = Shader.PropertyToID("_CrtReference");
        private static readonly int TubeMaskId = Shader.PropertyToID("_WallTubeMask");
        private static readonly int HasTubeMaskId = Shader.PropertyToID("_WallHasTubeMask");

        private Mesh? _surfaceMesh, _glowMesh;
        private Material? _surfaceMat, _glowMat;
        private MeshRenderer? _surfaceRenderer, _glowRenderer;
        private Camera? _captureCam;
        private RenderTexture? _captureRt, _tileRt, _ring;
        private MjpegScreen? _screen;
        private Renderer? _screenRenderer;
        private int _ringHead;
        private int _ringFilled;
        private float _sinceCapture;
        private readonly float[] _openAt = new float[ScreenMultiplyLayout.MaxSeats];
        private float _now;

        /// <summary>面を描く材質と取り込みの器を組めたか。</summary>
        public bool IsBuilt { get; private set; }

        /// <summary>メインスクリーンの画を取り込んだ累計（画に出た側の観測）。0 のままなら面は 1 枚も映らない。</summary>
        public int CaptureCount { get; private set; }

        /// <summary>いま点いている枚数。</summary>
        public int OpenCount { get; private set; }

        /// <summary>席へ寄っている途中の枚数（画に出た側の観測）。点いた直後は 1 以上、落ち着けば 0。</summary>
        public int ArrivingCount { get; private set; }

        /// <summary>出番の中で、席より奥に面があった最大の距離（m）。0 のままなら奥から寄る動きは出ていない。</summary>
        public float MaxEmergeSeenM { get; private set; }

        private void Awake() => Build();

        private void OnDestroy()
        {
            Kill(_surfaceMesh);
            Kill(_glowMesh);
            Kill(_surfaceMat);
            Kill(_glowMat);
            if (_captureCam != null) Kill(_captureCam.gameObject);
            ReleaseRt(ref _captureRt);
            ReleaseRt(ref _tileRt);
            ReleaseRt(ref _ring);
        }

        private static void Kill(Object? o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        private static void ReleaseRt(ref RenderTexture? rt)
        {
            if (rt == null) return;
            rt.Release();
            Kill(rt);
            rt = null;
        }

        /// <summary>
        /// 組む。Play では Awake が呼ぶ。Editor のプレビュー（<c>menu screen-wall</c>）は Awake が走らないのでここを直接呼ぶ。
        /// </summary>
        public void Build()
        {
            if (IsBuilt) return;
            var surface = Resources.Load<Shader>(SurfaceShaderResource);
            var glow = Resources.Load<Shader>(GlowShaderResource);
            if (surface == null || glow == null || !surface.isSupported)
            {
                Debug.LogWarning("[ScreenMultiply] シェーダが見つかりません。増えるスクリーンは出ません。");
                return;
            }
            if ((SystemInfo.copyTextureSupport & UnityEngine.Rendering.CopyTextureSupport.Basic) == 0
                || SystemInfo.supports2DArrayTextures == false)
            {
                Debug.LogWarning("[ScreenMultiply] この端末は取り込みのリングを作れません。増えるスクリーンは出ません。");
                return;
            }

            GraphicsFormat fmt = GraphicsFormat.R8G8B8A8_SRGB;
            _captureRt = new RenderTexture(new RenderTextureDescriptor(TileW * CaptureScale, TileH * CaptureScale, fmt, 16))
            { name = "ScreenMultiplyCapture", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            // 霧のぼけは mip で作る。取り込みの 1 枚で mip を作り、段ごとにリングへ写す。
            var tileDesc = new RenderTextureDescriptor(TileW, TileH, fmt, 0)
            { useMipMap = true, autoGenerateMips = false, mipCount = FeedMips };
            _tileRt = new RenderTexture(tileDesc)
            { name = "ScreenMultiplyTile", filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp };
            var ringDesc = new RenderTextureDescriptor(TileW, TileH, fmt, 0)
            {
                dimension = UnityEngine.Rendering.TextureDimension.Tex2DArray,
                volumeDepth = ScreenMultiplyLayout.DelaySlots,
                useMipMap = true, autoGenerateMips = false, mipCount = FeedMips,
            };
            _ring = new RenderTexture(ringDesc)
            { name = "ScreenMultiplyRing", filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp };
            _captureRt.Create();
            _tileRt.Create();
            _ring.Create();

            _surfaceMesh = ScreenMultiplyLayout.BuildSurfaces();
            _glowMesh = ScreenMultiplyLayout.BuildGlow();
            _surfaceMat = new Material(surface) { name = "ScreenMultiply (runtime)" };
            _glowMat = new Material(glow) { name = "ScreenMultiplyGlow (runtime)" };
            _surfaceMat.SetTexture(FeedTexId, _ring);
            _surfaceMat.SetFloat(RingCountId, ScreenMultiplyLayout.DelaySlots);
            _surfaceMat.SetFloat(DesatId, Desaturate);
            _surfaceMat.SetFloat(GainId, SurfaceGain);
            _surfaceMat.SetFloat(BodyGainId, BodyGain);
            _glowMat.SetFloat(GlowGainId, GlowGain);
            SetLook(ScreenFogLogic.Density, EmergeM, showFar: true);
            ResetOpenTimes();

            _surfaceRenderer = AddPart("ScreenMultiplySurfaces", _surfaceMesh, _surfaceMat);
            _glowRenderer = AddPart("ScreenMultiplyGlow", _glowMesh, _glowMat);
            IsBuilt = true;
            Hide();
        }

        private MeshRenderer AddPart(string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, worldPositionStays: false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            r.allowOcclusionWhenDynamic = false;
            return r;
        }

        /// <summary>
        /// 霧の濃さ・奥から寄る距離・霧の奥の段を出すか。本番は <see cref="Build"/> が既定を書く。
        /// 変えるのはプレビューの比較（霧なしの版）だけ。
        /// </summary>
        public void SetLook(float fogDensity, float emergeM, bool showFar)
        {
            WriteLook(_surfaceMat, fogDensity, emergeM, showFar);
            WriteLook(_glowMat, fogDensity, emergeM, showFar);
        }

        private static void WriteLook(Material? m, float fogDensity, float emergeM, bool showFar)
        {
            if (m == null) return;
            m.SetFloat(FogDensityId, fogDensity);
            m.SetFloat(FogClearId, ScreenFogLogic.ClearM);
            m.SetFloat(FogBlurId, ScreenFogLogic.MaxBlurLod);
            m.SetFloat(EmergeId, emergeM);
            m.SetFloat(ArriveSecId, ArriveSec);
            m.SetFloat(FarShowId, showFar ? 1f : 0f);
        }

        /// <summary>
        /// 取り込む相手のメインスクリーンを渡す。渡さなければシーンの <see cref="MjpegScreen"/> を探す。
        /// Editor のプレビュー（<c>menu screen-wall</c>）が、自分で組んだ本番の面を取り込ませるために使う。
        /// </summary>
        public void SetCaptureSource(Renderer screen) => _screenRenderer = screen;

        private void ResetOpenTimes()
        {
            for (int i = 0; i < _openAt.Length; i++) _openAt[i] = NeverOpened;
            _now = 0f;
            ArrivingCount = 0;
            MaxEmergeSeenM = 0f;
        }

        /// <summary>出番の頭。リングを空に戻し、次のフレームで最初の画を取り込む。</summary>
        public void BeginRun()
        {
            _ringHead = 0;
            _ringFilled = 0;
            _sinceCapture = CaptureIntervalSec;
            ResetOpenTimes();
        }

        /// <summary>1 フレーム分。出番の間（兆しの闇も含む）だけ呼ぶ。</summary>
        public void Drive(float dt, float big, float field, float span, float closing, float clock)
        {
            if (!IsBuilt || _surfaceMat == null || _glowMat == null) return;

            _sinceCapture += dt;
            if (_sinceCapture >= CaptureIntervalSec)
            {
                _sinceCapture = 0f;
                CaptureMainScreen();
            }

            // 席ごとの「点いた時刻」。奥から寄る動きはここから数える（目の時計は途中で止まるので使わない）。
            _now += dt;
            int seats = Mathf.Min(ScreenMultiplyLayout.Seats.Length, _openAt.Length);
            int arriving = 0;
            for (int i = 0; i < seats; i++)
            {
                bool lit = ScreenMultiplyLayout.SeatOpen(i, big, field, span) > 0.001f;
                if (!lit) { _openAt[i] = NeverOpened; continue; }
                if (_openAt[i] >= NeverOpened) _openAt[i] = _now;
                float a = (_now - _openAt[i]) / ArriveSec;
                if (a >= 1f) continue;
                arriving++;
                float left = (1f - a) * (1f - a) * (1f - a) * EmergeM;
                if (left > MaxEmergeSeenM) MaxEmergeSeenM = left;
            }
            ArrivingCount = arriving;
            _surfaceMat.SetFloatArray(OpenAtId, _openAt);
            _glowMat.SetFloatArray(OpenAtId, _openAt);
            _surfaceMat.SetFloat(NowId, _now);
            _glowMat.SetFloat(NowId, _now);

            _surfaceMat.SetFloat(BigId, big);
            _surfaceMat.SetFloat(FieldId, field);
            _surfaceMat.SetFloat(SpanId, span);
            _surfaceMat.SetFloat(ClosingId, closing);
            _surfaceMat.SetFloat(TimeId, clock);
            _surfaceMat.SetFloat(RingHeadId, _ringHead);
            _surfaceMat.SetFloat(RingFilledId, _ringFilled);
            _glowMat.SetFloat(BigId, big);
            _glowMat.SetFloat(FieldId, field);
            _glowMat.SetFloat(SpanId, span);
            _glowMat.SetFloat(TimeId, clock);

            OpenCount = ScreenMultiplyLayout.CountOpen(big, field);
            // 最初の画が入るまでは出さない（リングの中身は前の体験者の画）。
            bool show = _ringFilled > 0 && OpenCount > 0;
            if (_surfaceRenderer != null) _surfaceRenderer.enabled = show;
            if (_glowRenderer != null) _glowRenderer.enabled = show;
        }

        public void Hide()
        {
            OpenCount = 0;
            if (_surfaceRenderer != null) _surfaceRenderer.enabled = false;
            if (_glowRenderer != null) _glowRenderer.enabled = false;
        }

        private void CaptureMainScreen()
        {
            if (_captureRt == null || _tileRt == null || _ring == null) return;
            if (_screenRenderer == null)
            {
                _screen = FindObjectOfType<MjpegScreen>();
                if (_screen != null) _screenRenderer = _screen.GetComponent<Renderer>();
                if (_screenRenderer == null) return;
            }
            EnsureCamera();
            if (_captureCam == null) return;

            // 正面から、メインスクリーンの面ぴったりを平行投影で写す。
            Transform s = _screenRenderer.transform;
            Vector3 scale = s.lossyScale;
            _captureCam.transform.SetPositionAndRotation(s.position - s.forward * CaptureBackoffM, s.rotation);
            _captureCam.orthographicSize = Mathf.Abs(scale.y) * 0.5f;
            _captureCam.aspect = Mathf.Abs(scale.x / Mathf.Max(0.0001f, scale.y));
            _captureCam.cullingMask = 1 << _screenRenderer.gameObject.layer;
            _captureCam.targetTexture = _captureRt;
            // 筐体ごと撮る。面の枠はメインの筐体そのもの。管の輪郭はメインと同じマスクで面が切る。
            BindTubeMask(_screenRenderer.sharedMaterial);
            _captureCam.Render();

            RenderTexture? prev = RenderTexture.active;
            Graphics.Blit(_captureRt, _tileRt);
            RenderTexture.active = prev;
            _tileRt.GenerateMips();

            int next = _ringFilled == 0 ? 0 : (_ringHead + 1) % ScreenMultiplyLayout.DelaySlots;
            for (int mip = 0; mip < FeedMips; mip++) Graphics.CopyTexture(_tileRt, 0, mip, _ring, next, mip);
            _ringHead = next;
            _ringFilled = Mathf.Min(_ringFilled + 1, ScreenMultiplyLayout.DelaySlots);
            CaptureCount++;
        }

        /// <summary>
        /// メインスクリーンの管の輪郭を面へ渡す。参照の筐体つきの画面だけ（<c>_CrtReference</c> = 1）。
        /// 筐体の無い画面は取り込みの alpha がそのまま管の外形になる（材質が輪郭の外を切る・alpha 0 の背景）。
        /// </summary>
        private void BindTubeMask(Material? screenMaterial)
        {
            if (_surfaceMat == null) return;
            Texture? mask = TubeMaskOf(screenMaterial);
            _surfaceMat.SetTexture(TubeMaskId, mask != null ? mask : Texture2D.whiteTexture);
            _surfaceMat.SetFloat(HasTubeMaskId, mask != null ? 1f : 0f);
        }

        /// <summary>面へ渡す管の輪郭。参照の筐体つきでなければ null（取り込みの alpha を使う）。</summary>
        public static Texture? TubeMaskOf(Material? screenMaterial)
        {
            if (screenMaterial == null || !screenMaterial.HasProperty(ScreenMaskId)
                || !screenMaterial.HasProperty(CrtReferenceId)
                || screenMaterial.GetFloat(CrtReferenceId) < 0.5f) return null;
            return screenMaterial.GetTexture(ScreenMaskId);
        }

        private void EnsureCamera()
        {
            if (_captureCam != null) return;
            var go = new GameObject("[ScreenMultiplyCamera]");
            go.transform.SetParent(transform, worldPositionStays: false);
            _captureCam = go.AddComponent<Camera>();
            _captureCam.orthographic = true;
            _captureCam.clearFlags = CameraClearFlags.SolidColor;
            _captureCam.backgroundColor = new Color(0f, 0f, 0f, 0f);   // 外形は alpha に出る
            _captureCam.nearClipPlane = 0.01f;
            _captureCam.farClipPlane = CaptureDepthM;
            _captureCam.allowHDR = false;
            _captureCam.allowMSAA = false;
            _captureCam.depth = -101;
            _captureCam.stereoTargetEye = StereoTargetEyeMask.None;
            _captureCam.enabled = false;   // 手で Render する
        }
    }
}
