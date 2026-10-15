#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>R061: 段 4 の破片を撮影時の部屋の奥行きへ置く（<see cref="IntroRoomDepth"/>）。</summary>
    public sealed class IntroRoomDepthTests
    {
        private static IntroRoomDepth.Box Wall(Vector3 center, Vector3 half) => new IntroRoomDepth.Box
        {
            center = center,
            axisX = Vector3.right,
            axisY = Vector3.up,
            axisZ = Vector3.forward,
            half = half,
        };

        private static Vector3 Dir(float degX, float degY)
            => (Quaternion.Euler(-degY, degX, 0f) * Vector3.forward).normalized;

        [Test]
        public void Raycast_HitsTheFaceTowardTheHead()
        {
            var scene = new IntroRoomDepth.Scene();
            scene.boxes.Add(Wall(new Vector3(0f, 0f, 1.02f), new Vector3(2f, 2f, 0.02f)));
            IntroRoomDepth.Hit hit = IntroRoomDepth.Raycast(scene, Vector3.zero, Vector3.forward);
            Assert.That(hit.surface, Is.Not.EqualTo(IntroRoomDepth.FarSurface));
            Assert.That(hit.distance, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(Vector3.Dot(hit.normal, Vector3.back), Is.EqualTo(1f).Within(1e-5f), "面の法線は頭の側を向く");
        }

        [Test]
        public void FlatWall_PutsEveryVertexOnThePlane()
        {
            var scene = new IntroRoomDepth.Scene();
            scene.boxes.Add(Wall(new Vector3(0f, 0f, 1.02f), new Vector3(3f, 3f, 0.02f)));
            var dirs = new List<Vector3> { Dir(-10f, 0f), Dir(10f, 5f), Dir(0f, -12f) };
            float[] radii = IntroRoomDepth.SolvePiece(scene, Vector3.zero, Vector3.forward, dirs, out float center);
            Assert.That(center, Is.EqualTo(1f).Within(1e-4f));
            for (int i = 0; i < dirs.Count; i++)
                Assert.That(radii[i] * dirs[i].z, Is.EqualTo(1f).Within(1e-4f), $"頂点 {i} が壁の面から外れた");
        }

        [Test]
        public void NearWall_IsClampedToTheMinimumRadius()
        {
            var scene = new IntroRoomDepth.Scene();
            scene.boxes.Add(Wall(new Vector3(0f, 0f, 0.44f), new Vector3(3f, 3f, 0.02f)));
            float[] radii = IntroRoomDepth.SolvePiece(scene, Vector3.zero, Vector3.forward,
                new List<Vector3> { Dir(-8f, 0f), Dir(8f, 0f) }, out float center);
            Assert.That(center, Is.EqualTo(IntroRoomDepth.MinRadiusM).Within(1e-5f));
            foreach (float r in radii) Assert.That(r, Is.GreaterThanOrEqualTo(IntroRoomDepth.MinRadiusM - 1e-5f));
        }

        [Test]
        public void NothingHit_UsesTheFarSphere()
        {
            var scene = new IntroRoomDepth.Scene { hasFloor = false };
            float[] radii = IntroRoomDepth.SolvePiece(scene, Vector3.zero, Vector3.forward,
                new List<Vector3> { Dir(20f, 0f) }, out float center);
            Assert.That(center, Is.EqualTo(IntroRoomDepth.FarRadiusM));
            Assert.That(radii[0], Is.EqualTo(IntroRoomDepth.FarRadiusM));
        }

        [Test]
        public void LookingDown_HitsTheFloor()
        {
            var scene = new IntroRoomDepth.Scene { hasFloor = true, floorPoint = new Vector3(0f, -1.55f, 0f) };
            Vector3 down45 = new Vector3(0f, -1f, 1f).normalized;
            IntroRoomDepth.Hit hit = IntroRoomDepth.Raycast(scene, Vector3.zero, down45);
            Assert.That(hit.surface, Is.EqualTo(IntroRoomDepth.FloorSurface));
            Assert.That(hit.distance, Is.EqualTo(1.55f * Mathf.Sqrt(2f)).Within(1e-4f));
        }

        [Test]
        public void PieceAcrossAWallEdge_StaysOnOnePlane_AndDoesNotStretch()
        {
            // 壁は x < 0 だけ（z = 0.8）。その先は何も無い（4m の球）。
            var scene = new IntroRoomDepth.Scene { hasFloor = false };
            scene.boxes.Add(Wall(new Vector3(-1.5f, 0f, 0.82f), new Vector3(1.5f, 3f, 0.02f)));
            Vector3 center = Dir(-3f, 0f);
            var dirs = new List<Vector3> { Dir(-12f, 4f), Dir(-12f, -4f), Dir(6f, 0f) };
            float[] radii = IntroRoomDepth.SolvePiece(scene, Vector3.zero, center, dirs, out float rc);
            Assert.That(rc * center.z, Is.EqualTo(0.8f).Within(1e-3f), "中心は壁に乗る（多数決）");
            for (int i = 0; i < radii.Length; i++)
            {
                Assert.That(radii[i], Is.LessThanOrEqualTo(rc * IntroRoomDepth.MaxStretch + 1e-4f), $"頂点 {i} が伸びた");
                Assert.That(radii[i], Is.GreaterThanOrEqualTo(rc / IntroRoomDepth.MaxStretch - 1e-4f));
            }
            Assert.That(radii[2] * dirs[2].z, Is.EqualTo(0.8f).Within(1e-3f),
                "縁の向こうへ出た頂点は 4m ではなく壁の平面の延長に乗る");
        }

        [Test]
        public void Build_UsesTheRoomProxyWalls()
        {
            var room = new ShowRoomDef
            {
                floorY = 0f,
                floorW = 1.8f,
                floorD = 1.8f,
                walls = new[] { new ShowRoomWallDef { x1 = -0.5f, z1 = 0.5f, x2 = 0.455f, z2 = 0.5f, h = 1.8f, thick = 0.04f } },
            };
            IntroRoomDepth.Scene scene = IntroRoomDepth.Build(room,
                (xz, h) => new Vector3(xz.x, h, xz.y), 0f);
            Assert.That(scene.boxes.Count, Is.EqualTo(1));
            IntroRoomDepth.Hit wall = IntroRoomDepth.Raycast(scene, new Vector3(0f, 1.5f, 0f), Vector3.forward);
            Assert.That(wall.distance, Is.EqualTo(0.48f).Within(1e-4f), "壁の手前の面（厚み 4cm の半分）");
            IntroRoomDepth.Hit over = IntroRoomDepth.Raycast(scene, new Vector3(0f, 1.5f, 0f),
                new Vector3(0f, 1f, 1f).normalized);
            Assert.That(over.surface, Is.EqualTo(IntroRoomDepth.FarSurface), "壁の上を越えた視線は遠方");
            IntroRoomDepth.Hit floor = IntroRoomDepth.Raycast(scene, new Vector3(0f, 1.5f, 0f), Vector3.down);
            Assert.That(floor.distance, Is.EqualTo(1.5f).Within(1e-4f));
        }

        [Test]
        public void ApplyFracture_WithoutARoom_WritesOnes_AndUniformRoomScalesEveryPiece()
        {
            Mesh fracture = IntroFractureMesh.Build();
            Mesh peripheral = IntroPeripheralFractureMesh.Build();
            Mesh sparks = IntroSparkMesh.Build(IntroFractureMesh.LastPieces);
            try
            {
                IntroRoomDepth.Result off = IntroRoomDepth.ApplyFracture(fracture, Matrix4x4.identity, null);
                Assert.That(off.applied, Is.False);
                Assert.That(off.pieces, Is.EqualTo(IntroFractureMesh.LastPieceCount), "片の区切りがメッシュの片と一致する");
                AssertAll(fracture, IntroRoomDepth.FractureChannel, 1f, 1f);

                var uniform = new IntroRoomDepth.Scene { hasFloor = false, farRadius = 1.0f };
                Matrix4x4 pose = Matrix4x4.TRS(new Vector3(0.3f, 1.5f, -0.2f), Quaternion.Euler(10f, 35f, 0f), Vector3.one);
                IntroRoomDepth.Result on = IntroRoomDepth.ApplyFracture(fracture, pose, uniform);
                IntroRoomDepth.ApplyPeripheral(peripheral, pose, uniform);
                IntroRoomDepth.ApplySparks(sparks, on);
                Assert.That(on.applied, Is.True);
                Assert.That(on.medianRadius, Is.EqualTo(1f).Within(1e-5f));
                Assert.That(on.heroScale, Is.EqualTo(1f / 1.6f).Within(1e-5f));
                AssertAll(fracture, IntroRoomDepth.FractureChannel, 1f / 1.6f, 1f / 1.6f);
                AssertAll(sparks, IntroRoomDepth.SparkChannel, 1f / 1.6f, 0f);

                // 周辺の片は平らな三角形なので、頂点の名目の半径は 1.6 より短い。倍率は半径 1m に戻す値。
                Vector3[] vertices = peripheral.vertices;
                var scales = new List<Vector2>();
                peripheral.GetUVs(IntroRoomDepth.PeripheralChannel, scales);
                Assert.That(scales.Count, Is.EqualTo(vertices.Length));
                for (int i = 0; i < vertices.Length; i++)
                    Assert.That(vertices[i].magnitude * scales[i].x, Is.EqualTo(1f).Within(1e-4f), $"周辺の頂点 {i}");

                IntroRoomDepth.Reset(fracture, peripheral, sparks);
                AssertAll(fracture, IntroRoomDepth.FractureChannel, 1f, 1f);
                AssertAll(sparks, IntroRoomDepth.SparkChannel, 1f, 0f);
            }
            finally
            {
                Object.DestroyImmediate(fracture);
                Object.DestroyImmediate(peripheral);
                Object.DestroyImmediate(sparks);
            }
        }

        [Test]
        public void ShellRadius_MatchesTheShaderAndThePeripheralMesh()
        {
            Assert.That(IntroPeripheralFractureMesh.Radius, Is.EqualTo(IntroRoomDepth.ShellRadius));
            string hlsl = File.ReadAllText(Path.Combine(Application.dataPath, "Art/Shaders/Intro/IntroFractureTime.hlsl"));
            Match m = Regex.Match(hlsl, @"FractureShellPoint\(float2 local\)\s*\{\s*return normalize\(float3\(local / 0\.15, 1\.0\)\) \* ([0-9.]+);");
            Assert.That(m.Success, Is.True, "FractureShellPoint の式が変わった — IntroRoomDepth.ShellPoint と対で直す");
            Assert.That(float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(IntroRoomDepth.ShellRadius));
        }

        private static void AssertAll(Mesh mesh, int channel, float x, float y)
        {
            var values = new List<Vector2>();
            mesh.GetUVs(channel, values);
            Assert.That(values.Count, Is.EqualTo(mesh.vertexCount));
            for (int i = 0; i < values.Count; i++)
            {
                Assert.That(values[i].x, Is.EqualTo(x).Within(1e-5f), $"頂点 {i} の x");
                Assert.That(values[i].y, Is.EqualTo(y).Within(1e-5f), $"頂点 {i} の y");
            }
        }
    }
}
