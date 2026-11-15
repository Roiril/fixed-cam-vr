#nullable enable
using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// 実機の GPU 負荷を切り分けるための診断の口（Development ビルドだけ）。
/// intent の extra <c>perfres=&lt;倍率&gt;</c>（例 1.0）で、目のテクスチャの倍率の上限を実行時に下げる。
/// <para>
/// OVRManager の動的解像度は Quest 3 で上限 1.6 倍の目（幅 3264px）を確保し、実測では 1 度も下げなかった
/// （`IntroVeil.ShatterViewportScale` の注記と同じ観測）。上限そのものが GPU を食っているかを測るための口。
/// </para>
/// </summary>
public sealed class PerfResProbe : MonoBehaviour
{
    private float _scale;
    private float _wait = 2f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoStart()
    {
        if (!Debug.isDebugBuild) return;
        float s = ReadScale();
        if (s <= 0f) return;
        var go = new GameObject("[PerfResProbe]");
        DontDestroyOnLoad(go);
        go.AddComponent<PerfResProbe>()._scale = s;
        Debug.Log($"[PerfRes] 起動フラグ検出 — 目の倍率の上限を {s:F2} にする");
    }

    private static float ReadScale()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var up = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var act = up.GetStatic<AndroidJavaObject>("currentActivity");
            using var intent = act.Call<AndroidJavaObject>("getIntent");
            string v = intent.Call<string>("getStringExtra", "perfres");
            if (string.IsNullOrEmpty(v)) return 0f;
            return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)
                ? Mathf.Clamp(f, 0.5f, 2f) : 0f;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PerfRes] intent extra 読取失敗（perfres）: {e.Message}");
            return 0f;
        }
#else
        return 0f;
#endif
    }

    private void Update()
    {
        _wait -= FixedCamVr.Streaming.SimTime.Dt;
        if (_wait > 0f || OVRManager.instance == null || !XRSettings.enabled) return;
        // 既定の上限（EyeResolutionCap）は最初のフレームで書かれている。2 秒待ってから上書きする。
        EyeResolutionCap.Apply(_scale, "診断の口 perfres");
        enabled = false;
    }
}
