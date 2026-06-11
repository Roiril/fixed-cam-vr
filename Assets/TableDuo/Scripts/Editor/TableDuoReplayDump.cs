#nullable enable
using System.Collections.Generic;
using System.IO;
using TableDuoVr.Hands;
using TableDuoVr.Hands.Playback;
using TableDuoVr.Net;
using UnityEditor;
using UnityEngine;

namespace TableDuoVr.EditorTools
{
    /// <summary>リプレイファイル（TDVR1）のヘッダとレコード集計を Console に出す検証用メニュー。</summary>
    public static class TableDuoReplayDump
    {
        [MenuItem("Tools/FixedCamVr/Diagnostics/Dump TableDuo Replay", priority = 210)]
        public static void Dump()
        {
            string path = EditorUtility.OpenFilePanel(
                "リプレイファイル選択", Application.persistentDataPath, "bin");
            if (string.IsNullOrEmpty(path)) return;

            using var r = new BinaryReader(File.OpenRead(path));
            if (r.ReadInt32() != ReplayFormat.Magic)
            {
                Debug.LogError("[TableDuoReplayDump] magic 不一致（TDVR1 ではない）");
                return;
            }
            byte version = r.ReadByte();
            long t0 = r.ReadInt64();
            string config = r.ReadString();

            var poseCount = new Dictionary<ulong, int>();
            var propNames = new Dictionary<byte, string>();
            int propRecords = 0, eventCount = 0;
            bool hasLayouts = false;
            long tLast = t0;
            var tempPose = new AvatarPose();
            var roles = new List<string>();

            try
            {
                while (r.BaseStream.Position < r.BaseStream.Length)
                {
                    byte type = r.ReadByte();
                    tLast = r.ReadInt64();
                    switch (type)
                    {
                        case ReplayFormat.RecPose:
                        {
                            ulong id = r.ReadUInt64();
                            PoseRecordingFile.ReadPose(r, tempPose);
                            poseCount.TryGetValue(id, out int c);
                            poseCount[id] = c + 1;
                            break;
                        }
                        case ReplayFormat.RecProp:
                            r.ReadByte();
                            for (int i = 0; i < 7; i++) r.ReadSingle();
                            propRecords++;
                            break;
                        case ReplayFormat.RecEvent:
                            r.ReadString();
                            r.ReadString();
                            eventCount++;
                            break;
                        case ReplayFormat.RecRole:
                            roles.Add($"client{r.ReadUInt64()}=role{r.ReadByte()}/seat{r.ReadByte()}");
                            break;
                        case ReplayFormat.RecPropRegistry:
                            propNames[r.ReadByte()] = r.ReadString();
                            break;
                        case ReplayFormat.RecLayouts:
                            PoseRecordingFile.ReadLayout(r);
                            PoseRecordingFile.ReadLayout(r);
                            hasLayouts = true;
                            break;
                        default:
                            Debug.LogWarning($"[TableDuoReplayDump] 未知 type={type} @ {r.BaseStream.Position}");
                            goto done;
                    }
                }
            }
            catch (EndOfStreamException)
            {
                Debug.LogWarning("[TableDuoReplayDump] 尻切れ（記録中断ファイル）— ここまでの集計を表示");
            }
            done:

            var poseSummary = new System.Text.StringBuilder();
            foreach (var (id, c) in poseCount) poseSummary.Append($" client{id}:{c}f");
            Debug.Log($"[TableDuoReplayDump] v{version} 長さ={(tLast - t0) / 1000f:F1}s config=({config})\n" +
                      $"poses:{poseSummary} / roles: {string.Join(", ", roles)}\n" +
                      $"props: {propNames.Count}種 {propRecords}rec / events: {eventCount} / layouts: {hasLayouts}");
        }
    }
}
