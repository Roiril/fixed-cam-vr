#nullable enable
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using FixedCamVr.Diagnostics;
using FixedCamVr.Streaming;
using FixedCamVr.Streaming.Recording;
using FixedCamVr.Streaming.Replay;
using FixedCamVr.Tracking;
using UnityEditor;
using UnityEngine;

/// <summary>
/// <b>体験の再生（PC の Editor）</b>。Quest で録った 1 回ぶん（<see cref="ExperienceRecorder"/>）を、
/// 本番の Main シーンへ流し直す。入るのは頭と両手の姿勢・コントローラの生の入力・3 台のカメラの JPEG・
/// 言語と軽減。そこから先（区間・演出・合成・エージェント画面・音）は本番の処理がそのまま作る。
/// 撮るのは <see cref="XpReplayCapture"/>。
///
/// 起動は Editor の <c>menu replay</c>（<c>XpReplayRun</c>）。SessionState に録画の場所を置いて Play に入ると、
/// シーンの読み込み前にここが立ち上がる（全員の Awake / Start が「再生中」を見られる）。
///
/// <b>時計は実時間</b>（端末と同じ）。コマ送りにはしない — 本番の処理は実時間の時計（unscaled / realtime）を
/// 約 140 か所で読み、<c>Time.captureDeltaTime</c> はそのどれにも効かない（2026-10-23 実測: 進行 8.0s / 実時間 8.7s）。
/// PC が遅いと撮影のコマが間引かれるだけで、体験の進み方と音は端末と同じ時刻で進む。
///
/// ⚠ パススルー（現実の映像）は PC に無い。導入の現実が見えている所は黒になる（2026-10-23 ユーザー了承）。
/// ⚠ 頭の姿勢は記録のワールド座標をそのまま入れる。registration.json も記録の版を使うので、区間の判定は端末と同じになる。
/// </summary>
[DefaultExecutionOrder(-32000)]
public sealed class XpReplayDriver : MonoBehaviour
{
    public const string KeyDir = "fcv.replay.dir";
    public const string KeyFps = "fcv.replay.fps";
    public const string KeyDone = "fcv.replay.done";
    public const string KeyResult = "fcv.replay.result";

    /// <summary>起動から再生の頭までの待ち（著作の読み込み・カメラの初回展開）。</summary>
    private const float BootSec = 4f;
    /// <summary>記録の終わりから、撮るのを止めるまでの余り。</summary>
    private const float TailSec = 1.5f;

    public static XpReplayDriver? Instance { get; private set; }

    private string _dir = "";
    private XpRecMeta _meta = new();
    private XpRecFormat.Frame[] _track = Array.Empty<XpRecFormat.Frame>();
    private Feed _feed = null!;
    private int _cursor;
    private float _bootAt = -1f;
    private float _t0 = -1f;
    private bool _finished;
    private XpRecFormat.Frame _now;
    private Transform? _head, _rig, _left, _right;
    private ShowControlClient? _show;
    private StreamWriter? _xpLog;
    private TitleScreen? _title;
    private float _titleLagAt = -1f, _markLagAt = -1f;
    private int _catchUps;

    /// <summary>
    /// 記録の題字と報告に、ボタンの再現が追いつかないときの猶予（秒）。人の走行はボタンで再現されるので当たらない。
    /// 自動走行（<c>ShowWalkDebugDriver</c>）はボタンを押さずに直接閉じる・報告するので、その記録はここで追いつく。
    /// </summary>
    private const float CatchUpSec = 1.0f;

