#nullable enable
using System.Collections.Generic;
using FixedCamVr.Streaming.Cg;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 合成プレビュー（<c>Tools/FixedCamVr/Diagnostics/Preview Show Composite</c>）の**決め事**を固定する。
    ///
    /// ここが狂うと出力 PNG は「それらしく」出たまま中身が嘘になる — 撮り漏らし（人形が出るはずの
    /// カットが 1 枚足りない）、別カメラのプレートを当てての誤診、区間 post が乗った実機と違う色。
    /// **どれも絵を見ても気づけない**ので、目視ではなくここで止める。
    /// 対応する本番の振る舞い: TakeRunner の cameraIndex 決定 / ShowControlClient.ApplyPostForActive /
    /// capture-server.py の保存名規約。
    /// </summary>
    public sealed class ShowCompositePreviewPlanTests
    {
        private static ShowStepDef Step(string cg = "", int camera = -1, string source = TakeSchema.SourceLive)
            => new ShowStepDef { cg = cg, camera = camera, source = source };

        private static ShowTimelineDef Timeline(params ShowTimelineSegmentDef[] segments)
            => new ShowTimelineDef { rev = 1, schema = 3, segments = segments };

        private static ShowTimelineSegmentDef Segment(int lap, int camera, params ShowTakeDef[] takes)
            => new ShowTimelineSegmentDef { lap = lap, camera = camera, takes = takes };

        private static ShowTakeDef Take(string id, params ShowStepDef[] steps)
            => new ShowTakeDef { id = id, steps = steps };

        // ---- 撮影対象の収集 ----

        [Test]
        public void CollectShots_TakesOnlyStepsWithCg()
        {
            ShowTimelineDef tl = Timeline(Segment(3, 2,
                Take("t", Step(), Step(cg: "doll"), Step(cg: ""), Step(cg: "doll2"))));

            List<ShowCompositePreviewPlan.Shot> shots = ShowCompositePreviewPlan.CollectShots(tl);

            Assert.AreEqual(2, shots.Count, "cg が空のカットは撮らない");
            Assert.AreEqual(1, shots[0].stepIndex);
            Assert.AreEqual(3, shots[1].stepIndex, "step index は元の並びのまま（詰めない）");
        }

        [Test]
        public void CollectShots_KeepsAuthoredOrder()
        {
            // 並べ替えると前回出力との差分比較が効かなくなる。show.json の並び順そのままであること。
            ShowTimelineDef tl = Timeline(
                Segment(2, 1, Take("b", Step(cg: "doll"))),
                Segment(1, 0, Take("a", Step(cg: "doll"))));

            List<ShowCompositePreviewPlan.Shot> shots = ShowCompositePreviewPlan.CollectShots(tl);

            Assert.AreEqual(2, shots.Count);
            Assert.AreEqual("b", shots[0].takeId);
            Assert.AreEqual("a", shots[1].takeId);
        }

        [Test]
        public void CollectShots_ToleratesNullsAndEmpty()
        {
            // ライブ / キャッシュ / 焼き込みのどれからでも欠けた形が来る。例外で全滅させない。
            Assert.IsEmpty(ShowCompositePreviewPlan.CollectShots(null));
            Assert.IsEmpty(ShowCompositePreviewPlan.CollectShots(new ShowTimelineDef()));
            Assert.IsEmpty(ShowCompositePreviewPlan.CollectShots(
                Timeline(Segment(1, 0), new ShowTimelineSegmentDef { lap = 1, camera = 1, takes = null! })));
        }

        [Test]
        public void CollectShots_FillsTakeIdWhenBlank()
        {
            // id 未指定の演出は本番と同じ既定 id（L<lap>C<cam>#<index>）で呼ぶ。卓のリボンの表示と揃える。
            ShowTimelineDef tl = Timeline(Segment(3, 2, Take("", Step(cg: "doll"))));

            List<ShowCompositePreviewPlan.Shot> shots = ShowCompositePreviewPlan.CollectShots(tl);

            Assert.AreEqual("L3C2#0", shots[0].takeId);
        }

        // ---- 人形を構えるカメラ ----

        [Test]
        public void ResolveCamera_PrefersStepCamera()
        {
            Assert.AreEqual(3, ShowCompositePreviewPlan.ResolveCamera(Step(camera: 3), segmentCamera: 1));
        }

        [Test]
        public void ResolveCamera_FallsBackToSegmentCamera()
        {
            // step.camera 未指定（-1）は「いま映しているゾーンのカメラ」。素材カットでも同じ扱い
            // （人形の姿勢を決めるのは、その構図を撮った実カメラだから）。
            Assert.AreEqual(1, ShowCompositePreviewPlan.ResolveCamera(Step(camera: -1), segmentCamera: 1));
            Assert.AreEqual(1, ShowCompositePreviewPlan.ResolveCamera(
                Step(camera: -1, source: TakeSchema.SourceStill), segmentCamera: 1));
        }

        [Test]
        public void CollectShots_UsesResolvedCameraNotSegmentCamera()
        {
            ShowTimelineDef tl = Timeline(Segment(3, 2, Take("t", Step(cg: "doll", camera: 0))));

            ShowCompositePreviewPlan.Shot shot = ShowCompositePreviewPlan.CollectShots(tl)[0];

            Assert.AreEqual(0, shot.camera, "映すカメラ");
            Assert.AreEqual(2, shot.segmentCamera, "区間のカメラは別に残す");
        }

        // ---- プレートの対応付け ----

        private static readonly string[] Plates =
        {
            "camA_20260726_163310_287.jpg",
            "camA_20260725_090000_000.jpg",
            "camB_20260726_074727_877.jpg",
            "cap_20260726_120000_000.jpg",
            "notes.txt",
        };

        [Test]
        public void PickPlate_PicksNewestForCamera()
        {
            // 時刻部分が固定幅なので辞書順の最大 = 最新。
            Assert.AreEqual("camA_20260726_163310_287.jpg", ShowCompositePreviewPlan.PickPlate(Plates, "A"));
        }

        [Test]
        public void PickPlate_IgnoresOtherCamerasAndUnprefixed()
        {
            // **別カメラの絵を当てるのが一番まずい**（人形は合っているのに「ずれている」と誤診する）。
            // カメラの分からない cap_* も使わない。
            Assert.AreEqual("camB_20260726_074727_877.jpg", ShowCompositePreviewPlan.PickPlate(Plates, "B"));
            Assert.IsNull(ShowCompositePreviewPlan.PickPlate(Plates, "C"));
            Assert.IsNull(ShowCompositePreviewPlan.PickPlate(Plates, "D"));
        }

        [Test]
        public void PickPlate_RequiresExactIdBeforeUnderscore()
        {
            // "camAB_" は id "A" のプレートではない（前方一致だけで拾うと隣のカメラを掴む）。
            var files = new[] { "camAB_20260726_163310_287.jpg" };
            Assert.IsNull(ShowCompositePreviewPlan.PickPlate(files, "A"));
            Assert.AreEqual("camAB_20260726_163310_287.jpg", ShowCompositePreviewPlan.PickPlate(files, "AB"));
        }

        [Test]
        public void PickPlate_OnlyImageExtensions()
        {
            var files = new[] { "camA_1.txt", "camA_2.webm", "camA_3.png" };
            Assert.AreEqual("camA_3.png", ShowCompositePreviewPlan.PickPlate(files, "A"));
        }

        [Test]
        public void PickPlate_NullsAndEmptyAreSafe()
        {
            Assert.IsNull(ShowCompositePreviewPlan.PickPlate(null, "A"));
            Assert.IsNull(ShowCompositePreviewPlan.PickPlate(Plates, ""));
            Assert.IsNull(ShowCompositePreviewPlan.PickPlate(Plates, null));
            Assert.IsNull(ShowCompositePreviewPlan.PickPlate(new[] { (string)null!, "" }, "A"));
        }

        // ---- 出力ファイル名 ----

        [Test]
        public void ShotFileName_IsSortableAndShellSafe()
        {
            ShowTimelineDef tl = Timeline(Segment(3, 2, Take("", Step(cg: "doll"))));
            ShowCompositePreviewPlan.Shot shot = ShowCompositePreviewPlan.CollectShots(tl)[0];

            // 既定 id の '#' は落とす（Windows では通ってしまうので気づきにくい）。
            Assert.AreEqual("3_2_L3C2-0_0.png", ShowCompositePreviewPlan.ShotFileName(shot));
        }

        [Test]
        public void SanitizeToken_KeepsSafeCharsOnly()
        {
            Assert.AreEqual("cue_A-1", ShowCompositePreviewPlan.SanitizeToken("cue_A-1"));
            Assert.AreEqual("a-b-c", ShowCompositePreviewPlan.SanitizeToken("a/b c"));
            Assert.AreEqual("noid", ShowCompositePreviewPlan.SanitizeToken(""));
            Assert.AreEqual("noid", ShowCompositePreviewPlan.SanitizeToken(null));
        }

        [Test]
        public void CalibCheckFileName_IsPerCamera()
        {
            Assert.AreEqual("calibcheck_2.png", ShowCompositePreviewPlan.CalibCheckFileName(2));
        }

        // ---- post の解決 ----

        [Test]
        public void ResolvePost_StepWins()
        {
            var step = new ShowStepDef { hasPost = true, post = new PostParams { exposure = 1f } };
            var cam = new PostParams { exposure = 2f };
            var global = new PostParams { exposure = 3f };

            Assert.AreEqual(1f, ShowCompositePreviewPlan.ResolvePost(step, cam, global).exposure, 1e-4f);
        }

        [Test]
        public void ResolvePost_FallsBackCameraThenGlobal()
        {
            var cam = new PostParams { exposure = 2f };
            var global = new PostParams { exposure = 3f };

            Assert.AreEqual(2f, ShowCompositePreviewPlan.ResolvePost(Step(), cam, global).exposure, 1e-4f);
            Assert.AreEqual(3f, ShowCompositePreviewPlan.ResolvePost(Step(), null, global).exposure, 1e-4f);
        }

        [Test]
        public void ResolvePost_PresentFlagFalseIsIgnored()
        {
            // JsonUtility はキーが無くても入れ子を既定値で作る。宣言 bool が false なら**無いもの**として扱う
            // （ここを !=null で導くと、全カットに幽霊の post が乗る＝2026-07-23 監査 critical と同型）。
            var step = new ShowStepDef { hasPost = false, post = new PostParams { exposure = 1f } };
            var global = new PostParams { exposure = 3f };

            Assert.AreEqual(3f, ShowCompositePreviewPlan.ResolvePost(step, null, global).exposure, 1e-4f);
        }

        [Test]
        public void ResolvePost_NoDataIsNeutral()
        {
            // 何も無ければ素通し（コントラスト 1 / 彩度 1）。0 埋めにすると真っ黒な絵が出る。
            PostParams p = ShowCompositePreviewPlan.ResolvePost(null, null, null);

            Assert.AreEqual(0f, p.exposure, 1e-4f);
            Assert.AreEqual(1f, p.contrast, 1e-4f);
            Assert.AreEqual(1f, p.saturation, 1e-4f);
        }
    }
}
