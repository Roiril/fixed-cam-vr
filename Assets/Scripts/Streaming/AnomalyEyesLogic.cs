#nullable enable

using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>スクリーンの外の闇で目が開いていく段。</summary>
    public enum EyesStage
    {
        /// <summary>出ていない。</summary>
        Off,
        /// <summary><b>兆し</b>。大きい目が 1 つだけ、断片から見開くまで。</summary>
        Hint,
        /// <summary><b>凝視</b>。大きい目は開き切って動かない。体験者が気づいて報告する時間。</summary>
        Stare,
        /// <summary><b>開眼</b>。残りの目が、大きい目のまわりから 360 度へ広がって開く。</summary>
        Swarm,
        /// <summary>全部開いたまま。</summary>
        Hold,
        /// <summary>消えていく（報告で畳まれた / 区間が終わった）。</summary>
        Fading,
    }

    /// <summary>
    /// <b>スクリーンの外の黒い背景に、360 度いちめんの目が開く</b>異変の進み方。
    /// 出どころは <c>canon/LEDGER.md</c> 0075（言葉と参考画像）と 0076（緩急の作り直し）。
    ///
    /// <b>時計はここが持つ</b>（<see cref="OutroLogic.ScreenCollapse"/> と同じ流儀 —
    /// 進みを数値で出せば、実際に画へ書いた値をテレメトリに出せて、テストで固定でき、毎回同じ絵になる）。
    /// ここが出すのは 4 つの数だけで、どの目がいつ開くかはシェーダが
    /// <see cref="Field"/> と目ごとの順位（<c>rank</c>）から解く。
    ///
    /// ⚠⚠ <b>滑らかに開かない。「止まる」と「一気に」の繰り返しで組む</b>（0076）。
    ///   初版は全部が等速の滑らかな曲線で、ユーザー判定は
    ///   「目が開くのもゆっくり過ぎて怖くないし、演出として面白くない」だった。
    ///   <b>怖さは速さではなく落差から出る</b> — 長く止まってから 0.12 秒で見開く。
    ///
    /// 段の並び（合計 <b>4.92 秒</b>。10.5 → 8.4 → **4.92** と 2 度詰めた）:
    /// <list type="number">
    ///   <item><b>兆し</b>（<see cref="HintSec"/> = 1.42s）— 闇 0.70 → <b>音の頭で一気に</b>（0.04 秒で 0.45 まで）→
    ///     <b>音の膨らみに沿って開き切る</b>（頭から 0.27 秒）→ 開いたまま 0.45</item>
    ///   <item><b>凝視</b>（<see cref="StareSec"/> = 0.5s）— 静止。1 度だけ瞬く（0.055 秒）</item>
    ///   <item><b>開眼</b>（<see cref="SwarmSec"/> = 3.0s）— <b>さざめき → 間 → 一気に 360 度</b>。
    ///     1 つの目が開くのは 0.10 秒（<see cref="SwarmSpan"/>）</item>
    ///   <item><b>持続</b> — 畳まれるまで開いたまま</item>
    /// </list>
    ///
    /// ⚠⚠ <b>2026-08-20 に、止まっている 3 つ（闇 / 断片のまま静止 / 凝視）を 0.5 秒へ詰めた</b>
    ///   （<c>canon/LEDGER.md</c> 0094・早回しの動画を見たユーザーの判定「こっちのほうがいい」）。
    ///   <b>動いている所は 1 つも触っていない</b> — 断片が現れる 0.10 / 見開く 0.13 /
    ///   さざめき 0.27 / 間 0.36 / 一気に 1.56 は 0076 のまま。詰めたのは「間」だけ。
    ///
    /// ⚠⚠ <b>2026-09-04 に、兆しの開き方を「音の形」に合わせた</b>（<c>canon/LEDGER.md</c> 0135・
    ///   ユーザー逐語「最初におおきい目が開く時のアニメーションと、開く時の音が、一致感があまりない。
    ///   音にアニメーションを合わせてほしい。自然な感じに」）。
    ///   0131 で「見開く瞬間（1.234 秒）に音のいちばん大きいところ」を置いたが、音（もらった
    ///   <c>大きい目が出現.mp3</c>）は<b>0.66 秒で一気に立ち上がり、0.27 秒で膨らみ切って、あとは
    ///   1 秒以上鳴り続ける</b>形で、いちばん大きい所は事件ではなく<b>持続の途中</b>だった。
    ///   絵の方は 0.50 秒で断片が出て 0.50 秒止まり、1.10〜1.23 秒で見開く ＝ <b>音の頭（0.66）には
    ///   絵の出来事が無く、絵の見開き（1.23）には音の出来事が無い</b>。これが一致感の無さの正体。
    ///   ⇒ 断片と静止をやめ、<b>闇 → 音の頭で一気に → 音の膨らみのとおりに開き切る</b>にした。
    ///   開き具合は音の聴感（実効値の 0.6 乗・持続の高さで正規化）を 10ms で測った表
    ///   （<see cref="HintOpenKnots"/>）そのもの。音は 1 ビットも動かしていない。
    ///   「止まる → 一気に」（0076）は<b>闇 0.70 秒 → 音の頭</b>の落差として残っている。
    ///
    /// ⚠ <b>報告は引き金ではない。</b> 0075 の「ボタンはトリガーではなく、あくまでそれくらいの時間で」。
    ///   押さなくても段は同じ速さで進む。押した時に消えるのは
    ///   <see cref="ShowTakeDef.dismissible"/>（演出の側の仕組み・<c>LEDGER</c> 0050）であって、ここではない。
    ///
    /// ⚠ <b>時間はこの異変が出ているあいだだけ進む。</b> 畳まれたら
    ///   <see cref="CloseSec"/> かけて閉じ、そこで進みが 0 に戻る ＝ <b>次に出るときは必ず兆しから</b>。
    /// </summary>
    public sealed class AnomalyEyesLogic
    {
        /// <summary>
        /// 兆し（大きい目が 1 つだけ開く）の尺 (秒)。<b>2026-08-20 に 2.6 → 1.42</b>（0094）。
        /// 内訳は 闇 0.70 ＋ 音の頭で一気に〜膨らみ切る 0.27（<see cref="HintOpenKnots"/>）＋ 開いたまま 0.45。
        /// </summary>
        public const float HintSec = 1.42f;

        /// <summary>
        /// 凝視の尺 (秒)。<b>2026-08-20 に 2.8 → 0.5</b>（0094）。
        ///
        /// ⚠⚠ <b>0075 の「気づいて報告ボタンを押すくらいの時間」は、ここで無くなった。</b>
        /// 気づく 0.8 ＋ 手 1.0 ＋ 長押し 1.0 は 0.5 秒に収まらないので、
        /// <b>体験者が押し切る前に残りが開き始める</b>。詰めた側を選んだのはユーザーの判定（0094）で、
        /// 目の異変は <c>dismissible</c> を立てていない（＝ 押しても消えない・0084）ため、
        /// 「押し切れるか」は体験の成否に効かない。
        /// ⚠ 報告のための間を戻したくなったら、伸ばすのは<b>ここ</b>（開眼の側ではない）。
        /// </summary>
        public const float StareSec = 0.5f;

        /// <summary>残りが開き切るまでの尺 (秒)。0075 の「3 秒くらいですべての目が開き」。</summary>
        public const float SwarmSec = 3.0f;

        /// <summary>
        /// <b>閉じる尺 (秒)。開くのと同じく「一気に → 止まる → 一気に」で組む</b>
        /// （2026-08-17・<c>canon/LEDGER.md</c> 0084・ユーザー指定
        /// 「閉じるときは、開くときと同じように緩急つけて」）。
        ///
        /// ⚠⚠ <b>2026-08-17 まで 0.45 秒の一様なフェードだった</b>（旧 <c>FadeOutSec</c>）。
        /// 全部が同じ速さで薄くなるので、**閉じたのではなく消えた**（電源が落ちた）ように見える。
        /// いまは<b>開いた順の逆で閉じる</b> — いちめんが順に閉じ、大きい目だけが残って、最後にそれが閉じる。
        ///
        /// ⚠ 不透明度（<see cref="Fade"/>）では閉じない。**形で閉じる**
        /// （0028「外から消さない」と同じ考え — 薄くするのは消しゴムであって動きではない）。
        /// </summary>
        public const float CloseSec = 1.6f;

        /// <summary>
        /// いちめんの目が閉じ切るまで（<see cref="CloseSec"/> に対する割合）。
        /// ⚠⚠ <b>ここは「速い」ではなく「瞼が下りるのが見える」速さ</b>
        /// （2026-08-18・ユーザー赤入れ「最後の一つ以外も、目を閉じるようなアニメーションで閉じて」）。
        /// 1 つの目が閉じるのにかかるのは <see cref="CloseSpan"/> × この尺 ＝ <b>0.11 秒</b>
        /// （実物の瞬きと同じ）。旧値 0.30 × <see cref="SwarmSpan"/> では <b>0.012 秒</b>で、
        /// 瞼が下りる過程が 1 コマも描かれず「消えた」に見えていた。
        /// </summary>
        public const float CloseFieldAt = 0.38f;

        /// <summary>
        /// <b>大きい目が笑い切るまで</b>（2026-08-18・ユーザー赤入れ
        /// 「最後の一つは、笑っているみたいな感じで、目を細めてから閉じて」）。
        /// 下瞼が持ち上がって上に凸の三日月になる（<see cref="Smile"/>）。
        /// </summary>
        public const float CloseSmileAt = 0.62f;

        /// <summary>
        /// 細めたまま止まっている終わり。<b>ここがいちばん長い</b>
        /// （兆しの <see cref="HintHoldAt"/> と対になる ＝ 見開く前と閉じる前に同じ間がある）。
        /// </summary>
        public const float CloseHoldAt = 0.82f;

        /// <summary>
        /// <b>閉じるときの順の幅。</b> 開く <see cref="SwarmSpan"/>（0.035）より広い。
        ///
        /// ⚠⚠ この 2 つを同じにすると<b>閉じる動きが原理的に描けない</b>。
        /// 1 つの目が閉じるのにかかる時間 ＝ 幅 × いちめんが閉じる尺 なので、
        /// 0.035 × 0.61 秒 = <b>0.021 秒</b> ＝ 30fps で 0.6 コマ。瞼は下りずに消える。
        /// 0.18 なら 0.11 秒 ＝ 3.3 コマで、実物の瞬きと同じ速さになる。
        /// ⚠ 広げすぎると「順に閉じる」が消えて全部が一斉に閉じる。
        /// </summary>
        public const float CloseSpan = 0.18f;

        /// <summary>笑うときに下瞼がどれだけ持ち上がるか（0 = 笑わない）。絵を見て決めた値。</summary>
        public const float SmileLift = 1.15f;

        /// <summary>「開いている」とみなす下限（観測の数え方を 1 か所に固定する）。</summary>
        public const float OpenEpsilon = 0.05f;

        // ---- 兆しの中の刻み（秒。**闇 → 音の頭で一気に → 音の膨らみのとおりに** を作る・0135）-------
        /// <summary>
        /// <b>大きい目が動き出す時刻</b>（兆しの頭から・秒）＝ 音の頭。
        ///
        /// 焼いた <c>sfx_eye_big</c> は頭に 0.653 秒の無音を持ち、音が立つのは <b>0.66 秒</b>
        /// （10ms 窓の実効値が持続の高さの -30dB を越える所）。鳴らすのは兆しの頭のフレームで、
        /// <see cref="SfxPlayer.ScheduleLeadSec"/>（0.035 秒）だけ先読みして予約するので、
        /// 耳に届く頭は 0.695 秒。絵の 1 コマの遅れと音の出口の遅れはほぼ相殺するので 0.70。
        /// ⚠ 音を焼き直したら <c>EyeSoundTests.EyeBig_OpeningFollowsTheSoundsLoudness</c> が落ちる
        /// （焼いたファイルを測って、この値と下の表を突き合わせる）。
        /// </summary>
        public const float HintOnsetSec = 0.70f;

        /// <summary>
        /// <b>開き具合の表</b>（音の頭からの秒 → 開き具合）。<b>音の聴感そのもの</b>:
        /// 焼いた <c>sfx_eye_big</c> の 10ms 実効値を持続の高さ（1.0〜1.5 秒の平均）で割り、
        /// 0.6 乗（Stevens の法則）して、戻らないように最大値で保持し、1 で止めたもの。
        ///
        /// 形は <b>2 段</b>: 頭で一気に 0.45 まで（0.04 秒）→ 0.6 前後の肩（0.05〜0.12 秒）→
        /// もう一段膨らんで開き切る（0.27 秒）。肩は音の中にある膨らみの段そのもので、
        /// 消すと音と別の動きになる。
        /// ⚠ 表を手で整えない。音を焼き直したら測り直して写す（テストが突き合わせる）。
        /// </summary>
        public static readonly float[,] HintOpenKnots =
        {
            { 0.00f, 0.18f }, { 0.01f, 0.25f }, { 0.02f, 0.36f }, { 0.03f, 0.40f },
            { 0.04f, 0.45f }, { 0.06f, 0.55f }, { 0.09f, 0.59f }, { 0.12f, 0.64f },
            { 0.14f, 0.64f }, { 0.17f, 0.83f }, { 0.19f, 0.91f }, { 0.22f, 0.93f },
            { 0.27f, 1.00f },
        };

        /// <summary>開き切る時刻（兆しの頭から・秒）＝ 音の頭 ＋ 表の最後。</summary>
        public static float HintFullSec => HintOnsetSec + HintOpenKnots[HintOpenKnots.GetLength(0) - 1, 0];

        // ---- 凝視の中の瞬き（StareSec に対する割合）---------------------------------------
        /// <summary>瞬きの中心。静止のただ中で 1 度だけ落ちる。</summary>
        public const float StareBlinkAt = 0.46f;
        /// <summary>
        /// 瞬きの半幅。⚠ 凝視が 0.5 秒になったので、瞬きは<b>全体で 0.055 秒</b>
        /// （90Hz で 5 コマ）しかない。0094 の動画（30fps）ではほとんど見えていない。
        /// </summary>
        public const float StareBlinkHalf = 0.055f;
        /// <summary>瞬きで閉じ切らない量（完全に閉じると「消えた」に見える）。</summary>
        public const float StareBlinkFloor = 0.12f;

        // ---- 開眼の中の刻み（SwarmSec に対する割合）---------------------------------------
        /// <summary>さざめき（隣の数個が開く）の終わり。</summary>
        public const float SwarmRippleAt = 0.09f;
        /// <summary>さざめきで届く順位（＝ 大きい目から 24° ほど）。</summary>
        public const float SwarmRippleField = 0.14f;
        /// <summary><b>間</b>。何も起きない。ここが効く。</summary>
        public const float SwarmPauseAt = 0.21f;
        /// <summary>一気に 360 度まで開き切る終わり。残りは全開のまま。</summary>
        public const float SwarmRushAt = 0.73f;

        /// <summary>
        /// 目 1 つが開き切るのにかかる、開眼の尺に対する割合。
        /// <b>0.035 ＝ 3 秒のうち 0.10 秒</b>（実物の目が開く速さ）。
        /// ⚠ 初版は 0.28（0.84 秒）で、これが「ゆっくり過ぎて怖くない」の主因だった。
        /// </summary>
        public const float SwarmSpan = 0.035f;

        // ---- 待機中の視線（2026-08-17・0084「待機中は目がぎょろぎょろ動く感じ」）------------
        /// <summary>
        /// 開き切ってから視線が動き出すまでの立ち上がり (秒)。
        /// ⚠ 0 にしない — 開いた瞬間に全部が動き出すと、開眼の一撃と重なって<b>どちらも流れる</b>
        /// （0076「動かすものは 1 つに絞る」）。開き切った静止を一拍置いてから、目が動き始める。
        /// </summary>
        public const float GazeRiseSec = 0.9f;

        /// <summary>閉じ始めたら視線を止めるまで (秒)。<b>閉じる動きが主</b>なので速く落とす。</summary>
        public const float GazeFallSec = 0.25f;

        private float _t;          // この異変が出てからの経過（Fading では止める）
        private float _fade;
        private float _close;      // 閉じる進み 0..1（Fading のあいだだけ動く）
        private float _gaze;

        /// <summary>いまの段。</summary>
        public EyesStage Stage { get; private set; } = EyesStage.Off;

        /// <summary>大きい目の開き具合 0..1。</summary>
        public float Big { get; private set; }

        /// <summary>残りの目の広がり 0..1（0 = 1 つも開いていない / 1 = 全部開いた）。</summary>
        public float Field { get; private set; }

        /// <summary>
        /// 怖さの強度 0..1。開眼と一緒に上がり、持続で 1。
        /// 瞳孔の収縮・震え・輪郭の強さに効く。
        /// </summary>
        public float Intensity { get; private set; }

        /// <summary>全体の不透明度 0..1（畳まれると 0 へ落ちる）。</summary>
        public float Fade => _fade;

        /// <summary>開く目の割合 0..1（カットの <c>eyes</c> の値）。</summary>
        public float Density { get; private set; }

        /// <summary>
        /// <b>待機中の視線移動の強さ 0..1</b>（2026-08-17・<c>canon/LEDGER.md</c> 0084）。
        /// 開き切って一拍置いてから 1 へ上がり、閉じ始めたら落ちる。
        ///
        /// ⚠ <b>どの目がいつどこを見るかはここで決めない。</b> 1 つの値では目ごとに散らせないので、
        /// <b>強さだけを配ってシェーダが <c>seed</c> で位相を散らす</b>（瞬き <c>_EyeBlink</c> と同じ流儀）。
        /// 全部が同時に同じ方向を見ると、群れではなく<b>1 匹の生き物</b>に見える。
        /// </summary>
        public float Gaze => _gaze;

        /// <summary>
        /// <b>大きい目の笑い 0..1</b>（2026-08-18・ユーザー赤入れ
        /// 「最後の一つは、笑っているみたいな感じで、目を細めてから閉じて」）。
        /// 下瞼が中央ほど持ち上がって<b>上に凸の三日月</b>になる。
        /// ⚠ <see cref="Big"/> を下げて細めるのとは別物 — あちらは上下から均等に狭まる（眠そうな目）。
        /// 笑いは<b>下だけが上がる</b>。
        /// </summary>
        public float Smile { get; private set; }

        /// <summary>
        /// <b>いま閉じているか</b> 0..1。シェーダが「開きかけの断片」を止めるのに読む。
        ///
        /// ⚠⚠ 断片（<c>DROP_EARLY</c>）は<b>闇から現れるときの姿</b>で、開き具合が小さいほど強く欠ける。
        /// 閉じるときも開き具合は小さくなるので、そのままだと<b>瞼が下りるのではなく砕けて散る</b>
        /// （2026-08-18 の赤入れ「目を閉じるようなアニメーションで閉じて」の正体がこれ）。
        /// </summary>
        public float Closing => Stage == EyesStage.Fading ? 1f : 0f;

        /// <summary>
        /// いま配るべき<b>順の幅</b>（開く <see cref="SwarmSpan"/> / 閉じる <see cref="CloseSpan"/>）。
        /// ⚠ <b>シェーダへ渡すのは必ずこれ</b>。定数を直に渡すと閉じる動きが描けない。
        /// </summary>
        public float Span => Stage == EyesStage.Fading ? CloseSpan : SwarmSpan;

        /// <summary>いま画に何か出ているか。</summary>
        public bool Visible => _fade > 0.001f && (Big > OpenEpsilon || Field > 0f);

        /// <summary>
        /// 向きを直してよいか（<c>false</c> ＝ 直してよい）。
        /// <b>残りが開き始めたら二度と回さない</b> — 開いた目が動くと「回っている」が見えてしまう。
        /// </summary>
        /// ⚠ <b>閉じているあいだも回さない。</b> いちめんが閉じた後も大きい目が残って閉じるので、
        /// <c>Field &gt; 0</c> だけを見ると<b>最後の 1 つが閉じる途中で群れごと回る</b>（2026-08-17）。
        public bool AnchorLocked => Stage == EyesStage.Swarm || Stage == EyesStage.Hold
                                    || Stage == EyesStage.Fading;

        /// <summary>この異変が始まったフレームか（向きを頭へ合わせ直す縁）。</summary>
        public bool JustStarted { get; private set; }

        /// <summary>
        /// <b>ぜんぶ落とす</b>（ラン開始・中止・無効化）。<see cref="Tick"/> に <c>wanted:false</c> を
        /// 渡すのとは別物 —— あちらは<b>閉じる演出を始める</b>。ここは<b>無かったことにする</b>。
        /// ⚠ 体験者の交代でここを通らないと、次の人の視界に前の人の目が閉じ残る。
        /// </summary>
        public void Reset()
        {
            Stage = EyesStage.Off;
            _t = 0f;
            _fade = 0f;
            _close = 0f;
            _gaze = 0f;
            Big = 0f;
            Field = 0f;
            Intensity = 0f;
            Density = 0f;
            Smile = 0f;
            JustStarted = false;
        }

        /// <summary>1 フレーム進める。</summary>
        /// <param name="dt">経過 (秒)</param>
        /// <param name="wanted">カットが出せと言っているか</param>
        /// <param name="density">開く目の割合 0..1（カットの <c>eyes</c>）</param>
        /// <param name="rate">
        /// 進みの速さ（1 = 著作どおり）。<b>区間の半ばを過ぎても開き切っていないときの追い上げ</b>に使う
        /// （<see cref="EyesCueLogic"/>・<c>canon/LEDGER.md</c> 0093）。
        /// ⚠ <b>閉じる側は速めない</b>（呼び手が 1 を渡す）。1 つの瞼が下りるのは 0.11 秒しかなく、
        /// 倍にすると 0085 の赤入れ（閉じるアニメーション）が消える。
        /// </param>
        public void Tick(float dt, bool wanted, float density, float rate = 1f)
        {
            JustStarted = false;
            dt = Mathf.Max(0f, dt) * Mathf.Max(0f, rate);

            if (wanted)
            {
                Density = Mathf.Clamp01(density);
                if (Stage == EyesStage.Off)
                {
                    _t = 0f;
                    JustStarted = true;
                }
                _fade = 1f;   // 立ち上がりは兆しの曲線そのものが担う（重ねてぼかさない）
                _close = 0f;
                Smile = 0f;
                _t += dt;
                Advance();
                // 待機に入ってから視線が動き出す（開く動きと重ねない）。
                _gaze = Stage == EyesStage.Hold
                    ? Mathf.Min(1f, _gaze + (GazeRiseSec > 0f ? dt / GazeRiseSec : 1f))
                    : 0f;
                return;
            }

            if (Stage == EyesStage.Off) return;

            if (Stage != EyesStage.Fading)
            {
                // 閉じ始めた縁。**ここの値から 0 へ落とす** — 開き切る前に畳まれても形が飛ばない。
                _bigAtClose = Big;
                _fieldAtClose = Field;
                _intensityAtClose = Intensity;
                _close = 0f;
            }

            // 畳まれた。**進みは止める**（消えていく最中に残りが開き始めると、
            // 押した行為の結果が「消えた」ではなく「増えた」に見える）。
            Stage = EyesStage.Fading;
            _gaze = Mathf.Max(0f, _gaze - (GazeFallSec > 0f ? dt / GazeFallSec : 1f));
            _close = CloseSec > 0f ? Mathf.Min(1f, _close + dt / CloseSec) : 1f;

            // ⚠ 開いた順の逆で閉じる。**不透明度は最後まで 1**（薄くするのは動きではない）。
            Field = _fieldAtClose * CloseFieldCurve(_close);
            Big = _bigAtClose * CloseBigCurve(_close);
            // ⚠ 強度は大きい目と同じ曲線で落とす。いちめんに合わせて落とすと、
            //   笑っているあいだに瞳孔だけが開いていく（笑い目に見えない）。
            Intensity = _intensityAtClose * CloseBigCurve(_close);
            Smile = CloseSmileCurve(_close);

            if (_close < 1f) return;

            Stage = EyesStage.Off;
            _t = 0f;
            _fade = 0f;
            _close = 0f;
            _gaze = 0f;
            Big = 0f;
            Field = 0f;
            Intensity = 0f;
            Density = 0f;
            Smile = 0f;
        }

        // 閉じ始めた瞬間の値（ここから 0 へ落とす）。開き切る前に畳まれても形が飛ばない。
        private float _bigAtClose, _fieldAtClose, _intensityAtClose;

        /// <summary>
        /// いちめんの目の閉じ方。<b>一気に閉じて、あとは 0 のまま</b>。
        /// 順位の高い目から閉じる（<c>Field</c> を下げると外側から閉じる ＝ <b>開いた順の逆</b>）。
        /// </summary>
        public static float CloseFieldCurve(float x01)
        {
            float x = Mathf.Clamp01(x01);
            if (x >= CloseFieldAt) return 0f;
            // 頭を速く（一気に外側が閉じる）。
            float u = x / CloseFieldAt;
            return 1f - (1f - Mathf.Pow(1f - u, 2.2f));
        }

        /// <summary>
        /// <b>大きい目の笑い。</b> いちめんが閉じ切ってから細まり、以後は笑ったまま閉じる。
        /// ⚠ <see cref="CloseBigCurve"/>（開き具合）とは別物 — あちらを下げると上下から均等に狭まって
        /// <b>眠そうな目</b>になる。笑いは<b>下瞼だけが上がる</b>ので、形はシェーダが作る。
        /// </summary>
        public static float CloseSmileCurve(float x01)
        {
            float x = Mathf.Clamp01(x01);
            if (x < CloseFieldAt) return 0f;
            if (x < CloseSmileAt)
                return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(CloseFieldAt, CloseSmileAt, x));
            return 1f;
        }

        /// <summary>
        /// 大きい目の閉じ方。<b>いちめんが閉じるあいだも開いたまま → 細めて長い静止 → 最後にすっと閉じる</b>。
        /// 兆し（<see cref="HintCurve"/>）を逆から辿った形。
        /// ⚠ 細めるのは <see cref="CloseSmileCurve"/> の仕事。ここは<b>最後に閉じ切る</b>ぶんだけを持つ。
        /// </summary>
        public static float CloseBigCurve(float x01)
        {
            float x = Mathf.Clamp01(x01);
            if (x < CloseHoldAt) return 1f;
            return 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(CloseHoldAt, 1f, x));
        }

        private void Advance()
        {
            float t = _t;
            if (t < HintSec)
            {
                Stage = EyesStage.Hint;
                Big = HintCurve(t / HintSec);
                Field = 0f;
                Intensity = 0f;
                return;
            }

            t -= HintSec;
            if (t < StareSec)
            {
                Stage = EyesStage.Stare;
                Big = StareCurve(t / StareSec);
                Field = 0f;
                Intensity = 0f;
                return;
            }

            Big = 1f;
            t -= StareSec;
            if (t < SwarmSec)
            {
                Stage = EyesStage.Swarm;
                Field = SwarmCurve(t / SwarmSec);
                Intensity = Field;
                return;
            }

            Stage = EyesStage.Hold;
            Field = 1f;
            Intensity = 1f;
        }

        /// <summary>
        /// 兆しの曲線。<b>闇 → 音の頭で一気に → 音の膨らみのとおりに開き切る</b>（0135）。
        /// <paramref name="x01"/> は <see cref="HintSec"/> に対する進み。
        /// ⚠ 直線にも指数にもしない。形は <see cref="HintOpenKnots"/>（音を測った表）そのもの。
        /// 闇が 0.70 秒続いてから音と同時に開くので、「止まる → 一気に」（0076）は残っている。
        /// </summary>
        public static float HintCurve(float x01)
        {
            float u = Mathf.Clamp01(x01) * HintSec - HintOnsetSec;
            return HintOpenAt(u);
        }

        /// <summary>音の頭から <paramref name="secSinceOnset"/> 秒後の開き具合（表の線形補間）。</summary>
        public static float HintOpenAt(float secSinceOnset)
        {
            if (secSinceOnset < 0f) return 0f;
            int n = HintOpenKnots.GetLength(0);
            if (secSinceOnset >= HintOpenKnots[n - 1, 0]) return HintOpenKnots[n - 1, 1];
            for (int i = 1; i < n; i++)
            {
                float t1 = HintOpenKnots[i, 0];
                if (secSinceOnset > t1) continue;
                float t0 = HintOpenKnots[i - 1, 0];
                float v0 = HintOpenKnots[i - 1, 1], v1 = HintOpenKnots[i, 1];
                return t1 > t0 ? Mathf.Lerp(v0, v1, (secSinceOnset - t0) / (t1 - t0)) : v1;
            }
            return HintOpenKnots[n - 1, 1];
        }

        /// <summary>凝視の曲線。開き切ったまま、途中で 1 度だけ瞬く。</summary>
        public static float StareCurve(float x01)
        {
            float d = Mathf.Abs(Mathf.Clamp01(x01) - StareBlinkAt) / StareBlinkHalf;
            if (d >= 1f) return 1f;
            // 落ちて戻る（下向きの山）。閉じ切らない。
            float k = 1f - d * d * (3f - 2f * d);       // 中心 1 → 端 0
            return Mathf.Lerp(1f, StareBlinkFloor, k);
        }

        /// <summary>
        /// 開眼の曲線。<b>さざめき → 間 → 一気に</b>。
        /// ⚠ 等速で回すと「波が通り過ぎるのを眺める」になる。**止まる時間**が驚きを作る。
        /// </summary>
        public static float SwarmCurve(float x01)
        {
            float x = Mathf.Clamp01(x01);
            if (x < SwarmRippleAt)
                return Mathf.SmoothStep(0f, SwarmRippleField, x / SwarmRippleAt);
            if (x < SwarmPauseAt) return SwarmRippleField;
            if (x < SwarmRushAt)
            {
                float u = Mathf.InverseLerp(SwarmPauseAt, SwarmRushAt, x);
                // 頭を速く、尻を少し伸ばす（真後ろまで届いたのが分かる）。
                return Mathf.Lerp(SwarmRippleField, 1f, 1f - Mathf.Pow(1f - u, 1.9f));
            }
            return 1f;
        }

        /// <summary>
        /// 目 1 つの開き具合。<b>シェーダと同じ式</b>（片方だけ直すと、数えた本数と画が黙って食い違う）。
        /// 順位（<paramref name="rank01"/>）は<b>大きい目からの角度</b>で、0 = 隣・1 = 真後ろ。
        /// </summary>
        public static float EyeOpen(float field01, float rank01)
        {
            float span = Mathf.Max(0.005f, SwarmSpan);
            return Mathf.Clamp01((field01 * (1f + span) - rank01) / span);
        }
    }

    /// <summary>
    /// 大きい目を<b>体験者の視界へ入れ直す</b>かの判断。
    ///
    /// 目の群れはワールドに固定する（頭に張り付くと HUD に見える）。ところが体験者は歩きながら
    /// 頭を回すので、<b>ワールド固定のままだと大きい目が真後ろで開いて、誰にも見られないまま
    /// 兆しと凝視の 5.4 秒が終わる</b>。この異変は「1 つの目に気づく」ことが要なので、それでは成立しない。
    ///
    /// ⇒ <b>視界の外に居るあいだだけ、群れごと頭の向きへ合わせ直す。</b>
    ///   合わせ直すのは大きい目が <see cref="LostDeg"/> より外に <see cref="LostHoldSec"/> 続けて居るときだけで、
    ///   そのとき<b>開いている目は大きい目 1 つ（＝視界の外）だけ</b>なので、回ったことは 1 画素も見えない。
    ///
    /// ⚠ <b>残りが開き始めたら二度と回さない</b>（<see cref="AnomalyEyesLogic.AnchorLocked"/>）。
    ///   開いた目が動けば、それは「目が回った」ではなく「世界が回った」に見える。
    /// </summary>
    public sealed class EyeAnchorLogic
    {
        /// <summary>これより外なら「視界に入っていない」(度)。Quest 3 の表示画角は水平 ±55° 前後。</summary>
        public const float LostDeg = 80f;

        /// <summary>視界の外に居続けたら合わせ直すまでの時間 (秒)。首を振っただけでは動かさない。</summary>
        public const float LostHoldSec = 1.0f;

        private float _lostSec;

        /// <summary>視界の外に居続けている時間 (秒)。診断用。</summary>
        public float LostSec => _lostSec;

        /// <summary>1 フレーム進めて、合わせ直すべきなら true を返す（true を返した回は自分で数えを落とす）。</summary>
        /// <param name="dt">経過 (秒)</param>
        /// <param name="offAxisDeg">頭の正面から大きい目までの角度 (度)</param>
        /// <param name="locked">もう回してはいけないか（残りが開き始めた後）</param>
        public bool Tick(float dt, float offAxisDeg, bool locked)
        {
            if (locked || offAxisDeg <= LostDeg) { _lostSec = 0f; return false; }
            _lostSec += Mathf.Max(0f, dt);
            if (_lostSec < LostHoldSec) return false;
            _lostSec = 0f;
            return true;
        }

        /// <summary>数えを落とす（異変が終わった / 始まったとき）。</summary>
        public void Reset() => _lostSec = 0f;
    }
}
