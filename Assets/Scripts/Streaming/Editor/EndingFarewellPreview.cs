#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FixedCamVr.Streaming.Cg;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    public static partial class ShowCompositePreview
    {
        /// <summary>
        /// 帰還の別れの人形（LEDGER 0291）を、実プレート × 実合成シェーダ × 本番の立ち位置の解き方
        /// （<see cref="EndingFarewellLogic.TrySolve"/>）× 本番の時刻表（<see cref="EndingFarewellLogic.SampleAt"/>）で
        /// 段ごとに撮る。体験者は代役（actors[] の visitor）を締めのゾーンの重心に立てて実写へ焼き込む。
        ///
        /// 出力: <c>Logs/ending-farewell-preview/</c> に連番の PNG・帰還End の写真・<c>timeline.tsv</c>。
        /// これはデザインの確認で、実機の記録ではない（体験者の位置と頭の向きは仮置き）。
        /// </summary>
        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Ending Farewell", priority = 262)]
        public static void RunFarewell()
        {
            string root = Directory.GetParent(Application.dataPath)!.FullName;
            string outDir = Path.Combine(root, "Logs/ending-farewell-preview");
            if (Directory.Exists(outDir))
                foreach (string f in Directory.GetFiles(outDir, "*.png")) File.Delete(f);
            Directory.CreateDirectory(outDir);

            using FarewellScene scene = FarewellScene.Build(root, outDir);
            var tsv = new StringBuilder("file\tt\tphase\talpha\tturn01\tretreatM\tdollU\tdollV\tvisU\tvisV\n");
            var saved = new List<string>();

            // 0. 入れ替わりが終わった直後（人形が出る前）。
            saved.Add(scene.Shot("00_before.png", -1f, tsv));
            // 1. 時刻表を 0.25 秒刻みで。
            int i = 1;
            for (float t = 0f; t < EndingFarewellLogic.TotalSec + 0.2f; t += 0.25f, i++)
                saved.Add(scene.Shot($"{i:00}_t{t:0.00}.png", t, tsv));
            // 2. 段の代表（見つめる・回り切る直前・下がり始め・消えかけ）。
            float face = EndingFarewellLogic.AppearSec + EndingFarewellLogic.FaceSec - 0.05f;
            float turn = face + 0.05f + EndingFarewellLogic.TurnSec * 0.5f;
            float retreat = face + 0.05f + EndingFarewellLogic.TurnSec + 0.6f;
            float fading = EndingFarewellLogic.TotalSec - 0.4f;
            saved.Add(scene.Shot("key_1_face.png", face, tsv));
            saved.Add(scene.Shot("key_2_turn.png", turn, tsv));
            saved.Add(scene.Shot("key_3_retreat.png", retreat, tsv));
            saved.Add(scene.Shot("key_4_fading.png", fading, tsv));
            // 3. 帰還End の写真（見つめる段の最後の画。本番の EndingFrameCapture と同じく caption なし）。
            scene.SaveEndingPhoto("released-photo.png", face);

            File.WriteAllText(Path.Combine(outDir, "timeline.tsv"), tsv.ToString());
            File.WriteAllText(Path.Combine(outDir, "README.txt"),
                "Design preview of the farewell doll (LEDGER 0291). Not a visitor recording.\n" +
                $"Plate: {scene.PlateLabel}\nVisitor stand-in: actors[] visitor at the closing zone centroid.\n" +
                $"Anchor: {scene.AnchorText}\n");
            Debug.Log($"[FarewellPreview] {saved.Count} 枚 → {outDir}  {scene.AnchorText}");
        }

        /// <summary>帰還End の見本写真（体験者の代役 ＋ 見つめる別れの人形）。<see cref="CreateEndingSamples"/> が使う。</summary>
        private static Texture2D CreateFarewellReleasedSample(string root, string outDir)
        {
            using FarewellScene scene = FarewellScene.Build(root, outDir);
            float face = EndingFarewellLogic.AppearSec + EndingFarewellLogic.FaceSec - 0.05f;
            return scene.RenderEndingPhoto(face);
        }

        private sealed class FarewellScene : IDisposable
        {
            private readonly Stage _stage;
            private readonly Plate _baked;
            private readonly Texture2D _plateTexture;
            private readonly ShowActorDef _doll;
            private readonly Geometry _geom;
            private readonly PostParams _post;
            private readonly Vector3 _visitorFoot;
            private readonly EndingFarewellLogic.Anchor _anchor;
            public string PlateLabel { get; }
            public string AnchorText { get; }

            private FarewellScene(Stage stage, Plate baked, Texture2D plateTexture, ShowActorDef doll, Geometry geom,
                                  PostParams post, Vector3 visitorFoot, EndingFarewellLogic.Anchor anchor, string label)
            {
                _stage = stage; _baked = baked; _plateTexture = plateTexture; _doll = doll; _geom = geom;
                _post = post; _visitorFoot = visitorFoot; _anchor = anchor; PlateLabel = label;
                AnchorText = $"doll uv=({anchor.dollUv.x:0.000},{anchor.dollUv.y:0.000}) " +
                             $"visitor uv=({anchor.visitorUv.x:0.000},{anchor.visitorUv.y:0.000}) " +
                             $"gap={Vector3.Distance(anchor.foot, visitorFoot):0.00}m fromScreen={(anchor.fromScreen ? 1 : 0)} " +
                             $"faceYaw={EndingFarewellLogic.FaceYawDeg(anchor.foot, visitorFoot):0} awayYaw={anchor.awayYawDeg:0}";
            }

            public static FarewellScene Build(string root, string outDir)
            {
                string showPath = Path.Combine(root, "tools/web-compositor/show.json");
                ShowJson show = LoadShow(showPath, out _) ?? throw new InvalidOperationException("Missing show.json");
                var cues = JsonUtility.FromJson<EndingCueFile>(File.ReadAllText(showPath));
                EndingCue plateCue = Array.Find(cues.cues, c => c.id == "plate_A")
                    ?? throw new InvalidOperationException("Missing plate_A");
                Texture2D plateTexture = LoadEndingAsset(root, plateCue.sourceUrl);
                Stage stage = Stage.Create(outDir);
                try
                {
                    stage.ApplyLayout(show.layout);
                    stage.SetFeel(show.feel);
                    var plate = new Plate { texture = plateTexture, width = plateTexture.width,
                        height = plateTexture.height, label = plateCue.sourceUrl };
                    PreviewCameraDef? camera = show.CameraAt(0);
                    Geometry geom = stage.AimVirtualCamera(camera, plate.width, plate.height);
                    if (!geom.usable) throw new InvalidOperationException("Closing camera has no calibration");

                    ShowCompositePreviewPlan.Shot? closing = ShowCompositePreviewPlan.CollectShots(show.timeline)
                        .Find(s => s.takeId == "L4C0#0" && s.step.durKind == "untilMark")
                        ?? throw new InvalidOperationException("Missing closing untilMark step");
                    ShowActorDef doll = show.FindActor(closing.step.cg)
                        ?? throw new InvalidOperationException("Missing closing doll actor");
                    ShowActorDef visitor = show.FindActor(TakeSchema.SwapHumanActorId)
                        ?? throw new InvalidOperationException("Missing visitor actor");
                    if (!TryFollowStand(show, closing, camera, out Vector2 stand, out float yaw))
                        throw new InvalidOperationException("No closing visitor position");

                    // 体験者の代役を立てて実写へ焼き込む（本番では映像の中の本人）。
                    stage.SetPlateLuma(plate);
                    stage.UseActor(visitor);
                    stage.PlaceActor(visitor, stand, yaw, geom);
                    Vector3 visitorFoot = stage.ActorFootWorld;
                    Plate baked = stage.BakeActorIntoPlate(plate, "+visitor");

                    // 別れの人形の立ち位置は本番と同じ関数で解く。
                    Vector2 contain = stage.ContainFor(plate);
                    if (!EndingFarewellLogic.TrySolve(stage.CgCamera, contain, visitorFoot, out var anchor))
                        throw new InvalidOperationException("Farewell anchor did not resolve");
                    stage.UseActor(doll);
                    PostParams post = show.CameraPost(0) ?? show.post ?? new PostParams();
                    return new FarewellScene(stage, baked, plateTexture, doll, geom, post, visitorFoot, anchor,
                                             plateCue.sourceUrl);
                }
                catch
                {
                    stage.Dispose();
                    UnityEngine.Object.DestroyImmediate(plateTexture);
                    throw;
                }
            }

            private EndingFarewellLogic.Sample Pose(float t)
            {
                EndingFarewellLogic.Sample s = EndingFarewellLogic.SampleAt(t);
                bool shown = s.phase >= EndingFarewellLogic.Phase.Appear && s.phase <= EndingFarewellLogic.Phase.Retreat;
                if (!shown)
                {
                    _stage.HideActor();
                    _stage.CgStrength = 0f;
                    return s;
                }
                float faceYaw = EndingFarewellLogic.FaceYawDeg(_anchor.foot, _visitorFoot);
                float yawDeg = EndingFarewellLogic.TurnYawDeg(faceYaw, _anchor.awayYawDeg, s.turn01);
                _stage.PlaceActorAtWorld(_doll, _anchor.foot + _anchor.retreatDir * s.retreatM, yawDeg, _geom);
                _stage.CgStrength = s.alpha;
                return s;
            }

            public string Shot(string file, float t, StringBuilder tsv)
            {
                EndingFarewellLogic.Sample s = Pose(t);
                string caption = t < 0f
                    ? "farewell: before (swap finished, doll not yet shown)"
                    : $"farewell t={t:0.00}s  {s.phase}  alpha {s.alpha:0.00}  turn {s.turn01:0.00}  back {s.retreatM:0.00}m";
                _stage.Composite(_baked, true, _post, caption);
                string path = _stage.Save(file);
                tsv.Append(file).Append('\t').Append(t.ToString("0.00")).Append('\t').Append(s.phase)
                   .Append('\t').Append(s.alpha.ToString("0.00")).Append('\t').Append(s.turn01.ToString("0.00"))
                   .Append('\t').Append(s.retreatM.ToString("0.000"))
                   .Append('\t').Append(_anchor.dollUv.x.ToString("0.000")).Append('\t').Append(_anchor.dollUv.y.ToString("0.000"))
                   .Append('\t').Append(_anchor.visitorUv.x.ToString("0.000")).Append('\t').Append(_anchor.visitorUv.y.ToString("0.000"))
                   .Append('\n');
                return path;
            }

            public Texture2D RenderEndingPhoto(float t)
            {
                Pose(t);
                _stage.Composite(_baked, true, _post, "");
                return _stage.ReadEndingTexture();
            }

            public void SaveEndingPhoto(string file, float t)
            {
                Texture2D tex = RenderEndingPhoto(t);
                try { File.WriteAllBytes(Path.Combine(_stage.OutDir, file), tex.EncodeToPNG()); }
                finally { UnityEngine.Object.DestroyImmediate(tex); }
            }

            public void Dispose()
            {
                _stage.CgStrength = 1f;
                _stage.Dispose();
                UnityEngine.Object.DestroyImmediate(_plateTexture);
            }
        }
    }
}
