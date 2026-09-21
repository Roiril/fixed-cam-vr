// 連絡の面の呪いの斑（まだら）の場。
//
// 顔（CommsAvatar.shader）・地と走り書き（CommsPanelPlate.shader）・文字の切断（地のステンシル）が
// **全部この 1 つの場を読む**。面のローカル座標 (m・根の倍率を掛ける前) で解くので、
// 顔の枠の中と地の上で斑が途切れず、同じ塊として繋がって見える。
//
// ⚠⚠ C# の写し `CommsCurseLogic.Field` と同じ式でなければならない。片方だけ直すと、
//    テレメトリの「切られた字数」（commsCx）が画と食い違う。数値は両方に書いてある。
// ⚠ 実行時に乱数を振らない（`canon/LEDGER.md` 0073 — ちらつくと侵食ではなくノイズに見える）。
#ifndef FIXEDCAMVR_COMMS_CURSE_INCLUDED
#define FIXEDCAMVR_COMMS_CURSE_INCLUDED

// 斑の基本セル (m)。細かいオクターブはこの半分。
static const float CURSE_CELL_M = 0.05;
// 細かいオクターブの重み。
static const float CURSE_DETAIL_K = 0.35;
// 境目の柔らかさ（斑の閾値に対する幅）。
static const float CURSE_SOFT = 0.12;
// 文字を切る閾値（この k 以上の画素にステンシルを書く）。
static const float CURSE_CUT = 0.5;

float CurseHash21(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float CurseValueNoise(float2 uv)
{
    float2 i = floor(uv);
    float2 f = frac(uv);
    f = f * f * (3.0 - 2.0 * f);
    float a = CurseHash21(i);
    float b = CurseHash21(i + float2(1.0, 0.0));
    float c = CurseHash21(i + float2(0.0, 1.0));
    float d = CurseHash21(i + float2(1.0, 1.0));
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

// 面のローカル座標 p (m) → 0..1 の場。
float CurseField(float2 p)
{
    float n1 = CurseValueNoise(p / CURSE_CELL_M + float2(3.7, 1.9));
    float n2 = CurseValueNoise(p / (CURSE_CELL_M * 0.5) + float2(11.3, 7.1));
    return (n1 + CURSE_DETAIL_K * n2) / (1.0 + CURSE_DETAIL_K);
}

// 斑の量 mask (0..1) に対する、その画素の「呪われている度」0..1。
// mask = 1 なら全画素 1、0 なら全画素 0。境目だけが CURSE_SOFT の幅で柔らかい。
float CurseK(float field, float mask)
{
    return saturate((mask * (1.0 + CURSE_SOFT) - field) / CURSE_SOFT);
}

// ---- 左から右への塗り替わり（憑依の出し方）----
// sweep = (進み 0..1, 矩形の左端 x, 矩形の右端 x, 境界の幅 m)。
// C# の本文クリップと顔と地が同じ panel-local x を読む。

// tearGrid = (乱れの強さ, 明滅, 矩形の上端 y, 帯の高さ)。
// `_Sweep.yz` は左右の座標なので、縦帯の座標として再利用しない。
// 上端から数えた帯番号（0 が最上段。上端より上は 0 に寄せる）。
float CommsBandOf(float y, float4 tearGrid)
{
    return max(floor((tearGrid.z - y) / max(tearGrid.w, 1e-4)), 0.0);
}

// その画素の「塗り替わった度」0 / 1。反転した帯なら 1。
float CurseSweepK(float2 p, float4 sweep)
{
    if (sweep.x <= 0.0) return 0.0;
    if (sweep.x >= 1.0) return 1.0;
    float front = lerp(sweep.y, sweep.z, saturate(sweep.x));
    return step(p.x, front);
}

// ---- 連絡の面の乱れ（`canon/LEDGER.md` 0231）----
//
// ユーザー指定「メインスクリーンをまねした、エージェントスクリーン用の乱れ演出を作成してそれを使う」。
// 本編（`ScreenComposite.shader` の `GlitchUv` と post の乱れ）の成分を、この面の寸法へ移したもの:
//   1. 帯ごとの水平飛ばし（約 18Hz で組み替わる。強いほど多くの帯が飛ぶ）
//   2. 帯ごとの脱落（本編の「帯が砂になる」に当たる。⚠ この面は黒地に象牙の 2 色なので、灰色の砂を
//      混ぜると明るい板が出る（0011 / 0018）。**抜ける**方へ倒す）
//   3. 全体の明滅（約 42Hz・暗い側へだけ）
// ⚠⚠ **帯ごとの値は C# が 1 か所で作り、uniform で渡す**（`CommsCurseLogic.ComputeTear`）。
//    本編と違いこの面は 1 枚のテクスチャではなく、地・顔・文字（CPU）・ステンシルが別々の層なので、
//    HLSL に第二の実装を置くと必ずどれかが食い違う。ここは受け取った値を引くだけ。
// ⚠ 帯は塗り替わりと同じ格子（上端から sweep.w ごと）。帯は 8 本まで（面の丈 0.295m ÷ 0.04m ＝ 7.4）。
// ⚠ 本編にある全体の垂直の同期ずれは入れていない（この面では 0.15° ＝ 見えない。設計批評 2026-09-18）。

// 8 本の帯の値（a = 帯 0..3 / b = 帯 4..7）から、帯番号 i の値を分岐なしで引く。
float CommsPick8(float4 a, float4 b, float i)
{
    float4 ma = step(abs(float4(0.0, 1.0, 2.0, 3.0) - i), 0.5);
    float4 mb = step(abs(float4(4.0, 5.0, 6.0, 7.0) - i), 0.5);
    return dot(a, ma) + dot(b, mb);
}

// その y の帯の**中身が右へ動く量**（m）。中身をサンプルするときは引く（p.x - shift）。
float CommsTearShiftAt(float y, float4 tearGrid, float4 shiftA, float4 shiftB)
{
    return CommsPick8(shiftA, shiftB, CommsBandOf(y, tearGrid));
}

// その y の帯の脱落（0..1）。alpha に (1 - これ) を掛ける。
float CommsTearDropAt(float y, float4 tearGrid, float4 dropA, float4 dropB)
{
    return CommsPick8(dropA, dropB, CommsBandOf(y, tearGrid));
}

#endif
