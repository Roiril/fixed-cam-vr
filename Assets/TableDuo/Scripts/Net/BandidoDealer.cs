#nullable enable
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// バンディド（トンネル札）の完全ランダム配り直し（ホストのみ。AlgoDealer と同型）。
    ///
    /// スロット = ベイク時の姿勢の集合（各席の手札 3×2 + 山札 25）。**開始札 BANDIDO_bandy は混ぜない**
    /// （卓中央に固定・格子原点の定義なので配りの対象外）。位置はベイク済みスロットに固定し、
    /// **どのカードがどこに置かれるか**だけをランタイムで Fisher-Yates で permute する。
    ///
    /// カードは Rigidbody なし（physics:false = kinematic 追従 + BandidoCardSnap）なので velocity ゼロ化・
    /// ServerSettleKinematic は防御的（あれば呼ぶ）で、通常は transform 書き込みだけで足りる。
    /// サーバ権威 NetworkTransform なのでサーバが transform を書けば全員に同期される。
    /// TableDuoSceneSetup が Systems へ AddComponent し、bandidoRoot を SerializedObject で配線する。
    /// </summary>
    public sealed class BandidoDealer : MonoBehaviour
    {
        [Tooltip("バンディドのルート（子に Grabbable カード群）。TableDuoSceneSetup が SerializedObject で配線")]
        [SerializeField] private GameObject? bandidoRoot;

        // 開始札の名前（配りスロットから除外）
        private const string StartCardName = "BANDIDO_bandy";

        // サーバ側のみ: スロット = ベイク時のカード姿勢の集合（開始札を除く）。BoardReset と同じキャプチャ流儀
        private readonly List<(Grabbable card, Vector3 pos, Quaternion rot)> _slots = new();
        private bool _captured;

        private void Update()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening || !nm.IsServer)
            {
                _captured = false;
                _slots.Clear();
                return;
            }
            if (!_captured) Capture();
        }

        private void Capture()
        {
            _captured = true;
            _slots.Clear();
            if (bandidoRoot == null)
            {
                Debug.LogWarning("[TableDuo] BandidoDealer: bandidoRoot 未配線（配り直し不可）");
                return;
            }
            foreach (var grab in bandidoRoot.GetComponentsInChildren<Grabbable>(includeInactive: true))
            {
                if (grab.name == StartCardName) continue; // 開始札は混ぜない
                var t = grab.transform;
                _slots.Add((grab, t.position, t.rotation));
            }
            Debug.Log($"[TableDuo] BandidoDealer: スロットを記憶（{_slots.Count} 枚・開始札除く）");
        }

        /// <summary>開始札を除く全カードをスロットへ完全ランダムに配り直す（server 専用）。保持中は強制解放してから配る。</summary>
        public void ServerShuffleDeal()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || !_captured)
            {
                Debug.LogWarning("[TableDuo] BandidoDealer: server 以外/未キャプチャでは実行不可");
                return;
            }
            int n = _slots.Count;
            if (n == 0)
            {
                Debug.LogWarning("[TableDuo] BandidoDealer: スロットが空（bandidoRoot 配下に配り対象の Grabbable 無し）");
                return;
            }

            // カード→スロットの割当を Fisher-Yates で完全ランダムに permute。perm[i] = スロット i に置くカードの元 index
            var perm = new int[n];
            for (int i = 0; i < n; i++) perm[i] = i;
            for (int i = n - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                (perm[i], perm[j]) = (perm[j], perm[i]);
            }

            for (int i = 0; i < n; i++)
            {
                var card = _slots[perm[i]].card;
                var pos = _slots[i].pos;
                var rot = _slots[i].rot;
                if (card == null) continue;
                if (card.IsHeld) card.ServerForceRelease("bandidoDeal");
                card.transform.SetPositionAndRotation(pos, rot);
                // 物理カード（Rigidbody あり）だった場合の防御。バンディドは physics:false なので通常は無害な no-op
                var rb = card.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    rb.position = pos;
                    rb.rotation = rot;
                }
                card.ServerSettleKinematic();
            }
            Debug.Log($"[TableDuo] BandidoDealer: 配り直し（{n} 枚）");
        }
    }
}
