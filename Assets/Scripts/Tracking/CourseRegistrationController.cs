#nullable enable
using System;
using FixedCamVr.Streaming;
using UnityEngine;

namespace FixedCamVr.Tracking
{
    /// <summary>
    /// コース（ゾーンレイアウト）をトラッキング空間へ位置合わせする **2 点登録** の HMD 内フロー。
    /// 形状・カメラ割当は show.json layout 側（PC で編集）なので、ここで扱うのは
    /// <see cref="CourseFrame"/> の剛体変換 3 DOF（XZ 平行移動 + yaw）だけ。旧 ZoneCalibrator の
    /// 「ゾーン選択・ドラッグ・リサイズ・全体回転」の操作系は全廃し、登録リチュアルに置換した。
    ///
    /// フロー（Staff モードで右スティック押込により開始 = <see cref="Toggle"/> を Bridge が呼ぶ）:
    ///   1. CaptureP1 … 壁の外角（L の凸角 = course <see cref="regPoint1"/>）に先端を当てて
    ///      **A を押したまま 0.5 秒静止**（ホールド中の位置サンプル平均を採用 = 手先ジッタ低減。
    ///      0.5 秒未満で離すとマーク不成立でやり直し）
    ///   2. CaptureP2 … 北腕の東端（course <see cref="regPoint2"/>）に同じく当てて A ホールド 0.5 秒。
    ///      待機中は 1 点目との実測距離 vs 既知ベースラインの誤差 % をガイダンスへライブ表示
    ///      → 2 点から平行移動 + yaw を解く（スケールは解かない＝剛体）。2 点間の実測距離が
    ///        既知距離から <see cref="distanceTolerance"/> 以上ズレたらエラー表示してやり直し。
    ///   3. Verify … 壁ポリライン + フロア外周をワイヤーフレームでゴースト表示。実物の壁に重なるか目視。
    ///        B=確定（保存 + 終了） / A=最初からやり直し / 左スティック=平行移動 / 右スティック横=yaw 微調整。
    ///
    /// 入力は OvrControllerBridge から <see cref="Feed"/> で転送される（このアセンブリは OVRInput 非依存）。
    /// コントローラ先端の位置は <see cref="rightHandTransform"/> の position をそのまま使う
    /// （先端オフセット補正はしない。誤差 2〜3cm は 1m ベースライン + 40cm 回廊に対して許容）。
    ///
    /// 視界内ガイダンス（head-locked TextMesh）は常時 1 個生き、登録モード中は各ステップの指示、
    /// 非モード中は <see cref="CourseFrame.NeedsReRegistration"/> の警告を出す。ワイヤーフレームと
    /// ゾーン床フットプリントは登録モード中のみ生成・破棄する。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CourseRegistrationController : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("位置合わせ対象の CourseFrame。null なら Awake で同 GameObject から取得。")]
        [SerializeField] private CourseFrame? courseFrame;

        [Tooltip("HMD（CenterEyeAnchor）。ガイダンス TextMesh の head-lock 用。null なら Camera.main。")]
        [SerializeField] private Transform? headTransform;

        [Tooltip("コントローラ先端（RightHandAnchor）。position を登録基準点のタッチ位置に使う。" +
                 "null なら headTransform にフォールバック。")]
        [SerializeField] private Transform? rightHandTransform;

        [Tooltip("wall/floor 寸法の供給元。null or layout 未設定なら内蔵既定でワイヤーフレームを描く。")]
        [SerializeField] private ShowControlClient? showControl;

        [Header("Registration reference points（course space XZ, m）")]
        [Tooltip("ステップ1 の基準点 = 壁の外角（L の凸角）。")]
        [SerializeField] private Vector2 regPoint1 = new(-0.5f, 0.5f);

        [Tooltip("ステップ2 の基準点 = 北腕の東端。regPoint1 との距離が既知ベースライン。")]
        [SerializeField] private Vector2 regPoint2 = new(0.5f, 0.5f);

        [Tooltip("2 点間実測距離が既知距離からこの割合以上ズレたらエラーでやり直し。")]
        [SerializeField, Range(0.01f, 0.5f)] private float distanceTolerance = 0.15f;

