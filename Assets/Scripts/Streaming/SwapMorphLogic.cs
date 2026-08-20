#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>入れ替わりのほどけ</b>の進み方（`canon/LEDGER.md` 0089 / 0090）。UnityEngine の Mathf しか
    /// 使わない純ロジックで、実体（<see cref="SwapMorphFx"/>）はこの struct が返す値を
    /// uniform と人形の背丈へ流すだけ。
    ///
    /// <b>なぜ全画面の砂嵐をやめるか。</b> 全域一様な乱れは「機材が壊れた」で説明が付いてしまうので、
    /// その最中に何が入れ替わっても**入れ替わったことにならない**（`rules/sound-design.md` §4 の
    /// 「対象に紐づく非一様性だけが、原因が世界の側にあることを示せる」と同じ理屈）。
    /// 入れ替わりは<b>映像の中の体験者だけ</b>を襲う必要がある。
    ///
    /// <b>⚠⚠ 2026-08-19 に「砂粒」から「糸」へ作り直した</b>（ユーザーが参考画像を渡した）。
    /// ブロック状のノイズでは「壊れた」にしか読めない。参考画像は**人が細い線の束にほどけて
    /// 流れていく**絵で、線は連続した曲線でなければあの読みにならない。
    ///
    /// 段は 3 つ。
    /// <list type="table">
    /// <item><term>ほどける <see cref="RiseFrac"/></term>
    ///   <description>体が端から糸になる（<see cref="Sample.cover"/> が前線）。糸は外へ広がり
    ///   （<see cref="Sample.thread"/>）、ほどけた所はもつれの塊になって下を隠す（<see cref="Sample.knot"/>）</description></item>
    /// <item><term>吸い込まれる <see cref="MorphFrac"/></term>
    ///   <description>塊がほぐれながら、<see cref="Sample.heightM"/> の縮みに連れて集まる（等比）</description></item>
    /// <item><term>晴れる（残り）</term>
    ///   <description>糸が消える。人形になる向きは <see cref="Sample.real"/> が上がって実体が残る</description></item>
    /// </list>
    ///
    /// ⚠ <b>画面の差し替えは「ほどけ切った瞬間」でなければならない。</b> それより前だと体験者が
    ///   糸の下ではなく画の中で消え、後だと糸の中で背景が動く。<see cref="Sample.justCovered"/>
    ///   が立った 1 フレームで差し替える（<see cref="SwapMorphFx"/> が 1 回だけ呼ぶ）。
    ///   その瞬間 <see cref="Sample.knot"/> は 1 ＝ もつれが下を完全に隠している。
    /// </summary>
    public sealed class SwapMorphLogic
    {
        /// <summary>入れ替わりの向き。**データで指定しない**（<see cref="TakeSchema.TransSwap"/> の説明）。</summary>
        public enum Dir
        {
            /// <summary>人 → 人形。始まりは体験者の背丈、終わりは人形の背丈で、<b>終わったら人形が残る</b>。</summary>
            ToDoll,
            /// <summary>人形 → 人。始まりは人形の背丈、終わりは体験者の背丈で、<b>終わったら何も残らない</b>。</summary>
            ToHuman,
        }

        /// <summary>ほどける段の割合（全体に対して）。</summary>
        public const float RiseFrac = 0.32f;

        /// <summary>吸い込まれる段の割合。晴れる段は残り（1 - RiseFrac - MorphFrac = 0.26）。</summary>
        public const float MorphFrac = 0.42f;

        /// <summary>尺が指定されなかったときの全体（秒）。</summary>
        public const float DefaultTotalSec = TakeSchema.DefaultSwapMs / 1000f;

        /// <summary>短すぎる尺を弾く下限（秒）。3 段が読めなくなる。</summary>
        public const float MinTotalSec = 0.9f;

        /// <summary>長すぎる尺を弾く上限（秒）。演出ではなく事故に見える。</summary>
        public const float MaxTotalSec = 8f;

        /// <summary>
        /// 吸い込まれる段の終わりに残っている糸の量。**0 にしない** — 縮み切った所で糸が消えると
        /// 「集まって人形になった」ではなく「消えてから人形が出た」に見える。
        /// </summary>
        public const float ThreadAtPull = 0.55f;

        /// <summary>
        /// 体験者の背丈が測れないときの代用 (m)。HMD が取れていない状況
        /// （位置合わせ直後・トラッキングロスト）でも入れ替わりを止めないための値。
        /// </summary>
        public const float FallbackHumanHeightM = 1.65f;

        /// <summary>体験者の背丈としてあり得る範囲 (m)。外れ値で人型が化け物になるのを防ぐ。</summary>
        public const float MinHumanHeightM = 1.05f;
        public const float MaxHumanHeightM = 2.10f;

        /// <summary>
        /// ほどけ切った瞬間に 1 回だけ走らせる全画面の乱れ（強さ / 秒）。
        ///
        /// ⚠ <b>これは「入れ替わりを隠す」ためのものではない。</b> 隠すのは糸のもつれの仕事で、
        ///   こちらは<b>人型の外で同時に起きる差し替え</b>（3 周目 A なら左半分が凍結から録画へ、
        ///   4 周目 A なら左半分の人形の群れが消える）を覆うためのもの。もつれが既に体験者を
        ///   隠し切った後なので、「人 → 人形」の読みは 1 ビットも壊れない。
        /// ⚠ 短く弱くする。長くすると結局「全画面の砂嵐で変えた」に戻る。
        /// </summary>
        public const float VeilLevel = 0.55f;
        public const float VeilSec = 0.20f;

        /// <summary>1 フレーム分の出力。<see cref="SwapMorphFx"/> がそのまま uniform と背丈へ流す。</summary>
        public readonly struct Sample
        {
            /// <summary>入れ替わりが進行中か。false のときは他の値を読まない。</summary>
            public readonly bool active;

            /// <summary>
            /// ほどけの前線 0..1。人 → 人形は頭から、人形 → 人は足元から進む
            /// （どちらも<b>行き先の方向へ</b>ほどける）。ほどけ切ったら 1 のまま。
            /// </summary>
            public readonly float cover;

            /// <summary>
            /// もつれが塊になっている度合い 0..1。**1 で下（映像の中の体験者）を完全に隠す。**
            /// ほどけ切った所で 1 になり、吸い込まれるほどほぐれて 0 へ。
            /// </summary>
            public readonly float knot;

            /// <summary>体の外へ流れ出た糸の量 0..1（雲の広がり）。</summary>
            public readonly float thread;

            /// <summary>CG が<b>実体として</b>見えている度合い 0..1（0 = 糸だけ / 1 = 人形そのもの）。</summary>
            public readonly float real;

            /// <summary>いまの人型の背丈 (m)。</summary>
            public readonly float heightM;

            /// <summary>影・接地影の濃さの倍率 0..1。糸のもつれは光を遮らない。</summary>
            public readonly float ground;

            /// <summary>このフレームでほどけ切った（＝画面を差し替える 1 フレーム）。</summary>
            public readonly bool justCovered;

            /// <summary>
            /// このフレームで<b>晴れる段へ入った</b>（＝人型を人形へ差し替える 1 フレーム）。
            /// 縮み切って、まだもつれに覆われている瞬間。ここで姿を替えれば 1 画素も見えない。
            /// </summary>
            public readonly bool justSettling;

            /// <summary>このフレームで入れ替わりが終わった。</summary>
            public readonly bool justFinished;

            public Sample(bool active, float cover, float knot, float thread, float real,
                          float heightM, float ground,
                          bool justCovered, bool justSettling, bool justFinished)
            {
                this.active = active;
                this.cover = cover;
                this.knot = knot;
                this.thread = thread;
                this.real = real;
                this.heightM = heightM;
                this.ground = ground;
                this.justCovered = justCovered;
                this.justSettling = justSettling;
                this.justFinished = justFinished;
            }

            public static Sample Idle => new Sample(false, 0f, 0f, 0f, 0f, 0f, 1f, false, false, false);
        }

        private bool _active;
        private float _elapsed;
        private float _total = DefaultTotalSec;
        private Dir _dir = Dir.ToDoll;
        private float _fromH = FallbackHumanHeightM;
        private float _toH = 0.4f;
        private bool _covered;
        private bool _settling;
        private float _coverPeak;

        public bool Active => _active;
        public Dir Direction => _dir;

        /// <summary>
        /// <b>映像の中に写っている姿</b>の背丈 (m)。**マスクはこの大きさで引く**
        /// （`ScreenComposite` の `_SwapRect0`）。
        ///
        /// ⚠⚠ 人型（`Sample.heightM`）とは別物。人型は縮む / 育つが、**映像の中の人は動かない**。
        ///   人 → 人形は始めから終わりまで体験者の背丈。人形 → 人は<b>ほどけ切った縁で映像が
        ///   差し替わる</b>ので、そこから人の背丈になる。
        /// </summary>
        public float MaskHeightM => _dir == Dir.ToDoll ? _fromH : (_covered ? _toH : _fromH);

        /// <summary>
        /// マスクを引く枠。<paramref name="rect"/>（いまの人型の投影）の**足元を動かさずに**、
        /// 背丈だけ <paramref name="maskHeightM"/> の分へ引き直す。
        /// **実機とプレビューで同じ式を使う**（写経すると必ずいつか食い違う）。
        /// </summary>
        public static Vector4 MaskRect(Vector4 rect, float heightM, float maskHeightM)
        {
            if (heightM <= 1e-4f || maskHeightM <= 1e-4f) return rect;
            float foot = rect.y - rect.z;
            float hz = rect.z * (maskHeightM / heightM);
            return new Vector4(rect.x, foot + hz, hz, rect.w);
        }

        /// <summary>進み 0..1（テレメトリ用）。走っていなければ 0。</summary>
        public float Progress01 => _active && _total > 0f ? Mathf.Clamp01(_elapsed / _total) : 0f;

        /// <summary>
        /// 入れ替わりを始める。<paramref name="fromHeightM"/> / <paramref name="toHeightM"/> は
        /// 人型の背丈（足元は動かない）。
        /// </summary>
        public void Begin(Dir dir, float totalSec, float fromHeightM, float toHeightM)
        {
            _active = true;
            _elapsed = 0f;
            _total = Mathf.Clamp(totalSec > 0f ? totalSec : DefaultTotalSec, MinTotalSec, MaxTotalSec);
            _dir = dir;
            _fromH = Mathf.Max(0.05f, fromHeightM);
            _toH = Mathf.Max(0.05f, toHeightM);
            _covered = false;
            _settling = false;
            _coverPeak = 0f;
        }

        /// <summary>途中で畳む（演出の中止・ランリセット）。次の <see cref="Tick"/> は Idle を返す。</summary>
        public void Cancel()
        {
            _active = false;
            _elapsed = 0f;
            _covered = false;
            _settling = false;
            _coverPeak = 0f;
        }

        /// <summary>1 フレーム進める。走っていなければ <see cref="Sample.Idle"/>。</summary>
        public Sample Tick(float dt)
        {
            if (!_active) return Sample.Idle;

            _elapsed += Mathf.Max(0f, dt);
            float t = Mathf.Clamp01(_elapsed / Mathf.Max(_total, 1e-4f));

            float clearFrac = Mathf.Max(1f - RiseFrac - MorphFrac, 0.01f);
            float cover, knot, thread, real, h01;
            if (t < RiseFrac)
            {
                // ほどける。糸が広がり、ほどけた所がもつれの塊になって下を隠す。
                float u = Smooth(t / RiseFrac);
                cover = u;
                knot = u;
                thread = u;
                // 人形 → 人は、ここで人形の実体が糸に食われて消えていく。
                // ⚠ **前線より速く消す**（1.35 倍）。同じ速さだと、ほどけ切る瞬間まで
                //   実体の縁が残って「隠し切った」に見えない。
                real = _dir == Dir.ToHuman ? 1f - Smooth(Mathf.Min(1f, u * 1.35f)) : 0f;
                h01 = 0f;
            }
            else if (t < RiseFrac + MorphFrac)
            {
                // 吸い込まれる。もつれがほぐれながら、背丈の縮みに連れて集まる。
                float u = Smooth((t - RiseFrac) / MorphFrac);
                cover = 1f;
                // ⚠⚠ **覆いはここで薄めない**（2026-08-19 の 9 巡目・絵で直した）。0089 の言葉は
                //   「ノイズに覆われて見えなくなり、**その後にノイズの人型が**徐々に人形サイズに
                //   なり」なので、縮んでいる間も覆われたままでなければならない。
                //   薄めていた頃は、縮む段の途中で当人の顔がそのまま読めていた。
                knot = 1f;
                // 広がり（体の外へ流れ出た糸）だけは縮みに連れて引く。
                thread = Mathf.Lerp(1f, ThreadAtPull, u);
                real = 0f;
                h01 = u;
            }
            else
            {
                // 晴れる。糸が消え、人形になる向きだけ実体が出る。
                float u = Smooth((t - RiseFrac - MorphFrac) / clearFrac);
                cover = 1f;
                knot = Mathf.Lerp(1f, 0f, u);
                thread = Mathf.Lerp(ThreadAtPull, 0f, u);
                real = _dir == Dir.ToDoll ? u : 0f;
                h01 = 1f;
            }

            bool justCovered = !_covered && cover >= 0.999f;
            if (justCovered) _covered = true;
            _coverPeak = Mathf.Max(_coverPeak, cover);

            // 晴れる段へ入った縁。**縮み切っていて、まだもつれに覆われている**唯一の瞬間なので、
            // 人型を人形へ差し替えるならここしかない。
            bool justSettling = !_settling && t >= RiseFrac + MorphFrac;
            if (justSettling) _settling = true;

            bool justFinished = t >= 1f;
            if (justFinished) _active = false;

            return new Sample(true, cover, knot, thread, real, LerpHeight(h01), Ground(real),
                              justCovered, justSettling, justFinished);
        }

        /// <summary>
        /// 背丈は<b>等比</b>で動かす（線形にしない）。1.65m → 0.40m は 4 倍の縮尺で、線形だと
        /// 前半で一気に小さくなって後半はほとんど動かない ＝「縮んだ」ではなく「落ちた」に見える。
        /// 等比なら見かけの大きさが一定の速さで変わる。
        /// </summary>
        public float LerpHeight(float h01)
            => Mathf.Exp(Mathf.Lerp(Mathf.Log(_fromH), Mathf.Log(_toH), Mathf.Clamp01(h01)));

        /// <summary>
        /// 影と接地影の濃さ。<b>糸のもつれは光を遮らない</b>ので、ほどけているあいだは 0。
        ///
        /// 人 → 人形は始まりに人形が居ない（映像の中の本物の体験者が立っている）ので、
        /// 影が戻るのは実体が出てくる<b>晴れる段だけ</b>。
        /// 人形 → 人は始まりに人形が居るので、ほどけていく<b>その段で落ちて、戻らない</b>
        /// （戻る先はライブ映像で、そこには本物の影が最初から写っている）。
        /// </summary>
        private float Ground(float real)
            => _dir == Dir.ToDoll ? real : 1f - _coverPeak;

        private static float Smooth(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * (3f - 2f * x);
        }

        /// <summary>
        /// 体験者の背丈を HMD の高さから決める。
        ///
        /// ⚠ <b>推定は <see cref="Cg.ActorArmLogic.EstimateHeightM"/> をそのまま呼ぶ</b>（式を写さない）。
        ///   腕の写像が同じ推定身長を使っているので、片方だけ直すと<b>人型の背丈と腕の長さが
        ///   食い違った糸の figure</b> になる。
        /// 取れていなければ <see cref="FallbackHumanHeightM"/>。
        /// </summary>
        public static float HumanHeightFrom(bool hasHead, float headHeightM)
        {
            if (!hasHead || float.IsNaN(headHeightM)) return FallbackHumanHeightM;
            float h = Cg.ActorArmLogic.EstimateHeightM(headHeightM);
            if (float.IsNaN(h)) return FallbackHumanHeightM;
            return Mathf.Clamp(h, MinHumanHeightM, MaxHumanHeightM);
        }
    }
}
