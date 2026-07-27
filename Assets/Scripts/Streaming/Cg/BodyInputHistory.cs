#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming.Cg
{
    /// <summary>
    /// 体験者の身体入力を時刻つきで貯め、**過去の時点を補間して取り出す**リングバッファ。
    ///
    /// なぜ要るか: スクリーンに映っている実写は撮影から 100〜200ms 前の姿。
    /// CG 人形を「いま」の体験者の位置に描くと、歩いているあいだずっと先行してずれる
    /// （1m/s なら 15〜20cm）。**映像の遅延ぶんだけ過去を読めば映像と揃う**。
    /// 立ち止まっているときは差が出ないので、この補償は「歩いている最中だけ効く」。
    ///
    /// 補間の方針:
    ///   - 位置は線形、頭の向きは <see cref="Mathf.LerpAngle"/>（±180° をまたいでも回り込まない）
    ///   - **有効フラグ（手が取れているか）は補間しない** — 近い方のサンプルをそのまま採る。
    ///     取れ / 取れないの境界を混ぜると、無効な手の座標（0,0,0）へ引っ張られて腕が飛ぶ
    ///   - 履歴より古い時刻・新しい時刻を要求されたら端で頭打ち（外挿はしない。暴れる方が害が大きい）
    ///
    /// 純ロジック（時刻は呼び出し側が渡す）なので EditMode テストで固定できる。
    /// </summary>
    public sealed class BodyInputHistory
    {
        /// <summary>保持するサンプル数。90Hz で約 0.35 秒ぶん — 想定遅延 0.15s の倍以上あればよい。</summary>
        public const int Capacity = 32;

        private readonly float[] _times = new float[Capacity];
        private readonly ShowBodyInput[] _samples = new ShowBodyInput[Capacity];
        private int _count;
        private int _head;   // 次に書く位置

        /// <summary>貯まっているサンプル数。</summary>
        public int Count => _count;

        /// <summary>履歴を捨てる（人形の表示開始・ランのリセット時）。</summary>
        public void Clear()
        {
            _count = 0;
            _head = 0;
        }

        /// <summary>1 サンプル積む。<paramref name="time"/> は単調増加であること（unscaledTime）。</summary>
        public void Push(float time, in ShowBodyInput body)
        {
            _times[_head] = time;
            _samples[_head] = body;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }

        /// <summary>直近サンプルの時刻。空なら float.NegativeInfinity。</summary>
        public float LatestTime => _count > 0 ? _times[Index(_count - 1)] : float.NegativeInfinity;

        /// <summary>
        /// <paramref name="time"/> 時点の身体入力を補間して返す。空なら <see cref="ShowBodyInput.None"/>。
        /// </summary>
        public ShowBodyInput Sample(float time)
        {
            if (_count == 0) return ShowBodyInput.None;
            if (_count == 1) return _samples[Index(0)];

            float oldest = _times[Index(0)];
            float newest = _times[Index(_count - 1)];
            if (time <= oldest) return _samples[Index(0)];
            if (time >= newest) return _samples[Index(_count - 1)];

            // 新しい側から探す（要求は普通「少し前」なので後ろの方が当たりやすい）。
            for (int i = _count - 1; i > 0; i--)
            {
                float tb = _times[Index(i)];
                float ta = _times[Index(i - 1)];
                if (time < ta) continue;
                float span = tb - ta;
                float k = span > 1e-6f ? Mathf.Clamp01((time - ta) / span) : 0f;
                return Lerp(_samples[Index(i - 1)], _samples[Index(i)], k);
            }
            return _samples[Index(0)];
        }

        private int Index(int logical) => ((_head - _count + logical) % Capacity + Capacity) % Capacity;

        /// <summary>
        /// 2 サンプルの補間。**有効フラグは近い方を採る**（混ぜると無効な座標へ引っ張られて腕が飛ぶ）。
        /// </summary>
        public static ShowBodyInput Lerp(in ShowBodyInput a, in ShowBodyInput b, float k)
        {
            bool nearB = k >= 0.5f;

            bool hasHead = a.HasHead && b.HasHead;
            Vector3 head = hasHead ? Vector3.Lerp(a.HeadPos, b.HeadPos, k) : (nearB ? b.HeadPos : a.HeadPos);
            if (!hasHead) hasHead = nearB ? b.HasHead : a.HasHead;
            float yaw = (a.HasHead && b.HasHead)
                ? Mathf.LerpAngle(a.HeadYawDeg, b.HeadYawDeg, k)
                : (nearB ? b.HeadYawDeg : a.HeadYawDeg);

            bool leftValid = a.LeftValid && b.LeftValid;
            Vector3 left = leftValid ? Vector3.Lerp(a.LeftHandPos, b.LeftHandPos, k)
                                     : (nearB ? b.LeftHandPos : a.LeftHandPos);
            if (!leftValid) leftValid = nearB ? b.LeftValid : a.LeftValid;

            bool rightValid = a.RightValid && b.RightValid;
            Vector3 right = rightValid ? Vector3.Lerp(a.RightHandPos, b.RightHandPos, k)
                                       : (nearB ? b.RightHandPos : a.RightHandPos);
            if (!rightValid) rightValid = nearB ? b.RightValid : a.RightValid;

            return new ShowBodyInput(hasHead, head, yaw, leftValid, left, rightValid, right);
        }
    }
}
