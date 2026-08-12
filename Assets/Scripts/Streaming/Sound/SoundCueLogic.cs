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
        /// <summary>段 4 — 現実が割れてスクリーンへ吸い込まれる。</summary>
        Shatter,
        /// <summary>段 5 — 枠の中がカメラ映像になる（装置が点く）。</summary>
        Swap,
        /// <summary>終幕 — 隔離が開いて現実が戻る。**山にしない。**</summary>
        ShellOpen,
        /// <summary>映像の乱れ（<see cref="GlitchFx"/> と同時）。</summary>
        Glitch,
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
        private bool _sealFired, _shatterFired, _swapFired, _openFired;
        private bool _glitchArmed = true;
        private float _glitchCooldown;

        /// <summary>拾えなかった数（累積）。0 でないなら設計か閾値が間違っている。</summary>
        public int Dropped { get; private set; }

        /// <summary>ラン開始・導入のやり直しで呼ぶ。**ラッチを全部落とす。**</summary>
        public void ResetRun()
        {
            _titleWasVisible = false;
            _sealFired = _shatterFired = _swapFired = _openFired = false;
            _glitchArmed = true;
            _glitchCooldown = 0f;
        }

        /// <summary>今フレームに鳴らすものを返す（<paramref name="count"/> 本）。</summary>
        public ReadOnlySpan<SoundCue> Tick(float dt, in SoundShowState s, float glitchLevel,
                                           out int count)
        {
            count = 0;
            if (_glitchCooldown > 0f) _glitchCooldown -= dt;

            // --- タイトル -------------------------------------------------------
            if (s.titleVisible && !_titleWasVisible) Push(SoundCue.TitleIn, ref count);
            else if (!s.titleVisible && _titleWasVisible) Push(SoundCue.TitleOut, ref count);
            _titleWasVisible = s.titleVisible;

            // --- 導入の 3 つの山 ------------------------------------------------
            if (s.introActive)
            {
                if (!_sealFired && s.introWeights.shell >= ShellFireAt)
                {
                    _sealFired = true;
                    Push(SoundCue.SealClose, ref count);
                }
                if (!_shatterFired && s.introWeights.shatter >= ShatterFireAt)
                {
                    _shatterFired = true;
                    Push(SoundCue.Shatter, ref count);
                }
                if (!_swapFired && s.introStage == IntroStage.Swap)
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
                case SoundCue.Swap: return 0.55f;
                case SoundCue.TitleOut: return 0.70f;
                case SoundCue.ShellOpen: return 0.40f;
                case SoundCue.Glitch: return 0.35f;
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
                case SoundCue.Swap: return "sfx_swap";
                case SoundCue.ShellOpen: return "sfx_shell_open";
                case SoundCue.Glitch: return "sfx_glitch";      // 3 種から順に選ぶ
                default: return "";
            }
        }

        /// <summary>同じ音が並ばないよう変種を持つもの（末尾に <c>_1..N</c> が付く）。</summary>
        public static int VariantCount(SoundCue c) => c == SoundCue.Glitch ? 3 : 1;
    }
}
