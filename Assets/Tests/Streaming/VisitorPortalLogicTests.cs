#nullable enable
using System.Text;
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// <b>タブレットの口（HTTP）の判断</b>（2026-09-11・<c>canon/LEDGER.md</c> 0187）。
    /// ソケットは Editor で立てられないので、要求の読み方・道の選び方・応答の形をここで固定する。
    /// ⚠ 実機で口が開くか（bind）は走行の <c>visitor=1/…</c> でしか分からない。
    /// </summary>
    public sealed class VisitorPortalLogicTests
    {
        private static VisitorPortalLogic.Response Route(string method, string path, string body,
                                                         out int setCalls, out ShowLang lastLang, out bool lastRelief,
                                                         out int clearCalls)
        {
            int sets = 0, clears = 0; ShowLang l = ShowLang.Ja; bool r = false;
            var req = new VisitorPortalLogic.Request { method = method, path = path, contentLength = body.Length };
            var res = VisitorPortalLogic.Route(req, body, "<html>page</html>", "{\"ok\":true,\"lang\":\"ja\"}",
                (lang, relief, tabletId) => { sets++; l = lang; r = relief; return 7; },
                () => clears++, portalSessionId: "portal-a");
            setCalls = sets; lastLang = l; lastRelief = r; clearCalls = clears;
            return res;
        }

        [Test]
        public void ParseHead_ReadsMethodPathAndContentLength()
        {
            string head = "POST /set?x=1 HTTP/1.1\r\nHost: 192.168.10.31:8090\r\ncontent-length: 27\r\nAccept: */*";
            Assert.IsTrue(VisitorPortalLogic.TryParseHead(head, out var req));
            Assert.AreEqual("POST", req.method);
            Assert.AreEqual("/set", req.path, "クエリを落とす");
            Assert.AreEqual(27, req.contentLength, "ヘッダ名は大文字小文字を見ない");
        }

        [Test]
        public void ParseHead_RejectsGarbage()
        {
            Assert.IsFalse(VisitorPortalLogic.TryParseHead("", out _));
            Assert.IsFalse(VisitorPortalLogic.TryParseHead("nonsense", out _));
        }

        [Test]
        public void ParseSet_ReadsLangAndRelief_AndRejectsUnknownLang()
        {
            Assert.IsTrue(VisitorPortalLogic.TryParseSet("{\"lang\":\"EN\",\"relief\":true}", out var lang, out var relief));
            Assert.AreEqual(ShowLang.En, lang);
            Assert.IsTrue(relief);
            Assert.IsTrue(VisitorPortalLogic.TryParseSet("{ \"relief\" : false , \"lang\" : \"fr\" }", out lang, out relief));
            Assert.AreEqual(ShowLang.Fr, lang);
            Assert.IsFalse(relief);
            Assert.IsTrue(VisitorPortalLogic.TryParseSet("{\"lang\":\"ja\"}", out lang, out relief), "relief は省略できる");
            Assert.IsFalse(relief);
            Assert.IsFalse(VisitorPortalLogic.TryParseSet("{\"lang\":\"de\",\"relief\":true}", out _, out _), "知らない言語を既定へ倒さない");
            Assert.IsFalse(VisitorPortalLogic.TryParseSet("{\"relief\":true}", out _, out _), "lang が無い");
        }

        [Test]
        public void Get_Root_ServesThePage()
        {
            var res = Route("GET", "/", "", out int sets, out _, out _, out int clears);
            Assert.AreEqual(200, res.status);
            Assert.AreEqual("<html>page</html>", res.body);
            StringAssert.StartsWith("text/html", res.contentType);
            Assert.AreEqual(0, sets); Assert.AreEqual(0, clears);
            Assert.AreEqual(200, Route("GET", "/visitor.html", "", out _, out _, out _, out _).status);
        }

        [Test]
        public void Get_Status_ReturnsTheMainThreadSnapshot()
        {
            var res = Route("GET", "/status", "", out _, out _, out _, out _);
            Assert.AreEqual(200, res.status);
            Assert.AreEqual("{\"ok\":true,\"lang\":\"ja\"}", res.body);
        }

        [Test]
        public void Post_Set_QueuesTheChoice_AndReturnsTheSeq()
        {
            var res = Route("POST", "/set", "{\"lang\":\"fr\",\"relief\":true,\"tabletSessionId\":\"page-a\",\"portalSessionId\":\"portal-a\"}", out int sets, out var lang, out var relief, out _);
            Assert.AreEqual(200, res.status);
            Assert.AreEqual(1, sets);
            Assert.AreEqual(ShowLang.Fr, lang);
            Assert.IsTrue(relief);
            StringAssert.Contains("\"seq\":7", res.body);
        }

        [Test]
        public void Post_Set_WithBadBody_Is400_AndQueuesNothing()
        {
            var res = Route("POST", "/set", "{\"lang\":\"xx\"}", out int sets, out _, out _, out _);
            Assert.AreEqual(400, res.status);
            Assert.AreEqual(0, sets);
        }

        [Test]
        public void Post_Set_RequiresBothSessionIds()
        {
            Assert.AreEqual(400, Route("POST", "/set", "{\"lang\":\"ja\"}", out int sets, out _, out _, out _).status);
            Assert.AreEqual(400, Route("POST", "/set", "{\"lang\":\"ja\",\"tabletSessionId\":\"page-a\"}", out _, out _, out _, out _).status);
            Assert.AreEqual(0, sets);
        }

        [Test]
        public void Post_Set_RejectsOldPortalSession_AndKeepsTabletAttribution()
        {
            string tablet = "";
            var req = new VisitorPortalLogic.Request { method = "POST", path = "/set" };
            var old = VisitorPortalLogic.Route(req,
                "{\"lang\":\"ja\",\"tabletSessionId\":\"page-a\",\"portalSessionId\":\"old\"}",
                "", "{}", (lang, relief, id) => { tablet = id; return 1; }, () => { },
                portalSessionId: "current");
            Assert.AreEqual(409, old.status);
            Assert.AreEqual("", tablet);

            var current = VisitorPortalLogic.Route(req,
                "{\"lang\":\"ja\",\"tabletSessionId\":\"page-a\",\"portalSessionId\":\"current\"}",
                "", "{}", (lang, relief, id) => { tablet = id; return 2; }, () => { },
                portalSessionId: "current");
            Assert.AreEqual(200, current.status);
            Assert.AreEqual("page-a", tablet);
        }

        [Test]
        public void Post_Pulse_RequiresTabletId()
        {
            string tablet = "";
            var req = new VisitorPortalLogic.Request { method = "POST", path = "/tablet/pulse" };
            Assert.AreEqual(400, VisitorPortalLogic.Route(req, "{}", "", "{}",
                (lang, relief, id) => 1, () => { }, onPulse: id => tablet = id).status);
            Assert.AreEqual(200, VisitorPortalLogic.Route(req, "{\"tabletSessionId\":\"page-a\"}", "", "{}",
                (lang, relief, id) => 1, () => { }, onPulse: id => tablet = id).status);
            Assert.AreEqual("page-a", tablet);
        }

        [Test]
        public void Post_Clear_QueuesAClear()
        {
            var res = Route("POST", "/clear", "{\"tabletSessionId\":\"page-a\",\"portalSessionId\":\"portal-a\"}", out int sets, out _, out _, out int clears);
            Assert.AreEqual(200, res.status);
            Assert.AreEqual(1, clears); Assert.AreEqual(0, sets);
        }

        [Test]
        public void Post_Clear_RequiresCurrentSessionIds()
        {
            Assert.AreEqual(400, Route("POST", "/clear", "", out _, out _, out _, out int clears).status);
            Assert.AreEqual(0, clears);
            var req = new VisitorPortalLogic.Request { method = "POST", path = "/clear" };
            var old = VisitorPortalLogic.Route(req,
                "{\"tabletSessionId\":\"page-a\",\"portalSessionId\":\"old\"}",
                "", "{}", (lang, relief, id) => 1, () => clears++, portalSessionId: "current");
            Assert.AreEqual(409, old.status);
            Assert.AreEqual(0, clears);
        }

        [Test]
        public void UnknownPath_Is404()
        {
            Assert.AreEqual(404, Route("GET", "/nope", "", out _, out _, out _, out _).status);
            Assert.AreEqual(404, Route("GET", "/set", "", out _, out _, out _, out _).status, "GET で set は受けない");
        }

        [Test]
        public void Encode_WritesAValidHttpResponse_WithByteLength()
        {
            var res = new VisitorPortalLogic.Response { status = 200, contentType = "text/html; charset=utf-8", body = "日本語" };
            string text = Encoding.UTF8.GetString(VisitorPortalLogic.Encode(res));
            StringAssert.StartsWith("HTTP/1.1 200 OK\r\n", text);
            StringAssert.Contains("Content-Length: " + Encoding.UTF8.GetByteCount("日本語") + "\r\n", text, "文字数ではなくバイト数");
            StringAssert.Contains("Connection: close\r\n\r\n日本語", text);
        }

        // ---- 面が使う画像・動画（GET /asset/<name>）----
        private static VisitorPortalLogic.Response RouteAsset(string path, long rangeStart, long rangeEnd, byte[]? data)
        {
            var req = new VisitorPortalLogic.Request { method = "GET", path = path, rangeStart = rangeStart, rangeEnd = rangeEnd };
            return VisitorPortalLogic.Route(req, "", "<html>", "{}", (l, r, id) => 1, () => { }, (name) => name == "doctor.jpg" ? data : null);
        }

        [Test]
        public void Asset_ServesBytes_WithContentTypeFromExtension()
        {
            byte[] data = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            var res = RouteAsset("/asset/doctor.jpg", -1, -1, data);
            Assert.AreEqual(200, res.status);
            Assert.AreEqual("image/jpeg", res.contentType);
            Assert.AreSame(data, res.bytes);
            Assert.AreEqual(10, res.bytesCount);
            string head = Encoding.ASCII.GetString(VisitorPortalLogic.Encode(res), 0, 120);
            StringAssert.Contains("Content-Length: 10\r\n", head);
            StringAssert.Contains("Accept-Ranges: bytes\r\n", head);
        }

        [Test]
        public void Asset_Range_Returns206_WithContentRange()
        {
            byte[] data = new byte[100];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)i;
            var res = RouteAsset("/asset/doctor.jpg", 10, 19, data);
            Assert.AreEqual(206, res.status);
            Assert.AreEqual(10, res.bytesOffset);
            Assert.AreEqual(10, res.bytesCount);
            Assert.AreEqual(100, res.totalLength);
            byte[] all = VisitorPortalLogic.Encode(res);
            string text = Encoding.ASCII.GetString(all);
            StringAssert.Contains("HTTP/1.1 206 Partial Content\r\n", text);
            StringAssert.Contains("Content-Range: bytes 10-19/100\r\n", text);
            StringAssert.Contains("Content-Length: 10\r\n", text);
            Assert.AreEqual(10, all[all.Length - 10], "本文の先頭が範囲の先頭でない");
            Assert.AreEqual(19, all[all.Length - 1], "本文の末尾が範囲の末尾でない");
            // 開いた範囲（bytes=90-）は末尾まで
            var tail = RouteAsset("/asset/doctor.jpg", 90, -1, data);
            Assert.AreEqual(206, tail.status);
            Assert.AreEqual(10, tail.bytesCount);
        }

        [Test]
        public void Asset_Unknown_Is404_AndBadNamesAreRejected()
        {
            Assert.AreEqual(404, RouteAsset("/asset/nope.png", -1, -1, null).status);
            Assert.AreEqual(404, RouteAsset("/asset/../secret", -1, -1, new byte[1]).status, "名前に / や .. を通さない");
        }

        [Test]
        public void ParseHead_ReadsRange()
        {
            Assert.IsTrue(VisitorPortalLogic.TryParseHead("GET /asset/doctor.mp4 HTTP/1.1\r\nRange: bytes=1000-1999\r\n", out var req));
            Assert.AreEqual(1000, req.rangeStart);
            Assert.AreEqual(1999, req.rangeEnd);
            Assert.IsTrue(VisitorPortalLogic.TryParseHead("GET /asset/doctor.mp4 HTTP/1.1\r\nRange: bytes=500-\r\n", out req));
            Assert.AreEqual(500, req.rangeStart);
            Assert.AreEqual(-1, req.rangeEnd);
            Assert.IsTrue(VisitorPortalLogic.TryParseHead("GET / HTTP/1.1\r\n", out req));
            Assert.AreEqual(-1, req.rangeStart, "Range が無ければ -1");
        }

        [Test]
        public void ContentTypeFor_KnowsTheTypesThePageUses()
        {
            Assert.AreEqual("application/json; charset=utf-8", VisitorPortalLogic.ContentTypeFor("briefing-v1.JSON"));
            Assert.AreEqual("image/jpeg", VisitorPortalLogic.ContentTypeFor("doctor.JPG"));
            Assert.AreEqual("video/mp4", VisitorPortalLogic.ContentTypeFor("doctor.mp4"));
            Assert.AreEqual("application/octet-stream", VisitorPortalLogic.ContentTypeFor("x.bin"), "許可していない拡張子は従来どおり");
        }

        [Test]
        public void JsonString_EscapesQuotesAndBackslashes()
        {
            Assert.AreEqual("\"a\\\"b\\\\c\"", VisitorPortalLogic.JsonString("a\"b\\c"));
            Assert.AreEqual("\"\"", VisitorPortalLogic.JsonString(null));
        }
    }
}
