#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using FixedCamVr.Streaming;
using FixedCamVr.Streaming.Cg;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// <b>人の代役を「人形が立っている」と読ませない</b>（2026-08-22・実機の走行で 2 穴を踏んだ）。
    ///
    /// 持続の覆い（<c>swapHold</c>）と入れ替わりのほどける段は、覆いの形の供給元として
    /// <b>人の代役（<see cref="TakeSchema.SwapHumanActorId"/>）</b>を立てる。
    /// <see cref="ShowCgLayer.IsVisible"/> は「CG の層が描かれているか」なので代役でも true になり、
    /// それを人形の在否として読むと 2 つ壊れる:
    ///
    /// 1. <b>音</b> — 2 周目 C の保持中に「入れ替わった人形の笑い」が鳴り、カメラ C なので
    ///    増員（swell）まで育つ。実測（走行 20260822_080731）: sndSwap 0 → 2.29 / sndSwell 0 → 0.71
    /// 2. <b>入れ替わりの向き</b> — 保持から渡る swap カットで <c>fromDoll</c> が true に化け、
    ///    「人形 → 人形 ＝ 成立しない」へ倒れて<b>引き継ぎが 1 度も走らない</b>
    ///
    /// ⚠⚠ <b>どちらもプレビュー（<c>menu swap</c>）の絵には出ない。</b> あれは TakeRunner も
    ///   ShowSoundDirector も通らないので、**実機の走行のログでしか捕まらない**。
    ///   だから機械で固定する（`AppIsolationTests` と同じ「ソースを読む規約テスト」の流儀）。
    /// </summary>
    public class DollVisibilityTests
    {
        [Test]
        public void TheStandIn_IsNotADoll()
        {
            Assert.IsTrue(ShowCgLayer.IsDoll(true, "doll"), "人形は人形");
            Assert.IsFalse(ShowCgLayer.IsDoll(true, TakeSchema.SwapHumanActorId),
                           "人の代役（覆いの形の供給元）を人形として読まない");
            Assert.IsFalse(ShowCgLayer.IsDoll(false, "doll"), "描かれていなければ居ない");
            Assert.IsFalse(ShowCgLayer.IsDoll(true, ""), "誰も立っていなければ居ない");
        }

        /// <summary>
        /// 「人形が立っているか」を訊く側が <c>IsVisible</c> を読んでいないこと。
        ///
        /// ⚠ <c>IsVisible</c> 自体は正当な用途がある（CG の層が描かれているかの診断・
        ///   <see cref="SwapMorphFx"/> が自分の立てた代役を確かめる）。だから
        ///   **消費者ごとに許す / 許さないを決める**。
        /// </summary>
        [Test]
        public void Consumers_AskingWhetherADollIsPresent_ReadDollVisible()
        {
            // 人形の在否を訊く側（ここに IsVisible が出てはいけない）。
            var mustUseDollVisible = new[]
            {
                "Scripts/Streaming/Sound/ShowSoundDirector.cs",   // 人形の笑い
                "Scripts/Streaming/TakeRunner.cs",                // 入れ替わりの向き（fromDoll）
            };

            var offenders = new List<string>();
            foreach (string rel in mustUseDollVisible)
            {
                string path = Path.Combine(Application.dataPath, rel);
                Assert.IsTrue(File.Exists(path), $"見つからない: {rel}");
                string src = File.ReadAllText(path);

                // コメント行は除く（罠の説明で IsVisible の語が出るのは正しい）。
                foreach (string line in src.Split('\n'))
                {
                    string t = line.Trim();
                    if (t.StartsWith("//") || t.StartsWith("///") || t.StartsWith("*")) continue;
                    // 終幕写真の解除区間は人形の在否ではなくCG全体の消灯を確認する。
                    // 人の代役も残してはいけないため、ここだけ IsVisible が正しい。
                    if (rel.EndsWith("TakeRunner.cs")
                        && t == "bool cgActive = _cgLayer != null && _cgLayer.IsVisible;") continue;
                    if (Regex.IsMatch(line, @"\b_?cg(Layer)?\s*[.!]?\s*\.\s*IsVisible|\bcg\.IsVisible"))
                        offenders.Add($"{rel}: {t}");
                }
            }

            Assert.IsEmpty(offenders,
                "人形の在否は ShowCgLayer.DollVisible で訊く（IsVisible は人の代役でも true になる）:\n"
                + string.Join("\n", offenders));
        }
    }
}
