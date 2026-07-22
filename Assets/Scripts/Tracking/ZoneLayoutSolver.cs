#nullable enable
using System.Collections.Generic;
using UnityEngine;
using UDebug = UnityEngine.Debug;

namespace FixedCamVr.Tracking
{
    /// <summary>
    /// course space のカット・モデル（ループ上の切れ目 s ∈ [0,1) でカメラ区間を区切る）から
    /// 軸整列の矩形ゾーン群（<see cref="ZoneRect"/>）へ**決定的に展開する純関数**。
    ///
    /// course space:
    ///   - 原点 = フロア中心、+Z = 北、+X = 東、単位 m。
    ///   - 正準ループ = 矩形 x=±(floorW/2 - <see cref="LoopInset"/>), z=±(floorD/2 - LoopInset)
    ///     （床端から LoopInset だけ内側）。floor 1.8×1.8 なら x,z=±0.7。
    ///   - s=0 = 南辺中央 (0,-loopHalfZ)。時計回り（南→東→北→西）に周長で正規化して増加。
    ///     正方フロアではコーナー s = 0.125(SE) / 0.375(NE) / 0.625(NW) / 0.875(SW)。
    ///
    /// cuts:
    ///   - s 昇順（Solve 内で防御的にソートする）。セグメント i = cuts[i].s 〜 cuts[i+1].s（末尾 wrap）で
    ///     cuts[i].camAfter のカメラ。
    ///   - 隣接ゾーンはカット（辺の途中）を中心に計 overlapM 重なる（各ゾーンをカットから overlapM/2 ずつ延長）。
    ///   - セグメントがループのコーナーを跨ぐ場合は辺ごとに矩形へ分割し、同一 cameraIndex を割る。
    ///     コーナーで隣接辺の矩形へ延長して隙間なく重ねる。
    ///
    /// Unity 依存は Vector2/Mathf の struct/静的算術のみ（MonoBehaviour 非依存 → EditMode テスト容易）。
    /// </summary>
    public static class ZoneLayoutSolver
    {
        /// <summary>床端から正準ループまでの内側マージン (m)。floor 1.8 → loopHalf 0.7。</summary>
        public const float LoopInset = 0.2f;

        /// <summary>矩形のループ直交方向の外側限界を「床端 + このマージン」に置く (m)。
        /// 現行 authored（A:South 等）の ±1.35 相当（Quest ガーディアン ±1.3m プレイレンジ）に合わせた値。</summary>
        public const float OuterMargin = 0.45f;

        /// <summary>矩形のループ直交方向の内側限界（course 中心側）の絶対座標 (m)。
        /// 現行 authored の |0.25|（loop 0.7 の内側 0.45）に一致。</summary>
        public const float PerpInner = 0.25f;

        private const float Eps = 1e-3f;

        // ---- 入出力 ----

        /// <summary>Solve への入力。show.json layout をこの純データへ落として渡す。</summary>
        public struct ZoneLayoutInput
        {
            public float floorW;
            public float floorD;
            public float overlapM;
            /// <summary>PlayerZoneTracker.hysteresisShrink へ流す値（Solve は使わず applier がそのまま適用）。</summary>
            public float hysteresisM;
            public CutInput[] cuts;
        }

        /// <summary>ループ上の切れ目。s ∈ [0,1)、camAfter = このカット以降（次カットまで）のカメラ index。</summary>
        public struct CutInput
        {
            public float s;
            public int camAfter;
        }

        /// <summary>展開結果の 1 矩形（course space・XZ 軸整列）。y はここでは扱わない（applier が付与）。</summary>
        public struct ZoneRect
        {
            public float centerX;
            public float centerZ;
            public float halfX;
            public float halfZ;
            public int cameraIndex;
            public int priority;
            public string label;
        }

        // ---- v2: タイルペイント（grid）モデル ----

        /// <summary>layout ソースの選択結果。grid があれば grid、無ければ cuts、どちらも無ければ None。</summary>
        public enum LayoutSource { None, Cuts, Grid }

