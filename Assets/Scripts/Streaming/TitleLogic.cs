#nullable enable

namespace FixedCamVr.Streaming
{
    /// <summary>タイトル画面の段。</summary>
    public enum TitleStage
    {
        /// <summary>出していない（本編中・終了後・そもそも切ってある）。</summary>
        Off,
        /// <summary>
        /// <b>何も見えない真っ暗。A を待っている。</b>
        /// 2026-08-12 のユーザー指示で足した段 — 周回リセット直後はここに居る。
        /// 字も光も出さない（黒だけ）ので、A が「閉じる」ではなく<b>「呼び出す」</b>に変わった。
        /// </summary>
        Wait,
        /// <summary>字が現れている途中。</summary>
        In,
        /// <summary>出し切って、A を待っている。</summary>
        Hold,
        /// <summary>A が押された。光が走り、字が中心へ巻き込まれて焼け落ち、黒が開く。</summary>
        Out,
        /// <summary>閉じ切った。以後この体験では二度と出ない（ランリセットで戻る）。</summary>
        Done,
    }

    /// <summary>
    /// タイトルの各層へ配る値。<b>見え方の判断はすべて <see cref="TitleLogic"/> に集約する</b>
    /// （実行体は配るだけ — <see cref="IntroWeights"/> と同じ流儀）。struct なので毎フレーム作ってよい。
    /// </summary>
    public struct TitleWeights
    {
        /// <summary>黒の不透明度。1 で現実が 1 画素も見えない。</summary>
        public float veil;
        /// <summary>文字全体の不透明度。</summary>
        public float glyph;
        /// <summary>出現の進み（0 = 何も無い / 1 = 出し切り）。下から線が点いて面が満ちる。</summary>
        public float reveal;
        /// <summary>焼ける量（0 = そのまま / 1 = 焼け切り）。</summary>
        public float dissolve;
        /// <summary>
        /// 渦の進み（0 = そのまま / 1 = 巻き切り）。<b>版の中で中心へ寄せて、ねじる</b>。
        ///
        /// ⚠ <b>奥行きではない。</b> 2026-08-12 に「奥へ飛んでいくと箱に吸収されたみたいで変」と
        /// 言われて <c>RecedeM</c>（0.55m 退く）を捨てた代わりに置いたもの。
        /// z を引くと題字は視界の中で一様に小さくなるので、消滅ではなく<b>遠ざかった</b>と読まれる。
        /// </summary>
        public float swirl;
        /// <summary>走り抜ける光の位置（0..1 で左から右へ）。</summary>
        public float flashPos;
        /// <summary>走り抜ける光の強さ。</summary>
        public float flashAmt;
        /// <summary>文字の前後移動 (m)。<b>正で手前</b>。押した瞬間に迫り、そのあと奥へ退く。</summary>
        public float pushM;

        /// <summary>何も出していない状態。</summary>
        public static TitleWeights Hidden => new TitleWeights();
    }

    /// <summary>タイトルが読む観測値。</summary>
    public struct TitleInput
    {
        /// <summary>A が押された（この 1 フレームだけ true）。</summary>
        public bool dismissRequested;

        /// <summary>
        /// <b>体験エリアを隠すものが実際に立っているか</b>（封印の箱か隔離の黒）。
        /// 黒を開き始めてよいかの唯一の判断材料。<b>ただし待つのは
        /// <see cref="ConcealWaitMaxSec"/> まで</b> — ここをラッチにすると、隠すものが
        /// 壊れている現場でタイトルが永久に閉じない（この codebase が 4 回踏んだ型）。
        /// </summary>
        public bool concealReady;

        /// <summary>位置合わせ中など、いま画面を譲るべきか。譲っている間は時間も進めない。</summary>
        public bool suspended;
    }

