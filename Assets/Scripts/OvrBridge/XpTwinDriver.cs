#nullable enable
#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FixedCamVr.Diagnostics;
using FixedCamVr.Input;
using FixedCamVr.OvrBridge;
using FixedCamVr.Streaming;
using FixedCamVr.Streaming.Recording;
using FixedCamVr.Streaming.Replay;
using FixedCamVr.Tracking;
using UnityEditor;
using UnityEngine;

/// <summary>
/// <b>ツイン走行（PC の Editor・HMD も Quest も配信スマホも要らない）</b>。
/// 本番の Main シーンを最初から終幕まで流す。歩くのは <see cref="ShowWalkDebugDriver"/> の閉ループ（本番の状態を見て進む）、
/// 入力は生の左 X だけ（人と同じ順で練習と題字と報告を通す）、配信の JPEG はデジタルツインの動画から作った束。
///
/// <b>走った結果は体験の記録（xprec）に残す。</b>目線の動画は 2 通りで撮れる。
/// <c>capture=eye</c> なら走行の Play の中で <see cref="XpReplayCapture"/> が同時に撮る（1 回方式・twin-verify の既定）。
/// 撮る時計の頭は記録の頭（<c>ev=xprec state=start</c>）で、再生の tau と同じ所。<c>replay_xp.log</c> も同じ形で出力先へ書く。
/// 付けなければ、同じ記録を <c>unity.ps1 replay</c> で撮る（2 段。後ろ上の動画はこちらでしか撮れない）。
///
/// 配信のコマは時刻ではなく<b>頭の位置</b>で引く。forward 1 周ぶんの動画を全周に使い回せ、立ち止まっている間もコマを配り続ける
/// （同じコマで止めると砂嵐の判定が走る）。周の番号を右下の 3 マスに焼いてあるので、録画の取り違えは画に出る。
///
/// ⚠ 見た目の合否はここでは出ない。パススルーは無く、3 周目の録画の中身も 1 周目と同じ人の動画になる。
/// 出るのは体験の順序・時刻・演出が画に出たか・例外。
/// </summary>
[DefaultExecutionOrder(-32000)]
public sealed class XpTwinDriver : MonoBehaviour, ShowWalkDebugDriver.IHost, IXpCaptureSource
{
    public const string KeyPack = "fcv.twin.pack";
    public const string KeyOut = "fcv.twin.out";
    public const string KeyDone = "fcv.twin.done";
    public const string KeyResult = "fcv.twin.result";
    public const string KeySpeed = "fcv.twin.speed";
    public const string KeyDwell = "fcv.twin.dwell";
    public const string KeyNoMark = "fcv.twin.nomark";
    /// <summary>周の中でも報告する区間（"1B.2A.3B" のように周と区間の文字を . で区切る）。空なら 4-A の締めだけ押す。</summary>
    public const string KeyMarks = "fcv.twin.marks";
    public const string KeyRelief = "fcv.twin.relief";
    public const string KeyShowCalib = "fcv.twin.showcalib";
    /// <summary>走行の Play の中で撮る動画（"eye" か空）。撮り方の値は <see cref="XpReplayCapture"/> の <c>fcv.replay.*</c>。</summary>
    public const string KeyCapture = "fcv.twin.capture";

    private const float HeadHeightM = 1.54f;     // 立位 1.65m の目の高さ（VirtualWalkRun と同じ）
    private const float VideoLatencySec = 0.15f; // ShowCgLayer.VideoLatencySec と同じ。映っているのはこの秒数だけ前の姿
    private const float TailSec = 70f;           // 終了してから終幕を撮り切るまで（ExperienceRecorder.TailAfterFinishSec より短く）
    private const float HardLimitSec = 540f;
    private const float ReportHoldSec = 1.2f;    // VisitorMarkHoldLogic.DefaultHoldSec（1 秒）より少し長く
    private const float ExtraMarkDelaySec = 2.5f; // 区間に入ってから押すまで。入った瞬間の異変が見えてから押す人の間
    private const float LateMarkDelaySec = 9.2f;  // 区間名の後ろに L（例 3BL）。録画のカットが終わって人形のカットへ替わった後に押す
    private const float TitleReadSec = 3f;
    private const float TitleTapSec = 0.2f;
    private const float OnboardingLimitSec = 90f;

    [Serializable]
    private sealed class PackMeta
    {
        public float fps;
        public int laps;
        public int moveEnd;
        public float[] xs = Array.Empty<float>();
        public float[] zs = Array.Empty<float>();
        public string[] cams = Array.Empty<string>();
        public string calibOverride = "";
        public string source = "";
    }

