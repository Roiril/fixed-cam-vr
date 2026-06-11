#nullable enable
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 絵カードの分析用メタデータ。表面の向き（法線）を SessionLogger に提供する。
    /// RQ2「カードを手のどこへ向けるか」= 保持中の表面法線 × 手ランドマーク方向の角度分析。
    /// </summary>
    public sealed class CardProp : MonoBehaviour
    {
        [SerializeField] private string cardId = "";
        [Tooltip("カード表面の法線（ローカル）。Setup が表面 Quad の向きに合わせて設定する")]
        [SerializeField] private Vector3 faceNormalLocal = Vector3.up;

        public string CardId => cardId;

        /// <summary>表面法線（ワールド）。</summary>
        public Vector3 FaceNormalWorld => transform.TransformDirection(faceNormalLocal).normalized;

        public Grabbable? Grabbable { get; private set; }

        private void Awake()
        {
            Grabbable = GetComponent<Grabbable>();
        }
    }
}