    /// <summary>
    /// タイトル画面の状態機械（UnityEngine 非依存・dt 注入）。
    ///
    /// 段は <see cref="TitleStage"/> の 5 つだけで、<c>ShowPhase</c> も <c>IntroStage</c> も増やさない。
    /// タイトルは導入の<b>段 0（開始待ち）に被さる薄い層</b>で、体験の骨格には手を入れていない。
    ///
    /// ⚠ <b>閉じ方は 2 つある</b>: A（<see cref="RequestDismiss"/>）と、
    /// 卓の ⏭ 等で導入が段 0 を出たとき（実行体が <see cref="ForceClose"/> を打つ）。
    /// 片方しか無いと、コントローラが死んでいる現場でタイトルから出られない。
    /// </summary>
    public sealed class TitleLogic
    {
        /// <summary>
        /// 出すまでの間。
        /// ⚠ **0.5 → 0 にした**（2026-08-12）。A と同時に音の一撃が鳴るので、
        /// 字が半秒遅れて出ると「押した音」と「出た字」が別の出来事になる。
        /// </summary>
        public const float InDelaySec = 0f;

        /// <summary>
        /// 字が現れ切るまで。
        /// ⚠ **1.7 → 0.6 にした**（2026-08-12）。ユーザー指示は
        /// 「シネマチックタイトルの音<b>とともに</b>タイトルを表示」で、音の一撃は頭 0.09 秒にある。
        /// ゆっくり出すと一撃から取り残される。0 にしないのは、ぱっと出ると「UI が出た」に見えるから。
        /// </summary>
        public const float InSec = 0.6f;

        /// <summary>
        /// <b>A を押してから字が消え始めるまで</b>（出現の時間を含む）。
        /// 2026-08-12 ユーザー指示「2s でタイトルが消え今まで通りのパススルーとしよう」。
        /// ⚠ <b>A からの通算</b>で測る（Hold に入ってから 2 秒ではない）。
        /// </summary>
        public const float AutoDismissSec = 2.0f;

        /// <summary>
        /// 呼び出した直後に A の二度押しで即閉じないための不感時間。
        /// これが無いと、A を軽く 2 回叩いた現場でタイトルが一瞬で消える。
        /// </summary>
        public const float DismissLockoutSec = 0.4f;

        /// <summary>A の手応え。光が字を走り抜ける。<b>押した瞬間に始める</b>（遅れると効かない）。</summary>
        public const float FlashSec = 0.42f;

        /// <summary>光が走ってから字が焼け始めるまで。</summary>
        public const float DissolveDelaySec = 0.20f;

        /// <summary>
        /// 字が焼け切るまで。
        /// ⚠ **1.15 → 1.40 にした**（2026-08-13）。渦を強くした（中心 300°）ので、
        /// 短いと巻き込みが読めるより先に焼け終わる。**尺は渦が要求している**。
        /// </summary>
        public const float DissolveSec = 1.40f;

        /// <summary>黒が開き切るまで（クロスフェード）。</summary>
        public const float OpenSec = 1.25f;

        /// <summary>
        /// 隠すものが立つのを待つ上限。**超えたら諦めて開く**。
        /// 段 0 では封印の箱も隔離の黒も毎フレーム描かれているので、通常この待ちは 0 フレームで抜ける。
        /// </summary>
        public const float ConcealWaitMaxSec = 0.5f;

        /// <summary>A を押した瞬間に字が迫る量 (m)。<b>山なので必ず 0 へ戻る</b>。</summary>
        public const float PushM = 0.06f;

        /// <summary>
        /// 灰だけになった残りを引き取る点（焼けの進み）。ここから <c>glyph</c> が落ち始める。
        ///
        /// ⚠ <b>頭から不透明度を落とさない。</b> 墨は焼け際（<c>dissolve</c>）が食っていくので、
        /// 同時に全体を薄くすると<b>まだ焼けていない所まで半透明になる</b>
        /// （＝「燃えている」ではなく「フェードアウトしている」に見える）。
        /// </summary>
        public const float AshFadeFrom = 0.88f;

        private TitleStage _stage = TitleStage.Off;
        private float _elapsed;      // 段に入ってからの秒
        private float _openElapsed;  // 黒が開き始めてからの秒
        private bool _opening;
        private float _concealWait;

        public TitleStage Stage => _stage;

        /// <summary>いまタイトルが画面を持っているか（＝導入の開始合図を止めてよいか）。</summary>
        public bool Active => _stage == TitleStage.Wait || _stage == TitleStage.In
                              || _stage == TitleStage.Hold || _stage == TitleStage.Out;

