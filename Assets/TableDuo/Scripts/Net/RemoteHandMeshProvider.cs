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
        // そのため CustomBones に頼らず、Meta の FBX 命名規則で bone Transform を実体検索する。
        // 命名規則は OVRCustomSkeletonEditor.FbxBoneNameFromBoneId（legacy Hand）と一致させること。

        /// <summary>BoneId 0..18（skinnable）の FBX 名（"b_<side>" を前置）。送信側 OVRSkeleton.Bones の並びと一致。</summary>
        private static readonly string[] SkinnableBoneNames =
        {
            "wrist", "forearm_stub",
            "thumb0", "thumb1", "thumb2", "thumb3",
            "index1", "index2", "index3",
            "middle1", "middle2", "middle3",
            "ring1", "ring2", "ring3",
            "pinky0", "pinky1", "pinky2", "pinky3",
        };

        /// <summary>BoneId 19..23（指先マーカー）の名前要素（"<side>" 前置 + "_finger_tip_marker"）。</summary>
        private static readonly string[] FingerTipNames = { "thumb", "index", "middle", "ring", "pinky" };

        /// <summary>
        /// 手メッシュ階層から BoneId 順（<see cref="AvatarPose.BonesPerHand"/> 個）の Transform 配列を作る。
        /// 見つからない bone は null（呼び出し側でスキップ）。同期 boneRots の index と一致する。
        /// </summary>
        public static Transform?[] MapHandBonesByName(Transform root, bool isRight)
        {
            var map = new Transform?[AvatarPose.BonesPerHand];
            string side = isRight ? "r_" : "l_";
            for (int i = 0; i < SkinnableBoneNames.Length; i++)
            {
                map[i] = FindChildRecursive(root, "b_" + side + SkinnableBoneNames[i]);
            }
            for (int i = 0; i < FingerTipNames.Length; i++)
            {
                int boneId = SkinnableBoneNames.Length + i; // 19..23
                if (boneId >= map.Length) break;
                map[boneId] = FindChildRecursive(root, side + FingerTipNames[i] + "_finger_tip_marker");
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
