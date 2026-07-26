#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using FixedCamVr.Streaming.Cg;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// CG 人形（<see cref="ShowActorRig"/>）の見た目と腕の駆動を **Play せずに** PNG で確認する。
    ///
    /// Quest 実機も Link も無しに確かめられるのは次の 4 点で、どれも「実機で初めて壊れているのが分かる」
    /// のを避けるためのもの:
    ///   1. プレハブが Resources から読めて、専用シェーダで描かれる（真っ黒 / ピンクにならない）
    ///   2. show.json の heightM に合わせた縮尺が効いている（人形が巨大 / 極小にならない）
    ///   3. ハンドトラッキングの手の位置が腕に伝わる（上げる / 横に広げるで肘が自然に曲がる）
    ///   4. 手が取れない時は体側へ腕が降りる（idle・演出が止まらない）
    ///
    /// **視覚検証の作法**（.claude/rules/visual-verification.md）に従い、1 角度で判断しないよう
    /// 正面と斜めの 2 角度 × 4 ポーズを出す。出力は <c>Assets/Screenshots/actorviz/</c>。
    /// </summary>
    public static class ShowActorVizPreview
    {
        private const int Width = 960;
        private const int Height = 720;
        private const string OutDirRel = "Screenshots/actorviz";
        private const string DefaultActorResource = "ShowActors/Remy";
        private const string CgLayerName = "ShowCg";
        private const float ActorHeightM = 1.6f;    // show.json actors[].heightM の既定と同じ
        private const float HeadHeightM = 1.6f;     // 体験者の頭の高さ（実測値の目安）
        private const float ActorYawDeg = 180f;     // カメラ（-Z 側）を向かせる

        private struct Pose
        {
            public string name;
            public bool hasHands;
            public Vector3 leftOffset;    // 頭からの相対（ワールド）
            public Vector3 rightOffset;
        }

        private static readonly Pose[] Poses =
        {
            new() { name = "idle", hasHands = false },
            new() { name = "raise", hasHands = true,
                    leftOffset = new Vector3(-0.25f, 0.15f, 0.35f), rightOffset = new Vector3(0.25f, 0.15f, 0.35f) },
            new() { name = "wide", hasHands = true,
                    leftOffset = new Vector3(-0.60f, -0.10f, 0.10f), rightOffset = new Vector3(0.60f, -0.10f, 0.10f) },
            new() { name = "reach", hasHands = true,
                    leftOffset = new Vector3(-0.15f, -0.45f, 0.05f), rightOffset = new Vector3(0.30f, 0.35f, 0.45f) },
        };

        private struct Angle { public string name; public Vector3 pos; public Vector3 lookAt; }

        private static readonly Angle[] Angles =
        {
            new() { name = "front",   pos = new Vector3(0f, 1.25f, -2.6f),  lookAt = new Vector3(0f, 0.95f, 0f) },
            new() { name = "oblique", pos = new Vector3(1.9f, 1.6f, -1.9f), lookAt = new Vector3(0f, 0.95f, 0f) },
        };

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Show Actor", priority = 251)]
        private static void Run()
        {
            int layer = LayerMask.NameToLayer(CgLayerName);
            if (layer < 0)
            {
                Debug.LogError($"[ActorViz] レイヤ '{CgLayerName}' が未定義です。");
                return;
            }

            GameObject? prefab = Resources.Load<GameObject>(DefaultActorResource);
            if (prefab == null)
            {
                Debug.LogError($"[ActorViz] Resources/{DefaultActorResource} が見つかりません。" +
                               "先に Tools/FixedCamVr/Setup/Build Show Actor Prefab を実行してください。");
                return;
            }

            GameObject? root = null;
            RenderTexture? rt = null;
            Texture2D? tex = null;
            var saved = new List<string>();
            try
            {
                root = new GameObject("[ActorVizPreview]") { hideFlags = HideFlags.HideAndDontSave };

                GameObject actor = UnityEngine.Object.Instantiate(prefab, root.transform);
                actor.transform.localPosition = Vector3.zero;
                // カメラは -Z 側に置くので、人形はこちら（-Z）を向かせる＝ yaw 180。
                // 実行時の follow も体験者の頭 yaw で同じように回るので、腕の写像はこの向きで検証できる。
                actor.transform.localRotation = Quaternion.Euler(0f, ActorYawDeg, 0f);
                // ⚠ Edit Mode では骨を動かしてもスキニング結果が更新されず、**全ポーズが同じ絵になる**
                // （実測: idle と raise がバイト単位で同一の PNG になった）。毎レンダで再計算させる。
                foreach (var smr in actor.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    smr.forceMatrixRecalculationPerRender = true;
                    smr.updateWhenOffscreen = true;
                }
                var rig = actor.GetComponent<ShowActorRig>();
                if (rig == null) { Debug.LogError("[ActorViz] プレハブに ShowActorRig がありません。"); return; }
                rig.Prepare();

                // 実行時（ShowCgLayer.EnsureActor）と同じ縮尺の決め方。
                float k = Mathf.Clamp(ActorHeightM / Mathf.Max(0.1f, rig.MeasuredHeightM), 0.05f, 20f);
                actor.transform.localScale = Vector3.one * k;
                SetLayerRecursive(root.transform, layer);
                Debug.Log($"[ActorViz] 実寸 {rig.MeasuredHeightM:F2}m → 縮尺 {k:F3}（目標 {ActorHeightM:F2}m）" +
                          $" / 腕のボーン解決={(rig.HasRig ? "OK" : "NG（腕は動きません）")}");

                var camGo = new GameObject("ActorVizCamera") { hideFlags = HideFlags.HideAndDontSave };
                camGo.transform.SetParent(root.transform, false);
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.08f, 0.08f, 0.10f, 1f);
                cam.fieldOfView = 50f;
                cam.nearClipPlane = 0.03f;
                cam.farClipPlane = 30f;
                cam.cullingMask = 1 << layer;   // 開いているシーンの物を写さない

                rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { name = "ActorVizRT" };
                tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);

                string outDir = Path.Combine(Application.dataPath, OutDirRel);
                Directory.CreateDirectory(outDir);

                Vector3 head = new Vector3(0f, HeadHeightM, 0f);
                foreach (Pose p in Poses)
                {
                    // 追従の重み（0.35s）と平滑化を収束させてから撮る。実行時と同じ Drive を回すだけ。
                    rig.ResetPose();
                    var body = new ShowBodyInput(true, head, 0f,
                                                 p.hasHands, head + p.leftOffset,
                                                 p.hasHands, head + p.rightOffset);
                    for (int i = 0; i < 60; i++) rig.Drive(body, ActorYawDeg, 1f / 30f);

                    foreach (Angle a in Angles)
                    {
                        cam.transform.position = a.pos;
                        cam.transform.LookAt(a.lookAt, Vector3.up);
                        RenderToTexture(cam, rt, tex);
                        string path = Path.Combine(outDir, $"actorviz_{p.name}_{a.name}.png");
                        File.WriteAllBytes(path, tex.EncodeToPNG());
                        saved.Add(path);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[ActorViz] レンダ失敗: {e}");
            }
            finally
            {
                if (rt != null)
                {
                    RenderTexture.active = null;
                    rt.Release();
                    UnityEngine.Object.DestroyImmediate(rt);
                }
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                AssetDatabase.Refresh();
            }

            if (saved.Count > 0)
                Debug.Log($"[ActorViz] {saved.Count} 枚保存: Assets/{OutDirRel}/");
        }

        private static void SetLayerRecursive(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayerRecursive(t.GetChild(i), layer);
        }

        private static void RenderToTexture(Camera cam, RenderTexture rt, Texture2D tex)
        {
            cam.targetTexture = rt;
            // URP 2022.3 の Edit Mode では StandardRequest が destination に書かない実測があるため
            // Camera.Render() を正とする（RegistrationVizPreview と同じ判断）。
            cam.Render();
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0f, 0f, Width, Height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;
        }
    }
}
