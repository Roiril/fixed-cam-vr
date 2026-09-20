#nullable enable

namespace FixedCamVr.Streaming
{
    /// <summary>連絡の面の段。</summary>
    public enum CommsStage
    {
        /// <summary>出していない。</summary>
        Off,
        /// <summary>乗っ取りの侵入。通信面は完全に消え、空間エラーだけが先行する。</summary>
        Intrusion,
        /// <summary>枠が左から右へ開いている途中。</summary>
        In,
        /// <summary>文字が 1 字ずつ打たれている途中。</summary>
        Type,
        /// <summary>出し切って読ませている。</summary>
        Hold,
        /// <summary>引いている途中。</summary>
        Out,
        /// <summary>
        /// <b>連絡は無いが、報告の手元表示のために開いている</b>（<c>canon/LEDGER.md</c> 0058）。
        /// 枠は開き切っていて、AIエージェントからの文面は 1 字も出ていない。
        /// </summary>
        Guide,
    }

    /// <summary>連絡の面へ配る値（すべて 0..1）。</summary>
    public struct CommsWeights
    {
        /// <summary>地（受信票の面）の不透明度。</summary>
        public float panel;
        /// <summary>文字の不透明度。<b>何文字出ているかは <see cref="reveal"/> が持つ。</b></summary>
        public float glyph;
        /// <summary>枠の開き。0 = 左端に畳まれている / 1 = 開き切り。</summary>
        public float open;
        /// <summary>文字の出た割合（0 = 1 字も出ていない / 1 = 全部出た）。</summary>
        public float reveal;

        /// <summary>
        /// 下段（報告の押し方・ゲージ）の不透明度。<b>上段の文面とは別に動く</b> —
        /// 枠が開き切ってから出て、引くときは文面と一緒に消える。
        /// </summary>
        public float hint;

        /// <summary>
        /// 枠の<b>丈</b>。0 = 下段だけの細い受信票 / 1 = AIエージェントの文面が入る高さまで伸びている。
        /// ⚠ 下端を固定して伸び縮みする（実行体の <c>SetFrame</c>）。
        /// 連絡が無いのに文面のぶんの丈があると、<b>大きな空の箱</b>になる。
        /// </summary>
        public float body;

        /// <summary>
        /// 呪いの斑の量（0 = 通常の面 / 1 = 全面が呪われた双子）。<c>canon/LEDGER.md</c> 0229。
        /// 面が開くたびに 0 から目標（<see cref="CommsPanelLogic.SetCurseTarget"/>）へ
        /// <see cref="CommsCurseLogic.RampSec"/> で立ち上がる。畳まれていれば 0。
        /// </summary>
        public float curse;

        /// <summary>
        /// 左から右へ進む塗り替えの前線（0 = まだ通常 / 1 = 全面が呪われた双子）。
        /// 憑依の出し方（<see cref="CommsDelivery.Possessed"/>）でだけ動く。他の出方では 0。
        /// </summary>
        public float sweep;

        /// <summary>乱れの強さ 0..1（<c>canon/LEDGER.md</c> 0231）。塗り替わりの頭で立ち、塗り替わり切ってから引く。</summary>
        public float tear;

        /// <summary>乱れの種（面が開いてからの秒）。実行時の乱数の代わり。</summary>
        public float tearSeed;

        public static CommsWeights Hidden => new CommsWeights();
    }

    /// <summary>
    /// AIエージェントからの連絡（第 2 の面）の状態機械。<b>UnityEngine 非依存・dt 注入</b>。
    ///
    /// 立ち位置は `canon/LEDGER.md` 0043 — <b>本編のスクリーンとは別の面</b>を、
    /// 少し手前・少し外側に立てる。「せっかく VR で立体的なので、スクリーンにつけなくていい」。
    ///
    /// 出方は `canon/LEDGER.md` 0053（2026-08-16）:
    /// <b>枠が左端から右へ開き、開き切ってから文字が 1 字ずつ打たれる。</b>
    /// 引くときは逆で、文字が消えてから枠が左へ畳まれる。
    /// 装置が受信して、印字して、片づける — という順序がそのまま画になる。
    ///
    /// ⚠ <b>本編の進行を既読待ちにしない。</b> 読まなくても体験は進む（時間で引く）。
    /// 既読の操作を作らないのは、体験者が持つ唯一の入力（左のボタン ＝ 記録）と兼用させないため —
    /// 兼用すると「記録した」と「読んだ」が混ざって、押した時刻の意味が濁る。
    ///
    /// ⚠⚠ <b>ただし「押し始めたら走っている連絡は片づく」</b>（2026-08-16・
    /// <c>canon/LEDGER.md</c> 0065・<see cref="SetGuideWanted"/>）。ユーザーの
    /// 「X／Y を押して閉じる」への答えで、<b>体験者のボタンに 2 つ目の意味を与えずに</b>
    /// 「自分の行為がこの面に効く」を返す。閉じる操作ではないので、記録の時刻は濁らない。
    /// </summary>
    public sealed class CommsPanelLogic
    {
        /// <summary>枠が開き切るまで (秒)。</summary>
        public const float InSec = 0.45f;

