#nullable enable
using System;
using System.Collections.Generic;
using TableDuoVr.Hands;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// LAN 直結のホスト/クライアント接続管理 + pose の named message 配送。
    /// 配送経路: owner → (client なら server へ) → server が他クライアントへリレー。
    /// 起動引数 / Android intent extras（tdv_mode=host|client, tdv_ip=...）で UI 無し自動接続可:
    ///   adb shell am start -n <pkg>/com.unity3d.player.UnityPlayerActivity -e tdv_mode host
    /// フラグ一切無し（Quest ランチャーから普通に開いた）場合は **LAN ホスト自動発見**で接続する
    /// （HostBeacon / HostDiscovery・UDP :7778。役割もサーバが接続順に自動割当 — 先着=人役）。
    /// </summary>
    public sealed class ConnectionManager : MonoBehaviour
    {
        public static ConnectionManager? Instance { get; private set; }

        public enum AutoMode
        {
            None,
            Host,
            Client,
            /// <summary>LAN 上のホストを UDP ビーコンで自動発見して接続（フラグ無し起動の既定）。</summary>
            Discover,
        }

        public enum RoleOverride
        {
            None,
            Full,
            Hand,
            Spectator,
        }

        [SerializeField] private string defaultAddress = "192.168.1.10";
        [SerializeField] private ushort port = 7777;
        [SerializeField] private AutoMode autoMode = AutoMode.None;
        [SerializeField] private bool showGui = true;
        [Tooltip("接続上限（host 含む）。2人プレイヤー + 観戦者1 を許すため既定 3。超過する接続は server が拒否する。" +
                 "観戦者は席を持たない Spectator ロールで参加する（席衝突は WarnIfSeatCollision が検知）")]
        [SerializeField] private int maxClients = 3;

        [Header("Study（Editor 検証用。実機は tdv_* extras が優先）")]
        [SerializeField] private RoleOverride studyRole = RoleOverride.None;
        [SerializeField] private bool showHeadMarker;
        [SerializeField] private bool oneHandMode = true;
        [Tooltip("診断: 各席に静的アバターを先置き（描画/疎通/トラッキングの段階切り分け用）。接続で静的→ライブに差替。" +
                 "研究本番は OFF（相手不在時にアバターが居ると体験が変わる）。実機は tdv_preplace=on で有効化")]
        [SerializeField] private bool preplaceAvatars;
        [Tooltip("手役の手メッシュの見た目（Editor 検証既定。実機は tdv_hand extras が優先）。" +
                 "Default=Meta白手 / Realistic=人間の手 / Robot=機械の手。実機は左コントローラ Y でも巡回切替できる")]
        [SerializeField] private HandVariant studyHandVariant = HandVariant.Default;
        [Tooltip("L0（HMD/XR 無しのデスクトップ検証）: OVRCameraRig を切り DebugCamera+FakeHandDriver を有効化。" +
                 "Standalone Windows ビルドを CLI で host/client/spectator 起動して実機ゼロ検証する用。tdv_l0=on で有効化")]
        [SerializeField] private bool enableL0InEditor;

        private const string PoseMsg = "tdv_pose";
        private const string LayoutMsg = "tdv_layout";
        private readonly Dictionary<ulong, AvatarPose> _remotePoses = new();
        // clientId→その端末の手 bind 構造（指先 FK を本人の手寸法で計算するため）。host は自分のは静的 Captured を使う
        private readonly Dictionary<ulong, (HandSkeletonLayout? L, HandSkeletonLayout? R)> _remoteLayouts = new();
        private readonly AvatarPose _localPose = new();
        private bool _hasLocalPose;
        private string _ipInput = "";
        private string _status = "idle";

        /// <summary>リモートプレイヤーの pose を受信した（originClientId, pose）。pose は使い回しバッファ。</summary>
        public event Action<ulong, AvatarPose>? RemotePoseReceived;

        /// <summary>自分の pose をネットワークへ送出した（ワイヤ送出点）。WireTapRecorder が
        /// 「実際に相手へ送ったデータ」を記録するためのタップ。ローカル描画用の手とは別経路。</summary>
        public event Action<AvatarPose>? LocalPoseSent;

        /// <summary>手 layout を受信/格納した（originClientId）。SessionLogger が CSV に刻み、
        /// 解析側が「どこから本人の手寸法で FK されるか」の境界を判別できるようにする（study-validity）。</summary>
        public event Action<ulong>? HandLayoutReceived;

        /// <summary>壊れた pose メッセージを破棄した累計。SessionLogger が変化を event 行に刻む
        /// （破棄フレームが記録に残らず「凍結 vs 欠落」を判別できなくなるのを防ぐ）。</summary>
        public int DroppedPoseMessages { get; private set; }

        /// <summary>Seq 逆行（Unreliable の後着）で棄却した pose の累計。SessionLogger が刻む。</summary>
        public int RejectedStalePoses { get; private set; }

        // 受信 pose を一旦ここへデコードし、Seq が単調増加のときだけ本バッファへ反映する
        // （後着の古いパケットで pose が 1 フレーム巻き戻り、CSV/リプレイ/Grabbable を汚すのを防ぐ）
        private readonly AvatarPose _rxScratch = new();
        private readonly Dictionary<ulong, uint> _lastAcceptedSeq = new();

        // client 切断時の自動再接続（Wi-Fi 瞬断で HMD 内から復帰不能になるのを防ぐ。
        // 調査モードは GUI 非表示なので、これが無いと復帰手段が adb 再起動しかない）
        private string? _lastClientAddress;
        private bool _wasClient;
        private float _reconnectAt = -1f;
        private float _reconnectDelay = 2f;

        private void Awake()
        {
            // additive ロード / シーン再ロードの重複で pose ルーティングが誤インスタンスを指すのを防ぐ
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
            _ipInput = defaultAddress;

            // Inspector 既定 → 起動フラグの順で StudyConfig を確定（フラグ優先）
            StudyConfig.ForcedRole = studyRole switch
            {
                RoleOverride.Full => StudyConfig.Role.Full,
                RoleOverride.Hand => StudyConfig.Role.Hand,
                RoleOverride.Spectator => StudyConfig.Role.Spectator,
                _ => null,
            };
            StudyConfig.ShowHeadMarker = showHeadMarker;
            StudyConfig.OneHandMode = oneHandMode;
            StudyConfig.PreplaceAvatars = preplaceAvatars;
            StudyConfig.SelectedHandVariant = studyHandVariant;
            StudyLaunchFlags.Apply(); // tdv_* パースと優先順の定義は StudyLaunchFlags（Hands）に一元化
            ConfigureL0IfRequested();
            ConfigureFakeSenderIfRequested();
            if (StudyConfig.LaunchedWithStudyFlags)
            {
                showGui = false; // 調査セッションではデバッグ GUI を見せない
            }
        }

        // L0（HMD/XR 無しのデスクトップ検証）: OVRCameraRig を切り、DebugCamera + FakeHandDriver を有効化。
        // Standalone Windows ビルドを CLI で host/client/spectator 起動して実機ゼロ・MCP ゼロで検証するための土台。
        private void ConfigureL0IfRequested()
        {
            string? l0 = StudyLaunchFlags.Get("tdv_l0", "-tdvL0");
            bool enable = l0 == "on" || (l0 == null && enableL0InEditor);
            if (l0 == "off") enable = false;
            if (!enable) return;

            var rig = GameObject.Find("OVRCameraRig");
            if (rig != null) rig.SetActive(false);
            var dbg = GameObject.Find("DebugCamera");
            if (dbg != null) dbg.SetActive(true);
            var fake = FindObjectOfType<TableDuoVr.Hands.Playback.FakeHandDriver>(includeInactive: true);
            if (fake != null) fake.enabled = true; // OnEnable で HandPoseSourceRegistry に登録（合成 pose 供給）
            Debug.Log("[TableDuo] L0 モード: OVRCameraRig OFF / DebugCamera ON / FakeHandDriver ON（HMD/XR 不要）");
        }

        // ソロ実機検証（tdv_fake=on）: マネキン側 HMD を「固定動作の送信機」にする。
        // FakeHandDriver（合成モーション・Priority=10）が実トラッキングより優先されて送信ソースになり、
        // pose は通常どおり SubmitLocalPose → NGO named message で相手へ届く（受信側は無改変＝
        // ネットワーク経路の実測になる。L0 と違い OVRCameraRig は生かしたままなので HMD 描画も通常）。
        private void ConfigureFakeSenderIfRequested()
        {
            if (StudyLaunchFlags.Get("tdv_fake", "-tdvFake") != "on") return;
            var fake = FindObjectOfType<TableDuoVr.Hands.Playback.FakeHandDriver>(includeInactive: true);
            if (fake == null)
            {
                Debug.LogError("[TableDuo] tdv_fake=on だが FakeHandDriver がシーンに無い（Setup TableDuo Scene 未実行？）");
                return;
            }
            fake.enabled = true;
            Debug.Log("[TableDuo] ソロ検証モード: FakeHandDriver ON — 固定動作をネットワーク送信します（tdv_fake=on）");
        }

        private void Start()
        {
            // キービジュアル撮影モード: ネットワーク/preplace せず、authored ポーズ＋シネマカメラで1枚撮る
            if (StudyLaunchFlags.Get("tdv_keyvisual", "-tdvKeyVisual") == "on")
            {
                showGui = false; // 接続 GUI を映り込ませない
                new GameObject("KeyVisualDirector").AddComponent<KeyVisualDirector>().Run();
                return;
            }

            // 診断: 各席に静的アバターを先置き（疎通前から描画を確認できる・接続で差し替わる）
            if (StudyConfig.PreplaceAvatars && SeatAvatarPreview.Instance == null)
            {
                new GameObject("SeatAvatarPreview").AddComponent<SeatAvatarPreview>();
            }

            ResolveAutoMode(out var mode, out string? ip);
            switch (mode)
            {
                case AutoMode.Host:
                    StartHost();
                    break;
                case AutoMode.Client:
                    StartClient(ip ?? defaultAddress);
                    break;
                case AutoMode.Discover:
                    StartDiscovery();
                    break;
            }
        }

        // --- LAN ホスト自動発見（client 側） ---

        private HostDiscovery? _discovery;
        private bool _buildMismatchWarned;
        private TextMesh? _searchHud;

        // 探索中だけ HMD 視界に控えめな状態テキストを出す（接続確立で消える）。
        // OnGUI は HMD 内に映らないため、head-locked の 3D テキストで出す
        private void UpdateSearchHud(bool searching)
        {
            if (!searching)
            {
                if (_searchHud != null)
                {
                    Destroy(_searchHud.transform.gameObject);
                    _searchHud = null;
                }
                return;
            }
            var cam = Camera.main;
            if (cam == null) return;
            if (_searchHud == null)
            {
                var go = new GameObject("SearchHud");
                go.transform.SetParent(cam.transform, false);
                go.transform.localPosition = new Vector3(0f, -0.12f, 0.8f); // 視線やや下 80cm
                _searchHud = go.AddComponent<TextMesh>();
                _searchHud.anchor = TextAnchor.MiddleCenter;
                _searchHud.alignment = TextAlignment.Center;
                _searchHud.fontSize = 48;
                _searchHud.characterSize = 0.008f;
                _searchHud.color = new Color(1f, 1f, 1f, 0.85f);
            }
            _searchHud.text = _buildMismatchWarned
                ? "ホストを探しています…\n⚠ ビルド不一致を検出（両ビルドの焼き直し推奨）"
                : "ホストを探しています…\n（PC でホストを起動してください）";
        }

        /// <summary>ホスト探索を開始。発見次第 StartClient。切断後もビーコンが来れば再接続の種になる
        /// （listen は張りっぱなし・接続中の beacon は無視するだけなのでコスト無し）。</summary>
        public void StartDiscovery()
        {
            if (_discovery == null)
            {
                _discovery = gameObject.AddComponent<HostDiscovery>();
                _discovery.Found += OnHostFound;
            }
            _discovery.Begin();
            _status = "ホスト探索中…";
        }

        private void OnHostFound(string ip, ushort ngoPort, bool sceneHashMatches)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.IsListening || nm.IsConnectedClient) return; // 接続中/試行中は無視
            if (!sceneHashMatches && !_buildMismatchWarned)
            {
                _buildMismatchWarned = true;
                // 接続は許す（開発中の細差では動くことも多い）が、silent failure にしない
                Debug.LogWarning("[TableDuo] ⚠ ホストとシーン構成（ビルド）が一致しません。" +
                                 "掴み等の RPC が失敗する可能性 — 両ビルドを焼き直してください");
            }
            Debug.Log($"[TableDuo] ホスト発見 → {ip}:{ngoPort}（自動接続）");
            port = ngoPort;
            StartClient(ip);
        }

        private void OnDestroy()
        {
            var nm = NetworkManager.Singleton;
            if (nm != null)
            {
                nm.OnClientConnectedCallback -= OnClientConnected;
                nm.OnClientDisconnectCallback -= OnClientDisconnected;
            }
            if (Instance == this) Instance = null;
        }

        // 自動再接続の駆動（client 切断後のみ稼働。接続確立 or host 化で自動停止）
        private void Update()
        {
            // 自動発見モードの状態表示（未接続の間だけ「ホストを探しています…」）
            if (_discovery != null)
            {
                var nmd = NetworkManager.Singleton;
                UpdateSearchHud(nmd != null && !nmd.IsConnectedClient && !nmd.IsServer);
            }

            if (_reconnectAt < 0f || _lastClientAddress == null) return;
            var nm = NetworkManager.Singleton;
            if (nm == null) return;
            if (nm.IsConnectedClient || nm.IsServer)
            {
                _reconnectAt = -1f; // 復帰完了
                return;
            }
            if (Time.time < _reconnectAt) return;

            if (nm.IsListening)
            {
                // 前回試行が pending のまま（connect timeout 待ち）→ 一度掃除してから再試行
                nm.Shutdown();
                _reconnectAt = Time.time + 0.5f;
                return;
            }
            Debug.Log($"[TableDuo] 自動再接続 → {_lastClientAddress}:{port}");
            StartClient(_lastClientAddress);
            _reconnectDelay = Mathf.Min(_reconnectDelay * 2f, 10f);
            _reconnectAt = Time.time + _reconnectDelay;
        }

        public void StartHost()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.IsListening) return;
            _wasClient = false;
            _reconnectAt = -1f;
            GetTransport(nm).SetConnectionData("0.0.0.0", port, "0.0.0.0");
            if (nm.StartHost())
            {
                RegisterHandler(nm);
                _status = $"host :{port}";
                Debug.Log($"[TableDuo] Host 開始 port={port}");
                // 自動接続まわり: 役割割当の記憶をクリアし、存在通知ビーコンを開始
                TableDuoPlayer.ResetAutoRoleAssignments();
                var beacon = GetComponent<HostBeacon>() ?? gameObject.AddComponent<HostBeacon>();
                beacon.Begin(port);
            }
            else
            {
                _status = "host start failed";
            }
        }

        public void StartClient(string address)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.IsListening) return;
            _wasClient = true;
            _lastClientAddress = address;
            GetTransport(nm).SetConnectionData(address, port);
            if (nm.StartClient())
            {
                RegisterHandler(nm);
                _status = $"client → {address}:{port}";
                Debug.Log($"[TableDuo] Client 接続開始 {address}:{port}");
            }
            else
            {
                _status = "client start failed";
            }
        }

        /// <summary>自分の pose を送信する。owner が送信レート制御した上で呼ぶ。</summary>
        public void SubmitLocalPose(AvatarPose pose)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening || nm.CustomMessagingManager == null) return;

            _localPose.CopyFrom(pose);
            _hasLocalPose = true;

            var writer = new FastBufferWriter(PoseCodec.MaxBytes, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(nm.LocalClientId);
                PoseCodec.Write(ref writer, pose);
                if (nm.IsServer)
                {
                    SendToAllClientsExcept(nm, ref writer, nm.LocalClientId);
                }
                else
                {
                    nm.CustomMessagingManager.SendNamedMessage(
                        PoseMsg, NetworkManager.ServerClientId, writer, NetworkDelivery.Unreliable);
                }
                LocalPoseSent?.Invoke(pose); // 送出成功後にタップ（未接続 early-return は発火しない）
            }
            finally
            {
                writer.Dispose();
            }
        }

        private void RegisterHandler(NetworkManager nm)
        {
            // 再接続で前セッションの clientId 別 pose / layout バッファが残ると stale が混入するためクリア。
            // named message handler は session 終了で NGO 側が破棄するので毎 start 登録でよい
            _remotePoses.Clear();
            _remoteLayouts.Clear();
            _lastAcceptedSeq.Clear();
            _hasLocalPose = false;
            nm.CustomMessagingManager.RegisterNamedMessageHandler(PoseMsg, OnPoseMessage);
            nm.CustomMessagingManager.RegisterNamedMessageHandler(LayoutMsg, OnLayoutMessage);

            // 接続上限の enforcement と切断時のクリーンアップ（重複購読を避けて付け直す）
            nm.OnClientConnectedCallback -= OnClientConnected;
            nm.OnClientConnectedCallback += OnClientConnected;
            nm.OnClientDisconnectCallback -= OnClientDisconnected;
            nm.OnClientDisconnectCallback += OnClientDisconnected;
        }

        // server: 接続上限を超えたクライアントを拒否（席は2つ固定なので3人目で席衝突・アバター重なりを防ぐ）
        private void OnClientConnected(ulong clientId)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            if (nm.ConnectedClientsIds.Count > maxClients && clientId != nm.LocalClientId)
            {
                Debug.LogWarning($"[TableDuo] 接続上限({maxClients})超過のため client{clientId} を切断");
                nm.DisconnectClient(clientId);
                return;
            }
            // 途中参加（後から入る観戦者・再接続 client）へ既受信の layout を再送
            //（layout は各 client が接続直後に 1 回しか送らないため、リレーだけだと後着者に届かない）
            foreach (var kv in _remoteLayouts)
            {
                if (kv.Key == clientId) continue;
                SendLayoutTo(nm, clientId, kv.Key, kv.Value.L, kv.Value.R);
            }
        }

        /// <summary>origin の手 layout を target クライアントへ送る（server 用）。</summary>
        private static void SendLayoutTo(NetworkManager nm, ulong targetClientId, ulong originId,
            HandSkeletonLayout? l, HandSkeletonLayout? r)
        {
            var writer = new FastBufferWriter(PoseCodec.MaxLayoutBytes, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(originId);
                PoseCodec.WriteLayout(ref writer, l);
                PoseCodec.WriteLayout(ref writer, r);
                nm.CustomMessagingManager.SendNamedMessage(
                    LayoutMsg, targetClientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TableDuo] layout リレー失敗 client{originId}→client{targetClientId}: {e.Message}");
            }
            finally
            {
                writer.Dispose();
            }
        }

        // 切断時: その client の stale バッファを掃除し、状態表示を更新（再接続の導線を確保）。
        // クライアント側は NGO の DisconnectReason をログして原因究明を容易にする
        // （観戦者が接続後に落ちる等の切り分け用。理由が空なら transport/timeout）。
        private void OnClientDisconnected(ulong clientId)
        {
            _remotePoses.Remove(clientId);
            _remoteLayouts.Remove(clientId);
            _lastAcceptedSeq.Remove(clientId);
            var nm = NetworkManager.Singleton;
            if (nm == null) return;
            if (!nm.IsServer)
            {
                string reason = string.IsNullOrEmpty(nm.DisconnectReason)
                    ? "(理由なし＝transport/timeout か config 不一致)" : nm.DisconnectReason;
                Debug.LogWarning($"[TableDuo] サーバから切断された: {reason}");
                _status = $"切断: {reason}";
                // Wi-Fi 瞬断・host 一時フリーズからの自動復帰（指数バックオフで再接続。
                // host が listen し続けていれば player 再スポーン + layout 再送で状態は復元される）
                if (_wasClient && _lastClientAddress != null && _reconnectAt < 0f)
                {
                    _reconnectDelay = 2f;
                    _reconnectAt = Time.time + _reconnectDelay;
                    Debug.Log($"[TableDuo] {_reconnectDelay:F0}s 後に自動再接続を試行");
                }
            }
            else
            {
                Debug.Log($"[TableDuo] client{clientId} が切断");
                if (nm.IsListening) _status = $"host :{port} (client{clientId} 切断)";
            }
        }

        private void OnPoseMessage(ulong senderClientId, FastBufferReader reader)
        {
            // 壊れた／切り詰められた Unreliable パケット 1 発で named-message dispatch が
            // 例外で死に、以後の pose 受信・リレーが止まるのを防ぐ（受信ループの堅牢性）。
            try
            {
                reader.ReadValueSafe(out ulong originId);
                PoseCodec.Read(ref reader, _rxScratch);
                // Unreliable は順序保証なし。Seq 非増加（後着・重複）は棄却する。
                // wraparound は符号付き差分で判定（uint 一周しても正しく比較できる）
                if (_lastAcceptedSeq.TryGetValue(originId, out uint last)
                    && (int)(_rxScratch.Seq - last) <= 0)
                {
                    RejectedStalePoses++;
                    return;
                }
                _lastAcceptedSeq[originId] = _rxScratch.Seq;
                var pose = GetPoseBuffer(originId);
                pose.CopyFrom(_rxScratch);
                RemotePoseReceived?.Invoke(originId, pose);

                // server はオリジン以外のクライアントへリレー（3人目の観戦者などにも将来対応）
                var nm = NetworkManager.Singleton;
                if (nm != null && nm.IsServer)
                {
                    var writer = new FastBufferWriter(PoseCodec.MaxBytes, Allocator.Temp);
                    try
                    {
                        writer.WriteValueSafe(originId);
                        PoseCodec.Write(ref writer, pose);
                        SendToAllClientsExcept(nm, ref writer, originId);
                    }
                    finally
                    {
                        writer.Dispose();
                    }
                }
            }
            catch (Exception e)
            {
                DroppedPoseMessages++;
                Debug.LogWarning($"[TableDuo] pose メッセージの処理に失敗（破棄して継続・累計{DroppedPoseMessages}）: {e.Message}");
            }
        }

        private void OnLayoutMessage(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                reader.ReadValueSafe(out ulong originId);
                var l = PoseCodec.ReadLayout(ref reader);
                var r = PoseCodec.ReadLayout(ref reader);
                _remoteLayouts[originId] = (l, r);
                Debug.Log($"[TableDuo] 手 layout を client{originId} から受信（L={l != null} R={r != null}）");
                HandLayoutReceived?.Invoke(originId);

                // server は他クライアント（観戦者・対面プレイヤー）へもリレーする
                // （pose はリレー済みなのに layout だけ host 止まりだと、観戦 PC が手寸法を持てない）
                var nm = NetworkManager.Singleton;
                if (nm != null && nm.IsServer)
                {
                    foreach (var clientId in nm.ConnectedClientsIds)
                    {
                        if (clientId == originId || clientId == nm.LocalClientId) continue;
                        SendLayoutTo(nm, clientId, originId, l, r);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TableDuo] layout メッセージの処理に失敗: {e.Message}");
            }
        }

        private static void SendToAllClientsExcept(NetworkManager nm, ref FastBufferWriter writer, ulong except)
        {
            foreach (var clientId in nm.ConnectedClientsIds)
            {
                if (clientId == except || clientId == nm.LocalClientId) continue;
                nm.CustomMessagingManager.SendNamedMessage(
                    PoseMsg, clientId, writer, NetworkDelivery.Unreliable);
            }
        }

        /// <summary>
        /// clientId の最新 pose を返す（自分=送信キャッシュ / 他人=受信バッファ）。
        /// サーバ側の掴み追従（Grabbable）が使う。
        /// </summary>
        public bool TryGetPose(ulong clientId, out AvatarPose pose)
        {
            var nm = NetworkManager.Singleton;
            if (nm != null && clientId == nm.LocalClientId)
            {
                pose = _localPose;
                return _hasLocalPose;
            }
            return _remotePoses.TryGetValue(clientId, out pose!);
        }

        /// <summary>
        /// 自分の手 bind 構造を host へ1回送る（owner が layout キャプチャ後に呼ぶ）。
        /// host 自身は静的 Captured を使うので送信不要（ローカル格納のみ）。
        /// </summary>
        public void SubmitLocalLayout(HandSkeletonLayout? left, HandSkeletonLayout? right)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening || nm.CustomMessagingManager == null) return;
            if (nm.IsServer)
            {
                _remoteLayouts[nm.LocalClientId] = (left, right);
                HandLayoutReceived?.Invoke(nm.LocalClientId);
                return;
            }
            var writer = new FastBufferWriter(PoseCodec.MaxLayoutBytes, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(nm.LocalClientId);
                PoseCodec.WriteLayout(ref writer, left);
                PoseCodec.WriteLayout(ref writer, right);
                // 両手 layout は ~1.4KB で単一パケット上限(1264B)を超える → 断片化配送が必須
                // （Reliable だと OverflowException。実機で判明 2026-06-29）
                nm.CustomMessagingManager.SendNamedMessage(
                    LayoutMsg, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
                Debug.Log("[TableDuo] 自分の手 layout を host へ送信");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TableDuo] 手 layout 送信に失敗（FK はホスト layout フォールバック）: {e.Message}");
            }
            finally
            {
                writer.Dispose();
            }
        }

        /// <summary>
        /// clientId の手 bind 構造（指先 FK 用）。自分=静的 Captured / 他人=受信済み layout。
        /// 未受信なら host 自身の layout へフォールバック（最悪でも従来挙動＝現状維持）。
        /// </summary>
        public HandSkeletonLayout? GetHandLayout(ulong clientId, bool right)
        {
            var nm = NetworkManager.Singleton;
            if (nm != null && clientId == nm.LocalClientId)
            {
                return right ? HandSkeletonLayout.CapturedR : HandSkeletonLayout.CapturedL;
            }
            if (_remoteLayouts.TryGetValue(clientId, out var pair))
            {
                var layout = right ? pair.R : pair.L;
                if (layout != null) return layout;
            }
            return right ? HandSkeletonLayout.CapturedR : HandSkeletonLayout.CapturedL;
        }

        private AvatarPose GetPoseBuffer(ulong clientId)
        {
            if (!_remotePoses.TryGetValue(clientId, out var pose))
            {
                pose = new AvatarPose();
                _remotePoses[clientId] = pose;
            }
            return pose;
        }

        private static UnityTransport GetTransport(NetworkManager nm) =>
            (UnityTransport)nm.NetworkConfig.NetworkTransport;

        private void ResolveAutoMode(out AutoMode mode, out string? ip)
        {
            mode = autoMode;
            string? m = StudyLaunchFlags.Get("tdv_mode", "-tdvMode");
            ip = StudyLaunchFlags.Get("tdv_ip", "-tdvIp");
            if (string.IsNullOrEmpty(ip)) ip = null;

            if (m == "host") mode = AutoMode.Host;
            // client 指定でも IP 無しなら自動発見（IP を調べて打つ必要をなくす）
            else if (m == "client") mode = ip != null ? AutoMode.Client : AutoMode.Discover;
            // フラグ一切無し（＝Quest ランチャーから普通に開いた）は自動発見が既定。
            // Inspector で Host/Client を焼き込んだシーン（L0 検証等）は従来どおりそちらが勝つ
            else if (m == null && mode == AutoMode.None) mode = AutoMode.Discover;
        }

        private void OnGUI()
        {
            if (!showGui) return;
            var nm = NetworkManager.Singleton;
            GUILayout.BeginArea(new Rect(10, 10, 320, 200), GUI.skin.box);
            GUILayout.Label($"TableDuo: {_status}");
            if (nm != null && nm.IsListening)
            {
                GUILayout.Label($"clients={nm.ConnectedClientsIds.Count} local={nm.LocalClientId}");
            }
            else
            {
                if (GUILayout.Button("Host")) StartHost();
                GUILayout.BeginHorizontal();
                _ipInput = GUILayout.TextField(_ipInput, GUILayout.Width(180));
                if (GUILayout.Button("Join")) StartClient(_ipInput);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndArea();
        }
    }
}
