#nullable enable
using System;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 増えるスクリーンの取り込みは、メインスクリーンの筐体（<c>_CrtTvCase</c>）を写さない（0319 の見え）。
    /// 本編のメインは枠つき（0321）なので、撮る間だけ 0 にして必ず戻す。戻し忘れると本編の枠が消える。
    /// </summary>
    public sealed class ScreenMultiplyCaptureTests
    {
        private static Material ScreenMaterial(float tvCase)
        {
            Shader shader = Shader.Find("FixedCamVr/ScreenComposite");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            material.SetFloat("_CrtTvCase", tvCase);
            return material;
        }

        [Test]
        public void Capture_HidesTheCase_AndRestoresIt()
        {
            Material material = ScreenMaterial(IntroLogic.MainShowTvCase);
            try
            {
                using (ScreenMultiply.HideTvCase(material))
                    Assert.That(material.GetFloat("_CrtTvCase"), Is.EqualTo(0f), "取り込みにメインの筐体が写る");
                Assert.That(material.GetFloat("_CrtTvCase"), Is.EqualTo(IntroLogic.MainShowTvCase), "取り込みの後に本編の枠が消えた");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void Capture_RestoresTheCase_EvenWhenTheRenderThrows()
        {
            Material material = ScreenMaterial(IntroLogic.MainShowTvCase);
            try
            {
                Assert.Throws<InvalidOperationException>(() =>
                {
                    using (ScreenMultiply.HideTvCase(material)) throw new InvalidOperationException("render failed");
                });
                Assert.That(material.GetFloat("_CrtTvCase"), Is.EqualTo(IntroLogic.MainShowTvCase), "描画が投げた後に本編の枠が消えた");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void Capture_LeavesAFramelessScreen_AndAMissingMaterialAlone()
        {
            Material material = ScreenMaterial(0f);
            try
            {
                using (ScreenMultiply.HideTvCase(material))
                    Assert.That(material.GetFloat("_CrtTvCase"), Is.EqualTo(0f));
                Assert.That(material.GetFloat("_CrtTvCase"), Is.EqualTo(0f), "割れる遷移の本編に筐体が出た");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(material);
            }

            Assert.DoesNotThrow(() => { using (ScreenMultiply.HideTvCase(null)) { } });
        }
    }
}
