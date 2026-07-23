#nullable enable
using NUnit.Framework;
using TableDuoVr.Net;

namespace TableDuoVr.Tests
{
    /// <summary>
    /// TableDuoBuildInfo.ParseHash（sceneHash 前処理: BOM 除去 / trim / 先頭行抽出）。
    /// </summary>
    public class TableDuoBuildInfoTests
    {
        [Test]
        public void ParseHash_StripsBom()
        {
            Assert.AreEqual("abc123", TableDuoBuildInfo.ParseHash("﻿abc123"));
        }

        [Test]
        public void ParseHash_MultipleLines_FirstOnly()
        {
            Assert.AreEqual("line1", TableDuoBuildInfo.ParseHash("line1\nline2\nline3"));
        }

        [Test]
        public void ParseHash_TrimsWhitespace()
        {
            Assert.AreEqual("abc", TableDuoBuildInfo.ParseHash("  abc  "));
        }

        [Test]
        public void ParseHash_Empty()
        {
            Assert.AreEqual("", TableDuoBuildInfo.ParseHash(""));
        }

        [Test]
        public void ParseHash_Crlf_FirstLine()
        {
            // "line1\r\nline2" → Split('\n')[0]="line1\r" → Trim() で \r 除去 → "line1"
            Assert.AreEqual("line1", TableDuoBuildInfo.ParseHash("line1\r\nline2"));
        }
    }
}
