#nullable enable
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 終幕の写真は画面の物理の縦横比で撮る。16:9 の器へ写すと、参照の筐体つき（1280:1030）の画面が横に伸びる（0324）。
    /// </summary>
    public sealed class EndingFrameCaptureAspectTests
    {
        [Test]
        public void ReferenceCaseScreen_IsCapturedAt1280By1030()
            => Assert.That(EndingFrameCapture.HeightFor(CrtReferenceScreen.CaseAspect), Is.EqualTo(1030));

        [Test]
        public void SixteenByNineScreen_KeepsTheOldSize()
            => Assert.That(EndingFrameCapture.HeightFor(16f / 9f), Is.EqualTo(EndingFrameCapture.Height));

        [Test]
        public void UnreadableAspect_FallsBackTo16By9()
        {
            Assert.That(EndingFrameCapture.HeightFor(0f), Is.EqualTo(EndingFrameCapture.Height));
            Assert.That(EndingFrameCapture.HeightFor(float.NaN), Is.EqualTo(EndingFrameCapture.Height));
            Assert.That(EndingFrameCapture.HeightFor((Transform?)null), Is.EqualTo(EndingFrameCapture.Height));
        }

        [Test]
        public void ScreenTransform_DecidesTheHeight()
        {
            var go = new GameObject("screen");
            try
            {
                go.transform.localScale = new Vector3(1.33f * CrtReferenceScreen.CaseAspect, 1.33f, 1f);
                Assert.That(EndingFrameCapture.HeightFor(go.transform), Is.EqualTo(1030));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
