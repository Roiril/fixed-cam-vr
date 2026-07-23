#nullable enable
using NUnit.Framework;
using TableDuoVr.Hands;
using TableDuoVr.Net;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace TableDuoVr.Tests
{
    /// <summary>
    /// PoseCodec.WriteLayout/ReadLayout（手 layout の wire 契約）の往復・null・部分 BoneCount・
    /// 防御的クランプ。既存 PoseCodecTests が未被覆。指先 FK を本人の手寸法で計算する境界を守る。
    /// </summary>
    public class PoseCodecLayoutTests
    {
        private static HandSkeletonLayout MakeLayout(int boneCount)
        {
            var l = new HandSkeletonLayout { BoneCount = boneCount };
            for (int i = 0; i < AvatarPose.BonesPerHand; i++)
            {
                l.ParentIndex[i] = (short)(i - 1);
                l.BindLocalPos[i] = new Vector3(0.01f * i, -0.02f * i, 0.03f * i);
                l.BindLocalRot[i] = Quaternion.Euler(i * 1.5f, i * 2.5f, i * 0.5f);
            }
            return l;
        }

        [Test]
        public void WriteReadLayout_RoundTrip()
        {
            var src = MakeLayout(24);
            var writer = new FastBufferWriter(PoseCodec.MaxLayoutBytes, Allocator.Temp);
            byte[] bytes;
            try
            {
                PoseCodec.WriteLayout(ref writer, src);
                bytes = writer.ToArray();
            }
            finally { writer.Dispose(); }

            var reader = new FastBufferReader(bytes, Allocator.Temp);
            HandSkeletonLayout? dst;
            try { dst = PoseCodec.ReadLayout(ref reader); }
            finally { reader.Dispose(); }

            Assert.IsNotNull(dst);
            Assert.AreEqual(24, dst!.BoneCount);
            for (int i = 0; i < AvatarPose.BonesPerHand; i++)
            {
                Assert.AreEqual(src.ParentIndex[i], dst.ParentIndex[i], $"ParentIndex[{i}]");
                Assert.Less(Vector3.Distance(src.BindLocalPos[i], dst.BindLocalPos[i]), 1e-5f, $"BindLocalPos[{i}]");
                Assert.Less(Quaternion.Angle(src.BindLocalRot[i], dst.BindLocalRot[i]), 1e-3f, $"BindLocalRot[{i}]");
            }
        }

        [Test]
        public void WriteReadLayout_Null_RoundTripsToNull()
        {
            var writer = new FastBufferWriter(PoseCodec.MaxLayoutBytes, Allocator.Temp);
            byte[] bytes;
            try
            {
                PoseCodec.WriteLayout(ref writer, null);
                bytes = writer.ToArray();
            }
            finally { writer.Dispose(); }

            var reader = new FastBufferReader(bytes, Allocator.Temp);
            HandSkeletonLayout? dst;
            try { dst = PoseCodec.ReadLayout(ref reader); }
            finally { reader.Dispose(); }

            Assert.IsNull(dst, "count=0 経路 → null");
        }

        [Test]
        public void WriteReadLayout_PartialBoneCount()
        {
            var src = MakeLayout(10);
            const ushort sentinel = 0xBEEF;
            var writer = new FastBufferWriter(PoseCodec.MaxLayoutBytes, Allocator.Temp);
            byte[] bytes;
            try
            {
                PoseCodec.WriteLayout(ref writer, src);
                writer.WriteValueSafe(sentinel); // reader 位置が過不足なく進むことの検証用
                bytes = writer.ToArray();
            }
            finally { writer.Dispose(); }

            var reader = new FastBufferReader(bytes, Allocator.Temp);
            try
            {
                var dst = PoseCodec.ReadLayout(ref reader);
                Assert.IsNotNull(dst);
                Assert.AreEqual(10, dst!.BoneCount);
                for (int i = 0; i < 10; i++)
                {
                    Assert.AreEqual(src.ParentIndex[i], dst.ParentIndex[i], $"ParentIndex[{i}]");
                }
                // i>=10 は default（未格納）
                Assert.AreEqual((short)0, dst.ParentIndex[10]);
                // reader 位置が正しく layout ぶんだけ進んでいれば sentinel が読める
                reader.ReadValueSafe(out ushort back);
                Assert.AreEqual(sentinel, back, "layout 読取後の reader 位置が過不足なし");
            }
            finally { reader.Dispose(); }
        }

        [Test]
        public void ReadLayout_CountClampsToBonesPerHand()
        {
            const ushort sentinel = 0xBEEF;
            var writer = new FastBufferWriter(PoseCodec.MaxLayoutBytes, Allocator.Temp);
            byte[] bytes;
            try
            {
                // 自作バッファ: count=25（> 24）と 25 エントリを書く（通常 wire では Write が 24 に clamp するので到達しない）
                writer.WriteValueSafe((byte)25);
                for (int i = 0; i < 25; i++)
                {
                    writer.WriteValueSafe((short)i);
                    writer.WriteValueSafe(0.01f * i); writer.WriteValueSafe(-0.02f * i); writer.WriteValueSafe(0.03f * i);
                    writer.WriteValueSafe(0f); writer.WriteValueSafe(0f); writer.WriteValueSafe(0f); writer.WriteValueSafe(1f);
                }
                writer.WriteValueSafe(sentinel);
                bytes = writer.ToArray();
            }
            finally { writer.Dispose(); }

            var reader = new FastBufferReader(bytes, Allocator.Temp);
            try
            {
                var dst = PoseCodec.ReadLayout(ref reader);
                Assert.IsNotNull(dst);
                Assert.AreEqual(24, dst!.BoneCount, "BoneCount は 24 にクランプ");
                // 全 25 エントリを消費している（例外なし + sentinel が読める）
                reader.ReadValueSafe(out ushort back);
                Assert.AreEqual(sentinel, back, "全 25 エントリを消費");
            }
            finally { reader.Dispose(); }
        }
    }
}
