#nullable enable
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

/// <summary>
/// 目のテクスチャの倍率の上限を実行時に下げる。
/// <para>
/// OVRManager の動的解像度は Quest 3 で上限 1.6 倍の目（幅 3264px）を確保し、GPU 律速でも viewport を 1 度も
/// 下げなかった（2026-11-15 実測・<c>rvs=1.00</c> のまま）。つまり常に 1.6 倍で描いている。
/// 推奨解像度（1.0・ほぼパネル等倍）に下げると本編 80→87fps（CRT の中間 RT を Half にした後の実測）。
/// </para>
/// <para>
/// シーンの OVRManager の値は触らない（共有のシーンを書き換えずに実行時 API だけで完結する。<see cref="DisplayRateRequester"/> と同じ方針）。
/// 動的解像度は切らず、上限と下限だけ揃える。診断の口 <c>perfres</c>（<see cref="PerfResProbe"/>）が後から上書きする。
/// </para>
/// </summary>
public static class EyeResolutionCap
{
    /// <summary>目のテクスチャの倍率の上限。</summary>
    public const float MaxScale = 1.0f;

    private static bool _done;

    /// <summary>毎フレーム呼ぶ。OVRManager が立って XR が有効になった最初のフレームで 1 回だけ書く。</summary>
    public static void Tick()
    {
        if (_done || OVRManager.instance == null || !XRSettings.enabled) return;
        _done = true;
        Apply(MaxScale, "既定");
    }

    public static void Apply(float scale, string why)
    {
        OVRManager m = OVRManager.instance;
        if (m == null) return;
        m.quest2MaxDynamicResolutionScale = m.quest3MaxDynamicResolutionScale = m.maxDynamicResolutionScale = scale;
        float min = Mathf.Min(scale, m.minDynamicResolutionScale);
        m.quest2MinDynamicResolutionScale = m.quest3MinDynamicResolutionScale = m.minDynamicResolutionScale = min;
        XRSettings.eyeTextureResolutionScale = scale;
        if (GraphicsSettings.currentRenderPipeline is UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset urp)
            urp.renderScale = scale;
        Debug.Log($"[EyeRes] 目の倍率の上限を {scale:F2} にした（{why}）");
    }
}
