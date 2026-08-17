#nullable enable

namespace FixedCamVr.Streaming
{
    /// <summary>連絡の面の段。</summary>
    public enum CommsStage
    {
        /// <summary>出していない。</summary>
        Off,
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
    /// 既読の操作を作らないのは、体験者が持つ唯一の入力（左 X ＝ 記録）と兼用させないため —
    /// 兼用すると「記録した」と「読んだ」が混ざって、押した時刻の意味が濁る。
    ///
    /// ⚠⚠ <b>ただし「押し始めたら走っている連絡は片づく」</b>（2026-08-16・
    /// <c>canon/LEDGER.md</c> 0065・<see cref="SetGuideWanted"/>）。ユーザーの
    /// 「X／Y を押して閉じる」への答えで、<b>X／Y に 2 つ目の意味を与えずに</b>
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
        /// </summary>
        public const float CharsPerSec = 12f;

        /// <summary>打ち終わるまでの下限・上限 (秒)。文面が伸びても間延びさせない。</summary>
        public const float MinTypeSec = 0.15f;

        /// <summary>
        /// 打ち終わるまでの上限 (秒)。
        ///
        /// ⚠⚠ <b>2.5 → 3.5 へ上げた</b>（2026-08-17・<c>canon/LEDGER.md</c> 0073 で①へ自己紹介を
        /// 足し、41 文字になったため）。上限が効くと<b>打鍵の間隔が <see cref="CharsPerSec"/> より
        /// 短くなる</b> — 41 文字を 2.5 秒で打つと 61ms 間隔で、切り出した打鍵の尺（60ms）と
        /// ぶつかって<b>1 発ずつが繋がった連続音</b>になる（`rules/sound-design.md` §4）。
        /// **画は普通に出るので、音を聴くまで気づけない。**
        /// ⚠ 上限そのものを消さないのは、うっかり長い文面を書いたときの安全網だから。
        /// <c>CommsNoticeTextTests.LongestNotice_TypesSlowEnough_ForTheKeystrokeClips</c> が
        /// 実際の文面で間隔を測るので、次に文面を伸ばす人はそこで落ちる。
        /// </summary>
        public const float MaxTypeSec = 3.5f;

        /// <summary>
        /// 読ませる時間 (秒)。<b>打ち終わってから</b>数える。
        ///
        /// ⚠⚠ <b>1 つの値を全文面に使わない</b>（2026-08-16・<c>canon/LEDGER.md</c> 0065）。
        /// 7 秒固定だったころは、報告 1 回で面が <b>10.2 秒</b>灯っていた（3 分の体験の 5.7%）。
        /// <b>読む時間は打鍵中にもう始まっている</b>ので、打ち終わってから要るのは
        /// 「読み落としたぶんを拾う」時間だけ。それが何秒要るかは<b>文面の役割で違う</b>。
        /// </summary>
        public const float HoldSec = HoldBriefSec;

        /// <summary>
        /// ① 調査の指示。<b>本編の入口で 1 度だけ</b>出て、操作の教え方もここに乗る。
        /// 読み落とすと押し方が分からないまま体験が終わるので、受領より長い。
        /// </summary>
        public const float HoldBriefSec = 4.0f;

        /// <summary>
        /// ② 報告の受領。<b>いちばん短い</b> — 体験者<b>自身の行為</b>への返事なので、
        /// 何が起きたかは既に分かっている。読み落としても失うものが無い。
        /// ⚠ ここが長いと、押すたびに視界の隅が塞がって「装置が喋りすぎ」になる。
        /// </summary>
        public const float HoldReceiptSec = 2.5f;

        /// <summary>
        /// ③ 締めの催促。<b>いちばん長い</b> — これを読まないと締めのカットが進まない
        /// （体験者が押すまで待っている）。読ませ切ることを優先する。
        /// </summary>
        public const float HoldUrgentSec = 5.0f;

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
        private float _holdSec = HoldBriefSec;
        private bool _guideWanted;
        // 段へ入った瞬間の姿。**そこから動かす**ので、どの段へ移っても飛ばない。
        // ⚠⚠ **4 つ全部を覚える。** 2026-08-16 まで開きと丈しか継承しておらず、
        //    連絡が届くたびに**地の濃さと下段が 0 から張り直されていた** ＝ 走行中の面へ
        //    2 通目が来ると、受信票が 0.16 秒だけ黒へ落ちて戻る（画に出る不具合）。
        //    既存のテストは開き・丈・文字しか見ていなかったので素通りしていた。
        private float _openFrom, _bodyFrom, _panelFrom, _hintFrom;

        public CommsStage Stage => _stage;

        /// <summary>出ているか（実行体が面を描くべきか）。</summary>
        public bool Active => _stage != CommsStage.Off;

        /// <summary>打ち終わるまでの秒（この文面での実測値。プレビューと卓が読む）。</summary>
        public float TypeSec => _typeSec;

