#nullable enable
using System;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// スクリーン左上に日付と時刻（<c>canon/LEDGER.md</c> 0108）と、その右に<b>周回</b>（0167）を出す。
    /// <c>_OsdTex</c> / <c>_OsdRect</c> / <c>_OsdOpacity</c> の**唯一の writer**
    /// （<see cref="CameraFeelFx"/> / <see cref="GlitchFx"/> と同じ流儀）。
    ///
    /// <b>なぜ TMP の面ではなくシェーダの層か</b>: 面を別に立てると
    /// <c>_ScreenPower</c>（終幕の電池切れ）・切替の黒・管の曲面・縁の暗さに乗らず、
    /// 「画面の上に別の層が貼ってある」＝合成の証拠そのものになる。
    /// スクリーンより奥の TMP が深度で消える罠（<c>canon/LEDGER.md</c> 0027）とも無縁。
    ///
    /// <b>ユーザーの狙い</b>（0108 逐語）: 「カメラが切り替わって演出が入っても、この表示が
    /// 変わらずあり続けることで、スクリーン＝現実であり合成でない感じを強めたい」。
    /// <b>3 周目に 1 周目の録画が流れても時計は「いま」のまま進む</b> — 録画は配信の生 JPEG で
    /// OSD が焼き込まれていないので、装置が「これは今の映像だ」と主張し続ける形になる。
    ///
    /// <b>周回（0167）</b>: 「1周目」「2周目」「3周目」、帰りの区間は「最後」。
    /// <b>体験者が選んだ言語で出す</b>（English は <c>LAP 1</c> / <c>LAST</c>、
    /// Français は <c>TOUR 1</c> / <c>FIN</c>）。時刻は数字と区切りだけなので訳しようがない。
    /// <b>別の場所（バックルームズ）が映っているあいだは、時刻も周回も <c>?</c> になる</b> —
    /// 装置が場所を見失っている、という 1 つの出来事を 2 つの欄で言っている。
    ///
    /// ⚠ <b>秒が変わったときだけ敷き直す</b>（1Hz）。毎フレーム書くと 90Hz で 4 万画素を組み替える。
    ///   周回が変わった縁でも敷き直す（そこだけは次の秒を待たない）。
    /// ⚠ <b>版を掴めなければ何も書かない</b>（<c>_OsdRect</c> は 0 のまま ＝ シェーダは 1 画素も触らない）。
    ///   掴めたかは <see cref="Built"/> ＝ テレメトリの <c>osd=</c>。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ScreenOsd : MonoBehaviour
    {
        private static readonly int OsdTexId = Shader.PropertyToID("_OsdTex");
        private static readonly int OsdRectId = Shader.PropertyToID("_OsdRect");
        private static readonly int OsdOpacityId = Shader.PropertyToID("_OsdOpacity");

        /// <summary>版の置き場（<c>tools/make-osd-font.py</c> が焼く）。</summary>
        public const string GlyphResourcePath = "Osd/OsdGlyphs";

        /// <summary>
        /// 1 セルの高さ ÷ 枠の高さ。<b>参考画像（004.jpg）の実測に合わせた値</b>
        /// （2026-08-22 ユーザー赤入れ「参考画像に合わせてほしい」）。
        ///
        /// 参考は <b>字高 / 画像高 = 7/288 = 2.43%</b>。版のセルは字の 54/34 倍の高さを持つので
        /// <c>0.0243 × 54/34 = 0.0386</c>。スクリーンの垂直画角は約 36.8°（2.0m 先で ±18.4°）なので
        /// <b>1 セル 1.42° ＝ 字の高さ 0.89°</b>、文字列の全幅は<b>枠幅の 18.3%</b>（参考 18.6%）。
        ///
        /// ⚠ <b>読ませる面より小さいのは意図</b>（<c>HmdTextStyle</c> の補助は 1.5°）。
        /// これは読ませる面ではなく画の一部。
        /// ⚠ ただし<b>実機で読めるかは被って確かめる</b> — 見かけ角の机上見積もりは
        /// 2 回続けて外した前歴がある（<c>memory/hmd_text_style.md</c>）。
        /// ⚠ <b>版の縦横比を変えたらここも直す</b>（セル高が字高の何倍かが変わる）。
        /// </summary>
        public const float CellHeightK = 0.0386f;

        /// <summary>
        /// 枠の縁からの余白（枠の高さに対する割合・上と左で同じ）。
        /// ⚠ 管の角丸（<c>_CrtRound</c> 0.07）と縁の暗さ（<c>_CrtEdgeWidth</c> 0.17）に
        /// 食い込む位置なので、<b>絵を見て決めた</b>（`menu osd`）。
        /// </summary>
        public const float MarginK = 0.04f;

        [Tooltip("枠のアスペクトの供給元。null なら同 GameObject → シーンから探す。")]
        [SerializeField] private MjpegScreen? screen;

        [Tooltip("周回の出どころ（区間の周）。null ならシーンから探す。")]
        [SerializeField] private TimelineDirector? timeline;

        [Tooltip("走り切る周数の出どころ。null ならシーンから探す。")]
        [SerializeField] private ShowRunDirector? run;

        [Tooltip("いま画面を取っているカットの出どころ（異世界の判定）。null ならシーンから探す。")]
        [SerializeField] private TakeRunner? takes;

        private Material? _material;
        private Texture2D? _target;
        private Color32[]? _atlas;
        private Color32[]? _buffer;
        private int _cellW, _cellH;
        private Vector2 _previewFit = Vector2.one;
        private long _stamp = OsdClockLogic.Never;
        private readonly int[] _glyphs = new int[OsdClockLogic.CellCount];
        private bool _warned;

        private int _lap = -1;
        private int _totalLaps = ShowRunDefaults.TotalLaps;
        private bool _otherworld;
        private ShowLang _lang = ShowLang.Ja;
        // 最後に**敷いた**状態。時刻と同じで、変わった縁でしか敷き直さない。
        private int _drawnLap = int.MinValue;
        private int _drawnTotalLaps = int.MinValue;
        private bool _drawnOtherworld;
        private ShowLang _drawnLang = (ShowLang)(-1);

        /// <summary>
        /// 書く先（スクリーンの Renderer の材質）と版の両方を掴めているか。
        /// <b>ここが false だと時計は 1 画素も出ない。</b>
        /// 画には「無かった」としか出ないので、テレメトリが唯一の手掛かりになる。
        /// </summary>
        public bool Built => _material != null && _target != null;

        /// <summary>敷き直した累計（＝時計が進んでいる証拠）。診断用。</summary>
        public int Ticks { get; private set; }

        /// <summary>いま書いている不透明度 0..1。診断用。</summary>
        public float Opacity { get; private set; }

        /// <summary>
        /// <b>時計を消す</b>（立てているあいだ <see cref="Opacity"/> は 0）。
        ///
        /// 立てるのは <c>OutroDirector</c> だけ — <b>終幕の電源断の頭</b>で消す
        /// （<c>canon/LEDGER.md</c> 0111）。潰れていく画の上に時計を残すと、
        /// 矩形のサンプルの暗黙微分が飛んで<b>灰色の帯</b>になる（字ではなく汚れに見える）。
        ///
        /// ⚠ <b>時刻で嘘をつくのとは別のこと。</b> 禁じられているのは巻き戻し・停止・加速で
        /// （<c>rules/streaming.md</c>「装置が打っている時計」）、装置ごと落ちるときに
        /// 一緒に消えるのは装置の挙動として正しい。
        /// ⚠ 立てっぱなしにしない — 戻すのは <c>OutroDirector.ResetScreen</c> 1 本。
        /// </summary>
        public bool Suppressed { get; set; }

        /// <summary>
        /// いまの時刻の出どころ。**既定は端末の実時刻**（<c>canon/OPEN.md</c> Q12）。
        /// 当日が 9/6 なら自動で <c>2026/09/06</c> になり、リハの日はその日の日付が出る —
        /// どちらも現実なので嘘がない。
        ///
        /// 差し替えるのは Editor のプレビューだけ（決定論のため固定時刻を渡す）。
        /// ⚠ <b>体験の中で嘘の時刻へ差し替えない</b>（<see cref="OsdClockLogic"/> の注記）。
        /// </summary>
        public Func<DateTime>? TimeProvider { get; set; }

        /// <summary>いま出している周回の語（「1周目」「最後」「???」）。空なら欄は空白。</summary>
        public string Label { get; private set; } = "";

        /// <summary>
        /// テレメトリに出す周回のトークン（<c>osdLap=</c>）。
        /// ⚠ <b>敷いた結果を出す。</b> 状態だけを出すと「時計が組めていないのに周回だけ出ている」
        /// という嘘のログになる（画には 1 画素も出ていない）。
        /// </summary>
        public string LabelToken { get; private set; } = "-";

        /// <summary>異世界が映っているとみなしているか（診断用）。</summary>
        public bool Otherworld => _otherworld;

        private void Awake()
        {
            var r = GetComponent<Renderer>();
            _material = r != null ? r.material : null;
            if (screen == null) screen = GetComponent<MjpegScreen>();
            if (screen == null) screen = FindObjectOfType<MjpegScreen>();
            ReadShowState();
            BuildTarget();
        }

        private void OnDisable()
        {
            // 自分を外したら画は素へ戻す（このコンポーネントが無い状態と同じ画にして去る）。
            Opacity = 0f;
            _material?.SetVector(OsdRectId, Vector4.zero);
        }

        private void OnDestroy()
        {
            if (_target == null) return;
            if (Application.isPlaying) Destroy(_target); else DestroyImmediate(_target);
            _target = null;
        }

        private void Update()
        {
            ReadShowState();
            Tick(TimeProvider != null ? TimeProvider() : DateTime.Now);
        }

        /// <summary>
        /// 周回と異世界をシーンから読む（<c>canon/LEDGER.md</c> 0167）。
        ///
        /// ⚠ 読むのは <b>区間の周</b>（<see cref="TimelineDirector.CurrentLap"/>）— 体験者が
        /// いま立っている区間で、逆走すれば戻る。進行の周を出すと、引き返した人の画面だけが
        /// 実際より先に進む。<c>rules/show-design.md</c>「周回数は 2 つある」。
        /// ⚠ 異世界の判定は <see cref="TakeRunner.OtherworldActive"/> 1 本に寄せてある
        /// （音（<c>ShowSoundDirector</c>）と同じ 1 本。2 か所で判じると、片方だけ直したときに
        /// 「風は鳴っているのに時計は出たまま」が黙って起きる）。
        /// </summary>
        private void ReadShowState()
        {
            if (timeline == null) timeline = FindObjectOfType<TimelineDirector>();
            if (run == null) run = FindObjectOfType<ShowRunDirector>();
            if (takes == null) takes = FindObjectOfType<TakeRunner>();
            SetShowState(timeline != null ? timeline.CurrentLap : -1,
                         run != null ? run.TotalLaps : ShowRunDefaults.TotalLaps,
                         takes != null && takes.OtherworldActive,
                         ShowLanguage.Current);
        }

        /// <summary>
        /// 周回・異世界・言語を外から与える（Editor のプレビューが状態ごとに焼くための口）。
        /// 実機では <see cref="ReadShowState"/> が毎フレーム同じ値を入れる。
        /// </summary>
        public void SetShowState(int lap, int totalLaps, bool otherworld,
                                 ShowLang lang = ShowLang.Ja)
        {
            _lap = lap;
            _totalLaps = totalLaps;
            _otherworld = otherworld;
            _lang = lang;
        }

        /// <summary>
        /// Editor のプレビューが材質を直接与える口（<c>Awake</c> の代わり）。
        ///
        /// ⚠ <b>プレビューに敷き直しを模写させないため</b>にある。矩形の式・セルのコピー・
        /// ミップの作り直しを別に書くと、片方だけ直したときに「絵では合っているのに実機で違う」が
        /// 黙って起きる（この codebase が何度も踏んだ型）。
        /// ⚠ Edit モードで <c>Renderer.material</c> を触ると Unity が材質を複製して漏らすので、
        /// プレビュー側が作った材質をそのまま渡す。
        /// </summary>
        /// <param name="containScale">
        /// 映像の contain-fit 比（<see cref="MjpegScreen.ContainScale"/> 相当）。
        /// ⚠ 渡さないと letterbox の外＝枠の隅に出るので、**プレビューが実機と違う位置を映す**。
        /// </param>
        public void BindForPreview(Material material, Vector2 containScale)
        {
            _material = material;
            _previewFit = containScale;
            _stamp = OsdClockLogic.Never;
            BuildTarget();
        }

        /// <summary>
        /// 1 回ぶん進める。Editor のプレビューが固定時刻で叩けるよう公開してある。
        /// </summary>
        public void Tick(DateTime now)
        {
            if (!Built) return;
            // 秒が変わったときのほかに、**周回が変わった / 異世界へ入った縁**でも敷き直す
            //（`canon/LEDGER.md` 0167）。ここを見落とすと、周回は次の秒まで古いままになる。
            bool stateChanged = _lap != _drawnLap
                             || _totalLaps != _drawnTotalLaps
                             || _otherworld != _drawnOtherworld
                             || _lang != _drawnLang;
            if (OsdClockLogic.NeedsRedraw(now, _stamp, out long stamp) || stateChanged)
            {
                _stamp = stamp;
                Redraw(now);
            }
            WriteRect();
        }

        // 版から 26 セルぶんを 1 枚へ敷き直す。行ごとの Array.Copy なので画素の走査は無い。
        private void Redraw(DateTime now)
        {
            if (_atlas == null || _buffer == null || _target == null) return;
            if (!OsdClockLogic.FillCells(now, _lap, _totalLaps, _otherworld, _glyphs, _lang)) return;
            _drawnLap = _lap;
            _drawnTotalLaps = _totalLaps;
            _drawnOtherworld = _otherworld;
            _drawnLang = _lang;
            Label = _otherworld ? OsdClockLogic.MaskedLabel
                                : OsdClockLogic.LapLabel(_lap, _totalLaps, _lang);
            LabelToken = OsdClockLogic.LapToken(_lap, _totalLaps, _otherworld);

            int atlasW = _cellW * OsdClockLogic.GlyphCount;
            int dstW = _cellW * OsdClockLogic.CellCount;
            for (int y = 0; y < _cellH; y++)
            {
                int srcRow = y * atlasW;
                int dstRow = y * dstW;
                for (int i = 0; i < OsdClockLogic.CellCount; i++)
                    Array.Copy(_atlas, srcRow + _glyphs[i] * _cellW,
                               _buffer, dstRow + i * _cellW, _cellW);
            }
            _target.SetPixels32(_buffer);
            // ⚠ **ミップを作り直す。** 実機では 1 字がおよそ 16 画素まで縮むので、
            //   ミップが無いと縮小のエイリアスで秒が刻むたびに字がちらつく
            //   （＝そこだけ生き物のように見える）。1 秒に 1 度・4 万画素なので安い。
            _target.Apply(updateMipmaps: true);
            Ticks++;
        }

        private void WriteRect()
        {
            if (_material == null) return;
            // 枠 UV。左上に置く（uv.y は下原点なので上端から下ろす）。
            float aspect = screen != null ? screen.ScreenAspect : 16f / 9f;
            if (aspect <= 0.01f) aspect = 16f / 9f;
            float h = CellHeightK;
            // セルの縦横比を枠のアスペクトで割って、枠 UV の幅へ直す
            float w = h * ((float)_cellW / Mathf.Max(_cellH, 1)) / aspect * OsdClockLogic.CellCount;

            // ⚠⚠ **枠の左上ではなく「映像の左上」へ置く**（2026-08-22・絵で見て直した）。
            //   映像は 4:3、枠は 16:9 なので左右に必ず黒帯（letterbox）が出る。枠の隅に置くと
            //   **字の頭が黒帯の上に乗り**、そこだけコントラストが最大になって主張が強くなる。
            //   参考画像（004.jpg）の OSD も映像の中にある。合成の層は変えていない
            //   （装置が映像の上に重ねている ＝ DVR の OSD そのもの）ので、虚構は保たれる。
            // ⚠ 映像より大きくは寄せない（`Min` で枠の内側に留める）。ソースが枠より横長なら
            //   contain-fit は上下に帯を出すので、そのときは y の側が効く。
            Vector2 fit = screen != null ? screen.ContainScale : _previewFit;
            float left = 0.5f - 0.5f * Mathf.Clamp(fit.x, 0.05f, 1f);
            float top = 0.5f + 0.5f * Mathf.Clamp(fit.y, 0.05f, 1f);
            float x = left + MarginK / aspect;
            float y = top - MarginK - h;
            // 終幕の電源断の頭で消える（`Suppressed`）。矩形は書いたままにして不透明度だけ落とす
            // — 矩形を 0 にすると復帰のときに組み直しが要る。
            Opacity = Suppressed ? 0f : 1f;
            _material.SetVector(OsdRectId, new Vector4(x, y, w, h));
            _material.SetFloat(OsdOpacityId, Opacity);
        }

        private void BuildTarget()
        {
            var atlas = Resources.Load<Texture2D>(GlyphResourcePath);
            if (atlas == null)
            {
                Warn($"[ScreenOsd] 版 Resources/{GlyphResourcePath} が見つかりません" +
                     "（py -3.11 tools/make-osd-font.py で焼く）。時刻表示は出ません");
                return;
            }
            if (atlas.width % OsdClockLogic.GlyphCount != 0)
            {
                Warn($"[ScreenOsd] 版の幅 {atlas.width} が {OsdClockLogic.GlyphCount} で割り切れません"
                     + "（tools/make-osd-font.py の GLYPHS と OsdClockLogic.Glyphs が食い違っている）");
                return;
            }
            try
            {
                _atlas = atlas.GetPixels32();
            }
            catch (UnityException)
            {
                // Read/Write Enabled が落ちている（インポート設定）。**警告を出して黙って諦めない**。
                Warn($"[ScreenOsd] 版 {GlyphResourcePath} を読めません（Read/Write Enabled が要る）");
                return;
            }
            _cellW = atlas.width / OsdClockLogic.GlyphCount;
            _cellH = atlas.height;
            _buffer = new Color32[_cellW * OsdClockLogic.CellCount * _cellH];
            _target = new Texture2D(_cellW * OsdClockLogic.CellCount, _cellH,
                                    TextureFormat.RGBA32, mipChain: true)
            {
                name = "ScreenOsdText",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
            };
            _material?.SetTexture(OsdTexId, _target);
        }

        private void Warn(string message)
        {
            if (_warned) return;
            _warned = true;
            Debug.LogWarning(message);
        }
    }
}