    private string _pack = "", _outDir = "";
    private PackMeta _meta = new();
    private PackFeed _feed = null!;
    private float _speed, _dwell;
    private bool _noMark;
    private readonly HashSet<string> _markAt = new();
    private readonly HashSet<string> _markDone = new();
    private string _markSeg = "";
    private float _markSegSince;
    private float _bootAt;
    private Transform? _head, _rig, _left, _right;
    private ShowControlClient? _show;
    private ShowRunDirector? _run;
    private MjpegScreen? _screen;
    private OvrControllerBridge? _bridge;
    private TimelineDirector? _timeline;
    private FixedCamVr.Streaming.Cg.ShowCgLayer? _cg;
    private ScreenOverlayController? _overlay;
    private bool _xHeld;
    private float _reportUntil = -1f;
    private float _finishedFor;
    private bool _ending;
    private StreamWriter? _log;
    private readonly object _logLock = new();
    private readonly Queue<(float t, Vector2 p)> _posHistory = new();
    private float _badgeAt;
    private int _shownLap = -1;
    private bool _shownRec;
    private int _shownSegLap = -1;
    private int _shownSegCam = -1;
    private int _pendingLap = -2, _pendingSegLap = -2, _pendingSegCam = -2;
    private bool _pendingRec;
    private float _farewellLogAt;
    private bool _farewellShareLogged;
    private RenderTexture? _badgeRt;
    private Texture2D? _badgeRead;
    private float _runT0 = -1f;
    private readonly List<string> _notes = new();
    private XpReplayCapture? _capture;
    private float _recT0 = -1f;         // 撮る時計の頭（体験の記録の頭と同じ unscaledTime）
    private StreamWriter? _tauLog;      // replay_xp.log（同時撮影のときだけ）
    private string _tauLogError = "";
    private bool _recHeadLogged;
    private string _captureResult = "";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        string pack = SessionState.GetString(KeyPack, "");
        if (string.IsNullOrEmpty(pack)) return;
        try
        {
            var go = new GameObject("[XpTwin]");
            DontDestroyOnLoad(go);
            var d = go.AddComponent<XpTwinDriver>();
            d.Load(pack);
            // calib=show は本番の calib のまま走る（配置のせいか本番の不具合かを切り分ける校正）。
            bool useTwinCalib = SessionState.GetInt(KeyShowCalib, 0) == 0;
            XpReplay.Begin(d.Input_, d._feed, true, useTwinCalib ? d._meta.calibOverride : "");
            ShowWalkDebugDriver.Host = d;
            // 同時撮影。撮影は同じ GameObject の IXpCaptureSource（このドライバ）を読む。
            if (SessionState.GetString(KeyCapture, "") == "eye") d._capture = go.AddComponent<XpReplayCapture>();
            Debug.Log($"[Twin] 走行を準備した: 束 {pack} 速さ {d._speed:F2}m/s 滞在 {d._dwell:F1}s 報告 {(d._noMark ? "しない" : "する")}" +
                      $" 撮影 {(d._capture != null ? "目線を同時に撮る → " + SessionState.GetString(XpReplayCapture.KeyOut, "") : "しない")}");
        }
        catch (Exception e)
        {
            Debug.LogError("[Twin] 起動に失敗: " + e);
            SessionState.SetString(KeyResult, "error: " + e.Message);
            SessionState.SetBool(KeyDone, true);
            EditorApplication.ExitPlaymode();
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AfterScene()
    {
        if (ShowWalkDebugDriver.Host is not XpTwinDriver d) return;
        // 歩く本体は Quest の自動走行と同じ。起動フラグの代わりにここで立てる。
        d.gameObject.AddComponent<ShowWalkDebugDriver>();
    }

    private const string KeyFast = "fcv.twin.fast";
    /// <summary>固定刻みの 1 フレーム。本番の 90Hz と同じにする（「次のフレームで判定する」型の処理の意味を変えない）。</summary>
    private const float FastStepSec = 1f / 90f;
    private bool _fast;
    private bool _osdHooked;
    private float _wall0;

    private void Load(string pack)
    {
        _pack = pack;
        _outDir = SessionState.GetString(KeyOut, "");
        _speed = SessionState.GetFloat(KeySpeed, 0.25f);
        _dwell = SessionState.GetFloat(KeyDwell, 6f);
        _noMark = SessionState.GetInt(KeyNoMark, 0) != 0;
        _markAt.Clear();
        foreach (string s in SessionState.GetString(KeyMarks, "").Split('.', StringSplitOptions.RemoveEmptyEntries))
            _markAt.Add(s.Trim().ToUpperInvariant());
        _meta = JsonUtility.FromJson<PackMeta>(File.ReadAllText(Path.Combine(pack, "pack.json")))
                ?? throw new InvalidDataException("pack.json を読めない");
        if (_meta.xs.Length < 2 || _meta.moveEnd < 2) throw new InvalidDataException("pack.json の道が空");
        _feed = new PackFeed(pack, _meta);

        // 位置合わせは原点そのまま（ツインの座標 = course）。前回の値を残さない。CourseFrame は Awake で読むので、シーンより先に書く。
        Directory.CreateDirectory(Application.persistentDataPath);
        File.WriteAllText(Path.Combine(Application.persistentDataPath, "registration.json"),
            "{\"originX\":0,\"originZ\":0,\"yawDeg\":0,\"maxResidualM\":0,\"pointCount\":2,\"savedAtIso\":\"twin\",\"originY\":0,\"floorSpreadM\":0,\"regSchema\":2}");
        // 体験の記録を ON にする。残っている古い記録は混ざらないように消す。
        if (Directory.Exists(ExperienceRecorder.Root)) Directory.Delete(ExperienceRecorder.Root, true);
        Directory.CreateDirectory(ExperienceRecorder.Root);
        File.WriteAllText(Path.Combine(ExperienceRecorder.Root, "ON"), "twin");

        Directory.CreateDirectory(_outDir);
        _log = new StreamWriter(Path.Combine(_outDir, "run_xp.log"), false, new System.Text.UTF8Encoding(false));
        _wall0 = WallClock.Now;
        _fast = SessionState.GetInt(KeyFast, 0) != 0;
        if (_fast)
        {
            // 固定刻み: 1 フレーム = 1/90 秒で体験の時計を進め、描ける速さで回す（実時間より速い）。
            // シーンの Awake より前に立てる（VideoPlayer の時計の選び方などが Awake でこれを見る）。
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            SimTime.EnableFixedStep(FastStepSec);
        }
        else Application.targetFrameRate = 90;   // batchmode は上限が無い。本番の刻みで走らせる
        _log.WriteLine($"[XP] t={SimTime.Realtime:F2} ev=clock mode={(_fast ? "fast" : "real")} step={SimTime.StepSec:F5}");
    }

    // ------------------------------------------------------------------ IHost

    public float WalkSpeedMps => _speed;
    public bool SkipReport => _noMark;

    public IEnumerator PassOnboarding()
    {
        _show = FindObjectOfType<ShowControlClient>();
        _bridge = FindObjectOfType<OvrControllerBridge>();
        var title = FindObjectOfType<TitleScreen>();
        if (_bridge == null || _show == null)
        {
            Note("OvrControllerBridge が居ない");
            yield break;
        }
        StaffSetupPanel.Ensure()?.UseAutomation();
        if (SessionState.GetInt(KeyRelief, 0) != 0) HorrorRelief.Select(true);
        // 本番は準備画面の開始（Logic.Started）も見る。ツイン走行は準備画面を通らないので導入の完了だけを見る。
        _show.StartAuthorizedProvider = () => _bridge.OnboardingStage == HmdOnboardingStage.Complete;

        // 練習は案内を読み終えるまで受け付けない。人と同じく、受け付けられるまで押し直す。
        float begin = SimTime.Now;
        float T() => SimTime.Now - begin;
        float nextPress = 2f, titleSince = -1f;
        while (_bridge.OnboardingStage != HmdOnboardingStage.Complete && T() < OnboardingLimitSec)
        {
            HmdOnboardingStage stage = _bridge.OnboardingStage;
            if (stage == HmdOnboardingStage.Tutorial && T() >= nextPress)
            {
                Debug.Log($"[Twin] 報告の練習を左 X で押す（{T():F1}s）");
                yield return HoldX(ReportHoldSec);
                nextPress = T() + 1f;
                continue;
            }
            if (stage == HmdOnboardingStage.Title)
            {
                if (titleSince < 0f) titleSince = T();
                if (T() - titleSince >= TitleReadSec && T() >= nextPress)
                {
                    Debug.Log($"[Twin] 題字を左 X の短押しで閉じる（{T():F1}s）");
                    yield return HoldX(TitleTapSec);
                    nextPress = T() + 2f;
                    continue;
                }
            }
            yield return null;
        }
        if (_bridge.OnboardingStage != HmdOnboardingStage.Complete)
        {
            // ここを越えられない走行は本編を見ていない。残りの 7 分を待たず、落ちた所を報告する。
            Note($"導入が終わらない stage={_bridge.OnboardingStage} title={(title != null ? title.Stage.ToString() : "-")}");
            End("onboarding");
            yield break;
        }
        // 題字が閉じ切るまで待つ（閉じる演出の途中で歩き出さない）。
        float t = 0f;
        while (title != null && title.IsBlocking && t < 15f) { t += SimTime.Dt; yield return null; }
        _runT0 = SimTime.Now;
    }

    private IEnumerator HoldX(float sec)
    {
        _xHeld = true;
        float end = SimTime.Now + sec;
        while (SimTime.Now < end) yield return null;
        _xHeld = false;
        yield return null;
    }

    public List<WalkRouteLogic.Waypoint> BuildRoute(ShowLayoutDef layout, int laps, float dwellSec, out string log)
    {
        List<Vector2> ring = Ring(layout);
        int[] holds = SectionMiddles(ring, layout);
        var route = new List<WalkRouteLogic.Waypoint>(ring.Count * laps + 1);
        for (int lap = 0; lap < laps; lap++)
            for (int i = 0; i < ring.Count; i++)
                route.Add(new WalkRouteLogic.Waypoint(ring[i], Array.IndexOf(holds, i) >= 0 ? _dwell : 0f, -1));
        route.Add(new WalkRouteLogic.Waypoint(ring[0], 0f, -1));
        log = $"rail laps={laps} points={route.Count} holds={string.Join(",", holds)} dwell={_dwell:F1}";
        return route;
    }

    public List<Vector2> ClosingPath(Vector2 from)
    {
        List<Vector2> ring = Ring(_show!.Layout!);
        int start = 0;
        float best = float.PositiveInfinity;
        for (int i = 0; i < ring.Count; i++)
        {
            float d = (ring[i] - from).sqrMagnitude;
            if (d < best) { best = d; start = i; }
        }
        // 区間 A の中だけを進む（線をまたぐには 0.5m で足りる）。
        int n = Mathf.Min(ring.Count, Mathf.CeilToInt(0.5f / 0.05f));
        return Enumerable.Range(1, n).Select(k => ring[(start + k) % ring.Count]).ToList();
    }

    public bool TryGetFarewellStand(out Vector2 stand, out Vector2 doll)
    {
        stand = doll = default;
        if (_cg == null) _cg = FindObjectOfType<FixedCamVr.Streaming.Cg.ShowCgLayer>();
        if (_cg == null || !_cg.TryGetFarewellStand(out Vector3 s, out Vector3 d)) return false;
        stand = new Vector2(s.x, s.z);   // 位置合わせは原点そのままなので、ワールド = course
        doll = new Vector2(d.x, d.z);
        return true;
    }

    public void PressReport() => _reportUntil = SimTime.Now + ReportHoldSec;
    public bool ReportPressing => SimTime.Now < _reportUntil;

    private static List<Vector2> Ring(ShowLayoutDef layout)
    {
        ShowRoomWallDef[]? walls = layout.room?.walls;
        if (walls == null || walls.Length != 2) throw new InvalidOperationException("手すりの道には layout.room.walls の 2 枚が要る");
        return RailWalkRoute.Ring(new Vector2(walls[0].x1, walls[0].z1), new Vector2(walls[0].x2, walls[0].z2), new Vector2(walls[1].x2, walls[1].z2));
    }

    /// <summary>輪の点のうち、カメラの区画ごとの真ん中。そこで立ち止まる（演出の尺を見る）。</summary>
    private static int[] SectionMiddles(List<Vector2> ring, ShowLayoutDef layout)
    {
        int Cam(Vector2 p) => CamAt(layout, p);
        var result = new List<int>();
        foreach (int cam in ring.Select(Cam).Where(c => c >= 0).Distinct())
        {
            List<int> idx = Enumerable.Range(0, ring.Count).Where(i => Cam(ring[i]) == cam).ToList();
            result.Add(idx[idx.Count / 2]);
        }
        return result.ToArray();
    }

    // ------------------------------------------------------------------ 入力と姿勢

    private RawInput Input_()
    {
        int bits = RawInput.LConnected | RawInput.LTracked | RawInput.RConnected | RawInput.RTracked | RawInput.UserPresent;
        if (_xHeld || ReportPressing) bits |= RawInput.X;
        return new RawInput { bits = bits };
    }

    private void OnEnable()
    {
        Application.logMessageReceivedThreaded += OnLog;
        Application.logMessageReceived += OnMainLog;
    }

    private void OnDisable()
    {
        Application.logMessageReceivedThreaded -= OnLog;
        Application.logMessageReceived -= OnMainLog;
        lock (_logLock) { _log?.Dispose(); _log = null; }
        CloseTauLog();
        _feed?.Dispose();
        ShowWalkDebugDriver.Host = null;
        XpReplay.End();
        if (_badgeRt != null) Destroy(_badgeRt);
        if (_badgeRead != null) Destroy(_badgeRead);
    }

    private void ResolveRig()
    {
        if (_head != null) return;
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

    private void Update()
    {
        if (_bootAt <= 0f) _bootAt = SimTime.Now;
        ReportCaptureClock();
        ResolveRig();
        if (_run == null) _run = FindObjectOfType<ShowRunDirector>();
        if (_show == null) _show = FindObjectOfType<ShowControlClient>();
        if (_screen == null) _screen = FindObjectOfType<MjpegScreen>();
        if (_timeline == null) _timeline = FindObjectOfType<TimelineDirector>();
        if (_head == null) return;

        // 映っているのは少し前の姿（本番の遅延と揃える）。立ち止まっても配り続けるのは PackFeed の側。
        float now = SimTime.Now;
        if (_fast && !_osdHooked)
        {
            // 画の中の時計も体験の時計で進める（素のままだと実時間で刻み、速く進む画の中で遅れて見える）。
            DateTime started = DateTime.Now;
            float t0 = now;
            ScreenOsd[] osds = FindObjectsOfType<ScreenOsd>();
            foreach (ScreenOsd osd in osds) osd.TimeProvider = () => started.AddSeconds(SimTime.Now - t0);
            _osdHooked = osds.Length > 0;
        }
        Vector3 hp = _head.position;
        _posHistory.Enqueue((now, new Vector2(hp.x, hp.z)));
        while (_posHistory.Count > 1 && now - _posHistory.Peek().t > VideoLatencySec) _posHistory.Dequeue();
        int lap = _run != null ? Mathf.Clamp(_run.Lap, 1, _meta.laps) : 1;
        _feed.Tick(now, _posHistory.Peek().p, lap);

        ReadBadge(now);
        LogFarewellState(now);
        TickExtraMarks(now, new Vector2(hp.x, hp.z));

        if (_ending) return;
        if (_run != null && _run.Phase == ShowPhase.Finished) _finishedFor += SimTime.Dt;
        if (_finishedFor > TailSec) End("finished");
        else if (now - _bootAt > HardLimitSec) End("timeout");
    }

    private void LateUpdate()
    {
        if (_rig == null || _head == null) return;
        // 歩く本体は XZ と向きだけを動かす。高さと両手はここで決める（人形の背丈は頭の高さから決まる）。
        Vector3 hp = _head.position;
        if (Mathf.Abs(hp.y - HeadHeightM) > 1e-3f) _rig.position += Vector3.up * (HeadHeightM - hp.y);
        Vector3 flat = _head.forward;
        flat.y = 0f;
        Quaternion yaw = flat.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(flat.normalized) : Quaternion.identity;
        if (_left != null) _left.SetPositionAndRotation(_head.position + yaw * new Vector3(-0.22f, -0.45f, 0.25f), yaw);
        if (_right != null) _right.SetPositionAndRotation(_head.position + yaw * new Vector3(0.28f, -0.5f, 0.1f), yaw);
    }

    /// <summary>表示中のテクスチャの右下の 3 マスから周の番号を読む。録画の取り違えはここに出る。</summary>
    private void ReadBadge(float now)
    {
        if (now - _badgeAt < 0.25f || _screen == null) return;
        _badgeAt = now;
        // 録画は素の映像とは別の層（_OverlayTex）に載る。混ざっている間はそちらを読む。
        if (_overlay == null) _overlay = FindObjectOfType<ScreenOverlayController>();
        Texture? rec = _overlay != null && _overlay.Strength > 0.5f ? _overlay.Current?.frames?.Texture : null;
        bool isRec = rec != null;
        Texture? tex = isRec ? rec : _screen.LiveTexture;
        if (tex == null || tex.width != 640 || tex.height != 480) return;
        if (_badgeRt == null)
        {
            _badgeRt = new RenderTexture(3, 1, 0, RenderTextureFormat.ARGB32) { filterMode = FilterMode.Bilinear };
            _badgeRead = new Texture2D(3, 1, TextureFormat.RGB24, false);
        }
        // 16px 四方のマスが 3 つ、右下の角に並ぶ（make_pack.py の burn_badge）。
        Graphics.Blit(tex, _badgeRt, new Vector2(48f / 640f, 16f / 480f), new Vector2(1f - 48f / 640f, 0f));
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = _badgeRt;
        _badgeRead!.ReadPixels(new Rect(0, 0, 3, 1), 0, 0, false);
        RenderTexture.active = prev;
        int value = 0;
        for (int i = 0; i < 3; i++)
            if (_badgeRead.GetPixel(i, 0).grayscale > 0.5f) value |= 1 << i;
        int segLap = -1, segCam = -1;
        _timeline?.TryGetCurrentSegment(out segLap, out segCam);
        // 区間が替わったときも 1 行出す。印と rec/live が前の区間と同じまま次の区間へ入ると
        // その区間の頭が記録されず、判定が後ろ側の印を読む（速い走行の 3-C で偽 FAIL になった）。
        if (value == _shownLap && isRec == _shownRec && segLap == _shownSegLap && segCam == _shownSegCam) return;
        // 同じ読みが 2 回（0.25 秒おき）続いてから出す。録画の頭の 1〜2 コマは切替の前に撮った画で、
        // 前の周の印が一瞬だけ映る。区間の替わり目で採ると、それを頭として読んで偽 FAIL になった（2026-11-11）。
        if (value != _pendingLap || isRec != _pendingRec || segLap != _pendingSegLap || segCam != _pendingSegCam)
        {
            _pendingLap = value; _pendingRec = isRec; _pendingSegLap = segLap; _pendingSegCam = segCam;
            return;
        }
        _shownLap = value;
        _shownRec = isRec;
        _shownSegLap = segLap;
        _shownSegCam = segCam;
        Debug.Log($"[XP] t={SimTime.Realtime:F2} ev=twinlap shown={value} src={(isRec ? "rec" : "live")} plap={(_run != null ? _run.Lap : -1)} seg={segLap}/{segCam}");
    }

    /// <summary>別れの写真を待っている間、何が揃っていないかを出す（写真が撮れないと走行は上限まで待つ）。</summary>
    private void LogFarewellState(float now)
    {
        if (_cg == null || !_cg.FarewellAnchored || _head == null || now < _farewellLogAt) return;
        _farewellLogAt = now + 3f;
        var a = _cg.FarewellAnchor;
        if (!_farewellShareLogged)
        {
            _farewellShareLogged = true;
            Debug.Log($"[XP] t={SimTime.Realtime:F2} ev=twinfarewell share0={_cg.FarewellStandShare(0f):F2} " +
                      $"share05={_cg.FarewellStandShare(0.05f):F2} share10={_cg.FarewellStandShare(0.10f):F2} " +
                      $"share20={_cg.FarewellStandShare(0.20f):F2}");
        }
        Vector3 hp = _head.position;
        float dist = new Vector2(hp.x - a.visitorTargetFoot.x, hp.z - a.visitorTargetFoot.z).magnitude;
        float want = EndingFarewellLogic.FaceYawDeg(new Vector3(hp.x, 0f, hp.z), a.foot);
        var doll = FindObjectOfType<EndingFarewellDoll>();
        var runner = FindObjectOfType<TakeRunner>();
        Debug.Log($"[Twin] 別れの写真 phase={(doll != null ? doll.Phase.ToString() : "-")} ready={_cg.FarewellPhotoReady} block={_cg.FarewellPhotoBlock} " +
                  $"captured={(doll != null && doll.ShotCaptured)} window={(runner != null ? runner.EndingShotWindow.ToString() : "-")} " +
                  $"releasedReady={(runner != null && runner.EndingReleasedLiveReady)} 立つ所まで={dist:F2}m " +
                  $"向きの差={Mathf.DeltaAngle(_head.eulerAngles.y, want):F0}度 cg={_cg.IsVisible}");
    }

    /// <summary>
    /// <c>marks=</c> で挙げた区間に入ったら、<see cref="ExtraMarkDelaySec"/> 後に左 X を 1 回長押しする。
    /// 既定の走行は 4-A の締めでしか押さないので、1〜3 周目の報告で起きる演出（手形が消える・幕がたなびく・
    /// 返事の文）が 1 度も画に出ない。それを撮るための走り方で、判定の既定は変えない。
    /// 区間は頭の位置のグリッドで決める（周は <see cref="ShowRunDirector.Lap"/>）。各区間 1 回だけ。
    /// </summary>
    private void TickExtraMarks(float now, Vector2 pos)
    {
        if (_markAt.Count == 0 || _noMark || _run == null || _show == null || _run.Phase != ShowPhase.Run) return;
        int cam = CamAt(_show.Layout, pos);
        if (cam < 0) return;
        string seg = $"{_run.Lap}{(char)('A' + cam)}";
        if (seg != _markSeg)
        {
            _markSeg = seg;
            _markSegSince = now;
            return;
        }
        if (ReportPressing || _show.IsVisitorMarkBlocked || _show.IsMarkTooEarly) return;
        // 区間名だけ = 入って 2.5 秒後。後ろに L = 録画のカットの後。@N = 入って N 秒後（例 3A@11）。
        float since = now - _markSegSince;
        string hit = "";
        foreach (string tok in _markAt)
        {
            if (!tok.StartsWith(seg, StringComparison.Ordinal) || _markDone.Contains(tok)) continue;
            float delay = tok.Length == 2 ? ExtraMarkDelaySec
                : tok[2] == 'L' ? LateMarkDelaySec
                : tok[2] == '@' && float.TryParse(tok.Substring(3), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float sec) ? sec : -1f;
            if (delay < 0f || since < delay) continue;
            hit = tok;
            break;
        }
        if (hit.Length == 0) return;
        _markDone.Add(hit);
        PressReport();
        Debug.Log($"[Twin] {seg} で左 X を長押しして報告する（区間に入って {now - _markSegSince:F1}s）");
    }

    private static int CamAt(ShowLayoutDef? layout, Vector2 p)
    {
        ShowGridDef? g = layout?.grid;
        if (g == null || !g.HasData()) return -1;
        int[,] map = WalkRouteLogic.BuildCells(g, out int rows, out int cols);
        int c = Mathf.FloorToInt((p.x + g.tileM * cols * .5f) / g.tileM);
        int r = Mathf.FloorToInt((g.tileM * rows * .5f - p.y) / g.tileM);
        return r >= 0 && r < rows && c >= 0 && c < cols ? map[r, c] : -1;
    }

    private void Note(string text)
    {
        _notes.Add(text);
        Debug.LogWarning("[Twin] " + text);
    }

    // ------------------------------------------------------------------ 同時撮影（IXpCaptureSource）

    /// <summary>体験の記録が始まったか（撮る時計の頭）。記録が始まる前は撮らない — 2 段の再生も記録の頭から撮る。</summary>
    public bool Started => _recT0 >= 0f;

    /// <summary>走行が終わった（<see cref="End"/>）。撮影は End の中で閉じるので、ここは閉じ損ねたときの保険。</summary>
    public bool Finished => _ending;

    /// <summary>記録の頭からの秒。再生の <c>XpReplayDriver.Tau</c>（記録の t）と同じ所が 0。</summary>
    public float Tau => _recT0 < 0f ? -1f : SimTime.Now - _recT0;

    /// <summary>頭のワールド姿勢。記録（<c>ExperienceRecorder</c>）が残すのと同じ Camera.main の姿勢。</summary>
    public void HeadPose(out Vector3 pos, out Quaternion rot)
    {
        ResolveRig();
        pos = _head != null ? _head.position : Vector3.zero;
        rot = _head != null ? _head.rotation : Quaternion.identity;
    }

    /// <summary>撮影が閉じた。走行は止めない（撮れなくても判定は出す）。結果は twin_run.json の eyeVideo に書く。</summary>
    public void OnCaptureDone(string result)
    {
        _captureResult = result;
        // 警告にしない（run_xp.log の W/ 行は判定が数える。撮影の失敗は twin-verify が eyeVideo で FAIL にする）。
        if (result.StartsWith("error", StringComparison.Ordinal)) _notes.Add("目線の動画: " + result);
        Debug.Log("[Twin] 目線の動画: " + result);
    }

    /// <summary>
    /// メインスレッドの [XP]。記録の頭の行で撮る時計を立て、以後の行を tau つきで replay_xp.log へ書く（再生と同じ形）。
    /// 頭は行が出た瞬間の unscaledTime ＝ <c>ExperienceRecorder</c> の session の t0（同じフレームの同じ時計）。
    /// ⚠ ログの受け口の中では Debug.Log しない（受け口へ入り直す）。知らせることは <see cref="ReportCaptureClock"/> が出す。
    /// </summary>
    private void OnMainLog(string msg, string stack, LogType type)
    {
        if (_capture == null || msg == null || !msg.StartsWith("[XP]", StringComparison.Ordinal)) return;
        if (_recT0 < 0f)
        {
            if (msg.IndexOf(" ev=xprec state=start", StringComparison.Ordinal) < 0) return;
            _recT0 = SimTime.Now;
            try { _tauLog = new StreamWriter(Path.Combine(_outDir, "replay_xp.log"), false, new System.Text.UTF8Encoding(false)); }
            catch (Exception e) { _tauLogError = e.Message; }
        }
        if (_tauLog != null) XpCaptureLogic.WriteTauLine(_tauLog, Tau, msg);
    }

    /// <summary>撮る時計が立ったことを 1 度だけ出す（Update から。ログの受け口の外）。</summary>
    private void ReportCaptureClock()
    {
        if (_recT0 < 0f || _recHeadLogged) return;
        _recHeadLogged = true;
        Debug.Log($"[Twin] 撮る時計の頭 = 記録の頭（unscaledTime {_recT0:F2}）");
        if (_tauLogError.Length == 0) return;
        _notes.Add("replay_xp.log を開けない: " + _tauLogError);
        Debug.Log("[Twin] replay_xp.log を開けない: " + _tauLogError);
    }

    private void CloseTauLog()
    {
        try { _tauLog?.Dispose(); } catch { }
        _tauLog = null;
    }

    // ------------------------------------------------------------------ 終わり

    private void End(string why)
    {
        _ending = true;
        // 同時撮影は Play を出る前に書き切る（ExitPlaymode の後は mp4 にできない）。動画の終わり = 走行の終わり。
        if (_capture != null)
        {
            _capture.Finish();
            if (_captureResult.Length == 0) _captureResult = "error: 撮影が結果を返さなかった";
        }
        CloseTauLog();
        ExperienceRecorder? rec = FindObjectOfType<ExperienceRecorder>();
        string? recDir = rec != null ? rec.FinishAndWait(why, 20000) : null;
        string result = why == "finished" && recDir != null ? "ok" : "error: " + (recDir == null ? "記録が取れていない" : why);
        var inv = CultureInfo.InvariantCulture;
        string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        string json = "{\n"
            + $"  \"result\": \"{Esc(result)}\",\n"
            + $"  \"recDir\": \"{Esc(recDir ?? "")}\",\n"
            + $"  \"sec\": {(SimTime.Now - _bootAt).ToString("F1", inv)},\n"
            + $"  \"clock\": \"{(_fast ? "fast" : "real")}\",\n"
            + $"  \"wallSec\": {(WallClock.Now - _wall0).ToString("F1", inv)},\n"
            + $"  \"walkSec\": {(_runT0 > 0f ? SimTime.Now - _runT0 : 0f).ToString("F1", inv)},\n"
            + $"  \"phase\": \"{(_run != null ? _run.Phase.ToString() : "-")}\",\n"
            + $"  \"lap\": {(_run != null ? _run.Lap : -1)},\n"
            + $"  \"marks\": {(_show != null ? _show.VisitorMarkCount : -1)},\n"
            + $"  \"feedFrames\": {_feed.Given},\n"
            + $"  \"feedMaxErrM\": {_feed.MaxErrM.ToString("F3", inv)},\n"
            + $"  \"feedOffRouteTicks\": {_feed.OffRouteTicks},\n"
            + $"  \"feedOnRouteTicks\": {_feed.OnRouteTicks},\n"
            + $"  \"exceptions\": {RuntimeHealth.ExceptionCount},\n"
            // 同時撮影の結果（mp4 のフルパスか "error: ..."）。撮っていなければ空。
            + $"  \"eyeVideo\": \"{Esc(_captureResult)}\",\n"
            + $"  \"notes\": [{string.Join(",", _notes.Select(n => "\"" + Esc(n) + "\""))}]\n"
            + "}\n";
        File.WriteAllText(Path.Combine(_outDir, "twin_run.json"), json, new System.Text.UTF8Encoding(false));
        Debug.Log($"[Twin] 走行が終わった: {result} 周={(_run != null ? _run.Lap : -1)} 報告={(_show != null ? _show.VisitorMarkCount : -1)} " +
                  $"記録={recDir} 道とのずれ最大={_feed.MaxErrM:F3}m");
        SessionState.SetString(KeyResult, result);
        SessionState.SetBool(KeyDone, true);
        EditorApplication.ExitPlaymode();
    }

    private void OnLog(string msg, string stack, LogType type)
    {
        if (msg == null) return;
        lock (_logLock)
        {
            if (_log == null) return;
            // 解析器は [XP] と logcat 形式の警告を読む。Editor のログもこの形で渡す。
            if (msg.StartsWith("[XP]", StringComparison.Ordinal)) _log.WriteLine(msg);
            // 同時撮影の行（XpReplayCapture・ffmpeg・音）は判定に入れない。2 段の走行には無い警告で、
            // 「ほかの警告が N 行」の数え方が変わる。撮れたかは twin-verify が eyeVideo と mp4 の有無で見る。
            else if (msg.StartsWith("[Replay]", StringComparison.Ordinal)) { }
            else if (type == LogType.Warning) _log.WriteLine("W/Unity (    1): " + msg.Replace("\n", " "));
            else if (type != LogType.Log) _log.WriteLine("E/Unity (    1): " + msg.Replace("\n", " "));
            // [ShowAssetResolver] override は twin-verify.py が「ツインの素材を読んだか」を数える。
            else if (msg.StartsWith("[Twin]", StringComparison.Ordinal) || msg.StartsWith("[XPWalk]", StringComparison.Ordinal)
                     || msg.StartsWith("[ShowAssetResolver] override", StringComparison.Ordinal))
                _log.WriteLine(msg);
        }
    }

    // ------------------------------------------------------------------ 配信の束

    /// <summary>
    /// 束（カメラごと周ごとの MJR）から、頭の位置に合うコマを fps どおりに配る。3 台で同じコマ番号を共有する。
    /// 道から外れていれば各ファイルの最後の 1 コマ（人の居ない写真）。
    /// </summary>
    private sealed class PackFeed : XpReplay.IFrameFeed, IDisposable
    {
        private sealed class File_
        {
            public FileStream fs = null!;
            public RecordedSegmentFormat.FrameRef[] index = Array.Empty<RecordedSegmentFormat.FrameRef>();
        }

        private const float OnRouteM = 0.15f;       // これより離れたら道の上ではない
        private const float ReacquireM = 0.08f;     // 見失ってから道へ戻ったとみなす距離
        private const int WindowFrames = 60;        // 前へ探す幅（15fps で 4 秒ぶん）

        private readonly PackMeta _m;
        private readonly Dictionary<string, File_[]> _files = new();
        private readonly Dictionary<string, long> _given = new();
        private long _step;
        private int _cursor = -1;
        private int _frame = -1;   // -1 は人の居ない写真
        private int _lap = 1;

        public int Given { get; private set; }
        public float MaxErrM { get; private set; }
        public int OffRouteTicks { get; private set; }
        public int OnRouteTicks { get; private set; }

        public PackFeed(string dir, PackMeta m)
        {
            _m = m;
            foreach (string cam in m.cams)
            {
                var laps = new File_[m.laps];
                for (int l = 0; l < m.laps; l++)
                {
                    string path = Path.Combine(dir, $"{cam}_L{l + 1}.mjr");
                    var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024);
                    RecordedSegmentFormat.FrameRef[] index = RecordedSegmentFormat.BuildIndex(fs);
                    if (index.Length != m.xs.Length + 1)
                        throw new InvalidDataException($"{path}: {index.Length} コマ（期待 {m.xs.Length + 1}）");
                    laps[l] = new File_ { fs = fs, index = index };
                }
                _files[cam] = laps;
                _given[cam] = -1;
            }
        }

        private float Dist(int i, Vector2 p)
            => Mathf.Sqrt((_m.xs[i] - p.x) * (_m.xs[i] - p.x) + (_m.zs[i] - p.y) * (_m.zs[i] - p.y));

        public void Tick(float now, Vector2 p, int lap)
        {
            long step = (long)(now * _m.fps);
            if (step == _step) return;
            _step = step;
            _lap = lap;

            // 前の位置から先だけを探す（道は同じ所を別の向きで通らないが、周の継ぎ目では近いコマが固まる）。
            int best = -1;
            float bestD = float.PositiveInfinity;
            if (_cursor >= 0)
            {
                for (int k = 0; k <= WindowFrames; k++)
                {
                    int i = (_cursor + k) % _m.moveEnd;
                    float d = Dist(i, p);
                    if (d < bestD) { bestD = d; best = i; }
                }
                if (bestD > OnRouteM) best = -1;
            }
            if (best < 0)
            {
                bestD = float.PositiveInfinity;
                for (int i = 0; i < _m.moveEnd; i++)
                {
                    float d = Dist(i, p);
                    if (d < bestD) { bestD = d; best = i; }
                }
                if (bestD > ReacquireM) best = -1;
            }
            _cursor = best >= 0 ? best : _cursor;
            _frame = best;
            if (best >= 0) { OnRouteTicks++; MaxErrM = Mathf.Max(MaxErrM, bestD); }
            else OffRouteTicks++;
        }

        private static string Key(string source)
        {
            string k = XpRecFormat.CamFileName(source);
            return k.Substring(XpRecFormat.CamPrefix.Length, k.Length - XpRecFormat.CamPrefix.Length - XpRecFormat.CamSuffix.Length);
        }

        public bool HasPending(string source)
            => _given.TryGetValue(Key(source), out long g) && g != _step;

        public bool TryConsume(string source, ref byte[]? buf, out int length, out long seq)
        {
            length = 0;
            seq = 0;
            string key = Key(source);
            if (!_files.TryGetValue(key, out File_[]? laps) || _given[key] == _step) return false;
            File_ f = laps[Mathf.Clamp(_lap - 1, 0, laps.Length - 1)];
            RecordedSegmentFormat.FrameRef r = f.index[_frame >= 0 ? _frame : f.index.Length - 1];
            if (buf == null || buf.Length < r.length) buf = new byte[Math.Max(r.length, 128 * 1024)];
            f.fs.Position = r.offset;
            int got = 0;
            while (got < r.length)
            {
                int n = f.fs.Read(buf, got, r.length - got);
                if (n <= 0) return false;
                got += n;
            }
            _given[key] = _step;
            Given++;
            length = r.length;
            seq = _step;
            return true;
        }

        public void Dispose()
        {
            foreach (File_[] laps in _files.Values)
                foreach (File_ f in laps) { try { f.fs.Dispose(); } catch { } }
            _files.Clear();
        }
    }
}
#endif
