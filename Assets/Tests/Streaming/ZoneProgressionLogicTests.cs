#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// ZoneProgressionLogic（ショーの時計＝体験者のゾーン確定）の検証。
    /// 段 B で SwitchDirectorLogic から分離した dwell 判定を固定する。
    ///
    /// 本ロジックの核心は「画面の事情を一切知らない」こと — 凍結もクールダウンも dip も引数に無い。
    /// その不変条件（時計は演出中も止まらない）は配線層の SwitchWiringTests が担保する。
    /// </summary>
    public sealed class ZoneProgressionLogicTests
    {
        private const float Dwell = 2f;

        private static ZoneProgressionLogic Make(int current = 0, float dwell = Dwell)
        {
            var p = new ZoneProgressionLogic();
            p.Configure(dwell);
            p.Reset(current);
            return p;
        }

        [Test]
        public void Pending_CommitsOnlyAfterDwell()
        {
            var p = Make();
            p.Request(1, 0f);
            Assert.That(p.Tick(1.9f, out _), Is.False, "dwell 未達では確定しない");
            Assert.That(p.Tick(2.0f, out int committed), Is.True);
            Assert.That(committed, Is.EqualTo(1));
            Assert.That(p.Current, Is.EqualTo(1));
        }

        [Test]
        public void LatestTargetWins_DwellMeasuredFromLatest()
        {
            var p = Make();
            p.Request(1, 0f);
            p.Request(2, 0.5f);                          // 目標変更 → dwell は 0.5 起点
            Assert.That(p.Tick(2.4f, out _), Is.False);  // 2.4-0.5=1.9 < 2
            Assert.That(p.Tick(2.5f, out int committed), Is.True);
            Assert.That(committed, Is.EqualTo(2));
        }

        [Test]
        public void SameTargetContinues_DwellAccumulates()
        {
            // 同一目標を繰り返し要求しても計時は最初の要求時刻を保つ（毎フレーム Request される実配線を模す）。
            var p = Make();
            p.Request(1, 0f);
            p.Request(1, 1.0f);
            p.Request(1, 1.9f);
            Assert.That(p.Tick(2.0f, out int committed), Is.True, "計時がリセットされていたら確定しない");
            Assert.That(committed, Is.EqualTo(1));
        }

        [Test]
        public void SameAsCurrent_ClearsPending()
        {
            // 境界でうろついて現ゾーンへ戻る = 移動しなかった → 積んだ保留は捨てる。
            var p = Make(current: 0);
            p.Request(1, 0f);
            p.Request(0, 0.2f);
            Assert.That(p.HasPending, Is.False);
            Assert.That(p.Tick(10f, out _), Is.False);
            Assert.That(p.Current, Is.EqualTo(0));
        }

        [Test]
        public void InvalidTarget_DoesNothing()
        {
            var p = Make(current: 0);
            p.Request(-1, 0f);
            Assert.That(p.HasPending, Is.False);
            Assert.That(p.Tick(10f, out _), Is.False);
            Assert.That(p.Current, Is.EqualTo(0));
        }

        [Test]
        public void ClearPending_DropsUnconfirmedTarget()
        {
            // Web cameraOverride の適用時に使う（tracker ごと無効化される＝入力が切れるため）。
            var p = Make(current: 0);
            p.Request(1, 0f);
            Assert.That(p.HasPending, Is.True);
            p.ClearPending();
            Assert.That(p.HasPending, Is.False);
            Assert.That(p.Tick(10f, out _), Is.False, "クリア後は dwell を満たしても確定しない");
            Assert.That(p.Current, Is.EqualTo(0));
        }

        [Test]
        public void Reset_SetsCurrentAndDropsPending()
        {
            var p = Make(current: 0);
            p.Request(1, 0f);
            p.Reset(2);
            Assert.That(p.Current, Is.EqualTo(2));
            Assert.That(p.HasPending, Is.False);
        }

        [Test]
        public void ConsecutiveZones_CommitOneByOne()
        {
            // A→B→C と歩いた時、確定は 1 段ずつ出る（周回の進行ポインタが順方向一致で進む前提）。
            var p = Make(current: 0, dwell: 0.5f);
            p.Request(1, 0f);
            Assert.That(p.Tick(0.5f, out int c1), Is.True);
            Assert.That(c1, Is.EqualTo(1));
            p.Request(2, 1.0f);
            Assert.That(p.Tick(1.4f, out _), Is.False);
            Assert.That(p.Tick(1.5f, out int c2), Is.True);
            Assert.That(c2, Is.EqualTo(2));
        }

        [Test]
        public void ZeroDwell_CommitsOnNextTick()
        {
            // EditMode の配線テスト（時間凍結）が dwell=0 で 1 ステップ確定させる前提を固定する。
            var p = Make(current: 0, dwell: 0f);
            p.Request(1, 0f);
            Assert.That(p.Tick(0f, out int committed), Is.True);
            Assert.That(committed, Is.EqualTo(1));
        }

        [Test]
        public void NegativeDwell_ClampedToZero()
        {
            var p = new ZoneProgressionLogic();
            p.Configure(-5f);
            p.Reset(0);
            p.Request(1, 0f);
            Assert.That(p.Tick(0f, out _), Is.True);
        }

        [Test]
        public void Default_IsHalfSecond()
        {
            Assert.That(ZoneProgressionLogic.DefaultDwellSec, Is.EqualTo(0.5f));
        }

        [Test]
        public void WalkThroughInOneSecond_Commits()
        {
            // 既定 dwell 0.5s: 帯幅 ~0.45m を歩行 0.6〜1.5s で抜ける想定 → 1s 滞在で確定する。
            var p = Make(current: 0, dwell: ZoneProgressionLogic.DefaultDwellSec);
            p.Request(1, 0f);
            Assert.That(p.Tick(0.4f, out _), Is.False);
            Assert.That(p.Tick(1.0f, out int committed), Is.True);
            Assert.That(committed, Is.EqualTo(1));
        }
    }
}
