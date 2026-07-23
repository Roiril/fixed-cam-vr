#nullable enable
using System;
using FixedCamVr.Streaming;
using UnityEngine;

namespace FixedCamVr.Tracking
{
    /// <summary>
    /// コース（ゾーンレイアウト）をトラッキング空間へ位置合わせする **N 点登録** の HMD 内フロー。
    /// 形状・カメラ割当は show.json layout 側（PC で編集）なので、ここで扱うのは
    /// <see cref="CourseFrame"/> の剛体変換 3 DOF（XZ 平行移動 + yaw）だけ。旧 ZoneCalibrator の
    /// 「ゾーン選択・ドラッグ・リサイズ・全体回転」の操作系は全廃し、登録リチュアルに置換した。
    ///
    /// 基準点は show.json layout の <c>regPoints</c>（順序つき・2〜5 点・床マーカー運用）から供給される。
    /// 不在（0/1 点）なら <see cref="regPoint1"/>/<see cref="regPoint2"/> の既定 2 点へフォールバックする
    /// （後方互換・既存動作不変）。各点は床に貼った×印テープの真上へコントローラ先端をかざして取る
    /// （壁へのめり込み問題を消すため「当てる」→「かざす」へ変更）。
    ///
    /// フロー（トリガー 2 秒長押しで開始 = <see cref="Toggle"/> を Bridge が呼ぶ）:
    ///   0. Review … 開始時に既に有効な登録があれば（今セッション確定済み or registration.json ロード済み）
    ///      ここへ着地し、現在の登録をワイヤーフレームで表示する（保存日時 / 残差 / 点数付き）。
    ///      A=点 1 から再登録（Capture へ） / B=保存せず終了。未登録なら Capture から始まる。
    ///   1. Capture … 点 k/N をガイダンス表示し、床の×印の真上へ先端をかざして
    ///      **A を押したまま 0.5 秒静止**（ホールド中の位置サンプル平均を採用 = 手先ジッタ低減。
    ///      0.5 秒未満で離すとマーク不成立でやり直し）。2 点目以降は直前点との実測距離 vs authored 距離の
    ///      誤差 % をライブ表示する。N 点そろったら <see cref="RigidFit2D"/> で剛体フィット（2D Procrustes）。
    ///      **点毎残差の最大が <see cref="maxResidualM"/> を超えたら**、どの点が悪いかを表示して全体やり直し。
    ///   2. Verify … 壁ポリライン + フロア外周 + 登録点×マーカーをワイヤーフレームでゴースト表示。
    ///      実物の壁・床マーカーに重なるか目視。B=確定（保存 + 終了） / A=最初からやり直し。
    ///      （スティックナッジ微調整は廃止。N 点剛体フィット + 残差ガード + 約 10 秒のやり直しが精度を担保する。）
    ///
    /// 入力は OvrControllerBridge から <see cref="Feed"/> で転送される（このアセンブリは OVRInput 非依存）。
    /// コントローラ先端の位置は <see cref="rightHandTransform"/> の position をそのまま使う
    /// （先端オフセット補正はしない。誤差 2〜3cm はベースライン + 回廊 + オーバーラップに対して許容）。
    ///
    /// 視界内ガイダンスは自前 TextMesh を持たず、各ステップの指示を <see cref="GuidanceText"/> /
    /// <see cref="GuidanceColor"/> として公開する（単一サーフェス StatusHud が登録中に読み取って表示する。
    /// Tracking → Diagnostics の asmdef 依存を作らないためのプロバイダ方式）。要再登録警告は StatusHud が
    /// <see cref="CourseFrame.NeedsReRegistration"/> を直接読む。ワイヤーフレームとゾーン床フットプリントは
    /// 登録モード中のみ生成・破棄する。床フットプリントは show.json layout.grid があれば Web 卓で塗った
    /// 生タイル（<see cref="ZoneGridFootprint"/> の単一メッシュ）を、無ければ現存 <see cref="PlayerZone"/> の
    /// OBB を Quad で床投影する。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CourseRegistrationController : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("位置合わせ対象の CourseFrame。null なら Awake で同 GameObject から取得。")]
        [SerializeField] private CourseFrame? courseFrame;

