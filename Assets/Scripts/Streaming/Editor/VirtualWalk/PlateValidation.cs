#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using FixedCamVr.Streaming.Cg;
using FixedCamVr.Streaming.EditorTools.VirtualWalk;
using FixedCamVr.Tracking;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace FixedCamVr.Streaming.EditorTools.VirtualWalk
{
    /// <summary>保存済みの無人プレートで、人物と本番人形の寸法・接地・遮蔽を一緒に見る。</summary>
    public static class PlateValidation
    {
        [MenuItem("Tools/FixedCamVr/Diagnostics/Validate Real Plate Composite", priority = 259)]
        public static void Run() => ShowCompositePreview.RunPlateValidation();
    }
}

namespace FixedCamVr.Streaming.EditorTools
{
    public static partial class ShowCompositePreview
    {
        private const string PlateValidationRoot = "G:/マイドライブ/shubie/renders/plate-validation";

        [Serializable]
        private sealed class PlateValidationReport
        {
            public string show = "", output = "", visitor = "", dollPrefab = "";
            public string measurement = "Mesh height and projected-height differences include posture and body thickness; they are not zero-error acceptance gates. Manual calibration rms=0/points=0 does not establish physical accuracy: inspect source_*.jpg with calibration_*.png. Ordinary center/left/right pairs require both complete bodies visible; wall tests and B/C views of A's ending placement intentionally measure occlusion and are not evidence of a visible natural composite.";
            public bool complete;
            public PlateValidationFrame[] frames = Array.Empty<PlateValidationFrame>();
            public string[] skippedCases = Array.Empty<string>();
            public int endingAnimationFrames, endingAnimationFps = 30;
            public string endingAnimation = "ending-animation/f%05d.jpg";
            public float measuredDollBodyRadiusM, planningDollBodyRadiusM;
        }

        [Serializable]
        private sealed class PlateValidationFrame
        {
            public string camera = "", pose = "", plate = "", plateSha256 = "", geometry = "", png = "", blend = "";
            public string purpose = "";
            public int sourceWidth, sourceHeight, occluders;
            public bool calibrated, facing, personRight, photoReady;
            public bool visibilityRequired, constraintsPassed;
            public float expectedVisitorVisibleFraction, expectedDollVisibleFraction;
            public float walkDistanceM, animationTimeSec;
            public float actorsClearanceM;
            public Vector2 requestedVisitor, requestedDoll;
            public PlateValidationBody visitor = new(), doll = new();
        }

        [Serializable]
        private sealed class PlateValidationBody
        {
            public Vector3 foot;
            public Vector3 localBoundsMinM, localBoundsMaxM;
            public Vector3 feetLocalMinM, feetLocalMaxM;
            public int feetVertices;
            public float maxBodyRadiusM;
            public Vector2 viewportFoot;
            public float yawDeg, expectedHeightM, measuredHeightM, heightErrorM, soleErrorM;
            public float rawSoleErrorM, groundingCorrectionM;
            public bool rawSoleMeasured;
            public float minWallClearanceM, inFrameVertexFraction, projectedHeightPx, expectedHeightPx, projectedHeightErrorPx;
            public float visibleFraction, luma, saturation, grain;
            public int vertices, pixelsUnoccluded, pixelsVisible, materials, textures;
            public bool materialsResolved, feetInFrame;
        }

        private readonly struct PlateValidationPose
        {
            public readonly string Name;
            public readonly Vector2 Visitor, Doll;
            public readonly float Walk;
            public readonly bool Ending, Occlusion, OptionalWalking;
            public PlateValidationPose(string name, Vector2 visitor, Vector2 doll, float walk = 0f, bool ending = false, bool occlusion = false, bool optionalWalking = false)
            { Name = name; Visitor = visitor; Doll = doll; Walk = walk; Ending = ending; Occlusion = occlusion; OptionalWalking = optionalWalking; }
        }