    /// <summary>再生の頭からの秒（頭の前は負）。</summary>
    public float Tau => _t0 < 0f ? -1f : Time.unscaledTime - _t0;
    public bool Started => _t0 >= 0f;
    public bool Finished => _finished;
    public float Duration => _track.Length > 0 ? _track[_track.Length - 1].t : 0f;
    public string RecordingDir => _dir;
    public XpRecMeta Meta => _meta;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        string dir = SessionState.GetString(KeyDir, "");
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            var go = new GameObject("[XpReplay]");
            DontDestroyOnLoad(go);
            var d = go.AddComponent<XpReplayDriver>();
            if (!d.Load(dir)) { Fail("記録を読めない: " + dir); Destroy(go); return; }
            XpReplay.Begin(d.CurrentInput, d._feed);
            go.AddComponent<XpReplayCapture>();
            Instance = d;
            Debug.Log($"[Replay] 再生を準備した: {dir} 尺 {d.Duration:F1}s {d._track.Length} 行 " +
                      $"cams={string.Join(",", d._meta.cameras)} show={d._meta.showSha}");
        }
        catch (Exception e) { Fail("起動に失敗: " + e); }
    }

    private static void Fail(string why)
    {
        Debug.LogError("[Replay] " + why);
        SessionState.SetString(KeyResult, "error: " + why);
        SessionState.SetBool(KeyDone, true);
        EditorApplication.ExitPlaymode();
    }

    private bool Load(string dir)
    {
        _dir = dir;
        string metaPath = Path.Combine(dir, XpRecFormat.MetaFile);
        string trackPath = Path.Combine(dir, XpRecFormat.TrackFile);
        if (!File.Exists(metaPath) || !File.Exists(trackPath)) return false;
        _meta = JsonUtility.FromJson<XpRecMeta>(File.ReadAllText(metaPath)) ?? new XpRecMeta();
        using (var fs = File.OpenRead(trackPath)) _track = XpRecFormat.ReadTrack(fs);
        if (_track.Length < 2) return false;
        _feed = new Feed(dir, () => Tau);

        // 記録の頭の位置合わせを、再生の保存先（productName を差し替えた別フォルダ）へ置く。
        // CourseFrame は Awake で読むので、シーンより先に書く。
        string reg = Path.Combine(Application.persistentDataPath, "registration.json");
        Directory.CreateDirectory(Application.persistentDataPath);
        if (!string.IsNullOrEmpty(_meta.registrationJson)) File.WriteAllText(reg, _meta.registrationJson);
        else if (File.Exists(reg)) File.Delete(reg);
        return true;
    }

    // ------------------------------------------------------------------ 入力

    private RawInput CurrentInput()
    {
        if (!Started)
        {
            // 頭の前: 被っていて、何も押していない。コントローラは記録の頭と同じ繋がり方。
            int keep = RawInput.LConnected | RawInput.LTracked | RawInput.RConnected | RawInput.RTracked;
            int bits = (_track.Length > 0 ? _track[0].input.bits : 0) & keep;
            return new RawInput { bits = bits | RawInput.UserPresent };
        }
        return _now.input;
    }

    private void OnEnable()
    {
        Application.onBeforeRender += ApplyPose;
        Application.logMessageReceived += OnLog;
    }

    private void OnDisable()
    {
        Application.onBeforeRender -= ApplyPose;
        Application.logMessageReceived -= OnLog;
        _xpLog?.Dispose();
        _xpLog = null;
        _feed?.Dispose();
        XpReplay.End();
    }

    private void Update()
    {
        if (_bootAt < 0f) _bootAt = Time.unscaledTime;
        if (_show == null) _show = FindObjectOfType<ShowControlClient>();

        if (!Started && Time.unscaledTime - _bootAt >= BootSec && _show != null) BeginPlayback();

        float tau = Started ? Tau : 0f;
        _cursor = XpRecFormat.IndexAt(_track, tau, _cursor);
        _now = _track[Mathf.Max(0, _cursor)];

        if (Started)
        {
            ApplyPrefs(_now);
            CatchUp(tau);
            if (!_finished && tau > Duration + TailSec)
            {
                _finished = true;
                Debug.Log($"[Replay] 記録の終わりまで流した（{Duration:F1}s）");
            }
        }

        ApplyPose();
    }

    private void LateUpdate() => ApplyPose();

    private void BeginPlayback()
    {
        // スタッフの準備を抜ける（端末では引き渡しの瞬間から録っている）。
        StaffSetupPanel.Ensure()?.UseAutomation();
        var reg = FindObjectOfType<CourseRegistrationController>();
        if (reg != null && reg.IsActive) reg.Toggle();

        // 始めてよいかは記録の値（端末で練習と題字を通った瞬間）に合わせる。
        if (_show != null)
        {
            _show.StartAuthorizedProvider = () => Started && _now.input.Has(RawInput.StartAuthorized);
            if (!string.IsNullOrEmpty(_meta.showSha) && _show.ContentId != _meta.showSha)
                Debug.LogWarning($"[Replay] ⚠ 著作が端末と違う（端末 {_meta.showSha} / PC {_show.ContentId}）。" +
                                 "演出の時刻が端末とずれうる。同じ APK の焼き込みで流すこと");
        }

        // 言語と軽減はタブレットが題字で載せる。記録の最後の値を先に積んでおく（題字で戻されないように）。
        XpRecFormat.Frame last = _track[_track.Length - 1];
        VisitorPrefs.Set((ShowLang)last.lang, last.relief != 0, 1);
        ApplyPrefs(_track[0]);

        string? outDir = XpReplayCapture.OutputDir;
        if (outDir != null)
        {
            Directory.CreateDirectory(outDir);
            _xpLog = new StreamWriter(Path.Combine(outDir, "replay_xp.log"), false, new System.Text.UTF8Encoding(false));
        }

        // 画面の時計（OSD）は記録した日時から進める（PC の今の時刻を出さない）。
        if (DateTime.TryParse(_meta.startedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime started))
            foreach (ScreenOsd osd in FindObjectsOfType<ScreenOsd>())
                osd.TimeProvider = () => started.AddSeconds(Mathf.Max(0f, Tau));

        _t0 = Time.unscaledTime;
        Debug.Log($"[Replay] 再生の頭 t0={_t0:F2} reg={(string.IsNullOrEmpty(_meta.registrationJson) ? "なし" : "あり")}");
    }

    private void CatchUp(float tau)
    {
        if (_title == null) _title = FindObjectOfType<TitleScreen>();
        if (_title != null && _now.input.Has(RawInput.TitleClosed) && !_title.ClosedAlready && _title.IsBuilt)
        {
            if (_titleLagAt < 0f) _titleLagAt = tau;
            else if (tau - _titleLagAt > CatchUpSec)
            {
                if (_title.Stage == TitleStage.Wait) _title.ShowTitle();
                else if (_title.DismissTitle()) { _catchUps++; Debug.Log($"[Replay] 題字を記録に合わせて閉じた（tau={tau:F2}）"); }
                _titleLagAt = tau;
            }
        }
        else _titleLagAt = -1f;

        if (_show != null && _now.marks > _show.VisitorMarkCount)
        {
            if (_markLagAt < 0f) _markLagAt = tau;
            else if (tau - _markLagAt > CatchUpSec)
            {
                bool ok = _show.RecordVisitorMark();
                _catchUps++;
                Debug.Log($"[Replay] 報告を記録に合わせて足した（tau={tau:F2} 記録 {_now.marks} / 再生 {_show.VisitorMarkCount} ok={ok}）");
                _markLagAt = -1f;
            }
        }
        else _markLagAt = -1f;
    }

    /// <summary>ボタンで再現できず、記録に合わせて足した回数（0 なら入力だけで再現できた）。</summary>
    public int CatchUps => _catchUps;

    private static void ApplyPrefs(in XpRecFormat.Frame f)
    {
        var lang = (ShowLang)f.lang;
        if (ShowLanguage.Current != lang) ShowLanguage.Select(lang);
        bool relief = f.relief != 0;
        if (HorrorRelief.Enabled != relief) HorrorRelief.Select(relief);
    }

    // ------------------------------------------------------------------ 姿勢

    private void ApplyPose()
    {
        if (_track.Length == 0) return;
        if (_head == null)
        {
            Camera? cam = Camera.main;
            if (cam == null) return;
            _head = cam.transform;
            Transform? t = _head;
            while (t != null && t.name != "OVRCameraRig") t = t.parent;
            _rig = t != null ? t : _head.root;
            Transform? space = _head.parent;
            _left = space != null ? space.Find("LeftHandAnchor") : null;
            _right = space != null ? space.Find("RightHandAnchor") : null;
        }
        if (_rig == null) return;

        // 頭がワールドで記録の位置に来るよう、台座（OVRCameraRig）を動かす。頭の局所姿勢は OVR が決めるので触らない。
        Quaternion relRot = Quaternion.Inverse(_rig.rotation) * _head.rotation;
        Vector3 relPos = Quaternion.Inverse(_rig.rotation) * (_head.position - _rig.position);
        Pose(_now.head, out Vector3 hp, out Quaternion hq);
        Quaternion rigRot = hq * Quaternion.Inverse(relRot);
        _rig.SetPositionAndRotation(hp - rigRot * relPos, rigRot);

        if (_left != null) { Pose(_now.left, out Vector3 p, out Quaternion q); _left.SetPositionAndRotation(p, q); }
        if (_right != null) { Pose(_now.right, out Vector3 p, out Quaternion q); _right.SetPositionAndRotation(p, q); }
    }

    private static void Pose(in XpRecFormat.Pose p, out Vector3 pos, out Quaternion rot)
    {
        pos = new Vector3(p.px, p.py, p.pz);
        rot = new Quaternion(p.qx, p.qy, p.qz, p.qw);
        float n = rot.x * rot.x + rot.y * rot.y + rot.z * rot.z + rot.w * rot.w;
        rot = n > 1e-6f ? Quaternion.Normalize(rot) : Quaternion.identity;
    }

    /// <summary>頭のワールド姿勢（撮影カメラが追う）。</summary>
    public void HeadPose(out Vector3 pos, out Quaternion rot) => Pose(_now.head, out pos, out rot);

    private void OnLog(string msg, string stack, LogType type)
    {
        if (_xpLog == null || !Started || msg == null || !msg.StartsWith("[XP]", StringComparison.Ordinal)) return;
        _xpLog.Write("tau=");
        _xpLog.Write(Tau.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
        _xpLog.Write(' ');
        _xpLog.WriteLine(msg);
    }

    // ------------------------------------------------------------------ カメラの映像

    /// <summary>記録した JPEG を、再生の時刻に合わせて CameraStream へ渡す。</summary>
    private sealed class Feed : XpReplay.IFrameFeed, IDisposable
    {
        private sealed class Cam
        {
            public FileStream fs = null!;
            public RecordedSegmentFormat.FrameRef[] index = Array.Empty<RecordedSegmentFormat.FrameRef>();
            public int cursor;
            public int given = -1;
        }

        private readonly Dictionary<string, Cam> _cams = new();
        private readonly Func<float> _tau;

        public Feed(string dir, Func<float> tau)
        {
            _tau = tau;
            foreach (string path in Directory.GetFiles(dir, XpRecFormat.CamPrefix + "*" + XpRecFormat.CamSuffix))
            {
                string name = Path.GetFileName(path);
                string key = name.Substring(XpRecFormat.CamPrefix.Length, name.Length - XpRecFormat.CamPrefix.Length - XpRecFormat.CamSuffix.Length);
                var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024);
                var cam = new Cam { fs = fs, index = RecordedSegmentFormat.BuildIndex(fs) };
                _cams[key] = cam;
                Debug.Log($"[Replay] カメラ {key}: {cam.index.Length} コマ / {RecordedSegmentFormat.DurationSec(cam.index):F1}s");
            }
        }

        private int Target(Cam c)
        {
            if (c.index.Length == 0) return -1;
            float tau = _tau();
            // 頭の前は最初のコマを見せておく（端末では引き渡しの前から映っている）。
            int ms = tau < 0f ? c.index[0].ptsMs : Math.Max(c.index[0].ptsMs, (int)(tau * 1000f));
            c.cursor = RecordedSegmentFormat.SeekIndex(c.index, ms, c.cursor);
            return c.cursor;
        }

        private static string Key(string source)
        {
            string k = XpRecFormat.CamFileName(source);
            return k.Substring(XpRecFormat.CamPrefix.Length, k.Length - XpRecFormat.CamPrefix.Length - XpRecFormat.CamSuffix.Length);
        }

        public bool HasPending(string source)
        {
            if (!_cams.TryGetValue(Key(source), out Cam? c)) return false;
            int i = Target(c);
            return i >= 0 && i != c.given;
        }

        public bool TryConsume(string source, ref byte[]? buf, out int length, out long seq)
        {
            length = 0;
            seq = 0;
            if (!_cams.TryGetValue(Key(source), out Cam? c)) return false;
            int i = Target(c);
            if (i < 0 || i == c.given) return false;
            RecordedSegmentFormat.FrameRef r = c.index[i];
            if (buf == null || buf.Length < r.length) buf = new byte[Math.Max(r.length, 128 * 1024)];
            c.fs.Position = r.offset;
            int got = 0;
            while (got < r.length)
            {
                int n = c.fs.Read(buf, got, r.length - got);
                if (n <= 0) return false;
                got += n;
            }
            c.given = i;
            length = r.length;
            seq = i + 1;
            return true;
        }

        public void Dispose()
        {
            foreach (Cam c in _cams.Values) { try { c.fs.Dispose(); } catch { } }
            _cams.Clear();
        }
    }
}
#endif