        /// <summary>
        /// 1 秒あたり何文字打つか。速すぎると「一気に出た」に見え、遅いと読み終わる前に焦れる。
        ///
        /// ⚠⚠ <b>22 → 12 に落とした</b>（2026-08-16・<c>canon/LEDGER.md</c> 0056）。
        /// 1 文字 = 1 発の打鍵音を足したので、<b>この値がそのまま打鍵の間隔になる</b>。
        /// 22 文字/秒 ＝ 45ms 間隔では 1 発ずつが分かれて聞こえず、カタカタではなく
        /// 連続音になる（人が個々の打鍵を分けて聞けるのは 15〜20 発/秒あたりまで）。
        /// 12 ＝ 83ms は、実物のテレタイプ（10 文字/秒）と、
        /// もらった素材の押し込み→戻りの間隔（実測 80ms）の両方に近い。
        /// ⚠ 音の側は <see cref="CommsPanelLogic"/> の刻みを**そのまま数える**ので、
        /// ここを変えると打鍵の密度も一緒に変わる（対で直す必要は無い ＝ ずれようがない）。
        ///
        /// ⚠⚠ <b>これは日本語の速さ。Latin は <see cref="CharsPerSecLatin"/></b>
        /// （2026-09-04・<c>canon/LEDGER.md</c> 0149）。<see cref="CharsPerSecFor"/> が唯一の窓口。
        /// </summary>
        public const float CharsPerSec = 12f;

        /// <summary>
        /// English / Français を 1 秒あたり何文字打つか（2026-09-04・<c>canon/LEDGER.md</c> 0149・
        /// ユーザー報告「英語とフランス語は、日本語と同じ速さだと遅く感じるかもしれない」）。
        ///
        /// <b>同じことを言うのに Latin は仮名漢字の 1.8〜2.0 倍の字数が要る</b>（実測: 8 通の合計で
        /// 日本語 160 字 / English 292 字 / Français 312 字）。同じ速さで打つと、
        /// <b>読む側は 2 倍待つ</b>（①b は 2.92 秒 → 6.08 秒）。
        ///
        /// ⚠⚠ <b>上限を決めているのは打鍵音の粒立ち</b>（速いほど良いのではない）。実測:
        /// 音源 8 本は<b>実効 59.8ms</b>（-40dBFS まで）で、-12dB が 15ms・-20dB が 24.8ms・
        /// -30dB が 44.0ms。18 ＝ <b>55.6ms 間隔</b>なら前の一撃は -35dB まで落ちていて、
        /// 人が個々の打鍵を分けて聞ける 15〜20 発/秒の内側に収まる。
        /// ⚠ <b>実際の発音はもっと疎い</b> — 空白では鳴らない（<c>isVisible</c>）ので、
        /// English は約 17% が無音になり<b>およそ 15 発/秒</b>。
        /// ⚠ 22（45.5ms）は 0056 が「連続音になる」として退けた値。**ここを超えない**。
        ///
        /// ⚠ <b>「装置の声が言語で変わる」は体験者には起きない</b> — 1 人が浴びるのは 1 言語だけで、
        /// 2 つの速さを聞き比べられるのは開発側だけ。<b>読ませる相手は言語ごとに違う</b>方を採る。
        /// </summary>
        public const float CharsPerSecLatin = 18f;

        /// <summary>
        /// その言語の打鍵の速さ。<b>速さを読む所は必ずここを通す</b>
        /// （<c>CommsPanel</c> / <c>OutroReport</c> / テスト / 打鍵の見本）。
        /// </summary>
        public static float CharsPerSecFor(ShowLang lang)
            => lang == ShowLang.Ja ? CharsPerSec : CharsPerSecLatin;

        /// <summary>打ち終わるまでの下限・上限 (秒)。文面が伸びても間延びさせない。</summary>
        public const float MinTypeSec = 0.15f;

        /// <summary>
        /// 打ち終わるまでの上限 (秒)。
        ///
        /// ⚠⚠ <b>いちばん長い文面（①b の 37 文字 ＝ 3.08 秒）より長く保つ</b>。上限が効くと
        /// <b>打鍵の間隔が <see cref="CharsPerSec"/> より短くなる</b> ＝
        /// <b>その 1 通だけ速く打つ装置</b>になる（切り出した打鍵の尺 60ms を割れば連続音にもなる）。
        /// ⚠ 0096 で①が 4 行 48 文字になったとき 4.5 まで上げたが、
        /// 0097 で①と①b へ割ったので 3.5 へ戻した。
        /// **画は普通に出るので、音を聴くまで気づけない。**
        /// ⚠ 上限そのものを消さないのは、うっかり長い文面を書いたときの安全網だから。
        /// <c>CommsNoticeTextTests.EveryNotice_TypesAtTheDeviceSpeed</c> が
        /// <b>実際の文面で上限が効いていないこと</b>を確かめるので、次に文面を伸ばす人はそこで落ちる。
        ///
        /// ⚠⚠ <b>2026-09-03 に 3.5 → 7.0 へ上げた</b>（言語選択・3 言語）。
        /// 同じことを言うのに Latin は仮名漢字の<b>1.8〜2.0 倍の文字数</b>が要るので、
        /// 上限を上げないと English / Français のときだけ上限が効いて
        /// <b>その言語でだけ速く打つ</b>ことになる ＝ 実機で 1 言語だけ壊れる形になる。
        /// ⚠ <b>2026-09-04（0149）に速さの側も言語で分けた</b>（Latin 18 文字/秒）ので、
        /// いちばん長い Latin の文面でも 4.1 秒 ＝ <b>この上限には遠く届かない</b>。
        /// それでも下げないのは、<b>うっかり長い文面を書いたときの安全網</b>だから。
        /// ⚠ <b>日本語の見え方は 1 ビットも変わらない</b>（日本語の最長は 37 文字 ＝ 3.08 秒で、
        /// 旧上限 3.5 でも新上限 7.0 でも一度も効かない）。
        /// </summary>
        public const float MaxTypeSec = 7.0f;

