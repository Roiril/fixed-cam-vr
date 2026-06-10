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

        private void OnEnable()
        {
            if (OVRManager.display != null)
            {
                OVRManager.display.RecenteredPose += OnRecentered;
            }
        }

        private void OnDisable()
        {
            if (OVRManager.display != null)
            {
                OVRManager.display.RecenteredPose -= OnRecentered;
            }
        }

        private void OnRecentered()
        {
            Debug.Log("[TableDuo] OS recenter 検知 — 席アラインを再適用");
            Recentered?.Invoke();
        }
    }
}