        /// <summary>既存 Stage を共有し、実写・本番の較正・人形・影・合成材質をそのまま使う。</summary>
        public static void RunPlateValidation()
        {
            if (!EditorCliArgs.EnsureScene("Assets/Scenes/Main.unity"))
                throw new InvalidOperationException("[PlateValidation] Main scene could not be opened.");
            string explicitShow = EditorCliArgs.Get("show") ?? Path.Combine(Application.streamingAssetsPath, "show/show.json");
            ShowJson show = LoadShow(explicitShow, out string showPath)
                ?? throw new FileNotFoundException("[PlateValidation] show.json is missing.", explicitShow);
            ShowLayoutDef layout = show.layout ?? throw new InvalidOperationException("[PlateValidation] layout is missing.");
            if (!layout.hasRoom || layout.room == null || !layout.room.HasData())
                throw new InvalidOperationException("[PlateValidation] Authored room geometry is required.");
            ShowActorDef doll = show.FindActor("doll")
                ?? throw new InvalidOperationException("[PlateValidation] Production doll actor is missing.");
            if (Resources.Load<GameObject>(doll.prefab) == null)
                throw new InvalidOperationException("[PlateValidation] Production doll prefab is missing: " + doll.prefab);
            ShowActorDef visitorDef = show.FindActor("visitor")
                ?? throw new InvalidOperationException("[PlateValidation] Visitor actor is missing.");
            string outDir = Path.Combine(PlateValidationRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(outDir);
            var report = new PlateValidationReport { show = showPath, output = outDir, dollPrefab = doll.prefab };
            var frames = new List<PlateValidationFrame>();
            var skipped = new List<string>();
            var trash = new List<UnityEngine.Object>();
            AmbientMode ambientMode = RenderSettings.ambientMode;
            Color ambientColor = RenderSettings.ambientLight;
            var lights = UnityEngine.Object.FindObjectsOfType<Light>();
            var lightEnabled = new bool[lights.Length];
            for (int i = 0; i < lights.Length; i++) { lightEnabled[i] = lights[i].enabled; lights[i].enabled = false; }
            try
            {
                using var stage = Stage.Create(outDir);
                stage.ApplyLayout(layout);
                stage.SetFeel(show.feel);
                var proxy = PlateValidationField<ShowRoomProxy>(stage, "_roomProxy");
                var lightGo = new GameObject("[PlateValidation light]") { hideFlags = HideFlags.HideAndDontSave };
                trash.Add(lightGo);
                Light sun = lightGo.AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.cullingMask = 1 << LayerMask.NameToLayer(CgLayerName);
                ShowRoomLightDef roomLight = layout.room.hasLight && layout.room.light != null ? layout.room.light : new ShowRoomLightDef();
                sun.transform.rotation = Quaternion.Euler(roomLight.pitchDeg, roomLight.yawDeg, 0f);
                sun.color = ShowCgLayer.KelvinToLinearColor(roomLight.tempK).gamma;
                sun.intensity = roomLight.intensity;
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = Color.white * roomLight.ambient;

                GameObject visitorPrefab = Resources.Load<GameObject>(visitorDef.prefab)
                    ?? throw new InvalidOperationException("[PlateValidation] Visitor prefab is missing.");
                GameObject person = UnityEngine.Object.Instantiate(visitorPrefab);
                person.hideFlags = HideFlags.HideAndDontSave;
                trash.Add(person);
                foreach (MonoBehaviour behaviour in person.GetComponentsInChildren<MonoBehaviour>(true)) behaviour.enabled = false;
                person.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                person.transform.localScale = Vector3.one;
                Bounds bind = PlateValidationBounds(PlateValidationVertices(person));
                if (bind.size.y < 0.1f) throw new InvalidOperationException("[PlateValidation] Visitor height is invalid.");
                person.transform.localScale = Vector3.one * (visitorDef.heightM / bind.size.y);
                SetLayerRecursive(person.transform, LayerMask.NameToLayer(CgLayerName));
                using var avatar = new VisitorAvatar(person, trash);
                report.visitor = avatar.Description;
                var visitorNav = new WalkNavigation(layout, avatar.IdleBodyRadiusM, 0.04f, 0.5f, visitorDef.heightM);
                var dollNav = new WalkNavigation(layout, 0.18f, 0.04f, 0.5f, doll.heightM);
                EndingFarewellLogic.Anchor? endingFromA = null;

                foreach (bool endingPass in new[] { false, true })
                foreach (string id in new[] { "A", "B", "C" })
                {
                    PreviewCameraDef? camera = null;
                    foreach (PreviewCameraDef candidate in show.cameras) if (candidate.id == id) { camera = candidate; break; }
                    if (camera == null) throw new InvalidOperationException("[PlateValidation] Camera " + id + " is missing.");
                    string platePath = PlateValidationLatestPlate(id);
                    var texture = new Texture2D(2, 2, TextureFormat.RGB24, true) { hideFlags = HideFlags.HideAndDontSave };
                    trash.Add(texture);
                    byte[] plateBytes = File.ReadAllBytes(platePath);
                    if (!texture.LoadImage(plateBytes, false)) throw new InvalidDataException("[PlateValidation] Invalid plate: " + platePath);
                    string sha;
                    using (var hash = System.Security.Cryptography.SHA256.Create())
                        sha = BitConverter.ToString(hash.ComputeHash(plateBytes)).Replace("-", "").ToLowerInvariant();
                    File.WriteAllBytes(Path.Combine(outDir, "source_" + id + Path.GetExtension(platePath)), plateBytes);
                    var plate = new Plate { texture = texture, width = texture.width, height = texture.height, label = Path.GetFileName(platePath) };
                    Geometry geometry = stage.AimVirtualCamera(camera, plate.width, plate.height);
                    if (!geometry.usable || !geometry.fromCalib)
                        throw new InvalidOperationException("[PlateValidation] Matching calibration is required for camera " + id + ": " + geometry.label);
                    stage.SetPlateLuma(plate);
                    person.SetActive(false);
                    stage.HideActor();
                    stage.ShowWire(false);
                    stage.Composite(plate, false, new PostParams(), "camera " + id + " | real plate check", false);
                    PlateValidationVerifySource(stage, plate, id, outDir);
                    stage.ShowWire(true);
                    stage.Composite(plate, true, new PostParams(), "camera " + id + " | " + plate.label + " | calibration wire", false);
                    stage.Save("calibration_" + id + ".png");
                    stage.ShowWire(false);
                    var normalPositions = new List<Vector2>();

                    foreach (PlateValidationPose pose in PlateValidationPoses(layout))
                    {
                        if (pose.Ending != endingPass) continue;
                        Vector2 personXz, dollXz;
                        float personYaw, dollYaw;
                        float walkDistance = pose.Walk;
                        EndingFarewellLogic.Anchor ending = default;
                        if (pose.Ending)
                        {
                            if (id == "A")
                            {
                                if (!PlateValidationCorner(layout.room, out Vector2 corner, out Vector2 endA, out Vector2 endB))
                                    throw new InvalidOperationException("[PlateValidation] L wall endpoints are missing.");
                                Func<Vector3, Vector3, bool> acceptPair = PlateValidationPairAcceptance(stage, geometry, layout,
                                    person, avatar, doll, visitorNav, dollNav, out float measuredRadius, out Func<EndingFarewellLogic.Anchor, bool> acceptTrajectory);
                                float planningRadius = Mathf.Max(measuredRadius, EndingFarewellLogic.DefaultDollBodyRadiusM * doll.heightM / 0.4f);
                                report.measuredDollBodyRadiusM = measuredRadius; report.planningDollBodyRadiusM = planningRadius;
                                Debug.Log(string.Format(CultureInfo.InvariantCulture, "[PlateValidation] doll CPU max XZ radius={0:F8}m; runtime planning radius={1:F8}m", measuredRadius, planningRadius));
                                if (!EndingFarewellLogic.TrySolveAtWall(stage.CgCamera, stage.ContainFor(plate),
                                    PlateValidationWorld(corner, layout.room.floorY), PlateValidationWorld(endA, layout.room.floorY),
                                    PlateValidationWorld(endB, layout.room.floorY), out ending, visitorDef.heightM,
                                    avatar.IdleBodyRadiusM, doll.heightM, canStand: (point, radius) =>
                                        point.x - StageOriginX >= visitorNav.Bounds.xMin + radius
                                        && point.x - StageOriginX <= visitorNav.Bounds.xMax - radius
                                        && point.z >= visitorNav.Bounds.yMin + radius && point.z <= visitorNav.Bounds.yMax - radius,
                                    pairAccepted: acceptPair, dollBodyRadiusM: planningRadius, trajectoryAccepted: acceptTrajectory))
                                    throw new InvalidOperationException("[PlateValidation] Production ending placement cannot be solved for A.");
                                endingFromA = ending;
                            }
                            else ending = endingFromA ?? throw new InvalidOperationException("[PlateValidation] A ending anchor was not validated.");
                            personXz = new Vector2(ending.visitorTargetFoot.x - StageOriginX, ending.visitorTargetFoot.z);
                            dollXz = new Vector2(ending.foot.x - StageOriginX, ending.foot.z);
                            personYaw = EndingFarewellLogic.FaceYawDeg(ending.visitorTargetFoot, ending.foot);
                            dollYaw = EndingFarewellLogic.FaceYawDeg(ending.foot, ending.visitorTargetFoot);
                        }
                        else if (pose.Occlusion)
                        {
                            personXz = PlateValidationFindStand(pose.Visitor, visitorNav);
                            dollXz = PlateValidationFindStand(pose.Doll, dollNav, personXz, avatar.IdleBodyRadiusM + 0.16f);
                            personYaw = EndingFarewellLogic.FaceYawDeg(PlateValidationWorld(personXz, 0), PlateValidationWorld(dollXz, 0));
                            dollYaw = EndingFarewellLogic.FaceYawDeg(PlateValidationWorld(dollXz, 0), PlateValidationWorld(personXz, 0));
                        }
                        else if (!PlateValidationSelectPair(stage, geometry, layout, person, avatar, visitorDef, doll, visitorNav, dollNav,
                            pose, normalPositions, out personXz, out dollXz, out personYaw, out dollYaw, out walkDistance, out string selectionFailure))
                        {
                            skipped.Add(id + " " + pose.Name + ": " + selectionFailure);
                            Debug.LogWarning("[PlateValidation] Optional walking pose unavailable for " + id + ": " + selectionFailure);
                            continue;
                        }
                        person.SetActive(true);
                        person.transform.SetPositionAndRotation(PlateValidationWorld(personXz, layout.room.floorY), Quaternion.Euler(0, personYaw, 0));
                        avatar.Apply(walkDistance, walkDistance > 0f ? 1f : 0f);
                        if (pose.Occlusion)
                        {
                            Vector2[] posedHull = avatar.Footprint();
                            Vector2 rootXz = new(person.transform.position.x, person.transform.position.z);
                            for (int v = 0; v < posedHull.Length; v++) posedHull[v] -= rootXz;
                            personXz = PlateValidationFindHullStand(personXz, posedHull, visitorNav, dollXz, 0.18f);
                            person.transform.position = PlateValidationWorld(personXz, layout.room.floorY);
                            avatar.Apply(walkDistance, walkDistance > 0f ? 1f : 0f);
                        }
                        stage.PlaceActorAtWorld(doll, PlateValidationWorld(dollXz, layout.room.floorY), dollYaw, geometry);
                        GameObject dollObject = PlateValidationField<GameObject>(stage, "_actor");
                        foreach (MonoBehaviour behaviour in dollObject.GetComponentsInChildren<MonoBehaviour>(true)) behaviour.enabled = false;
                        // 足底は現在の骨から読む。bounds や root の高さを接地の証拠にしない。
                        Bounds dollBounds = PlateValidationBounds(PlateValidationVertices(dollObject));
                        float rawSole = dollBounds.min.y - layout.room.floorY;
                        Vector3 grounded = dollObject.transform.position + Vector3.up * (layout.room.floorY - dollBounds.min.y);
                        stage.PlaceActorAtWorld(doll, grounded, dollYaw, geometry);

                        var frame = new PlateValidationFrame { camera = id, pose = pose.Name, plate = platePath, plateSha256 = sha,
                            geometry = geometry.label, calibrated = geometry.fromCalib, sourceWidth = plate.width, sourceHeight = plate.height,
                            requestedVisitor = pose.Visitor, requestedDoll = pose.Doll, occluders = proxy.OccluderCount,
                            purpose = pose.Occlusion ? "intentional wall occlusion" : pose.Ending ? "production ending placement from A" : "both actors fully visible",
                            visibilityRequired = !pose.Occlusion && (!pose.Ending || id == "A"),
                            walkDistanceM = walkDistance, animationTimeSec = avatar.AnimationTime };
                        frame.visitor = PlateValidationMeasure(stage.CgCamera, person, visitorDef.heightM, layout.room.floorY, layout, personYaw);
                        Vector2[] footprint = avatar.Footprint();
                        for (int v = 0; v < footprint.Length; v++) footprint[v].x -= StageOriginX;
                        frame.visitor.minWallClearanceM = visitorNav.SweptFootprintClearance(footprint, footprint);
                        frame.doll = PlateValidationMeasure(stage.CgCamera, dollObject, doll.heightM, layout.room.floorY, layout, dollYaw);
                        frame.doll.rawSoleMeasured = true;
                        frame.doll.rawSoleErrorM = rawSole;
                        frame.doll.groundingCorrectionM = -rawSole;
                        Bounds finalDollBounds = PlateValidationBounds(PlateValidationVertices(dollObject));
                        Vector2[] dollHull = PlateValidationRectHull(finalDollBounds);
                        frame.doll.minWallClearanceM = dollNav.SweptFootprintClearance(dollHull, dollHull);
                        var dollPoints = new List<Vector2>();
                        foreach (Vector3 vertex in PlateValidationVertices(dollObject)) dollPoints.Add(new Vector2(vertex.x - StageOriginX, vertex.z));
                        frame.actorsClearanceM = PlateValidationActorsClearance(footprint, PlateValidationHull(dollPoints));
                        var roomBoxes = ShowRoomProxyLogic.Build(layout.room);
                        frame.expectedVisitorVisibleFraction = PlateValidationExpectedVisible(stage.CgCamera, PlateValidationVertices(person), Vector3.zero, roomBoxes);
                        frame.expectedDollVisibleFraction = PlateValidationExpectedVisible(stage.CgCamera, PlateValidationVertices(dollObject), Vector3.zero, roomBoxes);
                        frame.personRight = frame.visitor.viewportFoot.x > frame.doll.viewportFoot.x;
                        frame.facing = Vector3.Dot(person.transform.forward, (dollObject.transform.position - person.transform.position).normalized) > 0.95f
                            && Vector3.Dot(dollObject.transform.forward, (person.transform.position - dollObject.transform.position).normalized) > 0.95f;
                        frame.photoReady = pose.Ending && EndingFarewellLogic.IsPhotoReady(ending, person.transform.position, personYaw, dollYaw);
                        string key = id + "_" + pose.Name;
                        Color32[] personMask = PlateValidationMask(stage, person, dollObject, proxy, true, frame.visitor, outDir, key + "_visitor_mask.png");
                        Color32[] dollMask = PlateValidationMask(stage, dollObject, person, proxy, false, frame.doll, outDir, key + "_doll_mask.png");
                        PostParams post = camera.hasPost && camera.post != null ? camera.post : show.post ?? new PostParams();
                        string caption = "cam " + id + " | " + plate.label + " | " + pose.Name + " | " + geometry.label + "\n"
                            + PlateValidationCaption("visitor", frame.visitor) + "\n" + PlateValidationCaption("doll", frame.doll);
                        stage.Composite(plate, true, post, caption);
                        frame.blend = stage.MeasureBlend();
                        using (var image = new PlateValidationTexture(stage.ReadEndingTexture()))
                        {
                            PlateValidationColor(image.Texture, personMask, plate, stage.ContainFor(plate), frame.visitor);
                            PlateValidationColor(image.Texture, dollMask, plate, stage.ContainFor(plate), frame.doll);
                            File.WriteAllBytes(Path.Combine(outDir, key + "_image.png"), image.Texture.EncodeToPNG());
                        }
                        stage.Composite(plate, true, post, caption + "\n" + frame.blend);
                        frame.png = Path.GetFileName(stage.Save(key + ".png"));
                        frame.constraintsPassed = frame.visitor.minWallClearanceM >= 0.0399f && frame.doll.minWallClearanceM >= 0.0399f
                            && frame.actorsClearanceM >= 0.0399f
                            && (!frame.visibilityRequired || (frame.visitor.inFrameVertexFraction >= 0.999f && frame.doll.inFrameVertexFraction >= 0.999f
                                && frame.visitor.feetInFrame && frame.doll.feetInFrame && frame.visitor.pixelsVisible >= 128
                                && frame.doll.pixelsVisible >= 16 && frame.visitor.visibleFraction >= 0.95f && frame.doll.visibleFraction >= 0.95f
                                && PlateValidationProjectionFits(stage.CgCamera, PlateValidationVertices(person), pose.Ending ? 0.005f : 0.025f)
                                && PlateValidationProjectionFits(stage.CgCamera, PlateValidationVertices(dollObject), pose.Ending ? 0.005f : 0.025f)));
                        frames.Add(frame);
                        Debug.Log("[PlateValidation] " + key + " " + PlateValidationCaption("visitor", frame.visitor) + " | " + PlateValidationCaption("doll", frame.doll)
                            + " | doll CPU radius=" + frame.doll.maxBodyRadiusM.ToString("F8", CultureInfo.InvariantCulture) + "m");
                        if (!frame.constraintsPassed)
                        {
                            if (pose.OptionalWalking)
                            {
                                frames.RemoveAt(frames.Count - 1);
                                skipped.Add(id + " " + pose.Name + ": rendered constraints failed; rejected evidence=" + frame.png);
                                continue;
                            }
                            throw new InvalidOperationException("[PlateValidation] Placement constraints failed for " + key + "; inspect saved image and meta.json.");
                        }
                        if (!pose.Ending && !pose.Occlusion && !pose.OptionalWalking) normalPositions.Add(personXz);
                        if (pose.Ending && id == "A")
                            report.endingAnimationFrames = PlateValidationEndingGate(stage, plate, post, doll, geometry, ending,
                                person, personYaw, dollYaw, layout, outDir);
                    }
                }
                report.complete = true;
            }
            finally
            {
                RenderSettings.ambientMode = ambientMode;
                RenderSettings.ambientLight = ambientColor;
                for (int i = 0; i < lights.Length; i++) if (lights[i] != null) lights[i].enabled = lightEnabled[i];
                foreach (UnityEngine.Object item in trash) if (item != null) UnityEngine.Object.DestroyImmediate(item);
                report.frames = frames.ToArray();
                report.skippedCases = skipped.ToArray();
                File.WriteAllText(Path.Combine(outDir, "meta.json"), JsonUtility.ToJson(report, true), new UTF8Encoding(false));
                PlateValidationWriteIndex(report);
            }
            Debug.Log("[PlateValidation] output=" + outDir + " frames=" + frames.Count + " complete=" + report.complete);
        }

        private sealed class PlateValidationTexture : IDisposable
        {
            public readonly Texture2D Texture;
            public PlateValidationTexture(Texture2D texture) => Texture = texture;
            public void Dispose() => UnityEngine.Object.DestroyImmediate(Texture);
        }

        private static int PlateValidationEndingGate(Stage stage, Plate plate, PostParams post, ShowActorDef doll,
            Geometry geometry, EndingFarewellLogic.Anchor anchor, GameObject person, float personYaw, float dollYaw,
            ShowLayoutDef layout, string outDir)
        {
            var logic = new EndingFarewellLogic();
            var trace = new StringBuilder("timeSec\toutcome\tphase\talpha\tyawDeg\tnominalRetreatM\tphotoReady\tshotCaptured\trawSoleErrorM\tgroundingCorrectionM\tactualX\tactualZ\tvisitorDistanceM\tcameraDistanceM\tforwardX\tforwardZ\tdotVisitor\tdotCamera\twallClearanceM\tprojectedHeightPx\tanimationFrame\tretreatGeometryPassed\tstepDistanceM\tspeedMps\tcumulativeTravelM\tpeakSpeedMps\n");
            string animationDir = Path.Combine(outDir, "ending-animation");
            Directory.CreateDirectory(animationDir);
            var animationTrace = new StringBuilder("frame\tsourceFrame\ttimeSec\tphase\n");
            int animationFrame = 0;
            float initialCameraDistance = 0, previousVisitorDistance = 0, lastCameraDistance = 0;
            Vector3 previousFoot = default;
            float cumulativeTravel = 0, peakSpeed = 0;
            var wallProbe = new WalkNavigation(layout, 0.01f, 0.04f, 0.5f, doll.heightM);
            var destination = new RenderTexture(EndingFrameCapture.Width, EndingFrameCapture.Height, 0, RenderTextureFormat.ARGB32);
            destination.Create();
            if (EndingFrameCapture.CaptureComposite(null, destination))
                throw new InvalidOperationException("[PlateValidation] Capture accepted a missing composite material.");
            bool captured = false;
            EndingFarewellLogic.Phase previous = EndingFarewellLogic.Phase.Idle;
            var decision = new EndingDecisionLogic();
            decision.NotifyPromptReadable(0f);
            PlateValidationRelease(outDir);
            stage.HideActor();
            person.transform.SetPositionAndRotation(anchor.visitorTargetFoot + Vector3.forward * 0.4f, Quaternion.Euler(0, personYaw + 90f, 0));
            Plate wrongPerson = stage.BakeActorIntoPlate(plate, "+visitor-wrong-pose");
            person.transform.SetPositionAndRotation(anchor.visitorTargetFoot, Quaternion.Euler(0, personYaw, 0));
            Plate alignedPerson = stage.BakeActorIntoPlate(plate, "+visitor-ready");
            person.SetActive(false);
            try
            {
                for (int frame = 0; frame < 360; frame++)
                {
                    float time = frame / 30f;
                    if (time >= 1f) decision.NotifyReleased();
                    decision.Tick(time, true, 4, 0);
                    bool released = decision.Outcome == ShowEndingOutcome.Released;
                    // 最初は位置と向きの両方を外す。Face が撮影完了前に進まないことも見る。
                    bool aligned = time >= 2.5f;
                    Vector3 visitorFoot = anchor.visitorTargetFoot + (aligned ? Vector3.zero : Vector3.forward * 0.4f);
                    float yaw = aligned ? personYaw : personYaw + 90f;
                    person.transform.SetPositionAndRotation(visitorFoot, Quaternion.Euler(0, yaw, 0));
                    bool ready = EndingFarewellLogic.IsPhotoReady(anchor, visitorFoot, yaw, dollYaw);
                    EndingFarewellLogic.Step step = logic.Tick(1f / 30f, true, released, true, true, true, true);
                    EndingFarewellLogic.Sample sample = step.sample;
                    EndingFarewellLogic.PoseAt(anchor, sample, dollYaw, out Vector3 actorFoot, out float actorYaw);
                    stage.PlaceActorAtWorld(doll, actorFoot, actorYaw, geometry);
                    GameObject actor = PlateValidationField<GameObject>(stage, "_actor");
                    Bounds bounds = PlateValidationBounds(PlateValidationVertices(actor));
                    float rawSole = bounds.min.y - anchor.foot.y;
                    stage.PlaceActorAtWorld(doll, actor.transform.position + Vector3.up * (anchor.foot.y - bounds.min.y), actorYaw, geometry);
                    List<Vector3> currentVertices = PlateValidationVertices(actor);
                    var bodyPoints = new List<Vector2>();
                    float minV = float.PositiveInfinity, maxV = float.NegativeInfinity;
                    foreach (Vector3 vertex in currentVertices)
                    {
                        bodyPoints.Add(new Vector2(vertex.x - StageOriginX, vertex.z));
                        Vector3 uv = stage.CgCamera.WorldToViewportPoint(vertex);
                        if (uv.z > stage.CgCamera.nearClipPlane) { minV = Mathf.Min(minV, uv.y); maxV = Mathf.Max(maxV, uv.y); }
                    }
                    Vector2[] bodyHull = PlateValidationHull(bodyPoints);
                    float wallClearance = wallProbe.SweptFootprintClearance(bodyHull, bodyHull);
                    Vector3 actualFoot = actor.transform.position;
                    float stepDistance = frame > 0 ? Vector3.Distance(actualFoot, previousFoot) : 0;
                    float speed = stepDistance * 30f;
                    cumulativeTravel += stepDistance; peakSpeed = Mathf.Max(peakSpeed, speed); previousFoot = actualFoot;
                    Vector3 forward = actor.transform.forward;
                    float visitorDistance = Vector3.Distance(actualFoot, anchor.visitorTargetFoot);
                    float cameraDistance = Vector3.Distance(actualFoot, stage.CgCamera.transform.position);
                    float dotVisitor = Vector3.Dot(forward, (anchor.visitorTargetFoot - actualFoot).normalized);
                    float dotCamera = Vector3.Dot(forward, (stage.CgCamera.transform.position - actualFoot).normalized);
                    float projectedHeight = float.IsInfinity(minV) ? 0 : (maxV - minV) * stage.CgCamera.targetTexture.height;
                    stage.CgStrength = sample.alpha;
                    stage.Composite(aligned ? alignedPerson : wrongPerson, true, post, string.Format(CultureInfo.InvariantCulture,
                        "production ending gate | t={0:F2}s | {1} | ready={2} captured={3}", time, logic.Current, ready, captured));
                    if (logic.Current == EndingFarewellLogic.Phase.Face && ready && !captured && time >= 2.7f)
                    {
                        Material material = PlateValidationField<Material>(stage, "_compositeMat");
                        if (!EndingFrameCapture.CaptureComposite(material, destination))
                            throw new InvalidOperationException("[PlateValidation] Production ending capture failed.");
                        PlateValidationSaveRt(destination, Path.Combine(outDir, "ending_captured.png"));
                        logic.NotifyShotCaptured();
                        captured = true;
                        initialCameraDistance = cameraDistance;
                        previousVisitorDistance = visitorDistance;
                    }
                    bool retreating = logic.Current == EndingFarewellLogic.Phase.Retreat || logic.Current == EndingFarewellLogic.Phase.Done;
                    bool geometryPassed = !captured || (wallClearance >= 0.0399f
                        && speed <= 0.3505f && (!retreating || (visitorDistance >= previousVisitorDistance - 0.0001f && dotVisitor < -0.05f && dotCamera < -0.05f)));
                    trace.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "{0:F3}\t{1}\t{2}\t{3:F4}\t{4:F3}\t{5:F4}\t{6}\t{7}\t{8:F8}\t{9:F8}\t{10:F5}\t{11:F5}\t{12:F5}\t{13:F5}\t{14:F5}\t{15:F5}\t{16:F5}\t{17:F5}\t{18:F5}\t{19:F3}\t{20}\t{21}\t{22:F6}\t{23:F5}\t{24:F5}\t{25:F5}",
                        time, decision.Outcome, logic.Current, sample.alpha, actorYaw, sample.retreatM, ready, captured, rawSole, -rawSole,
                        actualFoot.x - StageOriginX, actualFoot.z, visitorDistance, cameraDistance, forward.x, forward.z,
                        dotVisitor, dotCamera, wallClearance, projectedHeight, captured ? animationFrame : -1, geometryPassed,
                        stepDistance, speed, cumulativeTravel, peakSpeed));
                    if (captured)
                    {
                        using var image = new PlateValidationTexture(stage.ReadEndingTexture());
                        File.WriteAllBytes(Path.Combine(animationDir, "f" + animationFrame.ToString("D5") + ".jpg"), image.Texture.EncodeToJPG(95));
                        animationTrace.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0}\t{1}\t{2:F3}\t{3}", animationFrame, frame, time, logic.Current));
                        animationFrame++;
                        previousVisitorDistance = visitorDistance;
                        lastCameraDistance = cameraDistance;
                    }
                    if (previous != logic.Current || frame % 15 == 0)
                    {
                        stage.Save("ending_gate_" + frame.ToString("D3") + "_" + logic.Current + ".png");
                        previous = logic.Current;
                    }
                    if (!geometryPassed) throw new InvalidOperationException("[PlateValidation] Retreat body geometry failed: inspect ending-gates.tsv and ending-animation.");
                    if (logic.Current == EndingFarewellLogic.Phase.Done) break;
                    if (!released && logic.Current != EndingFarewellLogic.Phase.Idle)
                        throw new InvalidOperationException("[PlateValidation] Farewell began before release.");
                    if (!captured && logic.Current > EndingFarewellLogic.Phase.Face)
                        throw new InvalidOperationException("[PlateValidation] Ending advanced before the photo was captured.");
                }
                if (!captured || logic.Current != EndingFarewellLogic.Phase.Done)
                    throw new InvalidOperationException("[PlateValidation] Ending gate did not reach captured/Done.");
                if (lastCameraDistance <= initialCameraDistance + 0.01f)
                    throw new InvalidOperationException("[PlateValidation] Retreat endpoint did not move away from the camera.");
                PlateValidationOutro(stage, alignedPerson, post, destination, outDir);
            }
            finally
            {
                File.WriteAllText(Path.Combine(outDir, "ending-gates.tsv"), trace.ToString(), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(animationDir, "frames.tsv"), animationTrace.ToString(), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(animationDir, "meta.json"), "{\"fps\":30,\"frames\":" + animationFrame.ToString(CultureInfo.InvariantCulture)
                    + ",\"source\":\"actual ScreenComposite after shot capture; no caption\"}", new UTF8Encoding(false));
                destination.Release(); UnityEngine.Object.DestroyImmediate(destination);
                stage.CgStrength = 1f;
                person.SetActive(true);
            }
            return animationFrame;
        }

        private static void PlateValidationSaveRt(RenderTexture source, string path)
        {
            RenderTexture previous = RenderTexture.active;
            using var image = new PlateValidationTexture(new Texture2D(source.width, source.height, TextureFormat.RGB24, false));
            try
            {
                RenderTexture.active = source;
                image.Texture.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                image.Texture.Apply();
                File.WriteAllBytes(path, image.Texture.EncodeToPNG());
            }
            finally { RenderTexture.active = previous; }
        }

        // Stage の public API に無い診断対象だけを読む。名前・型が変われば必ず失敗させる。
        private static T PlateValidationField<T>(Stage stage, string name) where T : class
        {
            FieldInfo field = typeof(Stage).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(typeof(Stage).FullName, name);
            if (!typeof(T).IsAssignableFrom(field.FieldType) || !(field.GetValue(stage) is T result))
                throw new InvalidOperationException("[PlateValidation] Stage diagnostic contract changed: " + name);
            return result;
        }

        private static string PlateValidationLatestPlate(string id)
        {
            string folder = Path.Combine(Application.streamingAssetsPath, "show/assets");
            string[] files = Directory.GetFiles(folder, "plate_" + id + "_*.jpg");
            if (files.Length == 0) throw new FileNotFoundException("[PlateValidation] No saved real plate for " + id, folder);
            Array.Sort(files, StringComparer.Ordinal);
            return files[files.Length - 1];
        }

        private static void PlateValidationVerifySource(Stage stage, Plate plate, string id, string outDir)
        {
            Material material = PlateValidationField<Material>(stage, "_compositeMat");
            float power = material.GetFloat("_ScreenPower"), collapse = material.GetFloat("_ScreenCollapse"), noise = material.GetFloat("_ScreenNoise");
            using var image = new PlateValidationTexture(stage.ReadEndingTexture());
            File.WriteAllBytes(Path.Combine(outDir, "platecheck_" + id + ".png"), image.Texture.EncodeToPNG());
            Color32[] source = plate.texture.GetPixels32(), captured = image.Texture.GetPixels32();
            Vector2 contain = stage.ContainFor(plate);
            double s = 0, c = 0, ss = 0, cc = 0, sc = 0;
            int count = 0;
            for (int y = 8; y < plate.height - 8; y += 8)
                for (int x = 8; x < plate.width - 8; x += 8)
                {
                    float u = ((x + 0.5f) / plate.width - 0.5f) * contain.x + 0.5f;
                    float v = ((y + 0.5f) / plate.height - 0.5f) * contain.y + 0.5f;
                    int ox = Mathf.Clamp((int)(u * image.Texture.width), 0, image.Texture.width - 1);
                    int oy = Mathf.Clamp((int)(v * image.Texture.height), 0, image.Texture.height - 1);
                    float a = PlateValidationLuma(source[y * plate.width + x]);
                    float b = PlateValidationLuma(captured[oy * image.Texture.width + ox]);
                    s += a; c += b; ss += a * a; cc += b * b; sc += a * b; count++;
                }
            double varianceS = ss - s * s / count, varianceC = cc - c * c / count;
            double correlation = varianceS > 1 && varianceC > 1 ? (sc - s * c / count) / Math.Sqrt(varianceS * varianceC) : 0;
            double sourceMean = s / count, capturedMean = c / count;
            bool visible = power > 0.99f && Mathf.Abs(collapse) < 0.0001f && Mathf.Abs(noise) < 0.0001f
                && capturedMean > Math.Max(1, sourceMean * 0.15) && (varianceS < 1 || correlation > 0.8);
            string evidence = Path.Combine(outDir, "plate-evidence.tsv");
            if (!File.Exists(evidence)) File.WriteAllText(evidence, "camera\tpower\tcollapse\tnoise\tsourceMean\timageMean\tcorrelation\tverified\n", new UTF8Encoding(false));
            File.AppendAllText(evidence, string.Format(CultureInfo.InvariantCulture, "{0}\t{1:F5}\t{2:F5}\t{3:F5}\t{4:F3}\t{5:F3}\t{6:F5}\t{7}\n",
                id, power, collapse, noise, sourceMean, capturedMean, correlation, visible), new UTF8Encoding(false));
            Debug.Log(string.Format(CultureInfo.InvariantCulture, "[PlateValidation] source {0}: power={1:F3} mean={2:F3}/{3:F3} correlation={4:F4} verified={5}",
                id, power, capturedMean, sourceMean, correlation, visible));
            if (!visible) throw new InvalidOperationException("[PlateValidation] Real plate did not render correctly for " + id + "; inspect plate-evidence.tsv and platecheck image.");
        }

        private static bool PlateValidationCorner(ShowRoomDef room, out Vector2 corner, out Vector2 endA, out Vector2 endB)
        {
            corner = endA = endB = default;
            for (int i = 0; i < room.walls.Length; i++)
                for (int j = i + 1; j < room.walls.Length; j++)
                {
                    ShowRoomWallDef a = room.walls[i], b = room.walls[j];
                    if (!a.IsUsable() || !b.IsUsable()) continue;
                    Vector2[] aa = { new(a.x1, a.z1), new(a.x2, a.z2) };
                    Vector2[] bb = { new(b.x1, b.z1), new(b.x2, b.z2) };
                    for (int ai = 0; ai < 2; ai++) for (int bi = 0; bi < 2; bi++)
                        if ((aa[ai] - bb[bi]).sqrMagnitude < 0.0001f)
                        { corner = aa[ai]; endA = aa[1 - ai]; endB = bb[1 - bi]; return true; }
                }
            return false;
        }

        private static List<PlateValidationPose> PlateValidationPoses(ShowLayoutDef layout)
        {
            float w = layout.room!.floorW * 0.5f, d = layout.room.floorD * 0.5f;
            var result = new List<PlateValidationPose>
            {
                new("center_idle", new Vector2(0.22f, -0.15f), new Vector2(-0.18f, -0.15f)),
                new("left_idle", new Vector2(-w + 0.25f, -d + 0.3f), new Vector2(-0.1f, -0.25f)),
                new("right_idle", new Vector2(w - 0.25f, d - 0.3f), new Vector2(0.1f, 0.1f))
            };
            foreach (ShowRoomWallDef wall in layout.room.walls)
            {
                Vector2 a = new(wall.x1, wall.z1), b = new(wall.x2, wall.z2);
                Vector2 along = (b - a).normalized, normal = new(-along.y, along.x);
                foreach (int side in new[] { -1, 1 })
                {
                    Vector2 beyond = b + along * 0.33f + normal * (side * 0.32f);
                    result.Add(new PlateValidationPose("wall_" + ShowCompositePreviewPlan.SanitizeToken(wall.id)
                        + (side < 0 ? "_front" : "_back"), beyond, b + along * 0.2f - normal * (side * 0.18f), 0.5f, occlusion: true));
                }
            }
            result.Add(new PlateValidationPose("ending_face", Vector2.zero, Vector2.zero, ending: true));
            result.Add(new PlateValidationPose("walk_extra", Vector2.zero, Vector2.zero, 0.35f, optionalWalking: true));
            return result;
        }

        private sealed class PlateValidationCandidate
        {
            public Vector2 Position, Uv;
            public Rect ImageBounds;
        }

        private sealed class PlateValidationNoPlacement : InvalidOperationException
        {
            public PlateValidationNoPlacement(string message) : base(message) { }
        }

        private static bool PlateValidationSelectPair(Stage stage, Geometry geometry, ShowLayoutDef layout,
            GameObject person, VisitorAvatar avatar, ShowActorDef visitor, ShowActorDef doll,
            WalkNavigation visitorNav, WalkNavigation dollNav, PlateValidationPose pose, List<Vector2> usedPositions,
            out Vector2 personXz, out Vector2 dollXz, out float personYaw, out float dollYaw, out float walk, out string failure)
        {
            personXz = dollXz = default; personYaw = dollYaw = 0f; walk = pose.Walk;
            var failures = new StringBuilder();
            float[] phases = pose.OptionalWalking ? new[] { 0.35f, 0.1f, 0.65f, 1f } : new[] { pose.Walk };
            foreach (float phase in phases)
                foreach (float yawOffset in new[] { 0f, 45f, -45f, 90f, -90f, 180f })
                {
                    try
                    {
                        PlateValidationSelectPairAtYaw(stage, geometry, layout, person, avatar, visitor, doll, visitorNav, dollNav,
                            pose, pose.OptionalWalking ? new List<Vector2>() : usedPositions, phase, yawOffset,
                            out personXz, out dollXz, out personYaw, out dollYaw);
                        walk = phase; failure = ""; return true;
                    }
                    catch (PlateValidationNoPlacement e)
                    {
                        failures.AppendFormat(CultureInfo.InvariantCulture, "distance={0:F2}m yawOffset={1:F0}: {2}; ", phase, yawOffset, e.Message);
                    }
                }
            failure = failures.ToString();
            if (!pose.OptionalWalking) throw new InvalidOperationException("[PlateValidation] Required standing pair unavailable for " + pose.Name + ": " + failure);
            return false;
        }

        private static void PlateValidationSelectPairAtYaw(Stage stage, Geometry geometry, ShowLayoutDef layout,
            GameObject person, VisitorAvatar avatar, ShowActorDef visitor, ShowActorDef doll,
            WalkNavigation visitorNav, WalkNavigation dollNav, PlateValidationPose pose, List<Vector2> usedPositions,
            float walk, float yawOffset, out Vector2 personXz, out Vector2 dollXz, out float personYaw, out float dollYaw)
        {
            dollYaw = stage.CgCamera.transform.eulerAngles.y + 180f;
            personYaw = dollYaw + yawOffset;
            Vector3 origin = PlateValidationWorld(Vector2.zero, layout.room!.floorY);
            person.SetActive(true);
            person.transform.SetPositionAndRotation(origin, Quaternion.Euler(0, personYaw, 0));
            avatar.Apply(walk, walk > 0 ? 1f : 0f);
            List<Vector3> personVertices = PlateValidationVertices(person);
            Vector2[] personHull = avatar.Footprint();
            for (int i = 0; i < personHull.Length; i++) personHull[i].x -= StageOriginX;
            avatar.Apply(0f, 0f);
            List<Vector3> standingVertices = PlateValidationVertices(person);
            stage.PlaceActorAtWorld(doll, origin, dollYaw, geometry);
            GameObject actor = PlateValidationField<GameObject>(stage, "_actor");
            Bounds dollBounds = PlateValidationBounds(PlateValidationVertices(actor));
            stage.PlaceActorAtWorld(doll, actor.transform.position + Vector3.up * (layout.room.floorY - dollBounds.min.y), dollYaw, geometry);
            List<Vector3> dollVertices = PlateValidationVertices(actor);
            Vector2[] dollHull = PlateValidationRectHull(PlateValidationBounds(dollVertices));
            List<ShowRoomProxyLogic.Box> boxes = ShowRoomProxyLogic.Build(layout.room);
            List<PlateValidationCandidate> people = PlateValidationCandidates(stage.CgCamera, personVertices, personHull, origin,
                visitor.heightM, visitorNav, boxes, standingVertices);
            List<PlateValidationCandidate> dolls = PlateValidationCandidates(stage.CgCamera, dollVertices, dollHull, origin,
                doll.heightM, dollNav, boxes);
            if (people.Count == 0 || dolls.Count == 0)
                throw new PlateValidationNoPlacement("No fully visible collision-free candidate: "
                    + pose.Name + " visitor=" + people.Count + " doll=" + dolls.Count);
            people.Sort((a, b) => a.Uv.x.CompareTo(b.Uv.x));
            float quantile = pose.Name.StartsWith("left", StringComparison.Ordinal) ? 0.15f
                : pose.Name.StartsWith("right", StringComparison.Ordinal) ? 0.85f : 0.5f;
            float targetU = people[Mathf.RoundToInt((people.Count - 1) * quantile)].Uv.x;
            float best = float.PositiveInfinity;
            personXz = dollXz = default;
            foreach (PlateValidationCandidate candidate in people)
            {
                bool used = false;
                foreach (Vector2 position in usedPositions) if (Vector2.Distance(position, candidate.Position) < 0.15f) used = true;
                if (used) continue;
                if (usedPositions.Count > 0)
                {
                    float centerU = stage.CgCamera.WorldToViewportPoint(PlateValidationWorld(usedPositions[0], layout.room.floorY)).x;
                    if (pose.Name.StartsWith("left", StringComparison.Ordinal) && candidate.Uv.x >= centerU - 0.02f) continue;
                    if (pose.Name.StartsWith("right", StringComparison.Ordinal) && candidate.Uv.x <= centerU + 0.02f) continue;
                }
                foreach (PlateValidationCandidate other in dolls)
                {
                    float gap = Vector2.Distance(candidate.Position, other.Position);
                    if (gap < avatar.IdleBodyRadiusM + 0.22f) continue;
                    // 低い人形を人物が隠さない配置を選ぶ。合成後のマスクでも可視を検査する。
                    if (!(candidate.ImageBounds.xMax + 0.025f < other.ImageBounds.xMin
                        || other.ImageBounds.xMax + 0.025f < candidate.ImageBounds.xMin)) continue;
                    float dollTarget = Mathf.Clamp01(candidate.Uv.x + (quantile < 0.5f ? 0.25f : -0.25f));
                    float score = Mathf.Abs(candidate.Uv.x - targetU) * 5f
                        + Mathf.Abs(other.Uv.x - dollTarget) * 2f + gap * 0.02f
                        + Mathf.Abs(candidate.ImageBounds.center.y - 0.5f) * 0.1f;
                    if (score >= best) continue;
                    best = score; personXz = candidate.Position; dollXz = other.Position;
                }
            }
            if (float.IsInfinity(best))
                throw new PlateValidationNoPlacement("No separated visible pair for " + pose.Name);
        }

        private static List<PlateValidationCandidate> PlateValidationCandidates(Camera camera, List<Vector3> vertices,
            Vector2[] hull, Vector3 origin, float height, WalkNavigation navigation, List<ShowRoomProxyLogic.Box> boxes,
            List<Vector3>? standingVertices = null)
        {
            var result = new List<PlateValidationCandidate>();
            Matrix4x4 projection = camera.projectionMatrix * camera.worldToCameraMatrix;
            var projected = new Vector4[vertices.Count + (standingVertices?.Count ?? 0)];
            for (int i = 0; i < vertices.Count; i++) projected[i] = projection * new Vector4(vertices[i].x, vertices[i].y, vertices[i].z, 1);
            if (standingVertices != null)
                for (int i = 0; i < standingVertices.Count; i++)
                {
                    Vector3 point = standingVertices[i];
                    projected[vertices.Count + i] = projection * new Vector4(point.x, point.y, point.z, 1);
                }
            var moved = new Vector2[hull.Length];
            for (float z = navigation.Bounds.yMin; z <= navigation.Bounds.yMax; z += 0.075f)
                for (float x = navigation.Bounds.xMin; x <= navigation.Bounds.xMax; x += 0.075f)
                {
                    Vector3 offset = new(x, 0, z);
                    Vector3 foot = origin + offset;
                    Vector4 delta = projection * new Vector4(x, 0, z, 0);
                    if (!PlateValidationClipFits(projected, delta, camera.nearClipPlane, out Rect imageBounds)) continue;
                    for (int i = 0; i < moved.Length; i++) moved[i] = hull[i] + new Vector2(x, z);
                    if (navigation.SweptFootprintClearance(moved, moved) < 0.04f) continue;
                    if (PlateValidationExpectedVisible(camera, vertices, offset, boxes) < 0.985f) continue;
                    if (imageBounds.height * camera.targetTexture.height < 12f) continue;
                    Vector3 uv = camera.WorldToViewportPoint(foot);
                    result.Add(new PlateValidationCandidate { Position = new Vector2(x, z), Uv = new Vector2(uv.x, uv.y), ImageBounds = imageBounds });
                }
            return result;
        }

        private static bool PlateValidationProjectionFits(Camera camera, List<Vector3> vertices, float margin = 0.025f)
        {
            foreach (Vector3 point in vertices)
            {
                Vector3 uv = camera.WorldToViewportPoint(point);
                if (uv.z <= camera.nearClipPlane || uv.x < margin || uv.x > 1f - margin || uv.y < margin || uv.y > 1f - margin) return false;
            }
            return true;
        }

        private static bool PlateValidationClipFits(Vector4[] projected, Vector4 delta, float near, out Rect imageBounds)
        {
            imageBounds = default;
            Vector2 lo = Vector2.one, hi = Vector2.zero;
            foreach (Vector4 original in projected)
            {
                Vector4 clip = original + delta;
                if (clip.w <= near) return false;
                float u = clip.x / clip.w * 0.5f + 0.5f, v = clip.y / clip.w * 0.5f + 0.5f;
                if (u < 0.025f || u > 0.975f || v < 0.025f || v > 0.975f) return false;
                lo = Vector2.Min(lo, new Vector2(u, v)); hi = Vector2.Max(hi, new Vector2(u, v));
            }
            imageBounds = Rect.MinMaxRect(lo.x, lo.y, hi.x, hi.y);
            return true;
        }

        private static Vector2[] PlateValidationRectHull(Bounds bounds)
            => new[] { new Vector2(bounds.min.x - StageOriginX, bounds.min.z), new Vector2(bounds.max.x - StageOriginX, bounds.min.z),
                new Vector2(bounds.max.x - StageOriginX, bounds.max.z), new Vector2(bounds.min.x - StageOriginX, bounds.max.z) };

        private static Vector2[] PlateValidationHull(List<Vector2> points)
        {
            points.Sort((a, b) => a.x == b.x ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));
            var hull = new List<Vector2>();
            foreach (Vector2 point in points)
            {
                while (hull.Count > 1 && PlateValidationCross(hull[hull.Count - 2], hull[hull.Count - 1], point) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(point);
            }
            int lower = hull.Count;
            for (int i = points.Count - 2; i >= 0; i--)
            {
                Vector2 point = points[i];
                while (hull.Count > lower && PlateValidationCross(hull[hull.Count - 2], hull[hull.Count - 1], point) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(point);
            }
            if (hull.Count > 1) hull.RemoveAt(hull.Count - 1);
            if (hull.Count < 3) throw new InvalidOperationException("[PlateValidation] Actor has no planar body hull.");
            return hull.ToArray();
        }

        private static float PlateValidationCross(Vector2 a, Vector2 b, Vector2 c)
            => (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);

        private static bool PlateValidationInside(Vector2 point, Vector2[] hull)
        {
            for (int i = 0; i < hull.Length; i++)
                if (PlateValidationCross(hull[i], hull[(i + 1) % hull.Length], point) < -0.000001f) return false;
            return true;
        }

        private static float PlateValidationPointSegment(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 delta = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(point - a, delta) / Mathf.Max(0.00000001f, delta.sqrMagnitude));
            return Vector2.Distance(point, a + delta * t);
        }

        private static float PlateValidationActorsClearance(Vector2[] person, Vector2[] doll)
        {
            foreach (Vector2 point in person) if (PlateValidationInside(point, doll)) return 0;
            foreach (Vector2 point in doll) if (PlateValidationInside(point, person)) return 0;
            float distance = float.PositiveInfinity;
            for (int i = 0; i < person.Length; i++)
                for (int j = 0; j < doll.Length; j++)
                {
                    Vector2 a = person[i], b = person[(i + 1) % person.Length];
                    Vector2 c = doll[j], d = doll[(j + 1) % doll.Length];
                    bool boundsOverlap = Mathf.Max(a.x, b.x) >= Mathf.Min(c.x, d.x) && Mathf.Max(c.x, d.x) >= Mathf.Min(a.x, b.x)
                        && Mathf.Max(a.y, b.y) >= Mathf.Min(c.y, d.y) && Mathf.Max(c.y, d.y) >= Mathf.Min(a.y, b.y);
                    if (boundsOverlap && PlateValidationCross(a, b, c) * PlateValidationCross(a, b, d) <= 0
                        && PlateValidationCross(c, d, a) * PlateValidationCross(c, d, b) <= 0) return 0;
                    distance = Mathf.Min(distance, PlateValidationPointSegment(a, c, d), PlateValidationPointSegment(b, c, d),
                        PlateValidationPointSegment(c, a, b), PlateValidationPointSegment(d, a, b));
                }
            return distance;
        }

        private static Func<Vector3, Vector3, bool> PlateValidationPairAcceptance(Stage stage, Geometry geometry, ShowLayoutDef layout,
            GameObject person, VisitorAvatar avatar, ShowActorDef doll, WalkNavigation visitorNav, WalkNavigation dollNav,
            out float measuredDollRadius, out Func<EndingFarewellLogic.Anchor, bool> trajectoryAccepted)
        {
            Vector3 origin = PlateValidationWorld(Vector2.zero, layout.room!.floorY);
            person.SetActive(true);
            person.transform.SetPositionAndRotation(origin, Quaternion.identity);
            avatar.Apply(0, 0);
            List<Vector3> personLocal = PlateValidationVertices(person);
            for (int i = 0; i < personLocal.Count; i++) personLocal[i] -= origin;
            Vector2[] personHull = avatar.Footprint();
            for (int i = 0; i < personHull.Length; i++) personHull[i] -= new Vector2(origin.x, origin.z);
            stage.PlaceActorAtWorld(doll, origin, 0, geometry);
            GameObject actor = PlateValidationField<GameObject>(stage, "_actor");
            List<Vector3> dollLocal = PlateValidationVertices(actor);
            Bounds bounds = PlateValidationBounds(dollLocal);
            Vector3 correction = Vector3.up * (origin.y - bounds.min.y);
            var dollPoints = new List<Vector2>();
            measuredDollRadius = 0;
            for (int i = 0; i < dollLocal.Count; i++)
            {
                dollLocal[i] = dollLocal[i] - origin + correction;
                dollPoints.Add(new Vector2(dollLocal[i].x, dollLocal[i].z));
                measuredDollRadius = Mathf.Max(measuredDollRadius, new Vector2(dollLocal[i].x, dollLocal[i].z).magnitude);
            }
            Vector2[] dollHull = PlateValidationHull(dollPoints);
            var personWorld = new List<Vector3>(personLocal.Count);
            var dollWorld = new List<Vector3>(dollLocal.Count);
            foreach (Vector3 point in personLocal) personWorld.Add(point);
            foreach (Vector3 point in dollLocal) dollWorld.Add(point);
            var movedPerson = new Vector2[personHull.Length];
            var movedDoll = new Vector2[dollHull.Length];
            List<ShowRoomProxyLogic.Box> room = ShowRoomProxyLogic.Build(layout.room);
            trajectoryAccepted = trial =>
            {
                float faceYaw = EndingFarewellLogic.FaceYawDeg(trial.foot, trial.visitorTargetFoot);
                Quaternion visitorYaw = Quaternion.Euler(0, EndingFarewellLogic.FaceYawDeg(trial.visitorTargetFoot, trial.foot), 0);
                for (int i = 0; i < personHull.Length; i++)
                {
                    Vector3 point = trial.visitorTargetFoot + visitorYaw * new Vector3(personHull[i].x, 0, personHull[i].y);
                    movedPerson[i] = new Vector2(point.x - StageOriginX, point.z);
                }
                float duration = EndingFarewellLogic.TurnSec + EndingFarewellLogic.RetreatSec;
                int samples = Mathf.CeilToInt(duration * 30f);
                Vector2[]? previousHull = null;
                Vector3 previousPosition = trial.foot;
                float previousTime = 0, previousVisitor = Vector3.Distance(trial.foot, trial.visitorTargetFoot);
                for (int frame = 0; frame <= samples; frame++)
                {
                    float time = Mathf.Min(duration, frame / 30f);
                    EndingFarewellLogic.Sample sample = EndingFarewellLogic.SampleAt(EndingFarewellLogic.AppearSec + EndingFarewellLogic.FaceSec + time);
                    EndingFarewellLogic.PoseAt(trial, sample, faceYaw, out Vector3 foot, out float yaw);
                    Quaternion rotation = Quaternion.Euler(0, yaw, 0);
                    for (int i = 0; i < dollHull.Length; i++)
                    {
                        Vector3 point = foot + rotation * new Vector3(dollHull[i].x, 0, dollHull[i].y);
                        movedDoll[i] = new Vector2(point.x - StageOriginX, point.z);
                    }
                    if (dollNav.SweptFootprintClearance(previousHull ?? movedDoll, movedDoll) < 0.04f
                        || PlateValidationActorsClearance(movedPerson, movedDoll) < 0.04f) return false;
                    float distance = Vector3.Distance(foot, trial.visitorTargetFoot);
                    if (distance < previousVisitor - 0.0001f) return false;
                    if (frame > 0 && Vector3.Distance(foot, previousPosition) / (time - previousTime) > 0.35f) return false;
                    if (sample.phase == EndingFarewellLogic.Phase.Retreat || sample.phase == EndingFarewellLogic.Phase.Done)
                    {
                        Vector3 forward = rotation * Vector3.forward;
                        if (Vector3.Dot(forward, (trial.visitorTargetFoot - foot).normalized) >= -0.05f
                            || Vector3.Dot(forward, (trial.cameraPosition - foot).normalized) >= -0.05f) return false;
                    }
                    previousHull = (Vector2[])movedDoll.Clone(); previousPosition = foot; previousTime = time; previousVisitor = distance;
                }
                return Vector3.Distance(previousPosition, trial.cameraPosition) > Vector3.Distance(trial.foot, trial.cameraPosition) + 0.01f;
            };
            return (visitorFoot, dollFoot) =>
            {
                Quaternion visitorYaw = Quaternion.Euler(0, EndingFarewellLogic.FaceYawDeg(visitorFoot, dollFoot), 0);
                Quaternion dollYaw = Quaternion.Euler(0, EndingFarewellLogic.FaceYawDeg(dollFoot, visitorFoot), 0);
                for (int i = 0; i < personLocal.Count; i++) personWorld[i] = visitorFoot + visitorYaw * personLocal[i];
                for (int i = 0; i < dollLocal.Count; i++) dollWorld[i] = dollFoot + dollYaw * dollLocal[i];
                if (!PlateValidationProjectionFits(stage.CgCamera, personWorld, 0.005f)
                    || !PlateValidationProjectionFits(stage.CgCamera, dollWorld, 0.005f)) return false;
                for (int i = 0; i < personHull.Length; i++)
                {
                    Vector3 point = visitorFoot + visitorYaw * new Vector3(personHull[i].x, 0, personHull[i].y);
                    movedPerson[i] = new Vector2(point.x - StageOriginX, point.z);
                }
                for (int i = 0; i < dollHull.Length; i++)
                {
                    Vector3 point = dollFoot + dollYaw * new Vector3(dollHull[i].x, 0, dollHull[i].y);
                    movedDoll[i] = new Vector2(point.x - StageOriginX, point.z);
                }
                return visitorNav.SweptFootprintClearance(movedPerson, movedPerson) >= 0.04f
                    && dollNav.SweptFootprintClearance(movedDoll, movedDoll) >= 0.04f
                    && PlateValidationActorsClearance(movedPerson, movedDoll) >= 0.04f
                    && PlateValidationExpectedVisible(stage.CgCamera, personWorld, Vector3.zero, room) >= 0.985f
                    && PlateValidationExpectedVisible(stage.CgCamera, dollWorld, Vector3.zero, room) >= 0.985f;
            };
        }

        private static float PlateValidationExpectedVisible(Camera camera, List<Vector3> vertices, Vector3 offset, List<ShowRoomProxyLogic.Box> boxes)
        {
            int visible = 0, total = 0;
            int step = Mathf.Max(1, vertices.Count / 160);
            for (int v = 0; v < vertices.Count; v += step)
            {
                Vector3 target = vertices[v] + offset;
                Vector3 direction = target - camera.transform.position;
                float length = direction.magnitude;
                bool blocked = false;
                foreach (ShowRoomProxyLogic.Box box in boxes)
                {
                    Quaternion rotation = Quaternion.Inverse(Quaternion.Euler(0, box.yawDeg, 0));
                    Vector3 center = box.center + Vector3.right * StageOriginX;
                    var ray = new Ray(rotation * (camera.transform.position - center), rotation * direction.normalized);
                    if (new Bounds(Vector3.zero, box.size).IntersectRay(ray, out float distance) && distance < length - 0.005f)
                    { blocked = true; break; }
                }
                if (!blocked) visible++;
                total++;
            }
            return total > 0 ? (float)visible / total : 0f;
        }

        private static Vector2 PlateValidationFindStand(Vector2 requested, WalkNavigation nav, Vector2? other = null, float separation = 0f)
        {
            Vector2 best = default;
            float score = float.PositiveInfinity;
            for (float z = nav.Bounds.yMin + nav.BodyRadiusM; z <= nav.Bounds.yMax - nav.BodyRadiusM; z += 0.025f)
                for (float x = nav.Bounds.xMin + nav.BodyRadiusM; x <= nav.Bounds.xMax - nav.BodyRadiusM; x += 0.025f)
                {
                    Vector2 p = new(x, z);
                    if (!nav.CanTraverse(p, p) || (other.HasValue && Vector2.Distance(p, other.Value) < separation)) continue;
                    float candidate = (p - requested).sqrMagnitude;
                    if (candidate < score) { score = candidate; best = p; }
                }
            if (float.IsInfinity(score)) throw new InvalidOperationException("[PlateValidation] No valid placement fits the floor and body width.");
            return best;
        }

        private static Vector2 PlateValidationFindHullStand(Vector2 requested, Vector2[] hull, WalkNavigation nav, Vector2 doll, float dollRadius)
        {
            Vector2 best = default;
            float score = float.PositiveInfinity;
            var moved = new Vector2[hull.Length];
            for (float z = nav.Bounds.yMin; z <= nav.Bounds.yMax; z += 0.05f)
                for (float x = nav.Bounds.xMin; x <= nav.Bounds.xMax; x += 0.05f)
                {
                    Vector2 at = new(x, z);
                    float distance = (at - requested).sqrMagnitude;
                    if (distance >= score) continue;
                    bool overlaps = false, inside = true;
                    for (int v = 0; v < hull.Length; v++)
                    {
                        moved[v] = hull[v] + at;
                        Vector2 a = moved[v], b = hull[(v + 1) % hull.Length] + at;
                        Vector2 delta = b - a;
                        Vector2 toDoll = doll - a;
                        if (delta.x * toDoll.y - delta.y * toDoll.x < 0f) inside = false;
                        float t = Mathf.Clamp01(Vector2.Dot(doll - a, delta) / Mathf.Max(1e-8f, delta.sqrMagnitude));
                        if (Vector2.Distance(doll, a + delta * t) < dollRadius + 0.04f) overlaps = true;
                    }
                    if (inside || overlaps || nav.SweptFootprintClearance(moved, moved) < 0.04f) continue;
                    score = distance; best = at;
                }
            if (float.IsInfinity(score)) throw new InvalidOperationException("[PlateValidation] Current posed body has no collision-free floor position.");
            return best;
        }

        private static Vector3 PlateValidationWorld(Vector2 xz, float floor) => new(StageOriginX + xz.x, floor, xz.y);

        private static List<Vector3> PlateValidationVertices(GameObject root)
        {
            var result = new List<Vector3>();
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is SkinnedMeshRenderer skin)
                {
                    Mesh mesh = skin.sharedMesh;
                    if (mesh == null) throw new InvalidOperationException("[PlateValidation] Skinned mesh is missing.");
                    Vector3[] vertices = mesh.vertices;
                    BoneWeight[] weights = mesh.boneWeights;
                    Matrix4x4[] bind = mesh.bindposes;
                    Transform[] bones = skin.bones;
                    if (weights.Length != vertices.Length || bind.Length != bones.Length)
                        throw new InvalidOperationException("[PlateValidation] Skin weights are incomplete.");
                    var matrices = new Matrix4x4[bones.Length];
                    for (int b = 0; b < bones.Length; b++) matrices[b] = bones[b].localToWorldMatrix * bind[b];
                    for (int v = 0; v < vertices.Length; v++)
                    {
                        BoneWeight bw = weights[v];
                        Vector3 world = matrices[bw.boneIndex0].MultiplyPoint3x4(vertices[v]) * bw.weight0;
                        if (bw.weight1 > 0) world += matrices[bw.boneIndex1].MultiplyPoint3x4(vertices[v]) * bw.weight1;
                        if (bw.weight2 > 0) world += matrices[bw.boneIndex2].MultiplyPoint3x4(vertices[v]) * bw.weight2;
                        if (bw.weight3 > 0) world += matrices[bw.boneIndex3].MultiplyPoint3x4(vertices[v]) * bw.weight3;
                        result.Add(world);
                    }
                }
                else if (renderer.TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null)
                    foreach (Vector3 vertex in filter.sharedMesh.vertices) result.Add(filter.transform.TransformPoint(vertex));
            }
            if (result.Count == 0) throw new InvalidOperationException("[PlateValidation] Actor contains no measurable vertices.");
            return result;
        }

        private static Bounds PlateValidationBounds(List<Vector3> vertices)
        {
            var bounds = new Bounds(vertices[0], Vector3.zero);
            foreach (Vector3 vertex in vertices) bounds.Encapsulate(vertex);
            return bounds;
        }

        private static PlateValidationBody PlateValidationMeasure(Camera camera, GameObject actor, float expectedHeight, float floor,
                                                                  ShowLayoutDef layout, float yaw)
        {
            List<Vector3> vertices = PlateValidationVertices(actor);
            Bounds bounds = PlateValidationBounds(vertices);
            var data = new PlateValidationBody { foot = actor.transform.position - Vector3.right * StageOriginX, yawDeg = yaw,
                expectedHeightM = expectedHeight, measuredHeightM = bounds.size.y, heightErrorM = bounds.size.y - expectedHeight,
                soleErrorM = bounds.min.y - floor, vertices = vertices.Count, materialsResolved = true, minWallClearanceM = 1000f };
            Quaternion inverseYaw = Quaternion.Inverse(actor.transform.rotation);
            var localBounds = new Bounds(inverseYaw * (vertices[0] - actor.transform.position), Vector3.zero);
            foreach (Vector3 vertex in vertices) localBounds.Encapsulate(inverseYaw * (vertex - actor.transform.position));
            foreach (Vector3 vertex in vertices)
            {
                Vector3 offset = vertex - actor.transform.position;
                data.maxBodyRadiusM = Mathf.Max(data.maxBodyRadiusM, new Vector2(offset.x, offset.z).magnitude);
            }
            data.localBoundsMinM = localBounds.min; data.localBoundsMaxM = localBounds.max;
            Bounds feet = default;
            foreach (Vector3 vertex in vertices)
                if (vertex.y <= floor + 0.15f)
                {
                    Vector3 local = inverseYaw * (vertex - actor.transform.position);
                    if (data.feetVertices++ == 0) feet = new Bounds(local, Vector3.zero);
                    else feet.Encapsulate(local);
                }
            data.feetLocalMinM = feet.min; data.feetLocalMaxM = feet.max;
            float minV = float.PositiveInfinity, maxV = float.NegativeInfinity;
            int inFrame = 0;
            foreach (Vector3 vertex in vertices)
            {
                Vector3 vp = camera.WorldToViewportPoint(vertex);
                if (vp.z > camera.nearClipPlane)
                {
                    minV = Mathf.Min(minV, vp.y); maxV = Mathf.Max(maxV, vp.y);
                    if (vp.x >= 0 && vp.x <= 1 && vp.y >= 0 && vp.y <= 1) inFrame++;
                }
                Vector2 xz = new(vertex.x - StageOriginX, vertex.z);
                foreach (ShowRoomWallDef wall in layout.room!.walls)
                {
                    if (vertex.y < floor || vertex.y > floor + wall.h) continue;
                    Vector2 a = new(wall.x1, wall.z1), b = new(wall.x2, wall.z2), delta = b - a;
                    float t = Mathf.Clamp01(Vector2.Dot(xz - a, delta) / Mathf.Max(1e-8f, delta.sqrMagnitude));
                    data.minWallClearanceM = Mathf.Min(data.minWallClearanceM, Vector2.Distance(xz, a + t * delta) - wall.thick * 0.5f);
                }
            }
            data.inFrameVertexFraction = (float)inFrame / vertices.Count;
            data.projectedHeightPx = float.IsInfinity(minV) ? 0f : (maxV - minV) * camera.targetTexture.height;
            Vector3 foot = new(bounds.center.x, floor, bounds.center.z);
            Vector3 topVp = camera.WorldToViewportPoint(foot + Vector3.up * expectedHeight);
            Vector3 footVp = camera.WorldToViewportPoint(foot);
            data.expectedHeightPx = Mathf.Abs(topVp.y - footVp.y) * camera.targetTexture.height;
            data.projectedHeightErrorPx = data.projectedHeightPx - data.expectedHeightPx;
            data.viewportFoot = new Vector2(footVp.x, footVp.y);
            data.feetInFrame = footVp.z > camera.nearClipPlane && footVp.x >= 0 && footVp.x <= 1 && footVp.y >= 0 && footVp.y <= 1;
            foreach (Renderer renderer in actor.GetComponentsInChildren<Renderer>(true))
                foreach (Material material in renderer.sharedMaterials)
                {
                    data.materials++;
                    if (material == null || material.shader == null || !material.shader.isSupported
                        || material.shader.name == "Hidden/InternalErrorShader") { data.materialsResolved = false; continue; }
                    foreach (string texture in material.GetTexturePropertyNames()) if (material.GetTexture(texture) != null) data.textures++;
                }
            if (!data.materialsResolved) throw new InvalidOperationException("[PlateValidation] Actor materials did not resolve.");
            return data;
        }

        private static Color32[] PlateValidationMask(Stage stage, GameObject target, GameObject other, ShowRoomProxy proxy,
                                                     bool person, PlateValidationBody data, string outDir, string file)
        {
            var savedMaterials = new Dictionary<Renderer, Material[]>();
            Transform blob = PlateValidationField<Transform>(stage, "_blob");
            bool blobActive = blob.gameObject.activeSelf;
            bool otherActive = other.activeSelf;
            other.SetActive(false);
            blob.gameObject.SetActive(false);
            try
            {
                foreach (Renderer renderer in target.GetComponentsInChildren<Renderer>(true))
                {
                    Material[] materials = renderer.sharedMaterials;
                    savedMaterials.Add(renderer, materials);
                    var body = new List<Material>();
                    foreach (Material material in materials)
                        if (material.shader.name != "FixedCamVr/ShowShadowProjector") body.Add(material);
                    renderer.sharedMaterials = body.ToArray();
                }
                proxy.gameObject.SetActive(false);
                Color32[] unoccluded = PlateValidationReadCg(stage);
                foreach (Color32 pixel in unoccluded) if (pixel.a >= 128) data.pixelsUnoccluded++;
                proxy.gameObject.SetActive(true);
                Color32[] visible = PlateValidationReadCg(stage);
                foreach (Color32 pixel in visible) if (pixel.a >= 128) data.pixelsVisible++;
                data.visibleFraction = data.pixelsUnoccluded > 0 ? (float)data.pixelsVisible / data.pixelsUnoccluded : 0f;
                using var image = new PlateValidationTexture(new Texture2D(stage.CgCamera.targetTexture.width,
                    stage.CgCamera.targetTexture.height, TextureFormat.RGBA32, false));
                image.Texture.SetPixels32(visible); image.Texture.Apply();
                File.WriteAllBytes(Path.Combine(outDir, file), image.Texture.EncodeToPNG());
                return visible;
            }
            finally
            {
                proxy.gameObject.SetActive(true);
                foreach (var pair in savedMaterials) pair.Key.sharedMaterials = pair.Value;
                other.SetActive(otherActive);
                blob.gameObject.SetActive(blobActive);
            }
        }

        private static Color32[] PlateValidationReadCg(Stage stage)
        {
            stage.CgCamera.Render();
            RenderTexture source = stage.CgCamera.targetTexture;
            RenderTexture previous = RenderTexture.active;
            RenderTexture resolve = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
            using var texture = new PlateValidationTexture(new Texture2D(source.width, source.height, TextureFormat.RGBA32, false));
            try
            {
                Graphics.Blit(source, resolve);
                RenderTexture.active = resolve;
                texture.Texture.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                texture.Texture.Apply();
                return texture.Texture.GetPixels32();
            }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(resolve); }
        }

        private static void PlateValidationColor(Texture2D image, Color32[] mask, Plate plate, Vector2 contain, PlateValidationBody data)
        {
            Color32[] pixels = image.GetPixels32();
            double luma = 0, saturation = 0, grain = 0;
            int count = 0;
            int mw = plate.width, mh = plate.height;
            for (int y = 0; y < image.height; y++)
                for (int x = 1; x < image.width - 1; x++)
                {
                    float u = ((x + 0.5f) / image.width - 0.5f) / contain.x + 0.5f;
                    float v = ((y + 0.5f) / image.height - 0.5f) / contain.y + 0.5f;
                    if (u < 0 || u >= 1 || v < 0 || v >= 1) continue;
                    if (mask[Mathf.Min(mh - 1, (int)(v * mh)) * mw + Mathf.Min(mw - 1, (int)(u * mw))].a < 128) continue;
                    int i = y * image.width + x;
                    Color32 c = pixels[i];
                    float value = PlateValidationLuma(c);
                    int max = Mathf.Max(c.r, Mathf.Max(c.g, c.b)), min = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
                    luma += value;
                    saturation += max > 12 ? (max - min) / (float)max : 0;
                    grain += Mathf.Abs(value - (PlateValidationLuma(pixels[i - 1]) + PlateValidationLuma(pixels[i + 1])) * 0.5f);
                    count++;
                }
            if (count > 0) { data.luma = (float)(luma / count); data.saturation = (float)(saturation / count * 100); data.grain = (float)(grain / count); }
        }

        private static float PlateValidationLuma(Color32 c) => 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
        private static string PlateValidationCaption(string label, PlateValidationBody body)
            => string.Format(CultureInfo.InvariantCulture, "{0} h={1:F3}/{2:F3}m sole={3:F4}m frame={4:P0} visible={5}/{6}px wall={7:F3}m",
                label, body.measuredHeightM, body.expectedHeightM, body.soleErrorM, body.inFrameVertexFraction,
                body.pixelsVisible, body.pixelsUnoccluded, body.minWallClearanceM);

        private static void PlateValidationWriteIndex(PlateValidationReport report)
        {
            var index = new StringBuilder("# Real plate composite validation\n\n");
            index.AppendLine("Complete: " + report.complete + ". Production doll arms are disabled. No virtual-room fallback is used.");
            index.AppendLine(report.measurement);
            index.AppendLine("Full composites include a caption. *_image.png excludes the caption; *_mask.png shows the isolated actor with room occlusion. source_*.jpg are byte-identical inputs.");
            index.AppendLine("platecheck_*.png and plate-evidence.tsv verify each real input through the compositor before wires or actors are added. Outro shader power/collapse/noise are restored before the next camera.");
            index.AppendLine("ending-animation/f00000.jpg onward is the actual caption-free compositor at 30fps, starting with the saved photo. frames.tsv links each video frame to the gate time. ending-gates.tsv records actual position, camera/person distances, face-direction dots and full-body wall clearance; a Retreat state label alone is not acceptance evidence.");
            index.AppendLine("speedMps is the measured distance between consecutive 30Hz world positions, with a 0.35m/s ceiling. cumulativeTravelM is the actual path length; nominalRetreatM only normalizes the animation progress. Before selection, trajectoryAccepted probes the complete CPU body hull through Turn and Retreat, including swept wall clearance.");
            index.AppendLine("\nCenter/left/right positions are searched separately for each camera, with 0.5m explicit floor padding, 4cm body clearance, 2.5% image margins and both actors visible. Wall tests record expected occlusion. ending_face uses camera A's production wall solver and the same world positions in B/C without relocation.");
            index.AppendLine("The three required pairs are standing poses. walk_extra uses an actual walking animation phase and searched body yaw; unavailable walking cases are recorded below, never replaced with idle poses.");
            index.AppendLine("All normal cameras are rendered before the ending pass. rawSoleErrorM is measured before the preview grounding correction; rawSoleMeasured identifies that probe. actorsClearanceM is the distance between both complete CPU-skinned XZ hulls, with overlaps recorded as zero and a 4cm minimum required.");
            foreach (string skipped in report.skippedCases) index.AppendLine("\nSkipped optional case: " + skipped);
            var table = new StringBuilder("camera\tpose\tactor\theightM\texpectedM\theightErrorM\tsoleErrorM\twallClearanceM\tinFrameFraction\tvisiblePixels\tunoccludedPixels\tvisibleFraction\tluma\tsaturation\tgrain\tmaterials\ttextures\tpurpose\tvisibilityRequired\tconstraintsPassed\texpectedVisibleFraction\trawSoleMeasured\trawSoleErrorM\tgroundingCorrectionM\tactorsClearanceM\n");
            foreach (PlateValidationFrame frame in report.frames)
            {
                index.AppendLine("\n- [" + frame.camera + " " + frame.pose + "](" + frame.png + ") — " + frame.geometry);
                foreach (var item in new[] { ("visitor", frame.visitor), ("doll", frame.doll) })
                    table.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "{0}\t{1}\t{2}\t{3:F5}\t{4:F5}\t{5:F5}\t{6:F5}\t{7:F5}\t{8:F5}\t{9}\t{10}\t{11:F5}\t{12:F3}\t{13:F3}\t{14:F3}\t{15}\t{16}\t{17}\t{18}\t{19}\t{20:F5}\t{21}\t{22:F8}\t{23:F8}\t{24:F5}",
                        frame.camera, frame.pose, item.Item1, item.Item2.measuredHeightM, item.Item2.expectedHeightM,
                        item.Item2.heightErrorM, item.Item2.soleErrorM, item.Item2.minWallClearanceM,
                        item.Item2.inFrameVertexFraction, item.Item2.pixelsVisible, item.Item2.pixelsUnoccluded,
                        item.Item2.visibleFraction, item.Item2.luma, item.Item2.saturation, item.Item2.grain,
                        item.Item2.materials, item.Item2.textures, frame.purpose, frame.visibilityRequired, frame.constraintsPassed,
                        item.Item1 == "visitor" ? frame.expectedVisitorVisibleFraction : frame.expectedDollVisibleFraction,
                        item.Item2.rawSoleMeasured, item.Item2.rawSoleErrorM, item.Item2.groundingCorrectionM, frame.actorsClearanceM));
            }
            File.WriteAllText(Path.Combine(report.output, "index.md"), index.ToString(), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(report.output, "metrics.tsv"), table.ToString(), new UTF8Encoding(false));
        }
    }
}
