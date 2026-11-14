#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming.Cg
{
    /// <summary>
    /// 体験者が歩いた跡を<b>道のり</b>つきの折れ線として貯め、「いまから道のり N m 後ろの点」を引く。
    /// <c>cgMode:"trail"</c> の人形が、体験者の後ろを同じ道筋でついて来るために使う。
    ///
    /// 時刻ではなく道のりで引くのが要点。止まっている間は人形も止まり、折り返した道でも
    /// 「通ってきた順」に戻れる（直線距離で後ろを取ると、折り返した瞬間に人形が前へ出る）。
    ///
    /// 返す点は <c>stepM</c> ごとの飛び石（<c>station</c>）の上だけ。道のりを連続に追うと
    /// 人形が床を滑って見えるので、1 歩ぶんずつ飛ばす。
    ///
    /// 純ロジック（位置は呼び出し側が渡す）なので EditMode テストで固定できる。
    /// </summary>
    public sealed class FootprintTrail
    {
        /// <summary>保持する点の数。超えたら古い方から捨てる。</summary>
        public const int Capacity = 512;

        /// <summary>直前の点からこれ未満しか動いていなければ点を足さない (m)。足踏みと頭の揺れを跡にしない。</summary>
        public const float MinStepM = 0.05f;

        /// <summary>1 回でこれを超えて動いたら追跡の跳びとみなし、跡を捨てて数え直す (m)。</summary>
        public const float MaxJumpM = 1.0f;

        private readonly Vector2[] _xz = new Vector2[Capacity];
        // 各点までの道のり（通算）。古い点を捨てても値を詰め直さないので、station の番号が巻き戻らない。
        private readonly float[] _dist = new float[Capacity];
        private int _count;
        private int _head;
        private float _length;
        private int _epoch;

        /// <summary>貯まっている点の数。</summary>
        public int Count => _count;

        /// <summary>記録した道のりの合計 (m)。古い点を捨てた分も含む通算。</summary>
        public float Length => _length;

        /// <summary>
        /// 数え直した回数（<see cref="Reset"/> と跳び）。同じ station 番号でも、
        /// 数え直しを挟めば別の場所を指すので、呼び出し側はこれと組で見る。
        /// </summary>
        public int Epoch => _epoch;

        /// <summary>跡を捨てる（体験者の交代）。</summary>
        public void Reset()
        {
            _count = 0;
            _head = 0;
            _length = 0f;
            _epoch++;
        }

        /// <summary>
        /// 体験者の course XZ を 1 つ渡す。直前の点から <see cref="MinStepM"/> 以上動いたときだけ点を足す。
        /// <see cref="MaxJumpM"/> を超える移動は追跡の跳びとみなし、跡を捨ててこの点から数え直す。
        /// </summary>
        public void Add(Vector2 courseXz)
        {
            if (float.IsNaN(courseXz.x) || float.IsNaN(courseXz.y)
                || float.IsInfinity(courseXz.x) || float.IsInfinity(courseXz.y)) return;
            if (_count == 0)
            {
                Push(courseXz, _length);
                return;
            }
            float d = Vector2.Distance(_xz[Index(_count - 1)], courseXz);
            if (d > MaxJumpM)
            {
                Reset();
                Push(courseXz, 0f);
                return;
            }
            if (d < MinStepM) return;
            _length += d;
            Push(courseXz, _length);
        }

        /// <summary>
        /// 道のりで <paramref name="gapM"/> だけ後ろの飛び石を引く。
        /// 目標の道のり = <see cref="Length"/> − gapM。<c>station = floor(目標 / stepM)</c> で、
        /// 返す点は道のり <c>station * stepM</c> の位置（折れ線上を線形補間）。
        /// 目標が 0 未満（まだそこまで歩いていない）か、既に捨てた区間に入るなら false。
        /// </summary>
        public bool TryPointBehind(float gapM, float stepM, out Vector2 xz, out int station)
        {
            xz = default;
            station = -1;
            if (_count == 0 || !(stepM > 0f) || float.IsNaN(gapM)) return false;
            float target = _length - gapM;
            if (target < 0f) return false;
            // 割り算の丸めで 2.0 が 1.9999 になり、1 つ手前の飛び石へ落ちるのを防ぐ。
            int st = Mathf.FloorToInt(target / stepM + 1e-4f);
            float s = Mathf.Min(st * stepM, _length);
            float oldest = _dist[Index(0)];
            if (s < oldest - 1e-5f) return false;

            if (_count == 1)
            {
                xz = _xz[Index(0)];
                station = st;
                return true;
            }
            for (int i = _count - 1; i > 0; i--)
            {
                float da = _dist[Index(i - 1)];
                if (s < da) continue;
                float db = _dist[Index(i)];
                float span = db - da;
                float k = span > 1e-6f ? Mathf.Clamp01((s - da) / span) : 0f;
                xz = Vector2.Lerp(_xz[Index(i - 1)], _xz[Index(i)], k);
                station = st;
                return true;
            }
            xz = _xz[Index(0)];
            station = st;
            return true;
        }

        /// <summary>
        /// 道のり <paramref name="s"/> の位置を引く（折れ線上を線形補間）。<see cref="Length"/> を超えたら最新の点。
        /// 跡が空か、<paramref name="s"/> が既に捨てた区間（最古の点より前）にあれば false。
        /// </summary>
        public bool TryPointAt(float s, out Vector2 xz)
        {
            xz = default;
            if (_count == 0 || float.IsNaN(s) || float.IsInfinity(s)) return false;
            if (s < _dist[Index(0)] - 1e-5f) return false;
            if (_count == 1 || s >= _length)
            {
                xz = _xz[Index(_count - 1)];
                return true;
            }
            for (int i = _count - 1; i > 0; i--)
            {
                float da = _dist[Index(i - 1)];
                if (s < da) continue;
                float db = _dist[Index(i)];
                float span = db - da;
                float k = span > 1e-6f ? Mathf.Clamp01((s - da) / span) : 0f;
                xz = Vector2.Lerp(_xz[Index(i - 1)], _xz[Index(i)], k);
                return true;
            }
            xz = _xz[Index(0)];
            return true;
        }

        /// <summary>
        /// 貯まっている折れ線のうち <paramref name="p"/> にいちばん近い点を探し、その点の道のり（通算）と距離を返す。
        /// 線分の途中も候補に入れる（点の間を射影する）。跡が空か <paramref name="p"/> が有限でなければ false。
        /// 距離が同じなら新しい（道のりの長い）方を採る。
        /// </summary>
        public bool TryNearest(Vector2 p, out float distAlong, out float distM)
        {
            distAlong = 0f;
            distM = float.PositiveInfinity;
            if (_count == 0 || float.IsNaN(p.x) || float.IsNaN(p.y)
                || float.IsInfinity(p.x) || float.IsInfinity(p.y)) return false;
            if (_count == 1)
            {
                distAlong = _dist[Index(0)];
                distM = Vector2.Distance(_xz[Index(0)], p);
                return true;
            }
            for (int i = 1; i < _count; i++)
            {
                Vector2 a = _xz[Index(i - 1)];
                Vector2 b = _xz[Index(i)];
                Vector2 ab = b - a;
                float len2 = ab.sqrMagnitude;
                float k = len2 > 1e-12f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2) : 0f;
                float d = Vector2.Distance(a + ab * k, p);
                if (d > distM) continue;
                float da = _dist[Index(i - 1)];
                distAlong = da + (_dist[Index(i)] - da) * k;
                distM = d;
            }
            return true;
        }

        private void Push(Vector2 xz, float dist)
        {
            _xz[_head] = xz;
            _dist[_head] = dist;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }

        private int Index(int logical) => ((_head - _count + logical) % Capacity + Capacity) % Capacity;
    }
}
