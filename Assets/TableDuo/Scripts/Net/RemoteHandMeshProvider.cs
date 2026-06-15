#nullable enable
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// リモートの手を Meta の白い手メッシュ（OVRCustomHandPrefab）で描くためのプレハブ/マテリアル供給。
    /// Setup が Meta パッケージの L/R カスタムハンドプレハブとローカル手と同じ URP マテリアルを割り当てる。
    /// 参照が無い場合 RemoteAvatarView は従来のカプセル手にフォールバックする。
    /// </summary>
    public sealed class RemoteHandMeshProvider : MonoBehaviour
    {
        public static RemoteHandMeshProvider? Instance { get; private set; }

        [SerializeField] private GameObject? leftHandPrefab;
        [SerializeField] private GameObject? rightHandPrefab;
        [Tooltip("ローカル手と同じ白い URP マテリアル（メッシュがマゼンタ化しないよう明示割当）")]
        [SerializeField] private Material? handMaterial;

        public Material? HandMaterial => handMaterial;

        public GameObject? GetPrefab(bool isRight) => isRight ? rightHandPrefab : leftHandPrefab;

        private void Awake() => Instance = this;
        private void OnDestroy() { if (Instance == this) Instance = null; }
    }
}
