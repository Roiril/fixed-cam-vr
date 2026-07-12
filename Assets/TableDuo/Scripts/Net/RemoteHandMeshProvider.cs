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

        /// <summary>Meta 白手（authored bind ポーズのまま）を container 下に生成し、隠しリファレンスとして返す。
        /// ⚠ layout の親子 FK や BindLocalRot から OVR 側基準を「計算」してはいけない —
        ///   layout の bind は live ストリームの中立ではなく（pinky0 で ~173° 乖離）、メッシュ実階層には
        ///   中間ノードもあり得る（FK 合成と実ワールド回転が ~100° 乖離、いずれも 2026-07-11 実測）。
        ///   正解は「白手そのものを live で駆動し、その実ワールド回転を参照する」こと。
        /// 駆動系（OVRSkeleton/OVRHand/Animator）を剥がし全 Renderer を無効化して返す。失敗時 null。</summary>
        private GameObject? BuildMetaReference(Transform container, bool isRight,
            out Transform?[] metaBones, out Quaternion wristFrame)
        {
            metaBones = System.Array.Empty<Transform?>();
            wristFrame = Quaternion.identity;
            var metaPrefab = GetPrefab(isRight, HandVariant.Default);
            if (metaPrefab == null) return null;
            var tmp = Object.Instantiate(metaPrefab, container, worldPositionStays: false);
            tmp.name = "MetaRefHidden";
            tmp.transform.localPosition = Vector3.zero;
            tmp.transform.localRotation = Quaternion.identity;
            var mb = MapHandBonesByName(tmp.transform, isRight, HandVariant.Default);
            var w = mb[0]; var idx = mb[6]; var mid = mb[9];
            var pnk = mb[16] != null ? mb[16] : mb[15];
            if (w == null || idx == null || mid == null || pnk == null)
            {
                if (Application.isPlaying) Object.Destroy(tmp); else Object.DestroyImmediate(tmp);
                return null;
            }
            Vector3 pW = container.InverseTransformPoint(w.position);
            Vector3 pI = container.InverseTransformPoint(idx.position);
            Vector3 pM = container.InverseTransformPoint(mid.position);
            Vector3 pP = container.InverseTransformPoint(pnk.position);
            wristFrame = HandRetarget.WristFrame(pW, pI, pM, pP, isRight);

            // live トラッキング/アニメの自走を止め、描画も消す（bone Transform だけ使う）
            foreach (var sk in tmp.GetComponents<OVRSkeleton>()) { if (Application.isPlaying) Object.Destroy(sk); else Object.DestroyImmediate(sk); }
            foreach (var h in tmp.GetComponents<OVRHand>()) { if (Application.isPlaying) Object.Destroy(h); else Object.DestroyImmediate(h); }
            var anim = tmp.GetComponent<Animator>();
            if (anim != null) { if (Application.isPlaying) Object.Destroy(anim); else Object.DestroyImmediate(anim); }
            foreach (var r in tmp.GetComponentsInChildren<Renderer>(true)) r.enabled = false;

            metaBones = mb;
            return tmp;
        }

        /// <summary>外部リグ手（Realistic/Robot）の構築結果。
        /// Instance はコンテナ（パック手 + 隠し Meta リファレンスを内包。破棄はこれ 1 個で済む）。
        /// 駆動は「MetaBones に live ローカル回転を流し込み → 実ワールド回転 × BoneOffsets を Bones へコピー」
        /// （<see cref="HandRetarget.ApplyFromReference"/>）。リグの軸規約・階層差の仮定が一切無い。</summary>
        public sealed class BuiltHand
        {
            public GameObject Instance = null!;      // コンテナ（pack inst + MetaRefHidden）
            public Transform?[] Bones = null!;       // パック側 bone（BoneId 順・未マップは null）
            public Transform?[] MetaBones = null!;   // 隠し白手 bone（BoneId 順・正解系の参照）
            public Quaternion[] BoneOffsets = null!; // C_i = inv(metaWorld_i) * packWorld_i（整列済み bind で捕捉・定数）
        }

        /// <summary>
        /// パックの手プレハブを parent 下に生成し、駆動可能な状態にして返す（Realistic/Robot 用）。
        /// - bone を BoneId 順にマッピング（1 個も当たらなければ失敗 → null）
        /// - メッシュ側バインドローカル回転を控える（リターゲット基準）
        /// - 手首→中指遠位で実寸に自動スケール
        /// - **手首幾何フレーム整列**: パックの authored 休めポーズ方向（指方向×甲法線）を
        ///   Meta authored 白手の方向に回して合わせる（「手のひらの後ろ方向に生える」2026-07-11 実害の根治）
        /// - 手首 bone を parent 原点へ整列（パックのメッシュは原点からオフセットしているため）
        /// - 整列後の **アンカー相対 bind 回転**（VarBindAnchorRel）を捕捉 → ワールドデルタ式リターゲットの基準
        /// - コライダー除去・全 Renderer を variant 材質で上書き（Standard 材質のマゼンタ化を回避）
        /// 失敗時は生成物を破棄して null。
        /// </summary>
        public BuiltHand? BuildExternalHand(Transform parent, bool isRight, HandVariant variant)
        {
            var prefab = GetPrefab(isRight, variant);
            if (prefab == null) return null;

            // コンテナ（pack inst と隠し白手リファレンスを同居させ、破棄を 1 個にまとめる）
            var container = new GameObject("HandVariant_" + variant);
            container.transform.SetParent(parent, worldPositionStays: false);
            container.transform.localPosition = Vector3.zero;
            container.transform.localRotation = Quaternion.identity;
            container.transform.localScale = Vector3.one;

            var inst = Object.Instantiate(prefab, container.transform, worldPositionStays: false);
            inst.transform.localPosition = Vector3.zero;
            inst.transform.localRotation = Quaternion.identity;
            inst.transform.localScale = Vector3.one;
            inst.SetActive(true);

            var bones = MapHandBonesByName(inst.transform, isRight, variant);
            bool anyMapped = false;
            foreach (var b in bones) { if (b != null) { anyMapped = true; break; } }

            // 正解系リファレンス: 隠し Meta 白手（authored bind のまま・非表示）
            Transform?[] metaBones = System.Array.Empty<Transform?>();
            Quaternion fOvr = Quaternion.identity;
            var metaRef = anyMapped ? BuildMetaReference(container.transform, isRight, out metaBones, out fOvr) : null;
            if (!anyMapped || metaRef == null)
            {
                if (Application.isPlaying) Object.Destroy(container); else Object.DestroyImmediate(container);
                return null;
            }

            // 自動スケール: 手首→中指遠位（無ければ人差し指）でパック手を**白手リファレンスの実測長**に合わせる。
            // 固定値 RefHandLenMeters だと白手と微妙にサイズが違って見える（2026-07-12 実機指摘）。
            // 同コンテナの白手から同じ 2 点間距離を測って合わせれば、切替時のサイズが白手と一致する。
            var wrist = bones[0];
            int tipIdx = bones[11] != null ? 11 : (bones[8] != null ? 8 : 7);
            var tip = bones[tipIdx];
            if (wrist != null && tip != null)
            {
                float meshLen = Vector3.Distance(wrist.position, tip.position);
                float targetLen = RefHandLenMeters; // 白手側で同 2 点が測れない時のフォールバック
                var mW = metaBones[0];
                var mTip = tipIdx < metaBones.Length ? metaBones[tipIdx] : null;
                if (mW != null && mTip != null)
                {
                    float metaLen = Vector3.Distance(mW.position, mTip.position);
                    if (metaLen > 1e-5f) targetLen = metaLen;
                }
                if (meshLen > 1e-5f)
                {
                    inst.transform.localScale = Vector3.one * (targetLen / meshLen);
                }
            }
            // 手首幾何フレーム整列: パック手の「指方向×甲法線」を白手（authored）のそれに回して合わせる。
            // パックの authored 休めポーズは白手基準と向きが違う（Male/Robot で各々バラバラ）ため、
            // これ無しでは静止時から手が明後日の方向に生える（2026-07-11 実害の根治）。
            {
                var idx1 = bones[6]; var mid1 = bones[9];
                var pnk1 = bones[16] != null ? bones[16] : bones[15];
                if (wrist != null && idx1 != null && mid1 != null && pnk1 != null)
                {
                    Vector3 pW = container.transform.InverseTransformPoint(wrist.position);
                    Vector3 pI = container.transform.InverseTransformPoint(idx1.position);
                    Vector3 pM = container.transform.InverseTransformPoint(mid1.position);
                    Vector3 pP = container.transform.InverseTransformPoint(pnk1.position);
                    var fVar = HandRetarget.WristFrame(pW, pI, pM, pP, isRight);
                    inst.transform.localRotation = fOvr * Quaternion.Inverse(fVar) * inst.transform.localRotation;
                }
            }
            // スケール・整列後に手首 bone を parent 原点へ整列（parent=手首アンカー。以後 parent が動くと手も追従）
            if (wrist != null)
            {
                inst.transform.position += parent.position - wrist.position;
            }

            // 定数オフセット捕捉: C_i = inv(metaWorld_i) * packWorld_i（両者いま同義の bind ポーズ）。
            // 以後の駆動は「metaBones を live で回す → metaWorld_i * C_i を pack へコピー」だけ。
            var offsets = new Quaternion[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                offsets[i] = (bones[i] != null && i < metaBones.Length && metaBones[i] != null)
                    ? Quaternion.Inverse(metaBones[i]!.rotation) * bones[i]!.rotation
                    : Quaternion.identity;
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

            return new BuiltHand { Instance = container, Bones = bones, MetaBones = metaBones, BoneOffsets = offsets };
        }
    }
}
