#nullable enable
using System.IO;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// <b>ホラー軽減モード</b>（2026-09-05 ユーザー指定・<c>canon/LEDGER.md</c> 0154）。
    ///
    /// ⚠ ここが守るのは<b>音でしか出ない壊れ方</b>:
    /// 前の体験者の選択が次の人へ持ち越す／倍率が 0.5 でなくなる／
    /// 「掛ける口」が 2 か所に増えて二乗になる。
    /// <b>どれも画にも録画にも 1 ビットも出ない</b>ので、機械が持つしかない。
    /// </summary>
    public sealed class HorrorReliefTests
    {
        [SetUp]
        public void Reset() => HorrorRelief.Reset();

        [TearDown]
        public void Restore() => HorrorRelief.Reset();

        /// <summary>押さなければ平時。<b>既定で軽減モードに入っていてはいけない。</b></summary>
        [Test]
        public void Default_IsOff()
        {
            Assert.IsFalse(HorrorRelief.Enabled);
            Assert.AreEqual(0, HorrorRelief.ChangeCount);
            Assert.AreEqual(1f, HorrorRelief.ShowGain, 1e-6f, "平時に既存の音を触っている");
        }

        /// <summary>ユーザー指定「既存の音が 1/2 になり」。</summary>
        [Test]
        public void On_HalvesTheExistingSound()
        {
            HorrorRelief.Toggle();
            Assert.IsTrue(HorrorRelief.Enabled);
            Assert.AreEqual(0.5f, HorrorRelief.Gain, 1e-6f, "倍率が半分でない");
            Assert.AreEqual(HorrorRelief.Gain, HorrorRelief.ShowGain, 1e-6f);
        }

        /// <summary>
        /// <b>出入りできる</b>。一方通行にすると、誤って長押しした体験者に出口が無い
        /// （右コントローラはスタッフのもので、左は同じボタンしかない）。
        /// </summary>
        [Test]
        public void Toggle_GoesBothWays()
        {
            Assert.IsTrue(HorrorRelief.Toggle(), "1 回目で入らない");
            Assert.IsFalse(HorrorRelief.Toggle(), "2 回目で出られない");
            Assert.AreEqual(2, HorrorRelief.ChangeCount);
        }

        /// <summary>
        /// ⚠⚠ <b>体験者が替わったら落ちる</b>（ユーザー指定「周回リセット時に次に持ち込まない」）。
        /// 回数も 0 へ戻す — 戻さないとテレメトリの <c>relief=</c> が走行をまたいで積み上がる。
        /// </summary>
        [Test]
        public void Reset_DropsEverything()
        {
            HorrorRelief.Toggle();
            HorrorRelief.Reset();
            Assert.IsFalse(HorrorRelief.Enabled);
            Assert.AreEqual(0, HorrorRelief.ChangeCount);
            Assert.AreEqual(1f, HorrorRelief.ShowGain, 1e-6f);
        }

        /// <summary>
        /// <see cref="HorrorRelief.Select"/> は検査用なので<b>回数を動かさない</b>
        /// （<see cref="ShowLanguage.Select"/> と同じ約束。動かすとテレメトリが嘘になる）。
        /// </summary>
        [Test]
        public void Select_DoesNotCount()
        {
            HorrorRelief.Select(true);
            Assert.IsTrue(HorrorRelief.Enabled);
            Assert.AreEqual(0, HorrorRelief.ChangeCount);
        }

        /// <summary>
        /// 注意書きの中の長押しは<b>異変の報告（1.0 秒）より長い</b>。
        /// ⚠ 同じボタンの短押しが言語の切り替えなので、巡らせるための押下が
        /// 軽減モードへ化けてはいけない。
        /// </summary>
        [Test]
        public void HoldSec_IsLongerThanTheVisitorMark()
        {
            Assert.Greater(HorrorRelief.HoldSec, FixedCamVr.Input.VisitorMarkHoldLogic.DefaultHoldSec,
                           "報告と同じか短いと、言語を巡らせる押下が軽減モードへ化ける");
        }

        private static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        /// <summary>
        /// ⚠⚠ <b>戻す場所は <c>TitleScreen.BeginTitle</c> の <see cref="ShowLanguage.Reset"/> の隣</b>、
        /// ただ 1 か所。<c>ShowSoundDirector.ResetRun</c> は「自分で書いてあるのに本番から 1 度も
        /// 呼ばれていなかった」（<c>memory/visitor_sound_reset.md</c>）ので、
        /// <b>呼び出し元が実在することを機械で持つ</b>。
        /// ⚠ 隣に置くのは、あちらが<b>相の遷移からもランリセットからも卓の ⏭ からも必ず通る</b>
        /// と実証済みの縁だから。別の場所へ足すと二重に戻して体験者の選択を消す。
        /// </summary>
        [Test]
        public void Reset_IsCalledFromTheSamePlaceAsTheLanguage()
        {
            string src = File.ReadAllText(
                Path.Combine(RepoRoot, "Assets", "Scripts", "Streaming", "TitleScreen.cs"));
            StringAssert.Contains("ShowLanguage.Reset();", src, "言語の戻し場所が消えている");
            StringAssert.Contains("HorrorRelief.Reset();", src,
                                  "TitleScreen.BeginTitle が軽減モードを戻していない"
                                  + "（前の体験者の選択が次の人へ持ち越す）");
        }

        /// <summary>
        /// ⚠⚠ <b>陽気な曲は段も相も読まない</b>（2026-09-05 ユーザー指定
        /// 「ホラー軽減モードにしたら、<b>注意書きの時点から</b>陽気な BGM をループさせよう」）。
        ///
        /// 注意書きは導入の段 0（<c>TitleStage.Wait</c>）で、本編は相 <c>Run</c>。
        /// 段や相で分岐を書いた瞬間に「注意書きでは鳴らない」「終幕で止まる」が生まれ、
        /// <b>どれも音でしか出ない</b>（画にも録画にも 1 ビットも出ない）。
        /// ⇒ 読んでよいのは <see cref="HorrorRelief.Enabled"/> だけ、を機械で持つ。
        /// </summary>
        [Test]
        public void ReliefBgm_DoesNotReadThePhaseOrTheStage()
        {
            string src = File.ReadAllText(Path.Combine(
                RepoRoot, "Assets", "Scripts", "Streaming", "Sound", "HorrorReliefAudio.cs"));
            // ⚠ コメントには段の名前が出てよい（説明のため）。**コードの行だけ**を見る。
            var code = new System.Text.StringBuilder();
            foreach (string line in src.Split('\n'))
            {
                string t = line.TrimStart();
                if (t.StartsWith("///") || t.StartsWith("//")) continue;
                code.Append(line).Append('\n');
            }
            foreach (string banned in new[]
                     { "ShowPhase", "IntroStage", "TitleStage", "OutroStage",
                       "ShowRunDirector", "IntroDirector", "OutroDirector", "TitleScreen" })
            {
                StringAssert.DoesNotContain(banned, code.ToString(),
                    $"陽気な曲が {banned} を読んでいる — 段や相で鳴らない場面が生まれる"
                    + "（注意書きの時点から鳴る、が壊れる）");
            }
        }

        /// <summary>
        /// ⚠⚠ <b>倍率を掛ける口は 1 つだけ。</b> <c>ShowSoundDirector</c> の <c>masterGain</c> にも
        /// 掛けると、<c>SfxPlayer.Play</c> の中でもう一度掛かって<b>二乗になる</b>
        /// （同ファイルが 2026-08 に同じ罠を警告している）。
        /// 実装は <c>HorrorReliefAudio</c> の <c>AudioListener.volume</c> ただ 1 行。
        /// </summary>
        [Test]
        public void ShowGain_IsAppliedInExactlyOnePlace()
        {
            string root = Path.Combine(RepoRoot, "Assets", "Scripts");
            int uses = 0;
            foreach (string path in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(path) == "HorrorRelief.cs") continue;   // 定義そのもの
                if (File.ReadAllText(path).Contains("HorrorRelief.ShowGain")) uses++;
            }
            Assert.AreEqual(1, uses,
                            $"ShowGain を掛けている実装が {uses} 箇所ある（1 箇所でなければ"
                            + "掛け忘れか二乗のどちらか。どちらも画にも録画にも出ない）");
        }
    }
}
