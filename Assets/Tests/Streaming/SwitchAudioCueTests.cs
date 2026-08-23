#nullable enable
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// カメラ切替の音（<c>canon/LEDGER.md</c> 0106 の警告つき ＋ 0112 の変種 6 本）。
    ///
    /// ⚠⚠ <b>音は録画に映らない。</b> しかもこの音の失敗は「素の切替音が鳴る」「同じ変種だけが鳴る」
    /// なので、実機で被って聴いても気づけない（鳴ってはいる）。焼き忘れ・取り込み忘れを
    /// 機械で捕まえられるのはここと、走行ログの <c>swAlert</c> / <c>swVar</c> だけ。
    /// </summary>
    public sealed class SwitchAudioCueTests
    {
        private static AudioClip? Plain(int i) =>
            Resources.Load<AudioClip>($"{SwitchAudioCue.DefaultResourcePrefix}{i}");

        private static AudioClip? Alert(int i) =>
            Resources.Load<AudioClip>($"{SwitchAudioCue.AlertResourcePrefix}{i}");

        /// <summary>
        /// ⚠ 焼いた後に <c>tools/unity.ps1 menu sound-import</c> を通していないと
        /// Resources から引けない（wav がプロジェクトへインポートされていない）。
        /// </summary>
        [Test]
        public void EveryVariant_IsInResources()
        {
            for (int i = 1; i <= SwitchAudioCue.DefaultVariantCount; i++)
            {
                Assert.IsNotNull(Plain(i),
                                 $"{SwitchAudioCue.DefaultResourcePrefix}{i} が無い — "
                                 + "`py -3.11 tools/ingest-sounds.py --only sfx_switch_1` の後に "
                                 + "`tools/unity.ps1 menu sound-import` を走らせること");
                Assert.IsNotNull(Alert(i),
                                 $"{SwitchAudioCue.AlertResourcePrefix}{i} が無い — "
                                 + "警告つきは素と**同じ本数**焼く（`canon/LEDGER.md` 0112）");
            }
        }

        /// <summary>
        /// 変種は<b>実体が違うだけでなく中身も違う</b>こと。
        ///
        /// ⚠ <c>AreNotSame</c> では焼き損ねを捕まえられない — 同じ波形を別ファイルへ 6 回書いても
        /// 通ってしまう。標本を読んで<b>長さか中身のどちらかが違う</b>ことまで見る。
        /// </summary>
        [Test]
        public void Variants_HaveDifferentWaveforms()
        {
            var seen = new List<float[]>();
            for (int i = 1; i <= SwitchAudioCue.DefaultVariantCount; i++)
            {
                var clip = Plain(i);
                Assert.IsNotNull(clip, $"変種 {i} が無い");
                var buf = new float[clip!.samples * clip.channels];
                clip.GetData(buf, 0);
                foreach (var prev in seen)
                {
                    Assert.IsFalse(SameWave(prev, buf),
                                   $"変種 {i} が別の変種と同じ波形 — "
                                   + "`SWITCH_VARIANTS` の行が重複しているか、焼き直しが効いていない");
                }
                seen.Add(buf);
            }
        }

        private static bool SameWave(float[] a, float[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (!Mathf.Approximately(a[i], b[i])) return false;
            }
            return true;
        }

        /// <summary>
        /// 素の切替音と警告つきは<b>別のクリップ</b>。同じ実体なら合成が効いていない
        /// （＝ 差し込みが素の音で鳴っているのに、掴めているので警告も出ない）。
        /// </summary>
        [Test]
        public void AlertClip_IsNotThePlainSwitchClip()
        {
            for (int i = 1; i <= SwitchAudioCue.DefaultVariantCount; i++)
            {
                Assert.AreNotSame(Plain(i), Alert(i),
                                  $"{i} 番の素の切替音と警告つきが同じ実体になっている");
            }
        }

        /// <summary>
        /// 警告つきは<b>素とは別に数える</b>（走行ログの <c>swAlert</c> の出どころ）。
        /// 累計（<c>swN</c>）には両方が乗る。
        /// </summary>
        [Test]
        public void PlayAlert_CountsSeparatelyFromPlain()
        {
            var go = new GameObject("switch-audio-cue-test");
            try
            {
                var cue = Wake(go);

                Assert.IsTrue(cue.HasClips, "素の切替音を掴めていない");
                Assert.IsTrue(cue.HasAlertClip, "警告つきの切替音を掴めていない");
                Assert.AreEqual(SwitchAudioCue.DefaultVariantCount, cue.ClipCount,
                                "変種を全部掴めていない（swVar の左が落ちる）");

                cue.Play();
                Assert.AreEqual(1, cue.PlayedCount, "素の 1 回は累計に乗る");
                Assert.AreEqual(0, cue.AlertCount, "素は警告つきに数えない");

                cue.PlayAlert();
                Assert.AreEqual(2, cue.PlayedCount, "累計は両方を数える");
                Assert.AreEqual(1, cue.AlertCount, "警告つきは別に数える");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// <b>同じ変種が 2 回続けて鳴らない</b>（0112 の要）。
        /// ⚠ 素と警告つきで番号を共有しているので、**交ぜて鳴らしても**続かないこと。
        /// </summary>
        [Test]
        public void Pick_NeverRepeatsBackToBack()
        {
            var go = new GameObject("switch-audio-cue-pick-test");
            try
            {
                var cue = Wake(go);
                var pick = typeof(SwitchAudioCue)
                    .GetMethod("Pick", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(pick, "Pick が無い");

                int last = -1;
                for (int i = 0; i < 400; i++)
                {
                    int v = (int)pick!.Invoke(cue, new object[] { SwitchAudioCue.DefaultVariantCount });
                    Assert.AreNotEqual(last, v, $"{i} 回目に同じ変種が 2 回続いた");
                    Assert.IsTrue(v >= 0 && v < SwitchAudioCue.DefaultVariantCount, "範囲外");
                    last = v;
                }
                Assert.AreEqual(SwitchAudioCue.DefaultVariantCount, cue.DistinctUsedCount,
                                "400 回引いても全部の変種が出ていない（一様でない）");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// 変種が 1 本しか無くても止まらないこと（焼き途中の状態で体験を壊さない）。
        /// </summary>
        [Test]
        public void Pick_WithSingleVariant_DoesNotThrow()
        {
            var go = new GameObject("switch-audio-cue-single-test");
            try
            {
                var cue = Wake(go);
                var pick = typeof(SwitchAudioCue)
                    .GetMethod("Pick", BindingFlags.Instance | BindingFlags.NonPublic);
                for (int i = 0; i < 8; i++)
                {
                    Assert.AreEqual(0, (int)pick!.Invoke(cue, new object[] { 1 }));
                }
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>⚠ Edit モードでは Awake が走らない（音源を掴む処理がそこに居る）。</summary>
        private static SwitchAudioCue Wake(GameObject go)
        {
            go.AddComponent<AudioSource>();
            var cue = go.AddComponent<SwitchAudioCue>();
            typeof(SwitchAudioCue)
                .GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(cue, null);
            return cue;
        }
    }
}
