#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// フェードの形。**「クロスフェードの真ん中で音が凹まない」を機械で固定する。**
    /// 2026-08-12 まで <see cref="BgmDirector"/> は振幅を線形に動かしていて、区間をまたぐたびに
    /// -3.01 dB の谷が出ていた。ここが緑である限り再発しない。
    /// </summary>
    public class SoundFadeTests
    {
        [Test]
        public void Cross_KeepsPowerConstant_AtEveryPoint()
        {
            for (int i = 0; i <= 100; i++)
            {
                float t = i / 100f;
                SoundFade.Cross(t, out float a, out float b);
                Assert.AreEqual(1f, SoundFade.PowerSum(a, b), 1e-4f,
                                $"t={t} で合成パワーが 1 から外れた（音が凹む / 膨らむ）");
            }
        }

        [Test]
        public void LinearCross_Dips3dB_AtMiddle_TheDefectWeFixed()
        {
            // 直したものが何だったかを残す。線形振幅で混ぜると真ん中は 0.5 / 0.5 で
            // 合成パワー 0.5 ＝ -3.01dB。これが「区間で曲が一瞬引っ込む」の正体だった。
            float lin = SoundFade.Gain(0.5f, SoundFade.Curve.Linear);
            float power = SoundFade.PowerSum(lin, 1f - lin);
            Assert.AreEqual(0.5f, power, 1e-5f);
            // ⚠ **パワー比の dB は 10*log10**（振幅比の 20*log10 ではない）。
            //    ここを間違えると -6dB という倍の数字が出る。
            float dip = 10f * Mathf.Log10(power);
            Assert.AreEqual(-3.01f, dip, 0.05f);

            // 等パワーなら同じ点で谷が無い。
            SoundFade.Cross(0.5f, out float a, out float b);
            Assert.AreEqual(0f, 10f * Mathf.Log10(SoundFade.PowerSum(a, b)), 0.01f);
        }

        [Test]
        public void Cross_Endpoints_AreExact()
        {
            SoundFade.Cross(0f, out float o0, out float i0);
            SoundFade.Cross(1f, out float o1, out float i1);
            Assert.AreEqual(1f, o0, 1e-6f);
            Assert.AreEqual(0f, i0, 1e-6f);
            Assert.AreEqual(0f, o1, 1e-6f);
            Assert.AreEqual(1f, i1, 1e-6f);
        }

        [Test]
        public void Perceptual_StartsSlow_SoLoudnessMovesEvenly()
        {
            // 聴感 ∝ 振幅^0.6 なので、聴感を直線にする振幅は t^(1/0.6)。
            // 真ん中で振幅が半分より小さいことが「ゆっくり立ち上がる」の実体。
            float half = SoundFade.Gain(0.5f, SoundFade.Curve.Perceptual);
            Assert.Less(half, 0.5f);
            Assert.AreEqual(0f, SoundFade.Gain(0f, SoundFade.Curve.Perceptual), 1e-6f);
            Assert.AreEqual(1f, SoundFade.Gain(1f, SoundFade.Curve.Perceptual), 1e-5f);

            // 聴感の高さ（振幅^0.6）は t に対しておおよそ直線になる
            for (int i = 1; i < 10; i++)
            {
                float t = i / 10f;
                float loud = (float)System.Math.Pow(SoundFade.Gain(t, SoundFade.Curve.Perceptual),
                                                    SoundFade.LoudnessExponent);
                Assert.AreEqual(t, loud, 0.02f, $"t={t} で聴感が直線から外れた");
            }
        }

        [Test]
        public void Gain_IsMonotone_ForEveryCurve()
        {
            foreach (SoundFade.Curve c in System.Enum.GetValues(typeof(SoundFade.Curve)))
            {
                float prev = -1f;
                for (int i = 0; i <= 50; i++)
                {
                    float v = SoundFade.Gain(i / 50f, c);
                    Assert.GreaterOrEqual(v, prev, $"{c} が単調でない");
                    prev = v;
                }
            }
        }

        [Test]
        public void Approach_IsFrameRateIndependent()
        {
            // 半減期で寄せるので、刻み幅を変えても同じ時刻には同じ値に居る。
            // （一定速度の寄せ方だと、フレームレートが違う実機と Editor で挙動が変わる）
            float a = 0f, b = 0f;
            for (int i = 0; i < 90; i++) a = SoundFade.Approach(a, 1f, 0.5f, 1f / 90f);
            for (int i = 0; i < 30; i++) b = SoundFade.Approach(b, 1f, 0.5f, 1f / 30f);
            Assert.AreEqual(a, b, 1e-3f);
            Assert.AreEqual(0.75f, a, 0.01f);   // 1 秒 ＝ 半減期 2 つぶん
        }

        [Test]
        public void Approach_UsesSeparateRiseAndFall()
        {
            float rising = SoundFade.Approach(0f, 1f, riseSec: 0.05f, fallSec: 1f, dt: 0.05f);
            float falling = SoundFade.Approach(1f, 0f, riseSec: 0.05f, fallSec: 1f, dt: 0.05f);
            Assert.Greater(rising, 0.4f, "上がりは速いはず");
            Assert.Greater(falling, 0.9f, "下がりは遅いはず");
        }

        [Test]
        public void DbToLin_RoundTrips()
        {
            Assert.AreEqual(0.5011f, SoundFade.DbToLin(-6f), 1e-3f);
            Assert.AreEqual(1f, SoundFade.DbToLin(0f), 1e-6f);
            Assert.AreEqual(-6f, SoundFade.LinToDb(SoundFade.DbToLin(-6f)), 1e-3f);
        }
    }
}
