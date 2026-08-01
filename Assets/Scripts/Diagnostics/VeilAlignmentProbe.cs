#nullable enable

using System;
using FixedCamVr.Streaming;
using UnityEngine;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// <b>導入の枠が本編のスクリーンに重なっているかを、HMD を被らずに測るための計測用フック。</b>
    ///
    /// 2026-08-01 に必要になった経緯: 枠の位置はパススルー（現実の映像）の上でしか見えないが、
    /// <b>HMD を被っていない間、<c>screencap</c> / <c>screenrecord</c> はアプリ自身のアイバッファしか
    /// 拾わない</b>（システムの絵もパススルーも黒で返る。ホーム画面を撮ると 910 万画素すべて 0 になることで
    /// 確認）。近接センサーを止める旧来のブロードキャスト（<c>com.oculus.vrpowermanager.prox_close</c>）は
    /// Quest 3 では効かない。つまり<b>被らない限り枠は画に写らない</b>。
    ///
    /// そこで見方を変える。覆いは Passthrough Windows 方式で<b>枠の中のフレームバッファ alpha を 0 にする</b>
    /// ので、<b>カメラの背景を明るくすれば「明るい面に空いた黒い矩形」として枠そのものが写る</b>
    /// （パススルーが合成されなくても、alpha 0 の画素は黒として撮れる）。
    /// そこへスクリーンの外周を線で重ねれば、<b>1 枚の画で枠とスクリーンのずれが測れる</b>。
    ///
    /// 起動フラグが無ければ<b>何もしない</b>（Development ビルド専用・本番の見えに影響しない）:
    /// <code>adb shell am start -e veilprobe 1 -n com.roiril.mawarimi/com.unity3d.player.UnityPlayerActivity</code>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VeilAlignmentProbe : MonoBehaviour
    {
        /// <summary>背景色。中間の灰にするのは、枠の黒と線の明色を同じ画で見分けるため。</summary>
        private static readonly Color Backdrop = new(0.38f, 0.38f, 0.42f, 1f);

        /// <summary>スクリーンの外周の色。背景・黒のどちらからも離す。</summary>
        private static readonly Color ScreenOutline = new(0.1f, 1f, 0.2f, 1f);

        /// <summary>線の太さ (m)。2m 先で約 0.3° ＝ 判定に十分細く、縮小しても消えない太さ。</summary>
        private const float LineWidth = 0.012f;

        /// <summary>覆いより後に描く（覆いが alpha を 0 にした画素の上に線を戻す）。</summary>
        private const int OutlineQueue = 5000;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            if (!Debug.isDebugBuild) return;
            if (!FlagPresent()) return;
            var go = new GameObject("[VeilAlignmentProbe]");
            DontDestroyOnLoad(go);
            go.AddComponent<VeilAlignmentProbe>();
            Debug.Log("[VeilProbe] 起動フラグ検出 — 背景を明るくしてスクリーン外周を重ねる");
        }

        private static bool FlagPresent()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var up = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var act = up.GetStatic<AndroidJavaObject>("currentActivity");
                using var intent = act.Call<AndroidJavaObject>("getIntent");
                string v = intent.Call<string>("getStringExtra", "veilprobe");
                return !string.IsNullOrEmpty(v);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[VeilProbe] intent extra 読取失敗: {e.Message}");
                return false;
            }
