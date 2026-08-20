#nullable enable

using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>闇の目を、いつ開き始めていつ閉じ始めるかを、体験者の居場所で決める</b>
    /// （2026-08-19・<c>canon/LEDGER.md</c> 0093）。
    ///
    /// ユーザー指定はこの 3 行:
    /// <list type="bullet">
    ///   <item>区間 C へ<b>入った瞬間</b>から目を出し始める</item>
    ///   <item>C の<b>長さの半分</b>まで来たら終了演出を始める</item>
    ///   <item>途中でカメラが切り替わっても<b>強制打ち切りにせず、倍速などで流しきる</b></item>
    /// </list>
    ///
    /// ⚠⚠ <b>「終了演出を始める」は「いま閉じ始める」ではない。</b> 開き切っていない目は閉じられない
    /// （閉じる曲線は開き切った形から逆に辿る）。半ばで終了を求められたとき、この層は
    /// <b>開くのを止めずに <see cref="HurryRate"/> 倍で追い上げ</b>、開き切った所で閉じへ渡す。
    /// これが「強制打ち切りではなく流しきる」の実体。
    ///
    /// ⚠ <b>閉じる動きは速めない。</b> 閉じは 1.6 秒しかなく、瞼が下りるのは 1 つ 0.11 秒
    /// （<c>canon/LEDGER.md</c> 0085 の赤入れ「目を閉じるようなアニメーションで閉じて」）。
    /// ここを倍速にすると 0.055 秒 ＝ 30fps で 1.6 コマになり、<b>閉じたのではなく消えたに戻る</b>。
    ///
    /// ⚠ <b>位置が測れない現場では、従来どおりカットの終わりで畳む</b>（<see cref="ZoneSpan.valid"/> が
    /// false ＝ 未登録・layout 不在）。位置が新しい必須条件になると、位置合わせをしていない機で
    /// 目が一生出なくなる。
    /// </summary>
    public sealed class EyesCueLogic
    {
        /// <summary>終了演出を始める区間の進み。<b>「C の長さの半分」そのもの</b>。</summary>
        public const float FinishAt = 0.5f;

        /// <summary>追い上げの速さ。ユーザー指定「倍速にするなどして」。</summary>
        public const float HurryRate = 2f;

        /// <summary>目を出し続けるか（<see cref="AnomalyEyesLogic.Tick"/> の <c>wanted</c>）。</summary>
        public bool Wanted { get; private set; }

        /// <summary>進みの速さ（1 = 著作どおり / <see cref="HurryRate"/> = 追い上げ中）。</summary>
        public float Rate { get; private set; } = 1f;

        /// <summary>終了演出を始めたか（一度立ったら、この出番のあいだ降りない）。</summary>
        public bool Finishing { get; private set; }

        /// <summary>いま出番の最中か。</summary>
        public bool Running { get; private set; }

        /// <summary>この区間ではもう出し切った（同じ滞在で二度目を始めない）。</summary>
        public bool Spent { get; private set; }

        /// <summary>
        /// <b>区間の半分（<see cref="FinishAt"/>）に体験者が実際に到達したか</b>。
        /// <see cref="Finishing"/> と違い、カットの終わり（<c>armed</c> 落ち）や区間離脱では立たない。
        /// 視界ジャック（<see cref="EyeJackLogic"/>）が「歩き続ける体験者」の発火の縁として読む。
        /// </summary>
        public bool HalfReached { get; private set; }

        private bool _hasSpan;
        private int _startCamera = -1;
        private int _startVisit = -1;
        private bool _finishRequested;

        /// <summary>やり直す（ラン開始・中止）。</summary>
        public void Reset()
        {
            Wanted = false;
            Rate = 1f;
            Finishing = false;
            Running = false;
            Spent = false;
            HalfReached = false;
            _hasSpan = false;
            _startCamera = -1;
            _startVisit = -1;
            _finishRequested = false;
        }

        /// <summary>
        /// <b>外の層が「終了演出に入れ」と言った</b>（視界ジャックの写真が尽きた —
        /// 「動かなかったら写真が終わったら元に戻して目も消えて終わる」・<c>canon/LEDGER.md</c> 0099）。
        /// 位置の半分と同じ扱いで、開き切っていなければ追い上げてから閉じる。
        /// 出番の外で呼ばれたら何もしない（次の出番へ持ち越さない）。
        /// </summary>
        public void RequestFinish()
        {
            if (Running) _finishRequested = true;
        }

        /// <summary>1 フレーム分の判断。</summary>
        /// <param name="armed">カットが「目を出す」と言っているか（<c>steps[].eyes &gt; 0</c>）。</param>
        /// <param name="stage">目の側のいまの段（開き切ったか・閉じ切ったかを読む）。</param>
        /// <param name="span">体験者が区間のどこまで来たか。</param>
        public void Tick(bool armed, EyesStage stage, ZoneSpan span)
        {
            // 閉じ切った。次は「別の区間へ入る」か「カットが言い直す」まで始めない
            // （半ばで閉じ切った後もカットは同じ区間で続くので、そのままだと兆しから鳴り直す）。
            if (Running && Finishing && stage == EyesStage.Off)
            {
                Running = false;
                Finishing = false;
                HalfReached = false;
                _finishRequested = false;
                Spent = true;
            }
            if (Spent && (!armed || (span.valid && span.visit != _startVisit))) Spent = false;

            if (!Running)
            {
                if (!armed || Spent)
                {
                    Wanted = false;
                    Rate = 1f;
                    _finishRequested = false;
                    return;
                }
                Running = true;
                Finishing = false;
                HalfReached = false;
                _finishRequested = false;
                _hasSpan = span.valid;
                _startCamera = span.camera;
                _startVisit = span.visit;
            }

            bool sameVisit = _hasSpan && span.valid
                             && span.visit == _startVisit && span.camera == _startCamera;
            // 区間を出た（カメラが切り替わった）／カットが終わった。**畳むのではなく流しきる**。
            bool left = !armed || (_hasSpan && !sameVisit);
            // C の長さの半分まで来た。
            bool half = sameVisit && span.progress01 >= FinishAt;
            if (half) HalfReached = true;
            if (left || half || _finishRequested) Finishing = true;

            // 閉じ始められるのは開き切ってから。閉じ始めたらもう戻らない。
            bool openDone = stage == EyesStage.Hold || stage == EyesStage.Fading;
            Wanted = !Finishing || !openDone;
            Rate = Finishing && !openDone ? HurryRate : 1f;
        }
    }
}
