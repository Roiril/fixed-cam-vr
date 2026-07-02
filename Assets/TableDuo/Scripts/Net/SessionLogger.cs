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
        private float _nextCardSnapshot;
        private readonly Vector3[] _landmarks = new Vector3[HandLandmarks.Count];
        // clientId ごとの直近 tracked 状態（ロスト遷移イベント用）。2人なので小さな配列で十分
        private readonly System.Collections.Generic.Dictionary<ulong, bool> _lastTracked = new();
        // 条件（role/marker/oneHand）を記録済みの clientId。条件は端末ごとの起動フラグなので clientId 別に1回記録
        private readonly System.Collections.Generic.HashSet<ulong> _conditionLogged = new();
        // ConnectionManager はシーンロード順で Instance 確定が遅れうるため、Update で遅延購読する
        private ConnectionManager? _subscribedCm;
        private int _lastDroppedPoses;
        private int _lastStalePoses;
        private float _nextFlush;

        public string? FilePath { get; private set; }

        /// <summary>イベントの横流し（SessionReplayRecorder が購読してリプレイにも刻む）。</summary>
        public static event Action<string, string>? EventLogged;

        /// <summary>イベント行を書く（MarkServer / Grabbable から）。</summary>
        public void LogEvent(string label, string detail = "")
        {
            if (_writer == null) return;
            _writer.WriteLine($"event,{EpochMs()},{Escape(label)},{Escape(detail)}");
            _writer.Flush();
            Debug.Log($"[TableDuo] mark: {label} {detail}");
            EventLogged?.Invoke(label, detail);
        }

        private void OnEnable()
        {
            Grabbable.GrabLogged += OnGrabLogged;
            TableDuoPlayer.RecenterReported += OnRecenterReported;
            TableDuoPlayer.ClockOffsetReported += OnClockOffsetReported;
            StudyConfig.HandVariantChanged += OnHandVariantChanged;
        }

        // client 壁時計のオフセット（serverUtc - clientUtc、RTT/2 補正済み）。
        // 解析時は pose 行の captureMs にこの値を足すと host 時計に整列する
        private void OnClockOffsetReported(ulong clientId, long offsetMs) =>
            LogEvent("clockOffset", $"client{clientId}:offsetMs={offsetMs}");

        // 手バリアントは調査条件（tdv_hand 固定・セッション中トグル禁止）。万一切り替わったら
        // 条件汚染として解析で除外できるよう必ず刻む（この端末=host 表示の見た目）
        private void OnHandVariantChanged() =>
            LogEvent("handVariantChanged", $"host:{StudyConfig.SelectedHandVariant}");

        private void OnDisable()
        {
            Grabbable.GrabLogged -= OnGrabLogged;
            TableDuoPlayer.RecenterReported -= OnRecenterReported;
            TableDuoPlayer.ClockOffsetReported -= OnClockOffsetReported;
            StudyConfig.HandVariantChanged -= OnHandVariantChanged;
            if (_subscribedCm != null)
            {
                _subscribedCm.HandLayoutReceived -= OnHandLayoutReceived;
                _subscribedCm = null;
            }
            CloseFile();
        }

        // layout 受信 = ここから先の pose 行が「本人の手寸法」で FK される境界（study-design §4 解析ノート）
        private void OnHandLayoutReceived(ulong clientId) => LogEvent("layoutReceived", $"client{clientId}");

        private void OnGrabLogged(string objectName, ulong clientId, bool isGrab) =>
            LogEvent(isGrab ? "grab" : "release", $"{objectName}:client{clientId}");

        // OS recenter は座標系を不連続化する。post-hoc 空間解析が境界を分割できるよう刻む（study-validity）
        private void OnRecenterReported(ulong clientId) => LogEvent("recenter", $"client{clientId}");

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
            if (_subscribedCm != cm)
            {
                if (_subscribedCm != null) _subscribedCm.HandLayoutReceived -= OnHandLayoutReceived;
                cm.HandLayoutReceived += OnHandLayoutReceived;
                _subscribedCm = cm;
            }
            // 破棄 pose メッセージの累計変化を刻む（「凍結 vs パケット欠落」の解析材料）
            if (cm.DroppedPoseMessages != _lastDroppedPoses)
            {
                LogEvent("posesDropped", $"total={cm.DroppedPoseMessages}");
                _lastDroppedPoses = cm.DroppedPoseMessages;
            }
            // Seq 逆行（後着 Unreliable）の棄却累計も刻む
            if (cm.RejectedStalePoses != _lastStalePoses)
            {
                LogEvent("posesStale", $"total={cm.RejectedStalePoses}");
                _lastStalePoses = cm.RejectedStalePoses;
            }
            // 定期 flush: クラッシュ・電池切れ・ANR kill で StreamWriter バッファ分
            //（数 KB＝直近数秒〜数十秒）を失わないようにする（欠測はセッション末尾に集中しがち）
            if (Time.time >= _nextFlush)
            {
                _nextFlush = Time.time + 2f;
                _writer?.Flush();
            }

            foreach (var player in _players)
            {
                if (player == null || player.SeatIndex < 0) continue;
                ulong clientId = player.OwnerClientId;
                if (!cm.TryGetPose(clientId, out AvatarPose pose)) continue;
                var seat = player.Seat; // 確定済み席アンカー（毎サンプル GameObject.Find しない）
                if (seat == null) continue;

                LogConditionOnce(player);
                WritePoseRow(epoch, clientId, player.Role, pose, seat);
                LogTrackingTransition(clientId, pose.TrackedR);
            }

            // カードは掴み中＝毎サンプル（操作の軌跡）+ 全カードを 2Hz スナップショット
            // （RQ「どのカードを指すか」は掴まず指す場合も解析対象 → 静止カードの位置も必要）
            bool snapshotAll = Time.time >= _nextCardSnapshot;
            if (snapshotAll) _nextCardSnapshot = Time.time + 0.5f;
            foreach (var card in _cards)
            {
                if (card == null || card.Grabbable == null) continue;
                bool held = card.Grabbable.IsHeld;
                if (!held && !snapshotAll) continue;
                _sb.Clear();
                _sb.Append("card,").Append(epoch).Append(',').Append(card.CardId).Append(',')
                   .Append(held ? card.Grabbable.HolderClientId.ToString() : "-1");
                AppendV3(card.transform.position);
                AppendV3(card.FaceNormalWorld);
                _writer!.WriteLine(_sb.ToString());
            }
        }

        // 条件（role/marker/oneHand）を clientId 別に1回記録。役割が同期確定してから（Role!=null）。
        // host ローカルの StudyConfig では相手デバイスの条件を取り違えるため、同期値を使う（study-validity）
        private void LogConditionOnce(TableDuoPlayer player)
        {
            if (player.Role is not { } role) return;
            ulong clientId = player.OwnerClientId;
            if (!_conditionLogged.Add(clientId)) return;
            LogEvent("condition",
                $"client{clientId}:role={role}:marker={(player.ShowHeadMarker ? 1 : 0)}:oneHand={(player.OneHandMode ? 1 : 0)}:hand={player.DeclaredHandVariant}");
            // 手バリアントは within-pair 条件＝全端末一致が前提。申告値が host と食い違えば
            // 条件汚染として当該ブロックを解析除外できるよう必ず刻む（TableDuoPlayer 側もエラーログを出す）
            if (player.DeclaredHandVariant != StudyConfig.SelectedHandVariant)
            {
                LogEvent("conditionMismatch",
                    $"client{clientId}:hand={player.DeclaredHandVariant}:host={StudyConfig.SelectedHandVariant}");
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
            // seq=送信連番（歯抜けで「凍結 vs パケット欠落」を判別）, captureMs=送信端末の壁時計
            _sb.Append(',').Append(pose.Seq).Append(',').Append(pose.CaptureMs);

            // 手役の右手 7 ランドマーク（FK・トラッキングスペース→ワールド）。
            // ★その client の実際の手 bind 構造で FK する（host 自身の手骨格だと別端末の手寸法を
            //   取り違えて RQ2/RQ3 の主計測に系統誤差。未受信なら host layout へフォールバック）
            var layoutR = ConnectionManager.Instance?.GetHandLayout(clientId, right: true)
                          ?? HandSkeletonLayout.CapturedR;
            HandLandmarks.Compute(layoutR,
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
            // 参加者/ペアID があればファイル名に含め、紙記録との突合・取り違え防止に使う
            string tag = "";
            if (!string.IsNullOrEmpty(StudyConfig.PairId)) tag += $"_pair{SafeTag(StudyConfig.PairId)}";
            if (!string.IsNullOrEmpty(StudyConfig.ParticipantId)) tag += $"_pid{SafeTag(StudyConfig.ParticipantId)}";
            string name = $"tdv_session_{DateTime.Now:yyyyMMdd_HHmmss}{tag}.csv";
            FilePath = Path.Combine(Application.persistentDataPath, name);
            // FileShare.Read: 記録中もファシリテータが tail / コピーできるようにする
            var stream = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(stream);
            _writer.WriteLine("# type,epochMs,... study-design.md §4 / pose: clientId,role,headP3,headQ4,wristRP3,wristRQ4,trackedR,pinchR,seq,captureMs,7landmarks(wrist,palm,thumb,index,middle,ring,pinky)x3 / card: cardId,holder(-1=未保持),pos3,normal3 / event: condition,recenter,grab,release,trackingLost/Regained,<mark>");
            // hand=手バリアント条件（この host 端末の表示。フル役=host 前提で「人役が見る手の見た目」＝操作因子）
            _writer.WriteLine($"# studyConfig: role={StudyConfig.ForcedRole} marker={StudyConfig.ShowHeadMarker} oneHand={StudyConfig.OneHandMode} hand={StudyConfig.SelectedHandVariant} participantId={StudyConfig.ParticipantId} pairId={StudyConfig.PairId}");
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

        // カンマは列区切り、改行は行区切りを壊すので無害化（外部 label に curl ?label= 等で混入しうる）。
        // 二重引用符・タブも RFC4180 パーサで列崩れを起こすので無害化する。
        private static string Escape(string s) =>
            s.Replace(',', ';').Replace('"', '\'').Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

        // ファイル名に使える文字だけに落とす（英数 . _ - 以外は _）。pid/pair の取り違え防止用。
        private static string SafeTag(string s)
        {
            var chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-')) chars[i] = '_';
            }
            return new string(chars);
        }
    }
}
