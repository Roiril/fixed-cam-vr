#nullable enable
using System.Reflection;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// T3: グリップ緊急停止（<see cref="ShowControlClient.ToggleActiveCameraCue"/>）が、server 到達状態でも
    /// ローカル再生中 cue を確実に止めることを固定する（監査バックログ 2026-07-23 節の 2）。
    ///
    /// バグ: server 到達時の停止分岐は <c>SendCommand("stopCue","")</c> を送るだけだった。スケジューラ発火 cue は
    /// server の activeCue が空のままなので、Apply の遷移判定（cueId != _appliedCue）が起きず StopOverlay されない。
    /// 修正: server 到達時も <c>_overlay.StopOverlay()</c> をローカル併用する。
    ///
    /// EditMode で検証する（実時間・Update 不要）。AddComponent は EditMode で Awake を走らせないため、
    /// ScreenOverlayController の _material/_player は未初期化のままだが、StopOverlay は _current!=null のとき
    /// それらに触れず _stopWhenFadedOut を立てるだけなので、ローカル停止の有無を副作用で観測できる。
    /// </summary>
    public sealed class GripStopLocalTests
    {
        private const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

        private readonly System.Collections.Generic.List<Object> _spawned = new();

        private static void SetField(object target, string name, object? value)
        {
            FieldInfo? f = target.GetType().GetField(name, BF);
            Assert.That(f, Is.Not.Null, $"field '{name}' not found on {target.GetType().Name}");
            f!.SetValue(target, value);
        }

        private static object GetField(object target, string name)
            => target.GetType().GetField(name, BF)!.GetValue(target);

        [TearDown]
        public void Cleanup()
        {
            foreach (var o in _spawned) if (o != null) Object.DestroyImmediate(o);
            _spawned.Clear();
        }

        [Test]
        public void GripStop_ServerReachable_AlsoStopsLocalOverlay()
        {
            LogAssert.ignoreFailingMessages = true; // fire-and-forget な /command 送信の失敗ログを無視

            // ScreenOverlayController（Renderer 要件のため MeshRenderer 同梱）。EditMode では Awake 未実行。
            var overlayGo = new GameObject("Overlay", typeof(MeshFilter), typeof(MeshRenderer));
            _spawned.Add(overlayGo);
            var overlay = overlayGo.AddComponent<ScreenOverlayController>();
            // 再生中を偽装（_current 非 null）。StopOverlay はこのとき _material/_player に触れない。
            SetField(overlay, "_current", new OverlayCueData { id = "cue_A_1", fadeOutSeconds = 0.5f });

            // ShowControlClient（Awake 未実行 → 参照は reflection で注入）。
            var showGo = new GameObject("Screen");
            _spawned.Add(showGo);
            var show = showGo.AddComponent<ShowControlClient>();

            var regGo = new GameObject("Registry");
            _spawned.Add(regGo);
            var reg = regGo.AddComponent<CameraStreamRegistry>(); // ActiveIndex=0（既定）で十分

            var server = ScriptableObject.CreateInstance<ShowServerSource>();
            _spawned.Add(server);

            SetField(show, "_overlay", overlay);
            SetField(show, "registry", reg);
            SetField(show, "server", server);
            // ServerReachable = server!=null && (realtime - _lastServerContactTime) < 40 を成立させる。
            SetField(show, "_lastServerContactTime", Time.realtimeSinceStartup);

            // 前提確認: まだ停止していない。
            Assert.That((bool)GetField(overlay, "_stopWhenFadedOut"), Is.False);

            show.ToggleActiveCameraCue();

            // 修正後: server 到達でもローカル StopOverlay が走り _stopWhenFadedOut=true になる。
            // 修正前: server 分岐は SendCommand のみ → false のまま（スケジューラ cue が止まらない穴）。
            Assert.That((bool)GetField(overlay, "_stopWhenFadedOut"), Is.True,
                "server 到達状態でもグリップ停止はローカル cue を止めるはず");
        }
    }
}