#else
            foreach (string a in Environment.GetCommandLineArgs())
                if (string.Equals(a, "-veilprobe", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
#endif
        }

        private Transform? _screen;
        private IntroVeil? _veil;
        private LineRenderer? _outline;
        private Material? _mat;
        private float _sinceLog;

        // ---- 追従の計測（頭を振らせて、スクリーンが正面まで来て止まるかを見る）----
        //
        // 自動走行は体験者の**位置**しか動かさないので、頭の向きは変わらず追従を試せない。
        // ここで実際にリグを回し、`頭の向き − スクリーンの向き` の残差を毎 0.25 秒吐く。
        //   静止中の残差 ≈ 0  → 正面まで来て止まっている（2026-08-01 の狙い）
        //   静止中の残差 ≈ 10 → 旧実装（deadzone の手前で止まる）に戻っている
        private Transform? _rig;
        private Transform? _head;
        private float _sweepT;
        private float _sinceSweepLog;

        /// <summary>ヨーを振る台形波。止まっている時間を長く取るのは、そこが判定点だから。</summary>
        private static readonly (float sec, float yaw)[] Sweep =
        {
            (4f, 0f), (1.2f, 35f), (4f, 35f), (1.2f, -30f), (4f, -30f), (1.2f, 0f),
        };

        private void Start()
        {
            // 背景を明るくする。**alpha は 1 のまま**（0 にすると覆いが穴を開ける前から
            // 全面 alpha 0 になり、枠が写らなくなる — rules/meta-xr.md の「背景は不透明」と同じ理由）。
            foreach (Camera cam in Camera.allCameras)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Backdrop;
            }

            _outline = BuildOutline();
            Resolve();
        }

        private LineRenderer? BuildOutline()
        {
            Shader? shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                Debug.LogWarning("[VeilProbe] Sprites/Default が無い — スクリーン外周を描けない");
                return null;
            }
            _mat = new Material(shader) { name = "VeilProbeOutline", renderQueue = OutlineQueue };
            _mat.color = ScreenOutline;

            var go = new GameObject("ScreenOutline");
            go.transform.SetParent(transform, worldPositionStays: false);
            var lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = _mat;
            lr.useWorldSpace = true;
            lr.loop = true;
            lr.positionCount = 4;
            lr.widthMultiplier = LineWidth;
            lr.numCapVertices = 2;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            return lr;
        }

        private void Update() => DriveYawSweep();

        /// <summary>リグのヨーを台形波で振る。<b>位置は触らない</b>（自動走行と喧嘩させない）。</summary>
        private void DriveYawSweep()
        {
            if (_rig == null || _head == null) return;
            _sweepT += Time.unscaledDeltaTime;

            float t = _sweepT, from = 0f;
            float target = Sweep[Sweep.Length - 1].yaw;
            foreach ((float sec, float yaw) in Sweep)
            {
                if (t < sec) { target = Mathf.Lerp(from, yaw, sec > 0f ? t / sec : 1f); break; }
                t -= sec; from = yaw; target = yaw;
            }
            Vector3 e = _rig.eulerAngles;
            _rig.rotation = Quaternion.Euler(e.x, target, e.z);

            _sinceSweepLog += Time.unscaledDeltaTime;
            if (_sinceSweepLog < 0.25f || _screen == null) return;
            _sinceSweepLog = 0f;
            float residual = Mathf.DeltaAngle(_screen.eulerAngles.y, _head.eulerAngles.y);
            Debug.Log($"[VeilProbe] follow t={_sweepT:F2} 頭={_head.eulerAngles.y:F1} " +
                      $"画面={_screen.eulerAngles.y:F1} 残差={residual:F2}deg");
        }

        private void LateUpdate()
        {
            if (_screen == null || _veil == null) Resolve();
            if (_screen == null || _outline == null) return;

            Vector3 s = _screen.lossyScale;
            Vector3 r = _screen.right * (s.x * 0.5f);
            Vector3 u = _screen.up * (s.y * 0.5f);
            Vector3 c = _screen.position;
            _outline.SetPosition(0, c - r - u);
            _outline.SetPosition(1, c + r - u);
            _outline.SetPosition(2, c + r + u);
            _outline.SetPosition(3, c - r + u);
            _outline.enabled = true;

            // 数値でも残す。画の測定と突き合わせれば、ずれが「枠の側」か「線の側」かが分かる。
            _sinceLog += Time.unscaledDeltaTime;
            if (_sinceLog < 1f || _veil == null) return;
            _sinceLog = 0f;
            float worst = 0f;
            foreach (Vector3 p in new[] { c - r - u, c + r - u, c + r + u, c - r + u })
                worst = Mathf.Max(worst, Mathf.Abs(_veil.SignedDistance(p, 1f)));
            Debug.Log($"[VeilProbe] 枠とスクリーンの隅の食い違い(閉じ切り時) 最大 " +
                      $"{Mathf.Asin(Mathf.Clamp(worst, -1f, 1f)) * Mathf.Rad2Deg:F2}deg");
        }

        private void Resolve()
        {
            if (_veil == null) _veil = FindObjectOfType<IntroVeil>();
            if (_head == null)
            {
                var anchor = GameObject.Find("CenterEyeAnchor");
                if (anchor != null) _head = anchor.transform;
            }
            if (_rig == null && _head != null)
            {
                // ShowWalkDebugDriver と同じ辿り方（親に別のグループが居ても効く）。
                Transform? t = _head;
                while (t != null && t.name != "OVRCameraRig") t = t.parent;
                _rig = t != null ? t : _head.root;
            }
            if (_screen != null) return;
            var overlay = FindObjectOfType<ScreenOverlayController>();
            if (overlay != null) _screen = overlay.transform;
        }

        private void OnDestroy()
        {
            if (_mat != null) Destroy(_mat);
        }
    }
}
