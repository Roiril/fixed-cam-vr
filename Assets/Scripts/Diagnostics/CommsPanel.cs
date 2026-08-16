#nullable enable
using FixedCamVr.Streaming;
using TMPro;
using UnityEngine;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// <b>AIエージェントからの連絡（第 2 の面）。</b> 本編のスクリーンとは別に、少し手前・少し外側に立てる。
    ///
    /// 判定は <c>canon/LEDGER.md</c> 0043（ユーザー逐語）:
    /// 「せっかく VR で立体的なので、スクリーンにつけなくていい。スクリーンよりも少し体験者に近く
    /// かつすこし外側に、新しいスクリーンとして設置するでもいいと思う」。
    /// 実装の順序と未確定は <c>.claude/plans/2026-08-15_comms-panel.md</c>。
    ///
    /// ⚠⚠ <b>送り主は AIエージェント</b>（2026-08-17・<c>canon/LEDGER.md</c> 0067・ユーザー指定
    /// 「上司じゃなくAIエージェントという事にしてほしい」）。2026-08-15〜16 は「上司」だった。
    /// <b>画に出る文字は 1 字も変わっていない</b> — 下の <see cref="TextFor"/> に送り主は書かれておらず、
    /// 紙の依頼書も「装置を通じて指示が伝達される」としか言わない。だから
    /// <b>いまの体験からは AI だと読み取れない</b>（出す手は <c>canon/OPEN.md</c> に案として置いてある）。
    /// ⚠ 依頼した側（0036「スタッフは上司である」）は覆されていない。変わったのは連絡の主体だけ。
    ///
    /// ⚠ <b>文面はコードが持っている</b>（show.json への著作 ＝ take の並列チャンネルは次の段）。
    /// 出る所は 3 点 — <see cref="CommsCueLogic"/>（<c>canon/LEDGER.md</c> 0054）。
    ///
    /// <b>出方</b>（<c>canon/LEDGER.md</c> 0053・2026-08-16）: 枠が<b>左端から右へ開き</b>、
    /// 開き切ってから文字が<b>1 字ずつ打たれる</b>。引くときは逆で、文字が消えてから枠が左へ畳まれる。
    /// 装置が受信して、印字して、片づける — という順序がそのまま画になる。
    /// 判断は <see cref="CommsPanelLogic"/>、配るのは <c>Apply</c> 1 か所。
    /// ⚠ 打つのは <c>TMP_Text.maxVisibleCharacters</c>（文字列を作り直さないので毎フレーム触ってよい）。
    ///
    /// <b>打鍵音</b>（<c>canon/LEDGER.md</c> 0056・2026-08-16）: 1 文字が出るたびに 1 発鳴る。
    /// 鳴らすのは <see cref="TypeAudioCue"/> で、<b>字を画へ書いているのと同じ行</b>から呼ぶ —
    /// 絵と音が同じ数えから出るのでずれようがない。速さ（12 文字/秒）は
    /// <see cref="CommsPanelLogic.CharsPerSec"/> がそのまま打鍵の間隔になる。
    ///
    /// ⚠ <b>追従は本編のスクリーンと同じ法則</b>（<see cref="YawFollowLogic"/>・ヨーだけ）。
    /// 新しい追従を書かない — 体験の中で追従の癖が 2 種類になると、どちらも「板」に見える。
    ///
    /// ⚠ <b>読まなくても体験は進む。</b> 既読の操作は作らない（体験者が持つ唯一の入力 ＝ 左 X は
    /// 記録専用で、兼用すると押した時刻の意味が濁る）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CommsPanel : MonoBehaviour
    {
        [Tooltip("体験の骨格。本編に入ったことを見るために読む。null なら実行時に探す。")]
        [SerializeField] private ShowRunDirector? runDirector;

        [Tooltip("体験者の報告を見るために読む（回数と、押した瞬間に演出が走っていたか）。null なら実行時に探す。")]
        [SerializeField] private ShowControlClient? showControl;

        [Tooltip("締めのカットが報告を待っているかを見るために読む。null なら実行時に探す。")]
        [SerializeField] private TimelineDirector? timeline;

        [Tooltip("頭の Transform。null なら CenterEyeAnchor を名前で探す。")]
        [SerializeField] private Transform? head;

        [Tooltip("打鍵音。null なら同 GameObject から取得（無ければ足す）。")]
        [SerializeField] private TypeAudioCue? typeSfx;

        // ---- 置き場所。**const**（SerializeField にすると既存シーンの YAML で 0 に読まれる）----
        /// <summary>頭からの距離 (m)。本編のスクリーンは 2.0m なので<b>0.5m 手前</b>。</summary>
        private const float DistanceM = 1.5f;

        /// <summary>
        /// 視線中心から外側へ振る角度（度）。<b>負が左</b>（ユーザーの図が左下だった）。
        ///
        /// ⚠⚠ <b>-20 → -8 へ寄せた</b>（2026-08-16・ユーザー指示「もう少し見やすい位置に…
        /// もう少し右下に」）。-20 では面の左半分が視線から 34° 外へ出ていて、
        /// <b>視界の端で読む</b>ことになっていた（快適に読めるのは ±15° 前後まで）。
        ///
        /// <b>本編のスクリーンの実測</b>（2.0m・2.3704 × 1.3333m・中心は視線から 8° 下）:
        /// 横 <b>±30.7°</b> / 縦 <b>+10.4°〜-26.4°</b>。この面（1.5m・0.76 × 0.32m ＝ 28.7° × 12.2°）は
        /// どう振ってもスクリーンの内側に入るので、<b>重ねない選択肢は無い</b> —
        /// 選べるのは「映像のどこへ重ねるか」だけ。だから中央を避けて<b>下の帯</b>へ置く。
        /// </summary>
        private const float YawOffsetDeg = -8f;

        /// <summary>
        /// 視線中心から下へ振る角度（度）。
        /// ⚠ <b>10 → 17 へ下げた</b>（同上）。面の縦は -23.1°〜-10.9° ＝ スクリーンの下端（-26.4°）の
        /// 内側に収まり、映像の中央（人形が立つあたり）から外れる。
        /// </summary>
        private const float PitchOffsetDeg = 17f;

        /// <summary>面の幅 (m)。1.5m 先で 0.76m ＝ <b>見かけ 28°</b>。</summary>
        private const float PanelW = 0.76f;
        // ---- 縦の組み立て -------------------------------------------------------------
        //
        // ⚠⚠ **面の高さは固定ではない**（2026-08-16・`canon/LEDGER.md` 0065）。
        //    2 つの帯が独立に出入りし、**枠は出ている帯だけを覆う**:
        //
        //        上段（AIエージェントの文面）   y ∈ [0, _bodyBandH]     ← 高さは**文面の実寸**
        //        ────────────────  y = 0（面の原点 ＝ 境目）
        //        下段（報告の状態）   y ∈ [-HintBandH, 0]     ← 中身が固定なので定数
        //
        //    それまでは「全体 0.32m を 6:4 で割り、下端固定で丈だけ伸ばす」だったが、
        //    下段から指示を剥がして**空になりうる**ようにしたので前提が崩れた。
        //    固定のままだと、1 行の受領が**上下に余白の空いた大きな箱**として出る（絵で見つけた）。

        /// <summary>上段と下段の境目（面のローカル y）。<b>ここが面の原点</b>。</summary>
        private const float HintBandTopY = 0f;

        /// <summary>
        /// 下段（`報告中` ＋ ゲージ）の帯の高さ (m)。<b>中身が変わらないので定数</b>
        /// （補助段 1.5° の 2 行 ＋ 余白）。⚠ 上段と違って実寸を測らないのは、
        /// 文面が 1 つしか無く、ゲージの進捗で行数が変わらないため。
        /// </summary>
        private const float HintBandH = 0.095f;

        /// <summary>
        /// 上段の上限 (m)。<b>折り返しの枠の高さ</b>で、実際に覆う高さは <see cref="_bodyBandH"/>。
        /// 最悪は 2 行（①③）なので、そこに余裕を足した値。
        /// </summary>
        private const float BodyMaxH = 0.20f;

        /// <summary>上段の字の上下に取る余白 (m)。</summary>
        private const float BodyPadM = 0.035f;

        /// <summary>
        /// 上段の帯の高さ (m)。<see cref="SetNotice"/> が文面を組むたびに実測から入れ直す。
        /// ⚠ 既定は組む前の保険（実測が入るまでの 1 フレームで箱が飛ばない）。
        /// </summary>
        private float _bodyBandH = BodyMaxH;

        /// <summary>上段の中心（面のローカル y）。<see cref="SetNotice"/> が文面の重心をここへ運ぶ。</summary>
        private float BodyCenterY => HintBandTopY + _bodyBandH * 0.5f;
        /// <summary>縁の張り出し (m)。地より一回り大きい面を裏に置いて枠に見せる。</summary>
        private const float BezelM = 0.012f;

        /// <summary>版の中の字の大きさ。<b>倍率は <see cref="TextScale"/> が transform で掛ける。</b></summary>
        private const float FontSize = 0.07f;

        /// <summary>
        /// 文字の拡大率。<b>fontSize ではなく scale で掛ける</b>（fontSize を上げると
        /// メッシュの座標だけ広がる — <c>canon/LEDGER.md</c> 0035）。
        ///
        /// ⚠⚠ <b>ここを手で決めない。</b> 初版 0.34 も 2 版目 0.67 も
        /// 「1 文字 0.9°／1.8°」のつもりで書かれていたが、どちらも <b>10 倍間違っていた</b>
        /// （3D の TextMeshPro は透視カメラのとき内部で 0.1 を掛ける）。
        /// 実際は 0.09°／0.18° ＝ <b>実機では点にしか見えていない</b>。
        /// いまは <see cref="HmdTextStyle"/> が距離から逆算する。
        /// </summary>
        private static float TextScale => HmdTextStyle.MeshScale(HmdTextStyle.BodyDeg, DistanceM, FontSize);

        /// <summary>
        /// 下段の拡大率。<b>補助の段</b>（1.5°）— 主役はAIエージェントの文面で、こちらは操作の銘板。
        /// <see cref="HmdTextStyle"/> の「補助は報告の面の見出しだけ」という但し書きが指すのがここ。
        /// </summary>
        private static float HintScale => HmdTextStyle.MeshScale(HmdTextStyle.MinorDeg, DistanceM, FontSize);

        // ---- 追従（`ScreenAnchor` / `TitleScreen` と同じ値。片方だけ変えない）----
        private const float YawDeadzoneDeg = 0.5f;
        private const float YawTrailDeg = 0f;
        private const float SmoothTimeSec = 0.30f;
        private const float MaxYawSpeedDegPerSec = 110f;
        private const float CatchUpThresholdDeg = 45f;
        private const float CatchUpBoost = 2f;
        private const float ResumeGapSec = 0.5f;

        /// <summary>⚠ 5000 を超えると URP の透明パスに入らず 1 画素も出ない（2026-07-31 実害）。</summary>
        private const int RenderQueue = 4980;
        private const int GlyphQueue = 4990;

        /// <summary>壊れの層。<b>文字より後に描く</b>（文字の上に矩形が乗る）。⚠ 5000 以下。</summary>
        private const int GlitchQueue = 4995;

        /// <summary>
        /// 壊れの層のシェーダ。⚠ <b>実行時に探すので Always Included に登録してある</b>
        /// （<c>ProjectSettings/GraphicsSettings.asset</c>。忘れると Editor では出て実機で剥がれる —
        /// 2026-07-31 に <c>IntroVeil</c> で実際に踏んだ）。
        /// </summary>
        private const string GlitchShaderName = "FixedCamVr/CommsGlitch";

        /// <summary>
        /// <b>文面（ユーザーが書いたまま・`canon/LEDGER.md` 0054）。</b>
        ///
        /// ⚠ <b>1 行は 14 文字まで</b>（面の幅から 1 文字 1.8° で入る数。折り返しは効くが
        /// 3 行目は面から出る）。触ったら <c>.\tools\unity.ps1 menu text-audit</c> を通す。
        /// ⚠ 文言を変えたら <c>menu hud-font</c> を再実行する（静的ベイクなので忘れると豆腐）。
        /// ⚠ <b>句点の有無を勝手に揃えない</b> — ①②に無く③にあるのはユーザーが書いた形。
        /// ⚠ 語は手元の面（<see cref="VisitorMarkGuidance"/>）と揃える —
        ///   あちらが「異変を報告」なのにこちらが「異常を記録」だと、同じ装置の言葉に聞こえない。
        /// ⚠ <b>身体を操作する指示にしない</b>（0034 — 「右手をあげてください」を伏線にしない）。
        /// </summary>
        private static string TextFor(CommsNotice n) => n switch
        {
            // ⚠⚠ **押し方はここにしか出ない**（2026-08-16・`canon/LEDGER.md` 0065）。
            //    それまで下段に `X／Y：異変を報告` を常に出していたが、面が開くたび
            //    ＝ 押している最中にも「押せ」と言い続けていた。**AIエージェントの言葉として 1 度だけ**言う。
            //    ⚠ キー名（X／Y）を出さない — 装置の面に入力機器の名前が出ると、
            //      調査の記録ではなくゲームの操作説明に見える。左で触れるのは X と Y だけで、
            //      **どちらでもよい**ので「手元のボタン」で足りる。
            CommsNotice.Begin => "調査を開始してください\n異変を認めたらボタンを長押し",
            CommsNotice.MarkLogged => "異常が記録されました",
            CommsNotice.MarkNothing => "異常は検出されませんでした",
            CommsNotice.Prompt => "異常が検出されました。\n記録してください。",
            _ => "",
        };

        /// <summary>
        /// その文面を読ませる時間 (秒)。<b>役割で違う</b>（`canon/LEDGER.md` 0065）。
        /// ①は押し方を含むので長め、②は自分の行為への返事なので最短、③は読まれないと
        /// 締めが進まないので最長。
        /// </summary>
        private static float HoldSecFor(CommsNotice n) => n switch
        {
            CommsNotice.Begin => CommsPanelLogic.HoldBriefSec,
            CommsNotice.Prompt => CommsPanelLogic.HoldUrgentSec,
            _ => CommsPanelLogic.HoldReceiptSec,
        };

        /// <summary>
        /// 面を組むときに使う文面 ＝ <b>いちばん長い行を持つもの</b>（13 文字）。
        ///
        /// ⚠ ここを短い文面にすると <c>menu text-audit</c> が<b>最悪の行を測らない</b>ので
        /// 「枠に収まっている」と嘘をつく。実行時はどの文面でも <see cref="SetNotice"/> が組み直す。
        /// ⚠ <b>行数の最悪（2 行 ＝ ③）はここでは測れない。</b> 縦の座りは
        /// <c>menu comms-preview</c> の絵で見る（4 文面ぶん焼く）。
        /// </summary>
        internal static string LongestNoticeText => TextFor(CommsNotice.Begin);

        private readonly CommsPanelLogic _logic = new CommsPanelLogic();
        private readonly CommsCueLogic _cue = new CommsCueLogic();
        private readonly YawFollowLogic _yawFollow = new YawFollowLogic();

        private Transform? _root;
        private MeshRenderer? _panelRenderer;
        private MeshRenderer? _bezelRenderer;
        // 壊れの層（`canon/LEDGER.md` 0068）。進みは映像とまったく同じものを読む。
        private MeshRenderer? _glitchRenderer;
        private Material? _glitchMat;
        private float _glitchLevel, _glitchBurst, _glitchSeed, _glitchOffsetX;
        // プレビュー（`menu comms-preview -Set decay=`）が注入する進み。**負なら実機の値を読む**。
        private float _decayOverride = -1f;
        private float _previewTimeSec;
        // ⚠ 地の色は**シェーダによってプロパティ名が違う**（URP は `_BaseColor` / 組み込みは `_Color`）。
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int GlitchLevelId = Shader.PropertyToID("_Level");
        private static readonly int GlitchBurstId = Shader.PropertyToID("_Burst");
        private static readonly int GlitchSeedId = Shader.PropertyToID("_Seed");
        private static readonly int GlitchAspectId = Shader.PropertyToID("_Aspect");
        // 枠を左端から右へ開くために、幅と「開いていないときの左端」を覚えておく。
        private float _panelW, _bezelW;
        private int _charCount;
        private Material? _panelMat;
        private Material? _bezelMat;
        private Mesh? _panelMesh;
        private TMP_Text? _text;
        private TMP_Text? _hint;
        // 報告の長押しの状態（`OvrControllerBridge` が毎フレーム push）。
        private float _markProgress;
        private bool _markConfirming;
        // 左コントローラの状態。⚠ **繋がっていなければ押し方を出さない**（嘘になる）。
        private bool _leftConnected = true;
        private bool _leftTracked = true;
        private string _hintBody = "";
        private bool _yawSeeded;
        // 報告の縁を取るために、直前に見た回数を覚えておく（ShowControlClient が真実源）。
        private int _lastMarkCount;
        private bool _runRestartHooked;
        // 直前のフレームで何文字出ていたか。**打鍵音はこの増分から鳴らす**（下の Apply）。
        private int _lastShown;
        // その字が絵を持つか（改行だけ false）。⚠ **全文が出ている一瞬にしか測れない** → SetNotice。
        private bool[]? _charVisible;

        /// <summary>実体を組めたか。<b>false なら一生出ない</b>（テレメトリが読む）。</summary>
        public bool IsBuilt => _text != null;

        /// <summary>
        /// 直近にシェーダへ書いた壊れの強さ（<b>画に出た側</b>の観測）。
        /// 発作の刻みで跳ねるので、**サンプルによっては 0 に近い値が出る**のが正常。
        /// </summary>
        public float GlitchLevel => _glitchLevel;

        /// <summary>壊れの層のマテリアルを掴めたか。<b>false なら進んでも 1 画素も変わらない。</b></summary>
        public bool GlitchBuilt => _glitchMat != null;

        /// <summary>
        /// 地と縁を組めたか。<b>false なら文字と壊れだけが宙に浮く。</b>
        /// ⚠⚠ 2026-08-17 まで実機がまさにこれだった（<c>Unlit/Color</c> がビルドから剥がれていた）。
        /// <b>Editor では出るので、この観測が無いと永久に気づけない。</b>
        /// </summary>
        public bool PanelBuilt => _panelMat != null && _bezelMat != null;

        /// <summary>いま読んでいる周回の進み 0..1（<b>映像の劣化とまったく同じ値</b>）。</summary>
        public float DecayProgress =>
            _decayOverride >= 0f ? _decayOverride
                                 : (runDirector != null ? runDirector.ScreenDecay : 0f);

        /// <summary>
        /// 壊れの進みと時刻を外から差し込む（<c>menu comms-preview -Set decay=</c> 専用）。
        /// ⚠ <b>実機では呼ばない。</b> 負を渡すと実機の値（<c>ShowRunDirector.ScreenDecay</c>）へ戻る。
        /// </summary>
        public void SetDecayForPreview(float progress01, float timeSec)
        {
            _decayOverride = progress01;
            _previewTimeSec = timeSec;
            TickGlitch(timeSec);
        }

        /// <summary>いまの段（テレメトリ用）。</summary>
        public CommsStage Stage => _logic.Stage;

        /// <summary>直近に書いた文字の不透明度（「画に出た」側の観測）。</summary>
        public float AppliedGlyph { get; private set; }

        /// <summary>直近に書いた枠の開き（0 = 畳まれている / 1 = 開き切り）。「画に出た」側の観測。</summary>
        public float AppliedOpen { get; private set; }

        /// <summary>直近に書いた枠の丈（0 = 下段だけ / 1 = 文面が入る高さ）。「画に出た」側の観測。</summary>
        public float AppliedBody { get; private set; }

        /// <summary>いま画に出ている文字数。<b>打鍵音はここから鳴る</b>ので、音の証拠でもある。</summary>
        public int VisibleChars { get; private set; }

        /// <summary>
        /// いまの文面が打ち切るまでに鳴る打鍵の数（<b>改行を除いた字数</b>）。
        /// 解析器が「連絡 n 通ぶんの合計」と <c>typeN</c> を突き合わせるために使う。
        /// </summary>
        public int NoticeChars { get; private set; }

        /// <summary>鳴らした打鍵の累計。<b>出た文字数の合計と一致するはず</b>（改行は除く）。</summary>
        public int TypedCount => typeSfx != null ? typeSfx.PlayedCount : 0;

        /// <summary>打鍵の音源を掴めているか。<b>false なら字は出るのに無音。</b></summary>
        public bool TypeSfxBuilt => typeSfx != null && typeSfx.HasClips;

        /// <summary>
        /// 連絡が届いた回数。<b>増えた瞬間に左コントローラを震わせる</b>のは
        /// <c>OvrControllerBridge</c>（Streaming / Diagnostics から OVR を触らない規約）。
        /// </summary>
        public int PulseCount { get; private set; }

        /// <summary>いま下段に出している文字（テスト・診断用）。</summary>
        public string HintBody => _hintBody;

        /// <summary>左コントローラが繋がっているか（テレメトリ用）。</summary>
        public bool LeftConnected => _leftConnected;

        /// <summary>左コントローラの位置が取れているか（テレメトリ用。人形の左腕が動く条件）。</summary>
        public bool LeftTracked => _leftTracked;

        /// <summary>
        /// 報告の長押しの状態を反映する。<c>OvrControllerBridge</c> が
        /// <c>VisitorMarkHoldLogic</c> の値を毎フレーム push する。
        ///
        /// ⚠ <b>押している最中は面が開く</b>（<see cref="CommsPanelLogic.SetGuideWanted"/>）。
        /// 手元に面が無くなったので、押した手応えを画で返せるのはここだけ。
        /// </summary>
        public void SetMarkState(float progress01, bool confirming)
        {
            _markProgress = Mathf.Clamp01(progress01);
            _markConfirming = confirming;
        }

        /// <summary>
        /// 左コントローラの状態を反映する。<b>繋がっていなければ押し方を出さない</b> —
        /// 押せないボタンの案内は嘘になる。
        /// ⚠ <paramref name="positionValid"/> はこの面の見え方には効かない（面は頭に追従する）。
        /// 観測（<c>ctrlL</c>）と、人形の左腕が動いているかの手掛かりのために受け取る。
        /// </summary>
        public void SetControllerState(bool connected, bool positionValid)
        {
            _leftConnected = connected;
            _leftTracked = positionValid;
        }

        /// <summary>直近に届いた連絡の種類（テレメトリ用。まだ 1 通も来ていなければ None）。</summary>
        public CommsNotice LastNotice { get; private set; } = CommsNotice.None;

        private void Awake()
        {
            ResolveRefs();
            Build();
            Apply(CommsWeights.Hidden);
        }

        private void OnDisable()
        {
            _logic.Disable();
            Apply(CommsWeights.Hidden);
            typeSfx?.StopAll();
        }

        private void OnDestroy()
        {
            OnDestroyHooks();
            if (_panelMat != null) Destroy(_panelMat);
            if (_bezelMat != null) Destroy(_bezelMat);
            if (_glitchMat != null) Destroy(_glitchMat);
            if (_panelMesh != null) Destroy(_panelMesh);
        }

        private void ResolveRefs()
        {
            if (runDirector == null) runDirector = FindObjectOfType<ShowRunDirector>();
            if (showControl == null) showControl = FindObjectOfType<ShowControlClient>();
            if (timeline == null) timeline = FindObjectOfType<TimelineDirector>();
            // ⚠ 打鍵音は**この面が持つ**（`ShowSoundDirector` は毎フレーム外から状態を見る層で、
            //    1 秒に 12 回・字の刻みちょうどには鳴らせない）。切替音と同じ構え。
            if (typeSfx == null) typeSfx = GetComponent<TypeAudioCue>();
            if (typeSfx == null) typeSfx = gameObject.AddComponent<TypeAudioCue>();
            if (head == null)
            {
                var anchor = GameObject.Find("CenterEyeAnchor");
                if (anchor != null) head = anchor.transform;
            }
            // ⚠ **ラン開始の号令にも繋ぐ。** 本編を出た縁（下の `inRun`）だけに頼ると、
            //   導入を持たない設定で相が Run のまま次のランが始まったとき、2 人目に①③が出ない。
            //   繋ぎ忘れはテストで捕まらない（2026-08-15 に音で踏んだ型）ので**二重に**閉じる。
            if (!_runRestartHooked && runDirector != null)
            {
                runDirector.RunRestarted += OnRunRestarted;
                _runRestartHooked = true;
            }
        }

        private void OnDestroyHooks()
        {
            if (_runRestartHooked && runDirector != null) runDirector.RunRestarted -= OnRunRestarted;
            _runRestartHooked = false;
        }

        private void OnRunRestarted()
        {
            _cue.ResetRun();
            _lastMarkCount = showControl != null ? showControl.VisitorMarkCount : 0;
            _logic.Disable();
            Apply(CommsWeights.Hidden);
            // 前の体験者の打鍵を次のランへ持ち越さない（`ShowSoundDirector.ResetRun` と同じ流儀）。
            typeSfx?.StopAll();
        }

        /// <summary>連絡を 1 通出す。<b>すでに出ていれば頭から出し直す</b>（重ねない）。</summary>
        public void Deliver(CommsNotice notice)
        {
            if (!IsBuilt || notice == CommsNotice.None) return;
            SetNotice(notice);
            // 打つ尺は文字数から決まる（文面を伸ばせば打つ時間も伸びる）。読ませる尺は役割で決まる。
            _logic.Begin(_charCount, HoldSecFor(notice));
            LastNotice = notice;
            PulseCount++;
            Debug.Log($"[Comms] AIエージェントからの連絡 {notice}「{TextFor(notice).Replace("\n", "／")}」"
                    + $"（{_charCount} 文字 / 打つ {_logic.TypeSec:0.00}s）");
        }

        private void Update()
        {
            if (!IsBuilt) return;

            // ---- 報告の縁を取る。⚠ **演出の有無は `ShowControlClient` が押した瞬間に凍らせた値**を使う。
            //      ここで `timeline.ActiveTakeId` を見ると、締めのカットは報告で畳まれた後なので
            //      「演出は無かった」に化けて、4 周目 A の連絡が真逆になる。
            bool markPressed = false, markHadTake = false;
            if (showControl != null)
            {
                if (showControl.VisitorMarkCount != _lastMarkCount)
                {
                    // 押し戻し（ラン開始で 0 に戻る）は報告ではない。
                    markPressed = showControl.VisitorMarkCount > _lastMarkCount;
                    markHadTake = showControl.LastMarkHadTake;
                    _lastMarkCount = showControl.VisitorMarkCount;
                }
            }

            CommsNotice next = _cue.Tick(new CommsCueInput
            {
                inRun = runDirector != null && runDirector.Phase == ShowPhase.Run,
                waitingForMark = timeline != null && timeline.IsWaitingForVisitorMark,
                markPressed = markPressed,
                markHadTake = markHadTake,
                dt = Time.unscaledDeltaTime,
            });
            if (next != CommsNotice.None) Deliver(next);

            // ⚠ **押している最中は面を開いたままにする**（`canon/LEDGER.md` 0058）。
            //   本編の外では開かない — 導入・終幕に手元の案内が浮くと世界が壊れる
            //   （元の面が queue 3000 で覆いに潰されていたのと同じ意図）。
            bool inRun = runDirector != null && runDirector.Phase == ShowPhase.Run;
            _logic.SetGuideWanted(inRun && _leftConnected
                                  && (_markProgress > 0f || _markConfirming));

            // ⚠ **壊れは面が出ていなくても進める。** 出た瞬間から正しい強さで出るようにするため
            //    （届いた所で 0 から立ち上がると「連絡が来ると壊れる」に見える）。
            TickGlitch(Time.unscaledTime);
            _logic.Tick(Time.unscaledDeltaTime);
            Apply(_logic.Weights);
        }

        private void LateUpdate()
        {
            if (!IsBuilt || !_logic.Active || head == null || _root == null) return;

            float dt = Time.unscaledDeltaTime;
            float headYaw = head.eulerAngles.y;
            if (!_yawSeeded || dt > ResumeGapSec)
            {
                _yawFollow.Reseat(_yawSeeded ? _yawFollow.CurrentYaw : headYaw);
                _yawSeeded = true;
            }
            else
            {
                _yawFollow.Step(headYaw, dt, YawDeadzoneDeg, YawTrailDeg, SmoothTimeSec,
                                MaxYawSpeedDegPerSec, CatchUpThresholdDeg, CatchUpBoost);
            }

            // 追従したヨーから見て「外側へ振って、下げて、手前に置く」。
            // ⚠ 面は体験者の方を向ける（板が斜めを向いていると読めない）。
            Quaternion yaw = Quaternion.Euler(0f, _yawFollow.CurrentYaw + YawOffsetDeg, 0f);
            Vector3 dir = yaw * Quaternion.Euler(PitchOffsetDeg, 0f, 0f) * Vector3.forward;
            Vector3 basePos = head.position + dir * DistanceM;
            // ⚠ **向きを先に決めてから横へ飛ばす**（`right` は rotation が決まらないと引けない）。
            _root.rotation = Quaternion.LookRotation(basePos - head.position, Vector3.up);
            // 周回の壊れ（`canon/LEDGER.md` 0068）。発作の刻みだけ、面ごと横へ飛ぶ。
            // ⚠ 追従の値そのものは汚さない（`_yawFollow` に足すと、飛んだ先から追従が始まって尾を引く）。
            _root.position = basePos + _root.right * _glitchOffsetX;
        }

        private void Build()
        {
            if (_text != null) return;
            var jp = JapaneseHudFont.TryGet();
            if (jp == null)
            {
                Debug.LogWarning("[Comms] 日本語フォントを解決できないので連絡の面は出しません");
                return;
            }

            var rootGo = new GameObject("CommsRoot");
            rootGo.transform.SetParent(transform, worldPositionStays: false);
            _root = rootGo.transform;

            // 地（受信票の面）。⚠ 標準シェーダが見つからなければ**文字だけ**にする
            //    （面が無くても読めるので、体験は止めない）。
            // ⚠ メッシュと幅は**地の外**で決める（壊れの層は地のシェーダが無くても出す）。
            _panelMesh = BuildQuad();
            _panelW = PanelW;
            // ⚠⚠ **`Unlit/Color` は実機のビルドに入っていない**（2026-08-17 に走行の画で判明）。
            //    組み込みシェーダでも、どのマテリアルからも参照されず Always Included にも無ければ
            //    剥がれる（2026-07-31 の `IntroVeil` と同じ型）。**Editor では出るので気づけない。**
            //    ⇒ 実機で **1 度も地も縁も描かれておらず、文字と壊れだけが宙に浮いていた。**
            //    URP の Unlit は URP のマテリアルが参照しているので必ず入っている。そちらを先に引く。
            Shader? flat = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            if (flat != null)
            {
                // 縁（裏の一回り大きい面）。⚠ **地だけだと真っ黒の中で面が消える**
                //    （2026-08-15 の実機の画で、文字だけが宙に浮いていた）。
                // ⚠ ここで渡す高さは**組んだ瞬間の見かけだけ**（`Apply` の `SetFrame` が
                //   毎フレーム中心と高さを置き直す）。
                _bezelW = PanelW + BezelM * 2f;
                _bezelRenderer = MakeQuad(rootGo.transform, "CommsBezelQuad",
                                          _bezelW, BodyMaxH + HintBandH + BezelM * 2f, 0.014f,
                                          flat, RenderQueue - 1, out _bezelMat);
                // 地。暗い漆のような面。純黒だと「穴」に見え、明るいと掲示物に見える。
                _panelRenderer = MakeQuad(rootGo.transform, "CommsPanelQuad",
                                          _panelW, BodyMaxH + HintBandH, 0.012f,
                                          flat, RenderQueue, out _panelMat);
            }
            else
            {
                // ⚠ 黙って飛ばさない。2026-08-17 まで警告が 1 行も無かったので、
                //   実機で地が消えていることに走行の画を拡大するまで気づけなかった。
                Debug.LogWarning("[Comms] 地のシェーダを引けないので文字と壊れだけになります"
                                 + "（Universal Render Pipeline/Unlit も Unlit/Color も見つからない）");
            }

            // ---- 壊れの層（`canon/LEDGER.md` 0068）。**文字より後に描く** ＝ 矩形が字の上に乗る。
            //      ⚠ z はわずかに手前。深度は見ない（`ZTest Always`）が、両眼で見たとき
            //        字と同一平面だと縞が字に食い込んで読みにくい。
            Shader? glitch = Shader.Find(GlitchShaderName);
            if (glitch != null)
            {
                _glitchRenderer = MakeQuad(rootGo.transform, "CommsGlitchQuad",
                                           _panelW, BodyMaxH + HintBandH, -0.002f,
                                           glitch, GlitchQueue, out _glitchMat);
            }
            else
            {
                // ⚠ 出ないだけで体験は止めない（連絡そのものは読める）。
                Debug.LogWarning($"[Comms] {GlitchShaderName} が見つからないので周回の壊れは出ません"
                                 + "（Always Included に登録されているか確認）");
            }

            // ---- 下段（報告の押し方・ゲージ）。**2026-08-16 にコントローラの先からここへ移した**
            //      （`canon/LEDGER.md` 0058）。上段より下・小さく・左揃え。
            //      ⚠ ゲージと見出しの大きさはリッチテキストで組む（`VisitorMarkGuidance`）。
            var hintGo = new GameObject("CommsHint");
            hintGo.transform.SetParent(rootGo.transform, worldPositionStays: false);
            var hintTmp = hintGo.AddComponent<TextMeshPro>();
            hintTmp.font = jp;
            hintTmp.alignment = TextAlignmentOptions.TopLeft;
            hintTmp.fontSize = FontSize;
            hintTmp.enableWordWrapping = false;
            hintTmp.richText = true;
            hintTmp.color = HmdTextStyle.Ink;
            var hintRt = (RectTransform)hintGo.transform;
            float hintScale = HintScale;
            hintRt.sizeDelta = new Vector2(PanelW * 0.92f / hintScale, HintBandH / hintScale);
            hintGo.transform.localScale = Vector3.one * hintScale;
            // 下段の帯の**上端**を境目へ合わせる（枠は中心が原点なので、帯の高さの半分だけ下げる）。
            hintGo.transform.localPosition = new Vector3(0f, HintBandTopY - HintBandH * 0.5f, 0f);
            var hintOverlay = Shader.Find("TextMeshPro/Distance Field Overlay");
            if (hintOverlay != null) hintTmp.fontMaterial.shader = hintOverlay;
            hintTmp.fontMaterial.renderQueue = GlyphQueue;
            _hint = hintTmp;

            var textGo = new GameObject("CommsText");
            textGo.transform.SetParent(rootGo.transform, worldPositionStays: false);
            var tmp = textGo.AddComponent<TextMeshPro>();
            tmp.font = jp;
            // ⚠ 組むのは**いちばん長い行を持つ文面**（`menu text-audit` に最悪を測らせる）。
            tmp.text = LongestNoticeText;
            // 揃えは左（`HmdTextStyle` の規約）。中央にしてよいのは黒の中に単独で出る面だけで、
            // ここは映像の上に立つ受信票なので、行頭が揃っている方が「印字されたもの」に見える。
            // ⚠⚠ **縦は上寄せ**（`Left` ＝ 縦中央 は使えない）。1 字ずつ出すと、2 行目の
            //    1 文字目が出た瞬間に TMP が「見えている行数」で縦中央を取り直し、
            //    **打ち終わった 1 行目がひょいと上へ跳ねる**（実測 38px）。
            //    枠の縦中央には、下の `sizeDelta` を本文の実高さに合わせることで座らせる。
            tmp.alignment = TextAlignmentOptions.TopLeft;
            tmp.fontSize = FontSize;
            tmp.enableWordWrapping = true;
            tmp.richText = false;
            tmp.color = HmdTextStyle.Ink;
            var rt = (RectTransform)textGo.transform;
            // ⚠ 大きさは scale で掛ける（fontSize を上げるとメッシュの座標だけ広がる — LEDGER 0035）。
            //   ⇒ **折り返し幅も scale で割る**。ここを固定値にすると、字の大きさを直したときに
            //     折り返しだけ取り残されて面からはみ出す。
            float scale = TextScale;
            rt.sizeDelta = new Vector2(PanelW * 0.92f / scale, BodyMaxH / scale);
            textGo.transform.localScale = Vector3.one * scale;
            var overlay = Shader.Find("TextMeshPro/Distance Field Overlay");
            if (overlay != null) tmp.fontMaterial.shader = overlay;
            tmp.fontMaterial.renderQueue = GlyphQueue;
            _text = tmp;
            SetNotice(CommsNotice.None);   // 組み上げたら、まず畳んだ状態にする
        }

        /// <summary>
        /// 文面を差し替えて、1 字ずつ出すための下ごしらえをする。
        /// <b>連絡が届いた瞬間に 1 回だけ</b>走る（毎フレームではない）。
        ///
        /// ⚠ <c>maxVisibleCharacters</c> はレイアウトを組み直さないので毎フレーム触ってよいが、
        /// <b>文字列そのものを変えたら組み直しが要る</b>（文字数も重心も変わる）。
        /// ⚠ 測る前に<b>全文を見えるところまで戻す</b> — 直前の文面の可視数が残っていると、
        /// <see cref="TMP_Text.textBounds"/> が<b>その一部だけ</b>の重心を返して面から外れる。
        /// </summary>
        private void SetNotice(CommsNotice notice)
        {
            TMP_Text? tmp = _text;
            if (tmp == null) return;
            string body = notice == CommsNotice.None ? LongestNoticeText : TextFor(notice);

            tmp.maxVisibleCharacters = int.MaxValue;
            if (tmp.text != body) tmp.text = body;
            tmp.ForceMeshUpdate(ignoreActiveState: true, forceTextReparsing: true);

            // ⚠ 上寄せにしたぶん、**全文が出ている状態の重心**を面の中心へ運ぶ（文面ごとに変わる —
            //    1 行と 2 行では重心が違うので、ここを 1 度きりにすると 2 行の文面が下へずれる）。
            //    `preferredHeight` で枠を詰める手もあるが、あれは字の上下に余白を含むので
            //    ぶんだけ本文が上へ寄る（実測 33px）。組み上がったメッシュの実寸から測る。
            float scale = TextScale;
            Bounds ink = tmp.textBounds;
            // ⚠⚠ **上段の高さは文面の実寸で決まる**（2026-08-16・`canon/LEDGER.md` 0065）。
            //    1 行の受領（②）と 2 行の指示（①）で同じ高さの箱を出していたので、
            //    ②が**上下に余白の空いた大きな箱**として出ていた（プレビューの絵で見つけた）。
            //    枠はこの高さを読んで縮む（`Apply` の `top`）。
            _bodyBandH = Mathf.Max(0.01f, ink.size.y * scale + BodyPadM);
            // ⚠ 運ぶ先は面の中心ではなく**上段の中心**（下段に報告の押し方が居るため）。
            tmp.transform.localPosition = new Vector3(0f, BodyCenterY - ink.center.y * scale, 0f);
            // ⚠ ここは**全文が出ている状態**（上で maxVisibleCharacters = int.MaxValue して
            //   組み直した直後）なので、`isVisible` が「その字が絵を持つか」を表す。
            //   ここでしか測れない（下で 0 に戻すと、以後は全部 false になる）。
            var info = tmp.textInfo;
            _charCount = info != null ? info.characterCount : 0;
            _charVisible = new bool[_charCount];
            int visible = 0;
            for (int i = 0; i < _charCount; i++)
            {
                _charVisible[i] = info!.characterInfo[i].isVisible;
                if (_charVisible[i]) visible++;
            }
            NoticeChars = visible;
            tmp.maxVisibleCharacters = 0;
            // ⚠ 文面を差し替えたら**打鍵の数えも 0 に戻す**。戻さないと、
            //    前の文面より短い文面では 1 発も鳴らず、長い文面では途中から鳴り始める。
            _lastShown = 0;
            VisibleChars = 0;
        }

        /// <summary>
        /// その字は絵を持つ字か（<see cref="SetNotice"/> が 1 度だけ測る）。
        /// <b>改行では打鍵を鳴らさない</b> — <c>maxVisibleCharacters</c> は改行も 1 文字として
        /// 数えるので、鳴らすと「字が出ていないのに 1 発鳴る」が起きる（③の文面は 2 行）。
        ///
        /// ⚠⚠ <b>毎フレーム <c>textInfo.characterInfo[i].isVisible</c> を見てはいけない。</b>
        /// あれは<b>いまの <c>maxVisibleCharacters</c> の下で描かれたか</b>を表すので、
        /// たったいま出た字は<b>必ず false</b>（前フレームの再生成にはまだ入っていない）。
        /// 2026-08-16 にこれで**打鍵が 1 発しか鳴らなかった**（プレビューの `type.tsv` が捕まえた）。
        /// ⇒ 全文が出ている状態で 1 度だけ測って覚えておく。
        /// ⚠ 分からないときは<b>鳴らす側へ倒す</b>（黙る方が気づけない）。
        /// </summary>
        private bool IsVisibleChar(int i)
        {
            if (_charVisible == null || i < 0 || i >= _charVisible.Length) return true;
            return _charVisible[i];
        }

        /// <summary>
        /// 地・縁の色を書く。<b>両方のプロパティへ書く</b> — 引けたシェーダで分岐すると、
        /// 片方を消したときに<b>黙って色が付かなくなる</b>（実機だけ真っ黒／真っ白になる型）。
        /// ⚠ <c>Material.color</c> は URP の <c>_BaseColor</c> を触らないので使わない。
        /// </summary>
        private static void SetFlatColor(Material m, Color c)
        {
            if (m.HasProperty(BaseColorId)) m.SetColor(BaseColorId, c);
            if (m.HasProperty(ColorId)) m.SetColor(ColorId, c);
        }

        /// <summary>面を 1 枚作る（地と縁で共有）。色は <see cref="Apply"/> が毎フレーム書く。</summary>
        private MeshRenderer MakeQuad(Transform parent, string name, float w, float h, float z,
                                      Shader shader, int queue, out Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = new Vector3(0f, 0f, z);   // 文字より奥
            go.transform.localScale = new Vector3(w, h, 1f);
            go.AddComponent<MeshFilter>().sharedMesh = _panelMesh;
            var r = go.AddComponent<MeshRenderer>();
            mat = new Material(shader) { name = name + " (runtime)", renderQueue = queue };
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.enabled = false;
            return r;
        }

        private static Mesh BuildQuad()
        {
            var m = new Mesh { name = "CommsPanelQuad" };
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

        private void Apply(in CommsWeights w)
        {
            AppliedGlyph = Mathf.Clamp01(w.glyph);
            AppliedOpen = Mathf.Clamp01(w.open);
            if (_text != null)
            {
                _text.alpha = AppliedGlyph;
                // 1 字ずつ出す。⚠ **切り上げ**（0 より大きければ 1 字目は出ている）。
                int shown = _charCount <= 0 ? 0
                          : Mathf.Clamp(Mathf.CeilToInt(Mathf.Clamp01(w.reveal) * _charCount), 0, _charCount);
                if (_text.maxVisibleCharacters != shown) _text.maxVisibleCharacters = shown;
                bool on = AppliedGlyph > 0.002f && shown > 0;
                if (_text.gameObject.activeSelf != on) _text.gameObject.SetActive(on);
                // ⚠⚠ **打鍵音は、字を画へ書いているこの行から鳴らす**（`canon/LEDGER.md` 0056）。
                //    絵と音が同じ数えから出るので、ずれようがない（乱れの育ちを 1 か所で
                //    数えているのと同じ理由 — 別々に数えると黙って食い違う）。
                //    ⚠ **増えた字数ぶん鳴らさない。** 1 フレームで 2 字進んだら（コマ落ち）
                //      同じ DSP 時刻に 2 発重なって 1 つの大きな音に潰れる。1 発だけ鳴らす。
                if (shown > _lastShown && IsVisibleChar(shown - 1)) typeSfx?.Play();
                _lastShown = shown;
                VisibleChars = shown;
            }
            ApplyHint(Mathf.Clamp01(w.hint));
            float pa = Mathf.Clamp01(w.panel);
            AppliedBody = Mathf.Clamp01(w.body);

            // ⚠⚠ **枠は「出ている帯」だけを覆う**（2026-08-16・`canon/LEDGER.md` 0065）。
            //    下段から指示を剥がしたので、**押していないときは下段に 1 文字も無い**。
            //    それまでの「下端を固定して丈だけ伸びる」ままだと、①の連絡が
            //    **下半分が空の大きな箱**として出る（プレビューの絵で見つけた）。
            //    ⚠ 下段の有無は不透明度だけでは決まらない — 文字が空でも hint は 1 になる。
            float hintK = Mathf.Clamp01(w.hint) * (_hintBody.Length > 0 ? 1f : 0f);
            // 上段の帯 = [0, _bodyBandH]（**文面の実寸**）/ 下段の帯 = [-HintBandH, 0]。
            float top = HintBandTopY + _bodyBandH * AppliedBody;
            float bottom = HintBandTopY - HintBandH * hintK;
            float h = Mathf.Max(0f, top - bottom);
            float cy = (top + bottom) * 0.5f;
            bool lit = pa > 0.01f && AppliedOpen > 0.001f && h > 0.0005f;

            // 地のシェーダは alpha を持たない（不透明）ので、明るさで濃さを出す（暗い場所なので十分）。
            if (_panelRenderer != null && _panelMat != null)
            {
                SetFlatColor(_panelMat, new Color(0.050f * pa, 0.042f * pa, 0.038f * pa, 1f));
                _panelRenderer.enabled = lit;
                SetFrame(_panelRenderer.transform, _panelW, AppliedOpen, cy, h);
            }
            if (_bezelRenderer != null && _bezelMat != null)
            {
                // 縁は地より明るい。ここだけが「面がある」ことを伝える。
                SetFlatColor(_bezelMat, new Color(0.150f * pa, 0.110f * pa, 0.085f * pa, 1f));
                _bezelRenderer.enabled = lit;
                SetFrame(_bezelRenderer.transform, _bezelW, AppliedOpen, cy, h + BezelM * 2f);
            }

            // ---- 壊れの層（`canon/LEDGER.md` 0068）----------------------------------
            // ⚠ **地・縁とまったく同じ枠に乗せる**（開き・丈・中心）。別に解くと、
            //    枠が開いている途中に壊れだけが枠の外へはみ出す。
            if (_glitchRenderer != null && _glitchMat != null)
            {
                _glitchMat.SetFloat(GlitchLevelId, _glitchLevel);
                _glitchMat.SetFloat(GlitchBurstId, _glitchBurst);
                _glitchMat.SetFloat(GlitchSeedId, _glitchSeed);
                // 縦横比はブロックを正方形に近づけるためだけ。丈は毎フレーム変わる。
                _glitchMat.SetFloat(GlitchAspectId, h > 0.0005f ? _panelW / h : 2.4f);
                _glitchRenderer.enabled = lit && _glitchLevel > CommsGlitchLogic.OffThreshold;
                SetFrame(_glitchRenderer.transform, _panelW, AppliedOpen, cy, h);
            }
        }

        /// <summary>
        /// 周回の壊れを 1 フレーム進める（<c>canon/LEDGER.md</c> 0068）。
        /// <b>進みは映像の劣化とまったく同じ値</b>（<see cref="DecayProgress"/>）。
        ///
        /// ⚠ <b>面が出ていないあいだも進める。</b> 届いた所で 0 から立ち上げると
        /// 「連絡が来ると壊れる」に見えて、因果が逆になる。
        /// </summary>
        private void TickGlitch(float timeSec)
        {
            _glitchLevel = CommsGlitchLogic.LevelFor(DecayProgress);
            // ⚠⚠ **発作は強さと別に渡す。** 1 本へ畳むと、シェーダはその値で被覆率を解くので
            //    **常時でもブロックが出る**（2026-08-17 に絵で見つけた）。
            _glitchBurst = CommsGlitchLogic.BurstAt(timeSec, _glitchLevel) ? 1f : 0f;
            _glitchSeed = CommsGlitchLogic.SeedAt(timeSec);
            _glitchOffsetX = CommsGlitchLogic.OffsetXAt(timeSec, _glitchLevel);
        }

        /// <summary>
        /// 下段（報告の押し方・ゲージ）を書く。<b>文言は
        /// <see cref="VisitorMarkGuidance"/> のまま</b>（コントローラの先に出していたときと同じ）。
        ///
        /// ⚠ 左コントローラが繋がっていなければ<b>何も出さない</b> — 押せないボタンの案内は嘘になる。
        /// ⚠ 変わったときだけ <c>SetText</c> する（毎フレームの GC を作らない）。
        /// </summary>
        private void ApplyHint(float alpha)
        {
            if (_hint == null) return;
            string body = _leftConnected
                ? VisitorMarkGuidance.Line(_markProgress, _markConfirming)
                : "";
            if (body != _hintBody)
            {
                _hint.SetText(body);
                _hintBody = body;
            }
            _hint.alpha = alpha;
            bool on = alpha > 0.002f && body.Length > 0;
            if (_hint.gameObject.activeSelf != on) _hint.gameObject.SetActive(on);
        }

        /// <summary>
        /// 枠を<b>左端を固定したまま</b>開き、<b>縦は中心と高さを直に置く</b>。
        /// <paramref name="kx"/> = 横の開き（0 = 左端に畳まれている / 1 = 開き切り）。
        ///
        /// 面のメッシュは中心が原点（頂点 ±0.5）なので、横は縮めると<b>両側から</b>縮む。
        /// 左端を残すには、縮めたぶんの半分だけそちらへ寄せる。
        ///
        /// ⚠⚠ <b>縦は「下端固定で伸びる」をやめた</b>（2026-08-16・<c>canon/LEDGER.md</c> 0065）。
        /// 下段が空になりうるので、<b>出ている帯だけを覆う</b>必要がある
        /// （呼び出し側が上端と下端から中心・高さを解く）。下端固定のままだと、
        /// 下段が無い連絡が<b>下半分の空いた箱</b>として出る。
        /// </summary>
        private static void SetFrame(Transform quad, float fullW, float kx, float centerY, float height)
        {
            quad.localScale = new Vector3(fullW * kx, height, 1f);
            // 左端は常に -w/2 に居る（縮めたぶんの半分だけ寄せる）。縦は解いた中心をそのまま置く。
            Vector3 p = quad.localPosition;
            quad.localPosition = new Vector3(-(fullW * 0.5f) * (1f - kx), centerY, p.z);
        }
    }
}
