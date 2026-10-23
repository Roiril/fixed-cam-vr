#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using FixedCamVr.Streaming.Recording;
using UnityEngine;

namespace FixedCamVr.Streaming.Replay
{
    /// <summary>
    /// <b>体験の記録</b>（紹介動画の素材。2026-10-23 ユーザー依頼）。
    /// Quest の録画は片眼の中心しか写らない。そこで体験 1 回ぶんの<b>入力</b>を残し、
    /// PC の Editor で本番の Main シーンへ流し直して、広い画角で撮り直す（<c>menu replay</c>）。
    ///
    /// 残すもの: 頭と両手の姿勢・コントローラの生の入力・言語と軽減（毎フレーム）、
    /// 3 台のカメラから受けて展開できた JPEG（そのまま）、[XP] テレメトリ、registration.json。
    /// 合成した画は残さない ＝ Quest で動画を圧縮しない（重いのは JPEG のコピーだけ）。
    ///
    /// <b>録り始め</b>: スタッフの準備画面が閉じた瞬間（引き渡し）。<b>録り終わり</b>: 準備画面が再び開いた・
    /// 体験が終わって <see cref="TailAfterFinishSec"/> 秒・<see cref="MaxDurationSec"/>・アプリの休止。
    ///
    /// <b>有効化</b>: 端末の <c>files/xprec/ON</c> があるときだけ（既定は録らない。<c>tools/quest-xprec.py on</c>）。
    /// 印は録り始めのたびに見る（アプリの再起動は要らない）。
    ///
    /// <b>体験を止めない</b>: 書き出しは背景スレッド。容量の上限 <see cref="MaxBytes"/> に当たったら
    /// 映像だけ捨てて姿勢と入力は録り続ける。例外は呼び出し側へ投げない。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ExperienceRecorder : MonoBehaviour
    {
        public const float TailAfterFinishSec = 75f;
        public const float MaxDurationSec = 15f * 60f;
        public const long MaxBytes = 3L * 1024 * 1024 * 1024;

        public static string Root => Path.Combine(Application.persistentDataPath, "xprec");

        private ShowControlClient? _show;
        private ShowRunDirector? _run;
        private CameraStreamRegistry? _registry;
        private TitleScreen? _title;
        private Transform? _head, _left, _right;

        private bool _prevStaffActive = true;
        private Session? _session;
        private float _finishedFor;

        /// <summary>記録の実行体を作る（無ければ）。シーンへは焼かず、実行時に生やす。</summary>
        public static ExperienceRecorder Ensure()
        {
            var existing = FindObjectOfType<ExperienceRecorder>();
            if (existing != null) return existing;
            var go = new GameObject("[ExperienceRecorder]");
            DontDestroyOnLoad(go);
            return go.AddComponent<ExperienceRecorder>();
        }

        /// <summary>録っている最中か（テレメトリが読む）。</summary>
        public bool Recording => _session != null;

        private void OnEnable()
        {
            CameraStream.AnyFrameDecoded += OnFrame;
            Application.logMessageReceivedThreaded += OnLog;
        }

        private void OnDisable()
        {
            CameraStream.AnyFrameDecoded -= OnFrame;
            Application.logMessageReceivedThreaded -= OnLog;
            Stop("disabled");
        }

        private void OnApplicationPause(bool paused) { if (paused) Stop("pause"); }
        private void OnApplicationQuit() => Stop("quit");

        private void LateUpdate()
        {
            if (XpReplay.Active) return;   // 再生中に録り直さない
            if (_show == null) _show = FindObjectOfType<ShowControlClient>();
            if (_run == null) _run = FindObjectOfType<ShowRunDirector>();
            if (_registry == null) _registry = FindObjectOfType<CameraStreamRegistry>();
            if (_title == null) _title = FindObjectOfType<TitleScreen>();
            if (_show == null) return;

            bool staffActive = _show.StaffSetupActive;
            if (_session == null && _prevStaffActive && !staffActive && MarkerOn()) Start_("handoff");
            else if (_session != null && !_prevStaffActive && staffActive) Stop("staff");
            _prevStaffActive = staffActive;
            if (_session == null) return;

            if (_run != null && _run.Phase == ShowPhase.Finished) _finishedFor += Time.unscaledDeltaTime;
            else _finishedFor = 0f;
            float t = Time.unscaledTime - _session.t0;
            if (_finishedFor > TailAfterFinishSec) { Stop("finished"); return; }
            if (t > MaxDurationSec) { Stop("maxDuration"); return; }

            ResolveRig();
            var f = new XpRecFormat.Frame
            {
                t = t,
                head = PoseOf(_head),
                left = PoseOf(_left),
                right = PoseOf(_right),
                input = new RawInput
                {
                    bits = XpReplay.Live.bits
                           | (_show.StartAuthorized ? RawInput.StartAuthorized : 0)
                           | (_title != null && _title.ClosedAlready ? RawInput.TitleClosed : 0),
                },
                marks = (ushort)Mathf.Clamp(_show.VisitorMarkCount, 0, ushort.MaxValue),
                lang = (byte)ShowLanguage.Current,
                relief = (byte)(HorrorRelief.Enabled ? 1 : 0),
            };
            _session.Enqueue(f);
        }

        private static bool MarkerOn()
        {
            try { return File.Exists(Path.Combine(Root, "ON")); }
            catch { return false; }
        }

        private void Start_(string reason)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string dir = Path.Combine(Root, stamp);
            try
            {
                Directory.CreateDirectory(dir);
                var meta = new XpRecMeta
                {
                    startedAt = DateTime.Now.ToString("o", CultureInfo.InvariantCulture),
                    device = SystemInfo.deviceModel ?? "",
                    build = Debug.isDebugBuild ? "dev" : "release",
                    showSha = _show != null ? _show.ContentId : "",
                    startReason = reason,
                    registrationJson = ReadRegistration(),
                };
                var cams = new List<string>();
                if (_registry != null)
                    for (int i = 0; i < _registry.Count; i++)
                        cams.Add(_registry.Get(i)?.SourceKey ?? "");
                meta.cameras = cams.ToArray();
                _session = new Session(dir, meta, Time.unscaledTime);
                _finishedFor = 0f;
                // t= は ShowTelemetryHost と同じ時計（realtimeSinceStartup）。突き合わせはこの行で頭を揃える。
                Debug.Log($"[XP] t={Time.realtimeSinceStartup:F2} ev=xprec state=start why={reason} dir={stamp} cams={cams.Count} show={meta.showSha}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[XpRec] 録り始められない: {e.Message}");
                _session = null;
            }
        }

