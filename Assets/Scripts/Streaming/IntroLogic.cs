#nullable enable

using System;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 導入演出の段。<b><see cref="ShowPhase"/> は増やさない</b> — これは
    /// <see cref="ShowPhase.Intro"/> の**内側**のサブ状態で、ゲート・終了判定・heartbeat・卓・
    /// シミュレータへの分岐を増やさないための設計（計画 2026-07-30_intro-passthrough-to-screen.md §6）。
    /// </summary>
    public enum IntroStage
    {
        /// <summary>まだ始まっていない（導入演出を出さない設定・本編中・終了後）。</summary>
        Off,
        /// <summary>段 0。起動時の黒が明けるのと、スタッフが「始める」と言うのを待つ。</summary>
        Black,
        /// <summary>段 1。素のパススルー。何も演出しない（段 2 の変化を読ませるための比較対象）。</summary>
        Real,
        /// <summary>段 2。色が抜け、コントラストが上がり、実物の輪郭が浮く。</summary>
        Degrade,
        /// <summary>段 3。輪郭だけの世界に、カメラの位置の印と壁・床の線が加わる。</summary>
        Structure,
        /// <summary>段 4。周縁から黒が寄せ、正面に長方形が残る（中はまだパススルー）。</summary>
        Frame,
        /// <summary>段 5。枠の中がカメラ映像へ。枠の中に自分が居る。</summary>
        Swap,
        /// <summary>演出は終わり。ここから慣らし歩行（尺は <see cref="ShowRunLogic"/> が数える）。</summary>
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
        /// コード既定。**合計 12 秒**（段 3 は段 2 と重なるので合計に入らない）。
        ///
        /// 当初は 33 秒で設計したが、体験の入口としてテンポが遅く飽びるため 2026-07-30 に詰めた。
        /// ここは「現実が映像になった」と分かればよく、**驚きの本体は本編 3 周にある**。
        /// 段 5 の「枠の中に自分が居る」は気づかせるだけで足りる — 慣らし歩行に入っても
        /// 枠の中に自分が映っている状態は続くので、歩きながら確かめられる。
        /// </summary>
        public static IntroTiming Default => new IntroTiming
        {
            realSec = 1.5f, degradeSec = 3.5f, structureSec = 2.5f,
            frameSec = 2.5f, swapSec = 4.5f, maxSec = 20f,
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
        /// 演出が実際に流れる秒数（慣らし歩行は含まない）。
        ///
        /// ⚠ **単純和ではない。** 段 3 は段 2 の後半から始まるので、重なった分は二度流れない。
        /// 卓の `intro-model.js` の `introStageSec` と**同じ式**にしてある — 片方だけ直すと
        /// 卓の表示と実機の尺が沈黙して食い違い、作者は尺を信じられなくなる。
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
        /// <summary>壁・床の線とカメラの印の強さ。</summary>
        public float structure;
        /// <summary>枠の閉じ具合。0 = 全画面 / 1 = スクリーンの開口だけ。</summary>
        public float frame;
        /// <summary>カメラ映像の不透明度（枠の中身）。</summary>
        public float live;
        /// <summary>粒状感・走査線の強さ（アプリ側の面で出す）。</summary>
        public float grain;
        /// <summary>乱れ（グリッチ）の強さ。すり替えの継ぎ目を隠す。</summary>
        public float glitch;

        /// <summary>導入演出を出していないときの値（本編と同じ見え）。</summary>
        public static IntroWeights Inactive => new IntroWeights
        {
            passthrough = 0f, degrade = 0f, edge = 0f, structure = 0f,
            frame = 1f, live = 1f, grain = 0f, glitch = 0f,
        };
    }

    /// <summary>段 4・段 5 へ進むかを決めるための観測値。</summary>
    public struct IntroInput
    {
        /// <summary>起動時の黒が明けたか（<c>StartupFader</c> が消えた）。</summary>
        public bool blackCleared;

        /// <summary>
        /// 体験者が<b>開始位置</b>（<c>layout.startSpot</c> の円）に留まっているか。
        /// これが立てばスタッフの合図を待たずに演出が始まる。未設定 / 位置合わせ未登録のときは
        /// 常に false になり、従来どおりスタッフ操作だけで進む（縮退）。
        /// </summary>
        public bool atStartSpot;
        /// <summary>頭の角速度 (度/秒)。大きいうちは枠を出さない（見ていない方向に枠が出る事故を防ぐ）。</summary>
        public float headTurnDegPerSec;
        /// <summary>枠の方向が視野中心の近くにあるか。</summary>
        public bool frameCentered;
        /// <summary>カメラのフレームが新鮮に届いているか。届いていなければ段 5 へ進まない（砂嵐を見せない）。</summary>
        public bool liveFresh;
        /// <summary>トラッキング原点が変わった（OS の recenter）。中止する。</summary>
        public bool recentered;
    }

    /// <summary>
    /// 導入演出の状態機械（UnityEngine 非依存・dt 注入）。
    ///
    /// 設計の正本は <c>.claude/plans/2026-07-30_intro-passthrough-to-screen.md</c>。要点:
    ///   - <b>視点は 1 度も動かさない</b>。動かすのは現実の側の身分（現実 → 映像）
    ///   - 枠は本編のスクリーンそのもの。開口と不透明度だけを動かすので「枠を運ぶ」処理が無い
    ///   - 段 2 と段 3 は<b>重なる</b>（段 2 の後半から構造の線が出始める）。重なりは段の直列ではなく
    ///     <see cref="IntroWeights"/> の重みで表す
    ///   - <b>手を上げたことは検出しない</b>。上げなければ伏線が張られないだけで、体験は壊れない
    /// </summary>
    public sealed class IntroLogic
    {
        /// <summary>段 4 を始めてよい頭の角速度の上限 (度/秒)。これより速く振っていたら待つ。</summary>
        public const float MaxHeadTurnForFrame = 45f;

        /// <summary>段 5 を始める前に、枠が視野中心の近くにあり続ける必要のある秒数。</summary>
        public const float FrameCenteredHoldSec = 0.5f;

        /// <summary>
        /// 段 5 のクロスフェードの秒数。ここは急がない（遅延と視差が同時に来る唯一の点）。
        /// 段 5 が 4.5 秒なので、フェード後に「自分だ」と気づく時間が 3 秒以上残る。
        /// </summary>
        public const float SwapCrossfadeSec = 1.2f;

        /// <summary>
        /// 段 2 の進行度がこれを超えたら、段 3 の構造の線が出始める（段の重なり）。
        /// **卓の `intro-model.js` の `STRUCTURE_OVERLAP_AT` と同じ値**（尺の表示が食い違うため）。
        /// </summary>
        public const float StructureOverlapAt = 0.6f;

        /// <summary>段 3 が段 2 に飲み込まれても、これだけは単独で流れる。</summary>
        public const float StructureMinOwnSec = 0.5f;

        /// <summary>
        /// 段 4・段 5 の開始条件を待てる上限 (秒)。超えたら条件を無視して進む。
        /// 演出全体が 12 秒なので、ここで 6 秒待つと「止まった」ように見える。
        /// </summary>
        public const float MaxHoldSec = 3f;

        private IntroTiming _t = IntroTiming.Default;
        private IntroStage _stage = IntroStage.Off;
        private float _stageElapsed;
        private float _totalElapsed;
        private float _centeredSec;
        private float _holdSec;
        private bool _advanceRequested;
        private bool _skipRequested;

        public IntroStage Stage => _stage;
        public float StageElapsedSec => _stageElapsed;
        public float TotalElapsedSec => _totalElapsed;

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
                    // ここは**時間で進めない**。始めてよいかは体験者の居場所か人間の判断で決まる。
                    //   - 体験者が開始位置（layout.startSpot）へ移動した、または
                    //   - スタッフが合図した
                    // どちらも「黒が明けている」ことが前提（明ける前に始めても何も見えない）。
                    if (input.blackCleared && (advance || input.atStartSpot)) Enter(IntroStage.Real);
                    // 段 0 は maxSec の計時に含めない（待っている時間は演出の尺ではない）。
                    _totalElapsed = 0f;
                    return IntroEvent.None;

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
                    // 頭を振っている間は枠を出さない（見ていない方向に枠が出ると台無し）。
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
                    // 枠を見ていて、かつ映像が届いていること。届いていなければ待つ（砂嵐を見せない）。
                    _centeredSec = input.frameCentered ? _centeredSec + dt : 0f;
                    bool ready = _centeredSec >= FrameCenteredHoldSec && input.liveFresh;
                    if (!advance && !ready && _holdSec < MaxHoldSec)
                    {
                        _holdSec += dt;
                        return IntroEvent.None;
                    }
                    // 待ちきれなかった。映像が無いなら段 5 は無意味なので、枠だけ出して本編へ。
                    if (!advance && !input.liveFresh)
                    {
                        _stage = IntroStage.Done;
                        return IntroEvent.Finished;
                    }
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

        private void Enter(IntroStage next)
        {
            _stage = next;
            _stageElapsed = 0f;
            _holdSec = 0f;
            _centeredSec = 0f;
        }

        /// <summary>
        /// いまの段から各層への重みを出す。<b>見え方の判断はすべてここ</b>（Director は配るだけ）。
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
                        // 旧コメントは「StartupFader の黒面が覆っているので見えない」と書いていたが、
                        // StartupFader は UI Canvas（queue 3000）で覆い（5000）より先に描かれるため
                        // rgb ごと潰される。つまりこの段は最初から素通しのパススルーだった。
                        //
                        // **それでよい**（2026-07-31 に確認して意図的に残した）。この段は開始の合図
                        // （通過ライン line_1 を横切る）を待つ区間で、実測 17 秒ある。真っ黒にすると
                        // 体験者は何も見えないまま歩いて線を越えることになり、運用が成立しない。
                        return new IntroWeights { passthrough = 1f, frame = 0f, live = 0f };

                    case IntroStage.Real:
                        return new IntroWeights { passthrough = 1f, frame = 0f, live = 0f };

                    case IntroStage.Degrade:
                    {
                        float p = Progress(_t.degradeSec);
                        return new IntroWeights
                        {
                            passthrough = 1f,
                            // 色 → コントラスト → 輪郭 → 粒 の順に足す。一度に全部動かすと
                            // 「質感が落ちた」ではなく「ただ壊れた」に見える。
                            degrade = p,
                            edge = SmoothStep(0.35f, 1f, p),
                            structure = SmoothStep(StructureOverlapAt, 1f, p),
                            grain = SmoothStep(0.6f, 1f, p) * 0.6f,
                            frame = 0f, live = 0f,
                        };
                    }

                    case IntroStage.Structure:
                        return new IntroWeights
                        {
                            passthrough = 1f, degrade = 1f, edge = 1f, structure = 1f,
                            grain = 0.6f, frame = 0f, live = 0f,
                        };

                    case IntroStage.Frame:
                    {
                        float p = Progress(_t.frameSec);
                        return new IntroWeights
                        {
                            passthrough = 1f,
                            degrade = 1f,
                            // 枠になるとき構造の線は引く。枠の中の現実に集中させる。
                            edge = 1f - 0.7f * p,
                            structure = 1f - p,
                            frame = SmoothStep(0f, 1f, p),
                            grain = 0.6f,
                            live = 0f,
                        };
                    }

                    case IntroStage.Swap:
                    {
                        float cross = _t.swapSec > 0f
                            ? Clamp01(_stageElapsed / Math.Max(SwapCrossfadeSec, 0.01f)) : 1f;
                        float s = SmoothStep(0f, 1f, cross);
                        return new IntroWeights
                        {
                            // 枠の中身が現実から映像へ入れ替わる。パススルーは最後まで切らない
                            // （切ると数百 ms の黒が出るため。切るのは本編に入ってから）。
                            passthrough = 1f - s,
                            degrade = 1f,
                            edge = 0f,
                            structure = 0f,
                            frame = 1f,
                            live = s,
                            grain = 0.6f * (1f - s),   // 以後は映像側の post FX が持つ
                            // 継ぎ目は乱れで隠す（企画書 2.3 の手法をここで一度見せておく）。
                            glitch = Bump(cross),
                        };
                    }

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
