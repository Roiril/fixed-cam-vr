#nullable enable
using TableDuoVr.Hands;
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

        // --- 手メッシュの bone マッピング（BoneId 順）---
        // OVRCustomHandPrefab は OVRCustomSkeleton.CustomBones が package 同梱状態では未マッピング
        // （全 null）。本来は Editor の "Auto Map Bones" が埋めるが空のまま出荷されている。
        // そのため CustomBones に頼らず、Meta の FBX 命名規則（<see cref="HandBoneTable"/>）で
        // bone Transform を実体検索する。命名/順序は HandBoneTable に集約（drift 防止）。

        /// <summary>
        /// 手メッシュ階層から BoneId 順（<see cref="AvatarPose.BonesPerHand"/> 個）の Transform 配列を作る。
        /// 見つからない bone は null（呼び出し側でスキップ）。同期 boneRots の index と一致する。
        /// </summary>
        public static Transform?[] MapHandBonesByName(Transform root, bool isRight)
        {
            var map = new Transform?[AvatarPose.BonesPerHand];
            for (int i = 0; i < map.Length; i++)
            {
                var name = HandBoneTable.FbxBoneName(i, isRight);
                if (name != null) map[i] = FindChildRecursive(root, name);
            }
            return map;
        }

        private static Transform? FindChildRecursive(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindChildRecursive(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
