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
    /// course 空間 → ワールドの変換は <c>CourseFrame</c>（HMD 位置合わせ）が持つので、
    /// <see cref="ShowControlClient.CourseToWorldProvider"/> / <see cref="ShowControlClient.CourseYawProvider"/>
    /// を通して引く（Streaming→Tracking の型参照を作らない既存規約）。
    /// **transform の親子付けでは駄目**（CourseFrame は transform を動かさず originXZ/yawDeg を数値で持つ）。
    ///
    /// **姿勢が未著作のカメラでは出さない**（当てずっぽうのパースで出す方が体験を壊す）。
    /// 腕の駆動は <see cref="ShowActorRig"/>（体験者のハンドトラッキング）。
    /// 設計の正本: <c>.claude/plans/2026-07-27_cg-actor-hand-tracking.md</c>。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShowCgLayer : MonoBehaviour
    {
        private static readonly int CgTexId = Shader.PropertyToID("_CgTex");
        private static readonly int CgStrengthId = Shader.PropertyToID("_CgStrength");

        /// <summary>身体入力がこの秒数届かなければ「手は取れていない」とみなす。</summary>
        private const float BodyInputTimeoutSec = 0.5f;

        [Tooltip("CG 人形だけを置くレイヤ名。仮想カメラはこのレイヤだけを描く。" +
                 "プロジェクトに未定義なら CG は出さない（フェイルソフト）。")]
        [SerializeField] private string cgLayerName = "ShowCg";

        [Tooltip("CG レイヤの RenderTexture の縦解像度。スクリーンのアスペクトで横を決める。")]
        [SerializeField, Min(64)] private int renderHeightPx = 720;

        [Tooltip("スクリーン（ScreenComposite マテリアル）を持つ Renderer。null なら同 GameObject から取る。")]
        [SerializeField] private Renderer? screenRenderer;

        [Tooltip("show.json の actors / カメラ姿勢の供給元。null ならシーンから探す。")]
        [SerializeField] private ShowControlClient? showControl;

        private Material? _material;
        private Camera? _virtualCam;
        private RenderTexture? _rt;
        private GameObject? _actorInstance;
        private ShowActorRig? _actorRig;
        private string _actorId = "";
        private int _layer = -1;
        private bool _visible;
        private string _mode = TakeSchema.CgFollow;
        private ShowActorDef? _actorDef;
        private ShowCameraPoseDef? _pose;

        private ShowBodyInput _body;
        private float _bodyStamp = -999f;

        /// <summary>いま CG を出しているか（HUD / 診断用）。</summary>
        public bool IsVisible => _visible;

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
            if (_rt != null)
            {
                _rt.Release();
                if (Application.isPlaying) Destroy(_rt); else DestroyImmediate(_rt);
                _rt = null;
            }
        }

        /// <summary>
        /// カットの CG 指定を適用する。<paramref name="actorId"/> が空なら消す。
        /// <paramref name="cameraIndex"/> は「いま映している映像を撮った実カメラ」の index
        /// （ライブでも録画でも、その構図に合わせて人形を立てるため）。
        /// </summary>
        public void Apply(string actorId, string cgMode, int cameraIndex)
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
            _actorRig?.ResetPose();
            _visible = true;
            ApplyCameraPose(pose);
            PlaceActor(def);
            SetStrength(1f);
        }

        /// <summary>CG を消す（カットが終わった / 指定の無いカットへ移った）。</summary>
        public void Hide()
        {
            _visible = false;
            _actorDef = null;
            _pose = null;
            SetStrength(0f);
            if (_virtualCam != null) _virtualCam.enabled = false;
            if (_actorInstance != null) _actorInstance.SetActive(false);
        }

        private void LateUpdate()
        {
            if (!_visible || _actorDef == null) return;
            if (_pose != null) ApplyCameraPose(_pose);   // 位置合わせ（登録）の更新に毎フレーム追従する
            PlaceActor(_actorDef);
            if (_actorRig != null && _actorRig.HasRig)
                _actorRig.Drive(CurrentBody(), _actorInstance!.transform.eulerAngles.y, Time.deltaTime);
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

        private void EnsureRenderTexture()
        {
            float aspect = 16f / 9f;
            var screen = screenRenderer != null ? screenRenderer.GetComponent<MjpegScreen>() : null;
            if (screen != null) aspect = screen.ScreenAspect;

            int h = Mathf.Max(64, renderHeightPx);
            int w = Mathf.Max(64, Mathf.RoundToInt(h * aspect));
            if (_rt != null && _rt.width == w && _rt.height == h) return;

            if (_rt != null) { _rt.Release(); Destroy(_rt); }
            _rt = new RenderTexture(w, h, 16, RenderTextureFormat.ARGB32) { name = "ShowCgLayer", useMipMap = false };
            _rt.Create();
            if (_virtualCam != null) _virtualCam.targetTexture = _rt;
            _material?.SetTexture(CgTexId, _rt);
        }

        // course 空間の姿勢 → 仮想カメラのワールド姿勢（位置合わせ済みの course フレーム上）。
        private void ApplyCameraPose(ShowCameraPoseDef pose)
        {
            if (_virtualCam == null) return;
            Transform t = _virtualCam.transform;
            t.position = CourseToWorld(new Vector2(pose.x, pose.z), pose.y);
            t.rotation = Quaternion.Euler(-pose.pitchDeg, CourseYawDeg() + pose.yawDeg, 0f);
            _virtualCam.fieldOfView = Mathf.Clamp(pose.fovDeg > 0f ? pose.fovDeg : 60f, 10f, 140f);
        }

        // ---- 人形 ----

        private void EnsureActor(ShowActorDef def)
        {
            if (_actorInstance != null && _actorId == def.id) { _actorInstance.SetActive(true); return; }

            if (_actorInstance != null) Destroy(_actorInstance);
            _actorInstance = null;
            _actorRig = null;

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
            _actorId = def.id;
        }

        // follow = 体験者の HMD の course XZ に立つ / fixed = 著作した位置に立つ。
        private void PlaceActor(ShowActorDef def)
        {
            if (_actorInstance == null) return;

            bool follow = _mode == TakeSchema.CgFollow;
            Vector2 xz = new Vector2(def.fixedX, def.fixedZ);
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
                    : CourseYawDeg() + def.fixedYawDeg;
            }
            else
            {
                yaw = CourseYawDeg() + def.fixedYawDeg;
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
