#nullable enable
using TableDuoVr.Hands;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 人役の一人称自己アバター（tdv_selfbody=on）。相手に見えるのと同じ Remy をもう1体、
    /// 自分の席下に生成し、**ローカル pose（ネット非経由）**で毎フレーム駆動する。頭ボーンを潰して
    /// 視界を塞がず、胴・腕・手（Remy 手）が下を向くと見える＝身体所有感。
    ///
    /// - 駆動 pose は <see cref="HandPoseSourceRegistry.Best"/>（= 送信しているのと同じ席フレーム採取値）。
    ///   相手に見える自分との差はネット平滑の有無だけ。
    /// - ローカルの Meta 白手メッシュはレンダラーだけ隠す（OVRHand/OVRSkeleton は生かす＝トラッキング・
    ///   ピンチ・送信は無変化）。掴み判定は描画と独立なので機能は劣化しない。
    /// - HandPoseSampler（実行順 0）が LateUpdate で採取した後に駆動するため [DefaultExecutionOrder(100)]。
    /// ローカル描画専用＝相手に見える自分（ネット越しの Remy）は不変。
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class LocalSelfBody : MonoBehaviour
    {
        private RemyAvatarRig? _rig;
        private IHandPoseSource? _source;
        private HandSkeletonLayout? _layoutL, _layoutR;

        /// <summary>
        /// 席アンカー下に一人称 Remy を生成し、ローカル白手メッシュを隠す。
        /// prefab 不在（Remy 未取り込み）なら自己ボディ無し（従来通り＝手だけ見える）でフォールバック。
        /// </summary>
        public void Initialize(Transform seat, GameObject remyPrefab, SkinnedMeshRenderer[] localHandRenderers)
        {
            _rig = new RemyAvatarRig(seat, remyPrefab, firstPerson: true);
            foreach (var smr in localHandRenderers)
            {
                if (smr != null) smr.enabled = false; // 白手はレンダラーのみ非表示（駆動系は生かす）
            }
            Debug.Log("[TableDuo] 一人称自己アバター ON（頭を潰した Remy をローカル pose で駆動 / 白手メッシュ非表示）");
        }

        private void LateUpdate()
        {
            if (_rig == null) return;
            _source ??= HandPoseSourceRegistry.Best;
            if (_source == null || !_source.IsValid)
            {
                _source = HandPoseSourceRegistry.Best;
                if (_source == null) return;
            }
            // 自分の layout（本人の手寸法）を ovrBind に使う。指リターゲットの基準
            _layoutL ??= HandSkeletonLayout.CapturedL;
            _layoutR ??= HandSkeletonLayout.CapturedR;
            _rig.Drive(_source.Current, _layoutL ?? HandSkeletonLayout.CapturedL,
                _layoutR ?? HandSkeletonLayout.CapturedR);
        }
    }
}
