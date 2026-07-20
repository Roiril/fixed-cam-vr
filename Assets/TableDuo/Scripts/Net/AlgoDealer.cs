#nullable enable
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// アルゴ（卓上ボードゲーム）の完全ランダム配り直し（ホストのみ）。
    ///
    /// スロット = ベイク時の 24 姿勢の集合（山札 16 + 手役の手札 4 + 人役の手札 4）。
    /// 山札・手札の**位置**はベイク済みスロットに固定し、**どのカードがどこに置かれるか**だけを
    /// ランタイムで完全ランダム化する（白黒の区別なし＝ユーザー要望「白黒も完全ランダム」）。
    /// レイアウトの知識（どれが山札でどれが手札か）はランタイムに持たない — ベイク姿勢がそのまま
    /// スロットで、Fisher-Yates でカード→スロットの割当を permute するだけ。
    ///
    /// <see cref="GameSwitcher"/> のゲーム切替はベイク時の決定的配置に戻す（配りは保持されない）ので、
    /// アルゴ開始時にホストが配り直しを押す運用。アルゴが stow 中（非アクティブ）でも実行可
    /// （Rigidbody は kinematic のまま姿勢だけ動き、次に表示された時に反映済み）。
    ///
    /// サーバ権威 NetworkTransform なので、サーバが transform を書けば全員に同期される。
    /// TableDuoSceneSetup が Systems へ AddComponent し、algoRoot を SerializedObject で配線する。
    /// </summary>
    public sealed class AlgoDealer : MonoBehaviour
    {
        [Tooltip("アルゴのルート（子に 24 枚の Grabbable カード）。TableDuoSceneSetup が SerializedObject で配線")]
        [SerializeField] private GameObject? algoRoot;

        // サーバ側のみ: スロット = ベイク時のカード姿勢の集合。BoardReset と同じキャプチャ流儀
        //（listen 中のみ採取、listen 解除で破棄・次の listen で再採取。シーン再ロード対応）
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
            if (algoRoot == null)
            {
                Debug.LogWarning("[TableDuo] AlgoDealer: algoRoot 未配線（配り直し不可）");
                return;
            }
            foreach (var grab in algoRoot.GetComponentsInChildren<Grabbable>(includeInactive: true))
            {
                var t = grab.transform;
                _slots.Add((grab, t.position, t.rotation));
            }
            Debug.Log($"[TableDuo] AlgoDealer: スロットを記憶（{_slots.Count} 枚）");
        }

        /// <summary>全カードをスロットへ完全ランダムに配り直す（server 専用）。保持中は強制解放してから配る。</summary>
        public void ServerShuffleDeal()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || !_captured)
            {
                Debug.LogWarning("[TableDuo] AlgoDealer: server 以外/未キャプチャでは実行不可");
                return;
            }
            int n = _slots.Count;
            if (n == 0)
            {
                Debug.LogWarning("[TableDuo] AlgoDealer: スロットが空（algoRoot 配下に Grabbable 無し）");
                return;
            }

            // カード→スロットの割当を Fisher-Yates で完全ランダムに permute（白黒の区別なし）。
            // perm[i] = スロット i に置くカードの元 index
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
                if (card.IsHeld) card.ServerForceRelease("algoDeal");
                card.transform.SetPositionAndRotation(pos, rot);
                // 物理カードは運動も止めて配置姿勢で静置する（velocity が残ると即滑り出す）
                var rb = card.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    rb.position = pos;
                    rb.rotation = rot;
                }
                // 再配置した山札はクリーンな凍結スタックへ戻す（restKinematic ピースのみ効く）。
                // これをしないと、一度離して dynamic 化したカードが配り直し後も沈み込む
                card.ServerSettleKinematic();
            }
            Debug.Log($"[TableDuo] AlgoDealer: 配り直し（{n} 枚）");
        }
    }
}
