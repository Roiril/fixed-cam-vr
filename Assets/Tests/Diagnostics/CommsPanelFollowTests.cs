#nullable enable
using System.Collections.Generic;
using System.Reflection;
using FixedCamVr.Diagnostics;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Tests.Diagnostics
{
    /// <summary>
    /// AIエージェントの面が<b>どこから出てくるか</b>を固定する（2026-09-04・ユーザー報告
    /// 「エージェントのスクリーンが出るとき、毎回、違うところから回ってくるような感じになっている」）。
    ///
    /// 追従（<see cref="YawFollowLogic"/>）は <c>LateUpdate</c> の中でしか進まず、
    /// 面が畳まれているあいだは 1 度も呼ばれない。だから種を持ち越すと
    /// <b>前に消えた場所のヨー</b>で凍り、次の連絡はそこから現在の頭へ向かって回り込んでくる。
    /// 体験者は区間を歩いて向きを変えるので、差は 180° まで開く。
    ///
    /// ⚠⚠ <b>これは画にしか出ない。</b> 段も文字数も打鍵も全部正しいまま、
    /// 出る方角だけが毎回違う（<c>[XP]</c> にも <c>ev=comms</c> にも 1 ビットも残らない）。
    /// だから機械はここで持つ。
    /// </summary>
    public sealed class CommsPanelFollowTests
    {
        private readonly List<GameObject> _spawned = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _spawned)
                if (go != null) Object.DestroyImmediate(go);
            _spawned.Clear();
        }

        private static readonly BindingFlags Priv =
            BindingFlags.Instance | BindingFlags.NonPublic;

        private static object? Get(object o, string field) =>
            o.GetType().GetField(field, Priv)?.GetValue(o);

        private static void Set(object o, string field, object? value)
        {
            FieldInfo? f = o.GetType().GetField(field, Priv);
            Assert.That(f, Is.Not.Null, $"CommsPanel.{field} が無い");
            f!.SetValue(o, value);
        }

        private static void Call(object o, string method) =>
            o.GetType().GetMethod(method, Priv)?.Invoke(o, null);

        private GameObject NewGo(string name)
        {
            var go = new GameObject(name);
            _spawned.Add(go);
            return go;
        }

        /// <summary>面と、その前に立つ頭を組む。⚠ Edit モードでは Awake が走らないので明示的に呼ぶ。</summary>
        private (CommsPanel panel, Transform head) SpawnPanel()
        {
            var panel = NewGo("[Test] CommsPanel").AddComponent<CommsPanel>();
            Call(panel, "Awake");
            if (!panel.IsBuilt)
                Assert.Ignore("日本語フォントを解決できないので面が組まれていない（実機と同じ挙動）");
            Transform head = NewGo("[Test] Head").transform;
            Set(panel, "head", head);
            return (panel, head);
        }

        private static CommsPanelLogic Logic(CommsPanel p) => (CommsPanelLogic)Get(p, "_logic")!;

        private static YawFollowLogic Follow(CommsPanel p) => (YawFollowLogic)Get(p, "_yawFollow")!;

        private static bool Seeded(CommsPanel p) => (bool)Get(p, "_yawSeeded")!;

        private static void LookAtYaw(Transform head, float yawDeg) =>
            head.rotation = Quaternion.Euler(0f, yawDeg, 0f);

        /// <summary>畳み切るまで段を送る（Tick は 1 回に 1 段しか進めない）。</summary>
        private static void Fold(CommsPanelLogic logic)
        {
            for (int i = 0; i < 10 && logic.Active; i++) logic.Tick(10f);
            Assert.That(logic.Active, Is.False, "畳み切れていない");
        }

        /// <summary>畳まれているあいだに種を捨てる。捨てないと次に出る所が前回に引きずられる。</summary>
        [Test]
        public void WhileFolded_TheFollowSeedIsDropped()
        {
            var (panel, head) = SpawnPanel();

            LookAtYaw(head, 90f);
            Logic(panel).Begin(4);
            Call(panel, "LateUpdate");
            Assert.That(Seeded(panel), Is.True, "出ているあいだは種を持つ");

            Fold(Logic(panel));
            Call(panel, "LateUpdate");
            Assert.That(Seeded(panel), Is.False, "畳まれたら種を捨てる");
        }

        /// <summary>
        /// <b>出る瞬間はいつも頭の正面。</b> 前に消えた場所（ここでは 90° 横）から
        /// 回り込んでこない。
        /// </summary>
        [Test]
        public void WhenItOpensAgain_ItStartsInFrontOfTheHead()
        {
            var (panel, head) = SpawnPanel();

            // 1 通目。体験者は真横（90°）を向いていた。
            LookAtYaw(head, 90f);
            Logic(panel).Begin(4);
            Call(panel, "LateUpdate");
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(Follow(panel).CurrentYaw, 90f)),
                        Is.LessThan(0.01f), "出た所は頭の正面");

            Fold(Logic(panel));
            Call(panel, "LateUpdate");

            // 歩いて向きが変わってから 2 通目が届く。
            LookAtYaw(head, 0f);
            Logic(panel).Begin(4);
            Call(panel, "LateUpdate");

            float yaw = Follow(panel).CurrentYaw;
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(yaw, 0f)), Is.LessThan(0.01f),
                        $"2 通目も頭の正面から開く（実測 {yaw:0.0}°）— "
                        + "前に消えた 90° から回り込んでいたら、それがユーザー報告の症状");
        }

        /// <summary>
        /// 出ているあいだは種を落とさない。落とすと<b>毎フレーム正面へ吸着する</b> ＝
        /// 追従の緩急（本編のスクリーンと同じ法則）が消える。
        /// </summary>
        [Test]
        public void WhileOpen_TheSeedIsKept_SoTheFollowNeverSnaps()
        {
            var (panel, head) = SpawnPanel();

            LookAtYaw(head, 0f);
            Logic(panel).Begin(4);
            Call(panel, "LateUpdate");

            LookAtYaw(head, 90f);
            for (int i = 0; i < 3; i++)
            {
                Logic(panel).Tick(0.01f);
                Call(panel, "LateUpdate");
                Assert.That(Logic(panel).Active, Is.True, "まだ出ている");
                Assert.That(Seeded(panel), Is.True, "出ているあいだは種を持ち続ける");
            }
        }
    }
}
