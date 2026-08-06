#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming.Cg
{
    /// <summary>
    /// 映像の上に立つ **CG 人形**を描く層（<c>ScreenComposite</c> の 3 層目 <c>_CgTex</c>）。
    ///
    /// 要点は「**実カメラの双子の仮想カメラ**で描く」こと。固定カメラの course 空間姿勢
    /// （<c>cameras[i].pose</c>・卓のフロアマップで著作）と同じ位置・向き・画角に Unity カメラを構え、
    /// CG レイヤだけを透明背景の RenderTexture へ描く。こうしないと人形は「貼り付けた絵」にしかならない。
    ///
    /// **像空間の対応付けが命**（2026-07-27 に作り直した部分）:
    ///   - RT のアスペクト・解像度は **ソース映像の実寸**に合わせる（スクリーン枠ではない）
    ///   - 画角は **水平** FOV（<c>hfovDeg</c>）を正とし、映像アスペクトから垂直 FOV へ変換して Unity に渡す
    ///   - 合成 UV はライブ映像と同じ contain-fit 枠（<c>_CgScale</c> = <see cref="MjpegScreen.ContainScale"/>）
    /// 旧実装はこの 3 つが全部ずれており、姿勢を完璧に測っても人形は合わなかった。
    /// 設計の正本: <c>.claude/plans/2026-07-27_cg-compositing-rebuild.md</c>。
    ///
    /// course 空間 → ワールドの変換は <c>CourseFrame</c>（HMD 位置合わせ）が持つので、
    /// <see cref="ShowControlClient.CourseToWorldProvider"/> / <see cref="ShowControlClient.CourseYawProvider"/>
    /// を通して引く（Streaming→Tracking の型参照を作らない既存規約）。
    /// **transform の親子付けでは駄目**（CourseFrame は transform を動かさず originXZ/yawDeg を数値で持つ）。
    ///
    /// **出さない条件**（当てずっぽうで出すより出さない方が体験を壊さない）:
    ///   - カメラ姿勢が未著作
    ///   - **HMD 位置合わせ（登録）が未完了** — course→world が identity へ落ちて全く違う場所に立つ
    /// 腕の駆動は <see cref="ShowActorRig"/>（体験者のハンドトラッキング）。
    ///
    /// **接地（2026-07-27 追加）**: 人形だけを描くと必ず「浮いて」見えるので、同じ RT へ
    ///   - 床への平面投影シャドウ（<c>FixedCamVr/ShowShadowProjector</c> を人形の Renderer に足す）
    ///   - 足元の接地影 blob（<c>FixedCamVr/ShowGroundBlob</c> の Quad 1 枚）
    ///   - 部屋プロキシによるオクルージョン（<see cref="ShowRoomProxy"/>）
    /// を重ねる。影は「rgb=0 / a=濃さ」の premultiplied 断片なので、合成側の over が自動的に乗算になる。
    /// **<c>layout.room</c> が未著作でも人形と影は出す**（床は course y=0 の無限平面）。
    /// プロキシに依存するのはオクルージョンだけ — ここを止めるとフェイルソフトが壊れる。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShowCgLayer : MonoBehaviour
    {
        private static readonly int CgTexId = Shader.PropertyToID("_CgTex");
        private static readonly int CgStrengthId = Shader.PropertyToID("_CgStrength");
        private static readonly int CgScaleId = Shader.PropertyToID("_CgScale");
        private static readonly int CgLensId = Shader.PropertyToID("_CgLens");
        private static readonly int CgFocalId = Shader.PropertyToID("_CgFocalN");
        private static readonly int CgSoftenId = Shader.PropertyToID("_CgSoften");
        private static readonly int LightDirId = Shader.PropertyToID("_LightDir");
        private static readonly int ShadowPlaneYId = Shader.PropertyToID("_ShadowPlaneY");
        private static readonly int ShadowLightDirId = Shader.PropertyToID("_ShadowLightDir");
        private static readonly int ShadowDensityId = Shader.PropertyToID("_ShadowDensity");
        private static readonly int BlobDensityId = Shader.PropertyToID("_BlobDensity");
        private static readonly int BlobFeatherId = Shader.PropertyToID("_BlobFeather");
        private static readonly int LightColorId = Shader.PropertyToID("_LightColor");
        private static readonly int AmbientId = Shader.PropertyToID("_Ambient");

        /// <summary>身体入力がこの秒数届かなければ「手は取れていない」とみなす。</summary>
        private const float BodyInputTimeoutSec = 0.5f;

        /// <summary>
        /// 映像の遅延 (秒)。**スクリーンに映っているのは撮影からこの秒数だけ前の姿**なので、
        /// 人形も同じ時刻の体験者を写さないと、歩いているあいだずっと先行してずれる（1m/s なら 15cm）。
        /// 現状は固定値。`CameraStream.EstimatedLatencyMs` は「Unity 受信からテクスチャ反映まで」しか
        /// 測れておらず（撮影・エンコード・伝送を含まない）、そのまま足すと過小補償になるので使わない。
        /// 現場で値の調整が要ると分かったら show.json の control へ出す。
        /// </summary>
        private const float VideoLatencySec = 0.15f;

        // 影 / 接地影のマテリアル。**Resources に置くのは build で剥がされないため** —
        // どのアセットからも参照されないシェーダはビルドから除去され、実行時 Shader.Find が null を返す。
        private const string ShadowMaterialResource = "ShowCg/ShowShadowProjector";
        private const string ShadowShaderName = "FixedCamVr/ShowShadowProjector";
        private const string BlobMaterialResource = "ShowCg/ShowGroundBlob";
        private const string BlobShaderName = "FixedCamVr/ShowGroundBlob";

        /// <summary><c>layout.room.light</c> が無いときの既定（**スキーマ既定値と同じ**にする）。</summary>
        private const float DefaultShadowDensity = 0.55f;
        private const float DefaultLightTempK = 4000f;
        private const float DefaultAmbient = 0.35f;
        private const float DefaultShadowSoftM = 0.12f;

        /// <summary>接地影の半径 = 身長 × これ（成人 1.6m で約 0.35m。人の影の広がりの実感値）。</summary>
        private const float BlobRadiusPerHeight = 0.22f;
        private const float BlobRadiusMinM = 0.15f;
        private const float BlobRadiusMaxM = 0.6f;

        /// <summary>接地影を床から浮かせる量 (m)。投影シャドウ（+0.002）より上に置く。</summary>
        private const float BlobLiftM = 0.004f;

        /// <summary>
        /// CG を実映像の粗さへ寄せるぼかし量（テクセル）。RT を映像実寸まで落としたうえで、さらにこれを掛ける。
        /// **SerializeField にしない** — 既存シーン / prefab の YAML に無いフィールドは型の default（0）で
        /// 読まれ、意図せず無効化される（このプロジェクトで何度も踏んでいる罠）。現場調整の対象でもない。
        /// </summary>
        private const float CgSoftenTexels = 0.7f;

        /// <summary>ソース実寸が取れないときに仮定するアスペクト（streamer / IP Camera Lite とも 4:3）。</summary>
        private const float FallbackSourceAspect = 4f / 3f;

        /// <summary>姿勢に画角が書かれていないときの**水平**画角。広角スマホの実測レンジの中央値あたり。</summary>
        private const float DefaultHfovDeg = 70f;

        // 光源の既定（course 空間）。show.json の layout.room.light が無い間はこれを使う。
        private const float DefaultLightYawDeg = 30f;
        private const float DefaultLightPitchDeg = 55f;

        [Tooltip("CG 人形だけを置くレイヤ名。仮想カメラはこのレイヤだけを描く。" +
                 "プロジェクトに未定義なら CG は出さない（フェイルソフト）。")]
        [SerializeField] private string cgLayerName = "ShowCg";

        [Tooltip("CG レイヤの RenderTexture の縦解像度の**上限**。実際はソース映像の高さと min を取る " +
                 "— 映像より鮮明な CG は『貼り付けた絵』に見えるので、わざと同じ粗さまで落とす。")]
        [SerializeField, Min(64)] private int renderHeightPx = 720;

        [Tooltip("スクリーン（ScreenComposite マテリアル）を持つ Renderer。null なら同 GameObject から取る。")]
        [SerializeField] private Renderer? screenRenderer;

        [Tooltip("show.json の actors / カメラ姿勢の供給元。null ならシーンから探す。")]
        [SerializeField] private ShowControlClient? showControl;

        [Tooltip("較正の照合（/info の lensId）に使う受信レジストリ。null ならシーンから探す。")]
        [SerializeField] private CameraStreamRegistry? registry;

        private Material? _material;
        private MjpegScreen? _screen;
        private Camera? _virtualCam;
        private RenderTexture? _rt;
        private GameObject? _actorInstance;
        private ShowActorRig? _actorRig;
        private Renderer[] _actorRenderers = System.Array.Empty<Renderer>();
        private MaterialPropertyBlock? _mpb;
        private string _actorId = "";
        /// <summary>プレハブが見つからず代用のカプセルを出しているか（大きさの掛け方が違う）。</summary>
        private bool _actorIsFallbackCapsule;
        private int _layer = -1;
        private bool _visible;
        private bool _rendering;
        private bool _warnedUnregistered;
        private string _mode = TakeSchema.CgFollow;
        private ShowActorDef? _actorDef;
        private ShowCameraPoseDef? _pose;
        private ShowPlacementDef? _placement;
        private ShowCameraCalibDef? _calibRaw;   // カメラ index から引いた較正（ソース照合はフレーム毎）
        private int _cameraIndex = -1;
        private bool _warnedCalibMismatch;
        private bool _projectionOverridden;

        // 接地（影 / 接地影 / 部屋プロキシ）。すべて実行時生成で、シーン・prefab には現れない。
        private ShowRoomProxy? _roomProxy;
        private Material? _shadowMat;
        private Material? _blobMat;
        private Transform? _blob;
        private bool _warnedShadowMissing;
        private bool _warnedBlobMissing;
        private bool _warnedMultiSubMesh;

        private ShowBodyInput _body;
        private float _bodyStamp = -999f;
        // 体験者の「体」の向き。頭の向きを鈍らせたもの（人は首を振っても体はすぐ回らない）。
        // 人形の向きと、腕の相対ベクトルを正規化する向きの**両方**にこれを使う。
        private float _bodyYawDeg;
        private bool _bodyYawSeeded;
        // 映像の遅延ぶん過去を読むための履歴（歩行中のズレを消す）。
        private readonly BodyInputHistory _bodyHistory = new BodyInputHistory();
        // 立ち位置も腕・向きと同じ時刻（映像の遅延ぶん過去）を読むための足跡。
        private readonly CourseTrack _courseTrack = new CourseTrack();

        /// <summary>いま CG を出しているか（HUD / 診断用）。</summary>
        public bool IsVisible => _visible && _rendering;

        /// <summary>
        /// 身体入力（ハンドトラッキング）が要るか。**人形を出していない間は false** なので、
        /// 供給側（OvrHandTrackingBridge）は毎フレームの GetHandState を丸ごと省ける。
        /// </summary>
        public bool WantsBody => _visible;

        /// <summary>体験者の頭・手のワールド姿勢を受け取る（Assembly-CSharp の OVR 橋渡しから push）。</summary>
        public void SetBodyInput(in ShowBodyInput body)
        {
            _body = body;
            _bodyStamp = Time.unscaledTime;
            _bodyHistory.Push(_bodyStamp, body);
        }

        private void Awake()
        {
            if (screenRenderer == null) screenRenderer = GetComponent<Renderer>();
            _material = screenRenderer != null ? screenRenderer.material : null;
            _screen = screenRenderer != null ? screenRenderer.GetComponent<MjpegScreen>() : null;
            if (showControl == null) showControl = FindObjectOfType<ShowControlClient>();
            if (registry == null) registry = FindObjectOfType<CameraStreamRegistry>();
            _layer = LayerMask.NameToLayer(cgLayerName);
            if (_layer < 0)
                Debug.LogWarning($"[ShowCgLayer] レイヤ '{cgLayerName}' が未定義。CG 人形は出ない" +
                                 "（Project Settings > Tags and Layers に追加すると有効になる）。");
            // マテリアルアセットに焼かれた古い値に引きずられないよう、なじませ量はここで明示する。
            _material?.SetFloat(CgSoftenId, CgSoftenTexels);
            SetStrength(0f);
        }

        private void OnDestroy()
        {
            Hide();
            if (_virtualCam != null) Destroy(_virtualCam.gameObject);
            if (_actorInstance != null) Destroy(_actorInstance);
            if (_roomProxy != null) Destroy(_roomProxy.gameObject);
            if (_blob != null) Destroy(_blob.gameObject);
            // 実行時複製したマテリアルは自分で始末する（Resources の .mat 資産そのものは触っていない）。
            if (_shadowMat != null) Destroy(_shadowMat);
            if (_blobMat != null) Destroy(_blobMat);
            ReleaseRenderTexture();
        }

        /// <summary>
        /// カットの CG 指定を適用する。<paramref name="actorId"/> が空なら消す。
        /// <paramref name="cameraIndex"/> は「いま映している映像を撮った実カメラ」の index
        /// （ライブでも録画でも、その構図に合わせて人形を立てるため）。
        /// <paramref name="placement"/> は cgMode="fixed" のときの立ち位置（カットが持つ。null なら actor の既定）。
        /// </summary>
        public void Apply(string actorId, string cgMode, int cameraIndex, ShowPlacementDef? placement = null)
        {
            if (string.IsNullOrEmpty(actorId)) { Hide(); return; }
            if (_layer < 0 || _material == null) return;
            if (showControl == null) { Debug.LogWarning("[ShowCgLayer] ShowControlClient が居ないので CG を出せない"); return; }

            ShowActorDef? def = showControl.FindActor(actorId);
            if (def == null)
            {
                Debug.LogWarning($"[ShowCgLayer] actor '{actorId}' が show.json に無い → CG を出さない");
                Hide();
                return;
            }
            bool hasCalibForPose = showControl.TryGetCameraCalib(cameraIndex, out ShowCameraCalibDef calibForPose);
            if (!showControl.TryGetCameraPose(cameraIndex, out ShowCameraPoseDef pose))
            {
                // 較正の解があるなら、そこから概算姿勢を作って出す。
                // 較正は「実測した姿勢」そのものなので、pose（人がドラッグした概算）が無いことを理由に
                // 出さないのは筋が通らない。卓は前から「較正があれば人形は出る」と判定して輪郭を描いており、
                // **卓で ✅ に見えて実機だけ出ない**という食い違いになっていた（2026-07-29 是正）。
                if (hasCalibForPose && calibForPose.IsUsable())
                {
                    pose = PoseFromCalib(calibForPose);
                }
                else
                {
                    // 当てずっぽうのパースで人形を出すと「浮いている / 床に埋まっている」になる。出さない方が良い。
                    Debug.LogWarning($"[ShowCgLayer] カメラ {cameraIndex} の姿勢も較正も未著作 → CG を出さない");
                    Hide();
                    return;
                }
            }

            EnsureCamera();
            EnsureRoomProxy();
            EnsureActor(def);
            _mode = TakeSchema.NormalizeCgMode(cgMode, out bool known);
            if (!known) Debug.LogWarning($"[ShowCgLayer] 未知の cgMode '{cgMode}' → follow として扱う");
            _actorDef = def;
            _pose = pose;
            _placement = placement;
            _cameraIndex = cameraIndex;
            // 較正の解があればそれが姿勢の正（pose は人がドラッグした概算にすぎない）。
            // ただしレンズ・解像度の照合は毎フレーム行う（起動直後は実寸が未確定なので Apply では決まらない）。
            _calibRaw = hasCalibForPose ? calibForPose : null;
            _warnedCalibMismatch = false;
            _actorRig?.ResetPose();
            _visible = true;
            _warnedUnregistered = false;
            Tick();
        }

        /// <summary>
        /// 較正の解から概算姿勢を作る（<c>cameras[].pose</c> が未著作でも人形を出すため）。
        /// 水平画角は内部行列から <c>2·atan(W / 2fx)</c> で戻す。較正が使える限りこの pose は
        /// 実際には使われない（毎フレームの <c>ResolveCalib</c> が calib を採る）が、
        /// レンズ・解像度が食い違ったときのフォールバック先として意味を持つ。
        /// </summary>
        private static ShowCameraPoseDef PoseFromCalib(ShowCameraCalibDef c)
        {
            float hfov = 2f * Mathf.Atan(c.srcW / (2f * Mathf.Max(c.fxPx, 1f))) * Mathf.Rad2Deg;
            return new ShowCameraPoseDef
            {
                x = c.x,
                z = c.z,
                y = c.y,
                yawDeg = c.yawDeg,
                pitchDeg = c.pitchDeg,
                hfovDeg = Mathf.Clamp(hfov, 10f, 170f),
            };
        }

        /// <summary>CG を消す（カットが終わった / 指定の無いカットへ移った）。</summary>
        public void Hide()
        {
            _visible = false;
            _rendering = false;
            _actorDef = null;
            _pose = null;
            _placement = null;
            SetStrength(0f);
            if (_virtualCam != null) _virtualCam.enabled = false;
            if (_actorInstance != null) _actorInstance.SetActive(false);
            if (_roomProxy != null) _roomProxy.gameObject.SetActive(false);
            if (_blob != null) _blob.gameObject.SetActive(false);
        }

        private void LateUpdate()
        {
            if (!_visible) return;
            Tick();
        }

        // 1 フレーム分の追従。位置合わせ（登録）・映像実寸・カメラ姿勢の変化に毎フレーム追従する。
        private void Tick()
        {
            if (_actorDef == null || _pose == null) return;

            // 未登録のまま出すと course→world が identity へ落ち、人形が全く違う場所に立つ。
            // 「出さない」で止める（黙って嘘の場所に立たせない）。登録が入れば次フレームから復帰する。
            if (!IsCourseRegistered())
            {
                if (!_warnedUnregistered)
                {
                    _warnedUnregistered = true;
                    Debug.LogWarning("[ShowCgLayer] HMD 位置合わせが未完了 → CG を出さない（登録すると出る）");
                }
                SetRendering(false);
                return;
            }
            _warnedUnregistered = false;
            SetRendering(true);

            EnsureRenderTexture();
            ShowCameraCalibDef? calib = ResolveCalib();
            if (calib != null) ApplyCameraCalib(calib);
            else ApplyCameraPose(_pose);
            // 部屋プロキシを先に置く（床の高さがこの後の影の落ち先になる）。
            _roomProxy?.Sync();
            Vector3 lightDir = ApplyLight();
            // 足跡は毎フレーム積む（PlaceActor が過去の時点を読むため）。
            if (showControl?.HeadCourseXZProvider != null)
                _courseTrack.Push(Time.unscaledTime, showControl.HeadCourseXZProvider());
            UpdateBodyYaw(Time.deltaTime);
            PlaceActor(_actorDef);
            ApplyGroundContact(_actorDef, lightDir);
            if (_actorRig != null && _actorRig.HasRig)
                _actorRig.Drive(CurrentBody(), _actorInstance!.transform.eulerAngles.y, Time.deltaTime,
                                _bodyYawDeg);
        }

        // 体の向きを頭の向きへ鈍らせて追わせる。**首を振っただけで体ごと回らない**ようにするのと、
        // 腕の相対ベクトルを体基準で正規化するのに要る（頭基準だと手が動いていないのに腕が振り回される）。
        private void UpdateBodyYaw(float dt)
        {
            ShowBodyInput body = CurrentBody();
            if (!body.HasHead) return;
            if (!_bodyYawSeeded)
            {
                // 初回はスナップ（人形が出た瞬間に体が回りながら現れると、それ自体が演出に見える）。
                _bodyYawDeg = body.HeadYawDeg;
                _bodyYawSeeded = true;
                return;
            }
            _bodyYawDeg = ActorArmLogic.SmoothYawDeg(_bodyYawDeg, body.HeadYawDeg, dt);
        }

        private bool IsCourseRegistered()
        {
            var f = showControl?.CourseRegisteredProvider;
            return f == null || f();   // 供給元が居ない（Editor プレビュー等）なら止めない
        }

        private void SetRendering(bool on)
        {
            if (_rendering == on) return;
            _rendering = on;
            if (_virtualCam != null) _virtualCam.enabled = on;
            if (_actorInstance != null) _actorInstance.SetActive(on);
            // プロキシと接地影は人形と生死を共にする（人形が居ないのに部屋の深度だけ書いても意味が無い）。
            if (_roomProxy != null) _roomProxy.gameObject.SetActive(on);
            if (_blob != null) _blob.gameObject.SetActive(on);
            SetStrength(on ? 1f : 0f);
        }

        // 供給が止まった（橋渡し未配置 / アプリ suspend）ときは「手は取れていない」へ倒す。
        // 生きているときは **映像の遅延ぶん過去**を読む — 「いま」の体験者を描くと歩行中ずっと先行する。
        private ShowBodyInput CurrentBody()
        {
            float now = Time.unscaledTime;
            if (now - _bodyStamp > BodyInputTimeoutSec) return ShowBodyInput.None;
            return _bodyHistory.Sample(now - VideoLatencySec);
        }

        // ---- course 空間 → ワールド ----

        private Vector3 CourseToWorld(Vector2 xz, float y)
        {
            var f = showControl?.CourseToWorldProvider;
            return f != null ? f(xz, y) : new Vector3(xz.x, y, xz.y);
        }

        private float CourseYawDeg()
        {
            var f = showControl?.CourseYawProvider;
            return f != null ? f() : 0f;
        }

        // ---- 仮想カメラ ----

        /// <summary>ソース映像の見かけアスペクト（未デコードなら 4:3 を仮定）。</summary>
        private float SourceAspect()
        {
            float a = _screen != null ? _screen.SourceAspect : 0f;
            return a > 0.01f ? a : FallbackSourceAspect;
        }

        private void EnsureCamera()
        {
            if (_virtualCam == null)
            {
                var go = new GameObject("[CgVirtualCamera]");
                _virtualCam = go.AddComponent<Camera>();
                _virtualCam.clearFlags = CameraClearFlags.SolidColor;
                _virtualCam.backgroundColor = new Color(0f, 0f, 0f, 0f);   // 透明背景 = 被覆率がアルファに出る
                _virtualCam.cullingMask = 1 << _layer;                    // CG レイヤだけ描く
                _virtualCam.nearClipPlane = 0.05f;
                _virtualCam.farClipPlane = 30f;
                _virtualCam.allowHDR = false;
                _virtualCam.allowMSAA = false;
                _virtualCam.depth = -100;                                  // HMD カメラより先に描く
                _virtualCam.stereoTargetEye = StereoTargetEyeMask.None;    // VR の両眼描画に巻き込まれない
            }
            EnsureRenderTexture();
            _virtualCam.enabled = true;
        }

        /// <summary>
        /// 部屋プロキシ（オクルーダ + 床の高さ）を実行時に用意する。仮想カメラと同じく
        /// **シーンには置かない** — 生成物が ShowCg レイヤに閉じているので、シーン側の配線が要らない。
        /// </summary>
        private void EnsureRoomProxy()
        {
            if (_roomProxy == null)
            {
                var go = new GameObject("[CgRoomProxy]");
                if (_layer >= 0) go.layer = _layer;
                _roomProxy = go.AddComponent<ShowRoomProxy>();
                _roomProxy.Initialize(showControl, _layer);
            }
            _roomProxy.gameObject.SetActive(true);
        }

        /// <summary>
        /// RT を**ソース映像の実寸**に合わせる（枠のアスペクトではない）。
        /// 解像度も映像に合わせて落とす — 640x480 の JPEG に 1280x720 の鮮鋭な CG を重ねると
        /// 輪郭のクッキリ度が食い違って「貼り付けた絵」に見える。<see cref="renderHeightPx"/> は上限。
        /// </summary>
        private void EnsureRenderTexture()
        {
            float aspect = SourceAspect();
            int srcH = _screen != null ? _screen.SourceHeight : 0;
            int h = srcH > 0 ? Mathf.Min(srcH, Mathf.Max(64, renderHeightPx)) : Mathf.Max(64, renderHeightPx);
            int w = Mathf.Max(64, Mathf.RoundToInt(h * aspect));

            if (_rt == null || _rt.width != w || _rt.height != h)
            {
                ReleaseRenderTexture();
                // depth は **24**（depth24 + stencil8）。16 だとステンシルが無く、平面投影シャドウの
                // 「1 画素 1 回だけ描く」が効かなくなって腕と胴の重なりが二重に暗くなる（濃い斑）。
                _rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32)
                {
                    name = "ShowCgLayer",
                    useMipMap = false,
                    // レンズ歪み補正で UV が枠外を指すことがある。繰り返すと反対側の人形が出る。
                    wrapMode = TextureWrapMode.Clamp,
                };
                _rt.Create();
                if (_virtualCam != null) _virtualCam.targetTexture = _rt;
                _material?.SetTexture(CgTexId, _rt);
            }

            // 合成 UV はライブ映像と同じ contain-fit 枠。MjpegScreen の計算をそのまま使う（二重計算しない）。
            Vector2 s = _screen != null ? _screen.ContainScale : Vector2.one;
            _material?.SetVector(CgScaleId, new Vector4(s.x, s.y, 0f, 0f));
        }

        private void ReleaseRenderTexture()
        {
            if (_rt == null) return;
            if (_virtualCam != null) _virtualCam.targetTexture = null;
            _rt.Release();
            if (Application.isPlaying) Destroy(_rt); else DestroyImmediate(_rt);
            _rt = null;
        }

        /// <summary>
        /// 使える較正か**毎フレーム**判定する。内部パラメータは撮り方（解像度・レンズ）に従属するので、
        /// 配信設定を変えた瞬間に無効へ倒さないと黙って狂う。実寸が未確定のうちは概算 pose で出す。
        /// </summary>
        private ShowCameraCalibDef? ResolveCalib()
        {
            if (_calibRaw == null) return null;
            int w = _screen != null ? _screen.SourceWidth : 0;
            int h = _screen != null ? _screen.SourceHeight : 0;
            if (w <= 0 || h <= 0) return null;                     // まだ 1 枚も来ていない
            string lens = LensIdOf(_cameraIndex);
            if (_calibRaw.MatchesSource(w, h, lens)) return _calibRaw;

            if (!_warnedCalibMismatch)
            {
                _warnedCalibMismatch = true;
                Debug.LogWarning($"[ShowCgLayer] カメラ {_cameraIndex} の較正は撮り方が違う" +
                                 $"（較正時 {_calibRaw.srcW}x{_calibRaw.srcH} lens'{_calibRaw.lensId}'" +
                                 $" / いま {w}x{h} lens'{lens}'）→ 概算の姿勢で出す。較正し直すこと");
            }
            return null;
        }

        private string LensIdOf(int index)
        {
            CameraStream? s = registry != null ? registry.Get(index) : null;
            StreamMetadata? m = s != null ? s.Metadata : null;
            return m != null ? (m.lensId ?? "") : "";
        }

        /// <summary>
        /// **較正の解**を仮想カメラへ適用する。内部パラメータは <c>projectionMatrix</c> へ直接入れる —
        /// Unity の physical camera（sensorSize / lensShift / gateFit）を経由すると符号と軸の
        /// 取り違えが必ず起きるうえ、主点ズレの表現に余計な変換が挟まる。
        /// </summary>
        private void ApplyCameraCalib(ShowCameraCalibDef c)
        {
            if (_virtualCam == null) return;
            Transform t = _virtualCam.transform;
            t.position = CourseToWorld(new Vector2(c.x, c.z), c.y);
            // roll は三脚の傾き。符号は卓のワイヤー重畳（較正の検証表示）で確定させる。
            t.rotation = Quaternion.Euler(-c.pitchDeg, CourseYawDeg() + c.yawDeg, c.rollDeg);

            _virtualCam.aspect = (float)c.srcW / Mathf.Max(1, c.srcH);
            _virtualCam.projectionMatrix = BuildProjectionMatrix(
                c.fxPx, c.fyPx, c.cxPx, c.cyPx, c.srcW, c.srcH,
                _virtualCam.nearClipPlane, _virtualCam.farClipPlane);
            _projectionOverridden = true;

            // 実レンズの歪みを **CG 側にも掛けて**合わせる（CG はピンホール、実映像は樽型に歪んでいる）。
            // 係数は OpenCV と同じ「正規化画像座標に対する k1」。
            SetLensDistortion(c.k1, c.cxPx / c.srcW, c.cyPx / c.srcH, c.fxPx / c.srcW, c.fyPx / c.srcH);
        }

        /// <summary>
        /// ピンホール内部行列 (fx, fy, cx, cy [px]) → Unity の射影行列。
        /// 画像は左上原点、Unity の frustum は下が bottom なので y を反転して渡す。
        /// </summary>
        public static Matrix4x4 BuildProjectionMatrix(float fxPx, float fyPx, float cxPx, float cyPx,
                                                      int wPx, int hPx, float near, float far)
        {
            float fx = Mathf.Max(1e-3f, fxPx), fy = Mathf.Max(1e-3f, fyPx);
            float w = Mathf.Max(1, wPx), h = Mathf.Max(1, hPx);
            float left = -cxPx * near / fx;
            float right = (w - cxPx) * near / fx;
            float top = cyPx * near / fy;
            float bottom = -(h - cyPx) * near / fy;
            return Matrix4x4.Frustum(left, right, bottom, top, near, far);
        }

        private void SetLensDistortion(float k1, float cxN, float cyN, float fxN, float fyN)
        {
            _material?.SetVector(CgLensId, new Vector4(k1, cxN, cyN, 0f));
            _material?.SetVector(CgFocalId, new Vector4(Mathf.Max(1e-4f, fxN), Mathf.Max(1e-4f, fyN), 0f, 0f));
        }

        // course 空間の姿勢 → 仮想カメラのワールド姿勢（位置合わせ済みの course フレーム上）。
        // **較正が無い / 使えないときのフォールバック**（人がフロアマップで置いた概算）。
        private void ApplyCameraPose(ShowCameraPoseDef pose)
        {
            if (_virtualCam == null) return;
            if (_projectionOverridden)
            {
                _virtualCam.ResetProjectionMatrix();   // fieldOfView 駆動へ戻す
                _projectionOverridden = false;
            }
            SetLensDistortion(0f, 0.5f, 0.5f, 1f, 1f);   // 概算に歪み補正は無い
            Transform t = _virtualCam.transform;
            t.position = CourseToWorld(new Vector2(pose.x, pose.z), pose.y);
            t.rotation = Quaternion.Euler(-pose.pitchDeg, CourseYawDeg() + pose.yawDeg, 0f);

            // **水平**画角が正（卓の扇もレンズのスペックも水平）。Unity の fieldOfView は垂直なので変換する。
            // 旧実装は水平の値をそのまま垂直として渡しており、4:3 なら実効画角が約 25% 違っていた。
            float aspect = SourceAspect();
            float hfov = Mathf.Clamp(pose.hfovDeg > 0f ? pose.hfovDeg : DefaultHfovDeg, 10f, 170f);
            _virtualCam.aspect = aspect;   // targetTexture 任せにしない（RT 再生成のタイミングに依存させない）
            _virtualCam.fieldOfView = HorizontalToVerticalFovDeg(hfov, aspect);
        }

        /// <summary>水平 FOV → 垂直 FOV（度）。<paramref name="aspect"/> は W/H。</summary>
        public static float HorizontalToVerticalFovDeg(float hfovDeg, float aspect)
        {
            float a = Mathf.Max(0.01f, aspect);
            float h = Mathf.Clamp(hfovDeg, 1f, 179f) * Mathf.Deg2Rad;
            return 2f * Mathf.Atan(Mathf.Tan(h * 0.5f) / a) * Mathf.Rad2Deg;
        }

        // ---- 照明 ----

        /// <summary>
        /// 人形の光の向きを **course 空間基準**でマテリアルへ渡す。
        /// 旧実装はワールド固定のベクトルで、Quest のトラッキング原点の向き次第で
        /// 部屋に対する光の向きが変わっていた（＝毎回違う陰影になる）。
        /// </summary>
        /// <returns>「光が来る向き」のワールドベクトル（正規化）。影の投影にも同じ値を使う。</returns>
        private Vector3 ApplyLight()
        {
            ShowRoomLightDef? light = ResolveLight();
            float yaw = light != null ? light.yawDeg : DefaultLightYawDeg;
            float pitch = light != null ? light.pitchDeg : DefaultLightPitchDeg;
            Vector3 dir = CourseLightDirToWorld(yaw, pitch, CourseYawDeg());

            if (_actorRenderers.Length == 0) return dir;

            // 主光源の色 = 色温度 × 強さ。環境光は影側の持ち上げ量。
            // **卓で著作したこの 3 つが絵に効くのはここだけ** — 繋ぐまではスライダを動かしても
            // 何も変わらず、著作者にはその理由に到達する手段が無かった。
            float tempK = light != null ? light.tempK : DefaultLightTempK;
            float intensity = light != null ? Mathf.Max(0f, light.intensity) : 1f;
            float ambient = light != null ? Mathf.Clamp01(light.ambient) : DefaultAmbient;
            Color lc = KelvinToLinearColor(tempK) * intensity;

            _mpb ??= new MaterialPropertyBlock();
            foreach (Renderer r in _actorRenderers)
            {
                if (r == null) continue;
                r.GetPropertyBlock(_mpb);
                _mpb.SetVector(LightDirId, new Vector4(dir.x, dir.y, dir.z, 0f));
                _mpb.SetVector(LightColorId, new Vector4(lc.r, lc.g, lc.b, 1f));
                _mpb.SetFloat(AmbientId, ambient);
                r.SetPropertyBlock(_mpb);
            }
            return dir;
        }

        /// <summary>
        /// 色温度 (K) → **linear** の RGB。卓（`room-model.js` の `kelvinToRgb`）と**同じ近似式**を使う
        /// — 卓のスウォッチと実機の人形の色が食い違うと、著作者は何を信じればいいのか分からなくなる。
        /// プロジェクトは Linear 色空間なので、sRGB のまま渡すと色が浅くなる（ここで変換する）。
        /// </summary>
        public static Color KelvinToLinearColor(float tempK)
        {
            // ⚠ NaN は Mathf.Clamp を素通りする（比較がすべて false になるため）。
            //    show.json のキー欠落や壊れた値で人形が真っ黒／真っ白になるのを防ぐ。
            if (!(tempK > 0f)) tempK = DefaultLightTempK;
            float t = Mathf.Clamp(tempK, 2000f, 8000f) / 100f;
            float r = t <= 66f ? 255f : 329.698727446f * Mathf.Pow(t - 60f, -0.1332047592f);
            float g = t <= 66f
                ? 99.4708025861f * Mathf.Log(t) - 161.1195681661f
                : 288.1221695283f * Mathf.Pow(t - 60f, -0.0755148492f);
            float b = t >= 66f ? 255f : (t <= 19f ? 0f : 138.5177312231f * Mathf.Log(t - 10f) - 305.0447927307f);
            return new Color(
                Mathf.GammaToLinearSpace(Mathf.Clamp01(r / 255f)),
                Mathf.GammaToLinearSpace(Mathf.Clamp01(g / 255f)),
                Mathf.GammaToLinearSpace(Mathf.Clamp01(b / 255f)),
                1f);
        }

        /// <summary><c>layout.room.light</c>（present-flag 込み）。未著作なら null。</summary>
        private ShowRoomLightDef? ResolveLight()
        {
            ShowRoomDef? room = showControl?.Room;
            return (room != null && room.hasLight && room.light != null) ? room.light : null;
        }

        /// <summary>
        /// course 空間の (方位角, 仰角) → ワールドの「光が来る向き」（正規化・上向き成分が正）。
        /// 真上・真下（±90°）はそのまま通す（方位角が無意味になるだけで縮退しない）。それを超える値だけ畳む。
        /// </summary>
        public static Vector3 CourseLightDirToWorld(float lightYawDeg, float lightPitchDeg, float courseYawDeg)
        {
            float yaw = (courseYawDeg + lightYawDeg) * Mathf.Deg2Rad;
            float pitch = Mathf.Clamp(lightPitchDeg, -90f, 90f) * Mathf.Deg2Rad;
            float cp = Mathf.Cos(pitch);
            return new Vector3(Mathf.Sin(yaw) * cp, Mathf.Sin(pitch), Mathf.Cos(yaw) * cp).normalized;
        }

        // ---- 接地（平面投影シャドウ / 接地影 blob）----

        /// <summary>
        /// 影の落ち先と接地影を毎フレーム更新する。**<see cref="PlaceActor"/> の後**に呼ぶこと
        /// （足元 XZ が決まる前に呼ぶと接地影が 1 フレーム遅れて足からずれる）。
        /// </summary>
        private void ApplyGroundContact(ShowActorDef def, Vector3 lightDirWorld)
        {
            if (_actorInstance == null) return;

            ShowRoomLightDef? light = ResolveLight();
            float density = light != null ? Mathf.Clamp01(light.shadowDensity) : DefaultShadowDensity;
            float softM = light != null ? Mathf.Max(0f, light.shadowSoftM) : DefaultShadowSoftM;

            // 部屋が未著作なら course y=0 の無限平面。**ここで諦めない**のが要点で、
            // プロキシに依存するのはオクルージョンだけ（影と接地影は room 無しでも出す）。
            float floorCourseY = _roomProxy != null ? _roomProxy.FloorCourseY : 0f;
            // course→world を通す（CourseFrame は XZ+yaw の剛体変換で y は素通しだが、
            // 生値を使うとその前提が変わった時に黙ってずれる）。
            float floorWorldY = CourseToWorld(Vector2.zero, floorCourseY).y;

            if (EnsureShadowMaterial())
            {
                _shadowMat!.SetFloat(ShadowPlaneYId, floorWorldY);
                _shadowMat.SetVector(ShadowLightDirId,
                    new Vector4(lightDirWorld.x, lightDirWorld.y, lightDirWorld.z, 0f));
                _shadowMat.SetFloat(ShadowDensityId, density);
            }
            // にじみ（shadowSoftM）は平面投影では表現できない（形をそのまま潰すため）。
            // 接地影の縁のぼけ幅として効かせる — 受け口だけ作って効かせないのは著作者への嘘。
            PlaceGroundBlob(def, floorWorldY, density, softM);
        }

        // 足元の接地影（Quad 1 枚）。投影シャドウだけだと、光が斜めのとき足元そのものは暗くならず
        // 「浮いている」が残る。人形と床の接点を落とすのはこの blob の仕事。
        private void PlaceGroundBlob(ShowActorDef def, float floorWorldY, float density, float softM)
        {
            if (!EnsureBlob() || _actorInstance == null) return;
            Vector3 p = _actorInstance.transform.position;
            float radius = Mathf.Clamp(Mathf.Max(0.2f, def.heightM) * BlobRadiusPerHeight,
                                       BlobRadiusMinM, BlobRadiusMaxM);
            _blob!.position = new Vector3(p.x, floorWorldY + BlobLiftM, p.z);
            // Quad を床へ寝かせる。シェーダが Cull Off なので表裏の取り違えで消えることはない。
            _blob.rotation = Quaternion.Euler(90f, 0f, 0f);
            _blob.localScale = new Vector3(radius * 2f, radius * 2f, 1f);
            _blobMat?.SetFloat(BlobDensityId, density);
            // にじみは m 指定。シェーダは半径に対する比で受けるのでここで割る（全域ぼけ = 1 で頭打ち）。
            _blobMat?.SetFloat(BlobFeatherId, BlobFeatherFromSoftM(softM, radius));
        }

        /// <summary>にじみ (m) → 接地影の縁のぼけ幅（半径に対する比）。半径が変われば比も変わる。</summary>
        public static float BlobFeatherFromSoftM(float softM, float radiusM)
            => Mathf.Clamp(softM / Mathf.Max(0.01f, radiusM), 0.05f, 1f);

        private bool EnsureBlob()
        {
            if (_blob != null) { _blob.gameObject.SetActive(true); return true; }
            if (!EnsureBlobMaterial()) return false;

            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "[CgGroundBlob]";
            if (_layer >= 0) go.layer = _layer;
            Collider? col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = _blobMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            _blob = go.transform;
            return true;
        }

        private bool EnsureShadowMaterial()
        {
            if (_shadowMat != null) return true;
            _shadowMat = LoadRuntimeMaterial(ShadowMaterialResource, ShadowShaderName,
                                             "影（平面投影シャドウ）", ref _warnedShadowMissing);
            return _shadowMat != null;
        }

        private bool EnsureBlobMaterial()
        {
            if (_blobMat != null) return true;
            _blobMat = LoadRuntimeMaterial(BlobMaterialResource, BlobShaderName,
                                           "接地影", ref _warnedBlobMissing);
            return _blobMat != null;
        }

        /// <summary>
        /// Resources の .mat を**複製して**返す（資産そのものを実行時に汚さない）。
        /// Resources 経由なのはビルドのシェーダ剥がし対策 — どのアセットからも参照されないシェーダは
        /// ビルドから除去され、実機だけ <c>Shader.Find</c> が null を返して影が消える。
        /// 見つからなければ警告 1 回で null（**影が出ないだけで人形は出る**）。
        /// </summary>
        private static Material? LoadRuntimeMaterial(string resourcePath, string shaderName,
                                                     string label, ref bool warned)
        {
            var loaded = Resources.Load<Material>(resourcePath);
            Shader? shader = loaded != null ? loaded.shader : Shader.Find(shaderName);
            if (shader == null)
            {
                if (!warned)
                {
                    warned = true;
                    Debug.LogWarning($"[ShowCgLayer] {label}のマテリアル '{resourcePath}' も" +
                                     $" シェーダ '{shaderName}' も見つからない → {label}なしで続行");
                }
                return null;
            }
            var mat = loaded != null ? new Material(loaded) : new Material(shader);
            mat.name = $"{shaderName} (runtime)";
            return mat;
        }

        /// <summary>
        /// 人形の各 Renderer に**影マテリアルを 1 枚足す**（本体 + 影 = 同じメッシュが 2 回描かれる）。
        /// SkinnedMeshRenderer でもスキニング後の頂点に効くので、腕を上げれば影も腕を上げる。
        ///
        /// ⚠ Unity は「マテリアル数 &gt; サブメッシュ数」のとき、余ったマテリアルを**最後のサブメッシュ**に
        ///    しか適用しない。サブメッシュが複数あるモデルでは最後の 1 つ分しか影が出ないので警告を出す
        ///    （<c>Build Show Actor Prefab</c> が作るプレハブは全サブメッシュが同一マテリアルなので、
        ///     1 メッシュ 1 サブメッシュに書き出したモデルなら踏まない）。
        /// </summary>
        private void AttachShadowMaterial()
        {
            if (!EnsureShadowMaterial()) return;
            foreach (Renderer r in _actorRenderers)
            {
                if (r == null) continue;
                Material[] mats = r.sharedMaterials;
                if (mats.Length > 0 && mats[mats.Length - 1] == _shadowMat) continue;   // 二重付与しない

                var next = new Material[mats.Length + 1];
                for (int i = 0; i < mats.Length; i++) next[i] = mats[i];
                next[mats.Length] = _shadowMat;
                r.sharedMaterials = next;

                if (!_warnedMultiSubMesh && SubMeshCountOf(r) > 1)
                {
                    _warnedMultiSubMesh = true;
                    Debug.LogWarning($"[ShowCgLayer] '{r.name}' はサブメッシュが複数あるため、" +
                                     "影が出るのは最後のサブメッシュだけになる" +
                                     "（人形を 1 メッシュ 1 マテリアルに書き出すと全身に影が出る）");
                }
            }
        }

        private static int SubMeshCountOf(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr)
                return smr.sharedMesh != null ? smr.sharedMesh.subMeshCount : 0;
            return r.TryGetComponent(out MeshFilter mf) && mf.sharedMesh != null
                ? mf.sharedMesh.subMeshCount : 0;
        }

        // ---- 人形 ----

        private void EnsureActor(ShowActorDef def)
        {
            if (_actorInstance != null && _actorId == def.id)
            {
                _actorInstance.SetActive(true);
                // **大きさは毎回入れ直す。** show.json はランの最中にも配られるので、ここで即 return すると
                // 「卓で heightM を変えたのに、その体験のあいだ人形だけ古い大きさのまま」になる
                // （一度出した後は id が同じなので、アプリを再起動するまで直らない）。
                ApplyActorScale(def);
                return;
            }

            if (_actorInstance != null) Destroy(_actorInstance);
            _actorInstance = null;
            _actorRig = null;
            _actorRenderers = System.Array.Empty<Renderer>();

            GameObject? prefab = string.IsNullOrEmpty(def.prefab) ? null : Resources.Load<GameObject>(def.prefab);
            if (prefab == null)
            {
                // プレハブ未用意でも「そこに人形が立つ」ことは確認できるようにする（素材待ちで詰まらせない）。
                Debug.LogWarning($"[ShowCgLayer] actor '{def.id}' のプレハブ '{def.prefab}' が Resources に無い → 代用の箱で出す");
                _actorInstance = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                _actorIsFallbackCapsule = true;
                Collider? col = _actorInstance.GetComponent<Collider>();
                if (col != null) Destroy(col);
            }
            else
            {
                _actorInstance = Instantiate(prefab);
                _actorIsFallbackCapsule = false;
                _actorRig = _actorInstance.GetComponent<ShowActorRig>();
                if (_actorRig != null)
                {
                    _actorRig.Prepare();
                }
                else
                {
                    Debug.LogWarning($"[ShowCgLayer] actor '{def.id}' のプレハブに ShowActorRig が無い" +
                                     " → 腕はハンドトラッキングで動かない（立つだけ）");
                }
            }

            _actorInstance.name = $"[CgActor:{def.id}]";
            SetLayerRecursive(_actorInstance.transform, _layer);
            _actorRenderers = _actorInstance.GetComponentsInChildren<Renderer>(true);
            _warnedMultiSubMesh = false;
            AttachShadowMaterial();
            ApplyActorScale(def);
            _actorId = def.id;
        }

        /// <summary>
        /// <c>show.json</c> の <c>heightM</c> を正として人形の実寸を合わせる（cm 単位の FBX でも破綻しない）。
        /// **生成時だけでなく、定義が配られるたびに呼ぶ** — 卓は体験の最中にも show.json を配るので、
        /// 生成時に 1 回だけ掛けると「変えたのに大きさだけ古いまま」が起きる。
        /// </summary>
        private void ApplyActorScale(ShowActorDef def)
        {
            if (_actorInstance == null) return;
            float h = Mathf.Max(0.2f, def.heightM);
            if (_actorIsFallbackCapsule)
            {
                _actorInstance.transform.localScale = new Vector3(0.35f, h * 0.5f, 0.35f);
                return;
            }
            if (_actorRig == null) return;   // リグ無しのプレハブは実寸が測れないので触らない
            float measured = Mathf.Max(0.1f, _actorRig.MeasuredHeightM);
            _actorInstance.transform.localScale = Vector3.one * Mathf.Clamp(h / measured, 0.05f, 20f);
        }

        // follow = 体験者の HMD の course XZ に立つ / fixed = 著作した位置に立つ。
        // 立ち位置は **カットの placement が優先**（同じ人形を別のカットで別の場所に立たせるため）。
        // placement が無ければ actor の既定（後方互換）。
        private void PlaceActor(ShowActorDef def)
        {
            if (_actorInstance == null) return;

            bool follow = _mode == TakeSchema.CgFollow;
            Vector2 xz = _placement != null
                ? new Vector2(_placement.x, _placement.z)
                : new Vector2(def.fixedX, def.fixedZ);
            float authoredYaw = _placement != null ? _placement.yawDeg : def.fixedYawDeg;
            if (follow && showControl?.HeadCourseXZProvider != null)
            {
                // ⚠ 「いま」ではなく**腕・向きと同じ時刻**（映像の遅延ぶん過去）を読む。
                //    ここだけ現在時刻にすると、歩きながら振り向いたときに体の位置と向きが食い違う。
                if (!_courseTrack.TrySample(Time.unscaledTime - VideoLatencySec, out xz))
                    xz = showControl.HeadCourseXZProvider();
            }

            Transform t = _actorInstance.transform;
            // 足元は床（course y=0）。リグの実寸に合わせて縮尺済みなので原点＝足元でよい。
            Vector3 pos = CourseToWorld(xz, 0f);
            if (_actorRig == null || !_actorRig.HasRig)
                pos.y += Mathf.Max(0.2f, def.heightM) * 0.5f;   // 代用カプセルは中心が原点
            t.position = pos;

            float yaw;
            ShowBodyInput body = CurrentBody();
            if (follow && body.HasHead)
            {
                // 人形は体験者の分身。**体**の向きを揃えると腕の写像と体の向きが常に整合する。
                // 頭の向きをそのまま使うと、首を振っただけで人形が体ごと回る。
                yaw = _bodyYawDeg;
            }
            else if (follow && _virtualCam != null)
            {
                // 身体入力が無い間はカメラの方を向かせる（背中だけを見せない）。
                Vector3 d = _virtualCam.transform.position - t.position;
                yaw = (new Vector2(d.x, d.z).sqrMagnitude > 1e-4f)
                    ? Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg
                    : CourseYawDeg() + authoredYaw;
            }
            else
            {
                yaw = CourseYawDeg() + authoredYaw;
            }
            t.rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        private static void SetLayerRecursive(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayerRecursive(t.GetChild(i), layer);
        }

        private void SetStrength(float v) => _material?.SetFloat(CgStrengthId, Mathf.Clamp01(v));
    }
}
