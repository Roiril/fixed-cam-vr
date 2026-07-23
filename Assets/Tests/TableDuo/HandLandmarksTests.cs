#nullable enable
using NUnit.Framework;
using TableDuoVr.Hands;
using UnityEngine;

namespace TableDuoVr.Tests
{
    /// <summary>
    /// 7 ランドマーク FK（HandLandmarks.Compute）の純計算を固定する。RQ2/RQ3 分析 CSV の座標を生む
    /// 純 FK なので、バグると研究データが静かに壊れる。GameObject 不要（HandSkeletonLayout は plain class）。
    /// legacy 24-bone の親&lt;子 単調前提で書く（非単調は RemyHandGeometry.FkLive 側で検証）。
    /// </summary>
    public class HandLandmarksTests
    {
        private const int N = AvatarPose.BonesPerHand; // 24

        private static Quaternion[] IdentityBones()
        {
            var b = new Quaternion[N];
            for (int i = 0; i < N; i++) b[i] = Quaternion.identity;
            return b;
        }

        /// <summary>親=i-1 の単調チェーン layout（BindLocalPos は per-bone 指定）。</summary>
        private static HandSkeletonLayout ChainLayout(int boneCount, System.Func<int, Vector3> bindPos)
        {
            var l = new HandSkeletonLayout { BoneCount = boneCount };
            for (int i = 0; i < N; i++)
            {
                l.ParentIndex[i] = (short)(i - 1); // bone0 の親=-1（wrist）
                l.BindLocalPos[i] = bindPos(i);
                l.BindLocalRot[i] = Quaternion.identity;
            }
            return l;
        }

        private static void AssertVec(Vector3 expected, Vector3 actual, string msg)
            => Assert.Less(Vector3.Distance(expected, actual), 1e-4f, $"{msg} 期待={expected} 実際={actual}");

        [Test]
        public void NullLayout_FillsWristAndReturnsFalse()
        {
            var results = new Vector3[HandLandmarks.Count];
            var wrist = new Vector3(1f, 2f, 3f);
            bool ok = HandLandmarks.Compute(null, wrist, Quaternion.identity, IdentityBones(), results);
            Assert.IsFalse(ok);
            for (int i = 0; i < HandLandmarks.Count; i++) AssertVec(wrist, results[i], $"results[{i}]");
        }

        [Test]
        public void ZeroBoneCount_FillsWristAndReturnsFalse()
        {
            var results = new Vector3[HandLandmarks.Count];
            var wrist = new Vector3(-2f, 0.5f, 4f);
            var layout = new HandSkeletonLayout { BoneCount = 0 };
            bool ok = HandLandmarks.Compute(layout, wrist, Quaternion.identity, IdentityBones(), results);
            Assert.IsFalse(ok);
            for (int i = 0; i < HandLandmarks.Count; i++) AssertVec(wrist, results[i], $"results[{i}]");
        }

        [Test]
        public void IdentityChain_MapsLandmarkBoneIndicesCorrectly()
        {
            // BindLocalPos[0]=0, それ以外は (1,0,0)。identity 回転で Pos[i]=wrist+(i,0,0)。
            var layout = ChainLayout(N, i => i == 0 ? Vector3.zero : new Vector3(1f, 0f, 0f));
            var results = new Vector3[HandLandmarks.Count];
            var wrist = new Vector3(10f, 20f, 30f);

            bool ok = HandLandmarks.Compute(layout, wrist, Quaternion.identity, IdentityBones(), results);
            Assert.IsTrue(ok);

            AssertVec(wrist, results[0], "wrist");
            AssertVec(wrist + new Vector3(HandBoneTable.Palm, 0f, 0f), results[1], "palm=BoneId9");
            AssertVec(wrist + new Vector3(HandBoneTable.ThumbTip, 0f, 0f), results[2], "thumbTip=19");
            AssertVec(wrist + new Vector3(HandBoneTable.IndexTip, 0f, 0f), results[3], "indexTip=20");
            AssertVec(wrist + new Vector3(HandBoneTable.MiddleTip, 0f, 0f), results[4], "middleTip=21");
            AssertVec(wrist + new Vector3(HandBoneTable.RingTip, 0f, 0f), results[5], "ringTip=22");
            AssertVec(wrist + new Vector3(HandBoneTable.PinkyTip, 0f, 0f), results[6], "pinkyTip=23");
        }

