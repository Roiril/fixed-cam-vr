#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Text;
using TableDuoVr.Hands;
using Unity.Netcode;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 調査セッションの一元ロガー（ホスト側のみ稼働）。study-design.md §4。
    /// ホストは全員の pose を持つため、ここ 1 箇所で両者分を CSV に書く。
    ///
    /// 行形式（type 列で区別）:
    ///   pose,  epochMs, clientId, role, head(p3,q4), wristR(p3,q4), trackedR, pinchR, landmarks7×3
    ///   card,  epochMs, cardId, holderClientId, pos3, normal3
    ///   event, epochMs, label, detail
    /// 座標は全てワールド。ファイルは起動ごとに新規（役割交代の再起動で上書きしない）。
    /// </summary>
    public sealed class SessionLogger : MonoBehaviour
    {
        [SerializeField] private float sampleRate = 30f;

        private StreamWriter? _writer;
        private readonly StringBuilder _sb = new(1024);
        private float _next;
        private TableDuoPlayer[] _players = Array.Empty<TableDuoPlayer>();
        private CardProp[] _cards = Array.Empty<CardProp>();
        private float _nextRefresh;
        private readonly Vector3[] _landmarks = new Vector3[HandLandmarks.Count];
        // clientId ごとの直近 tracked 状態（ロスト遷移イベント用）。2人なので小さな配列で十分
        private readonly System.Collections.Generic.Dictionary<ulong, bool> _lastTracked = new();

        public string? FilePath { get; private set; }

        /// <summary>イベント行を書く（MarkServer / Grabbable から）。</summary>
        public void LogEvent(string label, string detail = "")
        {
            if (_writer == null) return;
            _writer.WriteLine($"event,{EpochMs()},{Escape(label)},{Escape(detail)}");
            _writer.Flush();
            Debug.Log($"[TableDuo] mark: {label} {detail}");
        }

        private void OnEnable() => Grabbable.GrabLogged += OnGrabLogged;

        private void OnDisable()
        {
            Grabbable.GrabLogged -= OnGrabLogged;
            CloseFile();
        }

        private void OnGrabLogged(string objectName, ulong clientId, bool isGrab) =>
            LogEvent(isGrab ? "grab" : "release", $"{objectName}:client{clientId}");

        private void Update()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening || !nm.IsServer)
            {
                CloseFile();
                return;
            }
            if (_writer == null) OpenFile();
            if (Time.time < _next) return;
            _next = Time.time + 1f / sampleRate;

            if (Time.time >= _nextRefresh)
            {
                _nextRefresh = Time.time + 2f;
                _players = FindObjectsOfType<TableDuoPlayer>();
                _cards = FindObjectsOfType<CardProp>();
            }

            long epoch = EpochMs();
            var cm = ConnectionManager.Instance;
            if (cm == null) return;

            foreach (var player in _players)
            {
                if (player == null || player.SeatIndex < 0) continue;
                ulong clientId = player.OwnerClientId;
                if (!cm.TryGetPose(clientId, out AvatarPose pose)) continue;
                var seat = SeatLocator.Find(player.SeatIndex);
                if (seat == null) continue;

                WritePoseRow(epoch, clientId, player.Role, pose, seat);
                LogTrackingTransition(clientId, pose.TrackedR);
            }

            foreach (var card in _cards)
            {
                if (card == null || card.Grabbable == null || !card.Grabbable.IsHeld) continue;
                var p = card.transform.position;
                var n = card.FaceNormalWorld;
                _sb.Clear();
                _sb.Append("card,").Append(epoch).Append(',').Append(card.CardId).Append(',')
                   .Append(card.Grabbable.HolderClientId);
                AppendV3(p);
                AppendV3(n);
                _writer!.WriteLine(_sb.ToString());
            }
        }

        private void WritePoseRow(long epoch, ulong clientId, StudyConfig.Role? role,
            AvatarPose pose, Transform seat)
        {
            _sb.Clear();
            _sb.Append("pose,").Append(epoch).Append(',').Append(clientId).Append(',')
               .Append(role?.ToString() ?? "?");

            AppendV3(seat.TransformPoint(pose.HeadPos));
            AppendQ(seat.rotation * pose.HeadRot);
            AppendV3(seat.TransformPoint(pose.WristPosR));
            AppendQ(seat.rotation * pose.WristRotR);
            _sb.Append(',').Append(pose.TrackedR ? 1 : 0).Append(',').Append(pose.PinchR ? 1 : 0);

            // 手役の右手 7 ランドマーク（FK・トラッキングスペース→ワールド）
            HandLandmarks.Compute(HandSkeletonLayout.CapturedR,
                pose.WristPosR, pose.WristRotR, pose.BonesR, _landmarks);
            for (int i = 0; i < HandLandmarks.Count; i++)
            {
                AppendV3(seat.TransformPoint(_landmarks[i]));
            }
            _writer!.WriteLine(_sb.ToString());
        }

        private void LogTrackingTransition(ulong clientId, bool tracked)
        {
            if (_lastTracked.TryGetValue(clientId, out bool last) && last != tracked)
            {
                LogEvent(tracked ? "trackingRegained" : "trackingLost", $"client{clientId}");
            }
            _lastTracked[clientId] = tracked;
        }

        private void OpenFile()
        {
            string name = $"tdv_session_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            FilePath = Path.Combine(Application.persistentDataPath, name);
            // FileShare.Read: 記録中もファシリテータが tail / コピーできるようにする
            var stream = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(stream);
            _writer.WriteLine("# type,epochMs,... study-design.md §4 / pose: clientId,role,headP3,headQ4,wristRP3,wristRQ4,trackedR,pinchR,7landmarks(wrist,palm,thumb,index,middle,ring,pinky)x3");
            _writer.WriteLine($"# studyConfig: role={StudyConfig.ForcedRole} marker={StudyConfig.ShowHeadMarker} oneHand={StudyConfig.OneHandMode}");
            _writer.Flush();
            Debug.Log($"[TableDuo] SessionLogger 開始 → {FilePath}");
        }

        private void CloseFile()
        {
            if (_writer == null) return;
            _writer.Flush();
            _writer.Dispose();
            _writer = null;
            Debug.Log($"[TableDuo] SessionLogger 保存 → {FilePath}");
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused) _writer?.Flush();
        }

        private void AppendV3(Vector3 v)
        {
            _sb.Append(',').Append(v.x.ToString("F4", CultureInfo.InvariantCulture))
               .Append(',').Append(v.y.ToString("F4", CultureInfo.InvariantCulture))
               .Append(',').Append(v.z.ToString("F4", CultureInfo.InvariantCulture));
        }

        private void AppendQ(Quaternion q)
        {
            _sb.Append(',').Append(q.x.ToString("F4", CultureInfo.InvariantCulture))
               .Append(',').Append(q.y.ToString("F4", CultureInfo.InvariantCulture))
               .Append(',').Append(q.z.ToString("F4", CultureInfo.InvariantCulture))
               .Append(',').Append(q.w.ToString("F4", CultureInfo.InvariantCulture));
        }

        private static long EpochMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private static string Escape(string s) => s.Replace(',', ';');
    }
}
