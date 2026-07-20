#nullable enable
using UnityEngine;

namespace TableDuoVr.Hands
{
    /// <summary>
    /// TableDuo のコントローラ触覚（振動）フィードバック。ボタン/グリップ操作の受理・実行・発火・失敗を
    /// 振動で伝え、「押せていないのか未接続なのか分からない」を解消する。波形は両アプリ共通の
    /// <see cref="HapticVocabulary"/>（frequency=0.5 固定）。
    ///
    /// OVRInput.SetControllerVibration は呼び続けないと約 2 秒で自動停止するため、毎フレーム
    /// <see cref="LateUpdate"/> で対象コントローラに現在振幅を適用する（入力を読む Update より後に走らせ、
    /// 同フレームで反応させる）。非再生時は (0,0) を送って確実に止める。
    /// 時間進行の純ロジックは <see cref="HapticChannel"/> / <see cref="HapticPatternPlayer"/> に分離（テスト可能）。
    ///
    /// 振動は「押されたコントローラ側」に出す（LTouch で押したら LTouch へ）。左右は独立チャンネル。
    /// システム singleton としてシーンに 1 個（Systems 直下、Setup TableDuo Scene が配置）。
    /// </summary>
    public sealed class TableDuoHaptics : MonoBehaviour
    {
        private readonly HapticChannel _left = new();
        private readonly HapticChannel _right = new();
        private float _lastLeftAmp;
        private float _lastRightAmp;

        /// <summary>監視対象入力のダウンエッジ受理（アクションに繋がらなくても鳴る）。</summary>
        public void Ack(OVRInput.Controller c) => Channel(c)?.PlayOneShot(HapticVocabulary.AckPulses);

        /// <summary>短押しアクション実行。</summary>
        public void Action(OVRInput.Controller c) => Channel(c)?.PlayOneShot(HapticVocabulary.ActionPulses);

        /// <summary>長押し発火・モード遷移・確定。</summary>
        public void Fire(OVRInput.Controller c) => Channel(c)?.PlayOneShot(HapticVocabulary.FirePulses);

        /// <summary>失敗・拒否。</summary>
        public void Error(OVRInput.Controller c) => Channel(c)?.PlayOneShot(HapticVocabulary.ErrorPulses);

        /// <summary>長押しカウント進行中のランプ振動。progress01 &lt; 0 で解除。</summary>
        public void SetHoldProgress(OVRInput.Controller c, float progress01)
            => Channel(c)?.SetHoldProgress(progress01);

        // Touch は左右いずれかへ解決（複合値でもビットで振り分け・非 Touch は null=無視）
        private HapticChannel? Channel(OVRInput.Controller c)
        {
            if ((c & OVRInput.Controller.LTouch) != 0) return _left;
            if ((c & OVRInput.Controller.RTouch) != 0) return _right;
            return null;
        }

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            _lastLeftAmp = Apply(_left, OVRInput.Controller.LTouch, _lastLeftAmp, dt);
            _lastRightAmp = Apply(_right, OVRInput.Controller.RTouch, _lastRightAmp, dt);
        }

        private static float Apply(HapticChannel channel, OVRInput.Controller c, float lastAmp, float dt)
        {
            float amp = channel.Tick(dt);
            if (amp > 0f)
            {
                OVRInput.SetControllerVibration(HapticVocabulary.Frequency, amp, c);
            }
            else if (lastAmp > 0f)
            {
                OVRInput.SetControllerVibration(0f, 0f, c); // 立ち下がりで確実に停止
            }
            return amp;
        }

        private void OnDisable()
        {
            // 無効化・シーン破棄時に鳴りっぱなしを残さない
            OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.LTouch);
            OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
            _lastLeftAmp = 0f;
            _lastRightAmp = 0f;
        }
    }
}
