#nullable enable

using System;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// スクリーンの周りの闇に置く<b>黒い霧</b>の式。距離で光を吸う。遠いものほど暗く、ぼけ、光がにじむ。
    /// 読み手は増えるスクリーン（<see cref="ScreenMultiply"/>・<c>Resources/ScreenFog.hlsl</c>）と
    /// 導入のテレビ（<see cref="IntroLogic"/>）。値は 1 か所に置き、シェーダへは材質の値として渡す。
    ///
    /// ⚠ UnityEngine に依存しない（<see cref="IntroLogic"/> が読む）。
    /// </summary>
    public static class ScreenFogLogic
    {
        /// <summary>霧の濃さ（1/m）。1m 進むごとに光が exp(-これ) 倍になる。</summary>
        public const float Density = 0.38f;

        /// <summary>ここまでは霧が無い（m）。メインスクリーンは 2m にあり、霧の影響を受けない。</summary>
        public const float ClearM = 2.2f;

        /// <summary>霧を抜けきれない光のぼけの上限（取り込みの mip の段）。</summary>
        public const float MaxBlurLod = 3.6f;

        /// <summary>距離 <paramref name="distM"/> の光がどれだけ届くか（1 = そのまま / 0 = 見えない）。</summary>
        public static float Transmit(float distM)
            => MathF.Exp(-Density * MathF.Max(0f, distM - ClearM));
    }
}
