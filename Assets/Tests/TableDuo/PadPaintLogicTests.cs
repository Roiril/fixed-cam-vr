#nullable enable
using System.Collections.Generic;
using NUnit.Framework;
using TableDuoVr.Net;
using UnityEngine;

namespace TableDuoVr.Tests
{
    /// <summary>
    /// PadPaintLogic（描画パッド接触・UV 変換・間引き・バッファ上限）の EditMode テスト。
    /// パッド外形 210×297mm・上面ローカル y=0.0045。純関数のみ。
    /// </summary>
    public class PadPaintLogicTests
    {
        private static float SurfY => PadPaintLogic.SurfaceLocalY;

        [Test]
        public void TryGetContactUv_Corners_MapToUnitSquareCorners()
        {
            Assert.IsTrue(PadPaintLogic.TryGetContactUv(
                new Vector3(-PadPaintLogic.HalfX, SurfY, -PadPaintLogic.HalfZ), out var uv00));
            Assert.That(uv00.x, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(uv00.y, Is.EqualTo(0f).Within(1e-4f));

            Assert.IsTrue(PadPaintLogic.TryGetContactUv(
                new Vector3(PadPaintLogic.HalfX, SurfY, PadPaintLogic.HalfZ), out var uv11));
            Assert.That(uv11.x, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(uv11.y, Is.EqualTo(1f).Within(1e-4f));

            Assert.IsTrue(PadPaintLogic.TryGetContactUv(new Vector3(0f, SurfY, 0f), out var uvC));
            Assert.That(uvC.x, Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(uvC.y, Is.EqualTo(0.5f).Within(1e-4f));
        }

        [Test]
        public void TryGetContactUv_OutsideXZ_ReturnsFalse()
        {
            Assert.IsFalse(PadPaintLogic.TryGetContactUv(
                new Vector3(PadPaintLogic.HalfX + 0.001f, SurfY, 0f), out _), "X 範囲外");
            Assert.IsFalse(PadPaintLogic.TryGetContactUv(
                new Vector3(0f, SurfY, PadPaintLogic.HalfZ + 0.001f), out _), "Z 範囲外");
        }

        [Test]
        public void TryGetContactUv_YToleranceBoundaries()
        {
            // 既定 yTolBelow=0.004 / yTolAbove=0.002
            Assert.IsTrue(PadPaintLogic.TryGetContactUv(new Vector3(0f, SurfY - 0.004f, 0f), out _),
                "下許容ちょうど（面に押し込む側）は接触");
            Assert.IsFalse(PadPaintLogic.TryGetContactUv(new Vector3(0f, SurfY - 0.005f, 0f), out _),
                "下許容超過は非接触");
            Assert.IsTrue(PadPaintLogic.TryGetContactUv(new Vector3(0f, SurfY + 0.002f, 0f), out _),
                "上許容ちょうどは接触");
            Assert.IsFalse(PadPaintLogic.TryGetContactUv(new Vector3(0f, SurfY + 0.003f, 0f), out _),
                "上許容超過（浮いている）は非接触");
        }

        [Test]
        public void ShouldEmit_BelowThreshold_False_AboveThreshold_True()
        {
            var baseUv = new Vector2(0.5f, 0.5f);
            // du = 0.005 * 0.210 = 0.00105m < 0.0015m → 間引く
            Assert.IsFalse(PadPaintLogic.ShouldEmit(baseUv, new Vector2(0.505f, 0.5f)));
            // du = 0.01 * 0.210 = 0.0021m ≥ 0.0015m → 打つ
            Assert.IsTrue(PadPaintLogic.ShouldEmit(baseUv, new Vector2(0.51f, 0.5f)));
        }

        [Test]
        public void TryAdd_RespectsMaxSegmentsCap()
        {
            var buf = new List<PadPaintLogic.PaintSegment>();
            var seg = new PadPaintLogic.PaintSegment { a = Vector2.zero, b = Vector2.one, tool = 0 };
            for (int i = 0; i < PadPaintLogic.MaxSegments; i++)
                Assert.IsTrue(PadPaintLogic.TryAdd(buf, seg), $"{i} 本目は追加できるはず");
            Assert.AreEqual(PadPaintLogic.MaxSegments, buf.Count);
            Assert.IsFalse(PadPaintLogic.TryAdd(buf, seg), "上限超過は false");
            Assert.AreEqual(PadPaintLogic.MaxSegments, buf.Count, "超過分は追加されない");
        }
    }
}