        /// <summary>A を待っているか（＝ 真っ暗のまま呼び出しを待っている）。</summary>
        public bool AwaitingInput => _stage == TitleStage.Wait;

        /// <summary>字が立っているか。<b>音の一撃を鳴らす縁はここ。</b></summary>
        public bool GlyphShowing => _stage == TitleStage.In || _stage == TitleStage.Hold;

        /// <summary>
        /// 頭から出す（ランリセット・起動）。
        /// ⚠ **入るのは <see cref="TitleStage.Wait"/>（真っ暗）。** 字はまだ出さない。
        /// </summary>
        public void Begin()
        {
            _stage = TitleStage.Wait;
            _elapsed = 0f;
            _openElapsed = 0f;
            _opening = false;
            _concealWait = 0f;
        }

        /// <summary>出さない（本編中・終了後・切ってある）。</summary>
        public void Disable()
        {
            _stage = TitleStage.Off;
            _elapsed = 0f;
            _openElapsed = 0f;
            _opening = false;
        }

        /// <summary>
        /// A。<b>段で意味が変わる。</b>
        ///   Wait → 題字を呼び出す（音の一撃と同時）
        ///   In / Hold → 閉じる（自動で閉じるので通常は使わない。現場の逃げ道）
        /// </summary>
        public void RequestAdvance()
        {
            if (_stage == TitleStage.Wait)
            {
                _stage = TitleStage.In;
                _elapsed = 0f;
                return;
            }
            if (_elapsed < DismissLockoutSec) return;   // 二度押しで一瞬で消えないように
            RequestDismiss();
        }

        /// <summary>閉じる演出へ入る。<b>出現の途中でも受ける</b>（待たされる方が不快）。</summary>
        public void RequestDismiss()
        {
            if (_stage != TitleStage.In && _stage != TitleStage.Hold) return;
            // ⚠ **段を変える前に読む。** RevealNow は段を見るので、Out にしてから読むと
            //    必ず 1 が返り、出現の途中で押されたときに字が一瞬で完成してから消える
            //    （＝ 巻き戻ったように見える）。2026-08-12 にテストが捕まえた。
            _dismissReveal = RevealNow();
            _stage = TitleStage.Out;
            _elapsed = 0f;
            _openElapsed = 0f;
            _opening = false;
            _concealWait = 0f;
        }

        /// <summary>
        /// 演出なしで即座に畳む。卓の ⏭ 等で<b>導入が段 0 を出てしまったとき</b>の逃げ道で、
        /// これが無いとコントローラが死んでいる現場でタイトルから出られない。
        /// </summary>
        public void ForceClose()
        {
            if (_stage == TitleStage.Off) return;
            _stage = TitleStage.Done;
        }

        /// <summary>閉じる演出に入った時点の出現の進み（途中で押されても字が飛ばないように保つ）。</summary>
        private float _dismissReveal = 1f;

        /// <summary>時間を進める。</summary>
        public void Tick(float dt, TitleInput input)
        {
            if (dt < 0f) dt = 0f;
            if (_stage == TitleStage.Off || _stage == TitleStage.Done) return;

            // 譲っている間は時計を止める。再開したら続きから（やり直すと字が 2 度出る）。
            if (input.suspended) return;

            if (input.dismissRequested) RequestAdvance();

            _elapsed += dt;

            switch (_stage)
            {
                case TitleStage.Wait:
                    // 真っ暗のまま待つ。**時間では進まない**（A か ForceClose だけが出口）。
                    break;

                case TitleStage.In:
                    // ⚠ Hold へ移っても `_elapsed` を 0 に戻さない。
                    //    AutoDismissSec は **A からの通算**で測るため。
                    if (_elapsed >= InDelaySec + InSec) _stage = TitleStage.Hold;
                    break;

                case TitleStage.Hold:
                    // 2026-08-12 から**時間で閉じる**（A を押しっぱなしにする必要が無い）。
                    if (_elapsed >= AutoDismissSec) RequestDismiss();
                    break;

                case TitleStage.Out:
                    // 黒を開き始めてよいか。**隠すものが立っていることを確かめてから**開く。
                    // 立っていなくても ConcealWaitMaxSec で諦める（ラッチにしない）。
                    if (!_opening)
                    {
                        _concealWait += dt;
                        if (input.concealReady || _concealWait >= ConcealWaitMaxSec) _opening = true;
                    }
                    else
                    {
                        _openElapsed += dt;
                    }
                    if (_opening && _openElapsed >= OpenSec &&
                        _elapsed >= DissolveDelaySec + DissolveSec)
                    {
                        _stage = TitleStage.Done;
                    }
                    break;
            }
        }