        /// <summary>
        /// grid（タイルペイント）モデルの Solve 入力。
        /// cells は row-major（<c>cells[r*cols + c]</c>）で、値は カメラ index（0..8）または未割当を表す -1。
        /// row 0 = 北端（z=+d/2 側）、col 0 = 西端（x=-w/2）。
        /// </summary>
        public struct GridLayoutInput
        {
            // NOTE: SolveGrid はセル→course 変換を <see cref="CellRect"/>（cols·rows·tileM から NW 角アンカーで
            // 導出）に一本化したため floorW/floorD は読まなくなった。呼び出し側の寸法検証・後方互換のため残す
            // （Web 卓は floor = cols·tileM で grid をオーサリングするので通常一致する）。
            public float floorW;
            public float floorD;
            public float overlapM;
            /// <summary>PlayerZoneTracker.hysteresisShrink へ流す値（Solve は使わず applier がそのまま適用）。</summary>
            public float hysteresisM;
            public float tileM;
            public int cols;
            public int rows;
            /// <summary>長さ rows*cols、row-major。-1 = 未割当、0..8 = カメラ index。</summary>
            public int[] cells;
        }

        private enum Edge { South = 0, East = 1, North = 2, West = 3 }

        // ---- 本体 ----

        /// <summary>
        /// layout 入力を course space の矩形ゾーン群へ展開する。配列順 = セグメント順（cuts 昇順）→
        /// セグメント内は South→East→North→West。この順が PlayerZoneTracker の同優先度タイブレーク
        /// （配列先頭が勝つ）に効く：コーナーの重なりでは cuts 昇順で先のセグメントが勝つ。
        /// </summary>
        public static List<ZoneRect> Solve(ZoneLayoutInput input)
        {
            var result = new List<ZoneRect>(8);
            if (input.cuts == null || input.cuts.Length == 0) return result;

            float floorHalfX = input.floorW * 0.5f;
            float floorHalfZ = input.floorD * 0.5f;
            float hX = Mathf.Max(0.01f, floorHalfX - LoopInset); // ループ半幅（X）
            float hZ = Mathf.Max(0.01f, floorHalfZ - LoopInset); // ループ半奥行（Z）
            float outerX = floorHalfX + OuterMargin;             // 沿走/直交の外側限界（X 軸）
            float outerZ = floorHalfZ + OuterMargin;             // 同（Z 軸）
            float overlapHalf = Mathf.Max(0f, input.overlapM) * 0.5f;

            // ループ周長（南2hX + 東2hZ + 北2hX + 西2hZ = 4(hX+hZ)）と 5 ピース（南は s 原点で 2 分割）。
            // arc は非正規（m 単位）。
            float P = 4f * (hX + hZ);

            // ピース: (edge, arcStart, arcEnd, alongStart, alongEnd)
            // South は P0(原点→SE) と P4(SW→原点) に割れる。
            float c0 = 0f;
            float c1 = hX;              // P0 end (SE corner)
            float c2 = hX + 2f * hZ;    // P1 end (NE corner)
            float c3 = 3f * hX + 2f * hZ;   // P2 end (NW corner)
            float c4 = 3f * hX + 4f * hZ;   // P3 end (SW corner)
            // c5 = 4hX + 4hZ = P (origin)

            // cuts を s 昇順にソート（防御的コピー）。
            var cuts = new CutInput[input.cuts.Length];
            System.Array.Copy(input.cuts, cuts, cuts.Length);
            System.Array.Sort(cuts, (a, b) => a.s.CompareTo(b.s));

            int n = cuts.Length;
            for (int i = 0; i < n; i++)
            {
                float sStart = Mathf.Repeat(cuts[i].s, 1f);
                float sEnd = Mathf.Repeat(cuts[(i + 1) % n].s, 1f);
                int cam = cuts[i].camAfter;

                float a0 = sStart * P;
                float a1 = sEnd * P;
                if (a1 <= a0 + Eps) a1 += P; // wrap（末尾セグメント含む・単一カット時は全周）

                // per-edge の沿走座標カバレッジ（raw、オーバーラップ延長前）。
                var min = new float[4] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };
                var max = new float[4] { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };

                // [a0,a1] を [0,P) の 1〜2 区間へ割ってピースと交差。
                AccumulateInterval(a0, Mathf.Min(a1, P), min, max, hX, hZ, c0, c1, c2, c3, c4, P);
                if (a1 > P) AccumulateInterval(0f, a1 - P, min, max, hX, hZ, c0, c1, c2, c3, c4, P);

                // South→East→North→West の順で矩形化。
                EmitEdge(result, Edge.South, min[0], max[0], cam, i, hX, outerX, outerZ, overlapHalf);
                EmitEdge(result, Edge.East, min[1], max[1], cam, i, hZ, outerZ, outerX, overlapHalf);
                EmitEdge(result, Edge.North, min[2], max[2], cam, i, hX, outerX, outerZ, overlapHalf);
                EmitEdge(result, Edge.West, min[3], max[3], cam, i, hZ, outerZ, outerX, overlapHalf);
            }

