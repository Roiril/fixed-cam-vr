#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>節目で 1 回だけ鳴らす音。値は <c>Resources/Sound/sfx_&lt;小文字&gt;</c> に対応する。</summary>
    public enum SoundCue
    {
        None = 0,
        /// <summary>タイトルが立った（りんの当たり）。</summary>
        TitleIn,
        /// <summary>A を押してタイトルが閉じる（息を呑む）。</summary>
        TitleOut,
        /// <summary>
        /// 隔離が閉じて会場が消える。
        /// ⚠ <b>2026-08-15 以降は鳴らない</b>（隔離が閉じる段が無くなった）。音源は残してある。
        /// </summary>
        SealClose,
        /// <summary>
        /// 段 4 の頭 — 現実が細かく割れてスクリーンへ吸い込まれる。
        /// 2026-09-25 に選ばれた生成動画の破砕と帰還を、1 本の音として鳴らす。
        /// </summary>
        Shatter,
        /// <summary>
        /// 旧スクリーン音。2026-09-25 から入口では鳴らさない。音源は保持する。
        /// </summary>
        ScreenOn,
        /// <summary>
        /// 段 5 の頭に鳴らしていた砂嵐。
        /// ⚠ <b>2026-08-16 以降は鳴らない</b>（<c>canon/LEDGER.md</c> 0057・「ノイズは鳴らさない」）。
        /// 音源は残してある。
        /// </summary>
        ScreenNoise,
        /// <summary>
        /// 枠の中がカメラ映像になる（装置が点く）。
        /// ⚠ <b>2026-08-15 以降は鳴らない</b>。入口音は <see cref="Shatter"/> が担う。
        /// 音源は残してある。
        /// </summary>
        Swap,
        /// <summary>
        /// 終幕 — 隔離が開いて現実が戻る。
        /// ⚠ <b>2026-08-15 以降は鳴らない</b>（<c>canon/LEDGER.md</c> 0048）。終幕はパススルーへ
        /// 戻さなくなり、隔離が開く段そのものが無くなった。音源は残してある。
        /// ⚠ 終幕に鳴るのは <see cref="PowerOff"/> 1 本だけ（2026-08-23・0125）。
        /// <b>この音が復活すると黙って 2 本重なる。</b>
        /// </summary>
        ShellOpen,
        /// <summary>映像の乱れ（<see cref="GlitchFx"/> と同時）。</summary>
        Glitch,
        /// <summary>
        /// 家鳴り。
        /// ⚠ <b>2026-08-15 以降は鳴らない</b>（<c>canon/LEDGER.md</c> 0049 —「導入の家鳴りは無くす」
        /// 「それ以降も、家鳴りは無くしてほしい」）。音源 <c>amb_creak_1/2</c> は残してある。
        /// </summary>
        Creak,
        /// <summary>
        /// 鈴。<b>完全にスクリーンになった所に 1 回だけ</b>（段 5 の頭から
        /// <see cref="SoundCueLogic.BellAfterSwapSec"/> 後）。誰も鳴らしていないのに鳴る。
        /// ⚠ 2026-08-16 に段 3 の頭からここへ移した（<c>canon/LEDGER.md</c> 0057）。
        /// </summary>
        Bell,
        /// <summary>
        /// <b>人形の呼びかけ</b>（「あーそぼー」）。2 周目 C の接近で、
        /// <b>人形視点の最後のカット（追いつき）が始まると同時に</b> 1 回だけ鳴る
        /// （<c>canon/LEDGER.md</c> 0109・ユーザー提供の録音）。
        ///
        /// ⚠⚠ <b>この列挙だけは <see cref="SoundCueLogic"/> が縁を検出しない。</b>
        /// カットが持つ音なので、画を差し替えるのと同じ行（<see cref="TakeRunner"/>）から
        /// <see cref="ShowSoundDirector.PlaySpot"/> で撃つ。毎フレーム外から状態を見る層に
        /// 置くと、0.8〜1.4 秒刻みで進む 2 周目 C の接近では頭を取りこぼす
        /// （連絡の面の打鍵・カメラ切替と同じ理由）。
        /// ここに居るのは<b>音源の登録簿としての意味</b>（`Awake` の先読みと `ev=sfx` の名前）。
        /// </summary>
        DollCall,
        /// <summary>
        /// <b>終幕 — 装置の電源が落ちる</b>（段 <see cref="OutroStage.Collapse"/> の頭で 1 回だけ）。
        /// 音源はユーザー提供の mp3（2026-08-23・<c>canon/LEDGER.md</c> 0125）。
        ///
        /// ⚠ <b>「終幕に足す音は 1 本も無い」（0048）はここで覆っている。</b> 画は
        /// 2026-08-23（0111）にブラウン管の電源断へ作り替わっており、
        /// <b>その画に対応する音をユーザーが指定した</b>。
        /// ⚠ 鳴らす縁は<b>終幕の頭から <see cref="SoundShowState.outroPowerCutSec"/> 経った所</b>
        /// （＝ 管が潰れ始める瞬間。2026-10-21 に前へ砂嵐 0.95 秒が入った）で、
        /// 段名は読まない — 段名を読むと、名前を変えた日に黙って無音になる
        /// （<see cref="SoundShowState.outroElapsedSec"/> の注記と同じ轍）。
        /// </summary>
        PowerOff,

        /// <summary>
        /// <b>人形 End で黒へ落ちる</b>（<c>canon/LEDGER.md</c> 0312）。この終わり方は管が潰れないので、
        /// <see cref="PowerOff"/> の代わりに終幕の頭で 1 度鳴る。
        /// </summary>
        DollEndFall,

        /// <summary>
        /// <b>目が 1 つ開く</b>（<c>canon/LEDGER.md</c> 0131・ユーザー提供の太鼓）。
        /// 6 種を回して音程と音量を散らす（切替音と同じ仕組み）。
        ///
        /// ⚠⚠ <b>この列挙も <see cref="SoundCueLogic"/> は縁を検出しない。</b>
        /// 開くのは <see cref="AnomalyEyes"/> の中の出来事で、**どの目が開いたか**まで要る
        /// （その目の方角から鳴らすため）。毎フレーム外から見る層には置けない。
        ///
        /// ⚠ <b>1 発ずつ鳴らすと 3 秒に 189 発になる。</b> 開くのは実測で 63 個/秒なので、
        /// <see cref="AnomalyEyes"/> 側が最短間隔で間引く。ここに居るのは
        /// <b>音源の登録簿としての意味</b>（<c>Awake</c> の先読みと <c>ev=sfx</c> の名前）。
        /// </summary>
        EyeOpen,

        /// <summary>
        /// <b>最初の大きい目が見開く</b>（同上）。1 回の体験で 1 度きり。
        ///
        /// ⚠⚠ <b>鳴らすのは兆しの段の頭</b>で、見開く瞬間ではない。素材の頭に無音を足して
        /// <b>いちばん大きいところが見開く瞬間（頭から 1.234 秒）へ来るように焼いてある</b>
        /// （<c>tools/ingest-sounds.py</c> の <c>ALIGN</c>）。実行時に足し引きしない。
        /// </summary>
        EyeBig,

        /// <summary>
        /// <b>スクリーンが 1 枚増える</b>（<c>canon/LEDGER.md</c> 0317・試作）。音はカメラの切り替えと同じ素材で、
        /// 増えた面の方角から鳴らす。間引きは <see cref="AnomalyEyes"/> の側。
        /// </summary>
        ScreenAdd,

        /// <summary>
        /// <b>2 周目 B の別の場所（子供部屋の動画）の物音</b>（<c>canon/LEDGER.md</c> 0374）。
        /// 電灯と木馬が揺れ、人形が畳に落ちる 6 秒。採用された動画の音を無加工で抜いたもの
        /// （<c>tools/ingest-kidsroom-audio.py</c>）。動画そのものは音を出さずに再生されている。
        ///
        /// ⚠⚠ <b>この列挙も <see cref="SoundCueLogic"/> は縁を検出しない。</b> 動画のカット
        /// （<see cref="TakeRunner.KidsRoomCueId"/>）が画面を取ったのと同じ行（<see cref="TakeRunner"/>）から
        /// <see cref="ShowSoundDirector.PlaySpot"/> で撃ち、別の場所が途中で畳まれたら
        /// <see cref="ShowSoundDirector.StopSpot"/> で退かせる（呼びかけと同じ構え）。
        /// ここに居るのは<b>音源の登録簿としての意味</b>（<c>Awake</c> の先読みと <c>ev=sfx</c> の名前）。
        /// </summary>
        KidsRoom,

        /// <summary>
        /// <b>人形の笑い声を 1 回</b>（カットの <c>dollLaugh</c>・2 周目 A の作り直し）。
        /// 3 周目の一人の笑い（<c>bed_doll_one</c>）の頭 4.8 秒を切り出し、尻 0.6 秒を聴感直線で退かせたもの
        /// （<c>tools/ingest-doll-laugh.py</c>・音量は変えない）。
        ///
        /// ⚠ <see cref="SoundCueLogic"/> は縁を検出しない。カットが画面を取ったのと同じ行（<see cref="TakeRunner"/>）から
        /// <see cref="ShowSoundDirector.PlaySpot"/> で撃つ（呼びかけ・子供部屋の音と同じ構え）。
        /// 2D（<c>SpatialAudio.MonoRequired</c> に入れない）・劇伴は退かせない（<see cref="SoundCueLogic.DuckFor"/> 0）。
        /// </summary>
        DollLaughOnce,

        /// <summary>
        /// 段 3 — テレビが奥から目の前まで寄ってくるあいだの砂嵐（<c>canon/LEDGER.md</c> 0383）。
        /// スクリーンについて動く（3D）。<see cref="TvOn"/> が鳴った縁で退く（クロスフェード）。
        /// </summary>
        TvNoise,
        /// <summary>
        /// 段 4 の頭 — テレビが目の前に来て電源が入る（0383）。テレビ方式では <see cref="ScreenOn"/> の代わりに鳴る。
        /// </summary>
        TvOn,
        /// <summary>
        /// 3 周目 C の増えるスクリーンの裏で 1 本（0383・2D）。鳴らすのは <see cref="AnomalyEyes"/>。
        /// </summary>
        WallRise,
        /// <summary>
        /// 増えるスクリーンが 1 枚消えるたび（0383）。4 本を順に回す（TV-Hardware01 と 02 が交互）。
        /// 消えた面の方角から鳴らす。間引きは <see cref="AnomalyEyes"/> の側。
        /// </summary>
        ScreenRemove,
        /// <summary>
        /// 増えるスクリーンの最後の面に笑う人形の顔が出た瞬間に 1 回（2026-11-15・ユーザー指定の音源・2D）。
        /// 鳴らすのは <see cref="AnomalyEyes"/>（<see cref="ScreenMultiply.FaceJustStarted"/> の縁）。
        /// 2-A のカットの笑い声（<see cref="DollLaughOnce"/>）とは別の音源。
        /// </summary>
        ScreenFaceLaugh,
    }

    /// <summary>
    /// 状態の変化を拾って「いま鳴らすもの」を決める（UnityEngine 非依存・dt 注入）。
    ///
    /// **既存の演出コードに 1 行も足さないための層。** 導入・終幕・タイトル・乱れの各実装は
    /// 音のことを知らないままでよく、ここが外から重みを見て縁を検出する
    /// （<see cref="CameraFeelFx"/> が post を外から書くのと同じ構え）。
    ///
    /// ⚠ <b>カメラ切替だけはここに無い。</b> 切替は暗転の下り 70ms の中に音を置く必要があり、
    /// フレームをまたぐと J カットが崩れる。<see cref="CameraSwitchDirector"/> が
    /// <see cref="SwitchAudioCue"/> を直接叩く既存の経路が正しい。
    /// </summary>
    public sealed class SoundCueLogic
    {
        /// <summary>隔離が閉じたとみなす殻の重み。</summary>
        public const float ShellFireAt = 0.04f;
        /// <summary>割れ始めたとみなす破砕の進み。</summary>
        public const float ShatterFireAt = 0.02f;
        /// <summary>乱れを鳴らす閾値。これを下回るまで再武装しない。</summary>
        public const float GlitchFireAt = 0.15f;
        public const float GlitchRearmAt = 0.06f;
        /// <summary>乱れの最短間隔（秒）。連射すると「壊れた」ではなく「効果音」になる。</summary>
        public const float GlitchMinIntervalSec = 0.30f;

        // ⚠ 家鳴りの間隔（`CreakMinSec` / `CreakMaxSec`）と、段 0 から鈴までの秒数（`BellAtSec`）は
        //    2026-08-15 に消した（`canon/LEDGER.md` 0049）。家鳴りは鳴らさない。
        //    鈴は時間ではなく**段 3 へ入った縁**で鳴るので、待つ秒数そのものが要らない。

        /// <summary>
        /// 段 5 へ入ってから鈴を鳴らすまでの秒数。<b>＝ すり替えのクロスフェードの尺</b>
        /// （<see cref="IntroLogic.SwapCrossfadeSec"/>）なので、鳴るのは
        /// <b>完全にスクリーンになった所</b>（<c>canon/LEDGER.md</c> 0057・ユーザー指示
        /// 「鈴は、完全にスクリーンになったときになるようにしてほしい」）。
        ///
        /// 入口音が鳴り終わってから鈴を鳴らす。
        /// </summary>
        public const float BellAfterSwapSec = IntroLogic.SwapCrossfadeSec;

        /// <summary>
        /// 鈴の音源 `amb_bell` の頭から最大の山までの秒（10ms 実効値で測った値。頭 0.15 秒は -15〜-25dB の前打ち）。
        /// <b>音源を差し替えたら測り直す。</b>
        /// </summary>
        public const float BellPeakSec = 0.20f;

        /// <summary>
        /// テレビ方式（0309）で、段 4 の頭から鈴を鳴らすまでの秒（`canon/LEDGER.md` 0388）。
        /// 鈴の山が<b>映像が出切る秒</b>（<see cref="IntroLogic.TvPowerOnEndSec"/>）に来るよう、
        /// 予約の遅れ（35ms）と音源の山（<see cref="BellPeakSec"/>）を前へ引く。
        /// テレビ方式の段 5 は映像が出切った後の静止なので、<see cref="BellAfterSwapSec"/> で待つと
        /// 動かない画に鈴だけが鳴る。
        /// </summary>
        public const float BellAfterTvOnSec =
            IntroLogic.TvPowerOnEndSec - IntroLogic.TvOnLeadSec - BellPeakSec;

        /// <summary>
        /// 1 フレームに拾える上限。**種類の総数と同じにしてある ＝ 構造的に溢れない。**
        ///
        /// ⚠ 最初 3 にしていたら、テストが 4 本同時のフレームを作って 1 本落とした。
        /// 実際の体験でそこまで重なるかは怪しいが、**「たぶん重ならない」を根拠に上限を切ると、
        /// 重なった日に黙って音が消える**（音は録画にも映らないので永久に気づけない）。
        /// 配列を数個伸ばす代償で、その分岐ごと消す方が安い。<see cref="Dropped"/> は保険として残す。
        /// </summary>
        public const int MaxPerTick = 8;

        private readonly SoundCue[] _buf = new SoundCue[MaxPerTick];
        private bool _titleWasVisible;
        // ⚠ `SealClose` / `Swap` のラッチは持たない（2026-08-15 に鳴らさなくなった）。
        private bool _shatterFired;
        private bool _screenOnFired;
        private bool _tvNoiseFired;
        private bool _glitchArmed = true;
        private float _glitchCooldown;
        private bool _glyphWasShowing;
        private bool _bellFired;
        /// <summary>終幕の電源断を鳴らしたか。<b>終幕を抜けたフレームで自分で落ちる。</b></summary>
        private bool _powerOffFired;

        /// <summary>段 5 に入ってからの秒数。<b>鈴はここで数える</b>（外から段の経過が来ないため）。</summary>
        private float _swapSec;

        /// <summary>段 4 に入ってからの秒数（テレビ方式の鈴だけが読む）。入った縁で 0。負は段 4 の外。</summary>
        private float _frameSec = -1f;

        /// <summary>前フレームの導入の段。段 0 へ入った縁で「1 度だけ」を落とすために持つ。</summary>
        private IntroStage _lastIntroStage = IntroStage.Off;

        /// <summary>拾えなかった数（累積）。0 でないなら設計か閾値が間違っている。</summary>
        public int Dropped { get; private set; }

        /// <summary>ラン開始・導入のやり直しで呼ぶ。**ラッチを全部落とす。**</summary>
        public void ResetRun()
        {
            _titleWasVisible = false;
            _glitchArmed = true;
            _glitchCooldown = 0f;
            _glyphWasShowing = false;
            _powerOffFired = false;
            ResetIntroLatches();
        }

        /// <summary>
        /// 体験 1 回ぶんの「1 度だけ鳴る」を全部落とす。
        ///
        /// ⚠⚠ <b>2026-08-14 まで、これを落とす経路は <see cref="ResetRun"/> だけで、
        /// その呼び出し元がどこにも無かった。</b> ＝ アプリを起動してから<b>1 人目だけ導入の音が鳴り、
        /// 2 人目以降は隔離が閉じる音も管が点く音も鈴も終幕も無音</b>だった。
        /// 展示は 1 日に数十人が続けて体験するので、**ほぼ全員が無音の側に当たる**。
        /// しかも画は正常で、<b>音は録画に映らない</b>ので走行の証拠からは気づけない。
        ///
        /// ⇒ <b>呼び忘れが起きない形にした</b>: <see cref="Tick"/> が導入の段 0（<c>Black</c>）へ
        /// 入った縁で自分で落とす。段 0 はランリセットでも中止からの復帰でも必ず通るので、
        /// 号令を配る側の実装に依存しない。
        /// </summary>
        private void ResetIntroLatches()
        {
            _shatterFired = false;
            _screenOnFired = false;
            _tvNoiseFired = false;
            _bellFired = false;
            _swapSec = 0f;
            _frameSec = -1f;
        }

        /// <summary>
        /// 単発の乱れが掛かった<b>その行</b>で呼ぶ（`canon/LEDGER.md` 0388）。鳴らすなら true。
        ///
        /// ⚠ 閾値を毎フレーム外から見ると、画の乱れより 1〜2 コマ遅れて鳴る（包絡の立ち上がり 20ms と
        /// 実行順の 1 コマ）。画を書くのと同じ行から鳴らせば、遅れは予約の 35ms だけになる
        /// （カメラ切替と同じ約束）。鳴らす条件（閾値・再武装・最短間隔）は閾値の経路と同じ。
        /// </summary>
        /// <param name="level">掛けた強さ（育ちを掛けた後）。</param>
        /// <param name="levelBefore">掛ける直前の乱れの強さ。</param>
        public bool TryFireGlitchOnset(float level, float levelBefore)
        {
            if (levelBefore < GlitchRearmAt) _glitchArmed = true;
            if (!_glitchArmed || level < GlitchFireAt || _glitchCooldown > 0f) return false;
            _glitchArmed = false;
            _glitchCooldown = GlitchMinIntervalSec;
            return true;
        }

        /// <summary>今フレームに鳴らすものを返す（<paramref name="count"/> 本）。</summary>
        public ReadOnlySpan<SoundCue> Tick(float dt, in SoundShowState s, float glitchLevel,
                                           out int count)
        {
            count = 0;
            if (_glitchCooldown > 0f) _glitchCooldown -= dt;

            // ⚠⚠ **導入が頭から始まったら「1 度だけ」を落とす**（2026-08-14）。
            //    これが無いと 2 人目以降の体験で導入の音が 1 つも鳴らない（`ResetIntroLatches` の説明）。
            //    段 0 はランリセットでも中止からの復帰でも必ず通るので、ここが唯一の確実な縁。
            if (s.introActive && s.introStage == IntroStage.Black && _lastIntroStage != IntroStage.Black)
                ResetIntroLatches();
            _lastIntroStage = s.introStage;

            // --- タイトル -------------------------------------------------------
            // ⚠ **鳴らす縁は「画面を持った」ではなく「字が立った」**（2026-08-12 に流れが変わった）。
            //    周回リセット直後は真っ暗で A を待っているだけなので、そこで音を出すと
            //    「まだ何も始まっていない」と食い違う。
            if (s.titleGlyphShowing && !_glyphWasShowing) Push(SoundCue.TitleIn, ref count);
            else if (!s.titleGlyphShowing && _glyphWasShowing && s.titleVisible)
                Push(SoundCue.TitleOut, ref count);
            _glyphWasShowing = s.titleGlyphShowing;
            _titleWasVisible = s.titleVisible;

            // --- 導入の山（現実が割れる / スクリーンが出る）-----------------------
            //
            // ⚠⚠ **2026-08-15 に置き直した**（`canon/LEDGER.md` 0044）。段が
            //    Seal/Dark/Ignite/Live から Real/Degrade/Structure/Frame/Swap へ戻り、
            //    「隔離が閉じる」段も「闇の中で管が点く」段も無くなった。
            //
            //    - `SealClose`（`sfx_seal_close`）は**鳴らさない**。隔離が閉じる段が無くなった。
            //      音源は残してある
            //    - `Swap`（`sfx_swap`）も鳴らさない。
            if (s.introActive)
            {
                // 段 4 の頭 — 現実が割れ始める。
                if (!_shatterFired && s.introWeights.shatter >= ShatterFireAt)
                {
                    _shatterFired = true;
                    Push(SoundCue.Shatter, ref count);
                }
                // テレビが奥から寄ってくる段 3 の頭で砂嵐（0383）。電源が入るまで鳴り続ける。
                if (!_tvNoiseFired && s.introWeights.tv && s.introStage == IntroStage.Structure)
                {
                    _tvNoiseFired = true;
                    Push(SoundCue.TvNoise, ref count);
                }
                // テレビの電源（0309）。音は 0383 でユーザーが指定した TV-Turn_Off01（「ぴこりっ」の
                // ScreenOn は鳴らさない）。砂嵐は鳴らす側（ShowSoundDirector.FireCue）が退かせる。
                // 割れる遷移（tv=false）は生成動画の音に帰還まで含まれるため、何も重ねない。
                if (!_screenOnFired && s.introWeights.tv && s.introStage == IntroStage.Frame)
                {
                    _screenOnFired = true;
                    Push(SoundCue.TvOn, ref count);
                }
                // ⚠⚠ **その後のノイズ（`ScreenNoise`）は 2026-08-16 に鳴らさなくなった**
                //    （`canon/LEDGER.md` 0057・ユーザー指示「ノイズは鳴らさない」）。
                //    スクリーンが出る音を差し替えたので、後ろに足す音が要らなくなった。
                //    enum と音源（`sfx_screen_noise`）は残してある。
            }

            // --- 終幕 -----------------------------------------------------------
            // ⚠⚠ **2026-08-23 に 1 本だけ足した**（`canon/LEDGER.md` 0125・ユーザー指定
            //    「最後の電源が落ちるときにこの効果音を鳴らしてほしい」）。0048 の
            //    「終幕に足す音は 1 本も無い」はここで覆っている — あれはパススルーへ戻す
            //    5 段を廃止したときの判断で、いまの画は**ブラウン管の電源断**（0111）。
            //
            // ⚠⚠ **縁は「終幕が始まった」であって段名ではない。** 段名で切ると、名前を
            //    変えた日に黙って無音になる（2026-08-23 に敷く音で実際に踏んだ形）。
            //    終幕の頭 ＝ 潰れ始める瞬間なので、電源断の音はここ 1 点でよい。
            // ⚠ **`ResetIntroLatches` には入れない。** あれは導入の段 0 で落ちるので、
            //    終幕の後に導入へ戻らない限り再武装しない ＝ 2 人目以降が無音になる。
            //    ここは**終幕が終わったフレーム**で自分から再武装する。
            // ⚠ 2026-10-21 から、潰れ始めるのは終幕の頭ではなく砂嵐（0.95 秒）の後。
            //    電源断の音は画で管が潰れ始める瞬間（`outroPowerCutSec`）まで待つ。
            //    段名ではなく経過で待つ（上の注記と同じ理由）。
            if (s.outroActive)
            {
                if (!_powerOffFired && s.outroElapsedSec >= s.outroPowerCutSec)
                {
                    _powerOffFired = true;
                    Push(s.outroTrapped ? SoundCue.DollEndFall : SoundCue.PowerOff, ref count);
                }
            }
            else _powerOffFired = false;
            //    <see cref="SoundCue.ShellOpen"/> の enum と音源は残してある（鳴らさない）。

            // ⚠⚠ **人形の笑いはここには無い**（2026-08-16・`canon/LEDGER.md` 0066）。
            //    報告を押すまで**ループ**するので、一撃ではなく敷く音の器に載せてある
            //    （`SoundBedLogic.dolls` / `bed_dolls_laugh`）。ここへ戻すと 1 回で終わる。

            // --- 乱れ（何度でも鳴る）-------------------------------------------
            // 単発の乱れは <see cref="TryFireGlitchOnset"/> が画の頭で鳴らす。ここで拾うのは
            // 持続の乱れ（遷移の継ぎ目）が閾値を越えた所と、頭で鳴らせなかったもの。
            if (glitchLevel < GlitchRearmAt) _glitchArmed = true;
            if (_glitchArmed && glitchLevel >= GlitchFireAt && _glitchCooldown <= 0f)
            {
                _glitchArmed = false;
                _glitchCooldown = GlitchMinIntervalSec;
                Push(SoundCue.Glitch, ref count);
            }

            // --- 家鳴り -----------------------------------------------------------
            // ⚠⚠ **2026-08-15 に全廃した**（`canon/LEDGER.md` 0049・ユーザー逐語
            //    「導入の家鳴りは無くす」「それ以降も、家鳴りは無くしてほしい」）。
            //    段 0 にも本編にも 1 発も置かない。<see cref="SoundCue.Creak"/> の enum と
            //    音源（`amb_creak_1/2`）は残してある。

            // --- 鈴（完全にスクリーンになった所で 1 回だけ）----------------------
            // ⚠⚠ **2026-08-16 に段 3 の頭からここへ移した**（`canon/LEDGER.md` 0057・
            //    ユーザー逐語「鈴は、完全にスクリーンになったときになるようにしてほしい」）。
            //    2026-08-15 は段 3（輪郭だけの世界の始まり）、それ以前は段 0 の 6 秒後だった。
            //
            // ⚠ **ここだけは時間で待つ。** 段 5 の頭はまだ最後の破片が消えていく最中で、
            //    「完全にスクリーンになった」のは<b>すり替えのクロスフェードが終わった所</b>
            //    （`IntroLogic.SwapCrossfadeSec`）。段 5 は 4.5 秒あるので、待っても必ず鳴る
            //    （段 3 が実尺 1.1 秒で待てなかったのとは事情が違う）。
            // ⚠⚠ **テレビ方式は段 4 の中で鳴らす**（2026-11-15・`canon/LEDGER.md` 0388）。
            //    テレビ方式で「完全にスクリーンになった」のは電源の音が鳴り終わって映像が出切った所
            //    （段 4 の頭から `IntroLogic.TvPowerOnEndSec`）。段 5 はその後の静止なので、
            //    段 5 ＋ 1.2 秒で鳴らすと動かない画に鈴だけが来る。割れる遷移は従来どおり段 5 で待つ。
            bool tvIntro = s.introActive && s.introWeights.tv;
            if (s.introActive && s.introStage == IntroStage.Frame)
            {
                _frameSec = _frameSec < 0f ? 0f : _frameSec + dt;
                if (tvIntro && !_bellFired && _frameSec >= BellAfterTvOnSec && !s.registrationActive)
                {
                    _bellFired = true;
                    Push(SoundCue.Bell, ref count);
                }
            }
            else _frameSec = -1f;
            if (s.introActive && s.introStage == IntroStage.Swap)
            {
                _swapSec += dt;
                // テレビ方式で段 4 を待ち切らずに抜けた（スタッフの送り）ときは、段 5 へ入った縁で鳴らす。
                float wait = tvIntro ? 0f : BellAfterSwapSec;
                if (!_bellFired && _swapSec >= wait && !s.registrationActive)
                {
                    _bellFired = true;
                    Push(SoundCue.Bell, ref count);
                }
            }

            return new ReadOnlySpan<SoundCue>(_buf, 0, count);
        }

        private void Push(SoundCue c, ref int count)
        {
            if (count >= MaxPerTick) { Dropped++; return; }
            _buf[count++] = c;
        }

        /// <summary>
        /// テレビが定位置に着いたときの砂嵐の音量（0387・ユーザー指定「来るときのノイズの音の最大値を、今の半分の大きさに」）。
        /// 0383 で入れた時点の 1.0 の半分の振幅（-6.0dB）。焼いた素材（-24 LUFS）は変えず、鳴らす側で掛ける。
        /// </summary>
        public const float TvNoisePeakGain = 0.5f;

        /// <summary>
        /// テレビが定位置より <paramref name="depthM"/> m 奥にいるときの砂嵐の音量（0387「奥にいるときは小さく、
        /// 定位置につくときに最大値になるように徐々に上げて」）。
        ///
        /// 音の振幅を距離に反比例させる（点音源の 1/r）。見かけの幅も同じ比で大きくなるので、
        /// 音の大きさとテレビの見かけの大きさが同じ割合で育つ。奥の端（7m 奥 ＝ 9m 先）で最大の 2/9（-13.1dB）。
        ///
        /// ⚠ 距離の減衰を <c>AudioSource</c> に任せない。<see cref="SpatialAudio"/> は減衰を切ってあり
        ///   （<c>minDistance</c> 50m）、そちらを変えると他のすべての 3D 音の高さが動く。
        /// </summary>
        public static float TvNoiseGain(float depthM)
        {
            float d = depthM > 0f ? depthM : 0f;
            return TvNoisePeakGain * IntroLogic.TvRestM / (IntroLogic.TvRestM + d);
        }

        /// <summary>その節目で劇伴をどれだけ引くか（0..1）。**山ほど深く引く。**</summary>
        public static float DuckFor(SoundCue c)
        {
            switch (c)
            {
                case SoundCue.SealClose: return 0.85f;
                case SoundCue.Shatter: return 0.90f;    // 導入の山。劇伴を深く退かせる
                case SoundCue.ScreenOn: return 0.90f;   // 旧音の設定は保持する
                case SoundCue.TvOn: return 0.60f;       // テレビの電源。導入の山
                case SoundCue.WallRise: return 0.45f;   // 増えるスクリーンの裏。劇伴を引いて前へ出す
                case SoundCue.ScreenNoise: return 0.75f; // 山の尾。退かせたまま保つ
                case SoundCue.Swap: return 0.55f;
                // ⚠ 題字が閉じる音は劇伴を退かせない（2026-10-14・`rules/sound-design.md`「劇伴はそのまま続く」）。
                //   0.70 で引くと、押した瞬間に劇伴が沈み、吸い込みの一撃の前に戻ってきていた。
                case SoundCue.TitleOut: return 0f;
                case SoundCue.ShellOpen: return 0.40f;
                case SoundCue.Glitch: return 0.35f;
                case SoundCue.Bell: return 0.45f;
                // 終幕の頭。劇伴は 2.0 秒かけて退く途中なので、まだ鳴っている上に置かれる。
                case SoundCue.PowerOff: return 0.60f;
                // 人形 End。黒へ落ちた瞬間に劇伴を深く引き、落ちる音だけを前へ出す（0312）。
                case SoundCue.DollEndFall: return 0.90f;
                case SoundCue.Creak: return 0.15f;   // 退かせすぎると芝居がかる
                // 大きい目が見開く所。3 周目 C の山なので深く退かせる
                case SoundCue.EyeBig: return 0.75f;
                // ⚠ **目の一撃は退かせない**（0f）。3 秒に 20 発近く並ぶので、1 発ごとに引くと
                //    背景が波打つ（人形の笑いを退かせないのと同じ理由 —— 事件そのものだから）
                default: return 0f;
            }
        }

        /// <summary><c>Resources/Sound/</c> のどれを鳴らすか。**片方だけ直すと沈黙して食い違う。**</summary>
        public static string ResourceName(SoundCue c)
        {
            switch (c)
            {
                case SoundCue.TitleIn: return "sfx_title_in";
                case SoundCue.TitleOut: return "sfx_title_out";
                case SoundCue.SealClose: return "sfx_seal_close";
                case SoundCue.Shatter: return "sfx_shatter";
                case SoundCue.ScreenOn: return "sfx_screen_on";
                case SoundCue.ScreenNoise: return "sfx_screen_noise";
                case SoundCue.Swap: return "sfx_swap";
                case SoundCue.ShellOpen: return "sfx_shell_open";
                case SoundCue.Glitch: return "sfx_glitch";      // 3 種から順に選ぶ
                case SoundCue.Creak: return "amb_creak";        // 2 種
                case SoundCue.Bell: return "amb_bell";
                case SoundCue.DollCall: return "sfx_doll_call";
                case SoundCue.PowerOff: return "sfx_power_off";
                case SoundCue.DollEndFall: return "sfx_doll_end_fall";
                case SoundCue.EyeOpen: return "sfx_eye";        // 6 種
                case SoundCue.EyeBig: return "sfx_eye_big";
                case SoundCue.ScreenAdd: return "sfx_tv_degauss";   // 6 種（0383）
                case SoundCue.ScreenRemove: return "sfx_tv_off";    // 4 種（0383）
                case SoundCue.TvNoise: return "sfx_tv_noise";
                case SoundCue.TvOn: return "sfx_tv_on";
                case SoundCue.WallRise: return "sfx_wall_rise";
                case SoundCue.KidsRoom: return "sfx_kidsroom";
                case SoundCue.DollLaughOnce: return "sfx_doll_laugh_once";
                case SoundCue.ScreenFaceLaugh: return "sfx_screen_face_laugh";
                default: return "";
            }
        }

        /// <summary>同じ音が並ばないよう変種を持つもの（末尾に <c>_1..N</c> が付く）。</summary>
        public static int VariantCount(SoundCue c)
        {
            switch (c)
            {
                case SoundCue.Glitch: return 3;
                case SoundCue.Creak: return 2;
                case SoundCue.EyeOpen: return 6;
                case SoundCue.ScreenAdd: return 6;
                case SoundCue.ScreenRemove: return 4;
                default: return 1;
            }
        }
    }
}
