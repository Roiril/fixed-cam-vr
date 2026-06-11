#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using TableDuoVr.Hands;
using TableDuoVr.Hands.Playback;
using UnityEngine;
using UnityEngine.Networking;

namespace TableDuoVr.Net
{
    /// <summary>
    /// セッションリプレイの再生（stimulated recall 用・Editor Play Mode のフラット画面想定）。
    /// 使い方: TableDuoMain を開き、[TableDuo]/ReplayViewer を有効化して Play
    /// （ConnectionManager.autoMode=None のまま = NGO 非起動）。
    /// 再生/シーク/倍速/イベントジャンプ/視点プリセット（俯瞰・人の頭・手の視点）/音声並走。
    /// </summary>
    public sealed class ReplayViewer : MonoBehaviour
    {
        [Tooltip("空なら persistentDataPath の最新 tdv_replay_*.bin")]
        [SerializeField] private string filePath = "";
        [Tooltip("録音 wav（任意）。再生中に並走させる")]
        [SerializeField] private string audioWavPath = "";
        [Tooltip("記録先頭時点に対応する音声内の秒数（クラップ mark で校正）")]
        [SerializeField] private float audioOffsetSec;
        [SerializeField] private bool autoPlayOnLoad = true;

        private sealed class ClientStream
        {
            public StudyConfig.Role Role;
            public int Seat;
            public readonly List<long> Times = new();
            public readonly List<AvatarPose> Poses = new();
            public RemoteAvatarView? View;
            public int Cursor;
            public AvatarPose? Current;
        }

        private sealed class PropStream
        {
            public Transform? Target;
            public readonly List<long> Times = new();
            public readonly List<Vector3> Positions = new();
            public readonly List<Quaternion> Rotations = new();
            public int Cursor;
        }

        private readonly Dictionary<ulong, ClientStream> _clients = new();
        private readonly Dictionary<byte, PropStream> _props = new();
        private readonly List<(long t, string label, string detail)> _events = new();

        private bool _loaded;
        private long _t0;
        private long _tEnd;
        private double _time;       // 現在位置（epoch ms）
        private bool _playing;
        private float _speed = 1f;

        private Camera? _camera;
        private int _viewPreset;    // 0=自由 1=俯瞰 2=人の頭 3=手の視点
        private Vector2 _eventScroll;
        private AudioSource? _audio;
        private string _status = "";

        private void Start()
        {
            CreateCamera();
            Load();
        }

        // ---------- ロード ----------

