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
    /// 受けるのは 4 つだけ:
    ///   <c>GET /</c>（面）／ <c>GET /status</c>（この機の実値）／
    ///   <c>POST /set</c>（<c>{"lang":"en","relief":true}</c>）／ <c>POST /clear</c>（枠を空にする）。
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
        }

        /// <summary>返すもの。<see cref="Encode"/> がバイト列にする。</summary>
        public struct Response
        {
            public int status;
            public string contentType;
            public string body;
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
                if (!string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                int.TryParse(lines[i].Substring(c + 1).Trim(), out req.contentLength);
            }
            return true;
        }

        private static readonly Regex LangRx = new Regex("\"lang\"\\s*:\\s*\"([A-Za-z]{2})\"", RegexOptions.Compiled);
        private static readonly Regex ReliefRx = new Regex("\"relief\"\\s*:\\s*(true|false)", RegexOptions.Compiled);

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

        /// <summary>
        /// 道を選ぶ。<paramref name="statusJson"/> はこの機の実値（メインスレッドが組んだもの）、
        /// <paramref name="onSet"/> / <paramref name="onClear"/> はメインスレッドへ積む口（戻り値は受理番号）。
        /// </summary>
        public static Response Route(Request req, string body, string page, string statusJson,
                                     Func<ShowLang, bool, int> onSet, Action onClear)
        {
            string p = req.path;
            if (req.method == "GET" && (p == "/" || p == "/visitor.html" || p == "/index.html"))
                return new Response { status = 200, contentType = "text/html; charset=utf-8", body = page };
            if (req.method == "GET" && p == "/status")
                return new Response { status = 200, contentType = "application/json; charset=utf-8", body = statusJson };
            if (req.method == "POST" && p == "/set")
            {
                if (!TryParseSet(body, out ShowLang lang, out bool relief))
                    return Json(400, "{\"ok\":false,\"error\":\"lang は ja / en / fr\"}");
                int seq = onSet(lang, relief);
                return Json(200, "{\"ok\":true,\"seq\":" + seq + "}");
            }
            if (req.method == "POST" && p == "/clear")
            {
                onClear();
                return Json(200, "{\"ok\":true}");
            }
            return Json(404, "{\"ok\":false,\"error\":\"no such path\"}");
        }

        private static Response Json(int status, string body)
            => new Response { status = status, contentType = "application/json; charset=utf-8", body = body };

        /// <summary>HTTP/1.1 の応答をバイト列に。常に <c>Connection: close</c>（1 要求 1 接続）。</summary>
        public static byte[] Encode(Response r)
        {
            byte[] body = Encoding.UTF8.GetBytes(r.body ?? "");
            string reason = r.status switch { 200 => "OK", 400 => "Bad Request", 404 => "Not Found", _ => "Error" };
            var sb = new StringBuilder(256);
            sb.Append("HTTP/1.1 ").Append(r.status).Append(' ').Append(reason).Append("\r\n");
            sb.Append("Content-Type: ").Append(r.contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            sb.Append("Cache-Control: no-store\r\n");
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
