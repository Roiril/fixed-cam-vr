#nullable enable
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 「現在の頭の姿勢を席（初期目線アンカー）に合わせる」リグ再センタリングの純計算。
    /// OVR 非依存（Transform 操作のみ）なので EditMode テスト可能。
    /// </summary>
    public static class RigRecenter
    {
        /// <summary>
        /// <paramref name="rig"/>（= <paramref name="head"/> の先祖）を動かし・回し、頭が
        /// <paramref name="seat"/> の位置と yaw（水平向き）に一致するようにする。
        /// 水平を保つため pitch/roll は変えず yaw のみ合わせる。位置は高さ込みで席へ合わせる。
        /// </summary>
        public static void HeadToSeat(Transform rig, Transform head, Transform seat)
        {
            // yaw を席に合わせる（頭の現在位置を軸に回すので、回転で頭の位置は動かない）
            float yawDelta = Mathf.DeltaAngle(head.eulerAngles.y, seat.eulerAngles.y);
            rig.RotateAround(head.position, Vector3.up, yawDelta);

            // 頭を席の位置へ平行移動（rig を動かすと子の頭も追従）
            rig.position += seat.position - head.position;
        }
    }
}
