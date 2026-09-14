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
    /// <b>出方</b>: 面と文面の位置は固定し、透明度で出入りする。顔と本文の間の短い縦罫線だけが伸びる。
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

        /// <summary>顔の枠と切り抜きのシェーダ。⚠ <b>Always Included に入れてある</b>。</summary>
        private const string AvatarShaderName = "FixedCamVr/CommsAvatar";
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

        /// <summary>各文字が立ち上がる時間。出現時刻と打鍵音は従来の刻みを保つ。</summary>
        public const float GlyphFadeSec = 0.04f;

        private static readonly Color PanelColor = new Color(10f / 255f, 9f / 255f, 8f / 255f, 1f);
        private static readonly Color Ivory = new Color(209f / 255f, 199f / 255f, 184f / 255f, 1f);
        private static readonly Color EchoRed = new Color(135f / 255f, 61f / 255f, 53f / 255f, 1f);
        private static readonly Color DenialRed = new Color(184f / 255f, 48f / 255f, 40f / 255f, 1f);

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
            CommsNotice.TutorialAccepted => "報告を受け取りました",
            CommsNotice.TutorialReconnect => "左コントローラーを\n確認してください",
            CommsNotice.TutorialReminder => "異変に気づいたら\n今の操作で報告してください\n解析して対処を試みます",
            // ⓪a 名乗り。⚠ **「AI」とは書かない。「エージェント」と書く**（`canon/LEDGER.md` 0080）。
            //    ⚠ 紙の依頼書と同じ語（`docs/onsite/handout.html`「調査を支援するエージェント」）。
            //      片方だけ直すと、紙と装置が別のものを指しているように読める。
            //    ⚠ 1 行 14 文字を超える（17 文字）ので**2 行へ割ってある**（2026-08-23・0124）。
            //      任せると「私は調査を支援するエージ／ェントです」と語の途中で切れる。
            CommsNotice.Greeting => "私は調査を支援する\nエージェントです",
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
            CommsNotice.Takeover => "異常なしと判定しました",
            // ③a すっと浮かぶ一言（打鍵は鳴らない・`canon/LEDGER.md` 0168）。
            CommsNotice.Halt => "止まってください！",
            // ③ 締めの催促。⚠ **これだけが体験者自身を名指しする**（0096）。
            //    読まれないと締めのカットが進まないので、いちばん強い言い方をしている。
            CommsNotice.Prompt => "異常があなたを\n取り込もうとしています。\n排除してください。",
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
            CommsNotice.TutorialAccepted => "Report received.",
            CommsNotice.TutorialReconnect => "Check the left controller.",
            CommsNotice.TutorialReminder => "If you notice an anomaly,\nreport it the same way.\nI will analyse and respond.",
            CommsNotice.Greeting => "I am the agent assisting\nthe survey.",
            CommsNotice.Walk => "Start point marked.\nFollow the arrow.",
            CommsNotice.Arrived => "You have arrived.\nObservation will now begin.",
            CommsNotice.Begin => "Begin the survey.",
            // ①b 3 行目は「押すと何が起きるか」（日本語と同じ役割・0096）。
            CommsNotice.BeginHow => "If you see an anomaly,\nhold X or Y and the device\nwill analyse it.",
            CommsNotice.MarkLogged => "An anomaly was detected.",
            CommsNotice.MarkNothing => "No anomaly was detected.",
            CommsNotice.Takeover => "No anomaly was detected.",
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
            CommsNotice.TutorialAccepted => "Signalement reçu.",
            CommsNotice.TutorialReconnect => "Vérifiez la manette gauche.",
            CommsNotice.TutorialReminder => "Si vous voyez une anomalie,\nsignalez-la ainsi.\nJe tenterai de la traiter.",
            CommsNotice.Greeting => "Je suis l'agent qui assiste\ncette enquête.",
            CommsNotice.Walk => "Point de départ marqué.\nSuivez la flèche.",
            CommsNotice.Arrived => "Vous êtes arrivé.\nL'observation commence.",
            CommsNotice.Begin => "Commencez l'enquête.",
            CommsNotice.BeginHow => "Si vous voyez une anomalie,\nmaintenez X ou Y.\nL'appareil l'analysera.",
            CommsNotice.MarkLogged => "Anomalie détectée.",
            CommsNotice.MarkNothing => "Aucune anomalie détectée.",
            CommsNotice.Takeover => "Aucune anomalie n’a été\ndétectée.",
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
        /// 侵食が否定文を奪うとき、赤く残す語の長さ。各言語で「異常なし」に当たる部分だけを返す。
        /// </summary>
        public static int TakeoverDenialPrefixLength(ShowLang lang) => lang switch
        {
            ShowLang.En => "No anomaly".Length,
            ShowLang.Fr => "Aucune anomalie".Length,
            _ => "異常なし".Length,
        };

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
            foreach (CommsNotice n in System.Enum.GetValues(typeof(CommsNotice)))
            {
                string body = TextFor(n, lang);
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
        private MeshRenderer? _panelRenderer;
        private MeshRenderer? _dividerRenderer;
        private const int AvatarQueue = 4985;
        private const float TakeoverStretchM = 0.04f;
        private const float TakeoverThinK = 0.22f;

        // AI の侵食。映像劣化とは分離し、2-C の人形視点と 3-A の人形表示で段階的に進む。
        private float _glitchLevel;
        // 一部の字形だけに残す鈍い赤の残像。通常の全文複製にはしない。
        private TMP_Text? _textEcho;
        // 化けの組み合わせが変わる刻み。**-1 = まだ一度も掛けていない**。
        private int _corruptTick = -1;
        // プレビュー（`menu comms-preview -Set decay=`）が注入する進み。**負なら実機の値を読む**。
        private float _decayOverride = -1f;
        private float _previewTimeSec;
        // ⚠ 地の色は**シェーダによってプロパティ名が違う**（URP は `_BaseColor` / 組み込みは `_Color`）。
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        // 半透明へ倒すためのプロパティ（URP の Unlit）。⚠ 組み込みの `Unlit/Color` には 1 つも無い。
        private static readonly int SurfaceId = Shader.PropertyToID("_Surface");
        private static readonly int BlendId = Shader.PropertyToID("_Blend");
        private static readonly int SrcBlendId = Shader.PropertyToID("_SrcBlend");
        private static readonly int DstBlendId = Shader.PropertyToID("_DstBlend");
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
        private static readonly int SeizureId = Shader.PropertyToID("_Seizure");
        private static readonly int PanelXId = Shader.PropertyToID("_PanelX");
        // 枠を左端から右へ開くために、左端と全幅を覚えておく（地と縁で別々）。
        private float _panelW, _panelLeftX;
        private MeshRenderer? _avatarRenderer;
        private Material? _avatarMat;
        private int _charCount;
        private Material? _panelMat;
        private Material? _dividerMat;
        private Mesh? _panelMesh;
        private TMP_Text? _text;
        private MeshRenderer? _textRenderer, _echoRenderer;
        private TMP_Text? _hint;
        private Vector3[][]? _textBaseVertices, _echoBaseVertices;
        private Color32[][]? _textBaseColors, _echoBaseColors;
        private bool[] _missingGlyphs = System.Array.Empty<bool>();
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
        private bool _runRestartHooked;
        // 直前のフレームで何文字出ていたか。**打鍵音はこの増分から鳴らす**（下の Apply）。
        private int _lastShown;
        // ⚠⚠ いまの連絡は**すっと浮かぶ出方**か（`canon/LEDGER.md` 0168）。
        //    true のあいだ打鍵を 1 発も鳴らさない。あの出方は 1 フレームで全文が出るので、
        //    増分で鳴らす Apply は**放っておくと 1 発だけ鳴る**（画では気づけない）。
        private bool _silent;
        // その字が絵を持つか（改行だけ false）。⚠ **全文が出ている一瞬にしか測れない** → SetNotice。
        private bool[]? _charVisible;
        private bool _takeoverActive;
        private bool _takeoverModifiedThisRun;
        private bool _takeoverSoundCut;

        /// <summary>実体を組めたか。<b>false なら一生出ない</b>（テレメトリが読む）。</summary>
        public bool IsBuilt => _text != null;

        /// <summary>
        /// 直近にシェーダへ書いた壊れの強さ（<b>画に出た側</b>の観測）。
        /// 発作の刻みで跳ねるので、**サンプルによっては 0 に近い値が出る**のが正常。
        /// </summary>
        public float GlitchLevel => _glitchLevel;

        /// <summary>
        /// いま化けている字の数（<b>画に出た側</b>の観測）。
        /// ⚠ 刻みごとに組み合わせが変わるので、標本によって 0 が出るのは正常。
        /// <b>本編を通して 1 度も 0 を超えないなら壊れていない。</b>
        /// </summary>
        public int CorruptedChars { get; private set; }

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
        /// 直近に書いた侵食の進み（0 = スイ / 1 = 完全に市松人形）。
        /// <see cref="InvasionProgress"/> と同じ値なので、食い違ったら配線が壊れている。
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
        }

        /// <summary><see cref="SetDecayForPreview"/> の意味を明示した新しい名前。</summary>
        public void SetInvasionForPreview(float progress01, float timeSec)
            => SetDecayForPreview(progress01, timeSec);

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

        public CommsTakeoverPhase TakeoverPhase { get; private set; }
        public float AppliedTakeoverErase { get; private set; }
        public float AppliedTakeoverReveal { get; private set; }
        public float AppliedTakeoverStrain { get; private set; }
        public float AppliedTakeoverCollapse { get; private set; }
        public bool TakeoverResistance { get; private set; }
        public int TakeoverDeformedChars { get; private set; }
        public float AppliedPanelAlpha { get; private set; }
        public int TakeoverCutCount { get; private set; }
        public int TakeoverStartedCount { get; private set; }
        public int TakeoverCompletedCount { get; private set; }
        public int TakeoverTintedChars { get; private set; }

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

        private bool _onboardingActive;
        private CommsNotice _onboardingNotice = CommsNotice.None;

        private void Awake()
        {
            ResolveRefs();
            Build();
            Apply(CommsWeights.Hidden);
        }

        private void OnDisable()
        {
            _logic.Disable();
            _cue.ResetRun();
            _invasion.Reset();
            _glitchLevel = 0f;
            _corruptTick = -1;
            _takeoverModifiedThisRun = false;
            _onboardingActive = false;
            _onboardingNotice = CommsNotice.None;
            ResetTakeoverVisual();
            Apply(CommsWeights.Hidden);
            typeSfx?.StopAll();
        }

        private void OnDestroy()
        {
            OnDestroyHooks();
            if (_panelMat != null) Destroy(_panelMat);
            if (_dividerMat != null) Destroy(_dividerMat);
            if (_avatarMat != null) Destroy(_avatarMat);
            if (_panelMesh != null) Destroy(_panelMesh);
        }

        private void ResolveRefs()
        {
            if (runDirector == null) runDirector = FindObjectOfType<ShowRunDirector>();
            if (showControl == null) showControl = FindObjectOfType<ShowControlClient>();
            if (timeline == null) timeline = FindObjectOfType<TimelineDirector>();
            if (intro == null) intro = FindObjectOfType<IntroDirector>();
            if (walkGuide == null) walkGuide = FindObjectOfType<WalkGuide>();
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
            _onboardingActive = false;
            _onboardingNotice = CommsNotice.None;
            _cue.ResetRun();
            _invasion.Reset();
            _glitchLevel = 0f;
            _corruptTick = -1;
            _lastMarkCount = showControl != null ? showControl.VisitorMarkCount : 0;
            _logic.Disable();
            _silent = false;
            ResetTakeoverVisual();
            _takeoverModifiedThisRun = false;
            Apply(CommsWeights.Hidden);
            // 前の体験者の打鍵を次のランへ持ち越さない（`ShowSoundDirector.ResetRun` と同じ流儀）。
            typeSfx?.StopAll();
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
            // ⚠⚠ **床の矢印はこの連絡と対で出る**（`canon/LEDGER.md` 0079 の赤入れ 4
            //    「エージェントが説明し始めるときに、矢印が手前の線から 1 つづつ出てくるような感じに」）。
            //    ここで言わないと `WalkGuide` は保険（12 秒）が切れるまで 1 つも出さないので、
            //    **説明と矢印が別々の出来事になる**。
            // ⚠ 誘導そのものを出すか（幾何が解けたか・被っているか）は向こうが決める。
            //    ここは「説明を始めた」という事実だけを渡す。
            if (notice == CommsNotice.Walk) walkGuide?.NotifyExplaining();
            ResetTakeoverVisual();
            if (notice == CommsNotice.MarkLogged) _takeoverModifiedThisRun = false;
            // 「止まってください！」は、侵食した否定文を最後まで見せた後の次の発話。
            // ここから先へ崩壊中の頂点変形を持ち越さない。
            if (notice == CommsNotice.Halt) _takeoverModifiedThisRun = false;
            SetNotice(notice);
            // 打つ尺は文字数から決まる（文面を伸ばせば打つ時間も伸びる）。
            // 読ませる尺は**全文面で同じ 2 秒**（`canon/LEDGER.md` 0092）。
            // ⚠⚠ **③a だけは打たない**（`CommsCueLogic.DeliveryOf`・0168）。すっと浮かんで
            //    打鍵は 1 発も鳴らないので、**鳴るはずの数（`NoticeChars`）も 0 にする** —
            //    ここを字数のままにすると、解析器が「打鍵が字数の半分以下」と言い出す
            //    （`analyze-xp-log.py` は `ev=comms` の `chars` の合計と `typeN` を突き合わせる）。
            CommsDelivery delivery = CommsCueLogic.DeliveryOf(notice);
            _silent = delivery == CommsDelivery.Fade;
            if (_silent) NoticeChars = 0;
            if (delivery == CommsDelivery.Takeover)
            {
                _takeoverActive = true;
                // 打鍵の期待数も、完成できない原文のうち生成できる部分だけにする。
                NoticeChars = 0;
                for (int i = 0; i < CommsTakeoverLogic.MaxGeneratedChars(_charCount); i++)
                    if (IsVisibleChar(i)) NoticeChars++;
                TakeoverStartedCount++;
            }
            _logic.Begin(_charCount, delivery, persistent);
            LastNotice = notice;
            if (pulse) PulseCount++;
            if (!persistent) _cue.NotifyDelivered(notice);
            Debug.Log($"[Comms] AIエージェントからの連絡 {notice}「{TextFor(notice).Replace("\n", "／")}」"
                    + $"（{_charCount} 文字 / "
                    + (_silent ? $"すっと浮かぶ {_logic.TypeSec:0.00}s・打鍵なし"
                               : $"打つ {_logic.TypeSec:0.00}s") + "）");
        }

        private void Update()
        {
            if (!IsBuilt) return;

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
            if (!inRun && (_takeoverActive || _takeoverModifiedThisRun))
            {
                ResetTakeoverVisual();
                _takeoverModifiedThisRun = false;
                _logic.Disable();
            }
            if (!inRun) _takeoverModifiedThisRun = false;
            ObserveInvasion();
            bool introWaiting = inIntroPhase && intro != null && intro.Stage == IntroStage.Black;
            CommsNotice next = _cue.Tick(new CommsCueInput
            {
                inRun = inRun,
                inIntro = inIntroPhase,
                startAuthorized = showControl == null || showControl.StartAuthorized,
                introWaiting = introWaiting,
                panelDoneReading = _logic.DoneReading,
                // ③の時計は**締めのカットに入ってから**（0178）。報告待ちが立つのは待たない。
                closingSec = timeline != null ? timeline.ClosingTakeSec : -1f,
                markPressed = markPressed,
                markDetected = markPressed && showControl != null && showControl.LastMarkDetected,
                invasionProgress = InvasionProgress,
                takeoverPlaying = _takeoverActive,
                takeoverModified = _takeoverModifiedThisRun,
                takeoverAllowed = inRun,
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
            if (flat != null)
            {
                // 地は固定した寸法で置く。出入りは透明度だけを変える。
                _panelRenderer = MakeQuad(rootGo.transform, "CommsPanelQuad",
                                          _panelW, BodyMaxH + HintBandH, 0.012f,
                                          flat, RenderQueue, out _panelMat);
                // 顔と本文の間にだけ、短い縦罫線を置く。
                _dividerRenderer = MakeQuad(rootGo.transform, "CommsDivider",
                                            CommsFaceLayout.DividerW, CommsFaceLayout.DividerH, 0.006f,
                                            flat, AvatarQueue, out _dividerMat);
            }
            else
            {
                // ⚠ 黙って飛ばさない。2026-08-17 まで警告が 1 行も無かったので、
                //   実機で地が消えていることに走行の画を拡大するまで気づけなかった。
                Debug.LogWarning("[Comms] 地のシェーダを引けないので文字と壊れだけになります"
                                 + "（Universal Render Pipeline/Unlit も Unlit/Color も見つからない）");
            }

            BuildAvatar(rootGo.transform);

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

            _textEcho = MakeGlyphSurface(rootGo.transform, jp, "CommsTextEcho", GlyphQueue - 1, EchoRed);
            _text = MakeGlyphSurface(rootGo.transform, jp, "CommsText", GlyphQueue, Ivory);
            _echoRenderer = _textEcho.GetComponent<MeshRenderer>();
            _textRenderer = _text.GetComponent<MeshRenderer>();
            SetNotice(CommsNotice.None);   // 組み上げたら、まず畳んだ状態にする
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
            mat.SetFloat(PanelXId, CommsFaceLayout.CellCenterX(PanelW));
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
        private void SetNotice(CommsNotice notice)
        {
            TMP_Text? tmp = _text;
            if (tmp == null) return;
            TMP_Text? echo = _textEcho;
            string body = notice == CommsNotice.None ? LongestNoticeText : TextFor(notice);
            // 文面はこの原文のまま保つ。乱れは頂点だけに掛ける。
            _corruptTick = -1;

            tmp.maxVisibleCharacters = int.MaxValue;
            if (tmp.text != body) tmp.text = body;
            tmp.ForceMeshUpdate(ignoreActiveState: true, forceTextReparsing: true);
            if (echo != null)
            {
                echo.maxVisibleCharacters = int.MaxValue;
                if (echo.text != body) echo.text = body;
                echo.ForceMeshUpdate(ignoreActiveState: true, forceTextReparsing: true);
            }

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
            if (echo != null) echo.transform.localPosition = tmp.transform.localPosition;
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
            _missingGlyphs = new bool[_glyphCount];
            CaptureBaseMesh(tmp, out _textBaseVertices, out _textBaseColors);
            if (echo != null) CaptureBaseMesh(echo, out _echoBaseVertices, out _echoBaseColors);
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

        private static void CaptureBaseMesh(TMP_Text text, out Vector3[][] vertices,
                                            out Color32[][] colors)
        {
            TMP_MeshInfo[] meshInfo = text.textInfo.meshInfo;
            vertices = new Vector3[meshInfo.Length][];
            colors = new Color32[meshInfo.Length][];
            for (int i = 0; i < meshInfo.Length; i++)
            {
                vertices[i] = (Vector3[])meshInfo[i].vertices.Clone();
                colors[i] = (Color32[])meshInfo[i].colors32.Clone();
            }
        }

        /// <summary>
        /// 本文の原文と等幅配置を保ったまま、字形の頂点だけを更新する。
        /// </summary>
        private int ApplyGlyphMesh(TMP_Text text, Vector3[][]? baseVertices, Color32[][]? baseColors,
                                   float reveal, float globalAlpha, bool echo)
        {
            if (baseVertices == null || baseColors == null) return 0;
            TMP_TextInfo info = text.textInfo;
            if (info.meshInfo.Length != baseVertices.Length) return 0;

            for (int i = 0; i < info.meshInfo.Length; i++)
            {
                System.Array.Copy(baseVertices[i], info.meshInfo[i].vertices, baseVertices[i].Length);
                System.Array.Copy(baseColors[i], info.meshInfo[i].colors32, baseColors[i].Length);
            }

            int changedVisible = 0;
            int tintedVisible = 0;
            int glyphOrdinal = 0;
            bool taking = _takeoverActive || _takeoverModifiedThisRun;
            bool closingNotice = LastNotice == CommsNotice.Halt || LastNotice == CommsNotice.Prompt;
            float meshGlitch = taking || closingNotice ? 0f : _glitchLevel;
            float exact = Mathf.Clamp01(reveal) * _charCount;
            float charSec = _charCount > 0 ? _logic.TypeSec / _charCount : 0f;
            for (int i = 0; i < info.characterCount; i++)
            {
                TMP_CharacterInfo character = info.characterInfo[i];
                if (!character.isVisible) continue;

                float ageSec = Mathf.Max(0f, exact - i) * charSec;
                float fade = _silent ? 1f : GlyphFadeAlpha(ageSec);
                bool appeared = exact > i;
                bool tintDenial = taking && !echo && LastNotice == CommsNotice.Takeover
                    && i < TakeoverDenialPrefixLength(ShowLanguage.Current);
                bool missing = glyphOrdinal < _missingGlyphs.Length && _missingGlyphs[glyphOrdinal];
                bool echoOn = echo && CommsGlitchLogic.EchoAt(meshGlitch, _corruptTick, glyphOrdinal);
                float alpha = appeared ? fade * globalAlpha : 0f;
                // 画面の横位置ではなく原文の字順で追う。複数行でも後の行を先に消さない。
                float takeoverDeform = taking && !echo && appeared
                    ? Smooth01(AppliedTakeoverErase * _charCount - i) : 0f;
                if (echo ? !echoOn : missing)
                {
                    if (!echo && appeared && alpha > 0.004f) changedVisible++;
                    alpha = 0f;
                }
                else if (takeoverDeform > 0.001f && alpha > 0.004f)
                    changedVisible++;

                int material = character.materialReferenceIndex;
                int vertex = character.vertexIndex;
                Vector3[] vertices = info.meshInfo[material].vertices;
                Color32[] colors = info.meshInfo[material].colors32;
                float dx = CommsGlitchLogic.LineOffsetM(meshGlitch, _corruptTick,
                                                        character.lineNumber) / TextScale;
                if (echo)
                    dx += CommsGlitchLogic.EchoOffsetM(meshGlitch, _corruptTick, glyphOrdinal)
                          / TextScale;
                float centerX = (character.bottomLeft.x + character.topRight.x) * 0.5f;
                float glyphH = Mathf.Max(0.0001f, character.topRight.y - character.bottomLeft.y);
                byte a = (byte)Mathf.RoundToInt(Mathf.Clamp01(alpha) * 255f);
                for (int k = 0; k < 4; k++)
                {
                    vertices[vertex + k].x += dx;
                    if (takeoverDeform > 0f)
                    {
                        float bottomK = Mathf.Clamp01((character.topRight.y - vertices[vertex + k].y)
                                                       / glyphH);
                        float narrow = Mathf.Lerp(1f, TakeoverThinK,
                            takeoverDeform * bottomK);
                        vertices[vertex + k].x = centerX
                            + (vertices[vertex + k].x - centerX) * narrow;
                        vertices[vertex + k].y -= TakeoverStretchM / TextScale
                            * AppliedTakeoverStrain * takeoverDeform * bottomK;
                    }
                    Color32 c = colors[vertex + k];
                    if (tintDenial)
                    {
                        c.r = (byte)Mathf.RoundToInt(DenialRed.r * 255f);
                        c.g = (byte)Mathf.RoundToInt(DenialRed.g * 255f);
                        c.b = (byte)Mathf.RoundToInt(DenialRed.b * 255f);
                    }
                    c.a = (byte)(c.a * a / 255);
                    colors[vertex + k] = c;
                }
                if (tintDenial && appeared && alpha > 0.004f) tintedVisible++;
                glyphOrdinal++;
            }

            text.UpdateVertexData(TMP_VertexDataUpdateFlags.Vertices | TMP_VertexDataUpdateFlags.Colors32);
            if (!echo) TakeoverTintedChars = tintedVisible;
            return changedVisible;
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
            mat = new Material(shader) { name = name + " (runtime)" };
            MakeTranslucent(mat);
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
            bool taking = _takeoverActive || _takeoverModifiedThisRun;
            CommsTakeoverSample takeover = taking && _logic.Active
                && _logic.Delivery == CommsDelivery.Takeover
                ? _logic.TakeoverSample : new CommsTakeoverSample
                { phase = CommsTakeoverPhase.Off, erase = taking ? 1f : 0f,
                  strain = taking ? 1f : 0f, collapse = taking ? 1f : 0f,
                  soundCut = taking };
            TakeoverPhase = takeover.phase;
            AppliedTakeoverErase = Mathf.Clamp01(takeover.erase);
            AppliedTakeoverReveal = Mathf.Clamp01(takeover.reveal);
            AppliedTakeoverStrain = Mathf.Clamp01(takeover.strain);
            AppliedTakeoverCollapse = Mathf.Clamp01(takeover.collapse);
            TakeoverResistance = takeover.resistance;
            if (_takeoverActive && takeover.soundCut && !_takeoverSoundCut)
            {
                // 予約済みの最後の打鍵も同じ時刻で切る。止めた音を後の段へ持ち越さない。
                typeSfx?.StopAll();
                _takeoverSoundCut = true;
                _takeoverModifiedThisRun = true;
                TakeoverCutCount++;
            }
            if (_takeoverActive && takeover.phase == CommsTakeoverPhase.Complete)
            {
                _takeoverActive = false;
                _takeoverModifiedThisRun = true;
                TakeoverCompletedCount++;
            }

            float takeoverInk = taking
                ? 1f - Smooth01((AppliedTakeoverCollapse - 0.35f) / 0.65f)
                : 1f;
            if (taking && takeover.soundCut) takeoverInk = 0f;
            AppliedGlyph = Mathf.Clamp01(w.glyph) * takeoverInk;
            AppliedOpen = Mathf.Clamp01(w.open);
            float reveal = taking ? Mathf.Min(w.reveal,
                Mathf.Max(0f, CommsTakeoverLogic.MaxGeneratedChars(_charCount) - 0.001f)
                / Mathf.Max(1, _charCount)) : w.reveal;
            if (_text != null)
            {
                // 出現時刻は従来どおり。見え始めた後の 40ms だけ頂点 alpha を滑らかに立てる。
                int shown = _charCount <= 0 ? 0
                          : Mathf.Clamp(Mathf.CeilToInt(Mathf.Clamp01(reveal) * _charCount), 0, _charCount);
                bool on = AppliedGlyph > 0.002f && shown > 0;
                if (_textRenderer != null) _textRenderer.enabled = on;
                if (shown > _lastShown && _root != null && !_silent && !takeover.soundCut)
                {
                    // 低い描画頻度でも、同じコマに増えた字を間引かない。絵を持つ1字につき1打。
                    for (int i = _lastShown; i < shown; i++)
                        if (IsVisibleChar(i)) typeSfx?.Play(_root.position);
                }
                _lastShown = shown;
                VisibleChars = shown;

                bool closingNotice = LastNotice == CommsNotice.Halt || LastNotice == CommsNotice.Prompt;
                float ordinaryGlitch = taking || closingNotice ? 0f : _glitchLevel;
                CommsGlitchLogic.FillMissing(_missingGlyphs, _glyphCount, ordinaryGlitch, _corruptTick);
                CorruptedChars = ApplyGlyphMesh(_text, _textBaseVertices, _textBaseColors,
                                                 reveal, AppliedGlyph, echo: false);
                TakeoverDeformedChars = taking ? CorruptedChars : 0;
                if (_textEcho != null)
                {
                    bool echoOn = on && ordinaryGlitch > CommsGlitchLogic.OffThreshold;
                    if (_echoRenderer != null) _echoRenderer.enabled = echoOn;
                    ApplyGlyphMesh(_textEcho, _echoBaseVertices, _echoBaseColors,
                                   reveal, AppliedGlyph * 0.58f, echo: true);
                }
            }
            ApplyHint(taking ? 0f : Mathf.Clamp01(w.hint));
            float pa = Mathf.Clamp01(w.panel) * Smooth01(AppliedOpen);
            float panelPa = pa * (taking ? 1f - AppliedTakeoverCollapse : 1f);
            float contentPa = pa * takeoverInk;
            AppliedPanelAlpha = PanelAlpha * panelPa;
            AppliedBody = Mathf.Clamp01(w.body);

            // ⚠⚠ **枠は「出ている帯」だけを覆う**（2026-08-16・`canon/LEDGER.md` 0065）。
            //    下段から指示を剥がしたので、**押していないときは下段に 1 文字も無い**。
            //    それまでの「下端を固定して丈だけ伸びる」ままだと、①の連絡が
            //    **下半分が空の大きな箱**として出る（プレビューの絵で見つけた）。
            //    ⚠ 下段の有無は不透明度だけでは決まらない — 文字が空でも hint は 1 になる。
            float hintK = taking ? 0f : Mathf.Clamp01(w.hint) * (_hintBody.Length > 0 ? 1f : 0f);
            // 上段の帯 = [0, _bodyBandH]（**文面の実寸**）/ 下段の帯 = [-HintBandH, 0]。
            float top = HintBandTopY + _bodyBandH * AppliedBody;
            float bottom = HintBandTopY - HintBandH * hintK;
            float cy = (top + bottom) * 0.5f;
            float h = Mathf.Max(0f, top - bottom);
            if (h > 0.0005f) h = Mathf.Max(h, CommsFaceLayout.MinBoxH);
            bool lit = pa > 0.002f && h > 0.0005f;

            if (_panelRenderer != null && _panelMat != null)
            {
                SetFlatColor(_panelMat, new Color(PanelColor.r, PanelColor.g, PanelColor.b,
                                                  AppliedPanelAlpha));
                _panelRenderer.enabled = lit;
                SetFixedPanel(_panelRenderer.transform, _panelLeftX, _panelW, cy, h);
            }
            if (_dividerRenderer != null && _dividerMat != null)
            {
                SetFlatColor(_dividerMat, new Color(Ivory.r, Ivory.g, Ivory.b,
                    0.68f * contentPa));
                _dividerRenderer.enabled = lit;
                float dividerH = CommsFaceLayout.DividerH * Smooth01(AppliedOpen);
                float dividerStretch = TakeoverStretchM * AppliedTakeoverStrain;
                _dividerRenderer.transform.localPosition = new Vector3(
                    CommsFaceLayout.DividerCenterX(PanelW), cy - dividerStretch * 0.5f, 0.006f);
                _dividerRenderer.transform.localScale = new Vector3(
                    CommsFaceLayout.DividerW
                    * Mathf.Lerp(1f, TakeoverThinK, AppliedTakeoverStrain),
                    dividerH + dividerStretch, 1f);
            }
            ApplyAvatar(contentPa, cy, lit);

        }

        private void ResetTakeoverVisual()
        {
            _takeoverActive = false;
            _takeoverSoundCut = false;
            TakeoverPhase = CommsTakeoverPhase.Off;
            AppliedTakeoverErase = 0f;
            AppliedTakeoverReveal = 0f;
            AppliedTakeoverStrain = 0f;
            AppliedTakeoverCollapse = 0f;
            TakeoverResistance = false;
            TakeoverDeformedChars = 0;
            TakeoverTintedChars = 0;
            AppliedPanelAlpha = 0f;
            _silent = false;
        }

        private void ApplyAvatar(float alpha, float centerY, bool lit)
        {
            if (_avatarRenderer == null || _avatarMat == null) return;
            AppliedFace = lit
                ? Mathf.Clamp01(alpha) * CommsFaceLayout.Reveal(AppliedOpen, PanelW)
                : 0f;
            bool on = AppliedFace > 0.004f;

            // 顔と印字は同じ通信侵食度を読む。別の曲線にすると、文字と顔が別々の速さで
            // 壊れて見えるため、0 / 0.25 / 0.75 / 1 をそのまま両方へ渡す。
            AppliedFaceMix = _glitchLevel;

            float x = CommsFaceLayout.CellCenterX(PanelW);
            _avatarMat.SetFloat(OpacityId, AppliedFace);
            _avatarMat.SetFloat(FaceMixId, AppliedFaceMix);
            _avatarMat.SetFloat(SeizureId, AppliedTakeoverStrain);
            _avatarRenderer.enabled = on;
            _avatarRenderer.transform.localPosition = new Vector3(x, centerY, CommsFaceLayout.DepthM);
            _avatarRenderer.transform.localScale = Vector3.one * CommsFaceLayout.CellM;
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
            return CommsGlitchLogic.LevelFor(InvasionProgress);
        }

        private void TickGlitch(float timeSec)
        {
            _glitchLevel = ResolveCorruption();
            _corruptTick = CommsGlitchLogic.TickAt(timeSec);
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
