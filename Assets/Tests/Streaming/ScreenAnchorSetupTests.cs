using System.Reflection;
using NUnit.Framework;
using UnityEngine;
namespace FixedCamVr.Streaming.Tests
{
    public sealed class ScreenAnchorSetupTests
    {
        [Test] public void SetupStaysWorldFixed_RecallOnlyMovesScreen_ThenNormalFollowReturns()
        {
            var screen = new GameObject("setup-screen"); var head = new GameObject("setup-head");
            try
            {
                var anchor = screen.AddComponent<ScreenAnchor>();
                typeof(ScreenAnchor).GetField("head", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(anchor, head.transform);
                anchor.Lock(); anchor.SetSetupFrozen(true);
                screen.transform.position = new Vector3(3f, 1f, 3f);
                var before = screen.transform.position; head.transform.position = new Vector3(1f, 2f, 0f);
                head.transform.rotation = Quaternion.Euler(30f, 45f, 10f);
                typeof(ScreenAnchor).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(anchor, null);
                Assert.AreEqual(before, screen.transform.position);
                Assert.IsTrue(anchor.RepositionForSetup()); Assert.AreNotEqual(before, screen.transform.position);
                var recalled = screen.transform.position; head.transform.position += Vector3.right;
                typeof(ScreenAnchor).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(anchor, null);
                Assert.AreEqual(recalled, screen.transform.position);
                anchor.SetSetupFrozen(false); Assert.IsFalse(anchor.RepositionForSetup());
                typeof(ScreenAnchor).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(anchor, null);
                Assert.AreNotEqual(recalled, screen.transform.position);
            }
            finally { Object.DestroyImmediate(screen); Object.DestroyImmediate(head); }
        }
    }
}