        [Header("Nudge（Verify 中の微調整）")]
        [Tooltip("左スティックでの平行移動速度 (m/s)。")]
        [SerializeField, Min(0f)] private float nudgeMoveSpeed = 0.3f;

        [Tooltip("右スティック横での yaw 回転速度 (度/s)。")]
        [SerializeField, Min(0f)] private float nudgeYawSpeed = 10f;

        [Header("Wireframe fallback（course space・layout 不在時）")]
        [Tooltip("フロア幅 (X, m)。")]
        [SerializeField, Min(0.1f)] private float floorW = 1.8f;

        [Tooltip("フロア奥行 (Z, m)。")]
        [SerializeField, Min(0.1f)] private float floorD = 1.8f;

        [Tooltip("ワイヤーフレーム壁の高さ (m)。パーテーション高さの目安。")]
        [SerializeField, Min(0.1f)] private float wallHeight = 1.0f;

        [Header("Behavior")]
        [Tooltip("起動と同時に登録モードへ入る（通常は Staff モードで右スティック押込により入る）。")]
        [SerializeField] private bool startInRegistration = false;

        /// <summary>Bridge から毎フレーム渡される登録入力（モード中のみ）。</summary>
        public struct RegInput
        {
            public bool mark;         // A（右）の Down エッジ: マークサンプリング開始 / Verify 中はやり直し
            public bool markHeld;     // A（右）の押しっぱなし状態: ホールド平均サンプリングの継続判定
            public bool confirm;      // B（右）: Verify で確定
            public Vector2 nudgeMove; // 左スティック: 平行移動（x=world X, y=world Z）
            public float nudgeYaw;    // 右スティック横: yaw 微調整
        }

        private enum Phase { Idle, CaptureP1, CaptureP2, Verify }

        /// <summary>登録モード中か。Bridge はこれを見て通常入力を抑止する。</summary>
        public bool IsActive => _phase != Phase.Idle;

        private const float StickDeadzone = 0.15f;

        // マーク確定に必要な A ホールド秒。ホールド中の位置サンプルを平均して手先ジッタを均す
        //（押下瞬間の 1 サンプルは腕の振り・ボタン押し込みのブレをそのまま拾う）。
        private const float MarkHoldSec = 0.5f;

        // ステップ 2 のライブ誤差 % 表示の更新間隔 (秒)。毎フレームの文字列生成 GC を間引く。
        private const float LiveErrorInterval = 0.15f;

        private Phase _phase = Phase.Idle;
        private Vector3 _p1World;              // 1 点目のタッチ位置（ワールド・ホールド平均）
        private bool _hasP1;
        private string _transientMsg = "";     // エラー等の一時メッセージ
        private float _transientUntil;

        // A ホールド平均サンプリング（CaptureP1/P2 共通）。Feed が毎フレーム加算する（アロケーションなし）。
        private bool _sampling;
        private Vector3 _sampleAccum;
        private int _sampleCount;
        private float _sampleTime;

        // ステップ 2 のライブ誤差 % ガイダンス（間引き更新のキャッシュ）。
        private string _liveGuidanceText = "";
        private float _liveGuidanceNext;

        // ---- 可視化 ----
        private GameObject? _vizRoot;
        private TextMesh? _guidance;           // 常時 1 個（モード外の警告も担う）
        private MeshRenderer? _guidanceRenderer;

        // ワイヤーフレーム（course space に持ち、毎フレーム CourseFrame で world 変換して追従）
        private Vector2[] _floorCourse = Array.Empty<Vector2>();
        private Vector2[] _wallCourse = Array.Empty<Vector2>();
        private LineRenderer? _floorLine;
        private LineRenderer? _wallBottom;
        private LineRenderer? _wallTop;
        private LineRenderer[] _wallPosts = Array.Empty<LineRenderer>();

        // ゾーン床フットプリント（登録モード中の分かりやすさ用。現存 PlayerZone を色分け表示）
        private PlayerZone[] _footZones = Array.Empty<PlayerZone>();
        private Transform[] _footQuads = Array.Empty<Transform>();
        private Material[] _footMats = Array.Empty<Material>();
        private bool _zonesDirty;

