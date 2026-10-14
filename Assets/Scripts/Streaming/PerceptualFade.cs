#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 黒へ落とす・黒から上げる・黒の上に浮かべる係数を、<b>見た目の明るさ</b>で緩急づける（2026-10-14）。
    ///
    /// 画は<b>線形光</b>で掛け算される（ScreenComposite の <c>col *= 1 - _SwitchDim</c>、黒い板のアルファ合成も同じ）。
    /// 係数を時間に比例させると、見た目の明るさは (1-t)^(1/2.2) になり、<b>黒の直前の 1〜2 コマで一気に落ちる</b>
    /// （t=1 で傾きが無限大。0.07s の暗転を 90Hz で刻むと、最後の 1 刻みで見た目 0.45 → 0）。
    ///
    /// ここでは<b>見た目の明るさ</b>を先に決めて（落ちるときは 1 − level²）、それを線形光へ戻す。
    /// 落ちるとき（level 0→1）は ease-in、上がるとき（1→0）は鏡像の ease-out になる。
    /// <b>同じ関数を level に掛けるだけ</b>なので、dip の途中で向きが変わっても明るさは飛ばない
    /// （<c>CameraSwitchDirector.HandoffDip</c> は level の空間で暗さを引き継ぐ）。
    ///
    /// ⚠ 曲線の正本はここ 1 か所。シェーダ側の掛け算は線形のまま（2 か所に形を持たせると黙って食い違う）。
    /// </summary>
    public static class PerceptualFade
    {
        /// <summary>表示の γ。sRGB の近似。</summary>
        public const float DisplayGamma = 2.2f;

        /// <summary>
        /// 黒さの段階 <paramref name="level"/>（0 = 素の画、1 = 真っ黒）→ 線形光で掛ける暗さ（<c>_SwitchDim</c> / 黒い板のアルファ）。
        /// 見た目の明るさは 1 − level²。
        /// </summary>
        public static float Darken(float level)
        {
            float l = Mathf.Clamp01(level);
            float perceived = 1f - l * l;
            return 1f - Mathf.Pow(perceived, DisplayGamma);
        }

        /// <summary>
        /// 黒の上に浮かべる面・文字のアルファ。進み <paramref name="progress"/>（0→1）に対して、
        /// <b>見た目の明るさ</b>が smoothstep で立ち上がる（線形のアルファだと最初の数コマで急に見え始める）。
        /// </summary>
        public static float Reveal(float progress)
        {
            float p = Mathf.Clamp01(progress);
            float s = p * p * (3f - 2f * p);
            return Mathf.Pow(s, DisplayGamma);
        }
    }
}
