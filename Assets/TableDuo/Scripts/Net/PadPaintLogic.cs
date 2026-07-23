#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 「あと6画のくま」の描画パッド接触・UV 変換・セグメント間引きの純計算
    /// （シーン・ネットワーク非依存 = EditMode テスト可能）。
    /// パッド原点はパッド底面 XZ 中心（ローカル y=0）、上面はローカル y=<see cref="SurfaceLocalY"/>。
    /// 外形は W=<see cref="WidthM"/>(X) × H=<see cref="HeightM"/>(Z)。接触判定は物理を使わず、
    /// 呼び出し側が pad.InverseTransformPoint した「パッドローカル座標」をそのまま渡す。
    /// </summary>
    public static class PadPaintLogic
    {
        // パッド外形（メートル）。UV とデッドバンド判定の単一ソース。MarkerHoldTilt も参照する
        public const float WidthM = 0.210f;     // X 幅
        public const float HeightM = 0.297f;    // Z 高
        public const float HalfX = 0.105f;      // WidthM/2
        public const float HalfZ = 0.1485f;     // HeightM/2
        public const float SurfaceLocalY = 0.0045f; // パッド上面のローカル Y（＝描ける面）

        /// <summary>描画セグメントの上限（サーバ側記録バッファ・遅参加リプレイ用）。</summary>
        public const int MaxSegments = 8192;

        /// <summary>1 本の描画セグメント（パッド UV 空間の線分 + 使用ツール）。</summary>
        public struct PaintSegment
        {
            public Vector2 a;
            public Vector2 b;
            public byte tool;
        }

        /// <summary>
        /// パッドローカル座標が描画面に接触しているかを判定し、接触なら UV（0..1）を返す。
        /// 接触条件: |x| ≤ HalfX かつ |z| ≤ HalfZ かつ y ∈ [SurfaceLocalY−yTolBelow, SurfaceLocalY+yTolAbove]。
        /// UV = ((x+HalfX)/WidthM, (z+HalfZ)/HeightM)。
        /// ※ 仕様書の引数順（out を末尾）は C# の「省略可能引数は必須引数の後」制約に反するため、
        ///   out を padLocalPoint 直後へ繰り上げた（意味・既定値は同一）。
        /// </summary>
        public static bool TryGetContactUv(Vector3 padLocalPoint, out Vector2 uv,
            float yTolBelow = 0.004f, float yTolAbove = 0.002f)
        {
            uv = default;
            if (Mathf.Abs(padLocalPoint.x) > HalfX) return false;
            if (Mathf.Abs(padLocalPoint.z) > HalfZ) return false;
            // 境界ちょうど（y == 面高±許容）を接触に含める。float 和が Mono の拡張精度レジスタで
            // 比較されると境界一致が「超過」へ揺れるため、微小イプシロンで境界側に倒す
            const float eps = 1e-6f;
            float y = padLocalPoint.y;
            if (y < SurfaceLocalY - yTolBelow - eps || y > SurfaceLocalY + yTolAbove + eps) return false;
            uv = new Vector2((padLocalPoint.x + HalfX) / WidthM, (padLocalPoint.z + HalfZ) / HeightM);
            return true;
        }

        /// <summary>
        /// UV 差を物理距離（mm 空間 = du·WidthM, dv·HeightM）へ戻し、minDistM 以上動いていれば true
        /// （＝新セグメントを打つべき）。細かい手ブレでセグメントを乱発しないための間引き。
        /// </summary>
        public static bool ShouldEmit(Vector2 lastUv, Vector2 uv, float minDistM = 0.0015f)
        {
            float du = (uv.x - lastUv.x) * WidthM;
            float dv = (uv.y - lastUv.y) * HeightM;
            return du * du + dv * dv >= minDistM * minDistM;
        }

        /// <summary>
        /// セグメントをバッファへ追加する。上限 <see cref="MaxSegments"/> 超過は追加せず false を返す
        /// （視覚描画は別経路で継続し、記録だけ頭打ちにする）。
        /// </summary>
        public static bool TryAdd(List<PaintSegment> buffer, in PaintSegment seg)
        {
            if (buffer.Count >= MaxSegments) return false;
            buffer.Add(seg);
            return true;
        }
    }
}