        [Tooltip("HMD（CenterEyeAnchor）。rightHandTransform 未割当時のタッチ位置フォールバックに使う。")]
        [SerializeField] private Transform? headTransform;

        [Tooltip("コントローラ先端（RightHandAnchor）。position を登録基準点のタッチ位置に使う。" +
                 "null なら headTransform にフォールバック。")]
        [SerializeField] private Transform? rightHandTransform;

        [Tooltip("regPoints / wall / floor 寸法の供給元。null or layout 未設定なら既定 2 点 + 内蔵既定を使う。")]
        [SerializeField] private ShowControlClient? showControl;

        [Header("Fallback registration points（course space XZ, m・layout.regPoints 不在時）")]
        [Tooltip("既定ステップ1 の基準点。layout に regPoints が無い時のみ使う。")]
        [SerializeField] private Vector2 regPoint1 = new(-0.5f, 0.5f);

        [Tooltip("既定ステップ2 の基準点。layout に regPoints が無い時のみ使う。")]
        [SerializeField] private Vector2 regPoint2 = new(0.5f, 0.5f);

        [Tooltip("ライブ誤差 % ガイダンスに出す許容目安（表示のみ。合否は maxResidualM 側で判定する）。")]
        [SerializeField, Range(0.01f, 0.5f)] private float distanceTolerance = 0.15f;

