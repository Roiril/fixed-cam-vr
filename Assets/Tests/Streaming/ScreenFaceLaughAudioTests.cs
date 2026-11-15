#nullable enable
using System;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 3 周目 C の増えるスクリーンの最後の面に笑う人形の顔が出たときの笑い声（<c>canon/LEDGER.md</c> 0386・ユーザー指定の音源）。
    /// 2-A のカットの笑い声（<see cref="SoundCue.DollLaughOnce"/>）とは別の音源・別の id。
    ///
    /// ⚠ この音の失敗は画にも動画にも出ない。取り込み忘れは「顔は出るのに笑わない」になるだけ。
    /// </summary>
    public sealed class ScreenFaceLaughAudioTests
    {
        private const string Res = "Sound/sfx_screen_face_laugh";

        [Test]
        public void ResourceName_IsItsOwnClip()
        {
            Assert.That(SoundCueLogic.ResourceName(SoundCue.ScreenFaceLaugh), Is.EqualTo("sfx_screen_face_laugh"));
            Assert.That(SoundCueLogic.ResourceName(SoundCue.ScreenFaceLaugh),
                        Is.Not.EqualTo(SoundCueLogic.ResourceName(SoundCue.DollLaughOnce)), "2-A の笑い声と同じ音を鳴らさない");
            Assert.That(SoundCueLogic.VariantCount(SoundCue.ScreenFaceLaugh), Is.EqualTo(1));
            Assert.That(SoundCueLogic.DuckFor(SoundCue.ScreenFaceLaugh), Is.EqualTo(0f));
        }

        /// <summary>⚠ <c>py -3.11 tools/ingest-sounds.py --only sfx_screen_face_laugh</c> の後に <c>menu sound-import</c> を通していないと引けない。</summary>
        [Test]
        public void Clip_IsInResources_AndIs2D()
        {
            var clip = Resources.Load<AudioClip>(Res);
            Assert.IsNotNull(clip, $"{Res} が無い — ingest-sounds.py の後に `tools/unity.ps1 menu sound-import`");
            Assert.That(clip!.length, Is.EqualTo(1.10f).Within(0.05f), "ユーザーの mp3（1.1 秒）を端の無音だけ落とした尺");
            Assert.That(Array.IndexOf(SpatialAudio.MonoRequired, "sfx_screen_face_laugh"), Is.LessThan(0),
                        "2D（PlaySpot に方角を渡しても 3D にしない）");
        }
    }
}
