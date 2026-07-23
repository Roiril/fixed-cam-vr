#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using TableDuoVr.Hands;
using TableDuoVr.Hands.Playback;
using UnityEngine;

namespace TableDuoVr.Tests
{
    /// <summary>
    /// TDV2 記録再生ファイル（PoseRecordingFile）の往復・失敗系・ヘッダ回帰。
    /// L0/診断/lossless/リプレイ全記録系がこの 1 フォーマットに依存する。
    /// temp path は temporaryCachePath + Guid、[TearDown] で削除して persistentDataPath を汚さない。
    /// </summary>
    public class PoseRecordingFileTests
    {
        private const int Magic = 0x54445632;
        private readonly List<string> _paths = new();

        private string NewPath()
        {
            string p = Path.Combine(Application.temporaryCachePath, "tdv_test_" + Guid.NewGuid().ToString("N") + ".bin");
            _paths.Add(p);
            return p;
        }

        [TearDown]
        public void Cleanup()
        {
            foreach (var p in _paths)
            {
                try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
            }
            _paths.Clear();
        }

        private static AvatarPose MakePose(int seed)
        {
            var p = new AvatarPose
            {
                HeadPos = new Vector3(0.1f * seed, 1.55f + 0.01f * seed, -0.2f * seed),
                HeadRot = Quaternion.Euler(10f + seed, 45f - seed, 5f + 2f * seed),
                WristPosL = new Vector3(-0.2f - seed, 0.9f + 0.02f * seed, 0.3f * seed),
                WristRotL = Quaternion.Euler(seed, 90f, 20f - seed),
                WristPosR = new Vector3(0.25f + seed, 0.95f, 0.28f * seed),
                WristRotR = Quaternion.Euler(5f, -90f + seed, -15f),
                TrackedL = (seed & 1) == 0,
                TrackedR = (seed & 1) == 1,
                PinchL = (seed & 2) == 0,
                PinchR = (seed & 2) == 2,
                Seq = (uint)(1000 + seed),
                CaptureMs = 1_700_000_000_000L + seed,
            };
            for (int i = 0; i < AvatarPose.BonesPerHand; i++)
            {
                p.BonesL[i] = Quaternion.Euler(i * 3f + seed, i * 5f, i * 2f - seed);
                p.BonesR[i] = Quaternion.Euler(-i * 2f, i * 4f + seed, -i * 3f);
            }
            return p;
        }

        private static HandSkeletonLayout MakeLayout(int boneCount, int seed)
        {
            var l = new HandSkeletonLayout { BoneCount = boneCount };
            for (int i = 0; i < AvatarPose.BonesPerHand; i++)
            {
                l.ParentIndex[i] = (short)(i - 1);
                l.BindLocalPos[i] = new Vector3(0.01f * i + seed, -0.02f * i, 0.03f * i);
                l.BindLocalRot[i] = Quaternion.Euler(i * 1.5f + seed, i * 2.5f, i * 0.5f);
            }
            return l;
        }

        [Test]
        public void SaveLoad_RoundTrip_PreservesFramesAndInterval()
        {
            string path = NewPath();
            var frames = new List<AvatarPose> { MakePose(1), MakePose(2), MakePose(3) };
            float interval = 1f / 45f;
            PoseRecordingFile.Save(path, frames, interval, MakeLayout(24, 0), MakeLayout(24, 1));

            var data = PoseRecordingFile.Load(path);
            Assert.IsNotNull(data);
            Assert.AreEqual(3, data!.Frames.Count);
            Assert.AreEqual(interval, data.Interval, 1e-6f);

            for (int f = 0; f < 3; f++)
            {
                var a = frames[f];
                var b = data.Frames[f];
                Assert.Less(Vector3.Distance(a.HeadPos, b.HeadPos), 1e-6f, $"HeadPos[{f}]");
                Assert.Less(Quaternion.Angle(a.HeadRot, b.HeadRot), 1e-4f, $"HeadRot[{f}]");
                Assert.Less(Vector3.Distance(a.WristPosL, b.WristPosL), 1e-6f, $"WristPosL[{f}]");
                Assert.Less(Quaternion.Angle(a.WristRotL, b.WristRotL), 1e-4f, $"WristRotL[{f}]");
                Assert.Less(Vector3.Distance(a.WristPosR, b.WristPosR), 1e-6f, $"WristPosR[{f}]");
                Assert.Less(Quaternion.Angle(a.WristRotR, b.WristRotR), 1e-4f, $"WristRotR[{f}]");
                Assert.AreEqual(a.TrackedL, b.TrackedL, $"TrackedL[{f}]");
                Assert.AreEqual(a.TrackedR, b.TrackedR, $"TrackedR[{f}]");
                Assert.AreEqual(a.PinchL, b.PinchL, $"PinchL[{f}]");
                Assert.AreEqual(a.PinchR, b.PinchR, $"PinchR[{f}]");
                for (int i = 0; i < AvatarPose.BonesPerHand; i++)
                {
                    Assert.Less(Quaternion.Angle(a.BonesL[i], b.BonesL[i]), 1e-4f, $"BonesL[{f}][{i}]");
                    Assert.Less(Quaternion.Angle(a.BonesR[i], b.BonesR[i]), 1e-4f, $"BonesR[{f}][{i}]");
                }
            }
        }

