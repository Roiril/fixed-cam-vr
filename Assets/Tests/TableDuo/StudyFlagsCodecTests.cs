#nullable enable
using NUnit.Framework;
using TableDuoVr.Hands;

namespace TableDuoVr.Tests
{
    /// <summary>
    /// study-flags ビット配置と round-trip（cross-device 条件同期）を固定する。
    /// bit0=marker / bit1=oneHand / bit2-3=variant / bit4=selfBody。variant が 2bit を超えると bit4 を汚す
    /// ため、variant≤3 での非衝突を明示 pin する（HandVariant 拡張時に必ずこのテストが割れる）。
    /// </summary>
    public class StudyFlagsCodecTests
    {
        private static readonly HandVariant[] AllVariants =
        {
            HandVariant.Default, HandVariant.Realistic, HandVariant.Robot, HandVariant.FullBody,
        };

        [Test]
        public void PackDecode_RoundTripsAllCombinations()
        {
            foreach (bool marker in new[] { false, true })
            foreach (bool oneHand in new[] { false, true })
            foreach (bool selfBody in new[] { false, true })
            foreach (var variant in AllVariants)
            {
                byte f = StudyFlags.Pack(marker, oneHand, variant, selfBody);
                Assert.AreEqual(marker, StudyFlags.Marker(f), $"marker m={marker} o={oneHand} v={variant} s={selfBody}");
                Assert.AreEqual(oneHand, StudyFlags.OneHand(f), $"oneHand m={marker} o={oneHand} v={variant} s={selfBody}");
                Assert.AreEqual(selfBody, StudyFlags.SelfBody(f), $"selfBody m={marker} o={oneHand} v={variant} s={selfBody}");
                Assert.AreEqual(variant, StudyFlags.Variant(f), $"variant m={marker} o={oneHand} v={variant} s={selfBody}");
            }
        }

        [Test]
        public void BitAssignment_IndependentBits()
        {
            Assert.AreEqual(1, StudyFlags.Pack(true, false, HandVariant.Default, false), "marker=bit0 単独");
            Assert.AreEqual(2, StudyFlags.Pack(false, true, HandVariant.Default, false), "oneHand=bit1 単独");
            Assert.AreEqual(16, StudyFlags.Pack(false, false, HandVariant.Default, true), "selfBody=bit4 単独");
        }

        [Test]
        public void FullBodyVariant_DoesNotCollideWithSelfBodyBit()
        {
            // variant=FullBody(3) << 2 = 12。bit4(16=selfBody) と非衝突
            byte f = StudyFlags.Pack(false, false, HandVariant.FullBody, false);
            Assert.AreEqual(12, f, "FullBody(3)<<2=12");
            Assert.IsFalse(StudyFlags.SelfBody(f), "variant 12 は bit4 を汚さない");
            Assert.AreEqual(HandVariant.FullBody, StudyFlags.Variant(f));
        }

        [Test]
        public void VariantOfPack_IdentityForAll()
        {
            foreach (var variant in AllVariants)
            {
                Assert.AreEqual(variant, StudyFlags.Variant(StudyFlags.Pack(false, false, variant, false)));
                // 他ビットが立っていても variant の抽出は独立
                Assert.AreEqual(variant, StudyFlags.Variant(StudyFlags.Pack(true, true, variant, true)));
            }
        }
    }
}
