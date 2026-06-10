#nullable enable
using TableDuoVr.Hands;
using Unity.Netcode;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// AvatarPose ⇔ FastBuffer の序列化。頭・手首は float32、指 bone は half（FloatToHalf）。
    /// 1 pose ≈ 460B。per-bone NetworkTransform は使わない（規約）。
    /// </summary>
    public static class PoseCodec
    {
        /// <summary>sender id (8B) + pose 本体 + 余裕。Unreliable MTU(~1400B) 内に収まること。</summary>
        public const int MaxBytes = 600;

        public static void Write(ref FastBufferWriter w, AvatarPose p)
        {
            WriteV3(ref w, p.HeadPos);
            WriteQf(ref w, p.HeadRot);
            WriteV3(ref w, p.WristPosL);
            WriteQf(ref w, p.WristRotL);
            WriteV3(ref w, p.WristPosR);
            WriteQf(ref w, p.WristRotR);
            byte flags = (byte)((p.TrackedL ? 1 : 0) | (p.TrackedR ? 2 : 0));
            w.WriteValueSafe(flags);
            WriteBones(ref w, p.BonesL);
            WriteBones(ref w, p.BonesR);
        }

        public static void Read(ref FastBufferReader r, AvatarPose p)
        {
            ReadV3(ref r, out p.HeadPos);
            ReadQf(ref r, out p.HeadRot);
            ReadV3(ref r, out p.WristPosL);
            ReadQf(ref r, out p.WristRotL);
            ReadV3(ref r, out p.WristPosR);
            ReadQf(ref r, out p.WristRotR);
            r.ReadValueSafe(out byte flags);
            p.TrackedL = (flags & 1) != 0;
            p.TrackedR = (flags & 2) != 0;
            ReadBones(ref r, p.BonesL);
            ReadBones(ref r, p.BonesR);
        }

        private static void WriteBones(ref FastBufferWriter w, Quaternion[] bones)
        {
            for (int i = 0; i < AvatarPose.BonesPerHand; i++)
            {
                var q = bones[i];
                w.WriteValueSafe(Mathf.FloatToHalf(q.x));
                w.WriteValueSafe(Mathf.FloatToHalf(q.y));
                w.WriteValueSafe(Mathf.FloatToHalf(q.z));
                w.WriteValueSafe(Mathf.FloatToHalf(q.w));
            }
        }

        private static void ReadBones(ref FastBufferReader r, Quaternion[] bones)
        {
            for (int i = 0; i < AvatarPose.BonesPerHand; i++)
            {
                r.ReadValueSafe(out ushort x);
                r.ReadValueSafe(out ushort y);
                r.ReadValueSafe(out ushort z);
                r.ReadValueSafe(out ushort w);
                // half 化で正規化が崩れるので戻す
                var q = new Quaternion(Mathf.HalfToFloat(x), Mathf.HalfToFloat(y),
                    Mathf.HalfToFloat(z), Mathf.HalfToFloat(w));
                bones[i] = q.normalized;
            }
        }

        private static void WriteV3(ref FastBufferWriter w, Vector3 v)
        {
            w.WriteValueSafe(v.x);
            w.WriteValueSafe(v.y);
            w.WriteValueSafe(v.z);
        }

        private static void ReadV3(ref FastBufferReader r, out Vector3 v)
        {
            r.ReadValueSafe(out v.x);
            r.ReadValueSafe(out v.y);
            r.ReadValueSafe(out v.z);
        }

        private static void WriteQf(ref FastBufferWriter w, Quaternion q)
        {
            w.WriteValueSafe(q.x);
            w.WriteValueSafe(q.y);
            w.WriteValueSafe(q.z);
            w.WriteValueSafe(q.w);
        }

        private static void ReadQf(ref FastBufferReader r, out Quaternion q)
        {
            r.ReadValueSafe(out q.x);
            r.ReadValueSafe(out q.y);
            r.ReadValueSafe(out q.z);
            r.ReadValueSafe(out q.w);
        }
    }
}
