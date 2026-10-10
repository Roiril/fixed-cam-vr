#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using FixedCamVr.Tracking;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.TestTools.Utils;

namespace FixedCamVr.Diagnostics.Tests
{
    public sealed class ControllerGuidePanelRegistrationTests
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private readonly List<UnityEngine.Object> _created = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = _created.Count - 1; i >= 0; i--)
                if (_created[i] != null) UnityEngine.Object.DestroyImmediate(_created[i]);
            _created.Clear();
        }

        private GameObject Go(string name)
        {
            var go = new GameObject(name);
            _created.Add(go);
            return go;
        }

        private static void Set(object target, string field, object? value)
        {
            var f = target.GetType().GetField(field, Fields);
            Assert.That(f, Is.Not.Null, field);
            f!.SetValue(target, value);
        }

        private static void Invoke(object target, string method)
        {
            var m = target.GetType().GetMethod(method, Fields);
            Assert.That(m, Is.Not.Null, method);
            m!.Invoke(target, null);
        }

        private static void Phase(CourseRegistrationController registration, string phase)
        {
            var f = typeof(CourseRegistrationController).GetField("_phase", Fields)!;
            f.SetValue(registration, Enum.Parse(f.FieldType, phase));
            Invoke(registration, "UpdateGuidanceText");
        }

        private Fixture Setup()
        {
            var root = Go("guide-root");
            var textGo = Go("guide-text");
            textGo.transform.SetParent(root.transform, false);
            var tmp = textGo.AddComponent<TextMeshPro>();
            tmp.fontSize = .07f;
            tmp.color = HmdTextStyle.Ink;
            tmp.alignment = TextAlignmentOptions.TopLeft;
            tmp.enableWordWrapping = false;
            tmp.richText = false;
            textGo.transform.localScale = Vector3.one * 2f;
            tmp.rectTransform.sizeDelta = new Vector2(.14f, .075f); // world 0.28 x 0.15 m

            var head = Go("head").transform;
            head.position = new Vector3(0f, 1.6f, 0f);
            var hand = Go("hand").transform;
            hand.position = new Vector3(.25f, 1.15f, .45f);

            var regGo = Go("registration");
            var frame = regGo.AddComponent<CourseFrame>();
            var registration = regGo.AddComponent<CourseRegistrationController>();
            Set(registration, "courseFrame", frame);
            Set(registration, "rightHandTransform", hand);
            Set(registration, "headTransform", head);
            Invoke(registration, "ResolvePoints");

            var hud = Go("hud").AddComponent<StatusHud>();
            Set(hud, "registration", registration);

            var panel = root.AddComponent<ControllerGuidePanel>();
            Set(panel, "text", tmp);
            Set(panel, "controller", hand);
            Set(panel, "head", head);
            Set(panel, "statusHud", hud);
            Invoke(panel, "Awake");
            panel.SetRegistration(registration);
            panel.SetMode("REG");
            panel.SetControllerState(true, true);
            return new Fixture(panel, tmp, registration, frame, hand, head);
        }

        private static void Tick(in Fixture f) => Invoke(f.panel, "LateUpdate");

        [TestCase("Capture", "点 1／2")]
        [TestCase("Verify", "最大のずれ")]
        [TestCase("Review", "保存済みの位置合わせ")]
        public void ActiveRegistrationShowsCurrentProviderText(string phase, string expected)
        {
            var f = Setup();
            if (phase == "Review") f.frame.SetRegistration(Vector2.zero, 0f, .04f, 2, save: false);
            Phase(f.registration, phase);
            Tick(f);

            Assert.That(f.text.enabled, Is.True);
            Assert.That(f.text.text, Is.EqualTo(f.registration.GuidanceText));
            Assert.That(f.text.text, Does.Contain(expected));
            Assert.That(f.text.richText, Is.True, "進捗と警告の色タグを文字として出さない");
        }

        [Test]
        public void SamplingProgressUpdatesTheHandText()
        {
            var f = Setup();
            Phase(f.registration, "Capture");
            Tick(f);
            string before = f.text.text;

            f.registration.Feed(new CourseRegistrationController.RegInput { mark = true, markHeld = true });
            f.registration.Feed(new CourseRegistrationController.RegInput { markHeld = true, deltaTime = .25f });
            Invoke(f.registration, "UpdateGuidanceText");
            Tick(f);

            Assert.That(f.registration.SampleHoldProgress01, Is.EqualTo(.5f).Within(.001f));
            Assert.That(f.text.text, Is.Not.EqualTo(before));
            Assert.That(f.text.text, Does.Contain("計測中"));
            Assert.That(f.text.text, Does.Contain("かざしたまま静止"));
        }

        [Test]
        public void ActiveRegistrationShowsWhileStaffSetupPanelIsVisible()
        {
            var setup = Go("staff-setup").AddComponent<StaffSetupPanel>();
            Invoke(setup, "Awake");
            Assert.That(setup.Visible, Is.True);
            var f = Setup();
            Phase(f.registration, "Capture");
            Tick(f);
            Assert.That(f.text.enabled, Is.True);
        }

        [Test]
        public void ExitRestoresOriginalTextLayoutAndNormalBody()
        {
            var f = Setup();
            Vector2 size = f.text.rectTransform.sizeDelta;
            Vector2 position = f.text.rectTransform.anchoredPosition;
            float fontSize = f.text.fontSize;
            bool richText = f.text.richText;
            Vector4 margin = f.text.margin;
            Phase(f.registration, "Verify");
            Tick(f);
            Assert.That(f.text.rectTransform.sizeDelta.x, Is.GreaterThan(size.x));
            Assert.That(f.text.margin.x, Is.GreaterThan(margin.x));
            Vector2 baseLowerLeft = position - Vector2.Scale(f.text.rectTransform.pivot, size);
            Vector2 activeLowerLeft = f.text.rectTransform.anchoredPosition
                - Vector2.Scale(f.text.rectTransform.pivot, f.text.rectTransform.sizeDelta);
            Assert.That(activeLowerLeft,
                Is.EqualTo(baseLowerLeft).Using(Vector2ComparerWithEqualsOperator.Instance));

            Phase(f.registration, "Idle");
            f.panel.SetMode("NORMAL");
            Tick(f);

            Assert.That(f.text.rectTransform.sizeDelta, Is.EqualTo(size));
            Assert.That(f.text.rectTransform.anchoredPosition, Is.EqualTo(position));
            Assert.That(f.text.fontSize, Is.EqualTo(fontSize));
            Assert.That(f.text.richText, Is.EqualTo(richText));
            Assert.That(f.text.margin, Is.EqualTo(margin));
            Assert.That(f.text.text, Does.Contain("A2秒：新しい体験者にする"));
        }

        [Test]
        public void TrackingLossHidesAndRecoverySnapsToCurrentHand()
        {
            var f = Setup();
            Phase(f.registration, "Capture");
            Tick(f);
            Assert.That(f.text.enabled, Is.True);

            f.panel.SetControllerState(true, false);
            Tick(f);
            Assert.That(f.text.enabled, Is.False);

            f.hand.position = new Vector3(-.35f, .2f, .55f);
            f.panel.SetControllerState(true, true);
            Tick(f);
            Vector3 horizontal = f.hand.position - f.head.position;
            horizontal.y = 0f;
            horizontal.Normalize();
            Vector3 expected = f.hand.position + Vector3.up * .12f + horizontal * .06f;
            Assert.That(f.text.enabled, Is.True);
            Assert.That(f.panel.transform.position, Is.EqualTo(expected).Using(Vector3ComparerWithEqualsOperator.Instance));
        }

        [Test]
        public void RegistrationTextKeepsBodyAngleWhenHandDistanceChanges()
        {
            var f = Setup();
            Phase(f.registration, "Capture");
            Tick(f);
            AssertBodyAngle(f);

            f.panel.SetControllerState(true, false);
            Tick(f);
            f.hand.position = new Vector3(.55f, .95f, .95f);
            f.panel.SetControllerState(true, true);
            Tick(f);
            AssertBodyAngle(f);
        }

        [Test]
        public void InactiveRegistrationDoesNotShowDuringExperience()
        {
            var f = Setup();
            Phase(f.registration, "Idle");
            f.panel.SetMode("NORMAL");
            Tick(f);
            Assert.That(f.text.enabled, Is.False);
        }

        private static void AssertBodyAngle(in Fixture f)
        {
            float worldEm = HmdTextStyle.MeshWorldEm(f.text.fontSize, f.text.transform.lossyScale.y);
            float distance = Vector3.Distance(f.head.position, f.panel.transform.position);
            Assert.That(HmdTextStyle.DegreesOf(worldEm, distance),
                Is.EqualTo(HmdTextStyle.BodyDeg).Within(.001f));
        }

        private readonly struct Fixture
        {
            public readonly ControllerGuidePanel panel;
            public readonly TMP_Text text;
            public readonly CourseRegistrationController registration;
            public readonly CourseFrame frame;
            public readonly Transform hand, head;
            public Fixture(ControllerGuidePanel panel, TMP_Text text, CourseRegistrationController registration,
                CourseFrame frame, Transform hand, Transform head)
            {
                this.panel = panel;
                this.text = text;
                this.registration = registration;
                this.frame = frame;
                this.hand = hand;
                this.head = head;
            }
        }
    }
}
