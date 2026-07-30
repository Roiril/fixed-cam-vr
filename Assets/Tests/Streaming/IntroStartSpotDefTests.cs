#nullable enable
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// **開始位置の「未設定」を取り違えないことを固定する。**
    ///
    /// Unity の JsonUtility は <b>キーが無くても入れ子の実体を作る</b>。だから
    /// <c>layout.startSpot != null</c> は「著作された」を意味しない — 未著作でも (0,0) 半径 0.35 の円が
    /// 生きてしまい、体験者がそこに立つと導入が勝手に始まる／正しい場所に立っても始まらない。
    /// 計画 §11.5 が「勝手に (0,0) へ置くと『立っても始まらない』の原因になる」と名指しで禁じた状態。
    ///
    /// このプロジェクトは同じ罠を hasRoom / hasPost / hasBgm / ShowIntroDef.LooksUnset で繰り返し
    /// 踏んでおり、対処は毎回「宣言 bool と実体の AND」。ここもそれに揃える。
    /// </summary>
    public sealed class IntroStartSpotDefTests
    {
        private static ShowLayoutDef Layout(ShowStartSpotDef? spot, bool declared)
            => new ShowLayoutDef { startSpot = spot, hasStartSpot = declared };

        private static ShowStartSpotDef Spot(float x, float z, string label = "スタート")
            => new ShowStartSpotDef { x = x, z = z, radiusM = 0.35f, label = label };

        [Test]
        public void キー欠落で作られた空の実体は未設定として扱う()
        {
            // JsonUtility が作る姿: 座標 0・ラベル空・半径はフィールド初期値
            var ghost = new ShowStartSpotDef();
            Assert.That(ghost.LooksUnset, Is.True);
            Assert.That(Layout(ghost, declared: false).ResolveStartSpot(), Is.Null,
                "未著作の (0,0) が生きた開始位置に化けている（体験者が立っても始まらない事故の原因）");
        }

        [Test]
        public void 宣言が無くてもラベルがあれば著作済みとして救う()
        {
            // 卓が hasStartSpot を書く前の show.json（現地データがこれ）。捨てると現場が動かなくなる。
            Assert.That(Layout(Spot(0.15f, -0.35f), declared: false).ResolveStartSpot(), Is.Not.Null);
        }

        [Test]
        public void 宣言があれば原点でも著作済み()
        {
            var l = Layout(Spot(0f, 0f, "原点に置いた"), declared: true);
            Assert.That(l.ResolveStartSpot(), Is.Not.Null,
                "宣言 bool が来ているなら座標が原点でも著作されている");
        }

        [Test]
        public void 実体が無ければ未設定()
        {
            Assert.That(Layout(null, declared: true).ResolveStartSpot(), Is.Null);
        }

        [Test]
        public void 半径は範囲外ならコード既定へ落ちる()
        {
            var s = Spot(1f, 1f);
            s.radiusM = 0f;
            Assert.That(s.ResolveRadiusM(), Is.EqualTo(ShowStartSpotDef.DefaultRadiusM).Within(1e-4f));
            s.radiusM = 9f;   // 部屋より大きい = 通りすがりで始まる
            Assert.That(s.ResolveRadiusM(), Is.EqualTo(ShowStartSpotDef.DefaultRadiusM).Within(1e-4f));
            s.radiusM = 0.5f;
            Assert.That(s.ResolveRadiusM(), Is.EqualTo(0.5f).Within(1e-4f));
        }
    }
}
