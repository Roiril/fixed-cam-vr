#nullable enable
using FixedCamVr.Streaming;
using UnityEngine;

/// <summary>
/// 表示リフレッシュレートを実行時に要求する。
///
/// Quest 3 はアプリが何も要求しなければ 72Hz で走る（このプロジェクトは <c>targetFrameRate</c> も
/// <c>displayFrequency</c> も 1 行も書いていなかった）。90Hz にすると提示までの待ちが 1 フレームぶん
/// 短くなり（約 4ms）、頭を振ったときのスクリーンの追従も滑らかになる。
///
/// <b>遅延対策の主役ではない。</b> 映像の遅れは撮影・エンコード・Wi-Fi が支配していて、
/// ここで縮むのは提示ぶんだけ。快適性の項目として入れている。
///
/// 共有の ProjectSettings / OculusSettings.asset は触らない（同居アプリに影響しないよう実行時 API だけで完結）。
/// MonoBehaviour にしないのは、既存シーン / prefab へコンポーネントを 1 個増やさずに済ませるため
/// （<see cref="OvrControllerBridge"/> が起動直後だけ叩く）。
/// </summary>
public static class DisplayRateRequester
{
    /// <summary>要求する表示レート (Hz)。これ以下で使える最大値を選ぶ。</summary>
    public const float DesiredHz = 90f;

    /// <summary>XR の初期化を待つ上限 (秒)。過ぎたら諦めて既定のまま続ける。</summary>
    public const float RetryForSec = 10f;

    private static bool _done;
    private static float _elapsed;

    /// <summary>毎フレーム呼ぶ。成功か時間切れで以後は何もしない。</summary>
    public static void Tick(float unscaledDt)
    {
        if (_done) return;
        _elapsed += unscaledDt;
        if (TryApply()) return;
        if (_elapsed > RetryForSec)
        {
            _done = true;
            Debug.LogWarning($"[DisplayRate] {DesiredHz}Hz を要求できなかった（既定のまま続行）");
        }
    }

    private static bool TryApply()
    {
        var display = OVRManager.display;
        if (display == null) return false;

        float[]? available = display.displayFrequenciesAvailable;
        if (available == null || available.Length == 0) return false;

        // 要求値以下で最大のものを選ぶ（90 が無い機種で 120 を掴んで失敗しないように）。
        float best = 0f;
        foreach (float f in available)
        {
            if (f <= DesiredHz + 0.01f && f > best) best = f;
        }
        if (best <= 0f) return false;

        display.displayFrequency = best;
        DisplayRateInfo.Report(best);
        _done = true;
        Debug.Log($"[DisplayRate] 表示レートを {best}Hz に設定（候補 {available.Length} 件）");
        return true;
    }
}
