#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// ブラウン管の曲面（<see cref="CrtScreenMesh"/>）。
    /// **4 隅が動かないこと**が一番大事 — スクリーンの隅は <c>IntroVeil</c> の開口計算
    /// （眼と 4 辺を通る平面）と <c>MjpegScreen.ScreenAspect</c> の基準なので、
    /// 動かすとそちらが黙ってずれる。
    /// </summary>
    public sealed class CrtScreenMeshTests
    {
        [Test]
        public void 四隅と辺は動かない()
        {
            Mesh m = CrtScreenMesh.BuildMesh(8, 0.05f);
            Vector3[] v = m.vertices;
            foreach (Vector3 p in v)
            {
                bool onEdge = Mathf.Abs(Mathf.Abs(p.x) - 0.5f) < 1e-4f
                           || Mathf.Abs(Mathf.Abs(p.y) - 0.5f) < 1e-4f;
                if (onEdge)
                    Assert.That(p.z, Is.EqualTo(0f).Within(1e-5f),
                        "枠の辺の上の頂点が動いている（開口とアスペクトの基準がずれる）");
            }
            Object.DestroyImmediate(m);
        }

        [Test]
        public void 中央が体験者側へ膨らむ()
        {
            const float bulge = 0.05f;
            Mesh m = CrtScreenMesh.BuildMesh(8, bulge);
            Vector3[] v = m.vertices;
            float minZ = float.MaxValue;
            foreach (Vector3 p in v) minZ = Mathf.Min(minZ, p.z);
            // Unity の Quad は法線 -Z。体験者はそちら側に居るので、膨らみも -Z へ。
            Assert.That(minZ, Is.EqualTo(-bulge).Within(1e-5f));
            Object.DestroyImmediate(m);
        }

        [Test]
        public void 法線は体験者の側を向く()
        {
            Mesh m = CrtScreenMesh.BuildMesh(8, 0.05f);
            Vector3[] n = m.normals;
            // 巻き順を間違えると面が裏返り、**実機で真っ黒になる**（背面カリング）。
            Assert.That(n.Length, Is.GreaterThan(0));
            float sum = 0f;
            foreach (Vector3 v in n) sum += v.z;
            Assert.That(sum / n.Length, Is.LessThan(-0.5f), "法線が -Z を向いていない（面が裏返っている）");
            Object.DestroyImmediate(m);
        }

        [Test]
        public void 膨らみ0なら平らな面になる()
        {
            Mesh m = CrtScreenMesh.BuildMesh(6, 0f);
            foreach (Vector3 p in m.vertices) Assert.That(p.z, Is.EqualTo(0f).Within(1e-6f));
            Object.DestroyImmediate(m);
        }

        [Test]
        public void UVは枠いっぱいに張る()
        {
            Mesh m = CrtScreenMesh.BuildMesh(4, 0.05f);
            Vector2[] uv = m.uv;
            float minU = 1f, maxU = 0f, minV = 1f, maxV = 0f;
            foreach (Vector2 t in uv)
            {
                minU = Mathf.Min(minU, t.x); maxU = Mathf.Max(maxU, t.x);
                minV = Mathf.Min(minV, t.y); maxV = Mathf.Max(maxV, t.y);
            }
            Assert.That(minU, Is.EqualTo(0f).Within(1e-6f));
            Assert.That(maxU, Is.EqualTo(1f).Within(1e-6f));
            Assert.That(minV, Is.EqualTo(0f).Within(1e-6f));
            Assert.That(maxV, Is.EqualTo(1f).Within(1e-6f));
            Object.DestroyImmediate(m);
        }
    }
}
