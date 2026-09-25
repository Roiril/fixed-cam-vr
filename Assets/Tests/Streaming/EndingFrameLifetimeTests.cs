#nullable enable
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    public sealed class EndingFrameLifetimeTests
    {
        [Test]
        public void ForcedTrappedFreezeCapturesTheFirstVisibleFrameSynchronously()
        {
            var screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
            var material = new Material(Shader.Find("FixedCamVr/ScreenComposite"));
            var source = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            var readback = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            var previous = RenderTexture.active;
            source.SetPixel(0, 0, Color.red);
            source.Apply();
            material.SetTexture("_LiveTex", source);
            material.SetVector("_LiveScale", new Vector4(1, 1, 0, 0));
            material.SetFloat("_ScreenPower", 1f);
            material.SetFloat("_ScreenCollapse", 0f);
            var renderer = screen.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            var capture = screen.AddComponent<EndingFrameCapture>();
            Material? instance = null;
            try
            {
                Invoke(capture, "Awake");
                instance = renderer.sharedMaterial;
                capture.Freeze(EndingShotWindow.Trapped);

                Assert.That(capture.TrappedShot, Is.Not.Null);
                Assert.That(capture.ReleasedShot, Is.Null);
                Assert.That(capture.ShotCount, Is.EqualTo(1));
                Assert.That(Get(capture, "_frozen"), Is.True);
                var shot = (RenderTexture)capture.TrappedShot!;
                RenderTexture.active = shot;
                readback.ReadPixels(new Rect(shot.width / 2, shot.height / 2, 1, 1), 0, 0);
                readback.Apply();
                Color saved = readback.GetPixel(0, 0);
                Assert.That(saved.r, Is.GreaterThan(0.3f), "The saved image must contain rendered pixels");
                Assert.That(saved.r, Is.GreaterThan(saved.g * 2f));

                source.SetPixel(0, 0, Color.green);
                source.Apply();
                capture.Freeze(EndingShotWindow.Trapped);
                RenderTexture.active = shot;
                readback.ReadPixels(new Rect(shot.width / 2, shot.height / 2, 1, 1), 0, 0);
                readback.Apply();
                Assert.That(readback.GetPixel(0, 0), Is.EqualTo(saved), "A frozen photo must not follow the live image");
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(screen);
                if (instance != null && instance != material) Object.DestroyImmediate(instance);
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(readback);
            }
        }

        [Test]
        public void RunRestartAndDisableDiscardBothVisitorsShots()
        {
            var runGo = new GameObject("Ending frame lifetime run");
            var screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
            var material = new Material(Shader.Find("FixedCamVr/ScreenComposite"));
            var renderer = screen.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            var run = runGo.AddComponent<ShowRunDirector>();
            var capture = screen.AddComponent<EndingFrameCapture>();
            Material? instance = null;
            try
            {
                Invoke(capture, "Awake");
                instance = renderer.sharedMaterial;
                var first = new RenderTexture(16, 9, 0);
                var second = new RenderTexture(16, 9, 0);
                Set(capture, "_releasedShot", first);
                Set(capture, "_trappedShot", second);
                capture.Freeze();
                Assert.That(capture.ShotCount, Is.EqualTo(2));

                run.BeginRun();
                Assert.That(capture.ReleasedShot, Is.Null);
                Assert.That(capture.TrappedShot, Is.Null);
                Assert.That(first == null && second == null, Is.True, "Reset must destroy old GPU images");
                Assert.That(Get(capture, "_frozen"), Is.False, "Next visitor must be allowed to capture");

                var nextVisitor = new RenderTexture(16, 9, 0);
                Set(capture, "_trappedShot", nextVisitor);
                Invoke(capture, "OnDisable");
                Assert.That(capture.ShotCount, Is.Zero);
                Assert.That(nextVisitor == null, Is.True, "Disabled capture must not retain a stale visitor");
            }
            finally
            {
                Object.DestroyImmediate(screen);
                Object.DestroyImmediate(runGo);
                if (instance != null && instance != material) Object.DestroyImmediate(instance);
                Object.DestroyImmediate(material);
            }
        }

        private static object? Get(object target, string field) => target.GetType()
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
        private static void Set(object target, string field, object value) => target.GetType()
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
        private static void Invoke(object target, string method) => target.GetType()
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
    }
}
