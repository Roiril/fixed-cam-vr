#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming.Cg
{
    /// <summary>
    /// 体験者の身体（頭・手）→ CG 人形の手首目標への写像と、手が取れない間の idle 合流。純関数。
    ///
    /// 要点は 3 つ:
    /// - **肩を基準に写す**。手の絶対位置でも頭からの相対でもなく「肩から手へのベクトル」を
    ///   人形の肩へ移し、**腕の長さの比**で伸縮させる。こうすると体験者と人形で
    ///   背丈・頭身・肩幅・腕長のどれが違っても、「腕を伸ばし切った」が人形でも伸ばし切りになる
    /// - **体の向きで正規化する**。頭の向きではなく体の向き（頭の向きを鈍らせたもの）を使う。
    ///   首だけ振ったときに手が振り回されないため
    /// - **重みで合流する**。トラッキングが切れた瞬間に腕が飛ばないよう、idle ポーズへ時間で減衰合流する
    ///   （不変条件: ハンドトラッキング不在でも演出は止まらない）
    ///
    /// ## なぜ頭基準をやめたか（2026-08-05・実測）
    /// 旧実装は「頭 → 手」を**全高比**でスケールして人形の頭へ乗せていた。これは体験者と人形の
    /// 体の比率が同じことを仮定している。プレハブを実測すると頭から肩までが身長比で
    /// **人体 9.0% / 市松人形 2.8%（3.2 倍差）**あり、仮定が成り立たない。
    ///
    /// 人体比率の Remy でも実害が出ていた（`Preview Actor Motion` の実測）:
    /// **腕を下ろした姿勢で目標が腕の届く範囲を 26% 超え**、IK がクランプして腕が棒のように
    /// 伸び切っていた。体験中いちばん長い姿勢がこれなので、ほぼ常時その状態だったことになる。
    /// 肩基準 + 腕長比にすると超過は約 5%（＝人間も腕を下ろせば伸び切る）まで下がる。
    /// </summary>
    public static class ActorArmLogic
    {
        /// <summary>手の有効 / 無効が切り替わってから完全に反映されるまでの秒数。</summary>
        public const float DefaultBlendSec = 0.35f;

        /// <summary>目標位置の平滑化半減期（秒）。トラッキングの微振動を抑える。</summary>
        public const float DefaultSmoothHalfLifeSec = 0.08f;

        /// <summary>体の向きが頭の向きへ追いつく半減期（秒）。人は首を振っても体はすぐ回らない。</summary>
        public const float DefaultBodyYawHalfLifeSec = 0.35f;

        /// <summary>
        /// 頭と体の向きの差の上限（度）。人の首はこれ以上ねじれない。
        ///
        /// ⚠ これが無いと**体ごと回った体験者に人形の体が付いていけない**。実測（`Preview Actor Motion`）で
        /// 90° 振り向いた場面の人形は正面を向いたまま手だけが回り、**手が背中側へ回った絵**になった。
        /// 遅れは「首だけ振ったときに体ごと回らない」ためのもので、体ごと回ったときは追従させる。
        /// </summary>
        public const float MaxNeckTwistDeg = 50f;

        /// <summary>体験者の頭高としてあり得る下限（しゃがみ・床置き HMD の暴走を防ぐ）。</summary>
        public const float MinHeadHeightM = 0.8f;

        /// <summary>頭のトラッキング点から頭頂までの目安（身長 ≒ 頭高 + これ）。</summary>
        public const float HeadToTopM = 0.12f;

        // ---- 体験者の体格の推定 ---------------------------------------------
        // 比率は Remy（Mixamo の人体リグ）のボーンを実測して得た値。人形側は実ボーンを測れるが、
        // 体験者側は HMD の高さしか分からないので、ここだけ「人体はこういう比率」を仮定する。
        // ⚠ 人形側の肩は `LeftArm`（肩関節）ボーンなので、体験者側も同じ位置を指す比率にしてある。
        //    肩峰（もっと外側・上）で取ると腕長と食い違って、また伸び切る。

        /// <summary>頭（HMD ＝ 目の高さ）から肩関節までの下方向距離 / 身長。</summary>
        public const float PlayerShoulderDropRatio = 0.15f;

        /// <summary>体の中心から肩関節までの横方向距離 / 身長。</summary>
        public const float PlayerShoulderHalfWidthRatio = 0.104f;

        /// <summary>肩関節から手首までの距離 / 身長。</summary>
        public const float PlayerArmLengthRatio = 0.29f;

        /// <summary>頭（HMD）の高さから体験者の身長を推定する。</summary>
        public static float EstimateHeightM(float headHeightM) =>
            Mathf.Max(MinHeadHeightM, headHeightM) + HeadToTopM;

        /// <summary>体験者の腕（肩関節→手首）の長さ。</summary>
        public static float EstimateArmLengthM(float headHeightM) =>
            EstimateHeightM(headHeightM) * PlayerArmLengthRatio;

        /// <summary>
        /// 体験者の肩関節のワールド位置。
        /// <paramref name="side"/> は -1 = 左 / +1 = 右で、体の向き
        /// <paramref name="bodyYawDeg"/> に沿って横へずらす。
        /// </summary>
        public static Vector3 EstimateShoulder(Vector3 headWorld, float bodyYawDeg, float side,
                                               float headHeightM)
        {
            float h = EstimateHeightM(headHeightM);
            Vector3 right = Quaternion.Euler(0f, bodyYawDeg, 0f) * Vector3.right;
            return headWorld
                   + Vector3.down * (h * PlayerShoulderDropRatio)
                   + right * (Mathf.Sign(side) * h * PlayerShoulderHalfWidthRatio);
        }

        /// <summary>
        /// 腕の長さの比（人形 / 体験者）。手の相対ベクトルをこの比で伸縮させると、
        /// **「伸ばし切った」が人形でも伸ばし切りになる**。
        /// </summary>
        public static float ArmScale(float actorArmLengthM, float headHeightM)
        {
            float player = Mathf.Max(0.05f, EstimateArmLengthM(headHeightM));
            float actor = Mathf.Max(0.01f, actorArmLengthM);
            return Mathf.Clamp(actor / player, 0.05f, 5f);
        }

        // ---- 写像 -----------------------------------------------------------

        /// <summary>
        /// 体験者の手のワールド位置 → 人形の手首目標ワールド位置。
        ///
        /// <paramref name="playerBodyYawDeg"/> には**体の向き**（頭の向きを
        /// <see cref="SmoothYawDeg"/> で鈍らせたもの）を渡す。頭の向きを渡すと、
        /// 首を振っただけで手の相対ベクトルが回り、**手が動いていないのに人形の腕が振り回される**。
        ///
        /// <paramref name="actorYawDeg"/> との差だけ相対ベクトルを回すので、人形が体験者と違う向きに
        /// 立っていても（cgMode=fixed）手の動きが体に対して正しく乗る。
        /// </summary>
        public static Vector3 MapHandToActor(Vector3 handWorld, Vector3 playerShoulderWorld,
                                             float playerBodyYawDeg,
                                             Vector3 actorShoulderWorld, float actorYawDeg, float scale)
        {
            Vector3 rel = (handWorld - playerShoulderWorld) * scale;
            Quaternion yaw = Quaternion.Euler(0f, Mathf.DeltaAngle(playerBodyYawDeg, actorYawDeg), 0f);
            return actorShoulderWorld + yaw * rel;
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

        /// <summary>
        /// 頭の向き → 体の向き。最短回りで追い、**首のねじれの上限**で引きずられる。
        ///
        /// 遅れだけだと「首を振った」と「体ごと回った」の区別が付かず、後者で体が置いていかれる。
        /// 上限を入れると、大きく回ったときは体が必ず付いてくる（人の首がねじれ切って
        /// 体を引っ張るのと同じ）。
        /// </summary>
        public static float SmoothYawDeg(float currentDeg, float targetDeg, float dt,
                                         float halfLifeSec = DefaultBodyYawHalfLifeSec,
                                         float maxTwistDeg = MaxNeckTwistDeg)
        {
            float y = currentDeg;
            if (dt > 0f)
            {
                if (halfLifeSec <= 1e-4f) y = targetDeg;
                else
                {
                    float k = 1f - Mathf.Exp(-dt * 0.6931472f / halfLifeSec);
                    y = currentDeg + Mathf.DeltaAngle(currentDeg, targetDeg) * Mathf.Clamp01(k);
                }
            }
            // ねじれ切ったら体が引かれる。dt=0 でも効かせる（上限を超えた状態を保たない）。
            float twist = Mathf.DeltaAngle(y, targetDeg);
            float limit = Mathf.Max(0f, maxTwistDeg);
            if (Mathf.Abs(twist) > limit) y = targetDeg - Mathf.Sign(twist) * limit;
            return y;
        }
    }
}
