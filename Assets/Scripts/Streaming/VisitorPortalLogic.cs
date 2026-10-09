#nullable enable
using System;
using System.Text;
using System.Text.RegularExpressions;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>タブレットの口（HTTP）の判断。</b>純ロジック — ソケットも Unity も触らない
    /// （<see cref="VisitorPortal"/> がスレッドで呼ぶ。テストは <c>VisitorPortalLogicTests</c>）。
    ///
    /// 受ける状態 API は 4 つだけ:
    ///   <c>GET /status</c>（この機の実値）／
    ///   <c>POST /set</c>（言語・軽減・ページ ID・起動 ID）／ <c>POST /clear</c>（ページ ID・起動 ID。枠を空にする）／
    ///   <c>POST /tablet/pulse</c>（タブレットの在席）。
    /// 旧 UI と素材の GET は 410 を返す。UI と素材はタブレットアプリが持つ。
    /// ⚠ JSON は自前で読む（JsonUtility をサーバのスレッドから呼ばない）。
    /// </summary>
    public static class VisitorPortalLogic
    {
        /// <summary>要求行とヘッダを読んだ結果。</summary>
        public struct Request
        {
            public string method;
            public string path;
            public int contentLength;
            public string origin;
        }

        /// <summary>返すもの。<see cref="Encode"/> がバイト列にする。</summary>
        public struct Response
        {
            public int status;
            public string contentType;
            public string body;
            // GET /status を許可済みの博士 UI から読ませるときだけ設定する。
            public string allowOrigin;
        }

        /// <summary>
        /// 要求の頭（<c>\r\n\r\n</c> まで）を読む。<b>読めなければ false</b>（壊れた要求は 400 へ）。
        /// path はクエリを落とし、Content-Length は無ければ 0。
        /// </summary>
        public static bool TryParseHead(string head, out Request req)
        {
            req = default;
            if (string.IsNullOrEmpty(head)) return false;
            string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] parts = lines[0].Split(' ');
            if (parts.Length < 2) return false;
            req.method = parts[0].ToUpperInvariant();
            string path = parts[1];
            int q = path.IndexOf('?');
            if (q >= 0) path = path.Substring(0, q);
            req.path = path.Length == 0 ? "/" : path;
            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c <= 0) continue;
                string name = lines[i].Substring(0, c).Trim();
                string value = lines[i].Substring(c + 1).Trim();
                if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(value, out req.contentLength);
                else if (string.Equals(name, "Origin", StringComparison.OrdinalIgnoreCase))
                    req.origin = value;
            }
            return true;
        }

        private static readonly Regex LangRx = new Regex("\"lang\"\\s*:\\s*\"([A-Za-z]{2})\"", RegexOptions.Compiled);
        private static readonly Regex ReliefRx = new Regex("\"relief\"\\s*:\\s*(true|false)", RegexOptions.Compiled);
        private static readonly Regex TabletIdRx = new Regex("\"tabletSessionId\"\\s*:\\s*\"([A-Za-z0-9_-]{1,128})\"", RegexOptions.Compiled);
        private static readonly Regex PortalIdRx = new Regex("\"portalSessionId\"\\s*:\\s*\"([A-Za-z0-9_-]{1,128})\"", RegexOptions.Compiled);
        private static readonly Regex SequenceRx = new Regex("\"seq\"\\s*:\\s*(0|[1-9][0-9]*)(?=\\s*[,}])", RegexOptions.Compiled);
        private static readonly Regex RevisionRx = new Regex("\"briefingRevision\"\\s*:\\s*([1-9][0-9]*)(?=\\s*[,}])", RegexOptions.Compiled);
        private static readonly Regex CompletedRx = new Regex("\"briefingCompleted\"\\s*:\\s*(true|false)(?=\\s*[,}])", RegexOptions.Compiled);
        private static readonly Regex StaffConfirmedRx = new Regex("\"staffConfirmed\"\\s*:\\s*(true|false)(?=\\s*[,}])", RegexOptions.Compiled);
        public struct PreparationPulse
        {
            public string tabletSessionId, portalSessionId;
            public int seq, revision;
            public bool completed, staffConfirmed;
        }

        public static bool TryParsePreparation(string body, out PreparationPulse pulse)
        {
            pulse = default;
            var portal = PortalIdRx.Match(body ?? ""); var seq = SequenceRx.Match(body ?? "");
            var revision = RevisionRx.Match(body ?? ""); var completed = CompletedRx.Match(body ?? "");
            if (!TryParseTabletId(body, out pulse.tabletSessionId) || !portal.Success
                || !seq.Success || !int.TryParse(seq.Groups[1].Value, out pulse.seq)
                || !revision.Success || !int.TryParse(revision.Groups[1].Value, out pulse.revision)
                || !completed.Success) return false;
            pulse.portalSessionId = portal.Groups[1].Value;
            pulse.completed = completed.Groups[1].Value == "true";
            var staff = StaffConfirmedRx.Match(body ?? "");
            if (body != null && body.Contains("staffConfirmed") && !staff.Success) return false;
            pulse.staffConfirmed = staff.Success && staff.Groups[1].Value == "true";
            if (pulse.staffConfirmed && !pulse.completed) return false;
            return pulse.seq > 0 || !pulse.completed;
        }

        /// <summary>
        /// <c>POST /set</c> の本文を読む。<b>lang が ja / en / fr でなければ false</b>（打ち間違いを黙って既定にしない）。
        /// relief は無ければ false。
        /// </summary>
        public static bool TryParseSet(string body, out ShowLang lang, out bool relief)
        {
            lang = ShowLanguage.Default;
            relief = false;
            if (string.IsNullOrEmpty(body)) return false;
            Match m = LangRx.Match(body);
            if (!m.Success) return false;
            string code = m.Groups[1].Value.ToLowerInvariant();
            bool known = false;
            foreach (ShowLang l in ShowLanguage.All)
                if (ShowLanguage.Code(l) == code) { lang = l; known = true; break; }
            if (!known) return false;
            Match r = ReliefRx.Match(body);
            relief = r.Success && r.Groups[1].Value == "true";
            return true;
        }

        public static bool TryParseTabletId(string body, out string tabletId)
        {
            Match match = TabletIdRx.Match(body ?? "");
            tabletId = match.Success ? match.Groups[1].Value : "";
            return match.Success;
        }

        /// <summary>
        /// 道を選ぶ。<paramref name="statusJson"/> はこの機の実値（メインスレッドが組んだもの）、
        /// <paramref name="onSet"/> / <paramref name="onClear"/> はメインスレッドへ積む口（戻り値は受理番号）。
        /// </summary>
        public static Response Route(Request req, string body, string statusJson,
                                     Func<ShowLang, bool, string, int> onSet, Action onClear,
                                     string portalSessionId = "", Action<string>? onPulse = null,
                                     Action<PreparationPulse>? onPreparation = null, Action<string, int>? onStaffReset = null,
                                     Action<int>? onClearVersioned = null)
        {
            string p = req.path;
            if (req.method == "GET" && (p == "/" || p == "/visitor.html" || p == "/index.html" || p.StartsWith("/asset/")))
                return Json(410, "{\"ok\":false,\"error\":\"tablet app serves visitor UI\"}");
            if (req.method == "GET" && p == "/status")
                return new Response
                {
                    status = 200,
                    contentType = "application/json; charset=utf-8",
                    body = statusJson,
                    allowOrigin = IsAllowedStatusOrigin(req.origin) ? req.origin : "",
                };
            if (req.method == "POST" && p == "/tablet/pulse")
            {
                if (!TryParseTabletId(body, out string tabletId))
                    return Json(400, "{\"ok\":false,\"error\":\"tabletSessionId required\"}");
                bool preparation = body != null && (body.Contains("briefingCompleted") || body.Contains("briefingRevision") || body.Contains("staffConfirmed"));
                if (body != null && body.Contains("staffReset"))
                {
                    var reset = Regex.Match(body, "\"staffReset\"\\s*:\\s*true(?=\\s*[,}])");
                    var requestId = Regex.Match(body, "\"resetRequestId\"\\s*:\\s*\"([A-Za-z0-9_-]{1,128})\"");
                    var resetPortal = PortalIdRx.Match(body);
                    var resetSeq = Regex.Match(body, "\"settingsSeq\"\\s*:\\s*(0|[1-9][0-9]*)(?=\\s*[,}])");
                    int expectedSeq;
                    if (!reset.Success || !requestId.Success || !resetPortal.Success || preparation
                        || !resetSeq.Success || !int.TryParse(resetSeq.Groups[1].Value, out expectedSeq))
                        return Json(400, "{\"ok\":false}");
                    if (resetPortal.Groups[1].Value != portalSessionId) return Json(409, "{\"ok\":false}");
                    onPulse?.Invoke(tabletId);
                    onStaffReset?.Invoke(requestId.Groups[1].Value, expectedSeq);
                    return Json(200, "{\"ok\":true}");
                }
                PreparationPulse pulse = default;
                if (preparation && !TryParsePreparation(body!, out pulse))
                    return Json(400, "{\"ok\":false,\"error\":\"invalid preparation pulse\"}");
                if (preparation && pulse.portalSessionId != portalSessionId)
                    return Json(409, "{\"ok\":false,\"error\":\"portal session changed\"}");
                onPulse?.Invoke(tabletId);
                if (preparation) onPreparation?.Invoke(pulse);
                return Json(200, "{\"ok\":true}");
            }
            if (req.method == "POST" && p == "/set")
            {
                if (!TryParseSet(body, out ShowLang lang, out bool relief))
                    return Json(400, "{\"ok\":false,\"error\":\"lang は ja / en / fr\"}");
                bool hasTablet = TryParseTabletId(body, out string tabletId);
                Match portalMatch = PortalIdRx.Match(body ?? "");
                if (!hasTablet || !portalMatch.Success)
                    return Json(400, "{\"ok\":false,\"error\":\"session ids required\"}");
                if (portalMatch.Groups[1].Value != portalSessionId)
                    return Json(409, "{\"ok\":false,\"error\":\"portal session changed\"}");
                int seq = onSet(lang, relief, tabletId);
                return Json(200, "{\"ok\":true,\"seq\":" + seq + "}");
            }
            if (req.method == "POST" && p == "/clear")
            {
                bool hasTablet = TryParseTabletId(body, out _);
                Match portalMatch = PortalIdRx.Match(body ?? "");
                if (!hasTablet || !portalMatch.Success)
                    return Json(400, "{\"ok\":false,\"error\":\"session ids required\"}");
                if (portalMatch.Groups[1].Value != portalSessionId)
                    return Json(409, "{\"ok\":false,\"error\":\"portal session changed\"}");
                if (body != null && body.Contains("\"seq\""))
                {
                    var clearSeq = SequenceRx.Match(body);
                    if (!clearSeq.Success || !int.TryParse(clearSeq.Groups[1].Value, out int expectedSeq) || expectedSeq <= 0)
                        return Json(400, "{\"ok\":false,\"error\":\"invalid clear sequence\"}");
                    if (onClearVersioned != null) onClearVersioned(expectedSeq); else onClear();
                }
                else onClear();
                return Json(200, "{\"ok\":true}");
            }
            return Json(404, "{\"ok\":false,\"error\":\"no such path\"}");
        }

        private static Response Json(int status, string body)
            => new Response { status = status, contentType = "application/json; charset=utf-8", body = body };

        private static bool IsAllowedStatusOrigin(string origin)
            => origin == "http://192.168.10.31:8090" || origin == "http://192.168.10.32:8090";

        /// <summary>HTTP/1.1 の応答をバイト列に。常に <c>Connection: close</c>（1 要求 1 接続）。</summary>
        public static byte[] Encode(Response r)
        {
            byte[] body = Encoding.UTF8.GetBytes(r.body ?? "");
            string reason = r.status switch { 200 => "OK", 400 => "Bad Request", 404 => "Not Found", 409 => "Conflict", 410 => "Gone", _ => "Error" };
            var sb = new StringBuilder(256);
            sb.Append("HTTP/1.1 ").Append(r.status).Append(' ').Append(reason).Append("\r\n");
            sb.Append("Content-Type: ").Append(r.contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            if (!string.IsNullOrEmpty(r.allowOrigin))
            {
                sb.Append("Access-Control-Allow-Origin: ").Append(r.allowOrigin).Append("\r\n");
                sb.Append("Vary: Origin\r\n");
            }
            sb.Append("Connection: close\r\n\r\n");
            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            byte[] all = new byte[head.Length + body.Length];
            Buffer.BlockCopy(head, 0, all, 0, head.Length);
            Buffer.BlockCopy(body, 0, all, head.Length, body.Length);
            return all;
        }

        /// <summary>JSON の文字列の中身をエスケープする（<c>"</c> と <c>\</c> と制御文字だけ）。</summary>
        public static string JsonString(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            var sb = new StringBuilder(s!.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < 0x20) sb.Append(' ');
                else sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
