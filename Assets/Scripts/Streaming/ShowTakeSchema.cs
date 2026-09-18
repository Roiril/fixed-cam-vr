#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// show.json v3 の「演出（Take）」1 本。区間 <c>(lap, camera)</c> に付き、開始規則と占有ポリシーを持つ
    /// **剛体**（尺が決まっている）。中に <see cref="ShowStepDef"/>（カット）を順に持つ。
    ///
    /// v2 の <c>cues[]</c>（オーバーレイ）と <c>insert</c>（カメラ差し込み）を 1 語彙へ畳んだもの。
    /// 契約の正本は <c>.claude/plans/2026-07-25_shot-timeline-foundation.md</c> §6。
    ///
    /// JsonUtility の「null 入れ子を既定値で書く」罠を避けるため、<c>start</c> / <c>source</c> / <c>dur</c> は
    /// オブジェクトにせず**フラットな文字列判別子 + 値**で持つ。present-flag が要るのは post だけ。
    /// </summary>
    [Serializable] public sealed class ShowTakeDef
    {
        public string id = "";              // 区間内で一意。空なら実行時に "L<lap>C<cam>#<i>" で補う
        public string name = "";            // UI 表示のみ（実行に影響しない）

        public string at = TakeSchema.AtEnter;              // "enter" | "exit" | "line"
        public float offsetSec;                             // at=enter のみ（区間進入からの遅延）
        public string ifMissed = TakeSchema.MissedFireOnExit; // at=enter / line。"fireOnExit" | "skip"

        /// <summary>
        /// at=line のみ。<c>layout.lines[].id</c>（床の通過ライン）。空 / 未定義 id は発火しない。
        /// ラインの担当カメラがこの演出の区間のカメラと違う場合も発火しない（区間紐づけ）。
        /// </summary>
        public string lineId = "";

        public string policy = TakeSchema.PolicyHold;       // "hold" | "yield"

        /// <summary>
        /// 画面が塞がっていて出られなかったときの待ちの寿命。"segment"（既定）| "chain"。
        ///
        /// <b>chain = 「自分を塞いでいた演出が終わるまで待つ」</b>。区間を出ても待ち続ける。
        /// 「前の区間の離脱時演出が 8 秒あって、この区間の滞在が 5 秒しかない」ときに、
        /// 著作した演出が黙って消えるのを防ぐためのもの。
        ///
        /// 持ち越すのは**因果がはっきりしている場合だけ** — 離脱の瞬間に実際に別の演出（かライブ卓）が
        /// 画面を持っていて、かつ開始条件は既に満たしていた（Ready だった）演出に限る。
        /// 「歩くのが速くて時刻に届かなかった」ものは持ち越さない（それは ifMissed の担当）。
        /// at=exit には指定できない（離脱時の演出を別の区間で出すと文脈が最も壊れる）。
        /// </summary>
        public string wait = TakeSchema.WaitSegment;

        public bool once = true;                            // ラン内 1 回
        public float maxDurationSec;                        // watchdog。0 / 未指定 = コード既定 45

        /// <summary>
        /// <b>体験者が異変を報告したら、この演出は畳まれて現実（ライブ映像）へ戻るか。</b>
        /// 出どころは <c>canon/LEDGER.md</c> 0050 のユーザー逐語
        /// 「左半分に人形が大量にいて、それを異変だと思って**報告したらそれらが消え**」
        /// 「**推したら乱れたのちに元に戻って**終幕で」。
        ///
        /// ⚠⚠ <b>既定は false（消えない）。</b> JsonUtility は欠落キーを false で埋めるので、
        ///   既存の show.json・端末キャッシュ・焼き込みは**挙動が 1 ビットも変わらない**。
        ///   否定形（<c>noDismiss</c> 等）で持つと、古いデータが全部「消せる」側へ倒れて
        ///   <b>3 周目の録画（作品の核）が押しボタン 1 つで飛ぶ</b>。
        ///
        /// ⚠⚠ <b>旗を立てても消せない演出が 2 つある</b>（データで上書きできない構造ガード）:
        ///   ①現カットが <see cref="TakeSchema.DurUntilMark"/> ＝ 報告はそのカットが消費する（排他）
        ///   ②<c>run.outro.afterTakeId</c> が指す演出 ＝ 畳むと終幕が早撃ちされる
        ///   （<see cref="EndingCueLogic"/> は「指した演出が走らなくなった」で撃つ）。
        ///   どちらも <see cref="TakeRunner.SetTakes"/> / <see cref="TakeRunnerLogic.NotifyMarkPressed"/> が落とす。
        ///
        /// ⚠ <b>報告で現れてよいのは現実だけ。</b> 消えた跡に別の素材を出すと、報告が
        ///   「驚かせる引き金」になる（0050 4 回目「報告ボタンは演出の引き金にはしない。
        ///   驚かせるのは2周目Cの全画面にしよう」に反する）。
        /// </summary>
        public bool dismissible;

        public ShowStepDef[] steps = Array.Empty<ShowStepDef>();

        /// <summary>
        /// 演出中だけの BGM 指示（省略 = 区間で鳴っている曲がそのまま続く）。
        /// 画面と同じ規則で扱う: <b>演出は音も一時的に占有し、終わったら「いまの区間の音」へ戻る</b>。
        /// 型は区間 BGM と同じ <see cref="ShowBgmDef"/>（-1 = トラック既定を継承）。
        /// </summary>
        public ShowBgmDef? bgm;
        public bool hasBgm;                 // present-flag（宣言 bool が正。TimelinePresentFlags 参照）

        /// <summary>「自分を塞いでいた演出が終わるまで、区間を出ても待つ」か。at=exit では常に false。</summary>
        public bool IsChainWait => !IsExit && TakeSchema.IsChainWait(wait);

        public bool IsExit => TakeSchema.IsExit(at);
        /// <summary>開始規則が「このラインを通過したら」か（<see cref="lineId"/> の線分を横切ったら発火）。</summary>
        public bool IsLine => TakeSchema.IsLine(at);
        public bool SkipWhenMissed => TakeSchema.SkipWhenMissed(ifMissed);
        public bool IsYield => TakeSchema.IsYield(policy);
    }

    /// <summary>
    /// 演出の 1 カット。「何を映すか（source）＋ 何を重ねるか（cueId）＋ どれだけ（dur）＋ どう繋ぐか（transition）」。
    /// <c>-1</c> は「<c>cueId</c> の素材定義の値をそのまま使う（継承）」（<see cref="ShowBgmDef"/> と同流儀）。
    /// </summary>
    [Serializable] public sealed class ShowStepDef
    {
        public string source = TakeSchema.SourceLive;   // "live" | "inherit" | "clip" | "still" | "rec" | "plate"
        public int camera = -1;                         // source=live / rec で有効（rec は録画元カメラ）
        public string assetUrl = "";                    // source=clip/still のみ有効（sa:// / slot:// 可）
        public int recLap;                              // source=rec のみ。録画元の周（0 = 未指定 → 飛ばす）

        public string cueId = "";                       // オーバーレイ素材。空 = 重ねない
        public float strength = -1f;                    // 以下 -1 = 素材定義から継承
        public float fadeInSec = -1f;
        public float fadeOutSec = -1f;
        public float trimStartSec = -1f;
        public float trimEndSec = -1f;

        public string durKind = TakeSchema.DurSec;      // "sec" | "untilClipEnd" | "untilZoneChange"
        public float durSec;

        public string transition = TakeSchema.TransDip; // "cut" | "dip" | "fade" | "glitch"
        public float transitionMs;                      // 0 = 種別ごとのコード既定

        // カット頭で 1 回だけ走らせる「映像の乱れ」（企画書 2.3 の注意・移動の誘導）。
        // 遷移の glitch とは別物で、こちらはカットが始まってから単発で走る。0 = 出さない。
        public float glitch;
        public float glitchSec;

        /// <summary>
        /// 画が止まる秒数（0 = 止めない）。ライブも差し替え素材も一緒に凍る。
        /// 止まったのが装置なのか自分なのか一瞬わからない、という間を作る。
        /// 再開したときに自分が予測と違う位置に居ることになるのが効き目。
        /// </summary>
        public float hold;

        /// <summary>
        /// 焼き付きの濃さ 0..1（0 = 出さない）。カット頭の画を薄く残し、数秒かけて消す。
        /// 動いていない画素は同じ値なので何も起きず、**動いたものの跡だけ**が残る。
        /// </summary>
        public float burn;

        /// <summary>焼き付きが消えるまでの秒数（0 = 既定 2.5 秒）。</summary>
        public float burnSec;

        // ---- 左右分割（canon/LEDGER.md 0050。3 周目 A と 4 周目 A だけが使う）--------
        //
        //   カメラ A は L 字経路の手前で**環境が左右対称**なので、画面を縦に割って左右で
        //   別のものを出せる（0050 のユーザーの言葉「カメラAは、L字形路の手前側で、
        //   唯一環境が左右対称です」）。
        //   ⚠ **他のカメラでは使わない。** 対称でないので反転した瞬間に部屋が壊れる。
        //
        //   ⚠⚠ **「境目を幕の合わせ目へ置くと分割線が見えない」は 0050 ではなく
        //     `canon/LEDGER.md` 0113 の実装値**（「幕の折り目が画面のほぼ中央（x≈320）に来る
        //     据え方」）で、**0113 自身が「実装値はシュビーの判断」と明記している** ＝
        //     ユーザーの判定ではない。据え直しで合わせ目が動いたら、動かすのは
        //     この値の側（`splitX`）であって 0050 ではない。

        /// <summary>分割位置 0..1（0 = 分割しない）。0.5 で画面の中央。</summary>
        public float splitX;

        /// <summary>
        /// 左半分だけ左右反転してライブを読む。**差し替え素材（録画・生成画像）は反転しない** —
        /// 反転したまま録画を流すと映像の中の自分が逆走し、境目へ入って消える。
        /// </summary>
        public bool splitFlip;

        /// <summary>左半分だけ凍らせる（そのカットの頭の画で止まる）。</summary>
        public bool splitFreeze;

        // ---- 持続の覆い（`canon/LEDGER.md` 0102。3 周目 A の入りだけが使う）------------
        //
        //   入れ替わりの覆い（芯 ∪ 棒）を**包み切った状態で止めて、波だけ生かす**。
        //   3 周目 A へ入った瞬間から右半分の体験者が黒に包まれていて（2 周目 C で追いつかれた
        //   結果がそのまま続いている）、鏡映しの反対側からリアルタイムの自分が歩いてくる。
        //
        //   ⚠ このカットは <c>cg</c> を指さない。覆いの形を出すために人の代役
        //     （<see cref="TakeSchema.SwapHumanActorId"/>）を<b>実体としては 1 画素も描かず</b>立てる
        //     （<c>_SwapReal</c> = 0）。CG が居ない現場（較正未着・actors[] に visitor が無い）では
        //     覆いが出ないだけで、鏡映し → 凍結 → 録画 の筋はそのまま通る。
        //   ⚠⚠ <b>次のカットの <c>transition:"swap"</c> は、ほどける段を飛ばして縮む段から始まる</b>
        //     （もう包まれているので、ほどき直すと「1 度晴れてからまた包まれる」に見える）。
        //     判断は実機（<see cref="SwapMorphFx"/>）が「いま包んでいるか」で行うので、
        //     データ側で段を指す必要はない。

        /// <summary>
        /// このカットのあいだ、覆いを<b>包み切った状態で保持する</b>（0 → 1 のほどけは走らせない）。
        /// 保持は次のカットへ持ち越され、<c>transition:"swap"</c> のカットが引き継いで縮み → 晴れへ進む。
        /// 引き継がないカットへ移ると覆いは畳まれる。
        /// </summary>
        public bool swapHold;

        /// <summary>
        /// 覆いを効かせる<b>左端</b>（枠 UV 0..1。0 = 制限しない）。
        ///
        /// ⚠⚠ <b>左右分割と併せるときは必ず指す</b>（3 周目 A なら <c>0.5</c>）。覆いの芯は
        ///   <b>合成後の画</b>と無人プレートの差で引くので、制限しないと**左半分の鏡映しの人物や
        ///   録画の人物まで拾って包む**。<see cref="splitX"/> は覆い切った後のカットで 0 へ戻ることが
        ///   あるので、分割の値からは導かない（カットごとに宣言する）。
        /// </summary>
        public float swapMinX;

        /// <summary>
        /// このカットの<b>画の差し替えと同時に、カメラ切替の音を 1 発鳴らす</b>
        /// （`rules/sound-design.md`「カメラ切替の音」と同じ音源）。
        ///
        /// カメラを動かさない素材カット（<c>clip</c> / <c>plate</c>）でも「視点が急に切り替わった」
        /// として鳴らしたいときに使う（2 周目 C の接近 ＝ `canon/LEDGER.md` 0102）。
        /// カメラが実際に変わるカットは <see cref="CameraSwitchDirector"/> が既に鳴らすので、
        /// そこで立てると<b>二重に鳴る</b>。
        /// </summary>
        public bool switchSfx;

        /// <summary>
        /// このカットの<b>画の差し替えと同時に、人形の呼びかけ（「あーそぼー」）を 1 回鳴らす</b>
        /// （<c>canon/LEDGER.md</c> 0109・`rules/sound-design.md`「人形の呼びかけ」）。
        ///
        /// 立てているのは <b>2 周目 C の接近の最後のカット</b>（<c>pov_4</c> ＝ 追いつき）だけ。
        /// 音は <b>1.60 秒</b>あってカット（1.4 秒）より長いが、<b>画は待たない</b> —
        /// はみ出した 0.2 秒は次のカット（ライブ ＋ 持続の覆い）へ被る
        /// （ユーザー指示「映像はこの音を無視してそのまま先に進んで ok」）。
        ///
        /// ⚠ <see cref="switchSfx"/> と併せて立てられる（実際そうなっている）。層が違う音なので
        ///   潰れない — 切替音は装置の音・こちらは部屋で鳴っている声。
        /// ⚠ <b>音源は 1 本</b>（<c>Resources/Sound/sfx_doll_call</c>）。どれを鳴らすかは
        ///   データで指せない。2 本目の声が実在したときに初めて文字列の口を開ける。
        /// </summary>
        public bool dollCall;

        /// <summary>
        /// 第 2 の差し替え層へ出す素材の cue id（空 = 使わない）。
        /// **左と右へ別の素材を同時に置く**ときだけ要る（左＝録画 / 右＝無人プレート、など）。
        /// </summary>
        public string overlay2CueId = "";

        /// <summary>
        /// <b>スクリーンの外の闇で目が開く</b>異変の、開く目の割合 0..1（0 = 出さない）。
        /// 出どころは <c>canon/LEDGER.md</c> 0075。実体は <see cref="AnomalyEyes"/>。
        ///
        /// 進み方（兆し 1.42s → 凝視 0.5s → 開眼 3.0s ＝ 全開まで 4.92s）は
        /// <see cref="AnomalyEyesLogic"/> が持っていて、
        /// <b>ここで指せるのは「出すか」と「何割の目が開くか」だけ</b>。段の尺を著作させないのは、
        /// 凝視の 2.8 秒が<b>体験者が気づいて報告し切る時間</b>そのもので、現場で縮めると
        /// 「1 つの目に見られている」という要の beat が消えるから。
        ///
        /// ⚠ <b>大きい目は割合に関係なく必ず 1 つ出る。</b> 値は残りの目にだけ効く。
        /// ⚠ <b>スクリーンには出ない</b>（描くのは Background+100 ＝ 映像より後ろ）。画面を差し替える
        ///   カットにも、<c>source:"inherit"</c> のカットにも同じように足せる。
        /// ⚠ <b>報告で消したいなら演出の <see cref="ShowTakeDef.dismissible"/> を立てる</b>。
        ///   目そのものは報告を読まない（0075「ボタンはトリガーではなく」）。
        /// </summary>
        public float eyes;

        /// <summary>
        /// <b>目の視界ジャック</b>（<c>canon/LEDGER.md</c> 0099）。目が開いたら視界を乗っ取り、
        /// 当日撮った写真（<c>show.json</c> トップレベル <c>eyejack.photos[]</c>）を
        /// ぱぱぱっと流す。<see cref="eyes"/> が 0 のカットでは意味を持たない。
        ///
        /// 発火・尺・終わりの分岐は <see cref="EyeJackLogic"/> が持ち、**ここで指せるのは
        /// 「やるか」だけ**（当日に触る面を増やさない）。写真が 0 枚なら何も起きない
        /// （目は従来どおり）。既定 false — JsonUtility は欠落キーを false で埋めるので、
        /// 既存の show.json は挙動が 1 ビットも変わらない。
        /// </summary>
        public bool eyeJack;

        /// <summary>
        /// 人形に付き従う劣化の強さ 0..1（0 = 出さない）。人形のまわりだけ画が荒れ、人形が動くと荒れも動く。
        ///
        /// 全域一様な乱れはすべて機材のせいにできるので安全に見える（＝慣れる）。**対象に紐づく
        /// 非一様性**だけが、原因が世界の側にあることを示せる。<c>cg</c> を指すカットでのみ効く。
        /// </summary>
        public float aura;

        // CG レイヤ（映像の上に立つ人形）。空 = 出さない。actors[] の id を指す。
        public string cg = "";
        public string cgMode = TakeSchema.CgFollow;     // "follow"(体験者 XZ に追従) | "fixed"(下の placement)

        // cgMode="fixed" のときの立ち位置。**人形ではなくカットが持つ**のが要点 —
        // actor 側に位置を持たせていた旧設計では「同じ人形を別のカットで別の場所に立たせる」ができず、
        // 人形を複製する羽目になっていた（2026-07-27 設計批評）。hasPlacement が present-flag。
        // 未指定なら actor の fixedX/fixedZ/fixedYawDeg へフォールバックする（後方互換）。
        public ShowPlacementDef? placement;
        public bool hasPlacement;

        public PostParams? post;
        public bool hasPost;

        /// <summary>
        /// <b>このカットから劇伴を差し替える</b>（<c>canon/LEDGER.md</c> 0119）。
        /// 型は区間 BGM と同じ <see cref="ShowBgmDef"/>（-1 = トラック既定を継承）。
        ///
        /// ⚠⚠ <b>演出の <see cref="ShowTakeDef.bgm"/> とは意味が違う。</b>
        ///   あちらは<b>占有</b>（演出のあいだだけ差し替え、終わったら区間の曲へ戻る）。
        ///   こちらは<b>レーンそのものの書き換え</b>で、<b>演出が終わっても区間を移っても鳴り続ける</b>。
        ///   「ここから先はこの曲」と言いたいときに使う口で、戻すのは
        ///   別の指示（区間 / カット / 演出）かランのリセットだけ。
        ///
        /// 立てているのは <b>2 周目 C の接近、呼びかけ（「あーそぼー」）の次のカット</b>だけ
        /// （ユーザー指示「あーそーぼーの後、…クロスフェードで再生を始めてほしい」）。
        /// 区間の縁ではなくカットに載せているのは、<b>差し替えの合図が呼びかけだから</b> —
        /// 区間に載せると 3 周目 A に入るまで鳴り始めない。
        ///
        /// ⚠ 同じトラックを指すカットを 2 度通っても<b>頭出しし直さない</b>
        ///   （<see cref="BgmPlanLogic.Decide"/> が Retune へ倒す）。引き返して演出が
        ///   再演されても曲は続く。
        /// ⚠ 演出が音を占有中（<see cref="ShowTakeDef.bgm"/>）なら鳴らさず<b>戻り先だけ</b>変わる。
        /// </summary>
        public ShowBgmDef? bgm;
        public bool hasBgm;                 // present-flag（宣言 bool が正。TimelinePresentFlags 参照）

        /// <summary>
        /// <c>durKind:"untilLine"</c> のときに待つ床の線（<c>layout.lines[].id</c>）。
        /// **カットの中で位置に反応する唯一の口**で、3 周目 A の凍結点がこれを使う。
        ///
        /// ⚠ 演出の <c>lineId</c>（開始規則）とは別物。演出は「いつ始めるか」、カットは「いつ終えるか」。
        ///   走行中の演出に別の演出は割り込めない（<see cref="TakeRunnerLogic"/> は Ready で待たせる）ので、
        ///   「入った時から画を割っておいて、線を越えた瞬間に凍らせる」は**カットの側でしか書けない**。
        /// </summary>
        public string lineId = "";

        public bool IsUntilClipEnd => TakeSchema.IsUntilClipEnd(durKind);
        public bool IsUntilZoneChange => TakeSchema.IsUntilZoneChange(durKind);
        public bool IsUntilLine => TakeSchema.IsUntilLine(durKind);
        public bool IsUntilMark => TakeSchema.IsUntilMark(durKind);
        public bool HasCg => !string.IsNullOrEmpty(cg);
    }

    /// <summary>CG 人形の立ち位置（course 空間）。カット（<see cref="ShowStepDef"/>）が持つ。</summary>
    [Serializable] public sealed class ShowPlacementDef
    {
        public float x;
        public float z;
        public float yawDeg;   // course +Z を 0 とする向き
    }

    /// <summary>
    /// v3 スキーマの文字列判別子とコード既定値。**未知の値は既定へ倒す**（例外にしない = 既存流儀）。
    /// 判別が既定へ倒れたかは <c>known</c> out 引数で分かるので、呼び出し側が 1 回だけ警告ログを出せる。
    /// 純粋（UnityEngine 非依存）。
    /// </summary>
    public static class TakeSchema
    {
        public const string AtEnter = "enter";
        public const string AtExit = "exit";
        /// <summary>
        /// 「体験者が床の通過ライン（<c>layout.lines</c>）を横切ったら」。時刻ではなく場所の開始規則。
        /// ラインは担当カメラに紐づくので、別の区間で踏んでも発火しない。
        /// </summary>
        public const string AtLine = "line";

        public const string MissedFireOnExit = "fireOnExit";
        public const string MissedSkip = "skip";

        public const string PolicyHold = "hold";
        public const string PolicyYield = "yield";

        /// <summary>待ちは自分の区間まで（既定・従来の挙動）。</summary>
        public const string WaitSegment = "segment";
        /// <summary>自分を塞いでいた演出が終わるまで、区間を出ても待つ。</summary>
        public const string WaitChain = "chain";

        /// <summary>wait 判別子を正規化する。未知は <see cref="WaitSegment"/> へ倒し known=false。</summary>
        public static string NormalizeWait(string? w, out bool known)
        {
            known = w == WaitSegment || w == WaitChain;
            return known ? w! : WaitSegment;
        }

        public static bool IsChainWait(string? w) => w == WaitChain;

        public const string SourceLive = "live";
        public const string SourceInherit = "inherit";
        public const string SourceClip = "clip";
        public const string SourceStill = "still";
        /// <summary>端末内に録っておいた区間の映像（<c>camera</c> + <c>recLap</c> で指す）。</summary>
        public const string SourceRec = "rec";

        /// <summary>
        /// **そのカメラで撮った無人の実写プレート**。誰も居ない部屋だけが映り、その上に CG 人形を重ねられる。
        ///
        /// `still` と絵の出し方は同じだが、**CG 人形を許す点が違う**。一般の素材は「いつどこで撮ったか
        /// 分からない画」なので人形のパースが合わず、重ねると必ず浮く。プレートは `rec` と同じく
        /// **step.camera で撮った画**なので、そのカメラの較正がそのまま効く。
        /// </summary>
        public const string SourcePlate = "plate";

        public const string CgFollow = "follow";
        public const string CgFixed = "fixed";

        /// <summary>ラン中に卓が実体を差し替える素材の URL スキーム（<c>slot://&lt;name&gt;</c>）。</summary>
        public const string SlotScheme = "slot://";

        public const string DurSec = "sec";
        public const string DurUntilClipEnd = "untilClipEnd";

        /// <summary>
        /// 「次にカメラが切り替わるまで」。体験者が次の区間へ移って**ショーの時計**が確定した瞬間に畳む
        /// （<see cref="TakeRunner.NotifyZoneCommitted"/>）。素材の尺に縛られず、その区間に居るあいだ
        /// ずっと出しておける。
        ///
        /// ⚠ 畳むのは **ZoneCommitted（体験者の移動）だけ**。スタッフの手動送り・Web の cameraOverride・
        ///    インサートの画面切替は時計を動かさないので、これらでは終わらない（設計どおり）。
        /// ⚠ 体験者が動かなければ終わらないので、watchdog（<see cref="DefaultMaxDurationSec"/>）が上限を保証する。
        /// </summary>
        public const string DurUntilZoneChange = "untilZoneChange";

        /// <summary>
        /// 「この床の線を横切るまで」（<c>steps[].lineId</c> と対）。体験者が線を越えた瞬間に畳む。
        ///
        /// ⚠ **区間の中で位置を待てる唯一の尺**。演出（Take）の開始規則にも <c>at:"line"</c> があるが、
        ///   走行中の演出へ別の演出は割り込めないので、「区間へ入った時から画を割っておき、
        ///   線を越えた瞬間に次のカットへ移る」はこちらでしか書けない（canon/LEDGER.md 0050）。
        /// ⚠ 体験者が線を越えなければ終わらないので、watchdog（<see cref="DefaultMaxDurationSec"/>）が上限を保証する。
        /// ⚠ 線は担当カメラを持つ。カットのカメラと食い違う線では横断を数えない（演出の <c>at:"line"</c> と同じ規約）。
        /// </summary>
        public const string DurUntilLine = "untilLine";

        /// <summary>
        /// 「体験者が異変を報告するまで」（左 X / Y の 2 秒長押し）。4 周目 A の締めがこれを使う
        /// （canon/LEDGER.md 0050「そこで体験者が報告することで、画像生成人形が消えて実体の人形になり」）。
        ///
        /// ⚠⚠ **報告を体験の進行に使ってよいのはここだけ。** 規約は「報告は進行に 1 ビットも使わない・
        ///   押さなくても同じように進む」（rules/show-design.md）で、それは**驚かせる仕掛けの引き金に
        ///   しない**という意味。締めの 1 か所だけはユーザーが明示的に許可している（0050 4 回目
        ///   「報告ボタンは演出の引き金にはしない。驚かせるのは2周目Cの全画面にしよう」＋
        ///   「4周目のAで…そこで体験者が報告することで」）。
        /// ⚠ **押さなくても必ず終わる。** 押されなければ watchdog（<see cref="DefaultMaxDurationSec"/>）が
        ///   畳むので、押せなかった体験者が置き去りになることはない。尺を持たせるなら
        ///   <c>maxDurationSec</c> をその区間の想定滞在より短く切る。
        /// </summary>
        public const string DurUntilMark = "untilMark";

        public const string TransCut = "cut";
        public const string TransDip = "dip";
        public const string TransFade = "fade";
        /// <summary>
        /// 黒ではなく「映像の乱れ」で継ぎ目を覆う遷移。dip と同じ状態機械（落とし → 差し替え → 立ち上げ）を
        /// 通り、黒の代わりに <see cref="GlitchFx"/> の持続成分を上げ下げする。企画書 2.3 の
        /// 「差し替えの前後に映像の乱れを挿入し、伝送の劣化を装って継ぎ目を隠す」がこれ。
        /// </summary>
        public const string TransGlitch = "glitch";

        /// <summary>
        /// <b>入れ替わりのノイズ</b>（<c>canon/LEDGER.md</c> 0089）。全画面の砂嵐で覆う代わりに、
        /// <b>映像の中の体験者だけ</b>にノイズが湧き、人型に固まって体験者を隠し、そのまま
        /// 人形の大きさへ縮み、晴れると人形になっている。逆向き（人形 → 人）も同じ機構。
        ///
        /// 向きは<b>データで指定しない</b> — 「このカットが人形を出すか（<c>cg</c> が空でないか）」と
        /// 「直前に人形が出ていたか」から導く（<see cref="SwapMorphLogic.Dir"/>）。
        /// 出す ⇒ 人 → 人形 ／ 出さない ⇒ 人形 → 人。前後で同じなら入れ替わりではないので
        /// <see cref="TransGlitch"/> へ倒す（<see cref="TakeRunner"/> が警告を出す）。
        ///
        /// ⚠ 尺は <c>transitionMs</c>（既定 <see cref="DefaultSwapMs"/>）。dip / glitch と違って
        ///   <see cref="SplitTransition"/> の 40:60 では割らない — 段が 3 つあるため
        ///   （湧く → 縮む → 晴れる）配分は <see cref="SwapMorphLogic"/> が持つ。
        /// </summary>
        public const string TransSwap = "swap";

        /// <summary>
        /// 入れ替わりのノイズで<b>「人の側」の姿</b>に使う <c>actors[].id</c>。
        ///
        /// ⚠⚠ <b>人形を人の背丈へ引き伸ばしても人型には見えない</b>（2026-08-19 に絵で確かめた）。
        ///   市松人形は頭が大きいので、1.65m へ拡大すると人ではなく<b>頭の巨大な何か</b>になる。
        ///   ユーザーの言葉は「ノイズが<b>人型</b>になって…その後にノイズの人型が徐々に<b>人形サイズ</b>に
        ///   なり、ノイズが晴れたら<b>人形</b>になる」なので、<b>縮んでいる間の形は人</b>でなければならない。
        ///
        /// ⚠ 無ければ人形を引き伸ばして代用する（警告を 1 回出す）。**入れ替わりごと止めない** —
        ///   形が理想的でないことは、演出が 1 度も出ないことより桁違いに軽い。
        /// </summary>
        public const string SwapHumanActorId = "visitor";

        /// <summary>
        /// 入れ替わりの差分マスク（0095）に使う<b>無人プレート cue の id 規約</b>。
        /// 卓が撮った無人の実写プレートは `plate_A` / `plate_B` … の id で cues[] に居る。
        /// 無ければ CG の形で覆う（SwapMorphFx が警告を出す）。
        /// </summary>
        public static string SwapPlateCueId(string cameraId) => "plate_" + cameraId;

        /// <summary>演出の最大長 (秒)。超えたらランタイムが強制終了する（不変条件 2）。</summary>
        public const float DefaultMaxDurationSec = 45f;

        /// <summary>尺が決まらないカットの最終フォールバック (秒)。</summary>
        public const float FallbackStepDurSec = 4f;

        /// <summary>dip 遷移**全体**の既定 (ms)。落とし + 立ち上げの合計（既存の 70+100ms 相当）。</summary>
        public const float DefaultDipMs = 170f;

        /// <summary>fade 遷移全体の既定 (ms)。</summary>
        public const float DefaultFadeMs = 300f;

        /// <summary>glitch 遷移全体の既定 (ms)。黒より少し長く取らないと「壊れた」に見えない。</summary>
        public const float DefaultGlitchMs = 220f;

        /// <summary>
        /// 入れ替わりのノイズ全体の既定 (ms)。**桁が違う**のは、これが継ぎ目を隠す遷移ではなく
        /// <b>それ自体が見せ場</b>だから（湧く → 縮む → 晴れる の 3 段を読ませる必要がある）。
        /// 2.6 秒は 15fps の映像で各段が 8〜12 コマ乗る長さ。これより短いと縮む段が飛んで見える。
        /// </summary>
        public const float DefaultSwapMs = 2600f;

        /// <summary>glitch 遷移で到達する乱れの強さ。</summary>
        public const float GlitchTransitionLevel = 0.85f;

        /// <summary>
        /// <b>報告で演出を畳むときの遷移の長さ (ms)。</b>カットの遷移（既定 220ms）より長い。
        ///
        /// ⚠ 短いと「消えた」ではなく「切り替わった」に見える。体験者は自分の行為の結果を
        ///   探しているので、乱れが走り切ってから現実が出る間が要る。
        /// ⚠ 逆に長くすると装置が壊れたように見える（乱れは既に <see cref="GlitchTransitionLevel"/>
        ///   ＝ 0.85 まで上がるので、尺で強さを補う必要は無い）。
        /// </summary>
        public const float DismissGlitchMs = 420f;

        /// <summary>遷移全体の長さを「黒へ落とす / 黒から立ち上げる」へ配分する比（既存 70:100 に合わせる）。</summary>
        public const float DipDownRatio = 0.4f;

        /// <summary>遷移全体 (ms) を落とし・立ち上げの秒数へ分ける。cut（0）は 0/0。</summary>
        public static void SplitTransition(float totalMs, out float downSec, out float upSec)
        {
            float total = totalMs > 0f ? totalMs : 0f;
            downSec = total * DipDownRatio / 1000f;
            upSec = total * (1f - DipDownRatio) / 1000f;
        }

        public static bool IsExit(string? at) => at == AtExit;

        public static bool IsLine(string? at) => at == AtLine;

        public static bool SkipWhenMissed(string? ifMissed) => ifMissed == MissedSkip;

        public static bool IsYield(string? policy) => policy == PolicyYield;

        public static bool IsUntilClipEnd(string? durKind) => durKind == DurUntilClipEnd;

        public static bool IsUntilZoneChange(string? durKind) => durKind == DurUntilZoneChange;

        public static bool IsUntilLine(string? durKind) => durKind == DurUntilLine;

        public static bool IsUntilMark(string? durKind) => durKind == DurUntilMark;

        /// <summary>
        /// <b>締めの線</b> ＝ 3 周目 A で左半分が凍る床の線（<c>layout.lines[].id</c>）。
        /// 4 周目 A の③a「止まってください！」は<b>この線を踏んだ瞬間</b>に出る
        /// （2026-09-19・<c>canon/LEDGER.md</c> 0233・ユーザー指定
        /// 「時間指定で 4s ではなく、場所指定にし、その場所を、左右反転の演出のときのフリーズされる位置に」）。
        ///
        /// 台本から導く: <c>splitFreeze</c> のカットの<b>直前まで待っていた <c>untilLine</c> の線</b>。
        /// 凍るのはその線を越えた瞬間なので、「凍った場所」はその線そのもの。
        /// ⚠ <b>show.json に別の口を作らない。</b> 線を 2 か所で指すと、卓で凍結線を据え直したときに
        ///   片方だけ動いて「凍った場所」と「止まれと言われる場所」がずれる。
        /// ⚠ 見つからなければ空 ＝ 締めの線は無い。そのとき③a は時計（<c>CommsCueLogic.HaltAfterClosingSec</c>）
        ///   で出る（退避路。線が解決できないことを黙って③の欠落にしない）。
        /// ⚠ 解析器 <c>analyze-xp-log.py</c> の <c>closing_line_of()</c> が同じ規則で show.json から引く。
        ///   片方だけ変えると <c>ev=config closingLine=</c> の突き合わせが食い違う。
        /// </summary>
        public static string ResolveClosingLineId(ShowTakeDef?[]? takes)
        {
            if (takes == null) return "";
            foreach (ShowTakeDef? t in takes)
            {
                if (t?.steps == null) continue;
                string waiting = "";
                foreach (ShowStepDef? s in t.steps)
                {
                    if (s == null) continue;
                    if (s.IsUntilLine && !string.IsNullOrEmpty(s.lineId)) waiting = s.lineId;
                    if (s.splitFreeze && !string.IsNullOrEmpty(waiting)) return waiting;
                }
            }
            return "";
        }

        /// <summary>source 判別子を正規化する。未知は <see cref="SourceLive"/> へ倒し known=false。</summary>
        public static string NormalizeSource(string? source, out bool known)
        {
            known = source == SourceLive || source == SourceInherit
                    || source == SourceClip || source == SourceStill || source == SourceRec
                    || source == SourcePlate;
            return known ? source! : SourceLive;
        }

        /// <summary>cgMode 判別子を正規化する。未知は <see cref="CgFollow"/> へ倒し known=false。</summary>
        public static string NormalizeCgMode(string? mode, out bool known)
        {
            known = mode == CgFollow || mode == CgFixed;
            return known ? mode! : CgFollow;
        }

        /// <summary>「このカットは映像素材（動画 / 静止画 / プレート）を全面に出すか」＝ cue が要るか。</summary>
        public static bool IsAssetSource(string? source)
            => source == SourceClip || source == SourceStill || source == SourcePlate;

        /// <summary>
        /// 「この画は step.camera の構図そのものか」＝ CG 人形を重ねてパースが合うか。
        /// 端末内録画とプレートだけが該当する（どちらもそのカメラで撮っている）。
        /// </summary>
        public static bool MatchesCameraPerspective(string? source)
            => source == SourceRec || source == SourcePlate;

        /// <summary>「このカットは端末内録画を映すか」。</summary>
        public static bool IsRecSource(string? source) => source == SourceRec;

        /// <summary>素材スロット URL（<c>slot://name</c>）ならスロット名を返す。違えば空文字。</summary>
        public static string SlotName(string? url)
            => !string.IsNullOrEmpty(url) && url!.StartsWith(SlotScheme, StringComparison.Ordinal)
                ? url.Substring(SlotScheme.Length)
                : "";

        /// <summary>watchdog の上限を解決する（0 / 負値 = コード既定）。</summary>
        public static float ResolveMaxDuration(float v)
            => v > 0f ? v : DefaultMaxDurationSec;

        /// <summary>遷移時間 (ms) を解決する（0 / 負値 = 種別ごとのコード既定・cut は常に 0）。</summary>
        public static float ResolveTransitionMs(string? transition, float ms)
        {
            if (transition == TransCut) return 0f;
            if (ms > 0f) return ms;
            if (transition == TransFade) return DefaultFadeMs;
            if (transition == TransSwap) return DefaultSwapMs;
            return transition == TransGlitch ? DefaultGlitchMs : DefaultDipMs;
        }

        /// <summary>遷移の見た目が「黒」ではなく「乱れ」か。</summary>
        public static bool IsGlitchTransition(string? transition) => transition == TransGlitch;

        /// <summary>遷移が「入れ替わりのノイズ」か（<see cref="TransSwap"/>）。</summary>
        public static bool IsSwapTransition(string? transition) => transition == TransSwap;

        /// <summary>
        /// 素材定義の値と step の上書きを合成する（<c>-1</c> = 継承）。
        /// </summary>
        public static float Inherit(float stepValue, float cueValue)
            => stepValue < 0f ? cueValue : stepValue;
    }
}
