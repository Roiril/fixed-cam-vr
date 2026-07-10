#nullable enable
using TableDuoVr.Hands;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 自分のローカル手（OVRHandPrefab）をバリアント（Realistic/Robot）メッシュで表示する。
    /// Default 時は何もせず Meta 白手をそのまま見せる。
    ///
    /// 駆動方針: バリアントメッシュを **手アンカー（skeleton.transform ＝ OVRHand と同じ GameObject）**の子に
    /// 吊るし、手首 bone (i=0) 含む全 bone を毎フレーム skeleton のローカル回転からバインド差分リターゲット
    /// （<see cref="HandRetarget"/>）で駆動する。検証済みのリモート/プレビュー経路
    /// （RemoteHandView / TableDuoHandVariantPreview: anchor=wristRot ＋ i=0 から全 retarget）と 1:1 の構造。
    /// リモート手と同じ材質・スケール・配置ロジック（<see cref="RemoteHandMeshProvider.BuildExternalHand"/>）を共有する。
    ///
    /// ⚠ ライブ手首 *bone*（Bones[0].Transform）に identity で吊るしてはいけない — アンカー回転 ×
    /// Bones[0].localRotation が階層で既に乗るため、パックリグの手首 bind 差が未補正のまま出て
    /// 手首が約90°ズレ・指の曲げ軸も観察上崩れる（2026-07-10 実機で実害）。
    /// </summary>
    public sealed class LocalVariantHand : MonoBehaviour
    {
        [SerializeField] private OVRSkeleton? skeleton;
        [SerializeField] private bool isRight;
        [Tooltip("OVRHandPrefab の白手 SkinnedMeshRenderer。バリアント表示中は隠す")]
        [SerializeField] private SkinnedMeshRenderer? metaMesh;

        private RemoteHandMeshProvider.BuiltHand? _built;
        private HandVariant _builtVariant = HandVariant.Default;
        private bool _buildFailed; // 構築失敗ラッチ（毎フレ Instantiate+Destroy の 90Hz チャーン防止。バリアント切替でクリア）
        private bool _subscribed;
        private OVRMeshRenderer? _metaMeshRenderer; // 白手の可視制御主体（metaMesh と同 GameObject・遅延解決）

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
            SetMetaHandVisible(true); // 隠したまま無効化されないよう戻す
        }

        private void OnVariantChanged()
        {
            Teardown();
            _buildFailed = false;
            ApplyVariantVisibility();
        }

        private void ApplyVariantVisibility()
        {
            bool external = HandVariantTable.IsExternalRig(StudyConfig.SelectedHandVariant);
            SetMetaHandVisible(!external); // 外部リグ時は白手を隠す（構築は LateUpdate）
        }

        /// <summary>Meta 白手の表示/非表示。SMR だけ切っても OVRMeshRenderer.Update の
        /// ConfidenceBehavior.ToggleRenderer が毎フレーム enabled を復活させるため、
        /// 上流の OVRMeshRenderer ごと止める（LocalSelfBody→HandPoseSampler と同じ手法）。</summary>
        private void SetMetaHandVisible(bool visible)
        {
            if (metaMesh == null) return;
            if (_metaMeshRenderer == null) _metaMeshRenderer = metaMesh.GetComponent<OVRMeshRenderer>();
            if (_metaMeshRenderer != null) _metaMeshRenderer.enabled = visible;
            metaMesh.enabled = visible;
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
                if (_buildFailed && _builtVariant == variant) return; // 失敗ラッチ（切替まで再試行しない）
                Teardown();
                _builtVariant = variant;
                // 手アンカー（回転=送信 wristRot と同じフレーム）に吊るす。[TDV-WRIST] 実測で
                // アンカーと実手首 bone の位置差は delta=0 のため位置整列もこれで足りる
                _built = provider.BuildExternalHand(skeleton.transform, isRight, variant);
                SetMetaHandVisible(false);
                if (_built == null) { _buildFailed = true; return; }
            }

            // 全 bone をリターゲット（bone[0]=手首 も Solve でフレーム補正を載せる。
            // bone[1]=前腕は HandVariantTable 未マップ → bones[1]=null で自動 skip）。
            var bones = _built.Bones;
            var varBind = _built.VarBind;
            var live = skeleton.Bones;
            var bind = skeleton.BindPoses;
            int n = Mathf.Min(bones.Length, live.Count);
            for (int i = 0; i < n; i++)
            {
                if (bones[i] == null) continue;
                Quaternion ovrBind = (bind != null && i < bind.Count)
                    ? bind[i].Transform.localRotation : Quaternion.identity;
                bones[i]!.localRotation = HandRetarget.Solve(live[i].Transform.localRotation, ovrBind, varBind[i]);
            }
        }
    }
}
