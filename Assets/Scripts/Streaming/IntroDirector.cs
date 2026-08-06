#nullable enable

using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 導入演出（パススルー → 2D スクリーン）を回す実行体。
    /// 判定はすべて純ロジック <see cref="IntroLogic"/> にあり、ここは<b>観測と配布</b>だけを持つ。
    ///
    /// 設計の正本は <c>.claude/plans/2026-07-30_intro-passthrough-to-screen.md</c>。要点:
    ///   - <b><see cref="ShowPhase"/> を増やさない</b>。これは <see cref="ShowPhase.Intro"/> の内側の
    ///     サブ状態で、ゲート・終了判定・heartbeat・卓・シミュレータへの分岐を増やさない
    ///   - <b>視点は 1 度も動かさない</b>。動かすのは現実の側の身分（現実 → 映像）
    ///   - 演出が終わったら <see cref="ShowRunDirector.RestartIntroClock"/> を 1 回打つ。
    ///     こうしないと演出の秒数が <c>introMinSec</c> を食って慣らし歩行が短くなる
    ///
    /// パススルー自体の見た目（彩度・コントラスト・輪郭線）は <c>PassthroughStyler</c>
    /// （Assembly-CSharp 側）が <see cref="Weights"/> を読んで当てる。
    /// <b>Streaming asmdef から OVR を参照しない</b>という既存の規約を守るため、
    /// ハンドトラッキングの <c>ShowBodyInput</c> と同じ「向こうから読みに来る」形にしている。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class IntroDirector : MonoBehaviour
    {
        [Header("References (未配線でも実行時に自己解決する)")]
        [SerializeField] private ShowRunDirector? runDirector;
        [SerializeField] private IntroVeil? veil;
        [SerializeField] private IntroStructureWire? structureWire;
        [SerializeField] private GlitchFx? glitch;
        [SerializeField] private CameraStreamRegistry? registry;
        [SerializeField] private ShowControlClient? showControl;

        [Tooltip("頭の Transform（角速度の観測）。null なら CenterEyeAnchor を名前で探す。")]
        [SerializeField] private Transform? head;

        [Tooltip("本編のスクリーン（枠の中に来ているかの観測）。")]
        [SerializeField] private Transform? screenQuad;

        [Tooltip("起動時の黒が明けたとみなす秒数。StartupFader の最大待ち (4s) + フェード (0.5s) より長く。")]
        [SerializeField, Min(0f)] private float blackClearSec = 5f;

        [Tooltip("スクリーンが「視野中心にある」とみなす角度 (度)。")]
        [SerializeField, Range(5f, 60f)] private float centeredHalfAngleDeg = 25f;

        [Tooltip("フレームが「新鮮」とみなす経過 (秒)。これより古ければ段 5 へ進まない。")]
        [SerializeField, Min(0.1f)] private float freshFrameSec = 1.5f;

        [Tooltip("開始位置に留まったとみなす秒数。短いと通りすがりで始まる。")]
        [SerializeField, Min(0f)] private float startSpotHoldSec = 0.5f;

        private readonly IntroLogic _logic = new IntroLogic();
        private ShowIntroDef _def = new ShowIntroDef();
        private float _sinceStart;
        private Quaternion _lastHeadRot = Quaternion.identity;
        private float _headTurn;
        private bool _subscribed;
        private bool _clockRestarted;
        private float _inSpotSec;

        /// <summary>いまの重み。<c>PassthroughStyler</c> がここを読む。</summary>
        public IntroWeights Weights => _logic.Weights;

        /// <summary>いまの段（卓の heartbeat / StatusHud 用）。</summary>
        public IntroStage Stage => _logic.Stage;

        /// <summary>演出中か。</summary>
        public bool Active => _logic.Active;

        /// <summary>
        /// 導入を中止したか（トラッキング原点が変わって部屋の座標がずれた）。
        /// <b>本編へ自動で進ませないために公開する。</b>
        ///
        /// これが無いと中止が中止にならない。<see cref="AbortIntro"/> は <c>_logic.Disable()</c> するので
        /// <see cref="IntroLogic.Active"/>（<c>_stage != Off &amp;&amp; != Done</c>）が false へ落ち、
        /// <see cref="ShowRunDirector"/> が渡す introPlaying も false になる。
        /// <c>introMinSec</c> は起動から数えていて設営・待機でとうに過ぎており、体験者はスタート区間に
        /// 居る（そこで導入が始まったので）ため、<b>中止したその瞬間に「時間経過 ＋ スタート区間に居る」が
        /// 揃って本編へ飛ぶ</b>。ずれた座標系で 3 周が始まり、体験者は壁の位置が違う世界を手でたどる。
        /// しかも <see cref="PromptText"/> は相で門を閉じているので、飛んだ瞬間に警告まで消える。
        ///
        /// 落ちるのは <see cref="BeginIntro"/> だけ（＝ランリセット / 位置合わせのやり直し）。
        /// </summary>
        public bool Aborted => _aborted;

        /// <summary>
        /// 導入演出が<b>終わったか</b>（＝本編へ進んでよいか）。演出を出さない設定では最初から true。
        ///
        /// ⚠ <see cref="Active"/> の否定では代用できない。<b>段 0（開始待ち）でも Active は false</b> なので、
        /// 「進行中でない」を条件にすると<b>演出が始まる前に本編へ飛べてしまう</b>。慣らし歩行
        /// （<c>introMinSec</c>）がその猶予を兼ねていたため露見していなかったが、慣らしを 0 にした
        /// 瞬間に演出が 1 度も出なくなる（2026-08-06 に慣らしを外す判断が出て発覚）。
        ///
        /// 落ちるのは <see cref="BeginIntro"/>、立つのは <see cref="FinishIntro"/>。
        /// 中止は別軸（<see cref="Aborted"/>）で、こちらは立たない ＝ 中止した演出は「終わった」ではない。
        /// </summary>
        public bool Completed => _completed;

        /// <summary>条件待ちで足踏みしているか（スタッフが手で送れることを卓に出す）。</summary>
        public bool Holding => _logic.Holding;

        /// <summary>輪郭線の色（<c>PassthroughStyler</c> が読む）。</summary>
        public Color EdgeColor => _def.ResolveEdgeColor();

        /// <summary>
        /// HMD 内に出す合図。<b>段 5 の「右手を上げて」だけ</b>で、これが 3 周目の反転の伏線になる
        /// （画面の中の自分は上げるが、3 周目の背景は 1 周目の録画なので上がらない）。
        /// StatusHud が読む（Diagnostics への参照を作らないプロバイダ方式）。
        /// </summary>
        public string PromptText
        {
            get
            {
                // 中止は最優先。体験者が歩き出す前に止める。
                // ⚠ ただし**導入相の間だけ**。ShowRunLogic は中止と無関係に Intro → Run へ進むので、
                // 相を見ないと本編 3 周のあいだ顔の前に警告が浮きっぱなしになり、終了の暗転にも
                // 重なって残る（IntroPrompt は queue 5100 で ShowEndingFader の黒より手前）。
                // `_aborted` を落とすのは BeginIntro だけで、中止の原因（要再登録フラグ）は
                // sticky なので、フラグ側では消えない。
                // ⚠ 中止のメッセージはここに置かない。この面は視線前方 1.5m で、視界を閉じる
                // ShowEndingFader の黒は 0.3m ＝ **黒が手前に来て文字を隠す**（Canvas は深度で解決する）。
                // 「黒で閉じる」と「待ってよいと伝える」は同じ面が持つのが正しいので、中止時の 1 行は
                // ShowRunDirector.BlackoutMessage → ShowEndingFader が黒の上に描く。
                //
                // 理由を体験者に説明しないのは、**取れる手が 1 つも無い相手に伝えても減るのは不安ではなく
                // 没入**だから。旧文言「いちど止めます。スタッフをお呼びください」は、被っていて誰がどこに
                // 居るかも見えない相手に、人を呼ぶ役まで振っていた。スタッフ向けの復帰手順は StatusHud。

                // 「〜てみてください」の試行の含みを取る。上げた自分が画面に居ることが体験の内容
                // そのものなので、依頼文でも操作説明にならない（3 周目に手が上がらない反転の伏線）。
                if (_logic.Stage == IntroStage.Swap)
                    return _def.raiseHandPrompt ? "右手をあげてください" : string.Empty;

                // 演出が終わった直後の数秒だけ、歩き出す合図を出す（慣らし歩行の入口）。
                if (!_logic.Active && _walkPromptUntil > 0f && Time.unscaledTime < _walkPromptUntil)
                    return "歩いてください";

                // 段 0 は「何を待っているのか」を出す。ただし**体験者に手立てが無いことは出さない** —
                // 位置合わせが未了なのは機器側の不備で、読んでも体験者には何もできない（旧文言
                // 「位置合わせがまだです（スタッフが始めます）」は体験者への業務連絡になっていた）。
                // スタッフは StatusHud と卓の本番前チェックで気づく。
                if (_logic.Stage == IntroStage.Black)
                {
                    if (!IsCourseRegistered()) return string.Empty;
                    // 始まり方に合わせて言う。線なら歩いて入ってくる動きのまま始まるので「進む」、
                    // 円なら床の実物を指して「印に立つ」。旧文言「スタート位置に立ってください」は
                    // 線方式で誤り（立つ場所が無い）で、かつ「スタート位置」が現場の何を指すか
                    // 画面から分からなかった。
                    if (!string.IsNullOrEmpty(_def.startLineId)) return "そのまま前へ進んでください";
                    return showControl?.Layout?.ResolveStartSpot() != null ? "床の印に立ってください" : string.Empty;
                }
                return string.Empty;
            }
        }

        /// <summary>継ぎ目で「歩いてみてください」を出す秒数（慣らし歩行の 20 秒から借りる）。</summary>
        private const float WalkPromptSec = 4f;

        private float _walkPromptUntil = -1f;
        private bool _aborted;

        private void Awake()
        {
            ResolveRefs();
            _logic.Configure(_def.ToTiming());
        }

        private void OnEnable()
        {
            ResolveRefs();
            if (runDirector != null && !_subscribed)
            {
                runDirector.PhaseChanged += OnPhaseChanged;
                runDirector.IntroDefChanged += OnIntroDefChanged;
                runDirector.RunRestarted += OnRunRestarted;
                _subscribed = true;
                OnIntroDefChanged(runDirector.IntroDef);
                // 起動直後は Intro 相なので、そのまま演出を始める。
                if (runDirector.Phase == ShowPhase.Intro) BeginIntro();
                else _logic.Disable();
            }
        }

        // ▶ ラン開始（体験者交代）。相が Intro → Intro だと PhaseChanged が発火しないので、
        // ここで武装し直さないと次の体験者に演出が出ない。
        private void OnRunRestarted()
        {
            if (runDirector != null && runDirector.Phase == ShowPhase.Intro) BeginIntro();
        }

        private void OnDisable()
        {
            if (runDirector != null && _subscribed)
            {
                runDirector.PhaseChanged -= OnPhaseChanged;
                runDirector.IntroDefChanged -= OnIntroDefChanged;
                runDirector.RunRestarted -= OnRunRestarted;
            }
            _subscribed = false;
            // 自分が出した覆いを残して去らない（外したら従来どおり本編の見えになる）。
            _logic.Disable();
            veil?.SetHidden();
            structureWire?.SetHidden();
            glitch?.ResetAll();
        }

        private void ResolveRefs()
        {
            if (runDirector == null) runDirector = FindObjectOfType<ShowRunDirector>();
            if (veil == null) veil = FindObjectOfType<IntroVeil>();
            if (structureWire == null) structureWire = FindObjectOfType<IntroStructureWire>();
            if (glitch == null) glitch = FindObjectOfType<GlitchFx>();
            if (registry == null) registry = FindObjectOfType<CameraStreamRegistry>();
            if (showControl == null) showControl = FindObjectOfType<ShowControlClient>();
            if (head == null)
            {
                var anchor = GameObject.Find("CenterEyeAnchor");
                if (anchor != null) head = anchor.transform;
            }
        }

        private void OnIntroDefChanged(ShowIntroDef def)
        {
            _def = def ?? new ShowIntroDef();
            _logic.Configure(_def.ToTiming());
            // 走行中に「導入を出さない」へ変わったら畳む（卓から切れるようにしておく）。
            if (!_def.enabled && _logic.Active) FinishIntro(restartClock: false);
        }

        private void OnPhaseChanged(ShowPhase phase)
        {
            if (phase == ShowPhase.Intro) BeginIntro();
            else
            {
                _logic.Disable();
                veil?.SetHidden();
                structureWire?.SetHidden();
                glitch?.ResetAll();
            }
        }

        private void BeginIntro()
        {
            _clockRestarted = false;
            _walkPromptUntil = -1f;
            _aborted = false;
            _completed = false;
            if (!_def.enabled)
            {
                // 演出なし。従来どおり最初からスクリーンだけが見える。
                // **待つものが無いので「終わった」扱いにする** — でないと本編へ永久に進めない。
                _completed = true;
                _logic.Disable();
                veil?.SetHidden();
                structureWire?.SetHidden();
                return;
            }
            _logic.Begin();
            if (head != null) _lastHeadRot = head.rotation;
            _headTurn = 0f;
        }

        /// <summary>いまの段を今すぐ終える（卓 / 現地のスタッフ操作）。段 0 では「始める」の合図。</summary>
        public void RequestAdvanceStage() => _logic.RequestAdvance();

        /// <summary>演出を全部飛ばす（スタッフ操作）。</summary>
        public void RequestSkip() => _logic.RequestSkip();

        /// <summary>HMD を被り直された等でやり直す（段 1 から）。</summary>
        public void RequestRestart()
        {
            if (!_def.enabled) return;
            _clockRestarted = false;
            _logic.Restart();
        }

        private void Update()
        {
            _sinceStart += Time.unscaledDeltaTime;
            ObserveHead();

            // 中止中はここで折り返す。復帰は位置合わせの確定 1 つで済ませる（TryRecoverFromAbort）。
            if (_aborted) { TryRecoverFromAbort(); return; }

            if (!_logic.Active)
            {
                // 演出が終わった後は覆いを外したままにする（毎フレーム触らない）。
                return;
            }

            var ev = _logic.Tick(Time.unscaledDeltaTime, BuildInput());
            var w = _logic.Weights;
            veil?.Apply(w);
            // 段 3 の構造の線。show.json で部屋・カメラを個別に切れる。
            // ⚠ **個別のフラグを実行体へ渡すこと**（2026-08-01 修正）。旧実装は OR で
            //    「Apply を呼ぶか」だけを決めていて、壁だけ切る / 印だけ切るができなかった。
            if (structureWire != null)
            {
                structureWire.SetSources(_def.showRoomWire, _def.showCameraMarks);
                if (_def.showRoomWire || _def.showCameraMarks) structureWire.Apply(w);
                else structureWire.SetHidden();
            }
            // 段 5 の乱れはスクリーン内にも掛ける（継ぎ目は両側で隠す）。
            glitch?.SetSustain(w.glitch * Mathf.Clamp01(_def.glitchOnSwap));

            if (ev == IntroEvent.Finished) FinishIntro(restartClock: true);
            else if (ev == IntroEvent.Aborted) AbortIntro();
        }

        private void ObserveHead()
        {
            if (head == null) { _headTurn = 0f; return; }
            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f) return;
            float deg = Quaternion.Angle(_lastHeadRot, head.rotation);
            _lastHeadRot = head.rotation;
            // 生の差分は震えるので緩く平滑化する（閾値の前後でばたつかせない）。
            float inst = deg / dt;
            _headTurn = Mathf.Lerp(_headTurn, inst, 1f - Mathf.Exp(-8f * dt));
        }

        private IntroInput BuildInput() => new IntroInput
        {
            blackCleared = _sinceStart >= blackClearSec,
            atStartSpot = IsAtStartSpot(),
            headTurnDegPerSec = _headTurn,
            frameCentered = IsScreenCentered(),
            liveFresh = IsLiveFresh(),
            recentered = showControl?.CourseNeedsReRegProvider?.Invoke() ?? false,
        };

        /// <summary>
        /// 導入を始める合図が来たか。<b>HMD を被っていることが前提</b>で、その上で
        /// <c>run.intro.startLineId</c> があれば**そのラインを横切ったら**、無ければ従来どおり
        /// <c>layout.startSpot</c> の円に留まったら true になる。
        ///
        /// ⚠ <b>course 座標なので位置合わせが済んでいないと判定できない。</b> 未登録なら常に false を返し、
        /// スタッフ操作へ縮退する（<see cref="PromptText"/> がその理由を HMD 内で言う）。
        /// 黙って false を返すだけだと、現場で「立っても始まらない」が理由なしに起きる。
        /// </summary>
        private bool IsAtStartSpot()
        {
            // 置いてある HMD が位置条件をたまたま満たして勝手に始まるのを防ぐ。
            // 被り直すまでラッチも落とす（前の体験者が踏んだ線で次が始まらない）。
            if (!IsUserPresent()) { _inSpotSec = 0f; _startLineCrossed = false; return false; }
            if (!IsCourseRegistered()) { _inSpotSec = 0f; return false; }
            var head2 = showControl?.HeadCourseXZProvider;
            if (head2 == null) { _inSpotSec = 0f; return false; }
            Vector2 p = head2();

            // ライン指定があればそちらが正（円は見ない）。
            string lineId = _def.startLineId ?? "";
            if (!string.IsNullOrEmpty(lineId))
            {
                SyncStartLine(lineId);
                _startLine.Tick(Time.unscaledTime, p.x, p.y, Time.unscaledDeltaTime);
                LineCrossLogic.State[] st = _startLine.StateView;
                // 横切ったら以後ずっと true（段 0 を抜けるまで保持する）。横断は事象なので、
                // その 1 フレームを取り逃すと永久に始まらない。
                if (st.Length > 0 && st[0].crossed) _startLineCrossed = true;
                return _startLineCrossed;
            }

            var spot = showControl?.Layout?.ResolveStartSpot();
            if (spot == null) { _inSpotSec = 0f; return false; }
            float r = spot.ResolveRadiusM();
            bool inside = (p - new Vector2(spot.x, spot.z)).sqrMagnitude <= r * r;
            // 通りすがりで始めない。少し留まってから。
            _inSpotSec = inside ? _inSpotSec + Time.unscaledDeltaTime : 0f;
            return _inSpotSec >= startSpotHoldSec;
        }

        private bool IsUserPresent()
        {
            var f = showControl?.UserPresentProvider;
            return f == null || f();      // provider が無い環境（Editor 等）では邪魔しない
        }

        // --- 開始ライン（run.intro.startLineId → layout.lines[] の 1 本）---
        private readonly LineCrossLogic _startLine = new LineCrossLogic();
        private string _startLineApplied = " ";   // 未同期を表す番兵（"" は「指定なし」と区別する）
        private bool _startLineCrossed;

        private void SyncStartLine(string lineId)
        {
            if (lineId == _startLineApplied) return;
            _startLineApplied = lineId;
            _startLineCrossed = false;

            LineCrossLogic.Line line = LineCrossLogic.Line.Undefined;
            ShowLineDef[]? defs = showControl?.Layout?.lines;
            if (defs != null)
            {
                foreach (ShowLineDef? d in defs)
                {
                    if (d == null || d.id != lineId) continue;
                    int dir = LineCrossLogic.ParseDir(d.dir, out _);
                    line = LineCrossLogic.Line.Between(d.x1, d.z1, d.x2, d.z2, dir, d.camera);
                    break;
                }
            }
            if (!line.defined)
                Debug.LogWarning($"[Intro] 開始ライン '{lineId}' が layout.lines に無い — " +
                                 $"スタッフ操作でしか導入を始められない");
            _startLine.SetLines(new[] { line });
        }

        private bool IsCourseRegistered()
        {
            var f = showControl?.CourseRegisteredProvider;
            return f == null || f();      // provider が無い環境では邪魔しない
        }

        /// <summary>
        /// 枠の中に本編のスクリーンが来ているか。<b>枠は head-lock なので常に正面</b>で、
        /// 見るべきは「スクリーンの側が正面に来ているか」（スクリーンは yaw を緩く追従する）。
        /// </summary>
        private bool IsScreenCentered()
        {
            if (head == null || screenQuad == null) return true;   // 判定できないなら邪魔しない
            Vector3 to = screenQuad.position - head.position;
            if (to.sqrMagnitude < 1e-4f) return true;
            return Vector3.Angle(head.forward, to) <= centeredHalfAngleDeg;
        }

        /// <summary>
        /// 段 4（枠）が段 5（すり替え）へ進むための 2 条件。テレメトリ・診断用。
        /// **どちらが false で足踏みしているのかはログでしか分からない** —
        /// 実機で「枠までは出るのに映像へ変わらない」を追うときの唯一の手掛かりになる。
        /// </summary>
        public bool LiveFresh => IsLiveFresh();

        /// <inheritdoc cref="LiveFresh"/>
        public bool FrameCentered => IsScreenCentered();

        /// <summary>いま映すカメラのフレームが新鮮に届いているか（砂嵐を見せないための条件）。</summary>
        private bool IsLiveFresh()
        {
            if (registry == null) return false;
            var s = registry.GetActive();
            if (s == null || !s.IsConnected) return false;
            float last = s.LastFrameRealtime;
            if (last <= 0f) return false;
            return Time.realtimeSinceStartup - last <= freshFrameSec;
        }

        /// <summary>演出が完走したか。<see cref="Completed"/> の実体。</summary>
        private bool _completed;

        private void FinishIntro(bool restartClock)
        {
            veil?.SetHidden();
            structureWire?.SetHidden();
            glitch?.ResetAll();
            _logic.Disable();
            // ここで初めて本編へ進んでよくなる（段 0 と区別できる唯一の点）。
            _completed = true;
            if (!restartClock || _clockRestarted) return;
            _clockRestarted = true;
            // 演出は終わったが Intro 相（慣らし歩行）は続く。**ここで合図を切らない** —
            // 「現実が映像になった」直後に何をすればいいか分からないまま立ち尽くす時間が生まれる
            // （2026-07-30 の設計批評: 継ぎ目に歩き出す合図が 1 つも無い）。
            _walkPromptUntil = Time.unscaledTime + WalkPromptSec;
            // ここから慣らし歩行。企画書 3 章「固定視点による移動に慣れた後、追跡体験を開始する」。
            runDirector?.RestartIntroClock();
            Debug.Log("[Intro] 導入演出が終わり、慣らし歩行へ（固定視点に慣れる時間）");
        }

        /// <summary>
        /// 中止した時点の位置合わせの保存時刻。<b>これが変わったら（＝スタッフが B で確定したら）
        /// 導入をやり直す。</b>
        /// </summary>
        private string _abortStamp = "";

        private void AbortIntro()
        {
            veil?.SetHidden();
            structureWire?.SetHidden();
            glitch?.ResetAll();
            _logic.Disable();
            // 視界は ShowRunDirector.ShouldBlackout → ShowEndingFader の黒が閉じる。黙って本編の画に
            // 切り替わると、体験者は「始まった」と思って歩き出し、壁の位置が違う世界を手でたどる。
            _aborted = true;
            _abortStamp = showControl?.CourseRegistrationStampProvider?.Invoke() ?? "";
            Debug.LogWarning("[Intro] トラッキング原点が変わったので導入演出を中止しました — 位置合わせをやり直してください");
        }

        /// <summary>
        /// 位置合わせが確定し直されたら導入をやり直す（中止からの<b>1 段</b>復帰）。
        ///
        /// これが無いと復帰は「① 位置合わせを撃ち直す ② ランリセット」の 2 段で、**順序を逆にすると
        /// 直らない**（先にランリセットしても要再登録フラグが立ったままで即また中止される）。
        /// 現場のスタッフに順序を暗記させる代わりに、確定という事象を検出して構造で 1 段にする。
        ///
        /// ⚠ 要再登録フラグの false 化では代用できない。プレビュー（<c>SetRegistration(save:false)</c>）
        /// でもフラグは降りるので、B 確定の前に再開して登録ビューと演出が混ざる。
        /// </summary>
        private void TryRecoverFromAbort()
        {
            // 明示操作で本編へ進めた後は、やり直さない（スタッフがそう決めたということ）。
            if (runDirector != null && runDirector.Phase != ShowPhase.Intro) return;
            string stamp = showControl?.CourseRegistrationStampProvider?.Invoke() ?? "";
            if (stamp == _abortStamp) return;
            Debug.Log("[Intro] 位置合わせが直ったので導入演出をやり直します");
            BeginIntro();
        }
    }
}