        /// <summary>
        /// 読ませる時間 (秒)。<b>打ち終わってから</b>数える。<b>全文面で同じ値</b>。
        ///
        /// ⚠⚠ <b>役割ごとに分けていた 3 つ（4.0 / 2.5 / 5.0）は 2026-08-19 に畳んだ</b>
        /// （<c>canon/LEDGER.md</c> 0092・ユーザー指定「基本、出し切るまでに文字は読めるので、
        /// 出し切った後残す時間は一律 2s に」）。<b>読む時間は打鍵中にもう始まっている</b>ので、
        /// 打ち終わってから要るのは読み落としを拾う時間だけ — そこは文面の役割では変わらない。
        /// ⚠ 長い文面ほど画に居る時間は自然に長くなる（打つ尺が文字数から決まる）。
        ///   <b>だから役割で足す必要が無い</b>。
        /// ⚠ 0065 の「1 つの値を全文面に使わない」は<b>ここで覆っている</b>。
        ///   あれが避けたのは 7 秒固定（報告 1 回で面が 10.2 秒灯る）で、2 秒はその向きの逆。
        /// </summary>
        public const float HoldSec = 2.0f;

        /// <summary>
        /// <see cref="CommsDelivery.Fade"/> の連絡が浮かび上がるまで (秒)。
        ///
        /// ⚠ <b>0 にしない。</b> 1 フレームで出すと「浮かんだ」ではなく「点いた」に見える
        /// （装置が壊れて明滅したのと区別が付かない）。
        /// ⚠ <b>長くもしない。</b> 打つ間も惜しいから打たない一言なので、待たせたら意味が消える。
        /// </summary>
        public const float FadeInSec = 0.25f;

        /// <summary>
        /// <see cref="CommsDelivery.Fade"/> の連絡を読ませる時間 (秒)。
        ///
        /// ⚠⚠ <b>ここだけ <see cref="HoldSec"/>（一律 2 秒・<c>canon/LEDGER.md</c> 0092）から外れる。</b>
        /// あの 2 秒は「<b>打っているあいだにもう読み終わっている</b>ので、後は読み落としを拾うだけ」
        /// という理屈で、<b>打たない連絡にはその前提が無い</b>。ここは読む時間そのもの。
        /// ⚠ それでも短いのは、ユーザー指定が「<b>短く</b>その前に差し込む」だから（0168）。
        /// 画に居るのは <see cref="FadeInSec"/> ＋ ここ ＝ 1.35 秒
        /// （同じ 9 文字を打つと 0.75 ＋ 2.0 ＝ 2.75 秒）。
        /// </summary>
        public const float FadeHoldSec = 1.1f;

        /// <summary>乗っ取りが通信面へ侵入している時間 (秒)。この間、通信面は完全に隠す。</summary>
        public const float IntrusionSec = 0.9f;

        /// <summary>塗り替わり後の人形を保持する時間 (秒)。</summary>
        public const float PossessedHoldSec = 0.45f;

        /// <summary>乗っ取りを一斉に切断する時間 (秒)。</summary>
        public const float PossessedOutSec = 0.12f;

        /// <summary>塗り替わりの前に「遮断失敗」へ変わる時間 (秒)。</summary>
        public const float TakeoverBlockFailedLeadSec = 0.35f;

        /// <summary>その出方で読ませる時間 (秒)。<b>読ませる尺を読む所は必ずここを通す</b>。</summary>
        public static float HoldSecFor(CommsDelivery delivery)
            => delivery == CommsDelivery.Fade ? FadeHoldSec
             : delivery == CommsDelivery.Possessed ? PossessedHoldSec
             : HoldSec;

        /// <summary>引くまで (秒)。ぱっと消すと「消えた」ではなく「壊れた」に見える。</summary>
        public const float OutSec = 0.9f;

        /// <summary>
        /// 引くとき、文字が消え切るまで（<see cref="OutSec"/> に対する割合）。
        /// <b>枠が畳まれ始めるより先に消え切る</b> — 畳む枠から文字がはみ出さない。
        /// </summary>
        public const float GlyphOutAt = 0.35f;

