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
            var res = VisitorPortalLogic.Route(req, body, "{\"ok\":true,\"lang\":\"ja\"}",
                (lang, relief, tabletId) => { sets++; l = lang; r = relief; return 7; },
                () => clears++, portalSessionId: "portal-a");
            setCalls = sets; lastLang = l; lastRelief = r; clearCalls = clears;
            return res;
        }

        [Test]
        public void ParseHead_ReadsMethodPathAndContentLength()
        {
            string head = "POST /set?x=1 HTTP/1.1\r\nHost: 192.168.10.31:8090\r\ncontent-length: 27\r\noRiGiN: http://192.168.10.31:8090\r\nAccept: */*";
            Assert.IsTrue(VisitorPortalLogic.TryParseHead(head, out var req));
            Assert.AreEqual("POST", req.method);
            Assert.AreEqual("/set", req.path, "クエリを落とす");
            Assert.AreEqual(27, req.contentLength, "ヘッダ名は大文字小文字を見ない");
            Assert.AreEqual("http://192.168.10.31:8090", req.origin, "Origin のヘッダ名も大文字小文字を見ない");
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

        [TestCase("/")]
        [TestCase("/visitor.html")]
        [TestCase("/index.html")]
        [TestCase("/asset/doctor.jpg")]
        public void Get_LegacyUiPaths_Return410(string path)
        {
            var res = Route("GET", path, "", out int sets, out _, out _, out int clears);
            Assert.AreEqual(410, res.status);
            Assert.AreEqual("{\"ok\":false,\"error\":\"tablet app serves visitor UI\"}", res.body);
            Assert.AreEqual("application/json; charset=utf-8", res.contentType);
            Assert.AreEqual(0, sets); Assert.AreEqual(0, clears);
            StringAssert.StartsWith("HTTP/1.1 410 Gone\r\n", Encoding.UTF8.GetString(VisitorPortalLogic.Encode(res)));
        }

        [Test]
        public void Get_Status_ReturnsTheMainThreadSnapshot()
        {
            var res = Route("GET", "/status", "", out _, out _, out _, out _);
            Assert.AreEqual(200, res.status);
            Assert.AreEqual("{\"ok\":true,\"lang\":\"ja\"}", res.body);
        }

        [TestCase("http://192.168.10.31:8090")]
        [TestCase("http://192.168.10.32:8090")]
        public void Get_Status_AllowsTheTwoDoctorOrigins(string origin)
        {
            var req = new VisitorPortalLogic.Request { method = "GET", path = "/status", origin = origin };
            var res = VisitorPortalLogic.Route(req, "", "{}", (l, r, id) => 1, () => { });
            Assert.AreEqual(origin, res.allowOrigin);

            string encoded = Encoding.UTF8.GetString(VisitorPortalLogic.Encode(res));
            StringAssert.Contains("Access-Control-Allow-Origin: " + origin + "\r\n", encoded);
            StringAssert.Contains("Vary: Origin\r\n", encoded);
        }

        [TestCase("http://evil.example:8090")]
        [TestCase("null")]
        [TestCase("http://192.168.10.31:8090.evil.example")]
        [TestCase("http://192.168.10.31:80900")]
        public void Get_Status_RejectsUnknownAndSimilarOrigins(string origin)
        {
            var req = new VisitorPortalLogic.Request { method = "GET", path = "/status", origin = origin };
            var res = VisitorPortalLogic.Route(req, "", "{}", (l, r, id) => 1, () => { });
            Assert.IsTrue(string.IsNullOrEmpty(res.allowOrigin));
            string encoded = Encoding.UTF8.GetString(VisitorPortalLogic.Encode(res));
            StringAssert.DoesNotContain("Access-Control-Allow-Origin", encoded);
            StringAssert.DoesNotContain("Vary: Origin", encoded);
        }

        [TestCase("/set", "{\"lang\":\"ja\",\"tabletSessionId\":\"page-a\",\"portalSessionId\":\"portal-a\"}")]
        [TestCase("/clear", "{\"tabletSessionId\":\"page-a\",\"portalSessionId\":\"portal-a\"}")]
        [TestCase("/tablet/pulse", "{\"tabletSessionId\":\"page-a\"}")]
        public void CorsHeaders_AreNotAddedToPostEndpoints(string path, string body)
        {
            const string origin = "http://192.168.10.31:8090";
            var post = new VisitorPortalLogic.Request { method = "POST", path = path, origin = origin };
            var postRes = VisitorPortalLogic.Route(post, body, "{}",
                (l, r, id) => 1, () => { }, portalSessionId: "portal-a");
            Assert.AreEqual(200, postRes.status);
            string postEncoded = Encoding.UTF8.GetString(VisitorPortalLogic.Encode(postRes));
            StringAssert.DoesNotContain("Access-Control-Allow-Origin", postEncoded);
            StringAssert.DoesNotContain("Vary: Origin", postEncoded);
        }

        [Test]
        public void CorsHeaders_AreNotAddedToRetiredAssets()
        {
            const string origin = "http://192.168.10.31:8090";
            var asset = new VisitorPortalLogic.Request { method = "GET", path = "/asset/doctor.jpg", origin = origin };
            var assetRes = VisitorPortalLogic.Route(asset, "", "{}", (l, r, id) => 1, () => { });
            string assetEncoded = Encoding.UTF8.GetString(VisitorPortalLogic.Encode(assetRes));
            StringAssert.DoesNotContain("Access-Control-Allow-Origin", assetEncoded);
            StringAssert.DoesNotContain("Vary: Origin", assetEncoded);
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
                "{}", (lang, relief, id) => { tablet = id; return 1; }, () => { },
                portalSessionId: "current");
            Assert.AreEqual(409, old.status);
            Assert.AreEqual("", tablet);

            var current = VisitorPortalLogic.Route(req,
                "{\"lang\":\"ja\",\"tabletSessionId\":\"page-a\",\"portalSessionId\":\"current\"}",
                "{}", (lang, relief, id) => { tablet = id; return 2; }, () => { },
                portalSessionId: "current");
            Assert.AreEqual(200, current.status);
            Assert.AreEqual("page-a", tablet);
        }

        [Test]
        public void Post_Pulse_RequiresTabletId()
        {
            string tablet = "";
            var req = new VisitorPortalLogic.Request { method = "POST", path = "/tablet/pulse" };
            Assert.AreEqual(400, VisitorPortalLogic.Route(req, "{}", "{}",
                (lang, relief, id) => 1, () => { }, onPulse: id => tablet = id).status);
            Assert.AreEqual(200, VisitorPortalLogic.Route(req, "{\"tabletSessionId\":\"page-a\"}", "{}",
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
                "{}", (lang, relief, id) => 1, () => clears++, portalSessionId: "current");
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

        [Test]
        public void JsonString_EscapesQuotesAndBackslashes()
        {
            Assert.AreEqual("\"a\\\"b\\\\c\"", VisitorPortalLogic.JsonString("a\"b\\c"));
            Assert.AreEqual("\"\"", VisitorPortalLogic.JsonString(null));
        }
    }
}
