#nullable enable
using System.Collections.Generic;
using System.Text;
using TableDuoVr.Net;
using UnityEditor;
using UnityEngine;

namespace TableDuoVr.EditorTools
{
    /// <summary>
    /// アルゴ山札の静止状態チェック（Play 不要・HMD 不要・EditMode 完結・シーン非破壊）。
    ///
    /// 山札は「静止中は kinematic で凍結」設計（Grabbable.restKinematic）。つまり実機でカードは
    /// ベイク姿勢のまま固定され、物理シミュレーションで沈み込むことはない。したがって検証は
    /// 「ベイクした静止配置そのものが貫入していないか」を数値で見れば足りる（＝実機の見た目と一致）。
    ///
    /// このツールはシミュレーションせず、現在の配置で:
    /// - restKinematic フラグが全カードに立っているか（＝凍結される設計になっているか）
    /// - カード同士の真の貫入（Physics.ComputePenetration・box-box 正確判定）の最大/平均
    /// を Debug.Log に mm 単位で出す。シーンは一切変更しない。
    ///
    /// 期待値（修正後）: restKinematic = 24/24、貫入 max &lt; 0.1mm。
    /// （ベイクは 3mm ピッチ・カード厚 2.6mm ＝ 0.4mm 空隙なので貫入は原理的に 0）
    /// </summary>
    public static class TableDuoAlgoPhysicsCheck
    {
        private const string AlgoRootPath = "[TableDuo]/Props/Game_algo";
        private const string CardPrefix = "ALGO_";

        [MenuItem("Tools/FixedCamVr/Diagnostics/アルゴ山札の静止配置チェック", priority = 217)]
        public static void Run()
        {
            var cards = GatherCards();
            if (cards.Count == 0)
            {
                Debug.LogWarning($"[AlgoCheck] ALGO_ カードが見つからない（{AlgoRootPath} を確認 / Setup 済みか）");
                return;
            }

            int restKinematicCount = 0;
            foreach (var c in cards)
            {
                var grab = c.transform.GetComponent<Grabbable>();
                if (grab == null) continue;
                var so = new SerializedObject(grab);
                var p = so.FindProperty("restKinematic");
                if (p != null && p.boolValue) restKinematicCount++;
            }

            // 真の貫入は Physics.ComputePenetration（box-box 正確判定）で測る。
            // AABB オーバーラップは傾いたカードで偽陽性を出すため使わない
            float maxPen = 0f, sumPen = 0f;
            int penPairs = 0;
            string worstPair = "-";
            for (int i = 0; i < cards.Count; i++)
            {
                for (int j = i + 1; j < cards.Count; j++)
                {
                    var a = cards[i].col;
                    var b = cards[j].col;
                    if (!a.bounds.Intersects(b.bounds)) continue; // 粗ふるい
                    if (!Physics.ComputePenetration(
                            a, a.transform.position, a.transform.rotation,
                            b, b.transform.position, b.transform.rotation,
                            out _, out float pen) || pen <= 0f) continue;
                    penPairs++;
                    sumPen += pen;
                    if (pen > maxPen)
                    {
                        maxPen = pen;
                        worstPair = $"{cards[i].transform.name} × {cards[j].transform.name}";
                    }
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine($"[AlgoCheck] {cards.Count} 枚の静止配置");
            sb.AppendLine($"  restKinematic = {restKinematicCount}/{cards.Count}（凍結される設計か。全数であるべき）");
            sb.AppendLine($"  真の貫入 max = {maxPen * 1000f:F3} mm ({worstPair}) / mean = {(penPairs > 0 ? sumPen / penPairs * 1000f : 0f):F3} mm / 貫入ペア = {penPairs}");
            sb.Append("  目安: restKinematic 全数・貫入 max < 0.1mm なら OK（静止山札は凍結され沈み込まない）");
            Debug.Log(sb.ToString());
        }

        /// <summary>Game_algo 直下の ALGO_ カード（Collider を持つもの）を集める。</summary>
        private static List<(GameObject go, Transform transform, Collider col)> GatherCards()
        {
            var result = new List<(GameObject, Transform, Collider)>();
            var root = GameObject.Find(AlgoRootPath);
            if (root == null) return result;
            foreach (Transform child in root.transform)
            {
                if (!child.name.StartsWith(CardPrefix)) continue;
                var col = child.GetComponent<Collider>();
                if (col != null) result.Add((child.gameObject, child, col));
            }
            return result;
        }
    }
}
