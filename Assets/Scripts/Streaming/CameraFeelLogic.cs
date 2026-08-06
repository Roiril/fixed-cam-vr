#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 「装置らしさ」の時間変化を決める純ロジック（UnityEngine 非依存・dt 注入）。
    /// <see cref="CameraFeelFx"/> がこれを回してシェーダの uniform へ書く。
    ///
    /// <b>なぜ post とは別系統か</b>: post 12 項目は shader / 卓の FS_POST / common.js / pipeline.js の
    /// 4 箇所で手作業同期していて機械テストが無い。そこへ時間軸を持ち込むと沈黙した食い違いが必ず出る。
    /// こちらは実機だけが持つ「装置の挙動」で、著作するのは静的な強さだけ。
    ///
    /// 持っているのは 2 つ:
    ///   - <b>自動露出の追従遅れ</b>: 映像の明るさへ**遅れて**追いつく。減衰を 1 未満にしてあるので
    ///     行き過ぎて戻る（呼吸するような明滅）。画に映っていないのに明るさが動くと、
    ///     「画面の外で何かが起きた」と読まれる。
    ///   - <b>凍らせた 1 枚</b>: ホールド（画が止まる）と焼き付き（少し前の姿が薄く残る）。
    ///     フレーム履歴ではなく**指定した瞬間の 1 枚**なので決定的で、同じ show.json は同じ絵になる。
    /// </summary>
    public sealed class CameraFeelLogic
    {
        /// <summary>装置が目指す明るさ（映像の平均輝度 0..1）。</summary>
        public float TargetLuma = 0.34f;

        /// <summary>目標へ半分まで近づくのにかかる時間 (秒)。大きいほど鈍い。</summary>
        public float FollowHalfLifeSec = 1.1f;

        /// <summary>
        /// 減衰比。**1 未満だと行き過ぎて戻る**（＝呼吸）。1 以上にすると素直に収束して、
        /// 「装置が迷っている」感じが消える。
        /// </summary>
        public float Damping = 0.55f;

        /// <summary>露出バイアスの上下限 (EV)。ここを広げると画が破綻する。</summary>
        public float MaxBiasEv = 0.8f;

        /// <summary>露出 1EV あたりの周辺光量の変化。絞りが開くほど周辺が落ちる。</summary>
        public float VignettePerEv = 0.10f;

        /// <summary>ホールドが明けてから完全に戻るまでの時間 (秒)。</summary>
        private const float HoldReleaseSec = 0.25f;

        /// <summary>1 フレームで進める dt の上限 (秒)。ヒッチで 2 次系が発散するのを防ぐ。</summary>
        private const float MaxStepSec = 0.05f;

        private float _bias;
        private float _vel;
        private float _observed = -1f;

        private float _echo;
        private float _echoHold;
        // 1 秒あたりの減り。**開始時の濃さ ÷ 指定秒**（「濃さによらず一定量」だと薄い焼き付きほど
        // 早く消えて、著作した秒が意味を持たなくなる）。
        private float _echoRate = 1f / HoldReleaseSec;
        private bool _snapshotRequested;

        /// <summary>いまの露出バイアス (EV)。</summary>
        public float ExposureBias => _bias;

        /// <summary>いまの周辺光量の増減。露出を持ち上げたぶんだけ周辺が落ちる。</summary>
        public float VignetteBias => -_bias * VignettePerEv;

        /// <summary>凍らせた 1 枚の混合率 0..1（1 = 完全に止まって見える）。</summary>
        public float Echo => _echo;

        /// <summary>
        /// ホールド中か。**ここが真の間は差し替え素材（録画・動画）の時計も止める** —
        /// ライブだけ止めて録画が動くと「装置が固まった」に見えない。
        /// </summary>
        public bool Frozen => _echoHold > 0f;

        /// <summary>映像の平均輝度を観測する（-1 = まだ測れていない）。</summary>
        public void ObserveLuma(float luma)
        {
            if (luma >= 0f) _observed = luma;
        }

        /// <summary>
        /// 画を止める。<paramref name="sec"/> 秒そのままにして、そこから 0.25 秒で戻る。
        /// 止まったのが装置なのか自分なのか一瞬わからない、という間を作るためのもの。
        /// </summary>
        public void Hold(float sec)
        {
            if (sec <= 0f) return;
            _snapshotRequested = true;
            _echo = 1f;
            _echoHold = sec;
            _echoRate = 1f / HoldReleaseSec;
        }

        /// <summary>
        /// 焼き付き。いまの画を <paramref name="amount"/> の濃さで残し、<paramref name="sec"/> 秒かけて消す。
        /// 動いていない画素は同じ値なので何も起きず、**動いたものの跡だけ**が残る。
        /// </summary>
        public void Burn(float amount, float sec)
        {
            if (amount <= 0f) return;
            _snapshotRequested = true;
            _echo = Math.Min(1f, amount);
            _echoHold = 0f;
            _echoRate = _echo / Math.Max(0.01f, sec > 0f ? sec : 2.5f);
        }

        /// <summary>
        /// このフレームで新しく 1 枚凍らせるべきか（読むと下りる）。
        /// 呼び出し側はこれが真のときだけ <c>Graphics.Blit</c> する。
        /// </summary>
        public bool ConsumeSnapshotRequest()
        {
            if (!_snapshotRequested) return false;
            _snapshotRequested = false;
            return true;
        }

        /// <summary>すべて畳む（ラン開始・体験の終了・無効化）。</summary>
        public void Reset()
        {
            _bias = 0f;
            _vel = 0f;
            _echo = 0f;
            _echoHold = 0f;
            _snapshotRequested = false;
        }

        /// <summary>1 フレーム進める。</summary>
        public void Tick(float dt)
        {
            if (dt <= 0f) return;
            dt = Math.Min(dt, MaxStepSec);
            TickExposure(dt);
            TickEcho(dt);
        }

        private void TickExposure(float dt)
        {
            float target = 0f;
            if (_observed > 0.001f)
            {
                // 目標より暗ければ持ち上げる（＝装置が明るくしようとする）。
                target = Clamp((float)(Math.Log(TargetLuma / _observed) / Math.Log(2.0)), -MaxBiasEv, MaxBiasEv);
            }

            // 2 次系（バネ + ダンパ）。1 次遅れだと行き過ぎず、装置が「迷って」見えない。
            float half = Math.Max(0.05f, FollowHalfLifeSec);
            float omega = 0.6931472f / half;                    // ln2 / halfLife
            float accel = omega * omega * (target - _bias) - 2f * Damping * omega * _vel;
            _vel += accel * dt;
            _bias += _vel * dt;
            _bias = Clamp(_bias, -MaxBiasEv * 1.5f, MaxBiasEv * 1.5f);
        }

        private void TickEcho(float dt)
        {
            if (_echoHold > 0f)
            {
                _echoHold -= dt;
                return;
            }
            if (_echo <= 0f) return;
            _echo -= _echoRate * dt;
            if (_echo < 0f) _echo = 0f;
        }

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
