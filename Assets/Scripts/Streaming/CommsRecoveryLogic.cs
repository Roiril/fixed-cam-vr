#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>「復帰」の姿の 1 コマ（すべて 0..1 か m）。</summary>
    public struct CommsRecoverySample
    {
        /// <summary>復帰の時計が回っているか（③a〜③b と、報告で残りが消えていくあいだ）。</summary>
        public bool active;
        /// <summary>面に届いている電力 0..1。地・顔・文字の濃さに掛ける。<b>上がる方向にしか動かない</b>。</summary>
        public float power;
        /// <summary>面の縦の開き 0..1（1 = 正しい高さ）。電源を入れた管が横線から開く形。</summary>
        public float squash;
        /// <summary>帯の乱れの強さ 0..1（乗っ取りと同じ帯の飛び・脱落・<c>CommsCurseLogic.ComputeTear</c>）。</summary>
        public float tear;
        /// <summary>面全体の横の引っかかり (m・面のローカル)。</summary>
        public float jitterX;
        /// <summary>スイの顔のうち、まだ人形のまま残っている割合 0..1。</summary>
        public float residue;
        /// <summary>文字に赤が残る量 0..1（<see cref="CommsRecoveryLogic.RedOf"/> の閾値）。顔の残りと同じ 1 本の曲線から決まる。</summary>
        public float red;
        /// <summary>弱った起動の最中か（<see cref="CommsRecoveryLogic.BootSec"/> まで）。</summary>
        public bool booting;
        /// <summary>
        /// 面のうち、まだ中身として現れていない量 0..1（0 = 通常の面と 1 画素も変わらない）。
        /// 0.5 を超える分はブロックがまだ点いていない、0.5 以下は点いたブロックが砂嵐のまま（<c>CommsRecovery.hlsl</c>）。
        /// </summary>
        public float boot;
        /// <summary>余震の最中か。</summary>
        public bool aftershock;
    }

    /// <summary>
    /// 4 周目 A の③a「止まってください！」→③b の、<b>呪いから何とか戻ってきた姿</b>。UnityEngine 非依存。
    ///
    /// 出どころは <c>canon/LEDGER.md</c> 0270（ユーザー逐語「呪われていた状況から何とか戻ってきた感を
    /// 出すために、スクリーンの出現のさせ方を、ざざっと、弱ってる感じにしたり、スイが半分はまだ侵食されていて
    /// 文字も赤いところが残っているなどの演出を考えてこだわってほしい」）。
    /// キーフレームの値と形はシュビーの案（<c>canon/OPEN.md</c>）。設計批評（2026-10-17）を通してある。
    ///
    /// <b>時計は 1 本</b>。③a を組んだ縁で 0 から数え、同じ面のまま③b へ繋いでも数え直さない
    /// （<c>CommsPanelLogic.RecoverySec</c>）。段の尺（開く 0.45 / 浮かぶ 0.25 / 読ませる 1.1 …）には
    /// 1 ミリも触らない — この姿はその上に重ねるだけ。
    ///
    /// 形は 4 つ:
    /// <list type="number">
    /// <item><b>弱った起動</b>（0〜<see cref="BootSec"/>）— 博士タブレットで通信が切れる絵（<c>signal-cut.js</c>）の逆再生。
    ///   途切れた横線で点き、縦へ開くと面は砂嵐のブロックで埋まり、砂嵐がざ・ざ・ざと引きながら
    ///   ブロックごとに中身（地・顔・字）へ復号される（<c>canon/LEDGER.md</c> 0280「全部一気に出すのではなく、
    ///   グリッジノイズ的に、少しずつ」）。復号は段で進む（ホールドフレーム）。字が浮かぶ段にも重なる。</item>
    /// <item><b>余震</b> — ③b を打っている途中に 2 回だけ、帯の乱れが走り、人形が少し押し返してくる。</item>
    /// <item><b>残り</b> — 顔全体の粗い斑で人形とスイが約半々に混在し、脅威を言う句に赤が残る。</item>
    /// <item><b>解ける</b> — 体験者の報告が通った縁（呪いが解けた・0129）から <see cref="ReleaseSec"/> で 0 へ。</item>
    /// </list>
    ///
    /// ⚠⚠ <b>明るさを落とさない。</b> 電力は上がる方向にしか動かない。落とすと 3〜30Hz の明滅になる
    /// （光過敏の危険帯。終幕のちらつきを 0111 で捨てた理由と同じ）。「弱っている」は帯の乱れ・横の引っかかり・
    /// 開きの引っかかりで出す。
    /// ⚠ 乱れの強さは乗っ取り（<see cref="CommsPossessionLogic.TearPeak"/>）より上にしない — 乗っ取りより
    /// 激しいと「もっと壊れた」に読める。
    /// ⚠ 乱数を使わない。同じ時刻は同じ絵。
    /// </summary>
    public static class CommsRecoveryLogic
    {
        // ---- 弱った起動（0280: 砂嵐からブロックごとに復号される）------------------
        /// <summary>途切れた横線で点いている長さ (秒)。博士タブレットの「線が点へ縮む」の逆。</summary>
        public const float LineSec = 0.05f;

        /// <summary>横線が縦へ開き切るまで (秒)。開いた面は砂嵐のブロックで埋まる。</summary>
        public const float ExpandSec = 0.12f;

        /// <summary>砂嵐が引いて中身へ復号されるまで (秒)。博士タブレットの砂嵐（0.95 秒）を逆に回す。</summary>
        public const float DecodeSec = 0.75f;

        /// <summary>
        /// 弱った起動の長さ (秒)。開く 0.45 と字が浮かぶ 0.25 を越えて、読ませる段の頭まで掛かる
        /// （段の尺は変えない。字も同じブロックで復号されるので、浮かんだ字は砂嵐の切れ目から読めてくる）。
        /// </summary>
        public const float BootSec = LineSec + ExpandSec + DecodeSec;

        /// <summary>復号はこの刻みで段になって進む（ホールドフレーム。滑らかに晴らさない）。</summary>
        public const float BootHoldHz = 24f;

        /// <summary>横線と開く途中で、まだ点いていないブロックの量（<see cref="CommsRecoverySample.boot"/>）。</summary>
        public const float BootLineGap = 0.78f;

        /// <summary>
        /// 砂嵐がもう一度強まる区間（復号の進み 0..1）。博士タブレットの ざ・ざ・ざ（<c>signal-cut.js</c> の BURSTS）を
        /// 逆に並べたもの。強まる方向なので明るさは落ちない（砂嵐は地より明るい）。
        /// </summary>
        private static readonly float[] DecodeBursts = { 0.15f, 0.21f, 0.29f, 0.35f, 0.42f, 0.50f };

        /// <summary>起動の乱れの強さ（頭）。乗っ取りの頭打ち以下。</summary>
        public const float BootTearPeak = 0.55f;

        /// <summary>起動の横の引っかかりの最大 (m)。</summary>
        public const float BootJitterM = 0.006f;

        // ---- 余震 ------------------------------------------------------------------
        /// <summary>
        /// 余震を起こす③b の打鍵の進み。<b>③b の段に結ぶ</b>（絶対秒にすると言語で打つ尺が違う）。
        /// 読ませる段（Hold）には入れない — Hold は人形 End の時計が数え始める段。
        /// </summary>
        public static readonly float[] AftershockAtReveal = { 0.35f, 0.75f };

        /// <summary>余震の長さ (秒)。</summary>
        public const float AftershockSec = 0.14f;

        /// <summary>余震の乱れの強さ。</summary>
        public const float AftershockTear = 0.35f;

        /// <summary>余震のあと、押し返された量が戻るまで (秒)。</summary>
        public const float AftershockRecoverSec = 0.45f;

        /// <summary>余震で人形が押し返してくる量（残りへ足す）。</summary>
        public const float AftershockResiduePush = 0.10f;

        // ---- 残り ------------------------------------------------------------------
        /// <summary>顔に残る人形の割合（起動の頭 → ③a の終わり → 落ち着いた先）。</summary>
        public const float ResidueStart = 0.60f, ResidueHalt = 0.52f, ResidueFloor = 0.50f;

        /// <summary>
        /// 文字の赤 = 顔の残り × これ。<b>顔と文字は同じ 1 本の量を読む</b>（0229 の原則 — 別の曲線にすると
        /// 顔と文字が別々の速さで治って見える）。
        /// </summary>
        public const float RedPerResidue = 0.55f;

        /// <summary>③a が画から退く秒（開く 0.45 ＋ 浮かぶ 0.25 ＋ 読ませる 1.1）。</summary>
        public const float HaltEndSec = 1.80f;

        /// <summary>残りが落ち着く秒（③b の読ませる段の頭あたり。日本語で 1.8 ＋ 差し替え 0.35 ＋ 打つ 2.5）。</summary>
        public const float SettleEndSec = 4.6f;

        /// <summary>③a の字の赤が焼け落ちるまで (秒)。命令は読める状態で残す — 赤は起動の間だけ字の縁に残る。</summary>
        public const float HaltRedBurnSec = 1.0f;

        /// <summary>報告が通って（呪いが解けて）から残りが 0 になるまで (秒)。<c>ScreenDecayLogic.ReleaseSec</c> と同じ。</summary>
        public const float ReleaseSec = ScreenDecayLogic.ReleaseSec;

        /// <summary>時計が回っていないときの姿（通常の面と同じ）。</summary>
        public static CommsRecoverySample Idle => new CommsRecoverySample
        {
            active = false, power = 1f, squash = 1f, tear = 0f, jitterX = 0f, residue = 0f, red = 0f,
        };

        /// <summary>
        /// 1 コマぶんの姿。
        /// </summary>
        /// <param name="sec">③a を組んでからの秒。負なら <see cref="Idle"/>。</param>
        /// <param name="promptReveal">③b の打鍵の進み 0..1（③b を打っていなければ負）。</param>
        /// <param name="promptTypeSec">③b を打つ尺 (秒)。</param>
        /// <param name="releasedSec">報告が通ってからの秒（通っていなければ負）。</param>
        public static CommsRecoverySample Sample(float sec, float promptReveal = -1f, float promptTypeSec = 0f,
                                                 float releasedSec = -1f)
        {
            if (sec < 0f || float.IsNaN(sec)) return Idle;
            var s = new CommsRecoverySample { active = true, power = 1f, squash = 1f };

            // ---- 弱った起動 ----
            if (sec < BootSec)
            {
                s.booting = true;
                // 段の刻み。横の引っかかりと復号の進みはこの刻みの中では動かさない。
                float step = MathF.Floor(sec * BootHoldHz);
                float jitter = (Hash(1000 + (int)step) - 0.5f) * 2f * BootJitterM;
                if (sec < LineSec)
                {
                    // 途切れた横線で点く（まだ点いていないブロックがあるので線は切れ切れ）。
                    s.power = 0.30f;
                    s.squash = 0.03f;
                    s.boot = BootLineGap;
                    s.tear = BootTearPeak;
                    s.jitterX = jitter;
                }
                else if (sec < LineSec + ExpandSec)
                {
                    // 縦へ開く。開くほどブロックが点いて、面が砂嵐で埋まる。
                    float u = (sec - LineSec) / ExpandSec;
                    float uq = MathF.Floor(u * ExpandSec * BootHoldHz + 1f) / (ExpandSec * BootHoldHz);
                    s.power = 0.55f;
                    s.squash = 0.03f + 0.97f * (1f - Cube(1f - u));
                    s.boot = Lerp(BootLineGap, 0.5f, uq);
                    s.tear = BootTearPeak;
                    s.jitterX = jitter;
                }
                else
                {
                    // 砂嵐が引いていく。復号の進みは段で刻み、ざ・ざ・ざで一瞬だけ砂嵐が戻る。
                    float d = (sec - LineSec - ExpandSec) / DecodeSec;
                    float dq = MathF.Floor(d * DecodeSec * BootHoldHz) / (DecodeSec * BootHoldHz);
                    float noise = DecodeNoise(dq);
                    s.power = 0.80f + 0.20f * Clamp01(d);
                    s.squash = 1f;
                    s.boot = 0.5f * noise;
                    s.tear = BootTearPeak * noise;
                    s.jitterX = jitter * noise;
                }
            }

            // ---- 残り（起動の頭 → ③a の終わり → 落ち着いた先）----
            float residue;
            if (sec < HaltEndSec)
                residue = Lerp(ResidueStart, ResidueHalt, EaseOut(sec / HaltEndSec));
            else
                residue = Lerp(ResidueHalt, ResidueFloor, Smooth((sec - HaltEndSec) / (SettleEndSec - HaltEndSec)));

            // ---- 余震（③b の打鍵に結ぶ）----
            float push = 0f;
            if (promptReveal >= 0f && promptTypeSec > 0f)
            {
                for (int i = 0; i < AftershockAtReveal.Length; i++)
                {
                    float t = (promptReveal - AftershockAtReveal[i]) * promptTypeSec;
                    if (t < 0f) continue;
                    if (t < AftershockSec)
                    {
                        s.aftershock = true;
                        s.tear = Math.Max(s.tear, AftershockTear);
                        s.jitterX = 0.003f;
                        push = Math.Max(push, 1f);
                    }
                    else
                    {
                        push = Math.Max(push, 1f - EaseOut((t - AftershockSec) / AftershockRecoverSec));
                    }
                }
            }
            residue = Clamp01(residue + AftershockResiduePush * push);

            // ---- 解ける ----
            if (releasedSec >= 0f)
            {
                float k = 1f - Smooth(releasedSec / ReleaseSec);
                residue *= k;
                if (releasedSec >= 0f) { s.tear = 0f; s.jitterX = 0f; s.aftershock = false; }
            }
            s.residue = residue;
            s.red = residue * RedPerResidue;
            return s;
        }

        /// <summary>
        /// 字 <paramref name="index"/> に残る赤 0..1。<paramref name="red"/> は <see cref="CommsRecoverySample.red"/>。
        /// 字ごとの決定的なハッシュが閾値を下回った分だけ赤い。<b>連続量</b>なので、閾値が下がると
        /// 字ごとにぱちっと切り替わらず、赤が引いていく。
        /// ⚠ どの字に赤を許すかは呼び手が決める（<see cref="RedAllowed"/>）。
        /// </summary>
        public static float RedOf(int index, float red)
        {
            if (red <= 0f) return 0f;
            float h = Hash(index);
            return RedMax * Clamp01((red - h) / RedSoft);
        }

        /// <summary>
        /// 1 字に残る赤の上限。<b>字全体を赤くしない</b> — 0.8 なら <see cref="CornerRed"/> の遠い側は 0.5 で止まり、
        /// 赤は字の片側から反対側へ向かって薄れる形になる（全部赤い字は人形の嘘の一文と同じ姿になる）。
        /// </summary>
        public const float RedMax = 0.8f;

        /// <summary>赤が引いていく幅（ハッシュの単位）。</summary>
        public const float RedSoft = 0.10f;

        /// <summary>
        /// その字に赤を残してよいか（0270・設計批評）。⚠⚠ <b>命令の句（止まって・排除して）は赤くしない。</b>
        /// 赤は 3 周目で「人形の嘘」の色として教えた色なので、命令に載ると警告そのものが罠に読める。
        /// <list type="bullet">
        /// <item>③b — 脅威を言う句（最後の行より前）だけ。最後の行（排除してください / Remove it）は象牙。</item>
        /// <item>③a — 起動の間だけ字の縁に残り、<see cref="HaltRedBurnSec"/> で焼け落ちる（命令は読める状態で残す）。</item>
        /// </list>
        /// </summary>
        public static bool RedAllowed(bool isPrompt, int lineNumber, int lastLine)
            => isPrompt ? lineNumber < lastLine : true;

        /// <summary>
        /// ③a の字に残す赤の上限（焼け落ちる）。③b は 1。<paramref name="sec"/> は復帰の時計。
        /// ③a の赤は<b>字の片側だけ</b>に留める（0.6 未満 ＝ <see cref="CornerRed"/> の遠い側は象牙のまま）。
        /// </summary>
        public static float HaltRedCap(float sec) => 0.55f * (1f - Smooth(sec / HaltRedBurnSec));

        /// <summary>
        /// 字のどちら側から赤が残るか（0 = 左 / 1 = 上 / 2 = 右 / 3 = 下）。<b>字の全部を赤くしない</b> —
        /// 赤は字の片側だけに残り、反対側へ向かって引いていく。
        /// </summary>
        public static int RedSideOf(int index) => (int)(Hash(index + 101) * 4f) & 3;

        /// <summary>
        /// 字の 4 隅（TMP の頂点順: 0 = 左下 / 1 = 左上 / 2 = 右上 / 3 = 右下）ごとの赤の重み 0..1。
        /// <paramref name="amount"/> が 1 に近いほど字全体が赤くなる。
        /// </summary>
        public static float CornerRed(int side, int corner, float amount)
        {
            if (amount <= 0f) return 0f;
            bool near = side switch
            {
                0 => corner == 0 || corner == 1,
                1 => corner == 1 || corner == 2,
                2 => corner == 2 || corner == 3,
                _ => corner == 0 || corner == 3,
            };
            // 近い側は量そのまま。遠い側は量が 0.6 を超えてから追いつく（字全体が赤いのは量の大きい字だけ）。
            return near ? amount : Clamp01((amount - 0.6f) / 0.4f);
        }

        /// <summary>
        /// 復号の進み <paramref name="d"/>（0..1）での砂嵐の強さ 0..1。博士タブレットの砂嵐の強まり方
        /// （<c>signal-cut.js</c> の <c>intensity</c>）を時間を逆にして読む。頭の 12% は砂嵐だけ。
        /// </summary>
        public static float DecodeNoise(float d)
        {
            d = Clamp01(d);
            if (d >= 1f) return 0f;
            if (d < 0.12f) return 1f;
            float p = 1f - d;
            float n = MathF.Pow(p, 1.7f) * 0.75f;
            for (int i = 0; i + 1 < DecodeBursts.Length; i += 2)
                if (d >= DecodeBursts[i] && d < DecodeBursts[i + 1]) n += 0.38f;
            return Clamp01(n);
        }

        private static float Cube(float v) => v * v * v;

        private static float Hash(int i)
        {
            uint x = unchecked((uint)i * 747796405u + 2891336453u);
            x = ((x >> (int)((x >> 28) + 4)) ^ x) * 277803737u;
            x = (x >> 22) ^ x;
            return (x & 0xFFFFFF) / 16777216f;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
        private static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        private static float EaseOut(float t) { t = Clamp01(t); float u = 1f - t; return 1f - u * u * u; }
        private static float Smooth(float t) { t = Clamp01(t); return t * t * (3f - 2f * t); }
    }
}
