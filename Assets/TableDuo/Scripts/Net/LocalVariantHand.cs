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
        private readonly Quaternion[] _liveLocals = new Quaternion[TableDuoVr.Hands.AvatarPose.BonesPerHand]; // 毎フレの live ローカル回転（参照コピー式の入力）

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
            // 白手を見せるのは Default（と selfBody off の FullBody）だけ。Realistic/Robot はパック手が、
            // FullBody は自己ボディ（LocalSelfBody の Remy 手）が代替する。判定は TableDuoPlayer 側の
            // リコンサイルと同じ述語（HandPresentation）を共有し、購読順に依存しない
            SetMetaHandVisible(HandPresentation.WhiteHandVisible(
                StudyConfig.SelectedHandVariant, StudyConfig.ShowSelfBody));
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
                // アンカーと実手首 bone の位置差は delta=0 のため位置整列もこれで足りる。
                // 手首幾何フレーム整列（生える向きの根治）と隠し白手リファレンスの構築は provider 内で行う。
                // restPose（OVR bind）を渡してオフセット捕捉を rest 基準にする（静止時に指が曲がる残差の根治）
                var restPose = isRight ? HandSkeletonLayout.CapturedR : HandSkeletonLayout.CapturedL;
                // サイズ基準 = 表示中のライブ白手（OVRHandPrefab・HandScale で実手サイズに追従）の
                // 中指チェーン実測長（world・剛体セグメント和でポーズ不変）。これに pack を合わせると
                // 実手サイズと一致する（固定リファレンスに合わせると小さく見える 2026-07-12 実機指摘の根治）
                float liveLen = MiddleChainWorldLen(skeleton.Bones);
                _built = provider.BuildExternalHand(skeleton.transform, isRight, variant, restPose, liveLen);
                SetMetaHandVisible(false);
                if (_built == null) { _buildFailed = true; return; }
            }

            // 参照コピー式リターゲット（HandRetarget.ApplyFromReference）:
            // 隠し白手に live ローカル回転を流し込み、その実ワールド回転×定数オフセットをパック bone へコピー。
            // 式A（親相対バインド差分）はリグ間の軸規約差で曲げ軸が壊れた（2026-07-11 実機実害:
            // Robot 右曲がり / Realistic 逆曲がり・手が後ろ向きに生える）ため全廃。
            var live = skeleton.Bones;
            int n = Mathf.Min(_liveLocals.Length, live.Count);
            for (int i = 0; i < n; i++) _liveLocals[i] = live[i].Transform.localRotation;
            for (int i = n; i < _liveLocals.Length; i++) _liveLocals[i] = Quaternion.identity;
            HandRetarget.ApplyFromReference(_liveLocals, _built.MetaBones, _built.Bones,
                _built.BoneOffsets, smooth: 1f);
        }

        /// <summary>中指チェーン（wrist0→middle1(9)→middle2(10)→middle3(11)）の world セグメント長の和。
        /// 骨長は剛体でポーズに依らないので、指を曲げていても正しい「手の長さ」が測れる。
        /// bone 不足時は 0（呼び出し側はフォールバックへ）。</summary>
        private static float MiddleChainWorldLen(System.Collections.Generic.IList<OVRBone> bones)
        {
            if (bones == null || bones.Count <= 11) return 0f;
            var p0 = bones[0].Transform.position;
            var p9 = bones[9].Transform.position;
            var p10 = bones[10].Transform.position;
            var p11 = bones[11].Transform.position;
            return Vector3.Distance(p0, p9) + Vector3.Distance(p9, p10) + Vector3.Distance(p10, p11);
        }
    }
}
