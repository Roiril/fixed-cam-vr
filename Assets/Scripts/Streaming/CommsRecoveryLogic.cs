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
    /// <item><b>弱った起動</b>（0〜<see cref="BootSec"/>）— 横線から縦に開き、半分で 1 度引っかかる。
    ///   電力は段で上がる（ホールドフレーム・滑らかに上げない）。帯が飛び、抜け、面が横へ引っかかる。
    ///   字が浮かび始める前に終わる。</item>
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
        // ---- 弱った起動 ------------------------------------------------------------
        /// <summary>弱った起動の長さ (秒)。開く 0.45 にちょうど重なり、字が浮かび始める前に終わる。</summary>
        public const float BootSec = 0.45f;

        /// <summary>
        /// 起動の段（区間の始まり秒, 電力, 縦の開き, 横の引っかかり m）。<b>段の中は動かさない</b>
        /// （ホールドフレーム）。最後の段のあと <see cref="BootSec"/> まで 1 へ寄る。電力は段ごとに上がる。
        /// </summary>
        private static readonly float[] BootKeys =
        {
            // start, power, squash, jitterX
            0.00f, 0.30f, 0.06f, -0.004f,   // 横線で点く
            0.08f, 0.55f, 0.45f,  0.006f,   // 半分まで開いて引っかかる
            0.22f, 0.80f, 0.93f, -0.002f,   // ほぼ開く
        };

        /// <summary>最後の段が 1 へ寄り始める秒。</summary>
        public const float SettleStartSec = 0.32f;

        /// <summary>起動の乱れの強さ（頭）。乗っ取りの頭打ち以下。</summary>
        public const float BootTearPeak = 0.55f;

        /// <summary>起動の乱れが減り始める秒。</summary>
        public const float BootTearHoldSec = 0.22f;

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
                const int stride = 4;
                int last = BootKeys.Length / stride - 1;
                int seg = 0;
                for (int i = 0; i <= last; i++)
                    if (sec >= BootKeys[i * stride]) seg = i;
                s.power = BootKeys[seg * stride + 1];
                s.squash = BootKeys[seg * stride + 2];
                s.jitterX = BootKeys[seg * stride + 3];
                if (seg == last && sec >= SettleStartSec)
                {
                    // 最後の段だけは 1 へ寄る（セトリング）。ease-out で、行き過ぎない（弱っているので跳ねない）。
                    float k = EaseOut((sec - SettleStartSec) / (BootSec - SettleStartSec));
                    s.power = Lerp(s.power, 1f, k);
                    s.squash = Lerp(s.squash, 1f, k);
                    s.jitterX = Lerp(s.jitterX, 0f, k);
                }
                s.tear = sec < BootTearHoldSec
                    ? BootTearPeak
                    : Lerp(BootTearPeak, 0f, (sec - BootTearHoldSec) / (BootSec - BootTearHoldSec));
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