        private static readonly Color[] Palette =
        {
            new(0.3f, 1f, 0.5f), new(0.35f, 0.6f, 1f), new(1f, 0.7f, 0.3f),
            new(1f, 0.4f, 0.6f), new(0.7f, 0.5f, 1f),
        };

        private void Awake()
        {
            if (courseFrame == null) courseFrame = GetComponent<CourseFrame>();
        }

        private void OnEnable()
        {
            if (courseFrame != null) courseFrame.Changed += OnFrameChanged;
        }

        private void OnDisable()
        {
            if (courseFrame != null) courseFrame.Changed -= OnFrameChanged;
        }

        private void Start()
        {
            BuildGuidance();
            if (startInRegistration) SetActive(true);
        }

        private void OnDestroy()
        {
            TearDownViz();
            if (_guidance != null) Destroy(_guidance.gameObject);
        }

        /// <summary>Bridge のモード遷移（Staff → Registration 入場 / 退場）から呼ばれる。登録モードの ON/OFF。</summary>
        public void Toggle() => SetActive(!IsActive);

        private void SetActive(bool on)
        {
            if (IsActive == on) return;
            if (on)
            {
                _phase = Phase.CaptureP1;
                _hasP1 = false;
                _sampling = false;
                BuildViz();
                Debug.Log("[CourseReg] 登録モード ON — ステップ1: 壁の外角（L の凸角）に先端を当てて A を 0.5 秒ホールド");
            }
            else
            {
                _phase = Phase.Idle;
                _sampling = false;
                TearDownViz();
                Debug.Log("[CourseReg] 登録モード OFF");
            }
        }

        /// <summary>Bridge から毎フレーム呼ばれる（登録モード中のみ）。</summary>
        public void Feed(in RegInput input)
        {
            switch (_phase)
            {
                case Phase.CaptureP1:
                    UpdateMarkSampling(input, isSecond: false);
                    break;
                case Phase.CaptureP2:
                    UpdateMarkSampling(input, isSecond: true);
                    break;
                case Phase.Verify:
                    if (input.confirm) { ConfirmAndExit(); return; }
                    if (input.mark) { RestartCapture(); return; }
                    ApplyNudge(input.nudgeMove, input.nudgeYaw);
                    break;
            }
        }

        // A ホールド平均のサンプリング進行（CaptureP1/P2 共通）。
        //   Down エッジで開始 → ホールド中は毎フレーム位置を加算 → MarkHoldSec 経過で平均を確定。
        //   途中で離したら不成立（ガイダンスにやり直し表示。フェーズは変えないのでそのまま再トライ可）。
        private void UpdateMarkSampling(in RegInput input, bool isSecond)
        {
            if (!_sampling)
            {
                if (!input.mark) return;
                _sampling = true;
                _sampleAccum = Pointer(); // 押下フレームも 1 サンプル目として使う
                _sampleCount = 1;
                _sampleTime = 0f;
                return;
            }

            if (!input.markHeld)
            {
                // MarkHoldSec 未満で離した → マーク不成立（点は採らない）。
                _sampling = false;
                ShowTransient("マーク不成立\n先端を当てたまま A を 0.5 秒静止してください", 2.5f);
                return;
            }

            _sampleAccum += Pointer();
            _sampleCount++;
            _sampleTime += Time.deltaTime;
            if (_sampleTime < MarkHoldSec) return;

            _sampling = false;
            Vector3 avg = _sampleAccum / _sampleCount;
            if (isSecond) CaptureSecondAndSolve(avg);
            else CaptureFirst(avg);
        }

        private Vector3 Pointer()
        {
            if (rightHandTransform != null) return rightHandTransform.position;
            if (headTransform != null) return headTransform.position;
            return Camera.main != null ? Camera.main.transform.position : Vector3.zero;
        }

        private void CaptureFirst(Vector3 avgPos)
        {
            _p1World = avgPos;
            _hasP1 = true;
            _phase = Phase.CaptureP2;
            Debug.Log($"[CourseReg] P1 記録(0.5s 平均): world=({_p1World.x:F3},{_p1World.z:F3}) — ステップ2: 北腕の東端に当てて A ホールド");
        }

