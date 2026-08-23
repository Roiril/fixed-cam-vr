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
    ///
    /// <b>顔</b>（<c>canon/LEDGER.md</c> 0071・2026-08-17）: 面の<b>左に角丸の枠</b>が立ち、
    /// その中に AIエージェントの顔（paperdoll の「スイ」）が出る。寸法は
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
        /// <summary>縁の張り出し (m)。地より一回り大きい面を裏に置いて枠に見せる。</summary>
        private const float BezelM = 0.012f;

        /// <summary>
        /// 地の不透明度（2026-08-19・<c>canon/LEDGER.md</c> 0096・ユーザー指定
        /// 「スクリーンの背景 → 黒い半透明に」）。<b>後ろの映像が透ける</b>。
        /// ⚠ 縁が裏に敷いてあるので、中央の実効は 1-(1-<see cref="BezelAlpha"/>)(1-これ) ＝ 約 0.69。
        /// </summary>
        private const float PanelAlpha = 0.50f;

        /// <summary>縁の不透明度。<b>地より薄い</b> — 濃くすると中央だけ透けなくなる。</summary>
        private const float BezelAlpha = 0.38f;

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
        private static string TextFor(CommsNotice n) => n switch
        {
            // ⓪a 名乗り。⚠ **「AI」とは書かない。「エージェント」と書く**（`canon/LEDGER.md` 0080）。
            //    ⚠ 紙の依頼書と同じ語（`docs/onsite/handout.html`「調査を支援するエージェント」）。
            //      片方だけ直すと、紙と装置が別のものを指しているように読める。
            //    ⚠⚠ **14 文字ちょうど**（帯に入るのは 14.8 文字）。1 文字でも足すと 2 行へ折り返す。
            CommsNotice.Greeting => "私は調査支援エージェントです",
            // ⓪b 歩行の指示。**この連絡が床の矢印を出す**（`WalkGuide.NotifyExplaining`）。
            //    ⚠ 1 文目が 15 文字なので**2 行へ割ってある**（ユーザーの改行は文のあいだの 1 つだけ）。
            CommsNotice.Walk => "開始ポイントを\nマークしました。\n矢印から向かってください。",
            // ⓪c 演出の始まりの告知。**段 0 を抜けた縁**（＝ 導入演出が始まるのと同じフレーム）。
            //    ⓪b が出ていれば引かずに上書きする（`Deliver` は頭から出し直す）。
            CommsNotice.Arrived => "到着しました。\n観測装置を起動します。",
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
            CommsNotice.BeginHow => "異変を見つけたら\nボタンを長押ししてください\n装置が解析して対処を試みます",
            // ②a 報告が通った（解除が効いた）。
            CommsNotice.MarkLogged => "異変を排除しました",
            // ②b 報告が通らなかった（3 周目は AI が侵食されていて通らない — 0082）。
            CommsNotice.MarkNothing => "異常は検出されませんでした",
            // ③ 締めの催促。⚠ **これだけが体験者自身を名指しする**（0096）。
            //    読まれないと締めのカットが進まないので、いちばん強い言い方をしている。
            CommsNotice.Prompt => "異常があなたを\n取り込もうとしています。\n排除してください。",
            _ => "",
        };

        /// <summary>
        /// その連絡の文面（テストと <c>menu comms-preview</c> 用）。
        /// ⚠ <b>全部の文面を機械で測れるようにするために公開している。</b>
        /// <see cref="LongestNoticeText"/> だけを測っていた頃は、**行数の最悪が別の文面にある**と
        /// 誰も気づけなかった（0079 で①が 2 行・⓪b が 3 行になって顕在化した）。
        /// </summary>
        public static string NoticeText(CommsNotice n) => TextFor(n);

        /// <summary>
        /// 面を組むときに使う文面 ＝ <b>いちばん長い行を持つもの</b>（⓪a の 14 文字）。
        ///
        /// ⚠ ここを短い文面にすると <c>menu text-audit</c> が<b>最悪の行を測らない</b>ので
        /// 「枠に収まっている」と嘘をつく。実行時はどの文面でも <see cref="SetNotice"/> が組み直す。
        /// ⚠ <b>行数の最悪（4 行 ＝ ①）はここでは測れない。</b> 縦の座りは
        /// <c>menu comms-preview</c> の絵で見る。
        /// ⚠⚠ <b>2026-08-19 に② → ⓪aへ移した</b>（`canon/LEDGER.md` 0096）。名乗りが
        /// 「私は調査支援エージェントです」＝ 14 文字になり、帯に入る 14.8 文字にいちばん近い。
        /// <c>CommsNoticeTextTests.LongestNoticeText_ReallyHasTheLongestLine</c> が
        /// **本当に最長かを機械で確かめる**ので、文面を触った人はそこで落ちる。
        /// </summary>
        public static string LongestNoticeText => TextFor(CommsNotice.Greeting);

        private readonly CommsPanelLogic _logic = new CommsPanelLogic();
        private readonly CommsCueLogic _cue = new CommsCueLogic();
        private readonly YawFollowLogic _yawFollow = new YawFollowLogic();

        private Transform? _root;
        private MeshRenderer? _panelRenderer;
        private MeshRenderer? _bezelRenderer;
        /// <summary>表示側のバグ（分離）の層。<b>本体より先に描く</b>（下に敷く）。⚠ 5000 以下。</summary>
        private const int GhostQueueR = 4988;
        private const int GhostQueueC = 4989;

        // 顔の枠と切り抜き。地（4980）の後・文字の分離（4988）の前。
        // ⚠ 分離の複製は本体より先（下に敷く）。文字の層と同じ流儀。
        private const int AvatarGhostQueueR = 4983;
        private const int AvatarGhostQueueC = 4984;
        private const int AvatarQueue = 4985;

        // ⚠ 赤とシアン。**装置の意匠（暖色）ではなく、表示が壊れたときの色**（`canon/LEDGER.md` 0070）。
        private static readonly Color GhostRed = new Color(1.00f, 0.12f, 0.18f);
        private static readonly Color GhostCyan = new Color(0.10f, 0.88f, 0.95f);

        // AI の侵食（`canon/LEDGER.md` 0068 / 0069 / **0070**）。
        // ⚠⚠ 0070 から**単調ではない** — 3 周目で 1.0 に着き、帰りの A で回復する。
        private float _glitchLevel, _glitchOffsetX;
        // 帰りの区間に入ってからの経過（回復の進み）。
        private float _returnSec;
        // 表示側のバグ（赤・シアンの分離）。本体と同じ文面・同じ可視数を書く。
        private TMP_Text? _textR, _textC;
        // 地と文字に掛ける明るさ（1 = 平常。沈むだけで明るくはならない）。
        private float _glitchFlicker = 1f;
        // 化けの組み合わせが変わる刻み。**-1 = まだ一度も掛けていない**。
        private int _corruptTick = -1;
        // 壊す前の素の文面。**打鍵の数えも枠の高さもこちらが正**（化けても幅は変わらない）。
        private string _noticeSource = "";
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
        // 枠を左端から右へ開くために、左端と全幅を覚えておく（地と縁で別々）。
        private float _panelW, _panelLeftX, _bezelW, _bezelLeftX;
        // 顔の枠（本体）と、表示側のバグの複製 2 枚。⚠ 版が無くても枠だけは出す。
        private MeshRenderer? _avatarRenderer, _avatarGhostR, _avatarGhostC;
        private Material? _avatarMat, _avatarGhostMatR, _avatarGhostMatC;
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
        public bool PanelBuilt => _panelMat != null && _bezelMat != null;

        /// <summary>
        /// 顔の枠を組めたか（<c>canon/LEDGER.md</c> 0071）。
        /// <b>false なら枠も顔も 1 画素も出ない</b> — シェーダがビルドから剥がれた側の症状で、
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
        /// <b>周回の壊れ（<see cref="GlitchLevel"/>）と同じ値</b>なので、食い違ったら配線が壊れている。
        /// </summary>
        public float AppliedFaceMix { get; private set; }

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
            if (_avatarMat != null) Destroy(_avatarMat);
            if (_avatarGhostMatR != null) Destroy(_avatarGhostMatR);
            if (_avatarGhostMatC != null) Destroy(_avatarGhostMatC);
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
            // ⚠⚠ **床の矢印はこの連絡と対で出る**（`canon/LEDGER.md` 0079 の赤入れ 4
            //    「エージェントが説明し始めるときに、矢印が手前の線から 1 つづつ出てくるような感じに」）。
            //    ここで言わないと `WalkGuide` は保険（12 秒）が切れるまで 1 つも出さないので、
            //    **説明と矢印が別々の出来事になる**。
            // ⚠ 誘導そのものを出すか（幾何が解けたか・被っているか）は向こうが決める。
            //    ここは「説明を始めた」という事実だけを渡す。
            if (notice == CommsNotice.Walk) walkGuide?.NotifyExplaining();
            SetNotice(notice);
            // 打つ尺は文字数から決まる（文面を伸ばせば打つ時間も伸びる）。
            // 読ませる尺は**全文面で同じ 2 秒**（`canon/LEDGER.md` 0092）。
            _logic.Begin(_charCount);
            LastNotice = notice;
            PulseCount++;
            Debug.Log($"[Comms] AIエージェントからの連絡 {notice}「{TextFor(notice).Replace("\n", "／")}」"
                    + $"（{_charCount} 文字 / 打つ {_logic.TypeSec:0.00}s）");
        }

        private void Update()
        {
            if (!IsBuilt) return;

            // ---- 報告の縁を取る。⚠ **解除が通ったかは `ShowControlClient` が中継の戻り値で持っている**。
            //      ここで `timeline.ActiveTakeId` を見ると「演出が走っていたか」しか分からず、
            //      3 周目の入れ替わり（消えない）にも「記録されました」と返してしまう。
            bool markPressed = false, markResolved = false;
            if (showControl != null)
            {
                if (showControl.VisitorMarkCount != _lastMarkCount)
                {
                    // 押し戻し（ラン開始で 0 に戻る）は報告ではない。
                    markPressed = showControl.VisitorMarkCount > _lastMarkCount;
                    markResolved = showControl.LastMarkResolved;
                    _lastMarkCount = showControl.VisitorMarkCount;
                }
            }

            // ⚠ **導入でも連絡を出す**（2026-08-17・`canon/LEDGER.md` 0079）。出すのは段 0
            //   （開始待ち）のあいだだけで、タイトルが画面を持っているうちは 1 文字も出さない。
            bool inIntroPhase = runDirector != null && runDirector.Phase == ShowPhase.Intro;
            bool introWaiting = inIntroPhase && intro != null && intro.Stage == IntroStage.Black;
            CommsNotice next = _cue.Tick(new CommsCueInput
            {
                inRun = runDirector != null && runDirector.Phase == ShowPhase.Run,
                inIntro = inIntroPhase,
                startAuthorized = showControl == null || showControl.StartAuthorized,
                introWaiting = introWaiting,
                panelDoneReading = _logic.DoneReading,
                waitingForMark = timeline != null && timeline.IsWaitingForVisitorMark,
                markPressed = markPressed,
                markResolved = markResolved,
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
            bool inRun = runDirector != null && runDirector.Phase == ShowPhase.Run;
            _logic.SetGuideWanted(inRun && _leftConnected
                                  && (_markProgress > 0f || _markConfirming));

            // ⚠ **壊れは面が出ていなくても進める。** 出た瞬間から正しい強さで出るようにするため
            //    （届いた所で 0 から立ち上がると「連絡が来ると壊れる」に見える）。
            TickReturnClock(Time.unscaledDeltaTime);
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
            // ⚠ 飛ぶ幅も面と同じだけ縮める（`Scale`）。ここだけ実寸のままにすると、
            //   面が小さくなったぶん**飛びだけが大きく**見える。
            _root.position = basePos + _root.right * (_glitchOffsetX * Scale);
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
                // 縁（裏の一回り大きい面）。⚠ **地だけだと真っ黒の中で面が消える**
                //    （2026-08-15 の実機の画で、文字だけが宙に浮いていた）。
                // ⚠ ここで渡す高さは**組んだ瞬間の見かけだけ**（`Apply` の `SetFrame` が
                //   毎フレーム中心と高さを置き直す）。
                _bezelW = _panelW + BezelM * 2f;
                _bezelLeftX = _panelLeftX - BezelM;
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

            // ⚠⚠ **表示側のバグ（赤とシアンへ分離してずれる）は複製 3 枚で作る**
            //    （`canon/LEDGER.md` 0070・参考画像 2 枚目）。TMP の頂点を触る手もあるが、
            //    `maxVisibleCharacters` が打鍵中ずっと変わってメッシュが組み直されるので、
            //    **毎フレーム頂点へ書き戻す**必要がある。複製なら文面と可視数を写すだけで済む。
            //    ⚠ 複製は本体より**先に**描く（queue が小さい ＝ 下に敷く）。
            _textR = MakeGlyphSurface(rootGo.transform, jp, "CommsTextR", GhostQueueR, GhostRed);
            _textC = MakeGlyphSurface(rootGo.transform, jp, "CommsTextC", GhostQueueC, GhostCyan);
            _text = MakeGlyphSurface(rootGo.transform, jp, "CommsText", GlyphQueue, HmdTextStyle.Ink);
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

            // ⚠⚠ 表示側のバグ（赤とシアンの分離）は**顔だけ**が浴びる（`_Stroke = 0`）。
            //    枠は装置の意匠なので分離させない — 分離させると「枠が二重にずれた」に見えて、
            //    壊れているのが AI ではなく面そのものだ、という別の話になる。
            _avatarGhostR = MakeAvatar(parent, "CommsAvatarGhostR", AvatarGhostQueueR,
                                       GhostRed, art, doll, avatar, stroke: 0f, out _avatarGhostMatR);
            _avatarGhostC = MakeAvatar(parent, "CommsAvatarGhostC", AvatarGhostQueueC,
                                       GhostCyan, art, doll, avatar, stroke: 0f, out _avatarGhostMatC);
            _avatarRenderer = MakeAvatar(parent, "CommsAvatar", AvatarQueue,
                                         HmdTextStyle.Ink, art, doll, avatar,
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
        private void SetNotice(CommsNotice notice)
        {
            TMP_Text? tmp = _text;
            if (tmp == null) return;
            string body = notice == CommsNotice.None ? LongestNoticeText : TextFor(notice);
            // ⚠ 壊す前の姿を覚える。**重心も枠の高さも打鍵の数えも、こちらで測る**
            //   （`canon/LEDGER.md` 0069）。化けた文面で測ると、全角の空白が混ざった分だけ
            //   `textBounds` が縮んで、刻みのたびに文面が上下に跳ねる。
            _noticeSource = body;
            _corruptTick = -1;

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
            // ⚠ 明滅は**地と文字の両方**に掛ける（`canon/LEDGER.md` 0069）。
            //   片方だけだと「文字が消えかけている」ではなく「面の色が変わった」に見える。
            //   沈むだけで明るくはならない（電源が落ちかけている装置）。
            AppliedGlyph = Mathf.Clamp01(w.glyph) * _glitchFlicker;
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
            // 表示側のバグ（赤とシアンの分離）を本体へ揃える（`canon/LEDGER.md` 0070）。
            SyncGhosts();
            ApplyHint(Mathf.Clamp01(w.hint));
            float pa = Mathf.Clamp01(w.panel) * _glitchFlicker;
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
            float cy = (top + bottom) * 0.5f;
            // ⚠⚠ **顔の枠が縦にはみ出さない丈を必ず確保する**（2026-08-17・`canon/LEDGER.md` 0071）。
            //    伸ばすのは中心の周りへ対称に ＝ **文面も下段も 1mm も動かない**。
            //    0065 の「出ている帯だけを覆う」に反しない — あれが禁じたのは中身の無い空の箱で、
            //    いまは左に顔が居るので空ではない。いちばん高い姿（2 行 ＋ 下段）は下限を超える。
            float h = Mathf.Max(0f, top - bottom);
            if (h > 0.0005f) h = Mathf.Max(h, CommsFaceLayout.MinBoxH);
            bool lit = pa > 0.01f && AppliedOpen > 0.001f && h > 0.0005f;

            // 地は**黒い半透明**（2026-08-19・`canon/LEDGER.md` 0096）。
            // ⚠⚠ **薄めるのは alpha であって rgb ではない。** 2026-08-19 まで地は不透明で、
            //    出入りを rgb の掛け算（黒へ寄せる）で作っていた。半透明にしたので、
            //    そこを rgb のままにすると「消えていく」ではなく「黒くなっていく」に見える。
            if (_panelRenderer != null && _panelMat != null)
            {
                SetFlatColor(_panelMat, new Color(0.020f, 0.017f, 0.015f, PanelAlpha * pa));
                _panelRenderer.enabled = lit;
                SetFrame(_panelRenderer.transform, _panelLeftX, _panelW, AppliedOpen, cy, h);
            }
            if (_bezelRenderer != null && _bezelMat != null)
            {
                // 縁は地より明るい。ここだけが「面がある」ことを伝える。
                // ⚠ 縁は地の**裏に敷いた一回り大きい面**なので、中央では 2 枚が重なる
                //   （合成は 1-(1-地)(1-縁)）。縁を濃くすると中央だけ透けなくなる。
                SetFlatColor(_bezelMat, new Color(0.150f, 0.110f, 0.085f, BezelAlpha * pa));
                _bezelRenderer.enabled = lit;
                SetFrame(_bezelRenderer.transform, _bezelLeftX, _bezelW, AppliedOpen, cy,
                         h + BezelM * 2f);
            }
            ApplyAvatar(pa, cy, lit);
        }

        /// <summary>
        /// 顔の枠と顔を書く（<c>canon/LEDGER.md</c> 0071）。
        ///
        /// ⚠ <b>明滅は地・文字とまとめて浴びる</b>（<paramref name="alpha"/> に既に掛かっている）。
        /// 顔だけ平常のまま残ると、装置が沈むのに AI だけ無事に見える。
        /// ⚠ <b>横飛びは面ごと</b>（<c>LateUpdate</c> が root を動かす）ので、ここでは書かない。
        /// </summary>
        private void ApplyAvatar(float alpha, float centerY, bool lit)
        {
            if (_avatarRenderer == null || _avatarMat == null) return;
            // ⚠ **出していないときは 0 と言う。** 重みだけ立てて描いていない状態を
            //   「出た」と観測すると、実機で消えていても計器が緑になる。
            AppliedFace = lit
                ? Mathf.Clamp01(alpha) * CommsFaceLayout.Reveal(AppliedOpen, PanelW)
                : 0f;
            bool on = AppliedFace > 0.004f;

            // ⚠⚠ **侵食は周回の壊れとまったく同じ値を読む**（`canon/LEDGER.md` 0073）。
            //    独自の曲線を持たせない — 2 つ持つと、片方だけ直したときに
            //    「文字は原型を保てないのに顔はスイのまま」が黙って起きる。
            //    あの値は 3 周目 A で 1.0（＝ 完全に人形）に着き、**帰りの A で戻る**
            //    （`CommsGlitchLogic.CorruptionFor`）ので、AI の復帰がそのまま顔にも出る。
            AppliedFaceMix = _glitchLevel;

            float x = CommsFaceLayout.CellCenterX(PanelW);
            _avatarMat.SetFloat(OpacityId, AppliedFace);
            _avatarMat.SetFloat(FaceMixId, AppliedFaceMix);
            _avatarRenderer.enabled = on;
            _avatarRenderer.transform.localPosition = new Vector3(x, centerY, CommsFaceLayout.DepthM);

            // 表示側のバグ（赤とシアンの分離）。⚠ **文字と同じ振れ幅・同じ刻み**を読む
            //   （別々に持つと、面の中で分離の向きが場所によって違うことになる）。
            float dx = CommsGlitchLogic.SplitOffsetM(_glitchLevel, _corruptTick);
            bool ghost = on && dx > 0.0001f;
            ApplyAvatarGhost(_avatarGhostR, _avatarGhostMatR, x - dx, centerY, ghost);
            ApplyAvatarGhost(_avatarGhostC, _avatarGhostMatC, x + dx, centerY, ghost);
        }

        private void ApplyAvatarGhost(MeshRenderer? r, Material? m, float x, float y, bool on)
        {
            if (r == null || m == null) return;
            r.enabled = on;
            if (!on) return;
            m.SetFloat(OpacityId, AppliedFace);
            m.SetFloat(FaceMixId, AppliedFaceMix);
            r.transform.localPosition = new Vector3(x, y, CommsFaceLayout.DepthM);
        }

        /// <summary>
        /// 表示側のバグ（赤とシアンの分離）を本体へ揃える（<c>canon/LEDGER.md</c> 0070）。
        /// ⚠ <b>侵食が 0 のあいだは複製ごと消す</b>（描画も走らない）。
        /// </summary>
        private void SyncGhosts()
        {
            if (_text == null || _textR == null || _textC == null) return;
            float dx = CommsGlitchLogic.SplitOffsetM(_glitchLevel, _corruptTick);
            bool on = dx > 0.0001f && AppliedGlyph > 0.002f && VisibleChars > 0;
            ApplyGhost(_textR, -dx, on);
            ApplyGhost(_textC, dx, on);
        }

        /// <summary>
        /// 分離の 1 層を本体へ揃える。
        /// ⚠⚠ <b>文面・可視数・置き場所は本体から写す。</b> 別々に持つと、化けの組み合わせが
        /// 1 刻みずれた瞬間に「違う字が 3 つ並ぶ」になり、分離ではなく**別の文が重なって**見える。
        /// </summary>
        private void ApplyGhost(TMP_Text ghost, float dx, bool on)
        {
            if (ghost.gameObject.activeSelf != on) ghost.gameObject.SetActive(on);
            if (!on || _text == null) return;
            if (!string.Equals(ghost.text, _text.text, System.StringComparison.Ordinal))
                ghost.text = _text.text;
            if (ghost.maxVisibleCharacters != VisibleChars) ghost.maxVisibleCharacters = VisibleChars;
            // 明滅も一緒に浴びる（本体だけ沈むと分離だけが残って「色が出た」に見える）。
            ghost.alpha = AppliedGlyph;
            Vector3 p = _text.transform.localPosition;
            ghost.transform.localPosition = new Vector3(p.x + dx, p.y, p.z);
        }

        /// <summary>
        /// 周回の壊れを 1 フレーム進める（<c>canon/LEDGER.md</c> 0068）。
        /// <b>進みは映像の劣化とまったく同じ値</b>（<see cref="DecayProgress"/>）。
        ///
        /// ⚠ <b>面が出ていないあいだも進める。</b> 届いた所で 0 から立ち上げると
        /// 「連絡が来ると壊れる」に見えて、因果が逆になる。
        /// </summary>
        /// <summary>
        /// 帰りの区間（<c>lap &gt; totalLaps</c>）に入ってからの経過を数える。
        /// ⚠ 手前の周では 0 へ戻す — 戻さないと、次の体験者で**最初から回復済み**になる。
        /// </summary>
        private void TickReturnClock(float dt)
        {
            if (runDirector == null) { _returnSec = 0f; return; }
            bool returning = runDirector.Lap > runDirector.TotalLaps;
            _returnSec = returning ? _returnSec + Mathf.Max(0f, dt) : 0f;
        }

        /// <summary>
        /// いまの侵食 0..1（<c>canon/LEDGER.md</c> 0070）。
        /// ⚠⚠ <b>単調ではない</b> — 3 周目で 1.0 に着き（原型を保てない）、
        /// 帰りの A で <see cref="CommsGlitchLogic.RecoveredLevel"/> まで戻る（なんとか復帰）。
        /// 映像の劣化は単調のままなので、**ここだけが山になる**。
        /// </summary>
        private float ResolveCorruption()
        {
            // プレビューは侵食そのものを差し込む（回復の途中も `decay` で直に指定できる）。
            if (_decayOverride >= 0f) return CommsGlitchLogic.LevelFor(_decayOverride);
            int lap = runDirector != null ? runDirector.Lap : 1;
            int total = runDirector != null ? runDirector.TotalLaps : 3;
            return CommsGlitchLogic.CorruptionFor(DecayProgress, lap, total, _returnSec);
        }

        private void TickGlitch(float timeSec)
        {
            _glitchLevel = ResolveCorruption();
            _glitchOffsetX = CommsGlitchLogic.OffsetXAt(timeSec, _glitchLevel);
            _glitchFlicker = CommsGlitchLogic.PanelFlickerAt(timeSec, _glitchLevel);

            // ⚠ 文面の差し替えは**刻みごとに 1 回だけ**。毎フレームやると TMP が
            //   組み直す（文字列の割り当ても毎フレーム出る）。
            int tick = CommsGlitchLogic.TickAt(timeSec);
            if (tick != _corruptTick)
            {
                _corruptTick = tick;
                ApplyCorruption();
            }
        }

        /// <summary>
        /// 素の文面を壊して面へ書く（<c>canon/LEDGER.md</c> 0069）。
        ///
        /// ⚠⚠ <b>打鍵の数え（<see cref="IsVisibleChar"/>）は素の文面のまま</b>にしてある。
        /// 化けて字が出なくなっても<b>打鍵は鳴る</b> — 装置は打っていて、字が出なかっただけ。
        /// 音と絵が食い違うのではなく、**印字の失敗が音でも分かる**という側。
        ///
        /// ⚠ 幅は変わらない（化け先も空白も全角）ので、重心の運び直しも枠の測り直しも要らない。
        /// </summary>
        private void ApplyCorruption()
        {
            TMP_Text? tmp = _text;
            if (tmp == null || _noticeSource.Length == 0) return;
            string s = CommsGlitchLogic.Corrupt(_noticeSource, _glitchLevel, _corruptTick);
            if (!string.Equals(tmp.text, s, System.StringComparison.Ordinal)) tmp.text = s;

            int n = 0;
            for (int i = 0; i < s.Length && i < _noticeSource.Length; i++)
                if (s[i] != _noticeSource[i]) n++;
            CorruptedChars = n;
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
        /// ⚠⚠ <b>左端は引数で受け取る</b>（2026-08-17・<c>canon/LEDGER.md</c> 0071）。
        /// 顔の枠のぶん面が左へ伸びて、<b>面の原点（＝ 文面の帯の中心）が左右の中央でなくなった</b>。
        /// <c>-fullW/2</c> を左端と決め打ちしていた頃の式のままだと、顔のぶんだけ面が右へずれる。
        ///
        /// ⚠⚠ <b>縦は「下端固定で伸びる」をやめた</b>（2026-08-16・<c>canon/LEDGER.md</c> 0065）。
        /// 下段が空になりうるので、<b>出ている帯だけを覆う</b>必要がある
        /// （呼び出し側が上端と下端から中心・高さを解く）。下端固定のままだと、
        /// 下段が無い連絡が<b>下半分の空いた箱</b>として出る。
        /// </summary>
        private static void SetFrame(Transform quad, float leftX, float fullW, float kx,
                                     float centerY, float height)
        {
            float w = fullW * kx;
            quad.localScale = new Vector3(w, height, 1f);
            // 左端は常に leftX に居る（開いたぶんの半分だけ右へ出る）。縦は解いた中心をそのまま置く。
            Vector3 p = quad.localPosition;
            quad.localPosition = new Vector3(leftX + w * 0.5f, centerY, p.z);
        }
    }
}
