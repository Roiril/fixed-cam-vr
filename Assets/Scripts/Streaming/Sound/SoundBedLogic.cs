#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>体験のうち、音が読む値だけを集めたもの（毎フレーム作る struct）。</summary>
    public struct SoundShowState
    {
        /// <summary>タイトルの層が画面を持っているか（**真っ暗で A を待っている間も true**）。</summary>
        public bool titleVisible;

        /// <summary>
        /// <b>題字が実際に立っているか。</b> 2026-08-12 にタイトルの流れが変わり、
        /// 「画面を持っている」と「字が出ている」が別になった（A を押すまでは真っ暗）。
        /// 音の一撃を鳴らす縁はこちら。
        /// </summary>
        public bool titleGlyphShowing;
        /// <summary>導入演出が進行中か。</summary>
        public bool introActive;
        public IntroStage introStage;
        public IntroWeights introWeights;
        /// <summary>終幕が進行中か。</summary>
        public bool outroActive;
        public OutroStage outroStage;

        /// <summary>
        /// 終幕のいまの段の進み 0..1。<b>装置の声が「絵と同じ速さで」細るために要る</b>
        /// （段だけだと 6 秒の Flicker のあいだ音が一定になり、消えていく画と食い違う）。
        /// </summary>
        public float outroProgress01;
        public ShowPhase phase;
        /// <summary>映像の解像度の劣化（0 = 新しい / 1 = 落ち切った）。<see cref="ScreenDecayLogic"/>。</summary>
        public float decay;

        /// <summary>
        /// いまの周（1 始まり）。<b>環境音を周ごとに入れ替えるために読む</b>
        /// （2026-08-15・<c>canon/LEDGER.md</c> 0049）。0 以下は 1 周目として扱う。
        /// </summary>
        public int lap;
        /// <summary>信号断の強さ（<see cref="SignalLostFx"/>）。</summary>
        public float signalLost;
        /// <summary>位置合わせ作業中（スタッフが実物に線を重ねている）。</summary>
        public bool registrationActive;

        /// <summary>
        /// <b>締めのカットが体験者の報告を待っているか</b>
        /// （<c>TimelineDirector.IsWaitingForVisitorMark</c>）。
        /// ここが立った縁 ＝ <b>人形がたくさん出てくる所</b>で、人形の笑いが鳴る
        /// （2026-08-16・<c>canon/LEDGER.md</c> 0062）。
        ///
        /// ⚠ <b>敷く音は読まない。</b> 読むのは <see cref="SoundCueLogic"/> だけ。
        /// </summary>
        public bool markWaiting;

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
        /// <summary>
        /// 封印の箱の唸り。**3D**（箱に定位する）。
        /// ⚠ <b>2026-08-15 から常に 0</b> — 箱を退避したので定位する先が無い（音源は残してある）。
        /// </summary>
        public float seal;
        /// <summary>部屋のトーン（**3 本の合計**。どれをどれだけ鳴らすかは下の取り分）。</summary>
        public float room;

        /// <summary>
        /// 周ごとの環境音の取り分。<b>二乗の和が常に 1</b>（等パワー）なので、
        /// <see cref="room"/> に掛けても入れ替えの最中に音の密度が凹まない。
        ///
        /// 1 周目 = 合成の <c>bed_room</c> / 2 周目・3 周目 = ユーザー指定の音源
        /// （2026-08-15・<c>canon/LEDGER.md</c> 0049）。
        /// </summary>
        public float roomLap1, roomLap2, roomLap3;
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
    /// 2. <b>音は絵より先に来る。</b> 段 2（色が抜ける）で装置の声が入り始め、画が変わり切るのは段 5。
    ///    これが 13 秒の導入を「1 つの出来事」として繋ぐ（切替の J カットと同じ考え）
    /// 3. <b>隔離は帯域で表す。</b> 段 5 で会場が黒へ落ちるとき、部屋の音は<b>小さくならず狭くなる</b>
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

        /// <summary>
        /// 周ごとの環境音を入れ替える速さ（半減期・秒）。
        /// **気づかれない長さにする** — 半減期 2.5 秒なら、入れ替わりに約 8 秒かかる。
        /// 1 周 30 秒に対して 1/4 なので、区間を歩いているあいだに静かに入れ替わる。
        /// </summary>
        public const float AmbientCrossHalfLifeSec = 2.5f;

        private SoundBedGains _cur;
        private float _spotDuck;

        /// <summary>環境音の位置（0 = 1 周目 / 1 = 2 周目 / 2 = 3 周目）。整数の間を連続で動く。</summary>
        private float _ambPos;

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
            // 体験者が代わったら環境音も 1 周目へ戻す（**前の人の 3 周目から始めない**）。
            _ambPos = 0f;
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
            ApplyAmbientMix(dt, s.lap);
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

        /// <summary>
        /// 周ごとの環境音の取り分を進める。<b>入れ替わりに気づかれないための形。</b>
        ///
        /// 判定は <c>canon/LEDGER.md</c> 0049（ユーザー逐語「2周目と3周目の環境音を、以下にそれぞれ
        /// クロスフェードで入れ替えるようにして」「差し替えを気づかれないようにクロスフェードをお願い」）。
        ///
        /// ⚠ <b>周の番号で直接切り替えない。</b> 位置（0..2）を半減期で寄せ、その小数部を
        /// <see cref="SoundFade.Cross"/> に渡す。こうすると
        /// <b>どの瞬間も二乗の和が厳密に 1</b> ＝ 混ざっている最中に密度が凹まない
        /// （線形に混ぜると真ん中で -3dB の谷ができ、それが「切り替わった」の合図になる）。
        ///
        /// ⚠ <b>音量では入れ替えを表さない。</b> 3 本とも同じ高さ（-32 LUFS）へ揃えてあるので、
        /// 取り分だけが動く。片方が大きいと、どれだけ滑らかに混ぜても気づかれる。
        /// </summary>
        private void ApplyAmbientMix(float dt, int lap)
        {
            // 1 周目 = 0 / 2 周目 = 1 / 3 周目**以降** = 2（帰りの A も 3 周目の続きとして扱う）。
            float target = lap <= 1 ? 0f : (lap == 2 ? 1f : 2f);
            _ambPos = SoundFade.Approach(_ambPos, target, AmbientCrossHalfLifeSec, dt);

            int lo = (int)_ambPos;
            if (lo < 0) lo = 0; else if (lo > 1) lo = 1;
            SoundFade.Cross(_ambPos - lo, out float outGain, out float inGain);

            _cur.roomLap1 = lo == 0 ? outGain : 0f;
            _cur.roomLap2 = lo == 0 ? inGain : outGain;
            _cur.roomLap3 = lo == 0 ? 0f : inGain;
        }

        /// <summary>いまの状態が求める「あるべき高さ」（寄せる前の目標）。**設計はここに書いてある。**</summary>
        public static SoundBedGains Target(in SoundShowState s)
        {
            var g = new SoundBedGains { roomOpen = 1f };

            // --- 封印の箱 -------------------------------------------------------
            // ⚠⚠ **2026-08-15 に黙らせた**（`canon/LEDGER.md` 0044）。封印の箱を退避したので、
            //    この唸りは**定位する先が無い**（`bed_seal` は 3D で箱の面に置いていた）。
            //    タイトルの黒の下で先に鳴らす J カットもここが担っていたので、いまは
            //    黒の下は無音 — 代わりの案は `canon/OPEN.md`。音源は消していない。
            g.seal = 0f;

            // --- 部屋 -----------------------------------------------------------
            if (s.titleVisible) g.room = 0f;                       // タイトルは世界の手前
            else if (s.outroActive) g.room = OutroRoom(s.outroStage, s.outroProgress01);
            else if (s.introActive) g.room = 0.85f;
            else g.room = RoomInRun;

            // 隔離が閉じるほど部屋が狭くなる。**音量ではなく帯域**（`roomOpen`）で表す。
            float sealed01 = Math.Max(s.introWeights.shell, 0f);
            g.roomOpen = 1f - (1f - RoomOpenSealed) * Clamp01(sealed01);

            // --- 装置 -----------------------------------------------------------
            // ⚠ **絵より先に来る。** 段 2（色が抜ける）で入り始め、段 5 で画が変わる。
            if (s.titleVisible) g.device = 0f;
            else if (s.outroActive) g.device = OutroDevice(s.outroStage, s.outroProgress01);
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

        // 段ごとの装置。**段 2 から入り始める**（絵の格下げと同じ進行度で）。
        // 画が変わり切るのは段 5 なので、音の方が先に来る。
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

        /// <summary>
        /// 終幕の装置。<b>ちかちかしながら消えていくのと同じ進みで細っていく。</b>
        /// **山を作らない**（決め台詞を置かない）。
        ///
        /// ⚠ <b>ちらつきそのものを音へ写さない。</b> 電力は 6〜24Hz で跳ねるので、
        /// そのまま音量に掛けると低い唸りには「切れかけ」ではなく歪みとして乗る
        /// （しかも 90Hz のフレームで階段状に切り替わるのでクリックが出る）。
        /// 写すのは<b>痩せていく方だけ</b>で、ちかちかは画が担う。
        /// </summary>
        private static float OutroDevice(OutroStage st, float p)
        {
            if (st != OutroStage.Flicker) return 0f;   // Dark 以降は消えている
            return 1f - Smooth(Clamp01(p));
        }

        /// <summary>
        /// 終幕の部屋。<b>装置が黙るぶんだけ、体験者が実際に立っている部屋が前へ出る。</b>
        ///
        /// ⚠ <b>終幕で足す音は 1 つも無い。</b> 装置が引いた後に残るのは元からあった部屋の音だけで、
        /// それも <see cref="ShowPhase.Finished"/> の分岐が Done で無音へ落とす
        /// （`rules/sound-design.md`「終わりに音を残さない」）。
        /// </summary>
        private static float OutroRoom(OutroStage st, float p)
        {
            const float RoomAlone = 0.85f;
            if (st == OutroStage.Flicker)
                return RoomInRun + (RoomAlone - RoomInRun) * Smooth(Clamp01(p));
            return RoomAlone;                 // Dark / Report — 部屋だけが残る
        }

        private static float Smooth(float t) => t * t * (3f - 2f * t);

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
