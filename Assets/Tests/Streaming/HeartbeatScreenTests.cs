using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    public sealed class HeartbeatScreenTests
    {
        [Test]
        public void 警告が終わる前は出ない()
        {
            var logic = new HeartbeatScreenLogic();

            Assert.That(logic.ShouldApply(inRun: true, timelineSuppressed: false,
                                          registrationActive: false), Is.False);
        }

        [Test]
        public void 警告の自然完了後に出る()
        {
            var logic = new HeartbeatScreenLogic();

            logic.NotifyWarningCompleted();

            Assert.That(logic.ShouldApply(inRun: true, timelineSuppressed: false,
                                          registrationActive: false), Is.True);
        }

        [Test]
        public void 凍結で即座に止まる()
        {
            var logic = Started();

            logic.NotifyFreeze();

            Assert.That(logic.ShouldApply(inRun: true, timelineSuppressed: false,
                                          registrationActive: false), Is.False);
        }

        [Test]
        public void 凍結が先なら警告完了後も始まらない()
        {
            var logic = new HeartbeatScreenLogic();

            logic.NotifyFreeze();
            logic.NotifyWarningCompleted();

            Assert.That(logic.ShouldApply(inRun: true, timelineSuppressed: false,
                                          registrationActive: false), Is.False);
        }

        [Test]
        public void 凍結後に引き返しても再開しない()
        {
            var logic = Started();
            logic.NotifyFreeze();

            Assert.That(logic.ShouldApply(inRun: false, timelineSuppressed: false,
                                          registrationActive: false), Is.False);
            Assert.That(logic.ShouldApply(inRun: true, timelineSuppressed: false,
                                          registrationActive: false), Is.False);
        }

        [Test]
        public void リセット後は次の警告完了を待つ()
        {
            var logic = Started();
            logic.NotifyFreeze();

            logic.Reset();

            Assert.That(logic.ShouldApply(inRun: true, timelineSuppressed: false,
                                          registrationActive: false), Is.False);
            logic.NotifyWarningCompleted();
            Assert.That(logic.ShouldApply(inRun: true, timelineSuppressed: false,
                                          registrationActive: false), Is.True);
        }

        [Test]
        public void 位置合わせ中だけ止まり復帰する()
        {
            var logic = Started();

            Assert.That(logic.ShouldApply(inRun: true, timelineSuppressed: false,
                                          registrationActive: true), Is.False);
            Assert.That(logic.ShouldApply(inRun: true, timelineSuppressed: false,
                                          registrationActive: false), Is.True);
        }

        [Test]
        public void 手動占有中だけ止まり復帰する()
        {
            var logic = Started();

            Assert.That(logic.ShouldApply(inRun: true, timelineSuppressed: true,
                                          registrationActive: false), Is.False);
            Assert.That(logic.ShouldApply(inRun: true, timelineSuppressed: false,
                                          registrationActive: false), Is.True);
        }

        private static HeartbeatScreenLogic Started()
        {
            var logic = new HeartbeatScreenLogic();
            logic.NotifyWarningCompleted();
            return logic;
        }
    }
}
