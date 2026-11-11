#nullable enable
#if UNITY_EDITOR
using UnityEngine;

/// <summary>
/// <see cref="XpReplayCapture"/> が撮る相手に求めるもの。撮影は同じ GameObject に付いたこれを読む。
///
/// 実装は 2 つ。<see cref="XpReplayDriver"/>（記録を流し直して撮る。twin-verify の <c>--two-pass</c> と紹介動画）と、
/// <see cref="XpTwinDriver"/>（ツイン走行の Play の中で同時に撮る。twin-verify の既定）。
/// 撮り方（出力先・画角・寸法・fps）は SessionState の <c>fcv.replay.*</c> で渡す（書くのは Editor の入口）。
/// </summary>
public interface IXpCaptureSource
{
    /// <summary>撮る時計の頭を過ぎたか。過ぎるまで撮影は始まらない。</summary>
    bool Started { get; }

    /// <summary>撮り終えてよいか。立った次の LateUpdate で撮影は閉じる。</summary>
    bool Finished { get; }

    /// <summary>撮る時計（秒・頭の前は負）。<c>maxsec</c> の上限と <c>replay_xp.log</c> の <c>tau=</c> はこれで測る。</summary>
    float Tau { get; }

    /// <summary>頭のワールド姿勢（撮影カメラが追う）。</summary>
    void HeadPose(out Vector3 pos, out Quaternion rot);

    /// <summary>撮影が閉じた。結果は mp4 のフルパスか <c>"error: ..."</c>。1 回の撮影で 1 回だけ呼ばれる。</summary>
    void OnCaptureDone(string result);
}
#endif
