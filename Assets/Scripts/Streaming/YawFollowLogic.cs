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
    ///   - deadzone: |頭とスクリーンのヨー差| がこの範囲ならスクリーン不動（微小 jitter を吸収）
    ///   - trail: deadzone を超えたら「枠端に頭を置いたまま」= headYaw − sign(e)·deadzone を目標に減衰追従
    ///   - 速度上限: SmoothDampAngle の maxSpeed で full-field スライドを制限（vection 抑制）
    ///   - 逆走ガード: 差が catchUpThreshold を超えたら上限を catchUpBoost 倍にして一時的に追いつく
    /// 状態（現在ヨー・速度）は本クラスが保持する。lock ON 時は <see cref="Reseat"/> で現在ヨーを種にして
    /// スナップせず合流する。
    /// </summary>
    public sealed class YawFollowLogic
    {
        private float _screenYaw;
        private float _yawVel;
        private bool _seeded;

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
        }

        /// <summary>
        /// 1 フレーム分ヨーを進めて新しいスクリーンヨーを返す。パラメータは呼び手（ScreenAnchor の
        /// SerializeField）を単一のソースにするため引数で受ける。dt&lt;=0 は現状維持。
        /// </summary>
        public float Step(float headYaw, float dt, float deadzoneDeg, float smoothTime,
                          float maxSpeedDegPerSec, float catchUpThresholdDeg, float catchUpBoost)
        {
            if (!_seeded)
            {
                _screenYaw = headYaw;
                _seeded = true;
            }
            if (dt <= 0f) return _screenYaw;

            float e = Mathf.DeltaAngle(_screenYaw, headYaw); // 現在→頭 の最短角
            float absE = Mathf.Abs(e);
            // deadzone 内は不動。外なら「枠端に頭を置く」= 頭から deadzone だけ手前を目標にトレール。
            float targetYaw = absE <= deadzoneDeg
                ? _screenYaw
                : headYaw - Mathf.Sign(e) * deadzoneDeg;
            // 逆走ガード: 大きく置いていかれたら上限を一時的に上げて追いつく。
            float maxSpeed = absE >= catchUpThresholdDeg
                ? maxSpeedDegPerSec * Mathf.Max(1f, catchUpBoost)
                : maxSpeedDegPerSec;
            _screenYaw = Mathf.SmoothDampAngle(_screenYaw, targetYaw, ref _yawVel, smoothTime, maxSpeed, dt);
            return _screenYaw;
        }
    }
}
