#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 終幕（装置が力尽きて、報告を出して終わる）の実行体。<see cref="IntroDirector"/> と対で、
    /// <b>判断は <see cref="OutroLogic"/> にあり、ここは配線だけ</b>を持つ。
    ///
    /// 段は Collapse（ブラウン管の電源断 ＝ 潰れて線 → 点 → 消える）→ Dark（何も無い黒）
    /// → Report（報告の 4 行が 1 字ずつ打たれる）→ Done（そのまま次のランまで）。
    ///
    /// ⚠⚠ <b>パススルー・覆い・隔離には触らない</b>（2026-08-15 に作り替えた・
    /// <c>canon/LEDGER.md</c> 0048「パススルーには戻さず、背景が黒いまま」）。
    /// 本編の時点で背景は既に黒（<c>PassthroughStyler</c> が導入の後に切っている）ので、
    /// <b>何もしないことが「黒のまま」</b>。始めるときに覆い・隔離・乱れを畳むだけ。
    ///
    /// ⚠⚠ <b>スクリーンへ書く uniform は 2 本あり、既定が逆向き</b>
    /// （<c>_ScreenPower</c> = 1 が正常 / <c>_ScreenCollapse</c> = 0 が正常）。
    /// <see cref="IntroDirector"/> の <c>_CrtIgnite</c> と同じ罠を持つ — 終幕の値を残したまま
    /// 次の体験者が来ると<b>画がまるごと消える</b>ので、終幕を畳むすべての経路
    /// （終わった / ラン開始 / 相が変わった / <c>OnDisable</c>）で <see cref="ResetScreen"/> を通す。
    /// <b>「書くのをやめる」だけでは最後に書いた値が残る。</b>
    ///
    /// ⚠⚠ <b>2 本を別々に戻す実装にしない。</b> この codebase は「凍結が解けない」を 4 回
    /// 踏んでいる。戻す先が増えるほど、片方だけ書き戻す経路が生まれる。
    ///
    /// ⚠ 新しい凍結ラッチは足さない。終幕のあいだも画面のカメラ切替は裏で回っており、
    /// 見えなくなるのは電力を落としているからにすぎない。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OutroDirector : MonoBehaviour
    {
        [Header("References (未配線でも実行時に自己解決する)")]
        [SerializeField] private ShowRunDirector? runDirector;
        [SerializeField] private IntroVeil? veil;
        [SerializeField] private ContainmentShell? shell;
        [SerializeField] private GlitchFx? glitch;

        [Tooltip("スクリーンの実体。電力（_ScreenPower）を書く先の材質をここから取る。null なら実行時に探す。")]
        [SerializeField] private MjpegScreen? screen;

        [Tooltip("装置が打っている時計。電源断の頭で消す。null なら実行時に探す（無くても走る）。")]
        [SerializeField] private ScreenOsd? osd;

        private readonly OutroLogic _logic = new OutroLogic();
        private ShowOutroDef _def = new ShowOutroDef();
        private bool _subscribed;

        // --- スクリーンへ書く 2 本（_ScreenPower / _ScreenCollapse）-----------------
        // ⚠ 既定が逆向き（power=1 が正常 / collapse=0 が正常）。戻すのは ResetScreen 1 本。
        private static readonly int ScreenPowerId = Shader.PropertyToID("_ScreenPower");
        private static readonly int ScreenCollapseId = Shader.PropertyToID("_ScreenCollapse");
        private Material? _screenMat;
        private bool _warnedNoScreenMat;
        private float _powerWritten = -1f;
        private float _collapseWritten = -1f;

        /// <summary>
        /// 電源断の頭で時計を消すまでの割合（潰れの進みに対する）。
        ///
        /// ⚠⚠ <b>時計を潰れた座標のまま引き続けない。</b> OSD は矩形の中を直接サンプルするので、
        /// 潰れると暗黙の微分が飛んで<b>灰色の帯</b>になる（字ではなく汚れに見える）。
        /// 頭で消しておけば、潰れているあいだ時計は既に居ない。
        /// ⚠ 装置が落ちる話と矛盾しない（時計も装置が打っているもの）。
        /// </summary>
        private const float OsdCutAt = 0.12f;

        /// <summary>いまの段（卓の heartbeat / StatusHud / テレメトリ用）。</summary>
        public OutroStage Stage => _logic.Stage;

        /// <summary>計時が進んでいるか。</summary>
        public bool Active => _logic.Active;

        /// <summary>画に何かを出している / 出したままか（<see cref="OutroStage.Done"/> でも true）。</summary>
        public bool Presenting => _logic.Presenting;

        /// <summary>報告の面の不透明度（<c>OutroReport</c> が読む）。</summary>
        public float ReportAlpha => _logic.ReportAlpha;

        /// <summary>いまの段の進み 0..1（StatusHud・卓の heartbeat 用）。</summary>
        public float StageProgress01 => _logic.StageProgress01;

        /// <summary>
        /// 終幕に入ってからの総経過（秒）。<b>音がここを読む</b>
        /// （段の進みではない理由は <see cref="SoundShowState.outroElapsedSec"/>）。
        /// </summary>
        public float TotalElapsedSec => _logic.TotalElapsedSec;

        /// <summary>
        /// <b>実際にスクリーンの材質へ書いた電力</b>（<c>_ScreenPower</c>）。掴めていなければ <b>-1</b>。
        /// 「重みが動いた」ではなく「画に出た」の側の観測（<c>rules/streaming.md</c>）。
        /// </summary>
        public float PowerWritten => _screenMat != null ? _powerWritten : -1f;

        /// <summary>
        /// <b>実際に材質へ書いた電源断の進み</b>（<c>_ScreenCollapse</c>）。掴めていなければ <b>-1</b>。
        ///
        /// ⚠⚠ <b>これが無いと「潰れなかった」を誰も検出できない。</b> 段は正しく進み、
        /// <c>pw</c> も Dark 以降 0 になるので、**書けていなくても既存の判定は全部 PASS する**
        /// （画は「0.9 秒ふつうに映ってから黒へ瞬断」になる）。
        /// この codebase が繰り返し踏んだ「状態は進んだが効果が出ていない」型
        /// （<c>rules/troubleshooting.md</c>）。
        /// </summary>
        public float CollapseWritten => _screenMat != null ? _collapseWritten : -1f;

        private void OnEnable() => TrySubscribe();

        private void OnDisable()
        {
            if (runDirector != null && _subscribed)
            {
                runDirector.PhaseChanged -= OnPhaseChanged;
                runDirector.OutroDefChanged -= OnOutroDefChanged;
            }
            _subscribed = false;
            Stop();
        }

        // ShowRunDirector は ShowControlClient が実行時に自動生成することがあるので、居るまで毎フレーム試す。
        private void TrySubscribe()
        {
            if (_subscribed) return;
            Resolve();
            if (runDirector == null) return;
            runDirector.PhaseChanged += OnPhaseChanged;
            runDirector.OutroDefChanged += OnOutroDefChanged;
            _subscribed = true;
            OnOutroDefChanged(runDirector.OutroDef);
            // 終了状態で有効化された（＝シーンを跨いだ / 途中で足した）なら、そこから始める。
            if (runDirector.Phase == ShowPhase.Finished) Begin();
        }

        private void Resolve()
        {
            if (runDirector == null) runDirector = FindObjectOfType<ShowRunDirector>();
            if (veil == null) veil = FindObjectOfType<IntroVeil>();
            if (shell == null) shell = FindObjectOfType<ContainmentShell>();
            if (glitch == null) glitch = FindObjectOfType<GlitchFx>();
            if (screen == null) screen = FindObjectOfType<MjpegScreen>();
            if (osd == null) osd = FindObjectOfType<ScreenOsd>();
        }

        private void OnOutroDefChanged(ShowOutroDef def)
        {
            _def = def ?? new ShowOutroDef();
            _logic.Configure(_def.ToTiming());
            // 走行中に「終幕を出さない」へ変わったら畳む（卓から切れるようにしておく）。
            if (!_def.enabled && _logic.Presenting) Stop();
        }

        private void OnPhaseChanged(ShowPhase phase)
        {
            if (phase == ShowPhase.Finished) Begin();
            else Stop();   // ラン開始・本編へ戻った（＝次の体験者）。電力と面を戻す。
        }

        private void Begin()
        {
            if (!_def.enabled) return;
            _logic.Configure(_def.ToTiming());
            _logic.Begin();
            // 導入で使う層は畳んでおく（終幕はどれも使わない）。乱れも畳む —
            // 残ると消えていくスクリーンに縞が乗ったまま体験が終わる。
            veil?.SetHidden();
            shell?.SetHidden();
            glitch?.ResetAll();
            Debug.Log($"[Outro] 終幕を始める（{_def.ToTiming().TotalSec:F1}s・黒のまま管が落ちる）");
        }

        private void Stop()
        {
            _logic.Disable();
            veil?.SetHidden();
            shell?.SetHidden();
            glitch?.ResetAll();
            ResetScreen();
        }

        /// <summary>出していない間に材質を掴み直す間隔 (秒)。毎フレーム探すと只では済まない。</summary>
        private const float IdleResolveSec = 1f;
        private float _idleResolveWait;

        private void Update()
        {
            TrySubscribe();
            if (!_logic.Presenting)
            {
                // ⚠ **出していない間に 1 度だけ材質を掴んで既定を書いておく。**
                //   これが無いと <see cref="PowerWritten"/> が -1 のままで、テレメトリが
                //   「掴めていない（nc）」と「点いている（1.00）」を区別できない ＝
                //   本編中ずっと nc が出て、解析が偽の FAIL を出す。
                if (_powerWritten < 0f)
                {
                    _idleResolveWait -= Time.unscaledDeltaTime;
                    if (_idleResolveWait <= 0f)
                    {
                        _idleResolveWait = IdleResolveSec;
                        ResetScreen();
                    }
                }
                return;
            }

            OutroEvent ev = _logic.Tick(Time.unscaledDeltaTime);

            // 画へ出すのはこの 2 本だけ（報告の面は OutroReport が ReportAlpha を読む）。
            // ⚠ **順序に意味はないが、必ず両方書く** — 片方だけ書くと、潰れたまま電力が戻る
            //   / 電力を落としたのに画が潰れない、という中間の絵ができる。
            float collapse = _logic.ScreenCollapse;
            WritePower(_logic.ScreenPower);
            WriteCollapse(collapse);

            // 時計は潰れの頭で消す（潰れた座標のまま引くと灰色の帯になる）。
            if (osd != null) osd.Suppressed = collapse > OsdCutAt;

            if (ev == OutroEvent.Finished)
                Debug.Log("[Outro] 終幕が終わった（報告を出したまま次のランを待つ）");
        }

        /// <summary>
        /// 電力を書く先（<c>MjpegScreen</c> の Renderer のマテリアル）。
        /// <b><see cref="IntroDirector"/> / <c>CameraFeelFx</c> が掴んでいるのと同じ材質</b>で、
        /// 同じ流儀で取る（<c>Renderer.material</c> は 1 度実体化したらその後は同じ実体が返る）。
        ///
        /// ⚠ <b>掴めなかったときは何も書かない。</b> 掴めていないことは
        /// <see cref="PowerWritten"/> が -1 を返すことで実機ログに出る。
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
                Debug.LogWarning("[Outro] スクリーンの材質を掴めない — 電力（_ScreenPower）を書けない"
                                 + "（MjpegScreen が未配線か Renderer が無い）");
            }
            return _screenMat;
        }

        private void WritePower(float v)
        {
            var m = ResolveScreenMaterial();
            if (m == null) return;
            float p = Mathf.Clamp01(v);
            if (Mathf.Approximately(p, _powerWritten)) return;
            _powerWritten = p;
            m.SetFloat(ScreenPowerId, p);
        }

        private void WriteCollapse(float v)
        {
            var m = ResolveScreenMaterial();
            if (m == null) return;
            float c = Mathf.Clamp01(v);
            if (Mathf.Approximately(c, _collapseWritten)) return;
            _collapseWritten = c;
            m.SetFloat(ScreenCollapseId, c);
        }

        /// <summary>
        /// 管をふつうに映っている状態へ戻す。<b>終幕を畳むすべての経路がここを通る</b> —
        /// 通し忘れると最後に書いた値が残って画がまるごと消える。
        ///
        /// ⚠⚠ <b>3 つを必ず一緒に戻す</b>（電力 1 / 潰れ 0 / 時計を出す）。
        /// 別々に戻す実装にすると、片方だけ書き戻す経路が必ず生まれる —
        /// この codebase は「凍結が解けない」を 4 回踏んでいる。
        /// </summary>
        private void ResetScreen()
        {
            WritePower(1f);
            WriteCollapse(0f);
            if (osd != null) osd.Suppressed = false;
        }
    }
}
