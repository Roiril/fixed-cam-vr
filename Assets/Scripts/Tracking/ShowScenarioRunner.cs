#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using FixedCamVr.Streaming;

namespace FixedCamVr.Tracking
{
    /// <summary>
    /// 「体験者がこう歩いたら、ショーで何が起きるか」を **UnityEngine 非依存**で回して
    /// イベント列（トレース）を吐く純ロジック。実機なしでショーを検証するための正本
    /// （計画 <c>.claude/plans/2026-07-25_show-simulator.md</c> 段 S2）。
    ///
    /// Web 卓の <c>scenario-engine.js</c> が同じ入力から**同じトレース**を出すことをテストで固定する。
    /// これが無いと「卓で確認したのに実機で違う」という最悪の失敗を作るため、シミュレータの
    /// 価値はこの一致に懸かっている。
    ///
    /// **1 tick の評価順は本番の <see cref="CameraSwitchDirector"/> の因果と揃える**:
    ///   ① ゾーン判定 → 時計へ要求 ② 時計 Tick（ゾーン確定）③ 既定映し先を画面層へ
    ///   ④ 画面の commit 判定 ⑤ ゾーン確定の通知（周回 → 区間 → 演出の武装）⑥ 演出 Tick
    /// ⑤ を ④ の後に出すのは本番と同じ理由（先に通知すると演出の凍結が画面の切替を止めてしまう）。
    ///
    /// **モデル化しないもの**（実機でしか分からない領域・計画 §2）:
    ///   dip / フェードの実尺（切替は「決まった時刻」を記録する）、MJPEG の遅延・stall、
    ///   VR の体感、ライブ卓の手動操作。
    /// </summary>
    public static class ShowScenarioRunner
    {
        /// <summary>既定の刻み (ms)。実機は可変 fps だが判定は時刻差で書かれているので固定でよい。</summary>
        public const int DefaultTickMs = 20;

        /// <summary>体験者の位置サンプル（course 座標・時刻は ms）。</summary>
        public struct Sample
        {
            public int tMs;
            public float x, z;
        }

        /// <summary>シナリオの設定（ショーの静的な部分）。</summary>
        public sealed class Config
        {
            public ZonePickLogic.Box[] Zones = Array.Empty<ZonePickLogic.Box>();
            public float HysteresisShrink;
            public bool KeepLastWhenOutside = true;
            public float HeadY = 1.6f;

            public float DwellSec = ZoneProgressionLogic.DefaultDwellSec;
            public float CooldownSec = SwitchDirectorLogic.DefaultCooldownSec;

            public int[] CourseOrder = Array.Empty<int>();
            public TakeRunnerLogic.Def[] Takes = Array.Empty<TakeRunnerLogic.Def>();
            public string[] TakeIds = Array.Empty<string>();   // Takes と同じ並び（トレース表示用）

            /// <summary>
            /// 通過ライン（<c>layout.lines</c> 由来・スロット順）。
            /// <see cref="TakeRunnerLogic.Def.lineIndex"/> がこの配列を指す。
            /// </summary>
            public LineCrossLogic.Line[] Lines = Array.Empty<LineCrossLogic.Line>();

            /// <summary>
            /// 演出ごとの「カットが切り替えるカメラ」（Takes と同じ並び / カットごとに 1 要素）。
            /// live 以外のカット（inherit / clip / still）は -1。<see cref="TakeRunnerLogic.Def"/> は
            /// 尺しか持たないので、画面の見え方をトレースに載せるためにここで補う。
            /// </summary>
            public int[][]? StepCameras;

            public int StartCamera;
            public int TickMs = DefaultTickMs;
        }

        /// <summary>トレースの 1 イベント。JSON へ素直に落ちる平坦な形にする。</summary>
        public struct Event
        {
            public string kind;   // zone / lap / seg / take / step / end / screen
            public int t;         // ms
            public int a;         // zone,screen: camera / lap: lap / seg: lap / step: カット index
            public int b;         // seg: camera / step: カメラ（live 以外は -1）
            public string id;     // take/step/end: 演出 id / step: source
            public bool flag;     // end: forced（watchdog）

            public override string ToString()
                => $"{kind}@{t} a={a} b={b} id={id}{(flag ? " forced" : "")}";
        }

