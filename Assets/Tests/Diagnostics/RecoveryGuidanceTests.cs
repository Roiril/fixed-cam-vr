#nullable enable
using System;
using NUnit.Framework;

namespace FixedCamVr.Diagnostics.Tests
{
    /// <summary>
    /// 異常文言の純関数 <see cref="RecoveryGuidance"/> の検証。
    /// <b>このクラスの存在理由は「全異常に手順があること」を機械で固定すること</b> —
    /// 旧実装は異常 8 種のうち復帰動作を書いているものが 0 で、読んだスタッフは現場で黙って立つしか
    /// なかった。文言を場当たりに足すと同じ穴が再生産される。
    /// </summary>
    public sealed class RecoveryGuidanceTests
    {
        [Test]
        public void EveryAlert_HasBothWhatAndHow()
        {
            foreach (ShowAlert a in Enum.GetValues(typeof(ShowAlert)))
            {
                if (a == ShowAlert.None) continue;
                Assert.That(RecoveryGuidance.What(a, 2), Is.Not.Empty, $"{a} に「何が起きたか」が無い");
                Assert.That(RecoveryGuidance.How(a), Is.Not.Empty,
                    $"{a} に「何をすれば直るか」が無い — 読んでも動けない異常を足してはいけない");
            }
        }

        [Test]
        public void None_ReturnsEmpty()
        {
            Assert.That(RecoveryGuidance.What(ShowAlert.None), Is.Empty);
            Assert.That(RecoveryGuidance.How(ShowAlert.None), Is.Empty);
        }

        [Test]
        public void CameraAlerts_NameTheCamera()
        {
            // 3 台のうちどれを見に行けばいいか分からないとスタッフは動けない。
            Assert.That(RecoveryGuidance.What(ShowAlert.NoVideo, 2), Does.Contain("カメラ2"));
            Assert.That(RecoveryGuidance.What(ShowAlert.Throttled, 3), Does.Contain("カメラ3"));
        }

        [Test]
        public void CameraAlerts_OmitNumber_WhenUnknown()
        {
            Assert.That(RecoveryGuidance.What(ShowAlert.NoVideo, 0), Does.Contain("カメラの映像"));
        }

        [Test]
        public void NoRetiredWords_Anywhere()
        {
            // 語は「位置合わせ」「×印」「点」の 3 語に固定する。開発語が混ざったら落とす。
            string[] retired = { "残差", "誤差", "基準点", "再登録", "マーク", "砂嵐", "course", "周回リセット" };
            foreach (ShowAlert a in Enum.GetValues(typeof(ShowAlert)))
            {
                string text = RecoveryGuidance.What(a, 1) + " " + RecoveryGuidance.How(a);
                foreach (string w in retired)
                    Assert.That(text, Does.Not.Contain(w), $"{a} に廃語「{w}」が入っている");
            }
        }

        [Test]
        public void IntroAborted_TellsTheOneStepFix()
        {
            // 復帰は 1 段（位置合わせを確定し直すだけで導入がやり直される）。
            // 2 段手順を書き戻したら落ちる — 順序を逆にすると直らない手順を人間に暗記させないため。
            string how = RecoveryGuidance.How(ShowAlert.IntroAborted);
            Assert.That(how, Does.Contain("トリガー2秒"));
            Assert.That(how, Does.Not.Contain("グリップ"),
                "ランリセットは要らない（IntroDirector.TryRecoverFromAbort が構造で 1 段にした）");
        }

        [Test]
        public void Priority_PutsStoppedExperienceFirst()
        {
            // 宣言順が優先度。体験が止まっているものが先。
            Assert.That((int)ShowAlert.IntroAborted, Is.LessThan((int)ShowAlert.NeedsReRegistration));
            Assert.That((int)ShowAlert.NeedsReRegistration, Is.LessThan((int)ShowAlert.NoVideo));
            Assert.That((int)ShowAlert.NoVideo, Is.LessThan((int)ShowAlert.Throttled));
            Assert.That((int)ShowAlert.Throttled, Is.LessThan((int)ShowAlert.FloorNotMeasured));
        }
    }
}
