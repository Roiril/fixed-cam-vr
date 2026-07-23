#nullable enable
using NUnit.Framework;
using TableDuoVr.Hands;

namespace TableDuoVr.Tests
{
    /// <summary>
    /// HandPresentation（役割×バリアント→提示状態の純ロジック）と IsExternalRig の EditMode テスト。
    /// FullBody（手役のフル Remy 化）の中核仕様を真理値表で固定する:
    /// - FullBody は人役と同じ提示（自己ボディ・両手・リモートはフル形態）
    /// - FullBody から戻すと従来の手だけ提示（片手抑制・パック手/白手）へ完全復元
    /// - FullBody はパック手構築経路（IsExternalRig）に流れない
    /// </summary>
    public class HandPresentationTests
    {
        // --- IsExternalRig: FullBody が Realistic/Robot のパック手経路へ漏れない（回帰の要）---

        [Test]
        public void IsExternalRig_TrueOnlyForPackHandVariants()
        {
            Assert.IsFalse(HandVariantTable.IsExternalRig(HandVariant.Default));
            Assert.IsTrue(HandVariantTable.IsExternalRig(HandVariant.Realistic));
            Assert.IsTrue(HandVariantTable.IsExternalRig(HandVariant.Robot));
            Assert.IsFalse(HandVariantTable.IsExternalRig(HandVariant.FullBody),
                "FullBody が external 扱いだとパック手構築（prefab 不在）へ流れて失敗する");
        }

        // --- SelfBodyActive: 人役=ShowSelfBody / 手役=FullBody のときだけ同条件 ---

        [Test]
        public void SelfBodyActive_FullRole_FollowsShowSelfBodyRegardlessOfVariant()
        {
            foreach (HandVariant v in System.Enum.GetValues(typeof(HandVariant)))
            {
                Assert.IsTrue(HandPresentation.SelfBodyActive(StudyConfig.Role.Full, v, showSelfBody: true));
                Assert.IsFalse(HandPresentation.SelfBodyActive(StudyConfig.Role.Full, v, showSelfBody: false));
            }
        }

        [Test]
        public void SelfBodyActive_HandRole_OnlyWhenFullBody()
        {
            Assert.IsFalse(HandPresentation.SelfBodyActive(StudyConfig.Role.Hand, HandVariant.Default, true));
            Assert.IsFalse(HandPresentation.SelfBodyActive(StudyConfig.Role.Hand, HandVariant.Realistic, true));
            Assert.IsFalse(HandPresentation.SelfBodyActive(StudyConfig.Role.Hand, HandVariant.Robot, true));
            Assert.IsTrue(HandPresentation.SelfBodyActive(StudyConfig.Role.Hand, HandVariant.FullBody, true));
            // 人役と同じ規則: tdv_selfbody=off なら FullBody でも自己ボディ無し
            Assert.IsFalse(HandPresentation.SelfBodyActive(StudyConfig.Role.Hand, HandVariant.FullBody, false));
        }

        [Test]
        public void SelfBodyActive_Spectator_NeverActive()
        {
            foreach (HandVariant v in System.Enum.GetValues(typeof(HandVariant)))
            {
                Assert.IsFalse(HandPresentation.SelfBodyActive(StudyConfig.Role.Spectator, v, true));
            }
        }

        // --- SuppressLeftHand: FullBody 中だけ片手モードでも両手 ---

        [Test]
        public void SuppressLeftHand_HandRoleOneHand_SuppressedExceptFullBody()
        {
            Assert.IsTrue(HandPresentation.SuppressLeftHand(StudyConfig.Role.Hand, true, HandVariant.Default));
            Assert.IsTrue(HandPresentation.SuppressLeftHand(StudyConfig.Role.Hand, true, HandVariant.Realistic));
            Assert.IsTrue(HandPresentation.SuppressLeftHand(StudyConfig.Role.Hand, true, HandVariant.Robot));
            Assert.IsFalse(HandPresentation.SuppressLeftHand(StudyConfig.Role.Hand, true, HandVariant.FullBody),
                "FullBody は人役と同じ両手トラッキング");
        }

        [Test]
        public void SuppressLeftHand_TwoHandModeOrFullRole_NeverSuppressed()
        {
            foreach (HandVariant v in System.Enum.GetValues(typeof(HandVariant)))
            {
                Assert.IsFalse(HandPresentation.SuppressLeftHand(StudyConfig.Role.Hand, false, v));
                Assert.IsFalse(HandPresentation.SuppressLeftHand(StudyConfig.Role.Full, true, v));
            }
        }

        // --- WhiteHandVisible: Default のみ表示（FullBody は selfBody off のときだけ白手を残す）---

        [Test]
        public void WhiteHandVisible_TruthTable()
        {
            Assert.IsTrue(HandPresentation.WhiteHandVisible(HandVariant.Default, true));
            Assert.IsTrue(HandPresentation.WhiteHandVisible(HandVariant.Default, false));
            Assert.IsFalse(HandPresentation.WhiteHandVisible(HandVariant.Realistic, true));
            Assert.IsFalse(HandPresentation.WhiteHandVisible(HandVariant.Robot, true));
            Assert.IsFalse(HandPresentation.WhiteHandVisible(HandVariant.FullBody, true),
                "FullBody+selfBody は Remy 手が代替（白手二重表示を防ぐ）");
            Assert.IsTrue(HandPresentation.WhiteHandVisible(HandVariant.FullBody, false),
                "FullBody+selfBody off は人役の selfBody off と同じく白手を残す");
        }

        // --- RemoteHandsOnly: 手役でも FullBody 申告中はフル（Remy）形態 ---

        [Test]
        public void RemoteHandsOnly_HandRole_HandsOnlyExceptFullBody()
        {
            Assert.IsTrue(HandPresentation.RemoteHandsOnly(StudyConfig.Role.Hand, HandVariant.Default));
            Assert.IsTrue(HandPresentation.RemoteHandsOnly(StudyConfig.Role.Hand, HandVariant.Realistic));
            Assert.IsTrue(HandPresentation.RemoteHandsOnly(StudyConfig.Role.Hand, HandVariant.Robot));
            Assert.IsFalse(HandPresentation.RemoteHandsOnly(StudyConfig.Role.Hand, HandVariant.FullBody));
        }

        [Test]
        public void RemoteHandsOnly_FullRole_AlwaysFullView()
        {
            foreach (HandVariant v in System.Enum.GetValues(typeof(HandVariant)))
            {
                Assert.IsFalse(HandPresentation.RemoteHandsOnly(StudyConfig.Role.Full, v));
            }
        }

        // --- 往復の完全復元: FullBody→各バリアントで従来の手だけ提示に一致 ---

        [Test]
        public void ReturningFromFullBody_RestoresLegacyHandPresentation()
        {
            foreach (var v in new[] { HandVariant.Default, HandVariant.Realistic, HandVariant.Robot })
            {
                // 従来（FullBody 実装前）の手役提示と同値であること
                Assert.IsFalse(HandPresentation.SelfBodyActive(StudyConfig.Role.Hand, v, true));
                Assert.IsTrue(HandPresentation.SuppressLeftHand(StudyConfig.Role.Hand, true, v));
                Assert.IsTrue(HandPresentation.RemoteHandsOnly(StudyConfig.Role.Hand, v));
                Assert.AreEqual(v == HandVariant.Default, HandPresentation.WhiteHandVisible(v, true));
                Assert.AreEqual(HandVariantTable.IsExternalRig(v), v != HandVariant.Default);
            }
        }
    }
}
