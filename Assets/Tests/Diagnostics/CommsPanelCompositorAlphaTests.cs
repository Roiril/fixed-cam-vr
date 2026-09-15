#nullable enable

using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace FixedCamVr.Diagnostics.Tests
{
    /// <summary>
    /// 連絡の面の地は、compositor alpha を<b>閉じる方向にしか</b>動かさない
    /// （2026-09-15・<c>canon/LEDGER.md</c> 0227「タイトル前の暗い中でエージェントスクリーンの
    /// 背景ごしにパススルーが見えている」）。
    ///
    /// Quest の compositor はフレームバッファの alpha が 1 未満の画素にパススルーを混ぜる。地は黒い半透明
    /// （RGB の alpha 0.72・0096）なので、alpha も同じ式で書くと題字の黒が書いた alpha 1 を 0.72 へ
    /// 置き換え、その画素に 28% の現実が混ざる。alpha は <c>One OneMinusSrcAlpha</c>（TitleVeil / CommsAvatar
    /// と同じ）でなければならない。
    /// </summary>
    public sealed class CommsPanelCompositorAlphaTests
    {
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        [Test]
        public void PanelAndDivider_AddCompositorAlpha_NeverReplaceIt()
        {
            var go = new GameObject("CommsPanelCompositorAlphaTest");
            try
            {
                var panel = go.AddComponent<CommsPanel>();
                typeof(CommsPanel).GetMethod("Awake", Private)!.Invoke(panel, null);
                Assume.That(panel.IsBuilt, Is.True, "前提: 面を組める（シェーダとフォントが要る）");

                foreach (string field in new[] { "_panelMat", "_dividerMat" })
                {
                    var mat = (Material?)typeof(CommsPanel).GetField(field, Private)!.GetValue(panel);
                    Assert.That(mat, Is.Not.Null, field);
                    if (!mat!.HasProperty("_SrcBlendAlpha"))
                    {
                        // Unlit/Color へ落ちた環境（alpha そのものが無い ＝ 不透明）。ここでは判定しない。
                        Assert.Inconclusive($"{field}: {mat.shader.name} は alpha の別ブレンドを持たない");
                    }
                    Assert.That((BlendMode)mat.GetFloat("_SrcBlendAlpha"), Is.EqualTo(BlendMode.One),
                        $"{field}: compositor alpha を置き換えている（題字の黒の上でパススルーが透ける）");
                    Assert.That((BlendMode)mat.GetFloat("_DstBlendAlpha"), Is.EqualTo(BlendMode.OneMinusSrcAlpha),
                        $"{field}: compositor alpha の混ぜ方が TitleVeil と違う");
                    // RGB の混ぜ方（黒い半透明・0096）は変えていない。
                    Assert.That((BlendMode)mat.GetFloat("_SrcBlend"), Is.EqualTo(BlendMode.SrcAlpha), field);
                    Assert.That((BlendMode)mat.GetFloat("_DstBlend"), Is.EqualTo(BlendMode.OneMinusSrcAlpha), field);
                }
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