        private float RevealNow()
        {
            if (_stage == TitleStage.Hold) return 1f;
            if (_stage != TitleStage.In) return 1f;
            return Clamp01((_elapsed - InDelaySec) / InSec);
        }

        /// <summary>いまの段から各層への値を出す。<b>見え方の判断はすべてここ</b>。</summary>
        public TitleWeights Weights
        {
            get
            {
                switch (_stage)
                {
                    case TitleStage.Off:
                    case TitleStage.Done:
                        return TitleWeights.Hidden;

                    case TitleStage.Wait:
                        // **黒だけ。** 字も光も出さない。
                        return new TitleWeights { veil = 1f };

                    case TitleStage.In:
                    {
                        float r = RevealNow();
                        return new TitleWeights
                        {
                            // ⚠ 黒は**最初のフレームから 1**。ここを立ち上げると、起動直後の
                            // 1 フレームだけ現実が覗く（一瞬でも見せない、が依頼の要求）。
                            veil = 1f,
                            glyph = 1f,
                            reveal = r,
                        };
                    }

                    case TitleStage.Hold:
                        return new TitleWeights { veil = 1f, glyph = 1f, reveal = 1f };

                    case TitleStage.Out:
                    {
                        float d = Clamp01((_elapsed - DissolveDelaySec) / DissolveSec);
                        float open = _opening ? Clamp01(_openElapsed / OpenSec) : 0f;
                        float f = Clamp01(_elapsed / FlashSec);
                        // 渦は**押した瞬間から**進む（焼けより先に歪み始めてから火が回る）。
                        // 頭で急にねじれると「UI がアニメーションした」に見えるので溜めるが、
                        // 溜めすぎると**墨が残っているうちに渦が読めない**（二乗だと遅すぎた）。
                        float s = Clamp01(_elapsed / (DissolveDelaySec + DissolveSec));
                        return new TitleWeights
                        {
                            veil = 1f - SmoothStep01(open),
                            // 字は黒より先に消え切る。最後に残るのが黒だと、開いた瞬間に
                            // 現実だけが立ち上がって継ぎ目が 1 回で済む。
                            // ⚠ 落とすのは**灰だけになってから**（AshFadeFrom）。
                            glyph = 1f - SmoothStep01(Clamp01((d - AshFadeFrom) / (1f - AshFadeFrom))),
                            reveal = _dismissReveal,
                            // ⚠ **ここに緩急を付けない。** SmoothStep だと変化の 6 割が真ん中の
                            //    4 割に集まり、火が一瞬で走り抜けて「焼けた」ではなく「消えた」になる
                            //    （2026-08-13 実測）。焼け際は一定の速さで進むのが正しい。
                            dissolve = d,
                            // ≒ s^1.6（s*s だと巻き込みの 8 割が最後の 3 割に寄る）。
                            swirl = s * s * (1.5f - 0.5f * s),
                            flashPos = f,
                            flashAmt = Bump(f),
                            // 押した手応えだけ。**奥へは退かない**（退くと「箱に吸い込まれた」に見える）。
                            pushM = PushM * Bump(f),
                        };
                    }

                    default:
                        return TitleWeights.Hidden;
                }
            }
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        private static float SmoothStep01(float t)
        {
            t = Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        /// <summary>
        /// 0 → 1 → 0 の山。押した瞬間の光と、迫ってから戻る動きに使う。
        ///
        /// ⚠ <see cref="IntroLogic"/> にも同じ関数があるが<b>意図的に持たない</b>。
        /// タイトルは導入とは別の層で、導入の演出を変えるたびにタイトルの動きが変わってよい理由が無い。
        /// </summary>
        private static float Bump(float t)
        {
            t = Clamp01(t);
            return 1f - (2f * t - 1f) * (2f * t - 1f);
        }
    }
}
