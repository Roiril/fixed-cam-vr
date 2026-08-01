#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// スクリーンの水平追従（ヨー）の緩急を担う純ロジック。MonoBehaviour（<see cref="ScreenAnchor"/>）から
    /// 分離して EditMode テスト可能にする。2D ゲームカメラの camera window + damped smoothing + 速度上限
    /// （Itay Keren 分類）をヨーに適用する。
    ///
    /// 挙動:
    ///   - <b>deadzone = 動き出す閾値</b>: 止まっている間、|頭とスクリーンのヨー差| がこの範囲なら不動（微小 jitter を吸収）
    ///   - <b>trail = 止まる位置</b>: 目標は <c>headYaw − sign(e)·trail</c>。<b>trail=0 なら頭の正面ちょうどを目指す</b>
    ///   - 速度上限: SmoothDampAngle の maxSpeed で full-field スライドを制限（vection 抑制）
    ///   - 逆走ガード: 差が catchUpThreshold を超えたら上限を catchUpBoost 倍にして一時的に追いつく
    ///
    /// ⚠ <b>deadzone と trail は別物</b>（2026-08-01 に分離）。旧実装は 1 つの値を両方に使っていたため、
    /// スクリーンは<b>常に頭の 10° 手前で止まり、正面まで到達しなかった</b>。
    /// さらに「動き出した後も deadzone を効かせる」と、目標へ寄る途中で deadzone に入って止まってしまう
    /// （＝到達しない）ので、<b>動き出したら deadzone は見ない</b>（<see cref="_moving"/> のヒステリシス）。
    ///
    /// SmoothDamp は指数収束なので厳密には目標へ届かない。差と速度が <see cref="SettleDeg"/> /
    /// <see cref="SettleSpeedDegPerSec"/> を切ったら<b>目標へ吸着して停止する</b>（「そこで止まる」を字義どおりにする）。
    ///
    /// 状態（現在ヨー・速度）は本クラスが保持する。lock ON 時は <see cref="Reseat"/> で現在ヨーを種にして
    /// スナップせず合流する。
    /// </summary>
    public sealed class YawFollowLogic
    {
        /// <summary>目標との差がこれ未満なら到達とみなす (度)。</summary>
        public const float SettleDeg = 0.05f;

        /// <summary>到達判定に必要な角速度の上限 (度/秒)。速い通過中に止めないため。</summary>
        public const float SettleSpeedDegPerSec = 1f;

        private float _screenYaw;
        private float _yawVel;
        private bool _seeded;
        private bool _moving;

        /// <summary>現在のスクリーンヨー（度）。</summary>
        public float CurrentYaw => _screenYaw;

        /// <summary>種（現在ヨー）が設定済みか。未設定なら次の Step で headYaw を種にする。</summary>
        public bool Seeded => _seeded;

        /// <summary>
        /// 現在ヨーを与えて追従の起点にする（速度は 0 リセット）。
        /// lock ON / 凍結明け / resume 明けの「スナップ禁止・現在位置から合流」に使う。
        /// </summary>
        public void Reseat(float currentYaw)
        {
            _screenYaw = currentYaw;
            _yawVel = 0f;
            _seeded = true;
            _moving = false;
        }

        /// <summary>
        /// 1 フレーム分ヨーを進めて新しいスクリーンヨーを返す。パラメータは呼び手（ScreenAnchor の
        /// SerializeField / const）を単一のソースにするため引数で受ける。dt&lt;=0 は現状維持。
        /// </summary>
        /// <param name="deadzoneDeg">止まっている状態から動き出す閾値 (度)。</param>
        /// <param name="trailDeg">頭の手前どこで止まるか (度)。<b>0 なら頭の正面ちょうど</b>。</param>
        public float Step(float headYaw, float dt, float deadzoneDeg, float trailDeg, float smoothTime,
                          float maxSpeedDegPerSec, float catchUpThresholdDeg, float catchUpBoost)
        {
            if (!_seeded)
            {
                _screenYaw = headYaw;
                _seeded = true;
            }
            if (dt <= 0f) return _screenYaw;

            if (trailDeg < 0f) trailDeg = 0f;
            // trail より内側の deadzone は意味を持たない（目標に着いた時点で必ず |e| = trail になるので、
            // deadzone < trail だと到達直後に再び動き出して振動する）。
            if (deadzoneDeg < trailDeg) deadzoneDeg = trailDeg;

            float e = Mathf.DeltaAngle(_screenYaw, headYaw); // 現在→頭 の最短角
            float absE = Mathf.Abs(e);

            // 止まっている間だけ deadzone で動き出しを抑える。動き出した後は見ない（見ると到達しない）。
            if (!_moving)
            {
                if (absE <= deadzoneDeg) return _screenYaw;
                _moving = true;
            }

            float targetYaw = trailDeg > 0f ? headYaw - Mathf.Sign(e) * trailDeg : headYaw;
            // 逆走ガード: 大きく置いていかれたら上限を一時的に上げて追いつく。
            float maxSpeed = absE >= catchUpThresholdDeg
                ? maxSpeedDegPerSec * Mathf.Max(1f, catchUpBoost)
                : maxSpeedDegPerSec;
            _screenYaw = Mathf.SmoothDampAngle(_screenYaw, targetYaw, ref _yawVel, smoothTime, maxSpeed, dt);

            // 到達したら吸着して止める。指数収束の裾を引くと「いつまでも微動している」ように見える。
            if (Mathf.Abs(Mathf.DeltaAngle(_screenYaw, targetYaw)) <= SettleDeg
                && Mathf.Abs(_yawVel) <= SettleSpeedDegPerSec)
            {
                _screenYaw = targetYaw;
                _yawVel = 0f;
                _moving = false;
            }
            return _screenYaw;
        }
    }
}
