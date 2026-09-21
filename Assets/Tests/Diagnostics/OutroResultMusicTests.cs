#nullable enable
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Diagnostics.Tests
{
    public sealed class OutroResultMusicTests
    {
        private const string ClipPath = "Assets/Resources/Sound/bgm_nocturnal_waters.wav";
        private const float FullVolume = 0.3576f;

        [Test]
        public void ProvidedMusicIsStereo48kAnd80SecondsWithLoopImportSettings()
        {
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(ClipPath);
            Assert.IsNotNull(clip, ClipPath);
            Assert.AreEqual(48000, clip!.frequency);
            Assert.AreEqual(2, clip.channels);
            Assert.AreEqual(80f, clip.length, .01f);
            Assert.AreSame(clip, Resources.Load<AudioClip>(OutroResultMusic.ResourceName));

            var importer = (AudioImporter)AssetImporter.GetAtPath(ClipPath);
            Assert.IsNotNull(importer);
            var settings = importer!.defaultSampleSettings;
            Assert.AreEqual(AudioClipLoadType.CompressedInMemory, settings.loadType);
            Assert.AreEqual(AudioCompressionFormat.Vorbis, settings.compressionFormat);
            Assert.AreEqual(.42f, settings.quality, .0001f);
            Assert.IsTrue(settings.preloadAudioData);
            Assert.IsFalse(importer.forceToMono);
        }

        [Test]
        public void PresentationCreatesFlatLoopAndFadesToMeasuredGain()
        {
            var go = new GameObject("outro-result-music-test");
            try
            {
                var music = CreateMusic(go);
                var source = go.GetComponent<AudioSource>();
                Assert.IsNotNull(source);
                Assert.IsTrue(music.HasClip);
                Assert.AreSame(Resources.Load<AudioClip>(OutroResultMusic.ResourceName), source!.clip);
                Assert.IsTrue(source.loop);
                Assert.IsFalse(source.playOnAwake);
                Assert.AreEqual(0f, source.spatialBlend);
                Assert.IsFalse(source.spatialize);
                Assert.AreEqual(0f, music.Volume);

                music.SetPresented(true, .75f);
                Assert.AreEqual(FullVolume / 2f, music.Volume, .0001f);
                music.SetPresented(true, .75f);
                Assert.AreEqual(FullVolume, music.Volume, .0001f);
                Assert.AreEqual(source.volume, music.Volume);
                Assert.AreEqual(source.isPlaying, music.IsPlaying);
                Assert.AreEqual(source.isPlaying ? source.time : 0f,
                                music.PlaybackSeconds, .001f);

                source.Stop();
                music.SetPresented(true, .5f);
                Assert.AreEqual(FullVolume, music.Volume, .0001f,
                                "表示中の再生停止でフェードを戻さない");
                Assert.IsFalse(music.IsPlaying, "再生停止を毎フレーム隠さない");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void HidingAndDisablingStopImmediatelyAndNextPresentationStartsAtBeginning()
        {
            var go = new GameObject("outro-result-music-stop-test");
            try
            {
                var music = CreateMusic(go);
                var source = go.GetComponent<AudioSource>();
                Assert.IsNotNull(source);

                music.SetPresented(true, 1.5f);
                Assert.AreEqual(FullVolume, music.Volume, .0001f);
                music.SetPresented(false, 0f);
                Assert.IsFalse(music.IsPlaying);
                Assert.AreEqual(0f, music.Volume);
                Assert.AreEqual(0f, music.PlaybackSeconds);

                source!.time = 10f;
                music.SetPresented(true, .1f);
                Assert.AreEqual(0f, source.time, .05f);
                Assert.Greater(music.Volume, 0f);
                Assert.Less(music.Volume, FullVolume);

                typeof(OutroResultMusic).GetMethod("OnDisable",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(music, null);
                Assert.IsFalse(music.IsPlaying);
                Assert.AreEqual(0f, music.Volume);
                Assert.AreEqual(0f, music.PlaybackSeconds);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void MissingClipNeverStartsOrAccumulatesVolume()
        {
            var go = new GameObject("outro-result-music-missing-test");
            try
            {
                var music = CreateMusic(go);
                var source = go.GetComponent<AudioSource>();
                Assert.IsNotNull(source);
                source!.clip = null;
                for (int i = 0; i < 3; i++) music.SetPresented(true, .5f);
                Assert.IsFalse(music.HasClip);
                Assert.IsFalse(music.IsPlaying);
                Assert.AreEqual(0f, music.Volume);
                Assert.AreEqual(0f, music.PlaybackSeconds);

                source.clip = Resources.Load<AudioClip>(OutroResultMusic.ResourceName);
                music.SetPresented(true, .75f);
                Assert.AreEqual(FullVolume / 2f, music.Volume, .0001f,
                                "クリップ復帰後は表示開始をやり直す");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        private static OutroResultMusic CreateMusic(GameObject go)
        {
            var music = go.AddComponent<OutroResultMusic>();
            // EditMode の AddComponent が Awake を呼ばない環境でも同じ初期状態にする。
            if (go.GetComponent<AudioSource>() == null)
                typeof(OutroResultMusic).GetMethod("Awake",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(music, null);
            return music;
        }
    }
}
