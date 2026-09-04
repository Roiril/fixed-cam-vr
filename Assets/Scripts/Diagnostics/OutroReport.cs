#nullable enable
using FixedCamVr.Streaming;
using TMPro;
using UnityEngine;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// <b>終幕の報告。</b> 装置が力尽きて画が消えたあと、黒の中に浮かぶ 4 行
    /// （<c>canon/LEDGER.md</c> 0048）。文言は <see cref="OutroReportText"/> が持ち、
    /// 出す / 消すの判断は <see cref="OutroDirector.ReportAlpha"/> だけを読む。
    ///
    /// <b>体験前の注意書き（<see cref="TitleNotice"/>）と対になる面。</b> 置き方・大きさ・
    /// 深度の逃がし方はあちらと同じで、違うのは出る段だけ（あちらは真っ暗な待ち、こちらは終幕）。
    ///
    /// <b>出方</b>（<c>canon/LEDGER.md</c> 0063・2026-08-16）: <b>1 字ずつ打たれ、1 字ごとに
    /// 打鍵音が 1 発鳴る</b>（AIエージェントからの連絡＝<see cref="CommsPanel"/> と同じ装置の印字）。
    /// 速さは <see cref="CommsPanelLogic.CharsPerSecFor"/> をそのまま使う — 同じ装置が
    /// 面によって違う速さで打つと、別の装置が 2 台あるように聞こえる。
    /// ⚠ <b>言語では変わる</b>（日本語 12 / Latin 18 文字/秒・0149）。1 人が浴びるのは 1 言語だけ。
    ///
    /// ⚠ <b>不透明度のフェードは持たない。</b> 打鍵そのものが出現の演出で、重ねると
    /// 頭の数文字だけ薄いという半端な絵になる。<c>run.outro.reportFadeSec</c> は
    /// <b>段 Report の長さ</b>（＝ <see cref="OutroStage.Done"/> へ移るまで）としてだけ効く。
    /// 打ち切るまでの時間はそれより長いことがあり、<b>Done でも打ち続ける</b>（面は消えない）。
    ///
    /// ⚠ <b>揃えは左</b>（同 0063）。ただし<b>字の塊は視界の中央へ運ぶ</b> —
    /// 左寄せの意図は「行頭が揃って読める」ことで、「塊が視界の左に寄る」ことではない。
    ///
    /// ⚠ <b>「体験者の視界に文字を出さない」（rules/show-design.md）の対象外。</b>
    /// 体験そのものは既に終わっていて、この 4 行が終わったことを伝える唯一の手段
    /// （黒だけだと「まだ何か起きるのか」に見え、HMD を外してよいことが伝わらない）。
    ///
    /// ⚠⚠ <b>深度で弾かれる面。</b> 本編のスクリーン（2.0m・不透明・ZWrite On）より奥（2.6m）に
    /// 立つので、TMP の既定シェーダ（<c>ZTest LEqual</c>）のままだと <b>1 文字も出ない</b>
    /// （2026-08-13 に注意書きが丸ごと消えていた実害・<c>canon/LEDGER.md</c> 0027）。
    /// Overlay 版（<c>ZTest Always</c>）へ差し替える。
    ///
    /// ⚠ <b>失敗したら黙って出さない側へ倒す。</b> 日本語フォントが解決できない・実体を組めない
    /// ときは 1 文字も出さない（豆腐が 4 行並ぶ方が悪い）。組めたかは
    /// <see cref="IsBuilt"/> がテレメトリへ出す — <b>組めていないことに気づく口がそこしかない</b>。
    ///
    /// ⚠ 文言に新しい漢字・記号を足したら <c>.\tools\unity.ps1 menu hud-font</c> を再実行する。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OutroReport : MonoBehaviour
    {
        [Tooltip("終幕の実行体。null ならシーンから探す。居なければ何も出さない。")]
        [SerializeField] private OutroDirector? outro;

        [Tooltip("報告した数の供給元。null ならシーンから探す。居なければ 0 として出す。")]
        [SerializeField] private ShowControlClient? showControl;

        [Tooltip("打鍵音。null なら同 GameObject から取得（無ければ足す）。")]
        [SerializeField] private TypeAudioCue? typeSfx;

        [Tooltip("頭からの距離 (m)。体験前の注意書き（TitleNotice.distanceM）と同じ所に立てる。")]
        [SerializeField, Min(0.5f)] private float distanceM = 2.6f;

        [Tooltip("視線中心からどれだけ下に置くか (度)。注意書きと同じ据わりにする。")]
        [SerializeField, Range(-20f, 20f)] private float pitchOffsetDeg = 2.0f;

        /// <summary>タイトルの黒・題字より後に描く Queue。<b>5000 を超えない</b>（URP の透明パスは [2501, 5000]）。</summary>
        private const int RenderQueue = 5000;

        /// <summary>版の中の字の大きさ。<b>倍率は <see cref="TextScale"/> が transform で掛ける。</b></summary>
        private const float FontSize = 0.07f;

        /// <summary>文字の並ぶ幅 (m)。注意書き（<see cref="TitleNotice"/>）と同じ枠。</summary>
        private const float TextWidthM = 1.70f;

        /// <summary>文字の並ぶ高さ (m)。</summary>
        private const float TextHeightM = 1.00f;

        /// <summary>
        /// 文字の拡大率。<b>距離から逆算する</b>（<see cref="HmdTextStyle"/> が唯一の正）。
        /// 手で持っていた 8.5 倍は 2026-08-15 に捨てた — 経緯は <see cref="TitleNotice"/> の同名。
        /// </summary>
        private float TextScale =>
            HmdTextStyle.MeshScale(HmdTextStyle.BodyDeg, Mathf.Max(distanceM, 0.5f), FontSize);

        /// <summary>TMP の Overlay 版（<c>ZTest Always</c>）。<b>Always Included に入っている。</b></summary>
        private const string OverlayShaderName = "TextMeshPro/Distance Field Overlay";

        /// <summary>供給元が見つからないときの探し直しの間隔 (s)。毎フレーム探すと只では済まない。</summary>
        private const float ResolveRetrySec = 1f;

        private TMP_Text? _text;
        private HeadYawFollow? _follow;
        private float _alpha;
        private float _resolveWait;
        private int _shownCount = -1;
        // 打鍵の刻み。**画に何文字出したか**をここから決め、同じ数えから音を鳴らす。
        private float _typeElapsed;
        private int _charCount;
        private int _lastShown;
        // その字が絵を持つか（改行だけ false）。⚠ **全文が出ている一瞬にしか測れない** → SetBody。
        private bool[]? _charVisible;

        /// <summary>実体（TMP）を組めたか。<b>false なら一生出ない</b>（テレメトリが読む）。</summary>
        public bool IsBuilt => _text != null;

        /// <summary>いま実際に書いている不透明度（「段が進んだ」ではなく「画に出た」の側）。</summary>
        public float AppliedAlpha => _text != null ? _alpha : -1f;

        /// <summary>いま面に出している文字（テスト・診断用）。</summary>
        public string CurrentBody => _text != null ? _text.text : "";

        /// <summary>いま画に出ている文字数。<b>打鍵音はここから鳴る</b>ので、音の証拠でもある。</summary>
        public int VisibleChars { get; private set; }

        /// <summary>
        /// 打ち切るまでに鳴る打鍵の数（<b>改行を除いた字数</b>）。
        /// 解析器が <see cref="TypedCount"/> と突き合わせる。
        /// </summary>
        public int ReportChars { get; private set; }

        /// <summary>鳴らした打鍵の累計。<b>出た文字数と一致するはず</b>（改行は除く）。</summary>
        public int TypedCount => typeSfx != null ? typeSfx.PlayedCount : 0;

        /// <summary>打鍵の音源を掴めているか。<b>false なら字は出るのに無音。</b></summary>
        public bool TypeSfxBuilt => typeSfx != null && typeSfx.HasClips;

        private void Awake()
        {
            ResolveRefs();
            Build();
            SetAlpha(0f);
        }

        private void OnDisable()
        {
            _alpha = 0f;
            // 打鍵も頭へ戻す（次に出るときは 1 字目から打ち直す）。
            _typeElapsed = 0f;
            _lastShown = 0;
            VisibleChars = 0;
            if (_text != null) _text.maxVisibleCharacters = 0;
            SetAlpha(0f);
            typeSfx?.StopAll();
        }

        private void ResolveRefs()
        {
            if (outro == null) outro = FindObjectOfType<OutroDirector>();
            if (showControl == null) showControl = FindObjectOfType<ShowControlClient>();
            // ⚠ 打鍵音は**この面が持つ**（`ShowSoundDirector` は毎フレーム外から状態を見る層で、
            //    字の刻みちょうどには鳴らせない）。連絡の面・切替音と同じ構え。
            if (typeSfx == null) typeSfx = GetComponent<TypeAudioCue>();
            if (typeSfx == null) typeSfx = gameObject.AddComponent<TypeAudioCue>();
        }

        private void Build()
        {
            // ⚠ 日本語が出せないなら何も出さない。豆腐（□）が 4 行並ぶ方が、無いより悪い。
            var jp = JapaneseHudFont.TryGet();
            if (jp == null)
            {
                Debug.LogWarning("[OutroReport] 日本語フォントを解決できないので報告は出しません");
                return;
            }

            GameObject? go = null;
            try
            {
                // ⚠⚠ **頭のヨーだけを追う根の下へ置く**（2026-08-20 ユーザー赤入れ・
                //    注意書きと同じ指摘）。head-lock のままだと上下に振っても面が眼から離れず、
                //    4 行を読むあいだずっと目の前に貼り付く。題字と同じ `HeadYawFollow` に乗せる。
                _follow = HeadYawFollow.Attach(transform, "ReportYawFollow");
                go = new GameObject("Label");
                go.transform.SetParent(_follow.transform, worldPositionStays: false);
                var tmp = go.AddComponent<TextMeshPro>();
                tmp.font = jp;
                // ⚠⚠ **揃えは左**（`canon/LEDGER.md` 0063）。塊を中央へ運ぶのは SetBody。
                //    縦も**上寄せ**にする（`Left` ＝ 縦中央 は使えない）— 1 字ずつ出すと、
                //    行が増えた瞬間に TMP が「見えている行数」で縦中央を取り直し、
                //    打ち終わった行がひょいと上へ跳ねる（連絡の面で実測 38px）。
                tmp.alignment = TextAlignmentOptions.TopLeft;
                tmp.fontSize = FontSize;
                tmp.enableWordWrapping = true;
                tmp.richText = false;
                // 注意書きと同じ抑えた白。純白だと黒の中で浮いて掲示物に見える。
                tmp.color = HmdTextStyle.Ink;

                var rt = (RectTransform)go.transform;
                // ⚠ 折り返し幅も scale で割る（固定値にすると字を直したとき枠だけ取り残される）。
                float scale = TextScale;
                rt.sizeDelta = new Vector2(TextWidthM / scale, TextHeightM / scale);
                go.transform.localScale = Vector3.one * scale;
                go.transform.localRotation = Quaternion.identity;

                UseOverlayShader(tmp);
                tmp.fontMaterial.renderQueue = RenderQueue;
                _text = tmp;
                _shownCount = 0;
                SetBody(0);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[OutroReport] 実体を組めません — 報告は出しません: {e.Message}");
                if (go != null) Destroy(go);
                if (_follow != null) Destroy(_follow.gameObject);
                _follow = null;
                _text = null;
            }
        }

        /// <summary>
        /// 面を置く先（頭の正面から <see cref="pitchOffsetDeg"/> だけ下げた
        /// <see cref="distanceM"/> の点）。<b>字の塊の重心をここへ運ぶ</b>ので、
        /// 実際の <c>localPosition</c> は <see cref="SetBody"/> がここから差し引いて決める。
        /// </summary>
        private Vector3 BasePosition
        {
            get
            {
                float rad = pitchOffsetDeg * Mathf.Deg2Rad;
                float d = Mathf.Max(distanceM, 0.5f);
                return new Vector3(0f, -Mathf.Sin(rad) * d, Mathf.Cos(rad) * d);
            }
        }

        /// <summary>
        /// 文面を差し替えて、1 字ずつ出すための下ごしらえをする。
        /// <b>数が変わったときだけ</b>走る（毎フレームではない）。
        ///
        /// ⚠ 測る前に<b>全文を見えるところまで戻す</b> — 直前の可視数が残っていると
        /// <see cref="TMP_Text.textBounds"/> が<b>その一部だけ</b>の重心を返して面から外れる。
        /// ⚠ <b>x も y も中央へ運ぶ。</b> 揃えが左でも、字の塊そのものは視界の中央に居るべき
        /// （左寄せの意図は行頭が揃うことで、塊が左へ寄ることではない）。
        /// </summary>
        private void SetBody(int markCount)
        {
            TMP_Text? tmp = _text;
            if (tmp == null) return;
            string body = OutroReportText.Compose(markCount);

            // ⚠ 測るあいだだけ実体を起こす。**消えている面の <c>textBounds</c> は信用できない**
            //   （この面は出ていない間ずっと非アクティブ ＝ 数が変わるのは必ずその最中）。
            bool wasActive = tmp.gameObject.activeSelf;
            if (!wasActive) tmp.gameObject.SetActive(true);

            tmp.maxVisibleCharacters = int.MaxValue;
            if (tmp.text != body) tmp.SetText(body);
            // ⚠ **2 回呼ぶ。** 1 回目でまだ焼かれていないグリフの焼き付けを要求し、2 回目で
            //   焼けたものを含めて組み直す。1 回だと <c>textBounds</c> が足りない字を欠いたまま
            //   返り、**塊を運ぶ先が静かにずれる**（`HmdTextAudit.Layout` と同じ理由）。
            tmp.ForceMeshUpdate(ignoreActiveState: true, forceTextReparsing: true);
            tmp.ForceMeshUpdate(ignoreActiveState: true, forceTextReparsing: true);

            float scale = TextScale;
            Bounds ink = tmp.textBounds;
            tmp.transform.localPosition = BasePosition
                                        + new Vector3(-ink.center.x * scale, -ink.center.y * scale, 0f);

            // ⚠ ここは**全文が出ている状態**なので、`isVisible` が「その字が絵を持つか」を表す。
            //    ここでしか測れない（下で 0 に戻すと、以後は全部 false になる）。
            var info = tmp.textInfo;
            _charCount = info != null ? info.characterCount : 0;
            _charVisible = new bool[_charCount];
            int visible = 0;
            for (int i = 0; i < _charCount; i++)
            {
                _charVisible[i] = info!.characterInfo[i].isVisible;
                if (_charVisible[i]) visible++;
            }
            ReportChars = visible;

            tmp.maxVisibleCharacters = 0;
            // 文面を差し替えたら**打鍵の数えも 0 に戻す**（戻さないと、短い文面では 1 発も鳴らず、
            // 長い文面では途中から鳴り始める）。
            _lastShown = 0;
            VisibleChars = 0;
            _shownCount = markCount;

            if (!wasActive) tmp.gameObject.SetActive(false);
        }

        /// <summary>
        /// その字は絵を持つ字か（<see cref="SetBody"/> が 1 度だけ測る）。
        /// <b>改行では打鍵を鳴らさない</b> — <c>maxVisibleCharacters</c> は改行も 1 文字として
        /// 数えるので、鳴らすと「字が出ていないのに 1 発鳴る」が起きる（報告は改行を 4 つ持つ）。
        ///
        /// ⚠⚠ <b>毎フレーム <c>textInfo.characterInfo[i].isVisible</c> を見てはいけない。</b>
        /// あれは<b>いまの <c>maxVisibleCharacters</c> の下で描かれたか</b>を表すので、
        /// たったいま出た字は<b>必ず false</b>（連絡の面で踏んで、打鍵が 1 発しか鳴らなかった）。
        /// ⚠ 分からないときは<b>鳴らす側へ倒す</b>（黙る方が気づけない）。
        /// </summary>
        private bool IsVisibleChar(int i)
        {
            if (_charVisible == null || i < 0 || i >= _charVisible.Length) return true;
            return _charVisible[i];
        }

        /// <summary>
        /// TMP の <b>Overlay 版</b>（<c>ZTest Always</c>）へ差し替える。既定の
        /// <c>TextMeshPro/Distance Field</c> は ZTest をグローバル（<c>unity_GUIZTestMode</c>）で引くので
        /// マテリアルからは上書きできない。見つからないときは<b>差し替えずに続ける</b>
        /// （深度に負けて見えないかもしれないが、マテリアルを壊して字が化けるよりはよい）。
        /// </summary>
        private static void UseOverlayShader(TMP_Text tmp)
        {
            var overlay = Shader.Find(OverlayShaderName);
            if (overlay == null)
            {
                Debug.LogWarning($"[OutroReport] {OverlayShaderName} が見つかりません。" +
                                 "報告が本編スクリーンの深度に隠れる可能性があります");
                return;
            }
            tmp.fontMaterial.shader = overlay;
        }

        private void LateUpdate()
        {
            if (_text == null) return;

            if (outro == null || showControl == null)
            {
                _resolveWait += Time.unscaledDeltaTime;
                if (_resolveWait >= ResolveRetrySec)
                {
                    _resolveWait = 0f;
                    ResolveRefs();
                }
            }

            // 出す / 消すの判断は持たない（終幕の段が「出す」の門になる）。
            bool wanted = outro != null && outro.ReportAlpha > 0f;

            // ⚠ 文言の更新は**出る直前まで**。出ている最中に数が変わると、体験者の目の前で
            //    数字が書き換わる（報告は体験の終わりに確定した値であってライブの表示ではない）。
            if (!wanted)
            {
                int n = showControl != null ? showControl.VisitorMarkCount : 0;
                if (n != _shownCount) SetBody(n);
                // 次のランのために打鍵を頭へ戻す（前の体験者の続きから打ち始めない）。
                if (_typeElapsed > 0f)
                {
                    _typeElapsed = 0f;
                    _lastShown = 0;
                    VisibleChars = 0;
                    _text.maxVisibleCharacters = 0;
                    typeSfx?.StopAll();
                }
                _alpha = 0f;
                SetAlpha(0f);
                // ⚠ 出ていないあいだは頭の正面へ置き直し続ける。1 字目が打たれた瞬間に
                //   前の向きから緩慢に寄ってくると、報告が視界の外から流れ込む。
                _follow?.SnapToHead();
                return;
            }

            // ⚠ **不透明度はフェードしない。** 打鍵そのものが出現の演出なので、重ねると
            //    頭の数文字だけ薄いという半端な絵になる（1 字目が打たれるまで画には何も無い）。
            _alpha = 1f;
            _typeElapsed += Time.unscaledDeltaTime;

            // 1 字ずつ出す。⚠ **切り上げ**（0 より大きければ 1 字目は出ている）。
            int shown = _charCount <= 0
                      ? 0
                      : Mathf.Clamp(
                            Mathf.CeilToInt(_typeElapsed
                                            * CommsPanelLogic.CharsPerSecFor(ShowLanguage.Current)),
                            0, _charCount);
            if (_text.maxVisibleCharacters != shown) _text.maxVisibleCharacters = shown;

            // ⚠⚠ **打鍵音は、字を画へ書いているこの行から鳴らす**（`canon/LEDGER.md` 0063）。
            //    絵と音が同じ数えから出るので、ずれようがない。
            //    ⚠ **増えた字数ぶん鳴らさない。** 1 フレームで 2 字進んだら（コマ落ち）
            //      同じ DSP 時刻に 2 発重なって 1 つの大きな音に潰れる。1 発だけ鳴らす。
            //    ⚠⚠ **鳴らす場所も同じ行が決める**（2026-09-03・`canon/LEDGER.md` 0130）。
            //      打鍵は「スクリーン関係の音」なので、字が出ている面そのものから鳴る。
            //      渡すのは文字の Transform（`_follow` の下に居る）で、
            //      この部品が乗っている GameObject ではない（あちらは動かない ＝ 足元から鳴る）。
            if (shown > _lastShown && IsVisibleChar(shown - 1))
                typeSfx?.Play(_text.transform.position);
            _lastShown = shown;
            VisibleChars = shown;

            SetAlpha(_alpha);
        }

        private void SetAlpha(float a)
        {
            if (_text == null) return;
            _text.alpha = a;
            // 完全に消えている間は描画そのものを止める（体験中ずっと 0 の文字を描く理由が無い）。
            bool on = a > 0.002f && VisibleChars > 0;
            if (_text.gameObject.activeSelf != on) _text.gameObject.SetActive(on);
        }
    }
}
