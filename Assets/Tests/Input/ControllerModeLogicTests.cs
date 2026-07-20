#nullable enable
using System.Collections.Generic;
using FixedCamVr.Input;
using NUnit.Framework;

namespace FixedCamVr.Input.Tests
{
    /// <summary>
    /// ControllerModeLogic（Normal / Registration の 2 状態機械）の検証。
    /// トリガー長押しでの Registration 入場 / キャンセル（対称）・registrationActive 追従での確定・
    /// グリップ長押しでのランリセット要求・長押しラッチ（1 ホールド 1 発火）・進捗・ModeChanged を固定する。
    /// </summary>
    public sealed class ControllerModeLogicTests
    {
        private const float Hold = 2f;

        private static ControllerModeLogic Make(ControllerModeLogic.Mode start = ControllerModeLogic.Mode.Normal)
        {
            var l = new ControllerModeLogic();
            l.Configure(Hold);
            l.Reset(start);
            return l;
        }

        private static void Tick(ControllerModeLogic l, float dt = 0f,
            bool triggerHeld = false, bool gripHeld = false, bool registrationActive = false)
        {
            l.Tick(new ControllerModeLogic.Frame
            {
                deltaTime = dt,
                triggerHeld = triggerHeld,
                gripHeld = gripHeld,
                registrationActive = registrationActive,
            });
        }

        // ボタンを seconds 秒ぶん押し続けて 1 回離す（長押し 1 回分。0.5s 刻み）。
        private static void HoldTrigger(ControllerModeLogic l, float seconds, bool registrationActive = false)
        {
            float t = 0f;
            while (t < seconds) { Tick(l, dt: 0.5f, triggerHeld: true, registrationActive: registrationActive); t += 0.5f; }
            Tick(l, dt: 0.5f, triggerHeld: false, registrationActive: registrationActive);
        }

        private static void HoldGrip(ControllerModeLogic l, float seconds)
        {
            float t = 0f;
            while (t < seconds) { Tick(l, dt: 0.5f, gripHeld: true); t += 0.5f; }
            Tick(l, dt: 0.5f, gripHeld: false);
        }

        // ---- 既定 ----

        [Test]
        public void Default_IsNormal()
        {
            var l = Make();
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Normal));
        }

        // ---- トリガー長押しで Registration 入場 ----

        [Test]
        public void Trigger_BelowThreshold_StaysNormal()
        {
            var l = Make();
            Tick(l, dt: 1.0f, triggerHeld: true);
            Tick(l, dt: 0.9f, triggerHeld: true); // 計 1.9s < 2s
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Normal));
        }

        [Test]
        public void Trigger_ReachesThreshold_NormalToRegistration()
        {
            var l = Make();
            Tick(l, dt: 1.0f, triggerHeld: true);
            Tick(l, dt: 1.0f, triggerHeld: true); // 計 2.0s → Registration
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Registration));
        }

        [Test]
        public void Trigger_FiresOncePerHold_NoRetoggleWhileHeld()
        {
            var l = Make();
            // 押しっぱなしで 4 秒。2 秒で 1 回 Registration になり、離すまで再発火（＝キャンセル）しない。
            for (int i = 0; i < 8; i++) Tick(l, dt: 0.5f, triggerHeld: true, registrationActive: true);
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Registration));
        }

        // ---- registrationActive 追従 ----

        [Test]
        public void Normal_ExternalRegistrationActive_EntersRegistration()
        {
            // startInRegistration など外部起動でも追従する。
            var l = Make();
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
        public void Registration_Confirmed_ReturnsToNormal()
        {
            var l = Make(ControllerModeLogic.Mode.Registration);
            Tick(l, dt: 0f, registrationActive: false); // 確定して IsActive=false
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Normal));
        }

        // ---- トリガー長押しキャンセル（入場と対称）----

        [Test]
        public void Registration_TriggerHold_CancelsToNormal_EvenWhenActive()
        {
            var l = Make(ControllerModeLogic.Mode.Registration);
            // まだ登録アクティブでもトリガー 2 秒でキャンセルして Normal へ。
            HoldTrigger(l, 2f, registrationActive: true);
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Normal));
        }

        // ---- グリップ長押し = ランリセット（モードは変わらない）----

        [Test]
        public void Normal_GripHold_RequestsRunReset_WithoutModeChange()
        {
            var l = Make();
            int resets = 0;
            l.RunResetRequested += () => resets++;
            HoldGrip(l, 2f);
            Assert.That(resets, Is.EqualTo(1));
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Normal));
        }

        [Test]
        public void Normal_GripBelowThreshold_NoReset()
        {
            var l = Make();
            int resets = 0;
            l.RunResetRequested += () => resets++;
            Tick(l, dt: 1.0f, gripHeld: true);
            Tick(l, dt: 0.9f, gripHeld: true); // 1.9s < 2s
            Tick(l, dt: 0.5f, gripHeld: false);
            Assert.That(resets, Is.EqualTo(0));
        }

        [Test]
        public void Registration_GripHold_DoesNothing()
        {
            var l = Make(ControllerModeLogic.Mode.Registration);
            int resets = 0;
            l.RunResetRequested += () => resets++;
            float t = 0f;
            while (t < 2f) { Tick(l, dt: 0.5f, gripHeld: true, registrationActive: true); t += 0.5f; }
            Assert.That(resets, Is.EqualTo(0));
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Registration));
        }

        // ---- 再入場 ----

        [Test]
        public void ReEntry_NormalRegistrationNormalRegistration()
        {
            var l = Make();
            HoldTrigger(l, 2f, registrationActive: true);      // トリガー長押し → Registration（活動追従）
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Registration));
            Tick(l, registrationActive: false);                 // 確定 → Normal
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Normal));
            HoldTrigger(l, 2f, registrationActive: true);      // 再入場
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Registration));
        }

        // ---- ModeChanged イベント ----

        [Test]
        public void ModeChanged_FiresWithFromTo()
        {
            var l = Make();
            var events = new List<(ControllerModeLogic.Mode from, ControllerModeLogic.Mode to)>();
            l.ModeChanged += (from, to) => events.Add((from, to));

            HoldTrigger(l, 2f, registrationActive: true); // Normal → Registration
            Tick(l, registrationActive: false);            // Registration → Normal

            Assert.That(events.Count, Is.EqualTo(2));
            Assert.That(events[0], Is.EqualTo((ControllerModeLogic.Mode.Normal, ControllerModeLogic.Mode.Registration)));
            Assert.That(events[1], Is.EqualTo((ControllerModeLogic.Mode.Registration, ControllerModeLogic.Mode.Normal)));
        }

        [Test]
        public void TriggerHoldProgress_TracksHold()
        {
            var l = Make();
            Tick(l, dt: 1.0f, triggerHeld: true); // 1.0 / 2.0 = 0.5
            Assert.That(l.TriggerHoldProgress01, Is.EqualTo(0.5f).Within(1e-4f));
            Tick(l, dt: 0f, triggerHeld: false); // 離すと 0
            Assert.That(l.TriggerHoldProgress01, Is.EqualTo(0f).Within(1e-4f));
        }

        [Test]
        public void GripHoldProgress_TracksHold()
        {
            var l = Make();
            Tick(l, dt: 0.5f, gripHeld: true); // 0.5 / 2.0 = 0.25
            Assert.That(l.GripHoldProgress01, Is.EqualTo(0.25f).Within(1e-4f));
            Tick(l, dt: 0f, gripHeld: false);
            Assert.That(l.GripHoldProgress01, Is.EqualTo(0f).Within(1e-4f));
        }
    }
}
