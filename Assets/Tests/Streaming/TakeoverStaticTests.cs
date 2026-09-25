#nullable enable
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    public sealed class TakeoverStaticTests
    {
        [Test]
        public void TakeoverStaticHandsOffToHeartbeatWithoutReportingSignalLoss()
        {
            Shader shader = Shader.Find("FixedCamVr/ScreenComposite");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            var screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
            var renderer = screen.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            var signal = screen.AddComponent<SignalLostFx>();
            var feel = screen.AddComponent<CameraFeelFx>();
            typeof(SignalLostFx).GetField("_material", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(signal, material);
            try
            {
                Assert.That(material.GetFloat("_SignalLost"), Is.EqualTo(0f));
                feel.BeginTakeoverStatic();
                Assert.That(material.GetFloat("_NarrativeStatic"), Is.EqualTo(1f));
                Assert.That(material.GetFloat("_SignalLost"), Is.EqualTo(0f));
                Assert.That(signal.Level, Is.EqualTo(0f), "Narrative static must not be diagnosed as a connection failure");

                feel.NotifyHeartbeatWarningCompleted();
                Assert.That(material.GetFloat("_NarrativeStatic"), Is.EqualTo(0f), "The doll panel's end restores the live image");

                feel.BeginTakeoverStatic();
                feel.ResetAll();
                Assert.That(material.GetFloat("_NarrativeStatic"), Is.EqualTo(0f), "Abort and visitor restart must clear the static");

                typeof(SignalLostFx).GetField("_level", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(signal, 0.6f);
                typeof(SignalLostFx).GetMethod("SetLevel", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(signal, new object[] { 0.6f });
                feel.BeginTakeoverStatic();
                feel.NotifyHeartbeatWarningCompleted();
                Assert.That(material.GetFloat("_NarrativeStatic"), Is.EqualTo(0f));
                Assert.That(material.GetFloat("_SignalLost"), Is.EqualTo(0.6f),
                    "An actual connection failure must remain visible after the narrative static ends");
            }
            finally
            {
                Object.DestroyImmediate(screen);
                Object.DestroyImmediate(material);
            }
        }
    }
}
