#nullable enable
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// **導入の覆いと、その上に重ねるものの描画順を固定する。**
    ///
    /// 覆い（<c>IntroVeil.shader</c>）は Passthrough Windows 方式で、フレームバッファの alpha を書く
    /// 全画面 1 パス（<c>Blend Zero SrcAlpha</c> ＝ 結果 rgb = srcAlpha × 背景）。
    /// 段 2 / 段 3 では枠がまだ開いていて alpha = 1 − _Passthrough = 0 になるため、
    /// **これより前に描いた VR の絵は rgb ごと 0 に潰される**。
    ///
    /// 2026-07-30 の設計批評で「段 3 の構造の線は原理的に見えない」と指摘され、実際にそうだった
    /// （線は Sprites/Default ＝ Queue 3000、覆いは Queue 5000）。線を後に置くことで直したが、
    /// **この関係は 2 つのファイルに分かれていて、片方だけ変えると沈黙して段 3 が消える**。
    /// このプロジェクトが色温度・CG 投影・マスクの座標系で使っているのと同じ突き合わせをここにも置く。
    /// </summary>
    public sealed class IntroVeilOrderTests
    {
        private static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ShaderPath =>
            Path.Combine(RepoRoot, "Assets", "Art", "Shaders", "Intro", "IntroVeil.shader");

        /// <summary>Unity の組み込み Queue 名。"Overlay" = 4000。</summary>
        private const int OverlayQueue = 4000;

        /// <summary>
        /// 覆いの Queue。<b>4900</b>（後続を 5000 に収めるため 2026-07-31 に 5000 から下げた）。
        /// URP の透明パスは [2501, 5000] しか描かないので、覆いを 5000 にすると後続が範囲外へ落ちる。
        /// </summary>
        private const int ExpectedVeilQueue = 4900;

        [Test]
        public void Veil_RenderQueue_LeavesRoomForFollowers()
        {
            string src = File.ReadAllText(ShaderPath);
            Match m = Regex.Match(src, @"""Queue""\s*=\s*""Overlay\+(\d+)""");
            Assert.That(m.Success, Is.True,
                "IntroVeil.shader の Queue 宣言を読み取れない（形が変わった？）。" +
                "IntroStructureWire.VeilRenderQueue と対で管理している。");
            int queue = OverlayQueue + int.Parse(m.Groups[1].Value);
            Assert.That(queue, Is.EqualTo(ExpectedVeilQueue),
                $"覆いの描画順が変わった（{queue}）。IntroStructureWire.VeilRenderQueue も直すこと — " +
                "線が覆いより前に描かれると、段 3 は画面に 1 本も出なくなる（黒く潰される）。");
            Assert.That(queue, Is.LessThan(5000),
                "覆いを 5000 に置くと、その後に描くもの（構造の線・HMD 内の指示）が 5000 超になり、" +
                "URP の透明パス [2501, 5000] の外へ落ちて 1 つも描画されない（2026-07-31 実害）。");
        }

        [Test]
        public void Veil_BlendMode_StillZeroSrcAlpha()
        {
            // この前提（結果 rgb = srcAlpha × 背景）が崩れたら、上の「後に描けば見える」も崩れる。
            string src = File.ReadAllText(ShaderPath);
            Assert.That(Regex.IsMatch(src, @"Blend\s+Zero\s+SrcAlpha"), Is.True,
                "覆いのブレンドが Passthrough Windows 方式（Blend Zero SrcAlpha）でなくなった。" +
                "描画順の前提が変わるので IntroStructureWire の renderQueue を見直すこと。");
        }

        [Test]
        public void StructureWire_DrawsAfterVeil()
        {
            var go = new GameObject("IntroStructureWireOrderTest");
            try
            {
                var wire = go.AddComponent<IntroStructureWire>();
                // EnsureMaterial は private。実際に線を作らせて、共有マテリアルの queue を見る。
                var mat = typeof(IntroStructureWire)
                    .GetMethod("EnsureMaterial", System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Instance)!
                    .Invoke(wire, null) as bool? == true
                    ? typeof(IntroStructureWire)
                        .GetField("_mat", System.Reflection.BindingFlags.NonPublic
                            | System.Reflection.BindingFlags.Instance)!
                        .GetValue(wire) as Material
                    : null;
                Assert.That(mat, Is.Not.Null, "構造の線のマテリアルを作れない（Sprites/Default が無い？）");
                Assert.That(mat!.renderQueue, Is.GreaterThan(ExpectedVeilQueue),
                    $"構造の線が覆い（Queue {ExpectedVeilQueue}）より前に描かれる（実測 {mat.renderQueue}）。" +
                    "段 2 / 段 3 では覆いの alpha が 0 なので、線は rgb ごと 0 に潰されて 1 本も見えない。");
                Assert.That(mat.renderQueue, Is.LessThanOrEqualTo(5000),
                    $"構造の線が URP の透明パス [2501, 5000] の外（実測 {mat.renderQueue}）。" +
                    "どの描画パスにも入らないので、警告も出ないまま 1 本も描かれない（2026-07-31 実害）。");
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
