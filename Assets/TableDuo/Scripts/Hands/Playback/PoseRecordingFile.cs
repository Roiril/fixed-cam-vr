#nullable enable
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace TableDuoVr.Hands.Playback
{
    /// <summary>
    /// 手姿勢録画のバイナリ入出力。HandSkeletonLayout を同梱するので
    /// OVR の無い PC（L0 検証）でも指の形まで再生できる。
    /// </summary>
    public static class PoseRecordingFile
    {
        private const int Magic = 0x54445632; // "TDV2"（pinch フラグ追加で format 改訂）

        public sealed class Data
        {
            public float Interval = 1f / 30f;
            public HandSkeletonLayout? LayoutL;
            public HandSkeletonLayout? LayoutR;
            public readonly List<AvatarPose> Frames = new();
        }

        public static void Save(string path, IReadOnlyList<AvatarPose> frames, float interval,
            HandSkeletonLayout? layoutL, HandSkeletonLayout? layoutR)
        {
            using var w = new BinaryWriter(File.Create(path));
            w.Write(Magic);
            w.Write(AvatarPose.BonesPerHand);
            bool hasLayout = layoutL != null && layoutR != null;
            w.Write(hasLayout);
            if (hasLayout)
            {
                WriteLayout(w, layoutL!);
                WriteLayout(w, layoutR!);
            }
            w.Write(frames.Count);
            w.Write(interval);
            for (int i = 0; i < frames.Count; i++) WritePose(w, frames[i]);
        }

        public static Data? Load(string path)
        {
            if (!File.Exists(path)) return null;
            using var r = new BinaryReader(File.OpenRead(path));
            if (r.ReadInt32() != Magic) return null;
            int boneCount = r.ReadInt32();
            if (boneCount != AvatarPose.BonesPerHand) return null;

            var data = new Data();
            if (r.ReadBoolean())
            {
                data.LayoutL = ReadLayout(r);
                data.LayoutR = ReadLayout(r);
            }
            int frameCount = r.ReadInt32();
            data.Interval = r.ReadSingle();
            for (int i = 0; i < frameCount; i++)
            {
                var p = new AvatarPose();
                ReadPose(r, p);
                data.Frames.Add(p);
            }
            return data;
        }

        private static void WriteLayout(BinaryWriter w, HandSkeletonLayout l)
        {
            w.Write(l.BoneCount);
            for (int i = 0; i < AvatarPose.BonesPerHand; i++)
            {
                w.Write(l.ParentIndex[i]);
                WriteV3(w, l.BindLocalPos[i]);
                WriteQ(w, l.BindLocalRot[i]);
            }
        }

        private static HandSkeletonLayout ReadLayout(BinaryReader r)
        {
            var l = new HandSkeletonLayout { BoneCount = r.ReadInt32() };
            for (int i = 0; i < AvatarPose.BonesPerHand; i++)
            {
                l.ParentIndex[i] = r.ReadInt16();
                l.BindLocalPos[i] = ReadV3(r);
                l.BindLocalRot[i] = ReadQ(r);
            }
            return l;
        }

        private static void WritePose(BinaryWriter w, AvatarPose p)
        {
            WriteV3(w, p.HeadPos);
            WriteQ(w, p.HeadRot);
            WriteV3(w, p.WristPosL);
            WriteQ(w, p.WristRotL);
            WriteV3(w, p.WristPosR);
            WriteQ(w, p.WristRotR);
            w.Write(p.TrackedL);
            w.Write(p.TrackedR);
            w.Write(p.PinchL);
            w.Write(p.PinchR);
            for (int i = 0; i < AvatarPose.BonesPerHand; i++) WriteQ(w, p.BonesL[i]);
            for (int i = 0; i < AvatarPose.BonesPerHand; i++) WriteQ(w, p.BonesR[i]);
        }

        private static void ReadPose(BinaryReader r, AvatarPose p)
        {
            p.HeadPos = ReadV3(r);
            p.HeadRot = ReadQ(r);
            p.WristPosL = ReadV3(r);
            p.WristRotL = ReadQ(r);
            p.WristPosR = ReadV3(r);
            p.WristRotR = ReadQ(r);
            p.TrackedL = r.ReadBoolean();
            p.TrackedR = r.ReadBoolean();
            p.PinchL = r.ReadBoolean();
            p.PinchR = r.ReadBoolean();
            for (int i = 0; i < AvatarPose.BonesPerHand; i++) p.BonesL[i] = ReadQ(r);
            for (int i = 0; i < AvatarPose.BonesPerHand; i++) p.BonesR[i] = ReadQ(r);
        }

        private static void WriteV3(BinaryWriter w, Vector3 v)
        {
            w.Write(v.x); w.Write(v.y); w.Write(v.z);
        }

        private static Vector3 ReadV3(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        private static void WriteQ(BinaryWriter w, Quaternion q)
        {
            w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w);
        }

        private static Quaternion ReadQ(BinaryReader r) =>
            new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }
}
