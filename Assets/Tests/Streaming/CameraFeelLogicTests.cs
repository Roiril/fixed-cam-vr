#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 「装置らしさ」の時間変化（<see cref="CameraFeelLogic"/>）。
    ///
    /// ここが固定するのは**恐怖の効き目そのもの**なので、値を変えるときは意図を持って変えること:
    ///   - 露出は**遅れて**追いつき、**行き過ぎて戻る**（素直に収束すると装置が迷って見えない）
    ///   - 画のホールドは**必ず秒で明ける**（凍結が解けない事故はこの codebase が 4 回踏んでいる）
    ///   - 焼き付きは**減衰して必ず 0 になる**
    /// </summary>
    public sealed class CameraFeelLogicTests
    {
        private static void Advance(CameraFeelLogic l, float sec, float dt = 1f / 72f)
        {
            for (float t = 0f; t < sec; t += dt) l.Tick(dt);
        }

        [Test]
        public void 露出は暗い映像を持ち上げる方向へ動く()
        {
            var l = new CameraFeelLogic { TargetLuma = 0.34f };
            l.ObserveLuma(0.17f);   // 目標の半分 = +1EV 欲しい（上限 0.8EV でクランプ）
            Advance(l, 3f);
            Assert.That(l.ExposureBias, Is.GreaterThan(0.3f), "暗ければ持ち上げる");
            Assert.That(l.ExposureBias, Is.LessThanOrEqualTo(l.MaxBiasEv * 1.5f), "上限を大きく超えない");
        }

        [Test]
        public void 露出は遅れて追いつく()
        {
            var l = new CameraFeelLogic { TargetLuma = 0.34f, FollowHalfLifeSec = 1.1f };
            l.ObserveLuma(0.17f);
            l.Tick(1f / 72f);
            // **1 フレームで飛びつかない**のが要点。飛びつくと「装置が反応した」ではなく
            // 「加工が切り替わった」に見える。
            Assert.That(l.ExposureBias, Is.LessThan(0.05f));
        }

        [Test]
        public void 減衰が弱いと行き過ぎて戻る()
        {
            var l = new CameraFeelLogic { TargetLuma = 0.34f, FollowHalfLifeSec = 0.8f, Damping = 0.4f };
            l.ObserveLuma(0.17f);
            float peak = 0f;
            for (int i = 0; i < 400; i++) { l.Tick(1f / 72f); if (l.ExposureBias > peak) peak = l.ExposureBias; }
            // 目標（+1EV を 0.8 でクランプ）を一度超えてから戻る = 呼吸。
            Assert.That(peak, Is.GreaterThan(0.8f), "一度は行き過ぎる");
            Assert.That(l.ExposureBias, Is.LessThan(peak), "その後は戻る");
        }

        [Test]
        public void 明るさが測れていなければ露出は動かない()
        {
            var l = new CameraFeelLogic();
            Advance(l, 3f);
            Assert.That(l.ExposureBias, Is.EqualTo(0f).Within(1e-4f));
        }

        [Test]
        public void 周辺光量は露出と逆に動く()
        {
            var l = new CameraFeelLogic { TargetLuma = 0.34f };
            l.ObserveLuma(0.17f);
            Advance(l, 3f);
            // 露出を持ち上げる = 絞りが開く = 周辺が落ちる。符号が逆だと画が「明るく広がる」方向になり、
            // 装置の挙動として読めなくなる。
            Assert.That(l.VignetteBias, Is.LessThan(0f));
        }

        [Test]
        public void ホールドは必ず秒で明ける()
        {
            var l = new CameraFeelLogic();
            l.Hold(0.5f);
            Assert.That(l.Echo, Is.EqualTo(1f), "止まっている間は完全に凍る");
            Assert.That(l.Frozen, Is.True, "素材の時計も止める");

            Advance(l, 0.4f);
            Assert.That(l.Frozen, Is.True, "指定した秒までは止まったまま");

            Advance(l, 0.3f);
            Assert.That(l.Frozen, Is.False, "秒が過ぎたら必ず明ける");
            Advance(l, 0.5f);
            Assert.That(l.Echo, Is.EqualTo(0f).Within(1e-3f), "明けたら完全に戻る");
        }

        [Test]
        public void 焼き付きは減衰して消える_素材の時計は止めない()
        {
            var l = new CameraFeelLogic();
            l.Burn(0.4f, 2f);
            Assert.That(l.Echo, Is.EqualTo(0.4f).Within(1e-4f));
            Assert.That(l.Frozen, Is.False, "焼き付きは「薄く残る」だけ。再生は止めない");

            Advance(l, 1f);
            Assert.That(l.Echo, Is.LessThan(0.4f).And.GreaterThan(0f), "途中は薄くなっている");
            Advance(l, 1.5f);
            Assert.That(l.Echo, Is.EqualTo(0f).Within(1e-3f));
        }

        [Test]
        public void 凍らせる要求は一度だけ取り出せる()
        {
            var l = new CameraFeelLogic();
            Assert.That(l.ConsumeSnapshotRequest(), Is.False);
            l.Hold(0.3f);
            Assert.That(l.ConsumeSnapshotRequest(), Is.True);
            Assert.That(l.ConsumeSnapshotRequest(), Is.False, "同じ 1 枚を撮り直さない");
        }

        [Test]
        public void 強さゼロ以下の指示は無視する()
        {
            var l = new CameraFeelLogic();
            l.Hold(0f);
            l.Burn(0f, 2f);
            Assert.That(l.Echo, Is.EqualTo(0f));
            Assert.That(l.ConsumeSnapshotRequest(), Is.False, "撮る必要も無い");
        }

        [Test]
        public void Reset_で全部畳む()
        {
            var l = new CameraFeelLogic();
            l.ObserveLuma(0.1f);
            Advance(l, 2f);
            l.Hold(5f);
            l.Reset();
            Assert.That(l.ExposureBias, Is.EqualTo(0f));
            Assert.That(l.Echo, Is.EqualTo(0f));
            Assert.That(l.Frozen, Is.False, "ラン開始で画が止まったままにならない");
        }

        [Test]
        public void 巨大な_dt_でも発散しない()
        {
            // ヒッチ（ドメインリロード・HMD 着脱）で 2 次系が飛ばないことを固定する。
            var l = new CameraFeelLogic();
            l.ObserveLuma(0.05f);
            for (int i = 0; i < 20; i++) l.Tick(3f);
            Assert.That(float.IsNaN(l.ExposureBias), Is.False);
            Assert.That(System.Math.Abs(l.ExposureBias), Is.LessThanOrEqualTo(l.MaxBiasEv * 1.5f + 1e-3f));
        }

        [Test]
        public void 収束値は時間で追った先と一致する()
        {
            // Editor の合成プレビューは静止画 1 枚なので SteadyBiasFor だけを使う。
            // 時間で追った結果とずれると「プレビューと実機で画の明るさが違う」が黙って起きる。
            var l = new CameraFeelLogic();
            l.ObserveLuma(0.12f);
            Advance(l, 30f);
            Assert.That(l.ExposureBias, Is.EqualTo(l.SteadyBiasFor(0.12f)).Within(0.02f));
            Assert.That(l.VignetteBias, Is.EqualTo(l.VignetteBiasFor(l.ExposureBias)).Within(1e-5f));
        }

        [Test]
        public void 収束値_測れていなければ動かさない()
        {
            var l = new CameraFeelLogic();
            Assert.That(l.SteadyBiasFor(-1f), Is.EqualTo(0f));
            Assert.That(l.SteadyBiasFor(0f), Is.EqualTo(0f));
        }
    }
}
