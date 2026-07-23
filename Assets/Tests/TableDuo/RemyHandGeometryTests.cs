#nullable enable
using NUnit.Framework;
using TableDuoVr.Hands;
using TableDuoVr.Net;
using UnityEngine;

namespace TableDuoVr.Tests
{
    /// <summary>
    /// Remy 手首基底・W 写像・FK の純幾何（RemyHandGeometry、RemyAvatarRig から移設）を固定する。
    /// memory が示す最多バグ領域（Remy 指・席フレーム・handSkeletonVersion）で手掌性の符号反転と
    /// FK 親子順の解決が微妙なため、数式不変の behavior-preserving 抽出を pin する。
    /// static FK バッファ（FkPos/FkRot/FkDone）を読むテストは呼出直後に読み、複数ケースを 1 メソッドに詰めない
    /// （バッファ再利用による相互汚染回避）。
    /// </summary>
    public class RemyHandGeometryTests
    {
        private const int N = AvatarPose.BonesPerHand; // 24
        private const float Tol = 1e-4f;

        private static Quaternion[] IdentityRots()
        {
            var r = new Quaternion[N];
            for (int i = 0; i < N; i++) r[i] = Quaternion.identity;
            return r;
        }

        private static void AssertVec(Vector3 expected, Vector3 actual, string msg)
            => Assert.Less(Vector3.Distance(expected, actual), Tol, $"{msg} 期待={expected} 実際={actual}");

        // --- TryHandBasis ---

        [Test]
        public void TryHandBasis_ComputesForwardAndBackWithHandednessSign()
        {
            var wrist = Vector3.zero;
            Vector3? mid = new Vector3(0f, 0f, 1f);
            Vector3? idx = new Vector3(1f, 0f, 0f);
            Vector3? pnk = new Vector3(-1f, 0f, 0f);

            Assert.IsTrue(RemyHandGeometry.TryHandBasis(wrist, mid, idx, pnk, true, out var fR, out var bR));
            AssertVec(new Vector3(0f, 0f, 1f), fR, "f = (mid-wrist) 正規化");
            AssertVec(new Vector3(0f, -1f, 0f), bR, "右手 b = cross(idx-pnk, f) 正規化");

            Assert.IsTrue(RemyHandGeometry.TryHandBasis(wrist, mid, idx, pnk, false, out var fL, out var bL));
            AssertVec(new Vector3(0f, 0f, 1f), fL, "f は掌性に依らず同じ");
            AssertVec(new Vector3(0f, 1f, 0f), bL, "左手は b 符号反転");
        }

        [Test]
        public void TryHandBasis_NullInputsReturnFalse()
        {
            var w = Vector3.zero;
            Assert.IsFalse(RemyHandGeometry.TryHandBasis(w, null, new Vector3(1, 0, 0), new Vector3(-1, 0, 0), true, out _, out _));
            Assert.IsFalse(RemyHandGeometry.TryHandBasis(w, new Vector3(0, 0, 1), null, new Vector3(-1, 0, 0), true, out _, out _));
            Assert.IsFalse(RemyHandGeometry.TryHandBasis(w, new Vector3(0, 0, 1), new Vector3(1, 0, 0), null, true, out _, out _));
        }

        [Test]
        public void TryHandBasis_DegenerateReturnsFalse()
        {
            var w = Vector3.zero;
            // mid==wrist → f 退化
            Assert.IsFalse(RemyHandGeometry.TryHandBasis(w, w, new Vector3(1, 0, 0), new Vector3(-1, 0, 0), true, out _, out _),
                "mid==wrist");
            // idx==pnk → cross 退化
            var same = new Vector3(1, 0, 0);
            Assert.IsFalse(RemyHandGeometry.TryHandBasis(w, new Vector3(0, 0, 1), same, same, true, out _, out _),
                "idx==pnk");
        }

        // --- MakeW ---

        [Test]
        public void MakeW_MapsRemyBasisOntoAnchorBasis()
        {
            Vector3 remF = new(0f, 0f, 1f), remB = new(0f, 1f, 0f);
            Vector3 aF = new(1f, 0f, 0f), aB = new(0f, 1f, 0f);

            Quaternion w = RemyHandGeometry.MakeW(remF, remB, aF, aB);
            Quaternion mapped = w * Quaternion.LookRotation(remF, remB);
            Quaternion anchor = Quaternion.LookRotation(aF, aB);

            AssertVec(anchor * Vector3.forward, mapped * Vector3.forward, "forward が anchor 基底へ写る");
            AssertVec(anchor * Vector3.up, mapped * Vector3.up, "up が anchor 基底へ写る");
        }

        // --- RestRotFor ---

