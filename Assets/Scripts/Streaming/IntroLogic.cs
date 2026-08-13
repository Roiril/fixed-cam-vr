#nullable enable

using System;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 導入演出の段。<b><see cref="ShowPhase"/> は増やさない</b> — これは
    /// <see cref="ShowPhase.Intro"/> の**内側**のサブ状態で、ゲート・終了判定・heartbeat・卓・
    /// シミュレータへの分岐を増やさないための設計。
    ///
    /// ⚠ <b>2026-08-13 に段を作り直した。</b> 体験者は<b>封印の箱の中に入ってから</b>固定視点になる
    /// 運用へ変わったので、「箱の外で現実を格下げしていく」旧 5 段（Real / Degrade / Structure /
    /// Frame / Swap）は成立しない（中に入ると <c>shell=1</c> に倒れて 8.6 秒の真っ黒にしかならない）。
    /// いまは<b>閉じる → 闇 → 管が点く → 自分が映る</b>の 4 段 6.2 秒。
    /// </summary>
    public enum IntroStage
    {
        /// <summary>まだ始まっていない（導入演出を出さない設定・本編中・終了後）。</summary>
        Off,
        /// <summary>段 0。箱の外・素通し・封印の箱。エリアへ近づいたら次へ。</summary>
        Black,
        /// <summary>段 1。開口が閉じ切る（現実が閉じ、封印の箱も引く）。</summary>
        Seal,
        /// <summary>
        /// 段 2。全黒。<b>体験者が箱の中に入るのを待つ</b>（上限
        /// <see cref="IntroLogic.DarkHoldMaxSec"/> 秒で必ず抜ける）。
        /// </summary>
        Dark,
        /// <summary>段 3。闇の中でスクリーンの管が点く。<b>映像はまだ無い。</b></summary>
        Ignite,
        /// <summary>
        /// 段 4。管の中がカメラ映像へ。<b>枠の中に自分が居る。</b>
        /// 映像が来ていなければ<b>砂嵐</b>が出る（段を飛ばさない・<c>canon/LEDGER.md</c> 0025）。
        /// </summary>
        Live,
        /// <summary>演出は終わり。ここから本編（尺は <see cref="ShowRunLogic"/> が数える）。</summary>
        Done,
    }

    /// <summary>導入演出の合図。</summary>
    public enum IntroEvent
    {
        None,
        /// <summary>演出が終わった。呼び出し側は慣らし歩行の計時を始める。</summary>
        Finished,
        /// <summary>中止した（トラッキング原点が変わった等）。呼び出し側は黒にしてスタッフを呼ぶ。</summary>
        Aborted,
    }

    /// <summary>段ごとの尺（秒）。show.json の <c>run.intro</c> がそのまま入る。</summary>
    [Serializable]
    public struct IntroTiming
    {
        public float sealSec;
        public float darkSec;
        public float igniteSec;
        public float liveSec;
        /// <summary>これを超えたら段を飛ばして映像を出す（保険）。</summary>
        public float maxSec;

        /// <summary>
        /// コード既定。**合計 6.2 秒**（段 2 の待ちは尺に含めない — 中に入るまでの時間は演出ではない）。
        ///
        /// ⚠ この値は 4 箇所に現れる。**全部一致していること**:
        ///   ここ / <c>ShowIntroDef</c> / 卓の <c>intro-model.js</c> の <c>INTRO_DEFAULT</c> /
        ///   <c>capture-server.py</c> の <c>_default_show</c>。片方だけ直すと沈黙して食い違う。
        /// </summary>
        public static IntroTiming Default => new IntroTiming
        {
            sealSec = 1.4f, darkSec = 0.8f, igniteSec = 1.6f, liveSec = 2.4f, maxSec = 20f,
        };

        /// <summary>不正値を潰した複製。0 や負値はコード既定へ戻す（黙って 0 秒の段を作らない）。</summary>
        public IntroTiming Sanitized()
        {
            var d = Default;
            return new IntroTiming
            {
                sealSec = sealSec > 0f ? sealSec : d.sealSec,
                darkSec = darkSec > 0f ? darkSec : d.darkSec,
                igniteSec = igniteSec > 0f ? igniteSec : d.igniteSec,
                liveSec = liveSec > 0f ? liveSec : d.liveSec,
                maxSec = maxSec > 0f ? maxSec : d.maxSec,
            };
        }

        /// <summary>
        /// 演出が実際に流れる秒数（段 0 の待ちと段 2 の「中に入るのを待つ」時間は含まない）。
        ///
        /// 卓の <c>intro-model.js</c> の <c>introStageSec</c> と<b>同じ式</b>にしてある —
        /// 片方だけ直すと卓の表示と実機の尺が沈黙して食い違い、作者は尺を信じられなくなる。
        /// </summary>
        public float TotalSec
        {
            get
            {
                var s = Sanitized();
                return s.sealSec + s.darkSec + s.igniteSec + s.liveSec;
            }
        }
    }

    /// <summary>
    /// 各層へ配る重み（すべて 0..1）。<b>Director はこれを配るだけ</b>で、見え方の判断はここに集約する。
    /// struct なので毎フレーム作っても GC を踏まない。
    /// </summary>
    public struct IntroWeights
    {
        /// <summary>パススルーの不透明度。1 = 現実が見える / 0 = 見えない。</summary>
        public float passthrough;
        /// <summary>
        /// 色を抜く量（1 で完全なグレースケール）とコントラストの上げ量。
        /// ⚠ <b>いまの導入は 1 度も動かさない</b>（段としては廃止）。終幕（<see cref="OutroLogic"/>）が使う。
        /// </summary>
        public float degrade;
        /// <summary>実物の輪郭線の強さ。⚠ 導入では 0 のまま（終幕が使う）。</summary>
        public float edge;
        /// <summary>壁・床の線とカメラの印の強さ。⚠ 導入では 0 のまま。</summary>
        public float structure;
        /// <summary>枠の閉じ具合。0 = 全画面 / 1 = スクリーンの開口だけ。</summary>
        public float frame;

        /// <summary>
        /// 破砕の進み（0 = 割れていない / 1 = 入り切った）。
        ///
        /// ⚠ <b>いまの導入は 1 度も動かさない。</b> 段 4「現実が割れてスクリーンへ入る」は
        /// 「箱の外で見る」前提の演出で、中に入ってから固定視点になる運用では成立しない。
        /// <see cref="IntroShatterCurve"/> と 2 つの破片メッシュは<b>眠らせてあるだけ</b>で消していない。
        /// </summary>
        public float shatter;
        /// <summary>カメラ映像の不透明度（枠の中身）。</summary>
        public float live;
        /// <summary>粒状感・走査線の強さ（アプリ側の面で出す）。</summary>
        public float grain;
        /// <summary>乱れ（グリッチ）の強さ。すり替えの継ぎ目を隠す。</summary>
        public float glitch;

        /// <summary>
        /// <b>スクリーンの管が点いている量</b>（0 = 消えている / 1 = 点いている）。
        /// <c>ScreenComposite</c> の <c>_CrtIgnite</c> へそのまま渡る。
        ///
        /// ⚠⚠ <b>既定は 1。</b> 0 を書いたままにすると画がまるごと消えるので、
        /// 演出を出していない全期間（<see cref="Inactive"/>・終幕・中止・無効化）は必ず 1。
        /// 0 になるのは段 0〜2 の「まだ点いていない」区間だけ。
        /// </summary>
        public float ignite;

        /// <summary>
        /// 隔離殻の強さ。1 = 会場が黒に落ち、実物の壁と足元の床だけが残る（<see cref="ContainmentShell"/>）。
        ///
        /// ⚠ 殻は全画面の面（queue 4910）で<b>スクリーンごと黒く塗る</b>。だから段 3 で管が点くのと
        /// 入れ替わりに引く（残すと点いた管が見えない）。
        /// </summary>
        public float shell;

        /// <summary>
        /// 隔離殻が<b>実物の壁と床を見せるか</b>（1 = 見せる / 0 = 何も見せない ＝ 真っ黒）。
        ///
        /// 導入のあいだは 0。<b>中の様子は固定視点になるまで見せない</b>という約束
        /// （<c>canon/LEDGER.md</c> 0005）を、ここ 1 つで保証する。
        /// 終幕は 1 — 体験が終わって現実へ帰す段なので、壁と床が見えるのが正しい。
        /// </summary>
        public float shellReveal;

        /// <summary>
        /// 封印の箱の不透明度。<b>外から見た隔離</b>（<see cref="SealedBox"/>）。
        /// 段 0 で出しっぱなしにし、段 1 で閉じるのと一緒に引く。
        /// </summary>
        public float sealBox;

        /// <summary>導入演出を出していないときの値（本編と同じ見え）。</summary>
        public static IntroWeights Inactive => new IntroWeights
        {
            passthrough = 0f, degrade = 0f, edge = 0f, structure = 0f,
            frame = 1f, live = 1f, grain = 0f, glitch = 0f,
            ignite = 1f,
            shell = 0f, shellReveal = 0f, sealBox = 0f,
        };
    }

    /// <summary>段を進めるかを決めるための観測値。</summary>
    public struct IntroInput
    {
        /// <summary>起動時の黒が明けたか（<c>StartupFader</c> が消えた）。</summary>
        public bool blackCleared;

        /// <summary>
        /// 体験者が<b>開始の合図</b>（体験エリアへの接近 / 通過ライン / 開始位置の円）を満たしたか。
        /// これが立てばスタッフの合図を待たずに演出が始まる。判定できないときは常に false になり、
        /// 従来どおりスタッフ操作だけで進む（縮退）。
        /// </summary>
        public bool atStartSpot;
        /// <summary>頭の角速度 (度/秒)。大きいうちは管を点けない（見ていない方向で点く事故を防ぐ）。</summary>
        public float headTurnDegPerSec;
        /// <summary>スクリーンの方向が視野中心の近くにあるか。</summary>
        public bool frameCentered;
        /// <summary>
        /// カメラのフレームが新鮮に届いているか。
        ///
        /// ⚠ <b>これが false でも段 4 は飛ばさない</b>（2026-08-13 に変えた）。少し待つだけで、
        /// 待ちきれなければ砂嵐のまま段 4 へ進む。<b>装置が点いたのに何も映らないのは
        /// 「壊れている装置」として正しい画</b>で、演出ごと畳んで本編へ落とすより筋が通る。
        /// </summary>
        public bool liveFresh;
        /// <summary>トラッキング原点が変わった（OS の recenter）。中止する。</summary>
        public bool recentered;

        /// <summary>
        /// 体験エリア（隔離の footprint）の<b>外側までの距離 (m)</b>。中に居れば 0。
        /// 段 0 の封印の箱と、段 2 の「中に入ったか」がこれで決まる。
        /// 判定できない（未登録・layout 未着）ときは 0 ＝ 中に居る側へ倒す。
        /// </summary>
        public float outsideBoxM;
    }

    /// <summary>
    /// 導入演出の状態機械（UnityEngine 非依存・dt 注入）。
    ///
    /// 要点:
    ///   - <b>視点は 1 度も動かさない</b>。動かすのは現実の側の身分（現実 → 闇 → 映像）
    ///   - 枠は本編のスクリーンそのもの。開口と不透明度だけを動かすので「枠を運ぶ」処理が無い
    ///   - <b>手を上げたことは検出しない</b>。上げなければ伏線が張られないだけで、体験は壊れない
    /// </summary>
    public sealed class IntroLogic
    {
        /// <summary>段 3（管が点く）を始めてよい頭の角速度の上限 (度/秒)。これより速く振っていたら待つ。</summary>
        public const float MaxHeadTurnForIgnite = 45f;

        /// <summary>段 4 を始める前に、スクリーンが視野中心の近くにあり続ける必要のある秒数。</summary>
        public const float FrameCenteredHoldSec = 0.5f;

        /// <summary>
        /// 段 4 のクロスフェードの秒数。ここは急がない（遅延と視差が同時に来る唯一の点）。
        /// 段 4 が 2.4 秒なので、フェード後に「自分だ」と気づく時間が 1 秒以上残る。
        /// </summary>
        public const float LiveCrossfadeSec = 1.2f;

        /// <summary>
        /// 「近づいた」とみなす距離 (m)。体験エリアの境界からこれ以下まで来たら導入が始まる。
        /// </summary>
        public const float ApproachNearM = 1.0f;

        /// <summary>近づいたまま留まる秒数。通りすがりで始めない。</summary>
        public const float ApproachHoldSec = 0.4f;

        /// <summary>
        /// 段 4 の開始条件（映像が届いている・スクリーンを見ている）を待てる上限 (秒)。
        /// 超えたら<b>条件を無視して進む</b> — 映像が来ていなければ砂嵐のまま段 4 が流れる。
        /// </summary>
        public const float MaxHoldSec = 3f;

        /// <summary>
        /// 段 2 が「中に入った」を待てる上限 (秒)。<b>超えたら必ず抜ける</b> —
        /// 位置が解けない現場・端末を机に置いた自動走行でも体験は先へ進まなければならない。
        /// </summary>
        public const float DarkHoldMaxSec = 3f;

        /// <summary>
        /// 「中に居る」と<b>みなし始める</b>外側距離 (m)。境界そのもの（0）ではなく手前に取る。
        ///
        /// ⚠⚠ <b>黒は箱の面より先に立てる。</b> 判定は Update 時の CenterEyeAnchor だが、描画は
        /// 眼ごとの late-latch 姿勢で行われ、箱は <c>Cull Back</c> なので
        /// <b>描画側が先に中へ入った眼だけ壁が消えて、黒はまだ来ない</b> ＝ 一瞬パススルーで
        /// 中が覗ける（2026-08-13 ユーザー報告 ③）。余裕をここで作る。
        ///
        /// ⚠ <b>0.3m では足りなかった</b>（同日・2 度目の報告 <c>canon/LEDGER.md</c> 0028）。
        /// 0.55m へ広げてある。箱の面から 0.55m の所では箱は既に視界をほぼ埋めているので、
        /// 「箱の面」と「黒」の見分けはつかない ＝ 早めに倒しても画は変わらない。</summary>
        public const float InsideEnterM = 0.55f;

        /// <summary>
        /// 「外へ出た」と<b>みなし直す</b>外側距離 (m)。<see cref="InsideEnterM"/> より広く取って
        /// ヒステリシスにする（境界で震えると黒と箱が交互に点滅する）。
        /// </summary>
        public const float InsideExitM = 0.85f;

        private IntroTiming _t = IntroTiming.Default;
        private IntroStage _stage = IntroStage.Off;
        private float _stageElapsed;
        private float _totalElapsed;
        private float _centeredSec;
        private float _holdSec;
        private bool _advanceRequested;
        private bool _skipRequested;
        private bool _insideBox;
        private bool _sawOutsideBox;

        public IntroStage Stage => _stage;
        public float StageElapsedSec => _stageElapsed;
        public float TotalElapsedSec => _totalElapsed;

        /// <summary>いま体験エリアの中に居るとみなしているか（ヒステリシス付き・診断とテスト用）。</summary>
        public bool InsideBox => _insideBox;

        /// <summary>
        /// <see cref="Begin"/> 以降に一度でも体験エリアの外に居たか。
        /// <b>「歩いて入ってきた」を状態でなく事象として見るためのラッチ</b>（診断とテスト用）。
        /// </summary>
        public bool SawOutsideBox => _sawOutsideBox;

        /// <summary>演出中か（＝ Director が各層へ重みを配るべきか）。</summary>
        public bool Active => _stage != IntroStage.Off && _stage != IntroStage.Done;

        /// <summary>いま条件待ちで足踏みしているか（卓に出して、スタッフが手で進められるようにする）。</summary>
        public bool Holding => _holdSec > 0f;

        public void Configure(IntroTiming timing) => _t = timing.Sanitized();

        /// <summary>導入演出を頭から始める。</summary>
        public void Begin()
        {
            _stage = IntroStage.Black;
            _stageElapsed = 0f;
            _totalElapsed = 0f;
            _centeredSec = 0f;
            _holdSec = 0f;
            _advanceRequested = false;
            _skipRequested = false;
            _insideBox = false;
            _sawOutsideBox = false;
        }

        /// <summary>演出を出さない設定 / 本編中 / 終了後。重みは <see cref="IntroWeights.Inactive"/> になる。</summary>
        public void Disable()
        {
            _stage = IntroStage.Off;
            _stageElapsed = 0f;
            _totalElapsed = 0f;
            _holdSec = 0f;
        }

        /// <summary>
        /// HMD を被り直された等でやり直す。<b>段 1 から</b>（黒はもう明けているので段 0 へは戻らない）。
        /// </summary>
        public void Restart()
        {
            _stage = IntroStage.Seal;
            _stageElapsed = 0f;
            _totalElapsed = 0f;
            _centeredSec = 0f;
            _holdSec = 0f;
            _advanceRequested = false;
            _skipRequested = false;
        }

        /// <summary>いまの段を今すぐ終える（スタッフ操作）。段 0 ではこれが「始める」の合図。</summary>
        public void RequestAdvance() => _advanceRequested = true;

        /// <summary>演出を全部飛ばして本編の見えにする（スタッフ操作）。</summary>
        public void RequestSkip() => _skipRequested = true;

        /// <summary>時間を進める。</summary>
        public IntroEvent Tick(float dt, IntroInput input)
        {
            if (dt < 0f) dt = 0f;
            // 中に居るかは Weights と段 2 の待ちから読まれるので、観測値をここで覚えておく
            // （Weights に引数を足すと、呼び出し側が「いつの値か」を持つことになる）。
            //
            // ⚠ **ヒステリシス。** 単なる `outsideBoxM <= 0` だと、境界に立った体験者の
            //    震えで黒と箱が交互に点滅し、しかも黒が箱の面より遅れて立つ（③ の事故）。
            UpdateInsideBox(input.outsideBoxM);
            if (_stage == IntroStage.Off || _stage == IntroStage.Done) return IntroEvent.None;

            // トラッキング原点が変わったら続行しない。壁の位置が違う世界を見せることになり、
            // 体験者は壁を手でたどるので危ない。
            if (input.recentered)
            {
                _stage = IntroStage.Done;
                return IntroEvent.Aborted;
            }

            if (_skipRequested)
            {
                _skipRequested = false;
                _stage = IntroStage.Done;
                return IntroEvent.Finished;
            }

            _stageElapsed += dt;
            _totalElapsed += dt;

            // 保険。条件待ちで固まっても、体験は必ず本編へ入る。
            if (_totalElapsed >= _t.maxSec && _stage != IntroStage.Live)
            {
                _stage = IntroStage.Done;
                return IntroEvent.Finished;
            }

            bool advance = _advanceRequested;
            _advanceRequested = false;

            switch (_stage)
            {
                case IntroStage.Black:
                    // ここは**時間で進めない**。始めてよいかは体験者の居場所か人間の判断で決まる。
                    //   - 体験者が体験エリアへ近づいた、または
                    //   - スタッフが合図した、または
                    //   - **外から歩いて入ってしまった**（下の安全網）
                    // どれも「黒が明けている」ことが前提（明ける前に始めても何も見えない）。
                    //
                    // ⚠⚠ **安全網**（2026-08-13）。段 0 で体験エリアの中に入ると、重みは
                    //    `shell = 1 / sealBox = 0` ＝ **真っ黒**に倒れる（中の様子を見せない約束）。
                    //    そこで近づく合図が成立していないと、体験者は**何も起きない黒の中に立ったまま**
                    //    になり、スタッフの ⏭ 以外に出口が無い。開始位置が箱に近い現場ほど踏む。
                    //    ⚠ **状態ではなく事象で判定する**。「いま中に居る」で始めると、前の体験者が
                    //    中に立ったままのリセット・エリア内に置いた HMD で勝手に走り出す（0005 が禁じた形）。
                    //    外に居たことを見てから入ってきた場合だけ通す。
                    if (input.blackCleared
                        && (advance || input.atStartSpot || (_sawOutsideBox && _insideBox)))
                        Enter(IntroStage.Seal);
                    // 段 0 は maxSec の計時に含めない（待っている時間は演出の尺ではない）。
                    _totalElapsed = 0f;
                    return IntroEvent.None;

                case IntroStage.Seal:
                    if (advance || _stageElapsed >= _t.sealSec) Enter(IntroStage.Dark);
                    return IntroEvent.None;

                case IntroStage.Dark:
                {
                    if (!advance && _stageElapsed < _t.darkSec) return IntroEvent.None;
                    // **中に入るのを待つ。** ここが新しい運用の要で、体験者は封印の箱の中へ
                    // 歩いて入ってから固定視点になる。あわせて、頭を振っている間は管を点けない
                    // （見ていない方向でスクリーンが点くと出現そのものを見逃す）。
                    bool ready = _insideBox && input.headTurnDegPerSec <= MaxHeadTurnForIgnite;
                    if (!advance && !ready && _holdSec < DarkHoldMaxSec)
                    {
                        _holdSec += dt;
                        return IntroEvent.None;
                    }
                    Enter(IntroStage.Ignite);
                    return IntroEvent.None;
                }

                case IntroStage.Ignite:
                {
                    if (!advance && _stageElapsed < _t.igniteSec) return IntroEvent.None;
                    // スクリーンを見ていて、かつ映像が届いていること。少しは待つ（遅れて来ることがある）。
                    _centeredSec = input.frameCentered ? _centeredSec + dt : 0f;
                    bool ready = _centeredSec >= FrameCenteredHoldSec && input.liveFresh;
                    if (!advance && !ready && _holdSec < MaxHoldSec)
                    {
                        _holdSec += dt;
                        return IntroEvent.None;
                    }
                    // ⚠⚠ **映像が来なくても段 4 へ進む**（2026-08-13・canon/LEDGER.md 0025）。
                    //    旧実装はここで演出ごと畳んで本編へ落としていた ＝ カメラが 1 台も繋がって
                    //    いない現場では**管が点いた次の瞬間に導入が終わる**。体験者から見れば
                    //    「装置が点いたのに何も起きずに始まった」で、装置の側の理由が画に無い。
                    //    いまは進んで**砂嵐が出る**（`SignalLostFx` が既に未受信カメラを覆っている）。
                    //    装置は点いた、映すものが無い、という筋がそのまま画になる。
                    Enter(IntroStage.Live);
                    return IntroEvent.None;
                }

                case IntroStage.Live:
                    if (advance || _stageElapsed >= _t.liveSec)
                    {
                        _stage = IntroStage.Done;
                        return IntroEvent.Finished;
                    }
                    return IntroEvent.None;

                default:
                    return IntroEvent.None;
            }
        }

        /// <summary>
        /// 「中に居る」のヒステリシス。<b>入るのは早く（<see cref="InsideEnterM"/>）、出るのは遅く
        /// （<see cref="InsideExitM"/>）</b>。黒を箱の手前で先行させるのがここ 1 箇所の役目。
        /// </summary>
        private void UpdateInsideBox(float outsideM)
        {
            // 「外に居た」の観測は**出るときと同じ閾値**で取る（入る側で取ると境界の震えで立つ）。
            if (outsideM >= InsideExitM) _sawOutsideBox = true;

            if (_insideBox)
            {
                if (outsideM >= InsideExitM) _insideBox = false;
            }
            else if (outsideM <= InsideEnterM)
            {
                _insideBox = true;
            }
        }

        private void Enter(IntroStage next)
        {
            _stage = next;
            _stageElapsed = 0f;
            _holdSec = 0f;
            _centeredSec = 0f;
        }

        /// <summary>
        /// いまの段から各層への重みを出す。<b>見え方の判断はすべてここ</b>（Director は配るだけ）。
        ///
        /// ⚠ <c>degrade</c> / <c>edge</c> / <c>structure</c> / <c>shatter</c> / <c>grain</c> は
        /// 全段で 0。<b>語彙からは消していない</b>（終幕 <see cref="OutroLogic"/> が使う）。
        /// </summary>
        public IntroWeights Weights
        {
            get
            {
                switch (_stage)
                {
                    case IntroStage.Off:
                    case IntroStage.Done:
                        return IntroWeights.Inactive;

                    case IntroStage.Black:
                        // ⚠ **段の名前に反して、ここは黒くない。現実が見えている。**
                        // 開始の合図（体験エリアへの接近）を待つ区間で、体験者はスタッフに
                        // 誘導されて歩いてくる。真っ暗にすると運用が成立しない。
                        //
                        // 外から見た隔離＝**封印の箱**がここで出る（canon/LEDGER.md 0003）。
                        // **開かない。** 開ける代わりに、閉じてから中に入ってもらう。
                        return OutsideWeights(new IntroWeights
                        {
                            passthrough = 1f, frame = 0f, live = 0f, ignite = 0f,
                        });

                    case IntroStage.Seal:
                    {
                        // 開口が閉じ切る。閉じるのは**開口と現実の側**だけ。
                        float p = SmoothStep(0f, 1f, Progress(_t.sealSec));
                        var w = new IntroWeights
                        {
                            passthrough = 1f - p,
                            frame = p,
                            live = 0f,
                            ignite = 0f,
                        };
                        // 中に入っていれば黒（殻）が正。
                        if (_insideBox)
                        {
                            w.shell = 1f;
                            w.shellReveal = 0f;
                            w.sealBox = 0f;
                        }
                        else
                        {
                            w.shell = 0f;
                            w.shellReveal = 0f;
                            // ⚠⚠ **箱は薄くしない**（2026-08-13・canon/LEDGER.md 0028）。
                            //    旧実装は開口と同じ進みで `1 - p` へ引いていたので、段の半ばで
                            //    **箱が半透明になり、その向こう＝体験エリアの中が透けた**
                            //    （LEDGER 0005「中の様子は固定視点になるまで見せない」に反する）。
                            //    箱が消えるのは黒（殻）が代わりに立ってから ＝ 段 2 か、中に入った瞬間。
                            //    LEDGER 0023 ④ の「段 1 の最後に残る矩形が箱の面」とも、こちらが合う。
                            w.sealBox = 1f;
                        }
                        return w;
                    }

                    case IntroStage.Dark:
                        // 全黒。ここで体験者は箱の中へ歩いて入る。
                        return new IntroWeights
                        {
                            passthrough = 0f, frame = 1f, live = 0f, ignite = 0f,
                            shell = 1f, shellReveal = 0f, sealBox = 0f,
                        };

                    case IntroStage.Ignite:
                    {
                        // 闇の中で管が点く。**映像はまだ無い。**
                        float p = SmoothStep(0f, 1f, Progress(_t.igniteSec));
                        return new IntroWeights
                        {
                            passthrough = 0f, frame = 1f, live = 0f,
                            ignite = p,
                            // ⚠ **殻は管と入れ替わりに引く。** 殻は全画面の面（queue 4910・ZTest Always）で
                            //    スクリーンごと黒く塗るので、1 のまま残すと点いた管が 1 画素も見えない。
                            //    闇そのものは覆い（開口の外は不透明）と背景 alpha が保つ。
                            shell = 1f - p,
                            shellReveal = 0f,
                            sealBox = 0f,
                        };
                    }

                    case IntroStage.Live:
                    {
                        float cross = _t.liveSec > 0f
                            ? Clamp01(_stageElapsed / Math.Max(LiveCrossfadeSec, 0.01f)) : 1f;
                        float s = SmoothStep(0f, 1f, cross);
                        return new IntroWeights
                        {
                            // ⚠ **ここでパススルーを 1 画素も出さない**（canon/LEDGER.md 0005）。
                            passthrough = 0f,
                            frame = 1f,
                            live = s,
                            ignite = 1f,
                            // 継ぎ目は乱れで隠す。
                            glitch = Bump(cross),
                            shell = 0f,
                            shellReveal = 0f,
                            sealBox = 0f,
                        };
                    }

                    default:
                        return IntroWeights.Inactive;
                }
            }
        }

        /// <summary>
        /// 段 0 の隔離の出し分け。<b>中の様子は 1 画素も見せない</b>（<c>canon/LEDGER.md</c> 0005）。
        ///
        /// - 外に居る: 中を隠すのは封印の箱の仕事。隔離殻は要らない
        /// - <b>中に入ってしまった: 黒しか見せない</b>（<c>shellReveal = 0</c>）
        /// </summary>
        private IntroWeights OutsideWeights(IntroWeights w)
        {
            if (_insideBox)
            {
                w.shell = 1f;
                w.shellReveal = 0f;
                w.sealBox = 0f;      // 中からは背面カリングで見えない。値でも落としておく
                return w;
            }
            w.shell = 0f;
            w.shellReveal = 0f;
            w.sealBox = 1f;
            return w;
        }

        private float Progress(float span) => span > 0f ? Clamp01(_stageElapsed / span) : 1f;

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        /// <summary>0..1 の滑らかな立ち上がり（Unity の Mathf を使わない＝ UnityEngine 非依存を保つ）。</summary>
        public static float SmoothStep(float from, float to, float v)
        {
            if (to - from <= 1e-6f) return v >= to ? 1f : 0f;
            float t = Clamp01((v - from) / (to - from));
            return t * t * (3f - 2f * t);
        }

        /// <summary>0 → 1 → 0 の山（乱れを継ぎ目だけに乗せる）。</summary>
        public static float Bump(float t)
        {
            t = Clamp01(t);
            return 1f - (2f * t - 1f) * (2f * t - 1f);
        }
    }
}