        /// <summary>シナリオを回してトレースを返す。</summary>
        public static List<Event> Run(Config cfg, Sample[] samples)
        {
            var trace = new List<Event>();
            if (samples == null || samples.Length == 0) return trace;

            int tick = cfg.TickMs > 0 ? cfg.TickMs : DefaultTickMs;

            var progress = new ZoneProgressionLogic();
            progress.Configure(cfg.DwellSec);
            progress.Reset(cfg.StartCamera);

            var screen = new SwitchDirectorLogic();
            screen.Configure(cfg.CooldownSec, 0f);
            screen.Reset(cfg.StartCamera);

            var lap = new LapCounterLogic();
            lap.SetOrder(cfg.CourseOrder);

            var takes = new TakeRunnerLogic();
            takes.SetDefs(cfg.Takes);

            // 「出ないまま終わった演出」をトレースへ出す（黙って消さない）。コールバックは
            // OnZoneCommitted の中で呼ばれるので、その tick の時刻を捕まえて差し込む。
            int dropAtMs = 0;
            takes.TakeDropped = (index, reason) => trace.Add(new Event
            {
                kind = "drop", t = dropAtMs,
                a = reason == TakeRunnerLogic.DropReason.ScreenBusyAtExit ? 0 : 1,
                b = -1, id = TakeId(cfg, index),
            });

            // 通過ライン（人の層）。歩きのサンプルから毎 tick 更新する。
            var lines = new LineCrossLogic();
            lines.SetLines(cfg.Lines);

            int zoneIndex = -1;                 // ZonePickLogic の直近選択
            int curCam = cfg.StartCamera;       // 時計が確定しているゾーンのカメラ
            bool hasSeg = false;
            int segLap = 1, segCam = cfg.StartCamera;

            int startMs = samples[0].tMs;
            int endMs = samples[samples.Length - 1].tMs;

            // ---- 起動時のシード（スタート区間を「進入した」ことにする）----
            // 体験者は最初からスタート領域に居るので、時計（ZoneProgressionLogic）は確定イベントを出さない。
            // 実機はこの穴を <see cref="LapCounter.SeedCurrentZone"/> が塞いでいる（現在ゾーンを初回進入として
            // CueScheduler → TimelineDirector → TakeRunner へ流す）。ここに同じシードが無いと
            // **1 周目スタート領域の演出（at=enter / at=line）が永久に武装されない** — 実機では出るのに
            // シミュレータだけ「何も起きない」と嘘をつくことになる（2026-07-27 実害: 1 周目の通過ラインが
            // 卓で沈黙した）。離脱時の決着（ifMissed=fireOnExit）も hasSeg=false のままだと働かない。
            trace.Add(new Event { kind = "seg", t = startMs, a = lap.CurrentLap, b = cfg.StartCamera, id = "" });
            Emit(trace, cfg, takes.OnZoneCommitted(lap.CurrentLap, cfg.StartCamera,
                hadPrev: false, prevLap: lap.CurrentLap, prevCam: cfg.StartCamera, now: startMs / 1000f), startMs);
            hasSeg = true;
            segLap = lap.CurrentLap;
            segCam = cfg.StartCamera;

            for (int tMs = startMs; tMs <= endMs; tMs += tick)
            {
                float now = tMs / 1000f;
                dropAtMs = tMs;
                SampleAt(samples, tMs, out float x, out float z);

                // ① ゾーン判定 → 時計へ要求（通過ラインも同じ「人の層」なのでここで進める）
                lines.Tick(now, x, z, tick / 1000f);
                zoneIndex = ZonePickLogic.Pick(cfg.Zones, x, cfg.HeadY, z,
                    zoneIndex, cfg.HysteresisShrink, cfg.KeepLastWhenOutside);
                if (zoneIndex >= 0) progress.Request(cfg.Zones[zoneIndex].CameraIndex, now);

                // ② 時計 Tick（ゾーン確定）
                bool zoneCommitted = progress.Tick(now, out int zoneCam);
                if (zoneCommitted)
                {
                    curCam = zoneCam;
                    screen.SetAmbient(zoneCam);   // ③ 既定映し先を画面層へ
                }

                // ④ 画面（凍結・クールダウンを通過したら commit）
                if (screen.Tick(now, out int shown))
                    trace.Add(new Event { kind = "screen", t = tMs, a = shown, b = -1, id = "" });

                // ⑤ ゾーン確定の通知（周回 → 区間 → 演出の武装）
                if (zoneCommitted)
                {
                    trace.Add(new Event { kind = "zone", t = tMs, a = zoneCam, b = -1, id = "" });

                    if (lap.Feed(zoneCam))
                        trace.Add(new Event { kind = "lap", t = tMs, a = lap.CurrentLap, b = -1, id = "" });

                    int newLap = lap.CurrentLap;
                    trace.Add(new Event { kind = "seg", t = tMs, a = newLap, b = zoneCam, id = "" });

                    TakeRunnerLogic.Decision d =
                        takes.OnZoneCommitted(newLap, zoneCam, hasSeg, segLap, segCam, now);
                    Emit(trace, cfg, d, tMs);

                    hasSeg = true;
                    segLap = newLap;
                    segCam = zoneCam;
                }

                // ⑥ 演出 Tick（カット進行 / 終了 / 発火・通過ラインの横断も見る）
                Emit(trace, cfg, takes.Tick(now, curCam, lines.StateView), tMs);

                // 演出が画面を占有しているかを画面層へ反映する（本番の insert 凍結と同じ役割）。
                screen.SetInsertActive(takes.IsActive);
            }
            return trace;
        }

