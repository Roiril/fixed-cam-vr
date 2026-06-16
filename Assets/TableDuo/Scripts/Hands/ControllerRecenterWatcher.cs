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

        /// <summary>両手グリップ長押しが成立した（手動リセット要求）。</summary>
        public event Action? Recentered;

        private float _held;
        private bool _firedThisHold; // 握りっぱなしで連続発火しないよう、離すまで1回だけ

        private void Update()
        {
            // コントローラ専用: ハンドトラッキング時は両軸とも 0 を返すため発火しない
            float l = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, OVRInput.Controller.LTouch);
            float r = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, OVRInput.Controller.RTouch);
            bool bothHeld = l >= gripThreshold && r >= gripThreshold;

            if (!bothHeld)
            {
                _held = 0f;
                _firedThisHold = false;
                return;
            }
            if (_firedThisHold) return;

            _held += Time.deltaTime;
            if (_held >= holdSeconds)
            {
                _firedThisHold = true;
                Debug.Log("[TableDuo] コントローラ両手グリップ長押し → 視点リセット");
                Recentered?.Invoke();
            }
        }
    }
}