        private void Load()
        {
            string path = ResolvePath();
            if (!File.Exists(path))
            {
                _status = $"ファイルなし: {path}";
                Debug.LogWarning($"[TableDuo] Replay {_status}");
                return;
            }
            using var r = new BinaryReader(File.OpenRead(path));
            if (r.ReadInt32() != ReplayFormat.Magic)
            {
                _status = "magic 不一致";
                return;
            }
            r.ReadByte();              // version
            _t0 = r.ReadInt64();       // startEpochMs
            r.ReadString();            // studyConfig

            var propNames = new Dictionary<byte, string>();
            var tempPose = new AvatarPose();
            long t = _t0;
            while (r.BaseStream.Position < r.BaseStream.Length)
            {
                byte type;
                try
                {
                    type = r.ReadByte();
                    t = r.ReadInt64();
                    switch (type)
                    {
                        case ReplayFormat.RecPose:
                        {
                            ulong clientId = r.ReadUInt64();
                            PoseRecordingFile.ReadPose(r, tempPose);
                            var stream = GetClient(clientId);
                            stream.Times.Add(t);
                            var copy = new AvatarPose();
                            copy.CopyFrom(tempPose);
                            stream.Poses.Add(copy);
                            break;
                        }
                        case ReplayFormat.RecProp:
                        {
                            byte id = r.ReadByte();
                            var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                            var rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                            if (!_props.TryGetValue(id, out var ps))
                            {
                                ps = new PropStream();
                                _props[id] = ps;
                            }
                            ps.Times.Add(t);
                            ps.Positions.Add(pos);
                            ps.Rotations.Add(rot);
                            break;
                        }
                        case ReplayFormat.RecEvent:
                            _events.Add((t, r.ReadString(), r.ReadString()));
                            break;
                        case ReplayFormat.RecRole:
                        {
                            ulong clientId = r.ReadUInt64();
                            var stream = GetClient(clientId);
                            stream.Role = (StudyConfig.Role)r.ReadByte();
                            stream.Seat = r.ReadByte();
                            break;
                        }
                        case ReplayFormat.RecPropRegistry:
                        {
                            byte id = r.ReadByte();
                            propNames[id] = r.ReadString();
                            break;
                        }
                        case ReplayFormat.RecLayouts:
                            HandSkeletonLayout.CapturedL ??= PoseRecordingFile.ReadLayout(r);
                            HandSkeletonLayout.CapturedR ??= PoseRecordingFile.ReadLayout(r);
                            break;
                        default:
                            _status = $"未知レコード type={type}";
                            Debug.LogWarning($"[TableDuo] Replay {_status} — 以降を打ち切り");
                            goto done;
                    }
                }
                catch (EndOfStreamException)
                {
                    break; // 記録中断（クラッシュ等）の尻切れは許容
                }
            }
            done:
            _tEnd = t;
            _time = _t0;

            foreach (var (id, ps) in _props)
            {
                if (propNames.TryGetValue(id, out string? propName))
                {
                    var go = GameObject.Find(propName);
                    ps.Target = go != null ? go.transform : null;
                }
            }
            foreach (var (clientId, stream) in _clients)
            {
                var seat = SeatLocator.Find(stream.Seat);
                if (seat == null) continue;
                stream.View = RemoteAvatarView.Create(seat, handsOnly: stream.Role == StudyConfig.Role.Hand);
                Debug.Log($"[TableDuo] Replay client{clientId}: role={stream.Role} poses={stream.Poses.Count}");
            }

            // 先頭は最初の pose 時刻から（ヘッダ epoch は pose より早く、何も映らない区間ができる）
            long firstPose = long.MaxValue;
            foreach (var s in _clients.Values)
            {
                if (s.Times.Count > 0) firstPose = Math.Min(firstPose, s.Times[0]);
            }
            _time = firstPose != long.MaxValue ? firstPose : _t0;

            LoadAudio();
            _loaded = true;
            _status = $"{(_tEnd - _t0) / 1000f:F0}s / events={_events.Count}";
            Debug.Log($"[TableDuo] Replay ロード完了 {path} {_status}");
            if (autoPlayOnLoad) SetPlaying(true);
        }

        private ClientStream GetClient(ulong clientId)
        {
            if (!_clients.TryGetValue(clientId, out var s))
            {
                s = new ClientStream { Role = StudyConfig.Role.Full, Seat = (int)(clientId == 0 ? 0 : 1) };
                _clients[clientId] = s;
            }
            return s;
        }

        private string ResolvePath()
        {
            if (!string.IsNullOrEmpty(filePath)) return filePath;
            var files = Directory.GetFiles(Application.persistentDataPath, "tdv_replay_*.bin");
            if (files.Length == 0) return Path.Combine(Application.persistentDataPath, "tdv_replay_none.bin");
            Array.Sort(files);
            return files[^1];
        }

        private void LoadAudio()
        {
            if (string.IsNullOrEmpty(audioWavPath) || !File.Exists(audioWavPath)) return;
            var req = UnityWebRequestMultimedia.GetAudioClip("file://" + audioWavPath, AudioType.WAV);
            req.SendWebRequest().completed += _ =>
            {
                if (req.result == UnityWebRequest.Result.Success)
                {
                    _audio = gameObject.AddComponent<AudioSource>();
                    _audio.clip = DownloadHandlerAudioClip.GetContent(req);
                    _audio.playOnAwake = false;
                    Debug.Log("[TableDuo] Replay 音声ロード完了");
                }
                else
                {
                    Debug.LogWarning($"[TableDuo] Replay 音声ロード失敗: {req.error}");
                }
                req.Dispose();
            };
        }

