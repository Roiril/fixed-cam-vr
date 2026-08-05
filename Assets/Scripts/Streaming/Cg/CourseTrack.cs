#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming.Cg
{
    /// <summary>
    /// 体験者の course 上の足跡を時刻つきで貯め、**過去の時点を補間して取り出す**リングバッファ。
    ///
    /// なぜ要るか: 人形の腕と向きは <see cref="BodyInputHistory"/> 経由で
    /// 「映像の遅延ぶん過去」の体験者を読んでいるのに、**立ち位置だけ「いま」を読んでいた**。
    /// 歩きながら振り向くと、体の位置と向き・腕が別々の時刻を指す（1m/s で 15cm、90°/s で 13.5°）。
    /// 同じ時刻を読ませるためのもの。
    ///
    /// ⚠ 頭のワールド位置から course へ変換し直す手もあるが、course 変換は
    /// <see cref="ShowControlClient"/> が持つ剛体変換で、逆変換の口が無い。
    /// 変換は線形なので「変換してから補間」と「補間してから変換」は同じ値になる — だから
    /// **変換済みの値をそのまま貯める**のが最も少ない部品で済む。
    ///
    /// 純ロジック（時刻は呼び出し側が渡す）なので EditMode テストで固定できる。
    /// </summary>
    public sealed class CourseTrack
    {
        /// <summary>保持するサンプル数。90Hz で約 0.35 秒ぶん（想定遅延 0.15s の倍以上）。</summary>
        public const int Capacity = 32;

        private readonly float[] _times = new float[Capacity];
        private readonly Vector2[] _xz = new Vector2[Capacity];
        private int _count;
        private int _head;

        /// <summary>貯まっているサンプル数。</summary>
        public int Count => _count;

        /// <summary>履歴を捨てる（人形の表示開始・ランのリセット時）。</summary>
        public void Clear()
        {
            _count = 0;
            _head = 0;
        }

        /// <summary>1 サンプル積む。<paramref name="time"/> は単調増加であること（unscaledTime）。</summary>
        public void Push(float time, Vector2 xz)
        {
            _times[_head] = time;
            _xz[_head] = xz;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }

        /// <summary>
        /// <paramref name="time"/> 時点の位置を線形補間して返す。
        /// 履歴が空なら false（呼び出し側は「いま」の値へ落ちる）。
        /// 履歴の端より外なら端で頭打ち（外挿しない — 暴れる方が害が大きい）。
        /// </summary>
        public bool TrySample(float time, out Vector2 xz)
        {
            xz = default;
            if (_count == 0) return false;
            if (_count == 1) { xz = _xz[Index(0)]; return true; }

            float oldest = _times[Index(0)];
            float newest = _times[Index(_count - 1)];
            if (time <= oldest) { xz = _xz[Index(0)]; return true; }
            if (time >= newest) { xz = _xz[Index(_count - 1)]; return true; }

            for (int i = _count - 1; i > 0; i--)
            {
                float ta = _times[Index(i - 1)];
                if (time < ta) continue;
                float tb = _times[Index(i)];
                float span = tb - ta;
                float k = span > 1e-6f ? Mathf.Clamp01((time - ta) / span) : 0f;
                xz = Vector2.Lerp(_xz[Index(i - 1)], _xz[Index(i)], k);
                return true;
            }
            xz = _xz[Index(0)];
            return true;
        }

        private int Index(int logical) => ((_head - _count + logical) % Capacity + Capacity) % Capacity;
    }
}
