#nullable enable
using UnityEngine;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// <b>HMD の中に出す文字の唯一の正。</b> 大きさ・色・段の定義がここにしか無い状態を保つ。
    /// 題字（<c>TitleScreen</c>）だけは別の造形なので対象外。
    ///
    /// ⚠⚠ <b>面ごとに数字を決めない。</b> 2026-08-15 まで 7 面が
    /// それぞれ勝手な <c>fontSize</c> を持っていて、1 文字の見かけ角が
    /// <b>0.18°〜2.67° と 15 倍ばらついていた</b>（うち 1 面は実機で読めない大きさだった）。
    /// 原因は単位系が 2 つあること — 面を作った人が使った系の直感で数字を決めていた。
    ///
    /// <list type="bullet">
    /// <item>Canvas 系（<c>TextMeshProUGUI</c> ＋ WorldSpace Canvas）: 1 文字 ＝ <c>fontSize × canvasScale</c></item>
    /// <item>3D 系（<c>TextMeshPro</c>）: 1 文字 ＝ <c>fontSize × 0.1 × localScale</c>
    ///       （TMP は透視カメラのとき内部で 0.1 を掛ける）</item>
    /// </list>
    ///
    /// <b>決めるのは「役割」と「距離」だけで、fontSize は逆算する。</b>
    /// <see cref="CanvasFontSize"/> / <see cref="MeshScale"/> がその 2 系統を吸収する。
    ///
    /// ⚠ 面の数値を変えたら <c>.\tools\unity.ps1 menu text-audit</c> を通す
    /// （全面の実測見かけ角と、最長行が面からはみ出していないかを機械で見る）。
    /// </summary>
    public static class HmdTextStyle
    {
        // ---- 段（1 文字の見かけ角・度）------------------------------------------------
        //
        // ⚠ 既定は本文 1 つだけ。補助と注目は**例外 2 か所のためにしか存在しない**
        //   （報告の面のラベル / 黒の上の 1 行）。段を増やすと微妙な差が再発する。

        /// <summary>補助 — 主役の脇に小さく添える文字。<b>報告の面の「報告中」だけ</b>。</summary>
        public const float MinorDeg = 1.5f;

        /// <summary>本文 — <b>既定</b>。とくに理由が無ければこれ。</summary>
        public const float BodyDeg = 1.8f;

        /// <summary>注目 — 黒の中に 1 行だけ出て、見落とすと詰むもの。<b>中止の 1 行だけ</b>。</summary>
        public const float AlertDeg = 2.2f;

        /// <summary>補助が本文の何割か（TMP の <c>&lt;size=NN%&gt;</c> に渡す）。</summary>
        public static float MinorPercent => MinorDeg / BodyDeg * 100f;

        // ---- 色 ---------------------------------------------------------------------
        //
        // ⚠⚠ **2 色しか無い。** 2026-08-15 まで地の色が 4 種・警告色が 3 種あった。
        //   読み手（体験者 / スタッフ）で色を分けない — 分けると「別の装置が 2 台ある」ように
        //   見える。分かれているのは**出る場所**と**門**（`StatusHud.StaffViewing`）で足りる。

        /// <summary>地の色。装置が喋るときの声。純白にしないのは、黒の中で掲示物に見えるため。</summary>
        public static readonly Color Ink = new Color(0.82f, 0.78f, 0.72f, 1f);

        /// <summary>警告。対応が要る 1 行にだけ使う。</summary>
        public static readonly Color Alert = new Color(1.00f, 0.55f, 0.40f, 1f);

        /// <summary>ゲージの空き。<see cref="Ink"/> を落としたもの（別の色相を作らない）。</summary>
        public const float DimFactor = 0.30f;

        /// <summary>ゲージの空きの色。</summary>
        public static Color InkDim => new Color(Ink.r * DimFactor, Ink.g * DimFactor, Ink.b * DimFactor, 1f);

        // TMP のリッチテキスト（`<color=#...>`）用。⚠ 上の Color と必ず一致させる
        //    （`HmdTextStyleTests.Hex_MatchesColor` が固定する）。
        /// <summary><see cref="Ink"/> の 16 進。</summary>
        public const string InkHex = "D1C7B8";

        /// <summary><see cref="Alert"/> の 16 進。</summary>
        public const string AlertHex = "FF8C66";

        /// <summary><see cref="InkDim"/> の 16 進。</summary>
        public const string InkDimHex = "3F3C37";

        // ---- 大きさの逆算 -------------------------------------------------------------

        /// <summary>3D の TextMeshPro が透視カメラのとき内部で掛ける係数（TMP_Text.m_fontScale）。</summary>
        public const float MeshFontScale = 0.1f;

        /// <summary>見かけ角 <paramref name="deg"/> を距離 <paramref name="distanceM"/> での世界サイズ (m) にする。</summary>
        public static float WorldEm(float deg, float distanceM)
            => 2f * Mathf.Max(0.01f, distanceM) * Mathf.Tan(deg * 0.5f * Mathf.Deg2Rad);

        /// <summary>世界サイズ (m) を距離 <paramref name="distanceM"/> での見かけ角 (度) に戻す（監査用）。</summary>
        public static float DegreesOf(float worldEm, float distanceM)
            => 2f * Mathf.Atan(worldEm / (2f * Mathf.Max(0.01f, distanceM))) * Mathf.Rad2Deg;

        /// <summary>Canvas 系（<c>TextMeshProUGUI</c>）の fontSize。</summary>
        public static float CanvasFontSize(float deg, float distanceM, float canvasScale)
            => WorldEm(deg, distanceM) / Mathf.Max(1e-6f, canvasScale);

        /// <summary>
        /// 3D 系（<c>TextMeshPro</c>）の <c>localScale</c>。
        /// ⚠ <b>fontSize は小さいまま据え置き、倍率は scale で掛ける</b>（この codebase の流儀。
        /// fontSize を上げるとメッシュの座標そのものが広がる — <c>canon/LEDGER.md</c> 0035）。
        /// </summary>
        public static float MeshScale(float deg, float distanceM, float meshFontSize)
            => WorldEm(deg, distanceM) / Mathf.Max(1e-6f, meshFontSize * MeshFontScale);

        /// <summary>3D 系の 1 文字の世界サイズ (m)（監査用）。</summary>
        public static float MeshWorldEm(float meshFontSize, float meshScale)
            => meshFontSize * MeshFontScale * meshScale;

        /// <summary>Canvas 系の 1 文字の世界サイズ (m)（監査用）。</summary>
        public static float CanvasWorldEm(float fontSize, float canvasScale)
            => fontSize * canvasScale;

        // ---- 書式（文言を書く人が読む規約）---------------------------------------------
        //
        // 1. 操作は **`入力：動作`**（全角コロン）。`A = 確定` `トリガー2秒 → 〜` は使わない
        // 2. 括弧は**全角 `（ ）`**。要らない括弧は付けない
        // 3. 数値と単位のあいだに**半角空白**（`5 cm` / `0.05 m` / `123 ms`）。`%` だけ空けない
        // 4. **`⚠` は付けない**。警告色と出る場所で足りる
        // 5. 1 行に情報を 2 つまで。3 つ目は改行する（`・` で数珠つなぎにしない）
        // 6. 句点は**文章にだけ**（注意書き・終幕）。状態・操作・警告には付けない
        // 7. 揃えは**左**。中央にしてよいのは黒の中に単独で出る面だけ（注意書き・終幕・中止の 1 行）
    }
}
