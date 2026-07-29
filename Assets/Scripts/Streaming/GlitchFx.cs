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
        private Material? _material;

        /// <summary>現在の乱れ強度 (0-1)。HUD / 診断表示用。</summary>
        public float Level => _logic.Level;

        /// <summary>単発の乱れ（注意・移動の誘導、カット頭のアクセント）。</summary>
        public void Pulse(float level, float sec) => _logic.Pulse(level, sec);

        /// <summary>持続する乱れ（遷移のあいだ継ぎ目を覆う）。0 で解除。</summary>
        public void SetSustain(float level) => _logic.SetSustain(level);

        /// <summary>すべて畳む（ラン開始・体験の終了）。</summary>
        public void ResetAll()
        {
            _logic.Reset();
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
