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

        // カットが「この区間で目を出す」と言い始めた瞬間の居場所（Declare が刻む）。
        private bool _declHasSpan;
        private int _declCamera = -1;
        private int _declVisit = -1;

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
            _declHasSpan = false;
            _declCamera = -1;
            _declVisit = -1;
        }

        /// <summary>
        /// <b>カットが「目を出す」と言い始めた</b>（<c>steps[].eyes</c> が 0 → 正になった縁）。
        /// その瞬間の居場所を刻んでおき、<b>別の区間では出番を始めない</b>。
        ///
        /// ⚠⚠ <b>これが無いと、宣言していない区間で目が 1 つ開く</b>（2026-08-23 の実害・4 周目 A）。
        /// 区間の切り替わりは<b>2 つの速さで進む</b> —— <see cref="ZoneSpan"/> は体験者が線を跨いだ
        /// <b>生の瞬間</b>に進み（<c>ZoneLayoutApplier.OnZoneChanged</c>）、ショーのカット切替は
        /// <b>0.5 秒の滞在を待ってから</b>進む。その 0.5 秒のあいだ、
        /// <b>前の区間のカットがまだ「目を出す」と言ったまま、居場所だけが次の区間になっている</b>。
        /// <see cref="Spent"/> は滞在が変われば降りるので、そこで新しい出番が始まってしまい、
        /// 次の区間の頭で大きい目が 1 つだけ開いて閉じていた
        /// （実測 <c>logs/capture/20260823_151457_xp.log</c> t=488.06
        ///  <c>lap=4 take=L4C0#0 eyes=1/1/1.00/0.01/2.00/<b>0.00</b>/1</c> ＝
        ///  <b>カットは何も言っていないのに出番が走っている</b>）。
        ///
        /// ⚠ <b>位置を測れない現場では刻んでも効かない</b>（<see cref="ZoneSpan.valid"/> false）。
        /// 未登録の機で目が一生出なくなる方が高い（<see cref="Tick"/> の ④ と同じ理由）。
        /// </summary>
        public void Declare(ZoneSpan span)
        {
            _declHasSpan = span.valid;
            _declCamera = span.camera;
            _declVisit = span.visit;
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
                // ⚠⚠ **宣言された区間でしか始めない**（`Declare` を見よ）。滞在が変われば `Spent` は
                //    降りるが、それは「次にこの区間で言われたら出せる」という意味であって、
                //    **前の区間の言い残しで次の区間の頭に目を開いてよい**という意味ではない。
                if (!armed || Spent || !DeclaredHere(span))
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

        /// <summary>
        /// いま居る区間が、カットが目を宣言した区間か。
        /// <b>刻まれていない／位置を測れないときは常に true</b>（<see cref="Tick"/> の ④ と同じ思想 —
        /// 位置を必須条件にすると、位置合わせをしていない機で目が一生出なくなる）。
        /// </summary>
        private bool DeclaredHere(ZoneSpan span)
            => !_declHasSpan || !span.valid
               || (span.visit == _declVisit && span.camera == _declCamera);
    }
}
