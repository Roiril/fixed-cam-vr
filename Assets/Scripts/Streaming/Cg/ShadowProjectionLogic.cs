#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming.Cg
{
    /// <summary>
    /// 平面投影シャドウの射影計算（純関数）。<c>ShowShadowProjector.shader</c> の頂点シェーダと**同じ式**で、
    /// 数値として固定しておくためにここへ切り出してある（シェーダは EditMode テストから叩けない）。
    ///
    /// 式を変えるときは <b>必ず両方</b>直すこと。片方だけ直すと沈黙して食い違い、
    /// 「テストは通るのに実機の影がずれる」になる（このプロジェクトが何度も踏んでいる形）。
    /// </summary>
    public static class ShadowProjectionLogic
    {
        /// <summary>
        /// これ以下の上向き成分では影を出さない。光が真横〜下から来ていると床との交点が無限遠へ飛び、
        /// そのまま描くと**画面いっぱいの黒帯**になる（分母を守るだけでは足りない）。
        /// </summary>
        public const float MinLightUp = 0.02f;

        /// <summary>
        /// z-fight 回避のために平面から浮かせる量 (m)。**シェーダ側だけが適用する**
        /// （幾何ではなく描画の都合なので、この純関数は素の平面 y を返す）。
        /// </summary>
        public const float PlaneBiasM = 0.002f;

        /// <summary>
        /// ワールド座標 <paramref name="posWS"/> を、光線方向に沿って高さ <paramref name="planeY"/> の
        /// 水平面へ落とす。<paramref name="lightDir"/> は「**光が来る向き**」（上向き成分が正）。
        /// 光が真横以下・長さゼロなら false（影を出してはいけないケース）。
        /// </summary>
        public static bool TryProjectToPlane(Vector3 posWS, float planeY, Vector3 lightDir, out Vector3 projected)
        {
            projected = posWS;
            if (lightDir.sqrMagnitude < 1e-8f) return false;   // 未設定のベクトルを「真上」と誤解しない
            Vector3 l = lightDir.normalized;
            if (l.y <= MinLightUp) return false;

            float t = (posWS.y - planeY) / l.y;
            Vector3 p = posWS - l * t;
            projected = new Vector3(p.x, planeY, p.z);
            return true;
        }
    }
}
