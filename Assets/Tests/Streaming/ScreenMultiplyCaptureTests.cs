#nullable enable
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 増えるスクリーンはメインを筐体ごと取り込み、枠はメインと同じにする（0324）。
    /// 管と筐体の境は、参照の筐体つきの画面だけメインの輪郭マスクで切る。
    /// </summary>
    public sealed class ScreenMultiplyCaptureTests
    {
        private static Material ScreenMaterial(float reference, Texture? mask)
        {
            Shader shader = Shader.Find("FixedCamVr/ScreenComposite");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            material.SetFloat("_CrtReference", reference);
            material.SetTexture("_CrtScreenMaskTex", mask);
            return material;
        }

        [Test]
        public void ReferenceScreen_HandsItsContourToTheWall()
        {
            var mask = new Texture2D(4, 4);
            Material material = ScreenMaterial(1f, mask);
            try
            {
                Assert.That(ScreenMultiply.TubeMaskOf(material), Is.SameAs(mask), "面がメインの輪郭で管を切れない");
            }
            finally
            {
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(mask);
            }
        }

        [Test]
        public void ScreenWithoutReferenceCase_UsesTheCaptureAlpha()
        {
            var mask = new Texture2D(4, 4);
            Material material = ScreenMaterial(0f, mask);
            try
            {
                Assert.That(ScreenMultiply.TubeMaskOf(material), Is.Null, "筐体の無い 16:9 の画面を参照の輪郭で切った");
            }
            finally
            {
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(mask);
            }
            Assert.That(ScreenMultiply.TubeMaskOf(null), Is.Null);
        }
    }
}
