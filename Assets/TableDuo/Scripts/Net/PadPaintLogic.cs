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
        // パッド外形（メートル）。UV とデッドバンド判定の単一ソース。ToolGripDriver も参照する
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
        /// パッドローカル座標が XZ 枠内なら UV（0..1）と面からの高さ（heightAbove = y − 面高）を返す。
        /// Y の接触判定はここでは行わず（<see cref="ContactGate"/> がヒステリシス付きで判定する）、
        /// 枠内かどうかだけを返す。UV = ((x+HalfX)/WidthM, (z+HalfZ)/HeightM)。
        /// </summary>
        public static bool TryGetUv(Vector3 padLocalPoint, out Vector2 uv, out float heightAbove)
        {
            heightAbove = padLocalPoint.y - SurfaceLocalY;
            uv = default;
            if (Mathf.Abs(padLocalPoint.x) > HalfX) return false;
            if (Mathf.Abs(padLocalPoint.z) > HalfZ) return false;
            uv = new Vector2((padLocalPoint.x + HalfX) / WidthM, (padLocalPoint.z + HalfZ) / HeightM);
            return true;
        }

        /// <summary>
        /// 接触のヒステリシスゲート（ストローク端の欠け・チカチカ切れを消す）。
        /// 非接触→接触は面から <see cref="DownTol"/> 以内（押し込み側含む）+ 枠内、
        /// 接触→非接触は面から <see cref="UpTol"/> 超で離す or 枠外。境界の float 比較揺れ対策で ±eps。
        /// mutable struct（配列要素で持つ）。
        /// </summary>
        public struct ContactGate
        {
            public const float DownTol = 0.002f;  // 接触開始しきい（面上 2mm 以内 or 押し込み）
            public const float UpTol = 0.006f;     // 接触終了しきい（面上 6mm 超で離れる）
            private const float Eps = 1e-6f;

            private bool _contacting;
            public bool Contacting => _contacting;

            /// <summary>今フレームの (枠内, 面からの高さ) で状態を更新し、更新後の接触状態を返す。</summary>
            public bool Tick(bool inXz, float heightAbove)
            {
                if (!_contacting)
                {
                    if (inXz && heightAbove <= DownTol + Eps) _contacting = true;
                }
                else
                {
                    if (!inXz || heightAbove > UpTol + Eps) _contacting = false;
                }
                return _contacting;
            }
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

        /// <summary>保持者ローカル予測が描いた分の ClientRpc エコーを抑止する秒数（予測終了後もこの間は破棄）。</summary>
        public const float EchoSuppressSec = 0.75f;

        /// <summary>
        /// 保持者ローカル即時インクとサーバ経由 ClientRpc の二重描画を抑止するか。
        /// 予測中（predictingNow）、または予測終了から <see cref="EchoSuppressSec"/> 未満なら抑止する。
        /// </summary>
        public static bool ShouldSuppressEcho(bool predictingNow, float timeSincePredictEnd) =>
            predictingNow || timeSincePredictEnd < EchoSuppressSec;

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
