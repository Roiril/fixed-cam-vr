#nullable enable
using Unity.Netcode;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 海底探検のサイコロ（確定表示方式）。物理転がしはせず、**離した瞬間にサーバが出目（1–3）を確定**し、
    /// 全員に同じ数字をダイス上方に表示する。出目の読み取り齟齬（「今の 2？3？」）が起きず、
    /// CSV にも自動で残る（ゲーム進行＝ラウンド構造の事後復元に使える）。
    /// 実機の投擲物理（リリース速度 → Rigidbody impulse）は体験の質が足りなければ後日オプション。
    /// Grabbable と同じ GameObject に付ける（TableDuoSceneSetup が配線）。
    /// </summary>
    public sealed class DiceRoller : NetworkBehaviour
    {
        /// <summary>サーバ側で出目が確定した（dieName, rollerClientId, value）。SessionLogger が CSV に刻む。</summary>
        public static event System.Action<string, ulong, int>? DiceRolled;

        // 0 = 未ロール（表示なし）。海底探検のダイスは 1/2/3 が 2 面ずつの d6 相当
        private readonly NetworkVariable<byte> _value = new(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private Grabbable? _grab;
        private bool _wasHeld;
        private ulong _lastHolder;
        private TextMesh? _label;
        private Transform? _labelRoot;

        private void Awake()
        {
            _grab = GetComponent<Grabbable>();
            BuildLabel();
        }

        // 出目表示: ダイス上方に浮かぶ数字（全クライアントローカル生成・値だけ同期）
        private void BuildLabel()
        {
            _labelRoot = new GameObject("DiceValue").transform;
            _labelRoot.SetParent(transform, false);
            _labelRoot.localPosition = new Vector3(0f, 0.09f, 0f);
            var go = new GameObject("Text");
            go.transform.SetParent(_labelRoot, false);
            _label = go.AddComponent<TextMesh>();
            _label.anchor = TextAnchor.MiddleCenter;
            _label.alignment = TextAlignment.Center;
            _label.fontSize = 64;
            _label.characterSize = 0.02f; // fontSize64 × 0.02 ≈ 高さ 4cm 弱の数字
            _label.color = new Color(0.95f, 0.9f, 0.3f);
            _labelRoot.gameObject.SetActive(false);
        }

        private void Update()
        {
            // サーバ: 「持っていた → 離した」遷移で出目を確定（振る動作の終わり＝ロール）
            if (IsSpawned && IsServer && _grab != null)
            {
                bool held = _grab.IsHeld;
                if (held) _lastHolder = _grab.HolderClientId;
                if (_wasHeld && !held)
                {
                    int v = Random.Range(1, 4);
                    _value.Value = (byte)v;
                    Debug.Log($"[TableDuo] Dice {name} → {v}（client{_lastHolder}）");
                    DiceRolled?.Invoke(name, _lastHolder, v);
                }
                _wasHeld = held;
            }

            // 全員: 保持中は隠し、確定後は表示 + カメラへビルボード
            if (_label == null || _labelRoot == null) return;
            bool show = _value.Value > 0 && (_grab == null || !_grab.IsHeld);
            if (_labelRoot.gameObject.activeSelf != show) _labelRoot.gameObject.SetActive(show);
            if (!show) return;
            _label.text = _value.Value.ToString();
            var cam = Camera.main;
            if (cam != null)
            {
                _labelRoot.rotation = Quaternion.LookRotation(_labelRoot.position - cam.transform.position);
            }
        }
    }
}
