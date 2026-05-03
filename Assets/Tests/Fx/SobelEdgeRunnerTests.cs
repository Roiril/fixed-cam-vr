#nullable enable
using System.Reflection;
using FixedCamVr.Fx.Compute;
using NUnit.Framework;

namespace FixedCamVr.Fx.Tests
{
    /// <summary>
    /// SobelEdgeRunner の純粋ロジック (Kernel → ComputeShader カーネル名のマッピング) を検証。
    /// </summary>
    public sealed class SobelEdgeRunnerTests
    {
        private static string InvokeKernelName(SobelEdgeRunner.Kernel k)
        {
            var m = typeof(SobelEdgeRunner).GetMethod(
                "KernelName",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            return (string)m.Invoke(null, new object[] { k })!;
        }

        [Test]
        public void KernelName_Sobel_MapsToSobelEdge()
        {
            Assert.That(InvokeKernelName(SobelEdgeRunner.Kernel.Sobel), Is.EqualTo("SobelEdge"));
        }

        [Test]
        public void KernelName_SobelOverlay_MapsToSobelEdgeOverlay()
        {
            Assert.That(InvokeKernelName(SobelEdgeRunner.Kernel.SobelOverlay), Is.EqualTo("SobelEdgeOverlay"));
        }

        [Test]
        public void KernelName_Tonemap_MapsToReinhardTonemap()
        {
            Assert.That(InvokeKernelName(SobelEdgeRunner.Kernel.Tonemap), Is.EqualTo("ReinhardTonemap"));
        }
    }
}
