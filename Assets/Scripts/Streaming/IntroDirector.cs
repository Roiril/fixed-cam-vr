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
        [SerializeField] private ContainmentShell? shell;
        [SerializeField] private SealedBox? sealedBox;
        [SerializeField] private GlitchFx? glitch;
        [SerializeField] private CameraStreamRegistry? registry;
        [SerializeField] private ShowControlClient? showControl;

        [Tooltip("頭の Transform（角速度の観測）。null なら CenterEyeAnchor を名前で探す。")]
        [SerializeField] private Transform? head;

        [Tooltip("本編のスクリーン（枠の中に来ているかの観測）。")]
        [SerializeField] private Transform? screenQuad;

        [Tooltip("スクリーンの実体。管の点灯（_CrtIgnite）を書く先の材質をここから取る。null なら実行時に探す。")]
        [SerializeField] private MjpegScreen? screen;

        [Tooltip("起動時の黒が明けたとみなす秒数。StartupFader の最大待ち (4s) + フェード (0.5s) より長く。")]
        [SerializeField, Min(0f)] private float blackClearSec = 5f;

        [Tooltip("スクリーンが「視野中心にある」とみなす角度 (度)。")]
        [SerializeField, Range(5f, 60f)] private float centeredHalfAngleDeg = 25f;

        [Tooltip("フレームが「新鮮」とみなす経過 (秒)。これより古ければ段 4 へ進まない。")]
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

        /// <summary>
        /// <b>実際にスクリーンの材質へ書いた管の点灯量</b>（<c>_CrtIgnite</c>）。
        /// 書く先を掴めていなければ <b>-1</b>。
        ///
        /// 「重みが動いた」ではなく「画に出た」の側の観測（<c>rules/streaming.md</c> の
        /// 「状態が進んだではなく効果が出た」）。<c>ShowTelemetryHost</c> が読む。
        /// </summary>
        public float IgniteWritten => _screenMat != null ? _igniteWritten : -1f;

        /// <summary>輪郭線の色（<c>PassthroughStyler</c> が読む）。</summary>
        public Color EdgeColor => _def.ResolveEdgeColor();

        // ⚠⚠ **導入の合図（`IntroPrompt` の 1 行）は 2026-08-13 に廃止した**
        //    （`canon/LEDGER.md` 0033「前進してください見たいな文字は無しで」）。
        //    段 4 の「右手をあげてください」は 3 周目の反転の伏線なので、**被せる前にスタッフが
        //    口で伝える**（`docs/onsite-checklist.md`）。伝え忘れると反転が丸ごと不成立になる。
        //    ⚠ 文言を復活させるなら `unity.ps1 menu hud-font` の収集元へ
        //    `IntroDirector.cs` を戻すこと（外してある）。

        private bool _aborted;

        // --- スクリーンの管の点灯（_CrtIgnite）------------------------------------
        // ⚠⚠ **既定は 1（点いている）。** 0 を書いたままにすると画がまるごと消えるので、
        //    演出を出していない全期間（終わった / 中止した / 無効の設定 / 位置合わせ中 / 相が変わった）で
        //    必ず 1 を書き戻す。「書くのをやめる」だけでは最後に書いた 0 が残る。
        private static readonly int CrtIgniteId = Shader.PropertyToID("_CrtIgnite");
        private static readonly int IntroLiveId = Shader.PropertyToID("_IntroLive");
        private Material? _screenMat;
        private bool _warnedNoScreenMat;
        private float _igniteWritten = -1f;
        private float _liveWritten = -1f;

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
            shell?.SetHidden();
            sealedBox?.SetHidden();
            glitch?.ResetAll();
            ResetIgnite();
        }

        private void ResolveRefs()
        {
            if (runDirector == null) runDirector = FindObjectOfType<ShowRunDirector>();
            if (veil == null) veil = FindObjectOfType<IntroVeil>();
            if (structureWire == null) structureWire = FindObjectOfType<IntroStructureWire>();
            if (shell == null) shell = FindObjectOfType<ContainmentShell>();
            if (sealedBox == null) sealedBox = FindObjectOfType<SealedBox>();
            if (glitch == null) glitch = FindObjectOfType<GlitchFx>();
            if (registry == null) registry = FindObjectOfType<CameraStreamRegistry>();
            if (showControl == null) showControl = FindObjectOfType<ShowControlClient>();
            if (screen == null) screen = FindObjectOfType<MjpegScreen>();
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
                shell?.SetHidden();
                sealedBox?.SetHidden();
                glitch?.ResetAll();
                ResetIgnite();
            }
        }

        private void BeginIntro()
        {
            _clockRestarted = false;
            _aborted = false;
            _completed = false;
            // ⚠ **開始の合図を必ず武装し直す**（2026-08-09 実害）。旧実装はここで
            // `_startLineCrossed` も円の滞在も落としていなかったので、**前の体験者が踏んだ線・
            // 立った円がそのまま次のランへ持ち越され、リセットした瞬間に演出が走り出した**
            // （ユーザー報告「今はすぐに起動してしまう。最初からなのか、リセットしたあとからなのかは
            // 覚えてない」）。ラッチは 1 箇所で落とす。
            RearmStartSignal();
            if (!_def.enabled)
            {
                // 演出なし。従来どおり最初からスクリーンだけが見える。
                // **待つものが無いので「終わった」扱いにする** — でないと本編へ永久に進めない。
                _completed = true;
                _logic.Disable();
                veil?.SetHidden();
                structureWire?.SetHidden();
                shell?.SetHidden();
                sealedBox?.SetHidden();
                // 演出を出さない設定でも管は点いていなければならない（既定は 1）。
                ResetIgnite();
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

            // ⚠ **位置合わせ中は演出を止め、覆いを完全にどける。**
            // 覆いは queue 4900 の全画面 `Blend Zero SrcAlpha`（結果 = srcAlpha × 背景）なので、
            // 走っているだけで**先に描かれた登録ワイヤーと文字（queue 3000）を黒へ潰す**。
            // 位置合わせは現実に線を重ねて合わせる作業なので、これでは作業そのものが成立しない
            // （2026-08-09 ユーザー報告「位置合わせをしたかったり、ステータスやコントローラーの説明が
            //  パススルーに重なって何も見えない」）。現実は `PassthroughStyler` がカメラ背景の
            // alpha を 0 にして出す（覆いは使わない — 使うとこの潰しが戻ってくる）。
            bool registering = showControl?.CourseRegistrationActive ?? false;
            if (registering)
            {
                veil?.SetHidden();
                structureWire?.SetHidden();
                shell?.SetHidden();
                sealedBox?.SetHidden();
                glitch?.SetSustain(0f);
                // 位置合わせ中は素の画に戻す（管を消したままだとスクリーンが真っ黒になる）。
                ResetIgnite();
                // 位置合わせが終われば course 変換そのものが変わる。**前の座標系で満たした合図は無効**
                // なので、抜けた時点で必ず武装し直す（下の !_wasRegistering で 1 回だけ）。
                _wasRegistering = true;
                return;
            }
            if (_wasRegistering)
            {
                _wasRegistering = false;
                RearmStartSignal();
            }

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
            // 隔離殻（会場を黒で落とし、実物の壁と足元の床だけを残す）。覆いの**後**に描かれる面なので、
            // 覆いが枠を閉じた後も枠の中の現実に効き続ける（重み側で passthrough に連動して引く）。
            shell?.Apply(w);
            // 封印の箱（外から見た隔離）。段 0 で体験エリアの外に居るあいだだけ出る。
            sealedBox?.Apply(w);
            // 段 3 の構造の線。show.json で部屋・カメラを個別に切れる。
            // ⚠ **個別のフラグを実行体へ渡すこと**（2026-08-01 修正）。旧実装は OR で
            //    「Apply を呼ぶか」だけを決めていて、壁だけ切る / 印だけ切るができなかった。
            if (structureWire != null)
            {
                structureWire.SetSources(_def.showRoomWire, _def.showCameraMarks);
                if (_def.showRoomWire || _def.showCameraMarks) structureWire.Apply(w);
                else structureWire.SetHidden();
            }
            // 段 4 の乱れはスクリーン内にも掛ける（継ぎ目は両側で隠す）。
            glitch?.SetSustain(w.glitch * Mathf.Clamp01(_def.glitchOnSwap));
            // スクリーンの管の点灯（段 3 で 0 → 1）。書く先は MjpegScreen の材質。
            WriteIgnite(w.ignite, w.live);

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
            outsideBoxM = OutsideBoxM(),
        };

        /// <summary>
        /// 体験エリア（隔離の footprint）の外側までの距離 (m)。封印の箱の濃さがこれで決まる。
        ///
        /// ⚠ <b>解けないときは 0（＝中に居る扱い）</b>。未登録・layout 未着で「外に居る」と答えると、
        /// 現実の見当違いの所に黒い箱が立つ。出さない側へ倒す。
        /// </summary>
        private float OutsideBoxM()
        {
            if (!IsCourseRegistered()) return 0f;
            var head2 = showControl?.HeadCourseXZProvider;
            if (head2 == null) return 0f;
            if (!ContainmentShellLogic.TryFootprint(showControl?.Layout, showControl?.Room,
                                                    out Vector2 half)) return 0f;
            return ContainmentShellLogic.DistanceOutsideM(head2(), half);
        }

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
            if (!IsUserPresent()) { RearmStartSignal(); return false; }
            if (!IsCourseRegistered()) { _startSpot.NotifyUnavailable(); return false; }
            var head2 = showControl?.HeadCourseXZProvider;
            if (head2 == null) { _startSpot.NotifyUnavailable(); return false; }
            Vector2 p = head2();

            // ⚠ **体験エリアへ近づいたら始まる**（canon/LEDGER.md 0005）。導入は箱の外で流れるので、
            //    中に引いた通過ラインでは「踏んだ時にはもう中に居る」ことになって成立しない。
            //    footprint が解ける限りこちらが正で、線・円は解けないときの縮退。
            if (ContainmentShellLogic.TryFootprint(showControl?.Layout, showControl?.Room,
                                                   out Vector2 halfXZ))
            {
                if (!_warnedApproachOverridesLine && !string.IsNullOrEmpty(_def.startLineId))
                {
                    _warnedApproachOverridesLine = true;
                    Debug.Log($"[Intro] 開始は体験エリアへの接近で判定します" +
                              $"（run.intro.startLineId '{_def.startLineId}' は使いません）");
                }
                _startsByApproach = true;
                float outsideM = ContainmentShellLogic.DistanceOutsideM(p, halfXZ);
                bool fired = _approach.Tick(outsideM, IntroLogic.ApproachNearM, IntroLogic.ApproachHoldSec,
                                            Time.unscaledDeltaTime, valid: true);
                // ⚠ **黙って始まらない状態を作らない。** 武装は「離れている」か「近づいてきた」で立つので、
                //    近くに立ったまま動かないと待ち続ける。理由を 1 回だけ、状況で書き分けて名指しする。
                if (!fired && !_approach.Armed)
                {
                    _approachWaitSec += Time.unscaledDeltaTime;
                    if (_approachWaitSec > 8f && !_warnedApproachNotArmed)
                    {
                        _warnedApproachNotArmed = true;
                        Debug.LogWarning(
                            outsideM <= 0.01f
                                ? "[Intro] 体験エリアの中に立ったままなので開始待ちです。" +
                                  "いちど外へ出て、歩いて入り直すと始まります。スタッフの ⏭ でも進められます"
                                : $"[Intro] 近づいてくる動きが観測できていません（いま外へ {outsideM:F2}m / " +
                                  $"いちばん遠かったのが {_approach.MaxSeenM:F2}m）。" +
                                  $"{ApproachLogic.ApproachDeltaM:F2}m 以上離れた所から歩いてくるか、" +
                                  $"外へ {IntroLogic.ApproachNearM + ApproachLogic.ArmMarginM:F2}m 以上" +
                                  "離れると始まります。スタッフの ⏭ でも進められます");
                    }
                }
                else
                {
                    _approachWaitSec = 0f;
                }
                return fired;
            }
            _startsByApproach = false;

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
            if (spot == null) { _startSpot.NotifyUnavailable(); return false; }
            // ⚠ **円は「入ってきた」で判定する**（状態ではなく事象）。判断は StartSpotLogic 側。
            return _startSpot.Tick(p.x, p.y, spot.x, spot.z, spot.ResolveRadiusM(),
                                   startSpotHoldSec, Time.unscaledDeltaTime);
        }

        /// <summary>
        /// 開始の合図（線の横断ラッチ・円の滞在）を武装し直す。<b>落とし所はここ 1 箇所だけ</b> —
        /// 散らすと必ずどれかを落とし忘れ、前の体験者の条件が次のランへ漏れる。
        /// </summary>
        private void RearmStartSignal()
        {
            _startLineCrossed = false;
            _startLine.Reset();
            _startSpot.Rearm();
            _approach.Rearm();
            _warnedApproachNotArmed = false;
            _approachWaitSec = 0f;
        }

        private bool IsUserPresent()
        {
            var f = showControl?.UserPresentProvider;
            return f == null || f();      // provider が無い環境（Editor 等）では邪魔しない
        }

        // --- 開始ライン（run.intro.startLineId → layout.lines[] の 1 本）---
        private readonly LineCrossLogic _startLine = new LineCrossLogic();
        private readonly StartSpotLogic _startSpot = new StartSpotLogic();
        private readonly ApproachLogic _approach = new ApproachLogic();
        private bool _warnedApproachOverridesLine;
        private bool _warnedApproachNotArmed;
        private float _approachWaitSec;
        /// <summary>いま開始を判定しているのが「体験エリアへの接近」か（<see cref="PromptText"/> が読む）。</summary>
        private bool _startsByApproach;
        private bool _wasRegistering;
        private bool _startLineResolved;          // layout.lines から実際に引けたか
        private bool _startLineWarned;            // 警告は 1 回だけ（未着なら毎フレーム引き直すので）
        private string _startLineApplied = " ";   // 未同期を表す番兵（"" は「指定なし」と区別する）
        private bool _startLineCrossed;

        private void SyncStartLine(string lineId)
        {
            // ⚠ **解けていないなら毎フレーム引き直す。** id が同じなら二度と解かない旧実装だと、
            // 最初に評価した時点で `layout.lines` がまだ届いていない（起動直後・卓の long-poll 前）
            // 場合に線が Undefined のまま固定され、**以後スタッフ操作でしか導入を始められなくなる**。
            // 解けた後は id 一致で即 return するので、走査は「まだ解けていない間」だけ走る。
            if (lineId == _startLineApplied && _startLineResolved) return;
            bool sameId = lineId == _startLineApplied;
            _startLineApplied = lineId;
            if (!sameId) _startLineCrossed = false;

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
            _startLineResolved = line.defined;
            if (!line.defined && !_startLineWarned)
            {
                _startLineWarned = true;
                Debug.LogWarning($"[Intro] 開始ライン '{lineId}' が layout.lines に無い — " +
                                 $"届くまではスタッフ操作でしか導入を始められない");
            }
            _startLine.SetLines(new[] { line });
        }

        private bool IsCourseRegistered()
        {
            var f = showControl?.CourseRegisteredProvider;
            return f == null || f();      // provider が無い環境では邪魔しない
        }

        /// <summary>
        /// 管の点灯を書く先（<c>MjpegScreen</c> の Renderer のマテリアル）。
        /// <b><see cref="CameraFeelFx"/> が掴んでいるのと同じ材質</b>で、あちらと同じ流儀で取る
        /// （<c>Renderer.material</c> は 1 度実体化したらその後は同じ実体が返る）。
        ///
        /// ⚠ <b>掴めなかったときは何も書かない。</b> 0 を書いて画を消す方向へ倒さない。
        /// </summary>
        private Material? ResolveScreenMaterial()
        {
            if (_screenMat != null) return _screenMat;
            if (screen == null) screen = FindObjectOfType<MjpegScreen>();
            var r = screen != null ? screen.GetComponent<Renderer>() : null;
            _screenMat = r != null ? r.material : null;
            if (_screenMat == null && !_warnedNoScreenMat)
            {
                _warnedNoScreenMat = true;
                Debug.LogWarning("[Intro] スクリーンの材質を掴めない — 管の点灯（_CrtIgnite）を書けない"
                                 + "（MjpegScreen が未配線か Renderer が無い）");
            }
            return _screenMat;
        }

        /// <summary>
        /// 管の点灯量（0 = 消えている / 1 = 点いている）と、**映像そのものの出方**を書く。
        ///
        /// ⚠ **2 つは別物**。管が点くのは段 3、映像が出るのは段 4。分けていないと
        /// 「管が点き切った瞬間に映像も出る」ことになり、重み <c>live</c> が 0 のままなのに
        /// 画には映像が出る（2026-08-13 に Editor プレビューの絵で見つけた食い違い）。
        /// 実物のブラウン管も、電源を入れてから絵が出るまで暖機の時間がある。
        /// </summary>
        private void WriteIgnite(float v, float live)
        {
            var m = ResolveScreenMaterial();
            if (m == null) return;
            float c = Mathf.Clamp01(v);
            float l = Mathf.Clamp01(live);
            if (Mathf.Approximately(c, _igniteWritten) && Mathf.Approximately(l, _liveWritten)) return;
            _igniteWritten = c;
            _liveWritten = l;
            m.SetFloat(CrtIgniteId, c);
            m.SetFloat(IntroLiveId, l);
        }

        /// <summary>
        /// 管を点いた状態へ戻す。<b>演出を畳むすべての経路がここを通る</b> —
        /// 通し忘れると最後に書いた 0 が残って画がまるごと消える。
        /// </summary>
        private void ResetIgnite() => WriteIgnite(1f, 1f);

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
        /// 段 3（管が点く）が段 4（映像）へ進むための 2 条件。テレメトリ・診断用。
        /// **どちらが false で足踏みしているのかはログでしか分からない** —
        /// 実機で「管までは点くのに映像へ変わらない」を追うときの唯一の手掛かりになる。
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
            shell?.SetHidden();
            sealedBox?.SetHidden();
            glitch?.ResetAll();
            ResetIgnite();
            _logic.Disable();
            // ここで初めて本編へ進んでよくなる（段 0 と区別できる唯一の点）。
            _completed = true;
            if (!restartClock || _clockRestarted) return;
            _clockRestarted = true;
            // ⚠ ここで「歩いてください」を出していたが、面ごと廃止した（0033）。
            //    継ぎ目の合図はスタッフの声掛けが担う（`docs/onsite-checklist.md`）。
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
            shell?.SetHidden();
            sealedBox?.SetHidden();
            glitch?.ResetAll();
            ResetIgnite();
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
