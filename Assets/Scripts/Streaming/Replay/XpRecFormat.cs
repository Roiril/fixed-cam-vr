#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace FixedCamVr.Streaming.Replay
{
    /// <summary>
    /// 体験の記録 1 本の形（フォルダ 1 つ）。
    ///
    /// <code>
    /// meta.json          版・端末・同梱 show.json の sha・カメラの並び・registration.json の本文
    /// track.bin          "XPT1" + 1 フレーム 1 行（下の Frame）
    /// cam_&lt;名前&gt;.mjr   受けて展開できた JPEG（RecordedSegmentFormat の MJR1。pts は記録の頭からの ms）
    /// xp.log             その間の [XP] テレメトリ（再生の結果と突き合わせる）
    /// </code>
    ///
    /// 時刻はすべて「記録の頭からの秒」（Quest の <c>Time.unscaledTime</c> の差）。
    /// 純粋（UnityEngine 非依存）。EditMode テストは MemoryStream で往復できる。
    /// </summary>
    public static class XpRecFormat
    {
        public const int Version = 1;
        public const string TrackFile = "track.bin";
        public const string MetaFile = "meta.json";
        public const string XpLogFile = "xp.log";
        public const string CamPrefix = "cam_";
        public const string CamSuffix = ".mjr";

        private static readonly byte[] Magic = { (byte)'X', (byte)'P', (byte)'T', (byte)'1' };

        /// <summary>1 行の大きさ（byte）。f32 t + 姿勢 3 つ（f32×7）+ i32 入力 + u8 言語 + u8 軽減 + u16 報告の累計。</summary>
        public const int FrameBytes = 4 + 3 * 7 * 4 + 4 + 4;

        public struct Pose
        {
            public float px, py, pz, qx, qy, qz, qw;
            public static Pose Identity => new Pose { qw = 1f };
        }

        public struct Frame
        {
            public float t;
            public Pose head, left, right;
            public RawInput input;
            public byte lang;     // ShowLang の値
            public byte relief;   // 0 / 1
            /// <summary>報告の累計（<c>ShowControlClient.VisitorMarkCount</c>）。再生がボタンで再現できたかの照合に使う。</summary>
            public ushort marks;
        }

        public static void WriteHeader(Stream s) => s.Write(Magic, 0, Magic.Length);

        public static void WriteFrame(BinaryWriter w, in Frame f)
        {
            w.Write(f.t);
            WritePose(w, f.head);
            WritePose(w, f.left);
            WritePose(w, f.right);
            w.Write(f.input.bits);
            w.Write(f.lang);
            w.Write(f.relief);
            w.Write(f.marks);
        }

        /// <summary>全行を読む。壊れた末尾（途中で切れた行）は捨てる。時刻が戻る行も捨てる。</summary>
        public static Frame[] ReadTrack(Stream s)
        {
            var head = new byte[4];
            if (s.Read(head, 0, 4) != 4) return Array.Empty<Frame>();
            for (int i = 0; i < 4; i++) if (head[i] != Magic[i]) return Array.Empty<Frame>();
            var list = new List<Frame>(4096);
            var r = new BinaryReader(s);
            float last = float.NegativeInfinity;
            try
            {
                while (s.Length - s.Position >= FrameBytes)
                {
                    var f = new Frame
                    {
                        t = r.ReadSingle(),
                        head = ReadPose(r),
                        left = ReadPose(r),
                        right = ReadPose(r),
                        input = new RawInput { bits = r.ReadInt32() },
                        lang = r.ReadByte(),
                        relief = r.ReadByte(),
                        marks = r.ReadUInt16(),
                    };
                    if (float.IsNaN(f.t) || f.t < last) continue;
                    last = f.t;
                    list.Add(f);
                }
            }
            catch (EndOfStreamException) { }
            return list.ToArray();
        }

        /// <summary>
        /// 時刻 <paramref name="t"/> の行（t 以下の最後）。<paramref name="from"/> は前回の位置（前へ進むだけで済ませる）。
        /// 頭より前なら 0。
        /// </summary>
        public static int IndexAt(Frame[] track, float t, int from)
        {
            if (track.Length == 0) return -1;
            int i = from < 0 ? 0 : Math.Min(from, track.Length - 1);
            while (i > 0 && track[i].t > t) i--;
            while (i + 1 < track.Length && track[i + 1].t <= t) i++;
            return i;
        }

        public static string CamFileName(string source)
        {
            var chars = source.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (!(char.IsLetterOrDigit(chars[i]) || chars[i] == '-' || chars[i] == '_')) chars[i] = '_';
            return CamPrefix + new string(chars) + CamSuffix;
        }

        private static void WritePose(BinaryWriter w, in Pose p)
        {
            w.Write(p.px); w.Write(p.py); w.Write(p.pz);
            w.Write(p.qx); w.Write(p.qy); w.Write(p.qz); w.Write(p.qw);
        }

        private static Pose ReadPose(BinaryReader r) => new Pose
        {
            px = r.ReadSingle(), py = r.ReadSingle(), pz = r.ReadSingle(),
            qx = r.ReadSingle(), qy = r.ReadSingle(), qz = r.ReadSingle(), qw = r.ReadSingle(),
        };
    }

    /// <summary>meta.json の中身（JsonUtility）。</summary>
    [Serializable]
    public sealed class XpRecMeta
    {
        public int version = XpRecFormat.Version;
        public string startedAt = "";        // 端末の現地時刻（ISO）
        public string device = "";
        public string build = "";            // dev / release
        public string showSha = "";          // 同梱 show.json の sha256（再生側の同梱と照合する）
        public string startReason = "";      // 何を合図に録り始めたか
        public string stopReason = "";
        public float durationSec;
        public int frames;
        public string[] cameras = Array.Empty<string>();   // CameraSource.DisplayName の並び
        public int[] cameraFrames = Array.Empty<int>();
        public long bytes;
        public bool capped;                  // 容量の上限に当たって映像を落としたか
        public string registrationJson = ""; // 記録の頭の registration.json の本文（無ければ空）
    }
}
