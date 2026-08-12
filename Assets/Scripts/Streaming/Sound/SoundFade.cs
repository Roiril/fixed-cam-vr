#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 音量の増減の形（UnityEngine 非依存）。**この作品の音のフェードは全部ここを通る。**
    ///
    /// ⚠⚠ <b>振幅を線形に動かすクロスフェードは、中央で音が凹む。</b>
    /// 無相関な 2 つの音を t と 1-t で混ぜると、真ん中の合成パワーは 0.5² + 0.5² = 0.5
    /// ＝ <b>-3.01 dB の谷</b>。曲が切り替わるたびに一瞬引っ込むので、体験者には
    /// 「音が途切れた」に聞こえる。2026-08-12 まで <see cref="BgmDirector"/> がこれをやっていた。
    ///
    /// ⚠ <b>単独のフェードイン / アウトは等パワーではない。</b> 相手が居ないので合成パワーを
    /// 保つ必要が無く、代わりに<b>聴感の高さ</b>を一定の速さで動かしたい。聴感の高さは
    /// おおよそ振幅の 0.6 乗なので、振幅を t^(1/0.6) で動かすと聴感が直線になる
    /// （<see cref="Curve.Perceptual"/>）。線形振幅のフェードアウトは「最後まで残ってから急に消える」、
    /// dB 直線のフェードアウトは「すぐ消えてから長く尾を引く」に聞こえる。
    /// </summary>
    public static class SoundFade
    {
        /// <summary>聴感の高さと振幅の関係の指数（Stevens の法則の近似）。</summary>
        public const float LoudnessExponent = 0.6f;

        public enum Curve
        {
            /// <summary>振幅を直線で。**相関のある音どうしを混ぜるときだけ正しい**（同じ音の位相違い等）。</summary>
            Linear,
            /// <summary>合成パワーを一定に保つ（sin / cos）。**別々の曲を入れ替えるときはこれ。**</summary>
            EqualPower,
            /// <summary>聴感の高さを直線で動かす。**単独の音を出す / 消すときはこれ。**</summary>
            Perceptual,
        }

        /// <summary>0..1 の進み → 0..1 の振幅。</summary>
        public static float Gain(float t, Curve curve)
        {
            t = Clamp01(t);
            switch (curve)
            {
                case Curve.EqualPower:
                    return (float)Math.Sin(t * Math.PI * 0.5);
                case Curve.Perceptual:
                    return (float)Math.Pow(t, 1.0 / LoudnessExponent);
                default:
                    return t;
            }
        }

        /// <summary>
        /// クロスフェードの 2 つの倍率（出ていく方 / 入ってくる方）。
        /// **二乗の和が厳密に 1**（<c>sin² + cos² = 1</c>）なので、無相関な音でも谷ができない。
        /// </summary>
        public static void Cross(float t, out float fadeOut, out float fadeIn)
        {
            t = Clamp01(t);
            double a = t * Math.PI * 0.5;
            fadeOut = (float)Math.Cos(a);
            fadeIn = (float)Math.Sin(a);
        }

        /// <summary>
        /// 現在値を目標へ「半減期」で寄せる。dt に依存しない
        /// （フレームレートが変わっても同じ速さで届く ＝ 実機と Editor で挙動が揃う）。
        ///
        /// ⚠ <c>MoveTowards</c> のような一定速度の寄せ方は、目標が動く量に応じて所要時間が変わる。
        /// 音は「1 秒で入れ替える」と書いた通りに動いてほしいので、半減期で書く。
        /// </summary>
        public static float Approach(float current, float target, float halfLifeSec, float dt)
        {
            if (halfLifeSec <= 0f || dt <= 0f) return target;
            float k = (float)Math.Pow(0.5, dt / halfLifeSec);
            return target + (current - target) * k;
        }

        /// <summary>
        /// 上がるときと下がるときで速さを変えて寄せる。
        /// 砂嵐のように「速く来て、ゆっくり引く」ものに要る（対称だと復帰が唐突になる）。
        /// </summary>
        public static float Approach(float current, float target, float riseSec, float fallSec, float dt)
            => Approach(current, target, target > current ? riseSec : fallSec, dt);

        public static float DbToLin(float db) => (float)Math.Pow(10.0, db / 20.0);

        public static float LinToDb(float lin)
            => lin <= 1e-7f ? -140f : (float)(20.0 * Math.Log10(lin));

        /// <summary>2 つの倍率を混ぜたときの合成パワー（テストが谷の有無を見るために使う）。</summary>
        public static float PowerSum(float a, float b) => a * a + b * b;

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
