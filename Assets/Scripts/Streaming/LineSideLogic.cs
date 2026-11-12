#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 体験者が床の線（<c>layout.lines</c>）の<b>どちら側に居るか</b>を持つ純ロジック
    /// （UnityEngine 非依存）。カットの尺 <c>durKind:"untilBeyond"</c> がこれを読む。
    ///
    /// <see cref="LineCrossLogic"/> は「横切った」という<b>事象</b>を答える。こちらは「いま向こう側に居る」という
    /// <b>状態</b>を答える。事象で待つ <c>untilLine</c> は、カットが始まる前に越えていた人・位置が跳んで
    /// 横断が検出されなかった人を永久に待たせる（2026-11-12 のツイン走行 <c>posdrv-trail</c> で区間の終わりまで止まった）。
    /// 状態で待てば、カットの最初の Tick で終わる。
    ///
    /// 要点:
    ///   1. 側は線分を無限直線へ延ばした符号つき距離の符号（法線は <see cref="LineCrossLogic"/> と同じ A→B の左 90°）
    ///   2. 直線から <see cref="DeadbandM"/> 未満のあいだは前の側を保つ（未確定なら未確定のまま）
    ///   3. 「入ってきた側」は区間の確定（<see cref="CommitEntry"/>）で決め直す。基準は今の位置ではなく
    ///      <b>約 0.5m 手前に居た位置</b>（足跡の最古点）。区間の確定は dwell 0.5 秒の後なので、
    ///      入口に近い線は確定の時点で既に越えていることがある
    ///   4. 基準が不感帯の中なら未確定のまま置き、不感帯を出た最初の側を入ってきた側にする
    ///   5. <see cref="Beyond"/> = 入ってきた側が確定していて、いまその反対側に居る
    ///   6. 位置が取れないフレームは <see cref="Tick"/> を呼ばない（何も更新しない＝前の値のまま）
    /// </summary>
    public sealed class LineSideLogic
    {
        /// <summary>直線からこの距離 (m) 未満は側を変えない（トラッキングの揺れで反転させない）。</summary>
        public const float DeadbandM = 0.06f;

        /// <summary>足跡を 1 点打つ移動量 (m)。</summary>
        public const float TrailStepM = 0.25f;

        /// <summary>
        /// 足跡の点数（直近）。最古点が「約 0.5m 手前」＝ <see cref="TrailStepM"/> ×（点数 − 1）。
        /// 揃っていなければ入ってきた側の基準は今の位置になる。
        /// </summary>
        public const int TrailPoints = 3;

        /// <summary>1 フレームの移動がこれ (m) を超えたら跳び（recenter・テレポート）として足跡を捨てて数え直す。</summary>
        public const float MaxJumpM = 1.0f;

        /// <summary>線 1 本の状態。<see cref="TakeRunnerLogic.Tick"/> へそのまま渡す。</summary>
        public struct State
        {
            /// <summary>入ってきた側（-1 / +1 / 0 = 未確定）。</summary>
            public int entrySide;
            /// <summary>いまの側（-1 / +1 / 0 = 未確定）。</summary>
            public int side;
            /// <summary>入ってきた側の反対に居る（<see cref="Beyond"/> と同じ値）。</summary>
            public bool beyond;
            /// <summary>この線の担当カメラ（-1 = 未指定）。区間との照合に使う。</summary>
            public int camera;
        }

        private LineCrossLogic.Line[] _lines = Array.Empty<LineCrossLogic.Line>();
        private State[] _state = Array.Empty<State>();
        // 入ってきた側を、基準が不感帯の中だったので未確定のまま置いている線。
        private bool[] _entryPending = Array.Empty<bool>();

        // 直前のフレームの位置（跳びの判定と、入ってきた側の基準の最後の退避先）。
        private bool _hasPos;
        private float _posX, _posZ;

        // 足跡（古い順のリング）。_trailCount 点まで有効。
        private readonly float[] _trailX = new float[TrailPoints];
        private readonly float[] _trailZ = new float[TrailPoints];
        private int _trailHead;   // 次に書く位置
        private int _trailCount;

        /// <summary>線の数（スロット数）。</summary>
        public int Count => _lines.Length;

        /// <summary>
        /// 今の結果（スロット順）。<b>呼び出し側は書き換えないこと</b>
        /// （毎フレーム確保を避けるため内部配列をそのまま渡している）。
        /// </summary>
        public State[] StateView => _state;

        /// <summary>入ってきた側の反対に居るか。範囲外は false。</summary>
        public bool Beyond(int lineIndex)
            => lineIndex >= 0 && lineIndex < _state.Length && _state[lineIndex].beyond;

        /// <summary>入ってきた側（-1 / +1 / 0 = 未確定。範囲外は 0）。診断・テスト用。</summary>
        public int EntrySide(int lineIndex)
            => lineIndex >= 0 && lineIndex < _state.Length ? _state[lineIndex].entrySide : 0;

        /// <summary>いまの側（-1 / +1 / 0 = 未確定。範囲外は 0）。診断・テスト用。</summary>
        public int Side(int lineIndex)
            => lineIndex >= 0 && lineIndex < _state.Length ? _state[lineIndex].side : 0;

        /// <summary>
        /// 線を差し替える。<b>形が変わらない線は状態を保つ</b>（layout は走行中にも届くので、
        /// 同じ線のまま入ってきた側を落とすと、そのカットが区間の終わりまで止まる）。
        /// 形が変わった線・新しい線はいまの位置から側を取り直し、入ってきた側は未確定から始める
        /// （不感帯を出た最初の側が入ってきた側になる）。
        /// </summary>
        public void SetLines(LineCrossLogic.Line[] lines)
        {
            lines ??= Array.Empty<LineCrossLogic.Line>();
            var state = new State[lines.Length];
            var pending = new bool[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                if (i < _lines.Length && SameGeometry(_lines[i], lines[i]))
                {
                    state[i] = _state[i];
                    state[i].camera = lines[i].camera;
                    pending[i] = _entryPending[i];
                    continue;
                }
                state[i] = new State { camera = lines[i].camera };
                pending[i] = lines[i].defined;
                if (_hasPos) state[i].side = RawSide(lines[i], _posX, _posZ);
            }
            _lines = lines;
            _state = state;
            _entryPending = pending;
        }

        /// <summary>位置・足跡・側・入ってきた側をすべて落とす（ラン開始）。線はそのまま。</summary>
        public void Reset()
        {
            for (int i = 0; i < _state.Length; i++)
            {
                _state[i] = new State { camera = _lines[i].camera };
                _entryPending[i] = false;
            }
            _hasPos = false;
            ClearTrail();
        }

        /// <summary>
        /// 体験者の course 空間 XZ で 1 フレーム進める。<b>位置が取れないフレームは呼ばない</b>
        /// （呼ばなければ側も <see cref="Beyond"/> も前の値のまま）。
        /// </summary>
        public void Tick(float x, float z)
        {
            if (_hasPos)
            {
                float mx = x - _posX, mz = z - _posZ;
                if (mx * mx + mz * mz > MaxJumpM * MaxJumpM) ClearTrail();   // recenter / テレポート
            }
            _posX = x;
            _posZ = z;
            _hasPos = true;
            PushTrail(x, z);

            for (int i = 0; i < _state.Length; i++)
            {
                int raw = RawSide(_lines[i], x, z);
                if (raw != 0)
                {
                    _state[i].side = raw;
                    // 入ってきた側が未確定なら、不感帯を出た最初の側をそれにする。
                    if (_entryPending[i])
                    {
                        _state[i].entrySide = raw;
                        _entryPending[i] = false;
                    }
                }
                UpdateBeyond(i);
            }
        }

        /// <summary>
        /// 区間が確定した（体験者が新しい区間へ入った）。<b>全部の線の入ってきた側を決め直す</b>。
        /// 基準は足跡の最古点（約 0.5m 手前）、揃っていなければ今の位置。それが不感帯の中なら未確定のまま置き、
        /// 次の <see cref="Tick"/> 以降で不感帯を出た最初の側を入ってきた側にする。位置が一度も取れていなければ全部未確定。
        /// </summary>
        public void CommitEntry()
        {
            bool hasRef = _hasPos;
            float rx = _posX, rz = _posZ;
            if (_trailCount >= TrailPoints)
            {
                int oldest = (_trailHead - _trailCount + TrailPoints) % TrailPoints;
                rx = _trailX[oldest];
                rz = _trailZ[oldest];
            }
            for (int i = 0; i < _state.Length; i++)
            {
                int entry = hasRef ? RawSide(_lines[i], rx, rz) : 0;
                _state[i].entrySide = entry;
                // 未確定のまま置く。実体の無い線は側が決まらないので待たない（永久に未確定）。
                _entryPending[i] = entry == 0 && _lines[i].defined;
                UpdateBeyond(i);
            }
        }

        private void UpdateBeyond(int i)
            => _state[i].beyond = _state[i].entrySide != 0 && _state[i].side == -_state[i].entrySide;

        // 足跡を打つ。最後の点から TrailStepM 以上動いたときだけ（最初の 1 点は無条件）。
        private void PushTrail(float x, float z)
        {
            if (_trailCount > 0)
            {
                int last = (_trailHead - 1 + TrailPoints) % TrailPoints;
                float dx = x - _trailX[last], dz = z - _trailZ[last];
                if (dx * dx + dz * dz < TrailStepM * TrailStepM) return;
            }
            _trailX[_trailHead] = x;
            _trailZ[_trailHead] = z;
            _trailHead = (_trailHead + 1) % TrailPoints;
            if (_trailCount < TrailPoints) _trailCount++;
        }

        private void ClearTrail()
        {
            _trailHead = 0;
            _trailCount = 0;
        }

        // 直線からの符号つき距離で側を決める（-1 / +1）。不感帯の中・実体の無い線・短すぎる線は 0。
        private static int RawSide(LineCrossLogic.Line l, float x, float z)
        {
            if (!l.defined) return 0;
            float dx = l.bx - l.ax, dz = l.bz - l.az;
            float len = (float)Math.Sqrt(dx * dx + dz * dz);
            if (len < LineCrossLogic.MinLengthM) return 0;
            float d = ((x - l.ax) * dz - (z - l.az) * dx) / len;
            if (d >= DeadbandM) return 1;
            if (d <= -DeadbandM) return -1;
            return 0;
        }

        // 側の判定に効く形（端点と実体の有無）が同じか。向き・担当カメラは側に効かない。
        private static bool SameGeometry(LineCrossLogic.Line a, LineCrossLogic.Line b)
            => a.defined == b.defined && a.ax == b.ax && a.az == b.az && a.bx == b.bx && a.bz == b.bz;
    }
}
