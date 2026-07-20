#nullable enable
namespace FixedCamVr.Input
{
    /// <summary>
    /// コントローラ振動（触覚フィードバック）のパターン再生を「経過時間 → 現在振幅」に落とす純ロジック。
    /// MonoBehaviour（<c>FixedCamVr.OvrBridge.ControllerHaptics</c>）から分離して EditMode テスト可能にする。
    /// UnityEngine / OVRInput へ一切依存しない（時間は <see cref="Tick"/> の dt で受ける）。
    ///
    /// 振動ボキャブラリ（両アプリ共通仕様・計画 2026-07-20 の触覚フィードバック節）:
    ///   - <see cref="Pattern.Ack"/>    受理（監視入力のダウンエッジ）      : 40ms・amp 0.25 の単発
    ///   - <see cref="Pattern.Action"/> 短押しアクション実行                 : 80ms・amp 0.50 の単発
    ///   - <see cref="Pattern.Fire"/>   長押し発火 / モード遷移 / 確定保存    : 80ms×2（間 80ms）・amp 0.80
    ///   - <see cref="Pattern.Error"/>  失敗・拒否                           : 50ms×3（間 60ms）・amp 0.60
    ///   - HoldTick 長押しカウント進行                                       : 連続・amp 0.10→0.30 の progress 比例ランプ
    /// frequency は全パターン 0.5 固定（Quest 3 はほぼ振幅のみ体感差）。数値は const（SerializeField にすると
    /// 旧シーン YAML に未記載で 0 と読まれる罠 — OvrControllerBridge の閾値 const と同じ理由）。
    ///
    /// <b>重畳時の優先度（仕様として固定）</b>:
    ///   1. 単発パターン（Ack/Action/Fire/Error）は「<b>ピーク振幅が現在再生中より厳密に大きい時だけ差し替え</b>、
    ///      それ以外は再生中なら無視」。これにより弱い後着（Fire 中の Ack 等）が強い進行中パターンを潰さず、
    ///      同ピークの二重発火（確定保存 = RegistrationConfirmed と ModeChanged の同時 Fire 等）も 1 回に畳まれる。
    ///   2. HoldTick は連続の「床」として単発パターンと <b>max</b> で合成する（大振幅優先）。
    /// 出力振幅 = max(単発パターンの現在振幅, HoldTick ランプ)。
    /// </summary>
    public sealed class HapticSequenceLogic
    {
        /// <summary>全パターン共通の周波数（0.5 固定）。MonoBehaviour が振幅>0 の時に使う。</summary>
        public const float Frequency = 0.5f;

        public enum Pattern { Ack, Action, Fire, Error }

        // ---- 単発パターンのピーク振幅（差し替え判定に使う）----
        private const float AckAmp = 0.25f;
        private const float ActionAmp = 0.50f;
        private const float FireAmp = 0.80f;
        private const float ErrorAmp = 0.60f;

        // ---- HoldTick ランプ（progress 0→1 を amp 0.10→0.30 へ）----
        private const float HoldTickMinAmp = 0.10f;
        private const float HoldTickMaxAmp = 0.30f;

        // パターンごとのセグメント表 {継続秒, 振幅}（振幅 0 = 無音間隔）。
        // 並列 static readonly 配列（GC を出さないよう Trigger では複製しない・参照のみ保持）。
        private static readonly float[] AckDur = { 0.040f };
        private static readonly float[] AckAmpSeg = { AckAmp };

        private static readonly float[] ActionDur = { 0.080f };
        private static readonly float[] ActionAmpSeg = { ActionAmp };

        private static readonly float[] FireDur = { 0.080f, 0.080f, 0.080f };
        private static readonly float[] FireAmpSeg = { FireAmp, 0f, FireAmp };

        private static readonly float[] ErrorDur = { 0.050f, 0.060f, 0.050f, 0.060f, 0.050f };
        private static readonly float[] ErrorAmpSeg = { ErrorAmp, 0f, ErrorAmp, 0f, ErrorAmp };

        private bool _playing;
        private Pattern _cur;
        private float _curPeak;
        private float _elapsed;
        private float _holdProgress;
        private float _amp;

        /// <summary>直近 <see cref="Tick"/> が返した出力振幅 [0,1]。</summary>
        public float CurrentAmplitude => _amp;

        /// <summary>単発パターン再生中か（HoldTick は含まない）。</summary>
        public bool IsPlayingOneShot => _playing;

        /// <summary>
        /// 単発パターンを鳴らす。再生中でも「ピーク振幅が厳密に大きい」なら差し替え（＝ Ack→Action の昇格）、
        /// それ以外（同ピーク・低ピーク）は再生中なら無視する。
        /// </summary>
        public void Trigger(Pattern p)
        {
            float peak = Peak(p);
            if (_playing && peak <= _curPeak) return;
            _playing = true;
            _cur = p;
            _curPeak = peak;
            _elapsed = 0f;
        }

        /// <summary>長押しカウント進行の進捗 [0,1] を設定する。0 or 1 で HoldTick は停止（1 は発火済み扱い）。</summary>
        public void SetHoldProgress(float progress01)
        {
            _holdProgress = progress01 < 0f ? 0f : (progress01 > 1f ? 1f : progress01);
        }

        /// <summary>計時と HoldTick 進捗をクリアする（振幅 0）。</summary>
        public void Reset()
        {
            _playing = false;
            _elapsed = 0f;
            _holdProgress = 0f;
            _amp = 0f;
        }

        /// <summary>dt 秒進めて現在の出力振幅 [0,1] を返す。非再生かつ HoldTick 停止時は 0 を保証。</summary>
        public float Tick(float dt)
        {
            float oneShot = 0f;
            if (_playing)
            {
                _elapsed += dt;
                if (!TryAmplitudeAt(_cur, _elapsed, out oneShot))
                {
                    _playing = false;
                    oneShot = 0f;
                }
            }

            // HoldTick は progress ∈ (0,1) の間だけ連続で鳴らす（1 到達＝発火済みなので鳴らさない）。
            float hold = (_holdProgress > 0f && _holdProgress < 1f)
                ? HoldTickMinAmp + (HoldTickMaxAmp - HoldTickMinAmp) * _holdProgress
                : 0f;

            _amp = oneShot > hold ? oneShot : hold;
            return _amp;
        }

        // 経過時間 t（秒）でのパターン振幅。パターン総時間を超えたら false（再生終了）。
        private static bool TryAmplitudeAt(Pattern p, float t, out float amp)
        {
            float[] dur = DurTable(p);
            float[] segAmp = AmpTable(p);
            float acc = 0f;
            for (int i = 0; i < dur.Length; i++)
            {
                acc += dur[i];
                if (t < acc) { amp = segAmp[i]; return true; }
            }
            amp = 0f;
            return false;
        }

        private static float Peak(Pattern p) => p switch
        {
            Pattern.Ack => AckAmp,
            Pattern.Action => ActionAmp,
            Pattern.Fire => FireAmp,
            Pattern.Error => ErrorAmp,
            _ => 0f,
        };

        private static float[] DurTable(Pattern p) => p switch
        {
            Pattern.Ack => AckDur,
            Pattern.Action => ActionDur,
            Pattern.Fire => FireDur,
            Pattern.Error => ErrorDur,
            _ => AckDur,
        };

        private static float[] AmpTable(Pattern p) => p switch
        {
            Pattern.Ack => AckAmpSeg,
            Pattern.Action => ActionAmpSeg,
            Pattern.Fire => FireAmpSeg,
            Pattern.Error => ErrorAmpSeg,
            _ => AckAmpSeg,
        };
    }
}
