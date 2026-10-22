#nullable enable
using FixedCamVr.Streaming.Cg;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 人形について回る光だまり（canon/LEDGER.md 0287）の位置と大きさを固定する。
    ///
    /// 人形に変わった後は夜間モードの明暗曲線が黒を潰すので、黒いカーテンの前の黒髪が背景ごと消えていた。
    /// 光だまりは<b>頭のあたり</b>を中心に、人形の見かけの大きさに比例して置く。中心が胴へ下がると
    /// 頭の輪郭の後ろが暗いまま残り、半径が背丈に比例しないと遠い人形・近い人形で効き方が変わる。
    /// </summary>
    public sealed class ActorLightTests
    {
        private GameObject? _go;
        private Camera _cam = null!;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("actor-light-probe-cam");
            _cam = _go.AddComponent<Camera>();
            _cam.fieldOfView = 60f;
            _cam.aspect = 4f / 3f;
            _go.transform.position = new Vector3(0f, 0.2f, -2f);
            _go.transform.rotation = Quaternion.identity;
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
        }

        [Test]
        public void Center_SitsAtHeadHeight_NotMidBody()
        {
            Assert.IsTrue(ShowCgLayer.TryActorLight(_cam, Vector3.zero, 0.4f, Vector2.one, 1.5f,
                                                    out Vector4 light));
            Assert.IsTrue(ShowCgLayer.TryFrameUv(_cam, Vector3.up * 0.2f, Vector2.one, out Vector2 mid));
            Assert.Greater(light.y, mid.y, "中心は人形の真ん中より上（頭のあたり）");
            Assert.IsTrue(ShowCgLayer.TryFrameUv(_cam, Vector3.up * (0.4f * ShowCgLayer.ActorLightCenterK),
                                                 Vector2.one, out Vector2 head));
            Assert.AreEqual(head.x, light.x, 1e-4f);
            Assert.AreEqual(head.y, light.y, 1e-4f);
        }

        [Test]
        public void Radius_IsApparentHalfHeight_AndShrinksWithDistance()
        {
            Assert.IsTrue(ShowCgLayer.TryActorLight(_cam, Vector3.zero, 0.4f, Vector2.one, 1.5f,
                                                    out Vector4 near));
            Assert.IsTrue(ShowCgLayer.TryActorLight(_cam, new Vector3(0f, 0f, 2f), 0.4f, Vector2.one, 1.5f,
                                                    out Vector4 far));
            Assert.IsTrue(ShowCgLayer.TryActorFocus(_cam, Vector3.zero, 0.4f, Vector2.one, 1f,
                                                    out Vector4 focus));
            Assert.AreEqual(focus.z, near.z, 1e-4f, "半径の測り方は荒れ（_ActorFocus）と同じ");
            Assert.Less(far.z, near.z, "遠い人形ほど光だまりも小さい");
        }

        [Test]
        public void Strength_IsPassedThrough_AndNeverNegative()
        {
            Assert.IsTrue(ShowCgLayer.TryActorLight(_cam, Vector3.zero, 0.4f, Vector2.one, 1.5f, out Vector4 a));
            Assert.AreEqual(1.5f, a.w, 1e-6f);
            Assert.IsTrue(ShowCgLayer.TryActorLight(_cam, Vector3.zero, 0.4f, Vector2.one, -1f, out Vector4 b));
            Assert.AreEqual(0f, b.w, 1e-6f);
        }

        [Test]
        public void BehindCamera_WritesNothing()
        {
            Assert.IsFalse(ShowCgLayer.TryActorLight(_cam, new Vector3(0f, 0f, -4f), 0.4f, Vector2.one, 1.5f,
                                                     out Vector4 light));
            Assert.AreEqual(0f, light.w, "映っていない人形の光だまりは強さ 0（何も無い所を明るくしない）");
        }

        [Test]
        public void ShippedStrength_IsPositive()
        {
            // 0 にすると人形の頭が夜間モードの黒に溶ける状態へ黙って戻る。
            Assert.Greater(ShowCgLayer.ActorLightEv, 0f);
        }
    }
}
