#nullable enable
using System;
using UnityEngine;

namespace TableDuoVr.Hands
{
    /// <summary>
    /// OS の recenter（Oculus ボタン長押し）を検知して通知する。
    /// 放置するとトラッキングスペースが回って席とテーブルがズレるため、
    /// 購読側（TableDuoPlayer）が席アラインを再適用する — 要件 §6。
    /// OVR 依存をこの 1 クラスに閉じ込め、Net 層からは event だけ見る。
    /// </summary>
    public sealed class RecenterWatcher : MonoBehaviour
    {
        public event Action? Recentered;

        private bool _subscribed;

        private void OnEnable() => TrySubscribe();

        // OVRManager.display は OVRManager 側の初期化で生えるため、OnEnable の実行順によっては
        // まだ null（→黙って恒久無効＝OS recenter で席がズレたまま・ログにも残らない）。生えるまでリトライ
        private void Update()
        {
            if (!_subscribed) TrySubscribe();
        }

        private void TrySubscribe()
        {
            if (_subscribed || OVRManager.display == null) return;
            OVRManager.display.RecenteredPose += OnRecentered;
            _subscribed = true;
        }

        private void OnDisable()
        {
            if (_subscribed && OVRManager.display != null)
            {
                OVRManager.display.RecenteredPose -= OnRecentered;
            }
            _subscribed = false;
        }

        private void OnRecentered()
        {
            Debug.Log("[TableDuo] OS recenter 検知 — 席アラインを再適用");
            Recentered?.Invoke();
        }
    }
}