        private void Stop(string reason)
        {
            Session? s = _session;
            if (s == null) return;
            _session = null;
            float dur = Time.unscaledTime - s.t0;
            s.Finish(reason, dur);
            Debug.Log($"[XP] t={Time.realtimeSinceStartup:F2} ev=xprec state=stop why={reason} sec={dur:F1} frames={s.Frames} " +
                      $"jpeg={s.JpegCount} mb={s.Bytes / (1024f * 1024f):F0} capped={(s.Capped ? 1 : 0)}");
        }

        private void OnFrame(CameraStream stream, byte[] jpeg, int len)
        {
            Session? s = _session;
            if (s == null) return;
            s.EnqueueJpeg(stream.SourceKey, jpeg, len, Time.unscaledTime);
        }

        private void OnLog(string msg, string stack, LogType type)
        {
            Session? s = _session;
            if (s == null || msg == null || !msg.StartsWith("[XP]", StringComparison.Ordinal)) return;
            s.EnqueueLog(msg);
        }

        private static string ReadRegistration()
        {
            try
            {
                string p = Path.Combine(Application.persistentDataPath, "registration.json");
                return File.Exists(p) ? File.ReadAllText(p) : "";
            }
            catch { return ""; }
        }

        private void ResolveRig()
        {
            if (_head == null)
            {
                Camera? cam = Camera.main;
                _head = cam != null ? cam.transform : null;
            }
            if (_head != null && (_left == null || _right == null))
            {
                Transform? space = _head.parent;
                if (space != null)
                {
                    _left = space.Find("LeftHandAnchor");
                    _right = space.Find("RightHandAnchor");
                }
            }
        }

        private static XpRecFormat.Pose PoseOf(Transform? t)
        {
            if (t == null) return XpRecFormat.Pose.Identity;
            Vector3 p = t.position;
            Quaternion q = t.rotation;
            return new XpRecFormat.Pose { px = p.x, py = p.y, pz = p.z, qx = q.x, qy = q.y, qz = q.z, qw = q.w };
        }

        // ------------------------------------------------------------ 書き出し（背景スレッド）

        private sealed class Session
        {
            public readonly float t0;
            private readonly string _dir;
            private readonly XpRecMeta _meta;
            private readonly ConcurrentQueue<Item> _queue = new();
            private readonly ConcurrentBag<byte[]> _pool = new();
            private readonly AutoResetEvent _signal = new(false);
            private readonly Thread _thread;
            private volatile bool _closing;
            private long _queuedBytes;

            public int Frames;
            public int JpegCount;
            public long Bytes;
            public volatile bool Capped;

            private enum Kind { Track, Jpeg, Log }

            private struct Item
            {
                public Kind kind;
                public XpRecFormat.Frame frame;
                public string cam;
                public byte[] buf;
                public int len;
                public int ptsMs;
                public string text;
            }

            public Session(string dir, XpRecMeta meta, float t0)
            {
                _dir = dir;
                _meta = meta;
                this.t0 = t0;
                _thread = new Thread(Run) { IsBackground = true, Name = "XpRec" };
                _thread.Start();
            }

            public void Enqueue(in XpRecFormat.Frame f)
            {
                Frames++;
                _queue.Enqueue(new Item { kind = Kind.Track, frame = f });
                _signal.Set();
            }