        [Test]
        public void WristRotation_PropagatesToBones()
        {
            // 全ボーンが wrist 直下（parent=-1）。BindLocalPos[9]=(1,0,0) のみ。wristRot=yaw90。
            var layout = new HandSkeletonLayout { BoneCount = N };
            for (int i = 0; i < N; i++)
            {
                layout.ParentIndex[i] = -1;
                layout.BindLocalPos[i] = i == HandBoneTable.Palm ? new Vector3(1f, 0f, 0f) : Vector3.zero;
                layout.BindLocalRot[i] = Quaternion.identity;
            }
            var results = new Vector3[HandLandmarks.Count];
            var wrist = Vector3.zero;
            var yaw90 = Quaternion.Euler(0f, 90f, 0f);

            HandLandmarks.Compute(layout, wrist, yaw90, IdentityBones(), results);

            // Pos[9] = wrist + wristRot*(1,0,0) = (0,0,-1)
            AssertVec(new Vector3(0f, 0f, -1f), results[1], "palm が wristRot で回る");
        }

        [Test]
        public void NonIdentityBoneRotation_ChainsThroughDescendants()
        {
            // 親=i-1 チェーン。BindLocalPos[0]=0・他=(1,0,0)。boneRots[9]=yaw90 のみ。
            var layout = ChainLayout(N, i => i == 0 ? Vector3.zero : new Vector3(1f, 0f, 0f));
            var bones = IdentityBones();
            bones[HandBoneTable.Palm] = Quaternion.Euler(0f, 90f, 0f); // bone9 に回転
            var results = new Vector3[HandLandmarks.Count];
            var wrist = Vector3.zero;

            HandLandmarks.Compute(layout, wrist, Quaternion.identity, bones, results);

            // bone9 の回転は bone10.. へ連鎖: Pos[i]=Pos[i-1]+yaw90*(1,0,0)=前+(0,0,-1)（i>=10）
            // Pos[21]（middleTip）= (9,0,0) + 12*(0,0,-1) = (9,0,-12)
            AssertVec(new Vector3(9f, 0f, -12f), results[4], "boneRots[9] が子孫へ連鎖");
        }

        [Test]
        public void BoneCountBelowTipIndex_FallsBackToWrist()
        {
            var layout = ChainLayout(10, i => i == 0 ? Vector3.zero : new Vector3(1f, 0f, 0f));
            var results = new Vector3[HandLandmarks.Count];
            var wrist = new Vector3(5f, 0f, 0f);

            bool ok = HandLandmarks.Compute(layout, wrist, Quaternion.identity, IdentityBones(), results);
            Assert.IsTrue(ok, "layout 有り・BoneCount!=0 なら true");

            // Palm=9 < 10 → 計算値。tips(19..23) >= 10 → wrist フォールバック
            AssertVec(wrist + new Vector3(HandBoneTable.Palm, 0f, 0f), results[1], "palm は計算値");
            AssertVec(wrist, results[2], "thumbTip フォールバック");
            AssertVec(wrist, results[3], "indexTip フォールバック");
            AssertVec(wrist, results[4], "middleTip フォールバック");
            AssertVec(wrist, results[5], "ringTip フォールバック");
            AssertVec(wrist, results[6], "pinkyTip フォールバック");
        }

        [Test]
        public void ContractCountAndNamesOrder()
        {
            Assert.AreEqual(7, HandLandmarks.Count);
            Assert.AreEqual(7, HandLandmarks.Names.Length);
            var expected = new[] { "wrist", "palm", "thumbTip", "indexTip", "middleTip", "ringTip", "pinkyTip" };
            CollectionAssert.AreEqual(expected, HandLandmarks.Names);
        }
    }
}
