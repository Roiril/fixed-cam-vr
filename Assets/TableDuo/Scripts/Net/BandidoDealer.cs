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

            // 1. スロットを位置から分類（子順序に依存しない = レイアウト変更に頑健）:
            //    同一 XZ（1mm 以内）を共有する 2 枚以上のグループ = 山札スタック、単独 XZ = 手札
            var handPoses = new List<(Vector3 pos, Quaternion rot)>();
            var deckPoses = new List<(Vector3 pos, Quaternion rot)>();
            for (int i = 0; i < n; i++)
            {
                bool shared = false;
                for (int j = 0; j < n; j++)
                {
                    if (i == j) continue;
                    float dx = _slots[i].pos.x - _slots[j].pos.x;
                    float dz = _slots[i].pos.z - _slots[j].pos.z;
                    if (dx * dx + dz * dz < 1e-6f) { shared = true; break; } // (1mm)^2
                }
                if (shared) deckPoses.Add((_slots[i].pos, _slots[i].rot));
                else handPoses.Add((_slots[i].pos, _slots[i].rot));
            }
            // 手札を z でソート（席分け）: 前半 3 = 一方の席・後半 3 = もう一方（cz 不要）
            handPoses.Sort((a, b) => a.pos.z.CompareTo(b.pos.z));

            // 2. カード分類（名前 prefix）
            var gCards = new List<Grabbable>();
            var lCards = new List<Grabbable>();
            foreach (var s in _slots)
            {
                if (s.card == null) continue;
                if (s.card.name.StartsWith("BANDIDO_l", System.StringComparison.Ordinal)) lCards.Add(s.card);
                else gCards.Add(s.card); // g・unknown はまとめて g 扱い
            }
            ShuffleCards(gCards);
            ShuffleCards(lCards);

            // 3. 割当を構築
            var assign = new List<(Grabbable card, Vector3 pos, Quaternion rot)>();
            if (handPoses.Count == 6)
            {
                // 各席の手札構成: 「l×1 + g×2」or「g×3」を 50/50（ベイクと同じ制約をランタイムでも保証）
                for (int seat = 0; seat < 2; seat++)
                {
                    bool useL = Random.Range(0, 2) == 0 && lCards.Count > 0;
                    int b = seat * 3;
                    if (useL)
                    {
                        assign.Add((PopCard(lCards), handPoses[b].pos, handPoses[b].rot));
                        assign.Add((PopCard(gCards), handPoses[b + 1].pos, handPoses[b + 1].rot));
                        assign.Add((PopCard(gCards), handPoses[b + 2].pos, handPoses[b + 2].rot));
                    }
                    else
                    {
                        for (int k = 0; k < 3; k++)
                            assign.Add((PopCard(gCards), handPoses[b + k].pos, handPoses[b + k].rot));
                    }
                }
                // 山札 = 残りをまとめてシャッフルして積む
                var rest = new List<Grabbable>();
                rest.AddRange(gCards);
                rest.AddRange(lCards);
                ShuffleCards(rest);
                for (int i = 0; i < deckPoses.Count && i < rest.Count; i++)
                    assign.Add((rest[i], deckPoses[i].pos, deckPoses[i].rot));
            }
            else
            {
                // フォールバック（想定外レイアウト = 手札スロットが 6 でない）: 制約なしで全 permute
                Debug.LogWarning($"[TableDuo] BandidoDealer: 手札スロットが 6 個でない（{handPoses.Count}）→ 制約なし配り");
                var all = new List<Grabbable>();
                all.AddRange(gCards);
                all.AddRange(lCards);
                ShuffleCards(all);
                int idx = 0;
                foreach (var (pos, rot) in handPoses) if (idx < all.Count) assign.Add((all[idx++], pos, rot));
                foreach (var (pos, rot) in deckPoses) if (idx < all.Count) assign.Add((all[idx++], pos, rot));
            }

            // 4. transform 書き込み
            foreach (var (card, pos, rot) in assign)
            {
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
            Debug.Log($"[TableDuo] BandidoDealer: 配り直し（{assign.Count} 枚・手札構成制約つき）");
        }

        private static void ShuffleCards(List<Grabbable> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private static Grabbable PopCard(List<Grabbable> list)
        {
            int last = list.Count - 1;
            var v = list[last];
            list.RemoveAt(last);
            return v;
        }
    }
}
