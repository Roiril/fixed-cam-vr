#nullable enable
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 席＝初期目線アンカーの可視化（Scene ビュー）。
    /// このオブジェクトの **位置 = プレイヤーの初期目線位置**、**forward(+Z) = 初期視線方向**。
    /// EyeLevel トラッキング + リグを席にアラインすることで、起動時の目線がここに揃う。
    /// 位置・向きを Inspector / Scene ギズモで動かして調整できる（全員同じ高さ＝身長非依存）。
    /// </summary>
    public sealed class SeatEyeGizmo : MonoBehaviour
    {
        private void OnDrawGizmos()
        {
            var p = transform.position;

            // 目の位置
            Gizmos.color = new Color(0.2f, 0.9f, 1f, 0.95f);
            Gizmos.DrawWireSphere(p, 0.09f);

            // 視線方向
            Gizmos.color = new Color(0.2f, 1f, 0.6f, 0.95f);
            Gizmos.DrawRay(p, transform.forward * 0.45f);
            Gizmos.DrawRay(p + transform.forward * 0.45f, (-transform.forward + transform.up) * 0.08f);
            Gizmos.DrawRay(p + transform.forward * 0.45f, (-transform.forward - transform.up) * 0.08f);

            // 床までの高さ（目線高の目安）
            Gizmos.color = new Color(1f, 1f, 1f, 0.3f);
            Gizmos.DrawLine(p, new Vector3(p.x, 0f, p.z));
        }
    }
}
