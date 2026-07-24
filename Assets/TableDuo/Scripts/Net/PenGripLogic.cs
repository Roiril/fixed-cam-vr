#nullable enable
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 「あと6画のくま」のペン/消しゴム保持中の姿勢合成（シーン・ネットワーク非依存 = EditMode テスト可能）。
    /// 意図: ペンが手の傾き・ひねりに自然応答し、指先近くを軸に回る。方向は「手首→ピンチ点」線
    /// （書字時のバレル方向の近似）から作り、さらに一定角だけ下へ倒してペン先が紙へ向くようにする。
    /// 面（紙）を貫かないよう、ペン先が面より下へ行く分だけ全体を持ち上げる（面クランプ）。
    ///
    /// 幾何: ピンチ点（親指先・人差し指先の中点）を基準に、前方 dir へ gripBack だけ進んだ点がペン先 tip、
    /// tip から dir 逆方向へ tipDistance 戻った点がペン原点（＝バレル底面 = transform.position）。
    /// </summary>
    public static class PenGripLogic
    {
        // wrist≈pinch（手首とピンチ点がほぼ同一）の縮退しきい値。席スケール（m）で 1mm 未満は同一視
        private const float DirEps = 1e-6f;
        // 水平方位が定義できない（dir がほぼ鉛直）と見なすしきい値
        private const float HorizEps = 1e-5f;

        /// <summary>
        /// 「手首→ピンチ点」からペンの前方 dir を作る。dir0 = normalize(pinch−wrist) の俯角へ
        /// extraPitchDeg を足して下へ倒し、下向き角は ±maxDownDeg にクランプ（LookRotation 縮退回避）。
        /// wrist≈pinch の縮退時は fallbackDir を用いる（それも縮退なら +Z）。
        /// </summary>
        public static Vector3 ComputePenDir(Vector3 wristWorld, Vector3 pinchWorld,
            float extraPitchDeg, float maxDownDeg, Vector3 fallbackDir, out bool degenerate)
        {
            Vector3 v = pinchWorld - wristWorld;
            Vector3 dir0;
            if (v.sqrMagnitude < DirEps)
            {
                degenerate = true;
                dir0 = fallbackDir.sqrMagnitude > DirEps ? fallbackDir.normalized : Vector3.forward;
            }
            else
            {
                degenerate = false;
                dir0 = v.normalized;
            }

            float hLen = new Vector2(dir0.x, dir0.z).magnitude;
            Vector3 hdir;
            if (hLen < HorizEps)
            {
                // dir0 がほぼ鉛直 → 水平方位を fallback から取る（それも鉛直なら +Z）
                var fh = new Vector3(fallbackDir.x, 0f, fallbackDir.z);
                hdir = fh.sqrMagnitude > 1e-8f ? fh.normalized : Vector3.forward;
            }
            else
            {
                hdir = new Vector3(dir0.x, 0f, dir0.z) / hLen;
            }

            float curDown = Mathf.Atan2(-dir0.y, hLen);            // 現俯角（下向き正・rad）
            float maxRad = maxDownDeg * Mathf.Deg2Rad;
            float newDown = Mathf.Clamp(curDown + extraPitchDeg * Mathf.Deg2Rad, -maxRad, maxRad);
            float cos = Mathf.Cos(newDown);
            float sin = Mathf.Sin(newDown);
            return new Vector3(hdir.x * cos, -sin, hdir.z * cos);
        }

        /// <summary>
        /// ピンチ点・前方 dir から、面クランプ済みのペン原点（transform.position）を返す。
        /// tip = pinch + dir·gripBack。tip が面より下なら tip.y を面高へ持ち上げる（全体が +Y へ）。
        /// 原点 = tip − dir·tipDistance。<paramref name="clampApplied"/> は面クランプが効いたか。
        /// </summary>
        public static Vector3 ComposeTipPose(Vector3 pinchWorld, Vector3 dir,
            float gripBack, float tipDistance, float surfaceY, out bool clampApplied)
        {
            Vector3 tip = pinchWorld + dir * gripBack;
            clampApplied = false;
            if (tip.y < surfaceY)
            {
                tip.y = surfaceY;
                clampApplied = true;
            }
            return tip - dir * tipDistance;
        }

        /// <summary>
        /// ペンモードの総合合成（<see cref="ComputePenDir"/> → <see cref="ComposeTipPose"/>）。
        /// forward=合成後の前方、戻り値=ペン原点、clampApplied=面クランプ有無。
        /// フォールバック方向は +Z 固定（呼び出し側の時間平滑を挟まない一発計算用）。
        /// </summary>
        public static Vector3 ComposePenPose(Vector3 wristWorld, Vector3 pinchWorld,
            float gripBack, float tipDistance, float surfaceY,
            float extraPitchDeg, float maxDownDeg, out Vector3 forward, out bool clampApplied)
        {
            forward = ComputePenDir(wristWorld, pinchWorld, extraPitchDeg, maxDownDeg, Vector3.forward, out _);
            return ComposeTipPose(pinchWorld, forward, gripBack, tipDistance, surfaceY, out clampApplied);
        }

        /// <summary>
        /// フラットモード（消しゴム）の合成。位置はピンチ点直下（XZ=ピンチ点、Y=max(pinchY−holdDrop, 面高)）で
        /// 底面が面を割らない。回転は yaw のみ手追従（pitch/roll 0）。forward=水平方位（縮退時は fallback→+Z）。
        /// </summary>
        public static Vector3 ComposeFlatPose(Vector3 wristWorld, Vector3 pinchWorld,
            float holdDropM, float surfaceY, Vector3 fallbackForward, out Vector3 forward)
        {
            var flat = new Vector3(pinchWorld.x - wristWorld.x, 0f, pinchWorld.z - wristWorld.z);
            if (flat.sqrMagnitude > 1e-8f)
                forward = flat.normalized;
            else
                forward = fallbackForward.sqrMagnitude > 1e-8f ? fallbackForward.normalized : Vector3.forward;

            float y = Mathf.Max(pinchWorld.y - holdDropM, surfaceY);
            return new Vector3(pinchWorld.x, y, pinchWorld.z);
        }
    }
}
