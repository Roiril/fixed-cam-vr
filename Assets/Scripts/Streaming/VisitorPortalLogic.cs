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
    /// 受けるのは 5 つだけ:
    ///   <c>GET /</c>（面）／ <c>GET /status</c>（この機の実値）／
    ///   <c>POST /set</c>（言語・軽減・ページ ID・起動 ID）／ <c>POST /clear</c>（ページ ID・起動 ID。枠を空にする）／
    ///   <c>GET /asset/&lt;name&gt;</c>（面が使う画像・動画。<c>Resources/Visitor/&lt;name&gt;.bytes</c>。Range 対応）。
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
            // Range: bytes=a-b（無ければ rangeStart = -1）。動画の再生はブラウザが必ずこれを投げる。
            public long rangeStart;
            public long rangeEnd;
        }

        /// <summary>返すもの。<see cref="Encode"/> がバイト列にする。</summary>
        public struct Response
        {
            public int status;
            public string contentType;
            public string body;
            // 二進の応答（画像・動画）。bytes が非 null なら body は使わない。
            public byte[]? bytes;
            public int bytesOffset;
            public int bytesCount;
            public long totalLength;   // 206 のとき Content-Range に要る
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
            req.rangeStart = -1;
            req.rangeEnd = -1;
            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c <= 0) continue;
                string name = lines[i].Substring(0, c).Trim();
                string value = lines[i].Substring(c + 1).Trim();
                if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(value, out req.contentLength);
                else if (string.Equals(name, "Range", StringComparison.OrdinalIgnoreCase) && value.StartsWith("bytes="))
                {
                    string[] se = value.Substring(6).Split('-');
                    if (se.Length == 2)
                    {
                        if (se[0].Length > 0 && long.TryParse(se[0], out long a)) req.rangeStart = a;
                        if (se[1].Length > 0 && long.TryParse(se[1], out long b)) req.rangeEnd = b;
                        if (req.rangeStart < 0 && req.rangeEnd < 0) { /* 読めない Range は無視 */ }
                        else if (req.rangeStart < 0) req.rangeStart = 0;   // "bytes=-500"（末尾）は使わないので頭から
                    }
                }
            }
            return true;
        }

        private static readonly Regex LangRx = new Regex("\"lang\"\\s*:\\s*\"([A-Za-z]{2})\"", RegexOptions.Compiled);
        private static readonly Regex ReliefRx = new Regex("\"relief\"\\s*:\\s*(true|false)", RegexOptions.Compiled);
        private static readonly Regex TabletIdRx = new Regex("\"tabletSessionId\"\\s*:\\s*\"([A-Za-z0-9_-]{1,128})\"", RegexOptions.Compiled);
        private static readonly Regex PortalIdRx = new Regex("\"portalSessionId\"\\s*:\\s*\"([A-Za-z0-9_-]{1,128})\"", RegexOptions.Compiled);

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
        public static Response Route(Request req, string body, string page, string statusJson,
                                     Func<ShowLang, bool, string, int> onSet, Action onClear,
                                     Func<string, byte[]?>? asset = null, string portalSessionId = "",
                                     Action<string>? onPulse = null)
        {
            string p = req.path;
            if (req.method == "GET" && p.StartsWith("/asset/"))
            {
                string name = p.Substring(7);
                // 名前は英数字・点・下線・ハイフンだけ（Resources の外へは出られないが、念のため）
                foreach (char ch in name)
                    if (!(char.IsLetterOrDigit(ch) || ch == '.' || ch == '_' || ch == '-'))
                        return Json(404, "{\"ok\":false,\"error\":\"bad asset name\"}");
                byte[]? data = asset != null && name.Length > 0 ? asset(name) : null;
                if (data == null) return Json(404, "{\"ok\":false,\"error\":\"no such asset\"}");
                return Binary(data, ContentTypeFor(name), req);
            }
            if (req.method == "GET" && (p == "/" || p == "/visitor.html" || p == "/index.html"))
                return new Response { status = 200, contentType = "text/html; charset=utf-8", body = page };
            if (req.method == "GET" && p == "/status")
                return new Response { status = 200, contentType = "application/json; charset=utf-8", body = statusJson };
            if (req.method == "POST" && p == "/tablet/pulse")
            {
                if (!TryParseTabletId(body, out string tabletId))
                    return Json(400, "{\"ok\":false,\"error\":\"tabletSessionId required\"}");
                onPulse?.Invoke(tabletId);
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
                onClear();
                return Json(200, "{\"ok\":true}");
            }
            return Json(404, "{\"ok\":false,\"error\":\"no such path\"}");
        }

        private static Response Json(int status, string body)
            => new Response { status = status, contentType = "application/json; charset=utf-8", body = body };

        /// <summary>二進の応答。Range があれば 206 で部分を返す（動画の再生に要る）。</summary>
        public static Response Binary(byte[] data, string contentType, Request req)
        {
            long total = data.Length;
            if (req.rangeStart >= 0 && req.rangeStart < total)
            {
                long end = req.rangeEnd < 0 || req.rangeEnd >= total ? total - 1 : req.rangeEnd;
                if (end < req.rangeStart) end = req.rangeStart;
                return new Response
                {
                    status = 206, contentType = contentType, body = "", bytes = data,
                    bytesOffset = (int)req.rangeStart, bytesCount = (int)(end - req.rangeStart + 1), totalLength = total,
                };
            }
            return new Response { status = 200, contentType = contentType, body = "", bytes = data, bytesOffset = 0, bytesCount = data.Length, totalLength = total };
        }

        /// <summary>拡張子から Content-Type（面が使う種類だけ）。</summary>
        public static string ContentTypeFor(string name)
        {
            string n = name.ToLowerInvariant();
            if (n.EndsWith(".json")) return "application/json; charset=utf-8";
            if (n.EndsWith(".jpg") || n.EndsWith(".jpeg")) return "image/jpeg";
            if (n.EndsWith(".png")) return "image/png";
            if (n.EndsWith(".webp")) return "image/webp";
            if (n.EndsWith(".mp4")) return "video/mp4";
            if (n.EndsWith(".webm")) return "video/webm";
            if (n.EndsWith(".mp3")) return "audio/mpeg";
            if (n.EndsWith(".wav")) return "audio/wav";
            if (n.EndsWith(".ogg")) return "audio/ogg";
            return "application/octet-stream";
        }

        /// <summary>HTTP/1.1 の応答をバイト列に。常に <c>Connection: close</c>（1 要求 1 接続）。</summary>
        public static byte[] Encode(Response r)
        {
            bool bin = r.bytes != null;
            byte[] body = bin ? r.bytes! : Encoding.UTF8.GetBytes(r.body ?? "");
            int off = bin ? r.bytesOffset : 0;
            int cnt = bin ? r.bytesCount : body.Length;
            string reason = r.status switch { 200 => "OK", 206 => "Partial Content", 400 => "Bad Request", 404 => "Not Found", 409 => "Conflict", _ => "Error" };
            var sb = new StringBuilder(256);
            sb.Append("HTTP/1.1 ").Append(r.status).Append(' ').Append(reason).Append("\r\n");
            sb.Append("Content-Type: ").Append(r.contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(cnt).Append("\r\n");
            if (bin)
            {
                // 二進は動かないので短くキャッシュさせる（面を開き直すたびに数百 KB を送らない）。
                sb.Append("Cache-Control: max-age=600\r\n");
                sb.Append("Accept-Ranges: bytes\r\n");
                if (r.status == 206)
                    sb.Append("Content-Range: bytes ").Append(off).Append('-').Append(off + cnt - 1).Append('/').Append(r.totalLength).Append("\r\n");
            }
            else sb.Append("Cache-Control: no-store\r\n");
            sb.Append("Connection: close\r\n\r\n");
            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            byte[] all = new byte[head.Length + cnt];
            Buffer.BlockCopy(head, 0, all, 0, head.Length);
            Buffer.BlockCopy(body, off, all, head.Length, cnt);
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