            public void EnqueueJpeg(string cam, byte[] jpeg, int len, float now)
            {
                if (Capped || _closing || len <= 0) return;
                // 書き出しが追いつかないとき（溜まりが 64MB を超えた）も映像を捨てる。体験を止めない方を取る。
                if (Bytes + Interlocked.Read(ref _queuedBytes) + len > MaxBytes
                    || Interlocked.Read(ref _queuedBytes) > 64L * 1024 * 1024)
                {
                    Capped = true;
                    return;
                }
                if (!_pool.TryTake(out byte[]? buf) || buf.Length < len) buf = new byte[Math.Max(len, 128 * 1024)];
                Buffer.BlockCopy(jpeg, 0, buf, 0, len);
                Interlocked.Add(ref _queuedBytes, len);
                JpegCount++;
                int pts = Mathf.Max(0, Mathf.RoundToInt((now - t0) * 1000f));
                _queue.Enqueue(new Item { kind = Kind.Jpeg, cam = cam, buf = buf, len = len, ptsMs = pts });
                _signal.Set();
            }

            public void EnqueueLog(string line)
            {
                if (_closing) return;
                _queue.Enqueue(new Item { kind = Kind.Log, text = line });
                _signal.Set();
            }

            public void Finish(string reason, float durationSec)
            {
                _meta.stopReason = reason;
                _meta.durationSec = durationSec;
                _meta.frames = Frames;
                _closing = true;
                _signal.Set();
                // 書き残しを待つ上限は短く（メインスレッドを止める時間の上限）。間に合わなくても背景が書き切る。
                _thread.Join(300);
            }

            private void Run()
            {
                FileStream? track = null;
                BinaryWriter? tw = null;
                StreamWriter? log = null;
                var cams = new Dictionary<string, FileStream>();
                var camFrames = new Dictionary<string, int>();
                try
                {
                    track = new FileStream(Path.Combine(_dir, XpRecFormat.TrackFile), FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024);
                    XpRecFormat.WriteHeader(track);
                    tw = new BinaryWriter(track);
                    log = new StreamWriter(Path.Combine(_dir, XpRecFormat.XpLogFile), false, new System.Text.UTF8Encoding(false));
                    WriteMeta(camFrames);
                    double lastMeta = 0;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (true)
                    {
                        _signal.WaitOne(200);
                        while (_queue.TryDequeue(out Item it))
                        {
                            switch (it.kind)
                            {
                                case Kind.Track:
                                    XpRecFormat.WriteFrame(tw, it.frame);
                                    Bytes += XpRecFormat.FrameBytes;
                                    break;
                                case Kind.Log:
                                    log.WriteLine(it.text);
                                    break;
                                case Kind.Jpeg:
                                    try
                                    {
                                        if (!cams.TryGetValue(it.cam, out FileStream? fs))
                                        {
                                            fs = new FileStream(Path.Combine(_dir, XpRecFormat.CamFileName(it.cam)), FileMode.Create,
                                                                FileAccess.Write, FileShare.Read, 256 * 1024);
                                            RecordedSegmentFormat.WriteHeader(fs, 0, 0);
                                            cams[it.cam] = fs;
                                            camFrames[it.cam] = 0;
                                        }
                                        RecordedSegmentFormat.WriteFrame(fs, it.buf, it.len, it.ptsMs);
                                        camFrames[it.cam]++;
                                        Bytes += it.len + RecordedSegmentFormat.FrameHeaderBytes;
                                    }
                                    finally
                                    {
                                        Interlocked.Add(ref _queuedBytes, -it.len);
                                        _pool.Add(it.buf);
                                    }
                                    break;
                            }
                        }
                        // 落ちても途中までは使えるように、meta は数秒おきに書き直す。
                        if (sw.Elapsed.TotalSeconds - lastMeta > 5)
                        {
                            lastMeta = sw.Elapsed.TotalSeconds;
                            tw.Flush(); log.Flush();
                            foreach (var fs in cams.Values) fs.Flush();
                            WriteMeta(camFrames);
                        }
                        if (_closing && _queue.IsEmpty) break;
                    }
                    WriteMeta(camFrames);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[XpRec] 書き出し失敗: {e.Message}");
                    Capped = true;
                }
                finally
                {
                    try { tw?.Flush(); track?.Dispose(); } catch { }
                    try { log?.Dispose(); } catch { }
                    foreach (var fs in cams.Values) { try { fs.Dispose(); } catch { } }
                }
            }

            private void WriteMeta(Dictionary<string, int> camFrames)
            {
                var counts = new int[_meta.cameras.Length];
                for (int i = 0; i < counts.Length; i++)
                    counts[i] = camFrames.TryGetValue(_meta.cameras[i], out int n) ? n : 0;
                _meta.cameraFrames = counts;
                _meta.bytes = Bytes;
                _meta.capped = Capped;
                if (_meta.frames < Frames) _meta.frames = Frames;
                string path = Path.Combine(_dir, XpRecFormat.MetaFile);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(_meta, true));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
        }
    }
}