            return result;
        }

        // 区間 [lo,hi]（0..P）を 5 ピースに交差し、各辺の沿走座標 min/max を更新する。
        private static void AccumulateInterval(float lo, float hi, float[] min, float[] max,
            float hX, float hZ, float c0, float c1, float c2, float c3, float c4, float P)
        {
            if (hi <= lo + Eps) return;
            // P0: South, x 0→hX
            Clip(lo, hi, c0, c1, 0f, hX, Edge.South, min, max);
            // P1: East, z -hZ→hZ
            Clip(lo, hi, c1, c2, -hZ, hZ, Edge.East, min, max);
            // P2: North, x hX→-hX
            Clip(lo, hi, c2, c3, hX, -hX, Edge.North, min, max);
            // P3: West, z hZ→-hZ
            Clip(lo, hi, c3, c4, hZ, -hZ, Edge.West, min, max);
            // P4: South, x -hX→0
            Clip(lo, hi, c4, P, -hX, 0f, Edge.South, min, max);
        }

        private static void Clip(float lo, float hi, float ps, float pe, float alongStart, float alongEnd,
            Edge edge, float[] min, float[] max)
        {
            float o0 = Mathf.Max(lo, ps);
            float o1 = Mathf.Min(hi, pe);
            if (o1 <= o0 + Eps) return;
            float span = pe - ps;
            float t0 = (o0 - ps) / span;
            float t1 = (o1 - ps) / span;
            float v0 = Mathf.Lerp(alongStart, alongEnd, t0);
            float v1 = Mathf.Lerp(alongStart, alongEnd, t1);
            int e = (int)edge;
            min[e] = Mathf.Min(min[e], Mathf.Min(v0, v1));
            max[e] = Mathf.Max(max[e], Mathf.Max(v0, v1));
        }

        // 1 辺のカバレッジ [amin,amax]（沿走座標）から矩形を 1 個生成して result へ追加。
        // alongHalf = この辺の沿走半長（コーナー座標の絶対値）、alongOuter = 沿走軸の外側限界、
        // perpOuter = 直交軸の外側限界。
        private static void EmitEdge(List<ZoneRect> result, Edge edge, float amin, float amax,
            int cam, int segIndex, float alongHalf, float alongOuter, float perpOuter, float overlapHalf)
        {
            if (float.IsInfinity(amin) || float.IsInfinity(amax)) return;
            if (amax - amin < 1e-5f) return; // 実質ゼロ幅（コーナー点接触）はスキップ

            // 端がループコーナー（沿走がループ端に到達）ならコーナーへ延長して直交隣接辺と重ねる。
            // そうでなければ（辺途中のカット）overlapM/2 だけ延長して隣接ゾーンと overlapM 重ねる。
            bool lowCorner = amin <= -alongHalf + Eps;
            bool highCorner = amax >= alongHalf - Eps;
            float aLo = lowCorner ? -alongOuter : amin - overlapHalf;
            float aHi = highCorner ? alongOuter : amax + overlapHalf;

            float alongCenter = 0.5f * (aLo + aHi);
            float alongHalfOut = 0.5f * (aHi - aLo);

            // 直交方向: PerpInner..perpOuter を辺の符号側に置く。
            float perpCenterAbs = 0.5f * (PerpInner + perpOuter);
            float perpHalf = 0.5f * (perpOuter - PerpInner);

            var rect = new ZoneRect { cameraIndex = Mathf.Max(0, cam), priority = 0 };
            switch (edge)
            {
                case Edge.South: // 沿走 X / 直交 Z(-)
                    rect.centerX = alongCenter; rect.halfX = alongHalfOut;
                    rect.centerZ = -perpCenterAbs; rect.halfZ = perpHalf;
                    rect.label = $"seg{segIndex}:South";
                    break;
                case Edge.North: // 沿走 X / 直交 Z(+)
                    rect.centerX = alongCenter; rect.halfX = alongHalfOut;
                    rect.centerZ = perpCenterAbs; rect.halfZ = perpHalf;
                    rect.label = $"seg{segIndex}:North";
                    break;
                case Edge.East: // 沿走 Z / 直交 X(+)
                    rect.centerZ = alongCenter; rect.halfZ = alongHalfOut;
                    rect.centerX = perpCenterAbs; rect.halfX = perpHalf;
                    rect.label = $"seg{segIndex}:East";
                    break;
                case Edge.West: // 沿走 Z / 直交 X(-)
                    rect.centerZ = alongCenter; rect.halfZ = alongHalfOut;
                    rect.centerX = -perpCenterAbs; rect.halfX = perpHalf;
                    rect.label = $"seg{segIndex}:West";
                    break;
            }
            result.Add(rect);
        }

