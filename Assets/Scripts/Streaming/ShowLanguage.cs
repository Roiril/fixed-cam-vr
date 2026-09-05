#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>体験者が読む言語。</b>スタッフが読む面（<c>StatusHud</c> / 操作早見表 / 位置合わせの
    /// ガイダンス / 異常の直し方）は<b>日本語のまま</b>で、ここは 1 ビットも効かない。
    /// </summary>
    public enum ShowLang
    {
        /// <summary>日本語。既定。</summary>
        Ja = 0,
        /// <summary>English。</summary>
        En = 1,
        /// <summary>Français。</summary>
        Fr = 2,
    }

    /// <summary>
    /// <b>いま体験者に見せている言語。</b>選ぶのは体験者自身で、
    /// <b>体験前の注意書きが出ているあいだだけ</b>、手元（左）のボタンで巡る
    /// （<see cref="TitleStage.Wait"/> ＝ まだ何も始まっていない黒の中。
    /// 入力を読むのは <c>OvrControllerBridge</c>、面は <c>TitleNotice</c>）。
    ///
    /// ⚠ <b>static なのは、読む側が 3 つの asmdef に散っているから。</b>
    /// 文言を持つのは <c>Diagnostics</c>（注意書き・連絡・報告）、入力は Assembly-CSharp、
    /// 段は <c>Streaming</c>。<c>Streaming</c> は <c>Diagnostics</c> を参照できない（依存の向きが逆）ので、
    /// いちばん下のここに置くのが唯一の配線点になる（<c>JapaneseHudFont.TryGet()</c> と同じ流儀）。
    ///
    /// ⚠ <b>ここに表示用の文字列は置かない。</b> 言語の名前（日本語 / English / Français）を
    /// 持っているのは <c>TitleNotice</c> だけ。理由はフォントの静的ベイクで、
    /// <c>JapaneseHudFontSetup.CollectHudCharset()</c> の収集元に無いファイルへ非 ASCII を書くと
    /// <b>実機で豆腐になるのに警告が 1 件も出ない</b>。収集元は既に注意書きを含んでいるので、
    /// 名前をあちらに置く限り<b>フォントの配線は 1 行も触らなくてよい</b>。
    /// <see cref="Code"/> が ASCII なのも同じ理由（テレメトリと解析器が読む）。
    ///
    /// ⚠ <b>体験者が替わったら既定へ戻す</b>（<see cref="Reset"/>）。押さなかった体験者に
    /// 前の人の言語を出さない。呼ぶのはランリセット（<c>OvrControllerBridge.ResetRun</c>）と
    /// タイトルの出し直し（<c>TitleScreen.BeginTitle</c>）の 2 つ。
    /// </summary>
    public static class ShowLanguage
    {
        /// <summary>既定。<b>押さなければこれ。</b></summary>
        public const ShowLang Default = ShowLang.Ja;

        /// <summary>巡る順。<b>この配列が順序の唯一の正</b>（<see cref="Next"/> はここから引く）。</summary>
        public static readonly ShowLang[] All = { ShowLang.Ja, ShowLang.En, ShowLang.Fr };

        /// <summary>
        /// <b>単押しと見なす上限 (秒)</b>（2026-09-05 ユーザー指定・<c>canon/LEDGER.md</c> 0159
        /// 「長押しした後に離すと言語が変わるのが面倒なので、単押し以外で言語は変わらないように」）。
        /// 押していた時間がこれ以下で離したときだけ言語が巡る。
        ///
        /// ⚠⚠ <b>これが無いと、長押しを途中でやめた回が必ず言語を 1 つ進める。</b>
        /// 成立した長押し（<see cref="HorrorRelief.HoldSec"/> 秒）は入力側が食べているので
        /// もともと巡らないが、<b>1.4 秒で離した回</b>は単押しと 1 ビットも区別が付かなかった。
        /// ⚠ <b><see cref="HorrorRelief.HoldSec"/> より必ず短い。</b> 逆転すると、長押しが
        /// 成立する前にどの押下も単押しになり<b>軽減モードへ入れなくなる</b>。
        /// ⚠ 縮めると<b>ゆっくり押す人の言語が変わらなくなる</b>（画には何も出ないので気づけない）。
        /// ⭐ ここは<b>面のゲージが出始める境目でもある</b>（<c>TitleNotice.GaugeStart01</c>）＝
        /// <b>ゲージが出たら、離しても言語は変わらない</b>。押し方と画が同じ 1 つの値で動く。
        /// </summary>
        public const float TapMaxSec = 0.5f;

        /// <summary>
        /// この押下は言語を巡らせるか。<b>純関数</b>（入力側と面が同じ判定を読む唯一の口）。
        /// </summary>
        public static bool IsTap(float heldSec) => heldSec >= 0f && heldSec <= TapMaxSec;

        private static ShowLang _current = Default;

        /// <summary>いま体験者に見せている言語。</summary>
        public static ShowLang Current => _current;

        /// <summary>
        /// この体験で言語が変わった回数。<b>テレメトリ（<c>ev=sum</c> の <c>langN</c>）が読む。</b>
        /// ⚠ 押した回数ではなく<b>変わった回数</b> — 押しても変わらない実装になったら 0 のまま出る。
        /// <see cref="Reset"/> で 0 へ戻る（＝ 走行ごとの値）。
        /// </summary>
        public static int ChangeCount { get; private set; }

        /// <summary>次の言語（<see cref="All"/> の順に巡り、最後から先頭へ戻る）。純関数。</summary>
        public static ShowLang Next(ShowLang lang)
        {
            for (int i = 0; i < All.Length; i++)
                if (All[i] == lang) return All[(i + 1) % All.Length];
            return Default;   // 知らない値が来たら既定へ倒す
        }

        /// <summary>次の言語へ進める。<b>変わったら true。</b></summary>
        public static bool Cycle()
        {
            ShowLang next = Next(_current);
            if (next == _current) return false;
            _current = next;
            ChangeCount++;
            return true;
        }

        /// <summary>既定へ戻す（体験者の交代）。<b>変わった回数も 0 へ戻す。</b></summary>
        public static void Reset()
        {
            _current = Default;
            ChangeCount = 0;
        }

        /// <summary>
        /// 言語を直接指定する。<b>体験の中では呼ばない</b> — 体験者が押して巡らせるものなので、
        /// 実行時の入口は <see cref="Cycle"/> の 1 つだけ。使うのは
        /// <c>menu text-audit</c> のような Editor の検査と、テスト。
        ///
        /// ⚠ <b><see cref="ChangeCount"/> を動かさない。</b> あれは「体験者が押して変わった回数」で、
        /// 検査が回した分を混ぜるとテレメトリの <c>langN</c> が嘘になる。
        /// </summary>
        public static void Select(ShowLang lang) => _current = lang;

        /// <summary>
        /// <see cref="Code"/> の逆。読めない値は既定へ倒す（CLI の打ち間違いで止めない）。
        /// </summary>
        public static ShowLang Parse(string? code)
        {
            if (string.IsNullOrEmpty(code)) return Default;
            foreach (ShowLang lang in All)
                if (string.Equals(Code(lang), code, System.StringComparison.OrdinalIgnoreCase))
                    return lang;
            return Default;
        }

        /// <summary>
        /// ログ・卓が読む短い名前。<b>ASCII だけ</b>（フォントのベイクに関わらせない）。
        /// テレメトリの <c>lang=</c> と <c>tools/analyze-xp-log.py</c> が対で使う。
        /// </summary>
        public static string Code(ShowLang lang) => lang switch
        {
            ShowLang.En => "en",
            ShowLang.Fr => "fr",
            _ => "ja",
        };

        /// <summary>
        /// ドメインリロードを切った Editor で前の Play の選択が残らないようにする。
        /// ⚠ 実機（ビルド）では毎回まっさらなので効かないが、<b>Editor だけ挙動が違う</b>ものを残さない。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad() => Reset();
    }
}
