#nullable enable

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 「体験エリアへ<b>近づいてきた</b>」の判定（UnityEngine 非依存・dt 注入）。
    ///
    /// 導入を始める合図（<c>canon/LEDGER.md</c> 0005「その黒で囲まれてる領域に近づくまでは
    /// なにも始まらないように」）。<see cref="StartSpotLogic"/> と同じ規律で、
    /// <b>離れていたことを観測してから、近づいた滞在を数える</b>。
    ///
    /// ⚠ <b>「近い」という状態では始めない。</b> 起動時にたまたま近くに居た / 前の体験者が
    /// 立ったままだった、で勝手に走り出す（2026-08-09 に開始位置の円で実際に踏んだ形）。
    /// 一度 <see cref="ArmMarginM"/> ぶん外へ出ることが条件。
    /// </summary>
    public sealed class ApproachLogic
    {
        /// <summary>「離れている」とみなす追加の距離 (m)。近い判定の外側にこれだけ余裕を取る。</summary>
        public const float ArmMarginM = 0.35f;

        private bool _armed;
        private float _heldSec;

        /// <summary>離れていたことを観測済みか（テスト・診断用）。</summary>
        public bool Armed => _armed;

        /// <summary>やり直す（ラン開始・位置合わせのやり直し・HMD を外した）。</summary>
        public void Rearm()
        {
            _armed = false;
            _heldSec = 0f;
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
                _armed = false;
                _heldSec = 0f;
                return false;
            }

            if (outsideM > nearM + ArmMarginM)
            {
                _armed = true;
                _heldSec = 0f;
                return false;
            }

            if (!_armed) return false;

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