        private void CaptureSecondAndSolve(Vector3 avgPos)
        {
            if (courseFrame == null || !_hasP1) return;
            Vector3 p2World = avgPos;

            // XZ 平面での 2 点。y は無視（床は Guardian 基準）。
            Vector2 w1 = new(_p1World.x, _p1World.z);
            Vector2 w2 = new(p2World.x, p2World.z);
            Vector2 dWorld = w2 - w1;
            Vector2 dCourse = regPoint2 - regPoint1;

            float measured = dWorld.magnitude;
            float known = dCourse.magnitude;
            if (known < 1e-4f)
            {
                ShowTransient("基準点の設定が不正です（regPoint1==regPoint2）", 4f);
                RestartCapture();
                return;
            }

            float err = Mathf.Abs(measured - known) / known;
            if (err > distanceTolerance || measured < 1e-4f)
            {
                Debug.LogWarning($"[CourseReg] 距離不一致: 実測 {measured:F3}m / 期待 {known:F3}m (ズレ {err * 100f:F0}%) — やり直します");
                ShowTransient($"距離が合いません（実測 {measured:F2}m / 期待 {known:F2}m）。やり直します", 4f);
                RestartCapture();
                return;
            }

            // yaw: course delta（yaw=0 では world XZ に等しい）を world delta へ回す剛体回転。
            // CourseFrame の yaw 回転は world XZ を 2D で -yaw だけ回すので、
            // yaw = -(a→b の符号付き角度) となる（a=course delta, b=world delta）。
            float cross = dCourse.x * dWorld.y - dCourse.y * dWorld.x;
            float dot = dCourse.x * dWorld.x + dCourse.y * dWorld.y;
            float yawDeg = -Mathf.Atan2(cross, dot) * Mathf.Rad2Deg;

            // 平行移動: 各基準点で origin = w - Rot(course)。2 点平均で最小二乗の平行移動にする。
            Quaternion rot = Quaternion.Euler(0f, yawDeg, 0f);
            Vector3 r1 = rot * new Vector3(regPoint1.x, 0f, regPoint1.y);
            Vector3 r2 = rot * new Vector3(regPoint2.x, 0f, regPoint2.y);
            Vector2 o1 = new(w1.x - r1.x, w1.y - r1.z);
            Vector2 o2 = new(w2.x - r2.x, w2.y - r2.z);
            Vector2 origin = (o1 + o2) * 0.5f;

            // 保存はまだしない（Verify で B 確定するまで registration.json は書かない）。
            courseFrame.SetRegistration(origin, yawDeg, save: false);
            _phase = Phase.Verify;
            BuildWireframe();
            Debug.Log($"[CourseReg] 登録解決: origin=({origin.x:F3},{origin.y:F3}) yaw={yawDeg:F1}° — 壁に重なるか確認して B=確定 / A=やり直し");
        }

        private void ApplyNudge(Vector2 move, float yawIn)
        {
            if (courseFrame == null) return;
            Vector2 origin = courseFrame.OriginXZ;
            float yaw = courseFrame.YawDeg;
            bool changed = false;

            if (move.sqrMagnitude > StickDeadzone * StickDeadzone)
            {
                origin += move * (nudgeMoveSpeed * Time.deltaTime);
                changed = true;
            }
            if (Mathf.Abs(yawIn) > StickDeadzone)
            {
                yaw += yawIn * (nudgeYawSpeed * Time.deltaTime);
                changed = true;
            }
            // 毎フレームのディスク書き込みを避けるため save=false。永続化は B 確定時のみ。
            if (changed) courseFrame.SetRegistration(origin, yaw, save: false);
        }

        private void RestartCapture()
        {
            _phase = Phase.CaptureP1;
            _hasP1 = false;
            _sampling = false;
            TearDownWireframe();
            Debug.Log("[CourseReg] やり直し — ステップ1: 壁の外角に先端を当てて A を 0.5 秒ホールド");
        }

        private void ConfirmAndExit()
        {
            if (courseFrame != null)
            {
                courseFrame.SaveRegistration();
                Debug.Log($"[CourseReg] 確定・保存: origin=({courseFrame.OriginXZ.x:F3},{courseFrame.OriginXZ.y:F3}) yaw={courseFrame.YawDeg:F1}°");
            }
            SetActive(false);
        }

