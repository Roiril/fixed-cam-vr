#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>カメラごとの見る向きと、見ている面の近似（平面 1 枚）。</summary>
    public sealed class RoomWarpSpec
    {
        public readonly double[] Origin;
        public readonly double[] Rot;
        public readonly double TanH, TanV;
        public readonly double PlaneX, PlaneY, PlaneC;
        public readonly double[] Pivot;
        public readonly double Gain;

        public RoomWarpSpec(double[] origin, double[] rot, double tanH, double tanV,
                            double planeX, double planeY, double planeC, double[] pivot, double gain)
        {
            Origin = origin;
            Rot = rot;
            TanH = tanH;
            TanV = tanV;
            PlaneX = planeX;
            PlaneY = planeY;
            PlaneC = planeC;
            Pivot = pivot;
            Gain = gain;
        }
    }

    /// <summary>
    /// 空間の歪み（LEDGER 0313）。部屋全体を 3D で伸縮させ、傾け、部屋より長い波を通して投影し直す。
    /// 画面座標の波と違って直線が直線のまま残り、中央が止まって外ほど動く。
    /// 表と式は tools/space-warp-study の試作から写した。照合は RoomWarpTests の値で行う。
    /// </summary>
    public static class RoomWarpField
    {
        public const int GridWidth = 40;
        public const int GridHeight = 30;
        public const float Overscan = .85f;
        public const double Limit = 1.0 - Overscan;

        private const double Tau = Math.PI * 2.0;
        private const double RoomGain = 1.4;
        private const double WaveGain = 1.7;

        // kx, ky, kz, Hz, 位相, ax, ay, az。波長 2.4〜4.5m。
        private static readonly double[,] Waves =
        {
            { 1.5, .7, 1.4, .17, .4, .075, .030, .060 },
            { -.9, 2.0, 1.1, .13, 3.0, .030, .080, .035 },
            { 2.2, -1.0, 1.6, .47, 1.7, .022, .016, .026 },
        };

        // export_table.py の出力。カメラの据え付けを変えたら書き出し直す。
        private static readonly RoomWarpSpec SpecA = new RoomWarpSpec(
            new[] { 0.575, -0.813, 0.928 },
            new[] { 0.77085, -0.01914, 0.63673, 0.63622, 0.07313, -0.76803, -0.03186, 0.99714, 0.06855 },
            1.10983, 0.83237, -0.0889, -0.00572, 0.72186, new[] { -0.307, 0.25097, 0.83361 }, 0.68497);

        private static readonly RoomWarpSpec SpecB = new RoomWarpSpec(
            new[] { -1.005, -1.293, 0.034 },
            new[] { 0.99874, -0.02957, -0.0406, -0.04627, -0.22726, -0.97273, 0.01954, 0.97338, -0.22835 },
            1.10983, 0.83237, -0.77981, -0.38009, 1.53472, new[] { -0.97767, -0.65916, 0.18288 }, 0.7296);

        private static readonly RoomWarpSpec SpecC = new RoomWarpSpec(
            new[] { 0.606, 1.135, 1.875 },
            new[] { -0.49852, -0.23059, 0.83565, 0.74849, -0.60078, 0.28075, 0.4373, 0.76543, 0.4721 },
            1.10983, 0.83237, 0.31186, -0.44688, 0.90454, new[] { -0.31785, 0.82463, 1.35303 }, 0.47491);

        /// <summary>知らないカメラには掛けない。</summary>
        public static RoomWarpSpec? SpecFor(string? cameraId) => cameraId switch
        {
            "A" => SpecA,
            "B" => SpecB,
            "C" => SpecC,
            _ => null,
        };

        /// <summary>
        /// 見ている座標 (x, y: 中心 0・右と上が正。全体で ±Overscan) の、引くべき写真の位置までの差。単位は NDC。
        /// 差の大きさは <see cref="Limit"/> で頭打ちにする。写真の外は引かない。
        /// </summary>
        public static void Evaluate(RoomWarpSpec s, double x, double y, double t, out double dx, out double dy)
        {
            var frame = new Frame(t);
            WorldPoint(s, x, y, out double wx, out double wy, out double wz);
            OffsetAt(s, frame, x, y, wx, wy, wz, out dx, out dy);
        }

        /// <summary>
        /// 格子を埋める。行 0 が画面の下。値はテクスチャの座標（±0.5 が画の端）での差で、rg の順。
        /// </summary>
        public static void FillGrid(RoomWarpSpec s, double t, float[] rg)
        {
            if (rg.Length != GridWidth * GridHeight * 2) throw new ArgumentException("格子の大きさが違う", nameof(rg));
            var frame = new Frame(t);
            int k = 0;
            for (int j = 0; j < GridHeight; j++)
            {
                double y = ((j + .5) / GridHeight * 2.0 - 1.0) * Overscan;
                for (int i = 0; i < GridWidth; i++)
                {
                    double x = ((i + .5) / GridWidth * 2.0 - 1.0) * Overscan;
                    WorldPoint(s, x, y, out double wx, out double wy, out double wz);
                    OffsetAt(s, frame, x, y, wx, wy, wz, out double dx, out double dy);
                    rg[k++] = (float)(dx * .5);
                    rg[k++] = (float)(dy * .5);
                }
            }
        }

        /// <summary>時刻で決まる量。格子 1 枚につき 1 回だけ作る。</summary>
        private readonly struct Frame
        {
            public readonly double M00, M01, M02, M10, M11, M12, M22;
            public readonly double Px, Py, Pz;
            public readonly double Time;

            public Frame(double t)
            {
                Time = t;
                M00 = 1.0 + RoomGain * .085 * F(t, .13, .7);                  // 左右の伸縮
                M11 = 1.0 + RoomGain * -.075 * F(t, .17, 2.0);                // 奥行きの伸縮
                M22 = 1.0 + RoomGain * (.055 * F(t, .47, 4.1) + .04 * F(t, .11, 1.0)); // 高さの伸縮
                M02 = RoomGain * .105 * F(t, .17, 3.3);                       // 壁が左右へ傾く
                M12 = RoomGain * .070 * F(t, .13, 5.2);                       // 壁が前後へ傾く
                M10 = RoomGain * .085 * F(t, .23, 1.2);                       // 床の菱形
                M01 = RoomGain * .070 * F(t, .19, 4.4);
                Px = RoomGain * .050 * F(t, .13, 2.9);                        // 奥へ向かって細る
                Py = RoomGain * .060 * F(t, .19, .3);
                Pz = RoomGain * .035 * F(t, .23, 5.5);
            }

            private static double F(double t, double hz, double phase) => Math.Sin(Tau * hz * t + phase);
        }

        private static void WorldPoint(RoomWarpSpec s, double x, double y, out double wx, out double wy, out double wz)
        {
            double z = 1.0 / Math.Max(s.PlaneX * x + s.PlaneY * y + s.PlaneC, .05);
            double cx = x * s.TanH * z, cy = y * s.TanV * z, cz = -z;
            double[] r = s.Rot;
            wx = s.Origin[0] + r[0] * cx + r[1] * cy + r[2] * cz;
            wy = s.Origin[1] + r[3] * cx + r[4] * cy + r[5] * cz;
            wz = s.Origin[2] + r[6] * cx + r[7] * cy + r[8] * cz;
        }

        private static void OffsetAt(RoomWarpSpec s, in Frame f, double x, double y, double wx, double wy, double wz,
                                     out double dx, out double dy)
        {
            double yx = wx - s.Pivot[0], yy = wy - s.Pivot[1], yz = wz - s.Pivot[2];
            double w = Math.Max(1.0 + yx * f.Px + yy * f.Py + yz * f.Pz, .4);
            double sx = s.Pivot[0] + (f.M00 * yx + f.M01 * yy + f.M02 * yz) / w;
            double sy = s.Pivot[1] + (f.M10 * yx + f.M11 * yy + f.M12 * yz) / w;
            double sz = s.Pivot[2] + (f.M22 * yz) / w;

            // 支点の周りは波を通さない。中央が止まっていることが、空間に見える条件の 1 つ。
            double r = Math.Sqrt(yx * yx + yy * yy + yz * yz);
            double near = Math.Min(Math.Max((r - .3) / 1.2, 0.0), 1.0);
            near = near * near * (3.0 - 2.0 * near) * WaveGain;
            for (int i = 0; i < Waves.GetLength(0); i++)
            {
                double phase = Math.Sin(wx * Waves[i, 0] + wy * Waves[i, 1] + wz * Waves[i, 2]
                                        - Tau * Waves[i, 3] * f.Time + Waves[i, 4]) * near;
                sx -= Waves[i, 5] * phase;
                sy -= Waves[i, 6] * phase;
                sz -= Waves[i, 7] * phase;
            }

            // 投影し直す（回転の転置を掛ける）。
            double[] m = s.Rot;
            double ox = sx - s.Origin[0], oy = sy - s.Origin[1], oz = sz - s.Origin[2];
            double cx = ox * m[0] + oy * m[3] + oz * m[6];
            double cy = ox * m[1] + oy * m[4] + oz * m[7];
            double cz = Math.Max(-(ox * m[2] + oy * m[5] + oz * m[8]), 1e-3);
            double rawX = (cx / cz / s.TanH - x) * s.Gain;
            double rawY = (cy / cz / s.TanV - y) * s.Gain;

            double n = Math.Max(Math.Sqrt(rawX * rawX + rawY * rawY), 1e-6);
            double k = Limit * Math.Tanh(n / Limit) / n;
            dx = rawX * k;
            dy = rawY * k;
        }
    }

    /// <summary>
    /// 空間の歪みの時間。2-C の追いつきで始まり、人形化の暗転で終わる。同じランでは戻らない。
    /// 始まりは砂嵐の下で立ち上がるので、見え始めた時には満量になっている。
    /// </summary>
    public sealed class RoomWarpLogic
    {
        public const float RampSec = 2f;

        private bool _started, _ended;
        private float _elapsed;

        public bool Started => _started;
        public bool Ended => _ended;

        /// <summary>場を動かす時刻。</summary>
        public float Elapsed => _elapsed;

        public void Begin()
        {
            if (_started || _ended) return;
            _started = true;
            _elapsed = 0f;
        }

        /// <summary>人形化の暗転で画が隠れた。ズームごと戻す。</summary>
        public void NotifyCovered()
        {
            if (_started) _ended = true;
        }

        public void Reset()
        {
            _started = false;
            _ended = false;
            _elapsed = 0f;
        }

        /// <summary>今フレームの強さ 0..1 を返す。許可の無いフレームは 0 で、時刻も進めない。</summary>
        public float Tick(float dt, bool allowed)
        {
            if (!_started || _ended || !allowed) return 0f;
            _elapsed += Math.Max(dt, 0f);
            float k = Math.Min(_elapsed / RampSec, 1f);
            return k * k * (3f - 2f * k);
        }
    }
}
