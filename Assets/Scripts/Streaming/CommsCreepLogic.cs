#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 4 周目 A の③a / ③b で、連絡の面の縁から這い込む髪の場（<c>CommsRecovery.hlsl</c> の <c>CommsCreepEval</c>）の
    /// C# の写し。UnityEngine 非依存。画は GPU が描き、ここは「その字は髪の下か」を数えるためだけに同じ式を解く
    /// （テレメトリの赤い字の数。<c>canon/LEDGER.md</c> 0350）。
    ///
    /// ⚠ 定数と式は HLSL と <c>tools/comms-creep/bake.py</c> の確認用の描画が同じ値で持つ。直すときは 3 か所。
    /// 揺れ（房の先のゆっくりした振れ）は写していない — 数える用途では ±数 mm の差にしかならない。
    /// </summary>
    public static class CommsCreepLogic
    {
        public const float TileM = 0.40f;
        public const float ReachM = 0.24f;
        public const float RootV = 0.12f;
        public const float ThetaMax = 0.98f;

        /// <summary>焼いた到達順の版（<c>CurseCreepField.bytes</c>）の大きさ。行 0 が根元（v = 0）。</summary>
        public const int FieldW = 256, FieldH = 128;

        // 辺ごとの (寄り 0 のときの根元の位置 m, 寄り 1 あたりの進み m, 版の横の送り)。
        private static readonly float[] Left = { -0.20f, 0.44f, 0.07f };
        private static readonly float[] Top = { -0.20f, 0.40f, 0.31f };
        private static readonly float[] Right = { -0.24f, 0.42f, 0.57f };

        public static float Theta(float reach) => ThetaMax * Smooth(0f, 0.85f, reach);

        /// <summary>
        /// 面のローカル座標 (<paramref name="px"/>, <paramref name="py"/>) の、前線からの深さ。<b>正なら髪の下</b>。
        /// 版が無い・寸法が違う・寄りが 0 なら負（髪は無い）。
        /// </summary>
        public static float Depth(byte[]? field, float px, float py, float rectCx, float rectCy,
                                  float rectW, float rectH, float reach)
        {
            if (field == null || field.Length != FieldW * FieldH || reach <= 0.001f) return -1f;
            reach = reach > 1f ? 1f : reach;
            float loX = rectCx - rectW * 0.5f, loY = rectCy - rectH * 0.5f;
            float hiX = rectCx + rectW * 0.5f, hiY = rectCy + rectH * 0.5f;
            float swallow = Smooth(0.72f, 1f, reach);
            float theta = Theta(reach);
            float best = Side(field, px - loX, py - loY, rectW, Left, 0f, reach, swallow, theta);
            best = Math.Max(best, Side(field, hiY - py, px - loX, rectH, Top,
                                       TopEbb((px - loX) / Math.Max(rectW, 1e-4f)), reach, swallow, theta));
            best = Math.Max(best, Side(field, hiX - px, py - loY, rectW, Right, 0f, reach, swallow, theta));
            return best;
        }

        /// <summary>上の辺の髪を、顔の側と右の角に寄せる量 (m)。HLSL の <c>CommsCreepTopEbb</c> と対。</summary>
        public static float TopEbb(float xn)
            => 0.07f * Smooth(0.26f, 0.52f, xn) * (1f - Smooth(0.84f, 1f, xn));

        private static float Side(byte[] field, float d, float along, float span, float[] side, float ebb,
                                  float reach, float swallow, float theta)
        {
            float adv = side[0] + side[1] * reach + swallow * span - ebb * (1f - swallow);
            float v = (d - adv) / ReachM + RootV;
            float u = along / (2f * TileM) + side[2];
            return theta - SampleField(field, u, v);
        }

        /// <summary>版を双一次で引く。u は繰り返し、v は 0..1 に留める。</summary>
        public static float SampleField(byte[] field, float u, float v)
        {
            v = v < 0f ? 0f : (v > 1f ? 1f : v);
            u -= MathF.Floor(u);
            float x = u * FieldW - 0.5f, y = v * FieldH - 0.5f;
            int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
            float fx = x - x0, fy = y - y0;
            int xa = Wrap(x0), xb = Wrap(x0 + 1);
            int ya = Clamp(y0), yb = Clamp(y0 + 1);
            float a = field[ya * FieldW + xa], b = field[ya * FieldW + xb];
            float c = field[yb * FieldW + xa], e = field[yb * FieldW + xb];
            float top = a + (b - a) * fx, bottom = c + (e - c) * fx;
            return (top + (bottom - top) * fy) / 255f;
        }

        private static int Wrap(int x) => ((x % FieldW) + FieldW) % FieldW;
        private static int Clamp(int y) => y < 0 ? 0 : (y >= FieldH ? FieldH - 1 : y);

        private static float Smooth(float a, float b, float x)
        {
            float t = (x - a) / (b - a);
            t = t < 0f ? 0f : (t > 1f ? 1f : t);
            return t * t * (3f - 2f * t);
        }
    }
}
