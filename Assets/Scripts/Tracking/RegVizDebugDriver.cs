#nullable enable
using System;
using System.Collections;
using FixedCamVr.Streaming;
using UnityEngine;

namespace FixedCamVr.Tracking
{
    /// <summary>
    /// 位置合わせ検証ビュー（<see cref="ZoneGridFootprint"/> + 登録ワイヤーフレーム）を実機で自動起動させる
    /// **デバッグ起動フック**。親エージェント（シュビー）が Development ビルドを起動フラグ付きで立ち上げ、
    /// adb screencap で登録モードの見た目を検証するためのもの。
    ///
    /// 起動条件（両方満たす時だけ動く。フラグ不在なら GameObject すら作らない）:
    ///   - <see cref="Debug.isDebugBuild"/> が true（Release ビルドでは完全無効）。
    ///   - 起動フラグ: Android は intent extra <c>regviz</c>（<c>adb shell am start -e regviz 1 ...</c>）、
    ///     それ以外（Standalone）は起動引数 <c>-regviz</c>。
    ///
    /// 動作: 起動 5 秒後にシーンから ShowControlClient / CourseFrame / CourseRegistrationController を探し、
    /// layout が無ければ合成 grid を注入、CourseFrame に登録変換（origin/yaw）を **save なし**で入れて
    /// （registration.json は汚さない）<see cref="CourseRegistrationController.Toggle"/> で登録モードへ入る。
    /// 実機に有効な登録（registration.json）があれば Review 着地、無ければ Capture 着地になる（どちらも grid
    /// 生タイルは表示される）。ログは全て <c>[RegVizDriver]</c> タグ（親が adb logcat で拾う）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RegVizDebugDriver : MonoBehaviour
    {
        // 登録後相当の剛体変換（Editor プレビューツールと揃える）。
        private static readonly Vector2 RegisteredOrigin = new(0.4f, 0.25f);
        private const float RegisteredYawDeg = 25f;

        // シーン探索までの待ち（ShowControlClient の初期化列・registry stream 生成が済むのを待つ）。
        private const float StartupDelaySec = 5f;

        // フェーズ 1（登録ガイダンス表示）→ フェーズ 2（登録退場 + ステータス表示）の間隔。
        // 親が phase1（viz ready）で 1 枚、phase2 マーカーで 2 枚目の screencap を撮るための猶予。
        private const float Phase2DelaySec = 12f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!Debug.isDebugBuild) return;      // Release では完全無効
            if (!FlagPresent()) return;           // フラグ不在なら GameObject すら作らない
            var go = new GameObject("[RegVizDriver]");
            DontDestroyOnLoad(go);
            go.AddComponent<RegVizDebugDriver>();
            Debug.Log("[RegVizDriver] 起動フラグ検出 — 登録ビュー自動起動を予約（5 秒後）");
        }

        private static bool FlagPresent()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var up = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var act = up.GetStatic<AndroidJavaObject>("currentActivity");
                using var intent = act.Call<AndroidJavaObject>("getIntent");
                string v = intent.Call<string>("getStringExtra", "regviz");
                return !string.IsNullOrEmpty(v);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RegVizDriver] intent extra 読取失敗: {e.Message}");
                return false;
            }
#else
            foreach (string a in Environment.GetCommandLineArgs())
                if (string.Equals(a, "-regviz", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
#endif
        }

        private void Start() => StartCoroutine(DriveRoutine());

        private IEnumerator DriveRoutine()
        {
            yield return new WaitForSeconds(StartupDelaySec);

            var show = FindObjectOfType<ShowControlClient>();
            var frame = FindObjectOfType<CourseFrame>();
            var ctrl = FindObjectOfType<CourseRegistrationController>();
            if (show == null || frame == null || ctrl == null)
            {
                Debug.LogError($"[RegVizDriver] 必要コンポーネント不在: " +
                               $"ShowControlClient={(show != null)} CourseFrame={(frame != null)} " +
                               $"CourseRegistrationController={(ctrl != null)} — 終了");
                yield break;
            }

            if (show.Layout == null || !show.Layout.HasData())
            {
                show.SetLayoutForPreview(RegVizSampleLayout.Build());
                Debug.Log("[RegVizDriver] layout 不在のため合成 grid を注入");
            }

            // save:false ＝ registration.json は書かない（永続登録を汚さない）。
            frame.SetRegistration(RegisteredOrigin, RegisteredYawDeg, save: false);
            ctrl.Toggle();

            string phase = !ctrl.IsActive ? "Idle" : (frame.HasRegistration ? "Review" : "Capture");
            Debug.Log($"[RegVizDriver] viz ready (phase={phase}, active={ctrl.IsActive}, " +
                      $"origin=({frame.OriginXZ.x:F2},{frame.OriginXZ.y:F2}), yaw={frame.YawDeg:F0})");

            // --- フェーズ 2: 登録退場 → StatusHud のステータス表示 ON ---
            // 親がこの間に phase1（登録ガイダンス）の screencap を撮り、下の phase2 マーカーで 2 枚目を撮る。
            yield return new WaitForSeconds(Phase2DelaySec);
            try
            {
                // Toggle は「active → Idle」で保存せず退場する（B 確定のみが保存 = ConfirmAndExit）。
                // 専用 Cancel API は無く、これがキャンセル退場に相当する。
                if (ctrl.IsActive) ctrl.Toggle();

                // StatusHud は Diagnostics asmdef 側（Tracking → Diagnostics の asmdef 参照は禁止）。
                // 境界を破らないよう、型参照せず GameObject 名引き + SendMessage でステータス表示を ON にする。
                var hudGo = GameObject.Find("StatusHud");
                if (hudGo != null)
                {
                    hudGo.SendMessage("SetVisible", true, SendMessageOptions.DontRequireReceiver);
                    Debug.Log("[RegVizDriver] phase2 status hud shown (registration exited)");
                }
                else
                {
                    Debug.LogWarning("[RegVizDriver] phase2: StatusHud 不在 — ステータス表示スキップ（フェーズ 1 は成立）");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RegVizDriver] phase2 失敗（フェーズ 1 は成立）: {e.Message}");
            }
        }
    }

    /// <summary>
    /// 位置合わせ検証ビューのフォールバック用合成レイアウト（show.json 不在時）。
    /// Web 卓の実データ（12x12 tileM 0.15・カメラ 0/1/2 塗り分け・course order [0,1,2]）と同じ grid を返す。
    /// Editor プレビューツール（RegistrationVizPreview）と実機デバッグ起動フック（RegVizDebugDriver）が共有する。
    /// </summary>
    public static class RegVizSampleLayout
    {
        /// <summary>実データ相当の 12x12 grid レイアウトを組んで返す。</summary>
        public static ShowLayoutDef Build()
        {
            var grid = new ShowGridDef
            {
                tileM = 0.15f,
                cols = 12,
                rows = 12,
                cells = new[]
                {
                    "111222222222", "111222222222", "111222222222",
                    "111000000000", "111000000000", "111000000000", "111000000000",
                    "111000000000", "111000000000", "111000000000",
                    "000000000000", "000000000000",
                },
            };
            return new ShowLayoutDef
            {
                rev = 1,
                floor = new ShowFloorDef { w = 12 * 0.15f, d = 12 * 0.15f },
                grid = grid,
                course = new ShowCourseDef { order = new[] { 0, 1, 2 } },
            };
        }
    }
}