        // ---- v2: grid の展開 ----

        /// <summary>
        /// grid セル (r,c) の course space 矩形（NW 角アンカー: col0=x=-cols·tileM/2、row0=z=+rows·tileM/2）。
        /// <see cref="SolveGrid"/> のセル→course 変換と <see cref="ZoneGridFootprintLogic"/> の生タイル描画が
        /// 同一式を共有するための純関数。row 0=北端（z 大）、col 0=西端（x 小）。
        /// </summary>
        public static void CellRect(int r, int c, int rows, int cols, float tileM,
            out float xLo, out float xHi, out float zLo, out float zHi)
        {
            float halfW = cols * tileM * 0.5f;
            float halfD = rows * tileM * 0.5f;
            xLo = -halfW + c * tileM;
            xHi = -halfW + (c + 1) * tileM;
            zHi = halfD - r * tileM;
            zLo = halfD - (r + 1) * tileM;
        }

        /// <summary>
        /// 与えられた grid/cuts の有無から使用するレイアウトソースを決める。grid 優先（cuts は後方互換）。
        /// Applier の選択ロジックを純関数に切り出したもの（単体テスト用）。
        /// </summary>
        public static LayoutSource ChooseSource(bool hasGrid, bool hasCuts)
            => hasGrid ? LayoutSource.Grid : (hasCuts ? LayoutSource.Cuts : LayoutSource.None);

        /// <summary>
        /// show.json grid.cells（rows 本の文字列）を row-major の int 配列（-1=未割当 / 0..8=カメラ index）へ
        /// **例外を投げずに**変換する。行数≠rows / 行長≠cols / 未知文字は警告ログを出しつつ未割当扱いにする。
        /// </summary>
        public static int[] ParseGridCells(string[]? cells, int rows, int cols)
        {
            int rN = Mathf.Max(0, rows);
            int cN = Mathf.Max(0, cols);
            var grid = new int[rN * cN];
            for (int i = 0; i < grid.Length; i++) grid[i] = -1;
            if (rN == 0 || cN == 0) return grid;

            if (cells == null)
            {
                UDebug.LogWarning("[ZoneLayoutSolver] grid.cells が null。全タイル未割当として扱う。");
                return grid;
            }
            if (cells.Length != rN)
                UDebug.LogWarning($"[ZoneLayoutSolver] grid rows 不一致: cells={cells.Length} rows={rN}（過不足は未割当扱い）。");

            int usableRows = Mathf.Min(rN, cells.Length);
            for (int r = 0; r < usableRows; r++)
            {
                string row = cells[r] ?? "";
                if (row.Length != cN)
                    UDebug.LogWarning($"[ZoneLayoutSolver] grid 行 {r} の長さ {row.Length} != cols {cN}（過不足は未割当扱い）。");
                int usableCols = Mathf.Min(cN, row.Length);
                for (int c = 0; c < usableCols; c++)
                {
                    char ch = row[c];
                    if (ch == '.') continue;
                    if (ch >= '0' && ch <= '8') grid[r * cN + c] = ch - '0';
                    else UDebug.LogWarning($"[ZoneLayoutSolver] grid 未知文字 '{ch}' at ({r},{c})。未割当扱い。");
                }
            }
            return grid;
        }

