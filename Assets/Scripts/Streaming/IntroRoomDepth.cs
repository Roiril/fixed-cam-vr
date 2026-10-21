#nullable enable

using System;
using System.Collections.Generic;
using FixedCamVr.Streaming.Cg;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 段 4 の破片を撮影時の部屋の奥行きへ置く（R061・canon/LEDGER.md 0264 の続き）。
    ///
    /// 破片はシェーダの中では撮影時の頭から半径 <see cref="ShellRadius"/> の殻に乗っている（「名目の位置」）。
    /// 開始位置は L 字の壁から約 0.4m なので、殻のままだと割れる前の画が壁より奥に描かれて頭の動きで滑る。
    /// ここでは撮影の瞬間に 1 回だけ、殻の各片の向きを部屋プロキシ（<c>layout.room</c> の壁・箱、床、
    /// 遠方 <see cref="FarRadiusM"/> の球）へ光線で当て、<b>片ごとに 1 つの面</b>を選んで頂点をその平面へ置き直す。
    /// 返すのは「名目の半径に対する倍率」で、シェーダは撮影時の頭を中心にその倍率で縮めるだけ
    /// （撮影位置から見た片眼の絵は変わらない）。
    ///
    /// - 面は片の中心と外周の頂点の多数決で選ぶ（中心は 2 票）。壁の縁をまたぐ片も 1 枚の平面に乗り、
    ///   縁の向こう 4m まで伸びた板にならない（はみ出した側は平面の延長に乗る）
    /// - 片の中の奥行きの比は <see cref="MaxStretch"/> まで（光線が平面をかすめても伸び切らない）
    /// - 最短 <see cref="MinRadiusM"/>（眼の手前に破片を置かない）・最長 <see cref="FarRadiusM"/>
    /// - 部屋が無い（未登録・未著作）ときは呼び出し側が倍率 1 ＝ 従来の 1.6m の殻へ落とす
    /// </summary>
    public static class IntroRoomDepth
    {
        /// <summary>名目の殻の半径（m）。<c>IntroFractureTime.hlsl</c> の FractureShellPoint と
        /// <see cref="IntroPeripheralFractureMesh.Radius"/> と同じ値。</summary>
        public const float ShellRadius = 1.6f;
        public const float MinRadiusM = 0.6f;
        public const float FarRadiusM = 4.0f;
        /// <summary>片の中の奥行きの比の上限（中心の奥行きの 1/2 〜 2 倍）。</summary>
        public const float MaxStretch = 2.0f;
        /// <summary>覆いのローカル座標 → tan 空間（FractureShellPoint の 1 / 0.15）。</summary>
        public const float LocalToSlope = 1f / 0.15f;

        public const int FarSurface = -1;
        public const int FloorSurface = 1_000_000;

        /// <summary>world 空間の箱。軸は単位ベクトル・<c>half</c> は各軸の半寸。</summary>
        public struct Box
        {
            public Vector3 center;
            public Vector3 axisX;
            public Vector3 axisY;
            public Vector3 axisZ;
            public Vector3 half;
        }

        /// <summary>光線を当てる部屋（world 空間）。</summary>
        public sealed class Scene
        {
            public readonly List<Box> boxes = new List<Box>();
            public bool hasFloor;
            public Vector3 floorPoint;
            public Vector3 floorUp = Vector3.up;
            public float minRadius = MinRadiusM;
            public float farRadius = FarRadiusM;
        }

        /// <summary>光線が当たった面。<c>surface</c> = 箱 i の面 f は i*6+f / 床 <see cref="FloorSurface"/> /
        /// 遠方の球 <see cref="FarSurface"/>。平面は dot(normal, x) = offset。</summary>
        public struct Hit
        {
            public int surface;
            public float distance;
            public Vector3 normal;
            public float offset;
        }

        /// <summary>
        /// <c>layout.room</c> から光線を当てる部屋を組む。箱は <see cref="ShowRoomProxyLogic.Build"/>
        /// （CG のオクルーダ・隔離の殻と<b>同じ幾何</b>）、床は course の floorY の無限平面。
        /// 回転の規約は <see cref="ContainmentShellLogic.ToWorld"/> と同じ。
        /// </summary>
        public static Scene Build(ShowRoomDef? room, Func<Vector2, float, Vector3> courseToWorld,
                                  float courseYawDeg)
        {
            var scene = new Scene();
            float floorY = room != null ? room.floorY : 0f;
            scene.hasFloor = true;
            scene.floorPoint = courseToWorld(Vector2.zero, floorY);
            scene.floorUp = Vector3.up;
            foreach (ShowRoomProxyLogic.Box b in ShowRoomProxyLogic.Build(room))
            {
                Quaternion rot = Quaternion.Euler(0f, courseYawDeg + b.yawDeg, 0f);
                scene.boxes.Add(new Box
                {
                    center = courseToWorld(new Vector2(b.center.x, b.center.z), b.center.y),
                    axisX = rot * Vector3.right,
                    axisY = Vector3.up,
                    axisZ = rot * Vector3.forward,
                    half = b.size * 0.5f,
                });
            }
            return scene;
        }

        /// <summary>最も近い面。何にも当たらなければ遠方の球（<see cref="Scene.farRadius"/>）。</summary>
        public static Hit Raycast(Scene scene, Vector3 origin, Vector3 direction)
        {
            var best = new Hit { surface = FarSurface, distance = scene.farRadius };
            for (int i = 0; i < scene.boxes.Count; i++)
            {
                if (TryBox(scene.boxes[i], origin, direction, out float t, out int face, out Vector3 n)
                    && t < best.distance)
                {
                    best = new Hit
                    {
                        surface = i * 6 + face,
                        distance = t,
                        normal = n,
                        offset = Vector3.Dot(n, origin + direction * t),
                    };
                }
            }
            if (scene.hasFloor)
            {
                float facing = Vector3.Dot(direction, scene.floorUp);
                float height = Vector3.Dot(origin - scene.floorPoint, scene.floorUp);
                if (facing < -1e-4f && height > 0f)
                {
                    float t = height / -facing;
                    if (t < best.distance)
                    {
                        best = new Hit
                        {
                            surface = FloorSurface,
                            distance = t,
                            normal = scene.floorUp,
                            offset = Vector3.Dot(scene.floorUp, scene.floorPoint),
                        };
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// 片 1 枚ぶんの奥行き（m）。<paramref name="directions"/> は外周の頂点の向き（world・単位）、
        /// <paramref name="center"/> は片の中心の向き。返り値は頂点ごとの半径、<paramref name="centerRadius"/> は中心の半径。
        /// </summary>
        public static float[] SolvePiece(Scene scene, Vector3 origin, Vector3 center,
                                         IReadOnlyList<Vector3> directions, out float centerRadius)
        {
            Hit centerHit = Raycast(scene, origin, center);
            var votes = new Dictionary<int, int> { [centerHit.surface] = 2 };
            var hits = new Hit[directions.Count];
            for (int i = 0; i < directions.Count; i++)
            {
                hits[i] = Raycast(scene, origin, directions[i]);
                votes.TryGetValue(hits[i].surface, out int count);
                votes[hits[i].surface] = count + 1;
            }

            int chosen = centerHit.surface;
            int chosenVotes = votes[chosen];
            foreach (KeyValuePair<int, int> vote in votes)
            {
                // 同数なら中心の面（決定的・中心がいちばん目に付く）。
                if (vote.Value > chosenVotes)
                {
                    chosen = vote.Key;
                    chosenVotes = vote.Value;
                }
            }

            var radii = new float[directions.Count];
            float lo = scene.minRadius;
            float hi = Mathf.Max(scene.farRadius, lo);
            if (chosen == FarSurface)
            {
                centerRadius = hi;
                for (int i = 0; i < radii.Length; i++) radii[i] = hi;
                return radii;
            }

            // 選んだ面の平面（中心がその面に当たっていなければ、当たった頂点から取る）。
            Hit plane = centerHit;
            if (centerHit.surface != chosen)
                for (int i = 0; i < hits.Length; i++)
                    if (hits[i].surface == chosen) { plane = hits[i]; break; }

            float reference = TryPlane(plane, origin, center, out float tc) ? tc : float.NaN;
            if (float.IsNaN(reference))
            {
                // 中心の光線が平面に届かない（裏を向いている）。その面に当たった頂点の中央値で代える。
                var onPlane = new List<float>();
                for (int i = 0; i < hits.Length; i++)
                    if (hits[i].surface == chosen) onPlane.Add(hits[i].distance);
                onPlane.Sort();
                reference = onPlane.Count > 0 ? onPlane[onPlane.Count / 2] : hi;
            }
            reference = Mathf.Clamp(reference, lo, hi);
            centerRadius = reference;
            float near = Mathf.Max(lo, reference / MaxStretch);
            float far = Mathf.Min(hi, reference * MaxStretch);
            for (int i = 0; i < radii.Length; i++)
            {
                float t = TryPlane(plane, origin, directions[i], out float tv) ? tv : far;
                radii[i] = Mathf.Clamp(t, near, far);
            }
            return radii;
        }

        // ---- メッシュへの書き込み（撮影の瞬間に 1 回）----

        /// <summary>中央の破片の UV チャンネル（x = 頂点の倍率 / y = 片の中心の倍率）。<c>IntroFracture.shader</c> の TEXCOORD5。</summary>
        public const int FractureChannel = 5;
        /// <summary>周辺の破片の UV チャンネル（x = 頂点 / y = 片の中心）。<c>IntroPeripheralFracture.shader</c> の TEXCOORD4。</summary>
        public const int PeripheralChannel = 4;
        /// <summary>光の粒の UV チャンネル（x = 放出した片の倍率）。<c>IntroSpark.shader</c> の TEXCOORD4。</summary>
        public const int SparkChannel = 4;

        /// <summary>
        /// 代表の粒の起点（覆いのローカル）。<c>IntroSpark.hlsl</c> の HeroSpark が
        /// tan 空間 (-0.16, 0.12) から飛ばすので、ローカルは × 0.15。
        /// </summary>
        public static readonly Vector2 HeroOriginLocal = new Vector2(-0.16f * 0.15f, 0.12f * 0.15f);

        /// <summary>書き込んだ結果。<c>shatDepth=</c> の中身。</summary>
        public struct Result
        {
            public bool applied;
            public int pieces;
            public float minRadius;
            public float medianRadius;
            public float maxRadius;
            /// <summary>片ごとの中心の倍率（メッシュの片の順）。光の粒が引く。</summary>
            public float[] pieceScale;
            /// <summary>代表の粒の倍率（起点を含む片の中心）。</summary>
            public float heroScale;

            public string Describe(string mode) => applied
                ? FormattableString.Invariant($"{mode}:{minRadius:0.00}/{medianRadius:0.00}/{maxRadius:0.00}")
                : mode;
        }

        /// <summary>名目の殻の点（覆いのローカル → 撮影時の頭の空間）。FractureShellPoint と同じ式。</summary>
        public static Vector3 ShellPoint(Vector2 local)
            => new Vector3(local.x * LocalToSlope, local.y * LocalToSlope, 1f).normalized * ShellRadius;

        /// <summary>
        /// 中央の破片へ倍率を書く。<paramref name="scene"/> が null なら全部 1（従来の 1.6m の殻）。
        /// 片は Build が頂点を連続で積むので、uv1.xy（片の重心）が変わる所で区切る。
        /// </summary>
        public static Result ApplyFracture(Mesh mesh, Matrix4x4 captureHeadToWorld, Scene? scene)
        {
            Vector3[] vertices = mesh.vertices;
            var pieceData = new List<Vector4>(vertices.Length);
            var surfaces = new List<Vector4>(vertices.Length);
            mesh.GetUVs(1, pieceData);
            mesh.GetUVs(3, surfaces);
            var scales = new List<Vector2>(vertices.Length);
            var pieceScale = new List<float>();
            var centerRadii = new List<float>();
            float heroScale = 1f;
            bool heroFound = false;
            float nearestHero = float.PositiveInfinity;

            Vector3 origin = captureHeadToWorld.MultiplyPoint3x4(Vector3.zero);
            var keys = new List<Vector2>();
            var directions = new List<Vector3>();
            var nominalLengths = new List<float>();
            var front = new List<Vector2>();
            int start = 0;
            while (start < vertices.Length)
            {
                var key = new Vector2(pieceData[start].x, pieceData[start].y);
                int end = start;
                while (end < vertices.Length && pieceData[end].x == key.x && pieceData[end].y == key.y) end++;

                keys.Clear();
                directions.Clear();
                nominalLengths.Clear();
                front.Clear();
                for (int i = start; i < end; i++)
                {
                    var local = new Vector2(vertices[i].x, vertices[i].y);
                    if (surfaces.Count == vertices.Length && surfaces[i].x == IntroFractureMesh.FrontSurface)
                        front.Add(local);
                    if (keys.Contains(local)) continue;
                    keys.Add(local);
                    Vector3 nominal = captureHeadToWorld.MultiplyVector(ShellPoint(local));
                    nominalLengths.Add(nominal.magnitude);
                    directions.Add(nominal.normalized);
                }
                Vector3 centerNominal = captureHeadToWorld.MultiplyVector(ShellPoint(key));
                float centerLength = Mathf.Max(centerNominal.magnitude, 1e-5f);

                float centerScale = 1f;
                float[]? radii = null;
                if (scene != null)
                {
                    radii = SolvePiece(scene, origin, centerNominal.normalized, directions, out float rc);
                    centerScale = rc / centerLength;
                    centerRadii.Add(rc);
                }
                for (int i = start; i < end; i++)
                {
                    float vertexScale = 1f;
                    if (radii != null)
                    {
                        int k = keys.IndexOf(new Vector2(vertices[i].x, vertices[i].y));
                        vertexScale = radii[k] / Mathf.Max(nominalLengths[k], 1e-5f);
                    }
                    scales.Add(new Vector2(vertexScale, centerScale));
                }

                // 代表の粒の起点を含む片（含む片が無ければ中心がいちばん近い片）。
                bool inside = front.Count >= 3 && Contains(front, HeroOriginLocal);
                float distance = (key - HeroOriginLocal).sqrMagnitude;
                if (inside || (!heroFound && distance < nearestHero))
                {
                    heroScale = centerScale;
                    heroFound |= inside;
                    nearestHero = Mathf.Min(nearestHero, distance);
                }
                pieceScale.Add(centerScale);
                start = end;
            }
            mesh.SetUVs(FractureChannel, scales);

            var result = new Result
            {
                applied = scene != null,
                pieces = pieceScale.Count,
                pieceScale = pieceScale.ToArray(),
                heroScale = heroScale,
            };
            if (centerRadii.Count > 0)
            {
                centerRadii.Sort();
                result.minRadius = centerRadii[0];
                result.medianRadius = centerRadii[centerRadii.Count / 2];
                result.maxRadius = centerRadii[centerRadii.Count - 1];
            }
            return result;
        }

        /// <summary>周辺の破片へ倍率を書く。片は uv1.w（片の番号）が変わる所で区切る。中心は uv1.xyz。</summary>
        public static void ApplyPeripheral(Mesh mesh, Matrix4x4 captureHeadToWorld, Scene? scene)
        {
            Vector3[] vertices = mesh.vertices;
            var pieceData = new List<Vector4>(vertices.Length);
            mesh.GetUVs(1, pieceData);
            var scales = new List<Vector2>(vertices.Length);
            Vector3 origin = captureHeadToWorld.MultiplyPoint3x4(Vector3.zero);
            var directions = new List<Vector3>();
            var lengths = new List<float>();
            int start = 0;
            while (start < vertices.Length)
            {
                float id = pieceData[start].w;
                int end = start;
                while (end < vertices.Length && pieceData[end].w == id) end++;
                if (scene == null)
                {
                    for (int i = start; i < end; i++) scales.Add(Vector2.one);
                    start = end;
                    continue;
                }
                directions.Clear();
                lengths.Clear();
                for (int i = start; i < end; i++)
                {
                    Vector3 nominal = captureHeadToWorld.MultiplyVector(vertices[i]);
                    lengths.Add(Mathf.Max(nominal.magnitude, 1e-5f));
                    directions.Add(nominal.normalized);
                }
                Vector4 c = pieceData[start];
                Vector3 centerNominal = captureHeadToWorld.MultiplyVector(new Vector3(c.x, c.y, c.z));
                float[] radii = SolvePiece(scene, origin, centerNominal.normalized, directions, out float rc);
                float centerScale = rc / Mathf.Max(centerNominal.magnitude, 1e-5f);
                for (int i = 0; i < radii.Length; i++) scales.Add(new Vector2(radii[i] / lengths[i], centerScale));
                start = end;
            }
            mesh.SetUVs(PeripheralChannel, scales);
        }

        /// <summary>光の粒へ倍率を書く。流れる粒は放出した片（uv2.y）、代表の粒（uv2.y = -1）は起点の片。</summary>
        public static void ApplySparks(Mesh mesh, in Result fracture)
        {
            var pieceData = new List<Vector4>();
            mesh.GetUVs(2, pieceData);
            var scales = new List<Vector2>(pieceData.Count);
            float[]? pieces = fracture.applied ? fracture.pieceScale : null;
            for (int i = 0; i < pieceData.Count; i++)
            {
                float scale = 1f;
                if (pieces != null)
                {
                    int index = Mathf.RoundToInt(pieceData[i].y);
                    scale = index >= 0 && index < pieces.Length ? pieces[index] : fracture.heroScale;
                }
                scales.Add(new Vector2(scale, 0f));
            }
            mesh.SetUVs(SparkChannel, scales);
        }

        /// <summary>倍率 1（従来の 1.6m の殻）を書く。Build 直後に頂点の並びを決めておく。</summary>
        public static void Reset(Mesh? fracture, Mesh? peripheral, Mesh? sparks)
        {
            if (fracture != null) ApplyFracture(fracture, Matrix4x4.identity, null);
            if (peripheral != null) ApplyPeripheral(peripheral, Matrix4x4.identity, null);
            if (sparks != null) ApplySparks(sparks, default);
        }

        private static bool Contains(List<Vector2> polygon, Vector2 point)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                Vector2 a = polygon[i];
                Vector2 b = polygon[j];
                if ((a.y > point.y) != (b.y > point.y)
                    && point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x)
                    inside = !inside;
            }
            return inside;
        }

        private static bool TryPlane(in Hit plane, Vector3 origin, Vector3 direction, out float t)
        {
            float denominator = Vector3.Dot(plane.normal, direction);
            t = 0f;
            if (Mathf.Abs(denominator) < 1e-5f) return false;
            t = (plane.offset - Vector3.Dot(plane.normal, origin)) / denominator;
            return t > 1e-4f && !float.IsNaN(t) && !float.IsInfinity(t);
        }

        /// <summary>光線と箱（slab）。外から入る面だけを返す（頭が箱の中に居たら当たらない扱い）。</summary>
        private static bool TryBox(in Box box, Vector3 origin, Vector3 direction,
                                   out float t, out int face, out Vector3 normal)
        {
            t = 0f;
            face = 0;
            normal = Vector3.zero;
            Vector3 relative = origin - box.center;
            float tEnter = float.NegativeInfinity;
            float tExit = float.PositiveInfinity;
            int enterAxis = -1;
            float enterSign = 0f;
            for (int axis = 0; axis < 3; axis++)
            {
                Vector3 a = axis == 0 ? box.axisX : axis == 1 ? box.axisY : box.axisZ;
                float h = axis == 0 ? box.half.x : axis == 1 ? box.half.y : box.half.z;
                float o = Vector3.Dot(relative, a);
                float d = Vector3.Dot(direction, a);
                if (Mathf.Abs(d) < 1e-8f)
                {
                    if (o < -h || o > h) return false;
                    continue;
                }
                float t0 = (-h - o) / d;
                float t1 = (h - o) / d;
                float sign = -1f;   // t0 は -h 側の面
                if (t0 > t1)
                {
                    (t0, t1) = (t1, t0);
                    sign = 1f;
                }
                if (t0 > tEnter)
                {
                    tEnter = t0;
                    enterAxis = axis;
                    enterSign = sign;
                }
                if (t1 < tExit) tExit = t1;
                if (tEnter > tExit) return false;
            }
            if (enterAxis < 0 || tEnter <= 1e-4f || tEnter > tExit) return false;
            t = tEnter;
            Vector3 n = enterAxis == 0 ? box.axisX : enterAxis == 1 ? box.axisY : box.axisZ;
            normal = n * enterSign;
            face = enterAxis * 2 + (enterSign > 0f ? 1 : 0);
            return true;
        }
    }
}
