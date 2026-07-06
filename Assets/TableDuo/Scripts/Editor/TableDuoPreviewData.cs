#nullable enable
using System.IO;
using TableDuoVr.Hands.Playback;
using UnityEngine;

namespace TableDuoVr.EditorTools
{
    /// <summary>
    /// プレビュー系ツール共有の実録画ロード（TestData/tdv_handrec_real_*.bin）。
    /// TableDuoHandVariantPreview / TableDuoAvatarPreview が同じ実データで手・指を検証するための一元化。
    /// </summary>
    internal static class Playback
    {
        /// <summary>TestData の実手録画をロード。無ければ null。</summary>
        public static PoseRecordingFile.Data? LoadRecordingForPreview(string projectRoot)
        {
            foreach (var rel in new[] { "TestData/tdv_handrec_real_20260610.bin" })
            {
                var d = PoseRecordingFile.Load(Path.Combine(projectRoot, rel));
                if (d != null && d.Frames.Count > 0) return d;
            }
            var td = Path.Combine(projectRoot, "TestData");
            if (Directory.Exists(td))
            {
                foreach (var file in Directory.GetFiles(td, "tdv_handrec*.bin"))
                {
                    var d = PoseRecordingFile.Load(file);
                    if (d != null && d.Frames.Count > 0) return d;
                }
            }
            return null;
        }

        /// <summary>指がよく曲がっている（bind から角度が大きい）フレーム index＝表情のある一枚。</summary>
        public static int PickExpressiveFrame(PoseRecordingFile.Data data)
        {
            var bind = data.LayoutR;
            int best = data.Frames.Count / 2;
            if (bind == null) return best;
            float bestScore = -1f;
            for (int f = 0; f < data.Frames.Count; f++)
            {
                var pose = data.Frames[f];
                if (!pose.TrackedR) continue;
                float s = 0f;
                for (int i = 2; i <= 18 && i < bind.BoneCount; i++)
                    s += Quaternion.Angle(pose.BonesR[i], bind.BindLocalRot[i]);
                if (s > bestScore) { bestScore = s; best = f; }
            }
            return best;
        }
    }
}
