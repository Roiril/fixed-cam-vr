#nullable enable
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 人形視点が差し込まれるカットの切替音（<c>canon/LEDGER.md</c> 0106）。
    ///
    /// ⚠⚠ <b>音は録画に映らない。</b> しかもこの音の失敗は「素の切替音が鳴る」なので、
    /// 実機で被って聴いても気づけない（鳴ってはいる）。焼き忘れ・取り込み忘れを
    /// 機械で捕まえられるのはここと、走行ログの <c>swAlert</c> だけ。
    /// </summary>
    public sealed class SwitchAudioCueTests
    {
        /// <summary>
        /// ⚠ 焼いた後に <c>tools/unity.ps1 menu sound-import</c> を通していないと
        /// Resources から引けない（wav がプロジェクトへインポートされていない）。
        /// </summary>
        [Test]
        public void AlertClip_IsInResources()
        {
            var clip = Resources.Load<AudioClip>(SwitchAudioCue.AlertResourceName);
            Assert.IsNotNull(clip,
                             $"{SwitchAudioCue.AlertResourceName} が無い — "
                             + "`py -3.11 tools/ingest-sounds.py --only sfx_switch_alert` の後に "
                             + "`tools/unity.ps1 menu sound-import` を走らせること");
        }

        /// <summary>
        /// 素の切替音と警告つきは<b>別のクリップ</b>。同じ実体なら合成が効いていない
        /// （＝ 差し込みが素の音で鳴っているのに、掴めているので警告も出ない）。
        /// </summary>
        [Test]
        public void AlertClip_IsNotThePlainSwitchClip()
        {
            var plain = Resources.Load<AudioClip>($"{SwitchAudioCue.DefaultResourcePrefix}1");
            var alert = Resources.Load<AudioClip>(SwitchAudioCue.AlertResourceName);
            Assert.IsNotNull(plain, "素の切替音が無い");
            Assert.IsNotNull(alert, "警告つきの切替音が無い");
            Assert.AreNotSame(plain, alert, "素の切替音と警告つきが同じ実体になっている");
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
                go.AddComponent<AudioSource>();
                var cue = go.AddComponent<SwitchAudioCue>();
                // ⚠ Edit モードでは Awake が走らない（音源を掴む処理がそこに居る）。
                typeof(SwitchAudioCue)
                    .GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.Invoke(cue, null);

                Assert.IsTrue(cue.HasClips, "素の切替音を掴めていない");
                Assert.IsTrue(cue.HasAlertClip, "警告つきの切替音を掴めていない");

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
    }
}
