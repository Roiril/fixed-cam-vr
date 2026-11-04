#nullable enable
using System;
using System.Collections.Generic;
using FixedCamVr.Tracking;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace FixedCamVr.Streaming.EditorTools.VirtualWalk
{
    /// <summary>
    /// 仮想体験のデジタルツイン（<see cref="VirtualWalkRun"/> の第三者視点・<see cref="TwinStill"/>）。
    /// <c>layout.room.walls</c> の L 字は実物どおり<b>黒いパイプの枠に吊った灰色のカーテン</b>。
    /// それを、剥がれた青緑の壁とコンクリートの床の廃室に置き、天井から吊った<b>裸電球 1 つ</b>だけで照らす
    /// （光の筋・舞う埃・影は電球の追加ライトそのもの）。床にはゾーンの格子、三脚の配信スマホには視野と録画灯を重ねる。
    ///
    /// ⚠ <b>部屋の光は撮影の瞬間（<see cref="BeginShot"/>）だけ点く。</b>本番の CG 層（人形を実プレートへ重ねる仮想カメラ）は
    /// 部屋の光も霧も受けない — 目の絵（walk.mp4）の合成は本番の照明だけで決まる。
    /// ⚠ 部屋の見た目は実物の会場ではない（展示の作品世界へ寄せた舞台）。寸法の正は <c>layout.room</c> と <c>WalkNavigation</c>。
    /// </summary>
    internal sealed class VirtualRoom : IDisposable
    {
        public const int AtmosphereLayer = 28;  // 天井・電球・光の筋・埃（真上の記録からは外す）
        public const int GizmoLayer = 27;       // ツインの注記と機材（固定カメラの代役の画には写さない）
        private const string AssetDir = "Assets/Editor/VirtualWalkAssets/Twin/";
        private const float RoomH = 3.2f;
        private const float RailY = 0.948f;   // 実物の手すりの高さ（tools/walk-guide の実測）

        public readonly struct CameraMark
        {
            public readonly string Id;
            public readonly Vector3 Position;
            public readonly Quaternion Rotation;
            public readonly float VerticalFovDeg;
            public CameraMark(string id, Vector3 position, Quaternion rotation, float verticalFovDeg)
            { Id = id; Position = position; Rotation = rotation; VerticalFovDeg = verticalFovDeg; }
        }

        private readonly List<Light> _lights = new();
        private readonly List<(Light light, float baseIntensity)> _fills = new();
        /// <summary>電球の横へ漏れる光（漏れ・壁の光だまり）の強さの倍率。逆光の画では下げて、人を輪郭で見せる。</summary>
        public float FillScale = 1f;
        /// <summary>光の筋の霞の濃さの倍率。逆光の画では下げる（人の手前に霞が乗ると、黒い輪郭が灰色になる）。</summary>
        public float HazeScale = 1f;
        private const float HazeDensity = 0.02f;
        private Material? _haze;
        private readonly List<Material> _frustum = new(), _frustumFace = new(), _zoneFill = new(), _screens = new();
        private readonly List<(Material core, Material glow)> _led = new();
        private readonly List<Material> _labels = new();
        private readonly List<(GameObject label, Vector3 body)> _labelObjects = new();
        private readonly List<(GameObject label, Vector3 at)> _floorLabels = new();
        private readonly List<(Vector3 body, GameObject part)> _markParts = new();
        private readonly List<(Vector3 body, GameObject part)> _faceParts = new();
        private readonly List<(int cam, GameObject part)> _footprints = new();
        private readonly List<GameObject> _screenUi = new();
        private int _activeCam = -1;
        private readonly List<GameObject> _conduit = new();
        /// <summary>透視の画で、視野の床の足跡の輪郭を出すか。出すときは、いま映っているカメラの分だけ。</summary>
        public bool Footprints = true;
        private GameObject? _fineGrid;
        /// <summary>透視の画で視野の床の塗りを出すか（動画では出す。静止画では輪郭線だけ）。</summary>
        public bool FrustumFaces = true;
        /// <summary>透視の画で、足跡をいま映っているカメラの分だけにするか（静止画）。</summary>
        public bool FootprintsActiveOnly;
        /// <summary>床の細かい格子を出すか（rec のように床を静かな面にしたい画では外周だけ）。</summary>
        public bool FineGrid = true;
        /// <summary>視野の線を出すか（rec のように画面が視野を示している画では出さない）。</summary>
        public bool Frustums = true;
        private readonly List<(Renderer renderer, MaterialPropertyBlock block)> _actorLook = new();
        private readonly List<Color> _actorColor = new();
        /// <summary>代役の地の色の倍率（逆光の画で胴を黒い輪郭へ沈める。縁の光は照明なので残る）。</summary>
        public float ActorShade = 1f;
        private Texture2D? _clothNormal;

        /// <summary>
        /// 服の織り目と皺の法線（接線空間・AG 詰め）。代役の服は無地の樹脂に見えるので、細かい綾と、
        /// 縦に流れる柔らかい皺を足す。UV は服ごとのアトラスなので、模様の大きさは UV 1 枚に対して決める。
        /// </summary>
        private static Texture2D ClothNormal(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true, true) { name = "Twin cloth normal", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
            var h = new float[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = x / (float)size, v = y / (float)size;
                // 綾（斜めの細かい畝）
                float twill = Mathf.Sin((u * 96f + v * 96f) * Mathf.PI * 2f) * 0.015f;
                // 皺（縦長に伸ばした低周波のゆらぎを 2 段）
                float wr = Mathf.PerlinNoise(u * 9f, v * 2.5f) * 0.7f + Mathf.PerlinNoise(u * 21f + 7.3f, v * 6f + 1.1f) * 0.3f;
                // 大きな皺（脇から腰へ流れる 2〜3 本の斜めの谷）
                float fold = Mathf.Pow(Mathf.Abs(Mathf.Sin((u * 2.6f + v * 0.9f + Mathf.PerlinNoise(u * 3f, v * 3f) * 0.5f) * Mathf.PI * 2f)), 0.6f);
                h[y * size + x] = twill + Mathf.Pow(wr, 1.6f) * 1.4f + fold * 0.9f;
            }
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = h[y * size + (x + 1) % size] - h[y * size + (x + size - 1) % size];
                float dy = h[((y + 1) % size) * size + x] - h[((y + size - 1) % size) * size + x];
                Vector3 n = new Vector3(-dx * 5f, -dy * 5f, 1f).normalized;
                byte nx = (byte)Mathf.RoundToInt((n.x * 0.5f + 0.5f) * 255f), ny = (byte)Mathf.RoundToInt((n.y * 0.5f + 0.5f) * 255f);
                px[y * size + x] = new Color32(255, ny, (byte)Mathf.RoundToInt((n.z * 0.5f + 0.5f) * 255f), nx);
            }
            tex.SetPixels32(px);
            tex.Apply(true, false);
            return tex;
        }
        private Light? _key;
        private Vector2 _corner;
        private readonly List<int> _markCamera = new();
        private readonly float _floorY;
        private PipelineOverride? _pipeline;
        public Vector3 Bulb { get; private set; }
        public Vector3 Focus { get; private set; }
        public Vector2 RoomMin { get; private set; }
        public Vector2 RoomMax { get; private set; }
        /// <summary>いま映っているカメラのゾーンを床に薄く塗るか（動画では切替が読める。静止画では凡例の無い塗りになる）。</summary>
        public bool ZoneFill = true;
        private GameObject? _innerBorders, _outerBorder;
        /// <summary>透視の画で床の表示（格子・ゾーンの外周）を出すか。人に寄った画では床を素の床にする。</summary>
        public bool FloorMarks = true;
        /// <summary>透視の画で札と視野の線を伏せるカメラの位置（本体が闇に沈んで札だけ宙に浮く画）。</summary>
        public Vector3? HideMarkAt;
        private bool Hidden(Vector3 body) => HideMarkAt.HasValue && (body - HideMarkAt.Value).sqrMagnitude < 0.09f;
        /// <summary>カメラどうしのゾーンの境（動画では切替の位置が読める。静止画では凡例の無い線になる）。</summary>
        public bool ZoneBorders { set { if (_innerBorders != null) _innerBorders.SetActive(value); } }

        // 撮影の空気（暗部の青緑・霧）
        private static readonly Color Ambient = new(0.009f, 0.028f, 0.032f);
        private static readonly Color FogColor = new(0.004f, 0.017f, 0.019f);
        private const float FogDensity = 0.045f;

        private VirtualRoom(float floorY) { _floorY = floorY; }

        public static VirtualRoom Build(ShowLayoutDef layout, float floorY, IReadOnlyList<CameraMark> cameras, List<Object> trash)
        {
            var room = new VirtualRoom(floorY) { _pipeline = PipelineOverride.Apply() };
            var root = new GameObject("VirtualRoom (digital twin)");
            trash.Add(root);

            Shader surface = Need("FixedCamVr/Twin/Surface");
            Material wall = Surface(surface, "twin_wall", new Color(1.12f, 1.02f, 0.9f), 0.9f, 3.2f, 0.05f, 16f, 0f, 0.42f, trash);
            wall.SetFloat("_Band", 0.3f);
            wall.SetFloat("_Desat", 0.42f);     // 光の当たった塗膜を灰色がかった青緑に
            wall.SetFloat("_Peel", 1f);
            wall.SetFloat("_PeelScale", 1.9f);
            wall.SetColor("_PeelColor", new Color(0.36f, 0.39f, 0.37f));
            Material ceiling = Surface(surface, "twin_floor", new Color(0.5f, 0.48f, 0.45f), 0.5f, 1.2f, 0f, 8f, 0f, 0f, trash);
            ceiling.SetFloat("_Mold", 1f);
            ceiling.SetFloat("_RingStain", 1f);
            wall.SetFloat("_Mold", 0.6f);
            Material floor = Surface(surface, "twin_floor", new Color(0.86f, 0.9f, 0.9f), 0.45f, 1.6f, 0.10f, 34f, 0f, 0f, trash);
            floor.SetFloat("_Damp", 1f);
            floor.SetFloat("_FloorWear", 1f);
            Material cloth = Surface(surface, "twin_fabric", new Color(0.62f, 0.64f, 0.67f), 2.4f, 2.2f, 0f, 4f, 0.12f, 0.18f, trash);
            Material pipe = Surface(surface, null, new Color(0.016f, 0.016f, 0.018f), 1f, 0f, 0.1f, 18f, 0f, 0f, trash);
            Material brass = Surface(surface, null, new Color(0.10f, 0.075f, 0.045f), 1f, 0f, 0.7f, 40f, 0f, 0f, trash);
            Material phone = Surface(surface, null, new Color(0.02f, 0.021f, 0.024f), 1f, 0f, 0.6f, 90f, 0f, 0f, trash);
            phone.SetFloat("_Rim", 0.35f);
            Material glass = Surface(surface, null, Color.black, 1f, 0f, 0f, 1f, 0f, 0f, trash);
            glass.SetColor("_EmissionColor", new Color(1.15f, 1.08f, 0.95f));
            glass.SetFloat("_EmissionCore", 1f);

            ShowRoomWallDef[] walls = Array.FindAll(layout.room?.walls ?? Array.Empty<ShowRoomWallDef>(), w => w != null && w.IsUsable());

            // L の角（両方の壁の端が集まる点）。電球はその手前に吊り、部屋の奥の 2 面はそのすぐ後ろに立てる
            Vector2 corner = Vector2.zero;
            var ends = new List<Vector2>();
            foreach (ShowRoomWallDef w in walls) { ends.Add(new Vector2(w.x1, w.z1)); ends.Add(new Vector2(w.x2, w.z2)); }
            foreach (Vector2 e in ends)
                if (ends.FindAll(q => (q - e).sqrMagnitude < 0.01f).Count >= 2) corner = e;

            // ---- 部屋の殻（内向きの面。隅と床際に遮蔽を焼く） ----
            // 奥（L の角の側）は実物の部屋の壁（tools/twin/rig.json の room）。開いた側は撮る場所を残す
            float sx = corner.x < 0f ? -1f : 1f, sz = corner.y < 0f ? -1f : 1f;
            Vector2 roomBack = TwinRig.RoomBack;
            float backX = Mathf.Abs(roomBack.x), backZ = Mathf.Abs(roomBack.y);
            const float Open = 4.6f;
            float x0 = sx < 0f ? -backX : -Open, x1 = sx < 0f ? Open : backX;
            float z0 = sz < 0f ? -backZ : -Open, z1 = sz < 0f ? Open : backZ;
            float H = RoomH;
            float Side(Vector3 p) => Mathf.Min(Mathf.Min(p.x - x0, x1 - p.x), Mathf.Min(p.z - z0, z1 - p.z));
            float WallAo(Vector3 p)
            {
                float y = p.y - floorY;
                // 壁の面上の点は Side が 0 なので、隣の壁までの距離（角の暗がり）を別に取る
                float alongX = Mathf.Min(p.x - x0, x1 - p.x), alongZ = Mathf.Min(p.z - z0, z1 - p.z);
                float cornerD = Mathf.Max(alongX, alongZ);
                return Mathf.Clamp(1f - 0.55f * Mathf.Exp(-y / 0.28f) - 0.4f * Mathf.Exp(-(H - y) / 0.4f)
                                   - 0.5f * Mathf.Exp(-cornerD / 0.45f), 0.12f, 1f);
            }
            float FloorAo(Vector3 p)
            {
                float a = 1f - 0.5f * Mathf.Exp(-Side(p) / 0.4f);
                foreach (ShowRoomWallDef w in walls)
                {
                    float d = SegmentDistance(new Vector2(p.x, p.z), new Vector2(w.x1, w.z1), new Vector2(w.x2, w.z2));
                    a -= 0.55f * Mathf.Exp(-Mathf.Max(0f, d - w.thick * 0.5f) / 0.07f) + 0.18f * Mathf.Exp(-d / 0.45f);
                }
                return Mathf.Clamp(a, 0.12f, 1f);
            }
            float W = x1 - x0, D = z1 - z0;
            room.RoomMin = new Vector2(x0, z0);
            room.RoomMax = new Vector2(x1, z1);
            Part(root, "Floor", TwinMeshes.Plane(new Vector3(x0, floorY, z0), Vector3.forward, Vector3.right, D, W, 0.05f, FloorAo, trash, "floor"), floor, 0);
            Part(root, "Ceiling", TwinMeshes.Plane(new Vector3(x0, floorY + H, z0), Vector3.right, Vector3.forward, W, D, 0.4f, WallAo, trash, "ceiling"), ceiling, AtmosphereLayer);
            // U = 横（テクスチャの横）、V = 上（垂れたしみが縦に流れる）
            Part(root, "Wall N", TwinMeshes.Plane(new Vector3(x1, floorY, z1), Vector3.left, Vector3.up, W, H, 0.05f, WallAo, trash, "wallN"), wall, 0);
            Part(root, "Wall S", TwinMeshes.Plane(new Vector3(x0, floorY, z0), Vector3.right, Vector3.up, W, H, 0.05f, WallAo, trash, "wallS"), wall, 0);
            Part(root, "Wall E", TwinMeshes.Plane(new Vector3(x1, floorY, z0), Vector3.forward, Vector3.up, D, H, 0.05f, WallAo, trash, "wallE"), wall, 0);
            Part(root, "Wall W", TwinMeshes.Plane(new Vector3(x0, floorY, z1), Vector3.back, Vector3.up, D, H, 0.05f, WallAo, trash, "wallW"), wall, 0);
            var trim = new List<CombineInstance>();
            float cx = (x0 + x1) * 0.5f, cz = (z0 + z1) * 0.5f;
            foreach ((Vector3 c, Vector3 half) in new[]
                     {
                         (new Vector3(cx, floorY + 0.06f, z1 - 0.008f), new Vector3(W * 0.5f, 0.06f, 0.008f)),
                         (new Vector3(cx, floorY + 0.06f, z0 + 0.008f), new Vector3(W * 0.5f, 0.06f, 0.008f)),
                         (new Vector3(x1 - 0.008f, floorY + 0.06f, cz), new Vector3(0.008f, 0.06f, D * 0.5f)),
                         (new Vector3(x0 + 0.008f, floorY + 0.06f, cz), new Vector3(0.008f, 0.06f, D * 0.5f)),
                     })
                TwinMeshes.AddBox(trim, c, half, Quaternion.identity, trash);
            {
                float px = sx < 0f ? x0 + 0.05f : x1 - 0.05f;
                Vector2 bulbAt = Vector2.Lerp(corner, Vector2.zero, 0.3f);
                float pz = bulbAt.y - sz * 1.25f;    // 画で A の三脚・ブースの柱と重ならない位置
                var conduit = new List<CombineInstance>();
                TwinMeshes.AddCylinder(conduit, new Vector3(px, floorY + 0.12f, pz), new Vector3(px, floorY + H, pz), 0.0175f, 16, trash);
                var rust = new List<CombineInstance>();
                for (float by = 0.45f; by < H - 0.1f; by += 0.6f)
                {
                    TwinMeshes.AddBox(conduit, new Vector3(px - sx * 0.008f, floorY + by, pz), new Vector3(0.028f, 0.011f, 0.024f), Quaternion.identity, trash);
                    // 留め金から下へ垂れる錆（壁面に貼った細い楔）
                    var streak = new List<Vector3>
                    {
                        new(0f, 0f, -0.016f), new(0f, -0.2f - 0.15f * Mathf.PerlinNoise(by, 1.3f), -0.002f), new(0f, 0f, 0.016f),
                    };
                    rust.Add(new CombineInstance { mesh = WallDecal(streak, sx, trash), transform = Matrix4x4.Translate(new Vector3((sx < 0f ? x0 : x1) - sx * 0.0015f, floorY + by - 0.01f, pz)) });
                }
                TwinMeshes.AddBox(conduit, new Vector3(px - sx * 0.006f, floorY + 1.25f, pz + 0.1f), new Vector3(0.032f, 0.065f, 0.05f), Quaternion.identity, trash);
                var conduitMat = new Material(pipe) { name = "Conduit" };
                conduitMat.SetColor("_BaseColor", new Color(0.5f, 0.21f, 0.09f));
                conduitMat.SetFloat("_Spec", 0.55f);
                conduitMat.SetFloat("_Gloss", 28f);
                trash.Add(conduitMat);
                room._conduit.Add(Part(root, "Conduit", TwinMeshes.Combine(conduit, trash, "conduit"), conduitMat, AtmosphereLayer));
                var rustMat = new Material(surface) { name = "Rust streaks" };
                rustMat.SetColor("_BaseColor", new Color(0.66f, 0.26f, 0.08f));
                rustMat.SetFloat("_Cull", 0f);
                trash.Add(rustMat);
                GameObject rustGo = Part(root, "Rust streaks", TwinMeshes.Combine(rust, trash, "rust"), rustMat, AtmosphereLayer);
                rustGo.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
                room._conduit.Add(rustGo);
            }
            Part(root, "Trim", TwinMeshes.Combine(trim, trash, "trim"), pipe, 0);
            var poche = new List<CombineInstance>();
            const float Thick = 2.5f;
            TwinMeshes.AddBox(poche, new Vector3(cx, floorY + H * 0.5f, z1 + Thick * 0.5f + 0.001f), new Vector3(W * 0.5f + Thick, H * 0.5f, Thick * 0.5f), Quaternion.identity, trash);
            TwinMeshes.AddBox(poche, new Vector3(cx, floorY + H * 0.5f, z0 - Thick * 0.5f - 0.001f), new Vector3(W * 0.5f + Thick, H * 0.5f, Thick * 0.5f), Quaternion.identity, trash);
            TwinMeshes.AddBox(poche, new Vector3(x1 + Thick * 0.5f + 0.001f, floorY + H * 0.5f, cz), new Vector3(Thick * 0.5f, H * 0.5f, D * 0.5f), Quaternion.identity, trash);
            TwinMeshes.AddBox(poche, new Vector3(x0 - Thick * 0.5f - 0.001f, floorY + H * 0.5f, cz), new Vector3(Thick * 0.5f, H * 0.5f, D * 0.5f), Quaternion.identity, trash);
            Material cut = Surface(surface, null, new Color(0.006f, 0.012f, 0.013f), 1f, 0f, 0f, 1f, 0f, 0f, trash);
            cut.SetFloat("_LightGain", 0f);
            GameObject pocheGo = Part(root, "Wall section", TwinMeshes.Combine(poche, trash, "poche"), cut, 0);
            pocheGo.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
            var hatch = new List<(Vector3, Vector3, float)>();
            float ox0 = x0 - Thick, ox1 = x1 + Thick, oz0 = z0 - Thick, oz1 = z1 + Thick, hy = floorY + H + 0.002f;
            for (float c = ox0 - (oz1 - oz0); c < ox1; c += 0.12f)
            {
                // x = c + (z - oz0) の斜線を外枠で切り、部屋の内側の区間を抜く
                var A0 = new Vector2(c, oz0);
                var A1 = new Vector2(c + (oz1 - oz0), oz1);
                if (!Clip(ref A0, ref A1, ox0, ox1, oz0, oz1)) continue;
                Vector2 I0 = A0, I1 = A1;
                if (Clip(ref I0, ref I1, x0, x1, z0, z1))
                {
                    if ((I0 - A0).sqrMagnitude > 1e-6f) hatch.Add((new Vector3(A0.x, hy, A0.y), new Vector3(I0.x, hy, I0.y), 1f));
                    if ((A1 - I1).sqrMagnitude > 1e-6f) hatch.Add((new Vector3(I1.x, hy, I1.y), new Vector3(A1.x, hy, A1.y), 1f));
                }
                else hatch.Add((new Vector3(A0.x, hy, A0.y), new Vector3(A1.x, hy, A1.y), 1f));
            }
            Part(root, "Section hatch", TwinMeshes.Lines(hatch, trash, "hatch"),
                 LineMat(Need("FixedCamVr/Twin/Line"), "Section hatch", new Color(0.3f, 0.55f, 0.58f), 0.18f, trash), GizmoLayer);

            // ---- カーテンと枠（layout.room.walls） ----
            var frame = new List<CombineInstance>();
            var posts = new List<Vector2>();
            Vector2 focus = Vector2.zero;
            for (int i = 0; i < walls.Length; i++)
            {
                ShowRoomWallDef w = walls[i];
                var a = new Vector2(w.x1, w.z1);
                var b = new Vector2(w.x2, w.z2);
                var hooks = new List<float>();
                Part(root, "Curtain " + w.id, TwinMeshes.Curtain(a, b, floorY, w.h, 17 + i * 31, trash, "curtain " + w.id, hooks), cloth, 0);
                Vector2 t = (b - a).normalized;
                var n = new Vector2(-t.y, t.x);
                if (Vector2.Dot(n, -(a + b) * 0.5f) < 0f) n = -n;    // 枠の横棒はブースの内側
                const float Off = 0.045f;
                TwinMeshes.AddCylinder(frame, P(a - t * 0.02f, floorY + w.h + 0.015f), P(b + t * 0.02f, floorY + w.h + 0.015f), 0.015f, 14, trash);
                TwinMeshes.AddCylinder(frame, P(a + n * Off, floorY + RailY), P(b + n * Off, floorY + RailY), 0.015f, 14, trash);
                // カーテンを竿に吊るリングとフック（約 10cm おき）
                foreach (float u in hooks)
                {
                    Vector2 q = a + t * u;
                    TwinMeshes.AddCylinder(frame, P(q - t * 0.0025f, floorY + w.h + 0.015f), P(q + t * 0.0025f, floorY + w.h + 0.015f), 0.0195f, 10, trash);
                    TwinMeshes.AddCylinder(frame, P(q, floorY + w.h - 0.006f), P(q, floorY + w.h + 0.001f), 0.0022f, 6, trash);
                }
                // 頭の裏の暗い裏地（たわみの間から光が漏れて床に破線を作らないように）
                TwinMeshes.AddBox(frame, P((a + b) * 0.5f, floorY + w.h - 0.018f), new Vector3(0.0012f, 0.018f, Vector2.Distance(a, b) * 0.5f),
                                  Quaternion.Euler(0f, Mathf.Atan2(t.x, t.y) * Mathf.Rad2Deg, 0f), trash);
                // 中段の横棒を柱へ留める継手
                foreach (Vector2 e in new[] { a, b })
                    TwinMeshes.AddCylinder(frame, P(e, floorY + RailY), P(e + n * (Off + 0.02f), floorY + RailY), 0.019f, 14, trash);
                foreach (Vector2 e in new[] { a, b })
                    if (!posts.Exists(q => (q - e).sqrMagnitude < 0.01f)) posts.Add(e);
                focus += (a + b) * 0.5f;
            }
            foreach (Vector2 e in posts)
            {
                float h = 0f;
                foreach (ShowRoomWallDef w in walls) h = Mathf.Max(h, w.h);
                Vector3 c = P(e, floorY);
                TwinMeshes.AddCylinder(frame, c, c + Vector3.up * (h + 0.05f), 0.016f, 14, trash);
                // 継手（上端と中段）と丸い台座
                TwinMeshes.AddCylinder(frame, c + Vector3.up * (h - 0.02f), c + Vector3.up * (h + 0.055f), 0.021f, 14, trash);
                TwinMeshes.AddCylinder(frame, c + Vector3.up * 0.99f, c + Vector3.up * 1.05f, 0.021f, 14, trash);
                TwinMeshes.AddCylinder(frame, c, c + Vector3.up * 0.016f, 0.085f, 24, trash);
                TwinMeshes.AddCylinder(frame, c + Vector3.up * 0.016f, c + Vector3.up * 0.07f, 0.024f, 14, trash);
            }
            {
                float hMax = 0f;
                foreach (ShowRoomWallDef w in walls) hMax = Mathf.Max(hMax, w.h);
                TwinMeshes.AddBox(frame, new Vector3(corner.x, floorY + hMax * 0.5f, corner.y), new Vector3(0.03f, hMax * 0.5f, 0.03f), Quaternion.identity, trash);
            }
            if (frame.Count > 0) Part(root, "Frame", TwinMeshes.Combine(frame, trash, "frame"), pipe, 0);
            if (walls.Length > 0) focus /= walls.Length;
            // L の角（いちばん奥）とブースの中心の間に電球を吊る
            Vector2 bulbXZ = Vector2.Lerp(corner, Vector2.zero, 0.3f);
            room.Bulb = new Vector3(bulbXZ.x, floorY + 2.55f, bulbXZ.y);
            room.Focus = new Vector3(Mathf.Lerp(corner.x, 0f, 0.55f), floorY + 1.05f, Mathf.Lerp(corner.y, 0f, 0.55f));

            room._corner = corner;
            room.BuildBulb(root, glass, brass, pipe, trash);
            room.BuildFloorDebris(root, surface, corner, x0, x1, z0, z1, floorY, trash);
            // 湿った染み: 人の影が落ちる所（電球から見て人の向こう）を避け、ブースの脇の手前に
            floor.SetVector("_DampCenter", new Vector4(corner.x + 0.45f, floorY, corner.y - 1.35f, 0.62f));
            room.BuildFloorGrid(root, layout, floorY, trash);
            room.BuildCameraMarks(root, cameras, pipe, phone, trash);   // _corner を使う
            RenderSettings.ambientMode = AmbientMode.Flat;
            return room;
        }

        // ---------- 電球・光・空気 ----------

        private void BuildBulb(GameObject root, Material glass, Material brass, Material cord, List<Object> trash)
        {
            float ceilingY = _floorY + RoomH;
            var parts = new List<CombineInstance>();
            TwinMeshes.AddCylinder(parts, Bulb + Vector3.up * 0.075f, new Vector3(Bulb.x, ceilingY - 0.02f, Bulb.z), 0.0045f, 10, trash);
            TwinMeshes.AddCylinder(parts, new Vector3(Bulb.x, ceilingY - 0.022f, Bulb.z), new Vector3(Bulb.x, ceilingY, Bulb.z), 0.045f, 24, trash);
            // 金具は電球のすぐ脇にあり、光源に照らすと真っ白に飛ぶ。直接光は弱めて受ける
            var fixture = new Material(cord) { name = "Bulb fixture" };
            fixture.SetFloat("_LightGain", 0.05f);
            trash.Add(fixture);
            Part(root, "Cord", TwinMeshes.Combine(parts, trash, "cord"), fixture, AtmosphereLayer)
                .GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
            var socket = new List<CombineInstance>();
            TwinMeshes.AddCylinder(socket, Bulb + Vector3.up * 0.032f, Bulb + Vector3.up * 0.078f, 0.0155f, 16, trash);
            TwinMeshes.AddCylinder(socket, Bulb + Vector3.up * 0.026f, Bulb + Vector3.up * 0.034f, 0.0115f, 16, trash);
            var socketMat = new Material(brass) { name = "Bulb socket" };
            socketMat.SetFloat("_LightGain", 0.0f);
            socketMat.SetColor("_EmissionColor", new Color(0.035f, 0.03f, 0.022f));
            trash.Add(socketMat);
            Part(root, "Socket", TwinMeshes.Combine(socket, trash, "socket"), socketMat, AtmosphereLayer)
                .GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
            GameObject g = Part(root, "Bulb glass", TwinMeshes.Ellipsoid(new Vector3(0.029f, 0.036f, 0.029f), 14, trash, "bulb"), glass, AtmosphereLayer);
            g.transform.position = Bulb - Vector3.up * 0.008f;
            g.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
            g.GetComponent<MeshRenderer>().receiveShadows = false;

            // 光: 下向きの影つきスポット（主）と、天井と壁の上を照らす弱い点光源（影なし）
            var white = new Color(1f, 0.965f, 0.91f);   // 電球の白（3500〜4000K 寄り。照らされた壁が青灰に見える所で止める）
            Light key = AddLight(root, "Bulb key", LightType.Spot, Bulb - Vector3.up * 0.01f, Quaternion.Euler(90f, 0f, 0f), white, 9.5f, 9f);
            key.spotAngle = 78f;
            key.innerSpotAngle = 62f;     // 頂角 60 度前後。縁は半径の 15〜20% でぼかす（硬い縁は舞台照明に見える）
            _key = key;
            key.shadows = LightShadows.Soft;
            key.shadowStrength = 0.97f;
            key.shadowNearPlane = 0.05f;
            UniversalAdditionalLightData data = key.GetUniversalAdditionalLightData();
            data.usePipelineSettings = false;
            key.shadowBias = 0.012f;      // 大きいと影が床から浮き、裾の下から光が漏れる
            key.shadowNormalBias = 0.18f;
            // 全方向へ漏れる光（影つき）。カーテンと枠の影が奥の壁に落ちる
            Light spill = AddLight(root, "Bulb spill", LightType.Point, Bulb - Vector3.up * 0.008f, Quaternion.identity, white, 0.95f, 7f);
            _fills.Add((spill, spill.intensity));
            spill.shadows = LightShadows.Soft;
            spill.shadowStrength = 0.3f;    // 本体と同じ濃さの影は「4 台目のスタンド」に見える
            spill.shadowNearPlane = 0.06f;
            UniversalAdditionalLightData spillData = spill.GetUniversalAdditionalLightData();
            spillData.usePipelineSettings = false;
            spill.shadowBias = 0.04f;
            spill.shadowNormalBias = 0.4f;
            // 床と布の照り返し（光だまりから上へ。人の正面と壁の下を少し起こす）
            // 奥の壁の高い所に縦長の光だまり（裸電球は横へも光る。L の角の向きを強める）
            Vector3 toCorner = new Vector3(_corner.x - Bulb.x, 0f, _corner.y - Bulb.z).normalized;
            Light wash = AddLight(root, "Wall wash", LightType.Spot, Bulb, Quaternion.LookRotation(toCorner - Vector3.up * 0.4f), white, 7.8f, 6f);
            _fills.Add((wash, wash.intensity));
            wash.spotAngle = 76f;
            wash.innerSpotAngle = 30f;
            wash.shadows = LightShadows.Soft;
            wash.shadowStrength = 0.85f;
            UniversalAdditionalLightData washData = wash.GetUniversalAdditionalLightData();
            washData.usePipelineSettings = false;
            wash.shadowBias = 0.04f;
            wash.shadowNormalBias = 0.4f;
            // 天井の座の周り
            Light upward = AddLight(root, "Ceiling glow", LightType.Spot, Bulb + Vector3.up * 0.09f, Quaternion.LookRotation(Vector3.up), new Color(1f, 0.84f, 0.64f), 0.8f, 1.4f);
            upward.spotAngle = 150f;
            upward.innerSpotAngle = 60f;
            upward.shadows = LightShadows.None;

            Shader sprites = Need("FixedCamVr/Twin/Sprites");
            var rnd = new System.Random(4242);
            var motes = new List<(Vector3, float, float)>();
            for (int i = 0; i < 900; i++)
            {
                // 電球のすぐ下は光が強すぎて粒が雪のように固まる。高さは下へ寄せる
                float y = Mathf.Lerp(0.12f, Bulb.y - _floorY - 0.25f, Mathf.Pow((float)rnd.NextDouble(), 0.8f));
                float reach = 0.18f + (Bulb.y - _floorY - y) * 0.85f;
                float r = Mathf.Sqrt((float)rnd.NextDouble()) * reach;
                float ang = (float)rnd.NextDouble() * Mathf.PI * 2f;
                float size = 0.0015f + 0.008f * Mathf.Pow((float)rnd.NextDouble(), 3f);
                if (rnd.NextDouble() < 0.08) size = Mathf.Lerp(0.012f, 0.02f, (float)rnd.NextDouble());   // 奥行きの段（遠くの柔らかい玉）
                motes.Add((new Vector3(Bulb.x + Mathf.Cos(ang) * r, _floorY + y, Bulb.z + Mathf.Sin(ang) * r), size, (float)rnd.NextDouble()));
            }
            Material dust = SpriteMat(sprites, "Dust", new Color(0.55f, 0.52f, 0.47f), 0f, 3f, trash);
            dust.SetFloat("_Drift", 0.05f);
            Part(root, "Dust", TwinMeshes.Sprites(motes, trash, "dust"), dust, AtmosphereLayer);
            foreach ((float size, float gain, float falloff) in new[] { (0.06f, 1.0f, 2.4f), (0.14f, 0.45f, 2.8f), (0.3f, 0.14f, 2.6f), (0.6f, 0.04f, 2.4f) })
            {
                Material halo = SpriteMat(sprites, "Bulb halo", new Color(1f, 0.88f, 0.70f) * gain, 1f, falloff, trash);
                Part(root, "Bulb halo", TwinMeshes.Sprites(new List<(Vector3, float, float)> { (Bulb, size, 0f) }, trash, "halo"), halo, AtmosphereLayer);
            }

            // 光の筋（電球の下の箱の中を積分する）
            Shader volume = Need("FixedCamVr/Twin/Volume");
            var vm = new Material(volume) { name = "Light shaft" };
            trash.Add(vm);
            var min = new Vector3(Bulb.x - 2.6f, _floorY + 0.001f, Bulb.z - 2.6f);
            var max = new Vector3(Bulb.x + 2.6f, Bulb.y + 0.12f, Bulb.z + 2.6f);
            vm.SetVector("_BoxMin", min);
            vm.SetVector("_BoxMax", max);
            vm.SetFloat("_Density", HazeDensity);
            _haze = vm;
            vm.SetFloat("_Anisotropy", 0.28f);
            vm.SetFloat("_NoiseScale", 1.35f);
            vm.SetColor("_Tint", new Color(1f, 0.97f, 0.92f));
            vm.SetTexture("_HazeTex", HazeNoise(trash));
            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = "Light shaft volume";
            box.layer = AtmosphereLayer;
            box.transform.SetParent(root.transform, false);
            box.transform.position = (min + max) * 0.5f;
            box.transform.localScale = max - min;
            Object.DestroyImmediate(box.GetComponent<Collider>());
            var br = box.GetComponent<MeshRenderer>();
            br.sharedMaterial = vm;
            br.shadowCastingMode = ShadowCastingMode.Off;
            br.receiveShadows = false;
        }

        /// <summary>
        /// 床の古び: 奥の壁際に剥がれ落ちた塗膜の欠片を散らす（壁際 30cm に密、外へまばら）。
        /// 上塗り・中塗り・漆喰の 3 色の、反った不揃いな多角形。歩行の判定には入らない（見た目だけ）。
        /// 湿った染みは床のシェーダで描く（<c>_DampCenter</c>）。
        /// </summary>
        private void BuildFloorDebris(GameObject root, Shader surface, Vector2 corner, float x0, float x1, float z0, float z1, float floorY, List<Object> trash)
        {
            var rnd = new System.Random(913);
            var groups = new[] { new List<CombineInstance>(), new List<CombineInstance>(), new List<CombineInstance>() };
            float sx = corner.x < 0f ? -1f : 1f, sz = corner.y < 0f ? -1f : 1f;
            float backX = sx < 0f ? x0 : x1, backZ = sz < 0f ? z0 : z1;
            for (int k = 0; k < 64; k++)
            {
                bool alongX = rnd.NextDouble() < 0.5;
                float along = (float)rnd.NextDouble();
                float off = 0.015f + 0.32f * Mathf.Pow((float)rnd.NextDouble(), 3f);
                Vector3 c = alongX
                    ? new Vector3(Mathf.Lerp(backX - sx * 0.1f, corner.x - sx * 1.6f, along), floorY + 0.0012f, backZ - sz * off)
                    : new Vector3(backX - sx * off, floorY + 0.0012f, Mathf.Lerp(backZ - sz * 0.1f, corner.y - sz * 1.6f, along));
                float size = Mathf.Lerp(0.005f, 0.03f, Mathf.Pow((float)rnd.NextDouble(), 1.5f));
                int n = 5 + rnd.Next(3);
                var pts = new List<Vector3>();
                float spin = (float)rnd.NextDouble() * 6.28f;
                for (int j = 0; j < n; j++)
                {
                    float ang = -(j / (float)n * Mathf.PI * 2f + spin);
                    float r = size * Mathf.Lerp(0.55f, 1.15f, (float)rnd.NextDouble());
                    pts.Add(new Vector3(Mathf.Cos(ang) * r, 0f, Mathf.Sin(ang) * r * Mathf.Lerp(0.5f, 1f, (float)rnd.NextDouble())));
                }
                Mesh chip = FlatPolygon(pts, trash);
                var tilt = Quaternion.AngleAxis(Mathf.Lerp(4f, 16f, (float)rnd.NextDouble()), new Vector3((float)rnd.NextDouble() - 0.5f, 0f, (float)rnd.NextDouble() - 0.5f).normalized);
                double pick = rnd.NextDouble();
                groups[pick < 0.55 ? 0 : pick < 0.8 ? 1 : 2].Add(new CombineInstance { mesh = chip, transform = Matrix4x4.TRS(c, tilt, Vector3.one) });
            }
            Color[] colors = { new(0.05f, 0.15f, 0.15f), new(0.16f, 0.22f, 0.19f), new(0.3f, 0.31f, 0.29f) };
            for (int g = 0; g < 3; g++)
            {
                if (groups[g].Count == 0) continue;
                var m = new Material(surface) { name = "Paint chips " + g };
                m.SetColor("_BaseColor", colors[g]);
                m.SetFloat("_Spec", 0.04f);
                trash.Add(m);
                Part(root, "Paint chips " + g, TwinMeshes.Combine(groups[g], trash, "chips"), m, 0);
            }
        }

        /// <summary>壁に貼る薄い多角形（x = 一定の壁へ。pts は y-z 平面の点）。</summary>
        private static Mesh WallDecal(List<Vector3> pts, float wallSide, List<Object> trash)
        {
            var v = new List<Vector3>(pts);
            if (wallSide > 0f) v.Reverse();          // 内向き（-x を向く壁 / +x を向く壁）で表を部屋へ
            Mesh m = TwinMeshes.Fan(v, 1f, trash, "decal");
            var ns = new Vector3[m.vertexCount];
            for (int k = 0; k < ns.Length; k++) ns[k] = new Vector3(-wallSide, 0f, 0f);
            m.normals = ns;
            m.SetUVs(0, v.ConvertAll(p => new Vector2(p.z, p.y)));
            m.RecalculateTangents();
            return m;
        }

        private static Mesh FlatPolygon(List<Vector3> pts, List<Object> trash)
        {
            Mesh fan = TwinMeshes.Fan(pts, 1f, trash, "chip");
            var ns = new Vector3[fan.vertexCount];
            for (int k = 0; k < ns.Length; k++) ns[k] = Vector3.up;
            fan.normals = ns;
            fan.SetUVs(0, pts.ConvertAll(v => new Vector2(v.x, v.z)));
            fan.RecalculateTangents();
            return fan;
        }

        /// <summary>光の筋のむら（繰り返す 3D の値ノイズ 4 オクターブ）。シェーダで毎段ハッシュを回すと GPU が時間切れで落ちる。</summary>
        private static Texture3D HazeNoise(List<Object> trash)
        {
            const int N = 48;
            var rnd = new System.Random(77);
            const int Octaves = 4;
            var lattice = new float[Octaves][];
            for (int o = 0; o < Octaves; o++)
            {
                int n = 3 << o;
                lattice[o] = new float[n * n * n];
                for (int i = 0; i < lattice[o].Length; i++) lattice[o][i] = (float)rnd.NextDouble();
            }
            var px = new Color32[N * N * N];
            for (int z = 0; z < N; z++)
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                    {
                        float v = 0f, amp = 0.5f;
                        for (int o = 0; o < Octaves; o++)
                        {
                            int n = 3 << o;
                            float fx = x * n / (float)N, fy = y * n / (float)N, fz = z * n / (float)N;
                            int ix = (int)fx, iy = (int)fy, iz = (int)fz;
                            float tx = Smooth(fx - ix), ty = Smooth(fy - iy), tz = Smooth(fz - iz);
                            float L(int a, int b, int c) => lattice[o][((c % n) * n + (b % n)) * n + (a % n)];
                            float c00 = Mathf.Lerp(L(ix, iy, iz), L(ix + 1, iy, iz), tx);
                            float c10 = Mathf.Lerp(L(ix, iy + 1, iz), L(ix + 1, iy + 1, iz), tx);
                            float c01 = Mathf.Lerp(L(ix, iy, iz + 1), L(ix + 1, iy, iz + 1), tx);
                            float c11 = Mathf.Lerp(L(ix, iy + 1, iz + 1), L(ix + 1, iy + 1, iz + 1), tx);
                            v += amp * Mathf.Lerp(Mathf.Lerp(c00, c10, ty), Mathf.Lerp(c01, c11, ty), tz);
                            amp *= 0.5f;
                        }
                        byte b8 = (byte)Mathf.Clamp(Mathf.RoundToInt(v / 0.9375f * 255f), 0, 255);
                        px[(z * N + y) * N + x] = new Color32(b8, b8, b8, 255);
                    }
            var tex = new Texture3D(N, N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, name = "haze" };
            tex.SetPixels32(px);
            tex.Apply(false);
            trash.Add(tex);
            return tex;
        }

        private static float Smooth(float t) => t * t * (3f - 2f * t);

        // ---------- 注記（床のゾーン・固定カメラ） ----------

        private void BuildFloorGrid(GameObject root, ShowLayoutDef layout, float floorY, List<Object> trash)
        {
            ShowGridDef? g = layout.grid;
            if (g == null || g.rows <= 0 || g.cols <= 0 || g.tileM <= 0f) return;
            int[] cells = ZoneLayoutSolver.ParseGridCells(g.cells, g.rows, g.cols);
            if (cells.Length < g.rows * g.cols) return;
            Shader line = Need("FixedCamVr/Twin/Line");
            float y = floorY + 0.0025f;
            var fine = new List<(Vector3, Vector3, float)>();
            var border = new List<(Vector3, Vector3, float)>();
            float halfW = g.cols * g.tileM * 0.5f, halfD = g.rows * g.tileM * 0.5f;
            ShowRoomWallDef[] walls = Array.FindAll(layout.room?.walls ?? Array.Empty<ShowRoomWallDef>(), w => w != null && w.IsUsable());
            bool NearCurtain(Vector3 a, Vector3 b)
            {
                var mid = new Vector2((a.x + b.x) * 0.5f, (a.z + b.z) * 0.5f);
                foreach (ShowRoomWallDef w in walls)
                    if (SegmentDistance(mid, new Vector2(w.x1, w.z1), new Vector2(w.x2, w.z2)) < 0.07f) return true;
                return false;
            }
            for (int c = 1; c < g.cols; c++)
                for (int r = 0; r < g.rows; r++)
                {
                    float x = -halfW + c * g.tileM, za = halfD - r * g.tileM;
                    fine.Add((new Vector3(x, y, za), new Vector3(x, y, za - g.tileM), 0.22f));
                }
            for (int r = 1; r < g.rows; r++)
                for (int c = 0; c < g.cols; c++)
                {
                    float z = halfD - r * g.tileM, xa = -halfW + c * g.tileM;
                    fine.Add((new Vector3(xa, y, z), new Vector3(xa + g.tileM, y, z), 0.22f));
                }
            fine.RemoveAll(s => NearCurtain(s.Item1, s.Item2));
            int Cell(int r, int c) => r < 0 || c < 0 || r >= g.rows || c >= g.cols ? -9 : cells[r * g.cols + c];
            for (int r = 0; r < g.rows; r++)
                for (int c = 0; c < g.cols; c++)
                {
                    ZoneLayoutSolver.CellRect(r, c, g.rows, g.cols, g.tileM, out float x0, out float x1, out float z0, out float z1);
                    int me = Cell(r, c);
                    if (Cell(r, c + 1) != me) border.Add((new Vector3(x1, y, z0), new Vector3(x1, y, z1), Cell(r, c + 1) == -9 ? 0.9f : 0.6f));
                    if (c == 0) border.Add((new Vector3(x0, y, z0), new Vector3(x0, y, z1), 0.9f));
                    if (Cell(r + 1, c) != me) border.Add((new Vector3(x0, y, z0), new Vector3(x1, y, z0), Cell(r + 1, c) == -9 ? 0.9f : 0.6f));
                    if (r == 0) border.Add((new Vector3(x0, y, z1), new Vector3(x1, y, z1), 0.9f));
                }
            border.RemoveAll(s => NearCurtain(s.Item1, s.Item2));
            Material fineMat = LineMat(line, "Floor grid", new Color(0.35f, 0.78f, 0.86f), 0.42f, trash);
            fineMat.SetFloat("_ConeFade", 0.7f);
            Material borderMat = LineMat(line, "Zone border", new Color(0.45f, 0.92f, 1f), 0.75f, trash);
            _fineGrid = Part(root, "Floor grid", TwinMeshes.Lines(fine, trash, "grid"), fineMat, GizmoLayer);
            _outerBorder = Part(root, "Zone border", TwinMeshes.Lines(border.FindAll(s => s.Item3 > 0.8f), trash, "zones"), borderMat, GizmoLayer);
            _innerBorders = Part(root, "Zone border (between cameras)", TwinMeshes.Lines(border.FindAll(s => s.Item3 <= 0.8f), trash, "zonesInner"), borderMat, GizmoLayer);
            // ゾーンの塗り（いま映っているカメラのゾーンだけ明るい）
            int camMax = 0;
            foreach (int c in cells) camMax = Mathf.Max(camMax, c);
            for (int cam = 0; cam <= camMax; cam++)
            {
                var quads = new List<(Vector3, Vector2, float)>();
                for (int r = 0; r < g.rows; r++)
                    for (int c = 0; c < g.cols; c++)
                    {
                        if (Cell(r, c) != cam) continue;
                        ZoneLayoutSolver.CellRect(r, c, g.rows, g.cols, g.tileM, out float x0, out float x1, out float z0, out float z1);
                        quads.Add((new Vector3((x0 + x1) * 0.5f, y - 0.0005f, (z0 + z1) * 0.5f), new Vector2(g.tileM * 0.5f, g.tileM * 0.5f), 1f));
                    }
                Material fill = LineMat(line, "Zone fill " + cam, new Color(0.25f, 0.75f, 0.85f), 0f, trash);
                _zoneFill.Add(fill);
                Part(root, "Zone fill " + cam, TwinMeshes.Quads(quads, trash, "zonefill"), fill, GizmoLayer);
            }
        }

        private bool _shelfBuilt;

        /// <summary>C のアームを留める軽量ラック（柱 4 本と棚板 4 段）。</summary>
        private void BuildShelf(List<CombineInstance> parts, Bounds b, List<Object> trash)
        {
            _shelfBuilt = true;
            float h = b.size.y;
            foreach (float x in new[] { b.min.x, b.max.x })
                foreach (float z in new[] { b.min.z, b.max.z })
                    TwinMeshes.AddBox(parts, new Vector3(x, _floorY + h * 0.5f, z), new Vector3(0.018f, h * 0.5f, 0.018f), Quaternion.identity, trash);
            foreach (float y in new[] { 0.14f, 0.72f, 1.28f, h - 0.006f })
                TwinMeshes.AddBox(parts, new Vector3(b.center.x, _floorY + y, b.center.z), new Vector3(b.extents.x + 0.012f, 0.006f, b.extents.z + 0.012f), Quaternion.identity, trash);
        }

        private void BuildCameraMarks(GameObject root, IReadOnlyList<CameraMark> cameras, Material metal, Material phone, List<Object> trash)
        {
            Shader line = Need("FixedCamVr/Twin/Line");
            Shader sprites = Need("FixedCamVr/Twin/Sprites");
            Shader surface = Need("FixedCamVr/Twin/Surface");
            for (int i = 0; i < cameras.Count; i++)
            {
                CameraMark m = cameras[i];
                Vector3 p = m.Position, fwd = m.Rotation * Vector3.forward, up = m.Rotation * Vector3.up, right = m.Rotation * Vector3.right;
                // スマホ（横向き。背面のレンズが被写体を向く）と三脚
                var body = new List<CombineInstance>();
                Vector3 bodyC = p - fwd * 0.006f + right * 0.055f;
                body.Add(new CombineInstance
                {
                    mesh = TwinMeshes.Ellipsoid(new Vector3(0.078f, 0.037f, 0.0045f), 16, trash, "phonebody", 0.18f),
                    transform = Matrix4x4.TRS(bodyC, m.Rotation, Vector3.one),
                });
                TwinMeshes.AddBox(body, p + fwd * 0.0045f + right * 0.004f, new Vector3(0.017f, 0.013f, 0.0016f), m.Rotation, trash);
                TwinMeshes.AddCylinder(body, p - fwd * 0.002f, p + fwd * 0.0068f, 0.0062f, 16, trash);
                foreach (Vector3 o in new[] { up * 0.0085f + right * 0.009f, -up * 0.0075f + right * 0.009f })
                    TwinMeshes.AddCylinder(body, p + o - fwd * 0.002f, p + o + fwd * 0.0062f, 0.0038f, 12, trash);
                // 三脚の頭のクランプ（上下で挟む爪と、締めつまみ）
                foreach (float s in new[] { 1f, -1f })
                    TwinMeshes.AddBox(body, bodyC + up * (0.039f * s) + right * 0.0f, new Vector3(0.022f, 0.006f, 0.0085f), m.Rotation, trash);
                TwinMeshes.AddCylinder(body, bodyC - up * 0.045f - fwd * 0.012f, bodyC - up * 0.045f - fwd * 0.03f, 0.008f, 14, trash);
                Part(root, "Phone " + m.Id, TwinMeshes.Combine(body, trash, "phone"), phone, GizmoLayer);
                var screen = new Material(surface) { name = "Phone screen " + m.Id };
                screen.SetColor("_BaseColor", Color.black);
                screen.SetFloat("_Spec", 0.6f);
                screen.SetFloat("_Gloss", 140f);
                trash.Add(screen);
                _screens.Add(screen);
                screen.SetFloat("_Cull", 0f);
                screen.SetFloat("_Sheen", 0.016f);
                screen.SetFloat("_EmissionFit", (0.071f / 0.031f) / (4f / 3f));   // 画面の縦横比 / 映像（640x480）の縦横比
                Vector3 sc0 = bodyC - fwd * 0.0049f;
                var corners = new List<Vector3>
                {
                    sc0 - right * 0.071f - up * 0.031f, sc0 + right * 0.071f - up * 0.031f,
                    sc0 + right * 0.071f + up * 0.031f, sc0 - right * 0.071f + up * 0.031f,
                };
                Mesh screenMesh = TwinMeshes.Fan(corners, 1f, trash, "screen");
                screenMesh.SetUVs(0, new List<Vector2> { new(0, 0), new(1, 0), new(1, 1), new(0, 1) });
                screenMesh.SetNormals(new List<Vector3> { -fwd, -fwd, -fwd, -fwd });
                screenMesh.RecalculateTangents();
                Part(root, "Phone screen " + m.Id, screenMesh, screen, GizmoLayer);

                var stand = new List<CombineInstance>();
                Vector3 foot = new Vector3(bodyC.x, _floorY, bodyC.z) + new Vector3(fwd.x, 0, fwd.z).normalized * 0.025f;   // 脚はレンズ側（画面の前に脚と柱を出さない）
                float clampY = bodyC.y - 0.045f;
                switch (TwinRig.Mount(m.Id))
                {
                    case "floor":
                    {
                        // B: 床置きの黒い台（図 1）。足の板・背板・低い受け
                        Vector3 flat = new Vector3(fwd.x, 0f, fwd.z).normalized;
                        Quaternion yaw = Quaternion.LookRotation(flat, Vector3.up);
                        Vector3 baseC = new Vector3(bodyC.x, _floorY, bodyC.z);
                        TwinMeshes.AddBox(stand, baseC + Vector3.up * 0.003f, new Vector3(0.1f, 0.003f, 0.075f), yaw, trash);
                        TwinMeshes.AddBox(stand, baseC + Vector3.up * 0.073f - flat * 0.043f, new Vector3(0.09f, 0.067f, 0.019f), yaw, trash);
                        TwinMeshes.AddBox(stand, baseC + Vector3.up * 0.02f + flat * 0.026f, new Vector3(0.09f, 0.014f, 0.014f), yaw, trash);
                        break;
                    }
                    case "arm":
                    {
                        // C: 棚の上段の前梁に留めたアーム（企画書 2 節・図 1）
                        Bounds shelf = TwinRig.ShelfBounds;
                        var clampAt = new Vector3(bodyC.x + 0.02f, shelf.max.y - 0.02f, shelf.min.z - 0.012f);
                        TwinMeshes.AddBox(stand, clampAt, new Vector3(0.024f, 0.03f, 0.03f), Quaternion.identity, trash);
                        Vector3 p1 = clampAt + new Vector3(-0.02f, 0.17f, -0.05f);
                        TwinMeshes.AddCylinder(stand, clampAt, p1, 0.0105f, 10, trash);
                        TwinMeshes.AddCylinder(stand, p1, bodyC + fwd * 0.012f, 0.0085f, 10, trash);
                        if (!_shelfBuilt) BuildShelf(stand, shelf, trash);
                        break;
                    }
                    default:
                    {
                        // A: 三脚（図 1）
                        float hubY = Mathf.Min(clampY - 0.04f, 1.15f + _floorY);
                        float spread = Mathf.Clamp((hubY - _floorY) * 0.32f, 0.07f, 0.34f);
                        Vector3 hub = new Vector3(foot.x, hubY, foot.z);
                        TwinMeshes.AddCylinder(stand, hub, new Vector3(foot.x, clampY, foot.z), 0.008f, 10, trash);
                        TwinMeshes.AddBox(stand, new Vector3(foot.x, clampY + 0.006f, foot.z), new Vector3(0.02f, 0.008f, 0.02f), m.Rotation, trash);
                        // 連結は本体の裏（レンズ側）。画面側に出すと画面を横切る
                        TwinMeshes.AddBox(stand, (new Vector3(foot.x, clampY + 0.012f, foot.z) + bodyC) * 0.5f + fwd * 0.004f, new Vector3(0.006f, 0.02f, 0.006f), m.Rotation, trash);
                        for (int k = 0; k < 3; k++)
                        {
                            float a = (k * 120f + 30f) * Mathf.Deg2Rad;
                            TwinMeshes.AddCylinder(stand, hub, foot + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * spread + Vector3.up * 0.004f, 0.0065f, 8, trash);
                        }
                        break;
                    }
                }
                Part(root, "Stand " + m.Id, TwinMeshes.Combine(stand, trash, "stand"), StandMat(metal, trash), GizmoLayer);

                // 視野（四角錐）と録画灯
                // 視野: 四隅の光線は先へ行くほど消す。床に届いた光線は、その足跡（このカメラが床のどこを写すか）を描く
                float tanV = Mathf.Tan(m.VerticalFovDeg * 0.5f * Mathf.Deg2Rad), tanH = tanV * 4f / 3f;
                var seg = new List<(Vector3, Vector3, float, float)>();
                var hit = new List<Vector3>();
                foreach (int k in new[] { 0, 1, 3, 2 })      // 輪の順
                {
                    float sx = (k & 1) == 0 ? -1f : 1f, sy = (k & 2) == 0 ? -1f : 1f;
                    Vector3 dir = (fwd + right * sx * tanH + up * sy * tanV).normalized;
                    float toFloor = dir.y < -0.05f ? (_floorY + 0.003f - p.y) / dir.y : float.PositiveInfinity;
                    seg.Add((p, p + dir * 0.35f, 0.8f, 0f));
                    if (toFloor < 4.5f) hit.Add(p + dir * toFloor);
                }
                // 下の 2 隅だけが床に届くカメラ（上を向いた B）は、足元から 2 点への三角を足跡にする
                if (hit.Count == 2) hit.Insert(0, new Vector3(p.x, _floorY + 0.003f, p.z));
                var outline = new List<(Vector3, Vector3, float, float)>();
                if (hit.Count >= 3)
                    for (int k = 0; k < hit.Count; k++)
                    {
                        // 破線（ゾーンの外周の実線と平行に走っても、版ずれの二重線に見えない）
                        Vector3 a0 = hit[k], a1 = hit[(k + 1) % hit.Count];
                        float len = Vector3.Distance(a0, a1);
                        for (float t = 0.08f; t < len - 0.08f; t += 0.1f)
                            outline.Add((Vector3.Lerp(a0, a1, t / len), Vector3.Lerp(a0, a1, Mathf.Min(t + 0.055f, len - 0.08f) / len), 0.9f, 0.9f));
                    }
                Material fm = LineMat(line, "Frustum " + m.Id, new Color(0.5f, 0.93f, 1f), 0.4f, trash);
                _frustum.Add(fm);
                GameObject frustumGo = Part(root, "Frustum " + m.Id, TwinMeshes.Lines(seg, trash, "frustum"), fm, GizmoLayer);
                _markParts.Add((bodyC, frustumGo));
                if (outline.Count > 0)
                    _footprints.Add((i, Part(root, "Footprint " + m.Id, TwinMeshes.Lines(outline, trash, "footprint"), fm, GizmoLayer)));
                // 画面の UI（録画の経過時間・電池）。4:3 の映像の左右の黒帯に、カメラアプリの表示として
                var ui = new List<(Vector3, Vector3, float)>();
                Vector3 uiPlane = bodyC - fwd * 0.0052f;
                ui.AddRange(SevenSeg("00:12", uiPlane - right * 0.0675f + up * 0.0215f, right, up, 0.0052f));
                Vector3 bat = uiPlane + right * 0.0565f - up * 0.0275f;
                ui.Add((bat, bat + right * 0.009f, 1f)); ui.Add((bat + right * 0.009f, bat + right * 0.009f + up * 0.0045f, 1f));
                ui.Add((bat + right * 0.009f + up * 0.0045f, bat + up * 0.0045f, 1f)); ui.Add((bat + up * 0.0045f, bat, 1f));
                ui.Add((bat + right * 0.0095f + up * 0.0013f, bat + right * 0.0095f + up * 0.0032f, 1f));
                for (float fy = 0.0009f; fy < 0.0037f; fy += 0.0007f) ui.Add((bat + right * 0.001f + up * fy, bat + right * 0.0062f + up * fy, 1f));
                Material uiMat = LineMat(line, "Screen UI " + m.Id, new Color(0.92f, 0.95f, 0.95f), 0.85f, trash);
                GameObject uiGo = Part(root, "Screen UI " + m.Id, TwinMeshes.Strokes(ui, -fwd, 0.00055f, trash, "ui"), uiMat, GizmoLayer);
                uiGo.SetActive(false);
                _screenUi.Add(uiGo);
                Material ff = LineMat(line, "Frustum face " + m.Id, new Color(0.4f, 0.85f, 1f), 0.02f, trash);
                _frustumFace.Add(ff);
                if (hit.Count >= 3)
                    _faceParts.Add((bodyC, Part(root, "Frustum face " + m.Id, TwinMeshes.Fan(hit, 1f, trash, "frustumface"), ff, GizmoLayer)));
                // 札（A / B / C）。スマホの上に、ブースを開いた側から見て読める向きで（裏から見て鏡文字にしない）
                Vector3 front = new Vector3(-_corner.x, 0f, -_corner.y).normalized;
                Vector3 labelRight = Vector3.Cross(Vector3.up, -front);
                var glyph = Glyph(m.Id, -labelRight * 0.03f, labelRight, Vector3.up, 0.09f);
                Material lm = LineMat(line, "Label " + m.Id, new Color(0.6f, 0.95f, 1f), 0.8f, trash);
                _labels.Add(lm);
                if (glyph.Count > 0)
                {
                    GameObject label = Part(root, "Label " + m.Id, TwinMeshes.Strokes(glyph, -front, 0.008f, trash, "label"), lm, GizmoLayer);
                    label.transform.position = bodyC + Vector3.up * 0.06f;
                    _labelObjects.Add((label, bodyC));
                }
                Vector3 foot0 = new Vector3(bodyC.x, _floorY + 0.004f, bodyC.z);
                Vector3 toRoom = new Vector3(-foot0.x, 0f, -foot0.z).normalized;
                var floorGlyph = Glyph(m.Id, foot0 + toRoom * 0.42f - Vector3.forward * 0.05f - Vector3.left * 0.08f, Vector3.forward, Vector3.left, 0.16f);
                if (floorGlyph.Count > 0)
                {
                    GameObject fl = Part(root, "Floor label " + m.Id, TwinMeshes.Strokes(floorGlyph, Vector3.up, 0.014f, trash, "floorlabel"), lm, GizmoLayer);
                    _floorLabels.Add((fl, foot0 + toRoom * 0.42f));
                }
                Vector3 ledPos = bodyC + right * 0.06f + up * 0.026f - fwd * 0.006f;
                Material ledCore = SpriteMat(sprites, "REC core " + m.Id, new Color(1f, 0.1f, 0.06f), 1f, 2.2f, trash);
                Material ledGlow = SpriteMat(sprites, "REC glow " + m.Id, new Color(1f, 0.08f, 0.05f), 1f, 3.5f, trash);
                _led.Add((ledCore, ledGlow));
                Part(root, "REC " + m.Id, TwinMeshes.Sprites(new List<(Vector3, float, float)> { (ledPos, 0.0028f, 0f) }, trash, "rec"), ledCore, GizmoLayer);
                Part(root, "REC glow " + m.Id, TwinMeshes.Sprites(new List<(Vector3, float, float)> { (ledPos, 0.009f, 0f) }, trash, "recglow"), ledGlow, GizmoLayer);
                _markCamera.Add(i);
            }
            SetActiveCamera(-1);
        }

        /// <summary>
        /// 体験者の代役を、この部屋で撮る間だけ部屋の画に合わせる（髪を暗い茶に・服の艶を消す）。
        /// 撮影が終わると外すので、本番の CG 層の合成（walk.mp4 の目の画）の体は変わらない。
        /// </summary>
        public void SetActor(GameObject actor)
        {
            _actorLook.Clear();
            _actorColor.Clear();
            _clothNormal ??= ClothNormal(256);
            foreach (Renderer r in actor.GetComponentsInChildren<Renderer>(true))
            {
                var block = new MaterialPropertyBlock();
                switch (r.name)
                {
                    case "Hair": block.SetColor("_BaseColor", new Color(0.3f, 0.24f, 0.19f)); block.SetFloat("_Smoothness", 0.05f); break;
                    case "Body": block.SetColor("_BaseColor", new Color(0.36f, 0.34f, 0.33f)); block.SetFloat("_Smoothness", 0.1f); break;
                    case "Tops":
                        block.SetColor("_BaseColor", new Color(0.15f, 0.15f, 0.16f)); block.SetFloat("_Smoothness", 0.02f);
                        block.SetTexture("_BumpMap", _clothNormal); block.SetFloat("_BumpScale", 1.1f);
                        foreach (Material mat in r.sharedMaterials) if (mat != null) mat.EnableKeyword("_NORMALMAP");
                        break;
                    case "Shoes": block.SetColor("_BaseColor", new Color(0.3f, 0.3f, 0.3f)); block.SetFloat("_Smoothness", 0.15f); break;
                    case "Bottoms":
                        // カーゴの当て布とボタンが腿の「破れ」に見える。無地の暗い生地にする
                        block.SetTexture("_BaseMap", Texture2D.whiteTexture);
                        block.SetColor("_BaseColor", new Color(0.04f, 0.042f, 0.04f));
                        block.SetFloat("_Smoothness", 0f);
                        block.SetFloat("_EnvironmentReflections", 0f);
                        block.SetTexture("_BumpMap", _clothNormal); block.SetFloat("_BumpScale", 0.5f);
                        foreach (Material mat in r.sharedMaterials) if (mat != null) mat.EnableKeyword("_NORMALMAP");
                        break;
                    default: continue;
                }
                _actorLook.Add((r, block));
                _actorColor.Add(block.GetColor("_BaseColor"));
            }
        }

        /// <summary>
        /// この画角の撮影の前に札を合わせる。本体が画の外にあるカメラの札は出さない。
        /// 札の字の高さは画面の高さの 4% を上限にする（手前のカメラの札が主役と競わないように）。
        /// </summary>
        public void PrepareFor(Camera cam)
        {
            float pxH = 1080f, pxW = pxH * cam.aspect;
            foreach ((GameObject label, Vector3 body) in _labelObjects)
            {
                Vector3 vp = cam.WorldToViewportPoint(body);
                bool visible = vp.z > 0.05f && vp.z < 5.2f && vp.x > 0.1f && vp.x < 0.9f && vp.y > 0.1f && vp.y < 0.9f;
                // 別のカメラの本体（と三脚）に 80px 以内で重なる札は、どちらの札か読めないので出さない
                Vector3 at = cam.WorldToViewportPoint(body + Vector3.up * 0.1f);
                foreach ((GameObject other, Vector3 otherBody) in _labelObjects)
                {
                    if (other == label) continue;
                    Vector3 o = cam.WorldToViewportPoint(otherBody);
                    Vector3 foot = cam.WorldToViewportPoint(new Vector3(otherBody.x, _floorY, otherBody.z));
                    if (o.z <= 0f || o.z > at.z) continue;
                    // 本体の 80px 上から床の足元までの縦の帯（三脚）に札が入るか
                    float top = o.y * pxH + 80f, bottom = foot.y * pxH - 20f, y = at.y * pxH;
                    if (y > top || y < bottom) continue;
                    float k = Mathf.InverseLerp(o.y, foot.y, at.y);
                    float x = Mathf.Lerp(o.x, foot.x, k) * pxW;
                    if (Mathf.Abs(at.x * pxW - x) < 110f) visible = false;
                }
                label.SetActive(visible && Frustums && !Hidden(body));
                float screenH = cam.orthographic ? cam.orthographicSize * 2f : 2f * vp.z * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                label.transform.localScale = Vector3.one * Mathf.Min(1f, 0.04f * screenH / 0.09f);
            }
            foreach ((int camIndex, GameObject part) in _footprints)
                part.SetActive(cam.orthographic || Footprints && Frustums && (camIndex == _activeCam || !FootprintsActiveOnly));
            foreach ((Vector3 body, GameObject part) in _faceParts)
                part.SetActive(cam.orthographic || FrustumFaces && Frustums);
            if (_fineGrid != null) _fineGrid.SetActive(cam.orthographic || FineGrid && FloorMarks);
            if (_outerBorder != null) _outerBorder.SetActive(cam.orthographic || FloorMarks);
            foreach ((Vector3 body, GameObject part) in _markParts)
            {
                Vector3 vp = cam.WorldToViewportPoint(body);
                part.SetActive(cam.orthographic || Frustums && !Hidden(body) && (vp.z > 0.05f && vp.z < 5.2f && vp.x > 0.1f && vp.x < 0.9f && vp.y > 0.1f && vp.y < 0.9f));
            }
            foreach ((GameObject label, Vector3 at) in _floorLabels)
            {
                Vector3 vp = cam.WorldToViewportPoint(at);
                label.SetActive(cam.orthographic && vp.x > 0.06f && vp.x < 0.94f && vp.y > 0.06f && vp.y < 0.94f);
            }
        }

        /// <summary>
        /// 固定カメラ <paramref name="index"/> のスマホの画面に、そのカメラがいま撮っている画を流す
        /// （配信スマホの映像が、体験者が HMD の中で見ている画になる — 作品の仕組みを 1 枚の中で見せる）。
        /// </summary>
        public void SetScreenFeed(int index, Texture? feed)
        {
            for (int k = 0; k < _screens.Count; k++)
            {
                if (k == index && feed != null) _screens[k].SetTexture("_EmissionMap", feed);
                else _screens[k].SetTexture("_EmissionMap", Texture2D.whiteTexture);
                if (k < _screenUi.Count) _screenUi[k].SetActive(k == index && feed != null);
            }
            SetActiveCamera(index);
        }

        /// <summary>固定カメラ <paramref name="index"/> の位置・向き・画角で、部屋を撮る（スマホの画面に流す画）。</summary>
        public void Feed(TwinLens lens, CameraMark mark, float time, int frame)
        {
            Camera c = lens.Camera;
            c.orthographic = false;
            c.transform.SetPositionAndRotation(mark.Position, mark.Rotation);
            c.fieldOfView = mark.VerticalFovDeg;
            c.cullingMask = ~((1 << 30) | (1 << GizmoLayer) | (1 << WalkEvidence.Layer));
            lens.Develop(this, time, frame);
        }

        /// <summary>壁の配管を壁に沿ってずらす（画で機材と接して見える画角だけ）。</summary>
        public void ShiftConduit(Vector3 delta)
        {
            foreach (GameObject g in _conduit) g.transform.position += delta;
        }

        /// <summary>7 セグメントの数字と「:」（スマホの画面の経過時間）。</summary>
        private static List<(Vector3, Vector3, float)> SevenSeg(string text, Vector3 origin, Vector3 right, Vector3 up, float h)
        {
            float w = h * 0.55f;
            var segs = new List<(Vector3, Vector3, float)>();
            string[] map = { "abcdef", "bc", "abged", "abgcd", "fgbc", "afgcd", "afgedc", "abc", "abcdefg", "abfgcd" };
            Vector3 at = origin;
            foreach (char ch in text)
            {
                if (ch == ':')
                {
                    segs.Add((at + up * h * 0.28f, at + up * h * 0.34f, 1f));
                    segs.Add((at + up * h * 0.66f, at + up * h * 0.72f, 1f));
                    at += right * h * 0.35f;
                    continue;
                }
                int d = ch - '0';
                if (d < 0 || d > 9) { at += right * h * 0.8f; continue; }
                Vector3 P(float x, float y) => at + right * x + up * y;
                foreach (char s in map[d])
                    segs.Add(s switch
                    {
                        'a' => (P(0, h), P(w, h), 1f), 'b' => (P(w, h), P(w, h * 0.5f), 1f), 'c' => (P(w, h * 0.5f), P(w, 0), 1f),
                        'd' => (P(0, 0), P(w, 0), 1f), 'e' => (P(0, 0), P(0, h * 0.5f), 1f), 'f' => (P(0, h * 0.5f), P(0, h), 1f),
                        _ => (P(0, h * 0.5f), P(w, h * 0.5f), 1f),
                    });
                at += right * h * 0.85f;
            }
            return segs;
        }

        /// <summary>A / B / C を線で描く（文字資産に頼らない）。<paramref name="origin"/> は左下。</summary>
        private static List<(Vector3, Vector3, float)> Glyph(string id, Vector3 origin, Vector3 right, Vector3 up, float h)
        {
            var pts = new List<Vector2[]>();
            switch (id)
            {
                case "A":
                    pts.Add(new[] { new Vector2(0f, 0f), new Vector2(0.3f, 1f), new Vector2(0.6f, 0f) });
                    pts.Add(new[] { new Vector2(0.12f, 0.4f), new Vector2(0.48f, 0.4f) });
                    break;
                case "B":
                    pts.Add(new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0.38f, 1f), new Vector2(0.5f, 0.9f), new Vector2(0.5f, 0.6f),
                                    new Vector2(0.38f, 0.5f), new Vector2(0f, 0.5f) });
                    pts.Add(new[] { new Vector2(0.38f, 0.5f), new Vector2(0.55f, 0.4f), new Vector2(0.55f, 0.1f), new Vector2(0.42f, 0f), new Vector2(0f, 0f) });
                    break;
                case "C":
                    var arc = new Vector2[13];
                    for (int i = 0; i < arc.Length; i++)
                    {
                        float a = Mathf.Lerp(50f, 310f, i / (arc.Length - 1f)) * Mathf.Deg2Rad;
                        arc[i] = new Vector2(0.3f + 0.3f * Mathf.Cos(a), 0.5f + 0.5f * Mathf.Sin(a));
                    }
                    pts.Add(arc);
                    break;
            }
            var segs = new List<(Vector3, Vector3, float)>();
            foreach (Vector2[] stroke in pts)
                for (int i = 0; i + 1 < stroke.Length; i++)
                    segs.Add((origin + (right * stroke[i].x + up * stroke[i].y) * h,
                              origin + (right * stroke[i + 1].x + up * stroke[i + 1].y) * h, 1f));
            return segs;
        }

        /// <summary>いま映っているカメラ（<c>CameraStreamRegistry.ActiveIndex</c>）を、録画灯・視野・床のゾーンで示す。</summary>
        public void SetActiveCamera(int index)
        {
            _activeCam = index;
            for (int k = 0; k < _frustum.Count; k++)
            {
                bool on = _markCamera[k] == index;
                _frustum[k].SetFloat("_Intensity", on ? 0.7f : 0.3f);
                _frustumFace[k].SetFloat("_Intensity", on ? 0.05f : 0.015f);
                _labels[k].SetFloat("_Intensity", on ? 0.95f : 0.45f);
                _led[k].core.SetColor("_Color", on ? new Color(2.2f, 0.2f, 0.1f) : new Color(0.35f, 0.03f, 0.02f));
                _led[k].glow.SetColor("_Color", on ? new Color(0.45f, 0.03f, 0.015f) : new Color(0.05f, 0.004f, 0.002f));
                bool fed = _screens[k].GetTexture("_EmissionMap") is RenderTexture;
                _screens[k].SetColor("_EmissionColor", fed ? new Color(1.15f, 1.2f, 1.2f) : on ? new Color(0.008f, 0.02f, 0.022f) : Color.black);
            }
            for (int k = 0; k < _zoneFill.Count; k++) _zoneFill[k].SetFloat("_Intensity", ZoneFill && k == index ? 0.03f : 0f);
        }

        // ---------- 撮影 ----------

        /// <summary>この部屋を撮る間だけ部屋の光・環境光・霧を入れ、他の光（本番の CG 層の光）を消す。Dispose で戻す。</summary>
        public Shot BeginShot(float time) => new Shot(this, time);

        public readonly struct Shot : IDisposable
        {
            private readonly VirtualRoom _room;
            private readonly List<Light> _silenced;
            private readonly AmbientMode _mode;
            private readonly Color _ambient;
            private readonly SphericalHarmonicsL2 _probe;
            private readonly bool _fog;
            private readonly FogMode _fogMode;
            private readonly Color _fogColor;
            private readonly float _fogDensity;

            public Shot(VirtualRoom room, float time)
            {
                _room = room;
                _silenced = new List<Light>();
                foreach (Light l in Object.FindObjectsOfType<Light>())
                    if (l.enabled && !room._lights.Contains(l)) { l.enabled = false; _silenced.Add(l); }
                foreach (Light l in room._lights) l.enabled = true;
                foreach ((Light l, float b) in room._fills) l.intensity = b * room.FillScale;
                if (room._haze != null) room._haze.SetFloat("_Density", HazeDensity * room.HazeScale);
                for (int k = 0; k < room._actorLook.Count; k++)
                {
                    (Renderer r, MaterialPropertyBlock block) = room._actorLook[k];
                    if (r == null) continue;
                    block.SetColor("_BaseColor", room._actorColor[k] * room.ActorShade);
                    r.SetPropertyBlock(block);
                }
                _mode = RenderSettings.ambientMode; _ambient = RenderSettings.ambientLight; _probe = RenderSettings.ambientProbe;
                _fog = RenderSettings.fog; _fogMode = RenderSettings.fogMode; _fogColor = RenderSettings.fogColor; _fogDensity = RenderSettings.fogDensity;
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = Ambient;
                var sh = new SphericalHarmonicsL2();
                sh.AddAmbientLight(Ambient * 1.6f);
                RenderSettings.ambientProbe = sh;
                RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.Exponential;
                RenderSettings.fogColor = FogColor;
                RenderSettings.fogDensity = FogDensity;
                Shader.SetGlobalVector("_TwinAmbient", Ambient);
                Shader.SetGlobalVector("_TwinFogColor", FogColor);
                Shader.SetGlobalFloat("_TwinFogDensity", FogDensity);
                Shader.SetGlobalFloat("_TwinTime", time);
                if (room._key != null)
                {
                    Shader.SetGlobalVector("_TwinKeyPos", room._key.transform.position);
                    Shader.SetGlobalVector("_TwinKeyDir", room._key.transform.forward);
                    Color kc = room._key.color.linear * room._key.intensity;
                    Shader.SetGlobalVector("_TwinKeyColor", new Vector4(kc.r, kc.g, kc.b, 0f));
                    Shader.SetGlobalVector("_TwinKeyCos", new Vector4(Mathf.Cos(room._key.spotAngle * 0.5f * Mathf.Deg2Rad),
                                                                      Mathf.Cos(room._key.innerSpotAngle * 0.5f * Mathf.Deg2Rad), 0f, 0f));
                }
            }

            public void Dispose()
            {
                foreach (Light l in _room._lights) l.enabled = false;
                foreach ((Renderer r, MaterialPropertyBlock _) in _room._actorLook) if (r != null) r.SetPropertyBlock(null);
                foreach (Light l in _silenced) if (l != null) l.enabled = true;
                RenderSettings.ambientMode = _mode; RenderSettings.ambientLight = _ambient; RenderSettings.ambientProbe = _probe;
                RenderSettings.fog = _fog; RenderSettings.fogMode = _fogMode; RenderSettings.fogColor = _fogColor; RenderSettings.fogDensity = _fogDensity;
            }
        }

        public void Dispose()
        {
            _pipeline?.Restore();
            _pipeline = null;
            if (_clothNormal != null) Object.DestroyImmediate(_clothNormal);
            _clothNormal = null;
        }

        /// <summary>
        /// 電球の影を細かくするため、撮る間だけ URP の追加ライトの影の解像度（High 段）を上げる。
        /// 資産はディスクへ保存しない（終わったら値を戻して dirty も消す）。本番 CG の光（平行光）には効かない設定だけを触る。
        /// </summary>
        private sealed class PipelineOverride
        {
            private readonly Object _asset;
            private readonly List<(string name, int value)> _saved = new();

            private PipelineOverride(Object asset) { _asset = asset; }

            public static PipelineOverride? Apply()
            {
                RenderPipelineAsset? asset = GraphicsSettings.currentRenderPipeline;
                if (asset == null) { Debug.LogWarning("[VirtualRoom] URP の資産が無い（電球の影は既定の解像度）"); return null; }
                var o = new PipelineOverride(asset);
                var so = new SerializedObject(asset);
                o.Set(so, "m_AdditionalLightsRenderingMode", 1);          // 画素ごと
                o.Set(so, "m_AdditionalLightShadowsSupported", 1);
                o.Set(so, "m_AdditionalLightsShadowmapResolution", 4096);
                o.Set(so, "m_AdditionalLightsShadowResolutionTierHigh", 2048);
                o.Set(so, "m_SoftShadowsSupported", 1);
                so.ApplyModifiedPropertiesWithoutUndo();
                Debug.Log($"[VirtualRoom] {asset.name}: 撮影中だけ " + string.Join(", ", o._saved.ConvertAll(s => s.name)) + " を上書き");
                return o;
            }

            private void Set(SerializedObject so, string name, int value)
            {
                SerializedProperty? p = so.FindProperty(name);
                if (p == null) { Debug.LogWarning($"[VirtualRoom] URP 資産に {name} が無い"); return; }
                int now = p.propertyType == SerializedPropertyType.Boolean ? (p.boolValue ? 1 : 0) : p.intValue;
                if (now == value) return;
                _saved.Add((name, now));
                if (p.propertyType == SerializedPropertyType.Boolean) p.boolValue = value != 0;
                else p.intValue = value;
            }

            public void Restore()
            {
                if (_asset == null || _saved.Count == 0) return;
                var so = new SerializedObject(_asset);
                foreach ((string name, int value) in _saved)
                {
                    SerializedProperty p = so.FindProperty(name);
                    if (p.propertyType == SerializedPropertyType.Boolean) p.boolValue = value != 0;
                    else p.intValue = value;
                }
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.ClearDirty(_asset);
            }
        }

        // ---------- 体験者の HMD ----------

        /// <summary>頭に載せる HMD の代役。<b>実機の体験者は必ず被っている</b>ので、映像に写る形を揃える（本番の CG 層でも描くので URP/Lit）。</summary>
        public static Transform BuildHmd(List<Object> trash)
        {
            Shader lit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            Material Mat(string name, Color c, float smooth)
            {
                var m = new Material(lit) { name = name };
                m.SetColor("_BaseColor", c);
                m.SetFloat("_Smoothness", smooth);
                trash.Add(m);
                return m;
            }
            Material shell = Mat("HMD shell", new Color(0.12f, 0.12f, 0.12f), 0.32f);
            Material dark = Mat("HMD dark", new Color(0.03f, 0.03f, 0.035f), 0.6f);
            Material strap = Mat("HMD strap", new Color(0.16f, 0.16f, 0.17f), 0.2f);
            var root = new GameObject("HMD");
            trash.Add(root);
            void Piece(string name, Mesh mesh, Material m, Vector3 pos)
            {
                var g = new GameObject(name);
                g.transform.SetParent(root.transform, false);
                g.transform.localPosition = pos;
                g.AddComponent<MeshFilter>().sharedMesh = mesh;
                g.AddComponent<MeshRenderer>().sharedMaterial = m;
            }
            Piece("Visor", TwinMeshes.Ellipsoid(new Vector3(0.093f, 0.052f, 0.042f), 20, trash, "visor", 0.42f), shell, Vector3.zero);
            var front = new List<CombineInstance>();
            foreach (float x in new[] { -0.052f, 0.052f })
                TwinMeshes.AddCylinder(front, new Vector3(x, -0.005f, 0.036f), new Vector3(x, -0.005f, 0.046f), 0.011f, 16, trash);
            TwinMeshes.AddCylinder(front, new Vector3(0f, 0.012f, 0.040f), new Vector3(0f, 0.012f, 0.047f), 0.006f, 12, trash);
            Piece("Sensors", TwinMeshes.Combine(front, trash, "sensors"), dark, Vector3.zero);
            Piece("Front", TwinMeshes.Ellipsoid(new Vector3(0.088f, 0.046f, 0.012f), 20, trash, "front", 0.35f), dark, new Vector3(0f, 0f, 0.033f));
            // 前面の 3 つのセンサー（縦長の窓）
            Material pill = Mat("HMD sensor", new Color(0.2f, 0.2f, 0.21f), 0.92f);
            foreach (float x in new[] { -0.05f, 0f, 0.05f })
                Piece("Sensor", TwinMeshes.Ellipsoid(new Vector3(0.009f, 0.02f, 0.004f), 10, trash, "pill", 0.6f), pill, new Vector3(x, 0f, 0.046f));
            var band = new List<CombineInstance>();
            const int Segs = 18;
            Vector3 Ring(int k)
            {
                float a = Mathf.PI * (0.08f + 0.84f * k / Segs);
                return new Vector3(Mathf.Cos(a) * 0.088f, 0.005f, -Mathf.Sin(a) * 0.11f - 0.01f);
            }
            for (int k = 0; k < Segs; k++) TwinMeshes.AddCylinder(band, Ring(k), Ring(k + 1), 0.012f, 8, trash);
            Piece("Strap", TwinMeshes.Combine(band, trash, "strap"), strap, Vector3.zero);
            return root.transform;
        }

        // ---------- ゾーン ----------

        public static List<ZoneLayoutSolver.ZoneRect> SolveZones(ShowLayoutDef layout, out float hysteresis)
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
            hysteresis = ZoneLayoutSolver.ClampHysteresis(layout.hysteresisM, layout.overlapM);
            return ZoneLayoutSolver.SolveGrid(input);
        }

        // ---------- 部品 ----------

        private Light AddLight(GameObject root, string name, LightType type, Vector3 pos, Quaternion rot, Color color, float intensity, float range)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            go.transform.SetPositionAndRotation(pos, rot);
            var l = go.AddComponent<Light>();
            l.type = type;
            l.color = color;
            l.intensity = intensity;
            l.range = range;
            l.enabled = false;
            _lights.Add(l);
            return l;
        }

        private static Shader Need(string name)
            => Shader.Find(name) ?? throw new InvalidOperationException($"[VirtualRoom] シェーダ {name} が無い（{AssetDir}）");

        private static Material Surface(Shader sh, string? tex, Color tint, float scale, float bump, float spec, float gloss,
                                        float translucency, float grime, List<Object> trash)
        {
            var m = new Material(sh) { name = tex ?? "twin plain" };
            if (tex != null)
            {
                var t = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDir + tex + ".jpg");
                if (t == null) throw new InvalidOperationException($"[VirtualRoom] テクスチャ {AssetDir}{tex}.jpg が無い");
                m.SetTexture("_BaseMap", t);
            }
            m.SetColor("_BaseColor", tint);
            m.SetFloat("_TexScale", scale);
            m.SetFloat("_Bump", bump);
            m.SetFloat("_Spec", spec);
            m.SetFloat("_Gloss", gloss);
            m.SetFloat("_Translucency", translucency);
            m.SetFloat("_Grime", grime);
            trash.Add(m);
            return m;
        }

        private Material? _stand;
        private Material StandMat(Material metal, List<Object> trash)
        {
            if (_stand != null) return _stand;
            _stand = new Material(metal) { name = "Tripod" };
            _stand.SetColor("_BaseColor", new Color(0.05f, 0.05f, 0.055f));
            _stand.SetFloat("_Spec", 0.35f);
            _stand.SetFloat("_Gloss", 40f);
            _stand.SetFloat("_Rim", 0.35f);
            trash.Add(_stand);
            return _stand;
        }

        private static Material LineMat(Shader sh, string name, Color c, float intensity, List<Object> trash)
        {
            var m = new Material(sh) { name = name };
            m.SetColor("_Color", c);
            m.SetFloat("_Intensity", intensity);
            trash.Add(m);
            return m;
        }

        private static Material SpriteMat(Shader sh, string name, Color c, float mode, float falloff, List<Object> trash)
        {
            var m = new Material(sh) { name = name };
            m.SetColor("_Color", c);
            m.SetFloat("_Mode", mode);
            m.SetFloat("_Falloff", falloff);
            trash.Add(m);
            return m;
        }

        private static GameObject Part(GameObject root, string name, Mesh mesh, Material m, int layer)
        {
            var g = new GameObject(name) { layer = layer };
            g.transform.SetParent(root.transform, false);
            g.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = g.AddComponent<MeshRenderer>();
            r.sharedMaterial = m;
            bool solid = m.shader.name == "FixedCamVr/Twin/Surface";
            r.shadowCastingMode = solid ? ShadowCastingMode.On : ShadowCastingMode.Off;
            r.receiveShadows = solid;
            return g;
        }

        private static Vector3 P(Vector2 xz, float y) => new(xz.x, y, xz.y);

        /// <summary>線分を軸に沿った矩形で切る（Liang–Barsky）。外れていれば false。</summary>
        private static bool Clip(ref Vector2 a, ref Vector2 b, float xmin, float xmax, float zmin, float zmax)
        {
            float t0 = 0f, t1 = 1f;
            Vector2 d = b - a;
            float[] p = { -d.x, d.x, -d.y, d.y };
            float[] q = { a.x - xmin, xmax - a.x, a.y - zmin, zmax - a.y };
            for (int i = 0; i < 4; i++)
            {
                if (Mathf.Abs(p[i]) < 1e-9f) { if (q[i] < 0f) return false; continue; }
                float r = q[i] / p[i];
                if (p[i] < 0f) t0 = Mathf.Max(t0, r); else t1 = Mathf.Min(t1, r);
                if (t0 > t1) return false;
            }
            Vector2 a0 = a;
            a = a0 + d * t0;
            b = a0 + d * t1;
            return true;
        }

        private static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-8f));
            return Vector2.Distance(p, a + ab * t);
        }
    }
}