        /// <summary>引くとき、枠が畳まれ始める時点（<see cref="OutSec"/> に対する割合）。</summary>
        public const float FoldStartAt = 0.35f;

        /// <summary>地の濃さが乗り切る時点（<see cref="InSec"/> に対する割合）。開き切る前に濃さは決まる。</summary>
        public const float PanelInkAt = 0.35f;

        /// <summary>
        /// 下段が出始める時点（<see cref="InSec"/> に対する割合）。<b>枠が開き切ってから</b>。
        /// 開いている途中に出すと、まだ枠の無い所へ字がはみ出す。
        /// </summary>
        public const float HintInAt = 0.75f;

        private CommsStage _stage = CommsStage.Off;
        private float _elapsed;
        private float _typeSec = MinTypeSec;
        // 憑依の出し方（0230）で読ませる秒。文字の段は 出る → 読ませる → 塗り替わる の合計（`_typeSec`）。
        private float _readSec = CommsPossessionLogic.ReadMinSec;
        private CommsDelivery _delivery = CommsDelivery.Typed;
        private bool _persistent;
        private bool _guideWanted;
        // 段へ入った瞬間の姿。**そこから動かす**ので、どの段へ移っても飛ばない。
        // ⚠⚠ **4 つ全部を覚える。** 2026-08-16 まで開きと丈しか継承しておらず、
        //    連絡が届くたびに**地の濃さと下段が 0 から張り直されていた** ＝ 走行中の面へ
        //    2 通目が来ると、受信票が 0.16 秒だけ黒へ落ちて戻る（画に出る不具合）。
        //    既存のテストは開き・丈・文字しか見ていなかったので素通りしていた。
        private float _openFrom, _bodyFrom, _panelFrom, _hintFrom, _glyphFrom, _revealFrom;

        // 呪いの斑の量（`canon/LEDGER.md` 0229）。目標は実行体が毎フレーム押し込み、
        // ここは**面が開いてからの立ち上がり**だけを持つ（開いた縁で 0 から数え直す）。
        private float _curseTarget, _curseFrom, _curseShown, _curseRampSec;
        // 面が開いてからの秒。乱れ（0231）の種に使う（段をまたいで単調に増える）。
        private float _openSec;
        // 乗っ取り開始からの単調な時計。段を移っても、演出が終わってもランリセットまでは戻さない。
        private float _takeoverElapsedSec;

        public CommsStage Stage => _stage;

        /// <summary>斑の目標（実行体が侵食度と文面から決めて押し込む）。</summary>
        public float CurseTarget => _curseTarget;

        /// <summary>いま面へ書く斑の量。畳まれていれば 0。</summary>
        public float CurseShown => _stage == CommsStage.Off ? 0f : _curseShown;

        /// <summary>
        /// 斑の目標を押し込む。<b>変わったときだけ</b>いまの量から目標へ
        /// <see cref="CommsCurseLogic.RampSec"/> で寄せ直す（開いている最中に侵食度が上がった場合）。
        /// ⚠ 立ち上がりを 0 から始めるのは<b>面が開いた縁</b>（<see cref="Begin"/> /
        /// <see cref="SetGuideWanted"/>）だけ。ここでは戻さない。
        /// </summary>
        public void SetCurseTarget(float target)
        {
            target = Clamp01(target);
            if (System.Math.Abs(target - _curseTarget) < 0.0001f) return;
            _curseFrom = CurseShown;
            _curseTarget = target;
            _curseRampSec = 0f;
        }

        /// <summary>面が開く縁。「出た初めは通常の面」（0229）— 斑を 0 から立ち上げ直す。</summary>
        private void RestartCurseRamp()
        {
            _curseFrom = 0f;
            _curseShown = 0f;
            _curseRampSec = 0f;
        }

        /// <summary>出ているか（実行体が面を描くべきか）。</summary>
        public bool Active => _stage != CommsStage.Off;

        /// <summary>
        /// 文字の段の長さ（この文面での実測値。プレビューと卓が読む）。打つ出し方では打ち終わるまで、
        /// 憑依の出し方（0230）では 出る → 読ませる → 塗り替わる の合計。
        /// </summary>
        public float TypeSec => _typeSec;

        /// <summary>
        /// いまの連絡の出方。<b>面が打鍵を鳴らすかはこれが決める</b>
        /// （<c>CommsPanel.Apply</c>）。
        /// </summary>
        public CommsDelivery Delivery => _delivery;

        /// <summary>現在の段に入ってからの秒数。専用表示とプレビューが同じ時計を読む。</summary>
        public float StageElapsedSec => _elapsed;

        /// <summary>乗っ取り開始からの秒数。段をまたいでも同じ純ロジック時計が単調に進む。</summary>
        public float TakeoverElapsedSec => _takeoverElapsedSec;

        /// <summary>遮断が間に合わない時点へ達したか。塗り替わりの 0.35 秒前から true。</summary>
        public bool TakeoverBlockFailed
            => _delivery == CommsDelivery.Possessed
               && (_stage == CommsStage.Type
                   && _elapsed + 0.000001f >= System.Math.Max(0f,
                       CommsPossessionLogic.SweepStartSec(_readSec) - TakeoverBlockFailedLeadSec)
                   || _stage == CommsStage.Hold
                   || _stage == CommsStage.Out);

