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
    /// ⚠ 親（[Eyes]）は頭の<b>位置</b>にだけ付いて動く。向きはワールド固定なので、首を振るとメインスクリーンだけが
    /// 面の前を滑り、後ろにあった面が出てくる。
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

        /// <summary>面を描く材質と取り込みの器を組めたか。</summary>
        public bool IsBuilt { get; private set; }

        /// <summary>メインスクリーンの画を取り込んだ累計（画に出た側の観測）。0 のままなら面は 1 枚も映らない。</summary>
        public int CaptureCount { get; private set; }

        /// <summary>いま点いている枚数。</summary>
        public int OpenCount { get; private set; }

        private void Awake() => Build();

        private void OnDestroy()
        {
            Destroy(_surfaceMesh);
            Destroy(_glowMesh);
            Destroy(_surfaceMat);
            Destroy(_glowMat);
            if (_captureCam != null) Destroy(_captureCam.gameObject);
            ReleaseRt(ref _captureRt);
            ReleaseRt(ref _tileRt);
            ReleaseRt(ref _ring);
        }

        private static void ReleaseRt(ref RenderTexture? rt)
        {
            if (rt == null) return;
            rt.Release();
            Destroy(rt);
            rt = null;
        }

        private void Build()
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
            _tileRt = new RenderTexture(new RenderTextureDescriptor(TileW, TileH, fmt, 0))
            { name = "ScreenMultiplyTile", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var ringDesc = new RenderTextureDescriptor(TileW, TileH, fmt, 0)
            {
                dimension = UnityEngine.Rendering.TextureDimension.Tex2DArray,
                volumeDepth = ScreenMultiplyLayout.DelaySlots,
            };
            _ring = new RenderTexture(ringDesc)
            { name = "ScreenMultiplyRing", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
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

        /// <summary>出番の頭。リングを空に戻し、次のフレームで最初の画を取り込む。</summary>
        public void BeginRun()
        {
            _ringHead = 0;
            _ringFilled = 0;
            _sinceCapture = CaptureIntervalSec;
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
            _captureCam.Render();

            RenderTexture? prev = RenderTexture.active;
            Graphics.Blit(_captureRt, _tileRt);
            RenderTexture.active = prev;

            int next = _ringFilled == 0 ? 0 : (_ringHead + 1) % ScreenMultiplyLayout.DelaySlots;
            Graphics.CopyTexture(_tileRt, 0, 0, _ring, next, 0);
            _ringHead = next;
            _ringFilled = Mathf.Min(_ringFilled + 1, ScreenMultiplyLayout.DelaySlots);
            CaptureCount++;
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
