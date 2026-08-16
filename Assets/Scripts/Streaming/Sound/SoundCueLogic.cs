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
        /// 音源はユーザー指定の一撃を**小刻みに並べて 1.70 秒でだんだん小さくしたもの**
        /// （2026-08-16・<c>canon/LEDGER.md</c> 0057）。<see cref="ScreenOn"/> の手前で
        /// <b>鳴り終わって静かになる</b>のが要件。
        /// </summary>
        Shatter,
        /// <summary>
        /// 段 4 — <b>現実からカメラ映像への入れ替えが終わった所 ＝ スクリーンが出る瞬間</b>。**導入の山。**
        /// 音源はユーザー指定（2026-08-16 に Cyber14-1 へ差し替え・<c>canon/LEDGER.md</c> 0057）。
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
        /// ⚠ <b>2026-08-15 以降は鳴らない</b> — 同じ縁をユーザー指定の <see cref="ScreenOn"/> が持つ。
        /// 音源は残してある。
        /// </summary>
        Swap,
        /// <summary>
        /// 終幕 — 隔離が開いて現実が戻る。
        /// ⚠ <b>2026-08-15 以降は鳴らない</b>（<c>canon/LEDGER.md</c> 0048）。終幕はパススルーへ
        /// 戻さなくなり、隔離が開く段そのものが無くなった。<b>終幕に足す音は 1 本も無い。</b>
        /// 音源は残してある。
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
        /// <b>人形がたくさん出てくる所で、いろんな人形が笑う</b>（2026-08-16・
        /// <c>canon/LEDGER.md</c> 0062）。もらった 1 本の笑い声を 7 体ぶんに組んである
        /// （音程・音量・音程のカーブ・始まりをずらして一部重ねる）。
        ///
        /// ⚠ <b>鳴る場所は「締めのカットが報告を待ち始めた瞬間」</b>。
        /// 4 周目 A と名指ししない — 著作が変わっても追随するように、
        /// <c>CommsCueLogic</c> の③と同じ signal（<c>TimelineDirector.IsWaitingForVisitorMark</c>）を読む。
        /// </summary>
        DollsLaugh,
        /// <summary>
        /// 鈴。<b>完全にスクリーンになった所に 1 回だけ</b>（段 5 の頭から
        /// <see cref="SoundCueLogic.BellAfterSwapSec"/> 後）。誰も鳴らしていないのに鳴る。
        /// ⚠ 2026-08-16 に段 3 の頭からここへ移した（<c>canon/LEDGER.md</c> 0057）。
        /// </summary>
        Bell,
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
        /// 段 4 で枠の中の映像がここまで満ちたら「スクリーンが出る音」を鳴らす。
        ///
        /// ⚠⚠ <b>2026-08-16 に 0.45 → 1.0（入れ替えが終わった所）へ移した</b>
        /// （<c>canon/LEDGER.md</c> 0057・ユーザー指示「スクリーンのクロスフェードが
        /// 終わったときに ... これを一度だけ。前の割れる音とはかぶせない」）。
        ///
        /// <c>live = SmoothStep(0.55, 0.85, p)</c> なので、ちょうど段 4 の進み 0.85
        /// （＝ 頭から 2.125 秒）で鳴る。割れる音は 1.70 秒で鳴り終わるので、
        /// <b>0.37 秒の静けさを挟んでから</b>この音が来る。
        /// ⚠ 割れる音の尺（<c>tools/ingest-sounds.py</c> の <c>SWARM_SEC</c>）と対で決めた値。
        /// 片方だけ動かすと重なる。
        /// </summary>
        public const float ScreenOnAt = 0.999f;

        /// <summary>
        /// 段 5 へ入ってから鈴を鳴らすまでの秒数。<b>＝ すり替えのクロスフェードの尺</b>
        /// （<see cref="IntroLogic.SwapCrossfadeSec"/>）なので、鳴るのは
        /// <b>完全にスクリーンになった所</b>（<c>canon/LEDGER.md</c> 0057・ユーザー指示
        /// 「鈴は、完全にスクリーンになったときになるようにしてほしい」）。
        ///
        /// ⚠ <b>スクリーンが出る音（<see cref="SoundCue.ScreenOn"/>）と同じ瞬間にしない。</b>
        /// あれは段 4 の入れ替えが終わる所で、こちらはその 1.6 秒後。
        /// 重ねると 1 つの音に潰れて「その後」にならない（0030 と同じ理屈）。
        /// </summary>
        public const float BellAfterSwapSec = IntroLogic.SwapCrossfadeSec;

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
        private bool _shatterFired, _screenOnFired, _screenNoiseFired;
        private bool _glitchArmed = true;
        private float _glitchCooldown;
        private bool _glyphWasShowing;
        private bool _bellFired;

        /// <summary>締めのカットが報告を待ち始めた縁を取るための前フレームの値。</summary>
        private bool _markWasWaiting;
        /// <summary>人形の笑いはラン 1 回に 1 度だけ（<see cref="ResetRun"/> で落ちる）。</summary>
        private bool _dollsFired;

        /// <summary>段 5 に入ってからの秒数。<b>鈴はここで数える</b>（外から段の経過が来ないため）。</summary>
        private float _swapSec;

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
            _shatterFired = _screenOnFired = _screenNoiseFired = false;
            _bellFired = false;
            _swapSec = 0f;
            // ⚠⚠ **人形の笑いもここで落とす。** 鳴るのは本編の終わりだが、落とす場所を
            //    <see cref="ResetRun"/> に置くと**2 人目以降で鳴らない**
            //    （このクラスの `ResetRun` は呼び出し元がどこにも無い ＝ 上の注記と同じ穴）。
            //    段 0 は 1 人につき必ず 1 度通るので、ここが唯一の確実な縁。
            _markWasWaiting = false;
            _dollsFired = false;
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
            //    - `Swap`（`sfx_swap`）も**鳴らさない**。同じ縁に、ユーザーが指定した音源
            //      （`sfx_screen_on` ＝ Cyber03-2・0030 / 0032 で 2 度調整）がある。
            //      **もらった音を、シュビーが作った音で押しのけない**（rules/sound-design.md §4.5）
            if (s.introActive)
            {
                // 段 4 の頭 — 現実が割れ始める。
                if (!_shatterFired && s.introWeights.shatter >= ShatterFireAt)
                {
                    _shatterFired = true;
                    Push(SoundCue.Shatter, ref count);
                }
                // ⚠ **割れた先に映像が満ちる所**（2026-08-15 にここへ移した）。段 4 で枠の中が
                //    カメラ映像になるので、「スクリーンを出すときの音」もその瞬間へ来る。
                //    割れる音と同じフレームにならないよう `live` の進みで遅らせる。
                if (!_screenOnFired && s.introWeights.live >= ScreenOnAt)
                {
                    _screenOnFired = true;
                    Push(SoundCue.ScreenOn, ref count);
                }
                // ⚠⚠ **その後のノイズ（`ScreenNoise`）は 2026-08-16 に鳴らさなくなった**
                //    （`canon/LEDGER.md` 0057・ユーザー指示「ノイズは鳴らさない」）。
                //    スクリーンが出る音を差し替えたので、後ろに足す音が要らなくなった。
                //    enum と音源（`sfx_screen_noise`）は残してある。
            }

            // --- 終幕 -----------------------------------------------------------
            // ⚠⚠ **2026-08-15 から 1 本も鳴らさない**（`canon/LEDGER.md` 0048）。
            //    隔離が開く段が無くなり、終幕は「装置が力尽きて報告を出す」だけになった。
            //    足す音が無いのは欠落ではなく設計 — 装置が引いた後に残るのは部屋の音だけで、
            //    それも Done で無音へ落ちる（`rules/sound-design.md`「終わりに音を残さない」）。
            //    <see cref="SoundCue.ShellOpen"/> の enum と音源は残してある。

            // --- 人形がたくさん出てくる（締めのカットが報告を待ち始めた縁）--------
            // ⚠ **「4 周目 A」と名指ししない。** 締めのカットが待っていること自体を見るので、
            //    著作が変わっても追随する（`CommsCueLogic` の③と同じ signal）。
            // ⚠ 位置合わせ中は鳴らさない（スタッフの作業に世界の音を割り込ませない）。
            if (s.markWaiting && !_markWasWaiting && !_dollsFired && !s.registrationActive)
            {
                _dollsFired = true;
                Push(SoundCue.DollsLaugh, ref count);
            }
            _markWasWaiting = s.markWaiting;

            // --- 乱れ（何度でも鳴る）-------------------------------------------
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
            if (s.introActive && s.introStage == IntroStage.Swap)
            {
                _swapSec += dt;
                if (!_bellFired && _swapSec >= BellAfterSwapSec && !s.registrationActive)
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

        /// <summary>その節目で劇伴をどれだけ引くか（0..1）。**山ほど深く引く。**</summary>
        public static float DuckFor(SoundCue c)
        {
            switch (c)
            {
                case SoundCue.SealClose: return 0.85f;
                case SoundCue.Shatter: return 0.90f;    // 導入の山。劇伴を深く退かせる
                case SoundCue.ScreenOn: return 0.90f;   // スクリーンが出る瞬間。同じだけ退かせる
                case SoundCue.ScreenNoise: return 0.75f; // 山の尾。退かせたまま保つ
                case SoundCue.Swap: return 0.55f;
                case SoundCue.TitleOut: return 0.70f;
                case SoundCue.ShellOpen: return 0.40f;
                case SoundCue.Glitch: return 0.35f;
                case SoundCue.Bell: return 0.45f;
                // 人形が笑い出した所は劇伴を深く退かせる（画面が人形で埋まる場面）。
                // ⚠ 退きは 0.55 秒の半減期で戻るので、効くのは笑い始めだけ。
                //    7 秒のあいだずっと引かせたいなら、押し続ける経路が要る（いまは作っていない）。
                case SoundCue.DollsLaugh: return 0.85f;
                case SoundCue.Creak: return 0.15f;   // 退かせすぎると芝居がかる
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
                case SoundCue.DollsLaugh: return "amb_dolls_laugh";
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
                default: return 1;
            }
        }
    }
}
