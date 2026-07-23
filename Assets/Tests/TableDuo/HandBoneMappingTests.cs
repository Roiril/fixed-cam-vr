#nullable enable
using NUnit.Framework;
using TableDuoVr.Hands;

namespace TableDuoVr.Tests
{
    /// <summary>
    /// BoneId → FBX / パック手ボーン名の wire・リターゲット契約を固定する。
    /// 送信/受信メッシュ駆動/FK が同一 BoneId 順に依存する wire 契約 + 2026-07-12 に実バグのあった
    /// リターゲット名対応（Realistic の _end サフィックス位置・thumb2 欠け）を pin する。純関数のみ。
    /// </summary>
    public class HandBoneMappingTests
    {
        // --- HandBoneTable.FbxBoneName ---

        [Test]
        public void FbxBoneName_KnownIdsAndSides()
        {
            Assert.AreEqual("b_r_wrist", HandBoneTable.FbxBoneName(0, true));
            Assert.AreEqual("b_l_wrist", HandBoneTable.FbxBoneName(0, false));
            Assert.AreEqual("b_r_thumb0", HandBoneTable.FbxBoneName(2, true));
            Assert.AreEqual("b_r_pinky3", HandBoneTable.FbxBoneName(18, true));
            Assert.AreEqual("r_thumb_finger_tip_marker", HandBoneTable.FbxBoneName(19, true));
            Assert.AreEqual("r_pinky_finger_tip_marker", HandBoneTable.FbxBoneName(23, true));
        }

        [Test]
        public void FbxBoneName_OutOfRangeIsNull()
        {
            Assert.IsNull(HandBoneTable.FbxBoneName(-1, true));
            Assert.IsNull(HandBoneTable.FbxBoneName(24, true));
        }

        [Test]
        public void IsFingerTip_BoundaryRange()
        {
            Assert.IsFalse(HandBoneTable.IsFingerTip(18));
            for (int i = 19; i <= 23; i++) Assert.IsTrue(HandBoneTable.IsFingerTip(i), $"{i} は指先");
            Assert.IsFalse(HandBoneTable.IsFingerTip(24));
        }

        // --- HandVariantTable.BoneName ---

        [Test]
        public void Default_DelegatesToFbx()
        {
            Assert.AreEqual("b_r_wrist", HandVariantTable.BoneName(HandVariant.Default, 0, true));
            Assert.AreEqual(HandBoneTable.FbxBoneName(2, false),
                HandVariantTable.BoneName(HandVariant.Default, 2, false));
        }

        [Test]
        public void Realistic_SideSuffixEndPositionAndThumb2Gap()
        {
            Assert.AreEqual("Root.R", HandVariantTable.BoneName(HandVariant.Realistic, 0, true));
            Assert.AreEqual("Root.L", HandVariantTable.BoneName(HandVariant.Realistic, 0, false));
            Assert.AreEqual("UpwardThumb.R", HandVariantTable.BoneName(HandVariant.Realistic, 5, true));
            // _end は側サフィックスの後（UpwardThumb.R_end）
            Assert.AreEqual("UpwardThumb.R_end", HandVariantTable.BoneName(HandVariant.Realistic, 19, true));
            Assert.AreEqual("UpwardThumb.L_end", HandVariantTable.BoneName(HandVariant.Realistic, 19, false));
            // Male 親指は 3 節（thumb2=BoneId4 欠け）
            Assert.IsNull(HandVariantTable.BoneName(HandVariant.Realistic, 4, true));
        }

        [Test]
        public void Robot_SideIndependentNames()
        {
            Assert.AreEqual("Bone_Hand", HandVariantTable.BoneName(HandVariant.Robot, 0, true));
            Assert.AreEqual("Bone_ThumbUpper", HandVariantTable.BoneName(HandVariant.Robot, 5, true));
            Assert.AreEqual("Bone_ThumbUpper_end", HandVariantTable.BoneName(HandVariant.Robot, 19, true));
            // 左右同名（side 非依存）
            Assert.AreEqual(HandVariantTable.BoneName(HandVariant.Robot, 0, true),
                HandVariantTable.BoneName(HandVariant.Robot, 0, false));
            Assert.AreEqual(HandVariantTable.BoneName(HandVariant.Robot, 19, true),
                HandVariantTable.BoneName(HandVariant.Robot, 19, false));
        }

        [Test]
        public void FullBody_UnmappedIsNull()
        {
            // FullBody（enum 外の switch default）は未対応 boneId が null
            Assert.IsNull(HandVariantTable.BoneName(HandVariant.FullBody, 0, true));
            Assert.IsNull(HandVariantTable.BoneName(HandVariant.FullBody, 5, true));
        }

        [Test]
        public void WristBoneName_EqualsBoneNameAtZero_AllVariants()
        {
            foreach (HandVariant v in System.Enum.GetValues(typeof(HandVariant)))
            {
                Assert.AreEqual(HandVariantTable.BoneName(v, 0, true), HandVariantTable.WristBoneName(v, true),
                    $"{v} right");
                Assert.AreEqual(HandVariantTable.BoneName(v, 0, false), HandVariantTable.WristBoneName(v, false),
                    $"{v} left");
            }
        }
    }
}
