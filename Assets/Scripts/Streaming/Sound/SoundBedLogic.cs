#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>体験のうち、音が読む値だけを集めたもの（毎フレーム作る struct）。</summary>
    public struct SoundShowState
    {
        /// <summary>タイトルが立っているか（黒の中に題字）。</summary>
        public bool titleVisible;
        /// <summary>導入演出が進行中か。</summary>
        public bool introActive;
        public IntroStage introStage;
        public IntroWeights introWeights;
        /// <summary>終幕が進行中か。</summary>
        public bool outroActive;
        public OutroStage outroStage;
        public ShowPhase phase;
        /// <summary>映像の解像度の劣化（0 = 新しい / 1 = 落ち切った）。<see cref="ScreenDecayLogic"/>。</summary>
        public float decay;
        /// <summary>信号断の強さ（<see cref="SignalLostFx"/>）。</summary>
        public float signalLost;
        /// <summary>位置合わせ作業中（スタッフが実物に線を重ねている）。</summary>
        public bool registrationActive;

        public static SoundShowState Idle => new SoundShowState
        {
            introStage = IntroStage.Off,
            outroStage = OutroStage.Off,
            phase = ShowPhase.Intro,
            introWeights = IntroWeights.Inactive,
        };
    }

    /// <summary>敷く音それぞれの倍率（0..1）と、部屋の狭さ・劇伴を引く量。</summary>
    public struct SoundBedGains
    {
        /// <summary>封印の箱の唸り。**3D**（箱に定位する）。</summary>
        public float seal;
        /// <summary>部屋のトーン。</summary>
        public float room;
        /// <summary>装置（カメラ・伝送・スクリーン）の声。新しい方。</summary>
        public float device;
        /// <summary>同・痩せた方。<see cref="SoundShowState.decay"/> で等パワーに混ざる。</summary>
        public float deviceWorn;
        /// <summary>信号断の砂嵐。</summary>
        public float noise;
        /// <summary>
        /// 部屋の開き具合（1 = 広い / 0 = 隔離されて狭い）。低域通過フィルタの開度に写す。
        /// **隔離は音量ではなくここで表す** — 音量を下げると「遠ざかった」、
        /// 帯域を閉じると「閉じ込められた」に聞こえる。
        /// </summary>
        public float roomOpen;
        /// <summary>劇伴（`BgmDirector`）を引く量（0 = そのまま / 1 = 無音）。</summary>
        public float duck;
    }

    /// <summary>
    /// **この作品の音の設計そのもの。** 体験の状態から、敷く音の倍率を決める。
    /// UnityEngine 非依存・dt 注入（<see cref="ScreenDecayLogic"/> / <see cref="CameraFeelLogic"/> と同じ流儀）。
    ///
    /// 設計の正本は <c>.claude/rules/sound-design.md</c>。要点は 3 つ:
    ///
    /// 1. <b>層は「装置の音」と「現実の音」の 2 つだけ。</b> 劇伴（作者の声）を足すと
    ///    「装置は正直に映している」という前提が壊れ、3 周目のすり替えに気づく瞬間の価値が下がる
    /// 2. <b>音は絵より先に来る。</b> 段 2（色が抜ける）で装置の声が入り始め、段 5 で画が変わる。
    ///    これが 13 秒の導入を「1 つの出来事」として繋ぐ（切替の J カットと同じ考え）
    /// 3. <b>隔離は帯域で表す。</b> 段 1 で会場が黒へ落ちるとき、部屋の音は<b>小さくならず狭くなる</b>
    /// </summary>
    public sealed class SoundBedLogic
    {
        // ---- 寄せる速さ（半減期・秒）------------------------------------------
        // ⚠ 上がる速さと下がる速さを分けている。同じにすると、砂嵐が唐突に消えたり
        //    隔離が閉じるのが間延びしたりする。
        private const float SealRiseSec = 1.2f;
        private const float SealFallSec = 0.5f;
        private const float RoomSec = 0.9f;
        private const float DeviceRiseSec = 1.6f;    // 装置は「にじり寄る」。速いと効果音になる
        private const float DeviceFallSec = 0.7f;
        private const float NoiseRiseSec = 0.08f;    // 断は一瞬で来る
        private const float NoiseFallSec = 0.45f;    // 戻りはゆっくり（唐突に静かにならない）
        private const float OpenSec = 0.35f;
        private const float DuckRiseSec = 0.05f;
        private const float DuckFallSec = 0.55f;

        /// <summary>位置合わせ作業中に敷く音を何倍にするか。**スタッフが喋れる高さまで引く。**</summary>
        public const float RegistrationDuckScale = 0.22f;

        /// <summary>砂嵐が満ちたときに装置と部屋をどこまで引くか。</summary>
        public const float NoiseDuckScale = 0.35f;

        /// <summary>本編で部屋のトーンをどこまで下げるか（装置の声の下に敷く）。</summary>
        public const float RoomInRun = 0.34f;

        /// <summary>隔離が閉じ切ったときの部屋の開き具合。0 にはしない（完全に無響は不自然）。</summary>
        public const float RoomOpenSealed = 0.16f;

        /// <summary>
        /// 節目の一撃が鳴った瞬間に、**敷く音も一緒に退く**割合。
        ///
        /// 劇伴だけ引いて地の音を残すと、隔離が閉じる音も割れる音も**地に埋もれる**
        /// （2026-08-12 に通しの絵を見て気づいた。波形では事件が地と同じ高さに並んでいた）。
        /// 実際の録音でも、大きな出来事の瞬間は周囲の環境音が相対的に消える。
        ///
        /// ⚠ 0 にはしない。地ごと消すと「音が切れた」に聞こえて、装置が生きている前提が壊れる。
        /// </summary>
        public const float SpotBedDuck = 0.55f;

        private SoundBedGains _cur;
        private float _spotDuck;

        /// <summary>
        /// 装置の声の**合計**（新しい方 ＋ 痩せた方）。平滑化はこの 1 本で行う。
        ///
        /// ⚠⚠ <b>分配した結果を平滑化の状態へ書き戻してはいけない。</b> 2026-08-12 に
        /// <c>_cur.device</c>（＝ 分配後の「新しい方」）をそのまま次フレームの出発点にしていて、
        /// 周回が進むほど <c>cos</c> を毎フレーム掛け続ける形になり、**装置の合計が静かに痩せていった**
        /// （decay 0.2 で合成パワー 0.976）。絵の劣化は正しく進むのに音だけが小さくなるので、
        /// 実機で聞いても「そういう演出」に見えて気づけない。テストが数値で捕まえた。
        /// </summary>
        private float _deviceTotal;

        public SoundBedGains Gains => _cur;

        public void Reset()
        {
            _cur = default;
            _cur.roomOpen = 1f;
            _spotDuck = 0f;
            _deviceTotal = 0f;
        }

        /// <summary>一撃の音が鳴ったときに劇伴を短く引く（値は 0..1・そのまま最大値で上書き）。</summary>
        public void PushSpotDuck(float amount)
        {
            if (amount > _spotDuck) _spotDuck = amount;
        }

        public SoundBedGains Tick(float dt, in SoundShowState s)
        {
            SoundBedGains t = Target(s);

            _cur.seal = SoundFade.Approach(_cur.seal, t.seal, SealRiseSec, SealFallSec, dt);
            _cur.room = SoundFade.Approach(_cur.room, t.room, RoomSec, dt);
            _deviceTotal = SoundFade.Approach(_deviceTotal, t.device, DeviceRiseSec, DeviceFallSec, dt);
            _cur.noise = SoundFade.Approach(_cur.noise, t.noise, NoiseRiseSec, NoiseFallSec, dt);
            _cur.roomOpen = SoundFade.Approach(_cur.roomOpen, t.roomOpen, OpenSec, dt);

            // 痩せ具合は等パワーで混ぜる。線形にすると**進行の途中で装置の声が凹む**
            // （2 つの録り分けは無相関なので、絵の劣化が滑らかでも音だけ谷ができる）。
            SoundFade.Cross(Clamp01(s.decay), out float fresh, out float worn);
            _cur.deviceWorn = _deviceTotal * worn;
            _cur.device = _deviceTotal * fresh;

            _spotDuck = SoundFade.Approach(_spotDuck, 0f, DuckFallSec, dt);
            _cur.duck = SoundFade.Approach(_cur.duck, Math.Max(t.duck, _spotDuck),
                                           DuckRiseSec, DuckFallSec, dt);

            // 一撃が鳴っている間は地も退く（**事件を地に埋もれさせない**）。
            // ここで倍率を掛けるのは出力だけで、寄せている状態（_cur.*）には残さない
            // — 残すと引きが次フレームの出発点になって、地が静かに痩せていく
            //   （同日 `_deviceTotal` で踏んだのと同じ形の事故）。
            float keep = 1f - SpotBedDuck * _cur.duck;
            var outG = _cur;
            outG.seal *= keep;
            outG.room *= keep;
            outG.device *= keep;
            outG.deviceWorn *= keep;
            return outG;
        }

        /// <summary>いまの状態が求める「あるべき高さ」（寄せる前の目標）。**設計はここに書いてある。**</summary>
        public static SoundBedGains Target(in SoundShowState s)
        {
            var g = new SoundBedGains { roomOpen = 1f };

            // --- 封印の箱 -------------------------------------------------------
            // タイトルの黒の下で先に鳴らし始める（J カット）。A を押して黒が開いたとき、
            // 箱の声は**もう鳴っている**ので「場面が切り替わった」ではなく「幕が上がった」になる。
            if (s.titleVisible) g.seal = 0.55f;
            else if (s.introActive) g.seal = SealForStage(s);
            else g.seal = 0f;

            // --- 部屋 -----------------------------------------------------------
            if (s.titleVisible) g.room = 0f;                       // タイトルは世界の手前
            else if (s.outroActive) g.room = OutroRoom(s.outroStage);
            else if (s.introActive) g.room = 0.85f;
            else g.room = RoomInRun;

            // 隔離が閉じるほど部屋が狭くなる。**音量ではなく帯域**（`roomOpen`）で表す。
            float sealed01 = Math.Max(s.introWeights.shell, 0f);
            g.roomOpen = 1f - (1f - RoomOpenSealed) * Clamp01(sealed01);

            // --- 装置 -----------------------------------------------------------
            // ⚠ **絵より先に来る。** 段 2（色が抜ける）で入り始め、段 5 で画が変わる。
            if (s.titleVisible) g.device = 0f;
            else if (s.outroActive) g.device = OutroDevice(s.outroStage);
            else if (s.introActive) g.device = DeviceForStage(s);
            else g.device = 1f;

            // --- 砂嵐 -----------------------------------------------------------
            g.noise = Clamp01(s.signalLost);
            if (g.noise > 0f)
            {
                float keep = 1f - (1f - NoiseDuckScale) * g.noise;
                g.device *= keep;
                g.room *= keep;
            }

            // --- 位置合わせ中は全部引く（スタッフの作業音・声を通す）------------
            if (s.registrationActive)
            {
                g.seal *= RegistrationDuckScale;
                g.room *= RegistrationDuckScale;
                g.device *= RegistrationDuckScale;
                g.noise *= RegistrationDuckScale;
                g.roomOpen = 1f;
                g.duck = 1f;
            }

            // 体験が終わった後は何も残さない（最後に鳴っているのは無音、が正しい）。
            if (s.phase == ShowPhase.Finished && !s.outroActive)
            {
                g.seal = g.room = g.device = g.noise = 0f;
                g.duck = 1f;
            }
            return g;
        }

        // 段ごとの封印の箱。段 4 で箱が割れて食われるぶんだけ引く。
        private static float SealForStage(in SoundShowState s)
        {
            switch (s.introStage)
            {
                case IntroStage.Black:
                case IntroStage.Real:
                case IntroStage.Degrade:
                case IntroStage.Structure:
                    return 1f;
                case IntroStage.Frame:
                    return 1f - Clamp01(s.introWeights.shatter);
                default:
                    return 0f;    // Swap 以降 ＝ 箱はもう画面の中に飲まれている
            }
        }

        // 段ごとの装置。**段 2 から入り始める**（絵の格下げと同じ進行度で）。
        private static float DeviceForStage(in SoundShowState s)
        {
            switch (s.introStage)
            {
                case IntroStage.Black:
                case IntroStage.Real:
                    return 0f;
                case IntroStage.Degrade:
                    return 0.55f * Clamp01(s.introWeights.degrade);
                case IntroStage.Structure:
                    return 0.55f;
                case IntroStage.Frame:
                    return 0.55f + 0.30f * Clamp01(s.introWeights.shatter);
                case IntroStage.Swap:
                    return 0.85f + 0.15f * Clamp01(s.introWeights.live);
                default:
                    return 1f;
            }
        }

        // 終幕は導入の逆をたどる。**山を作らない**（決め台詞を置かない）。
        private static float OutroDevice(OutroStage st)
        {
            switch (st)
            {
                case OutroStage.Warm: return 1f;
                case OutroStage.Unswap: return 0.45f;
                case OutroStage.Open: return 0.15f;
                default: return 0f;
            }
        }

        private static float OutroRoom(OutroStage st)
        {
            switch (st)
            {
                case OutroStage.Warm: return RoomInRun;
                case OutroStage.Unswap: return 0.55f;
                case OutroStage.Open: return 0.85f;
                case OutroStage.Restore: return 0.85f;
                default: return 0.45f;          // Hold — 現実だけが残る。ここから静かに引く
            }
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
