#nullable enable
using System.Collections;
using TableDuoVr.Hands;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// キービジュアル撮影: 人役(Remy) と手役を「テーブルで一緒に遊んでいる」風の authored ポーズで配置し、
    /// シネマティックなカメラ＋暖色キーライトで高解像度スクショを1枚保存する。ネットワーク不要・単一プロセス。
    /// L0 desktop build を `-tdvKeyVisual on` で CLI 起動して発動（実機/Editor/MCP 不要）。
    /// 出力: persistentDataPath/keyvisual.png。OVRCameraRig / DebugCamera は切る。
    /// </summary>
    public sealed class KeyVisualDirector : MonoBehaviour
    {
        public void Run()
        {
            var rig = GameObject.Find("OVRCameraRig");
            if (rig != null) rig.SetActive(false);
            var dbg = GameObject.Find("DebugCamera");
            if (dbg != null) dbg.SetActive(false);

            var seat0 = SeatLocator.Find(0); // 人役
            var seat1 = SeatLocator.Find(1); // 手役
            if (seat0 != null) RemoteAvatarView.Create(seat0, handsOnly: false).PoseImmediate(RemyPose());
            if (seat1 != null) RemoteAvatarView.Create(seat1, handsOnly: true).PoseImmediate(HandPose());

            // 暖色キーライト（フラットな既定照明より立体感を出す）
            var lgt = new GameObject("KeyLight").AddComponent<Light>();
            lgt.type = LightType.Directional;
            lgt.intensity = 1.15f;
            lgt.color = new Color(1f, 0.96f, 0.88f);
            lgt.transform.rotation = Quaternion.Euler(40f, -38f, 0f);

            ComputeCam(seat0, seat1, out Vector3 camPos, out Vector3 look);
            var camGo = new GameObject("KeyVisualCamera");
            camGo.transform.position = camPos;
            camGo.transform.rotation = Quaternion.LookRotation(look - camPos, Vector3.up);
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 40f;
            cam.nearClipPlane = 0.03f;
            cam.farClipPlane = 100f;
            camGo.AddComponent<AudioListener>();

            StartCoroutine(Capture());
        }

        private IEnumerator Capture()
        {
            yield return new WaitForSeconds(1.5f); // SkinnedMesh の再スキン / Remy IK が落ち着くのを待つ
            string path = System.IO.Path.Combine(Application.persistentDataPath, "keyvisual.png");
            ScreenCapture.CaptureScreenshot(path, 2); // superSize 2 = 高解像度
            Debug.Log($"[TableDuo] キービジュアル保存 → {path}");
            yield return new WaitForSeconds(1.5f);     // PNG 書き出し待ち
            Application.Quit();                        // 撮影専用なのでプロセスを残さない
        }

        // 人役: テーブルへ少し前傾、右手を卓中央へ伸ばして「これ」と指す（カードゲームで盛り上がる感じ）。
        // 角度規約は TableDuoAvatarPreview の Neutral/Gesture に合わせる（小 Euler・z ひねり無し）。
        // 人役: 両手を体の近く低く置く（IK の伸び＝腕上がりを最小化）＋頭を相手（手役）へ向けて見つめる。
        // 「人が、差し出された宙の手を見つめる」非対称が映える。腕を遠くへ伸ばす動作は IK が暴れるので避ける。
        private static AvatarPose RemyPose() => new()
        {
            HeadPos = new Vector3(0f, -0.03f, 0.05f),
            HeadRot = Quaternion.Euler(12f, 0f, 0f),       // 卓越しに相手（手）の方を見る・やや伏し目
            WristPosR = new Vector3(0.17f, -0.46f, 0.18f), // 体に近く低い＝肘が自然に曲がり腕が上がらない
            WristRotR = Quaternion.Euler(20f, 0f, 0f),
            WristPosL = new Vector3(-0.17f, -0.46f, 0.18f),
            WristRotL = Quaternion.Euler(20f, 0f, 0f),
            TrackedR = true,
            TrackedL = true,
        };

        // 手役: 卓中央のカードへ手を低く差し出して指す（人と同じ場所へ「応える」）。片手のみ＝象徴的。
        private static AvatarPose HandPose() => new()
        {
            HeadPos = Vector3.zero,
            HeadRot = Quaternion.identity,
            WristPosR = new Vector3(0.04f, -0.42f, 0.40f),
            WristRotR = Quaternion.Euler(24f, 0f, 0f),
            TrackedR = true,
            TrackedL = false,
        };

        // 2席の中点を「横やや前・低め」から見るシネマティックな 3/4（俯瞰より顔と手元が映える）。
        private void ComputeCam(Transform? s0, Transform? s1, out Vector3 camPos, out Vector3 look)
        {
            if (s0 == null || s1 == null)
            {
                camPos = new Vector3(1.9f, 1.45f, -1.9f);
                look = new Vector3(0f, 0.92f, 0f);
                return;
            }
            Vector3 mid = (s0.position + s1.position) * 0.5f;
            look = new Vector3(mid.x, 0.86f, mid.z); // 卓・カードの高さに寄せる
            Vector3 axis = s1.position - s0.position; axis.y = 0f;
            Vector3 side = Vector3.Cross(axis.normalized, Vector3.up).normalized;
            float gap = axis.magnitude;
            // 横やや前・低めの 3/4 を少しタイトに
            camPos = mid + side * (gap * 0.3f + 0.98f) + axis.normalized * (gap * 0.12f) + Vector3.up * 0.72f;
        }
    }
}
