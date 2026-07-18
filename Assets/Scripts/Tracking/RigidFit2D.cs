#nullable enable
using UnityEngine;

namespace FixedCamVr.Tracking
{
    /// <summary>
    /// authored な course space 基準点（XZ）と HMD 実測ワールド基準点（XZ）の対応列から、
    /// <see cref="CourseFrame"/> の剛体変換 3 DOF（XZ 平行移動 origin + yaw）を最小二乗で解く純ロジック。
    /// スケールは解かない（剛体 = 2D Procrustes 閉形式）。MonoBehaviour 非依存でテスト可能。
    ///
    /// 解は CourseFrame と同じ順変換で表される： w ≈ origin + Rotation(yaw)·course
    /// （Rotation = Quaternion.Euler(0, yaw, 0)。<see cref="CourseFrame.CourseToWorld"/> と同一の回転規約）。
    /// N=2 のとき、旧 CourseRegistrationController の 2 点解（cross/dot による yaw + 2 点平均 origin）と
    /// 数学的に一致する（重心方式は 2 点で中点になり、cross/dot は 1/2 スケール差だけで atan2 は不変）。
    /// </summary>
    public static class RigidFit2D
    {
        /// <summary>フィット結果。<see cref="ok"/>=false は course 点が同一に潰れて向きを定義できない場合。</summary>
        public struct Result
        {
            /// <summary>解が定義できたか（course 点が広がっているか）。false なら他フィールドは無効。</summary>
            public bool ok;

            /// <summary>course 原点のワールド XZ（= t）。</summary>
            public Vector2 originXZ;

            /// <summary>CourseFrame の yaw（deg）。</summary>
            public float yawDeg;

            /// <summary>各点の残差 (m)、入力 course 点と同じ順。ok=false なら空。</summary>
            public float[] residualsM;

            /// <summary>残差最大の点 index（ok=false なら -1）。</summary>
            public int worstIndex;

            /// <summary>最大残差 (m)。</summary>
            public float maxResidualM;

            /// <summary>RMS 残差 (m)。</summary>
            public float rmsResidualM;
        }

        // course 点が「広がっている」と見なす重心からの二乗和の下限。これ未満なら向き不定 → ok=false。
        private const float CourseSpreadEpsilon = 1e-8f;

        /// <summary>
        /// 対応点列（course XZ ↔ world XZ、同じ順・同じ長さ）から剛体変換を解く。
        /// 2 点以上必要。長さ不一致 / 2 点未満 / course 潰れ は <see cref="Result.ok"/>=false を返す。
        /// </summary>
        public static Result Solve(Vector2[] course, Vector2[] world)
        {
            var r = new Result { ok = false, worstIndex = -1, residualsM = System.Array.Empty<float>() };
            if (course == null || world == null) return r;

            int n = course.Length;
            if (n < 2 || world.Length != n) return r;

            // 重心（authored 側 c̄、実測側 w̄）。
            Vector2 cBar = Vector2.zero, wBar = Vector2.zero;
            for (int i = 0; i < n; i++) { cBar += course[i]; wBar += world[i]; }
            cBar /= n;
            wBar /= n;

            // Σ cross(d_i, e_i)・Σ dot(d_i, e_i) で標準数学系の回転角 θ_std を導く（d=authored 差分, e=実測差分）。
            // 併せて course の広がり量（Σ|d|²）を測り、潰れ検出に使う。
            float sCross = 0f, sDot = 0f, courseSpread = 0f;
            for (int i = 0; i < n; i++)
            {
                Vector2 d = course[i] - cBar;
                Vector2 e = world[i] - wBar;
                sCross += d.x * e.y - d.y * e.x;
                sDot += d.x * e.x + d.y * e.y;
                courseSpread += d.sqrMagnitude;
            }
            if (courseSpread < CourseSpreadEpsilon) return r; // 全点同一等 → 向き不定

            // θ_std = atan2(Σcross, Σdot)。CourseFrame の Rotation=Euler(0,yaw,0) は XZ を
            // M(yaw)=[[cos,sin],[-sin,cos]] で回す（standard R(θ) の転置）ので、R(θ_std) と一致させるには yaw=-θ_std。
            float yawDeg = -Mathf.Atan2(sCross, sDot) * Mathf.Rad2Deg;

            // t = w̄ - M(yaw)·c̄。CourseFrame.CourseToWorld と同じ Quaternion.Euler を使い FP まで揃える。
            Quaternion rot = Quaternion.Euler(0f, yawDeg, 0f);
            Vector3 rc = rot * new Vector3(cBar.x, 0f, cBar.y);
            Vector2 origin = new(wBar.x - rc.x, wBar.y - rc.z);

            // 点毎残差（predicted = origin + M(yaw)·course_i）。
            var res = new float[n];
            float maxR = 0f, sumSq = 0f;
            int worst = 0;
            for (int i = 0; i < n; i++)
            {
                Vector3 pr = rot * new Vector3(course[i].x, 0f, course[i].y);
                float dx = world[i].x - (origin.x + pr.x);
                float dz = world[i].y - (origin.y + pr.z);
                float e = Mathf.Sqrt(dx * dx + dz * dz);
                res[i] = e;
                sumSq += e * e;
                if (e > maxR) { maxR = e; worst = i; }
            }

            r.ok = true;
            r.originXZ = origin;
            r.yawDeg = yawDeg;
            r.residualsM = res;
            r.worstIndex = worst;
            r.maxResidualM = maxR;
            r.rmsResidualM = Mathf.Sqrt(sumSq / n);
            return r;
        }
    }
}
