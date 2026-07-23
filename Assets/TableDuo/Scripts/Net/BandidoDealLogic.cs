#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// バンディド配札アルゴリズムの純ロジック（GameObject/UnityEngine.Random 非依存）。
    /// BandidoDealer.ServerShuffleDeal の分類→席分け→手札制約→山札充填を抽出。
    ///
    /// スロット = カード（同一 index）。位置は固定でカードだけを再割当するので、返り値は
    /// <c>assign[slotIndex] = cardIndex</c> の全単射。カード実体（Grabbable）↔ Kind/位置 の変換は
    /// MonoBehaviour（BandidoDealer）が担う。
    ///
    /// 手札構成制約（ベイクと同一）: 各席の手札 3 枚は「l×1 + g×2」or「g×3」を rng で 50/50。
    /// l 枯渇時は g×3 に落ちる。乱数列の bit 一致は元々未固定なので不問（有効な配札 = 制約充足 + 全単射を保存）。
    ///
    /// 乱数は <c>rng(maxExclusive) → [0, maxExclusive)</c> のデリゲート注入
    /// （本番 <c>m => UnityEngine.Random.Range(0, m)</c>、テストは seeded System.Random）。
    /// </summary>
    public static class BandidoDealLogic
    {
        public enum Kind { G, L }

        // 山札スタック検出: 同一 XZ（1mm 以内）を共有する 2 枚以上 = 山札 / 単独 = 手札
        private const float SharedXZSqr = 1e-6f; // (1mm)^2

        /// <summary>
        /// 同 XZ(1mm 以内) を共有する 2 枚以上 = 山札 / 単独 = 手札。
        /// handSlots は z 昇順にソート（席分け用・入力順に依存しない）、deckSlots は入力順を維持。
        /// 両出力リストは呼び出し前にクリアされる。
        /// </summary>
        public static void ClassifySlots(IReadOnlyList<Vector3> pos, List<int> handSlots, List<int> deckSlots)
        {
            if (pos == null) throw new System.ArgumentNullException(nameof(pos));
            if (handSlots == null) throw new System.ArgumentNullException(nameof(handSlots));
            if (deckSlots == null) throw new System.ArgumentNullException(nameof(deckSlots));
            handSlots.Clear();
            deckSlots.Clear();
            int n = pos.Count;
            for (int i = 0; i < n; i++)
            {
                bool shared = false;
                for (int j = 0; j < n; j++)
                {
                    if (i == j) continue;
                    float dx = pos[i].x - pos[j].x;
                    float dz = pos[i].z - pos[j].z;
                    if (dx * dx + dz * dz < SharedXZSqr) { shared = true; break; }
                }
                if (shared) deckSlots.Add(i);
                else handSlots.Add(i);
            }
            // 手札を z でソート（席分け）: 前半 3 = 一方の席・後半 3 = もう一方
            handSlots.Sort((a, b) => pos[a].z.CompareTo(pos[b].z));
        }

        /// <summary>
        /// 分類 → 席分け(前半3/後半3) → 手札制約(l×1+g×2 or g×3 を rng で 50/50・l 枯渇時は g×3)
        /// → 山札 = 残りを再シャッフルして deckSlots 順に充填。
        /// handSlots.Count != 6 は制約なし全 permute にフォールバック。
        /// 返り値 assign[slot] = card（全単射。カード枚数 &lt; スロット数の端は -1 が残りうる — 実バケでは発生しない）。
        /// </summary>
        public static int[] Assign(IReadOnlyList<Vector3> slotPositions, IReadOnlyList<Kind> cardKinds, System.Func<int, int> rng)
        {
            if (slotPositions == null) throw new System.ArgumentNullException(nameof(slotPositions));
            if (cardKinds == null) throw new System.ArgumentNullException(nameof(cardKinds));
            if (rng == null) throw new System.ArgumentNullException(nameof(rng));

            int n = slotPositions.Count;
            var assign = new int[n];
            for (int i = 0; i < n; i++) assign[i] = -1;
            if (n == 0) return assign;

            // 1. スロットを位置から分類
            var handSlots = new List<int>();
            var deckSlots = new List<int>();
            ClassifySlots(slotPositions, handSlots, deckSlots);

            // 2. カードを Kind で index 群に分けてシャッフル
            var gCards = new List<int>();
            var lCards = new List<int>();
            int cardCount = cardKinds.Count;
            for (int i = 0; i < cardCount; i++)
            {
                if (cardKinds[i] == Kind.L) lCards.Add(i);
                else gCards.Add(i); // G・unknown はまとめて g 扱い
            }
            DealShuffle.ShuffleInPlace(gCards, rng);
            DealShuffle.ShuffleInPlace(lCards, rng);

            // 3. 割当
            if (handSlots.Count == 6)
            {
                for (int seat = 0; seat < 2; seat++)
                {
                    bool useL = rng(2) == 0 && lCards.Count > 0;
                    int b = seat * 3;
                    if (useL)
                    {
                        assign[handSlots[b]] = PopLast(lCards);
                        assign[handSlots[b + 1]] = PopLast(gCards);
                        assign[handSlots[b + 2]] = PopLast(gCards);
                    }
                    else
                    {
                        for (int k = 0; k < 3; k++)
                            assign[handSlots[b + k]] = PopLast(gCards);
                    }
                }
                // 山札 = 残り g+l を再シャッフルして充填
                var rest = new List<int>();
                rest.AddRange(gCards);
                rest.AddRange(lCards);
                DealShuffle.ShuffleInPlace(rest, rng);
                for (int i = 0; i < deckSlots.Count && i < rest.Count; i++)
                    assign[deckSlots[i]] = rest[i];
            }
            else
            {
                // フォールバック（手札スロットが 6 でない）: 制約なしで全 permute
                var all = new List<int>();
                all.AddRange(gCards);
                all.AddRange(lCards);
                DealShuffle.ShuffleInPlace(all, rng);
                int idx = 0;
                for (int i = 0; i < handSlots.Count; i++) if (idx < all.Count) assign[handSlots[i]] = all[idx++];
                for (int i = 0; i < deckSlots.Count; i++) if (idx < all.Count) assign[deckSlots[i]] = all[idx++];
            }
            return assign;
        }

        private static int PopLast(List<int> list)
        {
            int last = list.Count - 1;
            int v = list[last];
            list.RemoveAt(last);
            return v;
        }
    }
}
