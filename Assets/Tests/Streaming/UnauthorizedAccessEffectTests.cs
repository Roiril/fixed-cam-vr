#nullable enable

using NUnit.Framework;
using UnityEngine;
using System.Reflection;

namespace FixedCamVr.Streaming.Tests
{
    public sealed class UnauthorizedAccessEffectTests
    {
        [Test]
        public void PlayBuildsBothLayers_StopHidesBoth()
        {
            GameObject anchor = NewAnchor("LifecycleAnchor");
            GameObject host = new GameObject("LifecycleEffect");
            try
            {
                var effect = host.AddComponent<UnauthorizedAccessEffect>();
                effect.Play(anchor.transform, new Vector2(2.7f, 1.51875f));
                effect.Sample(2.5f);

                Assert.IsTrue(effect.IsPlaying);
                Assert.AreSame(anchor.transform, effect.ScreenAnchor);
                Assert.AreEqual(26, effect.SpatialElementCount, "18列 + 4情報 + 4本の空間罫線");
                Assert.IsTrue(FindRoot(host, "UnauthorizedAccess.Screen").gameObject.activeSelf);
                Assert.IsTrue(FindRoot(host, "UnauthorizedAccess.Spatial").gameObject.activeSelf);

                effect.Stop();
                Assert.IsFalse(effect.IsPlaying);
                Assert.IsFalse(FindRoot(host, "UnauthorizedAccess.Screen").gameObject.activeSelf);
                Assert.IsFalse(FindRoot(host, "UnauthorizedAccess.Spatial").gameObject.activeSelf);
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(anchor);
            }
        }

        [Test]
        public void TwoEffectsCanRemainVisibleAtTheSameTime()
        {
            GameObject anchorA = NewAnchor("AnchorA");
            GameObject anchorB = NewAnchor("AnchorB");
            GameObject hostA = new GameObject("EffectA");
            GameObject hostB = new GameObject("EffectB");
            try
            {
                var a = hostA.AddComponent<UnauthorizedAccessEffect>();
                var b = hostB.AddComponent<UnauthorizedAccessEffect>();
                a.Play(anchorA.transform, new Vector2(2.7f, 1.51875f));
                b.Play(anchorB.transform, new Vector2(1.8f, 1.0f));
                a.Sample(3f);
                b.Sample(4f);

                Assert.IsTrue(FindRoot(hostA, "UnauthorizedAccess.Screen").gameObject.activeSelf);
                Assert.IsTrue(FindRoot(hostB, "UnauthorizedAccess.Screen").gameObject.activeSelf);
                Assert.AreNotSame(FindRoot(hostA, "UnauthorizedAccess.Spatial"),
                    FindRoot(hostB, "UnauthorizedAccess.Spatial"));
            }
            finally
            {
                Object.DestroyImmediate(hostA);
                Object.DestroyImmediate(hostB);
                Object.DestroyImmediate(anchorA);
                Object.DestroyImmediate(anchorB);
            }
        }

        [Test]
        public void ReplayingReusesGeneratedObjects()
        {
            GameObject anchor = NewAnchor("ReplayAnchor");
            GameObject host = new GameObject("ReplayEffect");
            try
            {
                var effect = host.AddComponent<UnauthorizedAccessEffect>();
                effect.Play(anchor.transform, new Vector2(2.7f, 1.51875f));
                Transform screen = FindRoot(host, "UnauthorizedAccess.Screen");
                Transform spatial = FindRoot(host, "UnauthorizedAccess.Spatial");
                int screenChildren = screen.childCount;
                int spatialChildren = spatial.childCount;

                effect.Stop();
                effect.Play(anchor.transform, new Vector2(2.2f, 1.2f));

                Assert.AreSame(screen, FindRoot(host, "UnauthorizedAccess.Screen"));
                Assert.AreSame(spatial, FindRoot(host, "UnauthorizedAccess.Spatial"));
                Assert.AreEqual(screenChildren, screen.childCount);
                Assert.AreEqual(spatialChildren, spatial.childCount);
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(anchor);
            }
        }

