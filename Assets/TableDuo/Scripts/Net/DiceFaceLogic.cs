#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// ダイス上面読みの純ロジック（GameObject 非依存）。DiceRoller.ReadTopFace から抽出。
    /// 出目は game-critical（DiceRolled → SessionLogger → CSV に残る調査データ）なので
    /// 軸→出目マッピング・決定性・不正入力・クランプを EditMode で固定する。
    /// </summary>
    public static class DiceFaceLogic
    {
        /// <summary>
        /// world up に最も揃うローカル軸 [+X,-X,+Y,-Y,+Z,-Z] を選び faceValues を引く。
        /// 同 dot は低 index 優先（決定的）。faceValues が 6 要素でなければ 1。結果は [1,9]。
        /// </summary>
        public static int ReadTopFace(Quaternion rot, IReadOnlyList<int>? faceValues)
        {
            Vector3 r = rot * Vector3.right, u = rot * Vector3.up, f = rot * Vector3.forward;
            Vector3[] axes = { r, -r, u, -u, f, -f };
            int best = 0;
            float bestDot = float.NegativeInfinity;
            for (int i = 0; i < axes.Length; i++)
            {
                float d = Vector3.Dot(axes[i], Vector3.up);
                if (d > bestDot)
                {
                    bestDot = d;
                    best = i;
                }
            }
            if (faceValues == null || faceValues.Count != 6) return 1;
            return Mathf.Clamp(faceValues[best], 1, 9);
        }
    }
}
