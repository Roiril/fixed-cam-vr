#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 終幕の段。<b>導入の逆再生ではない</b>（2026-08-15 に作り替えた・<c>canon/LEDGER.md</c> 0048）。
    /// パススルーへは戻さず、黒のまま装置だけが力尽きる。
    ///
    /// ⚠⚠ <b>2026-08-23 に <c>Flicker</c>（電池切れのちかちか・6 秒）を <see cref="Collapse"/> へ
    /// 置き換えた</b>（<c>canon/LEDGER.md</c> 0111・ユーザー逐語「テンポが悪く、終わったかが
    /// わかりずらい」「ちかちかする演出は目に悪いのでやめたい」）。
    /// </summary>
    public enum OutroStage
    {
        /// <summary>出していない。</summary>
        Off,
        /// <summary>
        /// <b>通信が切れる砂嵐</b>（2026-10-21・博士タブレットの説明の終わりと同じ消え方）。
        /// 画に砂嵐が乗り、ざ・ざ・ざと 3 度強まる。強まり方はシェーダが持ち、ここは進みだけを出す
        /// （<see cref="OutroLogic.ScreenNoise"/>）。<b>単調に増え、戻らない</b>。
        /// </summary>
        Noise,
        /// <summary>
        /// <b>ブラウン管の電源断。</b> 画が縦に潰れて横一本の線になり、線が中央へ縮んで点になり、
        /// 残光が消える。<b>単発・一方向・戻らない</b>（反復する明滅は 1 度も無い）。
        /// </summary>
        Collapse,
        /// <summary>何も無い黒。間。</summary>
        Dark,
        /// <summary>報告の文字が浮かぶ。</summary>
        Report,
        /// <summary>出し切った。<b>文字はここでも出したまま</b>（次のランで <see cref="OutroLogic.Disable"/>）。</summary>
        Done,
    }

    /// <summary>段が終わった合図。</summary>
    public enum OutroEvent { None, Finished }

    /// <summary>
    /// 終幕（本編 → 装置が力尽きて、報告を出して終わる）の判断・計時。UnityEngine 非依存・dt 注入。
    ///
    /// <b>2026-08-15 に骨格ごと作り替えた</b>（<c>canon/LEDGER.md</c> 0048）。
    /// 旧実装は導入を逆に辿ってパススルーへ戻す 5 段（Warm / Unswap / Open / Restore / Hold）だった。
    /// 戻す先が無くなったので、覆い・隔離・パススルーには<b>一切触らない</b> —
    /// 本編の時点で背景は既に黒（パススルーは切れている）なので、<b>何もしないことが「黒のまま」</b>。
    ///
    /// <b>2026-08-23 に消え方を作り替えた</b>（<c>canon/LEDGER.md</c> 0111・ユーザー逐語
    /// 「テンポが悪く、終わったかがわかりずらい」「ちかちかする演出は目に悪いのでやめたい」）。
    /// 設計は <c>reports/2026-08-23_outro-redesign.html</c>。
    ///
    /// ⚠⚠ <b>「ちかちか」（6〜24Hz の明滅・6 秒）は廃止した。</b> 理由は 2 つあり、どちらも
    /// 元に戻す理由にならない:
    /// <list type="number">
    ///   <item>6〜24Hz は光過敏性発作の危険帯（3〜30Hz）のただ中。VR で視界の中心を占める面で
    ///         6 秒間やってよい速さではない</item>
    ///   <item><b>明滅は往復する動きなので、原理的に終端に読めない。</b> しかも体験は 3 分間
    ///         「画面が変になる ＝ 異変」を教え続けているので、その語彙で終わろうとすると
    ///         最後の 1 手が異変と区別できない</item>
    /// </list>
    ///
    /// 代わりに置いたのが<b>ブラウン管の電源断</b>（<see cref="OutroStage.Collapse"/>）。
    /// 秩序だった一方向の一回性の事象で、体験がまだ一度も見せていない身振り。
    /// <b>意味は「装置が調査を終えて自分で落ちた」</b> — 誰かが外から切った、にはしない
    /// （外部の意図を示唆すると「まだ何かいる」と読まれ、直後に死んだ装置が報告を打つ矛盾も立つ）。
    ///
    /// ⚠⚠ <b>2026-10-21 に電源断の前へ砂嵐の段（<see cref="OutroStage.Noise"/>）を足した。</b>
    /// ユーザー指定「メインスクリーンの消失の仕方を、タブレットで博士が消えるときと同じような感じに」。
    /// 博士タブレットの <c>signal-cut.js</c>（砂嵐がざ・ざ・ざと強まる 0.95 秒 → 横一線に潰れて消える 0.26 秒）と
    /// 同じ尺と強まり方にした。砂嵐は強まるだけで往復しない（明滅ではない）。
    ///
    /// <b>形の寸法はシェーダが持ち、ここは進みだけを出す</b>（<c>_ScreenNoise</c> / <c>_ScreenCollapse</c>）。
    /// 数値として出す理由は 3 つとも変わっていない —
    /// (1) 実際に材質へ書いた値をテレメトリに出せる（「段が進んだ」ではなく「画に出た」の観測）、
    /// (2) EditMode テストで固定できる、(3) 実行のたびに同じ絵になる（乱数を使わない）。
    /// </summary>
    public sealed class OutroLogic
    {
        private OutroTiming _t = OutroTiming.Default;
        private OutroStage _stage = OutroStage.Off;
        private float _stageElapsed;
        private float _totalElapsed;
        private float _powerCutAt;

        public OutroStage Stage => _stage;
        public float StageElapsedSec => _stageElapsed;
        public float TotalElapsedSec => _totalElapsed;

        /// <summary>
        /// 終幕の頭から、管が潰れ始める（＝ 電源が落ちる）までの秒。帰還不能（砂嵐も潰れも見せない）は 0。
        /// <b>電源断の音はここで鳴る</b>（<c>SoundShowState.outroPowerCutSec</c>）。
        /// ⚠ 2026-10-21 に砂嵐の段を前に足したので、終幕の頭 ＝ 潰れ始め、ではなくなった。
        /// </summary>
        public float PowerCutAtSec => _powerCutAt;

        /// <summary>計時が進んでいるか（＝ まだ段が残っている）。</summary>
        public bool Active => _stage != OutroStage.Off && _stage != OutroStage.Done;

        /// <summary>
        /// 画に何かを出している / 出したままか。<b><see cref="OutroStage.Done"/> でも true</b> —
        /// 報告の文字は次のランまで消えないので、Director は <see cref="Active"/> ではなく
        /// こちらで配り続ける。
        /// </summary>
        public bool Presenting => _stage != OutroStage.Off;

        /// <summary>
        /// いまの段の進み 0..1（StatusHud・卓の heartbeat が読む「どこまで来たか」）。
        ///
        /// ⚠⚠ <b>音はここを読まない</b>（2026-08-23）。段相対なので、段の尺を 6.0 秒から
        /// 0.9 秒へ詰めた時に<b>音のランプだけが黙って 6.7 倍速くなる</b>
        /// （部屋の音が「せり上がる」音として聞こえる ＝ 誰も決めていない変更）。
        /// 音が読むのは <see cref="TotalElapsedSec"/> と、<c>SoundBedLogic</c> が持つ固定尺。
        /// </summary>
        public float StageProgress01
        {
            get
            {
                switch (_stage)
                {
                    case OutroStage.Noise: return Progress(_t.noiseSec);
                    case OutroStage.Collapse: return Progress(_t.collapseSec);
                    case OutroStage.Dark: return Progress(_t.darkSec);
                    case OutroStage.Report: return Progress(_t.reportFadeSec);
                    case OutroStage.Done: return 1f;
                    default: return 0f;
                }
            }
        }

        public void Configure(OutroTiming timing) => _t = timing.Sanitized();

        /// <summary>頭から始める。</summary>
        public void Begin(bool showCollapse = true)
        {
            // ⚠ 帰還不能（showCollapse=false）は砂嵐も見せない。通常の映像を再び出さないため。
            _stage = showCollapse ? OutroStage.Noise : OutroStage.Dark;
            _stageElapsed = 0f;
            _totalElapsed = 0f;
            _powerCutAt = showCollapse ? _t.noiseSec : 0f;
        }

        /// <summary>出さない（ラン開始・中止）。</summary>
        public void Disable()
        {
            _stage = OutroStage.Off;
            _stageElapsed = 0f;
            _totalElapsed = 0f;
            _powerCutAt = 0f;
        }

        public OutroEvent Tick(float dt)
        {
            if (dt < 0f) dt = 0f;
            if (!Active) return OutroEvent.None;

            _stageElapsed += dt;
            _totalElapsed += dt;

            switch (_stage)
            {
                case OutroStage.Noise:
                    if (_stageElapsed >= _t.noiseSec) Advance(OutroStage.Collapse);
                    return OutroEvent.None;

                case OutroStage.Collapse:
                    if (_stageElapsed >= _t.collapseSec) Advance(OutroStage.Dark);
                    return OutroEvent.None;

                case OutroStage.Dark:
                    if (_stageElapsed >= _t.darkSec) Advance(OutroStage.Report);
                    return OutroEvent.None;

                case OutroStage.Report:
                    if (_stageElapsed < _t.reportFadeSec) return OutroEvent.None;
                    _stage = OutroStage.Done;
                    return OutroEvent.Finished;

                default:
                    return OutroEvent.None;
            }
        }

        /// <summary>
        /// スクリーンへ掛ける電力 0..1（<c>_ScreenPower</c>）。
        /// <b>出していない間は 1</b>（＝ 本編と同じ見え。0 を返す側へ倒すと画がまるごと消える）。
        ///
        /// ⚠⚠ <b>潰れているあいだ（<see cref="OutroStage.Collapse"/>）は 1 のまま。</b>
        /// 「装置が死んだ」を言うスイッチはこの 1 本だけで、消え方そのものは
        /// <see cref="ScreenCollapse"/> が持つ。**二重に暗くしない** — 両方で落とすと、
        /// 線になる前に画が沈んで「潰れた」が読めなくなる。
        /// </summary>
        public float ScreenPower
        {
            get
            {
                switch (_stage)
                {
                    case OutroStage.Off: return 1f;
                    case OutroStage.Noise: return 1f;      // 砂嵐のあいだも電力は届いている
                    case OutroStage.Collapse: return 1f;   // まだ電力は届いている（画が潰れていく）
                    default: return 0f;                    // Dark / Report / Done ＝ 消えたまま
                }
            }
        }

        /// <summary>
        /// <b>ブラウン管の電源断の進み 0..1</b>（<c>_ScreenCollapse</c>）。
        /// 0 = ふつうの画 / 1 = 点が消え切った。
        ///
        /// <b>形（縦に潰れる → 横に縮む → 残光が消える の割合・線の太さ・明るさの上限）は
        /// シェーダが持つ。</b> ここが出すのは進みだけ — 2 か所に数字を書くと、
        /// 片方だけ直したときに黙って食い違う。
        ///
        /// ⚠ <b>単調に増える。</b> 反復も往復もしない（<c>canon/LEDGER.md</c> 0111）。
        /// ⚠ この段以外では 0。段が <see cref="OutroStage.Dark"/> へ進むのと同じ Tick で
        /// <see cref="ScreenPower"/> が 0 になるので、画は黒のまま連続する。
        /// </summary>
        public float ScreenCollapse
            => _stage == OutroStage.Collapse ? Progress(_t.collapseSec) : 0f;

        /// <summary>
        /// <b>砂嵐の進み 0..1</b>（<c>_ScreenNoise</c>）。0 = 砂嵐なし。
        /// <see cref="OutroStage.Noise"/> のあいだ 0 → 1、<see cref="OutroStage.Collapse"/> のあいだ 1
        /// （潰れていく帯の中身は砂嵐のまま）。それ以外は 0。
        ///
        /// ⚠ <b>強まり方（ざ・ざ・ざの位置・帯の裂け・転がる暗い帯）はシェーダが持つ。</b>
        /// 形の正は博士タブレットの <c>signal-cut.js</c>（<c>intensity</c> / <c>BURSTS</c>）で、
        /// シェーダの <c>CutNoiseIntensity</c> がその写し。ここに曲線を持たない。
        /// ⚠ 単調に増える（戻らない）。
        /// </summary>
        public float ScreenNoise
        {
            get
            {
                switch (_stage)
                {
                    case OutroStage.Noise: return Progress(_t.noiseSec);
                    case OutroStage.Collapse: return 1f;
                    default: return 0f;
                }
            }
        }

        /// <summary>報告の面の不透明度 0..1。</summary>
        public float ReportAlpha
        {
            get
            {
                switch (_stage)
                {
                    case OutroStage.Report: return Progress(_t.reportFadeSec);
                    case OutroStage.Done: return 1f;
                    default: return 0f;
                }
            }
        }

        private float Progress(float sec) => sec <= 0f ? 1f : Clamp01(_stageElapsed / sec);

        private void Advance(OutroStage next)
        {
            _stage = next;
            _stageElapsed = 0f;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }

    /// <summary>終幕の尺。<c>show.json</c> の <c>run.outro</c> から作る。</summary>
    [Serializable]
    public struct OutroTiming
    {
        /// <summary>
        /// <b>砂嵐が強まる尺</b>（2026-10-21）。博士タブレットの <c>signal-cut.js</c> の <c>NOISE_MS</c>（950ms）と同じ。
        /// </summary>
        public float noiseSec;

        /// <summary>
        /// <b>管が潰れて線になり、点になって消えるまで</b>（2026-08-23・<c>canon/LEDGER.md</c> 0111）。
        /// ⚠ 旧キー <c>flickerSec</c>（電池切れのちかちか・6 秒）の置き換え。
        /// </summary>
        public float collapseSec;

        /// <summary>消えてから報告が出るまでの、何も無い黒。</summary>
        public float darkSec;

        /// <summary>報告の文字が浮かび上がるまで。<b>出た後は消えない</b>（次のランまで）。</summary>
        public float reportFadeSec;

        /// <summary>
        /// コード既定。<b>合計 3.91s</b>（2026-10-21・砂嵐 0.95 ＋ 潰れ 0.26 ＋ 黒 1.2 ＋ 報告 1.5。旧 3.6s / さらに前 8.7s）。
        ///
        /// 報告の 1 文字目までが <b>2.1 秒</b>（旧 7.2 秒）、打ち終わりまでが
        /// <b>5.5 秒</b>（旧 10.6 秒）。ユーザー判定「テンポが悪く、終わったかがわかりずらい」
        /// （<c>canon/LEDGER.md</c> 0111）への答えの半分がこの尺で、もう半分は消え方そのもの。
        ///
        /// ⚠ <b><see cref="darkSec"/> は詰めていない。</b> 問題にされたのは 6 秒の側で、
        /// この「間」は報告に重みを与えている。**両方詰めると事務的になる。**
        /// </summary>
        public static OutroTiming Default => new OutroTiming
        {
            // ⚠ 2026-10-21 に博士タブレットの消え方（signal-cut.js の NOISE_MS 950 / OFF_MS 260）へ揃えた。
            //   旧既定は潰れ 0.9 秒だけ。報告の 1 文字目までは 2.1 → 2.41 秒。
            noiseSec = 0.95f,
            collapseSec = 0.26f,
            darkSec = 1.2f,
            reportFadeSec = 1.5f,
        };

        /// <summary>潰れの尺として受け付ける範囲（秒）。<see cref="PickCollapse"/>。</summary>
        public const float CollapseMinSec = 0.2f;
        public const float CollapseMaxSec = 2.0f;

        /// <summary>0 / 負 / 異常値を既定へ倒す（<c>run.outro</c> が欠けた show.json でも走る）。</summary>
        public OutroTiming Sanitized()
        {
            OutroTiming d = Default;
            return new OutroTiming
            {
                noiseSec = PickNoise(noiseSec, d.noiseSec),
                collapseSec = PickCollapse(collapseSec, d.collapseSec),
                darkSec = Pick(darkSec, d.darkSec),
                reportFadeSec = Pick(reportFadeSec, d.reportFadeSec),
            };
        }

        /// <summary>合計（報告を出し切るまで）。</summary>
        public float TotalSec => noiseSec + collapseSec + darkSec + reportFadeSec;

        /// <summary>砂嵐の尺として受け付ける範囲（秒）。長い砂嵐は「通信が切れた」ではなく故障の画になる。</summary>
        public const float NoiseMinSec = 0.3f;
        public const float NoiseMaxSec = 3.0f;

        private static float PickNoise(float v, float def)
            => v >= NoiseMinSec && v <= NoiseMaxSec ? v : def;

        private static float Pick(float v, float def) => v > 0.01f && v < 60f ? v : def;

        /// <summary>
        /// 潰れの尺だけ許容を絞る（<see cref="CollapseMinSec"/>〜<see cref="CollapseMaxSec"/>）。
        ///
        /// ⚠⚠ <b>ここを一般の <see cref="Pick"/>（0.01〜60 秒）にしてはいけない。</b>
        /// 旧キーを持つ show.json・端末キャッシュ・焼き込みには <c>flickerSec: 6.0</c> が
        /// 入っており、キー名を変えた以上その 6.0 は読まれない（＝ 既定 0.9 になる）が、
        /// **誰かが古い値をそのまま新キーへ写すと「6 秒かけて潰れる」が通ってしまう**。
        /// 電源断は一回性の事象なので、2 秒を超えたらそれはもう別の演出。
        /// </summary>
        private static float PickCollapse(float v, float def)
            => v >= CollapseMinSec && v <= CollapseMaxSec ? v : def;
    }
}
