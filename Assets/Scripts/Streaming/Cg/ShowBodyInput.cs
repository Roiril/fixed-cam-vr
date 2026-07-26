#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming.Cg
{
    /// <summary>
    /// CG 人形を動かすための体験者の身体入力（**ワールド座標**）。
    ///
    /// 供給元は <c>Assets/Scripts/OvrBridge/OvrHandTrackingBridge.cs</c>（Assembly-CSharp）で、
    /// <see cref="ShowCgLayer.SetBodyInput"/> へ毎フレーム push する。**Streaming asmdef は OVR を参照しない**
    /// という既存規約（Tracking→Streaming の delegate 注入と同じ向き）を守るための境界がこの型。
    ///
    /// 手の**回転は持たない**。OVR の手 basis は素直でなく（[[table_duo_wrist_anchor_basis]] の実害）、
    /// 640x480 の監視カメラ画質で手首の捻りは見えないため、位置だけを使う。
    /// 設計の正本: <c>.claude/plans/2026-07-27_cg-actor-hand-tracking.md</c>。
    /// </summary>
    public readonly struct ShowBodyInput
    {
        public readonly bool HasHead;
        public readonly Vector3 HeadPos;
        /// <summary>頭の向き（ワールド yaw・度）。follow の人形の体の向きに使う。</summary>
        public readonly float HeadYawDeg;

        public readonly bool LeftValid;
        public readonly Vector3 LeftHandPos;
        public readonly bool RightValid;
        public readonly Vector3 RightHandPos;

        public ShowBodyInput(bool hasHead, Vector3 headPos, float headYawDeg,
                             bool leftValid, Vector3 leftHandPos,
                             bool rightValid, Vector3 rightHandPos)
        {
            HasHead = hasHead;
            HeadPos = headPos;
            HeadYawDeg = headYawDeg;
            LeftValid = leftValid;
            LeftHandPos = leftHandPos;
            RightValid = rightValid;
            RightHandPos = rightHandPos;
        }

        /// <summary>何も取れていない状態（人形は idle ポーズで立ち続ける）。</summary>
        public static ShowBodyInput None => default;
    }
}
