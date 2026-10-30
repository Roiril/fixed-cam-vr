#nullable enable
using System.Collections.Generic;
using FixedCamVr.Streaming;
using UnityEngine;

namespace FixedCamVr.Tracking
{
    /// <summary>
    /// grid（カメラのタイル割り当て）から歩行ルートを機械的に作る純ロジック。
    /// Quest の <see cref="ShowWalkDebugDriver"/> 用の区画巡回と、共有のgrid解釈。
    /// Editor のアバター検証は壁・体幅を考慮する <see cref="WalkNavigation"/> で経路を作る。
    /// </summary>
    public static class WalkRouteLogic
    {
        public readonly struct Waypoint
        {
            public readonly Vector2 Course;
            public readonly float HoldSec;
            public readonly int Camera;
            public Waypoint(Vector2 c, float hold, int cam) { Course = c; HoldSec = hold; Camera = cam; }
        }

        /// <summary>cells（rows 本の文字列）を [row, col] → カメラ index（未割当 -1）へ。</summary>
        public static int[,] BuildCells(ShowGridDef g, out int rows, out int cols)
        {
            rows = Mathf.Max(0, g.rows);
            cols = Mathf.Max(0, g.cols);
            var map = new int[rows, cols];
            for (int r = 0; r < rows; r++)
            {
                string line = g.cells != null && r < g.cells.Length ? (g.cells[r] ?? "") : "";
                for (int c = 0; c < cols; c++)
                {
                    char ch = c < line.Length ? line[c] : '.';
                    map[r, c] = ch >= '0' && ch <= '8' ? ch - '0' : -1;
                }
            }
            return map;
        }

        public static Vector2 CellCenter(int r, int c, int rows, int cols, float tileM)
        {
            ZoneLayoutSolver.CellRect(r, c, rows, cols, tileM,
                out float xLo, out float xHi, out float zLo, out float zHi);
            return new Vector2((xLo + xHi) * 0.5f, (zLo + zHi) * 0.5f);
        }

        /// <summary>そのカメラのタイル集合の重心にいちばん近いタイル（＝代表点）。</summary>
        public static bool RepresentativeCell(int[,] map, int rows, int cols, int camera, out int rr, out int cc)
        {
            float sr = 0f, sc = 0f;
            int n = 0;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    if (map[r, c] == camera) { sr += r; sc += c; n++; }
            rr = cc = -1;
            if (n == 0) return false;
            sr /= n; sc /= n;

            float best = float.MaxValue;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    if (map[r, c] != camera) continue;
                    float d = (r - sr) * (r - sr) + (c - sc) * (c - sc);
                    if (d >= best) continue;
                    best = d; rr = r; cc = c;
                }
            return rr >= 0;
        }

        /// <summary>
        /// from セルから to セルへ、<paramref name="camA"/> か <paramref name="camB"/> のタイルだけを通る
        /// 最短経路（4 近傍 BFS）。第三のカメラの領域を横切らないことがこの関数の存在理由。
        /// </summary>
        private static List<(int r, int c)>? BfsPath(int[,] map, int rows, int cols,
            (int r, int c) from, (int r, int c) to, int camA, int camB)
        {
            var prev = new int[rows, cols];
            for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++) prev[r, c] = -2;

            var q = new Queue<(int r, int c)>();
            q.Enqueue(from);
            prev[from.r, from.c] = -1;
            int[] dr = { 1, -1, 0, 0 };
            int[] dc = { 0, 0, 1, -1 };

            while (q.Count > 0)
            {
                var cur = q.Dequeue();
                if (cur == to) break;
                for (int k = 0; k < 4; k++)
                {
                    int nr = cur.r + dr[k], nc = cur.c + dc[k];
                    if (nr < 0 || nr >= rows || nc < 0 || nc >= cols) continue;
                    if (prev[nr, nc] != -2) continue;
                    int cam = map[nr, nc];
                    if (cam != camA && cam != camB) continue;
                    prev[nr, nc] = cur.r * cols + cur.c;
                    q.Enqueue((nr, nc));
                }
            }
            if (prev[to.r, to.c] == -2) return null;

            var path = new List<(int r, int c)>();
            var p = to;
            while (true)
            {
                path.Add(p);
                int back = prev[p.r, p.c];
                if (back < 0) break;
                p = (back / cols, back % cols);
            }
            path.Reverse();
            return path;
        }

        /// <summary>
        /// order を <paramref name="laps"/> 周ぶん（+ 最後に order[0] へ戻る＝**帰りの A**）辿る
        /// ウェイポイント列。曲がり角だけを残す（直線区間の中間セルは間引く）。
        ///
        /// 締めの order[0] は「周回を確定させるためのおまけ」ではなく<b>体験の最後の区間</b>で、
        /// ここで前の周の録画が流れる。だから滞在（<paramref name="dwellSec"/>）も他と同じだけ取る
        /// （録画の再生を観測するため）。
        /// </summary>
        public static List<Waypoint> BuildRoute(int[,] map, int rows, int cols, float tileM,
            int[] order, int laps, float dwellSec, out string log)
        {
            var route = new List<Waypoint>();
            log = "";
            var reps = new Dictionary<int, (int r, int c)>();
            foreach (int cam in order)
            {
                if (RepresentativeCell(map, rows, cols, cam, out int r, out int c)) reps[cam] = (r, c);
                else Debug.LogWarning($"[XPWalk] カメラ {cam} のタイルが grid に無い");
            }
            if (reps.Count < 2) return route;

            // laps 周 + 帰りの A（order[0] へ戻ると周回が上がり、そこが最後の区間になる）。
            if (laps < 1) laps = 3;
            var seq = new List<int>();
            for (int lap = 0; lap < laps; lap++)
                foreach (int cam in order) seq.Add(cam);
            seq.Add(order[0]);

            var sb = new System.Text.StringBuilder();
            for (int i = 1; i < seq.Count; i++)
            {
                int from = seq[i - 1], to = seq[i];
                if (!reps.ContainsKey(from) || !reps.ContainsKey(to)) continue;
                List<(int r, int c)>? path = BfsPath(map, rows, cols, reps[from], reps[to], from, to);
                if (path == null)
                {
                    Debug.LogWarning($"[XPWalk] {from}->{to} の経路が無い（タイルが繋がっていない）— 直行する");
                    route.Add(new Waypoint(CellCenter(reps[to].r, reps[to].c, rows, cols, tileM), dwellSec, to));
                    sb.Append(from).Append("~>").Append(to).Append(' ');
                    continue;
                }

                // 曲がり角だけ残す（直線の途中は歩行で自然に通る）。
                for (int k = 1; k < path.Count - 1; k++)
                {
                    var a = path[k - 1]; var b = path[k]; var c2 = path[k + 1];
                    bool straight = (a.r == b.r && b.r == c2.r) || (a.c == b.c && b.c == c2.c);
                    if (straight) continue;
                    route.Add(new Waypoint(CellCenter(b.r, b.c, rows, cols, tileM), 0f, -1));
                }
                var last = path[path.Count - 1];
                route.Add(new Waypoint(CellCenter(last.r, last.c, rows, cols, tileM), dwellSec, to));
                sb.Append(from).Append("->").Append(to).Append(' ');
            }
            log = sb.ToString().TrimEnd();
            return route;
        }
    }
}
