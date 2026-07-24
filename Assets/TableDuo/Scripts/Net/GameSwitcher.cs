#nullable enable
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 卓上ボードゲームのランタイム切替（stow/show 方式）。
    /// NGO 1.x では in-scene NetworkObject の despawn/respawn・spawned オブジェクトへの SetActive が
    /// 地雷のため、全ゲームを常時 spawn したまま Renderer/Collider を off にし stow フラグで気配を消す。
    /// 掴み判定（<see cref="PinchGrabInteractor"/>）はコライダー非依存の距離検索なので、
    /// <see cref="Grabbable.IsStowed"/> で追加ゲートする（Collider off だけでは掴めてしまう）。
    ///
    /// アクティブゲームは <see cref="NetworkVariable{T}"/>（server write）で全 peer に同期。
    /// 切替はサーバ側で「旧・新 両ゲームの盤面リセット」を兼ねる（保持解放 → 初期姿勢復元 → stow 側 kinematic 化）。
    /// </summary>
    public sealed class GameSwitcher : NetworkBehaviour
    {
        /// <summary>切替時の盤面リセットに使う、各 Grabbable の初期姿勢（サーバ側キャプチャ）。</summary>
        private struct InitialPose
        {
            public Vector3 Pos;
            public Quaternion Rot;
            public Rigidbody? Rb;
        }

        [Tooltip("ゲーム別ルート（子に Grabbable プロップ群）。TableDuoSceneSetup が SerializedObject で配線")]
        [SerializeField] private GameObject[] gameRoots = System.Array.Empty<GameObject>();
        [Tooltip("ゲーム識別子（\"dsa\",\"algo\" 等）。FacilitatorMarkServer の game_<id> ラベル照合用")]
        [SerializeField] private string[] gameIds = System.Array.Empty<string>();
        [Tooltip("UI 表示名（\"海底探検\",\"アルゴ\" 等）。FacilitatorPanel のボタン用")]
        [SerializeField] private string[] displayNames = System.Array.Empty<string>();

        private readonly NetworkVariable<byte> _active = new(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // サーバ側のみ: 各 Grabbable の初期姿勢（切替時の盤面リセットに使う）。BoardReset と同じキャプチャ流儀
        private readonly Dictionary<Grabbable, InitialPose> _initial = new();
        private bool _captured;

        /// <summary>現在アクティブなゲームの index。</summary>
        public int ActiveIndex => _active.Value;

        /// <summary>登録ゲーム数。</summary>
        public int GameCount => gameRoots.Length;

        /// <summary>ゲーム index の表示名（範囲外は "?"）。</summary>
        public string GetDisplayName(int i) =>
            (i >= 0 && i < displayNames.Length) ? displayNames[i] : "?";

        /// <summary>ゲーム index の id（dsa/algo/geister/bandido/bear 等。範囲外は ""）。</summary>
        public string GetGameId(int i) =>
            (i >= 0 && i < gameIds.Length) ? gameIds[i] : "";

        /// <summary>アクティブゲームを切り替える（server 専用）。範囲外/同値は no-op。</summary>
        public void ServerSetActiveGame(int idx)
        {
            if (!IsServer)
            {
                Debug.LogWarning("[TableDuo] GameSwitcher.ServerSetActiveGame は server 専用");
                return;
            }
            if (idx < 0 || idx >= gameRoots.Length) return;
            if (idx == _active.Value) return;
            _active.Value = (byte)idx;
        }

        /// <summary>識別子でアクティブゲームを切り替える（server 専用）。見つからなければ false。</summary>
        public bool ServerSetActiveGameById(string id)
        {
            for (int i = 0; i < gameIds.Length; i++)
            {
                if (gameIds[i] == id)
                {
                    ServerSetActiveGame(i);
                    return true;
                }
            }
            return false;
        }

        public override void OnNetworkSpawn()
        {
            _active.OnValueChanged += OnActiveChanged;
            // spawn 順対策: 同フレームに他オブジェクトが未 spawn だと ApplyLocal が取りこぼす。
            // 1 フレーム遅延して初期状態を全 peer に反映する（BoardReset/TableDuoPlayer と同じ流儀）。
            StartCoroutine(InitialApplyDeferred());
        }

        public override void OnNetworkDespawn()
        {
            _active.OnValueChanged -= OnActiveChanged;
        }

        private IEnumerator InitialApplyDeferred()
        {
            yield return null;
            if (!IsSpawned) yield break;
            ApplyLocal(_active.Value);
            if (IsServer)
            {
                // 物理整合のみ（ポーズ復元はしない — ベイク時の初期位置のままのはず）:
                // 非アクティブゲームを kinematic 化し、アクティブは dynamic に戻す。
                // ただし restKinematic 物（山札等）はアクティブでも凍結のまま（下記 SetRootKinematic）
                EnsureCaptured();
                for (int i = 0; i < gameRoots.Length; i++)
                {
                    SetRootKinematic(i, stowed: i != _active.Value);
                }
            }
        }

        // BoardReset と同じく listen 中のみキャプチャ、listen 解除で破棄・再キャプチャ（シーン再ロード対応）
        private void Update()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening || !nm.IsServer)
            {
                _captured = false;
                _initial.Clear();
                return;
            }
            if (!_captured) CaptureInitial();
        }

        private void EnsureCaptured()
        {
            if (!_captured) CaptureInitial();
        }

        private void CaptureInitial()
        {
            _captured = true;
            _initial.Clear();
            foreach (var root in gameRoots)
            {
                if (root == null) continue;
                foreach (var grab in root.GetComponentsInChildren<Grabbable>(includeInactive: true))
                {
                    _initial[grab] = new InitialPose
                    {
                        Pos = grab.transform.position,
                        Rot = grab.transform.rotation,
                        Rb = grab.GetComponent<Rigidbody>(),
                    };
                }
            }
            Debug.Log($"[TableDuo] GameSwitcher: 初期姿勢を記憶（{_initial.Count} 個）");
        }

        private void OnActiveChanged(byte prev, byte cur)
        {
            ApplyLocal(cur);
            if (!IsServer) return;
            EnsureCaptured();
            // 切替 = 両ゲームの盤面リセット。順序: 保持解放 → 初期姿勢復元 → isKinematic 切替
            ServerRestoreRoot(prev, stow: true);
            ServerRestoreRoot(cur, stow: false);
        }

        /// <summary>全 peer: 指定 index のゲームだけ見せ、他は Renderer/Collider off + stow フラグで隠す。</summary>
        private void ApplyLocal(int idx)
        {
            for (int i = 0; i < gameRoots.Length; i++)
            {
                var root = gameRoots[i];
                if (root == null) continue;
                bool shown = i == idx;
                foreach (var r in root.GetComponentsInChildren<Renderer>(includeInactive: true)) r.enabled = shown;
                foreach (var c in root.GetComponentsInChildren<Collider>(includeInactive: true)) c.enabled = shown;
                foreach (var g in root.GetComponentsInChildren<Grabbable>(includeInactive: true)) g.SetStowed(!shown);
            }
        }

        /// <summary>サーバ: 指定ルート配下 Grabbable を初期姿勢へ戻し、stow なら kinematic 化する。</summary>
        private void ServerRestoreRoot(int idx, bool stow)
        {
            if (idx < 0 || idx >= gameRoots.Length || gameRoots[idx] == null) return;
            foreach (var grab in gameRoots[idx].GetComponentsInChildren<Grabbable>(includeInactive: true))
            {
                if (grab.IsHeld) grab.ServerForceRelease("gameSwitch");
                if (_initial.TryGetValue(grab, out var init))
                {
                    grab.transform.SetPositionAndRotation(init.Pos, init.Rot);
                    if (init.Rb != null)
                    {
                        init.Rb.velocity = Vector3.zero;
                        init.Rb.angularVelocity = Vector3.zero;
                        init.Rb.position = init.Pos;
                        init.Rb.rotation = init.Rot;
                    }
                }
                var rb = grab.GetComponent<Rigidbody>();
                // stow 中は必ず凍結。アクティブ化時も restKinematic 物（山札等）は凍結のまま
                // （dynamic に戻すと薄板 16 段が沈み込んで貫入する。掴めば Grabbable が個別に dynamic 化する）
                if (rb != null) rb.isKinematic = stow || grab.RestKinematic;
            }
        }

        /// <summary>サーバ: 指定ルート配下 Grabbable の Rigidbody を kinematic 切替（初期 apply の物理整合用）。
        /// stowed=false（アクティブ）でも restKinematic 物は凍結のまま維持する。</summary>
        private void SetRootKinematic(int idx, bool stowed)
        {
            if (idx < 0 || idx >= gameRoots.Length || gameRoots[idx] == null) return;
            foreach (var grab in gameRoots[idx].GetComponentsInChildren<Grabbable>(includeInactive: true))
            {
                var rb = grab.GetComponent<Rigidbody>();
                if (rb != null) rb.isKinematic = stowed || grab.RestKinematic;
            }
        }
    }
}