        [Test]
        public void SampleOutsideDurationHidesWithoutChangingPlaybackStateOrElapsed()
        {
            GameObject anchor = NewAnchor("RangeAnchor");
            GameObject host = new GameObject("RangeEffect");
            try
            {
                var effect = host.AddComponent<UnauthorizedAccessEffect>();
                effect.Play(anchor.transform, new Vector2(2.7f, 1.51875f));
                float elapsed = effect.Elapsed;
                effect.Sample(2f);
                Assert.IsTrue(effect.IsPlaying);
                Assert.AreEqual(elapsed, effect.Elapsed);
                Assert.IsTrue(FindRoot(host, "UnauthorizedAccess.Screen").gameObject.activeSelf);

                effect.Sample(-0.01f);
                Assert.IsFalse(FindRoot(host, "UnauthorizedAccess.Screen").gameObject.activeSelf);
                Assert.IsFalse(FindRoot(host, "UnauthorizedAccess.Spatial").gameObject.activeSelf);
                Assert.IsTrue(effect.IsPlaying, "Sampleは再生状態を触らない");
                Assert.AreEqual(elapsed, effect.Elapsed, "Sampleは経過時刻を触らない");

                effect.Sample(effect.Duration);
                Assert.IsFalse(FindRoot(host, "UnauthorizedAccess.Screen").gameObject.activeSelf);
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(anchor);
            }
        }

        [Test]
        public void ScreenFollowsAnchorWhileSpatialLayerStaysAtTriggerPose()
        {
            GameObject anchor = NewAnchor("PoseAnchor");
            anchor.transform.SetPositionAndRotation(new Vector3(1f, 2f, 3f), Quaternion.Euler(0f, 20f, 0f));
            GameObject host = new GameObject("PoseEffect");
            try
            {
                var effect = host.AddComponent<UnauthorizedAccessEffect>();
                effect.Play(anchor.transform, new Vector2(2.7f, 1.51875f));
                Transform screen = FindRoot(host, "UnauthorizedAccess.Screen");
                Transform spatial = FindRoot(host, "UnauthorizedAccess.Spatial");
                Vector3 spatialPosition = spatial.position;
                Quaternion spatialRotation = spatial.rotation;

                anchor.transform.SetPositionAndRotation(new Vector3(-2f, 1f, 4f), Quaternion.Euler(8f, 95f, 0f));
                effect.Sample(3f);

                Assert.Less(Vector3.Distance(anchor.transform.position - anchor.transform.forward * .06f,
                    screen.position), .0001f, "警告面は映像面の6cm手前");
                Assert.Less(Quaternion.Angle(anchor.transform.rotation, screen.rotation), 0.001f);
                Assert.AreEqual(spatialPosition, spatial.position);
                Assert.Less(Quaternion.Angle(spatialRotation, spatial.rotation), 0.001f);
                Assert.IsNull(spatial.parent, "空間側は頭の子に残さない");
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(anchor);
            }
        }

        [Test]
        public void DisablingHidesAndDestroyingRemovesDetachedSpatialRoot()
        {
            GameObject anchor = NewAnchor("DestroyAnchor");
            GameObject host = new GameObject("DestroyEffect");
            var effect = host.AddComponent<UnauthorizedAccessEffect>();
            effect.Play(anchor.transform, new Vector2(2.7f, 1.51875f));
            Transform spatial = FindRoot(host, "UnauthorizedAccess.Spatial");

            effect.enabled = false;
            Assert.IsFalse(spatial.gameObject.activeSelf);
            Assert.IsFalse(effect.IsPlaying);

            Object.DestroyImmediate(host);
            Assert.IsTrue(spatial == null, "独立world rootが残っている");
            Object.DestroyImmediate(anchor);
        }

        private static GameObject NewAnchor(string name) => new GameObject(name);

        private static Transform FindRoot(GameObject host, string name)
        {
            UnauthorizedAccessEffect owner = host.GetComponent<UnauthorizedAccessEffect>();
            if (name.EndsWith(".Spatial"))
            {
                return (Transform)typeof(UnauthorizedAccessEffect)
                    .GetField("_spatialRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(owner)!;
            }
            Transform? anchor = owner.ScreenAnchor;
            if (anchor != null)
                for (int i = 0; i < anchor.childCount; i++)
                    if (anchor.GetChild(i).name == name) return anchor.GetChild(i);
            Assert.Fail($"{name} was not found.");
            return null!;
        }
    }
}
