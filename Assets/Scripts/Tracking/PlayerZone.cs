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
        [SerializeField] private Vector3 halfExtents = new(1f, 2f, 1f);
        [SerializeField] private Vector3 centerOffset = Vector3.zero;
        [SerializeField, Min(0)] private int cameraIndex = 0;
        [SerializeField] private int priority = 0;
        [SerializeField] private string label = "";
        [SerializeField] private Color gizmoColor = new(0f, 1f, 0.5f, 0.25f);

        public int CameraIndex => cameraIndex;
        public int Priority => priority;
        public string Label => string.IsNullOrEmpty(label) ? name : label;
        public Vector3 HalfExtents => halfExtents;
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
