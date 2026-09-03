#nullable enable
using FixedCamVr.Streaming;
using TMPro;
using UnityEngine;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// **体験前の注意書き。周回リセット直後の真っ暗な待ちの中だけに出る。**
    ///
    /// 立ち位置は <see cref="TitleStage.Wait"/>（黒だけが立っていて A を待っている段）で、
    /// 題字と同じ場所に注意事項を置く。A が押されて題字が立ち上がったら消える
    /// （<see cref="TitleStage.In"/> 以降は <see cref="ShouldShow"/> が false になる）。
    ///
    /// ⚠ **これはホラー体験の入口に置く安全のための掲示**で、
    /// 「体験者の視界には文字を 1 つも出さない」（rules/show-design.md）の対象外。
    /// 世界観に混ざらないよう、出るのは<b>まだ何も始まっていない黒の中だけ</b>に閉じてある。
    ///
    /// ⚠ **失敗したら黙って出さない側へ倒す**。日本語フォントが解決できない・実体を組めない
    /// ときは 1 文字も出さずに黙る（<see cref="TitleScreen"/> の「タイトルが組めない現場で
    /// 体験が二度と始まらない」と同じ流儀で、この面が体験を止めることは無い）。
    ///
    /// ⚠ 文言に新しい漢字・記号を足したら `Tools/FixedCamVr/Setup/Generate Japanese HUD Font` を
    /// 再実行する（静的ベイクなので忘れると実機で豆腐になる）。
    ///
    /// ⚠⚠ <b>この面は言語の選択も兼ねる</b>（2026-09-03 ユーザー指定）。注意書きの下に
    /// 選べる 3 つ（日本語 / English / Français）を並べ、いま選んでいるものを括弧で囲む。
    /// 巡らせるのは<b>体験者が持つ左コントローラの X／Y</b>で、入力を読むのは
    /// <c>OvrControllerBridge</c>（OVRInput を触れるのは Assembly-CSharp だけ）。
    /// この面が出ていない間は切り替わらない（<see cref="IsShowing"/> が門）。
    /// 選ばれた言語は <see cref="ShowLanguage.Current"/> が持ち、
    /// AIエージェントの連絡・手元のゲージ・終幕の報告が同じ値を読む。
    /// <b>スタッフが読む面（StatusHud・操作早見表・位置合わせ）は日本語のまま。</b>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TitleNotice : MonoBehaviour
    {
        [Tooltip("段の供給元。null ならシーンから探す。居なければ何も出さない。")]
        [SerializeField] private TitleScreen? titleScreen;

        [Tooltip("頭からの距離 (m)。題字（TitleScreen.distanceM）と同じ所に立てる。")]
        [SerializeField, Min(0.5f)] private float distanceM = 2.6f;

        [Tooltip("視線中心からどれだけ下に置くか (度)。題字と同じ据わりにする。")]
        [SerializeField, Range(-20f, 20f)] private float pitchOffsetDeg = 2.0f;

        [Tooltip("出るまでの秒。黒の中にすっと現れる。")]
        [SerializeField, Min(0.01f)] private float fadeInSec = 0.3f;

        [Tooltip("消えるまでの秒。ぱっと消すと題字の立ち上がりと喧嘩する。")]
        [SerializeField, Min(0.01f)] private float fadeOutSec = 0.2f;

        /// <summary>
        /// 注意書きの本文（日本語）。<b>改行の位置まで含めてここが唯一の供給元</b>。
        ///
        /// ⚠⚠ <b>紙（<c>docs/onsite/handout.html</c> の 2 枚目）と同じことを言う</b>（2026-08-14）。
        /// 紙は 0039 / 0040 で 2 度差し替えたのに、この面だけ 0023 ⑤ の旧文言が残っていて、
        /// **同じ安全の掲示が受付とヘッドセットの中で食い違っていた**（`canon/OPEN.md` の宿題）。
        /// ⚠ <b>改行は 1 行 20 文字まで</b>（<see cref="TextWidthM"/> に 1 文字 1.8° で入る数）。
        /// 2026-08-15 に字を 1.8° へ上げたので、旧の 1 行 24〜26 文字では横 46° を超えて
        /// 読むのに首を振ることになる。<b>英語・フランス語は半角なので 40 文字まで。</b>
        /// ⚠ 文言を変えたら <c>menu hud-font</c> を再実行する（静的ベイクなので忘れると豆腐）。
        /// </summary>
        private const string NoticeJa =
            "本作品にはホラー表現および、\n" +
            "不安や恐怖を感じる演出が含まれます\n" +
            "\n" +
            "体験中に気分が悪くなった場合は、\n" +
            "その場で立ち止まり、ヘッドセットを外して\n" +
            "スタッフにお声がけください";

        /// <summary>
        /// 注意書きの本文（English）。<b>日本語と同じことを言う</b> — 訳し足しも訳し落としもしない
        /// （安全の掲示なので、言語で内容が変わったらそれは別の掲示）。
        /// </summary>
        private const string NoticeEn =
            "This work contains horror imagery and\n" +
            "scenes meant to unsettle or frighten.\n" +
            "\n" +
            "If you feel unwell at any point, stop\n" +
            "where you are, remove the headset and\n" +
            "let a member of staff know.";

        /// <summary>注意書きの本文（Français）。<see cref="NoticeEn"/> と同じ規律。</summary>
        private const string NoticeFr =
            "Cette expérience contient des scènes\n" +
            "d'horreur, conçues pour inquiéter\n" +
            "ou effrayer.\n" +
            "\n" +
            "Si vous vous sentez mal, arrêtez-vous,\n" +
            "retirez le casque et prévenez\n" +
            "un membre du personnel.";

        /// <summary>
        /// 言語の名前。<b>それぞれの言語で書く</b>（"Japanese" ではなく「日本語」）—
        /// 読めない言語の名前が読めない言語で書いてあると、自分の言語を選べない。
        ///
        /// ⚠⚠ <b>表示用の文字列がここにあるのは、このファイルがフォントの収集元だから</b>
        /// （<c>JapaneseHudFontSetup.CollectHudCharset()</c> ＝ <c>tools/unity.ps1</c> の
        /// <c>hud-font</c> の <c>Src</c>）。<c>ShowLanguage</c> 側へ移すと収集元の追加が要り、
        /// 忘れると<b>実機で豆腐になるのに警告が 1 件も出ない</b>（0035 の「声」と同じ型）。
        /// </summary>
        private static string NameOf(ShowLang lang) => lang switch
        {
            ShowLang.En => "English",
            ShowLang.Fr => "Français",
            _ => "日本語",
        };

        /// <summary>
        /// いま選んでいる言語の印。<b>色ではなく括弧</b>（この面は <c>richText</c> を切ってあり、
        /// 白 1 色しか出せない）。全角の角括弧は <see cref="NameOf"/> と同じ理由でここに置く。
        /// </summary>
        private static string Marked(ShowLang lang, ShowLang current)
            => lang == current ? "［" + NameOf(lang) + "］" : NameOf(lang);

        /// <summary>
        /// 選べる言語を 1 行に並べたもの。区切りは<b>全角空白</b>
        /// （半角空白 1 つだと、日本語と Latin が地続きに見えて 3 つに読めない）。
        /// </summary>
        private static string ChooserLine(ShowLang current)
        {
            var sb = new System.Text.StringBuilder(48);
            for (int i = 0; i < ShowLanguage.All.Length; i++)
            {
                if (i > 0) sb.Append('　');
                sb.Append(Marked(ShowLanguage.All[i], current));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 切り替え方の 1 行。<b>キー名（X／Y）を出さない</b> — 被った体験者に手元は見えないので、
        /// どちらを押しても同じにしてある（<c>canon/LEDGER.md</c> 0050）。
        /// AIエージェントの連絡①bが「ボタンを長押ししてください」と言うのと同じ言い方に揃える。
        /// </summary>
        private static string HintOf(ShowLang lang) => lang switch
        {
            ShowLang.En => "Press the button to change language.",
            ShowLang.Fr => "Appuyez pour changer de langue.",
            _ => "手元のボタンで言語が変わります",
        };

        /// <summary>本文だけ（言語ごと）。テストと <c>menu text-audit</c> が読む。</summary>
        public static string BodyFor(ShowLang lang) => lang switch
        {
            ShowLang.En => NoticeEn,
            ShowLang.Fr => NoticeFr,
            _ => NoticeJa,
        };

        /// <summary>
        /// 面に出す全文 ＝ <b>注意書き ＋ 空行 ＋ 言語の並び ＋ 切り替え方</b>。
        ///
        /// ⚠ <b>言語の並びは注意書きと同じ面に出す。</b> 別の面を立てると、
        /// 「まだ何も始まっていない黒の中」に装置の UI が 2 枚並ぶ（世界に混ざる面が増える）。
        /// ⚠ <b>選べることは、選ぶ前の言語でも読めなければならない。</b> だから 3 つの名前を
        /// 常に全部出す（次の言語だけを出す形は、いま何が選べるのかが分からない）。
        /// </summary>
        public static string ComposeFor(ShowLang lang)
            => BodyFor(lang) + "\n\n" + ChooserLine(lang) + "\n" + HintOf(lang);

        /// <summary>
        /// タイトルの黒（<c>FixedCamVr/TitleVeil</c> = 4950）と題字（<c>TitleGlyph</c> = 4960）より
        /// 後に描くための Queue。
        ///
        /// ⚠ **5000 を超えてはいけない**（2026-07-31 実害）。URP の透明パスが描くのは
        /// <c>RenderQueueRange.transparent</c> = [2501, 5000] だけで、超えた値はどの描画パスにも
        /// 入らず 1 ピクセルも出ない。しかも `Shader.Find` も配置も成功するので警告が 1 件も出ない。
        /// </summary>
        private const int RenderQueue = 5000;

        /// <summary>版の中の字の大きさ。<b>倍率は <see cref="TextScale"/> が transform で掛ける。</b></summary>
        private const float FontSize = 0.07f;

        /// <summary>
        /// 文字の並ぶ幅 (m)。<b>いちばん長い行がちょうど収まる幅</b>にしてある ＝
        /// 左揃えでも文の塊が視界の中央に座る。2.6m 先で 37°。
        /// </summary>
        private const float TextWidthM = 1.70f;

        /// <summary>
        /// 文字の並ぶ高さ (m)。<b>いちばん行数の多い言語</b>（Français ＝ 7 行）＋ 空行 ＋
        /// 言語の並び ＋ 切り替え方 ＝ 10 行 ＋ 行間。
        /// ⚠ 揃えは縦中央（<c>TextAlignmentOptions.Left</c>）なので、ここが実際の行数より
        /// 低いと塊が枠からはみ出して<b>上下が視界の外へ出る</b>。文言を足したら一緒に上げる。
        /// </summary>
        private const float TextHeightM = 1.75f;

        /// <summary>
        /// 文字の拡大率。<b>距離から逆算する</b>（<see cref="HmdTextStyle"/> が唯一の正）。
        ///
        /// ⚠⚠ 2026-08-14 まで <c>fontSize = 0.07</c> を「1 文字 7cm」のつもりで書いていて、
        /// 実機では <b>StatusHud の 1/8.5</b> ＝ 読めない大きさの白い点線に見えていた
        /// （ユーザー報告「すごく奥に小さい白い文字」・<c>canon/LEDGER.md</c> 0035）。
        /// そのとき実機の画で測って 8.5 倍したが、<b>倍率を手で持っている限り同じ事故が再発する</b>
        /// （実際 <see cref="CommsPanel"/> が 1 か月後に同じ間違いを 10 倍の規模でやった）。
        /// ⚠ 倍率は <b>transform の scale</b> で掛ける（fontSize を上げるとメッシュの座標が広がる）。
        /// </summary>
        private float TextScale =>
            HmdTextStyle.MeshScale(HmdTextStyle.BodyDeg, Mathf.Max(distanceM, 0.5f), FontSize);

        /// <summary>TMP の Overlay 版（<c>ZTest Always</c>）。<b>Always Included に入っている。</b></summary>
        private const string OverlayShaderName = "TextMeshPro/Distance Field Overlay";


        /// <summary>黒が実際に立っているとみなす不透明度（<see cref="TitleScreen.AppliedVeil"/>）。</summary>
        private const float VeilUpMin = 0.9f;

        /// <summary>供給元が見つからないときの探し直しの間隔 (s)。毎フレーム探すと只では済まない。</summary>
        private const float ResolveRetrySec = 1f;

        private TMP_Text? _text;
        private HeadYawFollow? _follow;
        private float _alpha;
        private float _resolveWait;
        private bool _wasShown;
        /// <summary>いま面に書いてある言語。<see cref="ShowLanguage.Current"/> と食い違ったら組み直す。</summary>
        private ShowLang _shownLang = ShowLanguage.Default;

        /// <summary>
        /// <b>いま注意書きが画に出ているか</b>（＝ 言語を選べる間か）。
        ///
        /// <c>OvrControllerBridge</c> がこれを見て、左（体験者）のボタンを言語の切り替えへ回す。
        /// ⚠ <b>「段が Wait」ではなく「面が組めていて、かつ出る条件を満たしている」</b>を返す —
        /// 面が組めていない現場（フォントが解決できない等）では言語の並びが 1 文字も見えないので、
        /// 押しても何も起きない方が正しい（見えない切り替えは、体験者には壊れているのと同じ）。
        /// </summary>
        public bool IsShowing => _text != null && ShouldShow();

        private void Awake()
        {
            ResolveRefs();
            Build();
            SetAlpha(0f);
        }

        private void OnDisable()
        {
            // 次に有効化されたときは黒の中へ改めて現れる（消えかけの途中から再開しない）。
            _alpha = 0f;
            _wasShown = false;
            SetAlpha(0f);
        }

        private void ResolveRefs()
        {
            if (titleScreen == null) titleScreen = FindObjectOfType<TitleScreen>();
        }

        private void Build()
        {
            // ⚠ 日本語が出せないなら何も出さない。豆腐（□）が並ぶ方が、注意書きが無いより悪い。
            var jp = JapaneseHudFont.TryGet();
            if (jp == null)
            {
                Debug.LogWarning("[TitleNotice] 日本語フォントを解決できないので注意書きは出しません（体験はそのまま始まります）");
                return;
            }

            GameObject? go = null;
            try
            {
                // ⚠ 3D の TextMeshPro のまま（世界空間 Canvas へ移す利点は無かった）。
                //    2026-08-14 に Canvas 方式も試したが、絵は同じで実機ログの
                //    `Screen position out of view frustum` も減らなかった（`canon/OPEN.md`）。
                // ⚠⚠ **頭のヨーだけを追う根の下へ置く**（2026-08-20 ユーザー赤入れ
                //    「目の前に追従するんだけど見づらい」）。頭の子のまま局所座標だけ決めると
                //    ＝ head-lock で、上下に振っても面が眼から離れない。題字と同じ法則
                //    （`HeadYawFollow` ＝ 本編のスクリーンと同じ `YawFollowLogic`）に乗せる。
                _follow = HeadYawFollow.Attach(transform, "NoticeYawFollow");
                go = new GameObject("Label");
                go.transform.SetParent(_follow.transform, worldPositionStays: false);
                var tmp = go.AddComponent<TextMeshPro>();
                tmp.font = jp;
                _shownLang = ShowLanguage.Current;
                tmp.text = ComposeFor(_shownLang);
                // ⚠ 揃えは**左**（`HmdTextStyle` の規約）。中央にしてよいのは「掲げる言葉」だけで、
                //   これは**読ませる文章**（安全の掲示）。5 行の散文を中央揃えにすると行頭が毎行ずれる。
                //   枠幅を最長行に合わせてあるので、左揃えでも塊としては視界の中央に座る。
                tmp.alignment = TextAlignmentOptions.Left;
                tmp.fontSize = FontSize;
                // 折り返しは残す（文言を足した誰かが枠の外へ流れ出さないための安全網）。
                tmp.enableWordWrapping = true;
                tmp.richText = false;
                // 題字の朱と競合させない、抑えた白。純白だと黒の中で浮いて掲示物に見える。
                tmp.color = HmdTextStyle.Ink;

                var rt = (RectTransform)go.transform;
                // ⚠ **大きさは scale で掛ける。** fontSize を上げるとメッシュの座標が広がるだけで、
                //    見かけの大きさは同じ。小さい字を拡大する方が、頂点の座標が素直に収まる。
                //    ⇒ **折り返し幅も同じ scale で割る**。ここを固定値にすると、字の大きさを
                //      直したときに折り返しだけ取り残されて枠からはみ出す。
                float scale = TextScale;
                rt.sizeDelta = new Vector2(TextWidthM / scale, TextHeightM / scale);
                go.transform.localScale = Vector3.one * scale;

                // 頭の正面やや下。姿勢は追従根が持つので、ここでは根から見た置き場所だけを決める。
                // ⚠ **黒の面（TitleScreen の覆い）はこれに乗っていない** — 覆いが頭から離れると
                //   振り向いた瞬間に縁が視界へ入って現実が細く覗く。動かすのは読ませる字だけ。
                float rad = pitchOffsetDeg * Mathf.Deg2Rad;
                float d = Mathf.Max(distanceM, 0.5f);
                go.transform.localPosition = new Vector3(0f, -Mathf.Sin(rad) * d, Mathf.Cos(rad) * d);
                // 傾けない（傾けると台形に見えて、行の揃いが崩れる）。
                go.transform.localRotation = Quaternion.identity;

                // タイトルの黒に潰されないように、黒と題字より後に描く。fontMaterial の getter が
                // インスタンスを作るので、共有マテリアルを汚さない。
                // ⚠⚠ **描画順だけでは足りない。深度でも弾かれていた**（2026-08-13・LEDGER 0027）。
                //    TMP の既定シェーダ（`TextMeshPro/Distance Field`）は `ZTest [unity_GUIZTestMode]`
                //    ＝ 既定で LEqual。この面は **2.6m** に立つのに、本編のスクリーン（不透明・
                //    ZWrite On）が **2.0m** に居るので、**注意書きはスクリーンの深度に隠れて
                //    1 文字も出ていなかった**。しかも警告は 1 件も出ない。
                UseOverlayShader(tmp);
                tmp.fontMaterial.renderQueue = RenderQueue;
                _text = tmp;
            }
            catch (System.Exception e)
            {
                // 組めなかった側は必ず「出さない」で終わらせる（半端な面を残さない）。
                Debug.LogWarning($"[TitleNotice] 実体を組めません — 注意書きは出しません: {e.Message}");
                if (go != null) Destroy(go);
                if (_follow != null) Destroy(_follow.gameObject);
                _follow = null;
                _text = null;
            }
        }

        /// <summary>
        /// TMP の <b>Overlay 版</b>（<c>ZTest Always</c>）へ差し替える。
        ///
        /// 既定の <c>TextMeshPro/Distance Field</c> は ZTest を
        /// <c>unity_GUIZTestMode</c>（グローバル・既定 LEqual）で引くので、<b>マテリアルからは
        /// 上書きできない</b>。グローバルを書き換える手もあるが、それは他の全 TMP へ効く。
        ///
        /// ⚠ <b>剥がれ対策で Always Included に入れてある</b>（実行時 <c>Shader.Find</c> だけの
        /// シェーダはビルドから外される・2026-07-31 実害）。それでも見つからないときは
        /// <b>差し替えずに続ける</b> — 深度に負けて見えないかもしれないが、
        /// マテリアルを壊して字が化けるよりはよい。
        /// </summary>
        private static void UseOverlayShader(TMP_Text tmp)
        {
            var overlay = Shader.Find(OverlayShaderName);
            if (overlay == null)
            {
                Debug.LogWarning($"[TitleNotice] {OverlayShaderName} が見つかりません。" +
                                 "注意書きが本編スクリーンの深度に隠れる可能性があります");
                return;
            }
            tmp.fontMaterial.shader = overlay;
        }

        /// <summary>
        /// いま注意書きを出す段か。<b>解決できない環境では false ＝ 出さない側へ倒す。</b>
        ///
        /// 段が <see cref="TitleStage.Wait"/> なのは「タイトルが画面を持っていて、まだ A を
        /// 待っている」ときだけ（実体を組めなければ <c>TitleLogic.Disable</c> が
        /// <see cref="TitleStage.Off"/> へ落とす）。
        ///
        /// ⚠ 黒が<b>実際に立っている</b>ことも見る（「段が進んだ」ではなく「画に出た」の側）。
        /// 位置合わせ中はタイトルが時計を止めたまま面だけ畳むので、段は Wait のまま
        /// <see cref="TitleScreen.AppliedVeil"/> が 0 になる。ここを見ないと、
        /// <b>作業中のスタッフの視界に注意書きだけが浮く</b>。
        /// </summary>
        private bool ShouldShow()
        {
            if (titleScreen == null) return false;
            if (titleScreen.Stage != TitleStage.Wait) return false;
            return titleScreen.AppliedVeil >= VeilUpMin;
        }

        private void LateUpdate()
        {
            if (_text == null) return;

            if (titleScreen == null)
            {
                // 供給元がまだ居ない（プレビュー・生成順）。毎フレーム探さず、間を置いて拾い直す。
                _resolveWait += Time.unscaledDeltaTime;
                if (_resolveWait >= ResolveRetrySec)
                {
                    _resolveWait = 0f;
                    ResolveRefs();
                }
            }

            // 体験者が手元のボタンで言語を巡らせたら、その場で書き直す。
            // ⚠ **文字列を作り直すのは変わったフレームだけ。** 毎フレーム組み直すと、
            //   TMP がメッシュを作り直して黒の中で 90Hz ぶんの GC を回す。
            if (_shownLang != ShowLanguage.Current)
            {
                _shownLang = ShowLanguage.Current;
                _text.text = ComposeFor(_shownLang);
            }

            bool show = ShouldShow();
            // ⚠ **出る縁で頭の正面へ置き直す。** 置き直さないと、前に消えたときのヨーから
            //   緩慢に寄ってくる ＝ 注意書きが視界の外から流れ込む。
            if (show && !_wasShown) _follow?.SnapToHead();
            _wasShown = show;
            float target = show ? 1f : 0f;
            float sec = show ? fadeInSec : fadeOutSec;
            _alpha = Mathf.MoveTowards(_alpha, target, Time.unscaledDeltaTime / Mathf.Max(0.01f, sec));
            SetAlpha(_alpha);
        }

        private void SetAlpha(float a)
        {
            if (_text == null) return;
            _text.alpha = a;
            // 完全に消えている間は描画そのものを止める（体験中ずっと 0 の文字を描く理由が無い）。
            if (_text.gameObject.activeSelf != (a > 0.002f)) _text.gameObject.SetActive(a > 0.002f);
        }
    }
}
