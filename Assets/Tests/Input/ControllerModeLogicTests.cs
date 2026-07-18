#nullable enable
using System.Collections.Generic;
using FixedCamVr.Input;
using NUnit.Framework;

namespace FixedCamVr.Input.Tests
{
    /// <summary>
    /// ControllerModeLogic（Run / Staff / Registration の状態機械）の検証。
    /// Run 封印・両グリップ 3 秒儀式・無操作タイムアウト・Registration 遷移（開始/確定/キャンセル）・
    /// 再入場・ModeChanged イベントを固定する。
    /// </summary>
    public sealed class ControllerModeLogicTests
    {
        private const float GripHold = 3f;
        private const float IdleTimeout = 120f;

        private static ControllerModeLogic Make(ControllerModeLogic.Mode start = ControllerModeLogic.Mode.Run)
        {
            var l = new ControllerModeLogic();
            l.Configure(GripHold, IdleTimeout);
            l.Reset(start);
            return l;
        }

        private static void Tick(ControllerModeLogic l, float dt = 0f,
            bool bothGrips = false, bool registrationActive = false,
            bool stickPressDown = false, bool staffActivity = false)
        {
            l.Tick(new ControllerModeLogic.Frame
            {
                deltaTime = dt,
                bothGrips = bothGrips,
                registrationActive = registrationActive,
                stickPressDown = stickPressDown,
                staffActivity = staffActivity,
            });
        }

        // 両グリップを hold 秒ぶん押し続けて 1 回離す（儀式 1 回分）。
        private static void HoldBothGrips(ControllerModeLogic l, float seconds)
        {
            // 0.5s 刻みで押し続ける。
            float t = 0f;
            while (t < seconds)
            {
                Tick(l, dt: 0.5f, bothGrips: true);
                t += 0.5f;
            }
            Tick(l, dt: 0.5f, bothGrips: false); // 離す（ラッチ解除）
        }

        // ---- 既定・Run 封印 ----

