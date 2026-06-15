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
        }

        [SerializeField] private string defaultAddress = "192.168.1.10";
        [SerializeField] private ushort port = 7777;
        [SerializeField] private AutoMode autoMode = AutoMode.None;
        [SerializeField] private bool showGui = true;

        [Header("Study（Editor 検証用。実機は tdv_* extras が優先）")]
        [SerializeField] private RoleOverride studyRole = RoleOverride.None;
        [SerializeField] private bool showHeadMarker;
        [SerializeField] private bool oneHandMode = true;

        private const string PoseMsg = "tdv_pose";
        private readonly Dictionary<ulong, AvatarPose> _remotePoses = new();
        private readonly AvatarPose _localPose = new();
        private bool _hasLocalPose;
        private string _ipInput = "";
        private string _status = "idle";

        /// <summary>リモートプレイヤーの pose を受信した（originClientId, pose）。pose は使い回しバッファ。</summary>
        public event Action<ulong, AvatarPose>? RemotePoseReceived;

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
                _ => null,
            };
            StudyConfig.ShowHeadMarker = showHeadMarker;
            StudyConfig.OneHandMode = oneHandMode;
            ApplyStudyFlags();
            if (StudyConfig.LaunchedWithStudyFlags)
            {
                showGui = false; // 調査セッションではデバッグ GUI を見せない
            }
        }

        /// <summary>tdv_role / tdv_marker / tdv_hands を intent extras（実機）/ コマンドライン（PC）から読む。</summary>
        private void ApplyStudyFlags()
        {
            string? role = GetLaunchValue("tdv_role", "-tdvRole");
            if (role == "full") StudyConfig.ForcedRole = StudyConfig.Role.Full;
            else if (role == "hand") StudyConfig.ForcedRole = StudyConfig.Role.Hand;
            if (role != null) StudyConfig.LaunchedWithStudyFlags = true;

            string? marker = GetLaunchValue("tdv_marker", "-tdvMarker");
            if (marker == "on") StudyConfig.ShowHeadMarker = true;
            else if (marker == "off") StudyConfig.ShowHeadMarker = false;

            string? hands = GetLaunchValue("tdv_hands", "-tdvHands");
            if (hands == "one") StudyConfig.OneHandMode = true;
            else if (hands == "two") StudyConfig.OneHandMode = false;

            if (StudyConfig.LaunchedWithStudyFlags)
            {
                Debug.Log($"[TableDuo] StudyConfig: role={StudyConfig.ForcedRole} marker={StudyConfig.ShowHeadMarker} oneHand={StudyConfig.OneHandMode}");
            }
        }

        private static string? GetLaunchValue(string extraKey, string cmdKey)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var intent = activity.Call<AndroidJavaObject>("getIntent");
                return intent.Call<string>("getStringExtra", extraKey);
            }
            catch (Exception)
            {
                return null;
            }
#else
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == cmdKey) return args[i + 1];
            }
            return null;
#endif
        }

        private void Start()
        {
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
            // 再接続で前セッションの clientId 別 pose バッファが残ると stale 姿勢が混入するためクリア。
            // named message handler は session 終了で NGO 側が破棄するので毎 start 登録でよい
            _remotePoses.Clear();
            _hasLocalPose = false;
            nm.CustomMessagingManager.RegisterNamedMessageHandler(PoseMsg, OnPoseMessage);
        }

        private void OnPoseMessage(ulong senderClientId, FastBufferReader reader)
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
            ip = null;
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var intent = activity.Call<AndroidJavaObject>("getIntent");
                string m = intent.Call<string>("getStringExtra", "tdv_mode");
                string i = intent.Call<string>("getStringExtra", "tdv_ip");
                if (m == "host") mode = AutoMode.Host;
                else if (m == "client") mode = AutoMode.Client;
                if (!string.IsNullOrEmpty(i)) ip = i;
            }
            catch (Exception)
            {
                // intent 無し起動は通常フロー
            }
#else
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-tdvMode")
                {
                    if (args[i + 1] == "host") mode = AutoMode.Host;
                    else if (args[i + 1] == "client") mode = AutoMode.Client;
                }
                else if (args[i] == "-tdvIp")
                {
                    ip = args[i + 1];
                }
            }
#endif
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
