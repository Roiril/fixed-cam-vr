#nullable enable

using System;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 段 4「現実が割れてスクリーンへ入る」の<b>数値の正本</b>。
    ///
    /// シェーダ側の式は <c>Assets/Art/Shaders/Intro/IntroShatter.hlsl</c> が 1 本だけ持ち、
    /// **数値はここから uniform で降りる**（覆いも封印の箱も同じ値を使う）。だから
    /// 「片方だけ直して沈黙して食い違う」が構造的に起きない。
    ///
    /// ⚠ <b>式の形（smoothstep の区間・二乗の場所）は HLSL と C# の 2 箇所にある。</b>
    /// ここの <see cref="ClosedAt"/> は「shatter = 1 で必ず全部閉じる」「スクリーンの上のセルは
    /// 割れない」という<b>契約だけ</b>をテストで固定するための写しで、実行時には使われない。
    /// 曲線を触ったら両方直すこと。
    /// </summary>
    public static class IntroShatterCurve
    {
        // ---- 段 4 を 2 つに割る（順番が意味を持つ）----------------------------------
        //
        // ⚠⚠ **パススルーを先に閉じ切ってから、箱を割る。** 逆でも同時でもいけない。
        // 箱は不透明で、その裏のパススルーが開いていると、**箱に割れ目が入った瞬間に
        // 体験エリアの中が覗ける**（`canon/LEDGER.md` 0005 が禁じているもの）。
        // 重ならないよう区間を分けてあるので、覗きは起こりようがない。
        //
        // 見え方としても筋が通る — **外側の世界が先に持っていかれ、最後に封印そのものが割れる**。

        /// <summary>覆い（パススルー）が割れ終わる進み。ここから先は箱の番。</summary>
        public const float VeilPhaseEnd = 0.35f;

        /// <summary>覆いの側の進み（段 4 全体の進み <paramref name="shatter"/> から取り出す）。</summary>
        public static float VeilShatter(float shatter) => Clamp01(shatter / VeilPhaseEnd);

        /// <summary>封印の箱の側の進み。覆いが閉じ切ってから始まる。</summary>
        public static float BoxShatter(float shatter) =>
            Clamp01((shatter - VeilPhaseEnd) / (1f - VeilPhaseEnd));

        // ---- 時計 -------------------------------------------------------------------

        /// <summary>周縁が先に割れる度合い（0 = 全部同時）。</summary>
        public const float Stagger = 0.38f;

        /// <summary>セルごとの出発のばらつき。揃いすぎると「シャッター」に見える。</summary>
        public const float Jitter = 0.14f;

        /// <summary>行き先へ引かれ始める進み。ここまでは割れ目が開くだけ。</summary>
        public const float PullAt = 0.18f;

        /// <summary>行き先へどこまで寄るか。1 にすると全部が矩形の上へ積み上がる。</summary>
        public const float TravelMax = 0.85f;

        /// <summary>寸法が落ち始める進み。</summary>
        public const float ShrinkAt = 0.72f;

        /// <summary>閉じ（現実を返す）が始まる進み。</summary>
        public const float CloseAt = 0.60f;

        // ---- 形 ---------------------------------------------------------------------

        /// <summary>割れ目の幅（セルの寸法に対する割合）。</summary>
        public const float GapMax = 0.14f;

        /// <summary>
        /// 覆いのセルの回り (rad)。<b>大きくしない。</b> 覆いのセルは「現実が覗く窓」で、
        /// 中身は回らない。窓だけ大きく回すと「マスクが回っている」と読まれる。
        /// </summary>
        public const float VeilSpin = 0.35f;

        /// <summary>
        /// 箱の破片の回り (rad ≒ 35°)。こちらは<b>実体の面</b>なので模様ごと回る。
        /// ⚠ これ以上振ると裏を向いて <c>Cull Back</c> で消える。
        /// </summary>
        public const float BoxSpin = 0.61f;

        /// <summary>覆いのセルが外れるときのずれ（覆いのローカル単位・±0.5 が端）。</summary>
        public const float VeilDrift = 0.004f;

        /// <summary>箱の破片が外れるときのずれ (m)。</summary>
        public const float BoxDrift = 0.03f;

        /// <summary>進む向きへ伸びる量（覆いのセルだけ。速度を形で伝える）。</summary>
        public const float VeilStretch = 1.6f;

        /// <summary>
        /// <b>封印の箱だけ</b>、スクリーン矩形の周り<c>これ</c>ぶんは割らずに残す。
        ///
        /// ⚠ 覆いのセルと箱の破片は<b>大きさが違う</b>（覆いは視野角で約 3.6°、箱は面の上で 0.075m）。
        /// 「矩形の中を向いていれば割らない」を両者が別々の刻みで判定すると、矩形の縁に
        /// <b>箱だけ消えて覆いは開いたままの帯</b>ができ、そこから体験エリアの中が細く覗く
        /// （2026-08-12 のプレビューで実測）。箱の側を 1 破片ぶん広めに残して塞ぐ。
        /// はみ出した分は開口が切るので画には出ない。
        /// </summary>
        public const float BoxKeepFar = 0.06f;

        /// <summary>
        /// 「いちばん遠い周縁」とみなす見かけの隔たり。単位は<b>覆いの面の上での m</b>。
        /// スクリーン矩形の半対角の <see cref="AbsorbSpread"/> 倍。
        ///
        /// ⚠ <b>基準は「視界の端」であって「覆いの面の端」ではない</b>（2026-08-12 実測で直した）。
        /// 覆いの面は ±63° まで広がっているのでそれを基準にすると、<b>実際に見える範囲は
        /// far 0.3 までしか使われず</b>、前線が視界を渡り切る前に段が終わる（プレビューで
        /// 半分過ぎても箱が無傷だった）。スクリーンの半対角（≈18°）の 1.25 倍が視界の端に当たる。
        /// </summary>
        public const float AbsorbSpread = 1.25f;

        /// <inheritdoc cref="AbsorbSpread"/>
        public static float AbsorbRangeM(float screenHalfW, float screenHalfH)
        {
            float diag = (float)Math.Sqrt(screenHalfW * screenHalfW + screenHalfH * screenHalfH);
            return Math.Max(diag, 0.05f) * AbsorbSpread;
        }

        // ---- マテリアルへ配る（実行時も Editor プレビューもここを通す）-----------------
        //
        // ⚠ **数値をマテリアルへ書く場所はここ 1 箇所だけ。** 覆い・封印の箱・プレビューが
        // それぞれ書いていると、プレビューだけ別の絵になる（このリポジトリが 4 回踏んだ型）。

        private static readonly int ShatterId = Shader.PropertyToID("_Shatter");
        private static readonly int StaggerId = Shader.PropertyToID("_Stagger");
        private static readonly int JitterId = Shader.PropertyToID("_Jitter");
        private static readonly int GapId = Shader.PropertyToID("_Gap");
        private static readonly int SpinId = Shader.PropertyToID("_Spin");
        private static readonly int DriftId = Shader.PropertyToID("_Drift");
        private static readonly int PullAtId = Shader.PropertyToID("_PullAt");
        private static readonly int TravelMaxId = Shader.PropertyToID("_TravelMax");
        private static readonly int ShrinkAtId = Shader.PropertyToID("_ShrinkAt");
        private static readonly int CloseAtId = Shader.PropertyToID("_CloseAt");
        private static readonly int StretchId = Shader.PropertyToID("_Stretch");
        private static readonly int KeepFarId = Shader.PropertyToID("_KeepFar");

        /// <summary>覆い（パススルーが覗く窓）へ配る。<paramref name="veilShatter"/> は前半の進み。</summary>
        public static void PushVeil(Material m, float veilShatter)
        {
            if (m == null) return;
            PushCommon(m, veilShatter);
            m.SetFloat(SpinId, VeilSpin);
            m.SetFloat(DriftId, VeilDrift);
            m.SetFloat(StretchId, VeilStretch);
        }

        /// <summary>封印の箱へ配る。<paramref name="boxShatter"/> は後半の進み。</summary>
        public static void PushBox(Material m, float boxShatter)
        {
            if (m == null) return;
            PushCommon(m, boxShatter);
            m.SetFloat(SpinId, BoxSpin);
            m.SetFloat(DriftId, BoxDrift);
            m.SetFloat(KeepFarId, BoxKeepFar);
        }

        private static void PushCommon(Material m, float shatter)
        {
            m.SetFloat(ShatterId, Clamp01(shatter));
            m.SetFloat(StaggerId, Stagger);
            m.SetFloat(JitterId, Jitter);
            m.SetFloat(GapId, GapMax);
            m.SetFloat(PullAtId, PullAt);
            m.SetFloat(TravelMaxId, TravelMax);
            m.SetFloat(ShrinkAtId, ShrinkAt);
            m.SetFloat(CloseAtId, CloseAt);
        }

        // ---- 契約の写し（テスト用。実行時には使わない）-------------------------------

        /// <summary>
        /// そのセルが割れるか。<b>スクリーン矩形の上にホームを持つセルは割らない</b> —
        /// 段 4 の終わりに枠の中まで消えると、枠がどこにあるか分からないまま段 5 の映像が点く。
        /// </summary>
        public static bool Shatters(float far) => far > 1e-4f;

        /// <summary>そのセルの進み（<c>IntroShardEval</c> の <c>p</c> と同じ式）。</summary>
        public static float Progress(float far, float r, float shatter)
        {
            far = Clamp01(far);
            float start = (1f - far) * Stagger + r * Jitter * far;
            float span = Math.Max(1f - Stagger - Jitter, 0.05f);
            return Clamp01((shatter - start) / span);
        }

        /// <summary>そのセルが閉じた量（<c>IntroShardEval</c> の <c>closed</c> と同じ式）。</summary>
        public static float ClosedAt(float far, float r, float shatter) =>
            SmoothStep(CloseAt, 1f, Progress(far, r, shatter));

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        private static float SmoothStep(float from, float to, float v)
        {
            if (to - from <= 1e-6f) return v >= to ? 1f : 0f;
            float t = Clamp01((v - from) / (to - from));
            return t * t * (3f - 2f * t);
        }
    }
}