        [Test]
        public void SaveLoad_Layouts_RoundTrip()
        {
            string path = NewPath();
            var lL = MakeLayout(24, 7);
            var lR = MakeLayout(24, 9);
            PoseRecordingFile.Save(path, new List<AvatarPose> { MakePose(1) }, 1f / 30f, lL, lR);

            var data = PoseRecordingFile.Load(path);
            Assert.IsNotNull(data);
            Assert.IsNotNull(data!.LayoutL);
            Assert.IsNotNull(data.LayoutR);
            AssertLayout(lL, data.LayoutL!);
            AssertLayout(lR, data.LayoutR!);
        }

        private static void AssertLayout(HandSkeletonLayout a, HandSkeletonLayout b)
        {
            Assert.AreEqual(a.BoneCount, b.BoneCount, "BoneCount");
            for (int i = 0; i < AvatarPose.BonesPerHand; i++)
            {
                Assert.AreEqual(a.ParentIndex[i], b.ParentIndex[i], $"ParentIndex[{i}]");
                Assert.Less(Vector3.Distance(a.BindLocalPos[i], b.BindLocalPos[i]), 1e-5f, $"BindLocalPos[{i}]");
                Assert.Less(Quaternion.Angle(a.BindLocalRot[i], b.BindLocalRot[i]), 1e-3f, $"BindLocalRot[{i}]");
            }
        }

        [Test]
        public void Save_NoLayout_LoadHasNullLayouts()
        {
            string path = NewPath();
            var frames = new List<AvatarPose> { MakePose(1), MakePose(2) };
            // layoutL=null → hasLayout=false 経路
            PoseRecordingFile.Save(path, frames, 1f / 30f, null, MakeLayout(24, 0));

            var data = PoseRecordingFile.Load(path);
            Assert.IsNotNull(data);
            Assert.IsNull(data!.LayoutL);
            Assert.IsNull(data.LayoutR);
            Assert.AreEqual(2, data.Frames.Count);
        }

        [Test]
        public void Load_MissingFile_ReturnsNull()
        {
            string path = Path.Combine(Application.temporaryCachePath, "tdv_test_missing_" + Guid.NewGuid().ToString("N") + ".bin");
            Assert.IsNull(PoseRecordingFile.Load(path));
        }

        [Test]
        public void Load_BadMagic_ReturnsNull()
        {
            string path = NewPath();
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write(0x11223344); // 別 int
                w.Write(AvatarPose.BonesPerHand);
                w.Write(false);
                w.Write(0);
                w.Write(1f / 30f);
            }
            Assert.IsNull(PoseRecordingFile.Load(path));
        }

        [Test]
        public void Load_BoneCountMismatch_ReturnsNull()
        {
            string path = NewPath();
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write(Magic);
                w.Write(23); // ≠ 24
                w.Write(false);
                w.Write(0);
                w.Write(1f / 30f);
            }
            Assert.IsNull(PoseRecordingFile.Load(path));
        }

        [Test]
        public void Save_HeaderBytes_Fixture()
        {
            string path = NewPath();
            PoseRecordingFile.Save(path, new List<AvatarPose> { MakePose(1) }, 1f / 30f,
                MakeLayout(24, 0), MakeLayout(24, 1));

            byte[] bytes = File.ReadAllBytes(path);
            Assert.AreEqual(0x54445632, BitConverter.ToInt32(bytes, 0), "先頭 int = Magic");
            Assert.AreEqual(24, BitConverter.ToInt32(bytes, 4), "次 int = BonesPerHand");
            Assert.AreEqual(1, bytes[8], "次 byte = hasLayout フラグ（layout あり=1）");
        }
    }
}
