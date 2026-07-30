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
                if (_aborted) return "いちど止めます。スタッフをお呼びください";

                if (_logic.Stage == IntroStage.Swap)
                    return _def.raiseHandPrompt ? "右手を上げてみてください" : string.Empty;

                // 演出が終わった直後の数秒だけ、歩き出す合図を出す（慣らし歩行の入口）。
                if (!_logic.Active && _walkPromptUntil > 0f && Time.unscaledTime < _walkPromptUntil)
                    return "そのまま歩いてみてください";

                // 段 0 は「何を待っているのか」を出す。開始位置を置いてあるのに位置合わせが
                // 済んでいないと、体験者がそこに立っても始まらない — 黙っていると原因が分からない。
                if (_logic.Stage == IntroStage.Black)
                {
                    if (showControl?.Layout?.ResolveStartSpot() == null) return string.Empty;   // スタッフ操作の運用
                    if (!IsCourseRegistered()) return "位置合わせがまだです（スタッフが始めます）";
                    return "スタート位置に立ってください";
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
            if (!_def.enabled)
            {
                // 演出なし。従来どおり最初からスクリーンだけが見える。
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

            if (!_logic.Active)
            {
                // 演出が終わった後は覆いを外したままにする（毎フレーム触らない）。
                return;
            }

            var ev = _logic.Tick(Time.unscaledDeltaTime, BuildInput());
            var w = _logic.Weights;
            veil?.Apply(w);
            // 段 3 の構造の線。show.json で部屋・カメラを個別に切れるので、
            // どちらも off なら重みを 0 にして畳む（線を出さない設定を「薄い線」にしない）。
            if (structureWire != null)
            {
                if (_def.showRoomWire || _def.showCameraMarks)
                {
                    var sw = w;
                    structureWire.Apply(sw);
                }
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
        /// 体験者が開始位置（<c>layout.startSpot</c>）に留まっているか。
        ///
        /// ⚠ <b>course 座標なので位置合わせが済んでいないと判定できない。</b> 未登録なら常に false を返し、
        /// スタッフ操作へ縮退する（<see cref="PromptText"/> がその理由を HMD 内で言う）。
        /// 黙って false を返すだけだと、現場で「立っても始まらない」が理由なしに起きる。
        /// </summary>
        private bool IsAtStartSpot()
        {
            var spot = showControl?.Layout?.ResolveStartSpot();
            if (spot == null || !IsCourseRegistered()) { _inSpotSec = 0f; return false; }
            var head2 = showControl?.HeadCourseXZProvider;
            if (head2 == null) { _inSpotSec = 0f; return false; }

            Vector2 p = head2();
            float r = spot.ResolveRadiusM();
            bool inside = (p - new Vector2(spot.x, spot.z)).sqrMagnitude <= r * r;
            // 通りすがりで始めない。少し留まってから。
            _inSpotSec = inside ? _inSpotSec + Time.unscaledDeltaTime : 0f;
            return _inSpotSec >= startSpotHoldSec;
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

        private void FinishIntro(bool restartClock)
        {
            veil?.SetHidden();
            structureWire?.SetHidden();
            glitch?.ResetAll();
            _logic.Disable();
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

        private void AbortIntro()
        {
            veil?.SetHidden();
            structureWire?.SetHidden();
            glitch?.ResetAll();
            _logic.Disable();
            // 体験者にも伝える。黙って本編の画に切り替わると、体験者は「始まった」と思って歩き出し、
            // 壁の位置が違う世界を手でたどることになる（計画 §9 の中止行が求めているのはこれ）。
            _aborted = true;
            Debug.LogWarning("[Intro] トラッキング原点が変わったので導入演出を中止しました — 位置合わせをやり直してください");
        }
    }
}
