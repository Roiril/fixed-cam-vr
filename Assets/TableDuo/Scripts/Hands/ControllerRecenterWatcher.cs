#nullable enable
using System;
using UnityEngine;

namespace TableDuoVr.Hands
{
    /// <summary>
    /// コントローラの**両手グリップ同時長押し（既定 3 秒）**で手動リセットを通知する。
    /// 入力は OVRInput のコントローラ grip 軸だけを読む（LTouch/RTouch）。ハンドトラッキングの
    /// ピンチ・ジェスチャーは一切見ないので、手の動きでは絶対に発火しない（誤検知防止）。
    /// 両手必須にすることで、片手の偶発的グリップでも発火しない。
    /// OVR 依存をこのクラスに閉じ込め、購読側（TableDuoPlayer）は event だけ見る（RecenterWatcher と同作法）。
    /// </summary>
    public sealed class ControllerRecenterWatcher : MonoBehaviour
    {
        [Tooltip("両手グリップを握り続ける秒数")]
        [SerializeField] private float holdSeconds = 3f;
        [Tooltip("grip 軸（0..1）をこの値以上で『握っている』とみなす")]
        [SerializeField, Range(0.1f, 1f)] private float gripThreshold = 0.6f;

        // 即リセットは右コントローラ A ボタン（Button.One）単押し固定。
        // SerializeField にすると既存シーン YAML 未反映時に enum が None(0) 等の型 default で
        // 読まれて発火しなくなる（unity-prefab-fields の罠）ため、ここは定数で持つ。

        /// <summary>両手グリップ長押しが成立した（手動リセット要求）。</summary>
        public event Action? Recentered;

        // 触覚フィードバック（任意）。未配置でも従来動作（振動が出ないだけ）。
        // A 単押し=Action（押した右手）、両手グリップは進入=Ack → HoldTick ランプ → 発火=Fire（両手）。
        private TableDuoHaptics? _haptics;

        private float _held;
        private bool _firedThisHold; // 握りっぱなしで連続発火しないよう、離すまで1回だけ
        private bool _wasBothHeld;   // 両手グリップの立ち上がり/立ち下がり検出用

        private void Awake()
        {
            // シーン内の system singleton を取得（Setup TableDuo Scene が Systems 直下に配置）
            _haptics = FindObjectOfType<TableDuoHaptics>();
        }

        private void Update()
        {
            // A ボタン単押しで即リセット（両手グリップ長押しより手軽）
            if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch))
            {
                _haptics?.Action(OVRInput.Controller.RTouch); // 短押しアクション実行
                Debug.Log("[TableDuo] A ボタン → 視点リセット");
                Recentered?.Invoke();
            }

            // コントローラ専用: ハンドトラッキング時は両軸とも 0 を返すため発火しない
            float l = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, OVRInput.Controller.LTouch);
            float r = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, OVRInput.Controller.RTouch);
            bool bothHeld = l >= gripThreshold && r >= gripThreshold;

            if (!bothHeld)
            {
                if (_wasBothHeld) ClearHoldHaptics(); // 途中で離した = ランプ停止（失敗ではないので Error は鳴らさない）
                _held = 0f;
                _firedThisHold = false;
                _wasBothHeld = false;
                return;
            }

            if (!_wasBothHeld)
            {
                // 両手グリップの立ち上がり: 受理を Ack で通知（まだアクションではない）
                _haptics?.Ack(OVRInput.Controller.LTouch);
                _haptics?.Ack(OVRInput.Controller.RTouch);
            }
            _wasBothHeld = true;

            if (_firedThisHold) return;

            _held += Time.deltaTime;

            // 長押しカウント進行中のランプ振動（両手）
            float progress = holdSeconds > 0f ? _held / holdSeconds : 1f;
            _haptics?.SetHoldProgress(OVRInput.Controller.LTouch, progress);
            _haptics?.SetHoldProgress(OVRInput.Controller.RTouch, progress);

            if (_held >= holdSeconds)
            {
                _firedThisHold = true;
                ClearHoldHaptics();
                _haptics?.Fire(OVRInput.Controller.LTouch); // 長押し発火（確定）
                _haptics?.Fire(OVRInput.Controller.RTouch);
                Debug.Log("[TableDuo] コントローラ両手グリップ長押し → 視点リセット");
                Recentered?.Invoke();
            }
        }

        private void ClearHoldHaptics()
        {
            _haptics?.SetHoldProgress(OVRInput.Controller.LTouch, -1f);
            _haptics?.SetHoldProgress(OVRInput.Controller.RTouch, -1f);
        }
    }
}
