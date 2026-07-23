#nullable enable
using System.Collections.Generic;
using NUnit.Framework;
using TableDuoVr.Hands;

namespace TableDuoVr.Tests
{
    /// <summary>
    /// tdv_* 起動フラグ → StudyConfig のパース/エイリアス/上書き規則を固定する（study セッションの正否を決める）。
    /// StudyLaunchFlags.ApplyFrom(get) に固定辞書 lambda を渡して Environment / Android intent を注入回避する
    /// （Apply() は Get を渡すだけ＝挙動不変）。StudyConfig の static は EditMode で RuntimeInitializeOnLoad が
    /// 発火しないため [SetUp]/[TearDown] で明示リセットしてテスト間汚染を防ぐ。
    /// </summary>
    public class StudyLaunchFlagsTests
    {
        [SetUp]
        [TearDown]
        public void ResetStudyConfig()
        {
            StudyConfig.ForcedRole = null;
            StudyConfig.ShowHeadMarker = false;
            StudyConfig.OneHandMode = true;
            StudyConfig.ShowSelfBody = true;
            StudyConfig.SelectedHandVariant = HandVariant.Default;
            StudyConfig.ParticipantId = "";
            StudyConfig.PairId = "";
            StudyConfig.PreplaceAvatars = false;
            StudyConfig.ShowPatternPanel = false;
            StudyConfig.LaunchedWithStudyFlags = false;
            StudyConfig.HandVariantLockedByFlag = false;
        }

        /// <summary>extra キー（tdv_*）→ 値の固定辞書から get 関数を作る。未登録キーは null（＝未指定）。</summary>
        private static System.Func<string, string, string?> Flags(params (string key, string value)[] kv)
        {
            var d = new Dictionary<string, string>();
            foreach (var (k, v) in kv) d[k] = v;
            return (extra, _) => d.TryGetValue(extra, out var val) ? val : null;
        }

        // --- role ---

        [Test]
        public void Role_MapsAndSetsLaunchedFlag()
        {
            StudyLaunchFlags.ApplyFrom(Flags(("tdv_role", "full")));
            Assert.AreEqual(StudyConfig.Role.Full, StudyConfig.ForcedRole);
            Assert.IsTrue(StudyConfig.LaunchedWithStudyFlags);

            ResetStudyConfig();
            StudyLaunchFlags.ApplyFrom(Flags(("tdv_role", "hand")));
            Assert.AreEqual(StudyConfig.Role.Hand, StudyConfig.ForcedRole);

            ResetStudyConfig();
            StudyLaunchFlags.ApplyFrom(Flags(("tdv_role", "spectator")));
            Assert.AreEqual(StudyConfig.Role.Spectator, StudyConfig.ForcedRole);
        }

        [Test]
        public void Role_Unknown_LeavesRoleButStillMarksLaunched()
        {
            StudyLaunchFlags.ApplyFrom(Flags(("tdv_role", "bogus")));
            Assert.IsNull(StudyConfig.ForcedRole, "未知の role 値は ForcedRole を変えない");
            Assert.IsTrue(StudyConfig.LaunchedWithStudyFlags, "role 指定があれば（値が未知でも）調査セッション扱い");
        }

        // --- on/off フラグ ---

        [Test]
        public void BoolFlags_OnOffOverrideOnlyWhenSpecified()
        {
            StudyLaunchFlags.ApplyFrom(Flags(
                ("tdv_marker", "on"),
                ("tdv_hands", "two"),
                ("tdv_selfbody", "off"),
                ("tdv_pattern", "on"),
                ("tdv_preplace", "on")));
            Assert.IsTrue(StudyConfig.ShowHeadMarker);
            Assert.IsFalse(StudyConfig.OneHandMode, "hands=two で片手 OFF");
            Assert.IsFalse(StudyConfig.ShowSelfBody, "selfbody=off");
            Assert.IsTrue(StudyConfig.ShowPatternPanel);
            Assert.IsTrue(StudyConfig.PreplaceAvatars);

            ResetStudyConfig();
            StudyLaunchFlags.ApplyFrom(Flags(
                ("tdv_marker", "off"),
                ("tdv_hands", "one")));
            Assert.IsFalse(StudyConfig.ShowHeadMarker);
            Assert.IsTrue(StudyConfig.OneHandMode, "hands=one で片手 ON");
        }

