#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using FixedCamVr.Diagnostics;
using FixedCamVr.Streaming.Cg;
using FixedCamVr.Tracking;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools.VirtualWalk
{
    /// <summary>
    /// <b>PC の仮想体験</b>: 体験者の代役（NPC）がブースを歩き、固定カメラ A/B/C が撮った映像が
    /// <b>実シェーダ</b>（ScreenComposite）と<b>実ロジック</b>（CameraSwitchDirector・GlitchFx・ZonePickLogic・
    /// YawFollowLogic・OutroLogic）を通ってスクリーンへ出る。<b>NPC の目から見た絵</b>を連番で残す。
    ///
    /// 2026-10-14 ユーザー指示「動作検証は、PCで仮想的な体験環境を作って、その中でNPCを歩かせて
    /// その視点から見てチェックするように」。
    ///
    /// <b>映像は実物に寄せる</b>: 各カメラの<b>無人の実プレート</b>（実機と同じ位置から撮った写真）を背景にし、
    /// NPC は<b>本番の CG 層</b>（較正済みの仮想カメラ・壁の遮蔽・同じ contain 枠とレンズ）で重ねる。
    /// スクリーンは <b>本番のヨー追従</b>（<see cref="YawFollowLogic"/>・0.30s・110°/s）で頭を追う（head-lock ではない）。
    /// 刻みは<b>実機と同じ 90Hz</b>（0.07s の暗転は 30fps だと 2 コマで、緩急が原理的に写らない）。
    ///
    /// ⚠ <b>実機の代わりではない</b>（<c>.claude/reference/why.md</c>「代理を厚くしない」・0236）。
    /// 圧縮・伝送の痩せ・遅延・両眼視差・実機ディスプレイは入っていない。見るのは<b>時刻・順序・曲線・重なり</b>。
    /// 見た目の合否は実機（<c>quest-capture</c>）で出す。
    /// ⚠ シェーダの <c>_Time.y</c>（砂嵐・点灯）は進まない。この 2 つはここでは判定しない。
    ///
    /// 使い方: <c>.\tools\unity.ps1 menu virtualwalk -Set laps=1</c>
    /// → <c>Logs/virtualwalk/&lt;時刻&gt;/</c> に f00000.jpg…（30fps）・frames.tsv（90Hz）・events.tsv
    /// → <c>py -3.11 tools/virtualwalk-video.py &lt;そのフォルダ&gt;</c> で mp4 と指標。
    /// </summary>
    public static class VirtualWalkRun
    {
        private const int TickHz = 90;                     // 実機の表示レート（DisplayRateRequester）
        private const int OutEvery = 3;                    // 90Hz → 30fps で書き出す
        private const int SrcW = 640, SrcH = 480;          // 実機の配信と較正の寸法
        private const float EyeFovV = 52f;                 // 片目の縦画角（スクリーン 62°×37° が収まる）
        private const float EyePitchDeg = 6f;              // 少し下を見る（スクリーンは 8 度下）
        private const float VisitorHeightM = 1.65f;
        private const float EyeBelowTopM = 0.11f;
        private const int ScreenLayer = 30;                // 目のカメラだけが見る層
        private const float ScreenDistM = 2.0f;            // ScreenAnchor.distance
        private const float ScreenDropM = 0.28f;           // ScreenAnchor.heightOffset（下へ）
        private const float ScreenW = 2.3704f, ScreenH = 1.3333f;
        private const float WalkSpeed = 0.5f;              // ShowWalkDebugDriver と同じ
        private const float StrideM = 1.3f;                // 2 歩 = 1 周期
        private const string CgLayerName = "ShowCg";
        private static readonly DateTime OsdStart = new DateTime(2026, 10, 14, 17, 20, 0);

        [Serializable] private sealed class Root
        {
            public ShowLayoutDef? layout;
            public Cam[]? cameras;
            public PostParams? post;
            public Control? control;
        }

        [Serializable] private sealed class Cam
        {
            public string id = "";
            public ShowCameraPoseDef? pose;
            public bool hasPose;
            public ShowCameraCalibDef? calib;
            public bool hasCalib;
        }

        [Serializable] private sealed class Control { public float switchGlitch; }

        private sealed class View
        {
            public string id = "";
            public Texture? live;          // 実プレート（無ければ仮想部屋の RT）
            public bool plate;
            public Vector3 pos;
            public Quaternion rot;
            public Matrix4x4 proj;
            public Vector4 lens = new Vector4(0f, 0.5f, 0.5f, 0f);
            public Vector4 focal = new Vector4(1f, 1f, 0f, 0f);
        }

        private static readonly List<UnityEngine.Object> _trash = new();

        [MenuItem("Tools/FixedCamVr/Diagnostics/Virtual Walk (NPC POV)", priority = 240)]
        public static void Run()
        {
            int laps = ArgInt("laps", 1);
            float dwellSec = ArgFloat("dwell", 3f);
            int eyeW = ArgInt("w", 960);
            int eyeH = eyeW * 9 / 16;
            bool outro = ArgInt("outro", 1) != 0;
            float maxSec = ArgFloat("maxsec", 240f);
            string tag = EditorCliArgs.Get("tag") ?? "";
            bool glitchStills = ArgInt("glitchstills", 0) != 0;
            bool commsOn = ArgInt("comms", 0) != 0;
            float takeoverAt = ArgFloat("takeover", -1f);

            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string outDir = Path.Combine(projectRoot, "Logs", "virtualwalk", tag.Length > 0 ? stamp + "_" + tag : stamp);
            Directory.CreateDirectory(outDir);

            Root? show = LoadShow(projectRoot, out string showPath);
            if (show?.layout?.grid == null || show.cameras == null || show.cameras.Length == 0)
            {
                Debug.LogError($"[VirtualWalk] show.json に layout.grid / cameras が無い（{showPath}）");
                return;
            }

            // Main.unity を汚さない。空の場面で組む（batchmode なので保存の確認は出ない）。
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            try
            {
                Execute(show, showPath, projectRoot, outDir, laps, dwellSec, eyeW, eyeH, outro, maxSec,
                        glitchStills, commsOn, takeoverAt);
            }
            finally
            {
                foreach (UnityEngine.Object o in _trash) if (o != null) UnityEngine.Object.DestroyImmediate(o);
                _trash.Clear();
            }
        }

        private static void Execute(Root show, string showPath, string projectRoot, string outDir, int laps,
                                    float dwellSec, int eyeW, int eyeH, bool outro, float maxSec,
                                    bool glitchStills, bool commsOn, float takeoverAt)
        {
            ShowLayoutDef layout = show.layout!;
            float floorY = layout.room != null ? layout.room.floorY : 0f;
            int cgLayer = LayerMask.NameToLayer(CgLayerName);
            if (cgLayer < 0) { Debug.LogError($"[VirtualWalk] レイヤ '{CgLayerName}' が無い"); return; }

            // ---- カメラごとの「見え」（実プレート＋較正） ----
            int camCount = Mathf.Min(3, show.cameras!.Length);
            var views = new View[camCount];
            bool needRoom = false;
            for (int i = 0; i < camCount; i++)
            {
                views[i] = MakeView(show.cameras[i], projectRoot);
                needRoom |= !views[i].plate;
            }

            // ---- 部屋: プレートが揃っていれば遮蔽（深度だけ）だけ。欠けたカメラがあれば仮想の部屋も組む ----
            BuildOccluders(layout, floorY, cgLayer);
            Camera? roomCam = null;
            if (needRoom)
            {
                VirtualRoom.Build(layout, floorY, _trash);
                var go = new GameObject("RoomCam");
                _trash.Add(go);
                roomCam = go.AddComponent<Camera>();
                roomCam.enabled = false;
                roomCam.cullingMask = ~((1 << ScreenLayer) | (1 << cgLayer));
                roomCam.clearFlags = CameraClearFlags.SolidColor;
                roomCam.backgroundColor = new Color(0.02f, 0.02f, 0.02f, 1f);
                for (int i = 0; i < camCount; i++)
                {
                    if (views[i].plate) continue;
                    var rt = new RenderTexture(SrcW, SrcH, 24, RenderTextureFormat.ARGB32)
                    { useMipMap = true, autoGenerateMips = true, antiAliasing = 1, filterMode = FilterMode.Trilinear };
                    rt.Create();
                    _trash.Add(rt);
                    views[i].live = rt;
                }
            }
            LightCg(layout);

            // ---- 人（体験者の代役。本番の CG 層に載る） ----
            var prefab = Resources.Load<GameObject>("ShowActors/Remy");
            if (prefab == null) { Debug.LogError("[VirtualWalk] ShowActors/Remy が無い"); return; }
            var npc = UnityEngine.Object.Instantiate(prefab);
            npc.name = "NPC (visitor)";
            _trash.Add(npc);
            foreach (var mb in npc.GetComponentsInChildren<MonoBehaviour>()) mb.enabled = false;
            foreach (var smr in npc.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                smr.updateWhenOffscreen = true;
                smr.forceMatrixRecalculationPerRender = true;
            }
            npc.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            float bindHeight = MeasureHeight(npc);
            npc.transform.localScale = Vector3.one * (bindHeight > 0.01f ? VisitorHeightM / bindHeight : 1f);
            NpcWalkPose? pose = NpcWalkPose.Bind(npc.transform);
            if (pose?.Head == null) { Debug.LogError("[VirtualWalk] Remy の骨を掴めない"); return; }
            Transform hmd = VirtualRoom.BuildHmd(_trash);
            SetLayerRecursive(npc, cgLayer);
            SetLayerRecursive(hmd.gameObject, cgLayer);

            // CG を描く仮想カメラ（ShowCgLayer.EnsureCamera と同じ設定）
            var cgGo = new GameObject("CgCam");
            _trash.Add(cgGo);
            var cgCam = cgGo.AddComponent<Camera>();
            cgCam.enabled = false;
            cgCam.cullingMask = 1 << cgLayer;
            cgCam.clearFlags = CameraClearFlags.SolidColor;
            cgCam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cgCam.allowHDR = false;
            cgCam.allowMSAA = true;
            cgCam.nearClipPlane = 0.05f;
            cgCam.farClipPlane = 30f;
            var cgRt = new RenderTexture(SrcW, SrcH, 24, RenderTextureFormat.ARGB32)
            { antiAliasing = 4, useMipMap = false, wrapMode = TextureWrapMode.Clamp, name = "CgRT" };
            cgRt.Create();
            _trash.Add(cgRt);

            // ---- スクリーン（実シェーダ・実 Director・実 GlitchFx） ----
            Shader? sh = Shader.Find("FixedCamVr/ScreenComposite");
            if (sh == null) { Debug.LogError("[VirtualWalk] ScreenComposite が無い"); return; }
            var screen = new GameObject("Screen") { layer = ScreenLayer };
            _trash.Add(screen);
            screen.AddComponent<MeshFilter>().sharedMesh = CrtScreenMesh.BuildMesh(14, 0.055f);
            var mr = screen.AddComponent<MeshRenderer>();
            var baseMat = new Material(sh) { name = "ScreenComposite (virtual walk)" };
            _trash.Add(baseMat);
            mr.sharedMaterial = baseMat;
            screen.transform.localScale = new Vector3(ScreenW, ScreenH, 1f);

            var regGo = new GameObject("Registry");
            _trash.Add(regGo);
            var registry = regGo.AddComponent<CameraStreamRegistry>();
            Refl.Set(registry, "_streams", Array.CreateInstance(typeof(CameraStream), camCount));
            Refl.Set(registry, "_activeIndex", 0);

            var glitch = screen.AddComponent<GlitchFx>();
            var director = screen.AddComponent<CameraSwitchDirector>();
            Refl.Set(director, "registry", registry);
            Refl.Set(director, "glitchFx", glitch);
            float simTime = 0f;
            const float dt = 1f / TickHz;
            director.SetDeltaSource(() => dt);
            director.SetClock(() => simTime);
            Refl.Call(glitch, "Awake");
            Refl.Call(director, "Awake");
            Refl.Call(director, "OnEnable");
            director.SetSwitchGlitch(show.control?.switchGlitch ?? 0f);
            Material mat = mr.sharedMaterial;   // Awake が r.material で複製した実体
            _trash.Add(mat);

            const float frameAspect = 16f / 9f;
            float srcAspect = SrcW / (float)SrcH;
            var contain = new Vector4(srcAspect / frameAspect, 1f, 0f, 0f);
            mat.SetVector("_LiveScale", contain);
            mat.SetVector("_CgScale", contain);
            mat.SetFloat("_FrameAspect", frameAspect);
            mat.SetFloat("_CrtRound", CameraFeelFx.CrtRound);
            mat.SetFloat("_CrtEdge", CameraFeelFx.CrtEdge);
            mat.SetFloat("_CrtEdgeWidth", CameraFeelFx.CrtEdgeWidth);
            mat.SetFloat("_Glare", CameraFeelFx.Glare);
            mat.SetFloat("_ChromaKill", CameraFeelFx.ChromaKill);
            mat.SetTexture("_CgTex", cgRt);
            mat.SetFloat("_CgStrength", 1f);
            ApplyPost(mat, show.post);
            void Bind(int i)
            {
                View v = views[i];
                mat.SetTexture("_LiveTex", v.live);
                mat.SetVector("_CgLens", v.lens);
                mat.SetVector("_CgFocalN", v.focal);
            }
            Bind(0);
            registry.ActiveChanged += i => { if (i >= 0 && i < camCount) Bind(i); };

            var osd = screen.AddComponent<ScreenOsd>();
            osd.enabled = false;
            osd.BindForPreview(mat, new Vector2(contain.x, contain.y));

            // ---- 終幕の報告（実物の OutroReport。写真は見本） ----
            var reportGo = new GameObject("OutroReport");
            _trash.Add(reportGo);
            var outroReport = reportGo.AddComponent<OutroReport>();
            ShowCompositePreview.CreateEndingSamples(out Texture2D relShot, out Texture2D trapShot);
            _trash.Add(relShot);
            _trash.Add(trapShot);
            outroReport.SetEndingImages(relShot, trapShot);
            bool reportShown = false;

            // ---- エージェントスクリーン（実物の CommsPanel・comms=1 のとき） ----
            // 台本: 0.6s ①「調査を開始してください。」（1 行）→ 読ませる 2s の終わりに①b（3 行。
            //       連続配信 = 0096 の「そのスクリーンのまま」。枠の高さが変わる組）
            //       → ①b を読ませている途中で報告を押し始める（走っている連絡が片づく・0065）
            //       → 1 秒の長押しで②「異常を検出しました」→ takeover=<秒> で乗っ取り（主画面の警告つき）。
            GameObject? commsGo = null;
            CommsPanel? comms = null;
            CommsPanelLogic? commsLogic = null;
            MethodInfo? commsApply = null;
            CommsTakeoverError? takeover = null;
            bool chainPending = false, chainDone = false, markDone = false, takeoverDone = false;
            float pressAt = -1f;
            bool releaseDone = false;
            int lastChars = 0;
            CommsStage lastStage = CommsStage.Off;
            // 連絡の面の数値（90Hz）。差し替え・押して片づける瞬間の濃さと枠の高さを前後で比べる。
            var commsTsv = new StringBuilder();
            commsTsv.AppendLine("tick\tt\tstage\tglyph\tchars\ttextId\ttextAlpha\tpanelAlpha\tbody\tband\tboxH\thint");
            FieldInfo? commsTextField = typeof(CommsPanel).GetField("_text", BindingFlags.NonPublic | BindingFlags.Instance);
            // 新しい枠の高さ（差し替えで動かす方）が無い版でも同じ表を出す（前後比較のため）。
            FieldInfo? bandField = typeof(CommsPanel).GetField("_bandShown", BindingFlags.NonPublic | BindingFlags.Instance)
                                ?? typeof(CommsPanel).GetField("_bodyBandH", BindingFlags.NonPublic | BindingFlags.Instance);
            if (commsOn)
            {
                commsGo = new GameObject("CommsPanel");
                _trash.Add(commsGo);
                comms = commsGo.AddComponent<CommsPanel>();
                Refl.Call(comms, "Awake");
                commsLogic = (CommsPanelLogic)Refl.Get(comms, "_logic");
                commsApply = typeof(CommsPanel).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance);
                comms.SetControllerState(false, false);
                comms.SetDecayForPreview(0f, 0f);
                if (!comms.IsBuilt) { Debug.LogError("[VirtualWalk] CommsPanel を組めない"); comms = null; }
            }
            bool stillsDone = false;

            // ---- 目 ----
            var eyeGo = new GameObject("NPC Eye");
            _trash.Add(eyeGo);
            var eye = eyeGo.AddComponent<Camera>();
            eye.enabled = false;
            eye.cullingMask = 1 << ScreenLayer;
            eye.clearFlags = CameraClearFlags.SolidColor;
            eye.backgroundColor = Color.black;
            eye.fieldOfView = EyeFovV;
            eye.nearClipPlane = 0.05f;
            eye.farClipPlane = 20f;
            var eyeRt = new RenderTexture(eyeW, eyeH, 24) { antiAliasing = 4 };
            eyeRt.Create();
            _trash.Add(eyeRt);
            var readTex = new Texture2D(eyeW, eyeH, TextureFormat.RGB24, false);
            _trash.Add(readTex);

            // ---- 道筋とゾーン（実ロジック） ----
            int[,] map = WalkRouteLogic.BuildCells(layout.grid!, out int rows, out int cols);
            int[] order = layout.course?.order is { Length: > 0 } o ? o : new[] { 0, 1, 2 };
            List<WalkRouteLogic.Waypoint> route = WalkRouteLogic.BuildRoute(map, rows, cols, layout.grid!.tileM,
                                                                             order, laps, dwellSec, out string routeLog);
            if (route.Count < 2) { Debug.LogError("[VirtualWalk] 道筋が作れない（grid を確認）"); return; }
            ZonePickLogic.Box[] zones = BuildZones(layout, out float hyst);
            var yaw = new YawFollowLogic();

            // ---- 走らせる ----
            var tsv = new StringBuilder();
            tsv.AppendLine("tick\tt\tx\tz\tbodyYaw\tscreenYaw\tpick\tactive\tdipping\tdim\tglitch\tglitchCount\tcollapse\tpower\treport");
            var events = new StringBuilder();
            Vector2 pos = route[0].Course;
            int wp = 1;
            float hold = route[0].HoldSec;
            float bodyYaw = YawTo(route[0].Course, route[1].Course);
            yaw.Reseat(bodyYaw);
            float phase = 0f, walk01 = 0f;
            int pick = -1, tick = 0, frame = 0;
            bool wasDipping = false, glitchArmed = true, outroStarted = false;
            float glitchOnSince = -1f, lastGlitchSfx = -10f, endHold = 0f;
            var outroLogic = new OutroLogic();
            outroLogic.Configure(OutroTiming.Default);
            director.SwitchCommitted += (cam, src) =>
                events.Append(F(simTime)).Append("\tcommit\tcam=").Append(cam).Append(" src=").Append(src).Append('\n');

            while (simTime < maxSec)
            {
                // 1) 歩く
                bool walking = false;
                if (!outroStarted)
                {
                    if (hold > 0f) hold -= dt;
                    else if (wp < route.Count)
                    {
                        Vector2 d = route[wp].Course - pos;
                        float step = WalkSpeed * dt;
                        if (d.magnitude <= step)
                        {
                            pos = route[wp].Course;
                            hold = route[wp].HoldSec;
                            wp++;
                        }
                        else
                        {
                            pos += d.normalized * step;
                            walking = true;
                            bodyYaw = Mathf.MoveTowardsAngle(bodyYaw, Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg, 240f * dt);
                        }
                    }
                    else if (outro)
                    {
                        outroStarted = true;
                        outroLogic.Begin();
                        events.Append(F(simTime)).Append("\toutro\tbegin\n");
                    }
                    else break;
                }
                walk01 = Mathf.MoveTowards(walk01, walking ? 1f : 0f, dt / 0.35f);
                phase += walking ? WalkSpeed * dt / StrideM * Mathf.PI * 2f : dt * 0.6f;

                Quaternion bodyRot = Quaternion.Euler(0f, bodyYaw, 0f);
                npc.transform.SetPositionAndRotation(new Vector3(pos.x, floorY, pos.y), bodyRot);
                pose.Apply(phase, walk01, 0f);
                Vector3 bodyFwd = bodyRot * Vector3.forward;
                hmd.SetPositionAndRotation(pose.Head!.position + Vector3.up * 0.085f + bodyFwd * 0.10f, bodyRot);
                Vector3 eyePos = new Vector3(pos.x, floorY + VisitorHeightM - EyeBelowTopM, pos.y) + bodyFwd * 0.04f;

                // 2) ゾーン → 実 Director（dwell・cooldown・dip・切替の乱れ）
                int now = ZonePickLogic.Pick(zones, pos.x, 1.5f, pos.y, pick, hyst, keepLastWhenOutside: true);
                if (now >= 0) pick = now;
                if (pick >= 0 && !outroStarted) director.RequestZone(zones[pick].CameraIndex);
                Refl.Call(director, "Update");
                glitch.TickExternal(dt, simTime);

                if (comms != null && commsLogic != null && commsApply != null)
                {
                    if (simTime >= 0.6f && lastStage == CommsStage.Off && !chainPending && !markDone && simTime < 1f)
                    {
                        comms.Deliver(CommsNotice.Begin);
                        chainPending = true;
                    }
                    if (chainPending && comms.Stage == CommsStage.Hold && commsLogic.StageElapsedSec >= 1.95f)
                    {
                        comms.Deliver(CommsNotice.BeginHow);
                        chainPending = false;
                        chainDone = true;
                        events.Append(F(simTime)).Append("\tcomms_deliver\tBeginHow (chained)\n");
                    }
                    // 読ませている最中に報告を押し始める（走っている連絡は片づく・0065）→ 1 秒の長押しで②。
                    // 本番の `Update` と同じく、押している間は毎刻 `SetGuideWanted(true)` を立てる。
                    if (chainDone && pressAt < 0f && !markDone
                        && comms.Stage == CommsStage.Hold && commsLogic.StageElapsedSec >= 1.0f)
                    {
                        pressAt = simTime;
                        comms.SetControllerState(true, true);
                        events.Append(F(simTime)).Append("\tpress\tstart\n");
                    }
                    if (pressAt >= 0f && !markDone)
                    {
                        float pr = Mathf.Clamp01((simTime - pressAt) / 1.0f);
                        comms.SetMarkState(pr, confirming: false);
                        commsLogic.SetGuideWanted(true);
                        if (pr >= 1f)
                        {
                            comms.Deliver(CommsNotice.MarkLogged);
                            markDone = true;
                            events.Append(F(simTime)).Append("\tcomms_deliver\tMarkLogged (press)\n");
                        }
                    }
                    if (pressAt >= 0f && markDone && !releaseDone && simTime >= pressAt + 1.3f)
                    {
                        releaseDone = true;
                        comms.SetMarkState(0f, confirming: false);
                        commsLogic.SetGuideWanted(false);
                        events.Append(F(simTime)).Append("\tpress\trelease\n");
                    }
                    if (!markDone && simTime >= 9f && comms.Stage == CommsStage.Off)
                    {
                        comms.Deliver(CommsNotice.MarkLogged);
                        markDone = true;
                        events.Append(F(simTime)).Append("\tcomms_deliver\tMarkLogged\n");
                    }
                    if (!takeoverDone && takeoverAt >= 0f && simTime >= takeoverAt && comms.Stage == CommsStage.Off)
                    {
                        comms.SetDecayForPreview(1f, 0f);
                        comms.Deliver(CommsNotice.Takeover);
                        var errorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Effects/UnauthorizedAccess.prefab");
                        takeover = commsGo!.GetComponent<CommsTakeoverError>() ?? commsGo.AddComponent<CommsTakeoverError>();
                        takeover.Configure(comms, errorPrefab);
                        takeover.ConfigureScreen(screen.transform, new Vector2(2.7f, 1.51875f));
                        takeover.Tick(comms.TakeoverVisible, 0f);
                        takeoverDone = true;
                        events.Append(F(simTime)).Append("\tcomms_deliver\tTakeover\n");
                    }
                    commsLogic.Tick(dt);
                    commsApply.Invoke(comms, new object[] { commsLogic.Weights });
                    takeover?.Tick(comms.TakeoverVisible, dt);

                    // 打鍵（本番は見える字 1 つにつき 1 回鳴る）と段の遷移を時刻で残す
                    int chars = comms.VisibleChars;
                    if (chars > lastChars)
                        events.Append(F(simTime)).Append("\ttype\tchars=").Append(chars - lastChars).Append('\n');
                    lastChars = chars;
                    if (comms.Stage != lastStage)
                        events.Append(F(simTime)).Append("\tcomms_stage\t").Append(comms.Stage).Append('\n');
                    lastStage = comms.Stage;

                    string commsBody = (commsTextField?.GetValue(comms) as TMPro.TMP_Text)?.text ?? "";
                    string textId = commsBody.Length == 0 ? "-" : commsBody.Substring(0, Mathf.Min(4, commsBody.Length)).Replace("\n", " ");
                    float glyphA = comms.AppliedGlyph;
                    float textAlpha = chars > 0 && glyphA > 0.002f ? glyphA : 0f;
                    float commsBand = bandField != null ? (float)bandField.GetValue(comms) : 0f;
                    commsTsv.Append(tick).Append('\t').Append(F(simTime)).Append('\t').Append(comms.Stage).Append('\t')
                            .Append(F(glyphA)).Append('\t').Append(chars).Append('\t').Append(textId).Append('\t')
                            .Append(F(textAlpha)).Append('\t').Append(F(comms.AppliedPanelAlpha)).Append('\t')
                            .Append(F(comms.AppliedBody)).Append('\t').Append(F(commsBand)).Append('\t')
                            .Append(F(commsBand * comms.AppliedBody)).Append('\t').Append(F(commsLogic.Weights.hint)).Append('\n');
                }

                // 音の発火（ShowSoundDirector と同じ閾値）を時刻で残す。音そのものは聴けない。
                if (director.Dipping && !wasDipping) events.Append(F(simTime)).Append("\tsfx_switch\tdipStart\n");
                wasDipping = director.Dipping;
                float gl = mat.GetFloat("_Glitch");
                if (gl > 0.01f && glitchOnSince < 0f) glitchOnSince = simTime;
                if (gl <= 0.01f && glitchOnSince >= 0f)
                {
                    events.Append(F(simTime)).Append("\tglitch_visual_end\tdur=").Append(F(simTime - glitchOnSince)).Append('\n');
                    glitchOnSince = -1f;
                }
                if (glitchArmed && gl >= 0.15f && simTime - lastGlitchSfx >= 0.30f)
                {
                    events.Append(F(simTime)).Append("\tsfx_glitch\tlevel=").Append(F(gl)).Append('\n');
                    lastGlitchSfx = simTime;
                    glitchArmed = false;
                }
                if (gl < 0.06f) glitchArmed = true;

                // 3) 終幕
                float collapse = 0f, power = 1f, report = 0f;
                if (outroStarted)
                {
                    outroLogic.Tick(dt);
                    // 材質へは本番（OutroDirector）と同じ生の値を書く。潰れ終わると 0 に戻り、電力 0 が隠す。
                    // 記録は「潰れの段を過ぎたら 1」にする（一方向かを数値で見るため）。
                    float rawCollapse = outroLogic.ScreenCollapse;
                    collapse = outroLogic.Stage == OutroStage.Noise ? 0f
                             : outroLogic.Stage == OutroStage.Collapse ? rawCollapse : 1f;
                    power = outroLogic.ScreenPower;
                    report = outroLogic.ReportAlpha;
                    mat.SetFloat("_ScreenCollapse", rawCollapse);
                    mat.SetFloat("_ScreenNoise", outroLogic.ScreenNoise);
                    mat.SetFloat("_ScreenPower", power);
                    if (report > 0f)
                    {
                        if (!reportShown)
                        {
                            outroReport.PresentPreview(12, 8, ShowEndingOutcome.Released, ShowLang.Ja);
                            SetLayerRecursive(reportGo, ScreenLayer);
                            reportShown = true;
                        }
                        Refl.Invoke(outroReport, "ApplyAlpha", report);
                        foreach (var f in outroReport.Fields) f.ForceMeshUpdate(true, true);
                    }
                    if (outroLogic.Stage == OutroStage.Done || report >= 0.999f) endHold += dt;
                    if (endHold > 1.5f) break;
                }

                // 4) スクリーンは頭のヨーを本番の式で追う
                float screenYaw = yaw.Step(bodyYaw, dt, HeadYawFollow.YawDeadzoneDeg, HeadYawFollow.YawTrailDeg,
                    HeadYawFollow.SmoothTimeSec, HeadYawFollow.MaxYawSpeedDegPerSec,
                    HeadYawFollow.CatchUpThresholdDeg, HeadYawFollow.CatchUpBoost);
                Quaternion sy = Quaternion.Euler(0f, screenYaw, 0f);
                Vector3 sp = eyePos + sy * Vector3.forward * ScreenDistM;
                sp.y = eyePos.y - ScreenDropM;
                screen.transform.SetPositionAndRotation(sp, sy);
                reportGo.transform.SetPositionAndRotation(eyePos, sy);
                if (commsGo != null)
                {
                    // CommsPanel の既定（頭から 1.5m・外へ 8°・下へ 17°。ヨーはスクリーンと同じ追従）
                    Vector3 cdir = Quaternion.Euler(17f, screenYaw - 8f, 0f) * Vector3.forward;
                    commsGo.transform.SetPositionAndRotation(eyePos + cdir * 1.5f, Quaternion.LookRotation(cdir));
                }

                tsv.Append(tick).Append('\t').Append(F(simTime)).Append('\t')
                   .Append(F(pos.x)).Append('\t').Append(F(pos.y)).Append('\t')
                   .Append(F(bodyYaw)).Append('\t').Append(F(screenYaw)).Append('\t')
                   .Append(pick >= 0 ? zones[pick].CameraIndex : -1).Append('\t').Append(registry.ActiveIndex).Append('\t')
                   .Append(director.Dipping ? 1 : 0).Append('\t').Append(F(mat.GetFloat("_SwitchDim"))).Append('\t')
                   .Append(F(gl)).Append('\t').Append(glitch.Count).Append('\t')
                   .Append(F(collapse)).Append('\t').Append(F(power)).Append('\t').Append(F(report)).Append('\n');

                // 5) 30fps で書き出す（数値は 90Hz で残っている）
                if (tick % OutEvery == 0)
                {
                    int active = Mathf.Clamp(registry.ActiveIndex, 0, camCount - 1);
                    View v = views[active];
                    if (!v.plate && roomCam != null)
                    {
                        roomCam.transform.SetPositionAndRotation(v.pos, v.rot);
                        roomCam.projectionMatrix = v.proj;
                        roomCam.targetTexture = (RenderTexture)v.live!;
                        roomCam.Render();
                        roomCam.targetTexture = null;
                    }
                    cgCam.transform.SetPositionAndRotation(v.pos, v.rot);
                    cgCam.projectionMatrix = v.proj;
                    cgCam.targetTexture = cgRt;
                    cgCam.Render();
                    cgCam.targetTexture = null;

                    mat.SetFloat("_SrcFrame", frame);
                    if (commsGo != null) SetLayerRecursive(commsGo, ScreenLayer);
                    foreach (Transform child in screen.transform) SetLayerRecursive(child.gameObject, ScreenLayer);
                    osd.Suppressed = collapse > 0.12f;
                    osd.Tick(OsdStart.AddSeconds(simTime));

                    eye.transform.SetPositionAndRotation(eyePos, Quaternion.Euler(EyePitchDeg, bodyYaw, 0f));
                    void Shoot(string path, bool png)
                    {
                        eye.targetTexture = eyeRt;
                        eye.Render();
                        eye.targetTexture = null;
                        RenderTexture.active = eyeRt;
                        readTex.ReadPixels(new Rect(0, 0, eyeW, eyeH), 0, 0);
                        readTex.Apply();
                        RenderTexture.active = null;
                        File.WriteAllBytes(path, png ? readTex.EncodeToPNG() : readTex.EncodeToJPG(90));
                    }
                    Shoot(Path.Combine(outDir, $"f{frame:00000}.jpg"), false);
                    frame++;

                    // 乱れの見た目を強さを揃えて比べる静止画（0055 の中盤 0.32 と上限 0.6・組み替え 3 通り）。
                    // 見た目を変えたら変更前後で 1 回ずつ走らせ、同じ名前どうしを並べる。
                    if (glitchStills && !stillsDone && simTime >= 4.0f)
                    {
                        float keepG = mat.GetFloat("_Glitch"), keepS = mat.GetFloat("_GlitchSeed");
                        string sd = Path.Combine(outDir, "stills");
                        Directory.CreateDirectory(sd);
                        foreach (float lvl in new[] { 0.32f, 0.6f })
                            for (int k = 0; k < 3; k++)
                            {
                                mat.SetFloat("_Glitch", lvl);
                                mat.SetFloat("_GlitchSeed", 10f + k * 0.37f);
                                Shoot(Path.Combine(sd, $"glitch_{lvl:0.00}_{k}.png"), true);
                            }
                        mat.SetFloat("_Glitch", 0f);
                        Shoot(Path.Combine(sd, "glitch_none.png"), true);
                        mat.SetFloat("_Glitch", keepG);
                        mat.SetFloat("_GlitchSeed", keepS);
                        stillsDone = true;
                    }
                }
                tick++;
                simTime += dt;
            }

            File.WriteAllText(Path.Combine(outDir, "frames.tsv"), tsv.ToString());
            File.WriteAllText(Path.Combine(outDir, "events.tsv"), "t\tkind\tdetail\n" + events);
            if (comms != null) File.WriteAllText(Path.Combine(outDir, "comms.tsv"), commsTsv.ToString());
            string plates = string.Join(" ", views.Select(v => v.id + (v.plate ? ":plate" : ":room")));
            Debug.Log($"[VirtualWalk] {frame} コマ（30fps）/ {tick} 刻み（90Hz）/ {simTime:F1} 秒 → {outDir.Replace('\\', '/')}\n"
                    + $"  show.json: {showPath.Replace('\\', '/')} / 道筋 {routeLog} / 周回 {laps} / 滞在 {dwellSec:F1}s\n"
                    + $"  映像 {plates} / 乱れ {glitch.Count} 回\n"
                    + "  ⚠ 圧縮・伝送の痩せ・遅延・両眼視差は入っていない。見るのは時刻・順序・曲線・重なり");
            Debug.Log("[VirtualWalk] OUT " + outDir.Replace('\\', '/'));
        }

        // ---------- 部品 ----------

        private static View MakeView(Cam def, string projectRoot)
        {
            var v = new View { id = def.id };
            ShowCameraCalibDef? c = def.hasCalib && def.calib != null && def.calib.IsUsable()
                                    && def.calib.srcW == SrcW && def.calib.srcH == SrcH ? def.calib : null;
            if (c != null)
            {
                v.pos = new Vector3(c.x, c.y, c.z);
                v.rot = Quaternion.Euler(-c.pitchDeg, c.yawDeg, c.rollDeg);
                v.proj = ShowCgLayer.BuildProjectionMatrix(c.fxPx, c.fyPx, c.cxPx, c.cyPx, c.srcW, c.srcH, 0.05f, 30f);
                v.lens = new Vector4(c.k1, c.cxPx / c.srcW, c.cyPx / c.srcH, 0f);
                v.focal = new Vector4(Mathf.Max(1e-4f, c.fxPx / c.srcW), Mathf.Max(1e-4f, c.fyPx / c.srcH), 0f, 0f);
            }
            else if (def.hasPose && def.pose != null)
            {
                ShowCameraPoseDef p = def.pose;
                v.pos = new Vector3(p.x, p.y, p.z);
                v.rot = Quaternion.Euler(-p.pitchDeg, p.yawDeg, 0f);
                float vf = Camera.HorizontalToVerticalFieldOfView(p.hfovDeg > 1f ? p.hfovDeg : 70f, SrcW / (float)SrcH);
                v.proj = Matrix4x4.Perspective(vf, SrcW / (float)SrcH, 0.05f, 30f);
            }

            string dir = Path.Combine(projectRoot, "Assets", "StreamingAssets", "show", "assets");
            string? file = Directory.Exists(dir)
                ? Directory.GetFiles(dir, $"plate_{def.id}_*.jpg").OrderBy(f => f, StringComparer.Ordinal).LastOrDefault()
                : null;
            if (file != null && c != null)
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGB24, true) { name = Path.GetFileName(file) };
                if (tex.LoadImage(File.ReadAllBytes(file), false) && tex.width == SrcW && tex.height == SrcH)
                {
                    tex.filterMode = FilterMode.Trilinear;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    tex.Apply(true);
                    _trash.Add(tex);
                    v.live = tex;
                    v.plate = true;
                    return v;
                }
                UnityEngine.Object.DestroyImmediate(tex);
            }
            return v;
        }

        /// <summary>壁の遮蔽（深度だけ書く）。本番の ShowRoomProxy と同じ材質。</summary>
        private static void BuildOccluders(ShowLayoutDef layout, float floorY, int cgLayer)
        {
            Material? occ = Resources.Load<Material>(ShowRoomProxy.OccluderMaterialResource);
            if (occ == null) { Debug.LogWarning("[VirtualWalk] 遮蔽の材質が無い（人が壁の後ろでも写る）"); return; }
            ShowRoomWallDef[]? walls = layout.room?.walls;
            if (walls == null) return;
            foreach (ShowRoomWallDef? wd in walls)
            {
                if (wd == null || !wd.IsUsable()) continue;
                var a = new Vector2(wd.x1, wd.z1);
                var b = new Vector2(wd.x2, wd.z2);
                Vector2 mid = (a + b) * 0.5f;
                GameObject g = GameObject.CreatePrimitive(PrimitiveType.Cube);
                g.name = "Occluder " + wd.id;
                g.layer = cgLayer;
                g.transform.SetPositionAndRotation(new Vector3(mid.x, floorY + wd.h * 0.5f, mid.y),
                    Quaternion.Euler(0f, Mathf.Atan2(b.x - a.x, b.y - a.y) * Mathf.Rad2Deg, 0f));
                g.transform.localScale = new Vector3(Mathf.Max(wd.thick, 0.02f), wd.h, Vector2.Distance(a, b));
                g.GetComponent<Renderer>().sharedMaterial = occ;
                UnityEngine.Object.DestroyImmediate(g.GetComponent<Collider>());
                _trash.Add(g);
            }
        }

        /// <summary>CG の照明（layout.room.light）。プレートの光に寄せるのは本番の ShowCgLayer と同じ考え。</summary>
        private static void LightCg(ShowLayoutDef layout)
        {
            var go = new GameObject("CgSun");
            _trash.Add(go);
            var l = go.AddComponent<Light>();
            l.type = LightType.Directional;
            float yawDeg = 30f, pitchDeg = 70f, intensity = 0.9f, ambient = 0.45f;
            ShowRoomDef? room = layout.room;
            if (room != null && room.hasLight && room.light != null)
            {
                yawDeg = room.light.yawDeg; pitchDeg = room.light.pitchDeg;
                intensity = room.light.intensity; ambient = room.light.ambient;
            }
            go.transform.rotation = Quaternion.Euler(pitchDeg, yawDeg, 0f);
            l.intensity = intensity;
            l.color = new Color(1f, 0.96f, 0.9f);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(ambient, ambient, ambient);
        }

        private static void ApplyPost(Material mat, PostParams? p)
        {
            if (p == null) return;
            mat.SetFloat("_Exposure", p.exposure);
            mat.SetFloat("_Contrast", p.contrast);
            mat.SetFloat("_Saturation", p.saturation);
            mat.SetFloat("_Temperature", p.temperature);
            mat.SetFloat("_Vignette", p.vignette);
            mat.SetFloat("_Grain", p.grain);
            mat.SetFloat("_Scanline", p.scanline);
            mat.SetFloat("_Lift", p.lift);
            mat.SetFloat("_Tint", p.tint);
            mat.SetFloat("_Aberration", p.aberration);
            mat.SetFloat("_Pixelate", p.pixelate);
            mat.SetFloat("_ScanlineCount", p.ResolveScanlineCount());
        }

        private static ZonePickLogic.Box[] BuildZones(ShowLayoutDef layout, out float hyst)
        {
            ShowGridDef g = layout.grid!;
            float w = layout.floor != null && layout.floor.w > 0f ? layout.floor.w : 1.8f;
            float d = layout.floor != null && layout.floor.d > 0f ? layout.floor.d : 1.8f;
            var input = new ZoneLayoutSolver.GridLayoutInput
            {
                floorW = w, floorD = d, overlapM = layout.overlapM, hysteresisM = layout.hysteresisM,
                tileM = g.tileM, cols = g.cols, rows = g.rows,
                cells = ZoneLayoutSolver.ParseGridCells(g.cells, g.rows, g.cols),
            };
            List<ZoneLayoutSolver.ZoneRect> rects = ZoneLayoutSolver.SolveGrid(input);
            hyst = ZoneLayoutSolver.ClampHysteresis(layout.hysteresisM, layout.overlapM);
            var boxes = new ZonePickLogic.Box[rects.Count];
            for (int i = 0; i < rects.Count; i++)
            {
                ZoneLayoutSolver.ZoneRect r = rects[i];
                boxes[i] = ZonePickLogic.Box.Aabb(r.centerX, 1.5f, r.centerZ, r.halfX, 2f, r.halfZ, r.cameraIndex, r.priority);
            }
            return boxes;
        }

        private static Root? LoadShow(string projectRoot, out string used)
        {
            used = "(なし)";
            string? explicitPath = EditorCliArgs.Get("show");
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(explicitPath)) candidates.Add(explicitPath!);
            candidates.Add(Path.Combine(projectRoot, "tools", "web-compositor", "show.json"));
            candidates.Add(Path.Combine(projectRoot, "Assets", "StreamingAssets", "show", "show.json"));
            foreach (string p in candidates)
            {
                if (!File.Exists(p)) continue;
                try
                {
                    var r = JsonUtility.FromJson<Root>(File.ReadAllText(p));
                    if (r?.layout == null) continue;
                    used = p;
                    return r;
                }
                catch (Exception e) { Debug.LogWarning($"[VirtualWalk] {p} を読めない: {e.Message}"); }
            }
            return null;
        }

        private static float MeasureHeight(GameObject go)
        {
            bool any = false;
            var b = new Bounds();
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>())
            {
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return any ? b.size.y : 0f;
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform c in go.transform) SetLayerRecursive(c.gameObject, layer);
        }

        private static float YawTo(Vector2 a, Vector2 b) => Mathf.Atan2(b.x - a.x, b.y - a.y) * Mathf.Rad2Deg;

        private static int ArgInt(string key, int def)
            => int.TryParse(EditorCliArgs.Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : def;

        private static float ArgFloat(string key, float def)
            => float.TryParse(EditorCliArgs.Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : def;

        private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);

        /// <summary>EditMode で MonoBehaviour を手で回す（SwitchWiringTests と同じ流儀）。</summary>
        private static class Refl
        {
            private const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

            public static void Set(object target, string field, object? value)
            {
                FieldInfo? f = target.GetType().GetField(field, BF);
                if (f == null) throw new MissingFieldException(target.GetType().Name, field);
                f.SetValue(target, value);
            }

            public static object Get(object target, string field)
            {
                FieldInfo? f = target.GetType().GetField(field, BF);
                if (f == null) throw new MissingFieldException(target.GetType().Name, field);
                return f.GetValue(target)!;
            }

            public static void Call(object target, string method)
                => target.GetType().GetMethod(method, BF, null, Type.EmptyTypes, null)?.Invoke(target, null);

            public static void Invoke(object target, string method, float arg)
            {
                MethodInfo? m = target.GetType().GetMethod(method, BF, null, new[] { typeof(float) }, null);
                if (m == null) throw new MissingMethodException(target.GetType().Name, method);
                m.Invoke(target, new object[] { arg });
            }
        }
    }
}
