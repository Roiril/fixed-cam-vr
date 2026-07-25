#nullable enable
using System;

namespace FixedCamVr.Tracking
{
    /// <summary>
    /// 「いまの位置がどのゾーンか」を選ぶ純ロジック（<see cref="PlayerZoneTracker"/> の Pick の中身）。
    /// UnityEngine 非依存にして、EditMode テストと **Web シミュレータの JS ミラー**の両方から
    /// 同じセマンティクスを使えるようにする（計画 2026-07-25_show-simulator.md 段 S1）。
    ///
    /// 判定順（<see cref="PlayerZoneTracker.Pick"/> と同一。変えるときはテストを先に直す）:
    ///   1. 直近ゾーンが shrink 後の箱に入っていれば維持（境界フリッカ防止のヒステリシス）
    ///   2. それ以外は包含する全ゾーンのうち priority 最大。同値なら**配列の先頭が勝つ**（厳密比較 &gt;）
    ///   3. どこにも入っていなければ keepLastWhenOutside の方針（維持 or 未選択）
    ///
    /// ゾーンは OBB（向き付き箱）。yaw は <see cref="Box.CosYaw"/>/<see cref="Box.SinYaw"/> で持つ
    /// （Quaternion を持ち込まず、XZ 平面の回転だけを扱う＝コースフレームの実態と一致）。
    /// </summary>
    public static class ZonePickLogic
    {
        /// <summary>ゾーン 1 つ分の判定に必要な最小データ。</summary>
        public struct Box
        {
            public float CenterX, CenterY, CenterZ;
            public float HalfX, HalfY, HalfZ;
            public float CosYaw, SinYaw;   // ゾーンの向き（yaw のみ）。無回転なら (1, 0)
            public int CameraIndex;
            public int Priority;

            /// <summary>無回転（AABB 互換）の箱を作る。</summary>
            public static Box Aabb(float cx, float cy, float cz, float hx, float hy, float hz,
                                   int cameraIndex, int priority = 0) => new()
            {
                CenterX = cx, CenterY = cy, CenterZ = cz,
                HalfX = hx, HalfY = hy, HalfZ = hz,
                CosYaw = 1f, SinYaw = 0f,
                CameraIndex = cameraIndex, Priority = priority,
            };

            /// <summary>yaw（度）付きの箱を作る。</summary>
            public static Box WithYaw(float cx, float cy, float cz, float hx, float hy, float hz,
                                      float yawDeg, int cameraIndex, int priority = 0)
            {
                double r = yawDeg * Math.PI / 180.0;
                Box b = Aabb(cx, cy, cz, hx, hy, hz, cameraIndex, priority);
                b.CosYaw = (float)Math.Cos(r);
                b.SinYaw = (float)Math.Sin(r);
                return b;
            }
        }

        /// <summary>
        /// 点が箱に含まれるか（<see cref="PlayerZone.Contains"/> と同一式）。
        /// <paramref name="shrink"/> は各軸を内側へ縮める量（ヒステリシス用・負の半長は 0 でクランプ）。
        /// </summary>
        public static bool Contains(in Box b, float x, float y, float z, float shrink = 0f)
        {
            float dx = x - b.CenterX, dy = y - b.CenterY, dz = z - b.CenterZ;
            // ワールド差分をゾーンローカル軸へ射影（right = (cos, 0, -sin) / forward = (sin, 0, cos)）。
            float lx = dx * b.CosYaw - dz * b.SinYaw;
            float lz = dx * b.SinYaw + dz * b.CosYaw;
            float hx = b.HalfX - shrink; if (hx < 0f) hx = 0f;
            float hy = b.HalfY - shrink; if (hy < 0f) hy = 0f;
            float hz = b.HalfZ - shrink; if (hz < 0f) hz = 0f;
            return Abs(lx) <= hx && Abs(dy) <= hy && Abs(lz) <= hz;
        }

        private static float Abs(float v) => v < 0f ? -v : v;

        /// <summary>
        /// ゾーンを選ぶ。返すのは <paramref name="boxes"/> 内の index（未選択は -1）。
        /// <paramref name="currentIndex"/> は直近の選択（無ければ -1）。
        /// </summary>
        public static int Pick(Box[] boxes, float x, float y, float z,
                               int currentIndex, float hysteresisShrink, bool keepLastWhenOutside)
        {
            if (boxes == null || boxes.Length == 0) return -1;

            // 1) 直近ゾーンを shrink 判定で維持（再評価しない）。
            if (currentIndex >= 0 && currentIndex < boxes.Length
                && Contains(boxes[currentIndex], x, y, z, hysteresisShrink))
            {
                return currentIndex;
            }

            // 2) 包含する中で priority 最大。同値は先勝ち（厳密比較なので先頭側が残る）。
            int best = -1;
            int bestPriority = int.MinValue;
            for (int i = 0; i < boxes.Length; i++)
            {
                if (!Contains(boxes[i], x, y, z)) continue;
                if (boxes[i].Priority > bestPriority)
                {
                    best = i;
                    bestPriority = boxes[i].Priority;
                }
            }
            if (best >= 0) return best;

            // 3) どこにも入っていない。
            return keepLastWhenOutside ? currentIndex : -1;
        }
    }
}
