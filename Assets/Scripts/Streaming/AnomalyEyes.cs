#nullable enable

using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>スクリーンの外の黒い背景に、360 度いちめんの目が開く</b>異変（<c>canon/LEDGER.md</c> 0075）。
    ///
    /// 進み方は <see cref="AnomalyEyesLogic"/>（純ロジック・テストあり）、座席表は
    /// <see cref="AnomalyEyesMesh"/>、目の形は <c>FixedCamVr/AnomalyEyes</c> シェーダ。
    /// ここがやるのは <b>3 つだけ</b> — 実体を組む / 頭に付いて回る / 数を材質へ書く。
    ///
    /// <b>どの区間で出すかはカットが決める</b>（<c>steps[].eyes</c> → <see cref="TakeRunner"/> →
    /// <see cref="Apply"/>）。⚠⚠ <b>いつ開き始めていつ閉じ始めるかはカットではなく体験者の居場所</b>
    /// （<see cref="EyesCueLogic"/>・2026-08-19・<c>canon/LEDGER.md</c> 0093）。
    /// 演出の仕組みへ乗せてあるので、
    /// <list type="bullet">
    ///   <item>報告で消える（<see cref="ShowTakeDef.dismissible"/>・<c>LEDGER</c> 0050）</item>
    ///   <item>区間を出れば<b>流しきって</b>畳まれる（<c>policy:"yield"</c> → <see cref="Release"/>）</item>
    ///   <item>中止・ラン開始・watchdog で必ず落ちる（<c>TakeRunner.CleanupActive</c> → <see cref="Abort"/>）</item>
    /// </list>
    /// が<b>ぜんぶ既存の経路のまま効く</b>。新しい掛けっぱなしの状態を作らない
    /// （この codebase は「凍結が解けない」を 4 回踏んでいる）。
    ///
    /// ⚠ <b>群れは頭の位置に付いて動く</b>（向きは付いてこない）。歩いて群れの外へ出られると
    ///   「目の壁」に見えてしまうので位置だけ追う。向きを追うと HUD に見えるので追わない。
    ///
    /// ⚠ <b>描くのは Background+100（1100）</b>。本編のスクリーン（Geometry / 不透明）が後から
    ///   上書きするので、目は<b>スクリーンの外にしか出ない</b>。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AnomalyEyes : MonoBehaviour
    {
        /// <summary>目のシェーダ名。ビルドから剥がれないよう Always Included にも入れてある。</summary>
        public const string ShaderName = "FixedCamVr/AnomalyEyes";

        /// <summary>視界ジャックのシェーダ名（<c>canon/LEDGER.md</c> 0099）。同じく Always Included。</summary>
        public const string JackShaderName = "FixedCamVr/EyeJack";

        // 視界ジャックの面の置き方。頭に固定した quad 1 枚（乗っ取りなので HUD 的な固定が正しい —
        // 群れが「向きを追うと HUD に見える」から追わないのと逆の理由で、ここは追う）。
        private const float JackDistM = 1.8f;      // 眼からの距離。近すぎると輻輳の負担（HmdTextStyle と同域）
        private const float JackHalfFovXDeg = 55f; // 覆う半画角。Quest 3 の表示画角より広めに取る
        private const float JackHalfFovYDeg = 50f;
        private const float JackMargin = 1.35f;    // レンズ周縁と首振り 1 フレームの余白

        [Tooltip("追従する頭（CenterEyeAnchor）。null なら名前で探し、無ければ Camera.main。")]
        [SerializeField] private Transform? head;

        [Tooltip("位置合わせ中に引っ込めるための ShowControlClient。null なら実行時に探す。")]
        [SerializeField] private ShowControlClient? showControl;

        /// <summary>
        /// 出荷する明るさ。<b>Editor プレビュー（<c>EyesPreview</c>）もこれを書く</b> —
        /// 書かないとプレビューだけシェーダ既定の 1.0 で描かれ、<b>明るさを触っても絵が変わらない</b>。
        ///
        /// ⚠ <b>シーン（<c>Main.unity</c> の <c>[Eyes]</c>）に焼かれている値が実物</b>。ここの既定が効くのは
        /// <c>[Eyes]</c> を作り直したときだけなので、<b>片方だけ動かすと黙って食い違う</b>。
        /// 2026-08-23 に 0.85 → 0.50（ユーザー赤入れ「目が明るすぎる。もう少し暗くしてほしい」）。
        /// </summary>
        public const float DefaultGain = 0.5f;

        [Tooltip("目の明るさ。上げすぎると「発光する記号」に見える。")]
        [SerializeField, Range(0f, 2f)] private float gain = DefaultGain;

        /// <summary>出荷する瞬きの量。<see cref="DefaultGain"/> と同じ理由でプレビューも書く。</summary>
        public const float DefaultBlink = 1f;

        [Tooltip("瞬きの量（0 = 瞬きしない）。")]
        [SerializeField, Range(0f, 1f)] private float blink = DefaultBlink;

        /// <summary>
        /// 出荷する目の色。<b>シェーダ既定（生成りに近い白）とは別の値</b>なので、
        /// プレビューが書かないと**プレビューだけ色が違う**。
        /// </summary>
        public static readonly Color DefaultColor = new(1.0f, 0.93f, 0.84f, 1f);

        [Tooltip("目の色。作品の色（暖色）へ寄せた白（参考画像の目は白い）。")]
        [SerializeField] private Color color = new(1.0f, 0.93f, 0.84f, 1f);

        private static readonly int BigId = Shader.PropertyToID("_EyeBig");
        private static readonly int FieldId = Shader.PropertyToID("_EyeField");
        private static readonly int SpanId = Shader.PropertyToID("_EyeSpan");
        private static readonly int DensityId = Shader.PropertyToID("_EyeDensity");
        private static readonly int FadeId = Shader.PropertyToID("_EyeFade");
        private static readonly int IntensityId = Shader.PropertyToID("_EyeIntensity");
        private static readonly int TimeId = Shader.PropertyToID("_EyeTime");
        private static readonly int GainId = Shader.PropertyToID("_EyeGain");
        private static readonly int BlinkId = Shader.PropertyToID("_EyeBlink");
        // 待機中の視線移動の強さ（`canon/LEDGER.md` 0084）。どの目がどこを見るかはシェーダが seed で散らす。
        private static readonly int GazeId = Shader.PropertyToID("_EyeGaze");
        // 笑い（下瞼が持ち上がる）と、閉じ中か（断片を止める）。`canon/LEDGER.md` 0085。
        private static readonly int SmileId = Shader.PropertyToID("_EyeSmile");
        private static readonly int ClosingId = Shader.PropertyToID("_EyeClosing");
        private static readonly int ColorId = Shader.PropertyToID("_EyeColor");
        private static readonly int JackTexId = Shader.PropertyToID("_JackTex");
        private static readonly int JackOnId = Shader.PropertyToID("_JackOn");
        private static readonly int JackUvId = Shader.PropertyToID("_JackUv");

        private readonly AnomalyEyesLogic _logic = new();
        private readonly EyeAnchorLogic _anchor = new();
        // 開き始め・閉じ始めを体験者の居場所で決める層（canon/LEDGER.md 0093）。
        private readonly EyesCueLogic _cue = new();
        // 視界ジャック（canon/LEDGER.md 0099）。目の内側の段 — 闇の所有者を 2 つにしない。
        private readonly EyeJackLogic _jack = new();

        private MeshRenderer? _renderer;
        private Material? _mat;
        private Mesh? _mesh;
        private EyeSeat[] _seats = System.Array.Empty<EyeSeat>();
        private float _clock;

        // 視界ジャックの実体（頭に固定する全視界 quad）と、当日写真の店。
        private EyeJackPhotoStore? _photos;
        private Transform? _jackQuad;
        private MeshRenderer? _jackRenderer;
        private Material? _jackMat;
        private Mesh? _jackMesh;
        private Texture2D[] _jackShots = System.Array.Empty<Texture2D>();
        private int _jackShownIndex = -1;

        // カットが言った値（毎フレームではなくカットの縁でだけ書き換わる。左右分割と同じ流儀）。
        private float _wanted;
        private bool _jackWanted;

        /// <summary>
        /// 実体（メッシュ + 材質）を組めたか。<b>false なら目は一生出ない</b> —
        /// <see cref="ShaderName"/> がビルドから剥がれた状態（2026-07-31 に覆いで踏んだ形）。
        /// テレメトリが <c>eyesBuilt=</c> で出す。
        /// </summary>
        public bool IsBuilt => _renderer != null;

        /// <summary>いまの段（診断・テレメトリ用）。</summary>
        public EyesStage Stage => _logic.Stage;

        /// <summary>直近に材質へ書いた「残りの目の広がり」0..1。</summary>
        public float AppliedField { get; private set; }

        /// <summary>直近に材質へ書いた「大きい目の開き」0..1。</summary>
        public float AppliedBig { get; private set; }

        /// <summary>直近に材質へ書いた不透明度 0..1（0 なら 1 画素も出ていない）。</summary>
        public float AppliedFade { get; private set; }

        /// <summary>
        /// いま開いている目の数（<b>画に出た側</b>の観測）。
        /// 「カットが指した」ではなく「何個ぶんの目が実際に開いているか」。
        /// </summary>
        public int OpenCount { get; private set; }

        /// <summary>座席の総数（大きい目を含む）。0 なら座席表を組めていない。</summary>
        public int SeatCount => _seats.Length;

        /// <summary>
        /// 体験者が区間のどこまで来たか 0..1。<b>-1 = 位置では測っていない</b>
        /// （未登録・layout 不在 ＝ 従来どおりカットの終わりで畳む）。テレメトリ用。
        /// </summary>
        public float SpanProgress01 => _spanProgress01;
        private float _spanProgress01 = -1f;

        /// <summary>終了演出へ入ったか（半ばを過ぎた / 区間を出た）。テレメトリ用。</summary>
        public bool IsFinishing => _cue.Finishing;

        /// <summary>いまの進みの速さ（1 = 著作どおり / 2 = 追い上げ中）。テレメトリ用。</summary>
        public float Rate => _cue.Rate;

        /// <summary>
        /// <b>いまカットが「目を出せ」と言っているか</b>（<c>steps[].eyes</c> の値）。テレメトリ用。
        ///
        /// ⚠⚠ <b>これが無いと、目が開いた理由を外から切り分けられない</b>（2026-08-23）。
        /// 4 周目 A で目が 1 つ開く報告を追ったとき、著作は 3 周目 C しか目を宣言していないのに
        /// 実機では開いていた。開いた数・不透明度・速さは出していたが、
        /// <b>「誰が開けと言ったか」を 1 つも出していなかった</b>ので、
        /// カットの指示が残っているのか、流しきりが降りていないのかを区別できなかった。
        /// </summary>
        public float WantedLevel => _wanted;

        /// <summary>流しきりの状態（<see cref="EyesCueLogic"/>）。テレメトリ用。</summary>
        public bool CueRunning => _cue.Running;

        // ---- 視界ジャックの観測（canon/LEDGER.md 0099。テレメトリ・卓 heartbeat 用）----

        /// <summary>ジャックの面（quad + シェーダ）を組めたか。false なら一生出ない。</summary>
        public bool JackBuilt => _jackRenderer != null;

        /// <summary>いま視界を乗っ取っているか。</summary>
        public bool JackActive => _jack.Active;

        /// <summary>端末に用意できた（デコード済みの）写真の枚数。</summary>
        public int JackPhotoCount => _photos?.ReadyCount ?? 0;

        /// <summary>この発火で出す枚数と 1 枚の尺（begin のログ用）。</summary>
        public int JackShowCount => _jack.ShowCount;
        public float JackPerSec => _jack.PerSec;

        /// <summary>実際に画へ出した写真の累計（「画に出た側」の観測）。</summary>
        public int JackShownTotal { get; private set; }

        /// <summary>直近の終わり方（"done" / "cut" / "wd" / "abort"。まだなら空）。</summary>
        public string JackLastEndWhy { get; private set; } = "";

        private void Awake()
        {
            Resolve();
            Build();
            Hide();
        }

        private void OnDisable()
        {
            // 掛けっぱなしにしない。次に有効化されたら必ず兆しから始まる。
            Abort();
        }

        private void OnDestroy()
        {
            if (_mat != null) DestroySafe(_mat);
            if (_mesh != null) DestroySafe(_mesh);
            if (_jackMat != null) DestroySafe(_jackMat);
            if (_jackMesh != null) DestroySafe(_jackMesh);
            _mat = null;
            _mesh = null;
            _jackMat = null;
            _jackMesh = null;
            _photos?.Dispose();
            _photos = null;
        }

        private void Resolve()
        {
            if (head == null)
            {
                var anchor = GameObject.Find("CenterEyeAnchor");
                if (anchor != null) head = anchor.transform;
                else if (Camera.main != null) head = Camera.main.transform;
            }
            if (showControl == null) showControl = FindObjectOfType<ShowControlClient>();
            // 目が開く音（0131）。掴めなければ無音で開くだけ（体験は止めない）。
            if (_sound == null) _sound = FindObjectOfType<ShowSoundDirector>();
            // 卓の heartbeat へ「この端末に写真が何枚届いたか」を出す（2 台のうち片方だけ
            // 届いていないのは無音の失敗 — canon/LEDGER.md 0099「当日にドタバタしたくない」）。
            if (showControl != null)
                showControl.EyeJackReadyCountProvider = () => _photos?.ReadyCount ?? 0;
        }

        private void Build()
        {
            if (_renderer != null) return;
            var shader = Shader.Find(ShaderName);
            if (shader == null)
            {
                // 出せないなら出さない（黒い板や白い面を視界に出す方が危ない）。
                Debug.LogWarning($"[AnomalyEyes] シェーダ {ShaderName} が見つかりません。目の異変は出ません。");
                return;
            }

            _seats = AnomalyEyesMesh.BuildSeats();
            _mesh = AnomalyEyesMesh.Build(_seats);

            var go = new GameObject("AnomalyEyesShell");
            go.transform.SetParent(transform, worldPositionStays: false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.AddComponent<MeshFilter>().sharedMesh = _mesh;

            _renderer = go.AddComponent<MeshRenderer>();
            _mat = new Material(shader) { name = "AnomalyEyes (runtime)" };
            _renderer.sharedMaterial = _mat;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            _renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            _renderer.allowOcclusionWhenDynamic = false;

            BuildJack();
        }

        /// <summary>
        /// 視界ジャックの面（頭に固定する全視界 quad）を組む。目とは独立に失敗してよい —
        /// 組めなければジャックだけが出ない（目は従来どおり）。
        /// </summary>
        private void BuildJack()
        {
            if (_jackRenderer != null) return;
            var shader = Shader.Find(JackShaderName);
            if (shader == null)
            {
                Debug.LogWarning($"[EyeJack] シェーダ {JackShaderName} が見つかりません。視界ジャックは出ません。");
                return;
            }

            float hw = JackDistM * Mathf.Tan(JackHalfFovXDeg * Mathf.Deg2Rad) * JackMargin;
            float hh = JackDistM * Mathf.Tan(JackHalfFovYDeg * Mathf.Deg2Rad) * JackMargin;
            _jackMesh = new Mesh { name = "EyeJackQuad" };
            _jackMesh.vertices = new[]
            {
                new Vector3(-hw, -hh, 0f), new Vector3(hw, -hh, 0f),
                new Vector3(-hw, hh, 0f), new Vector3(hw, hh, 0f),
            };
            _jackMesh.uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f),
            };
            _jackMesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            _jackMesh.RecalculateBounds();

            var go = new GameObject("EyeJackQuad");
            // 親の transform は群れの都合（頭の位置 + FaceHead の向き）で動くので、
            // world 座標を毎フレーム直接書く（worldPositionStays: false で入れて即上書き）。
            go.transform.SetParent(transform, worldPositionStays: false);
            go.AddComponent<MeshFilter>().sharedMesh = _jackMesh;
            _jackRenderer = go.AddComponent<MeshRenderer>();
            _jackMat = new Material(shader) { name = "EyeJack (runtime)" };
            _jackRenderer.sharedMaterial = _jackMat;
            _jackRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _jackRenderer.receiveShadows = false;
            _jackRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            _jackRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            _jackRenderer.allowOcclusionWhenDynamic = false;
            _jackRenderer.enabled = false;
            _jackQuad = go.transform;
        }

        /// <summary>
        /// カットが「目の異変を出す」と言った（<c>steps[].eyes</c>）。
        /// <paramref name="eyes"/> は<b>開く目の割合</b> 0..1（0 = 出さない）。大きい目は割合に関係なく必ず出る。
        ///
        /// ⚠ <b>毎フレームではなくカットの縁で呼ばれる</b>（左右分割・人形と同じ）。
        ///   同じ値を続けて言われても<b>進みは巻き戻らない</b>ので、カットをまたいでも 1 つの出来事として続く。
        ///
        /// <paramref name="jack"/> = 目の視界ジャック（<c>steps[].eyeJack</c>・<c>canon/LEDGER.md</c> 0099）。
        /// <paramref name="eyes"/> が 0 なら意味を持たない。同じくカットをまたいで 1 つの出来事
        /// （カット 1 → 2 の縁で二度目は始まらない — <see cref="EyeJackLogic.Spent"/>）。
        /// </summary>
        public void Apply(float eyes, bool jack = false)
        {
            float want = Mathf.Clamp01(eyes);
            // ⚠⚠ **言い始めた縁で「どの区間で言われたか」を刻む。** カットの切替は滞在 0.5 秒を
            //    待ってから進むのに、居場所（ZoneSpan）は線を跨いだ瞬間に進むので、その 0.5 秒は
            //    **前の区間のカットが「目を出す」と言ったまま、居場所だけ次の区間**になっている。
            //    刻まないと、そこで新しい出番が始まり**宣言していない区間の頭で目が 1 つ開く**
            //    （2026-08-23 の 4 周目 A）。詳しくは <see cref="EyesCueLogic.Declare"/>。
            if (want > 0f && _wanted <= 0f)
                _cue.Declare(showControl != null ? showControl.ZoneSpan : default);
            _wanted = want;
            _jackWanted = jack;
        }

        /// <summary>
        /// カットが終わった（区間を出た・演出が閉じた）。
        ///
        /// ⚠⚠ <b>ここでは切らない。流しきる</b>（2026-08-19・<c>canon/LEDGER.md</c> 0093
        /// 「演出途中にもしカメラが切り替わったら、倍速にするなどして、強制打ち切りではなく流しきって」）。
        /// 開き切っていなければ <see cref="EyesCueLogic.HurryRate"/> 倍で追い上げ、
        /// 開き切ってから <see cref="AnomalyEyesLogic.CloseSec"/> かけて閉じる（形で閉じる。薄くしない）。
        /// <b>本当に消すのは <see cref="Abort"/></b>（ラン開始・中止・位置合わせ）。
        /// ⚠ ジャックは流しきらない — 乗っ取った視界を返すのは即座でなければならない
        ///   （「今設定してるところで止める」・0099）。次の <c>LateUpdate</c> で消える。
        /// </summary>
        public void Release()
        {
            _wanted = 0f;
            _jackWanted = false;
        }

        /// <summary>
        /// <b>無かったことにする</b>（ラン開始・卓からの中止・位置合わせ・無効化）。
        /// <see cref="Release"/> と違って 1 フレームで消える —— 体験者が交代したのに
        /// 前の人の目が閉じ残ると、次の人は<b>最初から見られている</b>状態で始まる。
        /// </summary>
        public void Abort()
        {
            _wanted = 0f;
            _jackWanted = false;
            if (_jack.Active) JackLastEndWhy = "abort";
            _jack.Reset();
            _cue.Reset();
            _logic.Reset();
            _anchor.Reset();
            Hide();
            HideJack();
        }

        private void LateUpdate()
        {
            if (head == null || _sound == null) Resolve();

            float dt = Time.unscaledDeltaTime;
            _clock += dt;

            // 当日写真の同期は常に回す（位置合わせ中・演出の外でも。落とすだけで画には触らない）。
            _photos ??= new EyeJackPhotoStore();
            // ⚠ 乗っ取りの最中は差し替えを仕掛けない。`_jackShots` は Snapshot の**参照**なので、
            //    同期の完了が走ると掴んでいるテクスチャごと破棄される（機序は Tick の doc）。
            _photos.Tick(showControl, Time.unscaledTime, inUse: _jack.Active);

            // 位置合わせ中は引っ込める（現実に線を重ねて合わせる作業を邪魔しない — rules/show-design.md）。
            // ⚠ ここは「流しきる」の側ではない。合わせている人の視界から**すぐ**消す。
            if (showControl != null && showControl.CourseRegistrationActive)
            {
                if (_logic.Stage != EyesStage.Off || _cue.Running || _jack.Active) Abort();
                return;
            }

            // 開き始め・閉じ始めは体験者の居場所が決める（canon/LEDGER.md 0093）。
            // カットが言うのは「この区間で出す」までで、尺は言わない。
            ZoneSpan span = showControl != null ? showControl.ZoneSpan : default;
            _cue.Tick(_wanted > 0f, _logic.Stage, span);
            _spanProgress01 = span.valid ? span.progress01 : -1f;

            // ⚠⚠ **視界ジャックが覆っているあいだは閉じさせない**（canon/LEDGER.md 0099 / 0084）。
            //   覆いの裏で閉じるのは「閉じた」ではなく「消えた」— 0084 の赤入れ
            //   「閉じるときは、開くときと同じように緩急つけて」で作った 1.6 秒が、
            //   歩き続ける体験者では**1 フレームも見えないまま**終わる（区間の半分で
            //   終了演出に入るのと、ジャックが出るのが同じ縁だから）。
            //   ⚠ 開く側は塞がない（`_cue.Wanted` が false でも `Hold` に留めるだけ）。
            //   ⚠ 掛けっぱなしにならない — ジャックは armed が落ちた次のフレームで必ず消える。
            _logic.Tick(dt, _cue.Wanted || _jack.Active, _wanted, _cue.Rate);

            // 視界ジャック（canon/LEDGER.md 0099）。
            // ⚠ **目の Stage==Off の早期 return より前に置く。** 歩く体験者では、目が閉じ切った後も
            //   ジャックが数百 ms 残る（半分の縁で発火 → 目は追い上げ 1.6s で閉じ、写真は総尺 2.4s）。
            //   後ろに置くと、その残りのあいだ面が凍る（stale な写真が視界に貼り付いたまま）。
            DriveJack(dt);

            if (_logic.Stage == EyesStage.Off)
            {
                Hide();
                _lastOpenCount = 0;   // 次の出番で 1 発目から鳴る（0131）
                return;
            }
            if (_renderer == null || _mat == null) return;

            // 群れは頭の**位置**にだけ付いて動く。向きはワールド固定（頭に張り付くと HUD に見える）。
            if (head != null) transform.position = head.position;

            if (_logic.JustStarted) { FaceHead(); _anchor.Reset(); }
            else if (head != null)
            {
                // 大きい目が視界の外に居続けるなら、群れごと向き直す。
                // **開いているのは大きい目 1 つ（＝視界の外）だけ**の間しか回さないので、回転は見えない。
                Vector3 bigWorld = transform.rotation * AnomalyEyesMesh.BigDir;
                float off = Vector3.Angle(head.forward, bigWorld);
                if (_anchor.Tick(dt, off, _logic.AnchorLocked)) FaceHead();
            }

            _mat.SetFloat(BigId, _logic.Big);
            _mat.SetFloat(FieldId, _logic.Field);
            // ⚠ **定数ではなくロジックの値**。開くときと閉じるときで幅が違う（閉じる方が広い）。
            //   定数を直に渡すと、1 つの目が閉じるのに 0.02 秒しかかからず瞼が下りて見えない。
            _mat.SetFloat(SpanId, _logic.Span);
            _mat.SetFloat(DensityId, _logic.Density);
            _mat.SetFloat(FadeId, _logic.Fade);
            _mat.SetFloat(IntensityId, _logic.Intensity);
            _mat.SetFloat(TimeId, _clock);
            _mat.SetFloat(GainId, gain);
            _mat.SetFloat(BlinkId, blink);
            _mat.SetFloat(GazeId, _logic.Gaze);
            _mat.SetFloat(SmileId, _logic.Smile);
            _mat.SetFloat(ClosingId, _logic.Closing);
            _mat.SetColor(ColorId, color);
            _renderer.enabled = true;

            AppliedBig = _logic.Big;
            AppliedField = _logic.Field;
            AppliedFade = _logic.Fade;
            OpenCount = _seats.Length == 0
                ? 0
                : AnomalyEyesMesh.CountOpen(_seats, _logic.Big, _logic.Field, _logic.Density);

            // ⚠ **数え直した後に鳴らす**（`OpenCount` を読むので）。
            DriveEyeSfx(dt);
        }

        // ---- 目が開く音（2026-09-03・`canon/LEDGER.md` 0131）------------------------

        /// <summary>
        /// 一撃の最短間隔（秒）。
        ///
        /// ⚠⚠ <b>1 つの目に 1 発は鳴らせない。</b> 開くのは 189 個で、開眼の 3 秒に収まる
        /// （実測: 0.25 秒ごとに 10 / 17 / 0 / 47 / 40 / 32 / 24 / 17 / 2 個 ＝ 最大 188 個/秒）。
        /// 1 発ずつ鳴らせば太鼓ではなく雑音になり、6 声の発声器も尾で埋まる。
        ///
        /// 0.09 秒に間引くと <b>約 20 発</b>になり、<b>開き方の形がそのまま出る</b> ——
        /// さざめきで数発 → <b>間</b>で止まり → 一気に連なる。等間隔の連打にはならない。
        /// </summary>
        public const float EyeSfxMinIntervalSec = 0.09f;

        /// <summary>一撃ごとに振る音程の幅（±）。切替音（0.035）より広い。</summary>
        public const float EyeSfxPitchSpread = 0.05f;

        /// <summary>同・音量（± dB）。<b>連なるぶん広く取る</b>（打鍵と同じ流儀）。</summary>
        public const float EyeSfxGainSpreadDb = 2.0f;

        private ShowSoundDirector? _sound;
        private int _lastOpenCount;
        private float _eyeSfxCooldown;

        /// <summary>鳴らした一撃の累計（テレメトリ用）。<b>音は録画に映らない。</b></summary>
        public int EyeSfxCount { get; private set; }

        /// <summary>大きい目の音を鳴らしたか（1 回の出番に 1 度きり・テレメトリ用）。</summary>
        public bool EyeBigSfxFired { get; private set; }

        /// <summary>
        /// <b>目が開いた分だけ一撃を鳴らす</b>（最短間隔で間引く）。
        ///
        /// 鳴らす場所は<b>いちばん最後に開いた目の方角</b>（`canon/LEDGER.md` 0130 の 3D）。
        /// ⚠ 場所を持たせないと 20 発が同じ 1 点から来て、開いているのが「いちめん」に聞こえない。
        /// </summary>
        private void DriveEyeSfx(float dt)
        {
            if (_eyeSfxCooldown > 0f) _eyeSfxCooldown -= dt;

            // ⚠ **大きい目の音は兆しの段の頭で撃つ**（見開く瞬間ではない）。素材の頭に無音が
            //    足してあり、いちばん大きいところが見開く瞬間へ来るように焼いてある
            //    （`tools/ingest-sounds.py` の `ALIGN`）。実行時に足し引きしない。
            if (_logic.JustStarted)
            {
                _lastOpenCount = 0;
                EyeBigSfxFired = _sound != null
                                 && _sound.PlaySpot(SoundCue.EyeBig, BigEyeWorld());
                if (_sound != null && !EyeBigSfxFired)
                    Debug.LogWarning("[Eyes] 大きい目の音源がありません"
                                     + "（Resources/Sound/sfx_eye_big）。目は無音で開きます。"
                                     + "`py -3.11 tools/ingest-sounds.py --only sfx_eye_big` の後に "
                                     + "`.\\tools\\unity.ps1 menu sound-import` を走らせること。");
            }

            int now = OpenCount;
            if (now > _lastOpenCount)
            {
                // ⚠ **閉じるときは鳴らさない**（`Fading` は数が減るので、そもそもここへ来ない）。
                if (_eyeSfxCooldown <= 0f && _sound != null
                    && _sound.PlaySpot(SoundCue.EyeOpen, NewestOpenEyeWorld(),
                                       EyeSfxPitchSpread, EyeSfxGainSpreadDb))
                {
                    _eyeSfxCooldown = EyeSfxMinIntervalSec;
                    EyeSfxCount++;
                }
                _lastOpenCount = now;
            }
            else if (now < _lastOpenCount)
            {
                _lastOpenCount = now;
            }
        }

        /// <summary>大きい目のワールド位置。</summary>
        private Vector3 BigEyeWorld()
            => transform.TransformPoint(AnomalyEyesMesh.BigDir * AnomalyEyesMesh.RadiusM);

        /// <summary>
        /// <b>いちばん最後に開いた目</b>のワールド位置（開く順位がいまの進みにいちばん近いもの）。
        /// 座席が無ければ大きい目へ落とす。
        /// </summary>
        private Vector3 NewestOpenEyeWorld()
        {
            float best = -1f;
            Vector3 dir = AnomalyEyesMesh.BigDir;
            for (int i = 0; i < _seats.Length; i++)
            {
                EyeSeat s = _seats[i];
                if (s.big || s.presence > _logic.Density) continue;
                if (AnomalyEyesLogic.EyeOpen(_logic.Field, s.rank) * s.openMax
                    <= AnomalyEyesLogic.OpenEpsilon) continue;
                if (s.rank <= best) continue;
                best = s.rank;
                dir = s.dir;
            }
            return transform.TransformPoint(dir * AnomalyEyesMesh.RadiusM);
        }

        /// <summary>群れの向きを頭の水平の向きへ合わせる（大きい目が視界の端に来る）。</summary>
        private void FaceHead()
        {
            if (head == null) return;
            Vector3 f = head.forward;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-6f) return;
            transform.rotation = Quaternion.LookRotation(f.normalized, Vector3.up);
        }

        /// <summary>
        /// 視界ジャックの 1 フレーム分（判断は <see cref="EyeJackLogic"/>、ここは配線と描画だけ）。
        /// </summary>
        private void DriveJack(float dt)
        {
            bool armed = _jackWanted && _wanted > 0f;
            _jack.Tick(dt, armed, _logic.Stage, _cue.HalfReached, _photos?.ReadyCount ?? 0);

            if (_jack.JustStarted)
            {
                // 発火の瞬間の一式を写し取る（最中にリストが入れ替わっても順序が崩れない）。
                _jackShots = _photos != null ? _photos.Snapshot() : System.Array.Empty<Texture2D>();
                _jackShownIndex = -1;
                JackLastEndWhy = "";
            }
            if (_jack.FinishEyesRequested)
            {
                // 「写真が終わったら元に戻して目も消えて終わる」（0099）。効くのは次フレームの _cue.Tick。
                _cue.RequestFinish();
            }
            if (_jack.EndedWhy != "") JackLastEndWhy = _jack.EndedWhy;

            if (!_jack.Active || _jackRenderer == null || _jackMat == null
                || _jackQuad == null || head == null)
            {
                HideJack();
                return;
            }

            int idx = Mathf.Min(_jack.PhotoIndex, _jackShots.Length - 1);
            Texture2D? tex = idx >= 0 ? _jackShots[idx] : null;
            if (tex == null) { HideJack(); return; }

            // 頭に固定（視界そのものの乗っ取りなので、群れと違ってここは向きも追う）。
            _jackQuad.SetPositionAndRotation(
                head.position + head.rotation * new Vector3(0f, 0f, JackDistM), head.rotation);

            if (idx != _jackShownIndex)
            {
                _jackShownIndex = idx;
                JackShownTotal++;
                _jackMat.SetTexture(JackTexId, tex);
                _jackMat.SetVector(JackUvId, CoverUv(tex));
            }
            _jackMat.SetFloat(JackOnId, 1f);
            _jackRenderer.enabled = true;
        }

        /// <summary>
        /// cover-fit の uv 変換（xy = scale / zw = offset）。写真の中央を切り出して面を埋める —
        /// 余白（黒）を作ると「画像が浮いている」に見えて乗っ取りにならない。
        /// public なのは Editor プレビュー（EyeJackPreview）が同じ式を使うため（複製すると黙ってずれる）。
        /// </summary>
        public static Vector4 CoverUv(Texture2D tex)
        {
            float quadAspect = Mathf.Tan(JackHalfFovXDeg * Mathf.Deg2Rad)
                               / Mathf.Tan(JackHalfFovYDeg * Mathf.Deg2Rad);
            float photoAspect = tex.height > 0 ? (float)tex.width / tex.height : 1f;
            if (photoAspect > quadAspect)
            {
                float sx = quadAspect / photoAspect;
                return new Vector4(sx, 1f, (1f - sx) * 0.5f, 0f);
            }
            float sy = photoAspect / quadAspect;
            return new Vector4(1f, sy, 0f, (1f - sy) * 0.5f);
        }

        private void HideJack()
        {
            _jackShownIndex = -1;
            if (_jackRenderer != null) _jackRenderer.enabled = false;
            if (_jackMat != null) _jackMat.SetFloat(JackOnId, 0f);
        }

        private void Hide()
        {
            AppliedBig = 0f;
            AppliedField = 0f;
            AppliedFade = 0f;
            OpenCount = 0;
            if (_renderer != null) _renderer.enabled = false;
        }

        private static void DestroySafe(Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }
}
