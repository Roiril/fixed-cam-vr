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
        /// </summary>
        public const float CharsPerSec = 12f;

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
        /// 同じことを言うのに Latin は仮名漢字の<b>およそ 2.4 倍の文字数</b>が要る
        /// （①b は 日本語 37 文字 / English 75 文字）。<b>打鍵の速さは装置の声なので言語で変えない</b>
        /// （<see cref="CharsPerSec"/> を上げると、日本語のときだけ別の装置に聞こえる）ので、
        /// 伸びるのは<b>尺の側</b>。上限を上げないと English / Français のときだけ上限が効いて
        /// <b>その言語でだけ速く打つ</b>ことになる ＝ 実機で 1 言語だけ壊れる形になる。
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
        public void Begin(int charCount)
        {
            // ⚠⚠ **いまの姿から動かす**（`canon/LEDGER.md` 0058）。②の連絡は「押した瞬間」に届くので、
            //    開きを 0 から張り直すと**押し終わるたびに枠が畳まれて開き直る**（毎回かならず起きる吃り）。
            //    報告の手元表示で既に開いていれば、横は動かず**丈だけが伸びて文面の場所ができる**。
            // ⚠⚠ **枠も丈も出来上がっているなら、開く段そのものを飛ばす**（2026-08-19・0096）。
            //    通さないと、開き 1 → 1・丈 1 → 1 という**何も動かない 0.45 秒**が挟まり、
            //    そのあいだ面が空になる（体験者から見れば「消えて、少し待って、また出た」）。
            CommsWeights now = Weights;
            bool chained = _stage != CommsStage.Off && now.open >= 0.999f && now.body >= 0.999f;
            EnterStage(chained ? CommsStage.Type : CommsStage.In);
            _typeSec = charCount <= 0
                ? 0f
                : Clamp(charCount / CharsPerSec, MinTypeSec, MaxTypeSec);
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
                    if (_elapsed >= HoldSec)
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