        /// <summary>空間エラーの不透明度。通信面より先に出て、専用切断と同時に消える。</summary>
        public float TakeoverErrorOpacity
        {
            get
            {
                if (_delivery != CommsDelivery.Possessed) return 0f;
                switch (_stage)
                {
                    case CommsStage.Intrusion:
                        return 1f;
                    case CommsStage.In:
                        return Lerp(1f, 0.35f, Clamp01(_elapsed / InSec));
                    case CommsStage.Type:
                    case CommsStage.Hold:
                        return 0.35f;
                    case CommsStage.Out:
                        return 0.35f * PossessedCutOpacity;
                    default:
                        return 0f;
                }
            }
        }

        /// <summary>
        /// 憑依の出し方（0230）の段と値。他の出方と、面が畳まれている／開いている最中は <see cref="CommsPossessionPhase.Off"/>。
        /// 文字の段が「出る → 読ませる → 塗り替わる」で、読ませる段（Hold）以降は塗り替わったまま。
        /// </summary>
        public CommsPossessionSample PossessionSample
        {
            get
            {
                if (_delivery != CommsDelivery.Possessed || _stage == CommsStage.Off
                    || _stage == CommsStage.Intrusion || _stage == CommsStage.In)
                    return new CommsPossessionSample { phase = CommsPossessionPhase.Off };
                if (_stage == CommsStage.Type)
                    return CommsPossessionLogic.Sample(_elapsed, _readSec);
                // 読ませる段（呪われたまま）。乱れの尾（0231）は塗り替わり切った後も少し続くので時計を繋ぐ。
                if (_stage == CommsStage.Hold)
                    return CommsPossessionLogic.Sample(CommsPossessionLogic.DurationFor(_readSec) + _elapsed, _readSec);
                return new CommsPossessionSample { phase = CommsPossessionPhase.Cursed, show = 1f, sweep = 1f };
            }
        }

        /// <summary>
        /// <b>読ませ終わった</b>（<see cref="HoldSec"/> を満たした、または最初から何も出ていない）。
        ///
        /// ⚠⚠ <b>「畳み終わった」ではない。</b> ここが立った瞬間はまだ枠が開いているので、
        /// <b>次の連絡を入れれば同じ面のまま文面だけが替わる</b>（2026-08-19・
        /// <c>canon/LEDGER.md</c> 0096・ユーザー指定「前の言葉を表示して 2s たったら、
        /// そのスクリーンのまま、次の言葉が始まる。毎回消して表示しなおすのはしない」）。
        /// 畳み終わり（<see cref="CommsStage.Off"/>）を待って次を出すと、
        /// <b>枠が左へ畳まれてから開き直す</b> ＝ 毎回かならず吃る。
        /// ⚠ 押している最中（<see cref="CommsStage.Guide"/>）は「読ませ終わった」に含めない —
        ///   あれは連絡ではなく報告の手元表示で、次の連絡はその上へ届く。
        /// </summary>
        public bool DoneReading => _stage == CommsStage.Out || _stage == CommsStage.Off;

        /// <summary>
        /// 連絡が届いた。<b>すでに出ていれば頭から出し直す</b>（重ねない）。
        /// </summary>
        /// <param name="charCount">
        /// 打つ文字数。<b>尺はここから決まる</b>（文面が伸びれば打つ時間も伸びる）。
        /// 0 以下なら文字の段を飛ばす。
        /// </param>
        /// <param name="delivery">
        /// 出方（既定は打つ）。<see cref="CommsDelivery.Fade"/> なら
        /// <b>文面ごとすっと浮かび、打鍵は 1 発も鳴らない</b>（<c>canon/LEDGER.md</c> 0168）。
        /// </param>
        public void Begin(int charCount, CommsDelivery delivery = CommsDelivery.Typed,
                          bool persistent = false)
        {
            // 乗っ取りの時系列は通知で頭出しも中断もしない。
            if (_delivery == CommsDelivery.Possessed && _stage != CommsStage.Off) return;
            // ⚠⚠ **いまの姿から動かす**（`canon/LEDGER.md` 0058）。②の連絡は「押した瞬間」に届くので、
            //    開きを 0 から張り直すと**押し終わるたびに枠が畳まれて開き直る**（毎回かならず起きる吃り）。
            //    報告の手元表示で既に開いていれば、横は動かず**丈だけが伸びて文面の場所ができる**。
            // ⚠⚠ **枠も丈も出来上がっているなら、開く段そのものを飛ばす**（2026-08-19・0096）。
            //    通さないと、開き 1 → 1・丈 1 → 1 という**何も動かない 0.45 秒**が挟まり、
            //    そのあいだ面が空になる（体験者から見れば「消えて、少し待って、また出た」）。
            CommsWeights now = Weights;
            bool chained = _stage != CommsStage.Off && now.open >= 0.999f && now.body >= 0.999f;
            // ⚠ 面が畳まれた状態から開くときだけ、斑を 0 から立ち上げ直す（0229「出た初めはこれ」）。
            //   同じ面のまま次の文面へ繋ぐ（chained）ときは重なったままにする。
            if (_stage == CommsStage.Off) RestartCurseRamp();
            // ⚠ **出方を替えるのは段を移った後**（`EnterStage` は「いまの姿」を覚えるので、
            //   新しい出方の目で古い段を測らせない）。
            EnterStage(delivery == CommsDelivery.Possessed
                ? CommsStage.Intrusion
                : chained ? CommsStage.Type : CommsStage.In);
            _delivery = delivery;
            _persistent = persistent;
            if (delivery == CommsDelivery.Possessed) _takeoverElapsedSec = 0f;
            // 憑依の出し方（0230）: 文字の段は 出る → 読ませる（字数で伸びる）→ 塗り替わる の合計。
            _readSec = CommsPossessionLogic.ReadSecFor(charCount, ShowLanguage.Current);
            _typeSec = delivery == CommsDelivery.Possessed
                ? CommsPossessionLogic.DurationFor(_readSec)
                : delivery == CommsDelivery.Cursed
                ? CommsPossessionLogic.ShowSec
                : delivery == CommsDelivery.Fade
                ? FadeInSec
                : charCount <= 0
                    ? 0f
                    : Clamp(charCount / CharsPerSecFor(ShowLanguage.Current), MinTypeSec, MaxTypeSec);
        }

