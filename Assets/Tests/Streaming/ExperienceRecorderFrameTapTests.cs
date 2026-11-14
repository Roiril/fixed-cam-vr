#nullable enable
using System;
using System.IO;
using System.Reflection;
using FixedCamVr.Streaming.Replay;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 体験の記録が映像を受けるのは録っている間だけ。購読している間は <see cref="CameraStream.HasFrameObservers"/> が真になり、
    /// 背景のカメラも全コマ展開する（<c>StreamDecodeScheduler</c> の間引きが効かなくなる）。
    /// <c>xprec/ON</c> の無い本番機は一度も録らないので、一度も真にしてはいけない。
    /// </summary>
    public sealed class ExperienceRecorderFrameTapTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [Test]
        public void FrameObservers_AreSubscribedOnlyWhileRecording()
        {
            Assume.That(CameraStream.HasFrameObservers, Is.False, "他の購読が残っていると判定できない");
            string root = Path.Combine(Path.GetTempPath(), "fcv-xprec-tap-" + Guid.NewGuid().ToString("N"));
            var go = new GameObject("ExperienceRecorder frame tap");
            var recorder = go.AddComponent<ExperienceRecorder>();
            try
            {
                Set(recorder, "_rootOverride", Path.Combine(root, "a"));
                Invoke(recorder, "OnEnable");
                Assert.That(recorder.Recording, Is.False);
                Assert.That(CameraStream.HasFrameObservers, Is.False, "有効なだけで録っていない間は購読しない");

                Invoke(recorder, "Start_", "test");
                Assert.That(recorder.Recording, Is.True);
                Assert.That(CameraStream.HasFrameObservers, Is.True, "録っている間は全コマを受ける");

                Assert.That(recorder.FinishAndWait("test", 5000), Is.Not.Null);
                Assert.That(recorder.Recording, Is.False);
                Assert.That(CameraStream.HasFrameObservers, Is.False, "録り終わったら購読を外す");

                // 同じ秒に録り始めると同じ名前のフォルダになるので、置き場を分ける。
                Set(recorder, "_rootOverride", Path.Combine(root, "b"));
                Invoke(recorder, "Start_", "again");
                Assert.That(CameraStream.HasFrameObservers, Is.True);
                Invoke(recorder, "OnDisable");
                Assert.That(recorder.Recording, Is.False);
                Assert.That(CameraStream.HasFrameObservers, Is.False, "無効化で録り終え、購読も外す");

                Set(recorder, "_rootOverride", Path.Combine(root, "c"));
                Invoke(recorder, "Start_", "destroy");
                Assert.That(CameraStream.HasFrameObservers, Is.True);
                Invoke(recorder, "OnDestroy");
                Assert.That(CameraStream.HasFrameObservers, Is.False, "破棄で購読を外す");
            }
            finally
            {
                Invoke(recorder, "OnDisable");
                Object.DestroyImmediate(go);
                try { Directory.Delete(root, true); } catch { /* 一時フォルダ。背景の書き出しが残っていても次で消える */ }
            }
        }

        private static void Set(object target, string field, object value)
            => target.GetType().GetField(field, Private)!.SetValue(target, value);

        private static void Invoke(object target, string method, params object[] args)
            => target.GetType().GetMethod(method, Private)!.Invoke(target, args.Length == 0 ? null : args);
    }
}
