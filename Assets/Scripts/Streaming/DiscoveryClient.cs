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
    /// 発見表（id/uuid → 現在エンドポイント）と、TTL・conflict・切替候補の純判定ロジック。
    /// MonoBehaviour（<see cref="DiscoveryClient"/>）から分離して EditMode テスト可能にする
    /// （<see cref="CueScheduleLogic"/> と同じ方針）。時刻は呼び出し側が渡す（テストで決定的）。
    ///
    /// 設計の要:
    ///   - 表は install 毎 UUID をキーにする。同一機の IP 変化（roam）は同 uuid の Upsert で追従、
    ///     別機が同 id を名乗る（conflict）は「同 id・別 uuid が複数 live」で検出し自動切替を止める。
    ///   - 「切替すべきか」は id 一致・単一 uuid・現エンドポイントと異なる、を満たす候補を返すだけ。
    ///     実際の張替は MonoBehaviour が /info（cameraId + show）照合に成功した時のみ行う。
    /// </summary>
    public sealed class DiscoveryLogic
    {
        public const string RoleCamera = "camera";
        public const string RoleShowServer = "show-server";

        /// <summary>発見表 1 エントリ（uuid が一意キー）。</summary>
        public struct Entry
        {
            public string id;       // camera の A/B/C。show-server は空
            public string uuid;     // install 毎 UUID（一意キー）
            public string ip;
            public int port;
            public string version;
            public string role;     // "camera" | "show-server"
            public float lastSeen;
        }

        /// <summary>切替候補。hasCandidate=false なら他フィールド無効。</summary>
        public struct SwitchCandidate
        {
            public bool hasCandidate;
            public string ip;
            public int port;
            public string uuid;
        }

        private readonly Dictionary<string, Entry> _byUuid = new();
        private readonly List<string> _expiredScratch = new();
        private float _ttlSec = 12f;

        /// <summary>エントリの生存期間 (秒)。これを超えて未受信なら消える。</summary>
        public void SetTtl(float ttlSec) => _ttlSec = ttlSec > 0f ? ttlSec : 12f;

        /// <summary>登録エントリ数（テスト用）。</summary>
        public int Count => _byUuid.Count;

        /// <summary>announce を反映する。uuid をキーに upsert（同 uuid の IP 変化 = roam を追従）。</summary>
        public void Upsert(Entry e, float now)
        {
            if (string.IsNullOrEmpty(e.uuid)) return;
            e.lastSeen = now;
            if (string.IsNullOrEmpty(e.role)) e.role = RoleCamera;
            _byUuid[e.uuid] = e;
        }

        /// <summary>TTL 超過エントリを消す。</summary>
        public void Prune(float now)
        {
            _expiredScratch.Clear();
            foreach (var kv in _byUuid)
                if (now - kv.Value.lastSeen > _ttlSec)
                    _expiredScratch.Add(kv.Key);
            for (int i = 0; i < _expiredScratch.Count; i++)
                _byUuid.Remove(_expiredScratch[i]);
        }

        /// <summary>
        /// id のカメラを一意に解決する。live な同 id が単一 uuid なら entry を返す。
        /// 複数 uuid が live なら conflict=true・戻り値 false（自動切替を止める）。
        /// </summary>
        public bool TryGetCamera(string id, float now, out Entry entry, out bool conflict)
        {
            entry = default;
            conflict = false;
            if (string.IsNullOrEmpty(id)) return false;
            int matches = 0;
            Entry best = default;
            bool has = false;
            foreach (var kv in _byUuid)
            {
                Entry e = kv.Value;
                if (e.role != RoleCamera || e.id != id) continue;
                if (now - e.lastSeen > _ttlSec) continue;
                matches++;
                if (!has || e.lastSeen > best.lastSeen) { best = e; has = true; }
            }
            if (matches == 0) return false;
            if (matches > 1) { conflict = true; return false; }
            entry = best;
            return true;
        }

        /// <summary>HUD 用: conflict でも「最も新しい live エントリ」を返す（lastSeen 表示のため）。</summary>
        public bool TryGetLatestCamera(string id, float now, out Entry entry)
        {
            entry = default;
            if (string.IsNullOrEmpty(id)) return false;
            Entry best = default;
            bool has = false;
            foreach (var kv in _byUuid)
            {
                Entry e = kv.Value;
                if (e.role != RoleCamera || e.id != id) continue;
                if (now - e.lastSeen > _ttlSec) continue;
                if (!has || e.lastSeen > best.lastSeen) { best = e; has = true; }
            }
            entry = best;
            return has;
        }

        /// <summary>同 id・別 uuid が複数 live か（二重 ID）。</summary>
        public bool HasConflict(string id, float now)
        {
            TryGetCamera(id, now, out _, out bool conflict);
            return conflict;
        }

        /// <summary>
        /// 切替候補を返す。id が単一 uuid で解決でき、その現在 IP が現エンドポイントと異なるときのみ hasCandidate。
        /// conflict / 未発見 / 同一エンドポイントは候補なし。
        /// </summary>
        public SwitchCandidate FindSwitchCandidate(string id, string currentIp, int currentPort, float now)
        {
            if (!TryGetCamera(id, now, out Entry e, out _)) return default;
            if (e.ip == currentIp && e.port == currentPort) return default;
            return new SwitchCandidate { hasCandidate = true, ip = e.ip, port = e.port, uuid = e.uuid };
        }

        /// <summary>
        /// 張替を実行してよいかの純判定（全条件 AND）。DiscoveryClient がこの単一ソースを使う:
        ///   discoveryEnabled（キルスイッチ）・非 pinned・フレーム断・別エンドポイント候補あり・/info 照合済み。
        /// テストがこの真理値表を固定する。
        /// </summary>
        public static bool ShouldSwitch(bool discoveryEnabled, bool pinned, bool frameBroken, bool hasCandidate, bool infoVerified)
            => discoveryEnabled && !pinned && frameBroken && hasCandidate && infoVerified;

        /// <summary>最も新しい live な show-server を返す。</summary>
        public bool TryGetShowServer(float now, out string ip, out int port)
        {
            ip = "";
            port = 0;
            Entry best = default;
            bool has = false;
            foreach (var kv in _byUuid)
            {
                Entry e = kv.Value;
                if (e.role != RoleShowServer) continue;
                if (now - e.lastSeen > _ttlSec) continue;
                if (!has || e.lastSeen > best.lastSeen) { best = e; has = true; }
            }
            if (!has) return false;
            ip = best.ip;
            port = best.port;
            return true;
        }
    }

    /// <summary>
    /// fixedcam-discovery/1（UDP :8830）を話す発見クライアント。
    /// 「cameraId → 現在 IP」を実時間解決し、フレーム断が続いたカメラを /info 照合の上で自動で張り替える。
    ///
    /// 通信（設計原則: Quest はブロードキャスト送信のみ、受信は unicast 応答 + 届いた定期ブロードキャスト）:
    ///   - probe: UdpClient で subnet-directed broadcast（自 IP+netmask から算出、失敗時 255.255.255.255）へ送る。
    ///     周期は不健全/未解決カメラがある間 2s、全健全時 10s。
    ///   - announce 受信: 同じ UdpClient を :8830 に bind し、Task.Run のブロッキング Receive ループで受ける。
    ///     受信は lock 付きキューへ積み、Update で drain（MjpegStreamReceiver の単一スロット作法に倣い
    ///     メインスレッド steady-state ではアロケーションを出さない。パケットは低頻度）。
    ///
    /// 切替（実害駆動）:
    ///   カメラの CameraStream がフレーム断（未接続 or 受信 fps 0）5s 継続かつ発見表に別エンドポイントがある時のみ、
    ///   UnityWebRequest ではなく <see cref="StreamMetadataFetcher"/>（HttpClient・Player Settings の
    ///   "Allow downloads over HTTP" 影響を受けない実績経路）で /info を GET し cameraId + show を照合 → 一致時のみ張替。
    ///   pinned（卓の手動固定）カメラには適用しない。conflict（同 id・別 uuid）は切替停止 + HUD 警告。
    ///
    /// キルスイッチ: SerializeField <see cref="discoveryEnabled"/>（false で socket も開かない = 従来の静的 IP 運用へ縮退）
    ///   + show.json control.discoveryEnabled（false で probe/切替のみ停止・socket は維持）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DiscoveryClient : MonoBehaviour
    {
        private const string Proto = "fixedcam-discovery/1";

        [Tooltip("発見機構のマスターキルスイッチ。false なら socket を一切開かず従来の静的 IP 運用へ縮退する。")]
        [SerializeField] private bool discoveryEnabled = true;

        [Tooltip("カメラ受信ユニットの取得元。cameraId / 実効エンドポイント / 接続状態を読む。")]
        [SerializeField] private CameraStreamRegistry? registry;

        [Tooltip("pinned カメラ判定・discovery キルスイッチ・show-server 張替の連携先。null 可（卓連携なしでも動く）。")]
        [SerializeField] private ShowControlClient? showControl;

        [Tooltip("show トークン（隣ブース混線対策）。probe に載せ、announce / /info の show と照合する。")]
        [SerializeField] private string showToken = "mawarimi";

        [Tooltip("発見プロトコルの UDP ポート。streamer / 卓と一致させる。")]
        [SerializeField] private int discoveryPort = 8830;

        [Tooltip("全カメラ健全時の probe 周期 (秒)。")]
        [SerializeField, Min(1f)] private float probeIntervalHealthySec = 10f;

        [Tooltip("不健全/未解決カメラがある間の probe 周期 (秒)。")]
        [SerializeField, Min(0.5f)] private float probeIntervalUnhealthySec = 2f;

        [Tooltip("フレーム断がこの秒数継続したら発見表を参照して張替を検討する。")]
        [SerializeField, Min(1f)] private float frameBreakSwitchSec = 5f;

        [Tooltip("発見表エントリの生存期間 (秒)。未受信でこれを超えると消える。")]
        [SerializeField, Min(2f)] private float entryTtlSec = 12f;

        [Tooltip("卓サーバが /state を返さなくなってからこの秒数を超えたら、発見した show-server へ張り替える。")]
        [SerializeField, Min(2f)] private float serverUnreachableSec = 10f;

        [Tooltip("同一候補エンドポイントへの /info 照合を再試行する最短間隔 (秒)。ハンマリング防止。")]
        [SerializeField, Min(1f)] private float verifyCooldownSec = 5f;

        // ---- UDP ----
        private UdpClient? _udp;
        private CancellationTokenSource? _cts;
        private Task? _recvLoop;
        private IPEndPoint? _broadcastEp;
        private bool _warnedSendFail;

        // 受信キュー（背景スレッドが積み、Update が drain）。上限で古いものは落とす。
        private const int QueueCap = 64;
        private readonly object _queueLock = new();
        private readonly Queue<Datagram> _queue = new();
        private readonly struct Datagram
        {
            public readonly string json;
            public readonly string senderIp;
            public Datagram(string json, string senderIp) { this.json = json; this.senderIp = senderIp; }
        }

        private readonly DiscoveryLogic _logic = new();

        // per-camera 状態（registry.SourceCount 長）。
        private float[] _breakAccum = Array.Empty<float>();     // フレーム断の連続秒数
        private bool[] _verifying = Array.Empty<bool>();        // /info 照合中フラグ
        private string[] _lastVerifyEndpoint = Array.Empty<string>();
        private float[] _lastVerifyTime = Array.Empty<float>();
        private bool[] _conflict = Array.Empty<bool>();         // HUD 用
        private bool[] _missing = Array.Empty<bool>();          // HUD 用（フレーム断確定）

        private float _probeAccum;
        private int _seq;
        private float _serverEvalAccum;
        private bool _running;

        [Serializable] private class Announce
        {
            public string proto = "";
            public string type = "";
            public string show = "";
            public string role = "";
            public string id = "";
            public string uuid = "";
            public int httpPort;
            public string version = "";
            public string name = "";
        }

        private void OnEnable()
        {
            if (!discoveryEnabled)
            {
                Debug.Log("[Discovery] discoveryEnabled=false: 発見機構を停止（静的 IP 運用へ縮退）。");
                return;
            }
            if (registry == null)
            {
                Debug.LogWarning("[Discovery] registry 未設定: 発見機構を停止。");
                return;
            }

            int n = registry.SourceCount;
            _breakAccum = new float[n];
            _verifying = new bool[n];
            _lastVerifyEndpoint = new string[n];
            _lastVerifyTime = new float[n];
            _conflict = new bool[n];
            _missing = new bool[n];
            for (int i = 0; i < n; i++) _lastVerifyEndpoint[i] = "";

            _logic.SetTtl(entryTtlSec);
            _broadcastEp = ComputeBroadcastEndpoint();

            if (!OpenSocket())
            {
                _udp = null;
                return;
            }

            _running = true;
            _cts = new CancellationTokenSource();
            _recvLoop = Task.Run(() => RecvLoop(_cts.Token));
            Debug.Log($"[Discovery] 起動: port={discoveryPort} broadcast={_broadcastEp} show=\"{showToken}\" cameras={n}");
        }

        private bool OpenSocket()
        {
            // まず固定ポート bind（定期ブロードキャストも受けたいため）。失敗したら ephemeral で再試行
            // （unicast 応答は probe の送信元ポート宛に返るので ephemeral でも切替は機能する）。
            try
            {
                _udp = new UdpClient(AddressFamily.InterNetwork);
                _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udp.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
                _udp.EnableBroadcast = true;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Discovery] :{discoveryPort} bind 失敗（{e.Message}）。ephemeral ポートで再試行。");
                try { _udp?.Close(); } catch { }
            }
            try
            {
                _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
                _udp.EnableBroadcast = true;
                Debug.LogWarning("[Discovery] ephemeral ポートで起動（定期ブロードキャストは受けられないが unicast 応答は機能）。");
                return true;
            }
            catch (Exception e2)
            {
                Debug.LogWarning($"[Discovery] UDP socket を開けない: {e2.Message}. 発見機構を停止。");
                return false;
            }
        }

        private void OnDisable()
        {
            _running = false;
            try { _cts?.Cancel(); } catch { }
            try { _udp?.Close(); } catch { }   // ブロッキング Receive を破る
            _udp = null;
            var loop = _recvLoop;
            var cts = _cts;
            if (loop != null)
                loop.ContinueWith(t => { _ = t.Exception; try { cts?.Dispose(); } catch { } }, TaskScheduler.Default);
            else
                try { cts?.Dispose(); } catch { }
            _recvLoop = null;
            _cts = null;
            lock (_queueLock) _queue.Clear();
        }

        // ---- 受信（背景スレッド）----

        private void RecvLoop(CancellationToken ct)
        {
            var udp = _udp;
            if (udp == null) return;
            var remote = new IPEndPoint(IPAddress.Any, 0);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    byte[] data = udp.Receive(ref remote);
                    if (data == null || data.Length == 0) continue;
                    string json = Encoding.UTF8.GetString(data);
                    string ip = remote.Address.ToString();
                    lock (_queueLock)
                    {
                        if (_queue.Count >= QueueCap) _queue.Dequeue(); // 古いものを落とす
                        _queue.Enqueue(new Datagram(json, ip));
                    }
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException)
                {
                    // Windows では相手不達 ICMP で UDP Receive が ConnectionReset を投げることがある。
                    // 回復可能なので継続するが、tight loop を避けるため少し待つ。
                    if (ct.IsCancellationRequested) break;
                    try { Thread.Sleep(100); } catch { }
                }
                catch (Exception)
                {
                    if (ct.IsCancellationRequested) break;
                    try { Thread.Sleep(100); } catch { }
                }
            }
        }

        // ---- メインスレッド ----

        private void Update()
        {
            if (_udp == null || registry == null) return;
            float now = Time.realtimeSinceStartup;

            DrainQueue(now);
            _logic.Prune(now);

            // 実効キルスイッチ: master && show.json（省略時 true）。false なら probe/切替を止める（表更新は継続）。
            bool effEnabled = discoveryEnabled && (showControl == null || showControl.DiscoveryEnabled);
            if (!effEnabled)
            {
                // 断状態の HUD だけは維持したいので missing/conflict は評価する（切替はしない）。
                RefreshWarnFlags(now);
                return;
            }

            SendProbesIfDue(now);
            EvaluateSwitches(now);
            EvaluateServerDiscovery(now);
        }

        private void DrainQueue(float now)
        {
            while (true)
            {
                Datagram dg;
                lock (_queueLock)
                {
                    if (_queue.Count == 0) break;
                    dg = _queue.Dequeue();
                }
                Announce? a;
                try { a = JsonUtility.FromJson<Announce>(dg.json); }
                catch { continue; }
                if (a == null || a.proto != Proto || a.type != "announce") continue;
                if (!string.IsNullOrEmpty(showToken) && a.show != showToken) continue;
                if (string.IsNullOrEmpty(a.uuid)) continue; // 一意キーが無いと表に入れられない

                _logic.Upsert(new DiscoveryLogic.Entry
                {
                    id = a.id ?? "",
                    uuid = a.uuid,
                    ip = dg.senderIp,
                    port = a.httpPort,
                    version = a.version ?? "",
                    role = string.IsNullOrEmpty(a.role) ? DiscoveryLogic.RoleCamera : a.role,
                }, now);
            }
        }

        // HUD の conflict/missing だけ更新する（切替はしない。キルスイッチ中に使う）。
        private void RefreshWarnFlags(float now)
        {
            if (registry == null) return;
            int n = Mathf.Min(registry.SourceCount, _missing.Length);
            for (int i = 0; i < n; i++)
            {
                var src = registry.GetSource(i);
                string camId = src != null ? src.CameraId : "";
                _conflict[i] = !string.IsNullOrEmpty(camId) && _logic.HasConflict(camId, now);
                // missing は _breakAccum ベース。キルスイッチ中は断検知だけ回す。
                UpdateBreakAccum(i, now);
                _missing[i] = _breakAccum[i] >= frameBreakSwitchSec;
            }
        }

        private void UpdateBreakAccum(int i, float now)
        {
            if (registry == null) return;
            var stream = registry.Get(i);
            var src = registry.GetSource(i);
            if (stream == null || src == null || string.IsNullOrEmpty(src.CameraId))
            {
                _breakAccum[i] = 0f;
                return;
            }
            bool pinned = showControl != null && showControl.IsCameraPinned(i);
            if (pinned || stream.IsSuspended)
            {
                _breakAccum[i] = 0f;
                return;
            }
            bool broken = !stream.IsConnected || stream.ReceivedFps <= 0f;
            if (broken) _breakAccum[i] += Time.unscaledDeltaTime;
            else _breakAccum[i] = 0f;
        }

        private void SendProbesIfDue(float now)
        {
            float interval = AnyUnhealthy() ? probeIntervalUnhealthySec : probeIntervalHealthySec;
            _probeAccum += Time.unscaledDeltaTime;
            if (_probeAccum < interval) return;
            _probeAccum = 0f;
            SendProbe();
        }

        private bool AnyUnhealthy()
        {
            if (registry == null) return true;
            int n = registry.SourceCount;
            for (int i = 0; i < n; i++)
            {
                var src = registry.GetSource(i);
                if (src == null || string.IsNullOrEmpty(src.CameraId)) continue;
                var stream = registry.Get(i);
                if (stream == null) continue;
                if (stream.IsSuspended) continue;
                if (!stream.IsConnected || stream.ReceivedFps <= 0f) return true;
            }
            return false;
        }

        private void SendProbe()
        {
            if (_udp == null) return;
            _seq++;
            // 小さな固定形なので手組み（JsonUtility のアロケーションを避ける。低頻度なので影響は小さいが揃える）。
            string json = "{\"proto\":\"" + Proto + "\",\"type\":\"probe\",\"show\":\"" + showToken + "\",\"seq\":" + _seq + "}";
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            var ep = _broadcastEp ?? new IPEndPoint(IPAddress.Broadcast, discoveryPort);
            try { _udp.Send(bytes, bytes.Length, ep); }
            catch (Exception e)
            {
                if (!_warnedSendFail)
                {
                    _warnedSendFail = true;
                    Debug.LogWarning($"[Discovery] probe 送信失敗: {e.Message}（以後この警告は抑止）。");
                }
            }
        }

        private void EvaluateSwitches(float now)
        {
            if (registry == null) return;
            int n = registry.SourceCount;
            for (int i = 0; i < n; i++)
            {
                var src = registry.GetSource(i);
                var stream = registry.Get(i);
                if (src == null || stream == null) { if (i < _breakAccum.Length) _breakAccum[i] = 0f; continue; }
                string camId = src.CameraId;
                if (string.IsNullOrEmpty(camId)) { _breakAccum[i] = 0f; _missing[i] = false; _conflict[i] = false; continue; }

                _conflict[i] = _logic.HasConflict(camId, now);

                bool pinned = showControl != null && showControl.IsCameraPinned(i);
                if (pinned) { _breakAccum[i] = 0f; _missing[i] = false; continue; }

                UpdateBreakAccum(i, now);
                bool stale = _breakAccum[i] >= frameBreakSwitchSec;
                _missing[i] = stale;
                if (!stale) continue;
                if (_verifying[i]) continue;

                string curIp = src.EffectiveHostPublic;
                int curPort = src.EffectivePortPublic;
                var cand = _logic.FindSwitchCandidate(camId, curIp, curPort, now);
                if (!cand.hasCandidate) continue;

                string candEp = cand.ip + ":" + cand.port;
                if (candEp == _lastVerifyEndpoint[i] && now - _lastVerifyTime[i] < verifyCooldownSec) continue;

                _verifying[i] = true;
                _lastVerifyEndpoint[i] = candEp;
                _lastVerifyTime[i] = now;
                _ = VerifyAndApplyAsync(i, src, camId, cand.ip, cand.port);
            }
        }

        private async Task VerifyAndApplyAsync(int index, CameraSource src, string camId, string ip, int port)
        {
            try
            {
                string url = src.BuildInfoUrlFor(ip, port);
                if (string.IsNullOrEmpty(url))
                {
                    // /info 非対応（iPhone 等）は照合不能 → 発見対象外。
                    return;
                }
                var meta = await StreamMetadataFetcher.FetchInfoAsync(url, basicAuthToken: src.BasicAuthToken);
                if (!_running || registry == null) return;

                bool verified = meta != null
                          && meta.cameraId == camId
                          && (string.IsNullOrEmpty(showToken) || meta.show == showToken);

                // await 中に状態が変わりうる（回復・pin・キルスイッチ）ので張替直前に全条件を再検証する。
                bool effEnabled = discoveryEnabled && (showControl == null || showControl.DiscoveryEnabled);
                bool pinned = showControl != null && showControl.IsCameraPinned(index);
                bool stillBroken = index < _missing.Length && _missing[index];
                if (DiscoveryLogic.ShouldSwitch(effEnabled, pinned, stillBroken, hasCandidate: true, infoVerified: verified))
                {
                    registry.ApplyDiscoveryEndpoint(index, ip, port);
                    if (index < _breakAccum.Length) _breakAccum[index] = 0f;
                    Debug.Log($"[Discovery] cam {camId} 断→発見で張替: {ip}:{port} (uuid={meta!.uuid})");
                }
                else if (!verified)
                {
                    Debug.LogWarning($"[Discovery] /info 照合失敗 {ip}:{port} id={meta?.cameraId} show={meta?.show} 期待={camId}/{showToken}。張替しない。");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Discovery] /info 照合エラー {ip}:{port}: {e.Message}");
            }
            finally
            {
                if (index < _verifying.Length) _verifying[index] = false;
            }
        }

        private void EvaluateServerDiscovery(float now)
        {
            if (showControl == null) return;
            var srv = showControl.Server;
            if (srv == null) return; // 焼き込み ShowServer.asset が無いと override できない

            // 1s throttle（不通中に毎フレーム文字列連結しないため）。
            _serverEvalAccum += Time.unscaledDeltaTime;
            if (_serverEvalAccum < 1f) return;
            _serverEvalAccum = 0f;

            bool reachable = now - showControl.LastServerContactTime <= serverUnreachableSec;
            if (reachable) return;
            if (!_logic.TryGetShowServer(now, out string ip, out int port)) return;
            if (srv.Endpoint == ip + ":" + port) return; // 既に適用済み
            showControl.ApplyDiscoveredServer(ip, port);
        }

        // 自 IP + netmask から subnet-directed broadcast を算出。取れなければ 255.255.255.255。
        private IPEndPoint ComputeBroadcastEndpoint()
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
                        var mask = ua.IPv4Mask;
                        if (mask == null) continue;
                        byte[] ipb = ua.Address.GetAddressBytes();
                        byte[] mb = mask.GetAddressBytes();
                        if (ipb.Length != 4 || mb.Length != 4) continue;
                        // /24 未満（巨大サブネット）はスイープ相当の広域ブロードキャストを避け 255.255.255.255 に倒す。
                        int maskBits = 0;
                        foreach (byte b in mb) maskBits += CountBits(b);
                        if (maskBits < 24) continue;
                        byte[] bc = new byte[4];
                        for (int i = 0; i < 4; i++) bc[i] = (byte)(ipb[i] | (~mb[i] & 0xFF));
                        return new IPEndPoint(new IPAddress(bc), discoveryPort);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Discovery] broadcast 算出失敗（255.255.255.255 へフォールバック）: {e.Message}");
            }
            return new IPEndPoint(IPAddress.Broadcast, discoveryPort);
        }

        private static int CountBits(byte b)
        {
            int c = 0;
            while (b != 0) { c += b & 1; b >>= 1; }
            return c;
        }

        // ---- HUD 供給 ----

        /// <summary>HUD 1 行分のカメラ発見状態。</summary>
        public struct CameraDiscStatus
        {
            public string cameraId;
            public string host;
            public int port;
            public char layer;        // 'B'=baked / 'S'=show.json / 'D'=discovery / 'P'=pinned
            public float lastSeenAge;  // 発見表の最新エントリ経過秒。未発見は -1
            public bool conflict;      // 同 id・別 uuid 複数（二重 ID）
            public bool missing;       // フレーム断確定
        }

        /// <summary>HUD が回すカメラ数。</summary>
        public int CameraCount => registry != null ? registry.SourceCount : 0;

        /// <summary>index 番カメラの発見状態を返す（HUD 用）。範囲外・source 無しは false。</summary>
        public bool TryGetCameraStatus(int index, out CameraDiscStatus status)
        {
            status = default;
            if (registry == null) return false;
            var src = registry.GetSource(index);
            if (src == null) return false;
            status.cameraId = src.CameraId;
            status.host = src.EffectiveHostPublic;
            status.port = src.EffectivePortPublic;
            bool pinned = showControl != null && showControl.IsCameraPinned(index);
            status.layer = pinned ? 'P' : src.ActiveLayer switch
            {
                CameraSource.EndpointLayer.Discovery => 'D',
                CameraSource.EndpointLayer.Runtime => 'S',
                _ => 'B',
            };
            status.conflict = index < _conflict.Length && _conflict[index];
            status.missing = index < _missing.Length && _missing[index];
            float age = -1f;
            float now = Time.realtimeSinceStartup;
            if (!string.IsNullOrEmpty(src.CameraId)
                && _logic.TryGetLatestCamera(src.CameraId, now, out var e))
                age = now - e.lastSeen;
            status.lastSeenAge = age;
            return true;
        }
    }
}
