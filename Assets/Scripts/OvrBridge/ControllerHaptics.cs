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

        /// <summary>
        /// <b>体験者の手（左）へ返す振動。</b> スタッフの手（右）とは別の時間軸で鳴る。
        ///
        /// 体験者が持つのは記録ボタン（左 X）だけで、返すのは「受け取った」の 1 種類。
        /// <b>正誤は返さない</b> — 返すと答え合わせになり、装置が「何が異変か」を判定してしまう
        /// （この作品の恐怖は「装置は正直に映すだけ」の上に乗っている）。
        /// </summary>
        private readonly HapticSequenceLogic _left = new();

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

        /// <summary>体験者の報告ボタン（左 X / 左 Y）の 2 秒長押しが通った。<b>返すのはこれだけ。</b></summary>
        public void LeftMark() => _left.Trigger(HapticSequenceLogic.Pattern.Action);

        /// <summary>
        /// 体験者の長押しカウント進行の進捗 [0,1]（左）。0（or 1）で HoldTick 停止。
        /// <b>右の <see cref="SetHoldProgress"/> と混ぜない</b> — スタッフの長押しが体験者の手に
        /// 伝わると、世界の外の合図になる。
        /// </summary>
        public void SetLeftHoldProgress(float progress01) => _left.SetHoldProgress(progress01);

        /// <summary>
        /// <b>上司から連絡が届いた</b>（左）。<see cref="LeftMark"/> と<b>別のパターンにする</b> —
        /// 同じだと「自分が押した」と「向こうから来た」が混ざる。
        /// 2 連の <c>Fire</c> は長押しの発火と同じ形だが、右手にしか出ないので体験者には新しい合図になる。
        /// </summary>
        public void LeftNotify() => _left.Trigger(HapticSequenceLogic.Pattern.Fire);

        private void Update()
        {
            float dt = Time.deltaTime;
            float amp = _logic.Tick(dt);
            float freq = amp > 0f ? HapticSequenceLogic.Frequency : 0f;
            OVRInput.SetControllerVibration(freq, amp, OVRInput.Controller.RTouch);

            // 左は体験者の手。右と混ぜない（スタッフの操作が体験者の手に伝わると世界の外の合図になる）。
            float ampL = _left.Tick(dt);
            float freqL = ampL > 0f ? HapticSequenceLogic.Frequency : 0f;
            OVRInput.SetControllerVibration(freqL, ampL, OVRInput.Controller.LTouch);
        }

        private void OnDisable()
        {
            _logic.Reset();
            _left.Reset();
            OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
            OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.LTouch);
        }
    }
}