        /// <summary>段を移る。<b>いまの姿を覚えてから</b>移る（そこから動かすので飛ばない）。</summary>
        private void EnterStage(CommsStage next)
        {
            CommsWeights w = Weights;
            _openFrom = w.open;
            _bodyFrom = w.body;
            _panelFrom = w.panel;
            _hintFrom = w.hint;
            _glyphFrom = w.glyph;
            _revealFrom = w.reveal;
            _stage = next;
            _elapsed = 0f;
        }

        /// <summary>
        /// 報告の手元表示を出したいか（押している最中・余韻の最中）。
        /// <b>連絡が走っている最中は割り込まない</b> — 出したいままにしておけば、
        /// 読ませ終わったあとに <see cref="CommsStage.Guide"/> で開いたまま残る。
        /// </summary>
        public void SetGuideWanted(bool wanted)
        {
            bool rising = wanted && !_guideWanted;
            _guideWanted = wanted;
            // ⚠ 憑依の出し方（0230）の「出る → 読ませる → 塗り替わる」は途中で退かせない —
            //   塗り替わる前に畳むと、初見の人には「乗っ取られた」が一度も見えないまま終わる。
            //   押しっぱなしでも乗っ取りを最後まで描き、そのまま引く。
            if (_delivery == CommsDelivery.Possessed && _stage != CommsStage.Off) return;
            if (rising && (_stage == CommsStage.Off || _stage == CommsStage.Out))
            {
                // 畳まれた所から開くなら斑も 0 から（引いている最中からなら続きから）。
                if (_stage == CommsStage.Off) RestartCurseRamp();
                EnterStage(CommsStage.Guide);
            }
            // ⚠⚠ **押し始めたら、走っている連絡は片づく**（2026-08-16・`canon/LEDGER.md` 0065）。
            //    体験者が新しい行為を始めたのに前の通知が居座ると、装置が体験者を見ていないように
            //    見える。**これが「押して閉じる」の代わり** — 体験者のボタンに 2 つ目の意味を与えずに
            //    「自分の行為がこの面に効く」を返す唯一の形（記録の時刻は 1 ビットも濁らない）。
            //    ⚠ 移る先は `Out` ではなく `Guide`（枠は開いたまま、丈だけ縮んで文面が消える）。
            //      畳んでから開き直すと、1 秒後に届く②の連絡で必ず吃る。
            //    ⚠ `In`（枠が開いている最中）では退かせない — 出かけた文面を即座に引くと
            //      「装置が言いよどんだ」ように見えるだけで、体験者には何も伝わらない。
            else if (rising && (_stage == CommsStage.Type || _stage == CommsStage.Hold))
                EnterStage(CommsStage.Guide);
            else if (!wanted && _stage == CommsStage.Guide)
                EnterStage(CommsStage.Out);
        }

        /// <summary>導入の固定表示を終え、現在の面を静かに畳む。</summary>
        public void ReleasePersistent()
        {
            _persistent = false;
            if (_stage == CommsStage.Hold || _stage == CommsStage.Type)
                EnterStage(CommsStage.Out);
        }

        // ⚠ 「読ませ終わる前に引く」API（Retract）は 2026-08-17 に**足してすぐ外した**。
        //    導入の段 0 を抜けた瞬間に⓪b の指示を引かせるために作ったが、同じ縁で⓪c
        //    「ポイントに到着しました」が届くようになり（`canon/LEDGER.md` 0079 の赤入れ 3）、
        //    `Begin` の上書きで済むようになった。**呼び手の無い口を残さない。**