        // ---------- 再生 ----------

        private void Update()
        {
            if (!_loaded) return;
            if (_playing)
            {
                _time += Time.deltaTime * _speed * 1000.0;
                if (_time >= _tEnd)
                {
                    _time = _tEnd;
                    SetPlaying(false);
                }
            }
            ApplyAt((long)_time);
            UpdateCameraPreset();
        }

        private void ApplyAt(long t)
        {
            foreach (var stream in _clients.Values)
            {
                int idx = Advance(stream.Times, ref stream.Cursor, t);
                if (idx < 0 || stream.View == null) continue;
                stream.Current = stream.Poses[idx];
                stream.View.Apply(stream.Current);
            }
            foreach (var ps in _props.Values)
            {
                int idx = Advance(ps.Times, ref ps.Cursor, t);
                if (idx < 0 || ps.Target == null) continue;
                ps.Target.SetPositionAndRotation(ps.Positions[idx], ps.Rotations[idx]);
            }
            if (_audio != null && _audio.clip != null && _playing)
            {
                float want = (float)((t - _t0) / 1000.0) + audioOffsetSec;
                if (want >= 0f && want < _audio.clip.length && Mathf.Abs(_audio.time - want) > 0.25f)
                {
                    _audio.time = want;
                }
            }
        }

        /// <summary>時刻 t 以下の直近 index。順方向はカーソル前進、逆方向シークは二分探索。</summary>
        private static int Advance(List<long> times, ref int cursor, long t)
        {
            if (times.Count == 0 || t < times[0]) return -1;
            if (cursor >= times.Count) cursor = times.Count - 1;
            if (times[cursor] > t)
            {
                int lo = 0, hi = cursor;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    if (times[mid] <= t) lo = mid;
                    else hi = mid - 1;
                }
                cursor = lo;
            }
            else
            {
                while (cursor + 1 < times.Count && times[cursor + 1] <= t) cursor++;
            }
            return cursor;
        }

        private void SetPlaying(bool playing)
        {
            _playing = playing;
            if (_audio != null)
            {
                if (playing) _audio.Play();
                else _audio.Pause();
            }
        }

        private void Seek(long t)
        {
            _time = Math.Clamp(t, _t0, _tEnd);
            ApplyAt((long)_time);
        }

        // ---------- カメラ ----------

        private void CreateCamera()
        {
            var go = new GameObject("ReplayCamera");
            go.transform.SetParent(transform, false);
            _camera = go.AddComponent<Camera>();
            _camera.depth = 100f;
            _camera.nearClipPlane = 0.02f;
            go.transform.SetPositionAndRotation(new Vector3(1.6f, 1.6f, 0f), Quaternion.Euler(25f, -90f, 0f));
            _viewPreset = 1;
        }

        private void UpdateCameraPreset()
        {
            if (_camera == null) return;
            var cam = _camera.transform;
            switch (_viewPreset)
            {
                case 0: // 自由（WASD + 右ドラッグ）
                    FreeFly(cam);
                    break;
                case 1: // 俯瞰
                    cam.SetPositionAndRotation(new Vector3(1.6f, 1.7f, 0f), Quaternion.Euler(30f, -90f, 0f));
                    break;
                case 2: // 人（Full）の頭
                    ApplyHeadView(cam, StudyConfig.Role.Full);
                    break;
                case 3: // 手の視点（Hand 役の手首から少し引く）
                    ApplyHandView(cam);
                    break;
            }
        }

        private void ApplyHeadView(Transform cam, StudyConfig.Role role)
        {
            foreach (var s in _clients.Values)
            {
                if (s.Role != role || s.Current == null) continue;
                var seat = SeatLocator.Find(s.Seat);
                if (seat == null) return;
                cam.SetPositionAndRotation(
                    seat.TransformPoint(s.Current.HeadPos),
                    seat.rotation * s.Current.HeadRot);
                return;
            }
        }

