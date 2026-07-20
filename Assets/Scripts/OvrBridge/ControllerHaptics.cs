#nullable enable
using FixedCamVr.Input;
using UnityEngine;

namespace FixedCamVr.OvrBridge
{
    /// <summary>
    /// 右コントローラ（RTouch）の触覚（振動）フィードバックを毎フレーム駆動する橋渡し。
    /// asmdef を持たない（Assembly-CSharp）ため OVRInput にアクセスできる。パターンの時間進行は純ロジック
    /// <see cref="HapticSequenceLogic"/> に委譲し、ここは毎フレーム <c>OVRInput.SetControllerVibration</c> を
    /// RTouch へ適用するだけ（OVRInput の振動は呼び続けないと約 2 秒で自動停止するため、非再生時も
    /// (0,0) を毎フレーム送って停止を保証する）。
    ///
    /// 公開 API（OvrControllerBridge / 各コントローラが呼ぶ）:
    ///   <see cref="Ack"/> / <see cref="Action"/> / <see cref="Fire"/> / <see cref="Error"/> /
    ///   <see cref="SetHoldProgress"/>（progress 0 で HoldTick 停止）。
    /// HMD は体験者が装着している場面ではスタッフに視覚が見えないため、操作の受理・進行・発火・失敗を
    /// この振動で伝える（計画 2026-07-20 触覚フィードバック節）。
    /// </summary>
    public sealed class ControllerHaptics : MonoBehaviour
    {
        private readonly HapticSequenceLogic _logic = new();

        /// <summary>監視入力のダウンエッジ受理（アクションに繋がらなくても鳴る＝「入力は届いている」）。</summary>
        public void Ack() => _logic.Trigger(HapticSequenceLogic.Pattern.Ack);

        /// <summary>短押しアクションが実行された（カメラ Next / ステータストグル / 点サンプル確定 等）。</summary>
        public void Action() => _logic.Trigger(HapticSequenceLogic.Pattern.Action);

        /// <summary>長押し発火・モード遷移・確定保存。</summary>
        public void Fire() => _logic.Trigger(HapticSequenceLogic.Pattern.Fire);

        /// <summary>失敗・拒否（登録の残差 NG やり直し等）。</summary>
        public void Error() => _logic.Trigger(HapticSequenceLogic.Pattern.Error);

        /// <summary>長押しカウント進行の進捗 [0,1]。0（or 1）で HoldTick 停止。</summary>
        public void SetHoldProgress(float progress01) => _logic.SetHoldProgress(progress01);

        private void Update()
        {
            float amp = _logic.Tick(Time.deltaTime);
            float freq = amp > 0f ? HapticSequenceLogic.Frequency : 0f;
            OVRInput.SetControllerVibration(freq, amp, OVRInput.Controller.RTouch);
        }

        private void OnDisable()
        {
            _logic.Reset();
            OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
        }
    }
}