        /// <summary>畳む（ラン開始・本編を出た・中止）。</summary>
        public void Disable()
        {
            _stage = CommsStage.Off;
            _elapsed = 0f;
            _delivery = CommsDelivery.Typed;
            _persistent = false;
            _guideWanted = false;
            _openFrom = _bodyFrom = _panelFrom = _hintFrom = _glyphFrom = _revealFrom = 0f;
            _openSec = 0f;
            _takeoverElapsedSec = 0f;
            RestartCurseRamp();
        }

        /// <summary>時間を進める。</summary>
        public void Tick(float dt)
        {
            if (dt < 0f) dt = 0f;
            if (_stage == CommsStage.Off) return;
            _elapsed += dt;
            _openSec += dt;
            if (_delivery == CommsDelivery.Possessed) _takeoverElapsedSec += dt;
            // 斑は面が出ているあいだだけ進む（段に関わらず 1 本の時計）。
            _curseRampSec += dt;
            _curseShown = Lerp(_curseFrom, _curseTarget,
                               Smooth(Clamp01(_curseRampSec / CommsCurseLogic.RampSec)));
            switch (_stage)
            {
                case CommsStage.Intrusion:
                    if (_elapsed >= IntrusionSec) EnterStage(CommsStage.In);
                    break;
                case CommsStage.In:
                    if (_elapsed >= InSec) EnterStage(CommsStage.Type);
                    break;
                case CommsStage.Type:
                    if (_elapsed >= _typeSec) EnterStage(CommsStage.Hold);
                    break;
                case CommsStage.Hold:
                    // 読ませ終わったら引く。⚠ ただし**まだ押している最中なら開いたまま残す** —
                    //    引いてすぐ開き直すのは、体験者から見れば 1 度の操作の途中のちらつき。
                    if (!_persistent && _elapsed >= HoldSecFor(_delivery))
                        EnterStage(_delivery != CommsDelivery.Possessed && _guideWanted
                            ? CommsStage.Guide
                            : CommsStage.Out);
                    break;
                case CommsStage.Out:
                    if (_elapsed >= OutSecFor(_delivery))
                    {
                        // 乗っ取りは押しっぱなしで終わっても Guide へ戻さない。
                        // 離して次に押すまで、この保持要求は新しい立ち上がりにしない。
                        bool keepGuideLatch = _delivery == CommsDelivery.Possessed && _guideWanted;
                        FinishNotice();
                        if (keepGuideLatch) _guideWanted = true;
                    }
                    break;
                case CommsStage.Guide:
                    // 時間では終わらない。抜けるのは SetGuideWanted(false) か Begin か Disable。
                    break;
            }
        }

        /// <summary>いまの段から面と文字へ配る値。<b>見え方の判断はすべてここ</b>。</summary>
        public CommsWeights Weights
        {
            get
            {
                CommsWeights w = StageWeights;
                w.curse = CurseShown;
                if (_delivery == CommsDelivery.Possessed && _stage != CommsStage.Off)
                {
                    // 憑依の出し方（0230）: 斑ではなく前線。塗り替わる前は通常の面（斑 0）、
                    // 塗り替わった後は全面（0.75 でも 1 でも同じ Max）。前線の進みは `sweep`。
                    CommsPossessionSample s = PossessionSample;
                    w.sweep = s.sweep;
                    w.tear = s.tear;
                    w.tearSeed = _openSec;
                    w.curse = s.phase == CommsPossessionPhase.Cursed ? 1f : 0f;
                }
                else if (_delivery == CommsDelivery.Cursed && _stage != CommsStage.Off)
                {
                    // 乗っ取り後の連絡は、通常の面を 1 フレームも見せず最初から人形。
                    w.curse = 1f;
                    w.sweep = 1f;
                }
                return w;
            }
        }

