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

        /// <summary>
        /// <b>映像の中で、体験者の場所に人形が立っているか</b>（<c>ShowCgLayer.IsVisible</c>）。
        /// 立った縁 ＝ <b>入れ替わった瞬間</b>で、そこから人形が笑い始める
        /// （2026-08-18・<c>canon/LEDGER.md</c> 0086）。
        ///
        /// ⚠ <b>「3 周目」とは書かない。</b> 人形が立っていること自体を見るので、
        /// 台本が変わっても追随する（0062 で <see cref="markWaiting"/> をそう作ったのと同じ）。
        /// ⚠ 4 周目 A の締めも人形が立っているが、そちらは <see cref="markWaiting"/> が立つので
        /// 区別できる（あちらは群れ・<see cref="SoundBedGains.dolls"/>）。
        /// </summary>
        public bool dollPresent;

        /// <summary>
        /// いま居る区間のカメラ（0 = A / 1 = B / 2 = C。-1 = 未確定）。
        /// <b>笑う人形が増えるのは C だけ</b>という判断にだけ使う（<c>TimelineDirector.CurrentCamera</c>）。
        /// </summary>
        public int camera;

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
        /// <b>人形の笑い</b>（`bed_dolls_laugh`）。締めのカットが報告を待っているあいだだけ 1。
        /// **報告を押すまでループする**（2026-08-16・<c>canon/LEDGER.md</c> 0066）。
        ///
        /// ⚠ これは敷く音の器に載せているが、**地の音ではない**（-20 LUFS ＝ 地より 12dB 上）。
        /// 器を借りている理由は 1 つだけで、<b>ループして出し入れできるのがここしか無い</b>から。
        /// </summary>
        public float dolls;

        /// <summary>
        /// <b>入れ替わった人形（一人）の笑い</b>（`bed_doll_one`）。映像の中で体験者の場所に
        /// 人形が立っているあいだ鳴り続ける（2026-08-18・<c>canon/LEDGER.md</c> 0086）。
        /// </summary>
        public float dollOne;

        /// <summary>同・増えた 2 体（`bed_dolls_grow_a`）。<b>C でだけ入ってくる。</b></summary>
        public float dollsGrowA;

        /// <summary>
        /// 同・さらに増えた 4 体（`bed_dolls_grow_b`）。C の後半で入ってくる。
        ///
        /// ⚠ <b>3 枚は足し算で「増える」</b>（入れ替えではない）。1 枚を大きくすると
        /// 「人形が近づいてくる」に聞こえて、数が増えたことにならない。
        /// </summary>
        public float dollsGrowB;
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

        /// <summary>人形が笑っているあいだ、劇伴をどこまで引くか。**鳴っているあいだずっと。**</summary>
        public const float DollsDuck = 0.85f;

        // 人形の笑いの出入り（半減期・秒）。⚠ 入りは速く、切れは**押した手応え**なので更に速い。
        private const float DollsRiseSec = 0.25f;
        private const float DollsFallSec = 0.35f;

        // ---- 入れ替わった人形の笑い（3 周目・`canon/LEDGER.md` 0086）--------------
        //
        // 入れ替わった瞬間から鳴り始めるので、入りは速い（縁で 1 声目が立つ）。
        // 切れは**画から人形が消えたとき**なので、押した手応えより緩い。
        private const float DollOneRiseSec = 0.25f;
        private const float DollOneFallSec = 0.8f;

        /// <summary>
        /// 締めの群れへ譲るときの切れ（半減期・秒）。**画から人形が消えるときより速い。**
        ///
        /// ⚠ 実機で踏んだ（2026-08-18）: 4 周目 A へ入った瞬間、群れは 0.25 秒で立つのに
        /// 一人ぶんが 0.8 秒で引いたので、**2 秒間 2 つが重なって鳴っていた**（判定が捕まえた）。
        /// 群れは笑いの壁なので、そこへ吸い込まれる速さで引く。
        /// </summary>
        private const float DollYieldFallSec = 0.2f;

        /// <summary>
        /// 人形が画から消えても、これだけは笑いを保つ（秒）。
        ///
        /// ⚠ <b>区間の継ぎ目で穴を開けないための保持。</b> 3 周目は区間ごとに別の演出が走るので、
        /// カットの入れ替わりで <c>ShowCgLayer</c> が数フレーム消える。保持が無いと、
        /// A → B → C の継ぎ目で笑いが 3 回途切れる（＝ 人形が消えて戻ったように聞こえる）。
        /// </summary>
        public const float DollHoldSec = 1.5f;

        /// <summary>笑う人形が増えていく区間（C ＝ カメラ 2）。</summary>
        public const int SwellCamera = 2;

        /// <summary>
        /// C に入ってから人形が増え切るまでの秒数。
        ///
        /// ⚠ <b>実測の滞在に合わせた値</b>（2026-08-18）。自動走行の区間滞在は <b>8 秒</b>で、
        /// 14 秒にしていたら増え具合が 0.47 までしか行かず、<b>3 枚目が 1 度も入らなかった</b>。
        /// 9 秒なら 8 秒で 0.89 ＝ 3 枚とも鳴った状態で C を出る（長く居れば満杯で保つ）。
        /// </summary>
        public const float SwellRiseSec = 9f;

        /// <summary>C を離れたら戻る秒数（引き返しても、増えた人形がその場に残らない）。</summary>
        public const float SwellFallSec = 5f;

        /// <summary>人形が一人で笑っているあいだ、劇伴をどこまで引くか。**群れ（0.85）より浅い。**</summary>
        public const float DollSwapDuck = 0.45f;

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

        /// <summary>人形が画から消えてからの保持の残り（秒）。<see cref="DollHoldSec"/>。</summary>
        private float _dollHold;

        /// <summary>笑う人形の増え具合 0..1（C に居るあいだ増え、離れると戻る）。</summary>
        private float _swell01;

        /// <summary>締めの群れへ譲ったか（人形が画から消えるまで一人ぶんを戻さない）。</summary>
        private bool _swapMuted;

        /// <summary>同上（テレメトリが読む。**画にも一撃のログにも出ない**ので唯一の証拠）。</summary>
        public float Swell01 => _swell01;

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
            // 前の人の 3 周目 C で増えた人形を持ち越さない。
            _dollHold = 0f;
            _swell01 = 0f;
            _swapMuted = false;
        }

        /// <summary>一撃の音が鳴ったときに劇伴を短く引く（値は 0..1・そのまま最大値で上書き）。</summary>
        public void PushSpotDuck(float amount)
        {
            if (amount > _spotDuck) _spotDuck = amount;
        }

        public SoundBedGains Tick(float dt, in SoundShowState s)
        {
            // 区間の継ぎ目で人形が数フレーム消えても笑いに穴を開けない（<see cref="DollHoldSec"/>）。
            var st = s;
            if (st.dollPresent) _dollHold = DollHoldSec;
            else if (_dollHold > 0f)
            {
                _dollHold = Math.Max(0f, _dollHold - dt);
                st.dollPresent = true;
            }

            // ⚠⚠ **締めの群れへ譲ったら、その人形が画から消えるまで戻さない。**
            //    実機で踏んだ（2026-08-18）: 報告を押した瞬間に群れが止まり、人形はまだ画に残って
            //    いるので**一人ぶんが 0.78 まで戻って 3 秒鳴った**。報告のあとに笑い声が戻るのは、
            //    現実へ返す所（`canon/LEDGER.md` 0050）の逆をやっている。
            //    ⚠ latch にせず「人形が消えるまで」にしてあるのは、`untilMark` のカットが
            //      別の周に著作されても効き続けないようにするため。
            if (!st.dollPresent) _swapMuted = false;
            else if (st.markWaiting) _swapMuted = true;
            if (_swapMuted) st.dollPresent = false;

            SoundBedGains t = Target(st);

            _cur.seal = SoundFade.Approach(_cur.seal, t.seal, SealRiseSec, SealFallSec, dt);
            _cur.room = SoundFade.Approach(_cur.room, t.room, RoomSec, dt);
            ApplyAmbientMix(dt, st.lap);
            _deviceTotal = SoundFade.Approach(_deviceTotal, t.device, DeviceRiseSec, DeviceFallSec, dt);
            _cur.noise = SoundFade.Approach(_cur.noise, t.noise, NoiseRiseSec, NoiseFallSec, dt);
            _cur.dolls = SoundFade.Approach(_cur.dolls, t.dolls, DollsRiseSec, DollsFallSec, dt);
            _cur.dollOne = SoundFade.Approach(_cur.dollOne, t.dollOne, DollOneRiseSec,
                                              st.markWaiting ? DollYieldFallSec : DollOneFallSec,
                                              dt);
            ApplyDollSwell(dt, t.dollOne > 0f, st.camera);
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
            // ⚠ **人形の笑いは退かせない**（`dolls` / `dollOne` / `dollsGrow*`）。退きは
            //    「節目の一撃を地に埋もれさせない」ための仕組みで、笑い自体がその事件。
            //    自分で自分を引いたら意味が無い。3 周目は切替の一撃が 9 回以上入るので、
            //    ここで引くと**笑いが切替のたびに凹む**。
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

        /// <summary>
        /// <b>C に居るあいだ、笑う人形が増えていく。</b>
        ///
        /// 判定は <c>canon/LEDGER.md</c> 0086（ユーザー逐語「3-A,3-Bでは一人の女の子が笑ってる感じ。
        /// 3-Cでは徐々に増えていく感じ」）。
        ///
        /// ⚠ <b>1 枚を大きくするのではなく、層を足す。</b> 音量を上げると「人形が近づいてくる」に
        /// 聞こえて、<b>数が増えたことにならない</b>。2 枚目（2 体）と 3 枚目（4 体）が
        /// 重なりながら順に入ってくる。
        ///
        /// ⚠ <b>時間で増やす。</b> ここが <see cref="Target"/> ではなく Tick に居るのは、
        /// 増え具合が状態ではなく<b>C に居た時間</b>だから（<see cref="ApplyAmbientMix"/> と同じ理由）。
        ///
        /// ⚠ 出し入れは<b>聴感直線</b>（<see cref="SoundFade.Curve.Perceptual"/>）。
        /// 振幅を直線で動かすと、後半だけ急に増えたように聞こえる。
        /// </summary>
        private void ApplyDollSwell(float dt, bool laughing, int camera)
        {
            bool growing = laughing && camera == SwellCamera;
            _swell01 = growing
                ? Math.Min(1f, _swell01 + dt / SwellRiseSec)
                : Math.Max(0f, _swell01 - dt / SwellFallSec);

            // 2 枚は重なりながら入る（間を空けると「2 体増えた」「4 体増えた」の段が付く）。
            float a = SoundFade.Gain(Clamp01(_swell01 / 0.55f), SoundFade.Curve.Perceptual);
            float b = SoundFade.Gain(Clamp01((_swell01 - 0.45f) / 0.55f), SoundFade.Curve.Perceptual);

            // ⚠ 一人ぶんの高さに乗せる。人形が画から消えたら、増えた 2 枚も一緒に引く
            //    （**家族の出入りは 1 か所で決める**）。
            _cur.dollsGrowA = _cur.dollOne * a;
            _cur.dollsGrowB = _cur.dollOne * b;
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

            // --- 人形の笑い -------------------------------------------------------
            // 締めのカットが報告を待っているあいだだけ鳴る（**押すまでループ**）。
            // ⚠ 鳴っているあいだは劇伴を深く退かせる。**一撃の退き（`PushSpotDuck`）と違って
            //    押し続ける**ので、7 秒でも 40 秒でも退いたまま保つ。
            g.dolls = s.markWaiting ? 1f : 0f;
            if (g.dolls > 0f) g.duck = Math.Max(g.duck, DollsDuck * g.dolls);

            // --- 入れ替わった人形の笑い（3 周目）----------------------------------
            // 映像の中で体験者の場所に人形が立っているあいだ、その人形が笑う。
            // **入れ替わった瞬間から**（`dollPresent` が立った縁で 1 声目が鳴る）。
            //
            // ⚠ 締めの群れ（`dolls`）とは**同時に鳴らさない**。4 周目 A も人形は立っているが、
            //    あちらは「たくさん出てくる」場面なので、一人ぶんの笑いが混ざると数が濁る。
            // ⚠ 増える 2 枚は時間で決まるので Tick（`ApplyDollSwell`）が持つ。
            g.dollOne = s.dollPresent && !s.markWaiting ? 1f : 0f;
            if (g.dollOne > 0f) g.duck = Math.Max(g.duck, DollSwapDuck * g.dollOne);

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
                // 作業中に人形を笑わせない（引くのではなく黙らせる）。
                g.dolls = 0f;
                g.dollOne = 0f;
                g.roomOpen = 1f;
                g.duck = 1f;
            }

            // 体験が終わった後は何も残さない（最後に鳴っているのは無音、が正しい）。
            if (s.phase == ShowPhase.Finished && !s.outroActive)
            {
                g.seal = g.room = g.device = g.noise = g.dolls = g.dollOne = 0f;
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
