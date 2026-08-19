#nullable enable

using System.Collections.Generic;
using UnityEngine;

namespace FixedCamVr.Tracking
{
    /// <summary>ゾーン 1 つぶんの箱（<see cref="PlayerZone"/> から値だけを写したもの・テスト用に純データ）。</summary>
    public readonly struct SpanBox
    {
        public readonly Vector3 center;
        public readonly Quaternion rot;
        public readonly Vector3 half;
        public readonly int camera;

        public SpanBox(Vector3 center, Quaternion rot, Vector3 half, int camera)
        {
            this.center = center;
            this.rot = rot;
            this.half = half;
            this.camera = camera;
        }
    }

    /// <summary>
    /// <b>体験者が「いまの区間をどこまで来たか」を、床の上の距離で測る</b>（2026-08-19・
    /// <c>canon/LEDGER.md</c> 0093「体験者の位置ベースでの開始、終了にしてほしい」）。
    ///
    /// 時計で測ると、歩くのが速い人は演出が始まる前に区間を抜け、遅い人は終わった後も立っている。
    /// <b>区間の長さは決まっている</b>（grid の 1 タイル 0.15m × 枚数）ので、そこを分母に取る。
    ///
    /// 測り方（3 つとも「自分で決めた」ではなく、この作品の幾何から出る）:
    /// <list type="number">
    ///   <item><b>進む向き</b> ＝ その矩形の<b>長い方の水平軸</b>。区間は廊下の形（現行の C は 1.35m × 0.45m）
    ///     なので、長い方が歩く向きになる</item>
    ///   <item><b>長さ</b> ＝ <b>同じカメラの矩形すべて</b>をその軸へ射影した端から端。
    ///     ⚠ 貪欲分解（<see cref="ZoneLayoutSolver.SolveGrid"/>）は 1 つのカメラを複数の矩形に割ることがあり、
    ///     入った矩形だけで測ると<b>区間の半ばがもっと手前に来る</b></item>
    ///   <item><b>始まり</b> ＝ <b>入った瞬間の立ち位置</b>。矩形の端ではない
    ///     （重なり <c>overlapM</c> とヒステリシスのぶん、確定するのは端より内側になる）。
    ///     遠い方の端までを 1 として測るので、<b>0.5 は「入った所と奥の端のちょうど中間」</b></item>
    /// </list>
    ///
    /// ⚠ <b>y は見ない。</b> 背の高さも屈んだかも進みではない。
    /// </summary>
    public static class ZoneSpanMath
    {
        /// <summary>これより短い区間は測らない (m)。入った所が既に奥の端なら進みは意味を持たない。</summary>
        public const float MinSpanM = 0.10f;

        /// <summary>矩形が進む向き（長い方の水平軸・単位ベクトル）。解けなければ <c>Vector3.zero</c>。</summary>
        public static Vector3 TravelAxis(Quaternion rot, Vector3 half)
        {
            Vector3 a = half.x >= half.z ? rot * Vector3.right : rot * Vector3.forward;
            a.y = 0f;
            return a.sqrMagnitude < 1e-8f ? Vector3.zero : a.normalized;
        }

        /// <summary>箱を軸へ射影したときの半幅 (m)。</summary>
        public static float ProjectedHalf(in SpanBox b, Vector3 axis)
            => Mathf.Abs(Vector3.Dot(b.rot * Vector3.right, axis)) * b.half.x
             + Mathf.Abs(Vector3.Dot(b.rot * Vector3.forward, axis)) * b.half.z;

        /// <summary>
        /// 入った矩形（<paramref name="index"/>）から、進む軸と「入った所 → 奥の端」を解く。
        /// </summary>
        /// <param name="boxes">いま生きているゾーン全部（同じカメラの矩形を拾うため）。</param>
        /// <param name="index">入った矩形の位置。</param>
        /// <param name="entryWorld">入った瞬間の頭の位置（ワールド）。</param>
        /// <param name="axis">進む向き（単位ベクトル）。</param>
        /// <param name="uEntry">入った所（軸上の座標 m）。</param>
        /// <param name="uFar">奥の端（軸上の座標 m）。<paramref name="uEntry"/> より小さいこともある。</param>
        /// <returns>測れるか。false なら位置では測らない（＝ カットの終わりに任せる）。</returns>
        public static bool Solve(IReadOnlyList<SpanBox> boxes, int index, Vector3 entryWorld,
                                out Vector3 axis, out float uEntry, out float uFar)
        {
            axis = Vector3.zero;
            uEntry = 0f;
            uFar = 0f;
            if (boxes == null || index < 0 || index >= boxes.Count) return false;

            SpanBox entered = boxes[index];
            axis = TravelAxis(entered.rot, entered.half);
            if (axis == Vector3.zero) return false;

            // 同じカメラの矩形すべてを軸へ射影して、端から端を取る。
            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < boxes.Count; i++)
            {
                SpanBox b = boxes[i];
                if (b.camera != entered.camera) continue;
                float c = Vector3.Dot(b.center, axis);
                float h = ProjectedHalf(b, axis);
                if (c - h < min) min = c - h;
                if (c + h > max) max = c + h;
            }
            if (min > max) return false;

            // 入った所は端より内側とは限らない（重なりぶん外から確定することがある）ので挟む。
            uEntry = Mathf.Clamp(Vector3.Dot(entryWorld, axis), min, max);
            uFar = (uEntry - min) >= (max - uEntry) ? min : max;
            if (Mathf.Abs(uFar - uEntry) < MinSpanM) return false;

            // 軸は「奥へ向かう向き」に揃えておく（読む側が符号を気にしなくてよい）。
            if (uFar < uEntry)
            {
                axis = -axis;
                uEntry = -uEntry;
                uFar = -uFar;
            }
            return true;
        }

        /// <summary>いまの立ち位置の進み 0..1（0 = 入った所 / 1 = 奥の端）。</summary>
        public static float Progress01(Vector3 world, Vector3 axis, float uEntry, float uFar)
        {
            float span = uFar - uEntry;
            if (Mathf.Abs(span) < 1e-4f) return 0f;
            return Mathf.Clamp01((Vector3.Dot(world, axis) - uEntry) / span);
        }
    }
}
