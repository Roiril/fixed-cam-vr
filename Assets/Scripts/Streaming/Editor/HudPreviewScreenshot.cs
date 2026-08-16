#nullable enable
using System.IO;
using System.Reflection;
using FixedCamVr.Diagnostics;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// StatusHud / ControllerGuidePanel の見た目を Play モード無しで PNG 化する Editor プレビュー。
    /// Play モードは MCP ブリッジを落とす既知の wedge があるため（mcp-unity.md）、Edit モードのまま
    /// 実コンポーネントへ状態を注入 → private の描画/配置メソッドを reflection で 1 回駆動 →
    /// 専用カメラで RenderTexture に描いて Screenshots/hud-preview/ へ保存する。
    ///
    /// RegistrationVizPreview.cs と同じ「メニュー実行 → PNG → mtime 確認 → Read」の検証フロー用。
    /// シーンは汚さない（生成した一時オブジェクトは finally で全削除。コンポーネント状態の変更は
    /// SetVisible / SetMode のみで、シーン保存はしない）。
    /// </summary>
    public static class HudPreviewScreenshot
    {
        private const string OutDirRel = "Screenshots/hud-preview";
        private const string MainScenePath = "Assets/Scenes/Main.unity";
        private const int Width = 1600;
        private const int Height = 1200;

        /// <summary>
        /// 旧 batchmode 用エントリ。<see cref="Capture"/> が CLI から呼ばれたときに自分でシーンを開く
        /// ようになったので**もう要らない**（`unity.ps1 menu hud` は Capture を呼ぶ）。
        /// 過去の手順書・memory がこの名前を指しているので残してある。
        /// </summary>
        public static void CaptureBatch() => Capture();

        /// <summary>
        /// ⚠ <c>-nographics</c> を付けると <c>Camera.Render</c> が動かない。
        /// <c>unity run</c> は付けないので CLI からはそのまま撮れる。
        /// </summary>
        [MenuItem("Tools/FixedCamVr/Preview/HUD Preview (screenshot)", priority = 80)]
        public static void Capture()
        {
            // CLI（batchmode）は空シーンで始まるので自分で Main を開く。GUI では何もしない。
            if (!EditorCliArgs.EnsureScene(MainScenePath)) return;

            var hud = Object.FindObjectOfType<StatusHud>(includeInactive: true);
            var panel = Object.FindObjectOfType<ControllerGuidePanel>(includeInactive: true);
            if (hud == null || panel == null)
            {
                Debug.LogError($"[HudPreview] StatusHud={(hud != null)} / ControllerGuidePanel={(panel != null)} — " +
                               "Main.unity で Setup 済みのシーンを開いてから実行してください");
                return;
            }

            var temps = new System.Collections.Generic.List<GameObject>();
            try
            {
                // ---- 疑似アンカー（頭・右コントローラ）をシーン原点付近に立てる ----
                var headGo = new GameObject("[HudPreview] Head");
                temps.Add(headGo);
                headGo.transform.SetPositionAndRotation(new Vector3(0f, 1.6f, 0f), Quaternion.identity);

                var ctrlGo = new GameObject("[HudPreview] RightController");
                temps.Add(ctrlGo);
                // 右手で自然に持った位置: 頭の右下前方
                ctrlGo.transform.position = new Vector3(0.25f, 1.15f, 0.45f);

                // ---- StatusHud: 表示 ON + head 差し替え + private 描画/配置を 1 回駆動 ----
                SetPrivateField(hud, "head", headGo.transform);
                // 授業済みシーンのスクリーン Quad（authored 位置）が head 前方 1.6m より手前にあり
                // 実配置だと隠れて写らないため、プレビューに限り手前へ寄せる（見た目確認用・シーン非保存）。
                SetPrivateField(hud, "distance", 0.9f);
                SetPrivateField(hud, "heightOffset", -0.24f);
                hud.SetVisible(true);
                // RenderContent は「text.enabled かつ間引き間隔内」だと SetText をスキップする
                // （updateInterval 間引き）。シーン保存状態の enabled=true が残っていると空のまま
                // 撮れてしまうため、明示的に disabled へ落として間引き分岐を回避する。
                var hudTmp = GetTmp(hud);
                if (hudTmp != null) hudTmp.enabled = false;
                Invoke(hud, "RenderContent");
                // ApplyPose(yaw) で頭の正面へ配置（yaw=0 = +Z 方向）
                InvokeWith(hud, "ApplyPose", 0f);

                // ---- ControllerGuidePanel: NORMAL 本文 + アンカー差し替え + LateUpdate 1 回駆動 ----
                SetPrivateField(panel, "controller", ctrlGo.transform);
                SetPrivateField(panel, "head", headGo.transform);
                // パネルは「スタッフが被っているか」を StatusHud に問う（2026-08-07〜）。Edit モードでは
                // Awake が走らないので明示的に差す — 未配線のシーンだと自己解決に頼ることになり、
                // 見た目確認の一次証拠が空の PNG になる。
                SetPrivateField(panel, "statusHud", hud);
                panel.SetControllerState(true, true);
                panel.SetMode("NORMAL");
                Invoke(panel, "LateUpdate");
                Invoke(panel, "LateUpdate"); // SmoothDamp 初回シード後にもう 1 回（スナップ確定）

                // ---- Edit モードでは Awake（日本語フォント差し替え）が走っていないため明示適用し、
                //      TMP の遅延メッシュ生成も強制する（これが無いと豆腐 / 空白 PNG になる）----
                var jp = JapaneseHudFont.TryGet();
                ForceTmpReady(hud, jp);
                ForceTmpReady(panel, jp);
                UnityEngine.Canvas.ForceUpdateCanvases();

                // ---- 頭の位置から撮る（両パネルとも頭へ正対 billboard のため、頭からが正しい見え方）----
                string dir = Path.Combine(Application.dataPath, OutDirRel);
                Directory.CreateDirectory(dir);
                string stamp = System.DateTime.Now.ToString("yyyyMMdd-HHmmss");

                // 1 枚目: 正面（StatusHud が視界中央下に見える画）
                Shoot(headGo.transform.position, Quaternion.Euler(10f, 0f, 0f), 70f,
                      Path.Combine(dir, $"hud-{stamp}-status.png"));
                // 2 枚目: 右下のコントローラ方向を見た画（ガイドパネル）
                Vector3 toPanel = (panel.transform.position - headGo.transform.position).normalized;
                Shoot(headGo.transform.position, Quaternion.LookRotation(toPanel, Vector3.up), 55f,
                      Path.Combine(dir, $"hud-{stamp}-guide.png"));

                Debug.Log($"[HudPreview] 保存: Assets/{OutDirRel}/hud-{stamp}-status.png / hud-{stamp}-guide.png");
            }
            finally
            {
                foreach (var go in temps) Object.DestroyImmediate(go);
            }
        }

        private static void Shoot(Vector3 pos, Quaternion rot, float fov, string path)
        {
            var camGo = new GameObject("[HudPreview] Camera");
            try
            {
                var cam = camGo.AddComponent<Camera>();
                camGo.transform.SetPositionAndRotation(pos, rot);
                cam.fieldOfView = fov;
                cam.nearClipPlane = 0.05f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.08f, 0.08f, 0.10f, 1f); // 実機の暗環境相当の無地背景

                var rt = new RenderTexture(Width, Height, 24);
                var tex = new Texture2D(Width, Height, TextureFormat.RGB24, mipChain: false);
                try
                {
                    cam.targetTexture = rt;
                    cam.Render();
                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                    tex.Apply();
                    File.WriteAllBytes(path, tex.EncodeToPNG());
                }
                finally
                {
                    cam.targetTexture = null;
                    RenderTexture.active = null;
                    Object.DestroyImmediate(rt);
                    Object.DestroyImmediate(tex);
                }
            }
            finally
            {
                Object.DestroyImmediate(camGo);
            }
        }

        // private field "text"（TMP_Text）を取得する。
        private static TMPro.TMP_Text? GetTmp(Component holder)
        {
            var f = holder.GetType().GetField("text", BindingFlags.Instance | BindingFlags.NonPublic);
            if (f?.GetValue(holder) is TMPro.TMP_Text tmp) return tmp;
            Debug.LogError($"[HudPreview] {holder.GetType().Name} の text（TMP_Text）を取得できない");
            return null;
        }

        // private field "text"（TMP_Text）へ日本語フォントを適用し、遅延メッシュを即時生成する。
        // ForceMeshUpdate は 2 回呼ぶ（1 回目で dynamic atlas へのグリフ焼き付けを要求し、
        // 2 回目で焼けたグリフを含めて再レイアウトする。batchmode の豆腐対策）。
        private static void ForceTmpReady(Component holder, TMPro.TMP_FontAsset? jpFont)
        {
            var tmp = GetTmp(holder);
            if (tmp == null) return;
            tmp.enabled = true;
            if (jpFont != null) tmp.font = jpFont;
            tmp.ForceMeshUpdate(ignoreActiveState: true, forceTextReparsing: true);
            tmp.ForceMeshUpdate(ignoreActiveState: true, forceTextReparsing: true);
        }

        private static void SetPrivateField(object target, string name, object value)
        {
            var f = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (f == null) Debug.LogError($"[HudPreview] field 不明: {target.GetType().Name}.{name}");
            else f.SetValue(target, value);
        }

        private static void Invoke(object target, string method)
        {
            var m = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            if (m == null) Debug.LogError($"[HudPreview] method 不明: {target.GetType().Name}.{method}");
            else m.Invoke(target, null);
        }

        private static void InvokeWith(object target, string method, params object[] args)
        {
            var m = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            if (m == null) Debug.LogError($"[HudPreview] method 不明: {target.GetType().Name}.{method}");
            else m.Invoke(target, args);
        }
    }
}
