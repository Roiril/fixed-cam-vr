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

                // 実録画の指ポーズで Remy 指リターゲット（P3）を検証（05/06）。
                // ovrBind に録画同梱の実 layout を使い、実機と同じ入力系列で軸ズレ/巻き込みを見る
                CaptureRealFingerShots(view, seat, cam, dir);

                Debug.Log($"[TableDuo] アバタープレビュー保存 → {dir}\n" +
                          "01_front_neutral.png / 02_threequarter.png / 03_front_gesture.png / 04_side.png / " +
                          "05_fingers_front.png / 06_fingers_close.png（実録画指ポーズ）");
            }
            finally
            {
                if (seat != null) Object.DestroyImmediate(seat);
                if (camGo != null) Object.DestroyImmediate(camGo);
                if (lightGo != null) Object.DestroyImmediate(lightGo);
            }
        }

        /// <summary>
        /// 実トラッキング録画（TestData/tdv_handrec_real_*.bin）の表情豊かなフレームで Remy の
        /// 指リターゲットを撮る。Captured layout を録画同梱の実 layout に差し替え（ovrBind）、
        /// 撮影後に必ず復元する（static 汚染防止）。録画が無ければスキップ。
        /// </summary>
        private static void CaptureRealFingerShots(RemoteAvatarView view, GameObject seat, Camera cam, string dir)
        {
            string proj = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var rec = Playback.LoadRecordingForPreview(proj);
            if (rec == null || rec.Frames.Count == 0)
            {
                Debug.LogWarning("[TableDuo] 実録画（TestData/tdv_handrec_real_*.bin）が無いため指ショットをスキップ");
                return;
            }

            var prevL = HandSkeletonLayout.CapturedL;
            var prevR = HandSkeletonLayout.CapturedR;
            try
            {
                HandSkeletonLayout.CapturedL = rec.LayoutL;
                HandSkeletonLayout.CapturedR = rec.LayoutR;

                var frame = rec.Frames[Playback.PickExpressiveFrame(rec)];
                var p = new AvatarPose();
                p.CopyFrom(frame);
                // 手首はフレーミング固定（卓上の見やすい位置）に差し替え、指 bone は実録画のまま
                p.WristPosR = new Vector3(0.24f, -0.30f, 0.36f);
                p.WristRotR = Quaternion.Euler(-30f, -90f, 0f); // 甲をカメラへ（指の曲がりが見える向き）
                p.WristPosL = new Vector3(-0.24f, -0.42f, 0.36f);
                p.WristRotL = Quaternion.Euler(20f, 90f, 0f);
                p.TrackedL = true;
                p.TrackedR = true;

                view.PoseImmediate(p);
                SetLayerRecursive(seat, Layer);
                var aim = new Vector3(0f, -0.30f, 0.2f);
                Shot(cam, dir, "05_fingers_front.png", new Vector3(0f, -0.15f, 2.4f), aim);
                // 右手クローズアップ（指の節ごとの曲がり・巻き込み/反りの検証用）
                var handAim = new Vector3(0.24f, -0.30f, 0.36f);
                Shot(cam, dir, "06_fingers_close.png", handAim + new Vector3(0.05f, 0.25f, 0.75f), handAim);
            }
            finally
            {
                HandSkeletonLayout.CapturedL = prevL;
                HandSkeletonLayout.CapturedR = prevR;
            }
        }

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Hand Role Initial (screenshot)", priority = 214)]
        public static void CaptureHandRoleInitial()
        {
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "../Temp/AvatarPreview"));
            Directory.CreateDirectory(dir);

            // 白手メッシュ供給元。Edit モードは Awake 未実行で Instance=null のため手動注入する。
            var provider = Object.FindObjectOfType<RemoteHandMeshProvider>();
            if (provider == null)
            {
                Debug.LogError("[TableDuo] RemoteHandMeshProvider がシーンに無い。TableDuoMain を開いて Setup 済みか確認。");
                return;
            }
            var instProp = typeof(RemoteHandMeshProvider).GetProperty("Instance",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            var prevInstance = instProp?.GetValue(null);
            // 手 layout（bind）＝録画同梱の実 layout。無いと bone マッピングが立たず proxy 立方体に落ちる
            var rec = Playback.LoadRecordingForPreview(Path.GetFullPath(Path.Combine(Application.dataPath, "..")));
            var prevL = HandSkeletonLayout.CapturedL;
            var prevR = HandSkeletonLayout.CapturedR;

            GameObject? seat = null, camGo = null, lightGo = null;
            try
            {
                instProp?.SetValue(null, provider);
                if (rec != null && rec.Frames.Count > 0)
                {
                    HandSkeletonLayout.CapturedL = rec.LayoutL;
                    HandSkeletonLayout.CapturedR = rec.LayoutR;
                }

                seat = new GameObject("PreviewSeat");
                seat.transform.position = Vector3.zero;
                // 手役＝相手から見た手だけアバター。構築時に右手を休めポーズで即表示（ShowAtRest）
                var view = RemoteAvatarView.Create(seat.transform, handsOnly: true);

                foreach (var smr in seat.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    smr.forceMatrixRecalculationPerRender = true;
                SetLayerRecursive(seat, Layer);

                lightGo = new GameObject("PreviewLight");
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.1f;
                light.transform.rotation = Quaternion.Euler(40f, -25f, 0f);

                camGo = new GameObject("PreviewCam");
                var cam = camGo.AddComponent<Camera>();
                cam.fieldOfView = 40f;
                cam.nearClipPlane = 0.03f;
                cam.farClipPlane = 50f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.60f, 0.62f, 0.65f);
                cam.cullingMask = 1 << Layer;

                // 休め右手は席ローカル (0.20,-0.42,0.32) 付近＝卓上前方。手（約15cm）に寄る
                var aim = new Vector3(0.28f, -0.40f, 0.36f);
                Shot(cam, dir, "10_handrole_initial_front.png", aim + new Vector3(0f, 0.06f, -0.55f), aim);
                Shot(cam, dir, "11_handrole_initial_threequarter.png", aim + new Vector3(-0.4f, 0.28f, -0.4f), aim);
                Shot(cam, dir, "12_handrole_initial_top.png", aim + new Vector3(0f, 0.6f, -0.12f), aim);
                Debug.Log($"[TableDuo] 手役 初期ポーズ preview → {dir}\n" +
                          "10_handrole_initial_front / 11_..threequarter / 12_..top（接続直後 ShowAtRest）");
            }
            finally
            {
                if (seat != null) Object.DestroyImmediate(seat);
                if (camGo != null) Object.DestroyImmediate(camGo);
                if (lightGo != null) Object.DestroyImmediate(lightGo);
                instProp?.SetValue(null, prevInstance);
                HandSkeletonLayout.CapturedL = prevL;
                HandSkeletonLayout.CapturedR = prevR;
            }
        }

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Self Body (first-person)", priority = 211)]
        public static void CaptureSelfBody()
        {
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "../Temp/AvatarPreview"));
            Directory.CreateDirectory(dir);
            var remyPrefab = Resources.Load<GameObject>("RemyFullAvatar");
            if (remyPrefab == null) { Debug.LogError("[TableDuo] RemyFullAvatar prefab が無い"); return; }

            GameObject? seat = null, camGo = null, lightGo = null;
            try
            {
                seat = new GameObject("PreviewSeat");
                seat.transform.position = Vector3.zero; // 席原点＝一人称カメラ（目線アンカー）
                var rig = new RemyAvatarRig(seat.transform, remyPrefab, firstPerson: true);

                // 卓上に手を置き、やや下を向いた自然な座位で駆動（実録画の指があれば使う）
                var rec = Playback.LoadRecordingForPreview(Path.GetFullPath(Path.Combine(Application.dataPath, "..")));
                var prevL = HandSkeletonLayout.CapturedL; var prevR = HandSkeletonLayout.CapturedR;
                var p = SelfBodyPose();
                HandSkeletonLayout? layL = null, layR = null;
                if (rec != null && rec.Frames.Count > 0)
                {
                    HandSkeletonLayout.CapturedL = rec.LayoutL; HandSkeletonLayout.CapturedR = rec.LayoutR;
                    layL = rec.LayoutL; layR = rec.LayoutR;
                    var f = rec.Frames[Playback.PickExpressiveFrame(rec)];
                    System.Array.Copy(f.BonesR, p.BonesR, AvatarPose.BonesPerHand);
                    System.Array.Copy(f.BonesL, p.BonesL, AvatarPose.BonesPerHand);
                }
                try { rig.Drive(p, layL, layR); }
                finally { HandSkeletonLayout.CapturedL = prevL; HandSkeletonLayout.CapturedR = prevR; }

                foreach (var smr in seat.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    smr.forceMatrixRecalculationPerRender = true;
                SetLayerRecursive(seat, Layer);

                lightGo = new GameObject("PreviewLight");
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional; light.intensity = 1.1f;
                light.transform.rotation = Quaternion.Euler(50f, -20f, 0f);

                camGo = new GameObject("PreviewCam");
                var cam = camGo.AddComponent<Camera>();
                cam.fieldOfView = 82f; // VR に近い広画角（一人称で体がどう見えるか）
                cam.nearClipPlane = 0.03f; cam.farClipPlane = 50f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.30f, 0.30f, 0.32f);
                cam.cullingMask = 1 << Layer;

                // 手首向きの正解基準: 白手（OVRCustomHandPrefab_R）を同じ wristRot で Remy の右手の
                // すぐ横に置く（メッシュ bind=アンカーなので localRotation=wristRot が常に正しい向き）。
                // Remy 手と白手ゴーストの向きが一致していれば手首合成（OvrAnchorToRemyBind）が正しい
                var ghostPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Packages/com.meta.xr.sdk.core/Prefabs/OVRCustomHandPrefab_R.prefab");
                Transform? remyRHand = FindDeep(seat.transform, "mixamorig:RightHand");
                if (ghostPrefab != null)
                {
                    var ghost = (GameObject)Object.Instantiate(ghostPrefab, seat.transform, false);
                    foreach (var c in ghost.GetComponentsInChildren<MonoBehaviour>(true)) Object.DestroyImmediate(c);
                    ghost.transform.localPosition = p.WristPosR + new Vector3(0.16f, 0f, 0f); // Remy 右手の横
                    ghost.transform.localRotation = p.WristRotR;
                    SetLayerRecursive(ghost, Layer);

                    // 数値でも突き合わせ（スクショの画角に依存しない一次証拠）:
                    // Remy 手の「指方向・手の甲方向」を席ローカルで測り、ゴースト（正解）と比較する
                    if (remyRHand != null)
                    {
                        Transform? mid = FindDeep(remyRHand, "mixamorig:RightHandMiddle1");
                        Vector3 handLocal = seat.transform.InverseTransformPoint(remyRHand.position);
                        Vector3 fRemy = mid != null
                            ? seat.transform.InverseTransformDirection((mid.position - remyRHand.position).normalized)
                            : Vector3.zero;
                        // ゴースト（白手）の指方向は**メッシュ実ボーン**から測る（式や仮定を使わない＝真の基準。
                        // 自作の f0 仮定と比較すると循環になるため）
                        Transform? gWrist = FindDeep(ghost.transform, "b_r_wrist");
                        Transform? gMid = FindDeep(ghost.transform, "b_r_middle1");
                        Vector3 fGhost = (gWrist != null && gMid != null)
                            ? seat.transform.InverseTransformDirection((gMid.position - gWrist.position).normalized)
                            : Vector3.zero;
                        // 手の甲方向も実測（b0=down 仮定の検証）。remy 側も同様に測る
                        Transform? gIdx = FindDeep(ghost.transform, "b_r_index1");
                        Transform? gPnk = FindDeep(ghost.transform, "b_r_pinky1");
                        Vector3 bGhost = Vector3.zero;
                        if (gIdx != null && gPnk != null && fGhost != Vector3.zero)
                        {
                            var sG = seat.transform.InverseTransformDirection(gIdx.position - gPnk.position);
                            bGhost = Vector3.Cross(sG, fGhost).normalized;
                        }
                        Transform? rIdx = FindDeep(remyRHand, "mixamorig:RightHandIndex1");
                        Transform? rPnk = FindDeep(remyRHand, "mixamorig:RightHandPinky1");
                        Vector3 bRemy = Vector3.zero;
                        if (rIdx != null && rPnk != null && fRemy != Vector3.zero)
                        {
                            var sR = seat.transform.InverseTransformDirection(rIdx.position - rPnk.position);
                            bRemy = Vector3.Cross(sR, fRemy).normalized;
                        }
                        Debug.Log($"[TDV-CALIB] R target={p.WristPosR:F3} remyHandLocal={handLocal:F3} " +
                                  $"fingerDir remy={fRemy:F3} ghostMesh={fGhost:F3} angle={Vector3.Angle(fRemy, fGhost):F1}deg | " +
                                  $"backDir remy={bRemy:F3} ghost={bGhost:F3} bAngle={Vector3.Angle(bRemy, bGhost):F1}deg");
                    }
                }

                // 一人称カメラ＝目線アンカー（席原点）から少し下を向く。頭が潰れて視界を塞がないか＋
                // 胴/腕/手が下方に見えるかを確認
                Shot(cam, dir, "07_selfbody_lookdown.png", Vector3.zero, new Vector3(0f, -0.6f, 0.7f));
                Shot(cam, dir, "08_selfbody_straight.png", Vector3.zero, new Vector3(0f, -0.15f, 1f));
                // 右手クローズアップ（白手ゴーストとの向き比較）
                var rh = p.WristPosR;
                Shot(cam, dir, "09_selfbody_hand_calib.png", rh + new Vector3(0.08f, 0.35f, 0.55f), rh + new Vector3(0.08f, 0f, 0f));
                Debug.Log($"[TableDuo] 一人称自己ボディ preview → {dir}\n07_selfbody_lookdown / 08_selfbody_straight / 09_selfbody_hand_calib（白手=正解基準と並置）");
            }
            finally
            {
                if (seat != null) Object.DestroyImmediate(seat);
                if (camGo != null) Object.DestroyImmediate(camGo);
                if (lightGo != null) Object.DestroyImmediate(lightGo);
            }
        }

        // 卓上に両手を置いた座位（頭は正面・自己ボディが下に見える構え）
        private static AvatarPose SelfBodyPose() => new()
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

        private static Transform? FindDeep(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var f = FindDeep(root.GetChild(i), name);
                if (f != null) return f;
            }
            return null;
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