        [Test]
        public void RestRotFor_PointsAnchorForwardTowardDesired()
        {
            Vector3 aF = new(1f, 0f, 0f), aB = new(0f, 1f, 0f);
            Quaternion q = RemyHandGeometry.RestRotFor(aF, aB);

            Vector3 desiredF = new Vector3(0f, -0.21f, 0.98f).normalized;
            Vector3 got = (q * Quaternion.LookRotation(aF, aB)) * Vector3.forward;
            AssertVec(desiredF, got, "指を前・やや下へ向ける");
        }

        // --- FkLive ---

        [Test]
        public void FkLive_BelowMinBoneCountReturnsZero()
        {
            var layout = new HandSkeletonLayout { BoneCount = 16 };
            for (int i = 0; i < N; i++) { layout.ParentIndex[i] = -1; layout.BindLocalPos[i] = Vector3.zero; }
            Assert.AreEqual(0, RemyHandGeometry.FkLive(layout, IdentityRots()));
        }

        [Test]
        public void FkLive_ResolvesNonMonotonicParentOrderIteratively()
        {
            // ParentIndex を子→親の逆順に: bone i の親 = i+1（root=23, chain 23→..→0）。単一 pass では解けない。
            var layout = new HandSkeletonLayout { BoneCount = N };
            var live = new Quaternion[N];
            for (int i = 0; i < N; i++)
            {
                layout.ParentIndex[i] = (short)(i == N - 1 ? -1 : i + 1);
                layout.BindLocalPos[i] = new Vector3(0.01f * i, 0.02f, 0.03f);
                live[i] = Quaternion.Euler(0f, 10f, 0f);
            }

            int n = RemyHandGeometry.FkLive(layout, live);
            Assert.AreEqual(N, n, "反復 pass が全ボーンを解いて n を返す");

            // FkPos[child] == FkPos[parent] + FkRot[parent]*BindLocalPos[child]（child=0, parent=1）
            const int child = 0, parent = 1;
            Assert.IsTrue(RemyHandGeometry.FkDone[child] && RemyHandGeometry.FkDone[parent]);
            Vector3 expected = RemyHandGeometry.FkPos[parent]
                + RemyHandGeometry.FkRot[parent] * layout.BindLocalPos[child];
            AssertVec(expected, RemyHandGeometry.FkPos[child], "FK 親子合成");
        }

        // --- TryAnchorBasisFromLayout ---

        [Test]
        public void TryAnchorBasisFromLayout_UnresolvedRequiredBonesReturnsFalse()
        {
            // bone6↔7 の循環 → 6,7 と依存する 8.. が解けない（FkDone[6]=false）
            var layout = new HandSkeletonLayout { BoneCount = N };
            for (int i = 0; i < N; i++)
            {
                layout.ParentIndex[i] = (short)(i - 1);
                layout.BindLocalPos[i] = new Vector3(0.01f * i, 0.02f, 0.03f);
            }
            layout.ParentIndex[6] = 7;
            layout.ParentIndex[7] = 6;

            Assert.IsFalse(RemyHandGeometry.TryAnchorBasisFromLayout(layout, IdentityRots(), true, out _, out _));
        }

        [Test]
        public void TryAnchorBasisFromLayout_MatchesTryHandBasisWhenResolved()
        {
            // 全ボーン root（parent=-1）→ FkPos[i]=BindLocalPos[i]。0/6/9/16 を非退化に配置。
            var layout = new HandSkeletonLayout { BoneCount = N };
            for (int i = 0; i < N; i++) { layout.ParentIndex[i] = -1; layout.BindLocalPos[i] = Vector3.zero; }
            layout.BindLocalPos[0] = Vector3.zero;                 // wrist
            layout.BindLocalPos[9] = new Vector3(0f, 0f, 1f);      // middle1（mid）
            layout.BindLocalPos[6] = new Vector3(1f, 0f, 0f);      // index1（idx）
            layout.BindLocalPos[16] = new Vector3(-1f, 0f, 0f);    // pinky1（pnk）

            bool ok = RemyHandGeometry.TryAnchorBasisFromLayout(layout, IdentityRots(), true, out var f0, out var b0);
            Assert.IsTrue(ok);

            // バッファは直後まで有効。TryHandBasis(FkPos[0],FkPos[9],FkPos[6],FkPos[16],...) と一致
            RemyHandGeometry.TryHandBasis(RemyHandGeometry.FkPos[0], RemyHandGeometry.FkPos[9],
                RemyHandGeometry.FkPos[6], RemyHandGeometry.FkPos[16], true, out var f2, out var b2);
            AssertVec(f2, f0, "f が TryHandBasis と一致");
            AssertVec(b2, b0, "b が TryHandBasis と一致");
        }
    }
}
