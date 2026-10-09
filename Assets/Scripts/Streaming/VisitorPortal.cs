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
        private int _processedSeq, _visitorGeneration;
        private int _clearIssued, _clearProcessed;
        private int _staffResetIssued, _staffResetProcessed;
        private int _preparationIssued, _preparationProcessed, _briefingSeq;
        private bool _briefingCompleted, _preparationCompleteIssued;
        private bool _staffConfirmed, _staffConfirmedIssued;
        private string _acceptedTablet = "";
        private int _questTick;
        private long _nextQuestTick;
        /// <summary>
        /// スタッフ用準備の状態（<c>/status</c> の <c>staffSetup</c>）。値は <c>StaffSetupPanel</c> が埋める。
        /// タブレットは欠けた項目を「未確認」として扱う（後方互換）。
        /// </summary>
        public struct StaffStatus
        {
            public string stage, reason;
            public bool positionConfirmed;
            public int resetProgress;
            /// <summary>confirmed / needed / recenter / aligning。</summary>
            public string position;
            /// <summary>ok / checking / trouble。</summary>
            public string tablet;
            /// <summary>体験の素材を確認できたか。</summary>
            public bool content;
            /// <summary>カメラ A・B・C の順。<b>呼び出し側の配列を毎回使い回す</b>（毎フレーム作らない）。</summary>
            public StaffCameraStatus[]? cameras;
        }

        public struct StaffCameraStatus
        {
            /// <summary>"A" / "B" / "C"。</summary>
            public string id;
            /// <summary>ok / checking / trouble。</summary>
            public string state;
            /// <summary>"" / nostream / stale / identity / wrongcam / wrongshow。</summary>
            public string problem;
        }

        /// <summary>本編の進み（<c>staffSetup.run</c>）。ShowRunDirector と OutroDirector から読む。</summary>
        public struct RunStatus
        {
            /// <summary>INTRO / RUN / END。ShowRunDirector が無ければ空。</summary>
            public string phase;
            public int lap, laps, sec;
            /// <summary>off / playing / done。</summary>
            public string outro;
            /// <summary>released / trapped / ""。</summary>
            public string ending;
        }
        public Func<StaffStatus>? StaffStatusProvider { get; set; }
        public Func<bool>? StaffResetProvider { get; set; }
        private string _lastStaffResetId = "", _staffResetRejectedId = "";
        private OutroDirector? _outro;
        private long _nextOutroLookup;
        // 変化検知の前回値。カメラは呼び出し側の配列を使い回すので、比較用に値を写して持つ。
        private StaffStatus _lastStaff;
        private readonly StaffCameraStatus[] _lastStaffCameras = new StaffCameraStatus[3];
        private int _lastStaffCameraCount = -1;
        private RunStatus _lastRun;
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
        public LastRequest? LatestRequest => _lastRequest;
        /// <summary>HTTPでは受理済みだがUnityの設定枠へまだ届いていない要求も開始を止める。</summary>
        public bool HasQueuedSettings => Volatile.Read(ref _seqIssued) > _processedSeq;
        public bool HasQueuedClear => Volatile.Read(ref _clearIssued) > _clearProcessed;
        public bool HasQueuedStaffReset => Volatile.Read(ref _staffResetIssued) > _staffResetProcessed;
        public int VisitorGeneration => _visitorGeneration;
        public bool CurrentVisitorStaffConfirmed => CurrentVisitorBriefingCompleted && _staffConfirmed;
        public bool CurrentVisitorBriefingCompleted => CurrentVisitorSettingsApplied && _briefingCompleted
            && _lastRequest != null && _briefingSeq == _lastRequest.seq
            && _preparationProcessed == Volatile.Read(ref _preparationIssued);
        public bool CurrentVisitorSettingsApplied => _visitorGeneration > 0 && _lastRequest != null
            && !HasQueuedSettings && !HasQueuedClear && !HasQueuedStaffReset && VisitorPrefs.AppliedSeq == _lastRequest.seq
            && (VisitorPrefs.PendingSeq == _lastRequest.seq || VisitorPrefs.ConsumedSeq == _lastRequest.seq)
            && ShowLanguage.Code(ShowLanguage.Current) == _lastRequest.lang
            && HorrorRelief.Enabled == _lastRequest.relief;

        /// <summary>右Aの実リセット前に通信世代と設定を分離。HTTP検証と受付も同じロック内。</summary>
        public void BeginVisitorSession()
        {
            lock (_queueLock)
            {
                _portalSessionId = Guid.NewGuid().ToString("N");
                _visitorGeneration++;
                _processedSeq = _seqIssued;
                _clearProcessed = _clearIssued;
                _staffResetProcessed = _staffResetIssued;
                _lastRequest = null;
                _acceptedTablet = "";
                _preparationIssued = _preparationProcessed = _briefingSeq = 0;
                _briefingCompleted = _preparationCompleteIssued = false;
                _staffConfirmed = _staffConfirmedIssued = false;
                _lastStaffResetId = _staffResetRejectedId = "";
                VisitorPrefs.BeginVisitor();
                RefreshStatus(force: true);
            }
        }
        /// <summary>listen成功とは別の、実際のタブレット通信の年齢。</summary>
        public float LatestTabletAgeSec
        {
            get
            {
                if (_tablets.Count == 0) return float.PositiveInfinity;
                return (float)((Stopwatch.GetTimestamp() - _tablets[_tablets.Count - 1].seenAt)
                    / (double)Stopwatch.Frequency);
            }
        }
        public float CurrentVisitorTabletAgeSec
        {
            get
            {
                if (_lastRequest == null) return LatestTabletAgeSec;
                int i = _tablets.FindIndex(t => t.tabletSessionId == _lastRequest.tabletSessionId);
                return i < 0 ? float.PositiveInfinity : (float)((Stopwatch.GetTimestamp() - _tablets[i].seenAt)
                    / (double)Stopwatch.Frequency);
            }
        }

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
            long now = Stopwatch.GetTimestamp();
            if (now >= _nextQuestTick) { _questTick++; _nextQuestTick = now + Stopwatch.Frequency / 2; }
            StaffStatus staff = StaffStatusProvider != null ? StaffStatusProvider() : default;
            if (_outro == null && now >= _nextOutroLookup)
            {
                // 終幕の実行体が無い構成で毎フレーム全走査しない。
                _outro = FindObjectOfType<OutroDirector>();
                _nextOutroLookup = now + Stopwatch.Frequency * 2;
            }
            RunStatus run = ReadRun(_run, _outro);
            // 変わったフレームだけ組み直す（90Hz で文字列を作らない）。
            // スタッフ用準備と本編の進みは値で比べる（文字列の鍵へ足すと毎フレームの連結が増える）。
            // run.sec は 1 秒刻みなので、本編中は 1 秒に 1 回作り直す。
            bool staffChanged = StaffChanged(staff, run);
            string key = _portalSessionId + "|" + stage + "|" + phase + "|" + lang + "|" + (relief ? 1 : 0) + "|" + VisitorPrefs.PendingSeq
                         + "|" + VisitorPrefs.AppliedSeq + "|" + VisitorPrefs.ApplyCount + "|" + _received + "|" + _rejected
                         + "|" + _questTick + "|" + _preparationProcessed + "|" + _briefingCompleted + "|" + _staffConfirmed + "|" + _lastStaffResetId + "|" + _staffResetRejectedId;
            if (!force && !staffChanged && key == _lastKey) return;
            _lastKey = key;
            RememberStaff(staff, run);

            var sb = new StringBuilder(640);
            sb.Append("{\"ok\":true");
            sb.Append(",\"portalSessionId\":").Append(VisitorPortalLogic.JsonString(_portalSessionId));
            sb.Append(",\"visitorGeneration\":").Append(_visitorGeneration);
            sb.Append(",\"questTick\":").Append(_questTick);
            sb.Append(",\"staffResetId\":").Append(VisitorPortalLogic.JsonString(_lastStaffResetId));
            sb.Append(",\"staffResetRejectedId\":").Append(VisitorPortalLogic.JsonString(_staffResetRejectedId));
            sb.Append(",\"briefing\":{\"seq\":").Append(_briefingSeq)
                .Append(",\"revision\":").Append(_preparationProcessed)
                .Append(",\"tabletSessionId\":").Append(VisitorPortalLogic.JsonString(_acceptedTablet))
                .Append(",\"completed\":").Append(CurrentVisitorBriefingCompleted ? "true" : "false")
                .Append(",\"staffConfirmed\":").Append(CurrentVisitorStaffConfirmed ? "true" : "false").Append('}');
            sb.Append(",\"staffSetup\":");
            AppendStaffSetupJson(sb, staff, run);
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

        /// <summary>終幕の段の語。Off → off / Collapse・Dark・Report → playing / Done → done。</summary>
        public static string OutroCode(OutroStage stage)
            => stage == OutroStage.Off ? "off" : stage == OutroStage.Done ? "done" : "playing";

        /// <summary>締めの結果の語。帰還 → released / 人形化 → trapped / それ以外は空。</summary>
        public static string EndingCode(ShowEndingOutcome outcome)
            => outcome == ShowEndingOutcome.Released ? "released" : outcome == ShowEndingOutcome.Trapped ? "trapped" : "";

        private static RunStatus ReadRun(ShowRunDirector? run, OutroDirector? outro)
        {
            if (run == null) return new RunStatus { phase = "", outro = outro != null ? OutroCode(outro.Stage) : "off", ending = "" };
            return new RunStatus
            {
                phase = PhaseCode(run.Phase),
                lap = run.Lap,
                laps = run.TotalLaps,
                sec = Mathf.Max(0, Mathf.FloorToInt(run.RunElapsedSec)),
                outro = outro != null ? OutroCode(outro.Stage) : "off",
                ending = EndingCode(run.EndingOutcome),
            };
        }

        private bool StaffChanged(in StaffStatus staff, in RunStatus run)
        {
            if (staff.stage != _lastStaff.stage || staff.reason != _lastStaff.reason
                || staff.positionConfirmed != _lastStaff.positionConfirmed || staff.resetProgress != _lastStaff.resetProgress
                || staff.position != _lastStaff.position || staff.tablet != _lastStaff.tablet || staff.content != _lastStaff.content)
                return true;
            if (run.phase != _lastRun.phase || run.lap != _lastRun.lap || run.laps != _lastRun.laps || run.sec != _lastRun.sec
                || run.outro != _lastRun.outro || run.ending != _lastRun.ending)
                return true;
            int count = staff.cameras != null ? Math.Min(staff.cameras.Length, _lastStaffCameras.Length) : 0;
            if (count != _lastStaffCameraCount) return true;
            for (int i = 0; i < count; i++)
            {
                StaffCameraStatus c = staff.cameras![i], last = _lastStaffCameras[i];
                if (c.id != last.id || c.state != last.state || c.problem != last.problem) return true;
            }
            return false;
        }

        private void RememberStaff(in StaffStatus staff, in RunStatus run)
        {
            _lastStaff = staff;
            _lastStaff.cameras = null;
            _lastRun = run;
            _lastStaffCameraCount = staff.cameras != null ? Math.Min(staff.cameras.Length, _lastStaffCameras.Length) : 0;
            for (int i = 0; i < _lastStaffCameraCount; i++) _lastStaffCameras[i] = staff.cameras![i];
        }

        /// <summary>
        /// <c>staffSetup</c> の JSON（オブジェクト 1 つ）を書く。タブレットとの契約の形はここ 1 か所。
        /// </summary>
        public static void AppendStaffSetupJson(StringBuilder sb, in StaffStatus staff, in RunStatus run)
        {
            sb.Append("{\"stage\":").Append(VisitorPortalLogic.JsonString(staff.stage))
              .Append(",\"reason\":").Append(VisitorPortalLogic.JsonString(staff.reason))
              .Append(",\"positionConfirmed\":").Append(staff.positionConfirmed ? "true" : "false")
              .Append(",\"resetProgress\":").Append(staff.resetProgress)
              .Append(",\"position\":").Append(VisitorPortalLogic.JsonString(staff.position))
              .Append(",\"cameras\":[");
            int count = staff.cameras != null ? staff.cameras.Length : 0;
            for (int i = 0; i < count; i++)
            {
                StaffCameraStatus c = staff.cameras![i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(VisitorPortalLogic.JsonString(c.id))
                  .Append(",\"state\":").Append(VisitorPortalLogic.JsonString(c.state))
                  .Append(",\"problem\":").Append(VisitorPortalLogic.JsonString(c.problem)).Append('}');
            }
            sb.Append("],\"tablet\":").Append(VisitorPortalLogic.JsonString(staff.tablet))
              .Append(",\"content\":").Append(staff.content ? "true" : "false")
              .Append(",\"run\":{\"phase\":").Append(VisitorPortalLogic.JsonString(run.phase))
              .Append(",\"lap\":").Append(run.lap)
              .Append(",\"laps\":").Append(run.laps)
              .Append(",\"sec\":").Append(run.sec)
              .Append(",\"outro\":").Append(VisitorPortalLogic.JsonString(run.outro))
              .Append(",\"ending\":").Append(VisitorPortalLogic.JsonString(run.ending))
              .Append("}}");
        }

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
                        lock (_queueLock) res = VisitorPortalLogic.Route(req, Encoding.UTF8.GetString(body, 0, have), _statusJson,
                                                       OnSetFromThread, OnClearFromThread, _portalSessionId,
                                                       id => OnPulseFromThread(id, remoteIp), OnPreparationFromThread, OnStaffResetFromThread,
                                                       OnClearVersionedFromThread);
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
                string session = _portalSessionId;
                if (_acceptedTablet != tabletId) _preparationIssued = 0;
                _acceptedTablet = tabletId;
                _received++;
                _queue.Add(() =>
                {
                    if (session != _portalSessionId) return;
                    VisitorPrefs.Set(lang, relief, seq);
                    _processedSeq = seq;
                    _briefingCompleted = _staffConfirmed = false; _briefingSeq = 0;
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

        private void OnPreparationFromThread(VisitorPortalLogic.PreparationPulse pulse)
        {
            lock (_queueLock)
            {
                if (pulse.portalSessionId != _portalSessionId || pulse.tabletSessionId != _acceptedTablet
                    || (pulse.seq != 0 && pulse.seq != _seqIssued) || pulse.revision < _preparationIssued) return;
                if (pulse.revision == _preparationIssued && pulse.completed != _preparationCompleteIssued) return;
                if (pulse.revision == _preparationIssued && pulse.staffConfirmed != _staffConfirmedIssued) return;
                _preparationIssued = pulse.revision; _preparationCompleteIssued = pulse.completed;
                _staffConfirmedIssued = pulse.staffConfirmed;
                int request = _seqIssued;
                _queue.Add(() =>
                {
                    if (pulse.portalSessionId != _portalSessionId || request != Volatile.Read(ref _seqIssued)
                        || pulse.tabletSessionId != _acceptedTablet || pulse.revision != Volatile.Read(ref _preparationIssued)) return;
                    _preparationProcessed = pulse.revision; _briefingSeq = pulse.seq;
                    _briefingCompleted = pulse.completed;
                    _staffConfirmed = pulse.staffConfirmed;
                });
            }
        }

        private void OnStaffResetFromThread(string requestId, int settingsSeq)
        {
            lock (_queueLock)
            {
                string session = _portalSessionId;
                int reset = ++_staffResetIssued;
                _queue.Add(() =>
                {
                    if (session != _portalSessionId) return;
                    _staffResetProcessed = Math.Max(_staffResetProcessed, reset);
                    if (settingsSeq != Volatile.Read(ref _seqIssued) || StaffResetProvider == null || !StaffResetProvider())
                        _staffResetRejectedId = requestId;
                    else _lastStaffResetId = requestId;
                });
            }
        }

        private void OnClearFromThread() => QueueClearFromThread(-1);
        private void OnClearVersionedFromThread(int expectedSeq) => QueueClearFromThread(expectedSeq);
        private void QueueClearFromThread(int expectedSeq)
        {
            lock (_queueLock)
            {
                string session = _portalSessionId;
                int settingsSeq = _seqIssued;
                if (expectedSeq > 0 && expectedSeq != settingsSeq) return;
                int clear = ++_clearIssued;
                _queue.Add(() =>
                {
                    if (session != _portalSessionId) return;
                    _clearProcessed = Math.Max(_clearProcessed, clear);
                    if (settingsSeq != Volatile.Read(ref _seqIssued)) return;
                    VisitorPrefs.Clear();
                    _briefingCompleted = _staffConfirmed = false; _briefingSeq = 0;
                    _lastRequest = null; _acceptedTablet = "";
                    Debug.Log("[VisitorPortal] 枠を空にした（スタッフ）");
                });
            }
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
