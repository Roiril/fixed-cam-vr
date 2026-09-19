#nullable enable

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 割れた瞬間に破片と一緒に飛ぶ光の粒（canon/LEDGER.md 0235）。
    /// CPU のパーティクルは持たず、<b>時計 p から頂点シェーダが決定的に動かす静的メッシュ 1 つ</b>で出す
    /// （周辺の破片 <see cref="IntroPeripheralFractureMesh"/> と同じ作法）。
    ///
    /// 1 粒 = 四角 1 枚（4 頂点・2 三角）。流れる粒 <see cref="FlowSparkCount"/> ＋
    /// 代表の粒 <see cref="HeroSparkCount"/> ＝ 518 枚 / 2072 頂点なので <c>UInt16</c> で足りる。
    ///
    /// <b>頂点属性（<c>Assets/Resources/IntroSpark.shader</c> の Attributes と同じ並び。
    /// 片方だけ直すと黙って食い違う）:</b>
    ///
    /// | 属性 | 中身 |
    /// |---|---|
    /// | POSITION | 放出点（覆いのローカル ±0.30）の xy・z = 0 |
    /// | TEXCOORD0 | 四隅の符号 (±1, ±1) |
    /// | TEXCOORD1 | (seedA, seedB, kind 0 = 流れ / 1 = 代表, heroIndex) |
    /// | TEXCOORD2 | (macroOrder, macroIndex, pieceNoise 0..1, size) |
    /// | TEXCOORD3 | (star 上位 20% = 1, sizeMul 0.6..1.8, deathP .62..80, paletteT 0..1) |
    ///
    /// 放出点は片の<b>外周の辺の上</b>（頂点シェーダが同じ点を
    /// <c>FractureShellPoint</c> でシェルへ載せるので、破片と同じ面から生まれる）。
    ///
    /// <b>割れた片は 1 枚残らず粒を出す</b>（粒は「破片と一緒に飛ぶ」もの）。全片へ 1 本ずつ配ってから、
    /// 残りを sqrt(面積) に比例して配る ＝ 大きい片ほど多い。
    /// </summary>
    public static class IntroSparkMesh
    {
        /// <summary>流れる粒の総数。頂点数の上限（UInt16）を決めているのでここだけで変える。</summary>
        public const int FlowSparkCount = 512;

        /// <summary>代表の粒の数。<c>IntroSpark.hlsl</c> の <c>HeroSparkCount</c> と対。</summary>
        public const int HeroSparkCount = 6;

        /// <summary>
        /// 片 1 枚あたりの最低本数。<b>粒は「破片と一緒に飛ぶ」ものなので、割れた片は 1 枚残らず
        /// 粒を出す</b>（本数を稼ぐために片を絞らない）。残りを大きさで配る。
        /// </summary>
        public const int MinSparksPerPiece = 1;

        private const int Seed = 20260919;

        /// <summary>直近に組んだ粒の総数（流れ ＋ 代表）。</summary>
        public static int LastSparkCount { get; private set; }

        /// <summary>直近に組んだ流れる粒の数。</summary>
        public static int LastFlowCount { get; private set; }

        /// <summary>直近に粒を出した片の枚数（全片。512 枚を超える版でだけ絞られる）。</summary>
        public static int LastEmittingPieceCount { get; private set; }

        public static Mesh Build(IReadOnlyList<IntroFractureMesh.PieceInfo>? pieces)
        {
            var random = new System.Random(Seed);
            int[] perPiece = pieces == null || pieces.Count == 0
                ? Array.Empty<int>()
                : Allocate(pieces);

            int flowCount = 0;
            int emittingPieces = 0;
            for (int i = 0; i < perPiece.Length; i++)
            {
                flowCount += perPiece[i];
                if (perPiece[i] > 0) emittingPieces++;
            }
            int sparkCount = flowCount + HeroSparkCount;

            var positions = new List<Vector3>(sparkCount * 4);
            var corners = new List<Vector2>(sparkCount * 4);
            var seeds = new List<Vector4>(sparkCount * 4);
            var pieceData = new List<Vector4>(sparkCount * 4);
            var look = new List<Vector4>(sparkCount * 4);
            var triangles = new List<int>(sparkCount * 6);

            for (int pieceIndex = 0; pieceIndex < perPiece.Length; pieceIndex++)
            {
                int count = perPiece[pieceIndex];
                if (count <= 0) continue;
                IntroFractureMesh.PieceInfo piece = pieces![pieceIndex];
                float pieceNoise = (float)random.NextDouble();
                var data = new Vector4(piece.macroOrder, pieceIndex, pieceNoise, piece.size);
                for (int i = 0; i < count; i++)
                {
                    Vector2 origin = PointOnOutline(piece.outline, random.NextDouble());
                    var seed = new Vector4(
                        (float)random.NextDouble(), (float)random.NextDouble(), 0f, 0f);
                    AddSpark(positions, corners, seeds, pieceData, look, triangles,
                        origin, seed, data, NextLook(random));
                }
            }

            for (int hero = 0; hero < HeroSparkCount; hero++)
            {
                // 代表の粒は位置も明るさも IntroSpark.hlsl の HeroSpark が決める。
                // メッシュが運ぶのは種類と番号だけ（放出点は読まれない）。
                var seed = new Vector4(
                    (float)random.NextDouble(), (float)random.NextDouble(), 1f, hero);
                Vector4 heroLook = NextLook(random);
                heroLook.x = 1f;   // 代表は必ず十字の光芒を持つ
                AddSpark(positions, corners, seeds, pieceData, look, triangles,
                    Vector2.zero, seed, new Vector4(0f, -1f, 0f, 0f), heroLook);
            }

            LastFlowCount = flowCount;
            LastSparkCount = sparkCount;
            LastEmittingPieceCount = emittingPieces;

            var mesh = new Mesh
            {
                name = "IntroSparks",
                // 518 枚 × 4 頂点 = 2072。UInt16 の 65535 に十分収まる。
                indexFormat = IndexFormat.UInt16,
            };
            mesh.SetVertices(positions);
            mesh.SetUVs(0, corners);
            mesh.SetUVs(1, seeds);
            mesh.SetUVs(2, pieceData);
            mesh.SetUVs(3, look);
            mesh.SetTriangles(triangles, 0);
            // 頂点シェーダが撮影時の頭の空間からスクリーンまで運ぶ。元の平面の bounds では
            // 頭を動かした瞬間に全部カリングされる（破片メッシュと同じ理由）。
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 200f);
            mesh.UploadMeshData(markNoLongerReadable: false);
            return mesh;
        }

        /// <summary>
        /// <b>全部の片へまず 1 本</b>ずつ配り、残り（512 − 片数）を sqrt(面積) に比例して配る。
        /// 端数は大きい片から（同値は添字順）なので決定的。合計は必ず <see cref="FlowSparkCount"/>。
        /// ⚠ 片が 512 枚を超える版になったら、大きい方から 512 枚だけが 1 本ずつ出す。
        /// </summary>
        private static int[] Allocate(IReadOnlyList<IntroFractureMesh.PieceInfo> pieces)
        {
            int count = pieces.Count;
            var counts = new int[count];
            var order = new int[count];
            for (int i = 0; i < count; i++) order[i] = i;
            Array.Sort(order, (a, b) =>
            {
                int bySize = pieces[b].size.CompareTo(pieces[a].size);
                return bySize != 0 ? bySize : a.CompareTo(b);
            });

            if (count >= FlowSparkCount)
            {
                for (int i = 0; i < FlowSparkCount; i++) counts[order[i]] = MinSparksPerPiece;
                return counts;
            }

            double total = 0d;
            for (int i = 0; i < count; i++)
            {
                counts[i] = MinSparksPerPiece;
                total += pieces[i].size;
            }
            int assigned = count * MinSparksPerPiece;
            int extra = FlowSparkCount - assigned;
            if (extra <= 0 || total <= 0d) return counts;

            var remainder = new double[count];
            for (int i = 0; i < count; i++)
            {
                double exact = extra * pieces[i].size / total;
                int floor = (int)Math.Floor(exact);
                remainder[i] = exact - floor;
                counts[i] += floor;
                assigned += floor;
            }

            // 端数は大きい片から 1 本ずつ。合計は必ず FlowSparkCount ちょうどになる。
            var byRemainder = new int[count];
            for (int i = 0; i < count; i++) byRemainder[i] = i;
            Array.Sort(byRemainder, (a, b) =>
            {
                int byFraction = remainder[b].CompareTo(remainder[a]);
                if (byFraction != 0) return byFraction;
                int bySize = pieces[b].size.CompareTo(pieces[a].size);
                return bySize != 0 ? bySize : a.CompareTo(b);
            });
            for (int i = 0; assigned < FlowSparkCount; i = (i + 1) % count)
            {
                counts[byRemainder[i]]++;
                assigned++;
            }
            return counts;
        }

        /// <summary>外周の辺の上の点（周長で等分布・<paramref name="t"/> は 0..1）。</summary>
        private static Vector2 PointOnOutline(Vector2[] outline, double t)
        {
            if (outline == null || outline.Length == 0) return Vector2.zero;
            if (outline.Length == 1) return outline[0];

            double perimeter = 0d;
            for (int i = 0; i < outline.Length; i++)
                perimeter += (outline[(i + 1) % outline.Length] - outline[i]).magnitude;
            if (perimeter <= 0d) return outline[0];

            double walk = Math.Min(Math.Max(t, 0d), 0.999999d) * perimeter;
            for (int i = 0; i < outline.Length; i++)
            {
                Vector2 a = outline[i];
                Vector2 b = outline[(i + 1) % outline.Length];
                double length = (b - a).magnitude;
                if (walk <= length || i == outline.Length - 1)
                    return Vector2.Lerp(a, b, length > 0d ? (float)(walk / length) : 0f);
                walk -= length;
            }
            return outline[0];
        }

        /// <summary>
        /// 粒ごとの見え方。star は上位 20%（抽選値 0.8 以上）だけが十字の光芒を持つ。
        /// </summary>
        private static Vector4 NextLook(System.Random random)
        {
            float star = random.NextDouble() >= 0.8d ? 1f : 0f;
            float sizeMul = Mathf.Lerp(0.6f, 1.8f, (float)random.NextDouble());
            float deathP = Mathf.Lerp(0.62f, 0.80f, (float)random.NextDouble());
            float paletteT = (float)random.NextDouble();
            return new Vector4(star, sizeMul, deathP, paletteT);
        }

        private static void AddSpark(
            List<Vector3> positions,
            List<Vector2> corners,
            List<Vector4> seeds,
            List<Vector4> pieceData,
            List<Vector4> look,
            List<int> triangles,
            Vector2 origin,
            Vector4 seed,
            Vector4 piece,
            Vector4 lookData)
        {
            int first = positions.Count;
            var position = new Vector3(origin.x, origin.y, 0f);
            AddCorner(positions, corners, seeds, pieceData, look, position, new Vector2(-1f, -1f), seed, piece, lookData);
            AddCorner(positions, corners, seeds, pieceData, look, position, new Vector2(1f, -1f), seed, piece, lookData);
            AddCorner(positions, corners, seeds, pieceData, look, position, new Vector2(-1f, 1f), seed, piece, lookData);
            AddCorner(positions, corners, seeds, pieceData, look, position, new Vector2(1f, 1f), seed, piece, lookData);
            triangles.Add(first);
            triangles.Add(first + 2);
            triangles.Add(first + 1);
            triangles.Add(first + 2);
            triangles.Add(first + 3);
            triangles.Add(first + 1);
        }

        private static void AddCorner(
            List<Vector3> positions,
            List<Vector2> corners,
            List<Vector4> seeds,
            List<Vector4> pieceData,
            List<Vector4> look,
            Vector3 position,
            Vector2 corner,
            Vector4 seed,
            Vector4 piece,
            Vector4 lookData)
        {
            positions.Add(position);
            corners.Add(corner);
            seeds.Add(seed);
            pieceData.Add(piece);
            look.Add(lookData);
        }
    }
}