        private static void Emit(List<Event> trace, Config cfg, TakeRunnerLogic.Decision d, int tMs)
        {
            switch (d.action)
            {
                case TakeRunnerLogic.Action.BeginStep:
                {
                    string id = TakeId(cfg, d.takeIndex);
                    if (d.takeStarted)
                        trace.Add(new Event { kind = "take", t = tMs, a = -1, b = -1, id = id });
                    trace.Add(new Event
                    {
                        kind = "step", t = tMs, a = d.stepIndex,
                        b = StepCamera(cfg, d.takeIndex, d.stepIndex),
                        id = id,
                    });
                    break;
                }
                case TakeRunnerLogic.Action.EndTake:
                    trace.Add(new Event
                    {
                        kind = "end", t = tMs, a = d.returnCamera, b = -1,
                        id = TakeId(cfg, d.takeIndex), flag = d.forced,
                    });
                    break;
            }
        }

        private static string TakeId(Config cfg, int index)
            => index >= 0 && index < cfg.TakeIds.Length && !string.IsNullOrEmpty(cfg.TakeIds[index])
                ? cfg.TakeIds[index]
                : $"#{index}";

        // カットが切り替えるカメラ。Def は尺しか持たないので、呼び出し側が用意する StepCameras を引く。
        // 未設定なら -1（＝カメラを動かさないカット）。
        private static int StepCamera(Config cfg, int takeIndex, int stepIndex)
        {
            if (cfg.StepCameras == null) return -1;
            if (takeIndex < 0 || takeIndex >= cfg.StepCameras.Length) return -1;
            int[]? cams = cfg.StepCameras[takeIndex];
            if (cams == null || stepIndex < 0 || stepIndex >= cams.Length) return -1;
            return cams[stepIndex];
        }

        // 指定時刻の位置を線形補間で求める（サンプル間は等速で歩いたとみなす）。
        private static void SampleAt(Sample[] s, int tMs, out float x, out float z)
        {
            if (tMs <= s[0].tMs) { x = s[0].x; z = s[0].z; return; }
            int last = s.Length - 1;
            if (tMs >= s[last].tMs) { x = s[last].x; z = s[last].z; return; }
            for (int i = 1; i <= last; i++)
            {
                if (s[i].tMs < tMs) continue;
                int span = s[i].tMs - s[i - 1].tMs;
                float u = span <= 0 ? 0f : (tMs - s[i - 1].tMs) / (float)span;
                x = s[i - 1].x + (s[i].x - s[i - 1].x) * u;
                z = s[i - 1].z + (s[i].z - s[i - 1].z) * u;
                return;
            }
            x = s[last].x; z = s[last].z;
        }

        /// <summary>トレースを JSON 配列文字列へ（fixture 用・両側で同じ形）。</summary>
        public static string ToJson(List<Event> trace)
        {
            var sb = new StringBuilder("[\n");
            for (int i = 0; i < trace.Count; i++)
            {
                Event e = trace[i];
                sb.Append("  {\"kind\":\"").Append(e.kind).Append("\",\"t\":").Append(e.t)
                  .Append(",\"a\":").Append(e.a).Append(",\"b\":").Append(e.b)
                  .Append(",\"id\":\"").Append(e.id).Append("\",\"flag\":")
                  .Append(e.flag ? "true" : "false").Append('}');
                if (i < trace.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            return sb.Append("]\n").ToString();
        }
    }
}
