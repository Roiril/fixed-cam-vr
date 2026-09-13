#nullable enable

using System;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 導入演出の段。<b><see cref="ShowPhase"/> は増やさない</b> — これは
    /// <see cref="ShowPhase.Intro"/> の**内側**のサブ状態で、ゲート・終了判定・heartbeat・卓・
    /// シミュレータへの分岐を増やさないための設計。
    ///
        /// 段名と進行骨格は既存のまま保ち、見え方は 2026-09-13 に連続光学開口へ更新した。
        /// 現実の彩度が落ち、静止した後、全視野を囲む 4 辺が本編スクリーンへ閉じる。
    ///
    /// 段は <b>Black → Real → Degrade → Structure → Frame → Swap</b> の 5 段 13.1 秒
    /// （段 3 は段 2 と重なるので単純和ではない）。
    /// </summary>
    public enum IntroStage
    {
        /// <summary>まだ始まっていない（導入演出を出さない設定・本編中・終了後）。</summary>
        Off,
        /// <summary>段 0。素通しのパススルー。体験エリアへ近づいたら次へ。</summary>
        Black,
        /// <summary>段 1。素のパススルー。何も演出しない（段 2 の変化を読ませるための比較対象）。</summary>
        Real,
        /// <summary>段 2。現実の彩度だけが滑らかに落ちる。</summary>
        Degrade,
        /// <summary>段 3。彩度が落ち切った現実を静止して見せる。</summary>
        Structure,
        /// <summary>
        /// 段 4。全視野の現実を囲む 4 辺が、本編スクリーンの開口へ滑らかに閉じる。
        /// 後半はスクリーンの内側だけをカメラ映像へクロスフェードする。
        /// </summary>
        Frame,
        /// <summary>
        /// 段 5。枠の中がカメラ映像へ。<b>枠の中に自分が居る。</b>
        /// 映像が来ていなければ<b>砂嵐</b>が出る（段を飛ばさない・<c>canon/LEDGER.md</c> 0025）。
        /// </summary>
        Swap,
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
        public float realSec;
        public float degradeSec;
        public float structureSec;
        public float frameSec;
        public float swapSec;
        /// <summary>これを超えたら段を飛ばして枠を出す（保険）。</summary>
        public float maxSec;

        /// <summary>
        /// コード既定。**合計 13.1 秒**（段 3 は段 2 と重なるので単純和ではない）。
        ///
        /// ⚠ この値は 4 箇所に現れる。**全部一致していること**:
        ///   ここ / <c>ShowIntroDef</c> / 卓の <c>intro-model.js</c> の <c>INTRO_DEFAULT</c> /
        ///   <c>capture-server.py</c> の <c>_default_show</c>。片方だけ直すと沈黙して食い違う。
        /// </summary>
        public static IntroTiming Default => new IntroTiming
        {
            realSec = 1.5f, degradeSec = 3.5f, structureSec = 2.5f,
            frameSec = 2.5f, swapSec = 1.6f, maxSec = 20f,
        };

        /// <summary>不正値を潰した複製。0 や負値はコード既定へ戻す（黙って 0 秒の段を作らない）。</summary>
        public IntroTiming Sanitized()
        {
            var d = Default;
            return new IntroTiming
            {
                realSec = realSec > 0f ? realSec : d.realSec,
                degradeSec = degradeSec > 0f ? degradeSec : d.degradeSec,
                structureSec = structureSec > 0f ? structureSec : d.structureSec,
                frameSec = frameSec > 0f ? frameSec : d.frameSec,
                swapSec = swapSec > 0f ? swapSec : d.swapSec,
                maxSec = maxSec > 0f ? maxSec : d.maxSec,
            };
        }

        /// <summary>
        /// 演出が実際に流れる秒数（段 0 の待ちは含まない）。
        ///
        /// ⚠ <b>単純和ではない。</b> 段 3 は段 2 の後半から始まるので、重なった分は二度流れない。
        /// 卓の <c>intro-model.js</c> の <c>introStageSec</c> と<b>同じ式</b>にしてある —
        /// 片方だけ直すと卓の表示と実機の尺が沈黙して食い違い、作者は尺を信じられなくなる。
        /// </summary>
        public float TotalSec
        {
            get
            {
                var s = Sanitized();
                float own = s.structureSec - s.degradeSec * (1f - IntroLogic.StructureOverlapAt);
                if (own < IntroLogic.StructureMinOwnSec) own = IntroLogic.StructureMinOwnSec;
                return s.realSec + s.degradeSec + own + s.frameSec + s.swapSec;
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
        /// <summary>色を抜く量（1 で完全なグレースケール）とコントラストの上げ量。</summary>
        public float degrade;
        /// <summary>実物の輪郭線の強さ。</summary>
        public float edge;
        /// <summary>壁・床の線とカメラの印の強さ（<c>run.intro.showRoomWire</c> が既定 OFF）。</summary>
        public float structure;
        /// <summary>枠の閉じ具合。0 = 全画面 / 1 = スクリーンの開口だけ。</summary>
        public float frame;

        /// <summary>
        /// 段 4 の進行度。映像の観測値には使わない。
        /// <see cref="SoundCueLogic"/> と <see cref="SoundBedLogic"/> が既存の割れる音と劇伴の時刻を
        /// 保つために読む互換入力なので、段 4 の <c>p</c> をそのまま残す。
        /// </summary>
        public float shatter;
        /// <summary>
        /// スクリーン矩形のカメラ映像との混合量。<see cref="IntroVeil"/> の <c>_ScreenFade</c> へ渡る。
        /// <c>_IntroLive</c> へは 0/1 の表示ゲートとして変換して渡す。
        /// </summary>
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
        ///
        /// ⚠ <b>旧構成へ戻した 2026-08-15 以降、導入の全段でも 1。</b> 管は最初から点いていて、
        /// まだ何も映していない（<c>live = 0</c>）。装置は現場に据えてあるものなので、
        /// 体験者が来る前から電源が入っている方が筋が通る。
        /// 消えた管（<c>ignite = 0</c>）だと、素通しのパススルーの中に<b>黒い矩形</b>が浮くだけになる。
        /// </summary>
        public float ignite;

        /// <summary>
        /// 隔離殻の強さ。1 = 会場が黒に落ち、実物の壁と足元の床だけが残る（<see cref="ContainmentShell"/>）。
        ///
        /// ⚠ <b>いまの導入で動くのは段 5 だけ</b>（黒 → 映像の渡し）。パススルーが 0 になった後の
        /// 背景として使う。<b>パススルーが見えている分より大きくしない</b> — 殻は
        /// 「見えている現実のうち見せてはいけない所」を潰す層なので、映像へ移り切った後に残すと
        /// 画面の映像まで黒く塗る。
        /// </summary>
        public float shell;

        /// <summary>
        /// 隔離殻が<b>実物の壁と床を見せるか</b>（1 = 見せる / 0 = 何も見せない ＝ 真っ黒）。
        /// 導入のあいだは 0（段 5 の黒）。終幕は 1 — 現実へ帰す段なので壁と床が見えるのが正しい。
        /// </summary>
        public float shellReveal;

        /// <summary>
        /// 封印の箱の不透明度。<b>⚠⚠ 2026-08-15 以降、全段で 0（＝ 箱は出ない）。</b>
        ///
        /// 設定が「回収された壁の調査」へ変わり、体験エリアを隠す必要がなくなった
        /// （<c>canon/LEDGER.md</c> 0044）。実装（<see cref="SealedBox"/> ほか）は
        /// <c>Assets/Scripts/Streaming/Attic/</c> へ退避してあり、**消していない**。
        /// 場所と戻し方は <c>.claude/reference/attic-sealed-box.md</c>。
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
        /// <b>人が「始めてよい」と言ったか。</b> 実体はタイトルが画面を手放したこと
        /// （＝ スタッフが A を押して題字が焼け切ったこと）で、<b>タイトルを出さない・組めない現場では常に true</b>。
        ///
        /// ⚠⚠ <b>段 0 の自動出口はすべてこれでゲートする</b>（2026-08-14）。これが無いと、
        /// タイトルが立って A を待っているあいだも接近と安全網が生きているので、
        /// <b>スタッフが HMD を持って体験エリアを横切るだけで導入が始まり、題字が飛ぶ</b>
        /// （<see cref="TitleScreen"/> は段 0 を出た導入を見て自分を強制終了する）。
        ///
        /// ⚠ <b>スタッフの明示操作（⏭）はゲートしない。</b> あれは人の判断そのもので、
        /// 「まだ始めるな」を上書きする権利がある。
        /// </summary>
        public bool startAuthorized;

        /// <summary>
        /// <see cref="outsideBoxM"/> が信用できるか（位置合わせ済みで、体験エリアの形が解けている）。
        ///
        /// ⚠⚠ <b>0 は「本当に中に居る」と「解けない」の両方を意味していた。</b> 分けないと、
        /// 未登録の現場を「中に居る」と読んで自動で始めてしまう（2026-08-14・Codex 指摘）。
        /// 解けないあいだは中／外の判断そのものを更新しない。
        /// </summary>
        public bool outsideValid;

        /// <summary>
        /// 体験者が<b>開始の合図</b>（体験エリアへの接近 / 通過ライン / 開始位置の円）を満たしたか。
        /// これが立てばスタッフの合図を待たずに演出が始まる。判定できないときは常に false になり、
        /// 従来どおりスタッフ操作だけで進む（縮退）。
        /// </summary>
        public bool atStartSpot;

        /// <summary>
        /// <b>歩行誘導が体験者へ「円へ行け」と言い切っているか</b>（<c>canon/LEDGER.md</c> 0079）。
        ///
        /// ⚠⚠ true のあいだ、段 0 の自動の出口は <see cref="atStartSpot"/>（＝ 円へ着いた）
        /// <b>1 つだけ</b>になる。安全網（外 → 中）も救済（中に立ったまま 1 秒）も止まる。
        ///
        /// 理由: 床に円を描いて「ここへ来い」と言っておきながら、
        /// 体験エリアへ<b>近づいただけ</b>で演出が始まったら、装置が出した指示が嘘になる。
        ///
        /// ⚠⚠ <b>矢印が出る前から true になる</b>（<see cref="WalkGuideLogic.Reserving"/>・2026-09-06）。
        /// 誘導はエージェントの説明が届いてから出るので、題字が焼け切ってから実測 7.6 秒は
        /// まだ 1 画素も出ていない。そこを開けていたので、<b>体験エリアの中で始めた体験者は
        /// 下の救済（<see cref="ConcealStartSec"/>）で 1 秒後に導入へ落ち、円も矢印も一度も見なかった</b>。
        ///
        /// ⚠ <b>止めたぶんの出口は誘導の側が持つ</b>（<see cref="WalkGuideLogic.HoldMaxSec"/>）。
        /// 着かないまま上限を超えると誘導が畳まれてここが false へ戻り、従来の判定が生き返る。
        /// スタッフの ⏭（<see cref="RequestAdvance"/>）は元からゲートしていない。
        /// </summary>
        public bool guidingToSpot;

        /// <summary>頭の角速度 (度/秒)。大きいうちは枠を出さない（見ていない方向で枠が閉じる事故を防ぐ）。</summary>
        public float headTurnDegPerSec;
        /// <summary>スクリーンの方向が視野中心の近くにあるか。</summary>
        public bool frameCentered;
        /// <summary>
        /// カメラのフレームが新鮮に届いているか。
        ///
        /// ⚠ <b>これが false でも段 5 は飛ばさない</b>（2026-08-13・<c>canon/LEDGER.md</c> 0025）。
        /// 少し待つだけで、待ちきれなければ砂嵐のまま段 5 へ進む。<b>装置が枠になったのに
        /// 何も映らないのは「壊れている装置」として正しい画</b>で、演出ごと畳んで本編へ落とすより筋が通る。
        /// </summary>
        public bool liveFresh;
        /// <summary>トラッキング原点が変わった（OS の recenter）。中止する。</summary>
        public bool recentered;

        /// <summary>
        /// 体験エリア（隔離の footprint）の<b>外側までの距離 (m)</b>。中に居れば 0。
        /// 段 0 の安全網と救済がこれで決まる。
        /// 判定できないときは <see cref="outsideValid"/> が false になり、中／外の判断を更新しない。
        /// </summary>
        public float outsideBoxM;
    }

    /// <summary>
    /// 導入演出の状態機械（UnityEngine 非依存・dt 注入）。
    ///
    /// 要点:
    ///   - <b>視点は 1 度も動かさない</b>。動かすのは現実の側の身分（現実 → 映像）
    ///   - 枠は本編のスクリーンそのもの。開口と不透明度だけを動かすので「枠を運ぶ」処理が無い
        ///   - 段 2 と段 3 の尺の重なりは維持する。見た目は彩度の終端を静止して保つ
    ///   - <b>手を上げたことは検出しない</b>（<c>canon/LEDGER.md</c> 0034 で合図ごと廃止）
    /// </summary>
    public sealed class IntroLogic
    {
        /// <summary>段 4（枠が閉じる）を始めてよい頭の角速度の上限 (度/秒)。これより速く振っていたら待つ。</summary>
        public const float MaxHeadTurnForFrame = 45f;

        /// <summary>段 5 を始める前に、スクリーンが視野中心の近くにあり続ける必要のある秒数。</summary>
        public const float FrameCenteredHoldSec = 0.5f;

        /// <summary>
        /// 段 5 の鈴を鳴らす時刻。見た目は段 4 の後半で既にクロスフェードを終えている。
        /// 音の既存時刻を変えないために残す。
        /// </summary>
        public const float SwapCrossfadeSec = 1.2f;

        /// <summary>
        /// 段 2 と段 3 の尺を重ね始める進行度。
        /// **卓の `intro-model.js` の `STRUCTURE_OVERLAP_AT` と同じ値**（尺の表示が食い違うため）。
        /// </summary>
        public const float StructureOverlapAt = 0.6f;

        /// <summary>段 3 が段 2 に飲み込まれても、これだけは単独で流れる。</summary>
        public const float StructureMinOwnSec = 0.5f;

        /// <summary>
        /// 「近づいた」とみなす距離 (m)。体験エリアの境界からこれ以下まで来たら導入が始まる。
        /// </summary>
        public const float ApproachNearM = 1.0f;

        /// <summary>近づいたまま留まる秒数。通りすがりで始めない。</summary>
        public const float ApproachHoldSec = 0.4f;

        /// <summary>
        /// 段 4・段 5 の開始条件（映像が届いている・スクリーンを見ている）を待てる上限 (秒)。
        /// 超えたら<b>条件を無視して進む</b> — 映像が来ていなければ砂嵐のまま段 5 が流れる。
        /// </summary>
        public const float MaxHoldSec = 3f;

        /// <summary>
        /// 「体験エリアの中に居る」とみなし始める外側距離 (m)。段 0 の安全網と救済が読む。
        ///
        /// ⚠ <b>2026-08-15 に 0.55 → 0.0 へ戻した。</b> 0.55 は「黒を封印の箱の面より先に立てる」
        /// ための前倒しで、箱が消えた以上その理由が無い。いまは境界そのもの ＝
        /// 「実際に体験エリアへ入ったか」。
        /// </summary>
        public const float InsideEnterM = 0f;

        /// <summary>
        /// 「外へ出た」とみなし直す外側距離 (m)。<see cref="InsideEnterM"/> より広く取って
        /// ヒステリシスにする（境界で震えると安全網が点滅する）。
        /// </summary>
        public const float InsideExitM = 0.25f;

        /// <summary>
        /// <b>体験エリアの中に立ったまま、何も始まらない</b>のを救うまでの秒数。
        ///
        /// 接近（<see cref="ApproachLogic"/>）は「0.35m 縮む」余地を要求し、安全網は
        /// 「外に居たことがある」を要求するので、<b>最初から中に立たされた体験者はどちらも
        /// 成立せずスタッフの ⏭ 以外に出口が無い</b>（1.8m 四方の現場では普通に起きる）。
        ///
        /// ⚠ <b>無条件の時間切れにはしない。</b> 人が始めた（<see cref="IntroInput.startAuthorized"/>）
        /// かつ位置が信用できるときだけ効かせる — でないと、置いた HMD や前の体験者が
        /// 立ったままのリセットで勝手に走り出す（<c>canon/LEDGER.md</c> 0005 が禁じた形）。
        /// </summary>
        public const float ConcealStartSec = 1.0f;

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
        private float _concealHeldSec;

        public IntroStage Stage => _stage;
        public float StageElapsedSec => _stageElapsed;
        public float TotalElapsedSec => _totalElapsed;

        /// <summary>体験エリアの中に居るか（ヒステリシス付き・診断とテスト用）。</summary>
        public bool InsideBox => _insideBox;

        /// <summary>中に立ったまま何も始まらずに経った秒数（診断とテスト用）。</summary>
        public float ConcealHeldSec => _concealHeldSec;

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
            _concealHeldSec = 0f;
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
            _stage = IntroStage.Real;
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
            // 中に居るかは段 0 の安全網と救済から読まれるので、観測値をここで覚えておく。
            //
            // ⚠ **ヒステリシス。** 単なる `outsideBoxM <= 0` だと、境界に立った体験者の
            //    震えで安全網が点滅する。
            UpdateInsideBox(input.outsideBoxM, input.outsideValid);
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
            if (_totalElapsed >= _t.maxSec && _stage != IntroStage.Swap)
            {
                _stage = IntroStage.Done;
                return IntroEvent.Finished;
            }

            bool advance = _advanceRequested;
            _advanceRequested = false;

            switch (_stage)
            {
                case IntroStage.Black:
                {
                    // ここは**時間で進めない**。始めてよいかは体験者の居場所か人間の判断で決まる。
                    //
                    // ⚠⚠ **自動の出口はすべて「人が始めた」でゲートする**（2026-08-14）。
                    //    タイトルが立って A を待っているあいだも段 0 は生きているので、
                    //    **スタッフが HMD を持って体験エリアを横切るだけ**で下の安全網が成立し、
                    //    導入が始まって題字が飛んでいた。**スタッフの ⏭（advance）はゲートしない** —
                    //    あれは人の判断そのもので、「まだ始めるな」を上書きする権利がある。
                    //
                    // ⚠ **安全網は状態ではなく事象で判定する**。「いま中に居る」で始めると、
                    //    前の体験者が中に立ったままのリセット・エリア内に置いた HMD で勝手に走り出す
                    //    （0005 が禁じた形）。外に居たことを見てから入ってきた場合だけ通す。
                    //
                    // ⚠ **最初から中に立たされた体験者には出口が無い**（接近は「0.35m 縮む」余地が無く、
                    //    安全網は「外に居たことがある」を要求する）。人が始めたと言っていて、
                    //    位置が信用でき、それでも中に立ったまま `ConcealStartSec` 続いたら始める。
                    //
                    // ⚠⚠ **歩行誘導が出ているあいだは、出口が「円へ着いた」1 つだけになる**
                    //    （2026-08-17・`canon/LEDGER.md` 0079）。床に円を描いて「ここへ来い」と
                    //    言っておきながら、体験エリアへ近づいただけで演出が始まったら指示が嘘になる。
                    //    止めたぶんの出口は誘導の側が持つ（`WalkGuideLogic.HoldMaxSec` を超えると
                    //    誘導が畳まれて `guidingToSpot` が false へ戻り、下の 2 つが生き返る）。
                    bool concealStuck = input.startAuthorized && input.outsideValid
                                        && _insideBox && !input.guidingToSpot;
                    _concealHeldSec = concealStuck ? _concealHeldSec + dt : 0f;

                    bool fallback = !input.guidingToSpot
                                    && ((input.outsideValid && _sawOutsideBox && _insideBox)
                                        || _concealHeldSec >= ConcealStartSec);
                    bool auto = input.startAuthorized && (input.atStartSpot || fallback);
                    if (input.blackCleared && (advance || auto)) Enter(IntroStage.Real);
                    // 段 0 は maxSec の計時に含めない（待っている時間は演出の尺ではない）。
                    _totalElapsed = 0f;
                    return IntroEvent.None;
                }

                case IntroStage.Real:
                    if (advance || _stageElapsed >= _t.realSec) Enter(IntroStage.Degrade);
                    return IntroEvent.None;

                case IntroStage.Degrade:
                    if (advance || _stageElapsed >= _t.degradeSec) Enter(IntroStage.Structure);
                    return IntroEvent.None;

                case IntroStage.Structure:
                {
                    // 段 3 の尺は「段 2 の後半から始まって段 3 が終わるまで」。重なる分を引く。
                    // ⚠ この式は IntroTiming.TotalSec と卓の introStageSec が共有している。
                    float own = _t.structureSec - _t.degradeSec * (1f - StructureOverlapAt);
                    if (own < StructureMinOwnSec) own = StructureMinOwnSec;
                    if (!advance && _stageElapsed < own) return IntroEvent.None;
                    // 頭を振っている間は枠を閉じ始めない（見ていない方向で閉じると出来事を見逃す）。
                    if (!advance && input.headTurnDegPerSec > MaxHeadTurnForFrame && _holdSec < MaxHoldSec)
                    {
                        _holdSec += dt;
                        return IntroEvent.None;
                    }
                    Enter(IntroStage.Frame);
                    return IntroEvent.None;
                }

                case IntroStage.Frame:
                {
                    if (!advance && _stageElapsed < _t.frameSec) return IntroEvent.None;
                    // スクリーンを見ていて、かつ映像が届いていること。少しは待つ（遅れて来ることがある）。
                    _centeredSec = input.frameCentered ? _centeredSec + dt : 0f;
                    bool ready = _centeredSec >= FrameCenteredHoldSec && input.liveFresh;
                    if (!advance && !ready && _holdSec < MaxHoldSec)
                    {
                        _holdSec += dt;
                        return IntroEvent.None;
                    }
                    // ⚠⚠ **映像が来なくても段 5 へ進む**（canon/LEDGER.md 0025）。
                    //    旧実装はここで演出ごと畳んで本編へ落としていた ＝ カメラが 1 台も繋がって
                    //    いない現場では**枠が閉じた次の瞬間に導入が終わる**。体験者から見れば
                    //    「装置が枠になったのに何も起きずに始まった」で、装置の側の理由が画に無い。
                    //    いまは進んで**砂嵐が出る**（`SignalLostFx` が既に未受信カメラを覆っている）。
                    Enter(IntroStage.Swap);
                    return IntroEvent.None;
                }

                case IntroStage.Swap:
                    if (advance || _stageElapsed >= _t.swapSec)
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
        /// 「体験エリアの中に居る」のヒステリシス。<b>入るのは境界（<see cref="InsideEnterM"/>）、
        /// 出るのは遅く（<see cref="InsideExitM"/>）</b>。
        /// </summary>
        private void UpdateInsideBox(float outsideM, bool valid)
        {
            // ⚠⚠ **解けないあいだは中／外の判断そのものを更新しない**（2026-08-14）。
            //    観測値の 0 は「本当に中に居る」と「位置合わせが済んでいない・形が解けない」の
            //    両方を意味していた。区別せずに読むと、未登録の現場を「中に居る」と判定して
            //    救済まで走らせてしまう。
            if (!valid) return;

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
        /// ⚠ <c>sealBox</c> は全段 0（封印の箱は 2026-08-15 に退避した）。
        /// <c>ignite</c> は全段 1（管は最初から点いていて、まだ何も映していない）。
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
                        return new IntroWeights
                        {
                            passthrough = 1f, frame = 0f, live = 0f, ignite = 1f,
                        };

                    case IntroStage.Real:
                        // 近づいた。素のパススルー ＝ 会場と、まだ何も映していない管。
                        // 段 2 の格下げの比較対象になる。
                        return new IntroWeights
                        {
                            passthrough = 1f, frame = 0f, live = 0f, ignite = 1f,
                        };

                    case IntroStage.Degrade:
                    {
                        float p = Progress(_t.degradeSec);
                        return new IntroWeights
                        {
                            passthrough = 1f,
                            // 彩度だけを滑らかに抜く。輪郭・線・粒・乱れを足すと、連続した光学変化ではなく
                            // 映像効果の切り替えに見えるため、この段では使わない。
                            degrade = SmoothStep(0f, 1f, p),
                            edge = 0f, structure = 0f, grain = 0f, glitch = 0f,
                            frame = 0f, live = 0f, ignite = 1f,
                        };
                    }

                    case IntroStage.Structure:
                        // 次の開口を読ませる前の静止。格下げの終端をそのまま保持する。
                        return new IntroWeights
                        {
                            passthrough = 1f, degrade = 1f, edge = 0f, structure = 0f,
                            grain = 0f, glitch = 0f, frame = 0f, live = 0f, ignite = 1f,
                        };

                    case IntroStage.Frame:
                    {
                        float p = Progress(_t.frameSec);
                        float remaining = 1f - p;
                        return new IntroWeights
                        {
                            passthrough = 1f,
                            degrade = 1f,
                            edge = 0f,
                            structure = 0f,
                            // 4 辺の開口は段の先頭から動き、全視野から本編スクリーンへ連続して閉じる。
                            frame = 1f - remaining * remaining * remaining,
                            // 音と進行の互換入力。IntroVeil は読まない。
                            shatter = p,
                            grain = 0f,
                            glitch = 0f,
                            // 混合量は IntroVeil の _ScreenFade だけが持つ。IntroDirector の
                            // _IntroLive は 0/1 の表示ゲートなので、ここを二重に掛けない。
                            live = SmoothStep(0.78f, 0.96f, p),
                            ignite = 1f,
                        };
                    }

                    case IntroStage.Swap:
                        return new IntroWeights
                        {
                            // 開口と映像を静止して見せる。継ぎ目を隠す乱れは使わない。
                            passthrough = 0f,
                            degrade = 1f,
                            edge = 0f,
                            structure = 0f,
                            frame = 1f,
                            // ⚠ **段 4 で既に映像が出ている。** ここで 0 から上げ直すと、
                            //    出ていた映像が一度消えて戻る。段 5 は「自分だと気づく」ための間。
                            live = 1f,
                            ignite = 1f,
                            grain = 0f,
                            glitch = 0f,
                            // ⚠ **殻は立てない。** 段 4 の終わりで既に「黒 ＋ 枠の中に映像」なので、
                            //    ここで黒を被せると出ていた映像が一度消える。枠の外の黒は覆いが持つ。
                            shell = 0f,
                            shellReveal = 0f,
                        };

                    default:
                        return IntroWeights.Inactive;
                }
            }
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
