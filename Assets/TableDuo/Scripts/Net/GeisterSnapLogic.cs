#nullable enable
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// ガイスター駒のリリース時スナップの純計算（シーン・ネットワーク非依存 = EditMode テスト可能）。
    /// - 盤上リリース: 最寄りの空きセル中心へ吸着し、yaw は「正面が相手方向を向く」固定値
    ///   （裏の色マーカーを相手に見せないための正面合わせ）
    /// - 盤外リリース: XZ は維持、yaw は現姿勢から抽出して維持、直立化のみ（転倒防止）。
    ///   捕獲した駒の裏面を自由に確認できるようにするための緩いスナップ
    /// Y（接地高さ）は bounds 依存のため呼び出し側（GeisterPieceSnap）が確定する。
    /// </summary>
    public static class GeisterSnapLogic
    {
        /// <summary>盤グリッド定義（ワールド XZ・盤中心基準・正方グリッド）。cellsPerSide は 8 以下前提（占有 bitmask が ulong のため）。</summary>
        public struct Config
        {
            public float boardCenterX;
            public float boardCenterZ;
            public float cellPitch;
            public int cellsPerSide;
        }

        public struct Result
        {
            public bool onBoard;
            public float x;
            public float z;
            public float yawDeg;
            /// <summary>盤上のみ有効（0..cellsPerSide-1）。盤外は -1。</summary>
            public int col;
            public int row;
        }

        // 境界判定の許容差（0.01mm）。「center + half」の float 丸めで境界ちょうどが 1ulp 外れるのを防ぐ
        private const float BoundaryEpsilon = 1e-5f;

        /// <summary>ワールド XZ が盤フットプリント内ならセル番地を返す（境界上は内側扱い）。</summary>
        public static bool TryGetCell(float x, float z, in Config c, out int col, out int row)
        {
            col = row = -1;
            float half = c.cellsPerSide * 0.5f * c.cellPitch + BoundaryEpsilon;
            float dx = x - c.boardCenterX;
            float dz = z - c.boardCenterZ;
            if (Mathf.Abs(dx) > half || Mathf.Abs(dz) > half) return false;
            float offset = (c.cellsPerSide - 1) * 0.5f;
            col = Mathf.Clamp(Mathf.RoundToInt(dx / c.cellPitch + offset), 0, c.cellsPerSide - 1);
            row = Mathf.Clamp(Mathf.RoundToInt(dz / c.cellPitch + offset), 0, c.cellsPerSide - 1);
            return true;
        }

        public static void CellCenter(int col, int row, in Config c, out float x, out float z)
        {
            float offset = (c.cellsPerSide - 1) * 0.5f;
            x = c.boardCenterX + (col - offset) * c.cellPitch;
            z = c.boardCenterZ + (row - offset) * c.cellPitch;
        }

        public static int CellIndex(int col, int row, in Config c) => row * c.cellsPerSide + col;

        /// <summary>任意回転から水平 yaw（deg）を抽出。真上/真下向き（forward が鉛直）は up 軸へフォールバック。</summary>
        public static float ExtractYawDeg(Quaternion rot)
        {
            var f = rot * Vector3.forward;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-6f)
            {
                f = rot * Vector3.up;
                f.y = 0f;
                if (f.sqrMagnitude < 1e-6f) return 0f;
            }
            return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// リリース姿勢からスナップ先を決める。occupiedMask は他駒が占有するセルの bit（<see cref="CellIndex"/>）。
        /// 盤上: 最寄りの空きセル（全て埋まっていれば素の最寄りセル）。盤外: XZ・yaw 維持。
        /// </summary>
        public static Result SnapRelease(Vector3 pos, Quaternion rot, in Config c,
            ulong occupiedMask, float onBoardYawDeg)
        {
            var r = new Result { col = -1, row = -1 };
            if (TryGetCell(pos.x, pos.z, in c, out int col, out int row))
            {
                (col, row) = NearestFreeCell(pos.x, pos.z, in c, occupiedMask, col, row);
                CellCenter(col, row, in c, out r.x, out r.z);
                r.onBoard = true;
                r.col = col;
                r.row = row;
                r.yawDeg = onBoardYawDeg;
            }
            else
            {
                r.onBoard = false;
                r.x = pos.x;
                r.z = pos.z;
                r.yawDeg = ExtractYawDeg(rot);
            }
            return r;
        }

        /// <summary>リリース点に最も近い空きセル。同距離は走査順（低 index）優先 = 決定的。全セル占有時は fallback。</summary>
        private static (int col, int row) NearestFreeCell(float x, float z, in Config c,
            ulong occupiedMask, int fallbackCol, int fallbackRow)
        {
            int bestCol = -1, bestRow = -1;
            float bestSq = float.PositiveInfinity;
            for (int row = 0; row < c.cellsPerSide; row++)
            {
                for (int col = 0; col < c.cellsPerSide; col++)
                {
                    if ((occupiedMask & (1UL << CellIndex(col, row, in c))) != 0) continue;
                    CellCenter(col, row, in c, out float cx, out float cz);
                    float sq = (cx - x) * (cx - x) + (cz - z) * (cz - z);
                    if (sq < bestSq)
                    {
                        bestSq = sq;
                        bestCol = col;
                        bestRow = row;
                    }
                }
            }
            return bestCol >= 0 ? (bestCol, bestRow) : (fallbackCol, fallbackRow);
        }
    }
}
