#nullable enable

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// 現場で起きうる異常。<b>宣言順が優先度</b>（同時に立っても出すのは 1 件だけ）。
    /// 上にあるほど「体験が止まっている / 先に直さないと先へ進めない」。
    /// </summary>
    public enum ShowAlert
    {
        /// <summary>異常なし。</summary>
        None = 0,
        /// <summary>導入を中止した（トラッキング原点がずれた）。体験が止まっている。</summary>
        IntroAborted,
        /// <summary>位置合わせがずれた（OS の recenter）。このまま始めると部屋と合わない。</summary>
        NeedsReRegistration,
        /// <summary>位置合わせを一度もしていない。</summary>
        NotRegistered,
        /// <summary>いま映しているカメラの映像が来ていない。</summary>
        NoVideo,
        /// <summary>ヘッドセットが自己位置を見失った。</summary>
        TrackingLost,
        /// <summary>配信端末が熱で画質を落としている。体験は続けられる。</summary>
        Throttled,
        /// <summary>床の高さを測っていない位置合わせ（線や人形が沈んで見える）。</summary>
        FloorNotMeasured,
    }

    /// <summary>
    /// 異常を「何が起きたか」と「何をすれば直るか」の 2 行に翻訳する純関数。
    ///
    /// <b>なぜ 1 箇所に集めるか</b>: 旧実装は異常を 8 種類出していて、そのうち<b>復帰動作を書いている
    /// ものが 0 だった</b>（<c>⚠映像が届いていません（砂嵐表示中）</c>
    /// <c>⚠ヘッドセットの位置を見失っています</c> 等）。読んだスタッフは何をすればいいか分からず
    /// 現場で黙って立つ。文言を 1 つずつ場当たりに足すと同じ穴が再生産されるので、
    /// <b>異常を足すにはここへ手順を書く以外の道が無い</b>構造にしてある
    /// （<see cref="RecoveryGuidanceTests"/> が全異常に手順があることを機械で固定する）。
    ///
    /// 語は 3 語に固定する: <b>「位置合わせ」「×印」「点」</b>。
    /// 廃語 = 登録 / 再登録 / 基準点 / マーク / 残差 / 誤差 / 周回リセット / 砂嵐 / course。
    /// スタッフは開発者ではないので、意味の取れない言葉は判断に使えない。
    ///
    /// 操作の呼び方は <see cref="ControllerGuidePanel"/> と揃える（「トリガー2秒」「グリップ2秒」「A」「B」）。
    /// 同じ操作を 2 つの面が違う名前で呼ぶと、現場で照合できない。
    /// </summary>
    public static class RecoveryGuidance
    {
        /// <summary>
        /// 何が起きたかの 1 行。<paramref name="cameraNumber"/> は 1 始まり（0 以下なら番号を出さない）。
        /// カメラ番号を入れるのは、3 台のうちどれを見に行けばいいかが分からないと動けないため。
        /// </summary>
        public static string What(ShowAlert alert, int cameraNumber = 0)
        {
            string cam = cameraNumber > 0 ? "カメラ" + cameraNumber : "カメラ";
            return alert switch
            {
                ShowAlert.IntroAborted => "⚠部屋の位置がずれたので演出を止めました",
                ShowAlert.NeedsReRegistration => "⚠部屋の位置がずれています",
                ShowAlert.NotRegistered => "⚠部屋の位置を測っていません",
                ShowAlert.NoVideo => "⚠" + cam + "の映像が届いていません",
                ShowAlert.TrackingLost => "⚠ヘッドセットが自分の位置を見失いました",
                ShowAlert.Throttled => "⚠" + cam + "が熱で画質を落としています",
                ShowAlert.FloorNotMeasured => "⚠床の高さを測っていません",
                _ => "",
            };
        }

        /// <summary>
        /// 直し方の 1 行。<b>すべての異常が非空</b>（直せないものは「直せない」と書く。
        /// 何もできない異常は、そもそも表示する価値を問い直す）。
        /// </summary>
        public static string How(ShowAlert alert) => alert switch
        {
            // 位置合わせを確定し直すだけで導入はやり直される（IntroDirector.TryRecoverFromAbort）。
            // 旧実装は「① 位置合わせ ② ランリセット」の 2 段で、順序を逆にすると直らなかった。
            ShowAlert.IntroAborted => "トリガー2秒 → ×印を打ち直して B で確定（演出はやり直します）",
            ShowAlert.NeedsReRegistration => "トリガー2秒 → ×印を打ち直して B で確定",
            ShowAlert.NotRegistered => "トリガー2秒 → ×印を順に打って B で確定",
            ShowAlert.NoVideo => "端末の画面が消えていないか見る。直らなければ A でカメラを送る",
            ShowAlert.TrackingLost => "明るい方を向いて数歩歩くと戻ります",
            ShowAlert.Throttled => "体験は続けられます。次の人の前に充電を外して冷ます",
            ShowAlert.FloorNotMeasured => "トリガー2秒 → ×印に先を着けて打ち直すと合います",
            _ => "",
        };
    }
}
