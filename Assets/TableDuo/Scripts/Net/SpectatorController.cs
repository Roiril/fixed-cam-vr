#nullable enable
using TableDuoVr.Hands;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 観戦者（第三者視点）用のカメラ。<see cref="TableDuoPlayer"/> が Spectator ロールで
    /// 起動した時に <see cref="Activate"/> される。OVRCameraRig（HMD 視点）を止め、テーブルと
    /// 両プレイヤーを斜め上から見下ろす固定カメラへ切り替える。PC（Editor Play / L0 desktop）で 2 人の
    /// インタラクションを外から客観視する用途。
    ///
    /// カメラモードは3つ（PC ホスト画面左上の GUI ボタン／数字キー 1-3 で切替）:
    /// - <see cref="ViewMode.Overhead"/>: 両者を等距離で俯瞰（既定）
    /// - <see cref="ViewMode.FullFpv"/>: 人役（席0）の一人称視点＝そのプレイヤーが見ている画
    /// - <see cref="ViewMode.HandFpv"/>: 手役（席1）の一人称視点
    /// 一人称時は対象プレイヤーの頭 world pose にカメラを毎フレ追従させ、当人の頭ジオメトリは潰して
    /// カメラに埋まらないようにする（胴・腕・手は残すので自分の体を見下ろせる）。純ローカル＝ネット非関与。
    /// </summary>
    public sealed class SpectatorController : MonoBehaviour
    {
        public enum ViewMode { Overhead, FullFpv, HandFpv }

        [Tooltip("一人称視点の視野角（フラットモニタ向け。実 HMD の 90 は歪むので狭め）")]
        [SerializeField] private float fpvFieldOfView = 60f;
        [Tooltip("一人称視点でカメラを頭 pose から前方（視線方向）へずらす量。頭は潰すので通常 0 でよい")]
        [SerializeField] private float fpvEyeForward = 0f;

        [Tooltip("2席の中点から見たときの横オフセット倍率（席間距離×これ + 余白）。両者を等距離で profile 気味に収める")]
        [SerializeField] private float sideMargin = 1.1f;   // やや寄せ（旧 1.9）
        [Tooltip("注視点（席中点）からのカメラ高さ")]
        [SerializeField] private float cameraHeight = 1.35f; // やや寄せ（旧 1.7）
        [Tooltip("席が見つからない時のフォールバック位置/注視")]
        [SerializeField] private Vector3 fallbackPosition = new(2.4f, 2.4f, -2.4f);
        [SerializeField] private Vector3 fallbackLookAt = new(0f, 0.9f, 0f);
        [SerializeField] private float fieldOfView = 43f;   // やや寄せ（旧 50）

        private Camera? _cam;
        private bool _active;

        private ViewMode _mode = ViewMode.Overhead;
        private TableDuoPlayer? _fpvTarget;   // 現在追従中のプレイヤー（FPV 時）
        private TableDuoPlayer? _collapsed;   // 頭を潰しているプレイヤー（戻す用）
        private bool _fpvReady;               // FPV 対象の頭 pose が取れているか（GUI 表示用）

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
                StartCoroutine(CaptureAfter(12f)); // 両プレイヤー接続＋手の出現を待ってから撮る
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

        // ── カメラモード（俯瞰／人役一人称／手役一人称）──────────────────────────

        private void Update()
        {
            if (!_active) return;
            // 数字キー 1/2/3 で切替（PC ホストのキーボード）
            if (Input.GetKeyDown(KeyCode.Alpha1)) SetMode(ViewMode.Overhead);
            else if (Input.GetKeyDown(KeyCode.Alpha2)) SetMode(ViewMode.FullFpv);
            else if (Input.GetKeyDown(KeyCode.Alpha3)) SetMode(ViewMode.HandFpv);
        }

        // RemoteAvatarView.Update（実行順 0）で頭 pose を平滑した「後」に追従したいので LateUpdate。
        private void LateUpdate()
        {
            if (!_active || _cam == null || _mode == ViewMode.Overhead) return;

            // 対象未取得（未接続／接続前に切替）なら都度取り直す。接続後は安定するので毎フレ Find は問題にならない
            if (_fpvTarget == null)
            {
                AcquireFpvTarget();
                if (_fpvTarget == null) { _fpvReady = false; return; }
            }

            if (_fpvTarget.TryGetRemoteHeadWorldPose(out var headPos, out var headRot))
            {
                _cam.transform.SetPositionAndRotation(
                    headPos + headRot * (Vector3.forward * fpvEyeForward), headRot);
                _cam.fieldOfView = fpvFieldOfView;
                _fpvReady = true;
            }
            else
            {
                _fpvReady = false; // 接続済みだが pose 未着（アバター生成前）: カメラは据え置き
            }
        }

        /// <summary>現在のモードに対応する席ロールのプレイヤーを取得し、頭を潰す。</summary>
        private void AcquireFpvTarget()
        {
            var role = _mode == ViewMode.FullFpv ? StudyConfig.Role.Full : StudyConfig.Role.Hand;
            _fpvTarget = FindPlayer(role);
            if (_fpvTarget != null)
            {
                _fpvTarget.SetRemoteHeadCollapsed(true);
                _collapsed = _fpvTarget;
            }
        }

        private static TableDuoPlayer? FindPlayer(StudyConfig.Role role)
        {
            foreach (var p in FindObjectsOfType<TableDuoPlayer>())
            {
                if (p.Role == role) return p;
            }
            return null;
        }

        public void SetMode(ViewMode mode)
        {
            _mode = mode;
            _fpvReady = false;

            // 直前に潰した頭を戻す（対象が despawn 済みなら Unity の == null が真になり自動スキップ）
            if (_collapsed != null) _collapsed.SetRemoteHeadCollapsed(false);
            _collapsed = null;
            _fpvTarget = null;

            if (_cam == null) return;

            if (mode == ViewMode.Overhead)
            {
                ComputeFraming(out Vector3 camPos, out Vector3 look);
                _cam.transform.SetPositionAndRotation(camPos, Quaternion.LookRotation(look - camPos, Vector3.up));
                _cam.fieldOfView = fieldOfView;
            }
            else
            {
                AcquireFpvTarget(); // 実追従は LateUpdate。ここでは対象取得＋頭潰しだけ
            }
        }

        private void OnGUI()
        {
            if (!_active) return;
            // Quest（Android）実機にはマウスカーソルが無いので出さない。ホスト＝PC のみ
            if (Application.platform == RuntimePlatform.Android) return;

            const float w = 156f, h = 122f;
            var area = new Rect(12f, 12f, w, h);
            GUILayout.BeginArea(area, GUI.skin.box);
            GUILayout.Label("観戦カメラ");
            ModeButton("俯瞰 (1)", ViewMode.Overhead);
            ModeButton("人役視点 (2)", ViewMode.FullFpv);
            ModeButton("手役視点 (3)", ViewMode.HandFpv);
            if (_mode != ViewMode.Overhead && !_fpvReady)
            {
                GUILayout.Label("（対象 未接続）");
            }
            GUILayout.EndArea();
        }

        private void ModeButton(string label, ViewMode mode)
        {
            var prev = GUI.color;
            if (_mode == mode) GUI.color = Color.cyan; // 選択中を強調
            if (GUILayout.Button(label)) SetMode(mode);
            GUI.color = prev;
        }
    }
}
