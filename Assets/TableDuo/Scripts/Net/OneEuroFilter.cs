#nullable enable
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 1€ フィルタ（One Euro Filter・Casiez et al. 2012）の標準実装（float 版）。
    /// 静止時は cutoff を下げて手ブレを強く均し、速く動くほど cutoff を上げて追従遅れを消す
    /// （速度適応ローパス）。ペン先のジッタ除去に使う（<see cref="ToolGripDriver"/>）。
    ///
    /// 係数の既定値（minCutoff/beta/dCutoff）は VR ハンドトラッキングの一般的初期値。
    /// beta は「微分（m/s）→ cutoff 加算」の係数なので、メートル単位系では数十のオーダーが必要
    /// （例: beta=15 なら 3 m/s の速動で cutoff ≈ 46Hz ＝遅れ約 1cm。0.05 では適応がほぼ効かず 20cm 遅れる）。
    /// ⚠ 実機の手ブレ量・フレームレートで最適点が変わるため現場調整前提。
    /// </summary>
    public sealed class OneEuroFloatFilter
    {
        private readonly float _minCutoff;
        private readonly float _beta;
        private readonly float _dCutoff;

        private float _xPrev;   // 直近の平滑済み値
        private float _dxPrev;  // 直近の平滑済み微分
        private bool _init;

        public OneEuroFloatFilter(float minCutoff = 1.5f, float beta = 15f, float dCutoff = 1f)
        {
            _minCutoff = minCutoff;
            _beta = beta;
            _dCutoff = dCutoff;
        }

        /// <summary>状態を初期化する（掴み開始エッジで呼び、前ストロークの残留を消す）。</summary>
        public void Reset()
        {
            _init = false;
            _dxPrev = 0f;
        }

        public bool Initialized => _init;

        /// <summary>直近の平滑済み値（未初期化は 0）。</summary>
        public float Current => _xPrev;

        /// <summary>値 x を dt 秒経過として平滑する。dt≤0 は直近値を保持（未初期化なら x で初期化）。</summary>
        public float Filter(float x, float dt)
        {
            if (!_init)
            {
                _init = true;
                _xPrev = x;
                _dxPrev = 0f;
                return x;
            }
            if (dt <= 0f) return _xPrev; // フレーム停止・巻き戻り時は最後の値を保持

            float dx = (x - _xPrev) / dt;
            float dxHat = Mathf.Lerp(_dxPrev, dx, Alpha(_dCutoff, dt));
            float cutoff = _minCutoff + _beta * Mathf.Abs(dxHat);
            float xHat = Mathf.Lerp(_xPrev, x, Alpha(cutoff, dt));
            _xPrev = xHat;
            _dxPrev = dxHat;
            return xHat;
        }

        // cutoff（Hz）と dt からローパスの補間係数 α を得る。α = 1/(1 + τ/dt), τ = 1/(2π·cutoff)
        private static float Alpha(float cutoff, float dt)
        {
            float tau = 1f / (2f * Mathf.PI * cutoff);
            return 1f / (1f + tau / dt);
        }
    }

    /// <summary>1€ フィルタの Vector3 版（成分ごとに独立平滑）。ピンチ点のワールド座標に使う。</summary>
    public sealed class OneEuroFilter
    {
        private readonly OneEuroFloatFilter _x;
        private readonly OneEuroFloatFilter _y;
        private readonly OneEuroFloatFilter _z;

        public OneEuroFilter(float minCutoff = 1.5f, float beta = 15f, float dCutoff = 1f)
        {
            _x = new OneEuroFloatFilter(minCutoff, beta, dCutoff);
            _y = new OneEuroFloatFilter(minCutoff, beta, dCutoff);
            _z = new OneEuroFloatFilter(minCutoff, beta, dCutoff);
        }

        public void Reset()
        {
            _x.Reset();
            _y.Reset();
            _z.Reset();
        }

        public Vector3 Filter(Vector3 v, float dt) =>
            new Vector3(_x.Filter(v.x, dt), _y.Filter(v.y, dt), _z.Filter(v.z, dt));
    }
}
