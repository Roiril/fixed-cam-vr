#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>ホラー軽減モード。</b>体験前の注意書きが出ているあいだ、体験者が手元（左）の
    /// どれかのボタンを <see cref="HoldSec"/> 秒<b>長押し</b>すると入る／出る
    /// （2026-09-05 ユーザー指定・<c>canon/LEDGER.md</c> 0154）。
    ///
    /// 効くのは<b>音だけ</b>:
    ///   ① 既存の音（劇伴・敷く音・一撃・打鍵・切替）がすべて <see cref="Gain"/> 倍になる
    ///   ② 陽気な曲（<c>Sound/bed_relief</c>）が<b>常に</b>流れる
    /// どちらも <see cref="HorrorReliefAudio"/> が 1 箇所で掛ける。
    /// <b>画・尺・進行は 1 ビットも変わらない。</b>
    ///
    /// ⚠ <b>static なのは <see cref="ShowLanguage"/> と同じ理由。</b> 読む側が 3 つの asmdef に
    /// 散っている（文言は <c>Diagnostics</c>、入力は Assembly-CSharp、音は <c>Streaming</c>）。
    /// <c>Streaming</c> は <c>Diagnostics</c> を参照できないので、いちばん下のここが唯一の配線点。
    ///
    /// ⚠ <b>ここに表示用の文字列は置かない</b>（<see cref="ShowLanguage"/> と同じ）。
    /// 面に出す言い回しを持っているのは <c>TitleNotice</c> だけで、理由はフォントの静的ベイク
    /// （<c>JapaneseHudFontSetup.CollectHudCharset()</c> の収集元に無いファイルへ非 ASCII を
    /// 書くと、<b>実機で豆腐になるのに警告が 1 件も出ない</b>）。
    ///
    /// ⚠⚠ <b>体験者が替わったら必ず落とす</b>（<see cref="Reset"/>）。
    /// 呼ぶのは <c>TitleScreen.BeginTitle</c> ただ 1 か所で、<see cref="ShowLanguage.Reset"/> と
    /// <b>同じ行の隣</b>に置いてある。前の人が軽減モードで体験して、次の人が
    /// 何も押していないのに陽気な曲でホラーを見る、が起きてはいけない。
    /// ⚠ 戻し場所を増やさない — 増やすと「戻し忘れ」ではなく
    /// <b>「選んだ直後に二重に戻して体験者の選択が消える」</b>側の事故になる（0128 と同じ）。
    /// </summary>
    public static class HorrorRelief
    {
        /// <summary>
        /// 軽減モードのあいだ<b>既存の音ぜんぶ</b>に掛かる倍率（ユーザー指定「音量を半分に」）。
        ///
        /// ⚠⚠ <b>0.5 をこのファイル以外に書かない。</b> 掛ける口は
        /// <see cref="HorrorReliefAudio"/> の <c>AudioListener.volume</c> 1 つだけで、
        /// 陽気な曲の音量はこれに<b>連動しない</b>（<c>ignoreListenerVolume</c>）。
        /// 連動させると、倍率を動かしたときに陽気な曲まで一緒に下がる。
        /// </summary>
        public const float Gain = 0.5f;

        /// <summary>
        /// 切り替えに要る長押しの秒。<b>ここが唯一の正</b>（入力側は <c>OvrControllerBridge</c>）。
        ///
        /// ⚠ <b>異変の報告（1.0 秒）より長くする。</b> 注意書きの中では同じボタンの短押しが
        /// 言語の切り替えなので、巡らせるための押下が軽減モードへ化けてはいけない。
        /// ⚠ スタッフ側の長押し（2.0 秒）へ揃える理由も無い — あちらは誤操作したら体験が壊れる
        /// 操作で、こちらは押さなくても体験が成立する選択。
        /// </summary>
        public const float HoldSec = 1.5f;

        private static bool _enabled;

        /// <summary>いま軽減モードか。<b>押さなければ false。</b></summary>
        public static bool Enabled => _enabled;

        /// <summary>
        /// この体験で切り替えた回数。<b>テレメトリ（<c>ev=sum</c> の <c>relief=</c>）が読む。</b>
        /// ⚠ 押した回数ではなく<b>切り替わった回数</b>。<see cref="Reset"/> で 0 へ戻る。
        /// </summary>
        public static int ChangeCount { get; private set; }

        /// <summary>
        /// 既存の音に掛ける倍率（軽減中は <see cref="Gain"/>・平時は 1）。
        /// <b>掛けるのは <see cref="HorrorReliefAudio"/> だけ。</b>
        /// </summary>
        public static float ShowGain => _enabled ? Gain : 1f;

        /// <summary>切り替える。<b>切り替わった後の状態</b>を返す。</summary>
        public static bool Toggle()
        {
            _enabled = !_enabled;
            ChangeCount++;
            return _enabled;
        }

        /// <summary>体験者の交代。<b>切り替えた回数も 0 へ戻す。</b></summary>
        public static void Reset()
        {
            _enabled = false;
            ChangeCount = 0;
        }

        /// <summary>
        /// 実行時の入口は <see cref="Toggle"/> の 1 つだけなので、直接指定はテストと
        /// Editor の検査（<c>menu text-audit</c> がこの面を軽減モードで組む）専用。
        /// ⚠ <b><see cref="ChangeCount"/> を動かさない</b>（<see cref="ShowLanguage.Select"/> と同じ約束）。
        /// </summary>
        public static void Select(bool enabled) => _enabled = enabled;

        /// <summary>
        /// ドメインリロードを切った Editor で前の Play の選択が残らないようにする。
        /// ⚠ 実機（ビルド）では毎回まっさらなので効かないが、<b>Editor だけ挙動が違う</b>ものを残さない。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad() => Reset();
    }
}
