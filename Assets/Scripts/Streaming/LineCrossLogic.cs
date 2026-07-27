#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 通過ライン（床に引いた線分）を体験者が**横切った瞬間**を検出する純ロジック
    /// （UnityEngine 非依存・時刻は dt で注入）。
    ///
    /// 「この位置に来たら」の当初案は円（エリア進入）だったが、体験の言葉は
    /// **「このラインを通過したら」**なので線分の横断に変えた（2026-07-27 仕様変更）。
    /// エリアは「入って居続ける状態」、ラインは「一度きりの事象」で、後者の方が
    /// 演出の開始規則（＝ちょうど 1 回発火する）と型が合う。
    ///
    /// 発火するかどうかを決めるのは <see cref="TakeRunnerLogic"/> で、ここは
    /// 「今フレーム、どのラインを、どちら向きに横切ったか」しか答えない。
    ///
    /// 契約の正本は <c>.claude/plans/2026-07-27_position-trigger.md</c> §3。要点:
    ///   1. 判定は**毎フレームの移動線分 × ライン線分の交差**（端の外を回り込んだら横切っていない）
    ///   2. 横切った直後は、線から <see cref="RearmMarginM"/> 離れるまで再検出しない
    ///      （線上での立ち止まり・トラッキング揺れで連発しないため）
    ///   3. dt が不連続（HMD 着脱・復帰）／1 フレームの移動が <see cref="MaxStepM"/> 超（recenter・テレポート）
    ///      なら、その間の横断は数えない（勝手に発火させない）
    ///   4. 高さ (y) は見ない
    ///   5. ラインは**担当カメラに紐づく**（<see cref="Line.camera"/>）。区間の照合は
    ///      <see cref="TakeRunnerLogic"/> が行う（別のゾーンでラインを踏んでも何も起きない）
    /// </summary>
    public sealed class LineCrossLogic
    {
        /// <summary>横切った後、再検出を許すまでに線から離れるべき距離 (m)。</summary>
        public const float RearmMarginM = 0.06f;

        /// <summary>連続と見なす dt の上限 (秒)。超えたらその間の横断は数えない。</summary>
        public const float MaxContinuousDtSec = 0.5f;

        /// <summary>1 フレームの移動量の上限 (m)。超えたら recenter / テレポート扱い。</summary>
        public const float MaxStepM = 1.0f;

        /// <summary>短すぎる線は誤検出のもとなので無効扱いにする下限 (m)。</summary>
        public const float MinLengthM = 0.05f;

        /// <summary>
        /// 横断を「まだ有効」とみなす猶予 (秒)。
        ///
        /// 演出の武装は**ゾーン確定**（時計）で起きるが、確定には dwell（既定 0.5s）が要る。
        /// ゾーンの入口すぐに引いた線は「入った瞬間に横切る → 0.5s 後に武装」となり、
        /// 事象を 1 フレームで消すと**永久に発火しない**（作者から見て沈黙するのが最悪）。
        /// そこで横断時刻を覚えておき、dwell + 少しの猶予の内なら due と認める。
        /// 担当カメラの照合（<see cref="State.camera"/>）は猶予中も効くので、
        /// 「別の領域のラインを踏んでも何も起こらない」は保たれる。
        /// </summary>
        public const float CrossLatchSec = 0.6f;

        // 通過方向の判別子（show.json の layout.lines[].dir）。
        /// <summary>どちら向きでも発火する（既定）。</summary>
        public const string DirBoth = "both";
        /// <summary>ラインの法線向き（卓の矢印の向き）に横切った時だけ発火する。</summary>
        public const string DirForward = "fwd";
        /// <summary>矢印の逆向きに横切った時だけ発火する。</summary>
        public const string DirBack = "back";

        /// <summary>dir 文字列 → 内部表現（0=両方向 / +1=法線向き / -1=逆向き）。未知は両方向へ倒す。</summary>
        public static int ParseDir(string? dir, out bool known)
        {
            known = dir == DirBoth || dir == DirForward || dir == DirBack;
            if (dir == DirForward) return 1;
            if (dir == DirBack) return -1;
            return 0;
        }

        /// <summary>通過ライン 1 本。<c>defined=false</c> は「id だけあって layout に実体が無い」枠。</summary>
        public struct Line
        {
            public float ax, az;    // 端点 A（course 空間）
            public float bx, bz;    // 端点 B
            public int dir;         // 0=両方向 / +1=法線向き / -1=逆向き
            public int camera;      // 担当カメラ index（-1 = 未指定）
            public bool defined;

            public static Line Between(float ax, float az, float bx, float bz, int dir, int camera)
                => new() { ax = ax, az = az, bx = bx, bz = bz, dir = dir, camera = camera, defined = true };

            /// <summary>実体の無い枠（未定義 lineId 用）。camera=-1 で「どの区間にも属さない」。</summary>
            public static Line Undefined => new() { camera = -1 };
        }

        /// <summary>ライン 1 本の状態。<see cref="TakeRunnerLogic.Tick"/> へそのまま渡す。</summary>
        public struct State
        {
            /// <summary>今フレーム、有効な向きで横切ったか（**事象**なので 1 フレームだけ true）。</summary>
            public bool crossed;
            /// <summary>
            /// 最後に有効な向きで横切った時刻 (秒)。未横断は <see cref="float.NegativeInfinity"/>。
            /// <see cref="CrossLatchSec"/> の猶予内なら演出の発火条件として有効。
            /// </summary>
            public float crossedAtSec;
            /// <summary>このラインの担当カメラ（-1 = 未指定）。区間との照合に使う。</summary>
            public int camera;
        }

        private Line[] _lines = Array.Empty<Line>();
        private State[] _state = Array.Empty<State>();
        private bool[] _suppressed = Array.Empty<bool>();

        private bool _hasPrev;
        private float _prevX, _prevZ;

        /// <summary>ラインの数（スロット数）。</summary>
        public int Count => _lines.Length;

        /// <summary>
        /// 今フレームの結果（スロット順）。**呼び出し側は書き換えないこと**
        /// （毎フレーム確保を避けるため内部配列をそのまま渡している）。
        /// </summary>
        public State[] StateView => _state;

        /// <summary>ラインを差し替える。検出状態はリセットする（線が動いたら数え直すのが正）。</summary>
        public void SetLines(Line[] lines)
        {
            _lines = lines ?? Array.Empty<Line>();
            _state = new State[_lines.Length];
            _suppressed = new bool[_lines.Length];
            Reset();
        }

        /// <summary>検出状態と直前位置を初期化する（ラン開始・位置が取れなくなった時）。</summary>
        public void Reset()
        {
            for (int i = 0; i < _state.Length; i++)
            {
                _state[i] = new State
                {
                    crossed = false,
                    crossedAtSec = float.NegativeInfinity,
                    camera = _lines.Length > i ? _lines[i].camera : -1,
                };
                _suppressed[i] = false;
            }
            _hasPrev = false;
        }

        /// <summary>指定スロットが今フレーム横切られたか（診断・テスト用）。</summary>
        public bool Crossed(int slot) => slot >= 0 && slot < _state.Length && _state[slot].crossed;

        /// <summary>
        /// 体験者の course 空間 XZ と経過時間で 1 フレーム進める。
        /// 直前位置との線分がラインと交差したフレームだけ <see cref="State.crossed"/> が立ち、
        /// その時刻が <see cref="State.crossedAtSec"/> に残る（<see cref="CrossLatchSec"/> の猶予用）。
        /// </summary>
        public void Tick(float now, float x, float z, float dt)
        {
            bool continuous = _hasPrev && dt > 0f && dt <= MaxContinuousDtSec;
            if (continuous)
            {
                float mx = x - _prevX, mz = z - _prevZ;
                if (mx * mx + mz * mz > MaxStepM * MaxStepM) continuous = false;   // recenter / テレポート
            }

            for (int i = 0; i < _lines.Length; i++)
            {
                Line l = _lines[i];
                bool crossed = false;
                float crossedAt = _state[i].crossedAtSec;

                if (l.defined && TryNormal(l, out float nx, out float nz))
                {
                    float side = (x - l.ax) * nx + (z - l.az) * nz;   // 正規化済み法線 → 符号つき距離
                    if (_suppressed[i])
                    {
                        // 線の近くに居るあいだは再検出しない（往復・揺れで連発させない）。
                        if (Abs(side) >= RearmMarginM) _suppressed[i] = false;
                    }
                    else if (continuous && Intersects(_prevX, _prevZ, x, z, l))
                    {
                        int sign = side >= 0f ? 1 : -1;               // 渡った先の側 = 通過の向き
                        crossed = l.dir == 0 || l.dir == sign;
                        if (crossed) crossedAt = now;
                        // 向きが合わなくても「物理的には横切った」のでヒステリシスは掛ける。
                        _suppressed[i] = true;
                    }
                }

                _state[i] = new State { crossed = crossed, crossedAtSec = crossedAt, camera = l.camera };
            }

            _prevX = x;
            _prevZ = z;
            _hasPrev = true;
        }

        // ラインの単位法線（A→B を左に 90° 回した向き）。短すぎる線は false。
        private static bool TryNormal(Line l, out float nx, out float nz)
        {
            float dx = l.bx - l.ax, dz = l.bz - l.az;
            float len = Sqrt(dx * dx + dz * dz);
            if (len < MinLengthM) { nx = 0f; nz = 0f; return false; }
            nx = dz / len;
            nz = -dx / len;
            return true;
        }

        // 移動線分 (p0→p1) と ライン線分 (A→B) が交差するか（端点を含む）。
        private static bool Intersects(float p0x, float p0z, float p1x, float p1z, Line l)
        {
            float rx = p1x - p0x, rz = p1z - p0z;
            float sx = l.bx - l.ax, sz = l.bz - l.az;
            float denom = rx * sz - rz * sx;
            if (Abs(denom) < 1e-9f) return false;    // 平行 / 移動なし

            float qx = l.ax - p0x, qz = l.az - p0z;
            float t = (qx * sz - qz * sx) / denom;   // 移動線分上の位置
            float u = (qx * rz - qz * rx) / denom;   // ライン上の位置
            return t >= 0f && t <= 1f && u >= 0f && u <= 1f;
        }

        private static float Abs(float v) => v < 0f ? -v : v;
        private static float Sqrt(float v) => (float)Math.Sqrt(v);
    }
}
