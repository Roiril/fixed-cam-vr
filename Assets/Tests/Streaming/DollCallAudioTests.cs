#nullable enable
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 人形の呼びかけ「あーそぼー」（<c>canon/LEDGER.md</c> 0109）。
    /// 2 周目 C の接近で、<b>人形視点の最後のカット（追いつき）が始まると同時に</b>鳴る。
    ///
    /// ⚠⚠ <b>この音の失敗は画にも動画にも出ない。</b> 焼き忘れ・取り込み忘れ・
    /// 卓の白名簿漏れのどれが起きても「カットが無言で進む」だけなので、
    /// 実機の録画を何度見ても気づけない。機械で捕まえられるのは
    /// ここと、走行ログの <c>ev=sfx id=DollCall</c>（<c>tools/analyze-xp-log.py</c>）だけ。
    /// </summary>
    public sealed class DollCallAudioTests
    {
        private const string Res = "Sound/sfx_doll_call";

        /// <summary>
        /// Edit モードでは <c>Awake</c> が走らないので明示的に呼ぶ
        /// （音源を掴む処理と発声器の生成がそこに居る）。
        /// </summary>
        private static void Awake(Component c) =>
            c.GetType()
             .GetMethod("Awake", System.Reflection.BindingFlags.Instance
                                 | System.Reflection.BindingFlags.NonPublic)
             ?.Invoke(c, null);

        /// <summary>尺（秒）。<b>カット（1.4 秒）より長い</b>のが仕様。</summary>
        private const float ExpectedSec = 1.60f;

        /// <summary>追いつきのカットの尺（<c>show.json</c> の <c>pov_4</c>）。</summary>
        private const float CutSec = 1.4f;

        /// <summary>
        /// 登録簿と焼いたファイルの名前が一致している。
        /// ⚠ <c>SoundCueLogic.ResourceName</c> と <c>tools/ingest-sounds.py</c> の
        /// <c>VOICES</c> は**対**で、片方だけ直すと沈黙して食い違う。
        /// </summary>
        [Test]
        public void ResourceName_MatchesTheBakedFile()
        {
            Assert.That(SoundCueLogic.ResourceName(SoundCue.DollCall), Is.EqualTo("sfx_doll_call"));
            // 変種を持たない（`ShowSoundDirector.PlaySpot` は変種持ちを撃たない）。
            Assert.That(SoundCueLogic.VariantCount(SoundCue.DollCall), Is.EqualTo(1));
        }

        /// <summary>
        /// ⚠ 焼いた後に <c>tools/unity.ps1 menu sound-import</c> を通していないと
        /// Resources から引けない（wav がプロジェクトへインポートされていない）。
        /// </summary>
        [Test]
        public void Clip_IsInResources()
        {
            var clip = Resources.Load<AudioClip>(Res);
            Assert.IsNotNull(clip,
                             $"{Res} が無い — "
                             + "`py -3.11 tools/ingest-sounds.py --only sfx_doll_call` の後に "
                             + "`tools/unity.ps1 menu sound-import` を走らせること");
        }

        /// <summary>
        /// <b>声はカットより長い。</b> ユーザー指示（0109）が「1.6 秒で少しずれるが、
        /// 0.2 秒なので映像は無視して進んでよい」という形で成り立っているので、
        /// ここが縮むと**指示そのものが別のものになる**。
        ///
        /// ⚠ 自動の無音落とし（<c>ingest-sounds.py</c> の <c>trim</c>）に任せると
        /// 尻の減衰を掴めず 1.23 秒でぶつ切りになる。だから <c>VOICES</c> の
        /// <c>cut</c> は決め打ちにしてある。**この門はそれが戻っていないかを見ている。**
        /// </summary>
        [Test]
        public void Clip_OverhangsTheCut()
        {
            var clip = Resources.Load<AudioClip>(Res);
            if (clip == null) Assert.Ignore("音源が取り込まれていない（Clip_IsInResources を先に見る）");
            Assert.That(clip!.length, Is.EqualTo(ExpectedSec).Within(0.02f),
                        "尺が変わっている — `VOICES` の `cut` を戻したか、自動の無音落としに戻っている");
            Assert.That(clip.length, Is.GreaterThan(CutSec),
                        "声がカットより短くなった — 0109 の「0.2 秒はみ出す」が成り立たない");
        }

        /// <summary>
        /// <b>カットが持つ音として鳴る。</b> <c>ShowSoundDirector.PlaySpot</c> が
        /// 掴めた音を鳴らし、<c>LastCue</c> と累計（走行ログ <c>ev=sfx</c> の出どころ）が動く。
        ///
        /// ⚠ Edit モードでは <c>Awake</c> が走らないので明示的に呼ぶ
        /// （音源を掴む処理と声の生成がそこに居る）。
        /// </summary>
        [Test]
        public void PlaySpot_FiresAndIsCounted()
        {
            var go = new GameObject("doll-call-test");
            try
            {
                // ⚠ **発声器を先に起こす。** Edit モードでは Awake が走らないので声が 0 本になり、
                //    鳴らしたつもりで PlayedCount が動かない（この門が 1 度そこで落ちた）。
                var sfx = go.AddComponent<SfxPlayer>();
                Awake(sfx);
                var dir = go.AddComponent<ShowSoundDirector>();
                Awake(dir);

                if (Resources.Load<AudioClip>(Res) == null)
                    Assert.Ignore("音源が取り込まれていない（Clip_IsInResources を先に見る）");

                int before = dir.SpotCount;
                Assert.IsTrue(dir.PlaySpot(SoundCue.DollCall), "呼びかけを鳴らせなかった");
                Assert.That(dir.SpotCount, Is.EqualTo(before + 1), "一撃の累計が動いていない");
                Assert.That(dir.LastCue, Is.EqualTo(SoundCue.DollCall),
                            "走行ログの `ev=sfx id=` がこの値を出す");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// カットの <c>dollCall</c> が wire を往復する（卓 ⇄ 実機）。
        /// ⚠ <c>timeline-model.js</c> の白名簿から漏れると、**卓が 💾 保存した一押しで消える**。
        /// 向こう側の門は <c>tools/web-compositor/timeline-model.test.mjs</c>。
        /// </summary>
        [Test]
        public void DollCall_SurvivesTheWire()
        {
            const string json = "{\"source\":\"clip\",\"cueId\":\"pov_4\",\"durSec\":1.4,"
                                + "\"switchSfx\":true,\"dollCall\":true}";
            var step = JsonUtility.FromJson<ShowStepDef>(json);
            Assert.IsNotNull(step);
            Assert.IsTrue(step!.dollCall, "呼びかけが wire を渡っていない");
            Assert.IsTrue(step.switchSfx, "切替音と併せて立てられる（層が違う音なので潰れない）");

            // ⚠ 既定は false。既存の show.json は 1 ビットも挙動が変わらない
            //   （JsonUtility は欠落キーを false で埋める）。
            var old = JsonUtility.FromJson<ShowStepDef>("{\"source\":\"live\",\"camera\":2}");
            Assert.IsFalse(old.dollCall, "指定していないカットで鳴ってはいけない");
        }
    }
}