        private void ShowTransient(string msg, float seconds)
        {
            _transientMsg = msg;
            _transientUntil = Time.unscaledTime + seconds;
        }

        private void OnFrameChanged() => _zonesDirty = true;

        // ---- ガイダンス（常時 1 個・head-locked）--------------------------------

        private void BuildGuidance()
        {
            var head = HeadTransform();
            var go = new GameObject("[CourseRegGuidance]");
            if (head != null) go.transform.SetParent(head, worldPositionStays: false);
            go.transform.localPosition = new Vector3(0f, -0.12f, 1.2f);
            go.transform.localRotation = Quaternion.identity;

            _guidance = go.AddComponent<TextMesh>();
            _guidance.anchor = TextAnchor.MiddleCenter;
            _guidance.alignment = TextAlignment.Center;
            _guidance.characterSize = 0.02f;
            _guidance.fontSize = 90;
            _guidance.color = new Color(0.9f, 1f, 0.9f, 1f);
            var font = BuiltinFont();
            if (font != null)
            {
                _guidance.font = font;
                _guidanceRenderer = go.GetComponent<MeshRenderer>();
                if (_guidanceRenderer != null) _guidanceRenderer.sharedMaterial = font.material;
            }
            else
            {
                _guidanceRenderer = go.GetComponent<MeshRenderer>();
            }
            _guidance.text = "";
            if (_guidanceRenderer != null) _guidanceRenderer.enabled = false;
        }

