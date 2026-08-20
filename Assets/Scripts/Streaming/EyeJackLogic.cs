#nullable enable

using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>目の視界ジャック</b> — 目が開いたら視界を乗っ取って、当日撮った写真を
    /// ぱぱぱっと流す（<c>canon/LEDGER.md</c> 0099「3-C で現れる目は、そこにいる 4-A で出る
    /// 大量の人形の目である」「いろんな視点からの画像にぱぱぱっと切り替える」）。
    ///
    /// ここは<b>いつ乗っ取り、いつ返すか</b>の判断だけ（純ロジック・テストあり）。
    /// 描画と写真の入手は <see cref="AnomalyEyes"/> が持つ。
    ///
    /// <b>発火は 2 つの縁の早い方</b>:
    /// <list type="bullet">
    ///   <item><b>全開に達した</b>（<see cref="EyesStage.Hold"/>）＋ <see cref="HoldBeatSec"/> —
    ///     足を止めた体験者はこちら。開き切った目に見られる間を 1 拍置いてから乗っ取る</item>
    ///   <item><b>区間の半分に達した</b>（<see cref="EyesCueLogic.HalfReached"/>）—
    ///     歩き続ける体験者はこちら。実測の滞在（3:2 平均 7.0 秒）では全開 4.92 秒より
    ///     半分（約 3.5 秒）が先に来るので、<b>全開だけを待つと多数派に一度も出ない</b>
    ///     （2026-08-21 設計批評で dwell_stats.json から確定）</item>
    /// </list>
    ///
    /// <b>終わりの分岐</b>（<c>canon/LEDGER.md</c> 0099）:
    /// <list type="bullet">
    ///   <item>写真が尽きた → 視界を返し、<see cref="FinishEyesRequested"/> で目も閉じさせる
    ///     （「動かなかったら写真が終わったら元に戻して目も消えて終わる」）</item>
    ///   <item>カットが終わった / 区間を出た（<c>armed</c> 落ち）→ 即座に返す
    ///     （「動き続けたら今設定してるところで止める」— 目の側は従来どおり流しきる）</item>
    /// </list>
    ///
    /// ⚠ <b>1 枚の尺ではなく総尺を固定</b>（<see cref="TotalSec"/>）し、枚数で割る。
    ///   当日「撮りすぎた」で尺が壊れない。1 枚は <see cref="PerMinSec"/>〜<see cref="PerMaxSec"/> に
    ///   クランプし、入り切らない写真は<b>後ろから落とす</b>（順序はファイル名 = 決定的。乱数を使わない）。
    /// ⚠ <b>写真が 0 枚なら一生発火しない</b>（目は従来どおり）。当日フォルダが空でも体験は壊れない。
    /// </summary>
    public sealed class EyeJackLogic
    {
        /// <summary>写真を流す総尺 (秒)。「長すぎない快適な時間」の実体。</summary>
        public const float TotalSec = 2.4f;

        /// <summary>1 枚の尺の下限 (秒)。これより速いと「画像」ではなく明滅になる。</summary>
        public const float PerMinSec = 0.2f;

        /// <summary>1 枚の尺の上限 (秒)。枚数が少ない日でも 1 枚が居座らない。</summary>
        public const float PerMaxSec = 0.5f;

        /// <summary>全開（Hold）経由の発火で置く 1 拍 (秒)。開き切った目に見られてから乗っ取る。</summary>
        public const float HoldBeatSec = 0.4f;

        /// <summary>安全網。乗っ取りがこの秒数を超えたら強制的に視界を返す（写真 12 枚でも 2.4 秒）。</summary>
        public const float MaxActiveSec = 6f;

        /// <summary>いま視界を乗っ取っているか。</summary>
        public bool Active { get; private set; }

        /// <summary>いま出している写真（0 始まり）。<see cref="Active"/> のときだけ意味を持つ。</summary>
        public int PhotoIndex { get; private set; }

        /// <summary>この発火で出す枚数（総尺に入り切らない分を落とした後）。</summary>
        public int ShowCount { get; private set; }

        /// <summary>1 枚の尺 (秒)。この発火の枚数から決まる。</summary>
        public float PerSec { get; private set; }

        /// <summary>この tick で乗っ取りが始まった（テレメトリの縁）。</summary>
        public bool JustStarted { get; private set; }

        /// <summary>この tick で乗っ取りが終わった理由（空 = 終わっていない）。"done" / "cut" / "wd"。</summary>
        public string EndedWhy { get; private set; } = "";

        /// <summary>
        /// この tick で「目も閉じ始めろ」と言った（<see cref="EyesCueLogic.RequestFinish"/> へ渡す）。
        /// 写真が尽きたときだけ立つ。カットの終わりで切られたときは立たない —
        /// そちらの目の畳み方は従来の仕組み（流しきり）が持っている。
        /// </summary>
        public bool FinishEyesRequested { get; private set; }

        /// <summary>この出番で一度発火し終えた（同じ出番で二度乗っ取らない）。</summary>
        public bool Spent { get; private set; }

        private float _holdSec;    // Hold に入ってからの経過
        private float _activeSec;  // 乗っ取ってからの経過

        /// <summary>やり直す（ラン開始・中止・位置合わせ）。1 フレームで消える側。</summary>
        public void Reset()
        {
            Active = false;
            PhotoIndex = 0;
            ShowCount = 0;
            PerSec = 0f;
            JustStarted = false;
            EndedWhy = "";
            FinishEyesRequested = false;
            Spent = false;
            _holdSec = 0f;
            _activeSec = 0f;
        }

        /// <summary>1 フレーム分の判断。</summary>
        /// <param name="dt">経過秒。</param>
        /// <param name="armed">カットが「ジャックする」と言っているか（<c>eyes &gt; 0 かつ eyeJack</c>）。</param>
        /// <param name="stage">目の側のいまの段。</param>
        /// <param name="halfReached">体験者が区間の半分に達したか（<see cref="EyesCueLogic.HalfReached"/>）。</param>
        /// <param name="photoCount">いま使える写真の枚数（0 = 発火しない）。</param>
        public void Tick(float dt, bool armed, EyesStage stage, bool halfReached, int photoCount)
        {
            JustStarted = false;
            EndedWhy = "";
            FinishEyesRequested = false;

            if (!armed)
            {
                // カットが終わった / 区間を出た。「今設定してるところで止める」の実体。
                if (Active) EndedWhy = "cut";
                Active = false;
                Spent = false;
                _holdSec = 0f;
                _activeSec = 0f;
                return;
            }

            if (Active)
            {
                _activeSec += dt;
                if (_activeSec >= MaxActiveSec)
                {
                    // 安全網。ここに来るのは尺の計算が壊れたときだけ（凍結を作らない）。
                    Active = false;
                    Spent = true;
                    EndedWhy = "wd";
                    FinishEyesRequested = true;
                    return;
                }
                int idx = PerSec > 0f ? (int)(_activeSec / PerSec) : ShowCount;
                if (idx >= ShowCount)
                {
                    // 写真が尽きた。視界を返し、目も閉じさせる（0099 の「動かない体験者」の側）。
                    Active = false;
                    Spent = true;
                    EndedWhy = "done";
                    FinishEyesRequested = true;
                    return;
                }
                PhotoIndex = idx;
                return;
            }

            if (Spent || photoCount <= 0) return;

            // 目が閉じ始めてからは乗っ取らない（閉じの 1.6 秒に写真が被ると「元に戻して目も消えて」の順が壊れる）。
            if (stage == EyesStage.Fading || stage == EyesStage.Off) { _holdSec = 0f; return; }

            _holdSec = stage == EyesStage.Hold ? _holdSec + dt : 0f;
            bool fire = halfReached || _holdSec >= HoldBeatSec;
            if (!fire) return;

            ShowCount = Mathf.Min(photoCount, (int)(TotalSec / PerMinSec + 0.001f));
            PerSec = Mathf.Clamp(TotalSec / ShowCount, PerMinSec, PerMaxSec);
            PhotoIndex = 0;
            _activeSec = 0f;
            Active = true;
            JustStarted = true;
        }
    }
}
