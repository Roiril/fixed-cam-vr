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
    /// HMD 内の文字面は 3 つあり、どれも <see cref="StatusHud.StaffViewing"/> という同じ門を通す。
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

        [Test]
        public void IntroPrompt_FollowsGate()
        {
            var hud = Spawn<StatusHud>();
            var prompt = Spawn<IntroPrompt>();

            Wire(prompt, "statusHud", hud);
            Assert.That(Gate(prompt), Is.False, "右 B を押していない ＝ 導入の合図は出さない");

            hud.SetVisible(true);
            Assert.That(Gate(prompt), Is.True, "スタッフが見ているときは従来どおり読める");
        }

        /// <summary>
        /// **位置合わせ中は、視界に重なる面が登録ガイダンスへ譲る。**
        ///
        /// 門を <see cref="StatusHud.StaffViewing"/> へ統一した時（2026-08-07）、登録中も
        /// <c>IsActive</c> 経由で門が開くようにした。ところが登録ガイダンスを出しているのは
        /// StatusHud 自身（1.6m）で、IntroPrompt(1.5m) と ShowEndingFader(0.3m) はその手前に重なる。
        /// 結果、**トリガー長押しで登録へ入っても導入の「そのまま前へ進んでください」しか見えず、
        /// モードが変わっていないように見えた**（同日に現地で発覚）。
        ///
        /// 手元の <see cref="ControllerGuidePanel"/> は視界に重ならないので譲らない
        /// （登録中こそ操作早見表が要る）。
        /// </summary>
        [Test]
        public void Registration_SuppressesOverlappingSurfaces()
        {
            var hud = Spawn<StatusHud>();
            var reg = Spawn<Tracking.CourseRegistrationController>();
            Wire(hud, "registration", reg);

            var prompt = Spawn<IntroPrompt>();
            Wire(prompt, "statusHud", hud);
            var panel = Spawn<ControllerGuidePanel>();
            Wire(panel, "statusHud", hud);

            Assert.That(hud.RegistrationActive, Is.False, "初期状態は登録していない");

            reg.Toggle();   // トリガー長押しで位置合わせへ入る
            Assert.That(hud.RegistrationActive, Is.True);
            Assert.That(hud.StaffViewing, Is.True, "登録は必ずスタッフの仕事＝門は開く");

            Assert.That(Gate(prompt), Is.False,
                "導入の合図(1.5m)は登録ガイダンス(1.6m)を手前から隠してはいけない");
            Assert.That(Gate(panel), Is.True,
                "手元の早見表は視界に重ならない＝登録中こそ出す");
        }

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
    }
}
