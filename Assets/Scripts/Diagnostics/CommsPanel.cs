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
    /// <b>出方</b>: 通常時の面と文面の位置は固定し、透明度で出入りする。顔と本文の間の短い縦罫線だけが伸びる。
    /// 文字は従来の時刻で 1 字ずつ打たれ、各字の alpha が 40ms で立ち上がる。
    /// 判断は <see cref="CommsPanelLogic"/>、配るのは <c>Apply</c> 1 か所。
    /// ⚠ 打つ順序は文字の頂点 alpha で作る。文字列と字幅は毎フレーム変えない。
    ///
    /// <b>打鍵音</b>（<c>canon/LEDGER.md</c> 0056・2026-08-16）: 1 文字が出るたびに 1 発鳴る。
    /// 鳴らすのは <see cref="TypeAudioCue"/> で、<b>字を画へ書いているのと同じ行</b>から呼ぶ —
    /// 絵と音が同じ数えから出るのでずれようがない。速さ（<b>日本語 12 / Latin 18 文字/秒</b>・0149）は
    /// <see cref="CommsPanelLogic.CharsPerSecFor"/> がそのまま打鍵の間隔になる。
    ///
    /// ⚠ <b>追従は本編のスクリーンと同じ法則</b>（<see cref="YawFollowLogic"/>・ヨーだけ）。
    /// 新しい追従を書かない — 体験の中で追従の癖が 2 種類になると、どちらも「板」に見える。
    /// ⚠⚠ ただし<b>出る瞬間はいつも頭の正面</b>（畳まれているあいだ <c>LateUpdate</c> が種を捨てる）。
    /// 追従を持ち越すと、面は<b>前に消えた場所から回り込んでくる</b>。
    ///
    /// ⚠ <b>読まなくても体験は進む。</b> 既読の操作は作らない（体験者が持つ唯一の入力 ＝ 左のボタンは
    /// 記録専用で、兼用すると押した時刻の意味が濁る）。
    ///
    /// <b>顔</b>（<c>canon/LEDGER.md</c> 0071・2026-08-17）: 面の左に
    /// AIエージェントの顔（paperdoll の「スイ」）が出る。寸法は
    /// <see cref="CommsFaceLayout"/>、描くのは <c>CommsAvatar.shader</c>、版を焼くのは
    /// <c>tools/make-comms-face.py</c>。⚠ <b>顔のぶんは面を左へ伸ばして作る</b> —
    /// 文面の帯は 1mm も動いていない（詰めると最長の行が 3 行へ折り返して前提が崩れる）。
    ///
    /// <b>呪い</b>（<c>canon/LEDGER.md</c> 0229・2026-09-18）: 壊れは「字が抜ける引き算」ではなく、
    /// <b>呪われた双子の画面が斑で重なる足し算</b>。地のふちが毛羽立って黒くにじみ、顔が市松人形になり、
    /// 本文の行の位置に走り書きが乗る。斑の場は <c>CommsCurse.hlsl</c>（判断は
    /// <see cref="CommsCurseLogic"/>）1 つで、顔（<c>CommsAvatar.shader</c>）・地と走り書き
    /// （<c>CommsPanelPlate.shader</c>）・文字の切断（地が書くステンシルを TMP の材質が読む）が共有する。
    /// 面が開くたびに斑は 0 から目標へ 1 秒で立ち上がる（<see cref="CommsPanelLogic.SetCurseTarget"/>）。
    ///
    /// <b>憑依の出し方</b>（<c>canon/LEDGER.md</c> 0230・2026-09-18）: 侵食度 0.75 以降の連絡は
    /// <b>全文が一気に出て（打鍵なし）→ 読ませて → 左から顔と本文が呪われた双子へ塗り替わる</b>
    /// （時計は <see cref="CommsPossessionLogic"/>・本文は同位置の 2 枚の TMP を同じ x で切る）。
    /// 塗り替わりの頭で乱れの音が 1 発（<see cref="CurseSweepAudioCue"/>）。嘘の一文（3 周目 A）も同じ形で、
    /// Sweep 中だけ面の子が小さく揺れ、短い 4 回の乱れで帯の中身が横へ飛ぶ。読む区間と塗り替わり後は静止する。
    /// 旧「印字へ侵食が追いつく」の弧は捨てた（初見の人には装置の不調にしか見えない、がユーザーの判定の芯）。
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

        [Tooltip("導入がまだ開始待ち（段 0）かを見るために読む。歩行誘導の 2 通に要る。null なら実行時に探す。")]
        [SerializeField] private IntroDirector? intro;

        [Tooltip("床の矢印と円。⓪b『矢印の方向から…』を出した瞬間に矢印を出させる。null なら実行時に探す。")]
        [SerializeField] private WalkGuide? walkGuide;

        [Tooltip("頭の Transform。null なら CenterEyeAnchor を名前で探す。")]
        [SerializeField] private Transform? head;

        [Tooltip("打鍵音。null なら同 GameObject から取得（無ければ足す）。")]
        [SerializeField] private TypeAudioCue? typeSfx;

        [Tooltip("塗り替わりの頭の乱れの音（0230）。null なら同 GameObject から取得（無ければ足す）。")]
        [SerializeField] private CurseSweepAudioCue? sweepSfx;

        [Tooltip("乗っ取り中に主画面へ出す空間エラー。null なら映像だけを切り替える。")]
        [SerializeField] private GameObject? takeoverErrorPrefab;

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
        /// 横 <b>±30.7°</b> / 縦 <b>+10.4°〜-26.4°</b>。この面はどう振ってもスクリーンの内側に
        /// 入るので、<b>重ねない選択肢は無い</b> — 選べるのは「映像のどこへ重ねるか」だけ。
        /// だから中央を避けて<b>下の帯</b>へ置く。
        ///
        /// ⚠⚠ <b>顔の枠のぶん、面は左へ 0.194m 伸びた</b>（2026-08-17・0071）。
        /// 全幅 0.954m ＝ 見かけ 35.0° で、左端は視線から <b>-29.4°</b>（スクリーンの左端 -30.7° の内側）。
        /// <b>読む物はそこに無い</b> — 伸びた先に居るのは顔だけで、文面はこの角度のまま据え置き。
        /// </summary>
        private const float YawOffsetDeg = -8f;

        /// <summary>
        /// 視線中心から下へ振る角度（度）。
        /// ⚠ <b>10 → 17 へ下げた</b>（同上）。面の縦は -23.1°〜-10.9° ＝ スクリーンの下端（-26.4°）の
        /// 内側に収まり、映像の中央（人形が立つあたり）から外れる。
        /// </summary>
        private const float PitchOffsetDeg = 17f;

        /// <summary>
        /// <b>文面の帯</b>の幅 (m)。1.5m 先で 0.76m ＝ <b>見かけ 28°</b>。
        ///
        /// ⚠⚠ <b>これは面の全幅ではない</b>（2026-08-17・<c>canon/LEDGER.md</c> 0071）。
        /// 面は左へ <see cref="CommsFaceLayout.BandW"/> だけ伸びて、そこに顔の枠が立つ。
        /// 全幅は <see cref="CommsFaceLayout.FullW"/>。<b>文面はこの帯の中に据え置き</b>で、
        /// 字の大きさ・折り返し・重心の運びは 1 行も変わっていない。
        /// </summary>
        private const float PanelW = 0.76f;

        /// <summary>
        /// 面ぜんたいの倍率（2026-08-19・<c>canon/LEDGER.md</c> 0091・ユーザー指定
        /// 「AIエージェントのスクリーンの大きさを今の 2/3 にしてほしい」）。<b>根（<c>CommsRoot</c>）に掛ける</b>ので、地・縁・顔の枠・文字・
        /// 壊れの複製が<b>1 つの値で同じだけ縮む</b>。
        ///
        /// ⚠⚠ <b>この下に書いてある寸法と見かけ角は、倍率を掛ける前の値。</b>
        /// 実際に見えるのは全部その 2/3 で、主な所は
        /// 文面の帯 28° → <b>19.2°</b> ／ 顔を含む全幅 35.0° → <b>24.0°</b>。
        /// 置き場所（<see cref="YawOffsetDeg"/> / <see cref="PitchOffsetDeg"/> / <see cref="DistanceM"/>）は
        /// 面の原点の角度なので<b>動かない</b> — 面は文面の帯の中心へ向かって縮む。
        ///
        /// ⚠ <b>字も一緒に縮む</b>（本文 1.8° → 1.2° / 補助 1.5° → 1.0°）。
        /// 字だけ据え置くと 1 行 14 文字が帯に入らず、折り返しの前提（3 行まで）が崩れる。
        /// <c>menu text-audit</c> の狙い値もこの倍率を掛けてある（<c>HmdTextAudit</c>）。
        /// </summary>
        public const float Scale = 2f / 3f;

        /// <summary>顔の版（<c>Resources.Load</c> のパス）。焼くのは <c>tools/make-comms-face.py</c>。</summary>
        private const string FaceResourcePath = "Comms/SuiFace";

        /// <summary>
        /// 侵食の行き先（市松人形）の版。<c>canon/LEDGER.md</c> 0073。
        /// ⚠ <b>スイと同じ大きさ・同じ座りで焼いてあること</b>（<c>make-comms-face.py</c> の
        /// <c>compose</c> が 2 枚を同じ規則で収める）。ずれると「侵食」ではなく「入れ替わり」に見える。
        /// </summary>
        private const string DollFaceResourcePath = "Comms/DollFace";

        /// <summary>乗っ取られた本文に使う書体。欠けた Latin は通常本文の書体へフォールバックする。</summary>
        private const string HorrorFontResourcePath = "Fonts/CommsHorror SDF";

        /// <summary>顔の枠と切り抜きのシェーダ。⚠ <b>Always Included に入れてある</b>。</summary>
        private const string AvatarShaderName = "FixedCamVr/CommsAvatar";

        /// <summary>
        /// 地とその呪われた双子（毛羽立ち・走り書き・文字を切るステンシル）のシェーダ（0229）。
        /// ⚠ <b>Always Included に入れてある</b>。引けなければ URP の Unlit へ落ちる
        /// （地は出るが呪いは顔にしか出ない ＝ <see cref="PlateBuilt"/> が 0 で観測に出る）。
        /// </summary>
        private const string PlateShaderName = "FixedCamVr/CommsPanelPlate";

        /// <summary>
        /// 斑の中に文字を切るステンシルを書くシェーダ（色は書かない）。
        /// ⚠⚠ <b>地の双子と別の quad</b> — URP は 1 つのマテリアルの LightMode の無いパスを最初の
        /// 1 つしか描かないので、同じシェーダの第 2 パスにすると地が 1 画素も出ない（2026-09-18 に踏んだ）。
        /// ⚠ <b>Always Included に入れてある</b>。引けなければ文字は切れない（読める側へ倒れる）。
        /// </summary>
        private const string StencilShaderName = "FixedCamVr/CommsCurseStencil";

        /// <summary>
        /// 地の quad を矩形より外へ広げる幅 (m)。毛羽立ち（振幅 12mm）と煙のにじみ（35mm）がここに収まる。
        /// ⚠ <see cref="CommsFaceLayout"/> の寸法は 1mm も動かさない — 広がるのは描く面だけ。
        /// </summary>
        private const float PlateMarginM = 0.05f;

        /// <summary>文字を切るステンシルのビット。隔離の殻は 1・導入の破砕は 32 なので重ならない。</summary>
        private const int CurseStencilBit = 8;

        /// <summary>走り書きを乗せる行の上限（文面の最悪は 3 行・<see cref="BodyMaxH"/>）。</summary>
        private const int MaxScrawlLines = 4;
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
        /// 下段（`解析中` ＋ ゲージ）の帯の高さ (m)。<b>中身が変わらないので定数</b>
        /// （補助段 1.5° の 2 行 ＋ 余白）。⚠ 上段と違って実寸を測らないのは、
        /// 文面が 1 つしか無く、ゲージの進捗で行数が変わらないため。
        /// </summary>
        private const float HintBandH = 0.095f;

        /// <summary>
        /// 上段の上限 (m)。<b>折り返しの枠の高さ</b>で、実際に覆う高さは <see cref="_bodyBandH"/>。
        /// ⚠⚠ <b>最悪は 3 行</b>（⓪b と①b と③）。1 行はおよそ 0.047m なので 3 行 ＝ 0.141m、
        /// そこに字の上下の余白 0.035m を足して 0.176m。
        /// この値はその上限で、<b>ここを下回ると 4 行目へ折り返して面から溢れる</b>。
        /// ⚠ 2026-08-19 に 0.25 → 0.20 へ戻した（0096 で①が 4 行になったが、
        /// 0097 で①と①b へ割ったので最悪が 3 行に戻った）。
        /// ⚠ <c>CommsNoticeTextTests.MaxLines</c> と対の値。片方だけ動かさない。
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
        /// <summary>深い黒の面。後ろの映像は残す。</summary>
        private const float PanelAlpha = 0.72f;
        // 乗っ取り時だけ背後の警告を遮る。通常の通信面の大きさと濃さは維持する。
        private const float TakeoverPanelAlpha = 0.96f;

        /// <summary>各文字が立ち上がる時間。出現時刻と打鍵音は従来の刻みを保つ。</summary>
        public const float GlyphFadeSec = 0.04f;

        private static readonly Color PanelColor = new Color(10f / 255f, 9f / 255f, 8f / 255f, 1f);
        private static readonly Color Ivory = new Color(209f / 255f, 199f / 255f, 184f / 255f, 1f);
        // TMP の頂点色は線形空間で描かれる。出力画像で朱赤になるよう変換しておく。
        private static readonly Color LieRed = new Color(1f, 48f / 255f, 24f / 255f, 1f).linear;

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

        // ⚠⚠ **壊れの層（矩形を描くシェーダ）は 2026-08-17 に削除した**（`canon/LEDGER.md` 0069）。
        //    ユーザーの赤入れ「明るすぎる／色が鮮やかすぎる／四角すぎる」は、
        //    **壊れを別の層として貼っていた**ことから全部出ていた。
        //    この装置は画像を表示していない — **1 文字ずつ印字している**。だから壊れるのは印字。

        /// <summary>
        /// <b>文面（ユーザーが書いたまま・`canon/LEDGER.md` 0096）。</b>
        ///
        /// ⚠ <b>1 行は 14 文字まで</b>（面の幅から 1 文字 1.8° で入る数）。折り返しは効くが
        /// 任せると文の途中で切れるので<b>改行はこちらで入れる</b>（語を縮めて 1 行に収めるのは
        /// ユーザーの文言の書き換えなのでしない）。<b>行数は 4 行まで</b>（<see cref="BodyMaxH"/>）。
        /// 触ったら `menu text-audit`（はみ出し）と `menu comms-preview`（縦の座り）を通す。
        /// ⚠ 文言を変えたら `menu hud-font` を再実行する（静的ベイクなので忘れると豆腐）。
        /// ⚠ <b>句点の有無を勝手に揃えない</b> — 文面ごとに違うのはユーザーが書いた形。
        /// ⚠ 語は手元の面（<see cref="VisitorMarkGuidance"/>）と揃える —
        ///   あちらが「解析中」なのにこちらが「記録」だと、同じ装置の言葉に聞こえない。
        /// ⚠ <b>身体を操作する指示にしない</b>（0034 — 「右手をあげてください」を伏線にしない）。
        /// </summary>
        private static string TextJa(CommsNotice n) => n switch
        {
            CommsNotice.ControllerDisconnected => "左コントローラーを持ち\nXかYを押してください",
            CommsNotice.ControllerUntracked => "左コントローラーを\n少し前に出してください",
            CommsNotice.ControllerStaff => "接続を確認できません\nスタッフを呼んでください",
            CommsNotice.ControllerConfirmed => "左コントローラーを\n確認しました",
            CommsNotice.Tutorial => "装置が正常に動くか\nチェックします。XかYを\n1秒間押し続けてください",
            CommsNotice.TutorialShort => "もう少し長く\n押し続けてください",
            CommsNotice.TutorialAccepted => "報告できました。\n装置は正常です。",
            CommsNotice.TutorialReconnect => "左コントローラーを\n確認してください",
            CommsNotice.TutorialReminder => "異変に気づいたら\n今の操作で報告してください\n解析して対処を試みます",
            // ⓪a 名乗り。⚠ **「AI」とは書かない。「エージェント」と書く**（`canon/LEDGER.md` 0080）。
            //    報告練習の成功後に、支援エージェントのスイ本人として名乗る。
            //    1 行 14 文字以内、3 行で読める形に固定する。
            CommsNotice.Greeting => "申し遅れました。\n私は支援エージェントの\nスイです。",
            // ⓪b 歩行の指示。**この連絡が床の矢印を出す**（`WalkGuide.NotifyExplaining`）。
            //    ⚠ 1 文目が 15 文字なので**2 行へ割ってある**（ユーザーの改行は文のあいだの 1 つだけ）。
            CommsNotice.Walk => "開始ポイントを\nマークしました。\n矢印から向かってください。",
            // ⓪c 演出の始まりの告知。**段 0 を抜けた縁**（＝ 導入演出が始まるのと同じフレーム）。
            //    ⓪b が出ていれば引かずに上書きする（`Deliver` は頭から出し直す）。
            CommsNotice.Arrived => "到着しました。\n観測を開始します。",
            // ① 調査の開始。⚠⚠ **押し方はここにしか出ない**（2026-08-16・`canon/LEDGER.md` 0065）。
            //    下段は状態だけを持ち、指示を持たない（押している最中に「押せ」と言い続けない）。
            //    ⚠ キー名（X／Y）を出さない — 装置の面に入力機器の名前が出ると、
            //      調査の記録ではなくゲームの操作説明に見える。左で触れるのは X と Y だけで、
            //      **どちらでもよい**ので「ボタン」で足りる。
            //    ⚠⚠ **①b の 3 行目は「押すと何が起きるか」**（2026-08-19・0096）。
            //      報告を「対処」へ繋ぐ唯一の説明で、③の「排除してください」はこれを読んだ前提に立つ。
            //    ⚠⚠ **①と①b は時間で分ける**（2026-08-19・`canon/LEDGER.md` 0097・ユーザー指定
            //      「調査を開始してください→異変をみつけたら〜と、表示は時間的に分けて。
            //      その間を切り詰める」）。①を読ませ終わった縁で、**間を置かず同じ面のまま**①b へ替わる。
            CommsNotice.Begin => "調査を開始してください。",
            // ①b 押し方と、押すと何が起きるか。
            CommsNotice.BeginHow => "異変を見つけたら\nXかYを長押ししてください\n装置が解析して対処を試みます",
            // ②a 報告時に異常演出が画面を取っていた。
            CommsNotice.MarkLogged => "異常を検出しました",
            // ②b 報告時に異常演出が画面を取っていなかった。
            CommsNotice.MarkNothing => "異状は検出されませんでした",
            // ② 侵食度 0.75 以上で何も無い所へ報告した。判定を持たない一文（0232 — 乗っ取られた装置は
            //    正直に「異常なし」を言わない。「異常なし」は人形が塗り替えた行にしか出ない）。
            CommsNotice.MarkAnalyzing => "装置が解析しています",
            // 嘘の一文（0232）: 読ませる段は**当たりの報告の返事そのもの**。前線が行を渡り切ったコマに
            //    <see cref="TakeoverLieText"/>（その最小編集「…しませんでした」）へ書き換わる。
            CommsNotice.Takeover => "異常を検知しました",
            // ③a すっと浮かぶ一言（打鍵は鳴らない・`canon/LEDGER.md` 0168）。
            CommsNotice.Halt => "止まってください！",
            // ③ 締めの催促。⚠ **これだけが体験者自身を名指しする**（0096）。
            //    読まれないと締めのカットが進まないので、いちばん強い言い方をしている。
            CommsNotice.Prompt => "異変があなたを\n取り込もうとしています。\n排除してください。",
            _ => "",
        };

        /// <summary>
        /// 文面（English・2026-09-03）。<b>日本語と同じことを、同じ順で言う。</b>
        ///
        /// ⚠ <b>制約は日本語と同じ 2 つ</b>（<c>CommsNoticeTextTests</c> が機械で落とす）:
        /// 1 行は<b>半角 28 文字</b>まで（＝ 全角 14）、<b>3 行</b>まで。
        /// ⚠ <b>キー名（X／Y）を出さない</b> — 日本語と同じ理由（装置の面に入力機器の名前が出ると
        /// 調査の記録ではなくゲームの操作説明に見える）。
        /// ⚠ 「調査」は survey、「解析」は analyse で通す（手元のゲージの "Analysing" と対）。
        /// </summary>
        private static string TextEn(CommsNotice n) => n switch
        {
            CommsNotice.ControllerDisconnected => "Hold the left controller\nand press X or Y.",
            CommsNotice.ControllerUntracked => "Move the left controller\nslightly forward.",
            CommsNotice.ControllerStaff => "Controller not found.\nPlease call a staff member.",
            CommsNotice.ControllerConfirmed => "Left controller confirmed.",
            CommsNotice.Tutorial => "Let's check the device.\nHold X or Y\nfor one second.",
            CommsNotice.TutorialShort => "Keep holding the button\na little longer.",
            CommsNotice.TutorialAccepted => "Report sent.\nDevice operating normally.",
            CommsNotice.TutorialReconnect => "Check the left controller.",
            CommsNotice.TutorialReminder => "If you notice an anomaly,\nreport it the same way.\nI will analyse and respond.",
            CommsNotice.Greeting => "My apologies.\nI am Sui,\nyour support agent.",
            CommsNotice.Walk => "Start point marked.\nFollow the arrow.",
            CommsNotice.Arrived => "You have arrived.\nObservation will now begin.",
            CommsNotice.Begin => "Begin the survey.",
            // ①b 3 行目は「押すと何が起きるか」（日本語と同じ役割・0096）。
            CommsNotice.BeginHow => "If you see an anomaly,\nhold X or Y and the device\nwill analyse it.",
            CommsNotice.MarkLogged => "An anomaly was detected.",
            CommsNotice.MarkNothing => "No anomaly was detected.",
            CommsNotice.MarkAnalyzing => "Analyzing.",
            CommsNotice.Takeover => "An anomaly was detected.",
            CommsNotice.Halt => "Please stop!",
            // ③ ここだけが体験者自身を名指しする（0096）。
            CommsNotice.Prompt => "An anomaly is trying to\nabsorb you.\nRemove it.",
            _ => "",
        };

        /// <summary>文面（Français）。<see cref="TextEn"/> と同じ規律。</summary>
        private static string TextFr(CommsNotice n) => n switch
        {
            CommsNotice.ControllerDisconnected => "Tenez la manette gauche\net appuyez sur X ou Y.",
            CommsNotice.ControllerUntracked => "Avancez légèrement\nla manette gauche.",
            CommsNotice.ControllerStaff => "Manette introuvable.\nAppelez un membre\ndu personnel.",
            CommsNotice.ControllerConfirmed => "Manette gauche confirmée.",
            CommsNotice.Tutorial => "Vérifions le dispositif.\nMaintenez X ou Y\npendant une seconde.",
            CommsNotice.TutorialShort => "Maintenez le bouton\nun peu plus longtemps.",
            CommsNotice.TutorialAccepted => "Signalement envoyé.\nLe dispositif fonctionne.",
            CommsNotice.TutorialReconnect => "Vérifiez la manette gauche.",
            CommsNotice.TutorialReminder => "Si vous voyez une anomalie,\nsignalez-la ainsi.\nJe tenterai de la traiter.",
            CommsNotice.Greeting => "Veuillez m'excuser.\nJe suis Sui,\nvotre agente de soutien.",
            CommsNotice.Walk => "Point de départ marqué.\nSuivez la flèche.",
            CommsNotice.Arrived => "Vous êtes arrivé.\nL'observation commence.",
            CommsNotice.Begin => "Commencez l'enquête.",
            CommsNotice.BeginHow => "Si vous voyez une anomalie,\nmaintenez X ou Y.\nL'appareil l'analysera.",
            CommsNotice.MarkLogged => "Anomalie détectée.",
            CommsNotice.MarkNothing => "Aucune anomalie détectée.",
            CommsNotice.MarkAnalyzing => "Analyse en cours.",
            CommsNotice.Takeover => "Anomalie détectée.",
            CommsNotice.Halt => "Arrêtez-vous !",
            CommsNotice.Prompt => "Une anomalie tente de vous\nabsorber.\nSupprimez-la.",
            _ => "",
        };

        /// <summary>
        /// 体験者が選んだ言語の文面（<see cref="ShowLanguage.Current"/>）。
        /// 選ぶのは体験前の注意書きが出ているあいだだけなので、<b>この面が開いている最中に
        /// 言語が変わることはない</b>（＝ 打っている途中で文面が入れ替わらない）。
        /// </summary>
        private static string TextFor(CommsNotice n) => TextFor(n, ShowLanguage.Current);

        /// <summary>言語を明示した文面（テストと <c>menu text-audit</c> 用）。</summary>
        private static string TextFor(CommsNotice n, ShowLang lang) => lang switch
        {
            ShowLang.En => TextEn(n),
            ShowLang.Fr => TextFr(n),
            _ => TextJa(n),
        };

        /// <summary>
        /// その連絡の文面（テストと <c>menu comms-preview</c> 用）。
        /// ⚠ <b>全部の文面を機械で測れるようにするために公開している。</b>
        /// <see cref="LongestNoticeText"/> だけを測っていた頃は、**行数の最悪が別の文面にある**と
        /// 誰も気づけなかった（0079 で①が 2 行・⓪b が 3 行になって顕在化した）。
        /// </summary>
        public static string NoticeText(CommsNotice n) => TextFor(n);

        /// <summary>言語を明示した文面（テストが 3 言語ぶん測るために公開している）。</summary>
        public static string NoticeText(CommsNotice n, ShowLang lang) => TextFor(n, lang);

        /// <summary>
        /// 乗っ取られた本文。Takeover では真実と同位置に重ね、左から右の前線で赤いこの文へ置換する。
        /// Takeover 後の通常報告は最初からこの文だけを出す。
        /// </summary>
        public static string TakeoverLieText(ShowLang lang) => lang switch
        {
            ShowLang.En => "No anomaly was detected.",
            ShowLang.Fr => "Aucune anomalie détectée.",
            _ => "異常は検出されませんでした",
        };

        /// <summary>その言語で画へ出る文面ぜんぶ（連絡の文面 ＋ 嘘の書き換え先）。幅の物差しとテストが読む。</summary>
        public static System.Collections.Generic.IEnumerable<string> AllBodies(ShowLang lang)
        {
            foreach (CommsNotice n in System.Enum.GetValues(typeof(CommsNotice)))
                yield return TextFor(n, lang);
            yield return TakeoverLieText(lang);
        }

        /// <summary>
        /// 面を組むときに使う文面 ＝ <b>いちばん長い行を持つもの</b>（①b の 3 行目・14 文字）。
        ///
        /// ⚠ ここを短い文面にすると <c>menu text-audit</c> が<b>最悪の行を測らない</b>ので
        /// 「枠に収まっている」と嘘をつく。実行時はどの文面でも <see cref="SetNotice"/> が組み直す。
        /// ⚠ <b>行数の最悪（4 行 ＝ ①）はここでは測れない。</b> 縦の座りは
        /// <c>menu comms-preview</c> の絵で見る。
        /// ⚠⚠ <b>2026-08-23 に⓪a → ①bへ移した</b>（`canon/LEDGER.md` 0124）。名乗りが
        /// 「私は調査を支援する／エージェントです」＝ 2 行に割れて最長が 9 文字になり、
        /// ①bの 3 行目「装置が解析して対処を試みます」＝ 14 文字が最長になった。
        /// <c>CommsNoticeTextTests.LongestNoticeText_ReallyHasTheLongestLine</c> が
        /// **本当に最長かを機械で確かめる**ので、文面を触った人はそこで落ちる。
        /// </summary>
        /// <remarks>
        /// ⚠⚠ <b>2026-09-03 に手で選ぶのをやめた</b>（言語が 3 つになったので）。
        /// 3 言語 × 8 文面の中から<b>いちばん長い行を持つ文面を機械が選ぶ</b> —
        /// 手で書いておくと、English だけ長い行があるのに日本語の文面を宣言したまま気づけない
        /// （選び間違いは「その言語のときだけ枠から出る」＝ 実機で 1 言語だけ壊れる形で出る）。
        /// ⚠ 幅の物差しは <see cref="HmdTextStyle.LineWidth"/>（テストもここを使う ＝ 1 か所）。
        /// </remarks>
        public static string LongestNoticeText => _longestNoticeText ??= FindLongestNoticeText();

        private static string? _longestNoticeText;

        private static string FindLongestNoticeText()
        {
            string best = "";
            float bestW = -1f;
            foreach (ShowLang lang in ShowLanguage.All)
            foreach (string body in AllBodies(lang))
            {
                if (string.IsNullOrEmpty(body)) continue;
                float w = 0f;
                foreach (string line in body.Split('\n'))
                {
                    float lw = HmdTextStyle.LineWidth(line);
                    if (lw > w) w = lw;
                }
                if (w > bestW) { bestW = w; best = body; }
            }
            return best;
        }

        private readonly CommsPanelLogic _logic = new CommsPanelLogic();
        private readonly CommsCueLogic _cue = new CommsCueLogic();
        private readonly CommsInvasionLogic _invasion = new CommsInvasionLogic();
        private readonly YawFollowLogic _yawFollow = new YawFollowLogic();

        private Transform? _root;
        // HMD 追従は `_root`、乗っ取り中の面の揺れはその子に分離する。
        // LateUpdate が根の world pose を更新しても、Apply が書いた local pose は消えない。
        private Transform? _visualRoot;
        private MeshRenderer? _panelRenderer;
        private MeshRenderer? _dividerRenderer;
        // 文字を切るステンシルの quad（地と同じ寸法・色は書かない）。
        private MeshRenderer? _stencilRenderer;
        private Material? _stencilMat;
        private const int AvatarQueue = 4985;

        // AI の侵食。映像劣化とは分離し、2-C の人形視点と 3-A の人形表示で段階的に進む。
        private float _glitchLevel;
        // プレビュー（`menu comms-preview -Set decay=`）が注入する進み。**負なら実機の値を読む**。
        private float _decayOverride = -1f;
        private float _previewTimeSec;
        // 呪いの立ち上がりの観測（面が開いてから斑が目標へ届くまでの秒）。
        private float _curseOpenAt;
        private bool _curseRampReported;
        // 走り書きの行（面のローカル m: x0, x1, 行の中心 y, 字の高さ）と、その行の字順の範囲。
        private readonly Vector4[] _lineRects = new Vector4[MaxScrawlLines];
        private readonly int[] _lineFirstChar = new int[MaxScrawlLines];
        private readonly int[] _lineLastChar = new int[MaxScrawlLines];
        // ⚠ 地の色は**シェーダによってプロパティ名が違う**（URP は `_BaseColor` / 組み込みは `_Color`）。
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        // 半透明へ倒すためのプロパティ（URP の Unlit）。⚠ 組み込みの `Unlit/Color` には 1 つも無い。
        private static readonly int SurfaceId = Shader.PropertyToID("_Surface");
        private static readonly int BlendId = Shader.PropertyToID("_Blend");
        private static readonly int SrcBlendId = Shader.PropertyToID("_SrcBlend");
        private static readonly int DstBlendId = Shader.PropertyToID("_DstBlend");
        // compositor alpha の別ブレンド（0227）。URP の Unlit が持つ __srcA / __dstA。
        private static readonly int SrcBlendAlphaId = Shader.PropertyToID("_SrcBlendAlpha");
        private static readonly int DstBlendAlphaId = Shader.PropertyToID("_DstBlendAlpha");
        private static readonly int ZWriteId = Shader.PropertyToID("_ZWrite");
        private static readonly int AlphaClipId = Shader.PropertyToID("_AlphaClip");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        // 顔の枠（`CommsAvatar.shader`）。
        private static readonly int FaceTexId = Shader.PropertyToID("_Face");
        private static readonly int Face2TexId = Shader.PropertyToID("_Face2");
        private static readonly int FaceMixId = Shader.PropertyToID("_FaceMix");
        private static readonly int OpacityId = Shader.PropertyToID("_Opacity");
        private static readonly int RadiusId = Shader.PropertyToID("_Radius");
        private static readonly int StrokeId = Shader.PropertyToID("_Stroke");
        private static readonly int FaceOnId = Shader.PropertyToID("_FaceOn");
        // 呪いの斑（`CommsCurse.hlsl`）。顔と地が同じ名前で受ける。
        private static readonly int OriginId = Shader.PropertyToID("_Origin");
        private static readonly int SizeId = Shader.PropertyToID("_Size");
        // 左から右へ進む前線。顔・地・文字が同じ panel-local x を受ける。
        private static readonly int SweepId = Shader.PropertyToID("_Sweep");
        // 乱れ（0231）。帯ごとの値は C# が作って 3 つの材質へ同じものを配る。
        private static readonly int TearId = Shader.PropertyToID("_Tear");
        private static readonly int TearShiftAId = Shader.PropertyToID("_TearShiftA");
        private static readonly int TearShiftBId = Shader.PropertyToID("_TearShiftB");
        private static readonly int TearDropAId = Shader.PropertyToID("_TearDropA");
        private static readonly int TearDropBId = Shader.PropertyToID("_TearDropB");
        // 地の双子（`CommsPanelPlate.shader`）。
        private static readonly int RectHalfId = Shader.PropertyToID("_RectHalf");
        private static readonly int CurseId = Shader.PropertyToID("_Curse");
        private static readonly int TextCutId = Shader.PropertyToID("_TextCut");
        private static readonly int ScrawlId = Shader.PropertyToID("_Scrawl");
        private static readonly int InkId = Shader.PropertyToID("_Ink");
        private static readonly int InkAlphaId = Shader.PropertyToID("_InkAlpha");
        private static readonly int[] LineIds =
        {
            Shader.PropertyToID("_Line0"), Shader.PropertyToID("_Line1"),
            Shader.PropertyToID("_Line2"), Shader.PropertyToID("_Line3"),
        };
        private static readonly int LineRevealId = Shader.PropertyToID("_LineReveal");
        // TextMeshPro の材質が持つステンシル（`TMP_SDF Overlay.shader`）。
        private static readonly int StencilId = Shader.PropertyToID("_Stencil");
        private static readonly int StencilCompId = Shader.PropertyToID("_StencilComp");
        private static readonly int StencilReadMaskId = Shader.PropertyToID("_StencilReadMask");
        private static readonly int StencilWriteMaskId = Shader.PropertyToID("_StencilWriteMask");
        // 枠を左端から右へ開くために、左端と全幅を覚えておく（地と縁で別々）。
        private float _panelW, _panelLeftX;
        private MeshRenderer? _avatarRenderer;
        private Material? _avatarMat;
        private int _charCount;
        private Material? _panelMat;
        private Material? _dividerMat;
        private Mesh? _panelMesh;
        private TMP_Text? _text;
        private MeshRenderer? _textRenderer;
        private TMP_Text? _lieText;
        private MeshRenderer? _lieTextRenderer;
        private TMP_Text? _hint;
        private Vector3[][]? _textBaseVertices;
        private Color32[][]? _textBaseColors;
        private Vector2[][]? _textBaseUvs;
        private Vector3[][]? _lieBaseVertices;
        private Color32[][]? _lieBaseColors;
        private Vector2[][]? _lieBaseUvs;
        private int _glyphCount;
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
        private bool _promptReadableNotified;
        // 外部 Presentation の中止回数。増えたフレームで進行中の乗っ取りを即座に消す。
        private int _lastPresentationAbortCount;
        private bool _presentationAbortedThisFrame;
        private bool _runRestartHooked;
        private CameraFeelFx? _feelFx;
        // 直前のフレームで何文字出ていたか。**打鍵音はこの増分から鳴らす**（下の Apply）。
        private int _lastShown;
        // ⚠⚠ いまの連絡は**すっと浮かぶ出方**か（`canon/LEDGER.md` 0168）。
        //    true のあいだ打鍵を 1 発も鳴らさない。あの出方は 1 フレームで全文が出るので、
        //    増分で鳴らす Apply は**放っておくと 1 発だけ鳴る**（画では気づけない）。
        private bool _silent;
        // その字が絵を持つか（改行だけ false）。⚠ **全文が出ている一瞬にしか測れない** → SetNotice。
        private bool[]? _charVisible;
        // 嘘の一文（3 周目 A）が出ている最中（面が畳まれるまで）。再報告で頭へ戻さないために CueLogic へ渡す。
        private bool _lieActive;
        // 乗っ取り後の通常報告。真実本文とスイを一瞬も出さず、最初から嘘本文と人形を出す。
        private bool _cursedFromStart;
        // 憑依の出し方（0230）の縁: この連絡で乱れの音を鳴らしたか／塗り替わり切ったか。Deliver で落とす。
        private bool _sweepSfxFired, _sweepDone;
        // 乱れ（0231）の帯ごとの値。`CommsCurseLogic.ComputeTear` が毎フレーム作り、地・顔・ステンシルと本文の頂点が読む。
        private readonly float[] _tearShift = new float[CommsCurseLogic.TearMaxBands];
        private readonly float[] _tearDrop = new float[CommsCurseLogic.TearMaxBands];
        private float _tearFlicker = 1f;
        private float _tearTop;

        // Sweep 1.6 秒の中に 4 回。各 0.12〜0.13 秒、間は 0.28 秒以上静止する。
        private static readonly Vector2[] TearWindows =
        {
            new Vector2(0.02f, 0.14f), new Vector2(0.42f, 0.54f),
            new Vector2(0.82f, 0.94f), new Vector2(1.22f, 1.35f),
        };

        /// <summary>実体を組めたか。<b>false なら一生出ない</b>（テレメトリが読む）。</summary>
        public bool IsBuilt => _text != null;

        /// <summary>
        /// いまの侵食度（0 / 0.25 / 0.75 / 1）。斑の量そのものではない（<see cref="AppliedCurse"/>）。
        /// 呪いが解けても 1 を保つ（テレメトリの <c>commsGl</c>・`analyze-xp-log.py` が保持を見る）。
        /// </summary>
        public float GlitchLevel => _glitchLevel;

        /// <summary>
        /// いま斑に切られている字の数（<b>画に出た側</b>の観測）。字の中心が斑の中にあるものを数える
        /// （画素の切断はステンシルなので、字の端だけ千切れているものは数に入らない）。
        /// ⚠ 斑は面が開いてから 1 秒で立ち上がるので、開いた直後の標本で 0 なのは正常。
        /// <b>侵食度 &gt; 0 の面で本編を通して 1 度も 0 を超えないなら切れていない。</b>
        /// </summary>
        public int CorruptedChars { get; private set; }

        /// <summary>
        /// 直近に顔と地へ書いた斑の量（<b>画に出た側</b>の観測・0 = 通常の面 / 1 = 全面が呪われた双子）。
        /// </summary>
        public float AppliedCurse { get; private set; }

        /// <summary>斑の目標（侵食度と文面から決めた値。<see cref="AppliedCurse"/> はこれへ 1 秒で寄る）。</summary>
        public float CurseTarget { get; private set; }

        /// <summary>斑が目標へ届いた回数（面が開くたびに 1 回）。</summary>
        public int CurseRampCount { get; private set; }

        /// <summary>直近に斑が目標へ届くまでに掛かった秒（面が開いてから）。</summary>
        public float LastCurseRampSec { get; private set; }

        /// <summary>
        /// 地の双子のシェーダ（<c>CommsPanelPlate</c>）を引けたか。<b>false なら地は URP の Unlit で
        /// 出て、毛羽立ちも走り書きも文字の切断も出ない</b>（呪いは顔にしか出ない）。
        /// Editor では出るので、この 1 ビットが無いと実機で剥がれていることに気づけない。
        /// </summary>
        public bool PlateBuilt { get; private set; }

        /// <summary>
        /// 地と縁を組めたか。<b>false なら文字と壊れだけが宙に浮く。</b>
        /// ⚠⚠ 2026-08-17 まで実機がまさにこれだった（<c>Unlit/Color</c> がビルドから剥がれていた）。
        /// <b>Editor では出るので、この観測が無いと永久に気づけない。</b>
        /// </summary>
        public bool PanelBuilt => _panelMat != null && _dividerMat != null;

        /// <summary>
        /// 顔を組めたか（<c>canon/LEDGER.md</c> 0071）。
        /// <b>false なら顔が 1 画素も出ない</b> — シェーダがビルドから剥がれた側の症状で、
        /// <c>Unlit/Color</c> と同じ穴（<c>rules/unity-vr.md</c>）。Editor では出るので、
        /// <b>この 1 ビットが無いと実機で消えていることに永久に気づけない</b>。
        /// </summary>
        public bool AvatarBuilt => _avatarMat != null;

        /// <summary>
        /// 掴めた顔の版の枚数（0〜2）。<b>0 なら枠だけが出て中身が空</b>、
        /// <b>1 なら侵食が起きない</b>（スイのまま 3 周目を迎える）。
        /// </summary>
        public int FaceArtCount { get; private set; }

        /// <summary>直近に書いた顔の不透明度（「画に出た」側の観測）。</summary>
        public float AppliedFace { get; private set; }

        /// <summary>
        /// 直近に顔へ書いた斑の量（0 = スイ / 1 = 完全に市松人形）。<see cref="AppliedCurse"/> と同じ値
        /// （顔と地は同じ場を読む）。侵食度 &gt; 0 の面で 1 度も 0 を超えないなら配線が壊れている。
        /// </summary>
        public float AppliedFaceMix { get; private set; }

        /// <summary>主映像の劣化 0..1。通信侵食とは別の物語状態。</summary>
        public float DecayProgress => runDirector != null ? runDirector.ScreenDecay : 0f;

        /// <summary>通信画面の侵食度。実機では 0 / 0.25 / 0.75 / 1 のイベント段階。</summary>
        public float InvasionProgress => _decayOverride >= 0f ? _decayOverride : _invasion.Level;

        /// <summary>
        /// 通信侵食と時刻を外から差し込む（<c>menu comms-preview</c> 専用）。
        /// ⚠ <b>実機では呼ばない。</b> 負を渡すと <see cref="CommsInvasionLogic.Level"/> へ戻る。
        /// </summary>
        public void SetDecayForPreview(float progress01, float timeSec)
        {
            _decayOverride = progress01;
            _previewTimeSec = timeSec;
            TickGlitch(timeSec);
            PushCurseTarget();
        }

        /// <summary><see cref="SetDecayForPreview"/> の意味を明示した新しい名前。</summary>
        public void SetInvasionForPreview(float progress01, float timeSec)
            => SetDecayForPreview(progress01, timeSec);

        /// <summary>いまの段（テレメトリ用）。</summary>
        public CommsStage Stage => _logic.Stage;

        /// <summary>直近の連絡の出方（テレメトリ用）。</summary>
        public CommsDelivery Delivery => _logic.Delivery;

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

        /// <summary>憑依の出し方（0230）の段（テレメトリ用）。他の出方では Off。</summary>
        public CommsPossessionPhase PossessionPhase { get; private set; }

        /// <summary>直近に書いた前線の進み（0 = まだ通常 / 1 = 全面が呪われた双子）。「画に出た」側の観測。</summary>
        public float AppliedSweep { get; private set; }

        /// <summary>直近に書いた乱れの強さ（0231）。Sweep 中の短い 4 回だけ立ち、読ませる段は 0。</summary>
        public float AppliedTear { get; private set; }

        /// <summary>直近のフレームで飛んでいる帯の数（乱れが画へ出た側の観測）。</summary>
        public int TornBands { get; private set; }

        /// <summary>面だけに書いた揺れの強さ（0 = 静止 / 1 = Sweep 中）。</summary>
        public float AppliedShake { get; private set; }

        /// <summary>面だけに書いたローカル位置。HMD 追従の根は含まない。</summary>
        public Vector3 AppliedShakePosition { get; private set; }

        /// <summary>面だけに書いたローカル回転の角度（度）。</summary>
        public float AppliedShakeAngleDeg { get; private set; }

        /// <summary>直近に書いた地の不透明度（「画に出た」側の観測）。</summary>
        public float AppliedPanelAlpha { get; private set; }

        /// <summary>憑依の出し方で出した連絡の数（ラン内・侵食度 0.75 以降の返事と嘘の一文）。</summary>
        public int PossessedCount { get; private set; }

        /// <summary>塗り替わり切った回数。<see cref="PossessedCount"/> と対で出す（出したのに塗り替わらない、を見る）。</summary>
        public int SweepCount { get; private set; }

        /// <summary>嘘の一文（<see cref="CommsNotice.Takeover"/>）を出した回数。ラン 1 回に 1 度のはず。</summary>
        public int LieCount { get; private set; }

        /// <summary>Takeover の面とエラーが出ているあいだ true。</summary>
        public bool TakeoverVisible => _lieActive && _logic.Active;

        /// <summary>位置合わせ中に会話の時計を止めたまま、描画だけを隠しているか。</summary>
        public bool RegistrationHidden { get; private set; }

        /// <summary>指定した導入文が全文表示されたか。導入の状態機械が読ませる時間をここから数える。</summary>
        public bool IsOnboardingNoticeFullyShown(CommsNotice notice)
            => _onboardingActive && _onboardingNotice == notice && LastNotice == notice
               && _logic.Stage == CommsStage.Hold;

        /// <summary>乗っ取りの発火待ちから表示終了まで、報告入力を受け付けない。</summary>
        public bool TakeoverInputBlocked
            => isActiveAndEnabled
               && (TakeoverVisible
                   || (runDirector != null && runDirector.Phase == ShowPhase.Run
                       && timeline != null && !timeline.Suppressed
                       && timeline.PresentationAbortCount <= _lastPresentationAbortCount
                       && _cue.HasPendingTakeover(timeline.DollCatchUpCompletedCount)));

        /// <summary>Takeover 開始からの単調な秒数。段をまたいでも同じ純ロジック時計を読む。</summary>
        public float TakeoverElapsedSec => _logic.TakeoverElapsedSec;

        public float TakeoverFillStartSec => _logic.TakeoverFillStartSec;

        public float FailureAtSec => _logic.FailureAtSec;

        /// <summary>塗り替わりを止めるには遅い時点へ達したか。</summary>
        public bool TakeoverBlockFailed => _logic.TakeoverBlockFailed;

        /// <summary>主画面側へ出す侵入エラーの不透明度。</summary>
        public float TakeoverErrorOpacity => _logic.TakeoverErrorOpacity;

        public float TakeoverReadFocus => _logic.TakeoverReadFocus;

        /// <summary>連絡の面から鳴る音の位置。揺れる子ではなく面の根を使う。</summary>
        public Transform TakeoverSoundAnchor => _root != null ? _root : transform;

        /// <summary>塗り替わりの頭で鳴らした乱れの音の累計。</summary>
        public int SweepSfxCount => sweepSfx != null ? sweepSfx.PlayedCount : 0;

        /// <summary>乱れの音源を掴めているか。<b>false なら塗り替わりは無音。</b></summary>
        public bool SweepSfxBuilt => sweepSfx != null && sweepSfx.HasClips;

        /// <summary>嘘の一文（0232）で、差し替えた嘘の行のうち<b>いま実際に描いている字の数</b>。差し替える前は 0。</summary>
        public int LieChars { get; private set; }

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
            if (TakeoverInputBlocked)
            {
                _markProgress = 0f;
                _markConfirming = false;
                return;
            }
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

        private bool _onboardingActive;
        private CommsNotice _onboardingNotice = CommsNotice.None;

        private void Awake()
        {
            ResolveRefs();
            var takeoverError = GetComponent<CommsTakeoverError>();
            if (takeoverError == null) takeoverError = gameObject.AddComponent<CommsTakeoverError>();
            takeoverError.Configure(this, takeoverErrorPrefab);
            Build();
            Apply(CommsWeights.Hidden);
        }

        private void OnDisable()
        {
            _feelFx?.CancelTakeoverStatic();
            _logic.Disable();
            _cue.ResetRun();
            _promptReadableNotified = false;
            _invasion.Reset();
            _glitchLevel = 0f;
            _onboardingActive = false;
            _onboardingNotice = CommsNotice.None;
            ResetPossessionVisual();
            Apply(CommsWeights.Hidden);
            typeSfx?.StopAll();
            sweepSfx?.StopAll();
        }

        private void OnDestroy()
        {
            OnDestroyHooks();
            if (_panelMat != null) Destroy(_panelMat);
            if (_dividerMat != null) Destroy(_dividerMat);
            if (_stencilMat != null) Destroy(_stencilMat);
            if (_avatarMat != null) Destroy(_avatarMat);
            if (_panelMesh != null) Destroy(_panelMesh);
        }

        private void ResolveRefs()
        {
            if (runDirector == null) runDirector = FindObjectOfType<ShowRunDirector>();
            if (showControl == null) showControl = FindObjectOfType<ShowControlClient>();
            if (showControl != null) showControl.VisitorMarkBlockedProvider = IsTakeoverInputBlocked;
            if (timeline == null) timeline = FindObjectOfType<TimelineDirector>();
            if (intro == null) intro = FindObjectOfType<IntroDirector>();
            if (walkGuide == null) walkGuide = FindObjectOfType<WalkGuide>();
            if (_feelFx == null) _feelFx = FindObjectOfType<CameraFeelFx>();
            // ⚠ 打鍵音は**この面が持つ**（`ShowSoundDirector` は毎フレーム外から状態を見る層で、
            //    1 秒に 12 回・字の刻みちょうどには鳴らせない）。切替音と同じ構え。
            if (typeSfx == null) typeSfx = GetComponent<TypeAudioCue>();
            if (typeSfx == null) typeSfx = gameObject.AddComponent<TypeAudioCue>();
            // 塗り替わりの頭の乱れの音（0230）。打鍵と同じ構え（縁を取る場所が鳴らす）。
            if (sweepSfx == null) sweepSfx = GetComponent<CurseSweepAudioCue>();
            if (sweepSfx == null) sweepSfx = gameObject.AddComponent<CurseSweepAudioCue>();
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
            if (showControl != null && showControl.VisitorMarkBlockedProvider == IsTakeoverInputBlocked)
                showControl.VisitorMarkBlockedProvider = null;
        }

        private bool IsTakeoverInputBlocked() => TakeoverInputBlocked;

        private void OnRunRestarted()
        {
            _feelFx?.CancelTakeoverStatic();
            _onboardingActive = false;
            _onboardingNotice = CommsNotice.None;
            _cue.ResetRun();
            _promptReadableNotified = false;
            _invasion.Reset();
            _glitchLevel = 0f;
            _lastMarkCount = showControl != null ? showControl.VisitorMarkCount : 0;
            _lastPresentationAbortCount = timeline != null ? timeline.PresentationAbortCount : 0;
            _presentationAbortedThisFrame = false;
            _logic.Disable();
            ResetPossessionVisual();
            Apply(CommsWeights.Hidden);
            // 前の体験者の打鍵を次のランへ持ち越さない（`ShowSoundDirector.ResetRun` と同じ流儀）。
            typeSfx?.StopAll();
            sweepSfx?.StopAll();
        }

        /// <summary>連絡を 1 通出す。<b>すでに出ていれば頭から出し直す</b>（重ねない）。</summary>
        public void Deliver(CommsNotice notice)
            => Deliver(notice, persistent: false, pulse: true);

        /// <summary>HMD導入の文面を、次の状態へ進むまで同じ面に保つ。</summary>
        public void SetOnboardingNotice(CommsNotice notice)
        {
            if (!CommsCueLogic.IsOnboarding(notice) && notice != CommsNotice.Greeting) return;
            if (_onboardingActive && _onboardingNotice == notice) return;
            _onboardingActive = true;
            _onboardingNotice = notice;
            Deliver(notice, persistent: true, pulse: false);
        }

        /// <summary>導入の連絡面を畳み、通常の自動連絡へ返す。</summary>
        public void ClearOnboardingNotice()
        {
            if (!_onboardingActive) return;
            _onboardingActive = false;
            _onboardingNotice = CommsNotice.None;
            _logic.ReleasePersistent();
        }

        private void Deliver(CommsNotice notice, bool persistent, bool pulse)
        {
            if (!IsBuilt || notice == CommsNotice.None) return;
            // 乗っ取りは Guide だけでなく、後続通知でも頭出し・中断しない。
            if (_logic.Delivery == CommsDelivery.Possessed && _logic.Active) return;
            if (notice == CommsNotice.Takeover)
            {
                _markProgress = 0f;
                _markConfirming = false;
            }
            // ⚠⚠ **床の矢印はこの連絡と対で出る**（`canon/LEDGER.md` 0079 の赤入れ 4
            //    「エージェントが説明し始めるときに、矢印が手前の線から 1 つづつ出てくるような感じに」）。
            //    ここで言わないと `WalkGuide` は保険（12 秒）が切れるまで 1 つも出さないので、
            //    **説明と矢印が別々の出来事になる**。
            // ⚠ 誘導そのものを出すか（幾何が解けたか・被っているか）は向こうが決める。
            //    ここは「説明を始めた」という事実だけを渡す。
            if (notice == CommsNotice.Walk) walkGuide?.NotifyExplaining();
            ResetPossessionVisual();
            CommsDelivery delivery = CommsCueLogic.DeliveryOf(notice, InvasionProgress);
            // 締めの報告は、解除が通った同じフレームで ScreenDecayReleased が立つ。
            // 侵食度 1 のままでも、この返事だけはスイの通常表示へ戻す。
            if (notice == CommsNotice.MarkLogged
                && runDirector != null && runDirector.ScreenDecayReleased)
                delivery = CommsDelivery.Typed;
            // 本番では Takeover が唯一の乗っ取りの縁。それより前の通常報告を赤い嘘へ飛ばさない。
            // プレビューは runDirector を持たないので、侵食度だけで Cursed を直接確認できる。
            if (runDirector != null && !_cue.TakeoverDelivered
                && notice != CommsNotice.Takeover && delivery == CommsDelivery.Cursed)
                delivery = CommsDelivery.Typed;
            _cursedFromStart = delivery == CommsDelivery.Cursed;
            SetNotice(notice, _cursedFromStart ? TakeoverLieText(ShowLanguage.Current) : null);
            if (delivery == CommsDelivery.Possessed || _cursedFromStart) SetLieNotice();
            LastNotice = notice;
            // 文面が決まった所で斑の目標を押し込む（「止まってください！」以降は 0）。
            PushCurseTarget();
            // 打つ尺は文字数から決まる（文面を伸ばせば打つ時間も伸びる）。
            // 読ませる尺は**全文面で同じ 2 秒**（`canon/LEDGER.md` 0092）。
            // ⚠⚠ **③a だけは打たない**（`CommsCueLogic.DeliveryOf`・0168）。すっと浮かんで
            //    打鍵は 1 発も鳴らないので、**鳴るはずの数（`NoticeChars`）も 0 にする** —
            //    ここを字数のままにすると、解析器が「打鍵が字数の半分以下」と言い出す
            //    （`analyze-xp-log.py` は `ev=comms` の `chars` の合計と `typeN` を突き合わせる）。
            // ⚠⚠ **侵食度 0.75 以降は憑依の出し方**（0230）。全文が一気に出るので、これも打鍵 0。
            _silent = delivery != CommsDelivery.Typed;
            if (_silent) NoticeChars = 0;
            if (delivery == CommsDelivery.Possessed) PossessedCount++;
            if (notice == CommsNotice.Takeover)
            {
                _lieActive = true;
                LieCount++;
            }
            _logic.Begin(_charCount, delivery, persistent);
            LastNotice = notice;
            if (pulse) PulseCount++;
            if (!persistent) _cue.NotifyDelivered(notice);
            Debug.Log($"[Comms] AIエージェントからの連絡 {notice}「{TextFor(notice).Replace("\n", "／")}」"
                    + $"（{_charCount} 文字 / "
                    + (delivery == CommsDelivery.Possessed
                        ? $"一気に出る → 読ませる → 左から塗り替わる {_logic.TypeSec:0.00}s・打鍵なし"
                        : _silent ? $"すっと浮かぶ {_logic.TypeSec:0.00}s・打鍵なし"
                                  : $"打つ {_logic.TypeSec:0.00}s") + "）");
        }

        private void Update()
        {
            if (!IsBuilt) return;
            bool registrationActive = showControl != null
                && (showControl.CourseRegistrationActive || showControl.StaffSetupActive);
            if (registrationActive != RegistrationHidden)
            {
                RegistrationHidden = registrationActive;
                if (_visualRoot != null) _visualRoot.gameObject.SetActive(!registrationActive);
                if (registrationActive)
                {
                    typeSfx?.StopAll();
                    sweepSfx?.StopAll();
                }
            }
            if (registrationActive) return;
            if (ObservePresentationAbort()) return;

            if (_onboardingActive)
            {
                _logic.Tick(Time.unscaledDeltaTime);
                Apply(_logic.Weights);
                return;
            }

            // ---- 報告の縁を取る。表示状態は中継前に `ShowControlClient.LastMarkDetected` へ凍らせてある。
            //      解除可否は `LastMarkResolved` の別用途で、通信面の文面には使わない。
            bool markPressed = false;
            if (showControl != null)
            {
                if (showControl.VisitorMarkCount != _lastMarkCount)
                {
                    // 押し戻し（ラン開始で 0 に戻る）は報告ではない。
                    markPressed = showControl.VisitorMarkCount > _lastMarkCount;
                    _lastMarkCount = showControl.VisitorMarkCount;
                }
            }

            // ⚠ **導入でも連絡を出す**（2026-08-17・`canon/LEDGER.md` 0079）。出すのは段 0
            //   （開始待ち）のあいだだけで、タイトルが画面を持っているうちは 1 文字も出さない。
            bool inIntroPhase = runDirector != null && runDirector.Phase == ShowPhase.Intro;
            bool inRun = runDirector != null && runDirector.Phase == ShowPhase.Run;
            if (!inRun && _lieActive)
            {
                // 本編を出たら嘘の一文は畳む（終幕・中止に持ち越さない）。
                _feelFx?.CancelTakeoverStatic();
                ResetPossessionVisual();
                _logic.Disable();
            }
            ObserveInvasion();
            bool introWaiting = inIntroPhase && intro != null && intro.Stage == IntroStage.Black;
            CommsNotice next = _cue.Tick(new CommsCueInput
            {
                inRun = inRun,
                inIntro = inIntroPhase,
                startAuthorized = showControl == null || showControl.StartAuthorized,
                introWaiting = introWaiting,
                panelDoneReading = _logic.DoneReading,
                // ③a は締めの線を踏むか、最終 A の確定進入から 3 秒。演出の開始は待たない。
                closingSec = timeline != null ? timeline.ClosingAreaSec : -1f,
                closingLineDefined = timeline != null && timeline.ClosingLineDefined,
                closingLineCrossed = timeline != null && timeline.ClosingLineCrossed,
                dollCatchUpShowing = timeline != null && timeline.DollCallShowing,
                dollCatchUpCompletedCount = timeline != null ? timeline.DollCatchUpCompletedCount : 0,
                markPressed = markPressed,
                markDetected = markPressed && showControl != null && showControl.LastMarkDetected,
                invasionProgress = InvasionProgress,
                takeoverPlaying = _lieActive,
                takeoverAllowed = inRun,
                takeoverSuppressed = (timeline != null && timeline.Suppressed)
                                     || _presentationAbortedThisFrame,
                dt = Time.unscaledDeltaTime,
            });
            if (next != CommsNotice.None) Deliver(next);

            // ⚠⚠ **段 0 を抜けた瞬間は「引く」のではなく「上書きする」**（2026-08-17 の赤入れ 3）。
            //    それまではここで `_logic.Retract()` を打っていたが、いまは同じ縁で⓪c
            //    「ポイントに到着しました。観測装置を起動します」が届く。⓪b の指示が出ている最中でも
            //    `Deliver` が頭から出し直すので、枠は開いたまま文面だけが替わる。
            //    ⓪c は読ませ終われば自分で引く（`HoldSec`）ので、畳む処理は要らない。

            // ⚠ **押している最中は面を開いたままにする**（`canon/LEDGER.md` 0058）。
            //   本編の外では開かない — 導入・終幕に手元の案内が浮くと世界が壊れる
            //   （元の面が queue 3000 で覆いに潰されていたのと同じ意図）。
            _logic.SetGuideWanted(inRun && !TakeoverInputBlocked && _leftConnected
                                  && (_markProgress > 0f || _markConfirming));

            // 侵食度は面が出ていなくても読む。**斑の立ち上がりは面が開いた縁から**（0229
            //   「出た初めはこれ ← 1s ほどで重なる」）で、それは `CommsPanelLogic` が持つ。
            TickGlitch(Time.unscaledTime);
            PushCurseTarget();
            bool wasActive = _logic.Active;
            _logic.Tick(Time.unscaledDeltaTime);
            if (wasActive && !_logic.Active && _lieActive)
                _feelFx?.NotifyHeartbeatWarningCompleted();
            if (!_promptReadableNotified && runDirector != null
                && LastNotice == CommsNotice.Prompt
                && _logic.Stage == CommsStage.Hold)
            {
                _promptReadableNotified = true;
                runDirector.NotifyClosingPromptReadable();
            }
            if (_logic.Active && !wasActive)
            {
                _curseOpenAt = Time.unscaledTime;
                _curseRampReported = false;
            }
            Apply(_logic.Weights);
            // 斑が目標へ届いた縁を 1 回だけ数える（面が開いてからの秒が「1s ほどで重なる」の観測）。
            if (_logic.Active && !_curseRampReported && CurseTarget > 0.01f
                && AppliedCurse >= CurseTarget * 0.99f)
            {
                _curseRampReported = true;
                CurseRampCount++;
                LastCurseRampSec = Time.unscaledTime - _curseOpenAt;
            }
        }

        private bool ObservePresentationAbort()
        {
            int count = timeline != null ? timeline.PresentationAbortCount : 0;
            // 完了通知を読むより先に止められた場合も、その通知は消費して再開させない。
            _presentationAbortedThisFrame = count > _lastPresentationAbortCount;
            _lastPresentationAbortCount = count;
            if (!_presentationAbortedThisFrame && !(timeline != null && timeline.Suppressed)) return false;
            _feelFx?.CancelTakeoverStatic();
            if (_logic.Delivery != CommsDelivery.Possessed || !_logic.Active) return false;
            ResetPossessionVisual();
            _logic.Disable();
            Apply(CommsWeights.Hidden);
            return true;
        }

        /// <summary>
        /// 斑の目標を決めて <see cref="CommsPanelLogic"/> へ押し込む。
        /// 侵食度そのものではなく <see cref="CommsCurseLogic.MaskFor"/>（覆う面積で決めた閾値）。
        /// 「止まってください！」と続く警告は 0（エージェントがなんとか復帰して助ける — 0070）。
        /// 呪いが解けた後（報告が通った・0129）も 0。
        /// </summary>
        private void PushCurseTarget()
        {
            bool closing = LastNotice == CommsNotice.Halt || LastNotice == CommsNotice.Prompt;
            bool released = runDirector != null && runDirector.ScreenDecayReleased;
            float level = _glitchLevel;
            if (runDirector != null && !_cue.TakeoverDelivered)
                level = 0f;
            float target = closing || released ? 0f : CommsCurseLogic.MaskFor(level);
            CurseTarget = target;
            _logic.SetCurseTarget(target);
        }

        private void LateUpdate()
        {
            if (!IsBuilt || head == null || _root == null) return;

            // ⚠⚠ **引っ込んでいるあいだに種を捨てる**（2026-09-04・ユーザー報告
            //    「エージェントのスクリーンが出るとき、毎回、違うところから回ってくる」）。
            //    追従はこの下の `Step` でしか進まないので、面が畳まれているあいだ
            //    `_yawFollow` は**前に消えた場所のヨー**で凍る。捨てないと、次の連絡は
            //    そこから現在の頭へ向かって回り込んでくる ＝ **出るたびに違う方角から来る**
            //    （体験者は区間を歩いて向きを変えるので、差は 180° まで開く）。
            //    種が無ければ下の `Reseat` が**出る瞬間の頭**を種にするので、面は必ず正面へ開く。
            // ⚠ `TitleScreen` は周回リセットで同じことをしている（`_yawSeeded = false`）。
            //    あちらはランに 1 度しか出ないので縁がそこしかないだけで、規律は同じ。
            if (!_logic.Active)
            {
                _yawSeeded = false;
                return;
            }

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
            _root.rotation = Quaternion.LookRotation(basePos - head.position, Vector3.up);
            _root.position = basePos;
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
            // ⚠ 大きさは**根 1 か所**で決める（`canon/LEDGER.md` 0091）。
            //   個々の寸法へ倍率を配ると、次に足した部品が掛け忘れられて 1 つだけ大きく残る。
            _root.localScale = Vector3.one * Scale;
            var visualGo = new GameObject("CommsVisualRoot");
            visualGo.transform.SetParent(rootGo.transform, worldPositionStays: false);
            Transform visualRoot = visualGo.transform;
            _visualRoot = visualRoot;

            // 地（受信票の面）。⚠ 標準シェーダが見つからなければ**文字だけ**にする
            //    （面が無くても読めるので、体験は止めない）。
            // ⚠ メッシュと幅は**地の外**で決める（壊れの層は地のシェーダが無くても出す）。
            _panelMesh = BuildQuad();
            // ⚠ **面は左へ伸びる**（顔の枠のぶん）。文面の帯は 1mm も動かないので、
            //   原点は従来どおり文面の帯の中心のまま（`CommsFaceLayout` の但し書き）。
            _panelW = CommsFaceLayout.FullW(PanelW);
            _panelLeftX = CommsFaceLayout.LeftX(PanelW);
            // ⚠⚠ **`Unlit/Color` は実機のビルドに入っていない**（2026-08-17 に走行の画で判明）。
            //    組み込みシェーダでも、どのマテリアルからも参照されず Always Included にも無ければ
            //    剥がれる（2026-07-31 の `IntroVeil` と同じ型）。**Editor では出るので気づけない。**
            //    ⇒ 実機で **1 度も地も縁も描かれておらず、文字と壊れだけが宙に浮いていた。**
            //    URP の Unlit は URP のマテリアルが参照しているので必ず入っている。そちらを先に引く。
            Shader? flat = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            // 地は呪われた双子を描けるシェーダ（0229）。⚠ 引けなければ URP の Unlit へ落として
            //   **地だけは出す**（呪いは顔にしか出なくなる）。落ちたことは `PlateBuilt` が観測に出す。
            Shader? plate = Shader.Find(PlateShaderName);
            Shader? stencil = Shader.Find(StencilShaderName);
            // ⚠ 観測（commsCurse の 1 つ目）は**両方**引けたか。片方でも剥がれたら呪いの絵は欠ける。
            PlateBuilt = plate != null && stencil != null;
            if (plate == null)
            {
                Debug.LogWarning($"[Comms] {PlateShaderName} を引けないので地の呪い（毛羽立ち・走り書き）は"
                                 + "出ません（Always Included から外れていないか）");
            }
            if (stencil == null)
            {
                Debug.LogWarning($"[Comms] {StencilShaderName} を引けないので文字は斑で切れません"
                                 + "（Always Included から外れていないか）");
            }
            if (plate != null)
            {
                // 地は固定した寸法で置く。出入りは透明度だけを変える。混ぜ方はシェーダが持つ。
                _panelRenderer = MakeQuad(visualRoot, "CommsPanelQuad",
                                          _panelW, BodyMaxH + HintBandH, 0.012f,
                                          plate, RenderQueue, out _panelMat, translucent: false);
            }
            if (stencil != null)
            {
                // 文字を切るステンシル。地の直後（4981）に描き、色は書かない。
                _stencilRenderer = MakeQuad(visualRoot, "CommsPanelStencil",
                                            _panelW, BodyMaxH + HintBandH, 0.011f,
                                            stencil, RenderQueue + 1, out _stencilMat, translucent: false);
            }
            else if (flat != null)
            {
                _panelRenderer = MakeQuad(visualRoot, "CommsPanelQuad",
                                          _panelW, BodyMaxH + HintBandH, 0.012f,
                                          flat, RenderQueue, out _panelMat, translucent: true);
            }
            if (flat != null)
            {
                // 顔と本文の間にだけ、短い縦罫線を置く。
                _dividerRenderer = MakeQuad(visualRoot, "CommsDivider",
                                            CommsFaceLayout.DividerW, CommsFaceLayout.DividerH, 0.006f,
                                            flat, AvatarQueue, out _dividerMat, translucent: true);
            }
            if (plate == null && flat == null)
            {
                // ⚠ 黙って飛ばさない。2026-08-17 まで警告が 1 行も無かったので、
                //   実機で地が消えていることに走行の画を拡大するまで気づけなかった。
                Debug.LogWarning("[Comms] 地のシェーダを引けないので文字と壊れだけになります"
                                 + "（Universal Render Pipeline/Unlit も Unlit/Color も見つからない）");
            }

            BuildAvatar(visualRoot);

            // ---- 下段（報告の押し方・ゲージ）。**2026-08-16 にコントローラの先からここへ移した**
            //      （`canon/LEDGER.md` 0058）。上段より下・小さく・左揃え。
            //      ⚠ ゲージと見出しの大きさはリッチテキストで組む（`VisitorMarkGuidance`）。
            var hintGo = new GameObject("CommsHint");
            hintGo.transform.SetParent(visualRoot, worldPositionStays: false);
            var hintTmp = hintGo.AddComponent<TextMeshPro>();
            hintTmp.font = jp;
            hintTmp.alignment = TextAlignmentOptions.TopLeft;
            hintTmp.fontSize = FontSize;
            hintTmp.enableWordWrapping = false;
            hintTmp.richText = true;
            hintTmp.color = Ivory;
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

            _text = MakeGlyphSurface(visualRoot, jp, "CommsText", GlyphQueue, Ivory);
            _textRenderer = _text.GetComponent<MeshRenderer>();
            TMP_FontAsset? horror = Resources.Load<TMP_FontAsset>(HorrorFontResourcePath);
            if (horror == null)
            {
                Debug.LogWarning($"[Comms] {HorrorFontResourcePath} を読めないので嘘本文も通常書体で表示します");
                horror = jp;
            }
            else if (horror != jp)
            {
                var fallbacks = horror.fallbackFontAssetTable;
                if (fallbacks == null)
                {
                    fallbacks = new System.Collections.Generic.List<TMP_FontAsset>();
                    horror.fallbackFontAssetTable = fallbacks;
                }
                if (!fallbacks.Contains(jp)) fallbacks.Add(jp);
            }
            _lieText = MakeGlyphSurface(visualRoot, horror, "CommsLieText", GlyphQueue + 1, LieRed);
            _lieTextRenderer = _lieText.GetComponent<MeshRenderer>();
            _lieTextRenderer.enabled = false;
            SetNotice(CommsNotice.None);   // 組み上げたら、まず畳んだ状態にする
            // 本文だけを斑で切る。長押しゲージは乗っ取り後も操作の途中経過を返す。
            ApplyTextStencil(_text);
        }

        /// <summary>
        /// TextMeshPro の材質に「斑のビットが立っている画素は描かない」を入れる（0229）。
        /// ⚠ フォールバックで複数のサブメッシュ材質を持ちうるので <c>fontMaterials</c> 全部へ書く。
        /// ⚠ 書き込みはしない（WriteMask 0）— 文字が他の層のステンシルを汚さない。
        /// ⚠ 地のシェーダが無ければステンシルは 1 画素も立たないので、この設定は何もしない側へ倒れる
        /// （文字は全部出る ＝ 切れないだけで読める）。
        /// </summary>
        private static void ApplyTextStencil(TMP_Text? tmp)
        {
            if (tmp == null) return;
            foreach (Material m in tmp.fontMaterials)
            {
                if (m == null || !m.HasProperty(StencilId) || !m.HasProperty(StencilCompId)) continue;
                m.SetFloat(StencilId, CurseStencilBit);
                m.SetFloat(StencilCompId, (float)UnityEngine.Rendering.CompareFunction.NotEqual);
                if (m.HasProperty(StencilReadMaskId)) m.SetFloat(StencilReadMaskId, CurseStencilBit);
                if (m.HasProperty(StencilWriteMaskId)) m.SetFloat(StencilWriteMaskId, 0f);
            }
        }

        /// <summary>
        /// 顔の枠（角丸）と、その中の顔を組む（<c>canon/LEDGER.md</c> 0071）。
        ///
        /// ⚠ <b>版が無くても枠は出す。</b> 枠は装置の意匠で、顔はその中身。
        /// 中身が来ないときに枠ごと消すと、面の左が黙って空白になって原因に届かない。
        /// ⚠ <b>シェーダを引けなければ警告を出す。</b> 無言で飛ばすと、Editor では出るので
        /// 実機の画を拡大するまで誰も気づけない（<c>Unlit/Color</c> と同じ穴）。
        /// </summary>
        private void BuildAvatar(Transform parent)
        {
            Shader? avatar = Shader.Find(AvatarShaderName);
            if (avatar == null)
            {
                Debug.LogWarning($"[Comms] {AvatarShaderName} を引けないので顔の枠は出しません"
                                 + "（Always Included から外れていないか）");
                return;
            }
            var art = Resources.Load<Texture2D>(FaceResourcePath);
            var doll = Resources.Load<Texture2D>(DollFaceResourcePath);
            FaceArtCount = (art != null ? 1 : 0) + (doll != null ? 1 : 0);
            if (art == null)
            {
                Debug.LogWarning($"[Comms] 顔の版 Resources/{FaceResourcePath} が無いので枠だけになります"
                                 + "（py -3.11 tools/make-comms-face.py）");
            }
            else if (doll == null)
            {
                // ⚠ 出ないのは「3 周目で人形に変わる」という筋そのもの。**画は普通に出る**ので、
                //    走行の絵を見ても気づけない。だから言う。
                Debug.LogWarning($"[Comms] 侵食の版 Resources/{DollFaceResourcePath} が無いので"
                                 + "スイのまま変わりません（py -3.11 tools/make-comms-face.py）");
            }

            _avatarRenderer = MakeAvatar(parent, "CommsAvatar", AvatarQueue,
                                         Ivory, art, doll, avatar,
                                         stroke: CommsFaceLayout.StrokeK, out _avatarMat);
        }

        /// <summary>顔の面を 1 枚作る（本体と複製 2 枚で共有）。色と濃さは <see cref="Apply"/> が書く。</summary>
        private MeshRenderer MakeAvatar(Transform parent, string name, int queue, Color color,
                                        Texture2D? art, Texture2D? doll, Shader shader, float stroke,
                                        out Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, worldPositionStays: false);
            // ⚠ **正方形**（`CommsAvatar.shader` は uv 空間で角丸を解く）。
            go.transform.localScale = new Vector3(CommsFaceLayout.CellM, CommsFaceLayout.CellM, 1f);
            go.transform.localPosition = new Vector3(CommsFaceLayout.CellCenterX(PanelW), 0f,
                                                     CommsFaceLayout.DepthM);
            go.AddComponent<MeshFilter>().sharedMesh = _panelMesh;
            var r = go.AddComponent<MeshRenderer>();
            mat = new Material(shader) { name = name + " (runtime)", renderQueue = queue };
            if (art != null) mat.SetTexture(FaceTexId, art);
            // ⚠ 侵食の版が無ければ**スイを両方へ入れる**。空（黒）を入れると、
            //   進みが上がるにつれて顔が斑に欠けていく ＝ 版が無いことが「別の演出」に化ける。
            mat.SetTexture(Face2TexId, doll != null ? doll : art);
            mat.SetColor(ColorId, color);
            mat.SetFloat(RadiusId, CommsFaceLayout.RadiusK);
            mat.SetFloat(StrokeId, stroke);
            mat.SetFloat(FaceOnId, art != null ? 1f : 0f);
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.enabled = false;
            return r;
        }

        /// <summary>
        /// 文面の面を 1 枚組む（本体と、表示側のバグ用の複製 2 枚で共有）。
        /// ⚠ <b>設定を 3 か所に書かない</b> — 折り返し幅も大きさも揃えもここ 1 つから出る。
        /// </summary>
        private static TMP_Text MakeGlyphSurface(Transform parent, TMP_FontAsset? jp,
                                                 string name, int queue, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, worldPositionStays: false);
            var tmp = go.AddComponent<TextMeshPro>();
            if (jp != null) tmp.font = jp;
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
            tmp.color = color;
            // ⚠ 大きさは scale で掛ける（fontSize を上げるとメッシュの座標だけ広がる — LEDGER 0035）。
            //   ⇒ **折り返し幅も scale で割る**。ここを固定値にすると、字の大きさを直したときに
            //     折り返しだけ取り残されて面からはみ出す。
            float scale = TextScale;
            ((RectTransform)go.transform).sizeDelta = new Vector2(PanelW * 0.92f / scale, BodyMaxH / scale);
            go.transform.localScale = Vector3.one * scale;
            var overlay = Shader.Find("TextMeshPro/Distance Field Overlay");
            if (overlay != null) tmp.fontMaterial.shader = overlay;
            tmp.fontMaterial.renderQueue = queue;
            return tmp;
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
        private void SetNotice(CommsNotice notice, string? bodyOverride = null)
        {
            TMP_Text? tmp = _text;
            if (tmp == null) return;
            string body = bodyOverride ?? (notice == CommsNotice.None ? LongestNoticeText : TextFor(notice));
            // 文面はこの原文のまま保つ。乱れは頂点と画素の切断だけに掛ける。

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
            _glyphCount = visible;
            CaptureBaseMesh(tmp, out _textBaseVertices, out _textBaseColors, out _textBaseUvs);
            ComputeLineRects(tmp, info);
            // 組み直しでサブメッシュの材質が増えていても、切断の設定を落とさない。
            ApplyTextStencil(tmp);
            // ⚠ 文面を差し替えたら**打鍵の数えも 0 に戻す**。戻さないと、
            //    前の文面より短い文面では 1 発も鳴らず、長い文面では途中から鳴り始める。
            _lastShown = 0;
            VisibleChars = 0;
        }

        /// <summary>赤い嘘本文を通常本文と同じ位置へ組み、左右クリップ用の元メッシュを保存する。</summary>
        private void SetLieNotice()
        {
            if (_lieText == null || _text == null) return;
            _lieText.maxVisibleCharacters = int.MaxValue;
            _lieText.text = TakeoverLieText(ShowLanguage.Current);
            _lieText.transform.localPosition = _text.transform.localPosition;
            _lieText.ForceMeshUpdate(ignoreActiveState: true, forceTextReparsing: true);
            CaptureBaseMesh(_lieText, out _lieBaseVertices, out _lieBaseColors, out _lieBaseUvs);
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
        /// 走り書きを乗せる行の矩形（面のローカル m）を、組み上がった字の実寸から測る。
        /// 字の頂点は TMP のローカル単位なので、<see cref="TextScale"/> と本文の置き場所で面へ写す。
        /// </summary>
        private void ComputeLineRects(TMP_Text tmp, TMP_TextInfo? info)
        {
            var minX = new float[MaxScrawlLines];
            var maxX = new float[MaxScrawlLines];
            var minY = new float[MaxScrawlLines];
            var maxY = new float[MaxScrawlLines];
            for (int l = 0; l < MaxScrawlLines; l++)
            {
                _lineRects[l] = Vector4.zero;
                _lineFirstChar[l] = int.MaxValue;
                _lineLastChar[l] = -1;
                minX[l] = minY[l] = float.PositiveInfinity;
                maxX[l] = maxY[l] = float.NegativeInfinity;
            }
            if (info == null) return;
            for (int i = 0; i < info.characterCount; i++)
            {
                TMP_CharacterInfo ch = info.characterInfo[i];
                if (!ch.isVisible) continue;
                int line = ch.lineNumber;
                if (line < 0 || line >= MaxScrawlLines) continue;
                minX[line] = Mathf.Min(minX[line], ch.bottomLeft.x);
                maxX[line] = Mathf.Max(maxX[line], ch.topRight.x);
                minY[line] = Mathf.Min(minY[line], ch.bottomLeft.y);
                maxY[line] = Mathf.Max(maxY[line], ch.topRight.y);
                _lineFirstChar[line] = Mathf.Min(_lineFirstChar[line], i);
                _lineLastChar[line] = Mathf.Max(_lineLastChar[line], i);
            }
            float scale = TextScale;
            Vector3 pos = tmp.transform.localPosition;
            for (int l = 0; l < MaxScrawlLines; l++)
            {
                if (_lineLastChar[l] < 0) continue;
                _lineRects[l] = new Vector4(pos.x + minX[l] * scale, pos.x + maxX[l] * scale,
                                            pos.y + (minY[l] + maxY[l]) * 0.5f * scale,
                                            (maxY[l] - minY[l]) * scale);
            }
        }

        private static void CaptureBaseMesh(TMP_Text text, out Vector3[][] vertices,
                                            out Color32[][] colors, out Vector2[][] uvs)
        {
            TMP_MeshInfo[] meshInfo = text.textInfo.meshInfo;
            vertices = new Vector3[meshInfo.Length][];
            colors = new Color32[meshInfo.Length][];
            uvs = new Vector2[meshInfo.Length][];
            for (int i = 0; i < meshInfo.Length; i++)
            {
                vertices[i] = (Vector3[])meshInfo[i].vertices.Clone();
                colors[i] = (Color32[])meshInfo[i].colors32.Clone();
                uvs[i] = (Vector2[])meshInfo[i].uvs0.Clone();
            }
        }

        private int ApplySplitTruthMesh(float reveal, float globalAlpha)
        {
            if (_text == null || _textBaseVertices == null || _textBaseColors == null || _textBaseUvs == null)
                return 0;
            TMP_TextInfo info = _text.textInfo;
            if (info.meshInfo.Length != _textBaseVertices.Length) return 0;
            RestoreMesh(info, _textBaseVertices, _textBaseColors, _textBaseUvs);
            float exact = Mathf.Clamp01(reveal) * _charCount;
            float charSec = _charCount > 0 ? _logic.TypeSec / _charCount : 0f;
            float boundaryPanel = Mathf.Lerp(_panelLeftX, _panelLeftX + _panelW, AppliedSweep);
            float boundaryLocal = (boundaryPanel - _text.transform.localPosition.x) / TextScale;
            int overwritten = 0;
            for (int i = 0; i < info.characterCount; i++)
            {
                TMP_CharacterInfo ch = info.characterInfo[i];
                if (!ch.isVisible) continue;
                bool appeared = exact > i;
                float alpha = appeared
                    ? (_silent ? 1f : GlyphFadeAlpha(Mathf.Max(0f, exact - i) * charSec)) * globalAlpha
                    : 0f;
                float centerX = (ch.bottomLeft.x + ch.topRight.x) * 0.5f;
                float centerY = (ch.bottomLeft.y + ch.topRight.y) * 0.5f;
                float panelY = _text.transform.localPosition.y + centerY * TextScale;
                if (_lieActive)
                {
                    if (appeared && _text.transform.localPosition.x + centerX * TextScale < boundaryPanel)
                        overwritten++;
                    if (!ClipGlyphX(info, _textBaseVertices, _textBaseUvs, ch, boundaryLocal, keepLeft: false))
                        alpha = 0f;
                }
                else if (AppliedCurse > 0f)
                {
                    float px = _text.transform.localPosition.x + centerX * TextScale;
                    float py = panelY;
                    float halfW = (ch.topRight.x - ch.bottomLeft.x) * 0.35f * TextScale;
                    bool cutCenter = CommsCurseLogic.IsCut(px, py, AppliedCurse);
                    if (cutCenter && appeared) overwritten++;
                    if (cutCenter && CommsCurseLogic.IsCut(px - halfW, py, AppliedCurse)
                        && CommsCurseLogic.IsCut(px + halfW, py, AppliedCurse)) alpha = 0f;
                }
                alpha *= TearAlphaAt(panelY);
                ShiftGlyphX(info, ch, TearShiftAt(panelY) / TextScale);
                SetGlyphAlpha(info, ch, alpha);
            }
            _text.UpdateVertexData(TMP_VertexDataUpdateFlags.Vertices | TMP_VertexDataUpdateFlags.Colors32
                                   | TMP_VertexDataUpdateFlags.Uv0);
            return overwritten;
        }

        private int ApplyLieMesh(float globalAlpha)
        {
            if (_lieText == null || _lieBaseVertices == null || _lieBaseColors == null || _lieBaseUvs == null)
                return 0;
            TMP_TextInfo info = _lieText.textInfo;
            if (info.meshInfo.Length != _lieBaseVertices.Length) return 0;
            RestoreMesh(info, _lieBaseVertices, _lieBaseColors, _lieBaseUvs);
            float progress = _cursedFromStart ? 1f : AppliedSweep;
            float boundaryPanel = Mathf.Lerp(_panelLeftX, _panelLeftX + _panelW, progress);
            float boundaryLocal = (boundaryPanel - _lieText.transform.localPosition.x) / TextScale;
            int visible = 0;
            for (int i = 0; i < info.characterCount; i++)
            {
                TMP_CharacterInfo ch = info.characterInfo[i];
                if (!ch.isVisible) continue;
                bool kept = ClipGlyphX(info, _lieBaseVertices, _lieBaseUvs, ch, boundaryLocal, keepLeft: true);
                float center = _lieText.transform.localPosition.x
                             + (ch.bottomLeft.x + ch.topRight.x) * 0.5f * TextScale;
                float centerY = (ch.bottomLeft.y + ch.topRight.y) * 0.5f;
                float panelY = _lieText.transform.localPosition.y + centerY * TextScale;
                if (kept && center <= boundaryPanel && globalAlpha > 0.004f) visible++;
                ShiftGlyphX(info, ch, TearShiftAt(panelY) / TextScale);
                SetGlyphAlpha(info, ch, kept ? globalAlpha * TearAlphaAt(panelY) : 0f);
            }
            _lieText.UpdateVertexData(TMP_VertexDataUpdateFlags.Vertices | TMP_VertexDataUpdateFlags.Colors32
                                      | TMP_VertexDataUpdateFlags.Uv0);
            return visible;
        }

        private float TearShiftAt(float panelY)
        {
            int band = Mathf.Clamp(CommsCurseLogic.SweepBandOf(panelY, _tearTop),
                                   0, CommsCurseLogic.TearMaxBands - 1);
            return _tearShift[band];
        }

        private float TearAlphaAt(float panelY)
        {
            int band = Mathf.Clamp(CommsCurseLogic.SweepBandOf(panelY, _tearTop),
                                   0, CommsCurseLogic.TearMaxBands - 1);
            return (1f - _tearDrop[band]) * _tearFlicker;
        }

        private static void ShiftGlyphX(TMP_TextInfo info, TMP_CharacterInfo ch, float shift)
        {
            if (Mathf.Abs(shift) <= 0.000001f) return;
            Vector3[] vertices = info.meshInfo[ch.materialReferenceIndex].vertices;
            for (int k = 0; k < 4; k++) vertices[ch.vertexIndex + k].x += shift;
        }

        private static void RestoreMesh(TMP_TextInfo info, Vector3[][] vertices, Color32[][] colors,
                                        Vector2[][] uvs)
        {
            for (int i = 0; i < info.meshInfo.Length; i++)
            {
                System.Array.Copy(vertices[i], info.meshInfo[i].vertices, vertices[i].Length);
                System.Array.Copy(colors[i], info.meshInfo[i].colors32, colors[i].Length);
                System.Array.Copy(uvs[i], info.meshInfo[i].uvs0, uvs[i].Length);
            }
        }

        private static void SetGlyphAlpha(TMP_TextInfo info, TMP_CharacterInfo ch, float alpha)
        {
            Color32[] colors = info.meshInfo[ch.materialReferenceIndex].colors32;
            for (int k = 0; k < 4; k++)
            {
                int vi = ch.vertexIndex + k;
                Color32 c = colors[vi];
                c.a = (byte)(c.a * (byte)Mathf.RoundToInt(Mathf.Clamp01(alpha) * 255f) / 255);
                colors[vi] = c;
            }
        }

        private static bool ClipGlyphX(TMP_TextInfo info, Vector3[][] baseVertices, Vector2[][] baseUvs,
                                       TMP_CharacterInfo ch, float boundary, bool keepLeft)
        {
            int m = ch.materialReferenceIndex;
            int v = ch.vertexIndex;
            Vector3[] source = baseVertices[m];
            Vector2[] sourceUv = baseUvs[m];
            Vector3[] vertices = info.meshInfo[m].vertices;
            Vector2[] uvs = info.meshInfo[m].uvs0;
            float left = Mathf.Min(source[v].x, source[v + 1].x);
            float right = Mathf.Max(source[v + 2].x, source[v + 3].x);
            if (keepLeft)
            {
                if (boundary <= left) return false;
                if (boundary >= right) return true;
                ClipEdge(v + 1, v + 2, boundary);
                ClipEdge(v, v + 3, boundary);
            }
            else
            {
                if (boundary >= right) return false;
                if (boundary <= left) return true;
                ClipEdge(v + 2, v + 1, boundary);
                ClipEdge(v + 3, v, boundary);
            }
            return true;

            void ClipEdge(int fixedIndex, int movedIndex, float x)
            {
                float dx = source[movedIndex].x - source[fixedIndex].x;
                float t = Mathf.Abs(dx) < 0.00001f ? 0f : (x - source[fixedIndex].x) / dx;
                vertices[movedIndex] = Vector3.Lerp(source[fixedIndex], source[movedIndex], t);
                uvs[movedIndex] = Vector2.Lerp(sourceUv[fixedIndex], sourceUv[movedIndex], t);
            }
        }

        private static float Smooth01(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        public static float GlyphFadeAlpha(float ageSec) => Smooth01(ageSec / GlyphFadeSec);

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

        /// <summary>
        /// 面を 1 枚作る（地と縁で共有）。色は <see cref="Apply"/> が毎フレーム書く。
        /// <paramref name="translucent"/> は URP の Unlit を半透明へ倒すか（自前のシェーダは混ぜ方を持つので不要）。
        /// </summary>
        private MeshRenderer MakeQuad(Transform parent, string name, float w, float h, float z,
                                      Shader shader, int queue, out Material mat, bool translucent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = new Vector3(0f, 0f, z);   // 文字より奥
            go.transform.localScale = new Vector3(w, h, 1f);
            go.AddComponent<MeshFilter>().sharedMesh = _panelMesh;
            var r = go.AddComponent<MeshRenderer>();
            mat = new Material(shader) { name = name + " (runtime)" };
            if (translucent) MakeTranslucent(mat);
            mat.renderQueue = queue;   // ⚠ 半透明へ倒したあとに書く（倒す側が queue を上書きする）
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.enabled = false;
            return r;
        }

        /// <summary>
        /// 地と縁を<b>半透明</b>に倒す（2026-08-19・<c>canon/LEDGER.md</c> 0096・
        /// ユーザー指定「スクリーンの背景 → 黒い半透明に」）。
        ///
        /// ⚠⚠ <b>色に alpha を書くだけでは 1 ビットも効かない。</b> URP の Unlit は既定が不透明で、
        /// 面・キーワード・混ぜ方・深度書き込みの<b>4 つを全部倒して初めて</b> alpha が通る。
        /// ⚠ <b>フォールバックの <c>Unlit/Color</c> には alpha が無い</b>（プロパティが 1 つも無いので
        /// ここは丸ごと空振りし、従来どおり不透明で出る）。だから警告を出す — 黙って不透明に
        /// なると、実機の画を拡大するまで誰も気づけない。
        /// ⚠ <b>深度は書かない。</b> 書くと後ろの文字（TMP Overlay）が自分の地に隠れる。
        ///
        /// ⚠⚠ <b>compositor alpha は「足す」だけにする</b>（2026-09-15・<c>canon/LEDGER.md</c> 0227）。
        /// Quest の compositor は<b>フレームバッファの alpha が 1 未満の画素にパススルーを混ぜる</b>。
        /// URP の Unlit は既定で RGB と同じ <c>SrcAlpha OneMinusSrcAlpha</c> を alpha にも掛けるので、
        /// 地（alpha 0.72）は題字の黒が書いた alpha 1 を <b>0.72 へ置き換え</b>、その画素に 28% の現実が
        /// 混ざっていた ＝ 題字の前の暗闇で「背景ごしにパススルーが見える」。alpha を
        /// <c>One OneMinusSrcAlpha</c>（<c>TitleVeil</c> / <c>CommsAvatar</c> と同じ式）にすると、
        /// 地は下の alpha を<b>閉じる方向にしか</b>動かせない。RGB の混ぜ方（黒い半透明・0096）は変えない。
        /// </summary>
        private static void MakeTranslucent(Material m)
        {
            if (!m.HasProperty(SurfaceId))
            {
                Debug.LogWarning($"[Comms] {m.shader.name} は半透明に倒せないので地が不透明になります");
                return;
            }
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetFloat(SurfaceId, 1f);                     // 1 = Transparent
            if (m.HasProperty(BlendId)) m.SetFloat(BlendId, 0f);   // 0 = Alpha
            if (m.HasProperty(SrcBlendId))
                m.SetFloat(SrcBlendId, (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (m.HasProperty(DstBlendId))
                m.SetFloat(DstBlendId, (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (m.HasProperty(SrcBlendAlphaId))
                m.SetFloat(SrcBlendAlphaId, (float)UnityEngine.Rendering.BlendMode.One);
            if (m.HasProperty(DstBlendAlphaId))
                m.SetFloat(DstBlendAlphaId, (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (m.HasProperty(ZWriteId)) m.SetFloat(ZWriteId, 0f);
            if (m.HasProperty(AlphaClipId)) m.SetFloat(AlphaClipId, 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.DisableKeyword("_ALPHATEST_ON");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            m.SetShaderPassEnabled("ShadowCaster", false);
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
            // 憑依の出し方（0230）の段。他の出方では Off のまま。
            CommsPossessionSample poss = _logic.PossessionSample;
            PossessionPhase = poss.phase;
            bool keepDollState = KeepDollState();
            AppliedSweep = _cursedFromStart ? 1f : Mathf.Clamp01(w.sweep);
            if (!_logic.Active)
            {
                _lieActive = false;
                _cursedFromStart = false;
            }
            if (poss.phase == CommsPossessionPhase.Sweep && !_sweepSfxFired)
            {
                // 塗り替わりの頭で乱れの音を 1 発。前線が降り始めたのと同じフレーム（絵と音を同じ縁から出す）。
                _sweepSfxFired = true;
                if (_root != null && GetComponent<CommsTakeoverError>()?.UsesProvidedSweepAudio != true)
                    sweepSfx?.Play(_root.position);
            }
            if (poss.phase == CommsPossessionPhase.Cursed && !_sweepDone)
            {
                _sweepDone = true;
                SweepCount++;
            }

            AppliedGlyph = Mathf.Clamp01(w.glyph);
            AppliedOpen = Mathf.Clamp01(w.open);
            // 斑の量は `CommsPanelLogic` が面の開いた縁から 1 秒で立ち上げる（0229）。
            // 憑依の出し方では塗り替わる前 0・塗り替わった後 1（前線の進みは `AppliedSweep`）。
            AppliedCurse = _cursedFromStart || keepDollState ? 1f : Mathf.Clamp01(w.curse);
            float reveal = w.reveal;
            ApplyHint(Mathf.Clamp01(w.hint));
            float pa = Mathf.Clamp01(w.panel) * Smooth01(AppliedOpen);
            AppliedPanelAlpha = Mathf.Lerp(PanelAlpha, TakeoverPanelAlpha, TakeoverReadFocus) * pa;
            AppliedBody = Mathf.Clamp01(w.body);
            ApplyPossessionMotion(poss.phase, AppliedSweep);

            // ⚠⚠ **枠は「出ている帯」だけを覆う**（2026-08-16・`canon/LEDGER.md` 0065）。
            //    下段から指示を剥がしたので、**押していないときは下段に 1 文字も無い**。
            //    それまでの「下端を固定して丈だけ伸びる」ままだと、①の連絡が
            //    **下半分が空の大きな箱**として出る（プレビューの絵で見つけた）。
            //    ⚠ 下段の有無は不透明度だけでは決まらない — 文字が空でも hint は 1 になる。
            float hintK = Mathf.Clamp01(w.hint) * (_hintBody.Length > 0 ? 1f : 0f);
            // 上段の帯 = [0, _bodyBandH]（**文面の実寸**）/ 下段の帯 = [-HintBandH, 0]。
            float top = HintBandTopY + _bodyBandH * AppliedBody;
            float bottom = HintBandTopY - HintBandH * hintK;
            float cy = (top + bottom) * 0.5f;
            float h = Mathf.Max(0f, top - bottom);
            if (h > 0.0005f) h = Mathf.Max(h, CommsFaceLayout.MinBoxH);
            bool lit = pa > 0.002f && h > 0.0005f;
            // `_Sweep` は左→右の前線だけを持つ。縦帯の上端と高さは `_Tear.zw` へ分離する。
            var sweep = new Vector4(AppliedSweep, _panelLeftX, _panelLeftX + _panelW,
                                    CommsCurseLogic.TearBandM);
            _tearTop = top;
            AppliedTear = TearStrengthAt(poss.phase, AppliedSweep);
            int bandCount = Mathf.Clamp(CommsCurseLogic.SweepBandCount(top, bottom),
                                        0, CommsCurseLogic.TearMaxBands);
            CommsCurseLogic.ComputeTear(AppliedTear, TakeoverElapsedSec, bandCount,
                                        _tearShift, _tearDrop, out _tearFlicker);
            int torn = 0;
            for (int i = 0; i < _tearShift.Length; i++) if (Mathf.Abs(_tearShift[i]) > 0.0001f) torn++;
            TornBands = torn;

            if (_text != null)
            {
                // 出現時刻は従来どおり。見え始めた後の 40ms だけ頂点 alpha を滑らかに立てる。
                int shown = _charCount <= 0 ? 0
                          : Mathf.Clamp(Mathf.CeilToInt(Mathf.Clamp01(reveal) * _charCount), 0, _charCount);
                bool on = !_cursedFromStart && AppliedGlyph > 0.002f && shown > 0;
                if (_textRenderer != null) _textRenderer.enabled = on;
                if (shown > _lastShown && _root != null && !_silent)
                {
                    // 低い描画頻度でも、同じコマに増えた字を間引かない。絵を持つ1字につき1打。
                    for (int i = _lastShown; i < shown; i++)
                        if (IsVisibleChar(i)) typeSfx?.Play(_root.position);
                }
                _lastShown = shown;
                VisibleChars = shown;

                CorruptedChars = ApplySplitTruthMesh(reveal, AppliedGlyph);
            }
            if (_lieText != null)
            {
                bool lieOn = (_lieActive || _cursedFromStart)
                             && AppliedGlyph > 0.002f && reveal > 0.002f;
                if (_lieTextRenderer != null) _lieTextRenderer.enabled = lieOn;
                LieChars = lieOn ? ApplyLieMesh(AppliedGlyph) : 0;
            }

            if (_panelRenderer != null && _panelMat != null)
            {
                SetFlatColor(_panelMat, new Color(PanelColor.r, PanelColor.g, PanelColor.b,
                                                  AppliedPanelAlpha));
                _panelRenderer.enabled = lit;
                if (_panelMat.HasProperty(CurseId))
                    SetPlate(_panelRenderer.transform, _panelMat, _panelLeftX, _panelW, cy, h,
                             pa, reveal, sweep);
                else
                    SetFixedPanel(_panelRenderer.transform, _panelLeftX, _panelW, cy, h);
            }
            if (_stencilRenderer != null && _stencilMat != null)
            {
                _stencilRenderer.enabled = lit && (AppliedCurse > 0.001f || AppliedSweep > 0.001f);
                SetStencilQuad(_stencilRenderer.transform, _stencilMat, _panelLeftX, _panelW, cy, h, sweep);
            }
            if (_dividerRenderer != null && _dividerMat != null)
            {
                SetFlatColor(_dividerMat, new Color(Ivory.r, Ivory.g, Ivory.b, 0.68f * pa));
                _dividerRenderer.enabled = lit;
                float dividerH = CommsFaceLayout.DividerH * Smooth01(AppliedOpen);
                _dividerRenderer.transform.localPosition = new Vector3(
                    CommsFaceLayout.DividerCenterX(PanelW), cy, 0.006f);
                _dividerRenderer.transform.localScale = new Vector3(CommsFaceLayout.DividerW, dividerH, 1f);
            }
            ApplyAvatar(pa, cy, lit, sweep);
        }

        private static float TearStrengthAt(CommsPossessionPhase phase, float sweep)
        {
            if (phase != CommsPossessionPhase.Sweep) return 0f;
            float t = Mathf.Clamp01(sweep) * CommsPossessionLogic.SweepSec;
            const float edgeSec = 0.02f;
            for (int i = 0; i < TearWindows.Length; i++)
            {
                Vector2 window = TearWindows[i];
                if (t < window.x || t >= window.y) continue;
                float edge = Mathf.Min((t - window.x) / edgeSec, (window.y - t) / edgeSec);
                return CommsPossessionLogic.TearPeak * Smooth01(edge);
            }
            return 0f;
        }

        /// <summary>
        /// Sweep 中だけ面の子を小さく揺らす。単調な Takeover 時計だけを読むので、
        /// Preview が Apply を呼ぶ経路でも本番と同じ pose になる。
        /// </summary>
        private void ApplyPossessionMotion(CommsPossessionPhase phase, float sweep)
        {
            bool moving = phase == CommsPossessionPhase.Sweep
                          && sweep > 0f && sweep < 1f && _visualRoot != null;
            AppliedShake = moving ? 1f : 0f;
            if (!moving)
            {
                AppliedShakePosition = Vector3.zero;
                AppliedShakeAngleDeg = 0f;
                if (_visualRoot != null)
                {
                    _visualRoot.localPosition = Vector3.zero;
                    _visualRoot.localRotation = Quaternion.identity;
                }
                return;
            }

            float t = TakeoverElapsedSec;
            var p = new Vector3(
                Mathf.Sin(t * 41.3f) * 0.0044f + Mathf.Sin(t * 73.1f + 0.7f) * 0.0015f,
                Mathf.Sin(t * 53.7f + 1.4f) * 0.0034f + Mathf.Sin(t * 89.9f) * 0.0014f,
                0f);
            var euler = new Vector3(
                Mathf.Sin(t * 37.1f + 0.3f) * 0.18f,
                Mathf.Sin(t * 29.7f + 1.1f) * 0.22f,
                Mathf.Sin(t * 43.9f) * 0.37f + Mathf.Sin(t * 67.3f + 0.5f) * 0.12f);
            Quaternion rotation = Quaternion.Euler(euler);
            AppliedShakePosition = p;
            AppliedShakeAngleDeg = Quaternion.Angle(Quaternion.identity, rotation);
            _visualRoot!.localPosition = p;
            _visualRoot.localRotation = rotation;
        }

        /// <summary>憑依の出し方の縁と嘘の一文の状態を落とす（連絡が届いた縁・畳む縁・ラン開始）。</summary>
        private void ResetPossessionVisual()
        {
            _lieActive = false;
            _sweepSfxFired = false;
            _sweepDone = false;
            PossessionPhase = CommsPossessionPhase.Off;
            AppliedSweep = 0f;
            AppliedTear = 0f;
            TornBands = 0;
            ApplyPossessionMotion(CommsPossessionPhase.Off, 0f);
            _tearFlicker = 1f;
            _tearTop = 0f;
            System.Array.Clear(_tearShift, 0, _tearShift.Length);
            System.Array.Clear(_tearDrop, 0, _tearDrop.Length);
            LieChars = 0;
            _cursedFromStart = false;
            AppliedPanelAlpha = 0f;
            _silent = false;
        }

        private void ApplyAvatar(float alpha, float centerY, bool lit, Vector4 sweep)
        {
            if (_avatarRenderer == null || _avatarMat == null) return;
            AppliedFace = lit
                ? Mathf.Clamp01(alpha) * CommsFaceLayout.Reveal(AppliedOpen, PanelW)
                : 0f;
            bool on = AppliedFace > 0.004f;

            // 顔と地と文字は同じ斑の場と同じ量を読む（0229）。別の曲線にすると、
            // 文字と顔が別々の速さで壊れて見える。前線（0230）も同じ値を渡す。
            AppliedFaceMix = AppliedCurse;

            float x = CommsFaceLayout.CellCenterX(PanelW);
            _avatarMat.SetFloat(OpacityId, AppliedFace);
            _avatarMat.SetFloat(FaceMixId, AppliedFaceMix);
            _avatarMat.SetVector(OriginId, new Vector4(x, centerY, 0f, 0f));
            _avatarMat.SetVector(SizeId, new Vector4(CommsFaceLayout.CellM, CommsFaceLayout.CellM, 0f, 0f));
            _avatarMat.SetVector(SweepId, sweep);
            PushTear(_avatarMat);
            _avatarRenderer.enabled = on;
            _avatarRenderer.transform.localPosition = new Vector3(x, centerY, CommsFaceLayout.DepthM);
            _avatarRenderer.transform.localScale = Vector3.one * CommsFaceLayout.CellM;
        }

        /// <summary>
        /// 乗っ取り後は Guide や通常報告を開き直しても人形の状態を保つ。
        /// 4-A の Halt / Prompt、解除済み、新しいランだけは従来どおりスイへ戻す。
        /// </summary>
        private bool KeepDollState()
        {
            if (!_cue.TakeoverDelivered || _logic.Delivery == CommsDelivery.Possessed) return false;
            if (LastNotice == CommsNotice.Halt || LastNotice == CommsNotice.Prompt) return false;
            return runDirector == null || !runDirector.ScreenDecayReleased;
        }

        /// <summary>
        /// 通信侵食を 1 フレーム進める。
        ///
        /// ⚠ <b>面が出ていないあいだも進める。</b> 届いた所で 0 から立ち上げると
        /// 「連絡が来ると壊れる」に見えて、因果が逆になる。
        /// </summary>
        private void ObserveInvasion()
        {
            if (_decayOverride >= 0f || timeline == null) return;
            _invasion.Observe(timeline.CurrentLap, timeline.CurrentCamera,
                              timeline.ActiveStepCueId, timeline.DollReplacementShowing);
        }

        /// <summary>
        /// いまの侵食 0..1。2-C の単発人形視点で 0.25、続く連続列で 0.75、
        /// 3-A の人形が実際に表示された瞬間に 1 となる。新しい体験者まで減らない。
        /// </summary>
        private float ResolveCorruption()
        {
            return CommsCurseLogic.LevelFor(InvasionProgress);
        }

        private void TickGlitch(float timeSec)
        {
            _glitchLevel = ResolveCorruption();
        }

        /// <summary>
        /// 地の双子（<c>CommsPanelPlate.shader</c>）へ寸法と斑と走り書きの行を書く。
        /// quad は毛羽立ちのぶん矩形より <see cref="PlateMarginM"/> ずつ広く、矩形の実寸は uniform で渡す。
        /// </summary>
        private void SetPlate(Transform quad, Material mat, float leftX, float fullW, float centerY,
                              float height, float contentAlpha, float reveal, Vector4 sweep)
        {
            float w = fullW + PlateMarginM * 2f;
            float hh = height + PlateMarginM * 2f;
            float cx = leftX + fullW * 0.5f;
            quad.localScale = new Vector3(w, hh, 1f);
            Vector3 p = quad.localPosition;
            quad.localPosition = new Vector3(cx, centerY, p.z);
            mat.SetVector(OriginId, new Vector4(cx, centerY, 0f, 0f));
            mat.SetVector(SizeId, new Vector4(w, hh, 0f, 0f));
            mat.SetVector(RectHalfId, new Vector4(fullW * 0.5f, height * 0.5f, 0f, 0f));
            mat.SetFloat(CurseId, AppliedCurse);
            mat.SetVector(SweepId, sweep);
            PushTear(mat);
            mat.SetFloat(ScrawlId, 0f);
            mat.SetColor(InkId, Ivory);
            mat.SetFloat(InkAlphaId, contentAlpha);
            // 走り書きは印字が進んだ範囲まで（人形が「打たれた分」を塗りつぶしている）。
            float exact = Mathf.Clamp01(reveal) * _charCount;
            var lineReveal = Vector4.zero;
            for (int l = 0; l < MaxScrawlLines; l++)
            {
                mat.SetVector(LineIds[l], _lineRects[l]);
                if (_lineLastChar[l] < 0) continue;
                float span = Mathf.Max(1, _lineLastChar[l] - _lineFirstChar[l] + 1);
                lineReveal[l] = Mathf.Clamp01((exact - _lineFirstChar[l]) / span);
            }
            mat.SetVector(LineRevealId, lineReveal);
        }

        /// <summary>文字を切るステンシルの quad を地と同じ寸法に置き、斑の量と前線を書く。</summary>
        private void SetStencilQuad(Transform quad, Material mat, float leftX, float fullW, float centerY,
                                    float height, Vector4 sweep)
        {
            float w = fullW + PlateMarginM * 2f;
            float hh = height + PlateMarginM * 2f;
            float cx = leftX + fullW * 0.5f;
            quad.localScale = new Vector3(w, hh, 1f);
            Vector3 p = quad.localPosition;
            quad.localPosition = new Vector3(cx, centerY, p.z);
            mat.SetVector(OriginId, new Vector4(cx, centerY, 0f, 0f));
            mat.SetVector(SizeId, new Vector4(w, hh, 0f, 0f));
            mat.SetVector(RectHalfId, new Vector4(fullW * 0.5f, height * 0.5f, 0f, 0f));
            mat.SetFloat(CurseId, AppliedCurse);
            mat.SetVector(SweepId, sweep);
            PushTear(mat);
            mat.SetFloat(TextCutId, 1f);
        }

        /// <summary>乱れ（0231）の帯ごとの値を材質へ書く。地・顔・ステンシルが**同じ値**を読む。</summary>
        private void PushTear(Material mat)
        {
            mat.SetVector(TearId, new Vector4(AppliedTear, _tearFlicker,
                                              _tearTop, CommsCurseLogic.TearBandM));
            mat.SetVector(TearShiftAId, new Vector4(_tearShift[0], _tearShift[1], _tearShift[2], _tearShift[3]));
            mat.SetVector(TearShiftBId, new Vector4(_tearShift[4], _tearShift[5], _tearShift[6], _tearShift[7]));
            mat.SetVector(TearDropAId, new Vector4(_tearDrop[0], _tearDrop[1], _tearDrop[2], _tearDrop[3]));
            mat.SetVector(TearDropBId, new Vector4(_tearDrop[4], _tearDrop[5], _tearDrop[6], _tearDrop[7]));
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
            string body = !TakeoverInputBlocked && !_lieActive && _leftConnected
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
        /// 面の幅と位置を固定し、縦は出ている上段・下段の実寸へ合わせる。
        /// </summary>
        private static void SetFixedPanel(Transform quad, float leftX, float fullW,
                                          float centerY, float height)
        {
            quad.localScale = new Vector3(fullW, height, 1f);
            Vector3 p = quad.localPosition;
            quad.localPosition = new Vector3(leftX + fullW * 0.5f, centerY, p.z);
        }
    }
}
