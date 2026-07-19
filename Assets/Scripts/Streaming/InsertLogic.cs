#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// インサートショット（タイムライン区間から別カメラを一時的に差し込む演出）の純判定・計時ロジック。
    /// MonoBehaviour（<see cref="InsertController"/>）から分離して EditMode テスト可能にする。時刻・最新ゾーンは引数で受ける。
    ///
    /// アンカー（計画 2026-07-19_webui-timeline-authoring.md）:
    ///   - enter: 区間 (lap,camera) へ進入 + delaySec 後に insertCamera を durationSec 表示 → 最新ゾーンへ復帰
    ///   - exit : 区間 (lap,camera) から Zone 切替で離脱する瞬間（dip 黒中）に insertCamera へ差し替え、
    ///            durationSec 表示 → 最新ゾーンへ復帰。exit は <see cref="OnZoneCommitted"/> で即時発火する
    /// once=ラン内 1 回（<see cref="ResetRun"/> でクリア）。suppressed（activeCue 非空 / cameraOverride 非 null）中は発火しない。
    /// 同時に走るインサートは 1 本のみ（区間複数 insert は将来課題）。
    ///
    /// post / cueId の実適用は <see cref="InsertController"/> 側。ここは「どの def を・いつ・どのカメラで」だけを決める。
    /// </summary>
    public sealed class InsertLogic
    {
        /// <summary>インサート 1 本の定義。区間キー (lap,camera) と差し込み先・タイミング。</summary>
        public struct Def
        {
            public int lap;
            public int camera;        // 区間キーのカメラ
            public bool onExit;       // true=exit アンカー / false=enter アンカー
            public int insertCamera;  // 差し込むカメラ index
            public float delaySec;    // enter のみ（進入からの遅延）
            public float durationSec; // 表示秒数
            public string cueId;      // "" = cue なし
            public bool once;
        }

        public enum Action { None, BeginExit, BeginEnter, End }

        /// <summary>1 回の評価結果。action=None なら他フィールドは無効。</summary>
        public struct Decision
        {
            public Action action;
            public int defIndex;      // 発火した def（controller が post を引く）
            public int camera;        // Begin: insertCamera / End: 復帰カメラ
            public string cueId;      // Begin 時のみ
        }

        private enum Phase { Idle, EnterDelay, Showing }

        private Def[] _defs = Array.Empty<Def>();
        private bool[] _fired = Array.Empty<bool>();
        private Phase _phase = Phase.Idle;
        private int _activeIndex = -1;
        private float _timerEnd;
        private int _baseZoneCam;    // 復帰先のフォールバック（インサート開始時に入っていたゾーンカメラ）
        private bool _suppressed;

        /// <summary>インサート進行中（delay 待ち or 表示中）か。</summary>
        public bool IsActive => _phase != Phase.Idle;

        /// <summary>進行中インサートの def index（テスト・診断用。非進行時は -1）。</summary>
        public int ActiveDefIndex => _activeIndex;

        /// <summary>復帰先のフォールバック（インサート開始時のゾーンカメラ）。controller が最新ゾーン算出のベースに使う。</summary>
        public int BaseZoneCamera => _baseZoneCam;

        /// <summary>定義を差し替える。once の発火済みフラグと進行はリセットする（新ラン相当）。</summary>
        public void SetDefs(Def[] defs)
        {
            _defs = defs ?? Array.Empty<Def>();
            _fired = new bool[_defs.Length];
            _phase = Phase.Idle;
            _activeIndex = -1;
        }

        /// <summary>体験の明示リセット（once 発火済みと進行を全消去）。定義は保持。</summary>
        public void ResetRun()
        {
            for (int i = 0; i < _fired.Length; i++) _fired[i] = false;
            _phase = Phase.Idle;
            _activeIndex = -1;
        }

        /// <summary>ライブ抑止（activeCue 非空 / cameraOverride 非 null）を通知する。抑止中は新規発火しない。</summary>
        public void SetSuppressed(bool s) => _suppressed = s;

        /// <summary>
        /// ゾーン確定（LapCounter 由来の deterministic post-Feed lap）を受ける。
        /// 離脱区間 (prev) の exit インサートを即時発火し、進入区間 (new) の enter インサートを武装する。
        /// 返すのは最大 1 つの即時 Decision（BeginExit）。enter は <see cref="Tick"/> が delay 後に返す。
        /// 既にインサート進行中なら新規武装しない（同時 1 本・seed 再入も透過）。
        /// </summary>
        public Decision OnZoneCommitted(int newLap, int newCam, bool hadPrev, int prevLap, int prevCam, float now)
        {
            if (_phase != Phase.Idle) return default;

            // 1) 離脱区間の exit インサート（dip 黒中に即差し替え）。
            if (hadPrev && !_suppressed && !(prevLap == newLap && prevCam == newCam))
            {
                int ei = FindDef(prevLap, prevCam, onExit: true);
                if (ei >= 0)
                {
                    _phase = Phase.Showing;
                    _activeIndex = ei;
                    _timerEnd = now + _defs[ei].durationSec;
                    _baseZoneCam = newCam;   // 復帰先の既定 = 実際に入ったゾーン
                    _fired[ei] = true;
                    return new Decision
                    {
                        action = Action.BeginExit,
                        defIndex = ei,
                        camera = _defs[ei].insertCamera,
                        cueId = _defs[ei].cueId,
                    };
                }
            }

            // 2) 進入区間の enter インサートを武装（delaySec 後に Tick で発火）。
            if (!_suppressed)
            {
                int ni = FindDef(newLap, newCam, onExit: false);
                if (ni >= 0)
                {
                    _phase = Phase.EnterDelay;
                    _activeIndex = ni;
                    _timerEnd = now + Math.Max(0f, _defs[ni].delaySec);
                    _baseZoneCam = newCam;
                }
            }
            return default;
        }

        /// <summary>
        /// 毎フレーム評価。enter の delay 満了で BeginEnter、表示中の duration 満了で End（復帰先 = latestZoneCam）。
        /// delay 待ち中に抑止が入ったらキャンセルする（表示中の抑止は完了させる）。
        /// </summary>
        public Decision Tick(float now, int latestZoneCam)
        {
            switch (_phase)
            {
                case Phase.EnterDelay:
                    if (_suppressed) { _phase = Phase.Idle; _activeIndex = -1; return default; }
                    if (now < _timerEnd) return default;
                    {
                        int i = _activeIndex;
                        _phase = Phase.Showing;
                        _timerEnd = now + _defs[i].durationSec;
                        _fired[i] = true;
                        return new Decision
                        {
                            action = Action.BeginEnter,
                            defIndex = i,
                            camera = _defs[i].insertCamera,
                            cueId = _defs[i].cueId,
                        };
                    }
                case Phase.Showing:
                    if (now < _timerEnd) return default;
                    _phase = Phase.Idle;
                    _activeIndex = -1;
                    return new Decision { action = Action.End, camera = latestZoneCam };
                default:
                    return default;
            }
        }

        private int FindDef(int lap, int camera, bool onExit)
        {
            for (int i = 0; i < _defs.Length; i++)
            {
                Def d = _defs[i];
                if (d.lap != lap || d.camera != camera || d.onExit != onExit) continue;
                if (d.once && _fired[i]) continue;
                return i;
            }
            return -1;
        }
    }
}
