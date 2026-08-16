#nullable enable
using FixedCamVr.Streaming;
using TMPro;
using UnityEngine;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// <b>上司からの連絡（第 2 の面）。</b> 本編のスクリーンとは別に、少し手前・少し外側に立てる。
    ///
    /// 判定は <c>canon/LEDGER.md</c> 0043（ユーザー逐語）:
    /// 「せっかく VR で立体的なので、スクリーンにつけなくていい。スクリーンよりも少し体験者に近く
    /// かつすこし外側に、新しいスクリーンとして設置するでもいいと思う」。
    /// 実装の順序と未確定は <c>.claude/plans/2026-08-15_comms-panel.md</c>。
    ///
    /// ⚠⚠ <b>これは仮実装</b>（2026-08-15）。出るのは<b>本編に入って一定秒後に 1 回だけ</b>で、
    /// 文面もコードが持っている。show.json への著作（take の並列チャンネル）は次の段。
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
        /// ⚠ スクリーンは見かけ 55° 前後あるので、20° では半分ほど重なる。
        /// 重ねたくないなら 30° 以上へ振るが、そこまで外だと視界の端で読みにくい。
        /// </summary>
        private const float YawOffsetDeg = -20f;

        /// <summary>視線中心から下へ振る角度（度）。</summary>
        private const float PitchOffsetDeg = 10f;

        /// <summary>面の幅 (m)。1.5m 先で 0.76m ＝ <b>見かけ 28°</b>。</summary>
        private const float PanelW = 0.76f;
        /// <summary>面の高さ (m)。1.5m 先で 0.26m ＝ 見かけ 10°。</summary>
        private const float PanelH = 0.26f;
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
            CommsNotice.Begin => "調査を開始してください",
            CommsNotice.MarkLogged => "異常が記録されました",
            CommsNotice.MarkNothing => "異常は検出されませんでした",
            CommsNotice.Prompt => "異常が検出されました。\n記録してください。",
            _ => "",
        };

        /// <summary>
        /// 面を組むときに使う文面 ＝ <b>いちばん長い行を持つもの</b>（13 文字）。
        ///
        /// ⚠ ここを短い文面にすると <c>menu text-audit</c> が<b>最悪の行を測らない</b>ので
        /// 「枠に収まっている」と嘘をつく。実行時はどの文面でも <see cref="SetNotice"/> が組み直す。
        /// ⚠ <b>行数の最悪（2 行 ＝ ③）はここでは測れない。</b> 縦の座りは
        /// <c>menu comms-preview</c> の絵で見る（4 文面ぶん焼く）。
        /// </summary>
        internal static string LongestNoticeText => TextFor(CommsNotice.MarkNothing);

        private readonly CommsPanelLogic _logic = new CommsPanelLogic();
        private readonly CommsCueLogic _cue = new CommsCueLogic();
        private readonly YawFollowLogic _yawFollow = new YawFollowLogic();

        private Transform? _root;
        private MeshRenderer? _panelRenderer;
        private MeshRenderer? _bezelRenderer;
        // 枠を左端から右へ開くために、幅と「開いていないときの左端」を覚えておく。
        private float _panelW, _bezelW;
        private int _charCount;
        private Material? _panelMat;
        private Material? _bezelMat;
        private Mesh? _panelMesh;
        private TMP_Text? _text;
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

        /// <summary>いまの段（テレメトリ用）。</summary>
        public CommsStage Stage => _logic.Stage;

        /// <summary>直近に書いた文字の不透明度（「画に出た」側の観測）。</summary>
        public float AppliedGlyph { get; private set; }

        /// <summary>直近に書いた枠の開き（0 = 畳まれている / 1 = 開き切り）。「画に出た」側の観測。</summary>
        public float AppliedOpen { get; private set; }

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
            // 打つ尺は文字数から決まる（文面を伸ばせば打つ時間も伸びる）。
            _logic.Begin(_charCount);
            LastNotice = notice;
            PulseCount++;
            Debug.Log($"[Comms] 上司からの連絡 {notice}「{TextFor(notice).Replace("\n", "／")}」"
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
            _root.position = head.position + dir * DistanceM;
            _root.rotation = Quaternion.LookRotation(_root.position - head.position, Vector3.up);
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
            Shader? flat = Shader.Find("Unlit/Color");
            if (flat != null)
            {
                _panelMesh = BuildQuad();
                // 縁（裏の一回り大きい面）。⚠ **地だけだと真っ黒の中で面が消える**
                //    （2026-08-15 の実機の画で、文字だけが宙に浮いていた）。
                _bezelW = PanelW + BezelM * 2f;
                _bezelRenderer = MakeQuad(rootGo.transform, "CommsBezelQuad",
                                          _bezelW, PanelH + BezelM * 2f, 0.014f,
                                          flat, RenderQueue - 1, out _bezelMat);
                // 地。暗い漆のような面。純黒だと「穴」に見え、明るいと掲示物に見える。
                _panelW = PanelW;
                _panelRenderer = MakeQuad(rootGo.transform, "CommsPanelQuad",
                                          _panelW, PanelH, 0.012f,
                                          flat, RenderQueue, out _panelMat);
            }

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
            rt.sizeDelta = new Vector2(PanelW * 0.92f / scale, PanelH * 0.85f / scale);
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
            tmp.transform.localPosition = new Vector3(0f, -ink.center.y * scale, 0f);
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
            float pa = Mathf.Clamp01(w.panel);
            // Unlit/Color は alpha を持たないので、明るさで濃さを出す（暗い場所なので十分）。
            if (_panelRenderer != null && _panelMat != null)
            {
                _panelMat.color = new Color(0.050f * pa, 0.042f * pa, 0.038f * pa, 1f);
                _panelRenderer.enabled = pa > 0.01f && AppliedOpen > 0.001f;
                SetOpen(_panelRenderer.transform, _panelW, AppliedOpen);
            }
            if (_bezelRenderer != null && _bezelMat != null)
            {
                // 縁は地より明るい。ここだけが「面がある」ことを伝える。
                _bezelMat.color = new Color(0.150f * pa, 0.110f * pa, 0.085f * pa, 1f);
                _bezelRenderer.enabled = pa > 0.01f && AppliedOpen > 0.001f;
                SetOpen(_bezelRenderer.transform, _bezelW, AppliedOpen);
            }
        }

        /// <summary>
        /// 枠を<b>左端を固定したまま</b>右へ開く（0 = 左端に畳まれている / 1 = 開き切り）。
        ///
        /// 面のメッシュは中心が原点（頂点 ±0.5）なので、幅を縮めると<b>両側から</b>縮む。
        /// 左端を残すには、縮めたぶんの半分だけ左へ寄せる ＝
        /// <c>x = -(w/2)(1-k)</c>、<c>scale.x = w·k</c>。これで左端は常に <c>-w/2</c> に居る。
        /// </summary>
        private static void SetOpen(Transform quad, float fullW, float k)
        {
            Vector3 s = quad.localScale;
            quad.localScale = new Vector3(fullW * k, s.y, s.z);
            Vector3 p = quad.localPosition;
            quad.localPosition = new Vector3(-(fullW * 0.5f) * (1f - k), p.y, p.z);
        }
    }
}