        /// <summary>
        /// grid（タイルペイント）を course space の矩形ゾーン群へ展開する。
        ///
        /// アルゴリズム（決定的・貪欲矩形分解）:
        ///   1. 左上→右下の走査順で未消費かつ同一カメラのタイルを見つける。
        ///   2. その行で右方向へ同一カメラが続く限り伸ばす（行方向マージ）。
        ///   3. その列幅を保ったまま下方向へ、全列が同一カメラかつ未消費な行が続く限り伸ばす（行間マージ）。
        ///   4. 覆ったタイルを消費済みにして 1 矩形を確定。走査を続ける。
        /// 各矩形は course space 矩形へ変換し、全方向に overlapM/2 拡張する
        /// （隣接カメラ境界で計 overlapM 重なり → 既存 Pick+shrink ヒステリシスが効く）。
        ///
        /// 走査順が決定的なので、同一入力→同一出力・同一順序。ラベルはカメラ別連番 "cam0#0" 等。
        /// priority は全ゾーン 0（既存タイブレーク＝配列先頭が勝つ、を維持）。
        /// </summary>
        public static List<ZoneRect> SolveGrid(GridLayoutInput g)
        {
            var result = new List<ZoneRect>();
            int rows = g.rows, cols = g.cols;
            if (g.cells == null || rows <= 0 || cols <= 0 || g.tileM <= 0f) return result;
            if (g.cells.Length < rows * cols)
            {
                UDebug.LogWarning($"[ZoneLayoutSolver] grid cells 長 {g.cells.Length} < rows*cols {rows * cols}。展開を中止。");
                return result;
            }

            var consumed = new bool[rows * cols];
            float overlapHalf = Mathf.Max(0f, g.overlapM) * 0.5f;
            var camCount = new Dictionary<int, int>();

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int idx = r * cols + c;
                    if (consumed[idx]) continue;
                    int cam = g.cells[idx];
                    if (cam < 0) continue; // 未割当タイルはゾーンを作らない

                    // 2) 行方向マージ: 右へ同一カメラ・未消費が続く限り。
                    int c1 = c;
                    while (c1 + 1 < cols)
                    {
                        int j = r * cols + (c1 + 1);
                        if (consumed[j] || g.cells[j] != cam) break;
                        c1++;
                    }

                    // 3) 行間マージ: 列 [c,c1] 全てが同一カメラ・未消費な行が続く限り下へ。
                    int r1 = r;
                    bool grow = true;
                    while (grow && r1 + 1 < rows)
                    {
                        int rr = r1 + 1;
                        for (int cc = c; cc <= c1; cc++)
                        {
                            int j = rr * cols + cc;
                            if (consumed[j] || g.cells[j] != cam) { grow = false; break; }
                        }
                        if (grow) r1 = rr;
                    }

                    // 4) 消費済みにする。
                    for (int rr = r; rr <= r1; rr++)
                        for (int cc = c; cc <= c1; cc++)
                            consumed[rr * cols + cc] = true;

                    // course space へ。マージ矩形の NW 角は cell(r,c)、SE 角は cell(r1,c1) から取る
                    // （<see cref="CellRect"/> をフットプリント表示と共有 = grid 生タイルとゾーンが同一座標式）。
                    CellRect(r, c, rows, cols, g.tileM, out float xLo, out _, out _, out float zHi);
                    CellRect(r1, c1, rows, cols, g.tileM, out _, out float xHi, out float zLo, out _);

                    int n = camCount.TryGetValue(cam, out int v) ? v : 0;
                    camCount[cam] = n + 1;

                    result.Add(new ZoneRect
                    {
                        centerX = 0.5f * (xLo + xHi),
                        centerZ = 0.5f * (zLo + zHi),
                        halfX = 0.5f * (xHi - xLo) + overlapHalf,
                        halfZ = 0.5f * (zHi - zLo) + overlapHalf,
                        cameraIndex = cam,
                        priority = 0,
                        label = $"cam{cam}#{n}",
                    });
                }
            }

            return result;
        }
    }
}
