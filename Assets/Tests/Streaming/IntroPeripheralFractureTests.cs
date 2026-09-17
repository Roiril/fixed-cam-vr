#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace FixedCamVr.Streaming.Tests
{
    public sealed class IntroPeripheralFractureTests
    {
        [Test]
        public void Mesh_IsClosedFiniteCoarseAndDeterministic()
        {
            Mesh first = IntroPeripheralFractureMesh.Build();
            Mesh second = IntroPeripheralFractureMesh.Build();
            try
            {
                Assert.That(IntroPeripheralFractureMesh.LastPieceCount, Is.EqualTo(60));
                Assert.That(IntroPeripheralFractureMesh.LastTrianglePieceCount, Is.EqualTo(60));
                Assert.That(IntroPeripheralFractureMesh.LastQuadPieceCount, Is.Zero,
                    "周囲に四角い板として読める片を戻さない");
                Assert.That(IntroPeripheralFractureMesh.LastTrianglePieceCount
                            + IntroPeripheralFractureMesh.LastQuadPieceCount,
                    Is.EqualTo(IntroPeripheralFractureMesh.LastPieceCount));

                Vector3[] vertices = first.vertices;
                int[] indices = first.triangles;
                Assert.That(vertices.Length, Is.EqualTo(900));
                Assert.That(indices.Length / 3, Is.EqualTo(960));
                float minimumRadius = float.PositiveInfinity;
                float maximumRadius = 0f;
                for (int i = 0; i < vertices.Length; i++)
                {
                    Assert.That(IsFinite(vertices[i]), Is.True, $"頂点 {i} が有限値でない");
                    float radius = vertices[i].magnitude;
                    minimumRadius = Mathf.Min(minimumRadius, radius);
                    maximumRadius = Mathf.Max(maximumRadius, radius);
                    Assert.That(radius, Is.GreaterThan(0f));
                    Assert.That(radius, Is.LessThanOrEqualTo(IntroPeripheralFractureMesh.Radius + 1e-4f));
                }
                Assert.That(minimumRadius, Is.LessThan(IntroPeripheralFractureMesh.Radius - 1e-3f),
                    "平面内の補間頂点が追加されていない");
                Assert.That(maximumRadius,
                    Is.EqualTo(IntroPeripheralFractureMesh.Radius).Within(1e-4f), "元の外形頂点を失った");

                var edgeCounts = new Dictionary<Edge, int>();
                for (int i = 0; i < indices.Length; i += 3)
                {
                    CountEdge(edgeCounts, vertices[indices[i]], vertices[indices[i + 1]]);
                    CountEdge(edgeCounts, vertices[indices[i + 1]], vertices[indices[i + 2]]);
                    CountEdge(edgeCounts, vertices[indices[i + 2]], vertices[indices[i]]);
                }
                foreach (KeyValuePair<Edge, int> edge in edgeCounts)
                    Assert.That(edge.Value, Is.EqualTo(2), $"球殻に開いた辺がある: {edge.Key}");

                Assert.That(second.vertices, Is.EqualTo(first.vertices));
                Assert.That(second.triangles, Is.EqualTo(first.triangles));
                var firstPieces = new List<Vector4>();
                var secondPieces = new List<Vector4>();
                var firstTiming = new List<Vector4>();
                var firstEdges = new List<Vector4>();
                first.GetUVs(1, firstPieces);
                second.GetUVs(1, secondPieces);
                first.GetUVs(2, firstTiming);
                first.GetUVs(3, firstEdges);
                Assert.That(secondPieces, Is.EqualTo(firstPieces));
                for (int piece = 0; piece < IntroPeripheralFractureMesh.LastPieceCount; piece++)
                {
                    int firstVertex = piece * 15;
                    bool hasInteriorVertex = false;
                    for (int vertex = firstVertex; vertex < firstVertex + 15; vertex++)
                    {
                        Assert.That(firstPieces[vertex], Is.EqualTo(firstPieces[firstVertex]));
                        Assert.That(firstTiming[vertex], Is.EqualTo(firstTiming[firstVertex]));
                        Vector4 edge = firstEdges[vertex];
                        if (Mathf.Min(edge.x, edge.y, edge.z) > 1e-4f) hasInteriorVertex = true;
                    }
                    Assert.That(hasInteriorVertex, Is.True,
                        $"片 {piece} の内部格子が外周の亀裂として誤判定される");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void IntroVeil_PeripheralRenderersRequireFrozenFrameAndResumeAfterHidden()
        {
            var root = new GameObject("IntroPeripheralFractureTest");
            var left = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            var right = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            try
            {
                var veil = root.AddComponent<IntroVeil>();
                InvokeAwake(veil);
                veil.FrozenFrameProvider = () => new IntroFrozenFrameSource(
                    left, right, Matrix4x4.identity, Matrix4x4.identity);

                Assert.That(veil.PeripheralPieces, Is.InRange(48, 72));
                Assert.That(veil.PeripheralDrawn, Is.False);
                veil.Apply(new IntroWeights { passthrough = 1f, shatter = 0.02f, ignite = 1f });
                Assert.That(veil.HasFrozenFrame, Is.True);
                Assert.That(veil.PeripheralDrawn, Is.True);

                MeshRenderer window = Renderer(veil, "_peripheralWindowRenderer");
                MeshRenderer color = Renderer(veil, "_peripheralColorRenderer");
                Assert.That(window.sharedMaterial.shader.name,
                    Is.EqualTo("FixedCamVr/IntroPeripheralFracture"));
                Assert.That(window.sharedMaterial.renderQueue, Is.EqualTo(4904));
                Assert.That(window.sharedMaterial.GetInt("_ZTest"), Is.EqualTo((int)CompareFunction.Always));
                Assert.That(color.sharedMaterial.renderQueue, Is.EqualTo(4906));
                Assert.That(color.sharedMaterial.GetInt("_ColorMask"), Is.EqualTo(15));
                Assert.That(color.sharedMaterial.GetInt("_ZWrite"), Is.EqualTo(1));
                Assert.That(color.sharedMaterial.GetInt("_ZTest"), Is.EqualTo((int)CompareFunction.LessEqual));
                Assert.That(color.sharedMaterial.GetInt("_SrcBlend"), Is.EqualTo((int)BlendMode.SrcAlpha));
                Assert.That(color.sharedMaterial.GetInt("_DstBlend"),
                    Is.EqualTo((int)BlendMode.OneMinusSrcAlpha));
                Assert.That(color.sharedMaterial.GetInt("_SrcBlendAlpha"), Is.EqualTo((int)BlendMode.Zero));
                Assert.That(color.sharedMaterial.GetInt("_DstBlendAlpha"), Is.EqualTo((int)BlendMode.One));
                Assert.That(root.transform.Find("IntroPeripheralDepth"), Is.Null,
                    "周辺破片が depth/color の2回描画へ戻っている");

                veil.Apply(new IntroWeights { passthrough = 1f, shatter = 0.30f, ignite = 1f });
                Assert.That(window.enabled, Is.False, "引継ぎ後も全周 alpha 窓を描き続けている");
                Assert.That(color.enabled, Is.True);
                Assert.That(veil.PeripheralDrawn, Is.True, "中盤の実体片を描画中として観測できない");

                veil.Apply(new IntroWeights { passthrough = 1f, shatter = 0.86f, ignite = 1f });
                Assert.That(window.enabled, Is.False);
                Assert.That(color.enabled, Is.False);
                Assert.That(veil.PeripheralDrawn, Is.False, "退場後も周辺 renderer が残っている");

                AssertCentralContract(veil);
                veil.SetHidden();
                Assert.That(veil.PeripheralDrawn, Is.False);
                veil.Apply(new IntroWeights { passthrough = 1f, shatter = 0f, frame = 0f });
                veil.Apply(new IntroWeights { passthrough = 1f, shatter = 0.02f, ignite = 1f });
                Assert.That(veil.PeripheralDrawn, Is.True, "次の走行で周辺 renderer が再開しない");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(left);
                UnityEngine.Object.DestroyImmediate(right);
            }
        }

        [Test]
        public void IntroVeil_MissingFrozenFrameKeepsPeripheralOffAndCentralFallbackOn()
        {
            var root = new GameObject("IntroPeripheralFallbackTest");
            try
            {
                var veil = root.AddComponent<IntroVeil>();
                InvokeAwake(veil);
                veil.FrozenFrameProvider = () => null;
                veil.Apply(new IntroWeights { passthrough = 1f, shatter = 0.14f, ignite = 1f });
                Assert.That(veil.HasFrozenFrame, Is.False);
                Assert.That(veil.PeripheralDrawn, Is.False);
                Assert.That(veil.ShatterDrawn, Is.True, "中央の既存 fallback まで止めた");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void IntroVeil_DiagnosticSuppressionKeepsCentralFrozenFractureRunning()
        {
            var root = new GameObject("IntroPeripheralSuppressionTest");
            var left = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            var right = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            try
            {
                var veil = root.AddComponent<IntroVeil>();
                InvokeAwake(veil);
                typeof(IntroVeil).GetField("_suppressPeripheralForDiagnostics",
                    BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(veil, true);
                veil.FrozenFrameProvider = () => new IntroFrozenFrameSource(
                    left, right, Matrix4x4.identity, Matrix4x4.identity);

                veil.Apply(new IntroWeights { passthrough = 1f, shatter = 0.30f, ignite = 1f });

                Assert.That(veil.PeripheralDrawn, Is.False);
                Assert.That(veil.FrozenFrameAttempted, Is.True);
                Assert.That(veil.HasFrozenFrame, Is.True);
                Assert.That(veil.FrozenFrameCount, Is.EqualTo(1));
                Assert.That(veil.ShatterDrawn, Is.True, "診断スイッチが中央破片まで止めた");
                Assert.That(Renderer(veil, "_fractureDepthRenderer").enabled, Is.True,
                    "診断スイッチが中央の静止画 depth を止めた");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(left);
                UnityEngine.Object.DestroyImmediate(right);
            }
        }

        private static void AssertCentralContract(IntroVeil veil)
        {
            var baseMaterial = (Material)typeof(IntroVeil)
                .GetField("_mat", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(veil)!;
            MeshRenderer depth = Renderer(veil, "_fractureDepthRenderer");
            MeshRenderer color = Renderer(veil, "_fractureRenderer");
            var gaps = (Material)typeof(IntroVeil)
                .GetField("_gapsMat", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(veil)!;
            Assert.That(baseMaterial.renderQueue, Is.EqualTo(4900));
            Assert.That(depth.sharedMaterial.renderQueue, Is.EqualTo(4901));
            Assert.That(gaps.renderQueue, Is.EqualTo(4902));
            Assert.That(color.sharedMaterial.renderQueue, Is.EqualTo(4903));
        }

        private static void InvokeAwake(IntroVeil veil) => typeof(IntroVeil)
            .GetMethod("Awake", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(veil, null);

        private static MeshRenderer Renderer(IntroVeil veil, string field) =>
            (MeshRenderer)typeof(IntroVeil).GetField(field,
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(veil)!;

        private static bool IsFinite(Vector3 value) =>
            !float.IsNaN(value.x) && !float.IsInfinity(value.x)
            && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
            && !float.IsNaN(value.z) && !float.IsInfinity(value.z);

        private static void CountEdge(Dictionary<Edge, int> counts, Vector3 a, Vector3 b)
        {
            var edge = new Edge(a, b);
            counts.TryGetValue(edge, out int count);
            counts[edge] = count + 1;
        }

        private readonly struct Edge : IEquatable<Edge>
        {
            private readonly Vector3Int _a;
            private readonly Vector3Int _b;

            public Edge(Vector3 a, Vector3 b)
            {
                Vector3Int qa = Quantize(a);
                Vector3Int qb = Quantize(b);
                if (Compare(qa, qb) <= 0) { _a = qa; _b = qb; }
                else { _a = qb; _b = qa; }
            }

            public bool Equals(Edge other) => _a == other._a && _b == other._b;
            public override bool Equals(object? obj) => obj is Edge other && Equals(other);
            public override int GetHashCode() => (_a.GetHashCode() * 397) ^ _b.GetHashCode();
            public override string ToString() => $"{_a} -> {_b}";

            private static Vector3Int Quantize(Vector3 value) => new(
                Mathf.RoundToInt(value.x * 100000f),
                Mathf.RoundToInt(value.y * 100000f),
                Mathf.RoundToInt(value.z * 100000f));

            private static int Compare(Vector3Int a, Vector3Int b)
            {
                if (a.x != b.x) return a.x.CompareTo(b.x);
                if (a.y != b.y) return a.y.CompareTo(b.y);
                return a.z.CompareTo(b.z);
            }
        }
    }
}
