#nullable enable

using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    public sealed class UnauthorizedAccessEffectTests
    {
        private static readonly Vector2 ReferenceSize = new Vector2(2.7f, 1.51875f);

        [Test]
        public void PlayBuildsBothLayers_StopHidesBoth()
        {
            GameObject anchor = NewAnchor("LifecycleAnchor");
            GameObject host = new GameObject("LifecycleEffect");
            try
            {
                var effect = host.AddComponent<UnauthorizedAccessEffect>();
                effect.Play(anchor.transform, ReferenceSize);
                effect.Sample(2.5f);

                Assert.IsTrue(effect.IsPlaying);
                Assert.AreSame(anchor.transform, effect.ScreenAnchor);
                Assert.Greater(effect.SpatialElementCount, 180);
                Assert.AreEqual(880, effect.GlyphVertexCountDiagnostic, "220 glyph × 4 vertices");
                Assert.AreEqual(44, effect.WarningVertexCountDiagnostic, "11 bands × 4 vertices");
                Assert.AreEqual(12, effect.SubtitleVertexCountDiagnostic, "3 bands × 4 vertices");
                Assert.AreEqual(4, effect.SymbolVertexCountDiagnostic, "triangle image × 4 vertices");
                Assert.AreEqual(56, FindMesh(effect, "Unauthorized Access Decorations").vertexCount,
                    "2 exclamation quads + 12 hazard stripe quads");
                Assert.IsTrue(FindRoot(effect, "UnauthorizedAccess.Screen").gameObject.activeSelf);
                Assert.IsTrue(FindRoot(effect, "UnauthorizedAccess.Spatial").gameObject.activeSelf);

                effect.Stop();
                Assert.IsFalse(effect.IsPlaying);
                Assert.IsFalse(FindRoot(effect, "UnauthorizedAccess.Screen").gameObject.activeSelf);
                Assert.IsFalse(FindRoot(effect, "UnauthorizedAccess.Spatial").gameObject.activeSelf);
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
                a.Play(anchorA.transform, ReferenceSize);
                b.Play(anchorB.transform, new Vector2(1.8f, 1f));
                a.Sample(3f);
                b.Sample(4f);

                Assert.IsTrue(FindRoot(a, "UnauthorizedAccess.Screen").gameObject.activeSelf);
                Assert.IsTrue(FindRoot(b, "UnauthorizedAccess.Screen").gameObject.activeSelf);
                Assert.AreNotSame(FindRoot(a, "UnauthorizedAccess.Spatial"),
                    FindRoot(b, "UnauthorizedAccess.Spatial"));
                Assert.AreNotSame(FindMesh(a, "Unauthorized Access Glyphs"),
                    FindMesh(b, "Unauthorized Access Glyphs"));
                Assert.AreNotSame(FindMesh(a, "Unauthorized Access Symbol"),
                    FindMesh(b, "Unauthorized Access Symbol"));
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
        public void ReplayingReusesGeneratedObjectsAndMeshes()
        {
            GameObject anchor = NewAnchor("ReplayAnchor");
            GameObject host = new GameObject("ReplayEffect");
            try
            {
                var effect = host.AddComponent<UnauthorizedAccessEffect>();
                effect.Play(anchor.transform, ReferenceSize);
                effect.Sample(2f);
                Transform screen = FindRoot(effect, "UnauthorizedAccess.Screen");
                Transform spatial = FindRoot(effect, "UnauthorizedAccess.Spatial");
                Mesh warning = FindMesh(effect, "Unauthorized Access Warning Bands");
                Mesh subtitle = FindMesh(effect, "Unauthorized Access Subtitle Bands");
                Mesh symbol = FindMesh(effect, "Unauthorized Access Symbol");
                Mesh decorations = FindMesh(effect, "Unauthorized Access Decorations");
                Mesh glyphs = FindMesh(effect, "Unauthorized Access Glyphs");
                Mesh strips = FindMesh(effect, "Unauthorized Access Interference");
                int screenChildren = screen.childCount;
                int spatialChildren = spatial.childCount;

                Assert.Greater(warning.vertexCount, 0);
                Assert.Greater(subtitle.vertexCount, 0);
                Assert.Greater(symbol.vertexCount, 0);
                Assert.Greater(decorations.vertexCount, 0);
                Assert.Greater(glyphs.vertexCount, 0);
                Assert.Greater(strips.vertexCount, 0);
                effect.Stop();
                effect.Play(anchor.transform, new Vector2(2.2f, 1.2f));
                effect.Sample(2f);

                Assert.AreSame(screen, FindRoot(effect, "UnauthorizedAccess.Screen"));
                Assert.AreSame(spatial, FindRoot(effect, "UnauthorizedAccess.Spatial"));
                Assert.AreSame(warning, FindMesh(effect, "Unauthorized Access Warning Bands"));
                Assert.AreSame(subtitle, FindMesh(effect, "Unauthorized Access Subtitle Bands"));
                Assert.AreSame(symbol, FindMesh(effect, "Unauthorized Access Symbol"));
                Assert.AreSame(decorations, FindMesh(effect, "Unauthorized Access Decorations"));
                Assert.AreSame(glyphs, FindMesh(effect, "Unauthorized Access Glyphs"));
                Assert.AreSame(strips, FindMesh(effect, "Unauthorized Access Interference"));
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
        public void SampleIsDeterministicAndOutsideDurationIsPixelEmptyWithoutChangingPlayback()
        {
            GameObject anchor = NewAnchor("SampleAnchor");
            GameObject host = new GameObject("SampleEffect");
            try
            {
                var effect = host.AddComponent<UnauthorizedAccessEffect>();
                effect.Play(anchor.transform, ReferenceSize);
                float elapsed = effect.Elapsed;
                Transform screen = FindRoot(effect, "UnauthorizedAccess.Screen");
                Transform spatial = FindRoot(effect, "UnauthorizedAccess.Spatial");

                Assert.IsFalse(screen.gameObject.activeSelf, "0秒は画素を出さない");
                Assert.IsFalse(spatial.gameObject.activeSelf, "0秒は画素を出さない");

                effect.Sample(2.11f);
                Mesh glyphs = FindMesh(effect, "Unauthorized Access Glyphs");
                Mesh subtitle = FindMesh(effect, "Unauthorized Access Subtitle Bands");
                Mesh symbol = FindMesh(effect, "Unauthorized Access Symbol");
                Vector3[] firstGlyphVertices = glyphs.vertices;
                Color[] firstGlyphColors = glyphs.colors;
                Vector3[] firstSubtitleVertices = subtitle.vertices;
                Color[] firstSymbolColors = symbol.colors;
                effect.Sample(3.72f);
                effect.Sample(2.11f);
                CollectionAssert.AreEqual(firstGlyphVertices, glyphs.vertices);
                CollectionAssert.AreEqual(firstGlyphColors, glyphs.colors);
                CollectionAssert.AreEqual(firstSubtitleVertices, subtitle.vertices);
                CollectionAssert.AreEqual(firstSymbolColors, symbol.colors);
                Assert.IsTrue(effect.IsPlaying);
                Assert.AreEqual(elapsed, effect.Elapsed);

                effect.Sample(-.01f);
                Assert.IsFalse(screen.gameObject.activeSelf);
                Assert.IsFalse(spatial.gameObject.activeSelf);
                effect.Sample(effect.Duration);
                Assert.IsFalse(screen.gameObject.activeSelf, "7秒は0秒と同じく画素を出さない");
                Assert.IsFalse(spatial.gameObject.activeSelf);
                Assert.IsTrue(effect.IsPlaying, "Sampleは再生状態を触らない");
                Assert.AreEqual(elapsed, effect.Elapsed, "Sampleは経過時刻を触らない");

                effect.Stop();
                Assert.IsFalse(screen.gameObject.activeSelf, "Stopも0秒・7秒と同じく画素を出さない");
                Assert.IsFalse(spatial.gameObject.activeSelf);
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
            anchor.transform.localScale = new Vector3(.8f, 1.3f, 1.7f);
            GameObject host = new GameObject("PoseEffect");
            try
            {
                var effect = host.AddComponent<UnauthorizedAccessEffect>();
                effect.Play(anchor.transform, ReferenceSize);
                effect.Sample(1.2f);
                Transform screen = FindRoot(effect, "UnauthorizedAccess.Screen");
                Transform spatial = FindRoot(effect, "UnauthorizedAccess.Spatial");
                Vector3 spatialPosition = spatial.position;
                Quaternion spatialRotation = spatial.rotation;
                Mesh warning = FindMesh(effect, "Unauthorized Access Warning Bands");
                Mesh subtitle = FindMesh(effect, "Unauthorized Access Subtitle Bands");
                Mesh symbol = FindMesh(effect, "Unauthorized Access Symbol");
                Vector3[] warningVertices = warning.vertices;
                Vector3[] subtitleVertices = subtitle.vertices;
                Vector3[] symbolVertices = symbol.vertices;

                anchor.transform.SetPositionAndRotation(new Vector3(-2f, 1f, 4f), Quaternion.Euler(8f, 95f, 0f));
                effect.Sample(1.2f);

                Assert.Less(Vector3.Distance(anchor.transform.position - anchor.transform.forward * .06f,
                    screen.position), .0001f, "警告面は映像面の6cm手前");
                Assert.Less(Quaternion.Angle(anchor.transform.rotation, screen.rotation), .001f);
                Assert.Less(Vector3.Distance(spatialPosition, spatial.position), .0001f);
                Assert.Less(Quaternion.Angle(spatialRotation, spatial.rotation), .001f);
                CollectionAssert.AreEqual(warningVertices, warning.vertices);
                CollectionAssert.AreEqual(subtitleVertices, subtitle.vertices);
                CollectionAssert.AreEqual(symbolVertices, symbol.vertices,
                    "screen側パーツのlocal poseはanchor移動で変化しない");
                Assert.IsNull(spatial.parent, "空間側はanchorの子に残さない");
                Assert.AreEqual(host.scene, spatial.gameObject.scene, "空間rootはcomponentと同じsceneに置く");
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
            effect.Play(anchor.transform, ReferenceSize);
            effect.Sample(1f);
            Transform spatial = FindRoot(effect, "UnauthorizedAccess.Spatial");

            effect.enabled = false;
            Assert.IsFalse(spatial.gameObject.activeSelf);
            Assert.IsFalse(effect.IsPlaying);

            Object.DestroyImmediate(host);
            Assert.IsTrue(spatial == null, "独立world rootが残っている");
            Object.DestroyImmediate(anchor);
        }

        private static GameObject NewAnchor(string name) => new GameObject(name);

        private static Transform FindRoot(UnauthorizedAccessEffect effect, string name)
        {
            string fieldName = name.EndsWith(".Spatial") ? "_spatialRoot" : "_screenRoot";
            return (Transform)typeof(UnauthorizedAccessEffect)
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(effect)!;
        }

        private static Mesh FindMesh(UnauthorizedAccessEffect effect, string name) =>
            FindMeshTransform(effect, name).GetComponent<MeshFilter>().sharedMesh;

        private static Transform FindMeshTransform(UnauthorizedAccessEffect effect, string name)
        {
            Transform screen = FindRoot(effect, "UnauthorizedAccess.Screen");
            Transform spatial = FindRoot(effect, "UnauthorizedAccess.Spatial");
            MeshFilter[] screenMeshes = screen.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < screenMeshes.Length; i++)
                if (screenMeshes[i].sharedMesh.name == name) return screenMeshes[i].transform;
            MeshFilter[] spatialMeshes = spatial.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < spatialMeshes.Length; i++)
                if (spatialMeshes[i].sharedMesh.name == name) return spatialMeshes[i].transform;
            Assert.Fail($"{name} was not found.");
            return null!;
        }
    }
}
