#nullable enable
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 一撃の発声器の声の選び方（2026-09-04）。
    ///
    /// ⚠⚠ <b>輪番だと長い音が短い連打に奪われる。</b> 3 周目 C の目の一撃（0.09 秒刻みで約 20 発）が
    /// 大きい目の音（6.2 秒）を 7 発目で打ち切っていた（走行 20260903_210843）。
    /// 音は録画に映らず、打ち切りは <c>ev=sfx</c> にも出ない（鳴らした累計しか無い）ので、
    /// 機械で捕まえられるのはここだけ。
    /// </summary>
    public sealed class SfxPlayerTests
    {
        private static void Awake(MonoBehaviour c)
        {
            c.GetType().GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)
             ?.Invoke(c, null);
        }

        private static AudioSource? VoiceHolding(GameObject go, AudioClip clip)
        {
            foreach (var s in go.GetComponentsInChildren<AudioSource>())
                if (s.clip == clip) return s;
            return null;
        }

        /// <summary>長い音が鳴っているあいだに短い音を声の数より多く撃っても、長い音の声は奪われない。</summary>
        [Test]
        public void LongClip_SurvivesABurstOfShortOnes()
        {
            var go = new GameObject("sfx-player-test");
            try
            {
                var p = go.AddComponent<SfxPlayer>();
                Awake(p);
                var longClip = AudioClip.Create("long", 48000 * 6, 1, 48000, false);
                var shortClip = AudioClip.Create("short", 48000 / 10, 1, 48000, false);

                Assert.IsTrue(p.Play(longClip, pitchSpread: 0f, gainSpreadDb: 0f), "長い音を鳴らせない");
                var held = VoiceHolding(go, longClip);
                Assert.IsNotNull(held, "長い音がどの声にも載っていない");

                // 声は 6 本。短い一撃を 12 発 ＝ 輪番なら 6 発目で必ず長い音の声に当たる。
                for (int i = 0; i < 12; i++)
                    Assert.IsTrue(p.Play(shortClip, pitchSpread: 0f, gainSpreadDb: 0f), $"{i} 発目を鳴らせない");

                Assert.AreSame(longClip, held!.clip, "長い音の声が短い連打に奪われた（輪番に戻っている）");
                Assert.AreEqual(13, p.PlayedCount, "累計は全部数える");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// 全部ふさがっていれば、<b>鳴り終わりがいちばん近い声</b>を奪う
        /// （長い音ではなく、いちばん最初に撃った短い音）。
        /// </summary>
        [Test]
        public void WhenAllBusy_StealsTheVoiceThatEndsSoonest()
        {
            var go = new GameObject("sfx-player-steal-test");
            try
            {
                var p = go.AddComponent<SfxPlayer>();
                Awake(p);
                var longClip = AudioClip.Create("long", 48000 * 6, 1, 48000, false);
                var mid = AudioClip.Create("mid", 48000 * 2, 1, 48000, false);
                var shortest = AudioClip.Create("shortest", 48000 / 4, 1, 48000, false);
                var seventh = AudioClip.Create("seventh", 48000 / 10, 1, 48000, false);

                p.Play(longClip, pitchSpread: 0f, gainSpreadDb: 0f);
                p.Play(mid, pitchSpread: 0f, gainSpreadDb: 0f);
                p.Play(shortest, pitchSpread: 0f, gainSpreadDb: 0f);
                for (int i = 0; i < 3; i++) p.Play(mid, pitchSpread: 0f, gainSpreadDb: 0f);
                Assert.AreEqual(6, p.BusyVoices, "6 本ふさがっている前提");

                var wasShortest = VoiceHolding(go, shortest);
                Assert.IsNotNull(wasShortest);
                p.Play(seventh, pitchSpread: 0f, gainSpreadDb: 0f);

                Assert.AreSame(seventh, wasShortest!.clip, "鳴り終わりが最も近い声を奪っていない");
                Assert.IsNotNull(VoiceHolding(go, longClip), "長い音が奪われた");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>ラン開始の <c>StopAll</c> で空きに戻る（前の体験者の予約が残らない）。</summary>
        [Test]
        public void StopAll_FreesEveryVoice()
        {
            var go = new GameObject("sfx-player-stop-test");
            try
            {
                var p = go.AddComponent<SfxPlayer>();
                Awake(p);
                var clip = AudioClip.Create("c", 48000 * 3, 1, 48000, false);
                for (int i = 0; i < 6; i++) p.Play(clip, pitchSpread: 0f, gainSpreadDb: 0f);
                Assert.AreEqual(6, p.BusyVoices);
                p.StopAll();
                Assert.AreEqual(0, p.BusyVoices, "止めたのに予約が残っている");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
