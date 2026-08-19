#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>入れ替わりのノイズ</b>の進み方（`canon/LEDGER.md` 0089）。UnityEngine の Mathf しか使わない純ロジックで、
    /// 実体（<see cref="SwapMorphFx"/>）はこの struct が返す値を uniform と人形の背丈へ流すだけ。
    ///
    /// <b>なぜ全画面の砂嵐をやめるか。</b> 全域一様な乱れは「機材が壊れた」で説明が付いてしまうので、
    /// その最中に何が入れ替わっても**入れ替わったことにならない**（`rules/sound-design.md` §4 の
    /// 「対象に紐づく非一様性だけが、原因が世界の側にあることを示せる」と同じ理屈）。
    /// 入れ替わりは<b>映像の中の体験者だけ</b>を襲う必要がある。
    ///
    /// 段は 3 つ。ユーザーの言葉（2026-08-19）がそのまま段になっている:
    /// 「体験者を徐々にオーバーレイするノイズを走らせ、ノイズが人型になって体験者はノイズに覆われて
    /// 見えなくなり、その後にノイズの人型が徐々に人形サイズになり、ノイズが晴れたら人形になる」。
    ///
    /// <list type="table">
    /// <item><term>湧く <see cref="RiseFrac"/></term>
    ///   <description>人型の中を足元から <see cref="Sample.cover"/> が埋めていく。埋まった画素だけ砂になる</description></item>
    /// <item><term>縮む <see cref="MorphFrac"/></term>
    ///   <description>覆ったまま背丈が変わる。<see cref="Sample.heightM"/> は<b>等比</b>で動く</description></item>
    /// <item><term>晴れる（残り）</term>
    ///   <description>人形になる向きは <see cref="Sample.solid"/> が上がる／人へ戻る向きは cover が下がる</description></item>
    /// </list>
    ///
    /// ⚠ <b>画面の差し替えは「覆い切った瞬間」でなければならない。</b> それより前だと体験者が
    ///   ノイズの下ではなく画の中で消え、後だとノイズの中で背景が動く。<see cref="Sample.justCovered"/>
    ///   が立った 1 フレームで差し替える（<see cref="SwapMorphFx"/> が 1 回だけ呼ぶ）。
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

        /// <summary>湧く段の割合（全体に対して）。</summary>
        public const float RiseFrac = 0.32f;

        /// <summary>縮む / 育つ段の割合。晴れる段は残り（1 - RiseFrac - MorphFrac = 0.26）。</summary>
        public const float MorphFrac = 0.42f;

        /// <summary>尺が指定されなかったときの全体（秒）。</summary>
        public const float DefaultTotalSec = TakeSchema.DefaultSwapMs / 1000f;

        /// <summary>短すぎる尺を弾く下限（秒）。3 段が読めなくなる。</summary>
        public const float MinTotalSec = 0.9f;

        /// <summary>長すぎる尺を弾く上限（秒）。演出ではなく事故に見える。</summary>
        public const float MaxTotalSec = 8f;

        /// <summary>
        /// 体験者の背丈が測れないときの代用 (m)。HMD が取れていない状況
        /// （位置合わせ直後・トラッキングロスト）でも入れ替わりを止めないための値。
        /// </summary>
        public const float FallbackHumanHeightM = 1.65f;

        /// <summary>体験者の背丈としてあり得る範囲 (m)。外れ値で人型が化け物になるのを防ぐ。</summary>
        public const float MinHumanHeightM = 1.05f;
        public const float MaxHumanHeightM = 2.10f;

        /// <summary>
        /// 覆い切った瞬間に 1 回だけ走らせる全画面の乱れ（強さ / 秒）。
        ///
        /// ⚠ <b>これは「入れ替わりを隠す」ためのものではない。</b> 隠すのは人型のノイズの仕事で、
        ///   こちらは<b>人型の外で同時に起きる差し替え</b>（3 周目 A なら左半分が凍結から録画へ、
        ///   4 周目 A なら左半分の人形の群れが消える）を覆うためのもの。人型が既に体験者を
        ///   覆い切った後なので、「人 → 人形」の読みは 1 ビットも壊れない。
        /// ⚠ 短く弱くする。長くすると結局「全画面の砂嵐で変えた」に戻る。
        /// </summary>
        public const float VeilLevel = 0.55f;
        public const float VeilSec = 0.20f;

        /// <summary>1 フレーム分の出力。<see cref="SwapMorphFx"/> がそのまま uniform と背丈へ流す。</summary>
        public readonly struct Sample
        {
            /// <summary>入れ替わりが進行中か。false のときは他の値を読まない。</summary>
            public readonly bool active;

            /// <summary>ノイズが人型の中をどこまで埋めたか 0..1（足元 → 頭）。</summary>
            public readonly float cover;

            /// <summary>図形が「本物の人形」になっている度合い 0..1（0 = 砂 / 1 = 人形そのもの）。</summary>
            public readonly float solid;

            /// <summary>いまの人型の背丈 (m)。</summary>
            public readonly float heightM;

            /// <summary>影・接地影の濃さの倍率 0..1。砂の人型に影は落ちない。</summary>
            public readonly float ground;

            /// <summary>このフレームで覆い切った（＝画面を差し替える 1 フレーム）。</summary>
            public readonly bool justCovered;

            /// <summary>
            /// このフレームで<b>晴れる段へ入った</b>（＝人型を人形へ差し替える 1 フレーム）。
            /// 縮み切って、まだ砂に覆われている瞬間。ここで姿を替えれば 1 画素も見えない。
            /// </summary>
            public readonly bool justSettling;

            /// <summary>このフレームで入れ替わりが終わった。</summary>
            public readonly bool justFinished;

            public Sample(bool active, float cover, float solid, float heightM, float ground,
                          bool justCovered, bool justSettling, bool justFinished)
            {
                this.active = active;
                this.cover = cover;
                this.solid = solid;
                this.heightM = heightM;
                this.ground = ground;
                this.justCovered = justCovered;
                this.justSettling = justSettling;
                this.justFinished = justFinished;
            }

            public static Sample Idle => new Sample(false, 0f, 0f, 0f, 1f, false, false, false);
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
            float cover, solid, h01;
            if (t < RiseFrac)
            {
                cover = Smooth(t / RiseFrac);
                solid = 0f;
                h01 = 0f;
            }
            else if (t < RiseFrac + MorphFrac)
            {
                cover = 1f;
                solid = 0f;
                h01 = Smooth((t - RiseFrac) / MorphFrac);
            }
            else
            {
                float u = Smooth((t - RiseFrac - MorphFrac) / clearFrac);
                h01 = 1f;
                // 人形になる向きは砂が実体へ寄る。人へ戻る向きは砂が引いて下の映像が出る。
                solid = _dir == Dir.ToDoll ? u : 0f;
                cover = _dir == Dir.ToDoll ? 1f : 1f - u;
            }

            bool justCovered = !_covered && cover >= 0.999f;
            if (justCovered) _covered = true;
            _coverPeak = Mathf.Max(_coverPeak, cover);

            // 晴れる段へ入った縁。**縮み切っていて、まだ砂に覆われている**唯一の瞬間なので、
            // 人型を人形へ差し替えるならここしかない。
            bool justSettling = !_settling && t >= RiseFrac + MorphFrac;
            if (justSettling) _settling = true;

            bool justFinished = t >= 1f;
            if (justFinished) _active = false;

            return new Sample(true, cover, solid, LerpHeight(h01), Ground(solid),
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
        /// 影と接地影の濃さ。<b>砂の人型は光を遮らない</b>ので、覆われているあいだは 0。
        ///
        /// 人 → 人形は始まりに人形が居ない（映像の中の本物の体験者が立っている）ので、
        /// 影が戻るのは実体が出てくる<b>晴れる段だけ</b>。
        /// 人形 → 人は始まりに人形が居るので、覆われていく<b>湧く段で落ちて、戻らない</b>
        /// （戻る先はライブ映像で、そこには本物の影が最初から写っている）。
        /// </summary>
        private float Ground(float solid)
            => _dir == Dir.ToDoll ? solid : 1f - _coverPeak;

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
        ///   食い違った砂の figure</b> になる。
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
