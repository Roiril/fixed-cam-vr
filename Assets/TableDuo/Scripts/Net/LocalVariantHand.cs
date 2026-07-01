#nullable enable
using TableDuoVr.Hands;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 自分のローカル手（OVRHandPrefab）をバリアント（Realistic/Robot）メッシュで表示する。
    /// Default 時は何もせず Meta 白手をそのまま見せる。
    ///
    /// 駆動方針: バリアントメッシュを **ライブ skeleton の手首 bone の子**に吊るす。手首の位置・向きは
    /// 親（手首 bone）の追従で自動的に付き、指だけを毎フレーム skeleton のローカル回転からバインド差分
    /// リターゲット（<see cref="HandRetarget"/>）で曲げる。リモート手（<see cref="RemoteAvatarView"/>）と
    /// 同じ材質・スケール・配置ロジック（<see cref="RemoteHandMeshProvider.BuildExternalHand"/>）を共有する。
    ///
    /// ⚠ 手首の向きは親追従なので、バリアントリグのバインド軸が OVR とずれると定数分だけ傾く可能性がある
    /// （指の曲がり軸同様、実機で最終確認）。
    /// </summary>
    public sealed class LocalVariantHand : MonoBehaviour
    {
        [SerializeField] private OVRSkeleton? skeleton;
        [SerializeField] private bool isRight;
        [Tooltip("OVRHandPrefab の白手 SkinnedMeshRenderer。バリアント表示中は隠す")]
        [SerializeField] private SkinnedMeshRenderer? metaMesh;

        private RemoteHandMeshProvider.BuiltHand? _built;
        private HandVariant _builtVariant = HandVariant.Default;
        private bool _subscribed;

        private void OnEnable()
        {
            StudyConfig.HandVariantChanged += OnVariantChanged;
            _subscribed = true;
            ApplyVariantVisibility();
        }

        private void OnDisable()
        {
            if (_subscribed) { StudyConfig.HandVariantChanged -= OnVariantChanged; _subscribed = false; }
            Teardown();
            if (metaMesh != null) metaMesh.enabled = true; // 隠したまま無効化されないよう戻す
        }

        private void OnVariantChanged()
        {
            Teardown();
            ApplyVariantVisibility();
        }

        private void ApplyVariantVisibility()
        {
            bool external = HandVariantTable.IsExternalRig(StudyConfig.SelectedHandVariant);
            if (metaMesh != null) metaMesh.enabled = !external; // 外部リグ時は白手を隠す（構築は LateUpdate）
        }

        private void Teardown()
        {
            if (_built != null)
            {
                if (_built.Instance != null) Destroy(_built.Instance);
                _built = null;
            }
        }

        private void LateUpdate()
        {
            var variant = StudyConfig.SelectedHandVariant;
            if (!HandVariantTable.IsExternalRig(variant))
            {
                if (_built != null) Teardown();
                return;
            }
            if (skeleton == null || !skeleton.IsInitialized || skeleton.Bones.Count == 0) return;

            var provider = RemoteHandMeshProvider.Instance;
            if (provider == null) return;

            if (_built == null || _builtVariant != variant)
            {
                Teardown();
                _builtVariant = variant;
                var wristBone = skeleton.Bones[0].Transform; // ライブ手首 bone。ここに吊るせば追従は自動
                _built = provider.BuildExternalHand(wristBone, isRight, variant);
                if (metaMesh != null) metaMesh.enabled = false;
                if (_built == null) return;
            }

            // 指（BoneId>=2）をリターゲット。手首(0)/前腕(1)は親の手首 bone 追従に任せる（二重回転回避）。
            var bones = _built.Bones;
            var varBind = _built.VarBind;
            var live = skeleton.Bones;
            var bind = skeleton.BindPoses;
            int n = Mathf.Min(bones.Length, live.Count);
            for (int i = 2; i < n; i++)
            {
                if (bones[i] == null) continue;
                Quaternion ovrBind = (bind != null && i < bind.Count)
                    ? bind[i].Transform.localRotation : Quaternion.identity;
                bones[i]!.localRotation = HandRetarget.Solve(live[i].Transform.localRotation, ovrBind, varBind[i]);
            }
        }
    }
}