        private void ApplyHandView(Transform cam)
        {
            foreach (var s in _clients.Values)
            {
                if (s.Role != StudyConfig.Role.Hand || s.Current == null) continue;
                var seat = SeatLocator.Find(s.Seat);
                if (seat == null) return;
                var pos = seat.TransformPoint(s.Current.WristPosR);
                var rot = seat.rotation * s.Current.WristRotR;
                // 手首から少し後上方に引いて手自体も映す
                cam.SetPositionAndRotation(pos - rot * Vector3.forward * 0.15f + Vector3.up * 0.05f, rot);
                return;
            }
        }

        private void FreeFly(Transform cam)
        {
            float move = (Input.GetKey(KeyCode.LeftShift) ? 3f : 1f) * Time.deltaTime;
            var dir = new Vector3(Input.GetAxis("Horizontal"), 0f, Input.GetAxis("Vertical"));
            cam.position += cam.rotation * dir * move;
            if (Input.GetKey(KeyCode.E)) cam.position += Vector3.up * move;
            if (Input.GetKey(KeyCode.Q)) cam.position -= Vector3.up * move;
            if (Input.GetMouseButton(1))
            {
                var e = cam.eulerAngles;
                e.y += Input.GetAxis("Mouse X") * 2.5f;
                e.x -= Input.GetAxis("Mouse Y") * 2.5f;
                cam.eulerAngles = e;
            }
        }

        // ---------- UI ----------

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10, 10, 380, Screen.height - 20), GUI.skin.box);
            GUILayout.Label($"Replay: {_status}");

            if (_loaded)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(_playing ? "⏸ Pause" : "▶ Play", GUILayout.Width(90)))
                {
                    SetPlaying(!_playing);
                }
                foreach (float s in new[] { 0.25f, 0.5f, 1f, 2f })
                {
                    if (GUILayout.Button(_speed == s ? $"[{s}x]" : $"{s}x"))
                    {
                        _speed = s;
                    }
                }
                GUILayout.EndHorizontal();

                float dur = (_tEnd - _t0) / 1000f;
                float cur = (float)((_time - _t0) / 1000.0);
                GUILayout.Label($"{cur:F1}s / {dur:F1}s");
                float seek = GUILayout.HorizontalSlider(cur, 0f, dur);
                if (Mathf.Abs(seek - cur) > 0.05f)
                {
                    Seek(_t0 + (long)(seek * 1000f));
                }

                GUILayout.Space(6);
                GUILayout.Label("視点:");
                GUILayout.BeginHorizontal();
                string[] presets = { "自由", "俯瞰", "人の頭", "手の視点" };
                for (int i = 0; i < presets.Length; i++)
                {
                    if (GUILayout.Button(_viewPreset == i ? $"[{presets[i]}]" : presets[i]))
                    {
                        _viewPreset = i;
                    }
                }
                GUILayout.EndHorizontal();

                GUILayout.Space(6);
                GUILayout.Label($"音声 offset: {audioOffsetSec:F2}s（クラップ mark で校正）");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("-0.1")) audioOffsetSec -= 0.1f;
                if (GUILayout.Button("+0.1")) audioOffsetSec += 0.1f;
                if (GUILayout.Button("-1")) audioOffsetSec -= 1f;
                if (GUILayout.Button("+1")) audioOffsetSec += 1f;
                GUILayout.EndHorizontal();

                GUILayout.Space(6);
                GUILayout.Label($"イベント（クリックでジャンプ）: {_events.Count}");
                _eventScroll = GUILayout.BeginScrollView(_eventScroll);
                foreach (var (t, label, detail) in _events)
                {
                    float sec = (t - _t0) / 1000f;
                    if (GUILayout.Button($"{sec,7:F1}s  {label}  {detail}", GUI.skin.label))
                    {
                        Seek(t);
                    }
                }
                GUILayout.EndScrollView();
            }
            GUILayout.EndArea();
        }
    }
}
