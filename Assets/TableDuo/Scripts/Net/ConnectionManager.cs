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
    /// LAN 直結（手動 IP）のホスト/クライアント接続管理 + pose の named message 配送。
    /// 配送経路: owner → (client なら server へ) → server が他クライアントへリレー。
    /// 起動引数 / Android intent extras（tdv_mode=host|client, tdv_ip=...）で UI 無し自動接続可:
    ///   adb shell am start -n <pkg>/com.unity3d.player.UnityPlayerActivity -e tdv_mode host
    /// </summary>
    public sealed class ConnectionManager : MonoBehaviour
    {
        public static ConnectionManager? Instance { get; private set; }

        public enum AutoMode
        {
            None,
            Host,
            Client,
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

        /// <summary>手 layout を受信/格納した（originClientId）。SessionLogger が CSV に刻み、
        /// 解析側が「どこから本人の手寸法で FK されるか」の境界を判別できるようにする（study-validity）。</summary>
        public event Action<ulong>? HandLayoutReceived;

        /// <summary>壊れた pose メッセージを破棄した累計。SessionLogger が変化を event 行に刻む
        /// （破棄フレームが記録に残らず「凍結 vs 欠落」を判別できなくなるのを防ぐ）。</summary>
        public int DroppedPoseMessages { get; private set; }

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
            }
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

        public void StartHost()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.IsListening) return;
            GetTransport(nm).SetConnectionData("0.0.0.0", port, "0.0.0.0");
            if (nm.StartHost())
            {
                RegisterHandler(nm);
                _status = $"host :{port}";
                Debug.Log($"[TableDuo] Host 開始 port={port}");
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
            }
        }

        // 切断時: その client の stale バッファを掃除し、状態表示を更新（再接続の導線を確保）。
        // クライアント側は NGO の DisconnectReason をログして原因究明を容易にする
        // （観戦者が接続後に落ちる等の切り分け用。理由が空なら transport/timeout）。
        private void OnClientDisconnected(ulong clientId)
        {
            _remotePoses.Remove(clientId);
            _remoteLayouts.Remove(clientId);
            var nm = NetworkManager.Singleton;
            if (nm == null) return;
            if (!nm.IsServer)
            {
                string reason = string.IsNullOrEmpty(nm.DisconnectReason)
                    ? "(理由なし＝transport/timeout か config 不一致)" : nm.DisconnectReason;
                Debug.LogWarning($"[TableDuo] サーバから切断された: {reason}");
                _status = $"切断: {reason}";
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
                var pose = GetPoseBuffer(originId);
                PoseCodec.Read(ref reader, pose);
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
            if (m == "host") mode = AutoMode.Host;
            else if (m == "client") mode = AutoMode.Client;
            ip = StudyLaunchFlags.Get("tdv_ip", "-tdvIp");
            if (string.IsNullOrEmpty(ip)) ip = null;
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
