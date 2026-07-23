#nullable enable
using System.Collections.Generic;
using System.IO;
using TableDuoVr.Hands;
using TableDuoVr.Net;
using UnityEditor;
using UnityEngine;

namespace TableDuoVr.EditorTools
{
    /// <summary>
    /// 開いている TableDuoMain の卓上（海底探検の盤面）を複数角度でスクショする診断ツール。
    /// Play 不要・現シーンをそのまま撮る。配置・スケール調整の視覚検証用
    /// （visual-verification ルール: 判断は 1 角度でしない → 斜め/両席/真上を一括出力）。
    /// 出力: Temp/TablePreview/*.png
    ///
    /// 着座つき（2026-07-08 / 2026-07-09 役割別化）: 実運用の接続直後配置に合わせ、
    /// 人役席（Full=Seat0）へ座位 Remy、手役席（Hand=Seat1）へ白手 rest ポーズ（ShowAtRest）を
    /// 一時生成して寸法感（体 vs 机の高さ・リーチ・手の届き）を確認できる。
    /// ランタイム生成物ではなくプレビュー内だけ（撮影後に破棄）。
    /// </summary>
    public static class TableDuoTablePreview
    {
        private const int Size = 1100;

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Table (screenshot)", priority = 214)]
        public static void Capture() => CaptureInternal(withRemy: false);

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Table + Remy seated", priority = 215)]
        public static void CaptureWithRemy() => CaptureInternal(withRemy: true);

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Table (Geister)", priority = 216)]
        public static void CaptureGeister() => CaptureWithActiveGame("Game_geister");

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Table (Algo)", priority = 216)]
        public static void CaptureAlgo() => CaptureWithActiveGame("Game_algo");

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Table (Bandido)", priority = 216)]
        public static void CaptureBandido() => CaptureWithActiveGame("Game_bandido");

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Table (SixStrokesBear)", priority = 217)]
        public static void CaptureSixStrokesBear() => CaptureWithActiveGame("Game_bear");

        /// <summary>
        /// stow ベイクで不可視のゲームを撮影中だけ表示に入れ替えて撮る（他ゲームは非表示化）。
        /// GameSwitcher のランタイム挙動には触れず Renderer.enabled のみ往復する。
        /// </summary>
        private static void CaptureWithActiveGame(string gameRootName)
        {
            var props = GameObject.Find("[TableDuo]/Props");
            if (props == null)
            {
                Debug.LogWarning("[TablePreview] [TableDuo]/Props が見つからない（Setup 済みか確認）");
                return;
            }
            var restore = new List<(Renderer r, bool enabled)>();
            try
            {
                foreach (Transform game in props.transform)
                {
                    bool show = game.name == gameRootName;
                    foreach (var r in game.GetComponentsInChildren<Renderer>(true))
                    {
                        restore.Add((r, r.enabled));
                        r.enabled = show;
                    }
                }
                CaptureInternal(withRemy: false);
            }
            finally
            {
                foreach (var (r, enabled) in restore) if (r != null) r.enabled = enabled;
            }
        }

        private static void CaptureInternal(bool withRemy)
        {
            var table = GameObject.Find("[TableDuo]/Table") ?? GameObject.Find("Table");
            Vector3 center;
            float topY;
            float tableHx = 0.5f, tableHz = 0.4f; // 天板半径（既定・見つからない時）
            if (table != null)
            {
                var b = CalcBounds(table);
                center = new Vector3(b.center.x, b.max.y, b.center.z);
                topY = b.max.y;
                tableHx = b.extents.x;
                tableHz = b.extents.z;
            }
            else
            {
                center = new Vector3(0f, 0.75f, 0f);
                topY = 0.75f;
                Debug.LogWarning("[TablePreview] Table が見つからないため既定位置で撮影");
            }
            // 天板全体（+X 端の山札含む）が枠に収まるよう、天板半径に応じてカメラを引く。
            // baseline 0.45m（旧席デザインの半径感）を 1 として拡縮。1 未満には縮めない
            float frame = Mathf.Clamp(Mathf.Max(tableHx, tableHz) / 0.45f, 1f, 3f);

            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "../Temp/TablePreview"));
            Directory.CreateDirectory(dir);

            var camGo = new GameObject("TablePreviewCam");
            var lightGo = new GameObject("TablePreviewLight");
            var remyInstances = new List<GameObject>();
            System.Action? restoreHandEnv = null;
            try
            {
                if (withRemy) restoreHandEnv = SeatRoles(remyInstances);

                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.16f, 0.17f, 0.20f);
                cam.fieldOfView = 50f;
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.1f;
                lightGo.transform.rotation = Quaternion.Euler(55f, -25f, 0f);

                // 斜め上（人役側/手役側）・低め斜め・真上。オフセットを frame 倍して天板全体を収める
                Shot(cam, dir, "diag_fullside.png", center + new Vector3(0.55f, 0.75f, -0.85f) * frame, center);
                Shot(cam, dir, "diag_handside.png", center + new Vector3(-0.55f, 0.75f, 0.85f) * frame, center);
                Shot(cam, dir, "low_fullside.png", center + new Vector3(0.0f, 0.35f, -0.75f) * frame, center + new Vector3(0f, 0.02f, 0f));
                Shot(cam, dir, "top.png", center + new Vector3(0f, 1.1f * frame, 0.001f), center);

