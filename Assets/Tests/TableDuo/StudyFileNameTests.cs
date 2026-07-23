#nullable enable
using NUnit.Framework;
using TableDuoVr.Hands;

namespace TableDuoVr.Tests
{
    /// <summary>
    /// StudyFileName.SafeTag（pid/pair ファイル名サニタイズ・重複解消後の単一実装）。
    /// 英数 . _ - 以外は _ に落とす。
    /// </summary>
    public class StudyFileNameTests
    {
        [Test]
        public void SafeTag_SpaceAndSlash_ToUnderscore()
        {
            Assert.AreEqual("a_b_c", StudyFileName.SafeTag("a b/c"));
        }

        [Test]
        public void SafeTag_AllowedChars_Unchanged()
        {
            Assert.AreEqual("ok.name-1_2", StudyFileName.SafeTag("ok.name-1_2"), ". _ - と英数は保持");
        }

        [Test]
        public void SafeTag_Symbols_ToUnderscore()
        {
            // 記号・空白は _ に落ちる（英数 . _ - 以外）
            Assert.AreEqual("____", StudyFileName.SafeTag("!@ #"));
        }

        [Test]
        public void SafeTag_JapaneseLetters_Preserved()
        {
            // 実挙動: 日本語（かな/漢字）は Unicode Letter なので char.IsLetterOrDigit=true → 保持される。
            // 仕様本文の「日本語→_」は SafeTag の実挙動と異なるため、behavior-preserving 側に合わせて固定する。
            Assert.AreEqual("あ_い", StudyFileName.SafeTag("あ!い"));
        }

        [Test]
        public void SafeTag_Empty_Empty()
        {
            Assert.AreEqual("", StudyFileName.SafeTag(""));
        }
    }
}
