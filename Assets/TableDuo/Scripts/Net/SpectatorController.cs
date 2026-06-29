#nullable enable
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 観戦者（第三者視点）用の固定俯瞰カメラ。<see cref="TableDuoPlayer"/> が Spectator ロールで
    /// 起動した時に <see cref="Activate"/> される。OVRCameraRig（HMD 視点）を止め、テーブルと
    /// 両プレイヤーを斜め上から見下ろす固定カメラへ切り替える。PC（Editor Play）で 2 人の
    /// インタラクションを外から客観視する用途。自由飛行カメラは将来拡張（plan 参照）。
    /// </summary>
    public sealed class SpectatorController : MonoBehaviour
    {
        [Tooltip("俯瞰カメラのワールド位置（テーブル中心を斜め上から見下ろす既定）")]
        [SerializeField] private Vector3 cameraPosition = new(2.4f, 2.4f, -2.4f);
        [Tooltip("カメラの注視ワールド点（テーブル中心・着座目線あたり）")]
        [SerializeField] private Vector3 lookAt = new(0f, 0.9f, 0f);
        [SerializeField] private float fieldOfView = 50f;

        private Camera? _cam;
        private bool _active;

        public void Activate()
        {
            if (_active) return;
            _active = true;

            // HMD 視点（OVRCameraRig）を止める。観戦は PC モニタ前提で VR レンダリング不要。
            // 型依存を避けるため名前で探して GameObject ごと無効化（AudioListener も止まる）。
            var rig = GameObject.Find("OVRCameraRig");
            if (rig != null) rig.SetActive(false);

            var go = new GameObject("SpectatorCamera");
            go.transform.SetParent(transform, worldPositionStays: false);
            go.transform.position = cameraPosition;
            go.transform.rotation = Quaternion.LookRotation(lookAt - cameraPosition, Vector3.up);
            _cam = go.AddComponent<Camera>();
            _cam.fieldOfView = fieldOfView;
            _cam.nearClipPlane = 0.05f;
            _cam.farClipPlane = 100f;
            go.AddComponent<AudioListener>(); // 無効化した OVRCameraRig の AudioListener を補う

            Debug.Log($"[TableDuo] 観戦カメラ起動 pos={cameraPosition} look={lookAt}");
        }
    }
}
