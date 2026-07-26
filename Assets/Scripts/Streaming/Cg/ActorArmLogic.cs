#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming.Cg
{
    /// <summary>
    /// 体験者の身体（頭・手）→ CG 人形の手首目標への写像と、手が取れない間の idle 合流。純関数。
    ///
    /// 要点は 2 つ:
    /// - **相対で写す**。手の絶対位置ではなく「頭から手へのベクトル」を人形の頭基準へ移す。
    ///   こうすると体験者と人形の立ち位置・向き・背丈が違っても同じ動きに見える
    /// - **重みで合流する**。トラッキングが切れた瞬間に腕が飛ばないよう、idle ポーズへ時間で減衰合流する
    ///   （不変条件: ハンドトラッキング不在でも演出は止まらない）
    /// </summary>
    public static class ActorArmLogic
    {
        /// <summary>手の有効 / 無効が切り替わってから完全に反映されるまでの秒数。</summary>
        public const float DefaultBlendSec = 0.35f;

        /// <summary>目標位置の平滑化半減期（秒）。トラッキングの微振動を抑える。</summary>
        public const float DefaultSmoothHalfLifeSec = 0.08f;

        /// <summary>体験者の頭高としてあり得る下限（しゃがみ・床置き HMD の暴走を防ぐ）。</summary>
        public const float MinHeadHeightM = 0.8f;

        /// <summary>頭のトラッキング点から頭頂までの目安（身長 ≒ 頭高 + これ）。</summary>
        public const float HeadToTopM = 0.12f;

        /// <summary>
        /// 体験者と人形の体格比。人形の身長を体験者の推定身長で割る。
        /// 手の相対ベクトルをこの比で伸縮させると、小さい人形でも「同じ動き」に見える。
        /// </summary>
        public static float BodyScale(float actorHeightM, float headHeightM)
        {
            float player = Mathf.Max(MinHeadHeightM, headHeightM) + HeadToTopM;
            float actor = Mathf.Max(0.2f, actorHeightM);
            return Mathf.Clamp(actor / player, 0.2f, 3f);
        }

        /// <summary>
        /// 体験者の手のワールド位置 → 人形の手首目標ワールド位置。
        /// <paramref name="actorYawDeg"/> と <paramref name="playerYawDeg"/> の差だけ相対ベクトルを回すので、
        /// 人形が体験者と違う向きに立っていても（cgMode=fixed）手の動きが体に対して正しく乗る。
        /// </summary>
        public static Vector3 MapHandToActor(Vector3 handWorld, Vector3 headWorld, float playerYawDeg,
                                             Vector3 actorHeadWorld, float actorYawDeg, float scale)
        {
            Vector3 rel = (handWorld - headWorld) * scale;
            Quaternion yaw = Quaternion.Euler(0f, Mathf.DeltaAngle(playerYawDeg, actorYawDeg), 0f);
            return actorHeadWorld + yaw * rel;
        }

        /// <summary>
        /// 追従の重み（0=idle / 1=手に追従）を時間で動かす。dt / blendSec ずつ線形に寄せる。
        /// </summary>
        public static float Blend(float weight, bool valid, float dt, float blendSec = DefaultBlendSec)
        {
            float step = blendSec > 1e-4f ? Mathf.Max(0f, dt) / blendSec : 1f;
            return Mathf.Clamp01(valid ? weight + step : weight - step);
        }

        /// <summary>
        /// 位置の平滑化（フレームレート非依存の指数平滑）。halfLife 秒で目標との差が半分になる。
        /// </summary>
        public static Vector3 Smooth(Vector3 current, Vector3 target, float dt,
                                     float halfLifeSec = DefaultSmoothHalfLifeSec)
        {
            if (dt <= 0f) return current;                 // 時間が進んでいないなら動かさない
            if (halfLifeSec <= 1e-4f) return target;      // 平滑化なし＝即座に追従
            float k = 1f - Mathf.Exp(-dt * 0.6931472f / halfLifeSec);
            return Vector3.Lerp(current, target, Mathf.Clamp01(k));
        }
    }
}