        /// <summary>
        /// 連絡が届いた。<b>すでに出ていれば頭から出し直す</b>（重ねない）。
        /// </summary>
        /// <param name="charCount">
        /// 打つ文字数。<b>尺はここから決まる</b>（文面が伸びれば打つ時間も伸びる）。
        /// 0 以下なら文字の段を飛ばす。
        /// </param>
        /// <param name="holdSec">
        /// 読ませる時間 (秒)。<b>文面の役割ごとに違う</b>（<see cref="HoldBriefSec"/> /
        /// <see cref="HoldReceiptSec"/> / <see cref="HoldUrgentSec"/>）。0 以下は既定へ倒す。
        /// </param>
        public void Begin(int charCount, float holdSec = HoldBriefSec)
        {
            // ⚠⚠ **いまの姿から動かす**（`canon/LEDGER.md` 0058）。②の連絡は「押した瞬間」に届くので、
            //    開きを 0 から張り直すと**押し終わるたびに枠が畳まれて開き直る**（毎回かならず起きる吃り）。
            //    報告の手元表示で既に開いていれば、横は動かず**丈だけが伸びて文面の場所ができる**。
            EnterStage(CommsStage.In);
            _typeSec = charCount <= 0
                ? 0f
                : Clamp(charCount / CharsPerSec, MinTypeSec, MaxTypeSec);
            _holdSec = holdSec > 0f ? holdSec : HoldBriefSec;
        }

        /// <summary>段を移る。<b>いまの姿を覚えてから</b>移る（そこから動かすので飛ばない）。</summary>
        private void EnterStage(CommsStage next)
        {
            CommsWeights w = Weights;
            _openFrom = w.open;
            _bodyFrom = w.body;
            _panelFrom = w.panel;
            _hintFrom = w.hint;
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
            if (wanted && (_stage == CommsStage.Off || _stage == CommsStage.Out))
                EnterStage(CommsStage.Guide);
            // ⚠⚠ **押し始めたら、走っている連絡は片づく**（2026-08-16・`canon/LEDGER.md` 0065）。
            //    体験者が新しい行為を始めたのに前の通知が居座ると、装置が体験者を見ていないように
            //    見える。**これが「押して閉じる」の代わり** — X／Y に 2 つ目の意味を与えずに
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

        // ⚠ 「読ませ終わる前に引く」API（Retract）は 2026-08-17 に**足してすぐ外した**。
        //    導入の段 0 を抜けた瞬間に⓪b の指示を引かせるために作ったが、同じ縁で⓪c
        //    「ポイントに到着しました」が届くようになり（`canon/LEDGER.md` 0079 の赤入れ 3）、
        //    `Begin` の上書きで済むようになった。**呼び手の無い口を残さない。**

        /// <summary>畳む（ラン開始・本編を出た・中止）。</summary>
        public void Disable()
        {
            _stage = CommsStage.Off;
            _elapsed = 0f;
            _guideWanted = false;
            _openFrom = _bodyFrom = _panelFrom = _hintFrom = 0f;
        }

        /// <summary>時間を進める。</summary>
        public void Tick(float dt)
        {
            if (dt < 0f) dt = 0f;
            if (_stage == CommsStage.Off) return;
            _elapsed += dt;
            switch (_stage)
            {
                case CommsStage.In:
                    if (_elapsed >= InSec) EnterStage(CommsStage.Type);
                    break;
                case CommsStage.Type:
                    if (_elapsed >= _typeSec) EnterStage(CommsStage.Hold);
                    break;
                case CommsStage.Hold:
                    // 読ませ終わったら引く。⚠ ただし**まだ押している最中なら開いたまま残す** —
                    //    引いてすぐ開き直すのは、体験者から見れば 1 度の操作の途中のちらつき。
                    if (_elapsed >= _holdSec)
                        EnterStage(_guideWanted ? CommsStage.Guide : CommsStage.Out);
                    break;
                case CommsStage.Out:
                    if (_elapsed >= OutSec) Disable();
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
                switch (_stage)
                {
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
                        float t = Clamp01(_elapsed / OutSec);
                        // ⚠ **文字が先に消えてから枠が畳まれる。** 逆にすると、畳む枠から
                        //    文字がはみ出して「潰された」に見える。
                        float g = Smooth(Clamp01(t / GlyphOutAt));
                        float fold = Smooth(Clamp01((t - FoldStartAt) / (1f - FoldStartAt)));
                        return new CommsWeights
                        {
                            panel = _panelFrom,
                            glyph = 1f - g,
                            open = (1f - fold) * _openFrom,
                            body = _bodyFrom,     // 丈は畳むあいだ動かさない（横だけが閉じる）
                            reveal = 1f,
                            hint = _hintFrom * (1f - g),   // 下段も文面と一緒に消える（枠より先に）
                        };
                    }
                    default:
                        return CommsWeights.Hidden;
                }
            }
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
