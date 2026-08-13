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
        /// <summary>段 1 — 隔離が閉じて会場が消える。**導入で最初の山。**</summary>
        SealClose,
        /// <summary>
        /// 現実が割れてスクリーンへ吸い込まれる。
        /// ⚠ <b>2026-08-13 以降は鳴らない</b>（段 4「破砕」を廃止し重み <c>shatter</c> を眠らせた）。
        /// 音源と経路は残してある — 赤入れが返ってから消す。
        /// </summary>
        Shatter,
        /// <summary>
        /// 段 3 — 闇の中でブラウン管に電源が入る。**導入の山**（`canon/LEDGER.md` 0023 の ④）。
        /// 破砕を廃止して空いた山をここが引き受ける。音源はユーザー指定（2026-08-13）。
        /// </summary>
        ScreenOn,
        /// <summary>
        /// 段 3 の後半 — <b>管の面が満ちて走査が鳴き出す</b>。<see cref="ScreenOn"/> の尾に重なる。
        /// ユーザー指示（2026-08-13・<c>canon/LEDGER.md</c> 0030）
        /// 「これを最初に出して、その後ノイズを出すとかかな？」の後半。
        /// </summary>
        ScreenNoise,
        /// <summary>段 4 — 管の中がカメラ映像になる（装置が点く）。</summary>
        Swap,
        /// <summary>終幕 — 隔離が開いて現実が戻る。**山にしない。**</summary>
        ShellOpen,
        /// <summary>映像の乱れ（<see cref="GlitchFx"/> と同時）。</summary>
        Glitch,
        /// <summary>家鳴り。段 0 と本編にまばらに。<b>古い建物の中に居る</b>を音だけで立てる。</summary>
        Creak,
        /// <summary>鈴。段 0 に <b>1 回だけ</b>。誰も鳴らしていないのに鳴る。</summary>
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

        /// <summary>家鳴りの間隔（秒）。**等間隔にしない** — 規則正しいと建物ではなく機械に聞こえる。</summary>
        public const float CreakMinSec = 9f;
        public const float CreakMaxSec = 24f;

        /// <summary>段 0 に入ってから鈴が鳴るまで（秒）。**1 回だけ。**</summary>
        public const float BellAtSec = 6f;

        /// <summary>
        /// 管の点灯がここまで来たら 2 発目（ノイズ）を鳴らす。
        /// シェーダで面が満ち始めるのが <c>0.55</c> なので、そこに合わせてある
        /// （<c>ScreenComposite.shader</c> の <c>fill</c>）。**絵と対で直す。**
        /// </summary>
        public const float ScreenNoiseAt = 0.55f;

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
        private bool _sealFired, _shatterFired, _screenOnFired, _screenNoiseFired, _swapFired, _openFired;
        private bool _glitchArmed = true;
        private float _glitchCooldown;
        private bool _glyphWasShowing;
        private float _creakCountdown = -1f;
        private float _blackElapsed = -1f;
        private bool _bellFired;
        private uint _rng = 0x9E3779B9;

        /// <summary>拾えなかった数（累積）。0 でないなら設計か閾値が間違っている。</summary>
        public int Dropped { get; private set; }

        /// <summary>ラン開始・導入のやり直しで呼ぶ。**ラッチを全部落とす。**</summary>
        public void ResetRun()
        {
            _titleWasVisible = false;
            _sealFired = _shatterFired = _screenOnFired = _screenNoiseFired = _swapFired = _openFired = false;
            _glitchArmed = true;
            _glitchCooldown = 0f;
            _glyphWasShowing = false;
            _creakCountdown = -1f;
            _blackElapsed = -1f;
            _bellFired = false;
            _rng = 0x9E3779B9;
        }

        /// <summary>
        /// 決定論的な擬似乱数（0..1）。<b>種を固定してあるので走行のたびに同じ間隔になる</b> —
        /// 同じ show.json で違う音が出ると、何が効いたのか分からなくなる。
        /// </summary>
        private float NextRandom()
        {
            _rng = _rng * 1664525u + 1013904223u;
            return (_rng >> 8) / 16777216f;
        }

        private float NextCreakInterval()
            => CreakMinSec + (CreakMaxSec - CreakMinSec) * NextRandom();

        /// <summary>今フレームに鳴らすものを返す（<paramref name="count"/> 本）。</summary>
        public ReadOnlySpan<SoundCue> Tick(float dt, in SoundShowState s, float glitchLevel,
                                           out int count)
        {
            count = 0;
            if (_glitchCooldown > 0f) _glitchCooldown -= dt;

            // --- タイトル -------------------------------------------------------
            // ⚠ **鳴らす縁は「画面を持った」ではなく「字が立った」**（2026-08-12 に流れが変わった）。
            //    周回リセット直後は真っ暗で A を待っているだけなので、そこで音を出すと
            //    「まだ何も始まっていない」と食い違う。
            if (s.titleGlyphShowing && !_glyphWasShowing) Push(SoundCue.TitleIn, ref count);
            else if (!s.titleGlyphShowing && _glyphWasShowing && s.titleVisible)
                Push(SoundCue.TitleOut, ref count);
            _glyphWasShowing = s.titleGlyphShowing;
            _titleWasVisible = s.titleVisible;

            // --- 導入の山（隔離が閉じる / 装置が点く）-----------------------------
            // ⚠ 破砕（Shatter）は段の廃止で鳴らなくなった。判定はここではなく
            //   analyze-xp-log.py の「音（鳴ったか）」節から外してある。
            if (s.introActive)
            {
                // ⚠⚠ **段 0（Black）では鳴らさない**（2026-08-14・`canon/LEDGER.md` 0035）。
                //    重みだけを見ていたら、**起動直後に鳴っていた** — 段 0 で位置が解けていないと
                //    `outsideBoxM` が 0（＝中に居る扱い）へ倒れ、`OutsideWeights` が `shell = 1` を
                //    返すため。体験者がまだ何もしていない真っ暗の中で「隔離が閉じる」音が鳴り、
                //    本当に閉じる段 1〜2 では**もう鳴らない**（ラッチ済み）。
                //    実機ログで「段 Live まで進んだのに SealClose が 0 本」として出た。
                if (!_sealFired && s.introStage != IntroStage.Black
                    && s.introWeights.shell >= ShellFireAt)
                {
                    _sealFired = true;
                    Push(SoundCue.SealClose, ref count);
                }
                if (!_shatterFired && s.introWeights.shatter >= ShatterFireAt)
                {
                    _shatterFired = true;
                    Push(SoundCue.Shatter, ref count);
                }
                // 闇の中で管に電源が入る。**破砕を廃した導入の山はここ**。
                // 段の頭で鳴らす（絵より音が先に来る — rules/sound-design.md §4 の決めごと 1）。
                if (!_screenOnFired && s.introStage == IntroStage.Ignite)
                {
                    _screenOnFired = true;
                    Push(SoundCue.ScreenOn, ref count);
                }
                // ⚠ **管の面が満ちる所で 2 発目（ノイズ）。** 段の頭で 2 本重ねると 1 つの音に
                //    潰れるので、点灯の進み（`ignite`）を見て遅らせる。同じフレームでは鳴らない。
                if (!_screenNoiseFired && s.introStage == IntroStage.Ignite
                    && s.introWeights.ignite >= ScreenNoiseAt)
                {
                    _screenNoiseFired = true;
                    Push(SoundCue.ScreenNoise, ref count);
                }
                if (!_swapFired && s.introStage == IntroStage.Live)
                {
                    _swapFired = true;
                    Push(SoundCue.Swap, ref count);
                }
            }

            // --- 終幕 -----------------------------------------------------------
            if (s.outroActive && !_openFired && s.outroStage == OutroStage.Open)
            {
                _openFired = true;
                Push(SoundCue.ShellOpen, ref count);
            }

            // --- 乱れ（何度でも鳴る）-------------------------------------------
            if (glitchLevel < GlitchRearmAt) _glitchArmed = true;
            if (_glitchArmed && glitchLevel >= GlitchFireAt && _glitchCooldown <= 0f)
            {
                _glitchArmed = false;
                _glitchCooldown = GlitchMinIntervalSec;
                Push(SoundCue.Glitch, ref count);
            }

            // --- 家鳴り（段 0 と本編にまばらに）---------------------------------
            bool creakZone = (s.introActive && s.introStage == IntroStage.Black)
                             || (!s.introActive && !s.outroActive && s.phase == ShowPhase.Run);
            if (creakZone && !s.registrationActive)
            {
                if (_creakCountdown < 0f) _creakCountdown = NextCreakInterval();
                _creakCountdown -= dt;
                if (_creakCountdown <= 0f)
                {
                    _creakCountdown = NextCreakInterval();
                    Push(SoundCue.Creak, ref count);
                }
            }
            else
            {
                _creakCountdown = -1f;   // 区間を出たら数え直す（間延びした 1 発が飛び込まない）
            }

            // --- 鈴（段 0 に 1 回だけ）------------------------------------------
            if (s.introActive && s.introStage == IntroStage.Black)
            {
                if (_blackElapsed < 0f) _blackElapsed = 0f;
                _blackElapsed += dt;
                if (!_bellFired && _blackElapsed >= BellAtSec && !s.registrationActive)
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
                case SoundCue.Shatter: return 0.90f;
                case SoundCue.ScreenOn: return 0.90f;   // 導入の山。劇伴を深く退かせる
                case SoundCue.ScreenNoise: return 0.75f; // 山の尾。退かせたまま保つ
                case SoundCue.Swap: return 0.55f;
                case SoundCue.TitleOut: return 0.70f;
                case SoundCue.ShellOpen: return 0.40f;
                case SoundCue.Glitch: return 0.35f;
                case SoundCue.Bell: return 0.45f;
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
