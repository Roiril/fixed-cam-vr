#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 演出としての「映像の乱れ」。企画書 2.3 の
    /// 「ノイズやグリッチ等の乱れを一時的に重畳でき、差し替えの継ぎ目の隠蔽や、体験者の注意・移動の誘導に用いる」
    /// を実装する側。
    ///
    /// <c>_Glitch</c> / <c>_GlitchSeed</c> uniform の**唯一の writer**（<see cref="SignalLostFx"/> が
    /// <c>_SignalLost</c> の唯一の writer であるのと同じ流儀。外から material を直接書いても毎フレーム上書きされる）。
    ///
    /// 障害表示（<see cref="SignalLostFx"/> の砂嵐）とは uniform も意味も分ける:
    /// あちらは「本当に映像が来ていない」、こちらは「来ているが演出として壊して見せている」。
    /// シェーダでは演出の乱れ → 障害表示 の順に掛かるので、**実際に信号が切れたら障害表示が勝つ**。
    ///
    /// 駆動元は 4 つ:
    ///   - カットの遷移 <c>transition:"glitch"</c>（<see cref="CameraSwitchDirector"/> が持続成分を送る）
    ///   - カット頭の単発（<c>steps[].glitch</c> / <c>glitchSec</c>）
    ///   - ゾーン切替への重畳（<c>control.switchGlitch</c>）
    ///   - 卓からの手動発火（<c>control.glitchEpoch</c> の変化）
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GlitchFx : MonoBehaviour
    {
        private static readonly int GlitchId = Shader.PropertyToID("_Glitch");
        private static readonly int GlitchSeedId = Shader.PropertyToID("_GlitchSeed");

        private readonly GlitchEnvelopeLogic _logic = new GlitchEnvelopeLogic();
        private readonly GlitchEscalationLogic _esc = new GlitchEscalationLogic();
        private Material? _material;

        // 持続成分がいま立っているか（立ち上がりを 1 回として数えるため）。
        private bool _sustainOn;

        /// <summary>現在の乱れ強度 (0-1)。HUD / 診断表示用。</summary>
        public float Level => _logic.Level;

        /// <summary>このランで乱れが起きた回数（テレメトリ用）。</summary>
        public int Count => _esc.Count;

        /// <summary>大きくなり具合 0..1（テレメトリ用。1 回目は 0）。</summary>
        public float Escalation01 => _esc.Progress01;

        /// <summary>
        /// 乱れの音に掛ける倍率。<b>読むのは <c>ShowSoundDirector</c> だけ</b> —
        /// 音が別に数えると「画は激しいのに音は同じ」が沈黙して起きる（`canon/LEDGER.md` 0055）。
        /// </summary>
        public float SfxGain => _esc.VolumeGain;

        /// <summary>単発の乱れ（注意・移動の誘導、カット頭のアクセント）。</summary>
        public void Pulse(float level, float sec)
        {
            if (level <= 0.001f) { _logic.Pulse(level, sec); return; }
            // ⚠ **数えてから掛ける。** 逆にすると 1 回目から持ち上がって、台本の値が実機で出ない。
            _esc.Notify();
            _logic.Pulse(_esc.ApplyLevel(level), _esc.ApplyHold(sec));
        }

        /// <summary>持続する乱れ（遷移のあいだ継ぎ目を覆う）。0 で解除。</summary>
        public void SetSustain(float level)
        {
            bool on = level > 0.001f;
            // ⚠ 立ち上がりだけを 1 回として数える。毎フレーム数えると遷移 1 回で天井へ張り付く
            //    （遷移は 90Hz で 20 フレーム続く）。
            if (on && !_sustainOn) _esc.Notify();
            _sustainOn = on;
            _logic.SetSustain(on ? _esc.ApplyLevel(level) : 0f);
        }

        /// <summary>すべて畳む（ラン開始・体験の終了）。</summary>
        public void ResetAll()
        {
            _logic.Reset();
            // 体験 1 回ぶんの状態。ここはラン開始と終了の両方から呼ばれるので、
            // 落とす場所をここ 1 つにすれば次の体験者は必ず台本どおりから始まる。
            _esc.ResetRun();
            _sustainOn = false;
            Write(0f);
        }

        private void Awake()
        {
            var r = GetComponent<Renderer>();
            _material = r != null ? r.material : null;
        }

        private void OnEnable()
        {
            _logic.Reset();
            Write(0f);
        }

        private void OnDisable()
        {
            _logic.Reset();
            Write(0f);
        }

        private void Update()
        {
            // 切替 dip と同じく unscaledDeltaTime。timeScale=0 で乱れが凍りつかないようにする。
            Write(_logic.Tick(Time.unscaledDeltaTime));
        }

        private void Write(float level)
        {
            if (_material == null) return;
            _material.SetFloat(GlitchId, level);
            // シードは実時間。_Time.y を使わないのは Editor プレビュー（Play しない静止画検証）で
            // 同じ絵を再現できるようにするため。
            _material.SetFloat(GlitchSeedId, Time.unscaledTime);
        }
    }
}
