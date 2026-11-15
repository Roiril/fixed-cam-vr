#nullable enable

using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>目の前にメインスクリーンが増える</b>演出の実体（参考 <c>mul.jpg</c>・走馬灯）。
    /// 時計と出番は <see cref="AnomalyEyes"/> が持つ（<see cref="AnomalyEyesLogic"/> ＋ <see cref="EyesCueLogic"/>）。
    /// ここがやるのは 3 つ — メインスクリーンの画を取り込む / 面を組む / 数を材質へ書く。
    ///
    /// 画は本番のメインスクリーンを専用カメラで正面から写したもの。人形・乱れ・外形がそのまま付いて来る。
    /// 取り込みはリングへ順に積み、席ごとに少し前の画を映す（0 番から方位が離れるほど前の画）。
    ///
    /// メインスクリーンは<b>テレビの筐体（<c>_CrtTvCase</c>）ごと</b>写す。面の枠はメインと同じ筐体になる（0321 / 0324）。
    /// 面は自前の枠を描かない。筐体と管の境は、メインと同じ輪郭のマスク（<c>_CrtScreenMaskTex</c>）を面へ渡して決める。
    /// 色味・電源の入り切り・乱れは管の中だけに掛け、筐体は取り込んだままの色で出す。
    ///
    /// ⚠ 親（[Eyes]）は頭の<b>位置</b>にだけ付いて動く。向きはワールド固定なので、首を振るとメインスクリーンだけが
    /// 面の前を滑り、後ろにあった面が出てくる。面は帯ごとに頭を通る鉛直軸のまわりをゆっくり回る
    /// （<see cref="ScreenMultiplyLayout.DriftDeg"/>）。
    ///
    /// 闇には黒い霧がある（<see cref="ScreenFogLogic"/>）。遠い面ほど暗く、ぼけ、光がにじむ。
    /// 点いた面は霧の奥から席まで寄ってくる（<see cref="EmergeM"/> / <see cref="ArriveSec"/>）。
    /// 点いた直後の管は橙で、<see cref="ScreenMultiplyLayout.WarmSettleSec"/> かけて席の寒色へ落ち着く。
    /// 閉じる面はブラウン管の電源断で消える（上下に潰れて横一線 → 点 → 残光が <see cref="ScreenMultiplyLayout.AfterglowSec"/>）。
    /// 床（<see cref="SetFloorWorldY"/>）には面と暈を鏡映した灯が映る（<see cref="FloorReflection"/>）。
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
        /// <summary>取り込みの mip の段数。霧のぼけと床の映り込みのぼけに使う。</summary>
        private const int FeedMips = 5;
        private const float NeverOpened = 1e9f;

        /// <summary>
        /// 床に灯を映すか。⚠ 粗が出る・闇が灰色に寄るなら false にする（最初の霧の版で、にじみを強めて闇が灰色に寄った）。
        /// </summary>
        public const bool FloorReflection = true;
        /// <summary>床に映った灯の明るさ（面に対する比）。</summary>
        public const float MirrorGain = 0.18f;
        /// <summary>床に映った灯のぼけ（取り込みの mip の段を足す数）。</summary>
        public const float MirrorBlurLod = 2f;
        /// <summary>床の水平の揺らぎ（度・秒）。水面に映った灯に見せる。</summary>
        public const float MirrorSwayDeg = 0.4f;
        public const float MirrorSwaySec = 5f;
        /// <summary>頭からの水平距離がここまでで映り込みが消える（m）。</summary>
        public const float MirrorFadeM = 6f;

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
        private static readonly int ClosedAtId = Shader.PropertyToID("_WallClosedAt");
        private static readonly int AfterglowSecId = Shader.PropertyToID("_WallAfterglowSec");
        private static readonly int ArriveSecId = Shader.PropertyToID("_WallArriveSec");
        private static readonly int EmergeId = Shader.PropertyToID("_WallEmergeM");
        private static readonly int FogDensityId = Shader.PropertyToID("_WallFogDensity");
        private static readonly int FogClearId = Shader.PropertyToID("_WallFogClearM");
        private static readonly int FogBlurId = Shader.PropertyToID("_WallFogBlur");
        private static readonly int FarShowId = Shader.PropertyToID("_WallFarShow");
        private static readonly int WarmId = Shader.PropertyToID("_WallWarm");
        private static readonly int DriftId = Shader.PropertyToID("_WallDrift");
        private static readonly int MirrorId = Shader.PropertyToID("_WallMirror");
        private static readonly int FloorYId = Shader.PropertyToID("_WallFloorY");
        private static readonly int MirrorLookId = Shader.PropertyToID("_WallMirrorLook");
        private static readonly int MirrorFadeId = Shader.PropertyToID("_WallMirrorFadeM");
        private static readonly int ScreenMaskId = Shader.PropertyToID("_CrtScreenMaskTex");
        private static readonly int CrtReferenceId = Shader.PropertyToID("_CrtReference");
        private static readonly int TubeMaskId = Shader.PropertyToID("_WallTubeMask");
        private static readonly int HasTubeMaskId = Shader.PropertyToID("_WallHasTubeMask");
        private static readonly int SlotsId = Shader.PropertyToID("_WallSlots");
        private static readonly int SlotCountId = Shader.PropertyToID("_WallSlotCount");
        private static readonly int ShuffleId = Shader.PropertyToID("_WallShuffle");
        private static readonly int FaceTexId = Shader.PropertyToID("_WallFaceTex");
        private static readonly int FaceId = Shader.PropertyToID("_WallFace");

        // ── 撮りためた静止画（canon/LEDGER.md 0384 ⑤）─────────────────────────
        // 「そこに至るまでに10枚ほど撮影をして起き、それをランダムに並べてすべてのテレビの中に画像として入れて」
        // 「Cの区間だけでなく、A,Bともに」。出番の前からメインスクリーンを間隔を空けて撮り、
        // カメラ（区間）ごとに新しい順で SnapshotPerCamera 枚ずつ残す。面は席ごとに乱数でどれか 1 枚を映す。

        /// <summary>区間（カメラ）ごとに残す静止画の枚数。A・B・C で合わせて <see cref="SnapshotSlots"/> 枚。</summary>
        public const int SnapshotPerCamera = 4;
        public const int SnapshotCameras = 3;
        public const int SnapshotSlots = SnapshotPerCamera * SnapshotCameras;
        /// <summary>撮りためる間隔（秒）。1 区間に十数秒いるので、1 回の滞在から 1〜3 枚撮れる。</summary>
        public const float SnapshotIntervalSec = 4f;
        /// <summary>
        /// 画の中央の平均の明るさ（sRGB 0..1）がこれより暗い静止画は捨てる。切替の暗転・信号断の黒が
        /// 面に並ぶと「消えたテレビ」に見えるため。
        /// </summary>
        public const float SnapshotMinLuma = 0.06f;
        /// <summary>確かめる前の静止画を一時的に置くリングの段（静止画の段より後ろ）。</summary>
        private const int StagingSlot = ScreenMultiplyLayout.DelaySlots - 1;

        // ── 最後に残る面の笑う人形（canon/LEDGER.md 0384 ④）──────────────────
        // 「一番最後まで閉じないで残っているスクリーン(メインスクリーンじゃないやつ)の中に、最後に、
        //  笑ってる日本人形の顔面をドアップにしたものを合成して、笑った音もつけてほしい。」
        // 最後まで残るのは 0 番（大きい目の席）。ほかが閉じ切ったら 0 番を開き直して顔を映し、
        // 2.4 秒保ってから電源を落とす。笑い声は SoundCue.ScreenFaceLaugh（1.1 秒）を顔へ移った縁で 1 回。

        public const string FaceTextureResource = "ScreenMultiply/DollLaughFace";
        /// <summary>顔を保つ秒（0384 のとき笑い声 2.4 秒に合わせた尺。音源を替えても画は変えていない）。</summary>
        public const float FaceHoldSec = 2.4f;
        /// <summary>顔へ移る秒（面が開き直す秒も同じ）。</summary>
        public const float FaceInSec = 0.25f;
        /// <summary>保ち終えてから電源が落ちきるまでの秒。</summary>
        public const float FaceCloseSec = 0.6f;

        // 面と暈。[0] がそのもの、[1] が床に映した分。
        private Mesh? _surfaceMesh, _glowMesh;
        private readonly Material?[] _surfaceMats = new Material?[2];
        private readonly Material?[] _glowMats = new Material?[2];
        private readonly MeshRenderer?[] _surfaceRenderers = new MeshRenderer?[2];
        private readonly MeshRenderer?[] _glowRenderers = new MeshRenderer?[2];
        private Camera? _captureCam;
        private RenderTexture? _captureRt, _tileRt, _ring;
        private MjpegScreen? _screen;
        private Renderer? _screenRenderer;
        private int _ringHead;
        private int _ringFilled;
        private float _sinceCapture;
        private readonly float[] _openAt = new float[ScreenMultiplyLayout.MaxSeats];
        private readonly float[] _closedAt = new float[ScreenMultiplyLayout.MaxSeats];
        // シェーダへは 4 席ずつ詰めて渡す（float の配列は 1 要素ごとに float4 の場所を取る）。
        private readonly Vector4[] _openAtPacked = new Vector4[ScreenMultiplyLayout.MaxSeats / 4];
        private readonly Vector4[] _closedAtPacked = new Vector4[ScreenMultiplyLayout.MaxSeats / 4];
        private float _now;
        private float _floorWorldY;

        // 撮りためた静止画。段 i（0..SnapshotSlots-1）はカメラ i / SnapshotPerCamera のもの。
        private readonly bool[] _slotFilled = new bool[SnapshotSlots];
        private readonly int[] _cameraCursor = new int[SnapshotCameras];
        private readonly Vector4[] _slotsPacked = new Vector4[4];
        private readonly float[] _slotList = new float[16];
        private float _sinceSnapshot = SnapshotIntervalSec;
        private int _snapshotGen;
        private int _pendingSlot = -1;
        private float _shuffle;

        // 最後に残る面の顔。
        private Texture2D? _faceTex;
        private float _faceAt = NeverOpened;
        private float _faceBig;

        /// <summary>面を描く材質と取り込みの器を組めたか。</summary>
        public bool IsBuilt { get; private set; }

        /// <summary>メインスクリーンの画を取り込んだ累計（画に出た側の観測）。0 のままなら面は 1 枚も映らない。</summary>
        public int CaptureCount { get; private set; }

        /// <summary>いま点いている枚数。</summary>
        public int OpenCount { get; private set; }

        /// <summary>電源が切れた後の残光が残っている枚数。</summary>
        public int AfterglowCount { get; private set; }

        /// <summary>席へ寄っている途中の枚数（画に出た側の観測）。点いた直後は 1 以上、落ち着けば 0。</summary>
        public int ArrivingCount { get; private set; }

        /// <summary>出番の中で、席より奥に面があった最大の距離（m）。0 のままなら奥から寄る動きは出ていない。</summary>
        public float MaxEmergeSeenM { get; private set; }

        /// <summary>出番の頭からの秒（帯の回転と点いた時刻の時計）。</summary>
        public float RunSec => _now;

        /// <summary>いま面へ配れる静止画の枚数（0..<see cref="SnapshotSlots"/>）。0 なら従来の遅れた取り込みで映す。</summary>
        public int SnapshotCount { get; private set; }
        /// <summary>静止画として採った累計（体験者ごとに 0 へ戻る）。</summary>
        public int SnapshotTakenCount { get; private set; }
        /// <summary>暗すぎて捨てた累計。</summary>
        public int SnapshotDarkCount { get; private set; }
        /// <summary>カメラ（A・B・C）ごとの残っている枚数。</summary>
        public int SnapshotCountOf(int camera)
        {
            if (camera < 0 || camera >= SnapshotCameras) return 0;
            int n = 0;
            for (int k = 0; k < SnapshotPerCamera; k++) if (_slotFilled[camera * SnapshotPerCamera + k]) n++;
            return n;
        }

        /// <summary>笑う人形の顔の素材を読めたか。読めなければ顔は出ない（警告済み）。</summary>
        public bool FaceTextureLoaded => _faceTex != null;
        /// <summary>このフレームで 0 番の面が顔へ移り始めた（笑い声を鳴らす合図）。</summary>
        public bool FaceJustStarted { get; private set; }
        /// <summary>顔を保っている〜電源が落ちきるまで true。<see cref="AnomalyEyes"/> は目の時計が止まってもこの間は面を回す。</summary>
        public bool FaceHolding { get; private set; }
        /// <summary>材質へ書いた顔の量 0..1（画に出た側の観測）。</summary>
        public float FaceApplied { get; private set; }
        /// <summary>顔が出た累計。</summary>
        public int FaceShownCount { get; private set; }

        private void Awake() => Build();

        private void OnDestroy()
        {
            Kill(_surfaceMesh);
            Kill(_glowMesh);
            foreach (Material? m in _surfaceMats) Kill(m);
            foreach (Material? m in _glowMats) Kill(m);
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
            for (int k = 0; k < 2; k++)
            {
                string suffix = k == 0 ? "" : " floor";
                var sm = new Material(surface) { name = $"ScreenMultiply{suffix} (runtime)" };
                var gm = new Material(glow) { name = $"ScreenMultiplyGlow{suffix} (runtime)" };
                sm.SetTexture(FeedTexId, _ring);
                sm.SetFloat(RingCountId, ScreenMultiplyLayout.DelaySlots);
                sm.SetFloat(DesatId, Desaturate);
                sm.SetFloat(GainId, SurfaceGain);
                sm.SetFloat(BodyGainId, BodyGain);
                gm.SetFloat(GlowGainId, GlowGain);
                foreach (Material m in new[] { sm, gm })
                {
                    Color w = ScreenMultiplyLayout.WarmTint;
                    m.SetVector(WarmId, new Vector4(w.r, w.g, w.b, ScreenMultiplyLayout.WarmSettleSec));
                    m.SetFloat(AfterglowSecId, ScreenMultiplyLayout.AfterglowSec);
                    m.SetFloat(MirrorId, k);
                    m.SetVector(MirrorLookId, new Vector4(MirrorGain, MirrorBlurLod, MirrorSwayDeg * Mathf.Deg2Rad,
                                                          MirrorSwaySec));
                    m.SetFloat(MirrorFadeId, MirrorFadeM);
                    m.SetFloat(FloorYId, -1.6f);
                }
                _surfaceMats[k] = sm;
                _glowMats[k] = gm;
            }
            _faceTex = Resources.Load<Texture2D>(FaceTextureResource);
            if (_faceTex == null)
                Debug.LogWarning($"[ScreenMultiply] 笑う人形の顔 Resources/{FaceTextureResource} が見つかりません。最後の面に顔は出ません。");
            for (int k = 0; k < 2; k++)
            {
                _surfaceMats[k]!.SetTexture(FaceTexId, _faceTex != null ? _faceTex : Texture2D.blackTexture);
                _surfaceMats[k]!.SetFloat(FaceId, 0f);
                _surfaceMats[k]!.SetFloat(SlotCountId, 0f);
            }
            SetLook(ScreenFogLogic.Density, EmergeM, showFar: true);
            ResetOpenTimes();

            _surfaceRenderers[0] = AddPart("ScreenMultiplySurfaces", _surfaceMesh, _surfaceMats[0]!);
            _glowRenderers[0] = AddPart("ScreenMultiplyGlow", _glowMesh, _glowMats[0]!);
            _surfaceRenderers[1] = AddPart("ScreenMultiplySurfacesFloor", _surfaceMesh, _surfaceMats[1]!);
            _glowRenderers[1] = AddPart("ScreenMultiplyGlowFloor", _glowMesh, _glowMats[1]!);
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
            for (int k = 0; k < 2; k++)
            {
                WriteLook(_surfaceMats[k], fogDensity, emergeM, showFar);
                WriteLook(_glowMats[k], fogDensity, emergeM, showFar);
            }
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

        /// <summary>
        /// 床のワールドの高さ。灯はこの面に映る。本番は <see cref="AnomalyEyes"/> が部屋の床（course の床）から毎フレーム書く。
        /// </summary>
        public void SetFloorWorldY(float worldY) => _floorWorldY = worldY;

        /// <summary>
        /// 席 <paramref name="index"/> のいまの中心（群れの座標）。帯の回転を当てたもの。
        /// 0 番の方向（群れの向き直し）と、最後に点いた席の方角（音）に使う。
        /// </summary>
        public Vector3 SeatCenter(int index)
            => ScreenMultiplyLayout.DriftedCenter(ScreenMultiplyLayout.Seats[index], _now);

        private void ResetOpenTimes()
        {
            for (int i = 0; i < _openAt.Length; i++)
            {
                _openAt[i] = NeverOpened;
                _closedAt[i] = NeverOpened;
            }
            _now = 0f;
            ArrivingCount = 0;
            AfterglowCount = 0;
            MaxEmergeSeenM = 0f;
            ResetFace();
        }

        private void ResetFace()
        {
            _faceAt = NeverOpened;
            _faceBig = 0f;
            FaceJustStarted = false;
            FaceHolding = false;
            FaceApplied = 0f;
            foreach (Material? m in _surfaceMats) m?.SetFloat(FaceId, 0f);
        }

        /// <summary>
        /// 撮りためた静止画を捨てる（体験者の交代・中止）。前の体験者の部屋が次の人のテレビに映らないように。
        /// </summary>
        public void ClearSnapshots()
        {
            for (int i = 0; i < _slotFilled.Length; i++) _slotFilled[i] = false;
            for (int c = 0; c < _cameraCursor.Length; c++) _cameraCursor[c] = 0;
            SnapshotTakenCount = 0;
            SnapshotDarkCount = 0;
            _sinceSnapshot = SnapshotIntervalSec;
            _snapshotGen++;
            _pendingSlot = -1;
            RepackSlots();
        }

        /// <summary>
        /// 出番の外で毎フレーム。<see cref="SnapshotIntervalSec"/> ごとにメインスクリーンを 1 枚撮り、
        /// いま居る区間（<paramref name="camera"/> 0..2）の静止画として残す（0384 ⑤）。区間が分からなければ撮らない。
        /// </summary>
        public void TickSnapshots(float dt, int camera)
        {
            if (!IsBuilt || camera < 0 || camera >= SnapshotCameras) return;
            _sinceSnapshot += dt;
            if (_sinceSnapshot < SnapshotIntervalSec || _pendingSlot >= 0) return;
            if (!RenderTile()) return;
            _sinceSnapshot = 0f;
            int slot = camera * SnapshotPerCamera + _cameraCursor[camera];
            // 確かめる前は控えの段へ置く。暗い画で残っている良い画を潰さない。
            for (int mip = 0; mip < FeedMips; mip++) Graphics.CopyTexture(_tileRt, 0, mip, _ring, StagingSlot, mip);
            if (!SystemInfo.supportsAsyncGPUReadback) { CommitSnapshot(slot, camera); return; }
            int gen = _snapshotGen;
            _pendingSlot = slot;
            UnityEngine.Rendering.AsyncGPUReadback.Request(_tileRt, FeedMips - 1, TextureFormat.RGBA32, req =>
            {
                if (this == null || gen != _snapshotGen) return;
                _pendingSlot = -1;
                if (!req.hasError
                    && SnapshotLuma(req.GetData<Color32>(), req.width, req.height) < SnapshotMinLuma)
                {
                    SnapshotDarkCount++;
                    return;
                }
                CommitSnapshot(slot, camera);
            });
        }

        /// <summary>小さい mip の中央半分の平均の明るさ（sRGB 0..1）。外枠（筐体）を数えない。</summary>
        public static float SnapshotLuma(Unity.Collections.NativeArray<Color32> px, int w, int h)
        {
            if (w <= 0 || h <= 0 || px.Length < w * h) return 1f;
            float sum = 0f;
            int n = 0;
            for (int y = h / 4; y < h - h / 4; y++)
            for (int x = w / 4; x < w - w / 4; x++)
            {
                Color32 c = px[y * w + x];
                sum += (0.30f * c.r + 0.59f * c.g + 0.11f * c.b) / 255f;
                n++;
            }
            return n > 0 ? sum / n : 1f;
        }

        private void CommitSnapshot(int slot, int camera)
        {
            if (_ring == null) return;
            for (int mip = 0; mip < FeedMips; mip++) Graphics.CopyTexture(_ring, StagingSlot, mip, _ring, slot, mip);
            _slotFilled[slot] = true;
            _cameraCursor[camera] = (_cameraCursor[camera] + 1) % SnapshotPerCamera;
            SnapshotTakenCount++;
            RepackSlots();
        }

        private void RepackSlots()
        {
            int n = 0;
            for (int i = 0; i < _slotList.Length; i++) _slotList[i] = 0f;
            for (int i = 0; i < SnapshotSlots; i++) if (_slotFilled[i]) _slotList[n++] = i;
            for (int g = 0; g < _slotsPacked.Length; g++)
                _slotsPacked[g] = new Vector4(_slotList[g * 4], _slotList[g * 4 + 1], _slotList[g * 4 + 2], _slotList[g * 4 + 3]);
            SnapshotCount = n;
            foreach (Material? m in _surfaceMats)
            {
                if (m == null) continue;
                m.SetVectorArray(SlotsId, _slotsPacked);
                m.SetFloat(SlotCountId, n);
            }
        }

        /// <summary>出番の頭。リングを空に戻し、次のフレームで最初の画を取り込む。</summary>
        public void BeginRun()
        {
            _ringHead = 0;
            _ringFilled = 0;
            _sinceCapture = CaptureIntervalSec;
            ResetOpenTimes();
            // 並べ方は出番ごとに変える（同じ静止画が毎回同じ席に来ない）。
            _shuffle = Random.Range(0f, 1000f);
            foreach (Material? m in _surfaceMats) m?.SetFloat(ShuffleId, _shuffle);
        }

        /// <summary>1 フレーム分。出番の間（兆しの闇も含む）だけ呼ぶ。</summary>
        /// <summary>
        /// <b>このフレームで消えた面</b>の番号（消えた面が無ければ -1）。消える音を
        /// その面の方角から鳴らすため（<c>canon/LEDGER.md</c> 0383）。同じフレームに複数消えたら最後の 1 枚。
        /// </summary>
        public int NewestClosedSeat { get; private set; } = -1;

        public void Drive(float dt, float big, float field, float span, float closing, float clock)
        {
            NewestClosedSeat = -1;
            FaceJustStarted = false;
            if (!IsBuilt || _surfaceMats[0] == null) return;

            // 撮りためた静止画があればそれを配る（0384 ⑤）。無いとき（ツインで途中から始めた等）だけ、
            // 従来どおりメインを取り込み続け、席ごとに少し前の画を映す。
            // 閉じ始めたら取り込みを止める。閉じの段は区間の終わり（録画が尽きる暗転）と重なり、
            // 止めないとメインの暗転がそのまま面へ写って、電源が切れて見える前に全部が黒くなる。
            _sinceCapture += dt;
            if (SnapshotCount == 0 && _sinceCapture >= CaptureIntervalSec && closing < 0.5f)
            {
                _sinceCapture = 0f;
                CaptureMainScreen();
            }

            // 席ごとの「点いた時刻」と「消えた時刻」。奥から寄る動き・橙からの落ち着き・残光はここから数える
            // （目の時計は途中で止まるので使わない）。
            _now += dt;

            // 最後に残る 0 番の面（0384 ④）。ほかが閉じ切ったら開き直して顔を映し、笑い声 1 回ぶん保ってから落とす。
            if (_faceTex != null && _faceAt >= NeverOpened && closing > 0.5f && field <= 0.001f && big > 0.02f)
            {
                _faceAt = _now;
                _faceBig = big;
                FaceJustStarted = true;
                FaceShownCount++;
            }
            float face = 0f;
            if (_faceAt < NeverOpened)
            {
                float t = _now - _faceAt;
                face = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / FaceInSec));
                big = t < FaceHoldSec
                    ? Mathf.Lerp(_faceBig, 1f, face)
                    : Mathf.Clamp01(1f - (t - FaceHoldSec) / FaceCloseSec);
                FaceHolding = t < FaceHoldSec + FaceCloseSec;
            }
            FaceApplied = face;
            for (int k = 0; k < 2; k++) _surfaceMats[k]!.SetFloat(FaceId, face);

            int seats = Mathf.Min(ScreenMultiplyLayout.Seats.Length, _openAt.Length);
            int arriving = 0, afterglow = 0;
            for (int i = 0; i < seats; i++)
            {
                bool lit = ScreenMultiplyLayout.SeatOpen(i, big, field, span) > 0.001f;
                if (!lit)
                {
                    if (_openAt[i] < NeverOpened && _closedAt[i] >= NeverOpened)
                    {
                        _closedAt[i] = _now;
                        NewestClosedSeat = i;
                    }
                    if (_closedAt[i] < NeverOpened && _now - _closedAt[i] < ScreenMultiplyLayout.AfterglowSec)
                    {
                        afterglow++;
                        continue;
                    }
                    _openAt[i] = NeverOpened;
                    _closedAt[i] = NeverOpened;
                    continue;
                }
                if (_closedAt[i] < NeverOpened) { _openAt[i] = NeverOpened; _closedAt[i] = NeverOpened; }
                if (_openAt[i] >= NeverOpened) _openAt[i] = _now;
                float a = (_now - _openAt[i]) / ArriveSec;
                if (a >= 1f) continue;
                arriving++;
                float left = (1f - a) * (1f - a) * (1f - a) * EmergeM;
                if (left > MaxEmergeSeenM) MaxEmergeSeenM = left;
            }
            ArrivingCount = arriving;
            AfterglowCount = afterglow;
            Pack(_openAt, _openAtPacked);
            Pack(_closedAt, _closedAtPacked);

            var drift = new Vector4(
                ScreenMultiplyLayout.DriftDeg(ScreenMultiplyLayout.BandEye, _now) * Mathf.Deg2Rad,
                ScreenMultiplyLayout.DriftDeg(ScreenMultiplyLayout.BandUpper, _now) * Mathf.Deg2Rad,
                ScreenMultiplyLayout.DriftDeg(ScreenMultiplyLayout.BandTop, _now) * Mathf.Deg2Rad,
                ScreenMultiplyLayout.DriftDeg(ScreenMultiplyLayout.BandFar, _now) * Mathf.Deg2Rad);
            // 床は群れの座標で渡す（親は頭の位置にあり、向きは鉛直軸まわりだけ）。
            float floorLocalY = transform.InverseTransformPoint(new Vector3(transform.position.x, _floorWorldY,
                                                                            transform.position.z)).y;

            for (int k = 0; k < 2; k++)
            {
                WriteFrame(_surfaceMats[k], drift, floorLocalY, big, field, span, clock);
                WriteFrame(_glowMats[k], drift, floorLocalY, big, field, span, clock);
                Material sm = _surfaceMats[k]!;
                sm.SetFloat(ClosingId, closing);
                sm.SetFloat(RingHeadId, _ringHead);
                sm.SetFloat(RingFilledId, _ringFilled);
            }

            OpenCount = ScreenMultiplyLayout.CountOpen(big, field);
            // 最初の画が入るまでは出さない（リングの中身は前の体験者の画）。
            bool show = (_ringFilled > 0 || SnapshotCount > 0) && (OpenCount > 0 || afterglow > 0);
            for (int k = 0; k < 2; k++)
            {
                bool on = show && (k == 0 || FloorReflection);
                if (_surfaceRenderers[k] != null) _surfaceRenderers[k]!.enabled = on;
                if (_glowRenderers[k] != null) _glowRenderers[k]!.enabled = on;
            }
        }

        private void WriteFrame(Material? m, Vector4 drift, float floorLocalY, float big, float field, float span,
                                float clock)
        {
            if (m == null) return;
            m.SetVectorArray(OpenAtId, _openAtPacked);
            m.SetVectorArray(ClosedAtId, _closedAtPacked);
            m.SetFloat(NowId, _now);
            m.SetVector(DriftId, drift);
            m.SetFloat(FloorYId, floorLocalY);
            m.SetFloat(BigId, big);
            m.SetFloat(FieldId, field);
            m.SetFloat(SpanId, span);
            m.SetFloat(TimeId, clock);
        }

        private static void Pack(float[] src, Vector4[] dst)
        {
            for (int i = 0; i < dst.Length; i++)
                dst[i] = new Vector4(src[i * 4], src[i * 4 + 1], src[i * 4 + 2], src[i * 4 + 3]);
        }

        public void Hide()
        {
            OpenCount = 0;
            AfterglowCount = 0;
            ResetFace();
            foreach (MeshRenderer? r in _surfaceRenderers) if (r != null) r.enabled = false;
            foreach (MeshRenderer? r in _glowRenderers) if (r != null) r.enabled = false;
        }

        private void CaptureMainScreen()
        {
            if (!RenderTile()) return;
            int next = _ringFilled == 0 ? 0 : (_ringHead + 1) % ScreenMultiplyLayout.DelaySlots;
            for (int mip = 0; mip < FeedMips; mip++) Graphics.CopyTexture(_tileRt, 0, mip, _ring, next, mip);
            _ringHead = next;
            _ringFilled = Mathf.Min(_ringFilled + 1, ScreenMultiplyLayout.DelaySlots);
            CaptureCount++;
        }

        /// <summary>メインスクリーンを正面から 1 枚写して <c>_tileRt</c>（mip つき）に置く。写せなければ false。</summary>
        private bool RenderTile()
        {
            if (_captureRt == null || _tileRt == null || _ring == null) return false;
            if (_screenRenderer == null)
            {
                _screen = FindObjectOfType<MjpegScreen>();
                if (_screen != null) _screenRenderer = _screen.GetComponent<Renderer>();
                if (_screenRenderer == null) return false;
            }
            EnsureCamera();
            if (_captureCam == null) return false;

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
            return true;
        }

        /// <summary>
        /// メインスクリーンの管の輪郭を面へ渡す。参照の筐体つきの画面だけ（<c>_CrtReference</c> = 1）。
        /// 筐体の無い画面は取り込みの alpha がそのまま管の外形になる（材質が輪郭の外を切る・alpha 0 の背景）。
        /// </summary>
        private void BindTubeMask(Material? screenMaterial)
        {
            Texture? mask = TubeMaskOf(screenMaterial);
            foreach (Material? m in _surfaceMats)
            {
                if (m == null) continue;
                m.SetTexture(TubeMaskId, mask != null ? mask : Texture2D.whiteTexture);
                m.SetFloat(HasTubeMaskId, mask != null ? 1f : 0f);
            }
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
