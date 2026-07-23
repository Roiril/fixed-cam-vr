#nullable enable
using NUnit.Framework;
using TableDuoVr.Net;

namespace TableDuoVr.Tests
{
    /// <summary>
    /// SessionCsv.Escape（外部入力 label の CSV サニタイズ・列崩れ防止）。Replace 連鎖順は不変。
    /// </summary>
    public class SessionCsvTests
    {
        [Test]
        public void Escape_Comma_ToSemicolon()
        {
            Assert.AreEqual("a;b", SessionCsv.Escape("a,b"));
        }

        [Test]
        public void Escape_Quote_ToApostrophe()
        {
            Assert.AreEqual("a'b", SessionCsv.Escape("a\"b"));
        }

        [Test]
        public void Escape_NewlineCrTab_ToSpace()
        {
            Assert.AreEqual("a b  c", SessionCsv.Escape("a\nb\r\tc"));
        }

        [Test]
        public void Escape_Clean_Unchanged()
        {
            Assert.AreEqual("phase2", SessionCsv.Escape("phase2"));
        }

        [Test]
        public void Escape_Empty()
        {
            Assert.AreEqual("", SessionCsv.Escape(""));
        }
    }
}
