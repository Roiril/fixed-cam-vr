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
        /// <summary>
        /// 配信端末が熱い。⚠ streamer v0.11.0 で熱による fps・画質の低下は全廃したので
        /// 「いま落ちている」ではなく「この先 OS に絞られる」の警告（体験は続けられる）。
        /// </summary>
        Hot,
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
    /// 語は 4 語に固定する: <b>「位置合わせ」「×印」「点」「ずれ」</b>。
    /// 廃語 = 登録 / 再登録 / 基準点 / マーク / 残差 / 誤差 / 周回リセット / 砂嵐 / course。
    /// スタッフは開発者ではないので、意味の取れない言葉は判断に使えない。
    /// ⚠ <b>同じ規約を <c>FixedCamVr.Tracking.RegistrationGuidance</c> も守る</b> — 2026-08-15 まで
    /// あちらは廃語だらけで、<b>同じ 1 枚の面に 2 つの語彙が並んでいた</b>。
    ///
    /// 操作の呼び方は <see cref="ControllerGuidePanel"/> と揃える（「トリガー2秒」「A」「B」）。
    /// 同じ操作を 2 つの面が違う名前で呼ぶと、現場で照合できない。
    ///
    /// 書式は <see cref="HmdTextStyle"/> の規約（操作は <c>入力：動作</c> / 括弧は全角 /
    /// 数値と単位のあいだに半角空白 / <b><c>⚠</c> は付けない</b>）。
    /// ⚠ 記号を落としたのは、この行が<b>異常のときしか出ない</b>ので `⚠` に情報が無いため
    /// （字面が騒がしくなるだけ）。目立たせるのは警告色（<see cref="HmdTextStyle.Alert"/>）が担う。
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
                ShowAlert.IntroAborted => "部屋の位置がずれたので演出を止めました",
                ShowAlert.NeedsReRegistration => "部屋の位置がずれています",
                ShowAlert.NotRegistered => "部屋の位置を測っていません",
                ShowAlert.NoVideo => cam + "の映像が届いていません",
                ShowAlert.TrackingLost => "ヘッドセットが自分の位置を見失いました",
                ShowAlert.Hot => cam + "が熱くなっています",
                ShowAlert.FloorNotMeasured => "床の高さを測っていません",
                _ => "",
            };
        }

        /// <summary>
        /// 直し方。<b>すべての異常が非空</b>（直せないものは「直せない」と書く。
        /// 何もできない異常は、そもそも表示する価値を問い直す）。
        ///
        /// ⚠ <b>操作と結果を 1 行に押し込まない</b>（改行で分ける）。1 行が面の幅を超えると
        /// 端が切れるか、読むのに首を振ることになる。
        ///
        /// ⚠⚠ <b>語調は常体の動詞終止で揃える</b>（現場のチェックリストの形）。
        /// <see cref="What"/> の側は「〜ています／〜ました」の丁寧のままにする ＝
        /// <b>状態は報告、手はチェックリスト</b>という 2 つの調子が役割と対応する。
        /// 2026-08-16 まで「〜と戻ります」「〜を冷ます」「〜再起動する」が混ざっていた。
        /// </summary>
        public static string How(ShowAlert alert) => alert switch
        {
            // 位置合わせを確定し直すだけで導入はやり直される（IntroDirector.TryRecoverFromAbort）。
            // 旧実装は「① 位置合わせ ② ランリセット」の 2 段で、順序を逆にすると直らなかった。
            ShowAlert.IntroAborted => "トリガー2秒：位置合わせを開始\n×印を打ち直して B で確定すると再開する",
            ShowAlert.NeedsReRegistration => "トリガー2秒：位置合わせを開始\n×印を打ち直して B で確定",
            ShowAlert.NotRegistered => "トリガー2秒：位置合わせを開始\n×印を順に打って B で確定",
            // ⚠ 旧文の「A でカメラを送る」は**存在しない操作**だった（カメラの手送りは
            //   2026-08-12 に撤去済み・`canon/LEDGER.md`）。現場でできることに書き直した。
            ShowAlert.NoVideo => "カメラの画面が消えていないか見る\n直らなければ端末を再起動する",
            ShowAlert.TrackingLost => "明るい方を向いて数歩歩く",
            ShowAlert.Hot => "体験はこのまま続けられる\n次の人の前に充電を外して冷ます",
            ShowAlert.FloorNotMeasured => "トリガー2秒：位置合わせを開始\n×印に先を着けて打ち直す",
            _ => "",
        };
    }
}
