#nullable enable
using System;

namespace FixedCamVr.Streaming.Recording
{
    /// <summary>
    /// <b>L 字の角を回る進み具合</b>（`canon/LEDGER.md` 0274）。角の頂点から見た向きだけで決まり、
    /// 角からの距離（奥行き）は見ない。始めの壁の向きで 0、終わりの壁の向きで 1、
    /// 2 枚の壁の二等分線で 0.5。
    ///
    /// 2 周目 A の録画を「前へ進んだコマだけ」残す物差しと、3 周目 A で体験者の位置に合うコマを
    /// 選ぶ物差しが<b>同じこれ</b>であることが要点（片方だけ別の式にすると、録画の自分が体験者からずれる）。
    ///
    /// 純粋な計算。壁の座標は course 空間（<c>layout.room.walls</c>）。
    /// </summary>
    public readonly struct CornerProgress
    {
        /// <summary>2 枚の壁が角を共有しているとみなす端点の距離 (m)。</summary>
        public const float CornerTolM = 0.05f;

        public readonly float CornerX, CornerZ;
        private readonly float _ax, _az;     // 始めの壁の向き（単位）
        private readonly float _sign;        // 始め → 終わりへ回る向き（+1 / -1）
        private readonly float _span;        // 2 枚の壁のなす角 (rad)

        private CornerProgress(float cx, float cz, float ax, float az, float sign, float span)
        {
            CornerX = cx; CornerZ = cz; _ax = ax; _az = az; _sign = sign; _span = span;
        }

        /// <summary>
        /// 壁 2 枚から物差しを組む。2 枚が端点を 1 つ共有していなければ false（角が無い）。
        /// </summary>
        public static bool TryFromWalls(ShowRoomWallDef[]? walls, string fromWallId, string toWallId,
                                        out CornerProgress progress)
        {
            progress = default;
            if (walls == null) return false;
            ShowRoomWallDef? a = null, b = null;
            foreach (ShowRoomWallDef? w in walls)
            {
                if (w == null) continue;
                if (w.id == fromWallId) a = w;
                else if (w.id == toWallId) b = w;
            }
            if (a == null || b == null) return false;

            // 共有する端点を探す（4 通り）。
            float[] ax = { a.x1, a.x2 }, az = { a.z1, a.z2 }, bx = { b.x1, b.x2 }, bz = { b.z1, b.z2 };
            for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                if (Dist(ax[i], az[i], bx[j], bz[j]) > CornerTolM) continue;
                float cx = (ax[i] + bx[j]) * 0.5f, cz = (az[i] + bz[j]) * 0.5f;
                float ux = ax[1 - i] - cx, uz = az[1 - i] - cz;
                float vx = bx[1 - j] - cx, vz = bz[1 - j] - cz;
                float lu = MathF.Sqrt(ux * ux + uz * uz), lv = MathF.Sqrt(vx * vx + vz * vz);
                if (lu < 1e-4f || lv < 1e-4f) return false;
                ux /= lu; uz /= lu; vx /= lv; vz /= lv;
                float cross = ux * vz - uz * vx;
                float dot = ux * vx + uz * vz;
                float span = MathF.Abs(MathF.Atan2(cross, dot));
                if (span < 1e-3f) return false;   // 2 枚が一直線 → 角ではない
                progress = new CornerProgress(cx, cz, ux, uz, cross >= 0f ? 1f : -1f, span);
                return true;
            }
            return false;
        }

        /// <summary>
        /// (x, z) の進み具合。始めの壁の向き = 0、終わりの壁の向き = 1。範囲外も外挿して返す。
        /// 角の真上（向きが決まらない）は NaN。
        /// </summary>
        public float At(float x, float z)
        {
            float px = x - CornerX, pz = z - CornerZ;
            if (px * px + pz * pz < 1e-8f) return float.NaN;
            float cross = _ax * pz - _az * px;
            float dot = _ax * px + _az * pz;
            return MathF.Atan2(cross, dot) * _sign / _span;
        }

        private static float Dist(float x1, float z1, float x2, float z2)
        {
            float dx = x1 - x2, dz = z1 - z2;
            return MathF.Sqrt(dx * dx + dz * dz);
        }
    }
}
