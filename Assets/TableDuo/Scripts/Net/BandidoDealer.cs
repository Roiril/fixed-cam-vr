#nullable enable
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// バンディド（トンネル札）のランダム配り直し（ホストのみ。AlgoDealer と同型）。
    ///
    /// スロット = ベイク時の姿勢の集合（各席の手札 3×2 + 山札 25）。**開始札 BANDIDO_bandy は混ぜない**
    /// （卓中央に固定・格子の不動点なので配りの対象外）。位置はベイク済みスロットに固定し、
    /// **どのカードがどこに置かれるか**だけをランタイムで再割当する。
    ///
    /// 手札構成制約（ベイクと同一）: 各席の手札 3 枚は「l×1 + g×2」or「g×3」の 50/50。
    /// カード分類は名前 prefix（BANDIDO_l* / BANDIDO_g*）。手札 vs 山札スロットの識別は**子順序に依存せず
    /// 位置から**行う（同一 XZ を共有する 2 枚以上 = 山札スタック、残り 6 個 = 手札。レイアウト変更に頑健）。
    /// 各席の割当は手札スロットを z でソートして前半/後半に分ける（cz を知らずに席分けできる）。
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

        /// <summary>開始札を除く全カードを手札構成制約つきで配り直す（server 専用）。保持中は強制解放してから配る。</summary>
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

            // 分類→席分け→手札制約→山札充填は BandidoDealLogic（純ロジック）へ委譲。
            // カード実体（Grabbable）↔ 位置/Kind の変換だけをここで担う。
            // カード分類（名前 prefix）→ Kind 列、位置列を構築（スロット = カード = 同一 index）。
            var slotPositions = new Vector3[n];
            var cardKinds = new BandidoDealLogic.Kind[n];
            for (int i = 0; i < n; i++)
            {
                slotPositions[i] = _slots[i].pos;
                var c = _slots[i].card;
                cardKinds[i] = (c != null && c.name.StartsWith("BANDIDO_l", System.StringComparison.Ordinal))
                    ? BandidoDealLogic.Kind.L
                    : BandidoDealLogic.Kind.G; // g・unknown・null はまとめて g 扱い
            }

            // フォールバック警告（手札スロットが 6 でない）の観測挙動を保存（分類は決定的・rng 非依存）。
            var handSlots = new List<int>();
            var deckSlots = new List<int>();
            BandidoDealLogic.ClassifySlots(slotPositions, handSlots, deckSlots);
            if (handSlots.Count != 6)
                Debug.LogWarning($"[TableDuo] BandidoDealer: 手札スロットが 6 個でない（{handSlots.Count}）→ 制約なし配り");

            // assign[slot] = card（全単射）。本番の乱数源は UnityEngine.Random.Range を注入。
            int[] assign = BandidoDealLogic.Assign(slotPositions, cardKinds, m => Random.Range(0, m));

            // transform 書き込み: スロット i の姿勢へ assign[i] のカードを置く
            int placed = 0;
            for (int i = 0; i < n; i++)
            {
                int cardIdx = assign[i];
                if (cardIdx < 0) continue;
                var card = _slots[cardIdx].card;
                if (card == null) continue;
                var pos = _slots[i].pos;
                var rot = _slots[i].rot;
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
                placed++;
            }
            Debug.Log($"[TableDuo] BandidoDealer: 配り直し（{placed} 枚・手札構成制約つき）");
        }
    }
}