        private CommsWeights StageWeights
        {
            get
            {
                switch (_stage)
                {
                    case CommsStage.Intrusion:
                        return CommsWeights.Hidden;
                    case CommsStage.In:
                    {
                        // 枠は左端から右へ開き、**同時に文面のぶんだけ丈が伸びる**。
                        // ⚠ 動かすのは「いまの姿から」（`_openFrom` / `_bodyFrom`）。報告の手元表示で
                        //    既に開いていれば横は 1 のままで、丈だけが伸びる ＝ 横の吃りが出ない。
                        // ⚠ **地の濃さは先に決まる**（開きながら明るくなると「2 つのことが
                        //    起きている」に見える。動いているのは形だけにする）。
                        float k = Smooth(Clamp01(_elapsed / InSec));
                        return new CommsWeights
                        {
                            // ⚠ 濃さも下段も**いまの値から動かす**（開き・丈と同じ）。
                            //   0 から張り直すと、走行中の面へ 2 通目が来たときに
                            //   受信票が黒へ落ちて戻る／下段が消えて戻る（2026-08-16 に直した）。
                            panel = Lerp(_panelFrom, 1f,
                                         Smooth(Clamp01(_elapsed / InSec / PanelInkAt))),
                            glyph = 1f,
                            open = Lerp(_openFrom, 1f, k),
                            body = Lerp(_bodyFrom, 1f, k),
                            reveal = 0f,
                            // 下段は**枠が開き切ってから**出る（開いている途中に出すと枠の外へはみ出す）。
                            hint = Lerp(_hintFrom, 1f,
                                        Smooth(Clamp01((_elapsed / InSec - HintInAt) / (1f - HintInAt)))),
                        };
                    }
                    case CommsStage.Type:
                    {
                        // ⚠⚠ **憑依の出し方**（`canon/LEDGER.md` 0230）。字は最初から全部そこに在って
                        //    （`reveal = 1`）、`ShowSec` でふっと出る。塗り替わりは `Weights` が `sweep` へ出す。
                        if (_delivery == CommsDelivery.Possessed)
                        {
                            CommsPossessionSample s = CommsPossessionLogic.Sample(_elapsed, _readSec);
                            return new CommsWeights
                            { panel = 1f, glyph = s.show, open = 1f, body = 1f, reveal = 1f, hint = 1f };
                        }
                        // ⚠⚠ **すっと浮かぶ出方**（`canon/LEDGER.md` 0168）。字は最初から全部
                        //    そこに在って（`reveal = 1`）、**濃さだけが上がる**。
                        //    1 字ずつ出さないので `CommsPanel.Apply` の打鍵も鳴らない
                        //    （あちらは `shown` の増分で鳴らすが、増分は 1 回きり ＝ `_silent` で止める）。
                        if (_delivery == CommsDelivery.Fade || _delivery == CommsDelivery.Cursed)
                        {
                            float f = Smooth(_typeSec <= 0f ? 1f : Clamp01(_elapsed / _typeSec));
                            return new CommsWeights
                            { panel = 1f, glyph = f, open = 1f, body = 1f, reveal = 1f, hint = 1f };
                        }
                        // ⚠ **打つところは滑らかにしない。** ここを smoothstep で均すと
                        //    打鍵の間隔が伸び縮みして「機械が打っている」に見えない。
                        float p = _typeSec <= 0f ? 1f : Clamp01(_elapsed / _typeSec);
                        return new CommsWeights
                        { panel = 1f, glyph = 1f, open = 1f, body = 1f, reveal = p, hint = 1f };
                    }
                    case CommsStage.Hold:
                        return new CommsWeights
                        { panel = 1f, glyph = 1f, open = 1f, body = 1f, reveal = 1f, hint = 1f };
                    case CommsStage.Guide:
                    {
                        // 枠は開いているが、**文面のぶんの丈は無い**（下段だけの細い受信票）。
                        // 連絡を読ませ終わって戻ってきたときは、丈がここで縮む。
                        float k = Smooth(Clamp01(_elapsed / InSec));
                        return new CommsWeights
                        {
                            panel = Lerp(_panelFrom, 1f, k),
                            glyph = 1f,
                            open = Lerp(_openFrom, 1f, k),
                            body = Lerp(_bodyFrom, 0f, k),
                            reveal = 0f,
                            hint = Lerp(_hintFrom, 1f,
                                        Smooth(Clamp01((_elapsed / InSec - HintInAt) / (1f - HintInAt)))),
                        };
                    }
                    case CommsStage.Out:
                    {
                        if (_delivery == CommsDelivery.Possessed)
                        {
                            float cut = PossessedCutOpacity;
                            return new CommsWeights
                            {
                                panel = cut,
                                glyph = cut,
                                open = 1f,
                                body = 1f,
                                reveal = 1f,
                                hint = cut,
                            };
                        }
                        float t = Clamp01(_elapsed / OutSec);
                        // ⚠ **文字が先に消えてから枠が畳まれる。** 逆にすると、畳む枠から
                        //    文字がはみ出して「潰された」に見える。
                        float g = Smooth(Clamp01(t / GlyphOutAt));
                        float fold = Smooth(Clamp01((t - FoldStartAt) / (1f - FoldStartAt)));
                        return new CommsWeights
                        {
                            panel = _panelFrom,
                            glyph = _glyphFrom * (1f - g),
                            open = (1f - fold) * _openFrom,
                            body = _bodyFrom,     // 丈は畳むあいだ動かさない（横だけが閉じる）
                            reveal = _revealFrom,
                            hint = _hintFrom * (1f - g),   // 下段も文面と一緒に消える（枠より先に）
                        };
                    }
                    default:
                        return CommsWeights.Hidden;
                }
            }
        }

        private float PossessedCutOpacity => 1f - Smooth(Clamp01(_elapsed / PossessedOutSec));

        private static float OutSecFor(CommsDelivery delivery)
            => delivery == CommsDelivery.Possessed ? PossessedOutSec : OutSec;

        /// <summary>通知の自然終了。乗っ取りの通算時計だけは観測のため保持する。</summary>
        private void FinishNotice()
        {
            _stage = CommsStage.Off;
            _elapsed = 0f;
            _delivery = CommsDelivery.Typed;
            _persistent = false;
            _guideWanted = false;
            _openFrom = _bodyFrom = _panelFrom = _hintFrom = _glyphFrom = _revealFrom = 0f;
            _openSec = 0f;
            RestartCurseRamp();
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

        private static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);

        private static float Smooth(float t)
        {
            t = Clamp01(t);
            return t * t * (3f - 2f * t);
        }
    }
}
