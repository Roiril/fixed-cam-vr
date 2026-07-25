#nullable enable
using System.IO;
using System.Linq;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// v3 スキーマ（takes / steps）の wire 契約テスト。共有 fixture
    /// <c>Assets/Tests/Fixtures/show_timeline_v3_canonical.json</c> を JsonUtility でパースし、
    /// 設計 §6 の形がそのまま読めることを固定する（段 A で Web 側もこの fixture を読む）。
    ///
    /// v3 が入れ子を作らずフラットな文字列判別子にした理由がここで効く: present-flag が必要なのは
    /// <c>hasPost</c> だけで、JsonUtility の「省略キーの入れ子を既定インスタンスで作る」癖に晒される面が最小になる。
    /// したがって契約は「flag が honest bool と一致し、flag=false の入れ子を consumer が読まない」こと
    /// （v2 の TimelineFixtureContractTests と同じ流儀。null 性は実装詳細なので assert しない）。
    /// </summary>
    public sealed class TakeFixtureContractTests
    {
        private static ShowTimelineDef Load()
        {
            string path = Path.Combine(Application.dataPath, "Tests/Fixtures/show_timeline_v3_canonical.json");
            Assert.IsTrue(File.Exists(path), $"fixture が見つからない: {path}");
            var tl = JsonUtility.FromJson<ShowTimelineDef>(File.ReadAllText(path));
            Assert.IsNotNull(tl, "timeline のパースに失敗");
            return tl!;
        }

        private static ShowTakeDef Take(ShowTimelineDef tl, int lap, int camera)
        {
            ShowTimelineSegmentDef? seg = tl.segments.FirstOrDefault(s => s != null && s.lap == lap && s.camera == camera);
            Assert.IsNotNull(seg, $"区間 ({lap},{camera}) が無い");
            Assert.That(seg!.takes.Length, Is.GreaterThan(0), $"区間 ({lap},{camera}) に演出が無い");
            return seg.takes[0];
        }

        [Test]
        public void Fixture_IsRecognizedAsV3()
        {
            ShowTimelineDef tl = Load();
            Assert.That(tl.schema, Is.EqualTo(3));
            Assert.That(tl.IsV3(), Is.True);
            Assert.That(tl.HasData(), Is.True);
        }

        [Test]
        public void EnterTake_ParsesStartRuleAndInheritance()
        {
            ShowTakeDef t = Take(Load(), 1, 0);
            Assert.That(t.id, Is.EqualTo("L1C0#0"));
            Assert.That(t.at, Is.EqualTo(TakeSchema.AtEnter));
            Assert.That(t.IsExit, Is.False);
            Assert.That(t.offsetSec, Is.EqualTo(2f));
            Assert.That(t.SkipWhenMissed, Is.False, "fireOnExit（既定）");
            Assert.That(t.IsYield, Is.False, "hold（既定）");
            Assert.That(t.once, Is.True);
            Assert.That(TakeSchema.ResolveMaxDuration(t.maxDurationSec),
                Is.EqualTo(TakeSchema.DefaultMaxDurationSec), "0 はコード既定 45s");

            ShowStepDef s = t.steps[0];
            Assert.That(s.source, Is.EqualTo(TakeSchema.SourceInherit));
            Assert.That(s.cueId, Is.EqualTo("cue_A_1"));
            Assert.That(s.IsUntilClipEnd, Is.True);
            Assert.That(s.strength, Is.EqualTo(-1f), "-1 は素材定義から継承");
            Assert.That(s.hasPost, Is.False);
        }

        [Test]
        public void ExitTake_ParsesAnchorAndSegmentPost()
        {
            ShowTimelineDef tl = Load();
            ShowTakeDef t = Take(tl, 2, 1);
            Assert.That(t.IsExit, Is.True);
            Assert.That(t.steps[0].source, Is.EqualTo(TakeSchema.SourceLive));
            Assert.That(t.steps[0].camera, Is.EqualTo(2));
            Assert.That(t.steps[0].durSec, Is.EqualTo(3f));

            ShowTimelineSegmentDef seg = tl.segments.First(s => s.lap == 2 && s.camera == 1);
            Assert.That(seg.hasPost, Is.True, "区間 post は takes と独立に生きている");
            Assert.That(seg.post!.exposure, Is.EqualTo(0.3f));
        }

        [Test]
        public void RequestedShowcase_FourStepTake_ParsesInOrder()
        {
            // ユーザー要求の演出形: D 4s → 事前映像① → 1周目B録画+CG人形 → 1周目C録画+CG人形
            ShowTakeDef t = Take(Load(), 3, 1);
            Assert.That(t.offsetSec, Is.EqualTo(20f));
            Assert.That(t.maxDurationSec, Is.EqualTo(40f), "演出ごとの watchdog 上書き");
            Assert.That(t.steps.Length, Is.EqualTo(4));

            Assert.That(t.steps[0].source, Is.EqualTo(TakeSchema.SourceLive));
            Assert.That(t.steps[0].camera, Is.EqualTo(3), "新カメラ D");
            Assert.That(t.steps[0].durSec, Is.EqualTo(4f));

            Assert.That(t.steps[1].source, Is.EqualTo(TakeSchema.SourceClip));
            Assert.That(t.steps[1].assetUrl, Is.EqualTo("sa://assets/pre_01.mp4"));
            Assert.That(t.steps[1].IsUntilClipEnd, Is.True, "流し終わったら次へ");

            Assert.That(t.steps[2].assetUrl, Is.EqualTo("sa://assets/rec_lap1_B.mp4"));
            Assert.That(t.steps[2].cueId, Is.EqualTo("cg_doll_B"), "録画映像に CG 人形を重ねる");
            Assert.That(t.steps[2].strength, Is.EqualTo(0.8f), "明示値は継承しない");
            Assert.That(t.steps[2].hasPost, Is.True);
            Assert.That(t.steps[2].post!.saturation, Is.EqualTo(0.6f));

            Assert.That(t.steps[3].assetUrl, Is.EqualTo("sa://assets/rec_lap1_C.mp4"));
            Assert.That(t.steps[3].cueId, Is.EqualTo("cg_doll_C"));
            Assert.That(t.steps[3].hasPost, Is.False);
        }

        [Test]
        public void JsonUtility_RoundTrip_PreservesContract()
        {
            // 端末キャッシュ（persistentDataPath へ ToJson で書き戻す）を経ても契約が壊れないこと。
            ShowTimelineDef tl = Load();
            var back = JsonUtility.FromJson<ShowTimelineDef>(JsonUtility.ToJson(tl));

            Assert.That(back.schema, Is.EqualTo(3));
            Assert.That(back.IsV3(), Is.True);
            ShowTakeDef t = Take(back, 3, 1);
            Assert.That(t.steps.Length, Is.EqualTo(4));
            Assert.That(t.steps[1].assetUrl, Is.EqualTo("sa://assets/pre_01.mp4"));
            Assert.That(t.steps[0].strength, Is.EqualTo(-1f), "-1 継承マーカーが往復で壊れない");
            Assert.That(t.steps[2].hasPost, Is.True);
            Assert.That(t.steps[3].hasPost, Is.False);
            Assert.That(t.at, Is.EqualTo(TakeSchema.AtEnter));
        }

        [Test]
        public void EnsureTakes_IsIdempotentOnV3Fixture()
        {
            ShowTimelineDef tl = Load();
            string before = JsonUtility.ToJson(tl);
            TimelineMigration.EnsureTakes(tl);
            Assert.That(JsonUtility.ToJson(tl), Is.EqualTo(before),
                "既に takes を持つ v3 タイムラインは変換で一切変わらない");
        }
    }
}
