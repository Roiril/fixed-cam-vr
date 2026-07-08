#nullable enable
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 卓上ピース物理の衝突マトリクス設定（Systems に常駐・起動時 1 回）。
    /// TableProps レイヤー（ピース + 天板コライダー）を**自分同士のみ衝突**にする。
    /// 手・アバター・カード等の他コライダーとは衝突させない
    /// （トラッキングの手にコライダーが触れると jitter で駒が爆ぜる定番事故の予防。
    /// 掴みは物理でなくピンチ方式 = 一般的な VR インタラクションのまま）。
    /// DynamicsManager（ProjectSettings）は 2 アプリ共有なので触らず、ランタイムで設定する。
    /// </summary>
    public sealed class PiecePhysicsConfig : MonoBehaviour
    {
        /// <summary>ピース・天板コライダーが乗るレイヤー名（TableDuoSceneSetup が TagManager に確保）。</summary>
        public const string LayerName = "TableProps";

        private void Awake()
        {
            int layer = LayerMask.NameToLayer(LayerName);
            if (layer < 0)
            {
                Debug.LogWarning($"[TableDuo] レイヤー {LayerName} が未定義。ピースが手・アバター等と衝突し得る" +
                                 "（Setup TableDuo Scene を再実行してレイヤーを確保すること）");
                return;
            }
            for (int i = 0; i < 32; i++)
            {
                Physics.IgnoreLayerCollision(layer, i, i != layer);
            }
            Debug.Log($"[TableDuo] PiecePhysicsConfig: レイヤー {LayerName}({layer}) を自己衝突のみに設定");
        }
    }
}
