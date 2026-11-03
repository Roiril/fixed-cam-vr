#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace FixedCamVr.Streaming
{
    // show.json の形（JsonUtility で読む純データ）。読み書きと適用は ShowControlClient。
    // 2026-11-03 まで ShowControlClient.cs の先頭に同居していた。

    // ---- show.json layout（ゾーン形状・カメラ割当のデータ）----
    // Tracking 側（ZoneLayoutApplier）が読むため public。Streaming → Tracking の参照は作らない
    // （このデータは純データで Tracking の型を持ち込まない）。JsonUtility でパースする。

    /// <summary>course space のフロア寸法 (m)。</summary>
    [Serializable] public sealed class ShowFloorDef { public float w = 1.8f; public float d = 1.8f; }

    /// <summary>L 字壁の記述（描画・登録基準点導出用。Solver は使わない）。</summary>
    [Serializable] public sealed class ShowWallDef
    {
        public float[] corner = System.Array.Empty<float>();
        public float[] endX = System.Array.Empty<float>();
        public float[] endZ = System.Array.Empty<float>();
    }

    /// <summary>ループ上の切れ目。s ∈ [0,1)、camAfter = このカット以降のカメラ index。</summary>
    [Serializable] public sealed class ShowCutDef { public float s; public int camAfter; }

    // ---- 部屋の 3D プロキシ（layout.room）----
    // 実物と同じ位置に置く不可視の幾何。**1 つの幾何で 3 用途を兼ねる**:
    //   ①CG 人形のオクルーダ（壁の裏へ回れる） ②影の落ち先（シャドウキャッチャー） ③較正の参照点
    // 別々の幾何を持つと必ずズレるので分けない。レンダリング属性だけを用途ごとに切り替える。
    // 設計の正本: .claude/plans/2026-07-27_cg-compositing-rebuild.md §2.5

    /// <summary>プロキシの壁 1 枚（course 空間の線分 + 高さ + 厚み）。</summary>
    [Serializable] public sealed class ShowRoomWallDef
    {
        public string id = "";
        public float x1, z1;
        public float x2, z2;
        public float h = 1.0f;       // 床からの高さ (m)
        public float thick = 0.04f;  // 厚み (m)

        public bool IsUsable()
        {
            float dx = x2 - x1, dz = z2 - z1;
            return h > 0.01f && (dx * dx + dz * dz) > 1e-4f;
        }
    }

    /// <summary>プロキシの箱（机・柱など）。yawDeg 回転つきの直方体。</summary>
    [Serializable] public sealed class ShowRoomBoxDef
    {
        public string id = "";
        public float x, z;           // 中心の course 空間 XZ
        public float y;              // 床からの底面高さ（浮かせたい時だけ）
        public float w = 0.5f, d = 0.5f, h = 0.7f;
        public float yawDeg;

        public bool IsUsable() => w > 0.01f && d > 0.01f && h > 0.01f;
    }

    /// <summary>
    /// CG レイヤ専用の照明。**部屋で 1 つ**（カメラごとに持つと切替のたび人形の陰影が飛ぶ）。
    /// Quest に環境光推定 API は無いので人が著作する。卓の 5 スライダで合わせる。
    /// </summary>
    [Serializable] public sealed class ShowRoomLightDef
    {
        public float yawDeg = 30f;      // course +Z 基準の方位角
        public float pitchDeg = 55f;    // 仰角（真上が 90）
        public float tempK = 4000f;     // 色温度 (K)
        public float intensity = 1f;
        public float ambient = 0.35f;
        public float shadowDensity = 0.55f;  // 影の濃さ（0=影なし）
        public float shadowSoftM = 0.12f;    // 影のにじみ (m 相当)
    }

    /// <summary>部屋の 3D プロキシ。<c>layout.room</c>・present-flag は <c>layout.hasRoom</c>。</summary>
    [Serializable] public sealed class ShowRoomDef
    {
        public float floorY;               // 床の高さ（course y。通常 0）
        public float floorW = 1.8f;
        public float floorD = 1.8f;
        public ShowRoomWallDef[] walls = System.Array.Empty<ShowRoomWallDef>();
        public ShowRoomBoxDef[] props = System.Array.Empty<ShowRoomBoxDef>();
        public ShowRoomLightDef? light;
        public bool hasLight;

        /// <summary>床が正なら最低限プロキシとして成立する（壁ゼロでも影は落ちる）。</summary>
        public bool HasData() => floorW > 0.05f && floorD > 0.05f;
    }

    /// <summary>
    /// HMD 位置合わせのタッチ基準点（course space XZ、床マーカー運用）。順序 = タッチ順。
    /// フロアマップ UI で 2〜5 点をオーサリングする。label は HMD ガイダンス表示用（空可）。
    /// CourseRegistrationController が読む（Tracking → Streaming の既存参照方向を守る純データ）。
    /// </summary>
    [Serializable] public sealed class ShowRegPointDef { public float x; public float z; public string label = ""; }

    /// <summary>
    /// 導入演出を始める床の位置（course space の円）。卓のフロアマップ（🎬 モード）で 1 点だけ置く。
    /// 体験者の頭がこの円の中に少し留まったら、導入演出が段 0 を抜けて始まる。
    ///
    /// ⚠ course 座標なので<b>位置合わせが済んでいないと判定できない</b>。未登録のときは
    /// スタッフ操作へ縮退し、HMD 内でその理由を言う（黙って「立っても始まらない」を起こさない）。
    /// </summary>
    [Serializable] public sealed class ShowStartSpotDef
    {
        public float x;
        public float z;
        /// <summary>半径 (m)。小さすぎると立ち位置がシビアになり、大きすぎると通りすがりで始まる。</summary>
        public float radiusM = 0.35f;
        public string label = "";

        public const float DefaultRadiusM = 0.35f;

        /// <summary>実際に使える半径（0 / 負値・極端な値はコード既定へ）。</summary>
        public float ResolveRadiusM() => radiusM >= 0.1f && radiusM <= 2f ? radiusM : DefaultRadiusM;

        /// <summary>
        /// JsonUtility がキー欠落で作った空の実体か。座標もラベルも無く半径が既定のままなら、
        /// 誰も著作していない。**(0,0) を著作したいなら卓がラベルを付ける**（UI が必須にしている）。
        /// </summary>
        public bool LooksUnset =>
            Mathf.Approximately(x, 0f) && Mathf.Approximately(z, 0f) && string.IsNullOrEmpty(label);
    }

    /// <summary>
    /// 通過ライン（course space の線分）。「体験者がこのラインを通過したら演出を始める」発火点。
    /// 卓のフロアマップ（📏 モード）で著作し、演出（<see cref="ShowTakeDef"/>）が <c>lineId</c> で参照する。
    ///
    /// <b>ラインは担当カメラ（<see cref="camera"/>）に紐づく</b>。演出が属する区間 <c>(lap, camera)</c> の
    /// カメラと一致しないラインは、踏んでも何も起きない（別の領域での踏み間違いを構造的に無効化する）。
    /// 判定は HMD の course 空間 XZ の移動線分との交差のみ（高さは見ない）。契約は
    /// <c>.claude/plans/2026-07-27_position-trigger.md</c> §3。
    /// </summary>
    [Serializable] public sealed class ShowLineDef
    {
        public string id = "";
        public int camera = -1;                          // 担当カメラ index（-1 = 未指定）
        public float x1, z1;                             // 端点 A
        public float x2, z2;                             // 端点 B
        public string dir = LineCrossLogic.DirBoth;      // "both" | "fwd" | "back"
        public string label = "";
    }

    /// <summary>
    /// v2: タイルペイント（grid）モデル。フロアを正方タイルに切り、cells の各文字でカメラを塗る。
    /// cells は rows 本の文字列。row 0 = 北端（z=+d/2 側）、col 0 = 西端（x=-w/2）。
    /// 文字 '0'..'8' = カメラ index、'.' = 未割当。JsonUtility は string[] をパースできる。
    /// </summary>
    [Serializable] public sealed class ShowGridDef
    {
        public float tileM = 0.15f;
        public int cols;
        public int rows;
        public string[] cells = System.Array.Empty<string>();

        /// <summary>塗られたタイルが 1 つでもあれば present（cells に非空文字列が 1 行でもあるか）。</summary>
        public bool HasData()
        {
            if (cells == null || cols <= 0 || rows <= 0 || tileM <= 0f) return false;
            foreach (var row in cells) if (!string.IsNullOrEmpty(row)) return true;
            return false;
        }
    }

    /// <summary>
    /// 周回定義（フロアマップ UI で編集・layout と同じ保存単位）。
    /// order = 順方向のカメラ巡回順（カメラ index）。order[0] = スタート領域のカメラ。
    /// LapCounter がこの順で周回を数える（順方向一致でのみ前進）。
    /// </summary>
    [Serializable] public sealed class ShowCourseDef
    {
        public int[] order = System.Array.Empty<int>();

        /// <summary>2 カメラ以上の巡回順があれば present。</summary>
        public bool HasData() => order != null && order.Length > 0;
    }

    /// <summary>show.json の layout セクション。cuts / grid のどちらも無ければ「layout 未設定」として扱う。</summary>
    [Serializable] public sealed class ShowLayoutDef
    {
        public int rev;
        public ShowFloorDef? floor;
        public ShowWallDef? wall;
        public ShowCutDef[] cuts = System.Array.Empty<ShowCutDef>();
        public ShowGridDef? grid;   // v2: grid があれば grid 優先（cuts は後方互換）
        public ShowCourseDef? course;   // 周回定義（LapCounter が読む。ゾーン生成には使わない）
        // HMD 位置合わせの N 点基準（順序つき・2〜5）。不在（空）なら CourseRegistrationController は
        // 既定 2 点へフォールバックする。ゾーン生成・HasData() には関与しない（純粋に登録用データ）。
        public ShowRegPointDef[] regPoints = System.Array.Empty<ShowRegPointDef>();

        /// <summary>
        /// 位置合わせで基準点をタッチするとき、コントローラを**床から何 m の高さに構えるか**。
        /// 登録はここから床の高さ（course y=0 のワールド高さ）を逆算する
        /// （<c>floorY = median(タッチ位置の y) − regTouchHeightM</c>）。
        ///
        /// **既定 0 = 床に着ける。** 空中でホバーすると XZ が確実にぶれて残差ゲート 0.12m を圧迫するので、
        /// 精度としては 0 が最善。現場で腰高のマーカーを使う等の事情があれば、その高さを入れる。
        /// </summary>
        public float regTouchHeightM;
        // 通過ライン（演出の発火点となる床の線分）。ゾーン生成・HasData() には関与しない純データで、
        // TakeRunner だけが読む（regPoints と同じ立ち位置）。
        public ShowLineDef[] lines = System.Array.Empty<ShowLineDef>();
        // 部屋の 3D プロキシ（CG のオクルーダ / 影の落ち先 / 較正参照）。ゾーン生成・HasData() には関与しない。
        // 既存の floor / wall は「登録リチュアルのワイヤー表示」専用だったもので、room はその上位互換。
        // room があれば登録ワイヤーも room から描く（二重管理を作らない）。
        public ShowRoomDef? room;
        public bool hasRoom;

        /// <summary>
        /// 導入演出を始める床の位置（体験者がここへ移動したら段 0 を抜ける）。未設定ならスタッフ操作のみ。
        /// **`regPoints` とは別の集合**にしてある — 位置合わせ点は「HMD で手が届く」「タッチ順に意味がある」
        /// という別の制約を持つので、兼用すると片方を動かしたときにもう片方が壊れる。
        /// </summary>
        public ShowStartSpotDef? startSpot;
        /// <summary>
        /// 卓が「開始位置を著作した」と宣言したか。**`startSpot != null` を信じてはいけない** —
        /// JsonUtility はキーが無くても入れ子の実体を作るので、未著作でも (0,0) の円が生きてしまう
        /// （`hasRoom` / `hasPost` / `ShowIntroDef.LooksUnset` を置いているのと同じ罠）。
        /// 計画 §11.5 が「勝手に (0,0) へ置くと『立っても始まらない』の原因になる」と名指しで禁じた状態。
        /// </summary>
        public bool hasStartSpot;
        public float overlapM = 0.08f;
        public float hysteresisM = 0.12f;

        /// <summary>
        /// 実際に使える開始位置。宣言 bool と実体の AND で確定する（`TimelinePresentFlags` と同じ流儀）。
        /// 卓が `hasStartSpot` を書く前の古い show.json は `LooksUnset` で救う。
        /// **未設定なら null** ＝ スタッフ操作だけで始める運用。
        /// </summary>
        public ShowStartSpotDef? ResolveStartSpot()
        {
            if (startSpot == null) return null;
            if (startSpot.LooksUnset) return null;
            // 宣言が来ている show.json ではそれに従う。宣言そのものが無い（古い）なら実体の中身で判断する。
            return hasStartSpot || !string.IsNullOrEmpty(startSpot.label) ? startSpot : null;
        }

        /// <summary>ゾーン生成に使える layout データ（grid か cuts）を持つか。grid 優先の判定は Applier 側。</summary>
        public bool HasData()
            => (grid != null && grid.HasData()) || (cuts != null && cuts.Length > 0);
    }

    /// <summary>事前オーサリング済みスケジュール 1 行。camera はカメラ index。lap は 1 始まり。</summary>
    [Serializable] public sealed class ShowScheduleEntryDef
    {
        public int lap;
        public int camera;
        public string cueId = "";
        public float delaySec;      // ゾーン進入からの遅延
        public bool once = true;    // true = そのランで 1 回だけ
    }

    /// <summary>show.json の schedule セクション。rev で変更検出する。</summary>
    [Serializable] public sealed class ShowScheduleDef
    {
        public int rev;
        public ShowScheduleEntryDef[] entries = System.Array.Empty<ShowScheduleEntryDef>();

        /// <summary>1 行でもエントリがあれば present。</summary>
        public bool HasData() => entries != null && entries.Length > 0;
    }

    // ---- show.json 画像加工 / タイムライン（スキーマ v2）のパース構造体 ----

    /// <summary>
    /// 画像加工 7 項目（露出/コントラスト/彩度/色温度/ヴィネット/グレイン/走査線）。
    /// global（トップレベル post）/ カメラ別（cameras[i].post）/ タイムライン区間 / インサートで共通に使う。
    /// もとは ShowControlClient のネスト private だったが、TimelineDirector・タイムライン定義が共有するため
    /// public トップレベルへ昇格（型の位置が変わるだけでフィールド名は不変 = JsonUtility 往復は無影響）。
    /// </summary>
    [Serializable] public sealed class PostParams
    {
        public float exposure;
        public float contrast = 1f;
        public float saturation = 1f;
        public float temperature;
        public float vignette;
        public float grain;
        public float scanline;

        // 監視カメラらしさのための 2 項目（2026-07-26 追加。既定 0 = 何もしない＝旧データと同じ絵）。
        /// <summary>黒浮き（0..0.3）。コントラストの後に黒側の床を持ち上げる（安物センサーの締まらない黒）。</summary>
        public float lift;
        /// <summary>色かぶり（-1..1）。+ = 緑（蛍光灯 / 安物 CMOS）、- = マゼンタ。temperature の直交軸。</summary>
        public float tint;

        // 2026-07-29 追加。企画書「合成後の映像全体に色調補正や走査線・粒状感などの効果を施し、
        // 固定カメラ映像らしい質感に整える」の残り 2 軸と、走査線の本数。既定 0 / 0 は旧データと同じ絵。
        /// <summary>色収差（0..1）。放射方向に RGB をずらす。安いレンズの色ずれ。</summary>
        public float aberration;
        /// <summary>低解像度化（0..1）。サンプル位置を量子化してブロックを作る。伝送の劣化を装う。</summary>
        public float pixelate;

        /// <summary>
        /// 全項目が既定（＝何も加工しない）か。**「著作されていない post」を見分けるために要る。**
        ///
        /// JsonUtility は <c>"post": null</c> と書かれていても入れ子の実体を既定値で作るので、
        /// <c>post != null</c> だけで「著作された」と判定すると、**素通しの実体が
        /// global の加工を上書きして絵から加工が丸ごと消える**（2026-07-30 実機で発覚。
        /// 卓は未設定のカメラに `"post": null` を書いていた）。
        /// </summary>
        public bool IsDefaultLike()
            => exposure == 0f && contrast == 1f && saturation == 1f && temperature == 0f
               && vignette == 0f && grain == 0f && scanline == 0f && lift == 0f && tint == 0f
               && aberration == 0f && pixelate == 0f;
        /// <summary>
        /// 走査線の本数。0 = 未指定（コード既定 <see cref="DefaultScanlineCount"/>）。
        /// 実機は material の 240 固定で、卓は canvas の縦画素（360〜480）を使っていたため、
        /// 同じ scanline 値でも縞のピッチが 1.5〜2 倍食い違っていた。値を持たせて両者を揃える。
        /// </summary>
        public float scanlineCount;

        /// <summary>走査線本数のコード既定（従来 material に焼かれていた値）。</summary>
        public const float DefaultScanlineCount = 240f;

        /// <summary>走査線本数を解決する（0 / 負値 = コード既定）。</summary>
        public float ResolveScanlineCount() => scanlineCount > 0f ? scanlineCount : DefaultScanlineCount;
    }

    /// <summary>
    /// BGM トラック 1 本（show.json トップレベル bgmTracks[]）。cue と同じく URL 参照で、
    /// 📦 エクスポートで sa://assets/ へ焼き込まれる。ループ範囲はここが既定値（区間で上書き可）。
    /// </summary>
    [Serializable] public sealed class ShowBgmTrackDef
    {
        public string id = "";
        public string name = "";
        public string url = "";
        public float loopStartSec;      // ループ先頭（秒）
        public float loopEndSec;        // <=0 = クリップ末尾まで
        public float volume = 1f;
    }

    /// <summary>
    /// BGM の指示。show.json トップレベル bgm（ラン既定）と timeline.segments[].bgm（区間指示）で共用。
    /// 区間側の -1 は「トラック既定を継承」。action の既定は continue なので、
    /// JsonUtility が null 入れ子から作る幻のオブジェクトは何もしない（＝安全側）。
    /// </summary>
    [Serializable] public sealed class ShowBgmDef
    {
        public string action = BgmPlanLogic.ActionContinue;   // "play" | "stop" | "continue"
        public string trackId = "";
        public bool loop = true;
        public float startSec;              // 頭出し位置（ループ窓の外ならの窓頭へ丸める）
        public float loopStartSec = -1f;    // -1 = トラック既定
        public float loopEndSec = -1f;      // -1 = トラック既定
        public float volume = -1f;          // -1 = トラック既定
        public float fadeInSec = 1f;
        public float fadeOutSec = 1f;
        public bool restart;                // 同一トラックでも頭出しし直す

        /// <summary>実際に何かする指示か（play でトラック指定あり / stop）。</summary>
        public bool IsActionable()
            => action == BgmPlanLogic.ActionStop
               || (action == BgmPlanLogic.ActionPlay && !string.IsNullOrEmpty(trackId));
    }

    /// <summary>
    /// カメラの役割。<c>fx</c>（演出専用）はどのゾーンにも割り当てず、スタッフの巡回にも出さない
    /// （＝「カメラ D」のような、演出のカットからしか映らないカメラ）。未知値は <c>zone</c> へ倒す。
    /// </summary>
    public static class CameraRoles
    {
        public const string Zone = "zone";
        public const string Fx = "fx";

        public static bool IsFx(string? role) => role == Fx;
    }

    /// <summary>
    /// 実カメラの course 空間での姿勢（CG レイヤの仮想カメラがこの姿勢で構える）。
    /// course 空間は <c>CourseFrame</c> の 3DOF で実空間へ登録済みなので、体験者の位置と同じ座標系に乗る。
    /// 卓のフロアマップで**人が印をドラッグして置く概算**。<c>hasPose</c> が present-flag。
    ///
    /// **これは較正結果ではない**。実測の解は <see cref="ShowCameraCalibDef"/>（<c>cameras[].calib</c>）が持ち、
    /// あればそちらが勝つ。同居させないと卓のドラッグが解を破壊する。
    /// </summary>
    [Serializable] public sealed class ShowCameraPoseDef
    {
        public float x;          // course 空間 X (m)
        public float z;          // course 空間 Z (m)
        public float y = 1.2f;   // 床からの高さ (m)
        public float yawDeg;     // course +Z を 0 とする水平角
        public float pitchDeg;   // 下向きが負
        // **水平**画角 (度)。0 = 未著作（コード既定を使う）。
        // 旧 `fovDeg` は読まない — Unity 側が垂直として使い、卓の扇は水平で描いていて約 25% 食い違っていた
        // （2026-07-27 監査）。黙って意味を変えると「卓では合うのに実機が違う」を再生産するのでキーごと変える。
        public float hfovDeg;
    }

    /// <summary>
    /// 実カメラの**較正結果**。卓が実映像上の床点（<c>layout.regPoints</c> と同じ × 印）をクリックさせ、
    /// 平面ホモグラフィ分解で姿勢・焦点距離・歪みを解いた値。<c>hasCalib</c> が present-flag。
    ///
    /// **レンズと解像度に従属する**ので <see cref="srcW"/>/<see cref="srcH"/>/<see cref="lensId"/> をキーに保存し、
    /// 受信中のフレームと一致しないときは較正無効へ倒す（配信設定を変えた瞬間に黙って狂うのを防ぐ）。
    /// 内部パラメータは px 単位で持つ（正規化すると幅基準か高さ基準かで必ず取り違える）。
    /// </summary>
    [Serializable] public sealed class ShowCameraCalibDef
    {
        public float x;          // course 空間 (m)
        public float z;
        public float y;
        public float yawDeg;     // course +Z を 0 とする水平角
        public float pitchDeg;   // 下向きが負
        public float rollDeg;    // 三脚は必ず傾くので持つ

        public float fxPx;       // 焦点距離 (px)
        public float fyPx;
        public float cxPx;       // 主点 (px・画像左上原点)
        public float cyPx;
        public float k1;         // 半径方向歪み 1 係数（超広角で効く）

        public int srcW;         // 解いたときのフレーム実寸
        public int srcH;
        public string lensId = "";   // /info の lensId（レンズを切り替えたら別物）

        public float rmsPx;      // 再投影誤差（品質。登録リチュアルの maxResidualM と同じ立ち位置）
        public int pointCount;
        public string solvedAtIso = "";
        public ShowCalibRefDef[] refs = Array.Empty<ShowCalibRefDef>();   // 再解決用の対応点

        /// <summary>解ける値が入っているか（焦点距離と実寸が正）。</summary>
        public bool IsUsable() => fxPx > 1f && fyPx > 1f && srcW > 1 && srcH > 1;

        /// <summary>
        /// いま受信しているフレームに対して有効か。実寸が違えば内部パラメータは意味を失う。
        /// lensId は**両方が非空のときだけ**照合する（/info を持たない配信アプリを排除しない）。
        /// </summary>
        public bool MatchesSource(int w, int h, string? lens)
        {
            if (!IsUsable()) return false;
            if (w != srcW || h != srcH) return false;
            if (!string.IsNullOrEmpty(lensId) && !string.IsNullOrEmpty(lens) && lensId != lens) return false;
            return true;
        }
    }

    /// <summary>較正の対応点 1 個（映像上の画素 ↔ course 空間の床点）。再解決のために残す。</summary>
    [Serializable] public sealed class ShowCalibRefDef
    {
        public float u;   // 画像内の正規化座標 (0..1・左上原点)
        public float v;
        public float x;   // course 空間 (m)
        public float z;
        public float y;   // 床なら 0
    }

    /// <summary>
    /// 撮像の質（「装置らしさ」）。show.json トップレベル <c>feel</c>。<see cref="CameraFeelFx"/> が読む。
    ///
    /// post 12 項目と分けてあるのは、**こちらは時間で動く**ため。post は shader / 卓の FS_POST /
    /// common.js / pipeline.js の 4 箇所を手作業で同期していて機械テストが無く、そこへ時間軸を
    /// 持ち込むと沈黙した食い違いが必ず出る。
    ///
    /// **キーが無くても既定値で効く**（設定を書かないと何も起きない、では現場で使われない）。
    /// </summary>
    [Serializable] public sealed class ShowFeelDef
    {
        public const float DefaultNoiseDark = 0.10f;
        public const float DefaultNoiseFixed = 0.035f;
        public const float DefaultAgc = 0.7f;
        public const float DefaultTargetLuma = 0.34f;
        public const float DefaultFollowSec = 1.1f;

        /// <summary>暗部ほど強い粒。暗がりが物を隠せる状態を作る。</summary>
        public float noiseDark = DefaultNoiseDark;

        /// <summary>時間で動かない粒（画面に貼り付いた汚れ）。この中で動くものだけが浮く。</summary>
        public float noiseFixed = DefaultNoiseFixed;

        /// <summary>自動露出の追従の効き 0..1（0 = 追わない＝従来どおり静的）。</summary>
        public float agc = DefaultAgc;

        /// <summary>装置が目指す明るさ（映像の平均輝度 0..1）。</summary>
        public float targetLuma = DefaultTargetLuma;

        /// <summary>目標へ半分まで近づくのにかかる時間 (秒)。大きいほど鈍く、遅れが目に付く。</summary>
        public float followSec = DefaultFollowSec;

        /// <summary>
        /// JsonUtility は show.json に <c>feel</c> が無くても「全部 0」の実体を作る。
        /// 0 は「無効」ではなく「未指定」なので、既定へ落とす判定が要る
        /// （<see cref="ShowIntroDef"/> と同じ罠 — これが無いと**黙って効かなくなる**）。
        /// </summary>
        public bool LooksUnset()
            => noiseDark <= 0f && noiseFixed <= 0f && agc <= 0f && targetLuma <= 0f && followSec <= 0f;
    }

    /// <summary>端末内録画（前の周を録って 3 周目に流す）の設定。show.json トップレベル <c>record</c>。</summary>
    [Serializable] public sealed class ShowRecordDef
    {
        public bool enabled;
        public int[] laps = { 1 };          // 録る周（既定は 1 周目だけ）
        public int maxTotalMB = 200;        // ラン全体の上限
        public float fpsCap = 15f;          // 録画側の間引き（受信 fps より低くしてよい）

        /// <summary>
        /// 区間の**末尾**何秒を残すか。0 以下 = コード既定
        /// （<see cref="Recording.SegmentRecordWriter.DefaultTailSec"/> = 3 秒）。
        ///
        /// 頭からではなく末尾を残すのは、3 周目の再生が「区間へ入った瞬間」に始まるため。
        /// 頭から録ると映像の中の過去の自分も入口に居て、**体験者の現在位置に立つ CG 人形と重なる**。
        /// 末尾＝区間を出る直前なら、過去の自分は出口側に居て位置が分かれる。
        /// </summary>
        public float tailSec = Recording.SegmentRecordWriter.DefaultTailSec;

        /// <summary>
        /// カメラが**切り替わった後**も、出ていった区間のカメラを何秒録り続けるか。0 以下 = コード既定
        /// （<see cref="Recording.SegmentRecordWriter.DefaultPostSec"/> = 2 秒）。
        ///
        /// 切り替えの瞬間で切ると、**過去の自分が曲がり切る前に映像が終わる**（角を曲がる動きは
        /// カメラが切り替わってからも 1〜2 秒続く）。録画 1 本の尺は <c>tailSec + postSec</c> になる。
        /// </summary>
        public float postSec = Recording.SegmentRecordWriter.DefaultPostSec;

        /// <summary>
        /// **この床の線を横切った所から録り始める**（<c>layout.lines[].id</c>・空 = 末尾方式）。
        /// 2026-08-16 追加（<c>canon/LEDGER.md</c> 0061）。
        ///
        /// 末尾方式（<see cref="tailSec"/>）は「区間を出る直前」を残すので、**そのカメラに
        /// 体験者が写っていない区間では無人の部屋しか残らない**。実測（1 周目 A・4.89 秒）は
        /// 43 枚中 最後の 1 枚にしか人が写っていなかった。
        ///
        /// 線を指すと、その線を横切った瞬間から区間の終わりまで（＋ <see cref="postSec"/>）を残す。
        /// 3 周目の再生も同じ場所を起点にできるので、**過去の自分と現在の位置が空間で揃う**。
        ///
        /// ⚠ 効くのは**線の担当カメラ**（<c>lines[].camera</c>）と同じカメラの区間だけ。
        /// ⚠ **横切らなかった区間は末尾方式のまま**（何も残らない、を作らない）。
        /// </summary>
        public string startLineId = "";

        /// <summary>
        /// **区間へ入った瞬間から録る**区間の一覧（<c>"L&lt;周&gt;C&lt;カメラ&gt;"</c>・例 <c>"L2C0"</c>）。
        /// 2026-10-19 追加（<c>canon/LEDGER.md</c> 0272）。3 周目 A が入った瞬間から 2 周目 A の録画を
        /// 流すので、2 周目 A は入りから残っていないといけない。
        ///
        /// ⚠ 指した区間では <see cref="startLineId"/> も末尾方式（<see cref="tailSec"/>）も見ない。
        /// ⚠ 周とカメラの組で指すのは、**録る周すべてを頭から録ると容量を食う**ため
        ///   （1 周目 A は導入のぶん長い。ラン全体の上限 <see cref="maxTotalMB"/> を使い切ると後の区間が録れない）。
        /// </summary>
        public string[] enterStartSegments = Array.Empty<string>();

        /// <summary>
        /// <b>前へ進んだコマだけを残す</b>区間と、その物差し（`canon/LEDGER.md` 0274）。
        /// 3 周目 A が体験者の位置に合うコマを出すための録り方。
        /// </summary>
        public ShowRecordProgressDef progress = new();

        /// <summary>この区間を入った瞬間から録るか（<see cref="enterStartSegments"/>）。</summary>
        public bool StartsAtEnter(int lap, int camera)
        {
            if (enterStartSegments == null) return false;
            string key = $"L{lap}C{camera}";
            foreach (string s in enterStartSegments) if (s == key) return true;
            return false;
        }

        /// <summary>
        /// 旧キー（区間の頭から何秒録るか）。**末尾方式では使わない**。
        /// 端末キャッシュ・焼き込みの古い show.json が持っているので読めるようにだけしてある。
        /// </summary>
        public float maxSegmentSec = 60f;

        /// <summary>末尾の実効尺 (秒)。0 以下・未指定は既定へ倒す。</summary>
        public float TailSec => tailSec > 0f ? tailSec : Recording.SegmentRecordWriter.DefaultTailSec;

        /// <summary>切り替え後に録り続ける実効尺 (秒)。0 以下・未指定は既定へ倒す。</summary>
        public float PostSec => postSec > 0f ? postSec : Recording.SegmentRecordWriter.DefaultPostSec;

        public bool RecordsLap(int lap)
        {
            if (!enabled || laps == null) return false;
            foreach (int l in laps) if (l == lap) return true;
            return false;
        }
    }

    /// <summary>
    /// <c>record.progress</c>（`canon/LEDGER.md` 0274）。指した区間は<b>時間ではなく位置で</b>録る:
    ///   - 角を回る進み具合（<see cref="Recording.CornerProgress"/>・<see cref="fromWall"/> → <see cref="toWall"/>）が
    ///     それまでの最大を超えたコマだけを残す（戻ったぶんは撮影済みなので記録しない）。追い録りは時間どおり
    ///   - その試行を<b>採るのは</b>、進み具合が <see cref="adoptAt"/> に届き、かつ周回の順で次のカメラへ
    ///     進んで出ていったときだけ。それ以外（入ってすぐ引き返した等）は書き出さずに捨てるので、
    ///     次に同じ区間へ入ったときに録り直せる。採った後に戻ってきた回は録り直さない（従来どおり）
    /// </summary>
    [Serializable] public sealed class ShowRecordProgressDef
    {
        /// <summary>対象の区間（<c>"L&lt;周&gt;C&lt;カメラ&gt;"</c>）。空 = この機能を使わない。</summary>
        public string[] segments = Array.Empty<string>();

        /// <summary>進み具合 0 の壁（<c>layout.room.walls[].id</c>）。</summary>
        public string fromWall = "w1";

        /// <summary>進み具合 1 の壁。</summary>
        public string toWall = "w2";

        /// <summary>試行を採る進み具合の下限（0.5 = 2 枚の壁の二等分線 ＝ A の中央の線）。</summary>
        public float adoptAt = 0.5f;

        public bool Applies(int lap, int camera)
        {
            if (segments == null) return false;
            string key = $"L{lap}C{camera}";
            foreach (string s in segments) if (s == key) return true;
            return false;
        }
    }

    /// <summary>
    /// 体験 1 回の骨格。show.json トップレベル <c>run</c>。企画書 3 章
    /// 「経路を 3 周する／体験全体は導入を含め 3 分以内／各周およそ 30 秒／導入で固定視点に慣れてから開始」を
    /// データにしたもの。**キーが無い show.json でも既定値で成立する**（従来どおり無限に走る、にはしない —
    /// 終端が無いことこそが現状の欠落なので、既定でも 3 周で終わる）。
    /// </summary>
    [Serializable] public sealed class ShowRunDef
    {
        /// <summary>走り切る周数。0 / 負値 = コード既定 3。</summary>
        public int totalLaps = 3;

        /// <summary>導入（固定視点に慣らす自由歩行）を挟むか。</summary>
        public bool introEnabled = true;

        /// <summary>導入の最低尺 (秒)。これを過ぎ、かつスタート区間に居れば本編へ移る。</summary>
        public float introMinSec = 20f;

        /// <summary>導入を自動で終わらせるか。false ならスタッフの明示操作（卓 / 現地）だけで進む。</summary>
        public bool introAutoAdvance = true;

        /// <summary>
        /// 目安の尺 (秒)。**体験を止めない** — 超過を卓に知らせるだけの表示用。
        /// 著作した演出を黙って間引く実装にはしない（作者に発見手段が無くなる）。
        /// </summary>
        public float targetSec = 180f;

        /// <summary>強制終了 (秒)。動かない・固まった体験者への保険。0 / 負値 = 無効。</summary>
        public float hardLimitSec = 300f;

        /// <summary>終了時に黒へ落とす時間 (秒)。</summary>
        public float endFadeSec = 1.5f;

        /// <summary>
        /// 導入演出（パススルー → 2D スクリーン）。キーが無ければコード既定で成立する。
        /// 計画 2026-07-30_intro-passthrough-to-screen.md §7。
        /// </summary>
        public ShowIntroDef? intro;

        /// <summary>終幕演出（本編 → パススルーへ戻して終わる）。無ければコード既定で走る。</summary>
        public ShowOutroDef? outro;

        /// <summary>
        /// 終了条件が成立してから、演出が走っていなくても必ず待つ秒数。
        /// **帰りの A（lap = totalLaps + 1 の order[0]）に置いた演出が始まる猶予**で、
        /// これが 0 だと演出が走り出す前に暗転する順序が実在する（<see cref="ShowRunLogic.Configure"/>）。
        /// 0 / 未指定 = コード既定 <see cref="ShowRunDefaults.EndGraceSec"/>。
        /// </summary>
        public float endGraceSec = ShowRunDefaults.EndGraceSec;

        /// <summary>
        /// 走行中の演出を見せ切る上限 (秒)。帰りの A で流す録画は 2 周目 A の実滞在ぶん（20〜40 秒）に
        /// なるので、旧実装の 12 秒固定では途中で切れた。0 / 未指定 = コード既定
        /// <see cref="ShowRunDefaults.EndHoldMaxSec"/>。
        /// </summary>
        public float endHoldMaxSec = ShowRunDefaults.EndHoldMaxSec;

        /// <summary>コード既定の周数。</summary>
        public const int DefaultTotalLaps = 3;

        public int ResolveTotalLaps() => totalLaps > 0 ? totalLaps : DefaultTotalLaps;

        public float ResolveEndGraceSec() => endGraceSec > 0f ? endGraceSec : ShowRunDefaults.EndGraceSec;

        public float ResolveEndHoldMaxSec() => endHoldMaxSec > 0f ? endHoldMaxSec : ShowRunDefaults.EndHoldMaxSec;
    }

    /// <summary>
    /// 終幕演出の設定。show.json <c>run.outro</c>。
    ///
    /// ⚠ <see cref="ShowIntroDef"/> と同じく **present-flag は持たない**。キーが無い show.json
    /// （焼き込み・端末キャッシュに残った古いもの）で <c>enabled=false</c> に化けると、
    /// 終幕が黙って出ずに黒で終わる。<see cref="LooksUnset"/> で検出して既定へ落とす。
    /// </summary>
    [Serializable] public sealed class ShowOutroDef
    {
        /// <summary>終幕を出すか。false なら従来どおり黒へフェードして終わる。</summary>
        public bool enabled = true;

        /// <summary>
        /// 終幕を早めるラインの id（<c>layout.lines[]</c>）。<b>未実装（スキーマだけ）。</b>
        ///
        /// ⚠ <b>これは終端そのものではない。</b> 線を終端にすると、**帰りの経路がその線分を跨ぐ保証が
        /// 幾何上どこにも無い**ので、踏まなかった体験者が <c>hardLimitSec</c> まで終われなくなる。
        ///
        /// ⚠ <c>run.intro.startLineId</c> を継承しない（空の意味が 2 つになり、線を後で流用したときに
        /// 黙って壊れる）。同じ線を使いたいなら**同じ id を明示的に書く**。空 = 位置トリガー無し。
        /// </summary>
        public string lineId = "";

        /// <summary>
        /// <b>終幕の合図となる演出の id</b>（<c>timeline.segments[].takes[].id</c>）。
        /// この演出が走って、そして終わったら終幕へ入る（<c>canon/LEDGER.md</c> 0048・
        /// ユーザー逐語「周回中の例えば4週目Aの指定された演出終了後に終幕演出を流すみたいな」）。
        /// 判断は <see cref="EndingCueLogic"/>、配線は <see cref="ShowRunDirector"/>。
        ///
        /// ⚠ <b>空なら従来どおり</b>「周を走り切って <c>endGraceSec</c> が過ぎたら」で終わる。
        /// ⚠ <b>合図であって終端ではない。</b> 指した演出が最後まで走らない現場
        /// （体験者が別の区間へ抜けた / 最後のカットが「次にカメラが切り替わるまで」で終わらない）でも、
        /// <c>endHoldMaxSec</c> / <c>hardLimitSec</c> の安全網はそのまま効く。
        /// </summary>
        public string afterTakeId = "";

        // 尺の既定は 2 箇所（ここと <see cref="OutroTiming.Default"/>）に現れる。値は一致させること。
        // ⚠ キーは 2 度入れ替わっている:
        //    2026-08-15  旧 unswapSec / openSec / restoreSec / holdSec → flickerSec ほか（0048）
        //    2026-08-23  flickerSec → collapseSec（0111・ちかちかを電源断へ置き換えた）
        //    2026-10-21  noiseSec を足し、collapseSec の既定を 0.9 → 0.26（博士タブレットの消え方へ揃えた）
        //    旧キーしか持たない show.json（焼き込み・端末キャッシュ）では新キーが 0 になり、
        //    Sanitized() が既定へ倒すので**そのまま走る**。
        public float noiseSec = 0.95f;
        public float collapseSec = 0.26f;
        public float darkSec = 1.2f;
        public float reportFadeSec = 1.5f;
        //    2026-10-22  holdSec / closeFadeSec を足した（報告の出始めから 15 秒で面と曲が閉じる）
        public float holdSec = 9.5f;
        public float closeFadeSec = 4.0f;

        /// <summary>キーごと無い（JsonUtility が 0 で埋めた）形か。</summary>
        public bool LooksUnset() =>
            !enabled && noiseSec <= 0f && collapseSec <= 0f && darkSec <= 0f && reportFadeSec <= 0f;

        public OutroTiming ToTiming() => new OutroTiming
        {
            noiseSec = noiseSec, collapseSec = collapseSec, darkSec = darkSec, reportFadeSec = reportFadeSec,
            holdSec = holdSec, closeFadeSec = closeFadeSec,
        }.Sanitized();
    }

    /// <summary>
    /// 導入演出の設定。show.json <c>run.intro</c>。
    ///
    /// ⚠ <b>present-flag は持たない。</b> JsonUtility はキーが無いと「全部 0 / false」の実体を作るので、
    /// それを <see cref="LooksUnset"/> で検出して既定へ落とす（`enabled=false` に化けて演出が
    /// 黙って出なくなるのを防ぐ）。焼き込み・端末キャッシュに古い show.json が残っていても効く。
    /// </summary>
    [Serializable] public sealed class ShowIntroDef
    {
        /// <summary>導入演出を出すか。false なら従来どおり最初からスクリーンだけが見える。</summary>
        public bool enabled = true;

        // ⚠ 尺の既定は 4 箇所（ここ / <see cref="IntroTiming.Default"/> / 卓の intro-model.js の
        //    INTRO_DEFAULT / capture-server.py の _default_show）に現れる。**値は一致していること**。
        //    実害は出にくい（JsonUtility が埋めた 0 は Sanitized() が IntroTiming.Default で
        //    上書きするため）が、直接フィールドを読む経路が増えた瞬間に食い違う。
        //
        // ⚠ 2026-08-13 に段を作り直し（Real/Degrade/Structure/Frame/Swap → Seal/Dark/Ignite/Live）、
        //    **2026-08-15 に元へ戻した**（封印の箱が無くなったため・`canon/LEDGER.md` 0044）。
        //    どちらの向きでも、旧キーを持つ show.json は JsonUtility が黙って無視するので
        //    尺だけコード既定へ落ちる（画は出る）。

        /// <summary>演出の上限 (秒)。超えたら段を飛ばして映像を出す（条件待ちで固まらないための保険）。</summary>
        public float maxSec = 20f;

        /// <summary>段 1。素のパススルー（段 2 の変化を読ませるための比較対象）。</summary>
        public float realSec = 1.5f;
        /// <summary>段 2。色が抜け、コントラストが上がり、実物の輪郭が浮く。</summary>
        public float degradeSec = 3.5f;
        /// <summary>段 3。輪郭だけの世界（段 2 の後半から重なる）。</summary>
        public float structureSec = 2.5f;
        /// <summary>段 4。現実が割れてスクリーンへ吸い込まれ、枠が閉じる。</summary>
        public float frameSec = 5.0f;
        /// <summary>段 5。枠の中がカメラ映像へ。</summary>
        public float swapSec = 1.6f;

        /// <summary>
        /// 実物の輪郭線の色（<c>#rrggbb</c>）。既定は<b>生成り</b>（LEDGER 0010「全体的に暖色に」）。
        /// 純白は蛍光灯の下の点検作業に見える。段 2 の「色が抜けて輪郭だけが残る」は
        /// 灯りの下で見ているという前提の上に置く。
        /// </summary>
        public string edgeColor = "#ffcf9e";

        // ⚠ 既定は**どちらも false**（2026-08-01 ユーザー判断「雰囲気ぶち壊しだから要らない」）。
        //    細い寒色の線は現実の上に重なると計測器に見え、「現実がそのまま格下げされていく」という
        //    段 2 → 段 4 の筋を切る。位置合わせの現地検証は登録リチュアル（Review フェーズ）の
        //    ワイヤー表示が担うので、導入から消しても検証手段は残る。
        //    卓（⚙ 欄）で on にすれば従来どおり出る。
        //    JsonUtility は show.json に無いキーを false で埋めるので、この既定と一致する。

        /// <summary>段 3 でカメラの位置に印を出すか。</summary>
        public bool showCameraMarks;

        /// <summary>段 3 で壁・床の線を出すか。</summary>
        public bool showRoomWire;

        /// <summary>段 5 のすり替えに重ねる乱れの強さ。</summary>
        public float glitchOnSwap = 0f;

        /// <summary>
        /// 導入演出を始める**通過ライン**の id（<c>layout.lines[].id</c>）。空なら
        /// 従来どおり <c>layout.startSpot</c> の円に留まることで始まる。
        ///
        /// 円（その場所に立つ＝状態）ではなく線（横切る＝事象）にしたのは、体験者が
        /// **歩いて入ってくる動きのまま**始められるようにするため。開始位置に立ち止まって
        /// 待つ必要が無くなる。ラインは担当カメラを持つので、順路の入口に引いておけば
        /// 「入ってきた人が最初に踏む線」になる。
        /// </summary>
        public string startLineId = "";

        // ⚠ `raiseHandPrompt`（段 4 の「右手を上げて」）は 2026-08-13 に**廃止**した
        //    （`canon/LEDGER.md` 0034「伏線にするのはスマートではない」）。
        //    show.json に残っていても JsonUtility が黙って無視する。

        /// <summary>JsonUtility が既定値で埋めただけの実体か（＝ show.json に <c>intro</c> が無い）。</summary>
        public bool LooksUnset =>
            !enabled && maxSec <= 0f && realSec <= 0f && degradeSec <= 0f && swapSec <= 0f;

        public IntroTiming ToTiming() => new IntroTiming
        {
            realSec = realSec, degradeSec = degradeSec, structureSec = structureSec,
            frameSec = frameSec, swapSec = swapSec, maxSec = maxSec,
        }.Sanitized();

        /// <summary>輪郭線の色を解く（解けなければ生成り）。</summary>
        public Color ResolveEdgeColor()
            => ColorUtility.TryParseHtmlString(string.IsNullOrEmpty(edgeColor) ? "#ffcf9e" : edgeColor,
                out var c) ? c : new Color(1f, 0.812f, 0.62f, 1f);
    }

    /// <summary>
    /// 目の視界ジャックに流す当日写真（show.json トップレベル <c>eyejack</c>・
    /// <c>canon/LEDGER.md</c> 0099）。<c>photos[]</c> は卓のサーバが
    /// <c>tools/web-compositor/eyejack/</c> に置かれた写真を正規化して並べる URL 列
    /// （ファイル名順 = 決定的）。空 = ジャックは発火しない（目は従来どおり）。
    /// </summary>
    [Serializable] public sealed class ShowEyeJackDef
    {
        public string[] photos = Array.Empty<string>();

        /// <summary>実データを持つか（JsonUtility の既定オブジェクトを present と読まない）。</summary>
        public bool HasData() => photos != null && photos.Length > 0;
    }

    /// <summary>CG レイヤに立てる人形の定義。show.json トップレベル <c>actors</c>。</summary>
    [Serializable] public sealed class ShowActorDef
    {
        public string id = "";
        public string name = "";
        public string prefab = "";      // Resources 配下のプレハブ名
        public float heightM = 1.6f;
        public float fixedX;            // cgMode="fixed" のときの course 空間位置
        public float fixedZ;
        public float fixedYawDeg;
    }

    /// <summary>
    /// 素材スロットの束縛（<c>slot://name</c> → 実 URL）。**ラン中に卓が差し替える**ので
    /// timeline ではなく control に置く（timeline を書き換えると発火済み演出が再武装される）。
    /// </summary>
    [Serializable] public sealed class ShowSlotDef
    {
        public string name = "";
        public string url = "";
    }

    /// <summary>タイムライン区間 cue の任意上書き（強度・フェード・trim を丸ごと差し替える）。hasOverride が present-flag。</summary>
    [Serializable] public sealed class ShowCueOverrideDef
    {
        // 初期値は **-1 = 素材定義から継承**（v3 step の -1 継承と同じ流儀）。
        // JsonUtility は JSON に無いキーを初期値のまま残すので、キーが一部しか無い v2 JSON では
        // ここが「明示値」として効いてしまう。JS 側（timeline-model.js）は欠落キーを -1 にするため、
        // 初期値を 1f/0.5f のままにすると両側の移行結果が食い違う（2026-07-26 監査 LOW）。
        public float strength = -1f;
        public float fadeIn = -1f;
        public float fadeOut = -1f;
        public float trimStart = -1f;
        public float trimEnd = -1f;
    }

    /// <summary>タイムライン区間で発火する cue 1 本（従来 schedule.entries 相当 + 任意 override）。</summary>
    [Serializable] public sealed class ShowSegmentCueDef
    {
        public string cueId = "";
        public float delaySec;      // 区間進入からの遅延
        public bool once = true;    // true = そのランで 1 回だけ
        // JSON キー "override" は C# 予約語のため @override で受ける（実行時フィールド名は "override"）。
        public ShowCueOverrideDef? @override;
        public bool hasOverride;    // present-flag（ライブパース直後に @override!=null から確定）
    }

    /// <summary>
    /// 区間からの離脱（exit）/ 進入（enter）時に別カメラを差し込むインサートショット定義。
    /// exit: この区間から Zone 切替で離脱する瞬間（dip 黒中）に camera へ差し替える。
    /// enter: 区間進入 + delaySec 後に camera を durationSec 表示する。どちらも表示後は最新ゾーンへ復帰。
    /// </summary>
    [Serializable] public sealed class ShowInsertDef
    {
        public string anchor = "enter";   // "enter" | "exit"
        public int camera;                // 差し込むカメラ index
        public float delaySec;            // enter: 進入からの遅延（exit は 0 運用）
        public float durationSec = 4f;    // 表示秒数
        public string cueId = "";         // 任意。表示に合わせ PlayCue
        public bool once = true;          // true = そのランで 1 回だけ
        public PostParams? post;          // 任意。無ければインサート先カメラの post / global へフォールバック
        public bool hasPost;              // present-flag

        public bool IsExit => anchor == "exit";
    }

    /// <summary>
    /// タイムライン区間 = 「周回 lap にゾーン（カメラ camera）へ滞在する区間」。キーは (lap, camera)。
    /// 同一キーの区間は 1 個（Web が保証・Unity は先勝ち）。lap は 1 始まり、camera はカメラ index。
    /// </summary>
    [Serializable] public sealed class ShowTimelineSegmentDef
    {
        public int lap;
        public int camera;

        /// <summary>
        /// v3 の本体（演出）。0..N 本。v2 の <see cref="cues"/> / <see cref="insert"/> は読み取り互換のみで、
        /// ロード時に <see cref="TimelineMigration.EnsureTakes"/> がここへ変換して埋める。
        /// </summary>
        public ShowTakeDef[] takes = System.Array.Empty<ShowTakeDef>();

        public ShowSegmentCueDef[] cues = System.Array.Empty<ShowSegmentCueDef>();
        public PostParams? post;          // 区間滞在中の post 上書き（segment > camera > global）
        public bool hasPost;              // present-flag
        public ShowInsertDef? insert;
        public bool hasInsert;            // present-flag
        public ShowBgmDef? bgm;           // 区間進入時の BGM 指示（無指示＝鳴っている曲が続く）
        public bool hasBgm;               // present-flag
    }

    /// <summary>
    /// show.json の timeline セクション（スキーマ v2）。rev で変更検出する。
    /// rev>0 && segments 非空なら旧 schedule.entries を supersede する。
    /// </summary>
    [Serializable] public sealed class ShowTimelineDef
    {
        public int rev;

        /// <summary>
        /// スキーマ版。0 / 未指定 = v2（<c>cues[]</c> / <c>insert</c>）、3 = v3（<c>takes[]</c>）。
        /// v3 なら新しい実行経路（TakeRunner）が担当し、v2 なら従来経路（CueScheduler / InsertController）が動く。
        /// </summary>
        public int schema;

        public ShowTimelineSegmentDef[] segments = System.Array.Empty<ShowTimelineSegmentDef>();

        /// <summary>区間が 1 つでもあれば present。</summary>
        public bool HasData() => segments != null && segments.Length > 0;

        /// <summary>v3 として実行すべきか（明示 schema か、takes を持つ区間が 1 つでもあれば v3）。</summary>
        public bool IsV3()
        {
            if (schema >= 3) return true;
            if (segments == null) return false;
            foreach (ShowTimelineSegmentDef? s in segments)
                if (s != null && s.takes != null && s.takes.Length > 0) return true;
            return false;
        }
    }
}
