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
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShowCgLayer : MonoBehaviour
    {
        private static readonly int CgTexId = Shader.PropertyToID("_CgTex");
        private static readonly int CgStrengthId = Shader.PropertyToID("_CgStrength");
        private static readonly int CgScaleId = Shader.PropertyToID("_CgScale");
        private static readonly int LightDirId = Shader.PropertyToID("_LightDir");

        /// <summary>身体入力がこの秒数届かなければ「手は取れていない」とみなす。</summary>
        private const float BodyInputTimeoutSec = 0.5f;

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

        private Material? _material;
        private MjpegScreen? _screen;
        private Camera? _virtualCam;
        private RenderTexture? _rt;
        private GameObject? _actorInstance;
        private ShowActorRig? _actorRig;
        private Renderer[] _actorRenderers = System.Array.Empty<Renderer>();
        private MaterialPropertyBlock? _mpb;
        private string _actorId = "";
        private int _layer = -1;
        private bool _visible;
        private bool _rendering;
        private bool _warnedUnregistered;
        private string _mode = TakeSchema.CgFollow;
        private ShowActorDef? _actorDef;
        private ShowCameraPoseDef? _pose;
        private ShowPlacementDef? _placement;

        private ShowBodyInput _body;
        private float _bodyStamp = -999f;

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
        }

        private void Awake()
        {
            if (screenRenderer == null) screenRenderer = GetComponent<Renderer>();
            _material = screenRenderer != null ? screenRenderer.material : null;
            _screen = screenRenderer != null ? screenRenderer.GetComponent<MjpegScreen>() : null;
            if (showControl == null) showControl = FindObjectOfType<ShowControlClient>();
            _layer = LayerMask.NameToLayer(cgLayerName);
            if (_layer < 0)
                Debug.LogWarning($"[ShowCgLayer] レイヤ '{cgLayerName}' が未定義。CG 人形は出ない" +
                                 "（Project Settings > Tags and Layers に追加すると有効になる）。");
            SetStrength(0f);
        }

        private void OnDestroy()
        {
            Hide();
            if (_virtualCam != null) Destroy(_virtualCam.gameObject);
            if (_actorInstance != null) Destroy(_actorInstance);
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
            if (!showControl.TryGetCameraPose(cameraIndex, out ShowCameraPoseDef pose))
            {
                // 当てずっぽうのパースで人形を出すと「浮いている / 床に埋まっている」になる。出さない方が良い。
                Debug.LogWarning($"[ShowCgLayer] カメラ {cameraIndex} の姿勢が未著作 → CG を出さない");
                Hide();
                return;
            }

            EnsureCamera();
            EnsureActor(def);
            _mode = TakeSchema.NormalizeCgMode(cgMode, out bool known);
            if (!known) Debug.LogWarning($"[ShowCgLayer] 未知の cgMode '{cgMode}' → follow として扱う");
            _actorDef = def;
            _pose = pose;
            _placement = placement;
            _actorRig?.ResetPose();
            _visible = true;
            _warnedUnregistered = false;
            Tick();
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
            ApplyCameraPose(_pose);
            ApplyLight();
            PlaceActor(_actorDef);
            if (_actorRig != null && _actorRig.HasRig)
                _actorRig.Drive(CurrentBody(), _actorInstance!.transform.eulerAngles.y, Time.deltaTime);
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
            SetStrength(on ? 1f : 0f);
        }

        // 供給が止まった（橋渡し未配置 / アプリ suspend）ときは「手は取れていない」へ倒す。
        private ShowBodyInput CurrentBody()
            => (Time.unscaledTime - _bodyStamp) <= BodyInputTimeoutSec ? _body : ShowBodyInput.None;

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
                _rt = new RenderTexture(w, h, 16, RenderTextureFormat.ARGB32) { name = "ShowCgLayer", useMipMap = false };
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

        // course 空間の姿勢 → 仮想カメラのワールド姿勢（位置合わせ済みの course フレーム上）。
        private void ApplyCameraPose(ShowCameraPoseDef pose)
        {
            if (_virtualCam == null) return;
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
        private void ApplyLight()
        {
            if (_actorRenderers.Length == 0) return;

            float yaw = DefaultLightYawDeg, pitch = DefaultLightPitchDeg;
            ShowRoomDef? room = showControl?.Room;
            if (room != null && room.hasLight && room.light != null)
            {
                yaw = room.light.yawDeg;
                pitch = room.light.pitchDeg;
            }

            Vector3 dir = CourseLightDirToWorld(yaw, pitch, CourseYawDeg());
            _mpb ??= new MaterialPropertyBlock();
            foreach (Renderer r in _actorRenderers)
            {
                if (r == null) continue;
                r.GetPropertyBlock(_mpb);
                _mpb.SetVector(LightDirId, new Vector4(dir.x, dir.y, dir.z, 0f));
                r.SetPropertyBlock(_mpb);
            }
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

        // ---- 人形 ----

        private void EnsureActor(ShowActorDef def)
        {
            if (_actorInstance != null && _actorId == def.id) { _actorInstance.SetActive(true); return; }

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
                _actorInstance.transform.localScale = new Vector3(0.35f, Mathf.Max(0.2f, def.heightM) * 0.5f, 0.35f);
                Collider? col = _actorInstance.GetComponent<Collider>();
                if (col != null) Destroy(col);
            }
            else
            {
                _actorInstance = Instantiate(prefab);
                _actorRig = _actorInstance.GetComponent<ShowActorRig>();
                if (_actorRig != null)
                {
                    _actorRig.Prepare();
                    // show.json の heightM を正として実寸を合わせる（cm 単位の FBX でも破綻しない）。
                    float measured = Mathf.Max(0.1f, _actorRig.MeasuredHeightM);
                    float k = Mathf.Clamp(Mathf.Max(0.2f, def.heightM) / measured, 0.05f, 20f);
                    _actorInstance.transform.localScale = Vector3.one * k;
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
            _actorId = def.id;
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
            if (follow && showControl?.HeadCourseXZProvider != null) xz = showControl.HeadCourseXZProvider();

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
                // 人形は体験者の分身。体の向きを揃えると腕の写像と体の向きが常に整合する。
                yaw = body.HeadYawDeg;
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
