#nullable enable
using System;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
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
    /// <b>タブレットが直接繋ぐ状態 API。</b>この機の中で小さな HTTP サーバを立て、
    /// タブレットアプリから言語・軽減の選択を受ける
    /// （2026-09-11・<c>canon/LEDGER.md</c> 0187「メインのウェブ卓は経由せずにクエストとタブレットを直接つなぐ」）。
    ///
    /// タブレットアプリはスタッフ設定で α / β を選び、現地の静的割当 .31 / .32 の状態 API へ直接繋ぐ。
    /// 設定送信と反映確認は選んだ機に対して行い、卓に台帳を持たない。
    ///
    /// 流れ:
    ///   タブレットアプリ → <c>POST /set</c> → <see cref="VisitorPrefs.Set"/>（メインスレッドへ積む）
    ///   → 注意書きの段で <c>TitleScreen</c> が書く → <c>GET /status</c> で実値を返す。
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

        private const int ReadTimeoutMs = 3000;
        private const int MaxHeadBytes = 16 * 1024;
        private const int MaxBodyBytes = 16 * 1024;

        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private volatile string _statusJson = "{\"ok\":false}";
        private readonly object _queueLock = new object();
        private readonly List<Action> _queue = new List<Action>();
        private volatile int _received;   // POST /set を受けた累計（入力の累計。work-style §2-2）
        private int _rejected;   // 読めない本文を返した累計
        private string _model = "";
        private string _ip = "";
        private TitleScreen? _title;
        private ShowRunDirector? _run;
        private string _lastKey = "";
        private string _portalSessionId = "";
        private readonly List<TabletSeen> _tablets = new List<TabletSeen>();
        private LastRequest? _lastRequest;

        [Serializable] public sealed class TabletSeen
        {
            public string tabletSessionId = "";
            public string ip = "";
            public float ageSec;
            [NonSerialized] public long seenAt;
        }

        [Serializable] public sealed class LastRequest
        {
            public string tabletSessionId = "";
            public int seq;
            public string lang = "";
            public bool relief;
        }

        [Serializable] public sealed class HeartbeatSnapshot
        {
            public int schema = 1;
            public bool listening;
            public string portalSessionId = "";
            public TabletSeen[] tablets = Array.Empty<TabletSeen>();
            public LastRequest? lastRequest;
            public int appliedSeq;
            public int applyCount;
            public int received;
            public string lang = "";
            public bool relief;
            public int pendingSeq;
            public int consumedSeq;
        }

        /// <summary>待ち受けに成功しているか（bind に失敗すると false ＝ タブレットは繋げない）。</summary>
        public bool IsListening { get; private set; }

        /// <summary>この機の IPv4（取れなければ空）。ログと面に出す。</summary>
        public string LocalIp => _ip;

        /// <summary><c>POST /set</c> を受けた累計。テレメトリが読む。</summary>
        public int Received => _received;

        public HeartbeatSnapshot CreateHeartbeatSnapshot()
        {
            long now = Stopwatch.GetTimestamp();
            var tablets = new TabletSeen[_tablets.Count];
            for (int i = 0; i < _tablets.Count; i++)
            {
                TabletSeen seen = _tablets[i];
                tablets[i] = new TabletSeen
                {
                    tabletSessionId = seen.tabletSessionId,
                    ip = seen.ip,
                    ageSec = (float)((now - seen.seenAt) / (double)Stopwatch.Frequency),
                };
            }
            return new HeartbeatSnapshot
            {
                listening = IsListening,
                portalSessionId = _portalSessionId,
                tablets = tablets,
                lastRequest = _lastRequest,
                appliedSeq = VisitorPrefs.AppliedSeq,
                applyCount = VisitorPrefs.ApplyCount,
                received = _received,
                lang = ShowLanguage.Code(ShowLanguage.Current),
                relief = HorrorRelief.Enabled,
                pendingSeq = VisitorPrefs.PendingSeq,
                consumedSeq = VisitorPrefs.ConsumedSeq,
            };
        }

        private void Awake()
        {
            _portalSessionId = Guid.NewGuid().ToString("N");
        }

        private void Start()
        {
            _model = SystemInfo.deviceModel ?? "";
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
            Action[] batch;
            lock (_queueLock) { batch = _queue.ToArray(); _queue.Clear(); }
            foreach (var a in batch) a();
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
                         + "|" + VisitorPrefs.AppliedSeq + "|" + VisitorPrefs.ApplyCount + "|" + _received + "|" + _rejected;
            if (!force && key == _lastKey) return;
            _lastKey = key;

            var sb = new StringBuilder(320);
            sb.Append("{\"ok\":true");
            sb.Append(",\"portalSessionId\":").Append(VisitorPortalLogic.JsonString(_portalSessionId));
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
            if (_lastRequest != null)
                sb.Append(",\"lastRequest\":{\"tabletSessionId\":")
                  .Append(VisitorPortalLogic.JsonString(_lastRequest.tabletSessionId))
                  .Append(",\"seq\":").Append(_lastRequest.seq)
                  .Append(",\"lang\":").Append(VisitorPortalLogic.JsonString(_lastRequest.lang))
                  .Append(",\"relief\":").Append(_lastRequest.relief ? "true" : "false").Append('}');
            else sb.Append(",\"lastRequest\":null");
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
                        string remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";
                        res = VisitorPortalLogic.Route(req, Encoding.UTF8.GetString(body, 0, have), _statusJson,
                                                       OnSetFromThread, OnClearFromThread, _portalSessionId,
                                                       id => OnPulseFromThread(id, remoteIp));
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
        private int OnSetFromThread(ShowLang lang, bool relief, string tabletId)
        {
            lock (_queueLock)
            {
                int seq = ++_seqIssued;
                _received++;
                _queue.Add(() =>
                {
                    VisitorPrefs.Set(lang, relief, seq);
                    _lastRequest = new LastRequest { tabletSessionId = tabletId, seq = seq,
                                                     lang = ShowLanguage.Code(lang), relief = relief };
                    Debug.Log($"[VisitorPortal] タブレットから受けた: lang={ShowLanguage.Code(lang)} relief={relief} seq={seq}");
                });
                return seq;
            }
        }

        private void OnPulseFromThread(string tabletId, string ip)
        {
            long seenAt = Stopwatch.GetTimestamp();
            lock (_queueLock) _queue.Add(() =>
            {
                int existing = _tablets.FindIndex(t => t.tabletSessionId == tabletId);
                if (existing >= 0)
                {
                    if (_tablets[existing].seenAt > seenAt) return;
                    _tablets.RemoveAt(existing);
                }
                _tablets.Add(new TabletSeen { tabletSessionId = tabletId, ip = ip, seenAt = seenAt });
                _tablets.Sort((a, b) => a.seenAt.CompareTo(b.seenAt));
                if (_tablets.Count > 8) _tablets.RemoveAt(0);
            });
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