        private static Font? BuiltinFont()
        {
            Font? f = null;
            try { f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { /* older Unity */ }
            if (f == null) { try { f = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { /* ignore */ } }
            return f;
        }

        private void Update()
        {
            UpdateGuidanceText();
            if (IsActive) UpdateViz();
        }

        private void UpdateGuidanceText()
        {
            if (_guidance == null) return;
            string text;
            Color color = new(0.9f, 1f, 0.9f, 1f);

            if (!string.IsNullOrEmpty(_transientMsg) && Time.unscaledTime < _transientUntil)
            {
                text = _transientMsg;
                color = new Color(1f, 0.55f, 0.4f, 1f);
            }
            else
            {
                switch (_phase)
                {
                    case Phase.CaptureP1:
                        text = _sampling
                            ? "計測中… 当てたまま静止（0.5 秒）"
                            : "ステップ 1/2\n壁の外角（L の凸角）に先端を当てて\nA を押しながら 0.5 秒静止";
                        break;
                    case Phase.CaptureP2:
                        text = _sampling
                            ? "計測中… 当てたまま静止（0.5 秒）"
                            : LiveP2Guidance();
                        break;
                    case Phase.Verify:
                        text = "ワイヤーが実物の壁に重なるか確認\nB = 確定    A = やり直し\n左スティック = 移動   右スティック横 = 回転";
                        break;
                    default: // Idle
                        text = courseFrame != null && courseFrame.NeedsReRegistration
                            ? "⚠ トラッキング原点が変わりました\n両グリップ 3 秒 → 右スティック押込で再登録"
                            : "";
                        if (!string.IsNullOrEmpty(text)) color = new Color(1f, 0.7f, 0.3f, 1f);
                        break;
                }
            }

            _guidance.text = text;
            _guidance.color = color;
            if (_guidanceRenderer != null) _guidanceRenderer.enabled = !string.IsNullOrEmpty(text);
        }

        // ステップ 2 待機中のガイダンス。現在の先端位置と P1 の実測距離 vs 既知ベースラインの
        // 誤差 % をライブ表示する（確定前に「いま何 % ズレているか」を見ながら当てられる）。
        // 文字列生成は LiveErrorInterval 間隔に間引く（毎フレームの補間 GC を避ける）。
        private string LiveP2Guidance()
        {
            if (Time.unscaledTime < _liveGuidanceNext && !string.IsNullOrEmpty(_liveGuidanceText))
                return _liveGuidanceText;
            _liveGuidanceNext = Time.unscaledTime + LiveErrorInterval;

            float known = (regPoint2 - regPoint1).magnitude;
            if (!_hasP1 || known < 1e-4f)
            {
                _liveGuidanceText = "ステップ 2/2\n北腕の東端に先端を当てて\nA を押しながら 0.5 秒静止";
                return _liveGuidanceText;
            }

            Vector3 p = Pointer();
            float dx = p.x - _p1World.x;
            float dz = p.z - _p1World.z;
            float measured = Mathf.Sqrt(dx * dx + dz * dz);
            float errPct = (measured - known) / known * 100f;
            int tolPct = Mathf.RoundToInt(distanceTolerance * 100f);
            _liveGuidanceText =
                "ステップ 2/2\n北腕の東端に先端を当てて\nA を押しながら 0.5 秒静止\n" +
                $"誤差 {(errPct >= 0f ? "+" : "")}{errPct:F1}%（±{tolPct}% で確定可）";
            return _liveGuidanceText;
        }

        // ---- ワイヤーフレーム + フットプリント -----------------------------------

        private void BuildViz()
        {
            TearDownViz();
            _vizRoot = new GameObject("[CourseRegViz]");
            _zonesDirty = true;
            RefreshFootprints();
        }

        private void TearDownViz()
        {
            TearDownWireframe();
            foreach (var m in _footMats) if (m != null) Destroy(m);
            _footQuads = Array.Empty<Transform>();
            _footMats = Array.Empty<Material>();
            _footZones = Array.Empty<PlayerZone>();
            if (_vizRoot != null) Destroy(_vizRoot);
            _vizRoot = null;
        }

        private void BuildWireframe()
        {
            TearDownWireframe();
            if (_vizRoot == null) return;

            ResolveWireframeGeometry();
            var shader = Shader.Find("Sprites/Default");

            _floorLine = MakeLine("FloorOutline", shader, new Color(0.4f, 1f, 1f, 0.9f), _floorCourse.Length, loop: true);
            _wallBottom = MakeLine("WallBottom", shader, new Color(1f, 0.85f, 0.3f, 0.95f), _wallCourse.Length, loop: false);
            _wallTop = MakeLine("WallTop", shader, new Color(1f, 0.85f, 0.3f, 0.95f), _wallCourse.Length, loop: false);
            _wallPosts = new LineRenderer[_wallCourse.Length];
            for (int i = 0; i < _wallCourse.Length; i++)
                _wallPosts[i] = MakeLine($"WallPost_{i}", shader, new Color(1f, 0.85f, 0.3f, 0.95f), 2, loop: false);
        }

        private void TearDownWireframe()
        {
            if (_floorLine != null) Destroy(_floorLine.gameObject);
            if (_wallBottom != null) Destroy(_wallBottom.gameObject);
            if (_wallTop != null) Destroy(_wallTop.gameObject);
            foreach (var p in _wallPosts) if (p != null) Destroy(p.gameObject);
            _floorLine = null;
            _wallBottom = null;
            _wallTop = null;
            _wallPosts = Array.Empty<LineRenderer>();
        }

        private LineRenderer MakeLine(string lname, Shader shader, Color color, int count, bool loop)
        {
            var go = new GameObject(lname);
            go.transform.SetParent(_vizRoot!.transform, worldPositionStays: false);
            var lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = new Material(shader) { color = color };
            lr.useWorldSpace = true;
            lr.widthMultiplier = 0.01f;
            lr.numCapVertices = 2;
            lr.loop = loop;
            lr.positionCount = Mathf.Max(0, count);
            lr.textureMode = LineTextureMode.Stretch;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return lr;
        }

        // show.json layout の wall/floor があればそれを、無ければ内蔵既定で course space の
        // フロア外周と壁ポリラインを組む。
        private void ResolveWireframeGeometry()
        {
            float w = floorW, d = floorD;
            Vector2 corner = regPoint1;                  // L 凸角
            Vector2 endNorth = regPoint2;                // 北腕 東端
            Vector2 endWest = new(regPoint1.x, -regPoint1.y); // 西腕 南端（既定: 角を Z 反転）

            ShowLayoutDef? lay = showControl != null ? showControl.Layout : null;
            if (lay != null)
            {
                if (lay.floor != null && lay.floor.w > 0f && lay.floor.d > 0f)
                {
                    w = lay.floor.w;
                    d = lay.floor.d;
                }
                if (lay.wall != null)
                {
                    if (TryVec2(lay.wall.corner, out var c)) corner = c;
                    if (TryVec2(lay.wall.endX, out var ex)) endNorth = ex;
                    if (TryVec2(lay.wall.endZ, out var ez)) endWest = ez;
                }
            }

            float hx = w * 0.5f, hz = d * 0.5f;
            _floorCourse = new[]
            {
                new Vector2(-hx, -hz), new Vector2(hx, -hz),
                new Vector2(hx, hz), new Vector2(-hx, hz),
            };
            // 壁ポリライン: 西腕南端 → 凸角 → 北腕東端（L の 2 辺）。
            _wallCourse = new[] { endWest, corner, endNorth };
        }

        private static bool TryVec2(float[]? a, out Vector2 v)
        {
            if (a != null && a.Length >= 2) { v = new Vector2(a[0], a[1]); return true; }
            v = Vector2.zero;
            return false;
        }

        private void RefreshFootprints()
        {
            // 既存プールを破棄
            foreach (var m in _footMats) if (m != null) Destroy(m);
            foreach (var q in _footQuads) if (q != null) Destroy(q.gameObject);

            _footZones = FindObjectsOfType<PlayerZone>();
            _footQuads = new Transform[_footZones.Length];
            _footMats = new Material[_footZones.Length];
            var shader = Shader.Find("Sprites/Default");
            for (int i = 0; i < _footZones.Length; i++)
            {
                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.name = $"ZoneFoot_{_footZones[i].Label}";
                quad.transform.SetParent(_vizRoot!.transform, worldPositionStays: false);
                quad.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // 床に寝かせる
                var col = quad.GetComponent<Collider>();
                if (col != null) Destroy(col);
                var mat = new Material(shader);
                quad.GetComponent<Renderer>().sharedMaterial = mat;
                _footQuads[i] = quad.transform;
                _footMats[i] = mat;
            }
            _zonesDirty = false;
        }

        private void UpdateViz()
        {
            if (_vizRoot == null || courseFrame == null) return;

            if (_zonesDirty) RefreshFootprints();

            // フットプリント（現存ゾーンの OBB を床に投影）
            for (int i = 0; i < _footZones.Length; i++)
            {
                var z = _footZones[i];
                var q = _footQuads[i];
                if (z == null || q == null) continue;
                Vector3 c = z.Center;
                q.position = new Vector3(c.x, 0.02f + i * 0.002f, c.z);
                q.rotation = Quaternion.Euler(0f, z.Rotation.eulerAngles.y, 0f) * Quaternion.Euler(90f, 0f, 0f);
                q.localScale = new Vector3(z.HalfExtents.x * 2f, z.HalfExtents.z * 2f, 1f);
                Color baseColor = Palette[z.CameraIndex % Palette.Length];
                _footMats[i].color = new Color(baseColor.r, baseColor.g, baseColor.b, 0.2f);
            }

            // ワイヤーフレーム（course → world 変換で毎フレーム追従。nudge に即応）
            if (_floorLine != null)
                for (int i = 0; i < _floorCourse.Length; i++)
                    _floorLine.SetPosition(i, courseFrame.CourseToWorld(_floorCourse[i], 0.03f));

            if (_wallBottom != null && _wallTop != null)
            {
                for (int i = 0; i < _wallCourse.Length; i++)
                {
                    Vector3 bottom = courseFrame.CourseToWorld(_wallCourse[i], 0.03f);
                    Vector3 top = courseFrame.CourseToWorld(_wallCourse[i], wallHeight);
                    _wallBottom.SetPosition(i, bottom);
                    _wallTop.SetPosition(i, top);
                    if (i < _wallPosts.Length && _wallPosts[i] != null)
                    {
                        _wallPosts[i].SetPosition(0, bottom);
                        _wallPosts[i].SetPosition(1, top);
                    }
                }
            }
        }

        private Transform? HeadTransform()
        {
            if (headTransform != null) return headTransform;
            return Camera.main != null ? Camera.main.transform : null;
        }
    }
}
