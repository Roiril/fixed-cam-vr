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

        private readonly IntroLogic _logic = new IntroLogic();
        private ShowIntroDef _def = new ShowIntroDef();
        private float _sinceStart;
        private Quaternion _lastHeadRot = Quaternion.identity;
        private float _headTurn;
        private bool _subscribed;
        private bool _clockRestarted;

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
                if (_logic.Stage != IntroStage.Swap || !_def.raiseHandPrompt) return string.Empty;
                return "右手を上げてみてください";
            }
        }

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
                _subscribed = true;
                OnIntroDefChanged(runDirector.IntroDef);
                // 起動直後は Intro 相なので、そのまま演出を始める。
                if (runDirector.Phase == ShowPhase.Intro) BeginIntro();
                else _logic.Disable();
            }
        }

        private void OnDisable()
        {
            if (runDirector != null && _subscribed)
            {
                runDirector.PhaseChanged -= OnPhaseChanged;
                runDirector.IntroDefChanged -= OnIntroDefChanged;
            }
            _subscribed = false;
            // 自分が出した覆いを残して去らない（外したら従来どおり本編の見えになる）。
            _logic.Disable();
            veil?.SetHidden();
            glitch?.ResetAll();
        }

        private void ResolveRefs()
        {
            if (runDirector == null) runDirector = FindObjectOfType<ShowRunDirector>();
            if (veil == null) veil = FindObjectOfType<IntroVeil>();
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
                glitch?.ResetAll();
            }
        }

        private void BeginIntro()
        {
            _clockRestarted = false;
            if (!_def.enabled)
            {
                // 演出なし。従来どおり最初からスクリーンだけが見える。
                _logic.Disable();
                veil?.SetHidden();
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
            headTurnDegPerSec = _headTurn,
            frameCentered = IsScreenCentered(),
            liveFresh = IsLiveFresh(),
            recentered = showControl?.CourseNeedsReRegProvider?.Invoke() ?? false,
        };

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
            glitch?.ResetAll();
            _logic.Disable();
            if (!restartClock || _clockRestarted) return;
            _clockRestarted = true;
            // ここから慣らし歩行。企画書 3 章「固定視点による移動に慣れた後、追跡体験を開始する」。
            runDirector?.RestartIntroClock();
            Debug.Log("[Intro] 導入演出が終わり、慣らし歩行へ（固定視点に慣れる時間）");
        }

        private void AbortIntro()
        {
            veil?.SetHidden();
            glitch?.ResetAll();
            _logic.Disable();
            // 続行すると壁の位置が違う世界を見せることになる（体験者は壁を手でたどる）。
            Debug.LogWarning("[Intro] トラッキング原点が変わったので導入演出を中止しました — 位置合わせをやり直してください");
        }
    }
}
