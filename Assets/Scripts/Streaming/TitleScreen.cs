#nullable enable

using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// タイトル画面「廻リ視」。<b>体験が始まる前に、暗い地の中へ組んだ題字だけを置く。</b>
    ///
    /// 字形と質感は焼いた版（<c>Resources/Title/MawarimiTitle</c>・焼くのは
    /// <c>tools/make-title-art.py</c>）が持ち、ここは<b>版を 3 つの奥行きへ置いて重みを配る</b>だけ。
    ///
    /// 判定はすべて純ロジック <see cref="TitleLogic"/> にあり、ここは<b>観測と配布</b>だけを持つ
    /// （<see cref="IntroDirector"/> と同じ流儀）。
    ///
    /// 立ち位置は<b>導入の段 0（開始待ち）に被さる薄い層</b>。<c>ShowPhase</c> も
    /// <see cref="IntroStage"/> も増やしていないので、ゲート・終了判定・heartbeat・卓・
    /// シミュレータの分岐はどれも変わらない。
    ///
    /// ⚠ <b>壁を一瞬も見せない担保</b>（依頼の要求）。段 0 では <see cref="IntroDirector"/> が毎フレーム
    /// <see cref="SealedBox.Apply"/> / <see cref="ContainmentShell.Apply"/> を呼んでいるので、
    /// タイトルが立っている間ずっと<b>封印の箱は既に描かれている</b>。タイトルの黒は
    /// 封印の箱（queue 4920）より<b>後</b>（4950）に alpha を 1 へ戻しているだけなので、
    /// alpha を 0 へ戻せばその箱がそのまま現れる ＝ 開ける途中に壁が覗くフレームが構造的に無い。
    /// 念のため <see cref="ConcealReady"/> が立つのを待ってから開くが、
    /// <b>待ちは <see cref="TitleLogic.ConcealWaitMaxSec"/> で必ず切れる</b>（ラッチにしない）。
    ///
    /// ⚠ <b>失敗したら素通しに倒す</b>。シェーダやテクスチャが解決できず
    /// <see cref="IsBuilt"/> が false のときは <see cref="IsBlocking"/> も false になり、
    /// 導入は従来どおり始まる。ここをラッチにすると、タイトルが組めない現場で
    /// <b>体験が二度と始まらない</b>（2026-07-31 のシェーダ剥がれと同型の事故）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TitleScreen : MonoBehaviour
    {
        [Header("References（未配線でも実行時に自己解決する）")]
        [SerializeField] private ShowRunDirector? runDirector;
        [SerializeField] private IntroDirector? introDirector;
        [SerializeField] private SealedBox? sealedBox;
        [SerializeField] private ContainmentShell? shell;
        [SerializeField] private ShowControlClient? showControl;

        [Tooltip("頭の Transform。null なら CenterEyeAnchor を名前で探す。")]
        [SerializeField] private Transform? head;

        [Tooltip("タイトルを出すか。現場で切り分けるための逃げ道（本番は ON）。")]
        [SerializeField] private bool titleEnabled = true;

        [Tooltip("文字までの距離 (m)。2026-08-13 に 2.0 → 2.6（近すぎて見づらいという判定）。" +
                 "本編のスクリーンは 2.0m なので、題字はその奥に立つ。")]
        [SerializeField, Min(0.5f)] private float distanceM = 2.6f;

        [Tooltip("版（1024px の縦）の高さ (m)。距離 2.6m で 1.30m ＝ 横幅 2.60m ＝ 見かけ 53°。" +
                 "「廻」の実寸はこの 51%（＝ 0.66m ・見かけ 14°）。")]
        [SerializeField, Min(0.05f)] private float titleHeightM = 1.30f;

        [Tooltip("視線中心からどれだけ下に置くか (度)。少し見下ろす位置に置くと据わりがよい。")]
        [SerializeField, Range(-20f, 20f)] private float pitchOffsetDeg = 2.0f;

        // ---- 構造の値。**const にする**。SerializeField にすると既存シーン YAML に未記載で
        //      0 と読まれ、層が重なって奥行きが黙って消える
        //      （unity-prefab-fields の罠。LongPressSec と同じ手当て）。
        /// <summary>
        /// 3 つの層の奥行き (m)。添え（払い・ルビ）を奥、主（廻・リ）を中、朱（視）を手前。
        ///
        /// ⚠ <b>版を押し出さない。</b> 明朝の横画は髪の毛ほど細く、層を重ねると潰れて
        /// 「太いゴシック」になる。VR の立体感は両眼視差が主役なので、**平らな版のまま
        /// 層を離す**方が字を殺さずに奥行きが出る。10cm 離せば 2m 先でも視差 0.1° ＝
        /// 立体視の閾値（0.01° 前後）の 10 倍あり、はっきり分かれて見える。
        /// </summary>
        private const float LayerZDeco = 0.10f;
        private const float LayerZMain = 0f;
        private const float LayerZAccent = -0.06f;

        /// <summary>黒の面までの距離 (m)。文字より手前に置くと文字を隠すので必ず奥。</summary>
        private const float VeilDistanceM = 0.30f;
        /// <summary>黒の面の大きさ (m)。距離 0.3m でこの大きさなら視界を覆い切る（IntroVeil と同値）。</summary>
        private const float VeilSizeM = 2.0f;

        // ---- 追従 ----------------------------------------------------------------
        // ⚠⚠ **本編のスクリーンと同じ法則で追う**（2026-08-13・LEDGER 0029）。
        //    それまでは頭に 5Hz で遅れて付いてくるだけだったので、**上下に振っても題字が
        //    眼から離れず、目の前にぴったり貼り付いて見づらかった**。
        //    このプロジェクトは酔いにくい追従を既に持っている（`YawFollowLogic`）ので、
        //    値ごとそれに合わせる。**ヨーだけ追い、上下と傾きは追わない** —
        //    見上げれば題字は下に残る ＝ 眼に貼り付かない。
        //
        // 値は `HeadYawFollow` が唯一の供給元（`ScreenAnchor` と対）。**ここに数字を書かない** —
        // 書くと体験の中で追従の癖が 2 種類になる。
        private const float YawDeadzoneDeg = HeadYawFollow.YawDeadzoneDeg;
        private const float YawTrailDeg = HeadYawFollow.YawTrailDeg;   // 頭の正面ちょうどを目指して、そこで止まる
        private const float SmoothTimeSec = HeadYawFollow.SmoothTimeSec;
        private const float MaxYawSpeedDegPerSec = HeadYawFollow.MaxYawSpeedDegPerSec;
        private const float CatchUpThresholdDeg = HeadYawFollow.CatchUpThresholdDeg;
        private const float CatchUpBoost = HeadYawFollow.CatchUpBoost;
        /// <summary>これを超える dt は着脱・pause 明けとみなし、種を置き直して 1 フレーム進めない。</summary>
        private const float ResumeGapSec = HeadYawFollow.ResumeGapSec;

        // ⚠ **スクリーンに見せないための仕掛けはこの 2 つ**（ユーザー指示「スクリーンっぽさは
        //    感じさせないように」）: ①版を 3 つの奥行きへ離してある（両眼視差で層が分かれる）
        //    ②ごく遅い揺らぎ（0.3°/s 未満）。**枠も縁も付けない**。
        //    揺らぎを消すと、水平で正対する板 ＝ 掲示物になる。
        private const float DriftYawDeg = 2.2f;
        private const float DriftPitchDeg = 1.1f;
        private const float DriftYawSec = 13f;
        private const float DriftPitchSec = 17f;

        /// <summary>版の縦横比。<b>tools/make-title-art.py の W / H と同じ</b>（2048 / 1024）。</summary>
        private const float ArtAspect = 2f;

        /// <summary>
        /// 面を版よりどれだけ大きく張るか。<b>渦で回った墨が枠で切れないための余白</b>。
        /// 縦は版の高さの半分に対して墨の外縁が 1.58 倍なので、1.8 倍あれば掃く円が収まる。
        /// 横は版が元から 2 倍あるので足りている。
        /// </summary>
        private const float CanvasX = 1.0f;
        private const float CanvasY = 1.8f;

        /// <summary>隠すものが「立っている」とみなす不透明度。</summary>
        private const float ConcealMin = 0.9f;

        public const string VeilShaderName = "FixedCamVr/TitleVeil";
        public const string GlyphShaderName = "FixedCamVr/TitleGlyph";
        public const string ArtResourcePath = "Title/MawarimiTitle";

        private static readonly int OpacityId = Shader.PropertyToID("_Opacity");
        private static readonly int ArtId = Shader.PropertyToID("_Art");
        private static readonly int RevealId = Shader.PropertyToID("_Reveal");
        private static readonly int DissolveId = Shader.PropertyToID("_Dissolve");
        private static readonly int SwirlId = Shader.PropertyToID("_Swirl");
        private static readonly int ArtAspectId = Shader.PropertyToID("_ArtAspect");
        private static readonly int FlashPosId = Shader.PropertyToID("_FlashPos");
        private static readonly int FlashAmtId = Shader.PropertyToID("_FlashAmt");
        private static readonly int PhaseSecId = Shader.PropertyToID("_PhaseSec");

        private readonly TitleLogic _logic = new TitleLogic();

        private Transform? _veilQuad;
        private Transform? _lagRoot;
        private Transform? _glyphRoot;
        private float _glyphYOffset;
        private MeshRenderer? _veilRenderer;
        private MeshRenderer? _glyphRenderer;
        private Material? _veilMat;
        private Material? _glyphMat;
        private Mesh? _veilMesh;
        private Mesh? _glyphMesh;
        private bool _subscribed;
        private bool _dismissRequested;
        private readonly YawFollowLogic _yawFollow = new YawFollowLogic();
        private bool _yawSeeded;
        private float _driftSec;
        private bool _warnedNotBuilt;

        /// <summary>
        /// 実体（面・文字・マテリアル）を組めたか。<b>false ならタイトルは一生出ない</b> —
        /// シェーダがビルドから剥がれた / 版のテクスチャが見つからない、のどちらか。
        /// テレメトリが <c>titleBuilt=</c> で出す（「重みが動いた」ではなく「画に出た」の側）。
        /// </summary>
        public bool IsBuilt => _veilRenderer != null && _glyphRenderer != null;

        /// <summary>いま画面を持っているか。<b>組めていないときは必ず false</b>（素通しに倒す）。</summary>
        public bool IsBlocking => IsBuilt && titleEnabled && _logic.Active;

        /// <summary>A を待っているか（実行体が触覚・案内を出す判断に使う）。</summary>
        public bool AwaitingInput => IsBlocking && _logic.AwaitingInput;

        public TitleStage Stage => _logic.Stage;

        /// <summary>直近に書いた黒の不透明度。診断・テレメトリ用。</summary>
        public float AppliedVeil { get; private set; }

        /// <summary>直近に書いた文字の不透明度。診断・テレメトリ用。</summary>
        public float AppliedGlyph { get; private set; }

        private void Awake()
        {
            ResolveRefs();
            Build();
            Hide();
        }

        private void OnEnable()
        {
            ResolveRefs();
            if (runDirector != null && !_subscribed)
            {
                runDirector.PhaseChanged += OnPhaseChanged;
                runDirector.RunRestarted += OnRunRestarted;
                _subscribed = true;
                if (runDirector.Phase == ShowPhase.Intro) BeginTitle();
                else _logic.Disable();
            }
        }

        private void OnDisable()
        {
            if (runDirector != null && _subscribed)
            {
                runDirector.PhaseChanged -= OnPhaseChanged;
                runDirector.RunRestarted -= OnRunRestarted;
            }
            _subscribed = false;
            _logic.Disable();
            Hide();
        }

        private void OnDestroy()
        {
            if (_veilMat != null) DestroySafe(_veilMat);
            if (_glyphMat != null) DestroySafe(_glyphMat);
            if (_veilMesh != null) DestroySafe(_veilMesh);
            if (_glyphMesh != null) DestroySafe(_glyphMesh);
            _veilMat = null; _glyphMat = null; _veilMesh = null; _glyphMesh = null;
        }

        private void ResolveRefs()
        {
            if (runDirector == null) runDirector = FindObjectOfType<ShowRunDirector>();
            if (introDirector == null) introDirector = FindObjectOfType<IntroDirector>();
            if (sealedBox == null) sealedBox = FindObjectOfType<SealedBox>();
            if (shell == null) shell = FindObjectOfType<ContainmentShell>();
            if (showControl == null) showControl = FindObjectOfType<ShowControlClient>();
            if (head == null)
            {
                var anchor = GameObject.Find("CenterEyeAnchor");
                if (anchor != null) head = anchor.transform;
            }
        }

        private void OnPhaseChanged(ShowPhase phase)
        {
            if (phase == ShowPhase.Intro) BeginTitle();
            else { _logic.Disable(); Hide(); }
        }

        // ▶ ラン開始（体験者交代）。相が Intro → Intro だと PhaseChanged が飛ばないので、
        // ここで出し直さないと次の体験者にタイトルが出ない（IntroDirector と同じ手当て）。
        private void OnRunRestarted()
        {
            if (runDirector != null && runDirector.Phase == ShowPhase.Intro) BeginTitle();
        }

        private void BeginTitle()
        {
            _dismissRequested = false;
            _yawSeeded = false;
            _driftSec = 0f;
            if (!titleEnabled || !IsBuilt)
            {
                if (!IsBuilt && !_warnedNotBuilt)
                {
                    _warnedNotBuilt = true;
                    Debug.LogWarning("[Title] 実体を組めていないのでタイトルは出しません（導入はそのまま始まります）");
                }
                _logic.Disable();
                Hide();
                return;
            }
            _logic.Begin();
        }

        /// <summary>
        /// A（右コントローラ）。<b>受け取れたら true</b> を返す。
        ///
        /// ⚠ <b>2026-08-12 から段で意味が変わる</b>（ユーザー指示）:
        ///   真っ暗で待っている（<see cref="TitleStage.Wait"/>）→ <b>題字を呼び出す</b>
        ///   題字が立っている → 閉じる（通常は 2 秒で自動的に閉じるので現場の逃げ道）
        ///
        /// false は「タイトルが立っていない・実体を組めていない」で、押下は何にも繋がらない
        /// （Normal での A はこれ 1 つだけ。カメラ手動送りは 2026-08-12 に撤去した）。
        /// </summary>
        public bool RequestAdvance()
        {
            if (!IsBlocking) return false;
            // 譲っている間（位置合わせ・ステータス表示）は時計が止まっているので、受けても何も起きない。
            // **受けたふりをしない** — 呼び出し側はこの戻り値で触覚とログを出す。
            if (IsYielding) return false;
            if (_logic.Stage != TitleStage.Wait && !_logic.GlyphShowing) return false;
            _dismissRequested = true;
            return true;
        }

        /// <summary>
        /// いま画面を譲っているか（位置合わせ中 / スタッフがステータスを開いている）。
        ///
        /// ⚠ <b><see cref="IsBlocking"/> には含めない。</b> あれは「人が始めてよいと言ったか」
        /// （<c>ShowControlClient.StartAuthorized</c>）の供給元なので、譲るたびに false になると
        /// <b>スタッフが右 B を押しただけで体験が始まる</b>。
        /// </summary>
        private bool IsYielding =>
            (showControl?.CourseRegistrationActive ?? false) || (showControl?.StatusVisible ?? false);

        /// <summary>
        /// いま譲っているか（テレメトリ用）。<b>譲っている間は段が Wait のままでも黒は 0</b> なので、
        /// これを出さないと判定が「Wait なのに黒が立っていない」と誤って FAIL を出す
        /// （2026-08-14 の走行で実際に出した）。
        /// </summary>
        public bool Yielding => IsYielding;

        /// <summary>題字が立っているか（音の一撃を鳴らす縁）。</summary>
        public bool GlyphShowing => IsBuilt && titleEnabled && _logic.GlyphShowing;

        /// <summary>
        /// もう閉じ切っているか（＝ A が空振りしても<b>異常ではない</b>）。
        /// スタッフが二度押ししただけの空振りと、出せていない空振りを分けるために公開する。
        /// </summary>
        public bool ClosedAlready => _logic.Stage == TitleStage.Done;

        /// <summary>
        /// A が効かなかった理由（<b>現場で読める 1 行</b>）。効くはずの状態なら空を返す。
        ///
        /// ⚠ これが無いと、押しても何も起きない現場で切り分ける手掛かりが 1 つも無い
        /// （成功したときだけログが出ていた）。
        /// </summary>
        public string DescribeAdvanceBlock()
        {
            if (!titleEnabled) return "タイトルを出さない設定になっている";
            if (!IsBuilt) return "タイトルの実体を組めていない（シェーダが剥がれたか版が無い）";
            if (IsYielding) return "ステータス表示か位置合わせ中は受け付けない（右 B で閉じてから押す）";
            if (_logic.Stage == TitleStage.Done) return "タイトルはもう閉じている（体験はすでに始まっている）";
            if (_logic.Stage == TitleStage.Off) return "タイトルの段が Off（周回リセットで出し直す）";
            if (_logic.Stage == TitleStage.Out) return "いま閉じる演出の最中";
            return "";
        }

        /// <summary>演出なしで畳む（卓の ⏭ 等で導入が段 0 を出たとき）。</summary>
        public void ForceClose()
        {
            _logic.ForceClose();
            Hide();
        }

        private void Update()
        {
            if (_logic.Stage == TitleStage.Off || _logic.Stage == TitleStage.Done)
            {
                _dismissRequested = false;
                return;
            }

            // ⚠ 位置合わせ中は譲る。タイトルの黒は 0.3m・queue 4950 で、
            //    登録ガイダンス（StatusHud・1.6m）より手前かつ後に描かれるので、
            //    譲らないと作業中のスタッフに文字が 1 つも見えない（2026-08-07 の実害と同型）。
            //
            // ⚠⚠ **右 B のステータス表示にも同じことが起きる**（2026-08-14）。引き渡し直前の
            //    真っ暗な待ちで、スタッフがカメラの○×や位置合わせの残差を確かめようとしても
            //    **黒が StatusHud を丸ごと塗り潰す**ので「B が効いていない」としか見えなかった。
            //    どちらもコントローラを持つ人にしか起こせないので、体験者の視界に現実は漏れない。
            bool yielding = IsYielding;

            // 導入が段 0 を出てしまったら（卓の ⏭ 等）、タイトルは即座に畳む。
            // **コントローラが死んでいる現場での唯一の出口**なので消さないこと。
            if (!yielding && introDirector != null && introDirector.Active
                && introDirector.Stage != IntroStage.Black && _logic.Stage != TitleStage.Out)
            {
                ForceClose();
                return;
            }

            _logic.Tick(Time.unscaledDeltaTime, new TitleInput
            {
                dismissRequested = _dismissRequested,
                concealReady = ConcealReady(),
                suspended = yielding,
            });
            _dismissRequested = false;

            if (yielding) { Hide(); return; }
            Apply(_logic.Weights);
        }

        private void LateUpdate()
        {
            if (!IsBuilt || !_logic.Active) return;
            FollowHead();
        }

        /// <summary>
        /// 体験エリアを隠すものが実際に立っているか。<b>「重みを配った」ではなく「画に出た」の側</b>を見る。
        /// </summary>
        private bool ConcealReady()
        {
            // 導入が走っていないなら、そもそも覆いが alpha 0 を書いていない ＝ パススルーが出ない。
            // 隠すものは要らないので、待たずに開いてよい。
            if (introDirector == null || !introDirector.Active) return true;
            if (sealedBox != null && sealedBox.IsActive && sealedBox.AppliedOpacity >= ConcealMin) return true;
            if (shell != null && shell.IsActive && shell.AppliedStrength >= ConcealMin && !shell.Revealing) return true;
            return false;
        }

        // ---- 組み立て ------------------------------------------------------------

        private void Build()
        {
            if (_veilRenderer != null) return;

            Shader? veilShader = Shader.Find(VeilShaderName);
            Shader? glyphShader = Shader.Find(GlyphShaderName);
            var art = Resources.Load<Texture2D>(ArtResourcePath);
            if (veilShader == null || glyphShader == null || art == null)
            {
                Debug.LogWarning(
                    $"[Title] 実体を組めません（veil={(veilShader != null ? 1 : 0)} " +
                    $"glyph={(glyphShader != null ? 1 : 0)} art={(art != null ? 1 : 0)}）。" +
                    "シェーダは Always Included、版は Resources/Title に要る。タイトルは出ません");
                return;
            }

            // 黒の面。IntroVeil と同じ「視界を覆い切るだけ」の板。
            var veilGo = new GameObject("TitleVeilQuad");
            veilGo.transform.SetParent(transform, worldPositionStays: false);
            veilGo.transform.localPosition = new Vector3(0f, 0f, VeilDistanceM);
            veilGo.transform.localRotation = Quaternion.identity;
            veilGo.transform.localScale = new Vector3(VeilSizeM, VeilSizeM, 1f);
            _veilMesh = BuildUnitQuad();
            veilGo.AddComponent<MeshFilter>().sharedMesh = _veilMesh;
            _veilRenderer = veilGo.AddComponent<MeshRenderer>();
            _veilMat = new Material(veilShader) { name = "TitleVeil (runtime)" };
            ConfigureRenderer(_veilRenderer, _veilMat);
            _veilQuad = veilGo.transform;

            // ⚠ **スクリーンと同じ追従に乗せるのは文字だけ**（`FollowHead`）。黒の面まで乗せると、
            //    振り向いた瞬間に覆いの縁が視界へ入って現実が細く覗く
            //    （依頼の「一瞬でも壁が見えてはいけない」に反する）。覆いは頭に貼り付いたまま。
            var lagGo = new GameObject("TitleLagRoot");
            lagGo.transform.SetParent(transform, worldPositionStays: false);
            lagGo.transform.localPosition = Vector3.zero;
            lagGo.transform.localRotation = Quaternion.identity;
            _lagRoot = lagGo.transform;

            // ロゴタイプ。3 層を 1 メッシュに畳むので描画は 1 回で済む。
            var glyphGo = new GameObject("TitleGlyphMesh");
            glyphGo.transform.SetParent(lagGo.transform, worldPositionStays: false);
            glyphGo.transform.localPosition = new Vector3(0f, GlyphYOffset(), Mathf.Max(distanceM, 0.5f));
            glyphGo.transform.localRotation = Quaternion.identity;
            _glyphMesh = BuildGlyphMesh();
            glyphGo.AddComponent<MeshFilter>().sharedMesh = _glyphMesh;
            _glyphRenderer = glyphGo.AddComponent<MeshRenderer>();
            _glyphMat = new Material(glyphShader) { name = "TitleGlyph (runtime)" };
            _glyphMat.SetTexture(ArtId, art);
            // ⚠ 縦横比は**ここが唯一の供給元**。閉じるときの渦は版の中で解くので、
            //    シェーダ側に別の既定を持たせると渦が楕円になって字が横へ流れる。
            _glyphMat.SetFloat(ArtAspectId, ArtAspect);
            ConfigureRenderer(_glyphRenderer, _glyphMat);
            _glyphRoot = glyphGo.transform;
        }

        private static void ConfigureRenderer(MeshRenderer r, Material m)
        {
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            // 視錐台カリングで消えると視界に穴が空く（覆いと同じ理由）。
            r.allowOcclusionWhenDynamic = false;
            r.enabled = false;
        }

        /// <summary>
        /// 版の高さ方向の置き場所 (m)。版の中心が視線から <see cref="pitchOffsetDeg"/> 度だけ
        /// 下に来るように置く。<b>傾けない</b> — 傾けると版が台形に見えて、組んだ balance が崩れる。
        /// </summary>
        private float GlyphYOffset()
        {
            float d = Mathf.Max(distanceM, 0.5f);
            _glyphYOffset = -d * Mathf.Tan(pitchOffsetDeg * Mathf.Deg2Rad);
            return _glyphYOffset;
        }

        private static Mesh BuildUnitQuad()
        {
            var m = new Mesh { name = "TitleVeilQuad" };
            m.SetVertices(new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f),
            });
            m.SetUVs(0, new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f),
            });
            m.SetTriangles(new[] { 0, 2, 1, 2, 3, 1 }, 0);
            m.RecalculateBounds();
            return m;
        }

        /// <summary>
        /// ロゴタイプのメッシュ。**同じ版を 3 枚、別の Z に置く**（添え → 主 → 朱）。
        /// どの層かは頂点色で選び、シェーダがチャンネルを引く。
        ///
        /// ⚠ <b>奥の層から先に並べる</b>。半透明は深度で並べ替えられない（ZWrite Off / ZTest Always）ので、
        /// メッシュ内の三角形の順序がそのまま描画順になる。手前から並べると奥が上書きして層が裏返る。
        /// </summary>
        private Mesh BuildGlyphMesh()
        {
            float h = Mathf.Max(titleHeightM, 0.05f);
            float w = h * ArtAspect;

            var verts = new Vector3[3 * 4];
            var uv0 = new Vector2[3 * 4];
            var uv1 = new Vector2[3 * 4];
            var cols = new Color[3 * 4];
            var tris = new int[3 * 6];
            int v = 0, t = 0;

            // 添え（払い・ルビ・罫）→ 主（廻・リ）→ 朱（視）の順に、奥から手前へ。
            AddQuad(verts, uv0, uv1, cols, tris, ref v, ref t, w, h, LayerZDeco,  new Color(0f, 0f, 1f, 1f));
            AddQuad(verts, uv0, uv1, cols, tris, ref v, ref t, w, h, LayerZMain,  new Color(1f, 0f, 0f, 1f));
            AddQuad(verts, uv0, uv1, cols, tris, ref v, ref t, w, h, LayerZAccent, new Color(0f, 1f, 0f, 1f));

            var m = new Mesh { name = "TitleGlyphMesh" };
            m.SetVertices(verts);
            m.SetUVs(0, uv0);
            m.SetUVs(1, uv1);
            m.SetColors(cols);
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            return m;
        }

        /// <summary>
        /// 版 1 枚を、指定の奥行きに、縦横比を保って置く。
        ///
        /// ⚠⚠ <b>面は版より大きく張る</b>（<see cref="CanvasY"/>）。版は 2:1 で横に長く、
        /// 閉じるときに渦が字を回すので、<b>横の端にあった墨が縦へ回り込む</b>。
        /// 面が版どおり（縦が版の高さ）だと、その瞬間に<b>枠で切れて見切れる</b>
        /// （2026-08-13 の指摘）。墨の外縁は版の高さの半分の 1.58 倍の円を掃くので、
        /// 縦にその円が入るだけ張る。<b>uv は 0..1 の外へはみ出させる</b>ので版は伸びない
        /// （外側は空。シェーダの <c>InArt</c> が切る）。
        /// </summary>
        private void AddQuad(Vector3[] verts, Vector2[] uv0, Vector2[] uv1, Color[] cols, int[] tris,
                             ref int v, ref int t, float w, float h, float z, Color col)
        {
            int b = v;
            float hw = w * 0.5f * CanvasX;
            float hh = h * 0.5f * CanvasY;
            verts[v + 0] = new Vector3(-hw, -hh, z);
            verts[v + 1] = new Vector3(hw, -hh, z);
            verts[v + 2] = new Vector3(-hw, hh, z);
            verts[v + 3] = new Vector3(hw, hh, z);

            // 版は 1 枚まるごと使う。PNG の 1 行目が v=1（上）に入るので上下は合っている。
            // 面を広げたぶん uv は 0..1 の外まで伸びる（そこは空）。
            float u0 = 0.5f - 0.5f * CanvasX, u1 = 0.5f + 0.5f * CanvasX;
            float v0 = 0.5f - 0.5f * CanvasY, v1 = 0.5f + 0.5f * CanvasY;
            uv0[v + 0] = new Vector2(u0, v0);
            uv0[v + 1] = new Vector2(u1, v0);
            uv0[v + 2] = new Vector2(u0, v1);
            uv0[v + 3] = new Vector2(u1, v1);

            uv1[v + 0] = new Vector2(u0, v0);
            uv1[v + 1] = new Vector2(u1, v0);
            uv1[v + 2] = new Vector2(u0, v1);
            uv1[v + 3] = new Vector2(u1, v1);

            cols[v + 0] = col; cols[v + 1] = col; cols[v + 2] = col; cols[v + 3] = col;

            tris[t + 0] = b + 0; tris[t + 1] = b + 2; tris[t + 2] = b + 1;
            tris[t + 3] = b + 2; tris[t + 4] = b + 3; tris[t + 5] = b + 1;
            v += 4; t += 6;
        }

        // ---- 配布 ----------------------------------------------------------------

        /// <summary>重みを面へ流す。<b>判断はしない</b>（値を書くだけ）。</summary>
        public void Apply(in TitleWeights w) => Apply(w, 0f);

        /// <summary>Editor プレビュー用。<paramref name="phaseSec"/> で光の走りの時刻を外から与える。</summary>
        public void Apply(in TitleWeights w, float phaseSec)
        {
            if (!IsBuilt) return;
            AppliedVeil = Mathf.Clamp01(w.veil);
            AppliedGlyph = Mathf.Clamp01(w.glyph);

            if (_veilMat != null && _veilRenderer != null)
            {
                _veilMat.SetFloat(OpacityId, AppliedVeil);
                _veilMat.SetFloat(PhaseSecId, phaseSec);
                _veilRenderer.enabled = AppliedVeil > 0.002f;
            }
            if (_glyphMat != null && _glyphRenderer != null && _glyphRoot != null)
            {
                _glyphMat.SetFloat(OpacityId, AppliedGlyph);
                _glyphMat.SetFloat(RevealId, Mathf.Clamp01(w.reveal));
                _glyphMat.SetFloat(DissolveId, Mathf.Clamp01(w.dissolve));
                _glyphMat.SetFloat(SwirlId, Mathf.Clamp01(w.swirl));
                _glyphMat.SetFloat(FlashPosId, w.flashPos);
                _glyphMat.SetFloat(FlashAmtId, Mathf.Clamp01(w.flashAmt));
                _glyphMat.SetFloat(PhaseSecId, phaseSec);
                _glyphRenderer.enabled = AppliedGlyph > 0.002f;
                // 手前が正の push。local z は奥が正なので符号を反す。
                // ⚠ **奥へは退かない**（2026-08-13）。ここに負の値を積むと題字が一様に小さくなり、
                //    「焼けて消えた」ではなく「遠ざかって箱に吸い込まれた」に見える。
                //    退く代わりに版の中で中心へ巻き込む（`TitleWeights.swirl`）。
                _glyphRoot.localPosition =
                    new Vector3(0f, _glyphYOffset, Mathf.Max(distanceM, 0.5f) - w.pushM);
            }
        }

        /// <summary>畳む（本編・終了・位置合わせ中）。</summary>
        public void Hide()
        {
            AppliedVeil = 0f;
            AppliedGlyph = 0f;
            if (_veilRenderer != null) _veilRenderer.enabled = false;
            if (_glyphRenderer != null) _glyphRenderer.enabled = false;
        }

        /// <summary>
        /// <b>本編のスクリーンと同じ追従</b>（<see cref="YawFollowLogic"/>）。
        /// ヨーだけを緩急つきで追い、<b>上下と傾きは追わない</b>ので、見上げれば題字は下に残る。
        ///
        /// ⚠ <b>黒の面はこれに乗せない。</b> 覆いは頭に貼り付いたままでなければ、
        /// 振り向いた瞬間に縁が視界へ入って現実が細く覗く（依頼の絶対条件）。
        /// 動かすのは題字だけ。
        /// </summary>
        private void FollowHead()
        {
            if (head == null || _lagRoot == null) return;
            float dt = Time.unscaledDeltaTime;
            float headYaw = head.eulerAngles.y;

            // 着脱・pause 明けの巨大 dt は SmoothDamp をスナップさせる。種を置き直して進めない。
            if (!_yawSeeded || dt > ResumeGapSec)
            {
                _yawFollow.Reseat(_yawSeeded ? _yawFollow.CurrentYaw : headYaw);
                _yawSeeded = true;
                ApplyPose(_yawFollow.CurrentYaw);
                return;
            }

            _driftSec += dt;
            ApplyPose(_yawFollow.Step(headYaw, dt, YawDeadzoneDeg, YawTrailDeg, SmoothTimeSec,
                                      MaxYawSpeedDegPerSec, CatchUpThresholdDeg, CatchUpBoost));
        }

        /// <summary>題字を頭の前 <c>distanceM</c>・高さ head.y + オフセット に、指定ヨーで置く。</summary>
        private void ApplyPose(float yaw)
        {
            if (head == null || _lagRoot == null) return;
            // 静止していても層が分かるよう、ごく遅い揺らぎを足す（0.3°/s 未満）。
            // **これがスクリーンっぽさを消す唯一の動き**なので消さないこと。
            float dy = DriftYawDeg * Mathf.Sin(_driftSec * Mathf.PI * 2f / DriftYawSec);
            float dp = DriftPitchDeg * Mathf.Sin(_driftSec * Mathf.PI * 2f / DriftPitchSec);
            // ⚠ 位置は頭に付いてくるが、**姿勢はヨーだけ**（＋揺らぎ）。頭のピッチは入れない。
            _lagRoot.SetPositionAndRotation(head.position, Quaternion.Euler(dp, yaw + dy, 0f));
        }

        private static void DestroySafe(Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

#if UNITY_EDITOR
        /// <summary>Editor プレビューから実体だけ組ませる（Play しないで絵を出すため）。</summary>
        public void EditorBuild()
        {
            Build();
        }
#endif
    }
}
