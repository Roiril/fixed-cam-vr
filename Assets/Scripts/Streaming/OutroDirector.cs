#nullable enable
using System;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 終幕（2D スクリーン → パススルー）の実行体。<see cref="IntroDirector"/> と対で、
    /// <b>判断は <see cref="OutroLogic"/> にあり、ここは配線だけ</b>を持つ。
    ///
    /// 段は導入の逆を辿る:
    /// Warm（裏でパススルーを点火して待つ・画は本編のまま）→ Unswap（枠の中身が映像から現実へ）
    /// → Open（枠が開く）→ Restore（色と質感が戻る）→ Hold（素のパススルー）。
    ///
    /// 覆いは導入と<b>同じ <see cref="IntroVeil"/></b> を使う。開口の式を共有していないと
    /// 「閉じた形」と「開く形」が食い違うため（<see cref="OutroLogic"/> の設計注記）。
    /// パススルーの見え方（彩度・輪郭線）は <c>PassthroughStyler</c> が
    /// <see cref="Weights"/> を毎フレーム読んで流す — 導入と同じ経路。
    ///
    /// ⚠ 新しい凍結ラッチは足さない。終幕のあいだも画面のカメラ切替は裏で回っており、
    /// 見えなくなるのは覆いが閉じているからにすぎない（凍結は「解除されずに残る」事故を作る）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OutroDirector : MonoBehaviour
    {
        [Header("References (未配線でも実行時に自己解決する)")]
        [SerializeField] private ShowRunDirector? runDirector;
        [SerializeField] private IntroVeil? veil;
        [SerializeField] private ContainmentShell? shell;
        [SerializeField] private GlitchFx? glitch;

        private readonly OutroLogic _logic = new OutroLogic();
        private ShowOutroDef _def = new ShowOutroDef();
        private bool _subscribed;

        /// <summary>
        /// パススルーが実際に出ているかの供給元（OvrBridge の <c>PassthroughStyler</c> が注入）。
        /// Streaming asmdef は OVR を参照しない規約なので、判定は向こうから差し込む。
        ///
        /// null なら true（＝待たずに進む）。<see cref="OutroLogic.WarmMaxSec"/> の上限もあるので、
        /// 供給が無い環境（オフラインテスト・Editor）でも「本編のまま固まる」ことはない。
        /// </summary>
        public Func<bool>? PassthroughReadyProvider;

        /// <summary>いまの重み。<c>PassthroughStyler</c> がここを読む（導入と同じ語彙）。</summary>
        public IntroWeights Weights => _logic.Weights;

        /// <summary>いまの段（卓の heartbeat / StatusHud / テレメトリ用）。</summary>
        public OutroStage Stage => _logic.Stage;

        /// <summary>走っているか。</summary>
        public bool Active => _logic.Active;

        /// <summary>まだ画に何も出していない段か（Warm）。</summary>
        public bool Silent => _logic.Silent;

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
        }

        private void OnOutroDefChanged(ShowOutroDef def)
        {
            _def = def ?? new ShowOutroDef();
            _logic.Configure(_def.ToTiming());
            // 走行中に「終幕を出さない」へ変わったら畳む（卓から切れるようにしておく）。
            if (!_def.enabled && _logic.Active) Stop();
        }

        private void OnPhaseChanged(ShowPhase phase)
        {
            if (phase == ShowPhase.Finished) Begin();
            else Stop();   // ラン開始・本編へ戻った（＝次の体験者）。覆いを外して素の見えへ戻す。
        }

        private void Begin()
        {
            if (!_def.enabled) return;
            _logic.Configure(_def.ToTiming());
            _logic.Begin();
            Debug.Log($"[Outro] 終幕を始める（{_def.ToTiming().TotalSec:F1}s + 点火待ち）");
        }

        private void Stop()
        {
            _logic.Disable();
            veil?.SetHidden();
            shell?.SetHidden();
            glitch?.ResetAll();
        }

        private void Update()
        {
            TrySubscribe();
            if (!_logic.Active) return;

            OutroEvent ev = _logic.Tick(Time.unscaledDeltaTime, new OutroInput
            {
                passthroughReady = PassthroughReadyProvider?.Invoke() ?? true,
            });

            IntroWeights w = _logic.Weights;
            veil?.Apply(w);
            // 隔離殻。導入で閉じたものをここで開ける（Restore で色と一緒に会場が返る）。
            shell?.Apply(w);
            glitch?.SetSustain(w.glitch);

            if (ev == OutroEvent.Finished)
            {
                // Hold を過ぎた。覆いは全開（passthrough=1 / frame=0）なので Apply が自分で外している。
                // ここで SetHidden を重ねても害はないが、**乱れだけは明示的に畳む**
                // （残ると素のパススルーに縞が乗ったまま体験が終わる）。
                glitch?.ResetAll();
                Debug.Log("[Outro] 終幕が終わった（素のパススルー）");
            }
        }
    }
}
