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
        public void TryGetUv_Corners_MapToUnitSquareCorners()
        {
            Assert.IsTrue(PadPaintLogic.TryGetUv(
                new Vector3(-PadPaintLogic.HalfX, SurfY, -PadPaintLogic.HalfZ), out var uv00, out _));
            Assert.That(uv00.x, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(uv00.y, Is.EqualTo(0f).Within(1e-4f));

            Assert.IsTrue(PadPaintLogic.TryGetUv(
                new Vector3(PadPaintLogic.HalfX, SurfY, PadPaintLogic.HalfZ), out var uv11, out _));
            Assert.That(uv11.x, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(uv11.y, Is.EqualTo(1f).Within(1e-4f));

            Assert.IsTrue(PadPaintLogic.TryGetUv(new Vector3(0f, SurfY, 0f), out var uvC, out var h));
            Assert.That(uvC.x, Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(uvC.y, Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(h, Is.EqualTo(0f).Within(1e-6f), "面高ちょうどは heightAbove=0");
        }

        [Test]
        public void TryGetUv_HeightAbove_IsSignedDistanceFromSurface()
        {
            Assert.IsTrue(PadPaintLogic.TryGetUv(new Vector3(0f, SurfY + 0.01f, 0f), out _, out var hi));
            Assert.That(hi, Is.EqualTo(0.01f).Within(1e-5f), "浮いていれば正");
            Assert.IsTrue(PadPaintLogic.TryGetUv(new Vector3(0f, SurfY - 0.01f, 0f), out _, out var lo));
            Assert.That(lo, Is.EqualTo(-0.01f).Within(1e-5f), "押し込めば負");
        }

        [Test]
        public void TryGetUv_OutsideXZ_ReturnsFalse()
        {
            Assert.IsFalse(PadPaintLogic.TryGetUv(
                new Vector3(PadPaintLogic.HalfX + 0.001f, SurfY, 0f), out _, out _), "X 範囲外");
            Assert.IsFalse(PadPaintLogic.TryGetUv(
                new Vector3(0f, SurfY, PadPaintLogic.HalfZ + 0.001f), out _, out _), "Z 範囲外");
        }

        [Test]
        public void ContactGate_Hysteresis_EntersOnDownTolExitsOnUpTol()
        {
            var gate = new PadPaintLogic.ContactGate();
            // 面上 3mm（DownTol=2mm 超）ではまだ接触しない
            Assert.IsFalse(gate.Tick(true, 0.003f), "DownTol 超では非接触のまま");
            // 面上 2mm ちょうどで接触開始
            Assert.IsTrue(gate.Tick(true, PadPaintLogic.ContactGate.DownTol), "DownTol で接触開始");
            // 面上 5mm（UpTol=6mm 未満）へ浮いても接触維持（ヒステリシス）
            Assert.IsTrue(gate.Tick(true, 0.005f), "UpTol 未満では接触維持");
            // 押し込み側（負）でも接触維持
            Assert.IsTrue(gate.Tick(true, -0.01f), "押し込み中も接触");
            // 面上 7mm（UpTol 超）で離れる
            Assert.IsFalse(gate.Tick(true, 0.007f), "UpTol 超で非接触");
        }

        [Test]
        public void ContactGate_ExitsWhenLeavingXz()
        {
            var gate = new PadPaintLogic.ContactGate();
            Assert.IsTrue(gate.Tick(true, 0f));
            Assert.IsFalse(gate.Tick(false, 0f), "枠外に出たら接触終了");
        }

        [Test]
        public void ShouldSuppressEcho_WhilePredictingOrWithinWindow()
        {
            Assert.IsTrue(PadPaintLogic.ShouldSuppressEcho(true, 999f), "予測中は常に抑止");
            Assert.IsTrue(PadPaintLogic.ShouldSuppressEcho(false, 0.5f), "終了直後の窓内は抑止");
            Assert.IsFalse(PadPaintLogic.ShouldSuppressEcho(false, 1.0f), "窓を過ぎたら通す");
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
