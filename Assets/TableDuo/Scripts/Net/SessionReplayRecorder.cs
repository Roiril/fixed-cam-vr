#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using TableDuoVr.Hands;
using TableDuoVr.Hands.Playback;
using Unity.Netcode;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// セッション全記録（ホストのみ稼働）。stimulated recall 用リプレイの書き手。
    /// 両者の AvatarPose 全量 30Hz + prop transform（変化時のみ）+ イベント + 役割を
    /// 1 ファイル（TDVR1）に記録する。計画 → .claude/plans/2026-06-11_table-duo_replay.md
    /// </summary>
    public sealed class SessionReplayRecorder : MonoBehaviour
    {
        [SerializeField] private float poseRate = 30f;
        [SerializeField] private float propRate = 10f;

        private const float PropPosThreshold = 0.001f;  // 1mm
        private const float PropRotThresholdDeg = 1f;

        private BinaryWriter? _writer;
        private float _nextPose;
        private float _nextProp;
        private float _nextRefresh;
        private bool _layoutsWritten;

        private TableDuoPlayer[] _players = Array.Empty<TableDuoPlayer>();
        private Grabbable[] _props = Array.Empty<Grabbable>();
        private readonly Dictionary<Grabbable, byte> _propIds = new();
        private readonly Dictionary<byte, (Vector3 pos, Quaternion rot)> _lastProp = new();
        private readonly HashSet<ulong> _roleWritten = new();

        public string? FilePath { get; private set; }

        private void OnEnable() => SessionLogger.EventLogged += OnEventLogged;

        private void OnDisable()
        {
            SessionLogger.EventLogged -= OnEventLogged;
            CloseFile();
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused) _writer?.Flush();
        }

        private void Update()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening || !nm.IsServer)
            {
                CloseFile();
                return;
            }
            if (_writer == null) OpenFile();
            if (_writer == null) return;

            if (Time.time >= _nextRefresh)
            {
                _nextRefresh = Time.time + 2f;
                _players = FindObjectsOfType<TableDuoPlayer>();
                _props = FindObjectsOfType<Grabbable>();
                RegisterProps();
            }

            WriteLayoutsOnce();
            long epoch = EpochMs();

            if (Time.time >= _nextPose)
            {
                _nextPose = Time.time + 1f / poseRate;
                WritePoses(epoch);
            }
            if (Time.time >= _nextProp)
            {
                _nextProp = Time.time + 1f / propRate;
                WriteChangedProps(epoch);
            }
        }

        private void WritePoses(long epoch)
        {
            var cm = ConnectionManager.Instance;
            if (cm == null) return;
            foreach (var player in _players)
            {
                if (player == null) continue;
                ulong clientId = player.OwnerClientId;
                if (!cm.TryGetPose(clientId, out AvatarPose pose)) continue;

                if (player.Role is { } role && _roleWritten.Add(clientId))
                {
                    _writer!.Write(ReplayFormat.RecRole);
                    _writer.Write(epoch);
                    _writer.Write(clientId);
                    _writer.Write((byte)role);
                    _writer.Write((byte)player.SeatIndex);
                }

                _writer!.Write(ReplayFormat.RecPose);
                _writer.Write(epoch);
                _writer.Write(clientId);
                PoseRecordingFile.WritePose(_writer, pose);
            }
        }

        private void RegisterProps()
        {
            foreach (var prop in _props)
            {
                if (prop == null || _propIds.ContainsKey(prop)) continue;
                byte id = (byte)_propIds.Count;
                _propIds[prop] = id;
                _writer!.Write(ReplayFormat.RecPropRegistry);
                _writer.Write(EpochMs());
                _writer.Write(id);
                _writer.Write(prop.name);
            }
        }

        private void WriteChangedProps(long epoch)
        {
            foreach (var prop in _props)
            {
                if (prop == null || !_propIds.TryGetValue(prop, out byte id)) continue;
                var pos = prop.transform.position;
                var rot = prop.transform.rotation;
                if (_lastProp.TryGetValue(id, out var last)
                    && (last.pos - pos).sqrMagnitude < PropPosThreshold * PropPosThreshold
                    && Quaternion.Angle(last.rot, rot) < PropRotThresholdDeg)
                {
                    continue;
                }
                _lastProp[id] = (pos, rot);
                _writer!.Write(ReplayFormat.RecProp);
                _writer.Write(epoch);
                _writer.Write(id);
                _writer.Write(pos.x); _writer.Write(pos.y); _writer.Write(pos.z);
                _writer.Write(rot.x); _writer.Write(rot.y); _writer.Write(rot.z); _writer.Write(rot.w);
            }
        }

        private void WriteLayoutsOnce()
        {
            if (_layoutsWritten) return;
            var l = HandSkeletonLayout.CapturedL;
            var r = HandSkeletonLayout.CapturedR;
            if (l == null || r == null) return;
            _layoutsWritten = true;
            _writer!.Write(ReplayFormat.RecLayouts);
            _writer.Write(EpochMs());
            PoseRecordingFile.WriteLayout(_writer, l);
            PoseRecordingFile.WriteLayout(_writer, r);
        }

        private void OnEventLogged(string label, string detail)
        {
            if (_writer == null) return;
            _writer.Write(ReplayFormat.RecEvent);
            _writer.Write(EpochMs());
            _writer.Write(label);
            _writer.Write(detail);
            _writer.Flush();
        }

        private void OpenFile()
        {
            string name = $"tdv_replay_{DateTime.Now:yyyyMMdd_HHmmss}.bin";
            FilePath = Path.Combine(Application.persistentDataPath, name);
            var stream = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            _writer = new BinaryWriter(stream);
            _writer.Write(ReplayFormat.Magic);
            _writer.Write(ReplayFormat.Version);
            _writer.Write(EpochMs());
            _writer.Write($"role={StudyConfig.ForcedRole} marker={StudyConfig.ShowHeadMarker} oneHand={StudyConfig.OneHandMode}");
            _layoutsWritten = false;
            _roleWritten.Clear();
            _propIds.Clear();
            _lastProp.Clear();
            Debug.Log($"[TableDuo] ReplayRecorder 開始 → {FilePath}");
        }

        private void CloseFile()
        {
            if (_writer == null) return;
            _writer.Flush();
            _writer.Dispose();
            _writer = null;
            Debug.Log($"[TableDuo] ReplayRecorder 保存 → {FilePath}");
        }

        private static long EpochMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
