#nullable enable
using System.Collections.Generic;
using System.Reflection;
using FixedCamVr.Streaming;
using FixedCamVr.Streaming.Recording;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// B3 MonoBehaviour 配線。動画 cue の Prepare 失敗（errorReceived / タイムアウト）で
    /// <c>_current</c> が解放され、CameraSwitchDirector が cueActive=true のまま自動切替を恒久凍結する穴が
    /// 断たれることを固定する（GripStopLocalTests と同方式・EditMode + reflection）。
    ///
    /// EditMode では Awake が走らないため _player/_material は未初期化（null）だが、AbortCurrentCue は
    /// それらに null 安全に触れるので副作用（_current / _stopWhenFadedOut / _strength / _target）で観測できる。
    /// _logic はフィールド初期化子で生成されるため（Awake 不要）、GetField で取り出して直接駆動する。
    /// </summary>
    public sealed class OverlayVideoFailureTests
    {
        private sealed class EndedFrames : IFrameSequence
        {
            public Texture Texture { get; }
            public float Aspect => 1f;
            public float DurationSec => 1f;
            public EndedFrames(Texture texture) => Texture = texture;
            public bool Tick(float elapsedSec) => false;
            public void Dispose() { }
        }

        private const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

        private readonly List<Object> _spawned = new();

        [TearDown]
        public void Cleanup()
        {
            foreach (var o in _spawned) if (o != null) Object.DestroyImmediate(o);
            _spawned.Clear();
        }

        private static void SetField(object target, string name, object? value)
        {
            FieldInfo? f = target.GetType().GetField(name, BF);
            Assert.That(f, Is.Not.Null, $"field '{name}' not found on {target.GetType().Name}");
            f!.SetValue(target, value);
        }

        private static object GetField(object target, string name)
            => target.GetType().GetField(name, BF)!.GetValue(target);

        private static void Invoke(object target, string method, params object?[] args)
            => target.GetType().GetMethod(method, BF)!.Invoke(target, args);

        private ScreenOverlayController NewOverlay()
        {
            // Renderer 要件のため MeshRenderer 同梱。EditMode では Awake 未実行。
            var go = new GameObject("Overlay", typeof(MeshFilter), typeof(MeshRenderer));
            _spawned.Add(go);
            return go.AddComponent<ScreenOverlayController>();
        }

        private static OverlayPlaybackLogic Logic(ScreenOverlayController o)
            => (OverlayPlaybackLogic)GetField(o, "_logic");

        private static OverlayCueData VideoCue(string id = "cue_v")
            => new() { id = id, displayName = id, sourceUrl = "http://x/clip.mp4" };

        private static OverlayCueData StillCue(string id = "cue_s")
            => new() { id = id, displayName = id, sourceUrl = "http://x/frame.png" };

        [Test]
        public void RecordedEnd_CompletesTokenWithoutRevealingLive_AndKeepsLastFrame()
        {
            var o = NewOverlay();
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            Assert.That(shader, Is.Not.Null);
            var mat = new Material(shader!);
            var frame = new Texture2D(4, 4);
            _spawned.Add(mat);
            _spawned.Add(frame);
            SetField(o, "_material", mat);
            var cue = new OverlayCueData { id = "recorded", frames = new EndedFrames(frame) };
            SetField(o, "_current", cue);
            SetField(o, "_strength", 1f);
            SetField(o, "_target", 1f);
            int token = Logic(o).BeginPlay();
            Logic(o).EndLoad(token);
            SetField(o, "_framesCueToken", token);

            Assert.That(o.IsFinished(token), Is.False);
            Invoke(o, "TickFrames");

            Assert.That(o.IsFinished(token), Is.True, "次のカットへ進める");
            Assert.That(o.Current, Is.SameAs(cue), "次の素材が載るまでは録画を表示する");
            Assert.That(o.Strength, Is.EqualTo(1f), "下のライブを混ぜない");
            var held = (RenderTexture)GetField(o, "_heldFrameRt");
            Assert.That(held, Is.Not.Null);
            Assert.That(held.IsCreated(), Is.True);
            Assert.That(held, Is.Not.SameAs(frame), "元の録画を破棄しても最後の絵は残る");

            var next = new Texture2D(4, 4);
            _spawned.Add(next);
            Invoke(o, "SetOverlayTexture", next, 1f, "plate");
            Assert.That(GetField(o, "_heldFrameRt"), Is.Null, "次の素材へ替わってから保持画を解放する");
        }

        [Test]
        public void OnVideoError_CurrentGenVideo_ReleasesCurrent()
        {
            LogAssert.ignoreFailingMessages = true;
            var o = NewOverlay();
            var cue = VideoCue();
            SetField(o, "_current", cue);
            var l = Logic(o);
            l.BeginPrepare(l.BeginPlay(), 0f);         // 現行世代の Prepare 中

            Invoke(o, "OnVideoError", null, "err");
            Assert.That(GetField(o, "_current"), Is.Null, "現行世代の動画エラーで cue を畳み live 復帰する");
        }

        [Test]
        public void OnVideoError_StaleGen_LeavesCurrentIntact()
        {
            LogAssert.ignoreFailingMessages = true;
            var o = NewOverlay();
            var newCue = VideoCue("cue_new");
            SetField(o, "_current", newCue);
            var l = Logic(o);
            l.BeginPrepare(l.BeginPlay(), 0f);
            l.BeginPlay();                              // 新 cue が supersede（stale prepare のエラーが遅れて届く）

            Invoke(o, "OnVideoError", null, "stale err");
            Assert.That(GetField(o, "_current"), Is.SameAs(newCue), "stale 世代のエラーは新 cue を殺さない");
        }

        [Test]
        public void OnVideoError_StillImage_LeavesCurrentIntact()
        {
            LogAssert.ignoreFailingMessages = true;
            var o = NewOverlay();
            var still = StillCue();
            SetField(o, "_current", still);
            var l = Logic(o);
            l.BeginPlay();                              // 静止画は Prepare しない

            Invoke(o, "OnVideoError", null, "err");
            Assert.That(GetField(o, "_current"), Is.SameAs(still), "動画エラーは静止画 cue を畳まない");
        }

        [Test]
        public void CheckPrepareTimeout_ExceededVideo_ReleasesCurrent()
        {
            LogAssert.ignoreFailingMessages = true;
            var o = NewOverlay();
            SetField(o, "_current", VideoCue());
            var l = Logic(o);
            // 過去に Prepare 発行 → 既定 prepareTimeoutSec(6s) を確実に超過。
            l.BeginPrepare(l.BeginPlay(), Time.realtimeSinceStartup - 100f);

            Invoke(o, "CheckPrepareTimeout");
            Assert.That(GetField(o, "_current"), Is.Null, "Prepare タイムアウトで動画 cue を畳む");
        }

        [Test]
        public void AbortCurrentCue_ResetsFadeState()
        {
            LogAssert.ignoreFailingMessages = true;
            var o = NewOverlay();
            SetField(o, "_current", VideoCue());
            SetField(o, "_stopWhenFadedOut", true);
            SetField(o, "_strength", 1f);
            SetField(o, "_target", 1f);
            var l = Logic(o);
            l.BeginPrepare(l.BeginPlay(), 0f);

            Invoke(o, "OnVideoError", null, "err");     // AbortCurrentCue 経由

            Assert.That((bool)GetField(o, "_stopWhenFadedOut"), Is.False,
                "後続 Update のフェード完了分岐が誤作動しないよう false");
            Assert.That((float)GetField(o, "_strength"), Is.EqualTo(0f));
            Assert.That((float)GetField(o, "_target"), Is.EqualTo(0f));
        }

        [Test]
        public void AppliedCueId_ChangesOnlyWhenTextureIsActuallyApplied_AndClearsAtZero()
        {
            var o = NewOverlay();
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            Assert.That(shader, Is.Not.Null);
            var mat = new Material(shader!);
            var tex = new Texture2D(2, 2);
            _spawned.Add(mat);
            _spawned.Add(tex);
            SetField(o, "_material", mat);
            SetField(o, "_appliedCueId", "old_non_pov");
            SetField(o, "_current", VideoCue("pov_0"));

            Assert.That(o.AppliedCueId, Is.EqualTo("old_non_pov"),
                "Current が次の cue へ進んでも Prepare 完了前は前素材のまま");
            Invoke(o, "SetOverlayTexture", tex, 1f, "pov_0");
            Assert.That(o.AppliedCueId, Is.EqualTo("pov_0"));

            SetField(o, "_strength", 0f);
            SetField(o, "_target", 0f);
            SetField(o, "_stopWhenFadedOut", true);
            Invoke(o, "Update");
            Assert.That(o.AppliedCueId, Is.Empty, "cue 解除で適用済み id も消す");
        }

        [Test]
        public void PrepareFailure_ClearsAppliedCueId()
        {
            LogAssert.ignoreFailingMessages = true;
            var o = NewOverlay();
            SetField(o, "_current", VideoCue("pov_0"));
            SetField(o, "_appliedCueId", "old_non_pov");
            SetField(o, "_strength", 1f);
            var l = Logic(o);
            l.BeginPrepare(l.BeginPlay(), 0f);

            Invoke(o, "OnVideoError", null, "err");
            Assert.That(o.AppliedCueId, Is.Empty);
        }
    }
}
