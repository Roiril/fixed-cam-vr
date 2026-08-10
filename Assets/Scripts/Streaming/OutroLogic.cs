#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>終幕の段。<see cref="IntroStage"/> と対だが、**逆再生ではなく別の状態機械**。</summary>
    public enum OutroStage
    {
        /// <summary>出していない。</summary>
        Off,
        /// <summary>不可視。裏でパススルーを点火して、合成が始まるのを待つ。</summary>
        Warm,
        /// <summary>枠の中身が映像から現実へ戻る（導入の Swap の逆）。</summary>
        Unswap,
        /// <summary>枠が開いて、現実が視界いっぱいへ広がる（導入の Frame の逆）。</summary>
        Open,
        /// <summary>色と質感が戻る（導入の Degrade の逆）。</summary>
        Restore,
        /// <summary>素のパススルーで保持する。ここを過ぎたら終わり。</summary>
        Hold,
        /// <summary>終わった。</summary>
        Done,
    }

    /// <summary>終幕の観測値。</summary>
    public struct OutroInput
    {
        /// <summary>
        /// パススルーが**実際に合成されている**か。<b>「有効化を要求した」ではない。</b>
        /// 有効化は非同期で数百 ms かかるので、要求フラグで進めると終幕の 1 段目が黒で始まる。
        /// </summary>
        public bool passthroughReady;
    }

    /// <summary>段が終わった合図。</summary>
    public enum OutroEvent { None, Finished }

    /// <summary>
    /// 終幕（本編 → パススルーへ戻して終わる）の判断・計時。UnityEngine 非依存・dt 注入。
    ///
    /// <b>導入の逆再生だが、<see cref="IntroLogic"/> に向きを持たせるのではなく別ロジックにしてある。</b>
    /// 導入の各段は方向固有の条件（開始位置に居るか / 枠が中心に来たか / 映像が届いているか）を持っていて、
    /// 向きフラグを通すと分岐が倍になる。共有するのは <see cref="IntroWeights"/> の語彙と、
    /// 覆いの開口の式（<c>IntroVeil.BuildFramePlanes</c>）だけ — <b>開口の式が違うと閉じた形と開く形が食い違う</b>。
    ///
    /// <b>尺は導入から流用しない。</b> 導入の Swap 4.5s は「画面の中の人物が自分だと気づく」ための尺で、
    /// 終幕には要らない。逆に枠が開くところは導入より長い方が「戻ってきた」になる。
    ///
    /// 段の順序は導入の逆:
    ///   本編（映像・枠は閉じ切り）→ Unswap（中身が現実へ）→ Open（枠が開く）→ Restore（色が戻る）→ Hold
    /// </summary>
    public sealed class OutroLogic
    {
        /// <summary>パススルーの点火を待つ上限。ここを過ぎたら諦めて進む（黒いまま止まる方が悪い）。</summary>
        public const float WarmMaxSec = 1.5f;

        private OutroTiming _t = OutroTiming.Default;
        private OutroStage _stage = OutroStage.Off;
        private float _stageElapsed;
        private float _totalElapsed;

        public OutroStage Stage => _stage;
        public float StageElapsedSec => _stageElapsed;
        public float TotalElapsedSec => _totalElapsed;

        /// <summary>走っているか（＝ Director が覆いへ重みを配るべきか）。</summary>
        public bool Active => _stage != OutroStage.Off && _stage != OutroStage.Done;

        /// <summary>まだ画に何も出していない段か（＝ここで中止しても体験者には見えていない）。</summary>
        public bool Silent => _stage == OutroStage.Warm;

        public void Configure(OutroTiming timing) => _t = timing.Sanitized();

        /// <summary>頭から始める。</summary>
        public void Begin()
        {
            _stage = OutroStage.Warm;
            _stageElapsed = 0f;
            _totalElapsed = 0f;
        }

        /// <summary>出さない（ラン開始・中止）。</summary>
        public void Disable()
        {
            _stage = OutroStage.Off;
            _stageElapsed = 0f;
            _totalElapsed = 0f;
        }

        public OutroEvent Tick(float dt, OutroInput input)
        {
            if (dt < 0f) dt = 0f;
            if (!Active) return OutroEvent.None;

            _stageElapsed += dt;
            _totalElapsed += dt;

            switch (_stage)
            {
                case OutroStage.Warm:
                    // パススルーが実際に出るまで待つ。上限で諦めるのは、待ち続けて
                    // 「本編のまま固まる」方が体験者にとって悪いから。
                    if (input.passthroughReady || _stageElapsed >= WarmMaxSec) Advance(OutroStage.Unswap);
                    return OutroEvent.None;

                case OutroStage.Unswap:
                    if (_stageElapsed >= _t.unswapSec) Advance(OutroStage.Open);
                    return OutroEvent.None;

                case OutroStage.Open:
                    if (_stageElapsed >= _t.openSec) Advance(OutroStage.Restore);
                    return OutroEvent.None;

                case OutroStage.Restore:
                    if (_stageElapsed >= _t.restoreSec) Advance(OutroStage.Hold);
                    return OutroEvent.None;

                case OutroStage.Hold:
                    if (_stageElapsed < _t.holdSec) return OutroEvent.None;
                    _stage = OutroStage.Done;
                    return OutroEvent.Finished;

                default:
                    return OutroEvent.None;
            }
        }

        private void Advance(OutroStage next)
        {
            _stage = next;
            _stageElapsed = 0f;
        }

        /// <summary>いまの段の重み。<see cref="IntroWeights"/> と同じ語彙で、導入の各段を逆に辿る。</summary>
        public IntroWeights Weights
        {
            get
            {
                switch (_stage)
                {
                    case OutroStage.Warm:
                        // 本編と同じ見え。**ここで画を変えない**（パススルーの点火待ちを見せない）。
                        return IntroWeights.Inactive;

                    case OutroStage.Unswap:
                    {
                        // 枠の中身が映像 → 現実（まだ色は無い）。導入の Swap の逆。
                        float s = SmoothStep(Progress(_t.unswapSec));
                        return new IntroWeights
                        {
                            passthrough = s,
                            live = 1f - s,
                            frame = 1f,
                            degrade = 1f,
                            edge = 0f,
                            grain = 0.6f * s,
                            // 現実が戻る分だけ隔離も戻す。**まだ収容の中に居る**（開けるのは Restore）。
                            shell = s,
                        };
                    }

                    case OutroStage.Open:
                    {
                        // 枠が開く。導入の Frame の逆で、輪郭は開くにつれて戻す
                        //（枠の中へ集中させる必要がもう無い）。
                        float p = SmoothStep(Progress(_t.openSec));
                        return new IntroWeights
                        {
                            passthrough = 1f,
                            degrade = 1f,
                            edge = 0.3f + 0.7f * p,
                            frame = 1f - p,
                            grain = 0.6f,
                            live = 0f,
                            shell = 1f,
                        };
                    }

                    case OutroStage.Restore:
                    {
                        // 色・輪郭・粒が抜けて素の現実へ。**世界が最後に色を取り戻す。**
                        float p = SmoothStep(Progress(_t.restoreSec));
                        float d = 1f - p;
                        return new IntroWeights
                        {
                            passthrough = 1f,
                            degrade = d,
                            edge = d,
                            grain = 0.6f * d,
                            frame = 0f,
                            live = 0f,
                            // **ここで収容が解ける。** 色が戻るのと同じ速さで会場が返ってくる。
                            // 導入で閉じたものを終幕で開けないと、体験者は黒い箱の中に置き去りで終わる
                            // （スタッフが HMD を外しに来るのも見えない）。
                            shell = d,
                        };
                    }

                    case OutroStage.Hold:
                        return new IntroWeights { passthrough = 1f, frame = 0f, live = 0f, shell = 0f };

                    default:
                        return IntroWeights.Inactive;
                }
            }
        }

        private float Progress(float sec) => sec <= 0f ? 1f : Clamp01(_stageElapsed / sec);

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        private static float SmoothStep(float t)
        {
            t = Clamp01(t);
            return t * t * (3f - 2f * t);
        }
    }

    /// <summary>終幕の尺。<c>show.json</c> の <c>run.outro</c> から作る。</summary>
    [Serializable]
    public struct OutroTiming
    {
        public float unswapSec;
        public float openSec;
        public float restoreSec;
        public float holdSec;

        /// <summary>
        /// コード既定。導入（1.5 / 3.5 / 2.5 / 2.5 / 4.5 = 13.1s）より短く、開くところだけ長い。
        /// 合計 7.5s — 3 分の体験に足しても <c>targetSec</c> を大きくは超えない。
        /// </summary>
        public static OutroTiming Default => new OutroTiming
        {
            unswapSec = 1.5f,
            openSec = 2.5f,
            restoreSec = 2.0f,
            holdSec = 1.5f,
        };

        /// <summary>0 / 負 / 異常値を既定へ倒す（<c>run.outro</c> が欠けた show.json でも走る）。</summary>
        public OutroTiming Sanitized()
        {
            OutroTiming d = Default;
            return new OutroTiming
            {
                unswapSec = Pick(unswapSec, d.unswapSec),
                openSec = Pick(openSec, d.openSec),
                restoreSec = Pick(restoreSec, d.restoreSec),
                holdSec = Pick(holdSec, d.holdSec),
            };
        }

        /// <summary>合計（Warm は待ち時間なので含めない）。</summary>
        public float TotalSec => unswapSec + openSec + restoreSec + holdSec;

        private static float Pick(float v, float def) => v > 0.01f && v < 60f ? v : def;
    }
}