        [Header("Fit acceptance")]
        [Tooltip("剛体フィット後の点毎残差の最大がこの値 (m) 以下なら合格。超えたら該当点を表示してやり直し。")]
        [SerializeField, Min(0.01f)] private float maxResidualM = 0.12f;

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
            public float deltaTime;   // このフレームの経過時間 (秒)。ホールド平均計時に使う（EditMode 駆動可）
        }

        private enum Phase { Idle, Capture, Verify, Review }

        /// <summary>登録モード中か。Bridge はこれを見て通常入力を抑止する。</summary>
        public bool IsActive => _phase != Phase.Idle;

        /// <summary>
        /// ホールド平均サンプリングの進捗 [0,1]（非サンプル中は 0）。Bridge が長押し進捗と Max 合成して
        /// 触覚 HoldTick へ流す（0.5 秒ホールド中も進行ランプが鳴る）。
        /// </summary>
        public float SampleHoldProgress01 => _sampler.Progress01;

        /// <summary>
        /// 現在の登録ガイダンス文字列（登録モード中のみ非空）。StatusHud（単一サーフェス）が読み取って表示する。
        /// Idle では空文字（要再登録警告は StatusHud が CourseFrame から直接読む）。
        /// </summary>
        public string GuidanceText => _guidanceText;

        /// <summary>ガイダンス表示色（エラー時は橙・通常は緑）。StatusHud が反映する。</summary>
        public Color GuidanceColor => _guidanceColor;

        /// <summary>
        /// 点サンプルが 1 点確定した時に発火（0.5s ホールド平均が採れた瞬間）。触覚フィードバックの
        /// Action 発火に使う（購読側 = Assembly-CSharp の OvrControllerBridge。このアセンブリは OVRInput 非依存）。
        /// </summary>
        public event Action? PointCaptured;

        /// <summary>剛体フィットの残差過大 / 解不能でやり直しへ戻した時に発火（触覚 Error に使う）。</summary>
        public event Action? FitRejected;

        /// <summary>
        /// N 点そろい残差ガードを通過して Verify へ遷移した時に発火（触覚 Fire に使う。FitRejected と対称）。
        /// </summary>
        public event Action? FitAccepted;

        /// <summary>
        /// ホールドを 0.5 秒未満で離してマークが不成立になった時に発火（触覚 Error に使う）。
        /// </summary>
        public event Action? SampleAborted;

        /// <summary>Verify で B 確定・保存して登録を終えた時に発火（触覚 Fire に使う）。</summary>
        public event Action? RegistrationConfirmed;

        // マーク確定に必要な A ホールド秒。ホールド中の位置サンプルを平均して手先ジッタを均す
        //（押下瞬間の 1 サンプルは腕の振り・ボタン押し込みのブレをそのまま拾う）。
        private const float MarkHoldSec = 0.5f;

        // ライブ誤差 % 表示の更新間隔 (秒)。毎フレームの文字列生成 GC を間引く。
        private const float LiveErrorInterval = 0.15f;

        // 登録点×マーカーの腕の半長 (m)。
        private const float MarkerHalfM = 0.05f;

        private Phase _phase = Phase.Idle;

        // Verify ガイダンスへ出す、直近フィットの最大残差 (m)。SolveAndVerify で確定する。
        private float _verifyMaxResidualM;

        // authored 基準点（course space XZ）とラベル。SetActive / LayoutChanged で resolve する。
        private Vector2[] _authoredPoints = Array.Empty<Vector2>();
        private string[] _authoredLabels = Array.Empty<string>();

        // 実測タッチ位置（ワールド・ホールド平均）。_authoredPoints と同じ長さで確保する。
        private Vector3[] _capturedWorld = Array.Empty<Vector3>();
        private int _pointIndex;                // 現在キャプチャ中の点 index（0..N-1）

        private string _transientMsg = "";      // エラー等の一時メッセージ
        private float _transientUntil;

        // A ホールド平均サンプリング（dt 駆動の純ロジック。計時を EditMode でも決定的に固定できる）。
        private readonly HoldAverageSampler _sampler = new(MarkHoldSec);

        // ライブ誤差 % ガイダンス（間引き更新のキャッシュ）。点 index が変わったら即更新する。
        private string _liveGuidanceText = "";
        private float _liveGuidanceNext;
        private int _liveGuidanceForIndex = -1;

        // ガイダンス（StatusHud へ供給する文字列。自前 TextMesh は持たない）。
        private string _guidanceText = "";
        private Color _guidanceColor = new(0.9f, 1f, 0.9f, 1f);

        // ---- 可視化 ----
        private GameObject? _vizRoot;

        // ワイヤーフレーム（course space に持ち、毎フレーム CourseFrame で world 変換して追従）
        private Vector2[] _floorCourse = Array.Empty<Vector2>();
        private Vector2[] _wallCourse = Array.Empty<Vector2>();
        private LineRenderer? _floorLine;
        private LineRenderer? _wallBottom;
        private LineRenderer? _wallTop;
        private LineRenderer[] _wallPosts = Array.Empty<LineRenderer>();

        // 登録点×マーカー（点ごとに交差 2 本。Verify のワイヤーフレームと同じ寿命）
        private Vector2[] _markCourse = Array.Empty<Vector2>();
        private LineRenderer[] _markLines = Array.Empty<LineRenderer>(); // 2 * N 本

        // ゾーン床フットプリント（登録モード中の分かりやすさ用）。
        // grid（Web 卓で塗ったタイル）がある時は ZoneGridFootprint（生タイルの単一メッシュ）を使い、
        // grid 不在（v1 cuts / 既定ゾーン）時は現存 PlayerZone の OBB を Quad で床投影する。
        private ZoneGridFootprint? _gridFootprint;
        private bool _useGridFootprint;
        private PlayerZone[] _footZones = Array.Empty<PlayerZone>();
        private Transform[] _footQuads = Array.Empty<Transform>();
        private Material[] _footMats = Array.Empty<Material>();
        private bool _zonesDirty;

        private void Awake()
        {
            if (courseFrame == null) courseFrame = GetComponent<CourseFrame>();
            // 新規 SerializeField が古いシーン/prefab YAML に未反映だと型 default(0) で読まれる
            // （unity-prefab-fields の罠）。maxResidualM の有効域は Min(0.01) なので 0 は復元して既定へ。
            if (maxResidualM <= 0f) maxResidualM = 0.12f;
        }

        private void OnEnable()
        {
            if (courseFrame != null) courseFrame.Changed += OnFrameChanged;
            if (showControl != null) showControl.LayoutChanged += OnLayoutChanged;
        }

        private void OnDisable()
        {
            if (courseFrame != null) courseFrame.Changed -= OnFrameChanged;
            if (showControl != null) showControl.LayoutChanged -= OnLayoutChanged;
        }

        private void Start()
        {
            ResolvePoints(); // 既定 or layout の初期供給（次の登録開始に備える）
            if (startInRegistration) SetActive(true);
        }

        private void OnDestroy()
        {
            TearDownViz();
        }

        /// <summary>Bridge のモード遷移（Staff → Registration 入場 / 退場）から呼ばれる。登録モードの ON/OFF。</summary>
        public void Toggle() => SetActive(!IsActive);

        /// <summary>
        /// **Editor プレビュー / デバッグ起動フック用**エントリ。SetActive の状態機械を通さずに
        /// 可視化（ゾーン床フットプリント + ワイヤーフレーム + 登録点マーカー）を 1 回だけ静的に組み、
        /// 現在の CourseFrame 変換で 1 回配置する。Edit Mode の静的レンダ（多角度スクショ）で
        /// 「実運用と同一の viz コードパス」を Play なしに再現する用途。通常の登録フロー（Capture/Verify/Review）
        /// には使わない。呼ぶ前に courseFrame / showControl が注入済みであること。
        /// </summary>
        public void PreviewBuildViz()
        {
            ResolvePoints();   // Edit Mode は Start() が走らないため authored 点をここで供給
            BuildViz();        // フットプリント（grid 生タイル or PlayerZone OBB）
            BuildWireframe();  // 壁・フロア外周・登録点マーカー（Idle でも組めるよう非 Phase 依存で呼ぶ）
            UpdateViz();       // 現在の CourseFrame 変換で 1 回配置
        }

        // layout 更新（ライブ / キャッシュ由来）で regPoints が差し替わったら再供給する。
        // キャプチャ進行中に点数が変わると破綻するので、モード外（Idle）でのみ取り込む。
        private void OnLayoutChanged()
        {
            if (!IsActive) ResolvePoints();
        }

        private void SetActive(bool on)
        {
            if (IsActive == on) return;
            if (on)
            {
                ResolvePoints();                          // 最新 layout の regPoints を取り込む
                courseFrame?.BeginPreviewSession();       // 以後の Verify プレビューをトランザクション化する
                _capturedWorld = new Vector3[_authoredPoints.Length];
                _pointIndex = 0;
                _sampler.Reset();
                _liveGuidanceForIndex = -1;
                BuildViz();

                // 有効な登録が既にあれば Review 着地（現在の登録をワイヤーで見せ、ズレていれば A で再登録）。
                // 未登録なら従来どおり点 1 の Capture へ。
                if (courseFrame != null && courseFrame.HasRegistration)
                {
                    _phase = Phase.Review;
                    BuildWireframe();
                    Debug.Log($"[CourseReg] 登録モード ON（確認）— 登録済みの位置合わせを表示。" +
                              "ワイヤーが実物に重ならなければ A で再登録 / B で終了");
                }
                else
                {
                    _phase = Phase.Capture;
                    Debug.Log($"[CourseReg] 登録モード ON — {_authoredPoints.Length} 点登録。" +
                              "点 1: 床の×印の真上に先端をかざして A を 0.5 秒ホールド");
                }
            }
            else
            {
                // B 確定以外の退場（トリガー長押しキャンセル / Review-B）はプレビューを確定前 state へ戻す。
                // 確定経路は先に CommitPreviewSession 済みなので no-op（確定と共存できる）。
                courseFrame?.RollbackPreviewSession();
                _phase = Phase.Idle;
                _sampler.Reset();
                TearDownViz();
                Debug.Log("[CourseReg] 登録モード OFF");
            }
        }

        // show.json layout.regPoints（2〜5 点）を authored 基準点へ取り込む。不在（<2 点）なら既定 2 点。
        private void ResolvePoints()
        {
            ShowLayoutDef? lay = showControl != null ? showControl.Layout : null;
            ShowRegPointDef[]? rp = lay?.regPoints;
            if (rp != null && rp.Length >= 2)
            {
                int n = rp.Length;
                var pts = new Vector2[n];
                var labels = new string[n];
                for (int i = 0; i < n; i++)
                {
                    pts[i] = new Vector2(rp[i].x, rp[i].z);
                    labels[i] = rp[i].label ?? "";
                }
                _authoredPoints = pts;
                _authoredLabels = labels;
                Debug.Log($"[CourseReg] 基準点 {n} 点を layout.regPoints から供給");
            }
            else
            {
                _authoredPoints = new[] { regPoint1, regPoint2 };
                _authoredLabels = new[] { "", "" };
            }
        }

        /// <summary>Bridge から毎フレーム呼ばれる（登録モード中のみ）。</summary>
        public void Feed(in RegInput input)
        {
            switch (_phase)
            {
                case Phase.Capture:
                    UpdateMarkSampling(input);
                    break;
                case Phase.Verify:
                    if (input.confirm) { ConfirmAndExit(); return; }
                    if (input.mark) { RestartCapture(); return; }
                    break;
                case Phase.Review:
                    // 確認フェーズ: A=点 1 から再登録 / B=保存せず終了（RegistrationConfirmed は発火しない）。
                    if (input.mark) { RestartCapture(); return; }
                    if (input.confirm) { ExitReview(); return; }
                    break;
            }
        }

        // A ホールド平均のサンプリング進行（全点共通）。計時・平均は HoldAverageSampler（純ロジック）へ委譲。
        //   Down エッジで開始 → ホールド中は毎フレーム位置を加算 → MarkHoldSec 経過で平均を確定。
        //   途中で離したら不成立（ガイダンスにやり直し表示。点 index は進めないのでそのまま再トライ可）。
        private void UpdateMarkSampling(in RegInput input)
        {
            HoldAverageSampler.Result r =
                _sampler.Tick(input.mark, input.markHeld, input.deltaTime, Pointer(), out Vector3 avg);
            if (r == HoldAverageSampler.Result.Aborted)
            {
                // MarkHoldSec 未満で離した → マーク不成立（点は採らない）。
                ShowTransient("マーク不成立\n×印の真上にかざしたまま A を 0.5 秒静止してください", 2.5f);
                SampleAborted?.Invoke(); // 触覚 Error（ホールド中断）
                return;
            }
            if (r == HoldAverageSampler.Result.Captured) CapturePoint(avg);
        }

        private Vector3 Pointer()
        {
            if (rightHandTransform != null) return rightHandTransform.position;
            if (headTransform != null) return headTransform.position;
            return Camera.main != null ? Camera.main.transform.position : Vector3.zero;
        }

        // 1 点確定。まだ残りがあれば次点へ、最後の点なら剛体フィットへ。
        private void CapturePoint(Vector3 avgPos)
        {
            if (_pointIndex >= _capturedWorld.Length) return; // 念のため
            _capturedWorld[_pointIndex] = avgPos;
            Debug.Log($"[CourseReg] 点 {_pointIndex + 1}/{_authoredPoints.Length} 記録(0.5s 平均): " +
                      $"world=({avgPos.x:F3},{avgPos.z:F3})");
            PointCaptured?.Invoke(); // 触覚 Action（点サンプル確定）

            if (_pointIndex + 1 < _authoredPoints.Length)
            {
                _pointIndex++;
                _liveGuidanceForIndex = -1; // 次点のライブ表示を即更新
            }
            else
            {
                SolveAndVerify();
            }
        }

        // N 点そろったので剛体フィット → 合否ゲート → Verify へ。
        private void SolveAndVerify()
        {
            if (courseFrame == null) return;

            int n = _authoredPoints.Length;
            var worldXZ = new Vector2[n];
            for (int i = 0; i < n; i++) worldXZ[i] = new Vector2(_capturedWorld[i].x, _capturedWorld[i].z);

            RigidFit2D.Result fit = RigidFit2D.Solve(_authoredPoints, worldXZ);
            if (!fit.ok)
            {
                Debug.LogWarning("[CourseReg] 剛体フィット不能（基準点が重なっています）— やり直します");
                ShowTransient("基準点の設定が不正です（点が重なっています）", 4f);
                FitRejected?.Invoke(); // 触覚 Error（拒否・やり直し）
                RestartCapture();
                return;
            }

            if (fit.maxResidualM > maxResidualM)
            {
                int w = fit.worstIndex;
                Debug.LogWarning($"[CourseReg] フィット残差過大: 点 {w + 1} の残差 {fit.maxResidualM:F3}m " +
                                 $"> 許容 {maxResidualM:F3}m（RMS {fit.rmsResidualM:F3}m）— やり直します");
                ShowTransient($"点 {w + 1} の残差 {fit.maxResidualM:F2}m — タッチをやり直してください", 4f);
                FitRejected?.Invoke(); // 触覚 Error（残差過大・やり直し）
                RestartCapture();
                return;
            }

            // 保存はまだしない（Verify で B 確定するまで registration.json は書かない）。品質メタは stash され、
            // 確定時に json へ焼き込まれる（Review / StatusHud 表示に使う）。
            courseFrame.SetRegistration(fit.originXZ, fit.yawDeg, fit.maxResidualM, n, save: false);
            _verifyMaxResidualM = fit.maxResidualM;
            _phase = Phase.Verify;
            BuildWireframe();
            FitAccepted?.Invoke(); // 触覚 Fire（残差ガード通過・Verify 遷移。FitRejected と対称）
            Debug.Log($"[CourseReg] 登録解決: origin=({fit.originXZ.x:F3},{fit.originXZ.y:F3}) yaw={fit.yawDeg:F1}° " +
                      $"(max残差 {fit.maxResidualM:F3}m / RMS {fit.rmsResidualM:F3}m) — 壁・×印に重なるか確認して B=確定 / A=やり直し");
        }

        private void RestartCapture()
        {
            _phase = Phase.Capture;
            _pointIndex = 0;
            _sampler.Reset();
            _liveGuidanceForIndex = -1;
            TearDownWireframe();
            Debug.Log("[CourseReg] やり直し — 点 1: 床の×印の真上に先端をかざして A を 0.5 秒ホールド");
        }

        private void ConfirmAndExit()
        {
            if (courseFrame != null)
            {
                courseFrame.SaveRegistration();
                courseFrame.CommitPreviewSession(); // プレビューを確定（以後の SetActive(false) の Rollback は no-op）
                Debug.Log($"[CourseReg] 確定・保存: origin=({courseFrame.OriginXZ.x:F3},{courseFrame.OriginXZ.y:F3}) yaw={courseFrame.YawDeg:F1}°");
            }
            RegistrationConfirmed?.Invoke(); // 触覚 Fire（確定保存）
            SetActive(false);
        }

        // Review フェーズを保存せず終える（B）。既存登録は一切変えない。IsActive=false により
        // ControllerModeLogic が Normal へ戻す（その ModeChanged の Fire だけが鳴る＝確定保存とは区別）。
        private void ExitReview()
        {
            Debug.Log("[CourseReg] 確認フェーズ終了（保存なし）");
            SetActive(false);
        }

        private void ShowTransient(string msg, float seconds)
        {
            _transientMsg = msg;
            _transientUntil = Time.unscaledTime + seconds;
        }

        private void OnFrameChanged() => _zonesDirty = true;

        // ---- ガイダンス（StatusHud へ供給する文字列）----------------------------

        private void Update()
        {
            UpdateGuidanceText();
            if (IsActive) UpdateViz();
        }

        // 各ステップの指示を _guidanceText / _guidanceColor へ書く（StatusHud が登録中に読み取る）。
        // Idle は空文字（要再登録警告は StatusHud が CourseFrame から直接読む）。
        private void UpdateGuidanceText()
        {
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
                    case Phase.Capture:
                        // サンプリング中は進捗バー付きで毎フレーム更新（ガイダンスブランチは間引きなし）。
                        // 経過時間は Progress01（=time/hold）から復元する（SamplingLine は elapsed を受ける）。
                        text = _sampler.Active
                            ? RegistrationGuidance.SamplingLine(_sampler.Progress01 * MarkHoldSec, MarkHoldSec)
                            : CaptureGuidance();
                        break;
                    case Phase.Verify:
                        text = RegistrationGuidance.ResidualLine(_verifyMaxResidualM, maxResidualM)
                             + "\nワイヤーが実物の壁・床の×印に重なるか確認\nB = 確定    A = やり直し";
                        break;
                    case Phase.Review:
                        text = ReviewGuidance();
                        break;
                    default: // Idle
                        text = "";
                        break;
                }
            }

            _guidanceText = text;
            _guidanceColor = color;
        }

        // Capture 中のガイダンス。点 k/N と label を示し、2 点目以降は直前点との実測距離 vs
        // authored 距離の誤差 % をライブ表示する（確定前に「いま何 % ズレているか」を見ながら当てられる）。
        // 文字列生成は LiveErrorInterval 間隔に間引く（毎フレームの補間 GC を避ける。点 index 変化で即更新）。
        private string CaptureGuidance()
        {
            if (_pointIndex == _liveGuidanceForIndex
                && Time.unscaledTime < _liveGuidanceNext
                && !string.IsNullOrEmpty(_liveGuidanceText))
                return _liveGuidanceText;
            _liveGuidanceNext = Time.unscaledTime + LiveErrorInterval;
            _liveGuidanceForIndex = _pointIndex;

            int k = _pointIndex + 1, n = _authoredPoints.Length;
            string label = _authoredLabels[_pointIndex];
            string head = string.IsNullOrEmpty(label)
                ? "床の×印の真上に先端をかざして"
                : $"「{label}」の床の×印の真上に先端をかざして";
            string text = $"点 {k}/{n}\n{head}\nA を押しながら 0.5 秒静止";

            if (_pointIndex >= 1)
            {
                float authored = (_authoredPoints[_pointIndex] - _authoredPoints[_pointIndex - 1]).magnitude;
                if (authored > 1e-4f)
                {
                    Vector3 p = Pointer();
                    Vector3 prev = _capturedWorld[_pointIndex - 1];
                    float dx = p.x - prev.x, dz = p.z - prev.z;
                    float measured = Mathf.Sqrt(dx * dx + dz * dz);
                    float errPct = (measured - authored) / authored * 100f;
                    int tolPct = Mathf.RoundToInt(distanceTolerance * 100f);
                    text += $"\n直前の点との誤差 {(errPct >= 0f ? "+" : "")}{errPct:F1}%（±{tolPct}% 目安）";
                }
            }

            _liveGuidanceText = text;
            return text;
        }

        // Review（確認）フェーズのガイダンス。既存登録の保存日時 / 残差 / 点数を出し、A=再登録 / B=終了を促す。
        // recenter で原点が変わっていれば橙のリッチテキスト行で再登録を推奨する（全体色は緑のまま）。
        private string ReviewGuidance()
        {
            string header = RegistrationGuidance.ReviewHeader(
                courseFrame != null ? courseFrame.SavedAtIso : "",
                courseFrame != null ? courseFrame.MaxResidualM : 0f,
                courseFrame != null ? courseFrame.PointCount : 0);
            string text = header
                        + "\nワイヤーが実物に重ならなければ A で再登録"
                        + "\nA = 点1から再登録    B = OK（終了）";
            if (courseFrame != null && courseFrame.NeedsReRegistration)
                text += "\n<color=#FF8C40>⚠トラッキング原点が変わっています — 再登録を推奨</color>";
            return text;
        }

        // ---- ワイヤーフレーム + フットプリント -----------------------------------

        private void BuildViz()
        {
            TearDownViz();
            _vizRoot = new GameObject("[CourseRegViz]");

            // grid（Web 卓で塗った生タイル）があればそれを単一メッシュで表示（PlayerZone Quad は作らない）。
            // 無ければ従来どおり現存 PlayerZone の OBB を Quad で床投影する。
            _useGridFootprint = HasGridTiles();
            if (_useGridFootprint)
            {
                var go = new GameObject("ZoneGridFootprint");
                go.transform.SetParent(_vizRoot.transform, worldPositionStays: false);
                _gridFootprint = go.AddComponent<ZoneGridFootprint>();
                _gridFootprint.Initialize(showControl, courseFrame);
            }
            else
            {
                _zonesDirty = true;
                RefreshFootprints();
            }
        }

        // show.json layout.grid に塗りタイルがあるか。ある間は grid 生タイル表示を優先する。
        private bool HasGridTiles()
        {
            ShowLayoutDef? lay = showControl != null ? showControl.Layout : null;
            return lay != null && lay.grid != null && lay.grid.HasData();
        }

        private void TearDownViz()
        {
            TearDownWireframe();
            foreach (var m in _footMats) if (m != null) DestroySafe(m);
            _footQuads = Array.Empty<Transform>();
            _footMats = Array.Empty<Material>();
            _footZones = Array.Empty<PlayerZone>();
            _gridFootprint = null; // 子 GameObject は _vizRoot 破棄で消える（ZoneGridFootprint.OnDestroy が購読解除）
            _useGridFootprint = false;
            if (_vizRoot != null) DestroySafe(_vizRoot);
            _vizRoot = null;
        }

        // 可視化オブジェクトの破棄。Edit Mode（Editor プレビューツール）から TearDown 経路が呼ばれるため
        // Play 中は Destroy、非 Play は DestroyImmediate に分岐する（非 Play の Destroy は Unity がエラーにする）。
        private static void DestroySafe(UnityEngine.Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
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

            // 登録点×マーカー（点ごとに交差 2 本）。authored 点を course space に持ち、UpdateViz で追従。
            _markCourse = _authoredPoints;
            var markColor = new Color(1f, 0.35f, 0.9f, 0.95f);
            _markLines = new LineRenderer[_markCourse.Length * 2];
            for (int i = 0; i < _markCourse.Length; i++)
            {
                _markLines[i * 2] = MakeLine($"RegMark_{i}_a", shader, markColor, 2, loop: false);
                _markLines[i * 2 + 1] = MakeLine($"RegMark_{i}_b", shader, markColor, 2, loop: false);
            }
        }

        private void TearDownWireframe()
        {
            if (_floorLine != null) DestroySafe(_floorLine.gameObject);
            if (_wallBottom != null) DestroySafe(_wallBottom.gameObject);
            if (_wallTop != null) DestroySafe(_wallTop.gameObject);
            foreach (var p in _wallPosts) if (p != null) DestroySafe(p.gameObject);
            foreach (var m in _markLines) if (m != null) DestroySafe(m.gameObject);
            _floorLine = null;
            _wallBottom = null;
            _wallTop = null;
            _wallPosts = Array.Empty<LineRenderer>();
            _markLines = Array.Empty<LineRenderer>();
            _markCourse = Array.Empty<Vector2>();
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
        // フロア外周と壁ポリラインを組む。壁の既定角は fallback の regPoint1/regPoint2 から導く。
        private void ResolveWireframeGeometry()
        {
            float w = floorW, d = floorD;
            Vector2 corner = regPoint1;                  // L 凸角（既定）
            Vector2 endNorth = regPoint2;                // 北腕 東端（既定）
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
            foreach (var m in _footMats) if (m != null) DestroySafe(m);
            foreach (var q in _footQuads) if (q != null) DestroySafe(q.gameObject);

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
                if (col != null) DestroySafe(col);
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

            // grid 生タイル表示は ZoneGridFootprint が Changed / LayoutChanged 駆動で自己更新するので、
            // ここでは PlayerZone フットプリント（grid 不在時のみ）だけ毎フレーム追従させる。
            if (!_useGridFootprint)
            {
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
                    Color baseColor = ZonePalette.ForCamera(z.CameraIndex);
                    _footMats[i].color = new Color(baseColor.r, baseColor.g, baseColor.b, 0.2f);
                }
            }

            // ワイヤーフレーム（course → world 変換で毎フレーム追従。登録変換の変化に即応）
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

            // 登録点×マーカー（各点で course space の斜め 2 本を world 変換）。
            for (int i = 0; i < _markCourse.Length; i++)
            {
                int a = i * 2, b = i * 2 + 1;
                if (a >= _markLines.Length || _markLines[a] == null || _markLines[b] == null) continue;
                Vector2 p = _markCourse[i];
                _markLines[a].SetPosition(0, courseFrame.CourseToWorld(p + new Vector2(-MarkerHalfM, -MarkerHalfM), 0.031f));
                _markLines[a].SetPosition(1, courseFrame.CourseToWorld(p + new Vector2(MarkerHalfM, MarkerHalfM), 0.031f));
                _markLines[b].SetPosition(0, courseFrame.CourseToWorld(p + new Vector2(-MarkerHalfM, MarkerHalfM), 0.031f));
                _markLines[b].SetPosition(1, courseFrame.CourseToWorld(p + new Vector2(MarkerHalfM, -MarkerHalfM), 0.031f));
            }
        }
    }
}
