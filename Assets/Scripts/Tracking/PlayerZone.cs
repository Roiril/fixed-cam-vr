#nullable enable
using UnityEngine;

namespace FixedCamVr.Tracking
{
    /// <summary>
    /// プレイヤーが入った時に切り替えるカメラを定義する空間ゾーン。
    /// AABB（軸並行ボックス）で表現し、Transform に追従する（rotation は無視）。
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class PlayerZone : MonoBehaviour
    {
        [Tooltip("AABB の各軸の半長 (m)。負値は OnValidate で 0 にクランプされる。")]
        [SerializeField] private Vector3 halfExtents = new(1f, 2f, 1f);

        [Tooltip("Transform.position からの中心オフセット (m)。HMD 高さ (≈1.6m) を考慮する場合に使用。")]
        [SerializeField] private Vector3 centerOffset = Vector3.zero;

        [Tooltip("このゾーンに入った時にアクティブにする CameraStreamRegistry のインデックス。")]
        [SerializeField, Min(0)] private int cameraIndex = 0;

        [Tooltip("複数ゾーンが重なった時、値が大きいほど優先される。同値の場合は配列の先頭が優先。")]
        [SerializeField] private int priority = 0;

        [Tooltip("ログ表示用のラベル。空なら GameObject.name を使う。")]
        [SerializeField] private string label = "";

        [Tooltip("Scene ビューに描画する Gizmo の色 (alpha は面の濃さ)。")]
        [SerializeField] private Color gizmoColor = new(0f, 1f, 0.5f, 0.25f);

        /// <summary>このゾーンに入った時に切替対象となる CameraStreamRegistry のインデックス。</summary>
        public int CameraIndex => cameraIndex;

        /// <summary>選択優先度。値が大きいほど優先される。</summary>
        public int Priority => priority;

        /// <summary>ログ表示用ラベル。未設定時は GameObject 名を返す。</summary>
        public string Label => string.IsNullOrEmpty(label) ? name : label;

        /// <summary>各軸の半長 (m)。</summary>
        public Vector3 HalfExtents => halfExtents;

        /// <summary>ワールド座標での AABB 中心 (Transform.position + centerOffset)。</summary>
        public Vector3 Center => transform.position + centerOffset;

        /// <summary>
        /// world 座標 worldPos がこのゾーンに含まれるかを判定する。
        /// shrink を渡すと各軸を内側に縮めて判定（ヒステリシス用）。
        /// </summary>
        public bool Contains(Vector3 worldPos, float shrink = 0f)
        {
            Vector3 d = worldPos - Center;
            float hx = Mathf.Max(0f, halfExtents.x - shrink);
            float hy = Mathf.Max(0f, halfExtents.y - shrink);
            float hz = Mathf.Max(0f, halfExtents.z - shrink);
            return Mathf.Abs(d.x) <= hx && Mathf.Abs(d.y) <= hy && Mathf.Abs(d.z) <= hz;
        }

        private void OnValidate()
        {
            // halfExtents は非負に制限。Inspector からの誤入力やリファクタ時の負値を防ぐ。
            if (halfExtents.x < 0f) halfExtents.x = 0f;
            if (halfExtents.y < 0f) halfExtents.y = 0f;
            if (halfExtents.z < 0f) halfExtents.z = 0f;
        }

        private void OnDrawGizmos()
        {
            Color faceColor = gizmoColor;
            Color wireColor = new(gizmoColor.r, gizmoColor.g, gizmoColor.b, 1f);
            Vector3 size = halfExtents * 2f;

            Gizmos.color = faceColor;
            Gizmos.DrawCube(Center, size);
            Gizmos.color = wireColor;
            Gizmos.DrawWireCube(Center, size);
        }
    }
}
