#nullable enable
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Diagnostics.Tests
{
    /// <summary>
    /// **体験者の視界に文字を出さない**（2026-08-07 制定・ユーザー指摘「体験者が被っているときに
    /// 表示する文字、世界観を壊すので消してください。スタッフの時は表示していい」）。
    ///
    /// スタッフ向けの面は <see cref="StatusHud.StaffViewing"/> という同じ門を通す。
    /// ⚠ <b>例外が 1 つある</b> — 報告ボタンの面（<see cref="VisitorMarkPanel"/>）は体験者に見せる
    /// （2026-08-15・<c>canon/LEDGER.md</c> 0050）。門を足させないための test も下にある。
    /// この不変条件は<b>破れても沈黙する</b> — 実機を被って初めて分かり、ログにも `[XP]` にも出ない。
    /// だから門の判断だけは機械で固定しておく。
    ///
    /// ⚠ 守れるのは「門が正しく開閉するか」まで。**新しい文字面を足した人が門を通し忘れる**のは
    /// ここでは捕まえられない（各面が自分で <c>StaffViewing()</c> を呼ぶ設計のため）。
    /// 面を足すときは <c>.claude/rules/unity-vr.md</c> の「HMD に出す文言の規約」を読むこと。
    /// </summary>
    public sealed class HmdTextGateTests
    {
        private readonly List<GameObject> _spawned = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _spawned)
                if (go != null) Object.DestroyImmediate(go);
            _spawned.Clear();
        }

        private T Spawn<T>() where T : Component
        {
            var go = new GameObject("[Test] " + typeof(T).Name);
            _spawned.Add(go);
            return go.AddComponent<T>();
        }

        /// <summary>各面が持つ <c>private bool StaffViewing()</c> を呼ぶ。</summary>
        private static bool Gate(Component c)
        {
            MethodInfo? m = c.GetType().GetMethod("StaffViewing",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(m, Is.Not.Null,
                $"{c.GetType().Name} に StaffViewing() が無い — HMD 内の文字面は必ず門を通す");
            return (bool)m!.Invoke(c, null);
        }

        /// <summary>依存を差し込む（面によってフィールド名が違う）。</summary>
        private static void Wire(Component c, string fieldName, Object? hud)
        {
            FieldInfo? f = c.GetType().GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(f, Is.Not.Null, $"{c.GetType().Name}.{fieldName} が無い");
            f!.SetValue(c, hud);
        }

        // --- 門そのもの（StatusHud）---

        [Test]
        public void StaffViewing_ClosedByDefault()
        {
            var hud = Spawn<StatusHud>();
            // 体験者が被っている状態。右 B は押されておらず、位置合わせもしていない。
            Assert.That(hud.StaffViewing, Is.False);
        }

        [Test]
        public void StaffViewing_FollowsStatusToggle()
        {
            var hud = Spawn<StatusHud>();

            hud.SetVisible(true);       // スタッフが右 B で開いた
            Assert.That(hud.StaffViewing, Is.True);
            Assert.That(hud.IsVisible, Is.True, "門はトグルの真実源と食い違ってはいけない");

            hud.SetVisible(false);      // 閉じた ＝ 体験者に渡せる状態へ戻る
            Assert.That(hud.StaffViewing, Is.False);
        }

        // --- 各面が同じ門を見ているか ---
        //
        // 「解決できないときは出さない側へ倒す」も併せて固定する。判定できないときに出す設計だと、
        // StatusHud を持たないシーン・プレビューで体験者向けの文字が復活する（世界観を壊す側の
        // 失敗を既定にしない）。

        // ⚠ `IntroPrompt_FollowsGate` と `Registration_SuppressesOverlappingSurfaces` は
        //    2026-08-13 に消した。導入の合図（IntroPrompt）そのものを廃止したため
        //    （`canon/LEDGER.md` 0033）。**門の規約は残っている** — 視界に重なる面を足すときは
        //    `StatusHud.StaffViewing` を見て、位置合わせ中は登録ガイダンスへ譲ること。

        [Test]
        public void ControllerGuidePanel_FollowsGate()
        {
            var hud = Spawn<StatusHud>();
            var panel = Spawn<ControllerGuidePanel>();

            Wire(panel, "statusHud", hud);
            // 読み手はスタッフでも、パネルが浮くのはコントローラの位置＝体験者の視界の中。
            Assert.That(Gate(panel), Is.False);

            hud.SetVisible(true);
            Assert.That(Gate(panel), Is.True);
        }

        [Test]
        public void ShowEndingFader_FollowsGate()
        {
            var hud = Spawn<StatusHud>();
            var fader = Spawn<ShowEndingFader>();

            // ⚠ ShowEndingFader は SerializeField を持たず毎フレーム自己解決する（_run と同じ流儀）。
            Wire(fader, "_hud", hud);
            Assert.That(Gate(fader), Is.False, "体験者に見えるのは黒だけ");

            hud.SetVisible(true);
            Assert.That(Gate(fader), Is.True);

            // この面だけは自己解決しない（毎フレーム Resolve() が入れる）ので、居ないケースも書ける。
            // 他の 2 面は FindObjectOfType が開いているシーンの StatusHud を拾いうるため、
            // 「居ない → false」を EditMode で確かめると環境依存のテストになる（書かない）。
            Wire(fader, "_hud", null);
            Assert.That(Gate(fader), Is.False, "判定できないときは出さない側へ倒す");
        }

        /// <summary>
        /// ⚠⚠ <b>報告ボタンの面だけは門を通さない。</b> 2026-08-15 にユーザーがこの 1 面を名指しで
        /// 求めた（<c>canon/LEDGER.md</c> 0050「コントローラーの少し上、少し奥に、小さいスクリーンを
        /// 置いておいて、そこに、(X,Yで異変を報告) みたいに書いておいてほしい」）。
        ///
        /// このテストは<b>門を足させないため</b>にある。上の 3 面と並べて読むと
        /// 「体験者の視界に文字を出さない」に反しているように見えるので、
        /// 規約に忠実な次のシュビーが善意で <c>StaffViewing()</c> を足しうる。
        /// 足した瞬間、体験者には一生見えない面になる（しかも誰も気づかない）。
        /// </summary>
        [Test]
        public void VisitorMarkPanel_IsNotGatedByStaffViewing()
        {
            var panel = Spawn<VisitorMarkPanel>();

            MethodInfo? gate = panel.GetType().GetMethod("StaffViewing",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(gate, Is.Null,
                "報告ボタンの面は体験者に見せる（canon/LEDGER.md 0050）。StaffViewing の門を足さないこと");
        }

        /// <summary>
        /// ⚠⚠ <b>終幕の報告も門を通さない。</b> 体験そのものは既に終わっていて、この 4 行が
        /// 「終わった・HMD を外してよい」を伝える唯一の手段（<c>canon/LEDGER.md</c> 0048）。
        /// 門を足すと体験者には黒しか出ず、<b>いつまでも被ったまま待つことになる</b>。
        /// </summary>
        [Test]
        public void OutroReport_IsNotGatedByStaffViewing()
        {
            var report = Spawn<OutroReport>();

            MethodInfo? gate = report.GetType().GetMethod("StaffViewing",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(gate, Is.Null,
                "終幕の報告は体験者に見せる（canon/LEDGER.md 0048）。StaffViewing の門を足さないこと");
        }
    }
}
