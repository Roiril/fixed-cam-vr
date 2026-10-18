#nullable enable
using System;
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    public sealed class StreamDecodeSchedulerTests
    {
        [Test]
        public void Empty_ReturnsNoBackground()
        {
            var scheduler = new StreamDecodeScheduler();
            Assert.That(scheduler.NextBackground(Array.Empty<bool>()), Is.EqualTo(-1));
        }

        [Test]
        public void AllPriority_ReturnsNoBackground()
        {
            var scheduler = new StreamDecodeScheduler();
            Assert.That(scheduler.NextBackground(new[] { true, true, true }), Is.EqualTo(-1));
        }

        [Test]
        public void Backgrounds_ReceiveEqualTurns_WithoutChangingPriority()
        {
            var scheduler = new StreamDecodeScheduler();
            var priority = new[] { true, false, false, false, true };
            for (int turn = 0; turn < 12; turn++)
                Assert.That(scheduler.NextBackground(priority), Is.EqualTo(1 + turn % 3));
            Assert.That(priority, Is.EqualTo(new[] { true, false, false, false, true }));
        }

        [Test]
        public void PriorityChanges_AreObservedOnNextTurn()
        {
            var scheduler = new StreamDecodeScheduler();
            var priority = new[] { true, false, false };
            Assert.That(scheduler.NextBackground(priority), Is.EqualTo(1));
            priority[2] = true;
            Assert.That(scheduler.NextBackground(priority), Is.EqualTo(1));
            priority[0] = false;
            priority[1] = true;
            Assert.That(scheduler.NextBackground(priority), Is.EqualTo(0));
        }

        [Test]
        public void SmallerCameraArray_ResetsCursorIntoRange()
        {
            var scheduler = new StreamDecodeScheduler();
            Assert.That(scheduler.NextBackground(new[] { true, true, false, false }), Is.EqualTo(2));
            Assert.That(scheduler.NextBackground(new[] { false }), Is.EqualTo(0));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ThreeSynchronized30FpsInputs_KeepAllFramesAt90Hz(bool recordingSecondCamera)
        {
            var scheduler = new StreamDecodeScheduler();
            var priority = new[] { true, recordingSecondCamera, false };
            var pending = new bool[3];
            var decoded = new int[3];
            int maxDecodesInFrame = 0;
            for (int frame = 0; frame < 90; frame++)
            {
                if (frame % 3 == 0)
                    for (int camera = 0; camera < 3; camera++)
                    {
                        Assert.That(pending[camera], Is.False, $"camera={camera} frame={frame}: unread input overwritten");
                        pending[camera] = true;
                    }
                int background = scheduler.NextBackground(priority);
                int backgroundDecodes = 0;
                int frameDecodes = 0;
                for (int camera = 0; camera < 3; camera++)
                {
                    if (!priority[camera] && camera != background) continue;
                    if (!pending[camera]) continue;
                    pending[camera] = false;
                    decoded[camera]++;
                    frameDecodes++;
                    if (!priority[camera]) backgroundDecodes++;
                }
                Assert.That(backgroundDecodes, Is.LessThanOrEqualTo(1));
                maxDecodesInFrame = Math.Max(maxDecodesInFrame, frameDecodes);
            }
            Assert.That(decoded, Is.EqualTo(new[] { 30, 30, 30 }));
            Assert.That(maxDecodesInFrame, Is.EqualTo(recordingSecondCamera ? 3 : 2));
        }

        [Test]
        public void EmptyBackgroundSlots_AreNeverSelected()
        {
            var scheduler = new StreamDecodeScheduler();
            var priority = new[] { true, false, false, false };
            var pending = new[] { true, false, true, false };
            var excluded = new bool[4];
            for (int camera = 0; camera < 4; camera++)
                excluded[camera] = priority[camera] || !pending[camera];
            Assert.That(scheduler.NextBackground(excluded), Is.EqualTo(2));
            pending[2] = false;
            excluded[2] = true;
            Assert.That(scheduler.NextBackground(excluded), Is.EqualTo(-1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Three30FpsInputsAndDisconnectedFourth_KeepAllFramesAt90Hz(bool recordingSecondCamera)
        {
            var scheduler = new StreamDecodeScheduler();
            var priority = new[] { true, recordingSecondCamera, false, false };
            var pending = new bool[4];
            var excluded = new bool[4];
            var decoded = new int[4];
            for (int frame = 0; frame < 90; frame++)
            {
                if (frame % 3 == 0)
                    for (int camera = 0; camera < 3; camera++)
                    {
                        Assert.That(pending[camera], Is.False, $"camera={camera} frame={frame}: unread input overwritten");
                        pending[camera] = true;
                    }
                for (int camera = 0; camera < 4; camera++)
                    excluded[camera] = priority[camera] || !pending[camera];
                int background = scheduler.NextBackground(excluded);
                Assert.That(background, Is.Not.EqualTo(3));
                if (background >= 0) Assert.That(pending[background], Is.True);
                int backgroundDecodes = 0;
                for (int camera = 0; camera < 4; camera++)
                {
                    if (!priority[camera] && camera != background) continue;
                    if (!pending[camera]) continue;
                    pending[camera] = false;
                    decoded[camera]++;
                    if (!priority[camera]) backgroundDecodes++;
                }
                Assert.That(backgroundDecodes, Is.LessThanOrEqualTo(1));
            }
            Assert.That(decoded, Is.EqualTo(new[] { 30, 30, 30, 0 }));
        }
    }
}
