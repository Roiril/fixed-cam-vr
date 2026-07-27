#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 位置トリガー（床の円）の内外判定と滞在計時。「体験者がこの位置に来たら演出を始める」の
    /// **場所の部分**だけを持つ純ロジック（UnityEngine 非依存・時刻は dt で注入）。
    ///
    /// 発火するかどうかを決めるのは <see cref="TakeRunnerLogic"/> 側で、ここは
    /// 「いまどの円の中に、何秒連続で居るか」しか答えない（人の層と演出の層を混ぜない）。
    ///
    /// 契約の正本は <c>.claude/plans/2026-07-27_position-trigger.md</c> §3.3。要点:
    ///   1. 入る = <c>dist &lt;= rM</c> / 出る = <c>dist &gt; rM + <see cref="ExitMarginM"/></c>（出のヒステリシス）
    ///   2. 滞在秒は**連続**（円を出たら 0 に戻る）
    ///   3. 高さ (y) は見ない（しゃがみ・身長で発火が変わると現場で説明できない）
    ///   4. dt が不連続（&lt;=0 / <see cref="MaxContinuousDtSec"/> 超）なら滞在計時をやり直す
    ///      — HMD 着脱・アプリ復帰で「ずっと立っていた」と誤認しないため
    /// </summary>
    public sealed class SpotTriggerLogic
    {
        /// <summary>円の既定半径 (m)。回廊幅 0.45m に対して歩いて確実に踏める大きさ。</summary>
        public const float DefaultRadiusM = 0.25f;

        /// <summary>出る側のヒステリシス (m)。縁のトラッキング揺れで滞在計時が 0 に戻り続けるのを防ぐ。</summary>
        public const float ExitMarginM = 0.08f;

        /// <summary>半径の下限 (m)。0 や負値の円を「絶対に踏めない点」にしない。</summary>
        public const float MinRadiusM = 0.05f;

        /// <summary>連続と見なす dt の上限 (秒)。これを超えたら滞在計時はやり直す。</summary>
        public const float MaxContinuousDtSec = 0.5f;

        /// <summary>円 1 つ。<c>defined=false</c> は「id だけあって layout に実体が無い」枠（常に外）。</summary>
        public struct Spot
        {
            public float x;
            public float z;
            public float rM;
            public bool defined;

            public static Spot At(float x, float z, float rM)
                => new() { x = x, z = z, rM = rM, defined = true };

            /// <summary>実体の無い枠（未定義 spotId 用）。</summary>
            public static Spot Undefined => default;
        }

        /// <summary>円 1 つの現在状態。<see cref="TakeRunnerLogic.Tick"/> へそのまま渡す。</summary>
        public struct State
        {
            /// <summary>いま円の中に居るか（ヒステリシス適用後）。</summary>
            public bool inside;
            /// <summary>連続で円の中に居る秒数（入った瞬間は 0）。</summary>
            public float insideSec;
        }

        private Spot[] _spots = Array.Empty<Spot>();
        private State[] _state = Array.Empty<State>();

        /// <summary>円の数（スロット数）。</summary>
        public int Count => _spots.Length;

        /// <summary>
        /// 現在状態の配列（スロット順）。**呼び出し側は書き換えないこと**
        /// （毎フレーム確保を避けるため内部配列をそのまま渡している）。
        /// </summary>
        public State[] StateView => _state;

        /// <summary>円を差し替える。滞在計時は全てリセットする（円が動いたら計り直すのが正）。</summary>
        public void SetSpots(Spot[] spots)
        {
            _spots = spots ?? Array.Empty<Spot>();
            _state = new State[_spots.Length];
        }

        /// <summary>滞在計時と内外状態を初期化する（ラン開始・位置が取れなくなった時）。</summary>
        public void Reset()
        {
            for (int i = 0; i < _state.Length; i++) _state[i] = default;
        }

        /// <summary>指定スロットの円に居るか（診断・テスト用）。</summary>
        public bool IsInside(int slot)
            => slot >= 0 && slot < _state.Length && _state[slot].inside;

        /// <summary>指定スロットの連続滞在秒（居なければ 0）。</summary>
        public float InsideSecOf(int slot)
            => slot >= 0 && slot < _state.Length ? _state[slot].insideSec : 0f;

        /// <summary>
        /// 体験者の course 空間 XZ と経過時間で状態を進める。
        /// <paramref name="dt"/> が不連続なら「入っている」判定だけ更新して滞在秒は 0 から数え直す。
        /// </summary>
        public void Tick(float x, float z, float dt)
        {
            bool continuous = dt > 0f && dt <= MaxContinuousDtSec;
            for (int i = 0; i < _spots.Length; i++)
            {
                Spot s = _spots[i];
                if (!s.defined)
                {
                    _state[i] = default;
                    continue;
                }

                float r = s.rM < MinRadiusM ? MinRadiusM : s.rM;
                float threshold = _state[i].inside ? r + ExitMarginM : r;
                float dx = x - s.x, dz = z - s.z;
                bool inside = dx * dx + dz * dz <= threshold * threshold;

                if (!inside)
                {
                    _state[i] = default;
                    continue;
                }

                // 入り続けているなら加算、入った瞬間 / 不連続なら 0 から。
                float sec = _state[i].inside && continuous ? _state[i].insideSec + dt : 0f;
                _state[i] = new State { inside = true, insideSec = sec };
            }
        }
    }
}
