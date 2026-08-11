#nullable enable

namespace FixedCamVr.Streaming
{
    /// <summary>タイトル画面の段。</summary>
    public enum TitleStage
    {
        /// <summary>出していない（本編中・終了後・そもそも切ってある）。</summary>
        Off,
        /// <summary>字が現れている途中。</summary>
        In,
        /// <summary>出し切って、A を待っている。</summary>
        Hold,
        /// <summary>A が押された。光が走り、字が退き、黒が開く。</summary>
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
        /// <summary>溶ける量（0 = そのまま / 1 = 消え切り）。</summary>
        public float dissolve;
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
        /// <summary>出すまでの間。起動時の黒（StartupFader）が明けるのを待って字を出す。</summary>
        public const float InDelaySec = 0.5f;

        /// <summary>字が現れ切るまで。急ぐと「UI が出た」に見える。</summary>
        public const float InSec = 1.7f;

        /// <summary>A の手応え。光が字を走り抜ける。<b>押した瞬間に始める</b>（遅れると効かない）。</summary>
        public const float FlashSec = 0.42f;

        /// <summary>光が走ってから字が溶け始めるまで。</summary>
        public const float DissolveDelaySec = 0.20f;

        /// <summary>字が溶け切るまで。</summary>
        public const float DissolveSec = 1.15f;

        /// <summary>黒が開き切るまで（クロスフェード）。</summary>
        public const float OpenSec = 1.25f;

        /// <summary>
        /// 隠すものが立つのを待つ上限。**超えたら諦めて開く**。
        /// 段 0 では封印の箱も隔離の黒も毎フレーム描かれているので、通常この待ちは 0 フレームで抜ける。
        /// </summary>
        public const float ConcealWaitMaxSec = 0.5f;

        /// <summary>A を押した瞬間に字が迫る量 (m)。</summary>
        public const float PushM = 0.06f;

        /// <summary>溶けながら奥へ退く量 (m)。</summary>
        public const float RecedeM = 0.55f;

        private TitleStage _stage = TitleStage.Off;
        private float _elapsed;      // 段に入ってからの秒
        private float _openElapsed;  // 黒が開き始めてからの秒
        private bool _opening;
        private float _concealWait;

        public TitleStage Stage => _stage;

        /// <summary>いまタイトルが画面を持っているか（＝導入の開始合図を止めてよいか）。</summary>
        public bool Active => _stage == TitleStage.In || _stage == TitleStage.Hold || _stage == TitleStage.Out;

        /// <summary>A を待っているか。実行体はここで触覚・ガイドを出す。</summary>
        public bool AwaitingInput => _stage == TitleStage.In || _stage == TitleStage.Hold;

        /// <summary>頭から出す（ランリセット・起動）。</summary>
        public void Begin()
        {
            _stage = TitleStage.In;
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

        /// <summary>A。閉じる演出へ入る。<b>出現の途中でも受ける</b>（待たされる方が不快）。</summary>
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

            if (input.dismissRequested) RequestDismiss();

            _elapsed += dt;

            switch (_stage)
            {
                case TitleStage.In:
                    if (_elapsed >= InDelaySec + InSec) { _stage = TitleStage.Hold; _elapsed = 0f; }
                    break;

                case TitleStage.Hold:
                    // ここは時間で進まない。A か ForceClose だけが出口。
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
                        // 迫ってから退く。押した手応えを先に返し、そのあと奥へ抜けていく。
                        float push = PushM * Bump(f) - RecedeM * (d * d);
                        return new TitleWeights
                        {
                            veil = 1f - SmoothStep01(open),
                            // 字は黒より先に消え切る。最後に残るのが黒だと、開いた瞬間に
                            // 現実だけが立ち上がって継ぎ目が 1 回で済む。
                            glyph = 1f - SmoothStep01(d),
                            reveal = _dismissReveal,
                            dissolve = SmoothStep01(d),
                            flashPos = f,
                            flashAmt = Bump(f),
                            pushM = push,
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