        [Test]
        public void UnspecifiedFlags_KeepDefaults()
        {
            StudyLaunchFlags.ApplyFrom(Flags(("tdv_marker", "on"))); // 他は未指定
            Assert.IsTrue(StudyConfig.OneHandMode, "未指定 hands は既定 ON を保持");
            Assert.IsTrue(StudyConfig.ShowSelfBody, "未指定 selfbody は既定 ON を保持");
        }

        // --- hand エイリアス ---

        [Test]
        public void HandAlias_RealisticVariants()
        {
            foreach (var alias in new[] { "realistic", "male", "human", "skin" })
            {
                ResetStudyConfig();
                StudyLaunchFlags.ApplyFrom(Flags(("tdv_hand", alias)));
                Assert.AreEqual(HandVariant.Realistic, StudyConfig.SelectedHandVariant, $"'{alias}'→Realistic");
            }
        }

        [Test]
        public void HandAlias_RobotFullBodyAndUnknown()
        {
            StudyLaunchFlags.ApplyFrom(Flags(("tdv_hand", "robot")));
            Assert.AreEqual(HandVariant.Robot, StudyConfig.SelectedHandVariant);

            foreach (var alias in new[] { "remy", "full", "fullbody" })
            {
                ResetStudyConfig();
                StudyLaunchFlags.ApplyFrom(Flags(("tdv_hand", alias)));
                Assert.AreEqual(HandVariant.FullBody, StudyConfig.SelectedHandVariant, $"'{alias}'→FullBody");
            }

            ResetStudyConfig();
            StudyLaunchFlags.ApplyFrom(Flags(("tdv_hand", "wat")));
            Assert.AreEqual(HandVariant.Default, StudyConfig.SelectedHandVariant, "未知値→Default");
        }

        [Test]
        public void HandSpecified_SetsLockAndLaunchedFlags()
        {
            StudyLaunchFlags.ApplyFrom(Flags(("tdv_hand", "robot")));
            Assert.IsTrue(StudyConfig.HandVariantLockedByFlag);
            Assert.IsTrue(StudyConfig.LaunchedWithStudyFlags);
        }

        // --- pid / pair ---

        [Test]
        public void PidPair_NonEmptySetsFieldAndLaunched()
        {
            StudyLaunchFlags.ApplyFrom(Flags(("tdv_pid", "P07"), ("tdv_pair", "PAIR3")));
            Assert.AreEqual("P07", StudyConfig.ParticipantId);
            Assert.AreEqual("PAIR3", StudyConfig.PairId);
            Assert.IsTrue(StudyConfig.LaunchedWithStudyFlags);
        }

        [Test]
        public void PidPair_EmptyIgnored()
        {
            StudyLaunchFlags.ApplyFrom(Flags(("tdv_pid", ""), ("tdv_pair", "")));
            Assert.AreEqual("", StudyConfig.ParticipantId);
            Assert.AreEqual("", StudyConfig.PairId);
            Assert.IsFalse(StudyConfig.LaunchedWithStudyFlags, "空文字は無視され調査セッション扱いにならない");
        }

        // --- 全未指定 ---

        [Test]
        public void AllUnspecified_LeavesDefaultsAndNotLaunched()
        {
            StudyLaunchFlags.ApplyFrom(Flags());
            Assert.IsNull(StudyConfig.ForcedRole);
            Assert.IsFalse(StudyConfig.ShowHeadMarker);
            Assert.IsTrue(StudyConfig.OneHandMode);
            Assert.IsTrue(StudyConfig.ShowSelfBody);
            Assert.AreEqual(HandVariant.Default, StudyConfig.SelectedHandVariant);
            Assert.AreEqual("", StudyConfig.ParticipantId);
            Assert.AreEqual("", StudyConfig.PairId);
            Assert.IsFalse(StudyConfig.PreplaceAvatars);
            Assert.IsFalse(StudyConfig.ShowPatternPanel);
            Assert.IsFalse(StudyConfig.HandVariantLockedByFlag);
            Assert.IsFalse(StudyConfig.LaunchedWithStudyFlags);
        }
    }
}
