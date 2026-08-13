#nullable enable

using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 「体験エリアへ<b>近づいてきた</b>」の判定（UnityEngine 非依存の判断だけ・dt 注入）。
    ///
    /// 導入を始める合図（<c>canon/LEDGER.md</c> 0005「その黒で囲まれてる領域に近づくまでは
    /// なにも始まらないように」）。<see cref="StartSpotLogic"/> と同じ規律で、
    /// <b>「近い」という状態では始めない</b> — 起動時にたまたま近くに居た / 前の体験者が
    /// 立ったままだった、で勝手に走り出す（2026-08-09 に開始位置の円で実際に踏んだ形）。
    ///
    /// ⚠⚠ <b>2026-08-13 に武装の条件を作り直した。</b> それまでは「一度
    /// <see cref="ArmMarginM"/> ぶん外へ出る」ことだけが武装の条件だったので、
    /// <b>体験者を立たせる場所が箱から <c>nearM + ArmMarginM</c>（既定 1.35m）より内側にあると、
    /// 導入は自動では二度と始まらなかった</b>（2026-08-13 ユーザー指摘
    /// 「開始位置が箱に近かったりするとバグるよね」）。しかも黙って待つので、現場では
    /// 「立っても始まらない」としか見えない。
    ///
    /// いまは<b>「離れている」か「離れた所から近づいてきた」</b>のどちらかで武装する。
    /// 後者は <see cref="ApproachDeltaM"/> ぶん距離が縮んだこと ＝ <b>事象</b>なので、
    /// 立ち止まっている人・置かれた HMD では成立しない（防ぎたかった性質はそのまま残る）。
    ///
    /// 加えて <see cref="LineCrossLogic"/> / <see cref="StartSpotLogic"/> と同じ 2 つの不連続ガードを持つ。
    /// トラッキングの立ち上がり・recenter・HMD 着脱で距離が飛ぶと、その飛びがそのまま
    /// 「近づいてきた」に化けるため（<see cref="MaxStepM"/> / <see cref="MaxContinuousDtSec"/> /
    /// <see cref="SettleSec"/>）。
    /// </summary>
    public sealed class ApproachLogic
    {
        /// <summary>「離れている」とみなす追加の距離 (m)。近い判定の外側にこれだけ余裕を取る。</summary>
        public const float ArmMarginM = 0.35f;

        /// <summary>
        /// 「近づいてきた」と認める距離の縮み (m)。<b>これが箱の近くから始める現場の唯一の道</b>。
        /// 小さくすると立ち止まった人の揺れで武装してしまうので、頭の揺れ（数 cm）より十分大きく取る。
        /// </summary>
        public const float ApproachDeltaM = 0.35f;

        /// <summary>1 フレームでこれ以上距離が飛んだら軌跡として信用しない (m)。</summary>
        public const float MaxStepM = LineCrossLogic.MaxStepM;

        /// <summary>これ以上 dt が飛んだフレームは軌跡として信用しない (秒)。</summary>
        public const float MaxContinuousDtSec = LineCrossLogic.MaxContinuousDtSec;

        /// <summary>武装し直してから軌跡を信用し始めるまでの連続追跡時間 (秒)。</summary>
        public const float SettleSec = 0.5f;

        private bool _armed;
        private float _heldSec;
        private float _settledSec;
        private bool _hasPrev;
        private float _prevM;
        private float _maxSeenM;

        /// <summary>近づく合図を受け付ける状態か（テスト・診断用）。</summary>
        public bool Armed => _armed;

        /// <summary>落ち着いてから観測したいちばん遠い距離 (m)。診断用（現場の warning が読む）。</summary>
        public float MaxSeenM => _maxSeenM;

        /// <summary>やり直す（ラン開始・位置合わせのやり直し・HMD を外した）。</summary>
        public void Rearm()
        {
            _armed = false;
            _heldSec = 0f;
            _settledSec = 0f;
            _hasPrev = false;
            _maxSeenM = 0f;
        }

        /// <summary>
        /// 1 フレーム進める。
        /// </summary>
        /// <param name="outsideM">体験エリアの外側までの距離 (m)。中に居れば 0。</param>
        /// <param name="nearM">これ以下なら「近づいた」。</param>
        /// <param name="holdSec">近づいたまま留まる必要のある秒数（通りすがりで始めない）。</param>
        /// <param name="valid">距離が信用できるか（未登録・layout 未着なら false）。</param>
        /// <returns>近づいた合図が成立したか。</returns>
        public bool Tick(float outsideM, float nearM, float holdSec, float dt, bool valid)
        {
            if (!valid)
            {
                // 判定できない間は武装も解く。**解かないと、復帰した瞬間に古い滞在で発火する**。
                Rearm();
                return false;
            }

            // 軌跡の不連続。**ここで武装を落とさないと、トラッキングの立ち上がりで
            // 遠くの値を 1 度掴んだだけで「近づいてきた」が成立する**。
            bool continuous = dt > 0f && dt <= MaxContinuousDtSec;
            if (_hasPrev && continuous && Mathf.Abs(outsideM - _prevM) > MaxStepM) continuous = false;
            _prevM = outsideM;
            _hasPrev = true;
            if (!continuous)
            {
                _armed = false;
                _heldSec = 0f;
                _settledSec = 0f;
                _maxSeenM = outsideM;
                return false;
            }

            // 武装直後の数フレームはトラッキングが立ち上がる途中の値が混じる。
            if (_settledSec < SettleSec)
            {
                _settledSec += dt;
                _maxSeenM = Mathf.Max(_maxSeenM, outsideM);
                return false;
            }

            if (outsideM > _maxSeenM) _maxSeenM = outsideM;

            if (!_armed)
            {
                // ① 十分離れている（従来どおり）。② そこまで離れていなくても、
                //    **観測したいちばん遠い所から縮んできた**なら「近づいてきた」。
                if (outsideM > nearM + ArmMarginM) _armed = true;
                else if (_maxSeenM - outsideM >= ApproachDeltaM) _armed = true;
            }
            if (!_armed)
            {
                _heldSec = 0f;
                return false;
            }

            if (outsideM > nearM)
            {
                // 近い / 遠いの帯の間。**滞在は数えないが武装は保つ**（境界での震えで落とさない）。
                _heldSec = 0f;
                return false;
            }

            _heldSec += dt > 0f ? dt : 0f;
            return _heldSec >= holdSec;
        }
    }
}
