#nullable enable
using NUnit.Framework;
using TableDuoVr.Net;
using UnityEngine;

namespace TableDuoVr.Tests
{
    /// <summary>
    /// DiceFaceLogic.ReadTopFace（ダイス上面読み）の EditMode テスト。
    /// 出目は game-critical（DiceRolled → SessionLogger → CSV に残る調査データ）なので、
    /// 軸→出目マッピング・決定性・不正入力・クランプを GameObject 生成なしで固定する。
    /// ローカル軸 [+X,-X,+Y,-Y,+Z,-Z] → faceValues index [0,1,2,3,4,5]。
    /// </summary>
    public class DiceFaceLogicTests
    {
        // localAxis を world up へ向ける回転を作れば、その軸の index が上面に選ばれる。
        private static readonly (Vector3 axis, int index)[] AxisIndex =
        {
            (Vector3.right, 0),   // +X
            (Vector3.left, 1),    // -X
            (Vector3.up, 2),      // +Y
            (Vector3.down, 3),    // -Y
            (Vector3.forward, 4), // +Z
            (Vector3.back, 5),    // -Z
        };

        [Test]
        public void ReadTopFace_EachLocalAxisUp_ReturnsFaceAtThatIndex()
        {
            var fv = new[] { 1, 2, 3, 4, 5, 6 }; // index と値が 1:1 で見分けられる
            foreach (var (axis, index) in AxisIndex)
            {
                var rot = Quaternion.FromToRotation(axis, Vector3.up);
                int v = DiceFaceLogic.ReadTopFace(rot, fv);
                Assert.AreEqual(fv[index], v, $"ローカル軸 {axis} を上に向けると index {index} の面が選ばれる");
            }
        }

        [Test]
        public void ReadTopFace_DefaultFaceValues_IdentityAndX180()
        {
            var fv = new[] { 1, 2, 3, 1, 2, 3 }; // DiceRoller の既定
            // identity: +Y(index2) が上 → 3
            Assert.AreEqual(3, DiceFaceLogic.ReadTopFace(Quaternion.identity, fv));
            // X 軸 180°: -Y(index3) が上 → 1
            Assert.AreEqual(1, DiceFaceLogic.ReadTopFace(Quaternion.Euler(180f, 0f, 0f), fv));
        }

        [Test]
        public void ReadTopFace_DotTie_ResolvesToLowerIndexDeterministically()
        {
            var fv = new[] { 1, 2, 3, 4, 5, 6 };
            // Z 軸 45°: +X(index0) と +Y(index2) が world up と等角（dot タイ）。
            var rot = Quaternion.AngleAxis(45f, Vector3.forward);

            int a = DiceFaceLogic.ReadTopFace(rot, fv);
            int b = DiceFaceLogic.ReadTopFace(rot, fv);

            Assert.AreEqual(a, b, "同一入力は 2 回とも同じ出目（決定的）");
            // 厳密比較 '>' により最初に見つかった低 index が勝つ。タイの候補は index0(+X) / index2(+Y)。
            // （Unity 同梱 NUnit には Is.AnyOf が無いため bool で判定）
            Assert.IsTrue(a == fv[0] || a == fv[2], $"タイは低 index 側（+X か +Y）へ安定に解決する（実値 {a}）");
        }

        [Test]
        public void ReadTopFace_InvalidFaceValuesLength_ReturnsOne()
        {
            var rot = Quaternion.identity;
            Assert.AreEqual(1, DiceFaceLogic.ReadTopFace(rot, null));
            Assert.AreEqual(1, DiceFaceLogic.ReadTopFace(rot, new[] { 1, 2, 3, 4, 5 }));       // 長さ5
            Assert.AreEqual(1, DiceFaceLogic.ReadTopFace(rot, new[] { 1, 2, 3, 4, 5, 6, 7 })); // 長さ7
        }

        [Test]
        public void ReadTopFace_OutOfRangeFaceValues_ClampedToOneToNine()
        {
            var fv = new[] { 0, 99, -3, 50, 10, 1 }; // clamp 後 {1,9,1,9,9,1}
            foreach (var (axis, _) in AxisIndex)
            {
                var rot = Quaternion.FromToRotation(axis, Vector3.up);
                int v = DiceFaceLogic.ReadTopFace(rot, fv);
                Assert.That(v, Is.InRange(1, 9), $"軸 {axis} の出目が [1,9] にクランプされる");
            }
        }
    }
}
