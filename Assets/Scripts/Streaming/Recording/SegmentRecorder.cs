#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FixedCamVr.Streaming.Recording
{
    /// <summary>
    /// **1 周目の映像を端末内に録っておき、3 周目の演出で流す**ための録画係。
    ///
    /// 録画の単位は**区間**（<c>(lap, camera)</c>）で、駆動は<b>ショーの時計</b>
    /// （<see cref="CueScheduler.CameraEntered"/> = 体験者のゾーン進行）。
    /// 画面が何を映しているかとは無関係に録る — 演出が画面を横取りしている間も、
    /// その区間のカメラのライブを録り続ける。
    ///
    /// **区間の切れ目で録画は止まらない**（2026-08-14）。カメラが切り替わった後も、
    /// 出ていった区間のカメラを <c>record.postSec</c> 秒だけ録り続ける（追い録り）。
    /// 切り替えの瞬間で切ると**過去の自分が曲がり切る前に映像が終わる**ためで、
    /// 残るのは <c>[切替 - tailSec, 切替 + postSec]</c>。
    /// したがって切り替えの前後は**書き手が 2 本同時に走る**（現区間 + 1 つ前の追い録り）。
    /// 3 本目は作らない — 追い録りの最中にもう一度切り替わったら、古い方をその場で閉じる。
    ///
    /// 保存先は <c>Application.temporaryCachePath/rec/&lt;runEpoch&gt;/L&lt;lap&gt;C&lt;cam&gt;.mjr</c>。
    /// **ラン開始で前ランの録画を消す**（次の体験者に前の人の映像を出さない・端末に残さない）。
    ///
    /// 設計の正本: <c>.claude/plans/2026-07-26_show-sources-and-cg-layer.md</c> F2。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SegmentRecorder : MonoBehaviour
    {
        [Tooltip("区間進入（CameraEntered）の購読元。null なら同 GameObject → シーンから探す。")]
        [SerializeField] private CueScheduler? cueScheduler;

        [Tooltip("録画対象のカメラ受信ユニット。null なら同 GameObject → シーンから探す。")]
        [SerializeField] private CameraStreamRegistry? registry;

        [Tooltip("record 設定（show.json）の供給元。null なら同 GameObject → シーンから探す。")]
        [SerializeField] private ShowControlClient? showControl;

        /// <summary>いま開いている書き手 1 本ぶん。追い録り中のものも同じ形で持つ。</summary>
        private sealed class Segment
        {
            public SegmentRecordWriter writer = null!;
            public CameraStream stream = null!;
            public int lap;
            public int camera;
            public float startTime;      // Time.realtimeSinceStartup（pts の 0 点）
            public bool postRoll;        // true = 切り替え済み。postEndTime で閉じる
            public float postEndTime;
        }

        // 現区間（postRoll=false）は高々 1 本、追い録り（postRoll=true）も高々 1 本。
        private readonly List<Segment> _segments = new(2);
        // いま FrameTap を張っているストリーム（張り替えのたびに全部外してから張り直す）。
        private readonly List<CameraStream> _tapped = new(2);

        private int _runEpoch = -1;
        private bool _subscribed;

        // このランで書いた総バイト数（確定した区間の合計）。maxTotalMB は**ラン全体**の上限なので、
        // 区間ごとに使い切らせず残量を配る（旧実装は各区間へ満額を渡しており、区間数だけ端末を食えた）。
        private long _runBytes;
        private bool _budgetWarned;

        // 最後に開いた区間の (周, カメラ)。**閉じても消さない** — 録画は区間の切れ目で
        // 次が同一フレームに始まるので、消すと閉じた区間が何だったのか観測側から辿れない。
        private int _curLap = -1;
        private int _curCamera = -1;

        /// <summary>いま現区間を録画中か（HUD・診断用）。追い録りだけが走っている状態は含まない。</summary>
        public bool IsRecording
        {
            get
            {
                foreach (Segment s in _segments) if (!s.postRoll) return true;
                return false;
            }
        }

        /// <summary>切り替え後の追い録りが走っているか（診断用）。</summary>
        public bool IsPostRolling
        {
            get
            {
                foreach (Segment s in _segments) if (s.postRoll) return true;
                return false;
            }
        }

        /// <summary>このランで録画に使った総バイト数（診断用）。</summary>
        public long RunBytes => _runBytes;

        /// <summary>
        /// 最後に開いた区間の周（1 始まり・まだ 1 度も開いていなければ -1）。
        /// <b>画面に映っているカメラとは別物</b>（演出中は食い違う）。診断・テレメトリ用。
        /// </summary>
        public int CurrentLap => _curLap;

        /// <summary>最後に開いた区間の録画対象カメラ index（まだ開いていなければ -1）。</summary>
        public int CurrentCamera => _curCamera;

        // 最後に閉じた区間の実績。**バイト数だけでは「録れた」を判定できない**
        // （ヘッダだけの空ファイルでもバイト数は 0 にならない）ので、フレーム数を対で持つ。
        private int _lastFrames;
        private long _lastBytes;
        private int _lastLap = -1;
        private int _lastCamera = -1;
        private bool _lastStarted;

        /// <summary>最後に閉じた区間に書けたフレーム数（0 = 1 枚も録れていない）。</summary>
        public int LastSegmentFrames => _lastFrames;

        /// <summary>最後に閉じた区間に書けたバイト数。</summary>
        public long LastSegmentBytes => _lastBytes;

        /// <summary>最後に閉じた区間の周（まだ 1 本も閉じていなければ -1）。</summary>
        public int LastSegmentLap => _lastLap;

        /// <summary>最後に閉じた区間のカメラ index（まだ 1 本も閉じていなければ -1）。</summary>
        public int LastSegmentCamera => _lastCamera;

        /// <summary>
        /// 最後に閉じた区間が<b>録り始めの線</b>から録れたか（false = 末尾方式）。
        /// <b>枚数では区別できない</b>ので、線が効いたかの証拠はこれだけ。
        /// </summary>
        public bool LastSegmentStarted => _lastStarted;

        /// <summary>区間の録画を開いた <c>(lap, camera)</c>。</summary>
        public event Action<int, int>? SegmentOpened;

        /// <summary>
        /// 区間の録画を閉じた <c>(lap, camera, frames, bytes)</c>。
        /// **追い録りのぶんだけ切り替えより遅れて出る**ので、観測側は「開いた順」に閉じると思わないこと。
        /// ポーリングでは取り逃す（閉じた瞬間にしか実績が確定しない）ため、イベントで配る。
        /// </summary>
        public event Action<int, int, int, long>? SegmentClosed;

        private void Awake()
        {
            if (cueScheduler == null) cueScheduler = GetComponent<CueScheduler>();
            if (cueScheduler == null) cueScheduler = FindObjectOfType<CueScheduler>();
            if (registry == null) registry = GetComponent<CameraStreamRegistry>();
            if (registry == null) registry = FindObjectOfType<CameraStreamRegistry>();
            if (showControl == null) showControl = GetComponent<ShowControlClient>();
            if (showControl == null) showControl = FindObjectOfType<ShowControlClient>();
        }

        private void OnEnable()
        {
            if (cueScheduler != null && !_subscribed)
            {
                cueScheduler.CameraEntered += OnCameraEntered;
                _subscribed = true;
            }
        }

        private void OnDisable()
        {
            if (cueScheduler != null && _subscribed) cueScheduler.CameraEntered -= OnCameraEntered;
            _subscribed = false;
            StopAll();
        }

        private void OnDestroy() => StopAll();

        // 追い録りの期限は時計で見る（フレームが来なくなっても必ず閉じる）。
        // ⚠ ここに毎フレームのアロケーション（ラムダ・LINQ）を書かない（90Hz 維持）。
        private void Update()
        {
            if (_segments.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            TickStartLine(now);
            bool changed = false;
            for (int i = _segments.Count - 1; i >= 0; i--)
            {
                Segment s = _segments[i];
                if (!s.postRoll || now < s.postEndTime) continue;
                _segments.RemoveAt(i);
                changed = true;
                Close(s);
            }
            if (changed) RebindTaps();
        }

        /// <summary>
        /// ラン開始（体験者交代・卓の runEpoch 由来）。走行中の録画を閉じ、**端末に残っている録画を全部消す**。
        ///
        /// 現ランの epoch だけ残す実装だと、現地リセット（<see cref="ResetRunLocal"/> が epoch を自分で +1 する）で
        /// 進んだ番号に卓の runEpoch が後から追いつくと、前の体験者のファイルが「このランの録画」として
        /// 再生される。ラン開始時点でこのランの録画はまだ 1 本も無いので、全部消して困る場面は無い。
        /// </summary>
        public void ResetRun(int runEpoch)
        {
            StopAll();
            _runEpoch = runEpoch;
            _runBytes = 0;
            _budgetWarned = false;
            _curLap = -1;
            _curCamera = -1;
            PurgeAllRuns();
        }

        /// <summary>
        /// 現地のランリセット（右グリップ長押し）。卓が居ないので epoch は自分で 1 つ進める
        /// （0 に戻すと前の体験者の録画を上書きしつつ混ざる）。
        /// </summary>
        public void ResetRunLocal() => ResetRun(CurrentEpoch + 1);

        // ---- 録り始めの線（record.startLineId・`canon/LEDGER.md` 0061）----
        //
        // ⚠⚠ **自前の検出器を持つ。`TakeRunner` の線と共有しない。**
        //    向こうは「演出に線を使う台本があるときだけ」`LineCrossLogic` を回す
        //    （`_hasLineTakes`）ので、演出が線を使わない台本にした瞬間に**録画の起点が黙って
        //    消える**。録画の起点は著作の都合で消えてよいものではないので、ここで独立に回す。
        //    ⚠ 二重に数えているのは**別の線**（録画用に指した 1 本）で、同じ量ではない。
        private readonly LineCrossLogic _startLine = new();
        private string _startLineId = "";
        private int _startLineCamera = -1;
        private bool _startLineArmed;
        private float _lastLineNow;
        private bool _hasLastLineNow;
        private bool _warnedNoStartLine;

        /// <summary>
        /// 録り始めの線を毎フレーム見る。横切ったら、そのカメラで開いている区間の書き手へ
        /// <see cref="SegmentRecordWriter.MarkStart"/> を打つ。
        ///
        /// ⚠ 追い録り中の区間には打たない（もう切り替わっているので起点にならない）。
        /// </summary>
        private void TickStartLine(float now)
        {
            if (!_startLineArmed) return;
            Func<Vector2>? head = showControl != null ? showControl.HeadCourseXZProvider : null;
            if (head == null)
            {
                if (!_warnedNoStartLine)
                {
                    _warnedNoStartLine = true;
                    Debug.LogWarning("[SegmentRecorder] 体験者の位置が取れないので録り始めの線は効かない"
                                     + "（位置合わせが済んでいるか）。末尾方式のまま録る");
                }
                _hasLastLineNow = false;
                return;
            }

            float dt = _hasLastLineNow ? now - _lastLineNow : 0f;
            _lastLineNow = now;
            _hasLastLineNow = true;
            Vector2 xz = head();
            _startLine.Tick(now, xz.x, xz.y, dt);
            LineCrossLogic.State[] st = _startLine.StateView;
            if (st.Length == 0 || !st[0].crossed) return;

            for (int i = 0; i < _segments.Count; i++)
            {
                Segment s = _segments[i];
                if (s.postRoll || s.camera != _startLineCamera) continue;
                if (s.writer.HasStartMark) continue;
                s.writer.MarkStart(PtsOf(s, now));
                Debug.Log($"[SegmentRecorder] 録り始めの線を横切った line={_startLineId} "
                          + $"L{s.lap}C{s.camera} pos=({xz.x:F2},{xz.y:F2})");
            }
        }

        /// <summary>
        /// show.json の <c>record.startLineId</c> と <c>layout.lines</c> から検出器を組み直す。
        /// 区間を開くたびに呼ぶ（走行中に卓が線を動かしても追随する）。
        /// </summary>
        private void ApplyStartLine(ShowRecordDef? cfg)
        {
            string want = cfg != null && cfg.startLineId != null ? cfg.startLineId : "";
            ShowLineDef? found = null;
            if (!string.IsNullOrEmpty(want) && showControl != null && showControl.Layout != null
                && showControl.Layout.lines != null)
            {
                foreach (ShowLineDef? l in showControl.Layout.lines)
                {
                    if (l != null && l.id == want) { found = l; break; }
                }
            }

            if (found == null)
            {
                if (!string.IsNullOrEmpty(want) && _startLineId != want)
                {
                    // ⚠ **黙って末尾方式へ落ちない。** 「線を指したのに効いていない」は
                    //    録れた映像を見るまで分からない（しかも暗い現場では目で区別できない）。
                    Debug.LogWarning($"[SegmentRecorder] record.startLineId='{want}' が layout.lines に無い。"
                                     + "末尾方式で録る");
                }
                _startLineId = want;
                _startLineArmed = false;
                _startLineCamera = -1;
                _startLine.SetLines(Array.Empty<LineCrossLogic.Line>());
                return;
            }

            _startLineId = want;
            _startLineCamera = found.camera;
            int dir = LineCrossLogic.ParseDir(found.dir, out bool known);
            if (!known)
                Debug.LogWarning($"[SegmentRecorder] 未知の通過方向 '{found.dir}' → 両方向（line={want}）");
            _startLine.SetLines(new[]
            {
                LineCrossLogic.Line.Between(found.x1, found.z1, found.x2, found.z2, dir, found.camera),
            });
            _startLineArmed = true;
        }

        // ---- 録画 ----

        // ⚠ 読むのは **区間の周**（lap・逆走で戻る）。引き返して 1 周目 C に戻ったぶんは
        //   `L1C2.mjr` へ上書きする ＝ 3 周目に流れるのは「その場所で最後に自分がした動き」になる。
        //   進行の周で書くと、体験者が一度も達していない周の録画ができ、
        //   3 周目のカットが指す `recLap` と食い違って**黙って飛ぶ**。
        private void OnCameraEntered(int camera, int lap, int progressLap)
        {
            float now = Time.realtimeSinceStartup;
            ShowRecordDef? cfg = showControl != null ? showControl.RecordConfig : null;
            float postSec = cfg != null ? cfg.PostSec : SegmentRecordWriter.DefaultPostSec;

            // ① 走っていた追い録りをここで閉じる。同時に開くのは最大 2 本（現区間 + 1 つ前）で、
            //    追い録りの最中にもう一度切り替わったら古い方は諦める（録れたぶんはそのまま残る）。
            ClosePostRolls();

            // ② 出ていった区間を追い録りへ回す。**閉じない** — 過去の自分が曲がり切るまで録る。
            //    録らない設定（record OFF / 対象外の周）でも先にここを通す。そうしないと
            //    「3 周目に入った瞬間に 2 周目の録画が切れる」になる。
            foreach (Segment s in _segments)
            {
                if (s.postRoll) continue;
                s.postRoll = true;
                s.postEndTime = now + postSec;
                s.writer.BeginPostRoll(PtsOf(s, now), postSec);
            }

            // ③ 入った区間を開く。
            //    ⚠ 録り始めの線は**区間を開くたびに組み直す**（走行中に卓が線を動かしても追随する）。
            //      横断のラッチも落とす — 前の区間で踏んだ 1 回がこの区間の起点になってはいけない。
            ApplyStartLine(cfg);
            _startLine.Reset();
            _hasLastLineNow = false;
            if (cfg == null || !cfg.RecordsLap(lap)) { RebindTaps(); return; }

            // ⚠⚠ **一度録れた区間は録り直さない。**
            //   体験者が引き返すと区間キーは「前にそこに居たときの周」へ戻る
            //   （`LapCounterLogic.SegmentLap`）ので、同じ (周, カメラ) へ二度入りうる。
            //   `SegmentRecordWriter` は `FileMode.Create` で開くため、放っておくと
            //   **1 周目の映像が、引き返してすぐ出ていった数秒の断片に置き換わる**。
            //   3 周目に流すのはその映像 ＝ 作品の核なので、先に録れた方を守る。
            //   （どちらが良い素材かは機械には決められない。壊さない側へ倒す。）
            if (SegmentAlreadyRecorded(lap, camera))
            {
                Debug.Log($"[SegmentRecorder] 録画済みの区間へ戻ってきたので録り直さない lap={lap} camera={camera}");
                RebindTaps();
                return;
            }

            CameraStream? stream = registry != null ? registry.Get(camera) : null;
            if (stream == null) { RebindTaps(); return; }

            // ラン全体の残量を配る。まだ閉じていない追い録りのぶんは先に差し引く
            // （閉じるまで _runBytes に乗らないので、二重に配ると上限を越える）。
            long budget = (long)Mathf.Max(1, cfg.maxTotalMB) * 1024 * 1024;
            long reserved = 0;
            foreach (Segment s in _segments) reserved += s.writer.BufferedBytes;
            long remaining = budget - _runBytes - reserved;
            if (remaining <= RecordedSegmentFormat.HeaderBytes)
            {
                if (!_budgetWarned)
                {
                    _budgetWarned = true;
                    Debug.LogWarning($"[SegmentRecorder] ラン全体の録画容量 {cfg.maxTotalMB}MB を使い切った。以降の区間は録らない");
                }
                RebindTaps();
                return;
            }

            string path = SegmentPath(CurrentEpoch, lap, camera);
            var limits = new SegmentRecordWriter.Limits(remaining, cfg.fpsCap, cfg.TailSec);

            SegmentRecordWriter writer;
            try { writer = new SegmentRecordWriter(path, limits); }
            catch (Exception e)
            {
                Debug.LogWarning($"[SegmentRecorder] 録画を開始できない: {e.Message}");
                RebindTaps();
                return;
            }

            _segments.Add(new Segment
            {
                writer = writer,
                stream = stream,
                lap = lap,
                camera = camera,
                startTime = now,
            });
            _curLap = lap;
            _curCamera = camera;
            RebindTaps();
            Debug.Log($"[SegmentRecorder] 録画開始 lap={lap} camera={camera} " +
                      $"末尾{limits.tailSec:0.#}s + 切替後{postSec:0.#}s → {path}");
            SegmentOpened?.Invoke(lap, camera);
        }

        /// <summary>この区間の pts（区間先頭からの経過 ms）。</summary>
        private static int PtsOf(Segment s, float now) => Mathf.RoundToInt((now - s.startTime) * 1000f);

        // その (周, カメラ) を既に録ったか。**ファイルの実在で見る**（書き出しは区間を閉じるときなので、
        // 走行中の追い録りは自前のリストで見る）。ラン開始で録画ディレクトリごと消えるので、
        // 前の体験者のファイルを「録済み」と読むことはない。
        private bool SegmentAlreadyRecorded(int lap, int camera)
        {
            foreach (Segment s in _segments)
                if (s.lap == lap && s.camera == camera) return true;
            return !string.IsNullOrEmpty(ResolveRecorded(lap, camera));
        }

        /// <summary>走っている追い録りを全部閉じて実績を確定させる。閉じたら tap を張り直す。</summary>
        private void ClosePostRolls()
        {
            bool changed = false;
            for (int i = _segments.Count - 1; i >= 0; i--)
            {
                Segment s = _segments[i];
                if (!s.postRoll) continue;
                _segments.RemoveAt(i);
                changed = true;
                Close(s);
            }
            if (changed) RebindTaps();
        }

        private void StopAll()
        {
            foreach (CameraStream stream in _tapped) if (stream != null) stream.FrameTap = null;
            _tapped.Clear();
            for (int i = 0; i < _segments.Count; i++) Close(_segments[i]);
            _segments.Clear();
        }

        // 1 本を閉じてファイルを書き出し、実績（枚数・バイト数）を確定させる。
        private void Close(Segment s)
        {
            string path = s.writer.Path;
            s.writer.Dispose();          // 末尾を書き出す（この後で WrittenBytes / Capped が確定する）
            _lastBytes = s.writer.WrittenBytes;
            _lastFrames = s.writer.WrittenFrames;
            _lastLap = s.lap;
            _lastCamera = s.camera;
            _runBytes += s.writer.WrittenBytes;
            bool capped = s.writer.Capped;
            // 録り始めの線が効いたか。**録れた枚数だけでは区別できない**（末尾方式でも枚数は出る）。
            _lastStarted = s.writer.HasStartMark;
            Debug.Log($"[SegmentRecorder] 録画終了{(capped ? "（容量が足りず尺が縮んだ）" : "")} " +
                      $"lap={s.lap} camera={s.camera} frames={_lastFrames} bytes={_lastBytes} " +
                      $"起点={(_lastStarted ? "線" : "末尾")} → {path}");
            if (_lastFrames == 0)
                Debug.LogWarning($"[SegmentRecorder] 1 枚も録れていない lap={s.lap} camera={s.camera} " +
                                 $"— この区間を指す録画カットは無言で飛びます");
            SegmentClosed?.Invoke(s.lap, s.camera, _lastFrames, _lastBytes);
        }

        // 開いている区間ぶんの FrameTap を張り直す。**同じカメラに 2 本ぶら下がりうる**
        // （周をまたぐ同一カメラの区間）ので、ストリーム 1 つにつき 1 個の tap から全部へ配る。
        private void RebindTaps()
        {
            foreach (CameraStream stream in _tapped) if (stream != null) stream.FrameTap = null;
            _tapped.Clear();
            foreach (Segment s in _segments)
            {
                if (s.stream == null || _tapped.Contains(s.stream)) continue;
                CameraStream stream = s.stream;
                _tapped.Add(stream);
                stream.FrameTap = (buf, len) => Append(stream, buf, len);
            }
        }

        // 受信スレッドではなくメインスレッドから同期的に呼ばれる（CameraStream.FrameTap の契約）。
        private void Append(CameraStream stream, byte[] jpeg, int length)
        {
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < _segments.Count; i++)
            {
                Segment s = _segments[i];
                if (!ReferenceEquals(s.stream, stream)) continue;
                s.writer.TryAppend(jpeg, length, PtsOf(s, now));
            }
        }

        // ---- 参照（再生側）----

        private int CurrentEpoch => _runEpoch >= 0 ? _runEpoch : 0;

        /// <summary>録画ファイルのパス（存在するとは限らない）。</summary>
        public static string SegmentPath(int runEpoch, int lap, int camera)
            => Path.Combine(RunDir(runEpoch), $"L{lap}C{camera}.mjr");

        /// <summary>現在のランで録れている区間のパス。無ければ空文字。</summary>
        public string ResolveRecorded(int lap, int camera)
        {
            string p = SegmentPath(CurrentEpoch, lap, camera);
            return File.Exists(p) ? p : "";
        }

        private static string RootDir() => Path.Combine(Application.temporaryCachePath, "rec");

        private static string RunDir(int runEpoch) => Path.Combine(RootDir(), runEpoch.ToString());

        // 端末に残っている録画を全部消す。体験者の映像を端末に残さないための運用要件でもある。
        private static void PurgeAllRuns()
        {
            try
            {
                string root = RootDir();
                if (!Directory.Exists(root)) return;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
            catch (Exception e) { Debug.LogWarning($"[SegmentRecorder] 旧ランの掃除に失敗: {e.Message}"); }
        }
    }
}