                if (withRemy)
                {
                    // 寸法感用: 座位 Remy 全身と机を一緒に真横〜斜めから（体高 vs 天板高・肘/手の届き）
                    var bodyAim = new Vector3(center.x, topY + 0.15f, center.z);
                    Shot(cam, dir, "remy_side.png", bodyAim + new Vector3(2.4f, 0.15f, 0f), bodyAim);
                    Shot(cam, dir, "remy_front_full.png", bodyAim + new Vector3(0f, 0.10f, -2.6f), bodyAim);
                    Shot(cam, dir, "remy_diag.png", bodyAim + new Vector3(1.8f, 0.55f, -1.8f), bodyAim);
                }

                Debug.Log($"[TablePreview] {(withRemy ? "着座つき（人役 Remy / 手役 白手）7" : "4")} 枚保存 → {dir}（天板 y={topY:F3}）");
            }
            finally
            {
                foreach (var go in remyInstances) if (go != null) Object.DestroyImmediate(go);
                restoreHandEnv?.Invoke();
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(lightGo);
            }
        }

        /// <summary>
        /// 実運用の接続直後配置に合わせ、人役席（Full=Seat0）へ座位 Remy、
        /// 手役席（Hand=Seat1）へ白手 rest ポーズを一時生成する（撮影後に呼び出し側が破棄）。
        /// 戻り値は手役の白手描画に必要な static 環境（Provider/layout）を元に戻す復元 Action。
        /// </summary>
        private static System.Action SeatRoles(List<GameObject> outInstances)
        {
            // 手役の白手を実 Meta メッシュで描くための static 環境を先に整える（＝Preview Hand Role Initial と同じ）。
            var restoreHandEnv = PrepareHandEnv();
            // Seat0 = Full（人役）→ 座位 Remy 全身、Seat1 = Hand（手役）→ 白手 rest。SeatAvatarPreview.SeatIndexOf と対応
            SeatOne("[TableDuo]/Seats/Seat0", outInstances, handRole: false);
            SeatOne("[TableDuo]/Seats/Seat1", outInstances, handRole: true);
            return restoreHandEnv;
        }

        /// <summary>1 席へ役割別アバターを生成し、生成物を outInstances に追加する。</summary>
        private static void SeatOne(string seatPath, List<GameObject> outInstances, bool handRole)
        {
            var seat = GameObject.Find(seatPath);
            if (seat == null) return;
            int before = seat.transform.childCount;
            if (handRole)
            {
                // 手役＝相手から見た手だけアバター。Create が右手を休めポーズで即表示（ShowAtRest）＝接続直後の実配置
                var view = RemoteAvatarView.Create(seat.transform, handsOnly: true);
                foreach (var smr in view.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    smr.forceMatrixRecalculationPerRender = true;
            }
            else
            {
                var prefab = Resources.Load<GameObject>("RemyFullAvatar");
                if (prefab == null)
                {
                    Debug.LogWarning("[TablePreview] RemyFullAvatar prefab が無いため 人役 Remy 着座をスキップ");
                    return;
                }
                // コンストラクタが座位ポーズ + 休めポーズまで組む。firstPerson:false で頭も残す（全身確認）
                _ = new RemyAvatarRig(seat.transform, prefab, firstPerson: false);
            }
            for (int i = before; i < seat.transform.childCount; i++)
            {
                outInstances.Add(seat.transform.GetChild(i).gameObject);
            }
        }

        /// <summary>
        /// Edit モードで手だけアバターを実 Meta 白メッシュ + 実 layout で描くための static 環境を注入する。
        /// Awake 未実行で RemoteHandMeshProvider.Instance=null / Captured layout 未設定のため手動で立て、
        /// 戻り値の Action で元値へ復元する（Preview Hand Role Initial と同一手順）。
        /// </summary>
        private static System.Action PrepareHandEnv()
        {
            var provider = Object.FindObjectOfType<RemoteHandMeshProvider>();
            var instProp = typeof(RemoteHandMeshProvider).GetProperty("Instance",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            var prevInstance = instProp?.GetValue(null);
            var prevL = HandSkeletonLayout.CapturedL;
            var prevR = HandSkeletonLayout.CapturedR;

            if (provider != null)
            {
                instProp?.SetValue(null, provider);
            }
            else
            {
                Debug.LogWarning("[TablePreview] RemoteHandMeshProvider がシーンに無い — 手役は手首 proxy 立方体で表示（Setup 済みか確認）");
            }
            // 手 layout（bind）＝録画同梱の実 layout。無いと bone マッピングが立たず proxy に落ちる
            var rec = Playback.LoadRecordingForPreview(Path.GetFullPath(Path.Combine(Application.dataPath, "..")));
            if (rec != null && rec.Frames.Count > 0)
            {
                HandSkeletonLayout.CapturedL = rec.LayoutL;
                HandSkeletonLayout.CapturedR = rec.LayoutR;
            }

            return () =>
            {
                instProp?.SetValue(null, prevInstance);
                HandSkeletonLayout.CapturedL = prevL;
                HandSkeletonLayout.CapturedR = prevR;
            };
        }

        private static Bounds CalcBounds(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>();
            var b = rs.Length > 0 ? rs[0].bounds : new Bounds(go.transform.position, Vector3.one);
            foreach (var r in rs) b.Encapsulate(r.bounds);
            return b;
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
    }
}
