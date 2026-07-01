#nullable enable
using TableDuoVr.Hands;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 手メッシュのプレハブ/マテリアル供給 + バリアント別の bone マッピングと外部リグ構築。
    ///
    /// - Default : Meta の白い手メッシュ（OVRCustomHandPrefab）。Setup が L/R プレハブとローカル手と同じ URP マテリアルを割り当てる。
    /// - Realistic / Robot : 購入パック（VR Hands Starter Pack）の Male / Robot 手。別リグ命名なので
    ///   <see cref="HandVariantTable"/> で BoneId→bone 名を引き当て、<see cref="HandRetarget"/> でバインド差分駆動する。
    ///
    /// 参照が無い場合、呼び出し側（<see cref="RemoteAvatarView"/> / <see cref="LocalVariantHand"/>）は
    /// Default のカプセル手/Meta 手にフォールバックする。
    /// </summary>
    public sealed class RemoteHandMeshProvider : MonoBehaviour
    {
        public static RemoteHandMeshProvider? Instance { get; private set; }

        [Header("Default（Meta 白手 / OVRCustomHandPrefab）")]
        [SerializeField] private GameObject? leftHandPrefab;
        [SerializeField] private GameObject? rightHandPrefab;
        [Tooltip("ローカル手と同じ白い URP マテリアル（メッシュがマゼンタ化しないよう明示割当）")]
        [SerializeField] private Material? handMaterial;

        [Header("Realistic（人間の手 / VR Hands Starter Pack: Male Hand）")]
        [SerializeField] private GameObject? realisticLeftPrefab;
        [SerializeField] private GameObject? realisticRightPrefab;
        [Tooltip("パックの Standard マテリアルは URP でマゼンタ化するため、URP/Lit で肌テクスチャを割り当てた材質で全 Renderer を上書きする")]
        [SerializeField] private Material? realisticMaterial;

        [Header("Robot（機械の手 / VR Hands Starter Pack: Robot Hand）")]
        [SerializeField] private GameObject? robotLeftPrefab;
        [SerializeField] private GameObject? robotRightPrefab;
        [Tooltip("同上（金属 URP/Lit）。ロボットは多数の分割メッシュだが 1 材質で全上書きして統一する")]
        [SerializeField] private Material? robotMaterial;

        /// <summary>手首→中指遠位関節の実寸目安（m）。外部リグ手をこの長さに自動スケールする。</summary>
        private const float RefHandLenMeters = 0.15f;

        public Material? HandMaterial => handMaterial;

        public GameObject? GetPrefab(bool isRight, HandVariant variant) => variant switch
        {
            HandVariant.Realistic => isRight ? realisticRightPrefab : realisticLeftPrefab,
            HandVariant.Robot => isRight ? robotRightPrefab : robotLeftPrefab,
            _ => isRight ? rightHandPrefab : leftHandPrefab,
        };

        public Material? GetMaterial(HandVariant variant) => variant switch
        {
            HandVariant.Realistic => realisticMaterial,
            HandVariant.Robot => robotMaterial,
            _ => handMaterial,
        };

        private void Awake() => Instance = this;
        private void OnDestroy() { if (Instance == this) Instance = null; }

        // --- 手メッシュの bone マッピング（BoneId 順）---
        // Default(Meta) の OVRCustomHandPrefab は OVRCustomSkeleton.CustomBones が未マッピング（全 null）で
        // 出荷されるため、CustomBones に頼らず FBX 命名規則で bone Transform を実体検索する。
        // バリアント別命名は <see cref="HandVariantTable"/> に集約（drift 防止）。

        /// <summary>
        /// 手メッシュ階層から BoneId 順（<see cref="AvatarPose.BonesPerHand"/> 個）の Transform 配列を作る。
        /// 見つからない bone は null（呼び出し側でスキップ）。同期 boneRots の index と一致する。
        /// </summary>
        public static Transform?[] MapHandBonesByName(Transform root, bool isRight, HandVariant variant)
        {
            var map = new Transform?[AvatarPose.BonesPerHand];
            for (int i = 0; i < map.Length; i++)
            {
                var name = HandVariantTable.BoneName(variant, i, isRight);
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

        /// <summary>外部リグ手（Realistic/Robot）の構築結果。BoneId 順の bone とバインドローカル回転を持つ。</summary>
        public sealed class BuiltHand
        {
            public GameObject Instance = null!;
            public Transform?[] Bones = null!;   // BoneId 順（未マップは null）
            public Quaternion[] VarBind = null!; // BoneId 順のメッシュ側バインドローカル回転（未マップは identity）
        }

        /// <summary>
        /// パックの手プレハブを parent 下に生成し、駆動可能な状態にして返す（Realistic/Robot 用）。
        /// - bone を BoneId 順にマッピング（1 個も当たらなければ失敗 → null）
        /// - メッシュ側バインドローカル回転を控える（リターゲット基準）
        /// - 手首→中指遠位で実寸に自動スケール
        /// - 手首 bone を parent 原点へ整列（パックのメッシュは原点からオフセットしているため）
        /// - コライダー除去・全 Renderer を variant 材質で上書き（Standard 材質のマゼンタ化を回避）
        /// 失敗時は生成物を破棄して null。
        /// </summary>
        public BuiltHand? BuildExternalHand(Transform parent, bool isRight, HandVariant variant)
        {
            var prefab = GetPrefab(isRight, variant);
            if (prefab == null) return null;

            var inst = Object.Instantiate(prefab, parent, worldPositionStays: false);
            inst.transform.localPosition = Vector3.zero;
            inst.transform.localRotation = Quaternion.identity;
            inst.transform.localScale = Vector3.one;
            inst.SetActive(true);

            var bones = MapHandBonesByName(inst.transform, isRight, variant);
            bool anyMapped = false;
            foreach (var b in bones) { if (b != null) { anyMapped = true; break; } }
            if (!anyMapped) { Object.Destroy(inst); return null; }

            var bind = new Quaternion[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                bind[i] = bones[i] != null ? bones[i]!.localRotation : Quaternion.identity;
            }

            // 自動スケール: 手首→中指遠位（無ければ人差し指）でパック手を実寸に合わせる
            var wrist = bones[0];
            var tip = bones[11] ?? bones[8] ?? bones[7];
            if (wrist != null && tip != null)
            {
                float meshLen = Vector3.Distance(wrist.position, tip.position);
                if (meshLen > 1e-5f)
                {
                    inst.transform.localScale = Vector3.one * (RefHandLenMeters / meshLen);
                }
            }
            // スケール後に手首 bone を parent 原点へ整列（parent=手首アンカー。以後 parent が動くと手も追従）
            if (wrist != null)
            {
                inst.transform.position += parent.position - wrist.position;
            }

            foreach (var col in inst.GetComponentsInChildren<Collider>(true)) Object.Destroy(col);

            var mat = GetMaterial(variant);
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
            {
                if (mat != null)
                {
                    // submesh 数ぶん同じ材質で埋めて全スロットのマゼンタ化を防ぐ
                    int slots = Mathf.Max(1, r.sharedMaterials.Length);
                    var mats = new Material[slots];
                    for (int k = 0; k < slots; k++) mats[k] = mat;
                    r.sharedMaterials = mats;
                }
                if (r is SkinnedMeshRenderer smr)
                {
                    smr.updateWhenOffscreen = true;
                    smr.enabled = true;
                }
            }

            return new BuiltHand { Instance = inst, Bones = bones, VarBind = bind };
        }
    }
}
