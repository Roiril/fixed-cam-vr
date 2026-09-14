#nullable enable
using System.Collections.Generic;
using FixedCamVr.Input;
using NUnit.Framework;

namespace FixedCamVr.Input.Tests
{
    /// <summary>
    /// ControllerModeLogic（Normal / Registration の 2 状態機械）の検証。
    /// トリガー長押しでの Registration 入場 / キャンセル（対称）・registrationActive 追従での確定・
    /// 右 A 長押しでの体験者リセット要求・長押しラッチ（1 ホールド 1 発火）・進捗・ModeChanged を固定する。
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
            bool triggerHeld = false, bool resetHeld = false, bool registrationActive = false,
            bool faceButtonHeld = false)
        {
            l.Tick(new ControllerModeLogic.Frame
            {
                deltaTime = dt,
                triggerHeld = triggerHeld,
                resetHeld = resetHeld,
                registrationActive = registrationActive,
                faceButtonHeld = faceButtonHeld,
            });
        }

        // ボタンを seconds 秒ぶん押し続けて 1 回離す（長押し 1 回分。0.5s 刻み）。
        private static void HoldTrigger(ControllerModeLogic l, float seconds, bool registrationActive = false)
        {
            float t = 0f;
            while (t < seconds) { Tick(l, dt: 0.5f, triggerHeld: true, registrationActive: registrationActive); t += 0.5f; }
            Tick(l, dt: 0.5f, triggerHeld: false, registrationActive: registrationActive);
        }

        private static void HoldReset(ControllerModeLogic l, float seconds)
        {
            float t = 0f;
            while (t < seconds) { Tick(l, dt: 0.5f, resetHeld: true, faceButtonHeld: true); t += 0.5f; }
            Tick(l, dt: 0.5f, resetHeld: false);
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

        // ---- Normal の右 A 長押し = 体験者リセット（モードは変わらない）----

        [Test]
        public void Normal_ResetHold_RequestsRunReset_WithoutModeChange()
        {
            var l = Make();
            int resets = 0;
            l.RunResetRequested += () => resets++;
            HoldReset(l, 2f);
            Assert.That(resets, Is.EqualTo(1));
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Normal));
        }

        [Test]
        public void Normal_ResetBelowThreshold_NoReset()
        {
            var l = Make();
            int resets = 0;
            l.RunResetRequested += () => resets++;
            Tick(l, dt: 1.0f, resetHeld: true, faceButtonHeld: true);
            Tick(l, dt: 0.9f, resetHeld: true, faceButtonHeld: true); // 1.9s < 2s
            Tick(l, dt: 0.5f, resetHeld: false);
            Assert.That(resets, Is.EqualTo(0));
        }

        [Test]
        public void Registration_ResetHold_DoesNothingAndCannotCarryIntoNormal()
        {
            var l = Make(ControllerModeLogic.Mode.Registration);
            int resets = 0;
            l.RunResetRequested += () => resets++;
            for (int i = 0; i < 6; i++)
                Tick(l, dt: 0.5f, resetHeld: true, registrationActive: true, faceButtonHeld: true);
            Assert.That(resets, Is.EqualTo(0));
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Registration));

            Tick(l, dt: 0.1f, resetHeld: true, registrationActive: false, faceButtonHeld: true);
            for (int i = 0; i < 6; i++) Tick(l, dt: 0.5f, resetHeld: true, faceButtonHeld: true);
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Normal));
            Assert.That(resets, Is.EqualTo(0), "登録で押した A は Normal のリセットへ持ち越さない");

            Tick(l, dt: 0.1f, resetHeld: false);
            HoldReset(l, 2f);
            Assert.That(resets, Is.EqualTo(1), "一度離した後の新しい長押しは受け取る");
        }

        [Test]
        public void FirstResetHoldAfterRegistrationExit_IsAccepted()
        {
            var l = Make();
            int resets = 0;
            l.RunResetRequested += () => resets++;

            Tick(l, registrationActive: true);
            Tick(l, registrationActive: false);
            HoldReset(l, 2f);

            Assert.That(resets, Is.EqualTo(1));
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
        public void ResetHoldProgress_TracksHold()
        {
            var l = Make();
            Tick(l, dt: 0.5f, resetHeld: true, faceButtonHeld: true); // 0.5 / 2.0 = 0.25
            Assert.That(l.ResetHoldProgress01, Is.EqualTo(0.25f).Within(1e-4f));
            Tick(l, dt: 0f, resetHeld: false);
            Assert.That(l.ResetHoldProgress01, Is.EqualTo(0f).Within(1e-4f));
        }

        [Test]
        public void Normal_ResetHold_FiresOnlyOnceUntilRelease()
        {
            var l = Make();
            int resets = 0;
            l.RunResetRequested += () => resets++;

            for (int i = 0; i < 10; i++)
                Tick(l, dt: 0.5f, resetHeld: true, faceButtonHeld: true);
            Assert.That(resets, Is.EqualTo(1));

            Tick(l, dt: 0.1f, resetHeld: false);
            HoldReset(l, 2f);
            Assert.That(resets, Is.EqualTo(2));
        }

        // ---- A と重なったトリガーは長押しに数えない ----
        // ⚠ **B は 2026-09-14 に外した**（Bridge が `faceButtonHeld` へ渡さなくなった）。
        //   B は押しているあいだステータスを読む操作なので、含めると早見表を読みながら
        //   位置合わせへ入れない。渡す側の判断なので、このロジックの契約は変わっていない。

        [Test]
        public void Trigger_StartedWhileFaceButtonHeld_IsVoidedUntilRelease()
        {
            var l = Make();
            Tick(l, dt: 0.1f, triggerHeld: true, faceButtonHeld: true);
            Assert.That(l.TriggerHoldProgress01, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(l.TriggerHoldVoided, Is.True);
            Assert.That(l.VoidedHolds, Is.EqualTo(1));
            for (int i = 0; i < 6; i++) Tick(l, dt: 0.5f, triggerHeld: true);
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Normal));
            Assert.That(l.VoidedHolds, Is.EqualTo(1));

            Tick(l, dt: 0.5f, triggerHeld: false);
            Assert.That(l.TriggerHoldVoided, Is.False);
            for (int i = 0; i < 4; i++) Tick(l, dt: 0.5f, triggerHeld: true);
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Registration));
        }

        [Test]
        public void Trigger_FaceButtonPressedMidHold_VoidsTheRest()
        {
            var l = Make();
            Tick(l, dt: 1.0f, triggerHeld: true);
            Assert.That(l.TriggerHoldProgress01, Is.EqualTo(0.5f).Within(1e-4f));
            Tick(l, dt: 0.1f, triggerHeld: true, faceButtonHeld: true);
            Assert.That(l.TriggerHoldProgress01, Is.EqualTo(0f).Within(1e-4f));
            for (int i = 0; i < 6; i++) Tick(l, dt: 0.5f, triggerHeld: true);
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Normal));
        }

        [Test]
        public void Trigger_StartedWithinQuietAfterFaceButton_IsVoided()
        {
            var l = Make();
            Tick(l, dt: 0.1f, faceButtonHeld: true);
            Tick(l, dt: 0.1f);
            Tick(l, dt: 0.1f, triggerHeld: true);
            Assert.That(l.TriggerHoldVoided, Is.True);
            for (int i = 0; i < 6; i++) Tick(l, dt: 0.5f, triggerHeld: true);
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Normal));
            Assert.That(l.TriggerHoldProgress01, Is.EqualTo(0f).Within(1e-4f));
        }

        [Test]
        public void Trigger_StartedAfterQuietElapsed_CountsNormally()
        {
            var l = Make();
            Tick(l, dt: 0.1f, faceButtonHeld: true);
            Tick(l, dt: ControllerModeLogic.FaceButtonQuietSec + 0.05f);
            for (int i = 0; i < 4; i++) Tick(l, dt: 0.5f, triggerHeld: true);
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Registration));
        }

        [Test]
        public void Registration_TriggerCancel_WhileHoldingA_IsVoided()
        {
            // 登録の A 0.5 秒ホールド中に人差し指がトリガーへ掛かっても、キャンセルにはならない。
            var l = Make(ControllerModeLogic.Mode.Registration);
            float t = 0f;
            while (t < 3f) { Tick(l, dt: 0.5f, triggerHeld: true, registrationActive: true, faceButtonHeld: true); t += 0.5f; }
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Registration));
        }

        [Test]
        public void Reset_ClearsVoidedAndQuiet()
        {
            var l = Make();
            Tick(l, dt: 0.1f, triggerHeld: true, faceButtonHeld: true);
            Assert.That(l.VoidedHolds, Is.EqualTo(1));
            l.Reset();
            Assert.That(l.VoidedHolds, Is.EqualTo(0));
            Assert.That(l.TriggerHoldVoided, Is.False);
            for (int i = 0; i < 4; i++) Tick(l, dt: 0.5f, triggerHeld: true);
            Assert.That(l.Current, Is.EqualTo(ControllerModeLogic.Mode.Registration));
        }
    }
}
