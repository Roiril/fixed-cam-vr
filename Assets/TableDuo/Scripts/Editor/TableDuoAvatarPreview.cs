#nullable enable
using System.IO;
using TableDuoVr.Hands;
using TableDuoVr.Net;
using UnityEditor;
using UnityEngine;

namespace TableDuoVr.EditorTools
{
    /// <summary>
    /// 人側フルアバター（簡易人型）の見た目を Play / 実機なしで確認するためのスクショ撮影ツール。
    /// アクティブシーンに一時オブジェクトを生成し、専用レイヤ + カメラ cullingMask で
    /// アバターだけを実 URP パイプラインで描画（＝実ゲームと同じ色味）。テーブル等の環境は写らない。
    /// 撮影後に一時オブジェクトは破棄する（シーンは保存しない）。
    /// 出力先は &lt;project&gt;/Temp/AvatarPreview/（gitignore 配下・Read で確認可）。
    ///
    /// 注意: 手は RemoteHandMeshProvider が居ない隔離環境では Meta 白メッシュにならず
    /// 手首プロキシ（小さな立方体）で代替表示される。体・腕・頭の形状/配色確認が目的。
    /// </summary>
    public static class TableDuoAvatarPreview
    {
        private const int Size = 720;
        private const int Layer = 31; // 既定未使用レイヤ。カメラ cullingMask でこれだけ写す

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Full Avatar (screenshot)", priority = 210)]
        public static void Capture()
        {
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "../Temp/AvatarPreview"));
            Directory.CreateDirectory(dir);

            GameObject? seat = null;
            GameObject? camGo = null;
            GameObject? lightGo = null;
            try
            {
                seat = new GameObject("PreviewSeat");
                seat.transform.position = Vector3.zero;
                var view = RemoteAvatarView.Create(seat.transform, handsOnly: false);

                // エディタ同期実行中はフレーム更新が無く SMR が初回姿勢でスキンを焼いたままになる。
                // レンダー毎に行列再計算を強制し、ポーズ切替が各ショットに反映されるようにする
                foreach (var smr in seat.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    smr.forceMatrixRecalculationPerRender = true;
                }

                // 専用ライト（シーンのライト状態に依存しないよう自前で1灯）
                lightGo = new GameObject("PreviewLight");
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.1f;
                light.transform.rotation = Quaternion.Euler(40f, -25f, 0f);

                camGo = new GameObject("PreviewCam");
                var cam = camGo.AddComponent<Camera>();
                cam.fieldOfView = 32f;
                cam.nearClipPlane = 0.03f;
                cam.farClipPlane = 50f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.60f, 0.62f, 0.65f);
                cam.cullingMask = 1 << Layer; // アバターだけを描画（テーブル等を除外）

                var aim = new Vector3(0f, -0.38f, 0.05f); // 座位フルボディの重心寄り

                // 初期（未トラッキング）状態＝構築直後の休めポーズ。pose を当てずに描画して
                // 「接続直後に腕が T 字・手が外向き」で固まっていないことを確認する
                SetLayerRecursive(seat, Layer);
                Shot(cam, dir, "00_initial_rest.png", new Vector3(0f, -0.30f, 3.0f), aim);
                Shot(cam, dir, "00b_initial_threequarter.png", new Vector3(2.1f, -0.15f, 2.2f), aim);

                view.PoseImmediate(NeutralPose());
                SetLayerRecursive(seat, Layer);
                Shot(cam, dir, "01_front_neutral.png", new Vector3(0f, -0.30f, 3.0f), aim);
                Shot(cam, dir, "02_threequarter.png", new Vector3(2.1f, -0.15f, 2.2f), aim);
                Shot(cam, dir, "04_side.png", new Vector3(3.0f, -0.38f, 0.05f), aim);

                view.PoseImmediate(GesturePose());
                SetLayerRecursive(seat, Layer);
                Shot(cam, dir, "03_front_gesture.png", new Vector3(0f, -0.30f, 3.0f), aim);

                Debug.Log($"[TableDuo] アバタープレビュー保存 → {dir}\n" +
                          "01_front_neutral.png / 02_threequarter.png / 03_front_gesture.png / 04_side.png");
            }
            finally
            {
                if (seat != null) Object.DestroyImmediate(seat);
                if (camGo != null) Object.DestroyImmediate(camGo);
                if (lightGo != null) Object.DestroyImmediate(lightGo);
            }
        }

        private static void Shot(Camera cam, string dir, string file, Vector3 camPos, Vector3 aim)
        {
            cam.transform.position = camPos;
            cam.transform.rotation = Quaternion.LookRotation(aim - camPos, Vector3.up);

            var rt = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            cam.targetTexture = rt;
            cam.Render();

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;

            File.WriteAllBytes(Path.Combine(dir, file), tex.EncodeToPNG());

            cam.targetTexture = null;
            Object.DestroyImmediate(tex);
            rt.Release();
            Object.DestroyImmediate(rt);
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform c in go.transform) SetLayerRecursive(c.gameObject, layer);
        }

        // 頭は原点（席ローカル）、両手首は前方やや下＝テーブル上の自然な構え。
        // 手首回転は bind（横向き）を前方へ向ける yaw（右 -90/左 +90）＋軽い伏せで「机に手を置く」向きに。
        private static AvatarPose NeutralPose()
        {
            return new AvatarPose
            {
                HeadPos = Vector3.zero,
                HeadRot = Quaternion.identity,
                WristPosR = new Vector3(0.22f, -0.42f, 0.36f),
                WristRotR = Quaternion.Euler(20f, -90f, 0f),
                WristPosL = new Vector3(-0.22f, -0.42f, 0.36f),
                WristRotL = Quaternion.Euler(20f, 90f, 0f),
                TrackedR = true,
                TrackedL = true,
            };
        }

        private static AvatarPose GesturePose()
        {
            return new AvatarPose
            {
                HeadPos = new Vector3(0.03f, 0f, 0f),
                HeadRot = Quaternion.Euler(0f, -14f, 0f),
                WristPosR = new Vector3(0.30f, -0.04f, 0.24f),  // 右手を上げる
                WristRotR = Quaternion.Euler(-20f, 0f, 0f),
                WristPosL = new Vector3(-0.22f, -0.42f, 0.34f),
                WristRotL = Quaternion.Euler(10f, 0f, 0f),
                TrackedR = true,
                TrackedL = true,
            };
        }
    }
}
