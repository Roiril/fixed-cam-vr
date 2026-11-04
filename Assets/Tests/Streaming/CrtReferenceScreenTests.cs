#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    public sealed class CrtReferenceScreenTests
    {
        [Test]
        public void 開口の左右端は黒帯の内側へ一致する()
        {
            Rect r = CrtReferenceScreen.Aperture;
            Vector2 left = CrtReferenceScreen.ToLogicalUv(new Vector2(r.xMin, r.center.y));
            Vector2 right = CrtReferenceScreen.ToLogicalUv(new Vector2(r.xMax, r.center.y));
            Assert.That(left.x, Is.EqualTo(0.125f).Within(1e-6));
            Assert.That(right.x, Is.EqualTo(0.875f).Within(1e-6));
            Assert.That(left.y, Is.EqualTo(0.5f).Within(1e-6));
            Assert.That(right.y, Is.EqualTo(0.5f).Within(1e-6));
        }

        [Test]
        public void 導入の到着矩形で黒帯を二重に削らない()
        {
            Vector2 size = CrtReferenceScreen.VisibleScale(new Vector2(0.75f, 1));
            Assert.That(size.x, Is.EqualTo(CrtReferenceScreen.Aperture.width).Within(1e-6));
            Assert.That(size.y, Is.EqualTo(CrtReferenceScreen.Aperture.height).Within(1e-6));
        }

        [Test]
        public void 開口の中心は頭ではなくスクリーンの変換で決まる()
        {
            var go = new GameObject("ReferenceCenterTest");
            try
            {
                go.transform.SetPositionAndRotation(new Vector3(4, 2, 7), Quaternion.Euler(10, 80, 0));
                go.transform.localScale = new Vector3(CrtReferenceScreen.CaseAspect * 2, 2, 1);
                Vector3 world = CrtReferenceScreen.ApertureCenter(go.transform);
                Vector3 local = go.transform.InverseTransformPoint(world);
                Assert.That(local.x, Is.EqualTo(CrtReferenceScreen.Aperture.center.x - 0.5f).Within(1e-5));
                Assert.That(local.y, Is.EqualTo(CrtReferenceScreen.Aperture.center.y - 0.5f).Within(1e-5));
                Assert.That(local.z, Is.EqualTo(0).Within(1e-5));
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void 本番の画面は論理16対9と物理参照比率を分ける()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.SetActive(false);
            var original = new Material(Shader.Find("FixedCamVr/ScreenComposite"));
            Material? instance = null;
            go.GetComponent<Renderer>().sharedMaterial = original;
            try
            {
                go.transform.localScale = new Vector3(2.3704f, 1.3333f, 1);
                go.AddComponent<CrtScreenMesh>();
                var screen = go.AddComponent<MjpegScreen>();
                screen.EnableReferenceSurface();
                Assert.That(screen.ReferenceSurfaceActive, Is.True);
                Assert.That(screen.ScreenAspect, Is.EqualTo(16f / 9f).Within(1e-5));
                Assert.That(go.transform.localScale.x / go.transform.localScale.y,
                    Is.EqualTo(1280f / 1030f).Within(1e-5));
                float width = go.transform.localScale.x;
                screen.EnableReferenceSurface();
                Assert.That(go.transform.localScale.x, Is.EqualTo(width).Within(1e-6), "再適用で縮み続けない");
                var mat = go.GetComponent<Renderer>().sharedMaterial;
                instance = mat;
                Assert.That(mat.GetTexture("_CrtFrameTex"), Is.Not.Null);
                Assert.That(mat.GetTexture("_CrtCaseColorTex"), Is.Not.Null);
                Assert.That(mat.GetTexture("_CrtGlassTex"), Is.Not.Null);
                Assert.That(mat.GetTexture("_CrtNormalTex"), Is.Not.Null);
                Assert.That(mat.GetTexture("_CrtGradeTex"), Is.Not.Null);
                Assert.That(mat.GetTexture("_CrtReflectanceTex"), Is.Not.Null);
            }
            finally
            {
                Object.DestroyImmediate(go);
                if (instance != null) Object.DestroyImmediate(instance);
                Object.DestroyImmediate(original);
            }
        }
    }
}
