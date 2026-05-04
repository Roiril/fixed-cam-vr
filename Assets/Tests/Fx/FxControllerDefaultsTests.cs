#nullable enable
using System.Reflection;
using FixedCamVr.Fx.Blit;
using FixedCamVr.Fx.Compute;
using FixedCamVr.Fx.RendererFeature;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Fx.Tests
{
    /// <summary>
    /// 各 Fx コントローラの Inspector 既定値が、宣言された Range 属性の範囲内に収まっているかを検証。
    /// 実装に純粋なクランプ関数が無いため、最低限の不変条件として default が範囲外になっていない事だけ保証する。
    /// </summary>
    public sealed class FxControllerDefaultsTests
    {
        private const BindingFlags BFI = BindingFlags.NonPublic | BindingFlags.Instance;

        private readonly System.Collections.Generic.List<GameObject> _spawned = new();

        private T NewBehaviour<T>() where T : MonoBehaviour
        {
            var go = new GameObject(typeof(T).Name);
            _spawned.Add(go);
            return go.AddComponent<T>();
        }

        [TearDown]
        public void Cleanup()
        {
            foreach (var go in _spawned)
            {
                if (go != null) Object.DestroyImmediate(go);
            }
            _spawned.Clear();
        }

        private static void AssertFieldWithinRange(object instance, string fieldName)
        {
            var f = instance.GetType().GetField(fieldName, BFI);
            Assert.That(f, Is.Not.Null, $"field '{fieldName}' not found");
            var value = (float)f!.GetValue(instance)!;
            var range = f.GetCustomAttribute<UnityEngine.RangeAttribute>();
            Assert.That(range, Is.Not.Null, $"field '{fieldName}' has no [Range]");
            Assert.That(value, Is.GreaterThanOrEqualTo(range!.min).And.LessThanOrEqualTo(range.max),
                $"default of '{fieldName}' ({value}) is outside [{range.min}, {range.max}]");
        }

        [Test]
        public void FxCrtPostFxController_Defaults_AreWithinRange()
        {
            var c = NewBehaviour<FxCrtPostFxController>();
            AssertFieldWithinRange(c, "scanlineIntensity");
            AssertFieldWithinRange(c, "scanlineCount");
            AssertFieldWithinRange(c, "grainIntensity");
            AssertFieldWithinRange(c, "vignetteIntensity");
        }

        [Test]
        public void FxBlitChromaticAberration_Defaults_AreWithinRange()
        {
            var c = NewBehaviour<FxBlitChromaticAberration>();
            AssertFieldWithinRange(c, "strength");
            AssertFieldWithinRange(c, "falloff");
        }

        [Test]
        public void SobelEdgeRunner_Defaults_AreWithinRange()
        {
            var c = NewBehaviour<SobelEdgeRunner>();
            AssertFieldWithinRange(c, "edgeIntensity");
            AssertFieldWithinRange(c, "exposure");
        }

        [Test]
        public void SobelEdgeRunner_Update_NoCrashWhenDependenciesMissing()
        {
            // compute / binder / targetRenderer 未設定でも Update は早期 return するはず
            var c = NewBehaviour<SobelEdgeRunner>();
            var m = typeof(SobelEdgeRunner).GetMethod("Update", BFI);
            Assert.That(m, Is.Not.Null);
            Assert.DoesNotThrow(() => m!.Invoke(c, null));
        }

        [Test]
        public void FxBlitChromaticAberration_Update_NoCrashWhenDependenciesMissing()
        {
            var c = NewBehaviour<FxBlitChromaticAberration>();
            var m = typeof(FxBlitChromaticAberration).GetMethod("Update", BFI);
            Assert.That(m, Is.Not.Null);
            Assert.DoesNotThrow(() => m!.Invoke(c, null));
        }

        [Test]
        public void FxCrtPostFxController_Update_NoCrashWhenMaterialMissing()
        {
            var c = NewBehaviour<FxCrtPostFxController>();
            var m = typeof(FxCrtPostFxController).GetMethod("Update", BFI);
            Assert.That(m, Is.Not.Null);
            Assert.DoesNotThrow(() => m!.Invoke(c, null));
        }
    }
}
