#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>タブレットが直接繋ぐ口。</b>この機の中で小さな HTTP サーバを立て、
    /// 「体験の前に」の面（<c>Resources/Visitor/visitor.html</c>）を配り、言語・軽減の選択を受ける
    /// （2026-09-11・<c>canon/LEDGER.md</c> 0187「メインのウェブ卓は経由せずにクエストとタブレットを直接つなぐ」）。
    ///
    /// タブレットのブラウザで <c>http://&lt;この機の IP&gt;:8090/</c> を開く。**PC も卓も要らない。**
    /// α 用のタブレットは α の機の IP（現地の静的割当 .31 / .32・<c>docs/onsite/network-setup.md</c>）を
    /// ブックマークしておく ＝ 役の結び付けは「どの URL を開くか」で決まり、卓に台帳を持たない。
    ///
    /// 流れ:
    ///   面 → <c>POST /set</c> → <see cref="VisitorPrefs.Set"/>（メインスレッドへ積む）
    ///   → 注意書きの段で <c>TitleScreen</c> が書く → 面は <c>GET /status</c> で**実値**を読んで ✓ を出す。
    ///
    /// ⚠ ソケットは背景スレッド、Unity と static を触るのは <see cref="Update"/> だけ（キューで渡す）。
    /// ⚠ 判断は <see cref="VisitorPortalLogic"/>（純ロジック・テスト付き）。ここは配線だけ。
    /// ⚠ シーンには焼かない。<c>ShowControlClient.EnsureVisitorPortal</c> が起動時に生成する
    ///   （<c>SegmentRecorder</c> と同じ流儀 ＝ <c>menu scene</c> の焼き直しが要らない）。
    /// </summary>
    public sealed class VisitorPortal : MonoBehaviour
    {
        /// <summary>待ち受けるポート。配信スマホ 8080 / 卓 8099 / 発見 8830 と被らない。</summary>
        public const int Port = 8090;

        /// <summary>面の置き場（<c>Resources.Load</c> のパス。実体は <c>Assets/Resources/Visitor/visitor.html</c>）。</summary>
        public const string PageResource = "Visitor/visitor";

        private const int ReadTimeoutMs = 3000;
        private const int MaxHeadBytes = 16 * 1024;
        private const int MaxBodyBytes = 16 * 1024;

        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private string _page = "";
        private volatile string _statusJson = "{\"ok\":false}";
        private readonly object _queueLock = new object();
        private readonly List<Action> _queue = new List<Action>();
        private int _received;   // POST /set を受けた累計（入力の累計。work-style §2-2）
        private int _rejected;   // 読めない本文を返した累計
        private string _model = "";
        private string _ip = "";
        private TitleScreen? _title;
        private ShowRunDirector? _run;
        private string _lastKey = "";

        /// <summary>待ち受けに成功しているか（bind に失敗すると false ＝ タブレットは繋げない）。</summary>
        public bool IsListening { get; private set; }

        /// <summary>この機の IPv4（取れなければ空）。ログと面に出す。</summary>
        public string LocalIp => _ip;

        /// <summary><c>POST /set</c> を受けた累計。テレメトリが読む。</summary>
        public int Received => _received;

        private void Start()
        {
            _model = SystemInfo.deviceModel ?? "";
            var ta = Resources.Load<TextAsset>(PageResource);
            if (ta == null)
            {
                Debug.LogWarning($"[VisitorPortal] 面が無い（Resources/{PageResource}）。タブレットには空の面が出る");
                _page = "<!doctype html><meta charset=utf-8><title>visitor</title><p>visitor.html が APK に入っていません</p>";
            }
            else _page = ta.text;

            _ip = FindLocalIPv4();
            try
            {
                _listener = new TcpListener(IPAddress.Any, Port);
                _listener.Start();
                _cts = new CancellationTokenSource();
                IsListening = true;
                Task.Run(() => AcceptLoop(_listener, _cts.Token));
                Debug.Log($"[VisitorPortal] タブレットの口を開けた: http://{(_ip.Length > 0 ? _ip : "<この機のIP>")}:{Port}/");
            }
            catch (Exception e)
            {
                IsListening = false;
                Debug.LogWarning($"[VisitorPortal] :{Port} を開けなかった（タブレットは繋げない）: {e.Message}");
            }
            RefreshStatus(force: true);
        }

        private void OnDestroy()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            IsListening = false;
        }

        private void Update()
        {
            // 背景スレッドから積まれた要求をここで実行する（static と Unity を触るのはメインだけ）。
            if (_queue.Count > 0)
            {
                Action[] batch;
                lock (_queueLock) { batch = _queue.ToArray(); _queue.Clear(); }
                foreach (var a in batch) a();
            }
            RefreshStatus(force: false);
        }

        // ---- 状態（メインスレッドで組み、サーバのスレッドはそのまま返す）------------------------
        private void RefreshStatus(bool force)
        {
            if (_title == null) _title = FindObjectOfType<TitleScreen>();
            if (_run == null) _run = FindObjectOfType<ShowRunDirector>();
            string stage = _title != null ? _title.Stage.ToString() : "";
            string phase = _run != null ? PhaseCode(_run.Phase) : "";
            string lang = ShowLanguage.Code(ShowLanguage.Current);
            bool relief = HorrorRelief.Enabled;
            // 変わったフレームだけ組み直す（90Hz で文字列を作らない）。
            string key = stage + "|" + phase + "|" + lang + "|" + (relief ? 1 : 0) + "|" + VisitorPrefs.PendingSeq
                         + "|" + VisitorPrefs.AppliedSeq + "|" + _received + "|" + _rejected;
            if (!force && key == _lastKey) return;
            _lastKey = key;

            var sb = new StringBuilder(320);
            sb.Append("{\"ok\":true");
            sb.Append(",\"lang\":\"").Append(lang).Append('"');
            sb.Append(",\"relief\":").Append(relief ? "true" : "false");
            sb.Append(",\"titleStage\":").Append(VisitorPortalLogic.JsonString(stage));
            sb.Append(",\"phase\":").Append(VisitorPortalLogic.JsonString(phase));
            if (VisitorPrefs.HasPending)
                sb.Append(",\"pending\":{\"lang\":\"").Append(ShowLanguage.Code(VisitorPrefs.PendingLang))
                  .Append("\",\"relief\":").Append(VisitorPrefs.PendingRelief ? "true" : "false")
                  .Append(",\"seq\":").Append(VisitorPrefs.PendingSeq).Append('}');
            else sb.Append(",\"pending\":null");
            sb.Append(",\"appliedSeq\":").Append(VisitorPrefs.AppliedSeq);
            sb.Append(",\"applyCount\":").Append(VisitorPrefs.ApplyCount);
            sb.Append(",\"received\":").Append(_received);
            sb.Append(",\"rejected\":").Append(_rejected);
            sb.Append(",\"model\":").Append(VisitorPortalLogic.JsonString(_model));
            sb.Append(",\"ip\":").Append(VisitorPortalLogic.JsonString(_ip));
            sb.Append(",\"port\":").Append(Port);
            sb.Append('}');
            _statusJson = sb.ToString();
        }

        private static string PhaseCode(ShowPhase p)
            => p == ShowPhase.Intro ? "INTRO" : (p == ShowPhase.Finished ? "END" : "RUN");

        // ---- サーバ（背景スレッド）---------------------------------------------------------
        private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); }
                catch (ObjectDisposedException) { return; }
                catch (Exception) { if (ct.IsCancellationRequested) return; continue; }
                _ = Task.Run(() => Handle(client));
            }
        }

        private void Handle(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = ReadTimeoutMs;
                    client.SendTimeout = ReadTimeoutMs;
                    client.NoDelay = true;
                    NetworkStream ns = client.GetStream();
                    byte[] buf = new byte[MaxHeadBytes];
                    int len = 0;
                    int headEnd = -1;
                    while (headEnd < 0 && len < buf.Length)
                    {
                        int n = ns.Read(buf, len, buf.Length - len);
                        if (n <= 0) break;
                        len += n;
                        headEnd = IndexOfHeadEnd(buf, len);
                    }
                    VisitorPortalLogic.Response res;
                    if (headEnd < 0 || !VisitorPortalLogic.TryParseHead(Encoding.ASCII.GetString(buf, 0, headEnd), out var req))
                        res = new VisitorPortalLogic.Response { status = 400, contentType = "text/plain", body = "bad request" };
                    else
                    {
                        int bodyStart = headEnd + 4;
                        int want = Math.Min(Math.Max(req.contentLength, 0), MaxBodyBytes);
                        byte[] body = new byte[want];
                        int have = Math.Min(len - bodyStart, want);
                        if (have > 0) Buffer.BlockCopy(buf, bodyStart, body, 0, have);
                        while (have < want)
                        {
                            int n = ns.Read(body, have, want - have);
                            if (n <= 0) break;
                            have += n;
                        }
                        res = VisitorPortalLogic.Route(req, Encoding.UTF8.GetString(body, 0, have), _page, _statusJson,
                                                       OnSetFromThread, OnClearFromThread);
                        if (res.status == 400 && req.path == "/set") Interlocked.Increment(ref _rejected);
                    }
                    byte[] bytes = VisitorPortalLogic.Encode(res);
                    ns.Write(bytes, 0, bytes.Length);
                    ns.Flush();
                }
                catch (Exception) { /* 1 接続の失敗は他へ波及させない */ }
            }
        }

        private static int IndexOfHeadEnd(byte[] buf, int len)
        {
            for (int i = 3; i < len; i++)
                if (buf[i - 3] == '\r' && buf[i - 2] == '\n' && buf[i - 1] == '\r' && buf[i] == '\n') return i - 3;
            return -1;
        }

        // 受理番号はここで振る（応答に載せるため）。static へ書くのはメインスレッド。
        private int _seqIssued;
        private int OnSetFromThread(ShowLang lang, bool relief)
        {
            int seq = Interlocked.Increment(ref _seqIssued);
            Interlocked.Increment(ref _received);
            lock (_queueLock) _queue.Add(() =>
            {
                VisitorPrefs.Set(lang, relief, seq);
                Debug.Log($"[VisitorPortal] タブレットから受けた: lang={ShowLanguage.Code(lang)} relief={relief} seq={seq}");
            });
            return seq;
        }

        private void OnClearFromThread()
        {
            lock (_queueLock) _queue.Add(() =>
            {
                VisitorPrefs.Clear();
                Debug.Log("[VisitorPortal] 枠を空にした（スタッフ）");
            });
        }

        private static string FindLocalIPv4()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(ua.Address)) continue;
                        return ua.Address.ToString();
                    }
                }
            }
            catch (Exception) { }
            return "";
        }
    }
}
