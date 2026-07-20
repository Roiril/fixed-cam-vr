#nullable enable

namespace TableDuoVr.Hands
{
    /// <summary>
    /// 触覚パターンの 1 セグメント（この長さの間、この振幅で振動する）。
    /// UnityEngine 非依存の純データ。
    /// </summary>
    public readonly struct HapticPulse
    {
        /// <summary>セグメント長（秒）。</summary>
        public readonly float Duration;

        /// <summary>振幅 0..1。0 は無音セグメント（連打の間の休符）。</summary>
        public readonly float Amplitude;

        public HapticPulse(float duration, float amplitude)
        {
            Duration = duration;
            Amplitude = amplitude;
        }
    }

    /// <summary>
    /// 両アプリ（廻リ視 / TableDuo）共通の触覚ボキャブラリ。frequency=0.5 固定・
    /// 振幅と波形はここが唯一のソース。数値は const（SerializeField にすると旧シーン YAML で
    /// 0 に読まれ無音化する罠 = unity-prefab-fields を避ける）。
    /// パターン配列は static readonly（イベント毎の GC を出さない・読み取り専用で共有）。
    /// </summary>
    public static class HapticVocabulary
    {
        public const float Frequency = 0.5f;

        public const float AckAmp = 0.25f;
        public const float ActionAmp = 0.5f;
        public const float FireAmp = 0.8f;
        public const float ErrorAmp = 0.6f;
        public const float HoldMinAmp = 0.10f;
        public const float HoldMaxAmp = 0.30f;

        /// <summary>Ack: 監視対象入力のダウンエッジ受理（40ms 単発）。</summary>
        public static readonly HapticPulse[] AckPulses =
        {
            new HapticPulse(0.040f, AckAmp),
        };

        /// <summary>Action: 短押しアクション実行（80ms 単発）。</summary>
        public static readonly HapticPulse[] ActionPulses =
        {
            new HapticPulse(0.080f, ActionAmp),
        };

        /// <summary>Fire: 長押し発火・モード遷移・確定（80ms×2、間 80ms 無音）。</summary>
        public static readonly HapticPulse[] FirePulses =
        {
            new HapticPulse(0.080f, FireAmp),
            new HapticPulse(0.080f, 0f),
            new HapticPulse(0.080f, FireAmp),
        };

        /// <summary>Error: 失敗・拒否（50ms×3、間 60ms 無音）。</summary>
        public static readonly HapticPulse[] ErrorPulses =
        {
            new HapticPulse(0.050f, ErrorAmp),
            new HapticPulse(0.060f, 0f),
            new HapticPulse(0.050f, ErrorAmp),
            new HapticPulse(0.060f, 0f),
            new HapticPulse(0.050f, ErrorAmp),
        };

        /// <summary>
        /// HoldTick の連続振幅（長押しカウント進行中）。進捗 0..1 を 0.10→0.30 に線形補間。
        /// 範囲外はクランプ（Mathf 非依存で自前実装 = 純ロジックを保つ）。
        /// </summary>
        public static float HoldAmplitude(float progress01)
        {
            if (progress01 <= 0f) return HoldMinAmp;
            if (progress01 >= 1f) return HoldMaxAmp;
            return HoldMinAmp + (HoldMaxAmp - HoldMinAmp) * progress01;
        }
    }

    /// <summary>
    /// 単発（one-shot）パターンの時間進行を持つ純ロジック。Tick(dt) で経過を進め現在振幅を返し、
    /// 総尺を過ぎたら自動停止して 0 を返す。UnityEngine 非依存 = EditMode テスト可能。
    /// </summary>
    public sealed class HapticPatternPlayer
    {
        private HapticPulse[]? _pulses;
        private float _elapsed;
        private float _total;

        public bool IsPlaying => _pulses != null;

        public void Play(HapticPulse[] pulses)
        {
            _pulses = pulses;
            _elapsed = 0f;
            _total = 0f;
            for (int i = 0; i < pulses.Length; i++) _total += pulses[i].Duration;
        }

        public void Stop() => _pulses = null;

        /// <summary>dt 秒進めて現在振幅を返す。終了で停止し 0 を返す。</summary>
        public float Tick(float dt)
        {
            var pulses = _pulses;
            if (pulses == null) return 0f;

            _elapsed += dt;
            if (_elapsed >= _total)
            {
                _pulses = null;
                return 0f;
            }

            float t = 0f;
            for (int i = 0; i < pulses.Length; i++)
            {
                t += pulses[i].Duration;
                if (_elapsed < t) return pulses[i].Amplitude;
            }
            return 0f; // 総尺内なら上で必ず返るが、浮動小数の端で安全に 0
        }
    }

    /// <summary>
    /// 片コントローラ分の触覚状態。単発パターンと長押しランプの両方を持ち、毎フレーム振幅を合成する。
    /// 優先順位: 単発（振幅 &gt; 0）&gt; 長押しランプ &gt; 無音。純ロジック = EditMode テスト可能。
    /// </summary>
    public sealed class HapticChannel
    {
        private readonly HapticPatternPlayer _oneShot = new();
        private float _holdProgress = -1f; // <0 = 長押しランプ非アクティブ

        public bool IsIdle => !_oneShot.IsPlaying && _holdProgress < 0f;

        public void PlayOneShot(HapticPulse[] pulses) => _oneShot.Play(pulses);

        /// <summary>長押し進捗（0..1）。負値でランプ解除。</summary>
        public void SetHoldProgress(float progress01) => _holdProgress = progress01;

        public void ClearHold() => _holdProgress = -1f;

        /// <summary>この frame の振幅（単発優先・無ければ長押しランプ・無ければ 0）。</summary>
        public float Tick(float dt)
        {
            float a = _oneShot.Tick(dt);
            if (a > 0f) return a;
            if (_holdProgress >= 0f) return HapticVocabulary.HoldAmplitude(_holdProgress);
            return 0f;
        }
    }
}
