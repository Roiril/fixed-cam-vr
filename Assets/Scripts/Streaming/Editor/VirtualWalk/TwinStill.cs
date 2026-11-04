#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace FixedCamVr.Streaming.EditorTools.VirtualWalk
{
    /// <summary>
    /// デジタルツインの静止画（見た目の監査用・掲載用）。仮想体験と同じ部屋・同じ体験者の代役・同じ仕上げで、
    /// 決まったアングルを 1 回の起動でまとめて撮る。
    ///
    /// 使い方: <c>.\tools\unity.ps1 menu twin -Set tag=x</c>
    /// → <c>G:\マイドライブ\shubie\renders\twin\&lt;時刻&gt;_&lt;tag&gt;\</c> に hero / wide / low / close / overhead の PNG。
    /// 値: <c>w</c>（幅 既定 1920）・<c>npc=x;z</c>（立ち位置 m）・<c>yaw</c>（体の向き 度）・<c>t</c>（埃と霞の時刻 秒）・<c>cam</c>（いま映っているカメラ）。
    /// </summary>
    public static class TwinStill
    {
        private static readonly List<Object> _trash = new();

        [MenuItem("Tools/FixedCamVr/Diagnostics/Digital Twin Stills", priority = 241)]
        public static void Run()
        {
            int width = ArgInt("w", 1920);
            int height = width * 9 / 16;
            string tag = EditorCliArgs.Get("tag") ?? "";
            float time = ArgFloat("t", 12f);
            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string outRoot = EditorCliArgs.Get("out") ?? @"G:\マイドライブ\shubie\renders\twin";
            string outDir = Path.Combine(outRoot, tag.Length > 0 ? stamp + "_" + tag : stamp);

            VirtualWalkRun.Root? show = VirtualWalkRun.LoadShow(projectRoot, out string showPath);
            if (show?.layout == null || show.cameras == null)
            {
                Debug.LogError($"[TwinStill] show.json に layout / cameras が無い（{showPath}）");
                return;
            }
            Directory.CreateDirectory(outDir);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            try
            {
                Shoot(show, projectRoot, outDir, width, height, time);
            }
            finally
            {
                foreach (Object o in _trash) if (o != null) Object.DestroyImmediate(o);
                _trash.Clear();
            }
        }

        private static void Shoot(VirtualWalkRun.Root show, string projectRoot, string outDir, int width, int height, float time)
        {
            ShowLayoutDef layout = show.layout!;
            float floorY = layout.room != null ? layout.room.floorY : 0f;
            int cgLayer = LayerMask.NameToLayer("ShowCg");
            var views = show.cameras!.Take(3).Select(c => VirtualWalkRun.MakeView(c, projectRoot)).ToList();
            using VirtualRoom twin = VirtualRoom.Build(layout, floorY, VirtualWalkRun.Marks(views), _trash);
            twin.ZoneFill = false;
            twin.ZoneBorders = false;
            twin.FrustumFaces = false;
            // low: A の三脚が右端で切れないよう、電球を見上げる向きを左へ 3.5 度
            static Vector3 LowTarget(Vector3 pos, Vector3 target) => pos + Quaternion.Euler(0f, -3.5f, 0f) * (target - pos);
            twin.FootprintsActiveOnly = true;
            twin.SetActiveCamera(ArgInt("cam", 0));

            GameObject? npc = VirtualWalkRun.SpawnVisitor(cgLayer, _trash, out VisitorAvatar? spawned, out Transform hmd);
            if (npc == null || spawned == null) return;
            using VisitorAvatar avatar = spawned;
            twin.SetActor(npc);
            Vector2 at = ArgVec2("npc", new Vector2(0.18f, -0.12f));
            float yaw = ArgFloat("yaw", 205f);
            Quaternion body = Quaternion.Euler(0f, yaw, 0f);
            npc.transform.SetPositionAndRotation(new Vector3(at.x, floorY, at.y), body);
            avatar.Apply(ArgFloat("stride", 0.36f), 1f);   // 前の踵が着き、脚が V の字に開いたコマ
            // HMD を被った人が周りを探す動き: 頭を少し横へ
            float look = ArgFloat("look", 30f);
            avatar.Head.rotation = Quaternion.AngleAxis(look, Vector3.up) * avatar.Head.rotation;
            // 見えないまま探す手: 片腕を腰の高さで前へ、肘を少し曲げる（撮る間だけ）
            Vector3 bodyRight = body * Vector3.right;
            foreach (Transform bone in npc.GetComponentsInChildren<Transform>(true))
            {
                if (bone.name.EndsWith("RightArm", StringComparison.Ordinal))
                    bone.rotation = Quaternion.AngleAxis(-48f, bodyRight) * Quaternion.AngleAxis(12f, body * Vector3.forward) * bone.rotation;
                else if (bone.name.EndsWith("RightForeArm", StringComparison.Ordinal))
                    bone.rotation = Quaternion.AngleAxis(-22f, bodyRight) * bone.rotation;
            }
            // 指の曲げを戻す（歩きのクリップの握り拳が「輪を作った手」に見える）
            foreach (Transform bone in npc.GetComponentsInChildren<Transform>(true))
            {
                string n = bone.name;
                if (n.Contains("Hand") && (n.Contains("Index") || n.Contains("Middle") || n.Contains("Ring") || n.Contains("Pinky")))
                    bone.localRotation = Quaternion.Slerp(bone.localRotation, Quaternion.identity, 0.7f);
            }
            Quaternion face = Quaternion.AngleAxis(look, Vector3.up) * body;
            hmd.SetPositionAndRotation(avatar.Head.position + Vector3.up * 0.085f + face * Vector3.forward * 0.10f, face);

            Vector3 f = twin.Focus;
            Vector3 away = new Vector3(-f.x, 0f, -f.z).normalized;
            Vector3 sideways = Vector3.Cross(Vector3.up, away);
            var head = new Vector3(at.x, floorY + 1.55f, at.y);
            var lens = new TwinLens("Twin still", width, height, TwinLens.Grade.Cinematic, _trash);
            // 固定カメラの画面に、そのカメラがいま撮っている体験者を流す（配信の映像が HMD の中の体験になる）
            int feedCam = ArgInt("cam", 0);
            var marks = VirtualWalkRun.Marks(views);
            if (feedCam >= 0 && feedCam < marks.Count)
            {
                var feed = new TwinLens("Feed", 320, 240, TwinLens.Grade.Plan, _trash);
                twin.Feed(feed, marks[feedCam], time, 0);
                twin.SetScreenFeed(feedCam, feed.Output);
            }
            // 主画はブースの対角線（固定カメラ A の三脚が立つ線）から 40 度回り込む
            // 主画: wide と反対の対角（固定カメラ B の側）から、低く。照らされた L の内側を正面から見る
            Vector3 heroPos = f + Quaternion.Euler(0f, 34f, 0f) * away * 3.8f + Vector3.up * -0.22f;
            // 近接: L の角の反対側から、人の背後にブースの角と電球の光が来るように。頭は上 1/3、腰まで入る
            Vector3 fromCorner = new Vector3(head.x - f.x, 0f, head.z - f.z).normalized;
            Vector3 closePos = head + Quaternion.Euler(0f, ArgFloat("closeorbit", 40f), 0f) * fromCorner * 2.3f - Vector3.up * 0.12f;
            var shots = new List<(string name, Vector3 pos, Vector3 target, float fov)>
            {
                // 掲載の主画: 電球・L の角・人を 1 枚に。人は左 1/3、電球は右寄りの上、足元に床を残す
                ("hero", heroPos, heroPos + Quaternion.Euler(0f, 3.5f, 0f) * (f + Vector3.up * 0.34f - Vector3.Cross(Vector3.up, (f - heroPos).normalized) * 0.55f - heroPos), 50f),
                // 仮想体験の「立体」と同じ画角（walk の observer.mp4）
                ("wide", f + Quaternion.Euler(0f, -24f, 0f) * away * 4.54f + Vector3.up * 0.32f, f + Vector3.up * 0.18f, 48f),
                // 床すれすれから電球を見上げる（固定カメラの三脚から離れた側）
                ("low", f + away * 2.6f + sideways * 1.1f + Vector3.up * -0.8f, LowTarget(f + away * 2.6f + sideways * 1.1f + Vector3.up * -0.8f, twin.Bulb - Vector3.up * 0.75f), 52f),
                // 体験者の肩越し（HMD とブースの角と光の筋）
                ("close", closePos, closePos + Quaternion.Euler(0f, ArgFloat("closeyaw", 6f), 0f) * (head - Vector3.up * 0.3f - Vector3.Cross(Vector3.up, (head - closePos).normalized) * 0.5f - closePos), 40f),
            };
            foreach ((string name, Vector3 pos, Vector3 target, float fov) in shots)
            {
                twin.Footprints = name == "wide";
                twin.FloorMarks = name != "close";
                // wide: B の三脚は左の闇に沈み、札と線だけが宙に浮く
                int bIndex = marks.FindIndex(mk => mk.Id == "B");
                twin.HideMarkAt = name == "wide" && bIndex >= 0 ? marks[bIndex].Position : null;
                lens.Aim(pos, target, fov);
                lens.Shoot(twin, time, 0, Path.Combine(outDir, name + ".png"));
            }
            twin.HideMarkAt = null;
            // 固定カメラの肩越し（rec）: 画面に映る体験者と、奥にいる本人を 1 枚に（配信の映像が HMD の中の体験になる）。
            // 撮る瞬間だけ体験者をカメラの視線の奥（ブースの角寄り）へ立たせ直し、画面の中の全身と奥の本人の大きさを対応させる
            if (feedCam >= 0 && feedCam < marks.Count)
            {
                VirtualRoom.CameraMark m = marks[feedCam];
                Vector3 mf = m.Rotation * Vector3.forward, mr = m.Rotation * Vector3.right;
                Vector3 flat = new Vector3(mf.x, 0f, mf.z).normalized;
                // 電球の真下に立たせると正面から照らされて作り物らしさが出る。電球の手前（逆光）に立たせる
                // A と電球を結ぶ線の上、A から 1.5m（電球を背にした逆光。A の画に頭から足まで入る距離）
                Vector3 toBulbFlat = new Vector3(twin.Bulb.x - m.Position.x, 0f, twin.Bulb.z - m.Position.z);
                Vector3 toBulb = toBulbFlat.normalized;
                // 電球の手前（A の側）に立たせる。電球の真下より奥だと、正面から照らされて作り物に見える
                Vector3 recAt = new Vector3(m.Position.x, floorY, m.Position.z) + toBulb * (toBulbFlat.magnitude * ArgFloat("recfrac", 0.75f));
                Debug.Log($"[TwinStill] rec: A から電球まで水平 {toBulbFlat.magnitude:F2}m、人は {toBulbFlat.magnitude * ArgFloat("recfrac", 0.75f):F2}m");
                Vector3 conduitShift = new Vector3(0f, 0f, -0.45f);
                twin.ShiftConduit(conduitShift);
                Vector3 shift = recAt - npc.transform.position;
                npc.transform.position += shift;
                hmd.position += shift;
                var recFeed = new TwinLens("Feed", 320, 240, TwinLens.Grade.Plan, _trash);
                recFeed.Camera.transform.SetPositionAndRotation(m.Position, m.Rotation);
                recFeed.Camera.fieldOfView = m.VerticalFovDeg;
                recFeed.Camera.cullingMask = ~((1 << 30) | (1 << VirtualRoom.GizmoLayer) | (1 << WalkEvidence.Layer));
                recFeed.Shoot(twin, time, 0, Path.Combine(outDir, "rec_feed.png"));    // 画面に流している画そのもの（照合用）
                twin.SetScreenFeed(feedCam, recFeed.Output);
                twin.Frustums = false;
                twin.FineGrid = false;
                // スマホを左 1/3、人物を右 1/3 に置く対角の構図（カメラはスマホの右後ろ、少し上）
                // スマホの右後ろ 45cm から、視線を左へ 24 度振る: スマホが左 1/3、奥の本人が中央やや右に来る
                Vector3 recCam = m.Position - mf * ArgFloat("recback", 0.3f) + mr * ArgFloat("recright", 0.24f) + Vector3.up * ArgFloat("recup", -0.02f);
                Vector3 recLook = Quaternion.AngleAxis(ArgFloat("recyaw", -15f), Vector3.up) * flat;
                lens.Aim(recCam, recCam + recLook * 2f + Vector3.up * ArgFloat("recpitch", 0.46f), ArgFloat("recfov", 56f));
                // ピントはスマホの画面。奥の本人と部屋は少しぼける（画面の中の像が主役）
                lens.DofFocus = Vector3.Distance(recCam, m.Position);
                twin.FillScale = ArgFloat("recfill", 0.2f);
                twin.HazeScale = ArgFloat("rechaze", 0.5f);
                twin.ActorShade = ArgFloat("recshade", 0.6f);
                // 頭と HMD の影が霞の中に縦の柱を作り、背中に暗い筋として乗る。この画では体の影を落とさない
                // （体の影が落ちる床は、手前のぼけの中で画に入らない）
                var castBack = new List<(Renderer, ShadowCastingMode)>();
                foreach (Renderer r in npc.GetComponentsInChildren<Renderer>()) { castBack.Add((r, r.shadowCastingMode)); r.shadowCastingMode = ShadowCastingMode.Off; }
                foreach (Renderer r in hmd.GetComponentsInChildren<Renderer>()) { castBack.Add((r, r.shadowCastingMode)); r.shadowCastingMode = ShadowCastingMode.Off; }
                lens.Shoot(twin, time, 0, Path.Combine(outDir, "rec.png"));
                lens.DofFocus = 0f;
                twin.FillScale = 1f;
                twin.HazeScale = 1f;
                twin.ActorShade = 1f;
                foreach ((Renderer r, ShadowCastingMode mode) in castBack) r.shadowCastingMode = mode;
                twin.Frustums = true;
                twin.FineGrid = true;
                twin.ShiftConduit(-conduitShift);
                npc.transform.position -= shift;
                hmd.position -= shift;
            }
            // 真上（天井・電球・光の筋は外す）
            var plan = new TwinLens("Twin plan", height * 4 / 3, height, TwinLens.Grade.Plan, _trash);
            Camera top = plan.Camera;
            top.orthographic = true;
            // 3 台の固定カメラとブースを全部囲む範囲を中央に
            var bb = new Bounds(new Vector3(f.x, floorY, f.z), Vector3.zero);
            foreach (VirtualWalkRun.View v in views)
            {
                bb.Encapsulate(new Vector3(v.pos.x - 0.4f, floorY, v.pos.z - 0.4f));   // 三脚の脚まで
                bb.Encapsulate(new Vector3(v.pos.x + 0.4f, floorY, v.pos.z + 0.4f));
            }
            bb.Encapsulate(new Vector3(-0.9f, floorY, -0.9f)); bb.Encapsulate(new Vector3(0.9f, floorY, 0.9f));
            // 奥の壁（-x）を画の上端に置く（画の上 = -x、右 = +z）。L の奥の 2 面が画の上と右の細い帯になり、
            // ブースと 3 台は横長の画面に収まる
            top.orthographicSize = Mathf.Max(bb.extents.x + 0.3f, (bb.extents.z + 0.35f) * 0.75f) * 0.88f;   // 左の空いた床を詰める
            float halfW = top.orthographicSize * 4f / 3f;
            float cz = twin.RoomMax.y + 0.3f - halfW;    // 右端を奥の壁の断面に合わせる
            top.transform.SetPositionAndRotation(new Vector3(bb.center.x, floorY + 5f, cz), Quaternion.Euler(90f, -90f, 0f));
            top.cullingMask = ~((1 << 30) | (1 << VirtualRoom.AtmosphereLayer));
            plan.Shoot(twin, time, 0, Path.Combine(outDir, "overhead.png"));
            Debug.Log($"[TwinStill] {shots.Count + 2} 枚 {width}x{height} → {outDir.Replace('\\', '/')}");
            Debug.Log("[TwinStill] OUT " + outDir.Replace('\\', '/'));
        }

        private static int ArgInt(string key, int def)
            => int.TryParse(EditorCliArgs.Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : def;

        private static float ArgFloat(string key, float def)
            => float.TryParse(EditorCliArgs.Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : def;

        private static Vector2 ArgVec2(string key, Vector2 def)
        {
            string? s = EditorCliArgs.Get(key);
            if (s == null) return def;
            string[] p = s.Split(';');
            return p.Length == 2
                   && float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                   && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)
                ? new Vector2(x, z) : def;
        }
    }
}