        [Test]
        public void Default_IsRun()
        {
            var l = Make();
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Run));
        }

        [Test]
        public void Run_IgnoresAllButGripRitual()
        {
            var l = Make();
            // Run では stick 押込・活動・登録アクティブ（万一）でも何も起きない。
            Tick(l, dt: 1f, stickPressDown: true, staffActivity: true, registrationActive: true);
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Run));
        }

        // ---- 両グリップ 3 秒儀式 ----

        [Test]
        public void Grip_BelowThreshold_StaysRun()
        {
            var l = Make();
            Tick(l, dt: 1.0f, bothGrips: true);
            Tick(l, dt: 1.0f, bothGrips: true); // 計 2.0s < 3s
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Run));
        }

        [Test]
        public void Grip_ReachesThreshold_RunToStaff()
        {
            var l = Make();
            Tick(l, dt: 1.5f, bothGrips: true);
            Tick(l, dt: 1.5f, bothGrips: true); // 計 3.0s → Staff
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Staff));
        }

        [Test]
        public void Grip_FiresOncePerHold_NoRetoggleWhileHeld()
        {
            var l = Make();
            // 押しっぱなしで 6 秒。3 秒で 1 回 Staff になり、以降離すまで再発火しない。
            for (int i = 0; i < 12; i++) Tick(l, dt: 0.5f, bothGrips: true);
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Staff));
        }

        [Test]
        public void Grip_StaffToRun_AfterReleaseAndReHold()
        {
            var l = Make();
            HoldBothGrips(l, 3f); // Run → Staff
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Staff));
            HoldBothGrips(l, 3f); // Staff → Run
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Run));
        }

        // ---- Staff 無操作タイムアウト ----

        [Test]
        public void Staff_IdleTimeout_ReturnsToRun()
        {
            var l = Make(ControllerModeLogic.Mode.Staff);
            Tick(l, dt: 119f); // 未達
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Staff));
            Tick(l, dt: 2f); // 計 121s ≥ 120s
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Run));
        }

        [Test]
        public void Staff_ActivityResetsIdle()
        {
            var l = Make(ControllerModeLogic.Mode.Staff);
            Tick(l, dt: 119f);
            Tick(l, dt: 1f, staffActivity: true); // 活動 → idle リセット
            Tick(l, dt: 119f); // ここから 119s（まだ 120s 未満）
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Staff));
        }

        // ---- Registration 遷移 ----

        [Test]
        public void Staff_StickPress_EntersRegistration()
        {
            var l = Make(ControllerModeLogic.Mode.Staff);
            Tick(l, dt: 0f, stickPressDown: true);
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Registration));
        }

        [Test]
        public void Staff_ExternalRegistrationActive_EntersRegistration()
        {
            // startInRegistration など外部起動でも追従する。
            var l = Make(ControllerModeLogic.Mode.Staff);
            Tick(l, dt: 0f, registrationActive: true);
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Registration));
        }

        [Test]
        public void Registration_StaysWhileActive()
        {
            var l = Make(ControllerModeLogic.Mode.Registration);
            Tick(l, dt: 5f, registrationActive: true);
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Registration));
        }

        [Test]
        public void Registration_Confirmed_ReturnsToStaff()
        {
            var l = Make(ControllerModeLogic.Mode.Registration);
            Tick(l, dt: 0f, registrationActive: false); // 確定して IsActive=false
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Staff));
        }

        [Test]
        public void Registration_GripRitual_CancelsToStaff_EvenWhenActive()
        {
            var l = Make(ControllerModeLogic.Mode.Registration);
            // まだ登録アクティブでも両グリップ 3 秒でキャンセルして Staff へ。
            float t = 0f;
            while (t < 3f) { Tick(l, dt: 0.5f, bothGrips: true, registrationActive: true); t += 0.5f; }
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Staff));
        }

        [Test]
        public void Registration_StickPress_DoesNotDoubleTransition()
        {
            var l = Make(ControllerModeLogic.Mode.Registration);
            Tick(l, dt: 0f, registrationActive: true, stickPressDown: true);
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Registration));
        }

        // ---- 再入場 ----

        [Test]
        public void ReEntry_StaffRegistrationStaffRegistration()
        {
            var l = Make(ControllerModeLogic.Mode.Staff);
            Tick(l, stickPressDown: true);
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Registration));
            Tick(l, registrationActive: false); // 確定 → Staff
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Staff));
            Tick(l, stickPressDown: true); // 再入場
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Registration));
        }

        // ---- ModeChanged イベント ----

        [Test]
        public void ModeChanged_FiresWithFromTo()
        {
            var l = Make();
            var events = new List<(ControllerModeLogic.Mode from, ControllerModeLogic.Mode to)>();
            l.ModeChanged += (from, to) => events.Add((from, to));

            HoldBothGrips(l, 3f); // Run → Staff
            Tick(l, stickPressDown: true); // Staff → Registration
            Tick(l, registrationActive: false); // Registration → Staff

            Assert.That(events.Count, Is.EqualTo(3));
            Assert.That(events[0], Is.EqualTo((ControllerModeLogic.Mode.Run, ControllerModeLogic.Mode.Staff)));
            Assert.That(events[1], Is.EqualTo((ControllerModeLogic.Mode.Staff, ControllerModeLogic.Mode.Registration)));
            Assert.That(events[2], Is.EqualTo((ControllerModeLogic.Mode.Registration, ControllerModeLogic.Mode.Staff)));
        }

        [Test]
        public void GripHoldProgress_TracksHold()
        {
            var l = Make();
            Tick(l, dt: 1.5f, bothGrips: true); // 1.5 / 3.0 = 0.5
            Assert.That(l.GripHoldProgress01, Is.EqualTo(0.5f).Within(1e-4f));
            Tick(l, dt: 0f, bothGrips: false); // 離すと 0
            Assert.That(l.GripHoldProgress01, Is.EqualTo(0f).Within(1e-4f));
        }
    }
}
