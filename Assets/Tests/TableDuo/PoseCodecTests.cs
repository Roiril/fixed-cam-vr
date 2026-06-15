#nullable enable
using NUnit.Framework;
using TableDuoVr.Hands;
using TableDuoVr.Net;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace TableDuoVr.Tests
{
    public class PoseCodecTests
    {
        [Test]
        public void WriteRead_RoundTrip_PreservesPose()
        {
            var src = new AvatarPose
            {
                HeadPos = new Vector3(0.1f, 1.55f, -0.2f),
                HeadRot = Quaternion.Euler(10f, 45f, 5f),
                WristPosL = new Vector3(-0.2f, 0.9f, 0.3f),
                WristRotL = Quaternion.Euler(0f, 90f, 20f),
                WristPosR = new Vector3(0.25f, 0.95f, 0.28f),
                WristRotR = Quaternion.Euler(5f, -90f, -15f),
                TrackedL = true,
                TrackedR = false,
                PinchL = false,
                PinchR = true,
                Seq = 123456u,
                CaptureMs = 1_700_000_000_123L,
            };
            for (int i = 0; i < AvatarPose.BonesPerHand; i++)
            {
                src.BonesL[i] = Quaternion.Euler(i * 3f, i * 5f, i * 2f);
                src.BonesR[i] = Quaternion.Euler(-i * 2f, i * 4f, -i * 3f);
            }

            var writer = new FastBufferWriter(PoseCodec.MaxBytes, Allocator.Temp);
            byte[] bytes;
            try
            {
                PoseCodec.Write(ref writer, src);
                bytes = writer.ToArray();
            }
            finally
            {
                writer.Dispose();
            }

            var dst = new AvatarPose();
            var reader = new FastBufferReader(bytes, Allocator.Temp);
            try
            {
                PoseCodec.Read(ref reader, dst);
            }
            finally
            {
                reader.Dispose();
            }

            // float32 部はほぼ完全一致
            AssertVec(src.HeadPos, dst.HeadPos, 1e-5f, "HeadPos");
            AssertQuat(src.HeadRot, dst.HeadRot, 1e-5f, "HeadRot");
            AssertVec(src.WristPosL, dst.WristPosL, 1e-5f, "WristPosL");
            AssertQuat(src.WristRotL, dst.WristRotL, 1e-5f, "WristRotL");
            AssertVec(src.WristPosR, dst.WristPosR, 1e-5f, "WristPosR");
            AssertQuat(src.WristRotR, dst.WristRotR, 1e-5f, "WristRotR");
            Assert.AreEqual(src.TrackedL, dst.TrackedL);
            Assert.AreEqual(src.TrackedR, dst.TrackedR);
            Assert.AreEqual(src.PinchL, dst.PinchL);
            Assert.AreEqual(src.PinchR, dst.PinchR);
            Assert.AreEqual(src.Seq, dst.Seq);
            Assert.AreEqual(src.CaptureMs, dst.CaptureMs);

            // bone は half 精度。角度誤差 1° 未満を要求
            for (int i = 0; i < AvatarPose.BonesPerHand; i++)
            {
                float angL = Quaternion.Angle(src.BonesL[i], dst.BonesL[i]);
                float angR = Quaternion.Angle(src.BonesR[i], dst.BonesR[i]);
                Assert.Less(angL, 1f, $"BonesL[{i}] 角度誤差 {angL}°");
                Assert.Less(angR, 1f, $"BonesR[{i}] 角度誤差 {angR}°");
            }
        }

        [Test]
        public void Write_FitsInMaxBytes()
        {
            var pose = new AvatarPose();
            var writer = new FastBufferWriter(PoseCodec.MaxBytes, Allocator.Temp);
            try
            {
                // sender id ぶんの 8B を足しても MaxBytes に収まること（ConnectionManager と同じ構成）
                writer.WriteValueSafe(0UL);
                PoseCodec.Write(ref writer, pose);
                Assert.LessOrEqual(writer.Position, PoseCodec.MaxBytes);
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void AssertVec(Vector3 a, Vector3 b, float eps, string label)
        {
            Assert.Less(Vector3.Distance(a, b), eps, label);
        }

        private static void AssertQuat(Quaternion a, Quaternion b, float eps, string label)
        {
            Assert.Less(Quaternion.Angle(a, b), eps, label);
        }
    }
}
