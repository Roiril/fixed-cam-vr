#nullable enable
using FixedCamVr.Streaming.Cg;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>黒い波が読む体験者のエネルギー</b>（設計 E / F。`reports/2026-08-21_swap-wave-design.html`）。
    ///
    /// 体験者が歩く速さが波の全体のエネルギーになり、手が速く動いた所だけ局所で毛羽立つ。
    /// 説明を 1 文字も出さずに「自分に反応している」を発見させるための層で、
    /// **反応するのは呪い（覆い）だけ** — 映像・post・カメラ・進行には 1 ビットも触らない。
    ///
    /// ⚠⚠ <b>変えてよいのは見た目だけ。</b> 総尺・段の進行・差し替えの縁（<see cref="SwapMorphLogic"/>）は
    ///   1 ミリも動かさない。動かすと「歩かない体験者で演出が終わらない」を作る。
    ///
    /// ⚠ <b>新しいセンサは足していない。</b> 入力は既にある <see cref="ShowBodyInput"/>
    ///   （頭・両手のワールド位置）で、供給元も <see cref="Cg.ShowCgLayer"/> の履歴と同じもの
    ///   （＝ 映像の遅延ぶん過去。人形の位置と同じ時刻を読む）。
    ///
    /// ⚠ <b>遅れを消さない。</b> 平滑の半減期（体 0.8 秒・手 0.3 秒）は精度の妥協ではなく演出そのもの —
    ///   遅れて反応することが「気づかれた」と読ませる。
    ///
    /// dt 注入の純ロジックなので EditMode テストで固定できる。
    /// </summary>
    public sealed class SwapEnergyLogic
    {
        /// <summary>この速さ (m/s) でエネルギーが 1 になる。ふつうの歩行の速さ。</summary>
        public const float BodyFullSpeed = 1.2f;

        /// <summary>体の速さの平滑（半減期・秒）。<b>遅れは演出</b>なので短くしない。</summary>
        public const float BodyHalfLifeSec = 0.8f;

        /// <summary>手はこの速さ (m/s) で局所のエネルギーが 1 になる（体より速く動く）。</summary>
        public const float HandFullSpeed = 1.6f;

        /// <summary>手の速さの平滑（半減期・秒）。</summary>
        public const float HandHalfLifeSec = 0.3f;

        /// <summary>
        /// 静止しているときの振幅倍率。<b>0 にしない</b> — 完全に止めると死んで見える。
        /// 静の怖さは「小さく蠢き続ける」こと。
        /// </summary>
        public const float AmpFloor = 0.55f;

        /// <summary>静止しているときの山の速さの倍率。</summary>
        public const float SpeedFloor = 0.70f;

        /// <summary>歩き切ったときに山の速さへ足す分（＝ 全開で 1.30 倍）。</summary>
        public const float SpeedRange = 0.60f;

        /// <summary>
        /// 手の局所エネルギーへ掛ける結合の強さ。<b>弱く結ぶ</b>（設計 F「ゲイン 0.2〜0.4」）—
        /// 強くすると「呪い」ではなく「エフェクトのデモ」になる。
        /// </summary>
        public const float HotGain = 0.30f;

        /// <summary>
        /// 速さを測ってよい dt の上限（秒）。これを超えたフレームは**不連続**として扱い、
        /// 位置だけ拾い直して速さは更新しない（アプリの復帰・トラッキングの飛びで
        /// 立ち止まっている体験者が全開になるのを防ぐ。<c>VisitorMarkHoldLogic</c> と同じ理由）。
        /// </summary>
        public const float MaxDtSec = 0.25f;

        /// <summary>
        /// あり得る速さの上限 (m/s)。recenter でワールドが飛ぶと数十 m/s が出る。
        /// 切らないと平滑の半減期ぶん（0.8 秒）全開が居座る。
        /// </summary>
        public const float MaxSpeed = 6f;

        /// <summary>
        /// 入力を読まないときのエネルギー（＝ 倍率がすべて 1 になる値）。
        /// 層を切って焼く Editor プレビュー（<c>-Set layers=</c>）と、
        /// 体の入力が 1 度も来ない構成のための中立値。
        /// </summary>
        public const float NeutralEnergy = 1f;

        private struct Track
        {
            public bool seeded;
            public Vector3 pos;
            public float speed;
        }

        private Track _head, _left, _right;

        /// <summary>平滑後の全身のエネルギー 0..1（0 = 立ち止まっている）。</summary>
        public float Energy01 { get; private set; }

        /// <summary>左手の局所エネルギー 0..1（手が取れていなければ 0）。</summary>
        public float LeftHot01 { get; private set; }

        /// <summary>右手の局所エネルギー 0..1。</summary>
        public float RightHot01 { get; private set; }

        /// <summary>波の振幅へ掛ける倍率（設計 E の写像）。</summary>
        public float AmpMul => AmpFloor + (1f - AmpFloor) * Energy01;

        /// <summary>山の走る速さへ掛ける倍率（設計 E の写像）。</summary>
        public float SpeedMul => SpeedFloor + SpeedRange * Energy01;

        /// <summary>入れ替わりの始まり・畳みでリセットする（前の体験者の速さを持ち越さない）。</summary>
        public void Reset()
        {
            _head = default;
            _left = default;
            _right = default;
            Energy01 = 0f;
            LeftHot01 = 0f;
            RightHot01 = 0f;
        }

        /// <summary>
        /// 1 フレーム進める。<paramref name="body"/> は<b>人形と同じ時刻</b>の体の入力
        /// （<see cref="Cg.ShowCgLayer.BodySnapshot"/>）。
        /// </summary>
        public void Tick(float dt, in ShowBodyInput body)
        {
            if (dt <= 0f) return;
            bool trust = dt <= MaxDtSec;

            // 体は **XZ だけ**（頭を上下に振っただけで波が荒れないように）。
            Vector3 headXz = body.HasHead
                ? new Vector3(body.HeadPos.x, 0f, body.HeadPos.z)
                : Vector3.zero;
            float head = Step(ref _head, body.HasHead, headXz, dt, trust, BodyHalfLifeSec);
            Energy01 = Mathf.Clamp01(head / BodyFullSpeed);

            LeftHot01 = Mathf.Clamp01(
                Step(ref _left, body.LeftValid, body.LeftHandPos, dt, trust, HandHalfLifeSec)
                / HandFullSpeed);
            RightHot01 = Mathf.Clamp01(
                Step(ref _right, body.RightValid, body.RightHandPos, dt, trust, HandHalfLifeSec)
                / HandFullSpeed);
        }

        /// <summary>
        /// 1 点の速さを平滑して返す。取れていない間は 0 へ落ちる
        /// （＝ 手が取れない体験者では設計 A〜D の絵に自然に落ちる。分岐を増やさない）。
        /// </summary>
        private static float Step(ref Track t, bool valid, Vector3 pos, float dt, bool trust,
                                 float halfLifeSec)
        {
            float target = 0f;
            if (!valid)
            {
                t.seeded = false;
            }
            else if (!t.seeded || !trust)
            {
                // 1 フレーム目と不連続のフレームは**位置だけ拾う**（速さは前の値のまま保つ）。
                t.seeded = true;
                t.pos = pos;
                target = t.speed;
            }
            else
            {
                target = Mathf.Min((pos - t.pos).magnitude / dt, MaxSpeed);
                t.pos = pos;
            }
            t.speed = Approach(t.speed, target, dt, halfLifeSec);
            return t.speed;
        }

        /// <summary>半減期 <paramref name="halfLifeSec"/> で <paramref name="target"/> へ寄せる。</summary>
        private static float Approach(float now, float target, float dt, float halfLifeSec)
        {
            float k = 1f - Mathf.Exp(-0.6931472f * dt / Mathf.Max(halfLifeSec, 1e-3f));
            return now + (target - now) * k;
        }
    }
}
