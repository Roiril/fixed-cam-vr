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
        [Tooltip("2席の中点から見たときの横オフセット倍率（席間距離×これ + 余白）。両者を等距離で profile 気味に収める")]
        [SerializeField] private float sideMargin = 1.9f;
        [Tooltip("注視点（席中点）からのカメラ高さ")]
        [SerializeField] private float cameraHeight = 1.7f;
        [Tooltip("席が見つからない時のフォールバック位置/注視")]
        [SerializeField] private Vector3 fallbackPosition = new(2.4f, 2.4f, -2.4f);
        [SerializeField] private Vector3 fallbackLookAt = new(0f, 0.9f, 0f);
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
            // L0 でフラット描画用の DebugCamera が出ていれば切る（観戦は俯瞰カメラ1本にする）
            var dbg = GameObject.Find("DebugCamera");
            if (dbg != null) dbg.SetActive(false);

            // 2席の中点を等距離から見るよう動的算出（hardcode のコーナー固定だと片方の席に寄って
            // もう片方＝特に手役が小さく見えにくかった。レイアウト非依存で両者を均等に framing）
            ComputeFraming(out Vector3 camPos, out Vector3 look);

            var go = new GameObject("SpectatorCamera");
            go.transform.SetParent(transform, worldPositionStays: false);
            go.transform.position = camPos;
            go.transform.rotation = Quaternion.LookRotation(look - camPos, Vector3.up);
            _cam = go.AddComponent<Camera>();
            _cam.fieldOfView = fieldOfView;
            _cam.nearClipPlane = 0.05f;
            _cam.farClipPlane = 100f;
            go.AddComponent<AudioListener>(); // 無効化した OVRCameraRig の AudioListener を補う

            Debug.Log($"[TableDuo] 観戦カメラ起動 pos={camPos:F2} look={look:F2}");

            // 診断モード（先置き）時は数秒後に観戦ビューを1枚 PNG 保存。
            // ヘッドレス/実機ゼロ（standalone CLI）でも framing を Read で確認できるようにする。
            if (TableDuoVr.Hands.StudyConfig.PreplaceAvatars)
            {
                StartCoroutine(CaptureAfter(6f));
            }
        }

        private System.Collections.IEnumerator CaptureAfter(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            string path = System.IO.Path.Combine(Application.persistentDataPath, "spectator_shot.png");
            ScreenCapture.CaptureScreenshot(path);
            Debug.Log($"[TableDuo] 観戦スクショ保存 → {path}");
        }

        /// <summary>2 席アンカーから「両者を等距離・横やや上から見る」位置と注視点を算出。席不在ならフォールバック。</summary>
        private void ComputeFraming(out Vector3 camPos, out Vector3 look)
        {
            var s0 = SeatLocator.Find(0);
            var s1 = SeatLocator.Find(1);
            if (s0 == null || s1 == null)
            {
                camPos = fallbackPosition;
                look = fallbackLookAt;
                return;
            }
            Vector3 p0 = s0.position, p1 = s1.position;
            Vector3 mid = (p0 + p1) * 0.5f;
            look = new Vector3(mid.x, 0.7f, mid.z); // テーブル＋着座者の下半身も入る高さ

            Vector3 axis = p1 - p0; axis.y = 0f; // 2席を結ぶ水平線
            if (axis.sqrMagnitude < 1e-4f) { camPos = fallbackPosition; return; }
            float gap = axis.magnitude;
            // 2席線に直交する水平方向＝両者を profile 気味に等距離で収められる側
            Vector3 side = Vector3.Cross(axis.normalized, Vector3.up).normalized;
            camPos = mid + side * (gap * 0.5f + sideMargin) + Vector3.up * cameraHeight;
        }
    }
}
