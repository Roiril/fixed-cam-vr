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
                Assert.AreEqual(4, effect.ContextVertexCountDiagnostic, "context image × 4 vertices");
                Assert.AreEqual(4, effect.StatusVertexCountDiagnostic, "status image × 4 vertices");
                Assert.AreEqual(4, effect.FailureVertexCountDiagnostic, "failure image × 4 vertices");
                Assert.AreEqual(56, FindMesh(effect, "Unauthorized Access Decorations").vertexCount,
                    "2 exclamation quads + 12 hazard stripe quads");
                Assert.AreEqual(4, FindMesh(effect, "Unauthorized Access Context").vertexCount);
                Assert.AreEqual(4, FindMesh(effect, "Unauthorized Access Status").vertexCount);
                Assert.AreEqual(4, FindMesh(effect, "Unauthorized Access Failure").vertexCount);
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
                Assert.AreNotSame(FindMesh(a, "Unauthorized Access Context"),
                    FindMesh(b, "Unauthorized Access Context"));
                Assert.AreNotSame(FindMesh(a, "Unauthorized Access Status"),
                    FindMesh(b, "Unauthorized Access Status"));
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
                Mesh context = FindMesh(effect, "Unauthorized Access Context");
                Mesh status = FindMesh(effect, "Unauthorized Access Status");
                Mesh failure = FindMesh(effect, "Unauthorized Access Failure");
                Mesh glyphs = FindMesh(effect, "Unauthorized Access Glyphs");
                Mesh strips = FindMesh(effect, "Unauthorized Access Interference");
                int screenChildren = screen.childCount;
                int spatialChildren = spatial.childCount;

                Assert.Greater(warning.vertexCount, 0);
                Assert.Greater(subtitle.vertexCount, 0);
                Assert.Greater(symbol.vertexCount, 0);
                Assert.Greater(decorations.vertexCount, 0);
                Assert.Greater(context.vertexCount, 0);
                Assert.Greater(status.vertexCount, 0);
                Assert.Greater(failure.vertexCount, 0);
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
                Assert.AreSame(context, FindMesh(effect, "Unauthorized Access Context"));
                Assert.AreSame(status, FindMesh(effect, "Unauthorized Access Status"));
                Assert.AreSame(failure, FindMesh(effect, "Unauthorized Access Failure"));
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
            Mesh context = FindMesh(effect, "Unauthorized Access Context");
            Mesh status = FindMesh(effect, "Unauthorized Access Status");
            Mesh failure = FindMesh(effect, "Unauthorized Access Failure");

            effect.enabled = false;
            Assert.IsFalse(spatial.gameObject.activeSelf);
            Assert.IsFalse(effect.IsPlaying);

            Object.DestroyImmediate(host);
            Assert.IsTrue(spatial == null, "独立world rootが残っている");
            Assert.IsTrue(context == null, "context meshが残っている");
            Assert.IsTrue(status == null, "status meshが残っている");
            Assert.IsTrue(failure == null, "failure meshが残っている");
            Object.DestroyImmediate(anchor);
        }

        [Test]
        public void ContextAndStatusPhasesAreDeterministicAcrossArbitrarySampleOrder()
        {
            GameObject anchor = NewAnchor("PhaseAnchor");
            GameObject host = new GameObject("PhaseEffect");
            Material? material = null;
            Texture2D? warning = null;
            Texture2D? subtitle = null;
            Texture2D? symbol = null;
            Texture2D? noise = null;
            Texture2D? context = null;
            Texture2D? attempt = null;
            Texture2D? failed = null;
            try
            {
                Shader shader = Shader.Find("FixedCamVr/UnauthorizedAccessFx");
                Assert.IsNotNull(shader);
                material = new Material(shader);
                warning = NewTexture("warning");
                subtitle = NewTexture("subtitle");
                symbol = NewTexture("symbol");
                noise = NewTexture("noise");
                context = NewTexture("context");
                attempt = NewTexture("attempt");
                failed = NewTexture("failed");
                var effect = host.AddComponent<UnauthorizedAccessEffect>();
                effect.Configure(material, warning, subtitle, symbol, noise, context, attempt, failed);
                effect.Play(anchor.transform, ReferenceSize);

                effect.Sample(6.7f);
                Material statusMaterial = FindMaterial(effect, "Unauthorized Access Status");
                Assert.AreSame(failed, statusMaterial.GetTexture("_MainTex"));
                Assert.Greater(FindMesh(effect, "Unauthorized Access Status").colors[0].a, 0f);
                effect.Sample(1.8f);
                Material contextMaterial = FindMaterial(effect, "Unauthorized Access Context");
                Assert.AreSame(context, contextMaterial.GetTexture("_MainTex"));
                Assert.AreSame(attempt, statusMaterial.GetTexture("_MainTex"));
                Color contextColor = FindMesh(effect, "Unauthorized Access Context").colors[0];
                Color attemptColor = FindMesh(effect, "Unauthorized Access Status").colors[0];
                Assert.Greater(contextColor.a, 0f);
                Assert.Greater(attemptColor.a, 0f);

                effect.Sample(4.2f);
                Assert.AreSame(failed, statusMaterial.GetTexture("_MainTex"));
                Color failedColor = FindMesh(effect, "Unauthorized Access Status").colors[0];
                Assert.Greater(failedColor.a, 0f);
                effect.Sample(1.8f);
                Assert.AreSame(attempt, statusMaterial.GetTexture("_MainTex"));
                Assert.AreEqual(contextColor, FindMesh(effect, "Unauthorized Access Context").colors[0]);
                Assert.AreEqual(attemptColor, FindMesh(effect, "Unauthorized Access Status").colors[0]);

                effect.Sample(effect.Duration);
                Assert.IsFalse(FindRoot(effect, "UnauthorizedAccess.Screen").gameObject.activeSelf);
                Assert.IsFalse(FindRoot(effect, "UnauthorizedAccess.Spatial").gameObject.activeSelf);
                effect.Stop();
                Assert.IsFalse(FindRoot(effect, "UnauthorizedAccess.Screen").gameObject.activeSelf);
                Assert.IsFalse(FindRoot(effect, "UnauthorizedAccess.Spatial").gameObject.activeSelf);
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(anchor);
                DestroyImmediate(material);
                DestroyImmediate(warning);
                DestroyImmediate(subtitle);
                DestroyImmediate(symbol);
                DestroyImmediate(noise);
                DestroyImmediate(context);
                DestroyImmediate(attempt);
                DestroyImmediate(failed);
            }
        }

        [Test]
        public void TakeoverSampleAccumulatesFiveFixedMessagesAtAbsoluteSeconds()
        {
            GameObject anchor = NewAnchor("TakeoverAnchor");
            GameObject host = new GameObject("TakeoverEffect");
            Material? material = null;
            Texture2D? warning = null;
            Texture2D? subtitle = null;
            Texture2D? symbol = null;
            Texture2D? noise = null;
            Texture2D? context = null;
            Texture2D? attempt = null;
            Texture2D? failed = null;
            try
            {
                Shader shader = Shader.Find("FixedCamVr/UnauthorizedAccessFx");
                Assert.IsNotNull(shader);
                material = new Material(shader);
                warning = NewTexture("warning");
                subtitle = NewTexture("subtitle");
                symbol = NewTexture("symbol");
                noise = NewTexture("noise");
                context = NewTexture("context");
                attempt = NewTexture("attempt");
                failed = NewTexture("failed");
                var effect = host.AddComponent<UnauthorizedAccessEffect>();
                effect.Configure(material, warning, subtitle, symbol, noise, context, attempt, failed);
                effect.Play(anchor.transform, ReferenceSize);

                Material status = FindMaterial(effect, "Unauthorized Access Status");
                Material failure = FindMaterial(effect, "Unauthorized Access Failure");
                effect.SampleTakeover(0f, false, 1f);
                Assert.AreEqual("WARNING", effect.TakeoverCaption);
                AssertCumulativeTextVisible(effect, 1);
                AssertUniformPositiveAlpha(effect, "Unauthorized Access Warning Bands");
                Assert.AreEqual(.34f * ReferenceSize.y,
                    MeshCenter(effect, "Unauthorized Access Warning Bands").y, .0001f);
                Assert.AreEqual(.075f * ReferenceSize.y,
                    MeshSize(effect, "Unauthorized Access Warning Bands").y, .0001f);
                Vector3[][] fixedVertices = CaptureTextVertices(effect);
                effect.SampleTakeover(.999f, true, 1f);
                AssertCumulativeTextVisible(effect, 1);
                AssertTextVerticesEqual(fixedVertices, CaptureTextVertices(effect));

                effect.SampleTakeover(1f, false, 1f);
                Assert.AreEqual("WARNING | 不正アクセス", effect.TakeoverCaption);
                AssertCumulativeTextVisible(effect, 2);
                AssertUniformPositiveAlpha(effect, "Unauthorized Access Subtitle Bands");
                Assert.AreEqual(.24f * ReferenceSize.y,
                    MeshCenter(effect, "Unauthorized Access Subtitle Bands").y, .0001f);
                Assert.AreEqual(.06f * ReferenceSize.y,
                    MeshSize(effect, "Unauthorized Access Subtitle Bands").y, .0001f);
                effect.SampleTakeover(1.999f, true, 1f);
                AssertCumulativeTextVisible(effect, 2);

                effect.SampleTakeover(2f, false, 1f);
                Assert.AreEqual("WARNING | 不正アクセス | 接続元不明", effect.TakeoverCaption);
                AssertCumulativeTextVisible(effect, 3);
                AssertUniformPositiveAlpha(effect, "Unauthorized Access Context");
                Assert.AreEqual(.155f * ReferenceSize.y,
                    MeshCenter(effect, "Unauthorized Access Context").y, .0001f);
                Assert.AreEqual(.06f * ReferenceSize.y,
                    MeshSize(effect, "Unauthorized Access Context").y, .0001f);
                effect.SampleTakeover(2.999f, true, 1f);
                AssertCumulativeTextVisible(effect, 3);

                effect.SampleTakeover(3f, true, 1f);
                Assert.AreEqual("WARNING | 不正アクセス | 接続元不明 | 遮断を執行", effect.TakeoverCaption);
                AssertCumulativeTextVisible(effect, 4);
                AssertUniformPositiveAlpha(effect, "Unauthorized Access Status");
                Assert.AreSame(attempt, status.GetTexture("_MainTex"),
                    "blockFailedが早く届いても4秒までは遮断を執行する");
                Assert.AreSame(failed, failure.GetTexture("_MainTex"));
                Assert.AreEqual(.07f * ReferenceSize.y,
                    MeshCenter(effect, "Unauthorized Access Status").y, .0001f);
                Assert.AreEqual(.06f * ReferenceSize.y,
                    MeshSize(effect, "Unauthorized Access Status").y, .0001f);
                effect.SampleTakeover(3.999f, true, 1f);
                Assert.AreSame(attempt, status.GetTexture("_MainTex"));
                AssertCumulativeTextVisible(effect, 4);

                effect.SampleTakeover(4f, false, 1f);
                Assert.AreEqual("WARNING | 不正アクセス | 接続元不明 | 遮断を執行 | 失敗",
                    effect.TakeoverCaption);
                AssertCumulativeTextVisible(effect, 5);
                Assert.AreSame(attempt, status.GetTexture("_MainTex"), "遮断を執行の画像を差し替えない");
                Assert.AreSame(failed, failure.GetTexture("_MainTex"));
                Assert.AreEqual(-.015f * ReferenceSize.y,
                    MeshCenter(effect, "Unauthorized Access Failure").y, .0001f);
                Assert.AreEqual(.06f * ReferenceSize.y,
                    MeshSize(effect, "Unauthorized Access Failure").y, .0001f);
                AssertTextVerticesEqual(fixedVertices, CaptureTextVertices(effect));
                AssertTakeoverTextDoesNotOverlap(effect);

                effect.SampleTakeover(8f, true, 1f);
                Transform screen = FindRoot(effect, "UnauthorizedAccess.Screen");
                Transform spatial = FindRoot(effect, "UnauthorizedAccess.Spatial");
                Vector3[] before = FindMesh(effect, "Unauthorized Access Glyphs").vertices;
                Assert.Greater(MaxAlpha(screen), 0f);
                Assert.Greater(MaxAlpha(spatial), 0f);
                effect.SampleTakeover(8.1f, true, 1f);
                Assert.IsTrue(VerticesDiffer(before, FindMesh(effect, "Unauthorized Access Glyphs").vertices),
                    "7秒を越えても空間の数字が動く");

                effect.SampleTakeover(4f, true, 1f);
                float fullAlpha = MaxAlpha(screen);
                effect.SampleTakeover(4f, true, .35f);
                Assert.AreEqual(fullAlpha * .35f, MaxAlpha(screen), .0001f);
                Assert.Greater(MaxAlpha(spatial), 0f, "低いopacityでも空間側を同時に残す");
                effect.SampleTakeover(4f, true, 0f);
                Assert.AreEqual(string.Empty, effect.TakeoverCaption);
                Assert.IsFalse(screen.gameObject.activeSelf);
                Assert.IsFalse(spatial.gameObject.activeSelf);

                effect.Sample(2f);
                Assert.IsTrue(screen.gameObject.activeSelf, "通常SampleはTakeover専用modeを解除する");
                Assert.AreSame(attempt, status.GetTexture("_MainTex"));
                AssertAlpha(effect, "Unauthorized Access Failure", 0f);
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(anchor);
                DestroyImmediate(material);
                DestroyImmediate(warning);
                DestroyImmediate(subtitle);
                DestroyImmediate(symbol);
                DestroyImmediate(noise);
                DestroyImmediate(context);
                DestroyImmediate(attempt);
                DestroyImmediate(failed);
            }
        }

        [Test]
        public void TakeoverKnownTransparentPaddingUsesVisibleGlyphBounds()
        {
            GameObject anchor = NewAnchor("PaddingAnchor");
            GameObject host = new GameObject("PaddingEffect");
            Material? material = null;
            Texture2D[] textures = new Texture2D[0];
            Texture2D? failed = null;
            try
            {
                var effect = ConfigureTakeoverEffect(host, out material, out textures);
                failed = new Texture2D(211, 107) { name = "failed-v4" };
                effect.Configure(material, textures[0], textures[1], textures[2], textures[3],
                    textures[4], textures[5], failed);
                effect.Play(anchor.transform, ReferenceSize);
                effect.SampleTakeover(4f, true, 1f);

                Bounds bounds = MeshBounds(effect, "Unauthorized Access Failure");
                float visibleMinX = bounds.min.x + bounds.size.x * (10f / 211f);
                float visibleMaxX = bounds.min.x + bounds.size.x * (200f / 211f);
                float visibleMinY = bounds.min.y + bounds.size.y * (8f / 107f);
                float visibleMaxY = bounds.min.y + bounds.size.y * (99f / 107f);
                Assert.AreEqual(0f, (visibleMinX + visibleMaxX) * .5f, .0001f);
                Assert.AreEqual(-.015f * ReferenceSize.y, (visibleMinY + visibleMaxY) * .5f, .0001f);
                Assert.AreEqual(.06f * ReferenceSize.y, visibleMaxY - visibleMinY, .0001f);
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(anchor);
                DestroyImmediate(material);
                DestroyImmediate(textures);
                DestroyImmediate(failed);
            }
        }

        [Test]
        public void TakeoverReadFocusKeepsAccumulatedMessagesReadableAndSuppressesSurroundings()
        {
            GameObject anchor = NewAnchor("ReadFocusAnchor");
            GameObject host = new GameObject("ReadFocusEffect");
            Material? material = null;
            Texture2D[] textures = new Texture2D[0];
            try
            {
                var effect = ConfigureTakeoverEffect(host, out material, out textures);
                effect.Play(anchor.transform, ReferenceSize);
                const float seconds = .5f;

                effect.SampleTakeover(seconds, false, 1f);
                float warning = MaxAlpha(effect, "Unauthorized Access Warning Bands");
                float symbol = MaxAlpha(effect, "Unauthorized Access Symbol");
                float decorations = MaxAlpha(effect, "Unauthorized Access Decorations");
                float glyphs = MaxAlpha(effect, "Unauthorized Access Glyphs");
                float interference = MaxAlpha(effect, "Unauthorized Access Interference");

                effect.SampleTakeover(seconds, false, .35f, 1f);
                AssertAlpha(effect, "Unauthorized Access Warning Bands", warning * .55f);
                AssertAlpha(effect, "Unauthorized Access Subtitle Bands", 0f);
                AssertAlpha(effect, "Unauthorized Access Symbol", symbol * .14f);
                AssertAlpha(effect, "Unauthorized Access Decorations", decorations * .14f);
                AssertAlpha(effect, "Unauthorized Access Context", 0f);
                AssertAlpha(effect, "Unauthorized Access Status", 0f);
                AssertAlpha(effect, "Unauthorized Access Glyphs", glyphs * .14f);
                AssertAlpha(effect, "Unauthorized Access Interference", interference * .14f);

                effect.SampleTakeover(1.5f, false, .35f, 1f);
                AssertAlpha(effect, "Unauthorized Access Warning Bands", .55f);
                AssertAlpha(effect, "Unauthorized Access Subtitle Bands", .55f);
                effect.SampleTakeover(2.5f, false, .35f, 1f);
                AssertAlpha(effect, "Unauthorized Access Warning Bands", .55f);
                AssertAlpha(effect, "Unauthorized Access Subtitle Bands", .55f);
                AssertAlpha(effect, "Unauthorized Access Context", .55f);
                effect.SampleTakeover(3.5f, false, .35f, 1f);
                AssertAlpha(effect, "Unauthorized Access Status", .55f);
                AssertAlpha(effect, "Unauthorized Access Failure", 0f);
                effect.SampleTakeover(4.5f, true, .35f, 1f);
                AssertAlpha(effect, "Unauthorized Access Status", .55f);
                AssertAlpha(effect, "Unauthorized Access Failure", .55f);
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(anchor);
                DestroyImmediate(material);
                DestroyImmediate(textures);
            }
        }

        [Test]
        public void TakeoverPartialReadFocusInterpolatesEachOpacityCoefficient()
        {
            GameObject anchor = NewAnchor("PartialReadFocusAnchor");
            GameObject host = new GameObject("PartialReadFocusEffect");
            Material? material = null;
            Texture2D[] textures = new Texture2D[0];
            try
            {
                var effect = ConfigureTakeoverEffect(host, out material, out textures);
                effect.Play(anchor.transform, ReferenceSize);
                effect.SampleTakeover(2f, false, 1f);
                float context = MaxAlpha(effect, "Unauthorized Access Context");
                float symbol = MaxAlpha(effect, "Unauthorized Access Symbol");

                effect.SampleTakeover(2f, false, .35f, .5f);
                AssertAlpha(effect, "Unauthorized Access Context",
                    context * .35f * Mathf.Lerp(1f, .55f / .35f, .5f));
                AssertAlpha(effect, "Unauthorized Access Symbol",
                    symbol * .35f * Mathf.Lerp(1f, .14f / .35f, .5f));
                AssertAlpha(effect, "Unauthorized Access Status", 0f);
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(anchor);
                DestroyImmediate(material);
                DestroyImmediate(textures);
            }
        }

        [Test]
        public void TakeoverZeroOpacityHidesBothLayersWhileReadFocusIsActive()
        {
            GameObject anchor = NewAnchor("ReadFocusCutAnchor");
            GameObject host = new GameObject("ReadFocusCutEffect");
            Material? material = null;
            Texture2D[] textures = new Texture2D[0];
            try
            {
                var effect = ConfigureTakeoverEffect(host, out material, out textures);
                effect.Play(anchor.transform, ReferenceSize);
                effect.SampleTakeover(2f, true, .35f, 1f);
                Transform screen = FindRoot(effect, "UnauthorizedAccess.Screen");
                Transform spatial = FindRoot(effect, "UnauthorizedAccess.Spatial");
                Assert.IsTrue(screen.gameObject.activeSelf);
                Assert.IsTrue(spatial.gameObject.activeSelf);

                effect.SampleTakeover(2f, true, 0f, 1f);
                Assert.IsFalse(screen.gameObject.activeSelf);
                Assert.IsFalse(spatial.gameObject.activeSelf);
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(anchor);
                DestroyImmediate(material);
                DestroyImmediate(textures);
            }
        }

        [Test]
        public void RepeatedReadFocusSamplesDoNotAccumulateAndNormalSampleRestoresBaseColors()
        {
            GameObject anchor = NewAnchor("ReadFocusRepeatAnchor");
            GameObject host = new GameObject("ReadFocusRepeatEffect");
            Material? material = null;
            Texture2D[] textures = new Texture2D[0];
            try
            {
                var effect = ConfigureTakeoverEffect(host, out material, out textures);
                effect.Play(anchor.transform, ReferenceSize);
                effect.Sample(2f);
                Color[][] normalColors = CaptureColors(effect);

                effect.SampleTakeover(2f, false, .35f, 1f);
                Color[][] firstFocusedColors = CaptureColors(effect);
                effect.SampleTakeover(2f, false, .35f, 1f);
                AssertColorsEqual(firstFocusedColors, CaptureColors(effect));

                effect.Sample(2f);
                AssertColorsEqual(normalColors, CaptureColors(effect));
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(anchor);
                DestroyImmediate(material);
                DestroyImmediate(textures);
            }
        }

        private static GameObject NewAnchor(string name) => new GameObject(name);

        private static readonly string[] MeshNames =
        {
            "Unauthorized Access Warning Bands",
            "Unauthorized Access Subtitle Bands",
            "Unauthorized Access Symbol",
            "Unauthorized Access Decorations",
            "Unauthorized Access Context",
            "Unauthorized Access Status",
            "Unauthorized Access Failure",
            "Unauthorized Access Glyphs",
            "Unauthorized Access Interference",
        };

        private static readonly string[] TextMeshNames =
        {
            "Unauthorized Access Warning Bands",
            "Unauthorized Access Subtitle Bands",
            "Unauthorized Access Context",
            "Unauthorized Access Status",
            "Unauthorized Access Failure",
        };

        private static UnauthorizedAccessEffect ConfigureTakeoverEffect(GameObject host,
            out Material material, out Texture2D[] textures)
        {
            Shader shader = Shader.Find("FixedCamVr/UnauthorizedAccessFx");
            Assert.IsNotNull(shader);
            material = new Material(shader!);
            textures = new[]
            {
                NewTexture("warning"), NewTexture("subtitle"), NewTexture("symbol"),
                NewTexture("noise"), NewTexture("context"), NewTexture("attempt"),
                NewTexture("failed"),
            };
            var effect = host.AddComponent<UnauthorizedAccessEffect>();
            effect.Configure(material, textures[0], textures[1], textures[2], textures[3],
                textures[4], textures[5], textures[6]);
            return effect;
        }

        private static Texture2D NewTexture(string name)
        {
            var texture = new Texture2D(4, 2) { name = name };
            return texture;
        }

        private static void DestroyImmediate(Object? value)
        {
            if (value != null) Object.DestroyImmediate(value);
        }

        private static void DestroyImmediate(Object[] values)
        {
            for (int i = 0; i < values.Length; i++) DestroyImmediate(values[i]);
        }

        private static Transform FindRoot(UnauthorizedAccessEffect effect, string name)
        {
            string fieldName = name.EndsWith(".Spatial") ? "_spatialRoot" : "_screenRoot";
            return (Transform)typeof(UnauthorizedAccessEffect)
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(effect)!;
        }

        private static Mesh FindMesh(UnauthorizedAccessEffect effect, string name) =>
            FindMeshTransform(effect, name).GetComponent<MeshFilter>().sharedMesh;

        private static Material FindMaterial(UnauthorizedAccessEffect effect, string name) =>
            FindMeshTransform(effect, name).GetComponent<MeshRenderer>().sharedMaterial;

        private static float MaxAlpha(Transform root)
        {
            float max = 0f;
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                Color[] colors = filter.sharedMesh.colors;
                for (int i = 0; i < colors.Length; i++) max = Mathf.Max(max, colors[i].a);
            }
            return max;
        }

        private static float MaxAlpha(UnauthorizedAccessEffect effect, string name)
        {
            float max = 0f;
            Color[] colors = FindMesh(effect, name).colors;
            for (int i = 0; i < colors.Length; i++) max = Mathf.Max(max, colors[i].a);
            return max;
        }

        private static void AssertAlpha(UnauthorizedAccessEffect effect, string name, float expected) =>
            Assert.AreEqual(Mathf.Clamp01(expected), MaxAlpha(effect, name), .0001f, name);

        private static void AssertOnlyTextVisible(UnauthorizedAccessEffect effect, string visibleName)
        {
            string[] textMeshes =
            {
                "Unauthorized Access Warning Bands",
                "Unauthorized Access Subtitle Bands",
                "Unauthorized Access Context",
                "Unauthorized Access Status",
                "Unauthorized Access Failure",
            };
            for (int i = 0; i < textMeshes.Length; i++)
            {
                if (textMeshes[i] == visibleName)
                    Assert.Greater(MaxAlpha(effect, textMeshes[i]), 0f, textMeshes[i]);
                else
                    AssertAlpha(effect, textMeshes[i], 0f);
            }
        }

        private static void AssertCumulativeTextVisible(UnauthorizedAccessEffect effect, int visibleCount)
        {
            for (int i = 0; i < TextMeshNames.Length; i++)
            {
                if (i < visibleCount)
                    Assert.Greater(MaxAlpha(effect, TextMeshNames[i]), 0f, TextMeshNames[i]);
                else
                    AssertAlpha(effect, TextMeshNames[i], 0f);
            }
        }

        private static void AssertUniformPositiveAlpha(UnauthorizedAccessEffect effect, string name)
        {
            Color[] colors = FindMesh(effect, name).colors;
            Assert.Greater(colors.Length, 0, name);
            Assert.Greater(colors[0].a, 0f, name);
            for (int i = 1; i < colors.Length; i++)
                Assert.AreEqual(colors[0].a, colors[i].a, .0001f, $"{name} vertex {i}");
        }

        private static Vector2 MeshCenter(UnauthorizedAccessEffect effect, string name)
        {
            Vector3 center = MeshBounds(effect, name).center;
            return new Vector2(center.x, center.y);
        }

        private static Vector2 MeshSize(UnauthorizedAccessEffect effect, string name)
        {
            Vector3 size = MeshBounds(effect, name).size;
            return new Vector2(size.x, size.y);
        }

        private static Bounds MeshBounds(UnauthorizedAccessEffect effect, string name)
        {
            // 描画カリング用boundsは固定。文字の実際の頂点から範囲を測る。
            Vector3[] vertices = FindMesh(effect, name).vertices;
            var bounds = new Bounds(vertices[0], Vector3.zero);
            foreach (Vector3 vertex in vertices) bounds.Encapsulate(vertex);
            return bounds;
        }

        private static Vector3[][] CaptureTextVertices(UnauthorizedAccessEffect effect)
        {
            var vertices = new Vector3[TextMeshNames.Length][];
            for (int i = 0; i < TextMeshNames.Length; i++)
                vertices[i] = FindMesh(effect, TextMeshNames[i]).vertices;
            return vertices;
        }

        private static void AssertTextVerticesEqual(Vector3[][] expected, Vector3[][] actual)
        {
            Assert.AreEqual(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++)
                CollectionAssert.AreEqual(expected[i], actual[i], TextMeshNames[i]);
        }

        private static void AssertTakeoverTextDoesNotOverlap(UnauthorizedAccessEffect effect)
        {
            Bounds first = MeshBounds(effect, TextMeshNames[0]);
            Assert.LessOrEqual(first.max.y, .40f * ReferenceSize.y + .0001f,
                "上端の日時表示に余白を残す");
            Assert.AreEqual(0f, first.center.x, .0001f);
            Bounds previous = first;
            for (int i = 1; i < TextMeshNames.Length; i++)
            {
                Bounds current = MeshBounds(effect, TextMeshNames[i]);
                Assert.Greater(previous.min.y, current.max.y,
                    $"{TextMeshNames[i - 1]} and {TextMeshNames[i]} overlap");
                Assert.AreEqual(0f, current.center.x, .0001f, TextMeshNames[i]);
                previous = current;
            }
            Assert.GreaterOrEqual(previous.min.y, -.05f * ReferenceSize.y - .0001f,
                "通信面との間に余白を残す");
        }

        private static Color[][] CaptureColors(UnauthorizedAccessEffect effect)
        {
            var colors = new Color[MeshNames.Length][];
            for (int i = 0; i < MeshNames.Length; i++) colors[i] = FindMesh(effect, MeshNames[i]).colors;
            return colors;
        }

        private static void AssertColorsEqual(Color[][] expected, Color[][] actual)
        {
            Assert.AreEqual(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++)
                CollectionAssert.AreEqual(expected[i], actual[i], MeshNames[i]);
        }

        private static bool VerticesDiffer(Vector3[] first, Vector3[] second)
        {
            if (first.Length != second.Length) return true;
            for (int i = 0; i < first.Length; i++)
                if (first[i] != second[i]) return true;
            return false;
        }

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
