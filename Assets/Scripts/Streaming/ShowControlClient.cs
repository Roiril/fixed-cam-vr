#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace FixedCamVr.Streaming
{
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
        // ⚠ 2026-08-15 にキーが入れ替わった（旧 unswapSec / openSec / restoreSec / holdSec）。
        //    旧キーしか持たない show.json（焼き込み・端末キャッシュ）では新キーが 0 になり、
        //    Sanitized() が既定へ倒すので**そのまま走る**。
        public float flickerSec = 6.0f;
        public float darkSec = 1.2f;
        public float reportFadeSec = 1.5f;

        /// <summary>キーごと無い（JsonUtility が 0 で埋めた）形か。</summary>
        public bool LooksUnset() =>
            !enabled && flickerSec <= 0f && darkSec <= 0f && reportFadeSec <= 0f;

        public OutroTiming ToTiming() => new OutroTiming
        {
            flickerSec = flickerSec, darkSec = darkSec, reportFadeSec = reportFadeSec,
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
        public float frameSec = 2.5f;
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

    /// <summary>
    /// Web オペレータ卓（show.json）の状態を long-poll で受けて Unity 側へ適用するクライアント。
    /// Screen GameObject（MjpegScreen / ScreenOverlayController と同居）に付ける。
    ///
    /// 適用対象:
    ///   - cameras[].host/port/auth → 各 CameraStream の接続先を実行時上書き（DHCP ズレを Web から復旧）
    ///   - cameras[].post           → カメラ別の画像加工（明るさ等）。アクティブカメラ切替時に適用
    ///   - post.*（global）         → カメラ別 post 未設定時のフォールバック（ショー全体グレーディング）
    ///   - control.activeCue        → ScreenOverlayController.PlayCue / StopOverlay
    ///   - control.cameraOverride   → registry.SetActive + ゾーン Tracker の無効化
    /// 逆方向: /unity/heartbeat へ 2s ごとに現状（アクティブカメラ・fps・発火中 cue）を報告。
    ///
    /// 永続化（Quest 単体ビルドで PC 不在でも Web 設定を参照するため）:
    ///   受信した cameras 設定 + post を persistentDataPath/show_config.json にキャッシュし、
    ///   次回起動時に server 接続前へ適用する。優先順位は 焼き込み.asset < 端末キャッシュ < ライブ long-poll。
    ///   サーバ不在でもキャッシュ適用とカメラ別 post（ゾーン切替連動）は動き続ける。
    /// </summary>
    public sealed class ShowControlClient : MonoBehaviour
    {
        private static readonly int ExposureId = Shader.PropertyToID("_Exposure");
        private static readonly int ContrastId = Shader.PropertyToID("_Contrast");
        private static readonly int SaturationId = Shader.PropertyToID("_Saturation");
        private static readonly int TemperatureId = Shader.PropertyToID("_Temperature");
        private static readonly int VignetteId = Shader.PropertyToID("_Vignette");
        private static readonly int GrainId = Shader.PropertyToID("_Grain");
        private static readonly int ScanlineId = Shader.PropertyToID("_Scanline");
        private static readonly int LiftId = Shader.PropertyToID("_Lift");
        private static readonly int TintId = Shader.PropertyToID("_Tint");
        private static readonly int AberrationId = Shader.PropertyToID("_Aberration");
        private static readonly int PixelateId = Shader.PropertyToID("_Pixelate");
        private static readonly int ScanlineCountId = Shader.PropertyToID("_ScanlineCount");

        [SerializeField] private ShowServerSource? server;
        [SerializeField] private CameraStreamRegistry? registry;

        [Tooltip("カメラ手動 override 中に無効化するゾーン Tracker（PlayerZoneTracker）。" +
                 "asmdef 循環回避のため Behaviour 参照で持つ。null なら override 時も Tracker は動き続ける。")]
        [SerializeField] private Behaviour? zoneTrackerToDisable;

        [Tooltip("heartbeat 送信間隔 (秒)。")]
        [SerializeField, Min(0.5f)] private float heartbeatInterval = 2f;

        [Tooltip("端末ローカルへ保存する設定キャッシュのファイル名（persistentDataPath 配下）。" +
                 "Quest 単体ビルドで PC 不在時、前回 Web で設定した IP / 画像加工を起動時に再適用する。")]
        [SerializeField] private string configCacheFileName = "show_config.json";

        [Tooltip("事前オーサリング済み cue スケジュールを駆動する CueScheduler。" +
                 "schedule / cue 解決 / activeCue 抑止状態を供給する。null なら自動発火なし。")]
        [SerializeField] private CueScheduler? cueScheduler;

        [Tooltip("カメラ override を出どころ Override として通す CameraSwitchDirector。" +
                 "null なら従来どおり registry.SetActive を直接叩く（後方互換・その場合 LapCounter 側は External 扱い）。")]
        [SerializeField] private CameraSwitchDirector? switchDirector;

        [Tooltip("タイムライン（show.json timeline スキーマ v2）を CueScheduler / InsertController / " +
                 "post 上書きへ分配する TimelineDirector。null なら timeline は無視され従来 schedule で動く（後方互換）。")]
        [SerializeField] private TimelineDirector? timelineDirector;

        [Tooltip("BGM 再生器。show.json の bgmTracks / bgm と区間 bgm 指示を受ける。" +
                 "null ならシーンから探す（[Bgm]）。見つからなければ BGM 制御なし（後方互換）。")]
        [SerializeField] private BgmDirector? bgmDirector;

        private ScreenOverlayController? _overlay;
        private Material? _material;
        private int _rev = -1;
        private string _appliedCue = "";
        private string _appliedOverride = "";

        // ---- 発見（discovery）連携用の公開状態 ----
        // DiscoveryClient が「PC 卓が不通か」を判定するために最後に /state 応答を得た時刻を持つ。
        // control.discoveryEnabled はキルスイッチ（false で probe/切替を全停止。省略時 true）。
        private float _lastServerContactTime = -999f;

        /// <summary>最後に /state を成功受信した realtimeSinceStartup。未接続なら大きな負値。</summary>
        public float LastServerContactTime => _lastServerContactTime;

        // コントローラ操作モード（NORMAL/REG）。OvrControllerBridge が遷移時に push し、heartbeat に載せる。
        private string _controllerMode = "NORMAL";

        // server 未設定、または /state を ServerStaleSeconds 受信できていない = 不通と見なす。
        // long-poll 上限(35s)より長くとり、健全な idle 接続を誤って不通判定しない。
        private const float ServerStaleSeconds = 40f;

        /// <summary>server が実効的に通じているか（cue 試射のローカルフォールバック分岐に使う）。</summary>
        private bool ServerReachable
            => server != null && (Time.realtimeSinceStartup - _lastServerContactTime) < ServerStaleSeconds;

        /// <summary>コントローラ操作モードのラベル（NORMAL/REG）を設定する。heartbeat で卓へ報告する。</summary>
        public void SetControllerMode(string mode) => _controllerMode = string.IsNullOrEmpty(mode) ? "NORMAL" : mode;

        /// <summary>
        /// いまのコントローラ操作モード（<c>NORMAL</c> / <c>REG</c>）。テレメトリが読む。
        ///
        /// ⚠ これが無かったせいで、<b>「トリガー長押しが発火していないのか、発火しても
        /// 画に出ていないのか」を実機ログから切り分けられなかった</b>（2026-08-09）。
        /// 卓の heartbeat にしか出ておらず、卓が居ない現場では観測手段がゼロだった。
        /// </summary>
        public string ControllerMode => _controllerMode;

        /// <summary>卓サーバの接続先設定（null なら卓連携なし）。DiscoveryClient が現エンドポイント比較に読む。</summary>
        public ShowServerSource? Server => server;

        /// <summary>show.json control.discoveryEnabled（省略時 true）。DiscoveryClient のキルスイッチ上書き。</summary>
        public bool DiscoveryEnabled { get; private set; } = true;

        /// <summary>index 番カメラが卓で手動固定（pinned）されているか。pinned には discovery を適用しない。</summary>
        public bool IsCameraPinned(int index)
            => index >= 0 && index < _cameras.Length && _cameras[index] != null && _cameras[index]!.pinned;

        // 直近に解決したカメラ別設定 / global post（ライブ or 端末キャッシュ由来）。
        // ゾーン自律切替（ActiveChanged）でカメラ別 post を再適用するため保持する。
        private CameraDef[] _cameras = Array.Empty<CameraDef>();
        private PostParams _globalPost = new PostParams();
        private bool _subscribed;

        // ---- post 上書き層（タイムライン）----
        // 区間 post は TimelineDirector が SetPostOverride で掛け外し（区間滞在中のみ）。
        // インサート表示中は _insertPostActive の insert 層が segment 層より優先する
        // （insert 中は insert.post ?? インサート先カメラ post ?? global。segment 層は素通ししない）。
        private PostParams? _segmentPostOverride;
        private bool _insertPostActive;
        private PostParams? _insertPostOverride;

        // ゾーン layout（ライブ or 端末キャッシュ由来）。Tracking 側（ZoneLayoutApplier）が読む。
        private ShowLayoutDef? _layout;
        private int _appliedLayoutRev = -1;

        // 素材スロットの束縛（control.slots 由来）。slot://name を実 URL へ解決するのに引く。
        // ラン中に卓が差し替えるので timeline とは独立に持つ（timeline を触ると once が再武装される）。
        private ShowSlotDef[] _slots = Array.Empty<ShowSlotDef>();

        // 端末内録画の設定（record 由来）。SegmentRecorder が読む。
        private ShowRecordDef? _record;

        /// <summary>端末内録画の設定（show.json <c>record</c>）。未指定なら null。</summary>
        public ShowRecordDef? RecordConfig => _record;

        // 体験の骨格（run 由来）。ShowRunDirector が読む。欠落ならコード既定で 3 周・導入あり。
        private ShowRunDef? _run;

        /// <summary>体験の骨格（show.json <c>run</c>）。未指定なら null（＝コード既定）。</summary>
        public ShowRunDef? RunConfig => _run;

        private ShowFeelDef? _feel;

        /// <summary>撮像の質（show.json <c>feel</c>）。未指定なら null（＝コード既定で効く）。</summary>
        public ShowFeelDef? FeelConfig => _feel;

        private CameraFeelFx? _feelFx;

        // 撮像の質の writer。シーンに居なければ何もしない（配線前でも体験は成立する）。
        private CameraFeelFx? ResolveFeelFx()
        {
            if (_feelFx != null) return _feelFx;
            _feelFx = FindObjectOfType<CameraFeelFx>();
            return _feelFx;
        }

        private void PushFeel() => ResolveFeelFx()?.Configure(_feel);

        /// <summary>いま効いている設定がどこから来たか（<c>none</c> / <c>baked</c> / <c>cache</c> / <c>live</c>）。
        ///
        /// **端末キャッシュは焼き込みより優先される**ので、古いキャッシュが残っていると APK を焼き直しても
        /// 設定が変わらない。実測（2026-07-31）で 2 台の Quest のキャッシュを突き合わせたところ、
        /// 片方にだけ <c>run.intro.startLineId</c> が無く、**導入の始まり方が機ごとに違っていた**。
        /// しかも <c>timeline.rev</c> は両方 21 で一致しており、rev では検出できない。
        ///
        /// 解析（tools/analyze-xp-log.py）は PC の show.json を期待値にするので、実機がどの設定で
        /// 走ったのかを言えないと「出なかった演出」を誤検出する。<see cref="DescribeConfig"/> と対で使う。</summary>
        public string ConfigOrigin { get; private set; } = "none";

        /// <summary>効いている設定の骨格を 1 行で要約する。解析器が PC の show.json から同じ要約を作って
        /// 突き合わせる。ハッシュではなく**項目を並べる**のは、食い違ったときにどこが違うかを名指しするため
        /// （ハッシュだと「違う」としか言えず、現場で直せない）。</summary>
        public string DescribeConfig()
        {
            int segs = _timeline?.segments?.Length ?? 0;
            int takes = 0;
            if (_timeline?.segments != null)
                foreach (var s in _timeline.segments) takes += s?.takes?.Length ?? 0;

            ShowIntroDef? intro = _run?.intro;
            bool introOn = (_run?.introEnabled ?? true) && (intro == null || intro.enabled);
            string startLine = intro != null && !string.IsNullOrEmpty(intro.startLineId)
                ? intro.startLineId : "-";

            // 録る周は指紋に入れる。ここがずれていると **rec カットが全部無言で飛ぶ**（録画は
            // ラン中にしか作れないので、後から気づいても取り返せない）。
            string recLaps = "-";
            if (_record != null && _record.enabled && _record.laps != null && _record.laps.Length > 0)
                recLaps = string.Join(",", _record.laps);

            return $"src={ConfigOrigin} rev={_rev} tlrev={(_timeline?.rev ?? -1)} " +
                   $"segs={segs} takes={takes} cues={_cues.Length} cams={_cameras.Length} " +
                   $"lines={(_layout?.lines?.Length ?? 0)} laps={(_run?.totalLaps ?? -1)} " +
                   $"intro={(introOn ? 1 : 0)} startLine={startLine} " +
                   $"rec={((_record?.enabled ?? false) ? 1 : 0)} recLaps={recLaps}";
        }

        // 卓からの手動グリッチ / 導入終了 / 体験終了の世代カウンタ（runEpoch と同じ「変化のみ発火」方式）。
        private int _knownGlitchEpoch;
        private bool _glitchEpochKnown;
        private int _knownIntroEpoch;
        private bool _introEpochKnown;
        private int _knownRunEndEpoch;
        private bool _runEndEpochKnown;

        // CG レイヤの人形定義（actors 由来）。ShowCgLayer が id で引く。
        private ShowActorDef[] _actors = Array.Empty<ShowActorDef>();

        /// <summary>CG レイヤの人形定義を id で引く。未定義なら null。</summary>
        public ShowActorDef? FindActor(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (ShowActorDef? a in _actors)
                if (a != null && a.id == id) return a;
            return null;
        }

        /// <summary>index 番カメラの course 空間姿勢（CG レイヤ用）。未著作なら false。</summary>
        public bool TryGetCameraPose(int index, out ShowCameraPoseDef pose)
        {
            pose = null!;
            if (index < 0 || index >= _cameras.Length) return false;
            CameraDef? c = _cameras[index];
            if (c == null || !c.hasPose || c.pose == null) return false;
            pose = c.pose;
            return true;
        }

        /// <summary>
        /// index 番カメラの**較正結果**（実映像から解いた実測値）。未較正なら false。
        /// 呼び出し側は受信中フレームの実寸・lensId で <see cref="ShowCameraCalibDef.MatchesSource"/> を
        /// 必ず確認すること（解像度やレンズを変えたら較正は無効）。
        /// </summary>
        public bool TryGetCameraCalib(int index, out ShowCameraCalibDef calib)
        {
            calib = null!;
            if (index < 0 || index >= _cameras.Length) return false;
            CameraDef? c = _cameras[index];
            if (c == null || !c.hasCalib || c.calib == null || !c.calib.IsUsable()) return false;
            calib = c.calib;
            return true;
        }

        /// <summary>index 番カメラが演出専用（<c>role:"fx"</c>）か。ゾーン割当・スタッフ巡回から外す。</summary>
        public bool IsFxCamera(int index)
            => index >= 0 && index < _cameras.Length && _cameras[index] != null
               && CameraRoles.IsFx(_cameras[index]!.role);

        // cue 定義（scheduler の cue 解決 + 手動 activeCue 発火の両方が引く）。
        private CueDef[] _cues = Array.Empty<CueDef>();
        // 事前オーサリング済みスケジュール（ライブ or 端末キャッシュ由来）。CueScheduler へ供給。
        private ShowScheduleDef? _schedule;
        private int _appliedScheduleRev = -1;
        // タイムライン（スキーマ v2・ライブ or 端末キャッシュ由来）。TimelineDirector へ供給。
        private ShowTimelineDef? _timeline;
        private int _appliedTimelineRev = -1;

        // BGM ライブラリ + ラン既定（ライブ or 端末キャッシュ由来）。BgmDirector へ供給。
        private ShowBgmTrackDef[] _bgmTracks = Array.Empty<ShowBgmTrackDef>();
        private ShowBgmDef? _bgmDefault;

        // timeline が有効（rev>0 && segments 非空）で、かつ TimelineDirector が配線されているか。
        // これが true の間だけ timeline が schedule.entries を supersede する。
        private bool TimelineActive
            => _timeline != null && _timeline.rev > 0
               && _timeline.segments != null && _timeline.segments.Length > 0;

        /// <summary>現在の layout（未設定なら null）。ZoneLayoutApplier が Rebuild で参照する。</summary>
        public ShowLayoutDef? Layout => _layout;

        /// <summary>
        /// 部屋の 3D プロキシ（<c>layout.room</c>）。未著作なら null。
        /// present-flag は **宣言 bool AND 実データ**で確定する（JsonUtility が null 入れ子を既定オブジェクトとして
        /// 書き戻すため、`!=null` 純導出だと端末キャッシュ往復で幽霊の部屋が湧く）。
        /// </summary>
        public ShowRoomDef? Room
        {
            get
            {
                ShowLayoutDef? l = _layout;
                if (l == null || !l.hasRoom) return null;
                return (l.room != null && l.room.HasData()) ? l.room : null;
            }
        }

        /// <summary>layout（cuts/floor/overlap 等）が変わった時に発火する。</summary>
        public event Action? LayoutChanged;

        /// <summary>
        /// レイアウトを直接差し替えて <see cref="LayoutChanged"/> を発火する **Editor プレビュー / デバッグ起動フック /
        /// テスト専用**フック。show.json を読まずに任意の <see cref="ShowLayoutDef"/> を注入して
        /// 登録ビュー（ZoneGridFootprint / ワイヤーフレーム）を組ませるためのもの。運用コード（long-poll・
        /// キャッシュ・焼き込み経路）からは呼ばない。ネットワーク系の状態（rev / server / heartbeat）には触れない。
        /// </summary>
        public void SetLayoutForPreview(ShowLayoutDef? layout)
        {
            _layout = layout;
            LayoutChanged?.Invoke();
        }

        /// <summary>周回巡回順（layout.course.order）。未設定なら空配列。LapCounter が読む。</summary>
        public int[] CourseOrder
            => _layout != null && _layout.course != null && _layout.course.order != null
                ? _layout.course.order
                : Array.Empty<int>();

        /// <summary>course（周回巡回順）が変わった時に発火する。LapCounter が購読して order を再取得する。</summary>
        public event Action? CourseChanged;

        // ---- ラン概念（runEpoch）----
        // control.runEpoch が変わった＝新しい体験者のランが始まった。周回リセット + cue 発火済みフラグ全消去。
        // 「既知値」を起動時にキャッシュ / 焼き込みから初期化し、以後の変化のみ発火する
        // （起動のたびに誤リセットしない）。
        private int _knownRunEpoch;

        // 走行中の演出を中止する世代（control.takeAbortEpoch）。runEpoch と同じ「変化のみ発火」方式。
        // 起動時は初回 Apply / 焼き込みロードで現在値へ同期し、そこからの増分だけを中止として扱う。
        private int _knownTakeAbortEpoch;
        private bool _takeAbortEpochKnown;

        // カメラ切替の現場調整（control 由来。0=未指定でコード既定）。ライブ / キャッシュ / 焼き込みで更新し
        // ApplySwitchTiming で CameraSwitchDirector へ流す。キャッシュへ往復させ PC 不在起動でも値を保つ。
        private float _switchDwellSec;
        private float _switchCooldownSec;

        /// <summary>ラン開始（runEpoch 変化）で発火する。LapCounter が購読して周回・cue をリセットする。</summary>
        public event Action? RunReset;

        /// <summary>heartbeat に載せる現在周回数の供給元（LapCounter が注入。null なら -1）。</summary>
        public Func<int>? CurrentLapProvider;

        // heartbeat に載せる HMD の course space XZ・現在ゾーンラベルの供給元。
        // Streaming → Tracking の参照を作らないため Func で注入する（ZoneLayoutApplier が設定）。
        /// <summary>HMD 位置を course space XZ で返す供給元（null なら heartbeat に載せない）。</summary>
        public Func<Vector2>? HeadCourseXZProvider;
        /// <summary>現在ゾーンのラベルを返す供給元（null なら空文字）。</summary>
        public Func<string>? CurrentZoneLabelProvider;
        /// <summary>
        /// course space の (XZ, y) をワールド座標へ変換する供給元（CourseFrame.CourseToWorld を注入）。
        /// CG レイヤの仮想カメラ・人形を位置合わせ済みの実空間へ置くのに使う。null ならワールド＝course。
        /// </summary>
        public Func<Vector2, float, Vector3>? CourseToWorldProvider;
        /// <summary>course space の yaw（度）を返す供給元（CourseFrame.YawDeg を注入）。null なら 0。</summary>
        public Func<float>? CourseYawProvider;
        /// <summary>
        /// HMD 位置合わせ（登録）が済んでいるかの供給元（CourseFrame.HasRegistration を注入）。
        /// **未登録のまま CG を出すと人形が全く違う場所に立つ**（course→world が identity へ落ちるため）。
        /// null なら「登録の有無を知らない」＝ CG を止めない（オフラインテスト・Editor プレビュー用）。
        /// </summary>
        public Func<bool>? CourseRegisteredProvider;

        /// <summary>
        /// 「要再登録」の供給元（CourseFrame.NeedsReRegistration を注入）。OS の recenter で立つ。
        /// **これが立ったまま体験を始めると、ゾーンが実空間に対してズレたまま動く**
        /// （「歩いても切り替わらない」の最有力原因）。HMD 内には出るが体験者が被っている間は
        /// 誰も読めないので、heartbeat で卓の本番前チェックへ届ける（2026-07-28）。
        /// </summary>
        public Func<bool>? CourseNeedsReRegProvider;

        /// <summary>
        /// 位置合わせが<b>確定保存された時刻</b>の供給元（CourseFrame.SavedAtIso を注入）。
        /// これが変わった＝スタッフが B で確定した、という<b>事象</b>を検出するために使う。
        ///
        /// <see cref="IntroDirector"/> が導入の中止から自力で復帰するのに要る。中止の復帰は本来
        /// 「① 位置合わせを撃ち直す ② ランリセット」の 2 段で、**順序を逆にすると直らない**
        /// （先にランリセットしても要再登録フラグが立ったままで即また中止される）。人間に順序を
        /// 暗記させる代わりに、確定を検出して導入をやり直すことで 1 段にする。
        ///
        /// ⚠ <c>CourseNeedsReRegProvider</c> の false 化では代用できない。プレビュー
        /// （<c>SetRegistration(save:false)</c>）でもフラグは降りるので、B 確定の前に導入が再開して
        /// 登録ビューと演出が混ざる。<c>SavedAtIso</c> は <c>SaveRegistration</c> でしか変わらない。
        /// </summary>
        public Func<string>? CourseRegistrationStampProvider;

        private string ConfigCachePath => Path.Combine(Application.persistentDataPath, configCacheFileName);

        [Serializable] private class ShowState
        {
            public int rev;
            public CameraDef[] cameras = Array.Empty<CameraDef>();
            public CueDef[] cues = Array.Empty<CueDef>();
            public PostParams? post;
            public ControlState? control;
            public ShowLayoutDef? layout;
            public ShowScheduleDef? schedule;
            public ShowTimelineDef? timeline;   // スキーマ v2（present なら schedule を supersede）
            public ShowBgmTrackDef[]? bgmTracks;  // BGM ライブラリ
            public ShowBgmDef? bgm;               // ラン既定 BGM（IsActionable() が present 判定）
            public ShowRecordDef? record;         // 端末内録画の設定（欠落 = 無効）
            public ShowActorDef[]? actors;        // CG レイヤの人形定義
            public ShowRunDef? run;               // 体験の骨格（周数・導入・終端）。欠落 = コード既定
            public ShowFeelDef? feel;             // 撮像の質（装置らしさ）。欠落 = コード既定
        }
        [Serializable] private class CameraDef
        {
            public string id = "";
            public string sourceId = "";
            public string host = "";
            public int port;
            public string auth = "";       // "user:pass"（空=認証なし）
            public PostParams? post;        // カメラ別画像加工（null=global にフォールバック）
            // JsonUtility は null の入れ子クラスを既定値オブジェクトとして書き出すため、
            // キャッシュ往復後に post の null 判定が壊れる。「個別 post を持つか」は明示 bool を正にする
            // （ライブ受信パース直後に post!=null から確定し、キャッシュにも保存して往復させる）。
            public bool hasPost;
            // 卓で host を手入力すると自動で true。true のカメラには DiscoveryClient が
            // 発見層を適用しない（手動固定を尊重）。キャッシュへも往復させる（CachedConfig.cameras 経由）。
            public bool pinned;
            // "zone"（既定・ゾーンに割り当てる）| "fx"（演出専用。ゾーン自動切替にもスタッフ巡回にも出さない）
            public string role = CameraRoles.Zone;
            // CG レイヤの仮想カメラが構える姿勢（course 空間）。hasPose が present-flag。
            // **人がドラッグして置く概算**で、較正の解ではない（calib があればそちらが勝つ）。
            public ShowCameraPoseDef? pose;
            public bool hasPose;
            // 較正の解（卓が実映像上の床点から解いた実測値）。hasCalib が present-flag。
            // 空の既定オブジェクトが来ても IsUsable() が false になるので幽霊武装しない。
            public ShowCameraCalibDef? calib;
            public bool hasCalib;
        }

        // 端末ローカルへ保存する設定キャッシュ（show.json のうち実機が参照する部分のみ）。
        // course は layout に内包されるため layout の保存で往復する。
        [Serializable] private class CachedConfig
        {
            public CameraDef[] cameras = Array.Empty<CameraDef>();
            public PostParams? post;
            public ShowLayoutDef? layout;
            public CueDef[] cues = Array.Empty<CueDef>();
            public ShowScheduleDef? schedule;
            public ShowTimelineDef? timeline;   // スキーマ v2（オフライン supersede 用）
            public ShowBgmTrackDef[] bgmTracks = Array.Empty<ShowBgmTrackDef>();
            public ShowBgmDef? bgm;             // ラン既定 BGM（PC 不在起動でも同じ曲で始まる）
            public ShowRecordDef? record;       // 端末内録画の設定（PC 不在でも録れるように往復させる）
            public ShowActorDef[] actors = Array.Empty<ShowActorDef>();
            public ShowFeelDef? feel;           // 撮像の質（PC 不在でも同じ画で始まる）
            // 体験の骨格（PC 不在の現地でも 3 周で終わるように往復させる）。
            public ShowRunDef? run;
            // 直近に既知だった runEpoch。起動時にこれを「既知値」として復元し、
            // PC 不在の再起動で同一 epoch を誤リセットしない。
            public int runEpoch;
            // カメラ切替の現場調整（control 由来）。0=未指定でコード既定。PC 不在起動でも値が生きるよう往復させる。
            public float switchDwellSec;
            public float switchCooldownSec;

            /// <summary>
            /// このキャッシュを書いた APK の識別子（<c>Application.buildGUID</c>）。
            ///
            /// ⚠⚠ <b>キャッシュは焼き込みより優先されるので、これが無いと「APK を焼き直しても
            /// 設定が変わらない」</b>。2026-07-31 に PC の show.json・焼き込み・Quest 2 台の
            /// キャッシュの<b>4 者がずれていて正しいのは 1 つだけ</b>という実測がある
            /// （片方の機にだけ古い <c>startLineId</c> が残り、導入の始まり方が機ごとに違った）。
            /// しかも <c>timeline.rev</c> は全部一致していたので<b>版番号では気づけない</b>。
            /// </summary>
            public string buildGuid = "";
        }
        [Serializable] private class CueDef
        {
            public string id = "";
            public string name = "";
            public string maskUrl = "";
            public string sourceUrl = "";
            public float strength = 1f;
            public bool loop = true;
            public float fadeIn = 0.5f;
            public float fadeOut = 0.5f;
            public float trimStart = 0f;
            public float trimEnd = 0f;   // <=0 = 最後まで

            // 色統計マッチング（企画書 2.3「差し替え素材の全体には色統計マッチングを施し、実写映像と
            // 継ぎ目なく合成する」）。卓が cue 保存時に Reinhard per-channel を解いて **gain/offset へ落として**
            // 焼く。実機は overlay サンプル後に out = src*gain + offset を掛けるだけ（6 float・ほぼ無料）。
            // 統計そのものを実機で毎フレーム取ると Quest には重く、しかもマスク領域が動かない固定視点では
            // 事前に解ける値なので、卓で解いて配るのが正しい分担。
            public bool hasMatch;
            public float[] matchGain = { 1f, 1f, 1f };
            public float[] matchOffset = { 0f, 0f, 0f };
        }
        // PostParams は public トップレベルへ昇格済み（ファイル冒頭）。CameraDef.post / _globalPost /
        // タイムライン各定義 / SetPostOverride が共有する。
        // discoveryEnabled は「省略時 true」を守るため C# 初期化子で true にする
        // （JsonUtility.FromJson は既定コンストラクタで初期化子を走らせてから present なキーだけ上書きするため、
        //  JSON にキーが無ければ true が残る。control ブロックごと無い場合も呼び出し側が true 扱いにする）。
        [Serializable] private class ControlState
        {
            public string? activeCue;
            public string? cameraOverride;
            public bool discoveryEnabled = true;
            // 体験者 1 人分のラン識別子。Web の「ラン開始」で ++ される。変化＝周回 / cue のリセット。
            public int runEpoch;
            // 走行中の演出（Take）の中止世代。Web の「■ 画面を取り返す」で ++ される。
            // ⚠ activeCue を空にする形では伝わらない（自動発火の演出は activeCue が空のまま走るため）。
            public int takeAbortEpoch;
            // カメラ切替の現場調整（CameraSwitchDirector へ流す）。present 判定は「>0 で適用 / 0=未指定でコード既定」。
            public float minDwellSec;
            public float switchCooldownSec;
            // 素材スロットの束縛（slot://name → 実 URL）。timeline を触らずに素材だけ差し替えるための面。
            public ShowSlotDef[] slots = Array.Empty<ShowSlotDef>();

            // ゾーン切替そのものに重ねる「映像の乱れ」の強さ（0 = 重ねない）。
            public float switchGlitch;
            // 卓からの手動発火。**変化**で 1 回走る（「空を空にする」形では伝わらないため世代カウンタ）。
            public int glitchEpoch;
            public float glitchLevel;
            public float glitchSec;

            // 導入を終えて本編へ進める合図（世代カウンタ）。卓の「⏭ 導入を終える」。
            public int introAdvanceEpoch;
            // 体験を終える合図（世代カウンタ）。卓の「■ 体験を終える」。
            public int runEndEpoch;
        }

        private void Awake()
        {
            _overlay = GetComponent<ScreenOverlayController>();
            var renderer = GetComponent<Renderer>();
            _material = renderer != null ? renderer.material : null;

            // ゾーン自律切替・オペレータ override でアクティブカメラが変わったら
            // そのカメラ別 post を貼り直す（server 不在でも効かせたいので Awake で購読）。
            if (registry != null && !_subscribed)
            {
                registry.ActiveChanged += OnActiveCameraChanged;
                _subscribed = true;
            }
        }

        private void Start()
        {
            // 優先順位: 焼き込み StreamingAssets < 端末キャッシュ < ライブ long-poll（後勝ち）。
            // 焼き込みの読込は UnityWebRequest（Android は jar: URL）なので非同期。
            // registry の Awake（stream 生成）が済んだ後に、この初期化列を回す。
            _ = RunInitAsync();
        }

        private async Task RunInitAsync()
        {
            try { await InitializeAsync(destroyCancellationToken); }
            catch (OperationCanceledException) { }
            catch (Exception e) { Debug.LogWarning($"[ShowControl] 初期化失敗: {e.Message}"); }
        }

        private async Task InitializeAsync(CancellationToken ct)
        {
            // 1) 焼き込み StreamingAssets/show/show.json（最下位）。無ければ何もしない。
            await LoadBakedShowAsync(ct);
            // 2) 端末キャッシュ（焼き込みを上書き）。ライブが既に適用済みなら両方スキップ（ライブ優先）。
            if (_rev < 0) LoadAndApplyCache();
            // 2.5) 録画係。**卓が居なくても録れなければならない**（1 周目を録って 3 周目に流すのは
            //      現地 PC 不在でも成立する体験）。旧実装はライブ受信の Apply でしか EnsureRecorder を
            //      呼んでおらず、焼き込み / 端末キャッシュの record.enabled は読むだけで録画係が
            //      生成されず、Quest 単体では 1 フレームも録れなかった（2026-07-29 修正）。
            EnsureRecorder();
            // 2.6) 体験の骨格。録画係と同じ理由で**卓が居なくても成立させる**（3 周で終わることは
            //      現地 PC 不在でも体験の一部）。相は Intro から始まる（起動＝導入）。
            ResolveRunDirector()?.Configure(_run);
            PushFeel();
            // 3) 統合後の接続先・post を一度反映（焼き込み/キャッシュのどちらが勝っても 1 回）。
            ApplyCameraEndpoints();
            ApplyPostForActive();
            // 3.5) カメラ切替タイミング（焼き込み / キャッシュ由来。未指定なら Director がコード既定へ戻す）。
            ApplySwitchTiming();
            // 3.6) BGM。トラック表 + ラン既定を供給して再生を開始する（show.json に bgm 指定が
            //      無ければ BgmDirector の既定クリップ = 従来の固定ループがそのまま鳴る）。
            var bgm = ResolveBgmDirector();
            if (bgm != null)
            {
                bgm.SetServer(server);
                PushBgm();
                bgm.Begin();
            }
            // 4) 周回順・スケジュールを LapCounter / CueScheduler へ供給。
            PushCourseAndSchedule();
            // 5) layout / course が入っていれば通知（ZoneLayoutApplier / LapCounter が再取得）。
            if (_layout != null)
            {
                LayoutChanged?.Invoke();
                CourseChanged?.Invoke();
            }
        }

        private void OnDestroy()
        {
            if (registry != null && _subscribed)
            {
                registry.ActiveChanged -= OnActiveCameraChanged;
                _subscribed = false;
            }
        }

        // enable 期間だけ生きる CTS。destroy token と束ねて disable / destroy 両方で止める。
        // これが無いと disable→enable のたびに long-poll / heartbeat が多重起動する。
        private CancellationTokenSource? _loopCts;

        private void OnEnable()
        {
            // 実測滞在時間の計時はサーバの有無と無関係に回す（卓が後から立ち上がっても
            // 直近の滞在を送れるように送信待ちへ溜めておく。上限 64 で古い方から捨てる）。
            SubscribeDwell();

            // server 未設定でも component は生かす（端末キャッシュ適用・カメラ別 post の
            // ゾーン切替連動は server なしで成立する）。long-poll / heartbeat だけスキップ。
            if (server == null)
            {
                Debug.Log("[ShowControl] server 未設定。オペレータ卓なしで続行（キャッシュ設定のみ適用）。");
                return;
            }
            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
            _ = PollLoopAsync(_loopCts.Token);
            _ = HeartbeatLoopAsync(_loopCts.Token);
        }

        private void OnDisable()
        {
            UnsubscribeDwell();
            _loopCts?.Cancel();
            _loopCts?.Dispose();
            _loopCts = null;
        }

        // ---- 実測滞在時間（区間 (lap, camera) にどれだけ居たか）----
        //   卓のリボン UI が「進入 +20s の演出が実測平均 8s の区間に置かれている」を検出するための一次データ。
        //   駆動はショーの時計（CueScheduler.CameraEntered = ZoneCommitted 由来）だけ＝画面の切替では動かない。

        private readonly SegmentDwellLog _dwell = new SegmentDwellLog();
        private bool _dwellSubscribed;

        private void SubscribeDwell()
        {
            if (_dwellSubscribed) return;
            // 既存シーンで cueScheduler が未配線でも成立させる（ResolveXxx と同流儀）。
            if (cueScheduler == null) cueScheduler = FindObjectOfType<CueScheduler>();
            if (cueScheduler == null) return;
            cueScheduler.CameraEntered += OnSegmentEnteredForDwell;
            _dwellSubscribed = true;
        }

        private void UnsubscribeDwell()
        {
            if (!_dwellSubscribed || cueScheduler == null) { _dwellSubscribed = false; return; }
            cueScheduler.CameraEntered -= OnSegmentEnteredForDwell;
            _dwellSubscribed = false;
        }

        // CueScheduler の引数順は (camera, lap)。区間キーは (lap, camera) なので入れ替えて渡す。
        private void OnSegmentEnteredForDwell(int camera, int lap)
            => _dwell.Enter(lap, camera, Time.realtimeSinceStartup);

        /// <summary>
        /// long-poll / heartbeat ループを掴み直す（server の接続先が発見で張り替わった時に呼ぶ）。
        /// 進行中の long-poll（最大 35s ハング）を即座に切って新エンドポイントで再起動する
        /// （URL 自体は BuildUrl が毎回 EffectiveHost を読むので次周回で反映されるが、
        ///  ハング中の request を待たせないためループごと差し替える。OnEnable と同じ _loopCts 再生成方式）。
        /// </summary>
        public void RestartServerLoop()
        {
            if (server == null) return;
            _loopCts?.Cancel();
            _loopCts?.Dispose();
            _loopCts = null;
            if (!isActiveAndEnabled) return;
            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
            _ = PollLoopAsync(_loopCts.Token);
            _ = HeartbeatLoopAsync(_loopCts.Token);
        }

        /// <summary>
        /// 発見した PC 卓（role="show-server"）の現在 IP を server へ反映しループを掴み直す。
        /// DiscoveryClient が「server 未通 N 秒」を確認した時のみ呼ぶ。接続先が実際に変わった時だけ再起動。
        /// </summary>
        public void ApplyDiscoveredServer(string host, int port)
        {
            if (server == null)
            {
                Debug.LogWarning("[ShowControl] server 未設定のため発見した show-server を適用できない（ShowServer.asset を割り当てよ）。");
                return;
            }
            string before = server.Endpoint;
            server.ApplyRuntimeEndpoint(host, port);
            if (server.Endpoint != before)
            {
                Debug.Log($"[ShowControl] 発見した show-server を適用: {host}:{port}（loop 再起動）");
                RestartServerLoop();
            }
        }

        private void OnActiveCameraChanged(int _) => ApplyPostForActive();

        // カメラ override を出どころ Override として適用する（LapCounter が周回に数えないため）。
        // director があればそこを通し、無ければ従来どおり registry を直接叩く（後方互換）。
        private void SetActiveOverride(int index)
        {
            var dir = ResolveSwitchDirector();
            if (dir != null)
                dir.SetActiveExternal(index, CameraSwitchDirector.SwitchSource.Override);
            else
                registry?.SetActive(index);
        }

        // switchDirector 参照を遅延解決する。既存シーンの YAML に SerializeField 参照が焼かれていなくても
        // （シーン再生成前のビルドで動く）タイミング現場調整・Override 経路を成立させるためのフォールバック。
        // ShowControlClient は Screen GameObject（Director と同居）に付くため GetComponent が第一選択。
        private CameraSwitchDirector? ResolveSwitchDirector()
        {
            if (switchDirector != null) return switchDirector;

            switchDirector = GetComponent<CameraSwitchDirector>();
            if (switchDirector != null) return switchDirector;

            // シーン全体からの拾い上げは最後の手段。**自分と違う registry を回している Director は採らない**
            // （別リグ・EditMode テストの偽 registry と本物のシーンが混ざると、切替が別のカメラ群へ飛ぶ。
            //   2026-07-27 実害: Main.unity を開いたまま EditMode を回すと override のテストが赤くなった）。
            CameraSwitchDirector? found = FindObjectOfType<CameraSwitchDirector>();
            if (found != null && (registry == null || found.Registry == null || found.Registry == registry))
                switchDirector = found;
            return switchDirector;
        }

        // control 由来のカメラ切替タイミングを Director へ流す（present 判定は Director 側の ResolveTiming）。
        private void ApplySwitchTiming()
            => ResolveSwitchDirector()?.ApplyTimingOverride(_switchDwellSec, _switchCooldownSec);

        // ラン開始（runEpoch 変化 / 現地手動）。cue 発火済みを消し、周回リセットを購読者（LapCounter）へ通知する。
        // cueScheduler.ResetRun は LapCounter 未配線でもスケジューラ単独で成立させるための直接呼び（LapCounter.ResetRun でも呼ぶが冪等）。
        private void TriggerRunReset()
        {
            Debug.Log($"[ShowControl] ラン開始（runEpoch={_knownRunEpoch}）: 周回 / cue / タイムライン / BGM をリセット");
            VisitorMarkCount = 0;   // 体験 1 回ぶんの状態（memory/show_run_skeleton.md）
            _dwell.Reset();   // 計時中の部分区間は「体験者 1 人分の滞在」として成立しないので捨てる
            cueScheduler?.ResetRun();
            timelineDirector?.ResetRun();
            ResolveBgmDirector()?.ResetRun();
            // 端末内録画も世代を切り替える（前の体験者の映像を次のランへ持ち越さない・端末に残さない）。
            ResolveRecorder()?.ResetRun(_knownRunEpoch);
            RunReset?.Invoke();
            // 骨格も頭から（導入があれば導入から）。ここが「新しい体験者」の唯一の入口。
            ResolveRunDirector()?.BeginRun();
        }

        /// <summary>
        /// 現地の「新しい体験者」（右グリップ 2 秒長押し）。卓の ▶ ラン開始と**同じ号令元**を通す。
        ///
        /// 旧実装はコントローラ側が TimelineDirector / SegmentRecorder / LapCounter / BgmDirector を
        /// 個別に叩いており、ラン開始が 2 系統に割れていた（実測滞在が前の体験者の分と混ざる・
        /// 骨格の相が戻らない）。処理を 1 か所へ寄せる。
        /// </summary>
        public void BeginNewVisitorRunLocal()
        {
            Debug.Log("[ShowControl] ラン開始（現地・右グリップ長押し）");
            ReleaseLiveHolds();
            VisitorMarkCount = 0;   // 体験 1 回ぶんの状態（memory/show_run_skeleton.md）
            _dwell.Reset();
            cueScheduler?.ResetRun();
            timelineDirector?.ResetRun();
            ResolveBgmDirector()?.ResetRun();
            // 現地は卓と runEpoch を共有できないので、録画側は自分で世代を進める。
            ResolveRecorder()?.ResetRunLocal();
            RunReset?.Invoke();
            ResolveRunDirector()?.BeginRun();
        }

        /// <summary>
        /// <b>卓が前の体験者のために掛けた「画面の占有」を現地で外す。</b>
        ///
        /// ⚠⚠ 卓の ▶ ラン開始は `cameraOverride` / `activeCue` / `slots` を空にしてから
        /// `runEpoch` を進めるが、<b>現地の右グリップ長押しはそれらに触れなかった</b>
        /// （サーバへ書けないので当然だが、<b>ローカルの適用状態まで残していた</b>）。
        /// 壊れる手順は現場で普通に起きる:
        /// <b>① 体験者 A の最中に卓でカメラ固定か手動 cue を使う ② PC が落ちる・卓を閉じる・
        /// Wi-Fi が切れる ③ スタッフが右グリップでリセットして体験者 B を始める</b>。
        /// B には前の手動映像が残り、ゾーンの自動切替は凍結されたままになる。
        ///
        /// ⚠ 卓が生きているなら次の long-poll で卓の状態が正として戻ってくる（それでよい —
        /// 卓が居るなら卓の ▶ を使う）。ここが効くのは<b>卓が落ちている現場</b>。
        /// </summary>
        private void ReleaseLiveHolds()
        {
            bool hadCue = !string.IsNullOrEmpty(_appliedCue);
            bool hadOverride = !string.IsNullOrEmpty(_appliedOverride);
            if (!hadCue && !hadOverride) return;

            string prevOverride = _appliedOverride;
            _appliedCue = "";
            _appliedOverride = "";
            _overlay?.StopOverlay();
            // override 中は tracker を止めてある。戻すと OnEnable が記憶ゾーンを捨てて
            // 現在位置から引き直す（＝通常経路でゾーンのカメラへ復帰する）。
            if (zoneTrackerToDisable != null) zoneTrackerToDisable.enabled = true;
            ResolveSwitchDirector()?.SetOverrideActive(false);
            cueScheduler?.SetLiveCueActive(false);
            timelineDirector?.SetSuppressed(false);
            Debug.LogWarning("[ShowControl] 卓が掛けたままだった占有を現地で外しました"
                             + $"（固定={(hadOverride ? prevOverride : "-")} 演出={(hadCue ? "あり" : "-")}）");
        }

        /// <summary>
        /// 導入を終えて本編（1 周目）を始める。<see cref="ShowRunDirector"/> だけが呼ぶ。
        ///
        /// <b>端末内録画の世代は切り替えない。</b> 録画のリセットは「新しい体験者」だけの仕事で、
        /// ここに載せるとラン開始が複数系統に増え、遅れて届いた runEpoch が 1 周目の録画を消す経路ができる
        /// （企画書 3 周目の素材が黙って消える）。
        /// </summary>
        public void BeginMainRun()
        {
            _dwell.Reset();
            cueScheduler?.ResetRun();
            timelineDirector?.ResetRun();
            ResolveBgmDirector()?.ResetRun();
            RunReset?.Invoke();   // LapCounter が lap=1 / pos=0 / 現在ゾーンの再シード
        }

        // 骨格の実行体。既存シーンに未配置でも自分で載せる（prefab / シーンの SerializeField 欠落で
        // 機能が全死した過去の事故対策。EnsureRecorder と同流儀）。
        private ShowRunDirector? _runDirector;

        /// <summary>
        /// 導入演出の実行体。**居なければ null を返すだけで自動生成はしない** — 導入は
        /// シーンに焼かれた覆い・線・パススルー面と組でしか成立しないので、
        /// コンポーネントだけ湧かせても何も見えない（`Setup Main Demo Scene` の再実行が要る）。
        /// </summary>
        private IntroDirector? _introDirector;
        private IntroDirector? ResolveIntroDirector()
        {
            if (_introDirector != null) return _introDirector;
            _introDirector = FindObjectOfType<IntroDirector>();
            return _introDirector;
        }

        private ShowRunDirector? ResolveRunDirector()
        {
            if (_runDirector != null) return _runDirector;
            _runDirector = FindObjectOfType<ShowRunDirector>();
            if (_runDirector == null)
            {
                var go = new GameObject("[ShowRun]");
                _runDirector = go.AddComponent<ShowRunDirector>();
                _runDirector.Configure(_run);
                Debug.Log("[ShowControl] 体験の骨格（ShowRunDirector）をシーンへ自動生成した");
            }
            return _runDirector;
        }

        /// <summary>体験の骨格（相・経過・周数）。卓の heartbeat と HMD 表示が読む。</summary>
        public ShowRunDirector? RunDirector => ResolveRunDirector();

        // 既存シーンで未配線でも動くよう遅延解決する（BgmDirector と同流儀）。
        private Recording.SegmentRecorder? _recorder;

        private Recording.SegmentRecorder? ResolveRecorder()
        {
            if (_recorder != null) return _recorder;
            _recorder = FindObjectOfType<Recording.SegmentRecorder>();
            return _recorder;
        }

        /// <summary>
        /// show.json の <c>record.enabled</c> が立っていて録画係がシーンに居なければ自分で載せる。
        /// prefab / シーンの SerializeField 欠落で機能が全死した過去の事故を繰り返さないための自己修復
        /// （<see cref="TimelineDirector"/> が <see cref="TakeRunner"/> を載せるのと同じ流儀）。
        /// </summary>
        private void EnsureRecorder()
        {
            if (_record == null || !_record.enabled) return;
            if (ResolveRecorder() != null) return;
            var go = new GameObject("[SegmentRecorder]");
            _recorder = go.AddComponent<Recording.SegmentRecorder>();
            _recorder.ResetRun(_knownRunEpoch);
            Debug.Log("[ShowControl] 端末内録画が有効なので SegmentRecorder を自動生成した");
        }

        // ---- BGM（bgmTracks / ラン既定 / 区間指示は TimelineDirector 経由）----

        // 既存シーンで bgmDirector が未配線でも動くよう遅延解決する（post/switch の ResolveXxx と同流儀）。
        private BgmDirector? ResolveBgmDirector()
        {
            if (bgmDirector != null) return bgmDirector;
            bgmDirector = FindObjectOfType<BgmDirector>();
            return bgmDirector;
        }

        // トラック表と既定 BGM を BgmDirector へ流す。既定は「実際に変わった時だけ」適用する
        // （show.json の rev はカメラ設定の変更等でも上がるため、毎回適用すると曲が鳴り直す）。
        private string _appliedBgmSignature = "\u0000";
        private void PushBgm()
        {
            var dir = ResolveBgmDirector();
            if (dir == null) return;
            dir.SetTracks(_bgmTracks);
            string sig = _bgmDefault == null ? ""
                : $"{_bgmDefault.action}|{_bgmDefault.trackId}|{_bgmDefault.loop}|{_bgmDefault.startSec}|"
                  + $"{_bgmDefault.loopStartSec}|{_bgmDefault.loopEndSec}|{_bgmDefault.volume}";
            if (sig == _appliedBgmSignature) return;
            _appliedBgmSignature = sig;
            dir.SetShowDefault(_bgmDefault, _bgmDefault != null);
        }

        // ---- state long-poll ----

        private async Task PollLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    string url = server!.BuildUrl($"/state?rev={_rev}");
                    using var req = UnityWebRequest.Get(url);
                    req.timeout = 35; // サーバ側 long-poll 上限 25s より長く
                    var op = req.SendWebRequest();
                    while (!op.isDone)
                    {
                        ct.ThrowIfCancellationRequested();
                        await Task.Yield();
                    }
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        await Task.Delay(2000, ct); // サーバ不在。静かにリトライ
                        continue;
                    }
                    // /state を成功受信した = 卓が通じている。DiscoveryClient の「不通 N 秒」判定の基準。
                    _lastServerContactTime = Time.realtimeSinceStartup;
                    var state = JsonUtility.FromJson<ShowState>(req.downloadHandler.text);
                    if (state == null)
                    {
                        // 200 で空/壊れ JSON を返し続ける異常系でホットループにしない
                        await Task.Delay(1000, ct);
                        continue;
                    }
                    if (state.rev != _rev)
                    {
                        _rev = state.rev;
                        Apply(state);
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ShowControl] poll error: {e.Message}");
                    try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return; }
                }
            }
        }

        private void Apply(ShowState state)
        {
            ConfigOrigin = "live";  // 卓が配った設定。以後キャッシュ・焼き込みでは上書きされない
            // 1) カメラ設定（IP / 認証 / カメラ別画像加工）を反映
            _cameras = state.cameras ?? Array.Empty<CameraDef>();
            // ライブ受信パース直後だけ post / pose の null 判定が信頼できる → ここで present-flag を確定
            foreach (var c in _cameras)
            {
                if (c == null) continue;
                c.hasPost = c.post != null && !c.post.IsDefaultLike();  // 素通しの実体は「未著作」扱い（卓が "post": null を書くため）
                c.hasPose = c.pose != null;
                c.hasCalib = c.calib != null && c.calib.IsUsable();
            }
            ApplyCameraEndpoints();
            if (state.post != null) _globalPost = state.post;
            ApplyPostForActive(); // global + アクティブカメラの個別 post をマテリアルへ

            // 1.3) cue 定義を保持（scheduler の cue 解決 + 手動 activeCue 発火が引く）。
            _cues = state.cues ?? Array.Empty<CueDef>();
            // 1.35) 端末内録画の設定 / CG 人形の定義 / 素材スロットの束縛。
            _record = state.record;
            _actors = state.actors ?? Array.Empty<ShowActorDef>();
            _slots = state.control?.slots ?? Array.Empty<ShowSlotDef>();
            EnsureRecorder();
            // 1.37) 体験の骨格。設定の反映だけで、相は動かさない（相を動かすのは runEpoch と明示操作だけ）。
            _run = state.run;
            ResolveRunDirector()?.Configure(_run);
            // 1.38) 撮像の質（装置らしさ）。キーが無くても既定で効くので、null をそのまま渡してよい。
            _feel = state.feel;
            PushFeel();

            // 1.5) ゾーン layout（cuts/floor/overlap/course）。JsonUtility は null 入れ子を既定値で書くため
            //      「cuts が空でない」を present 判定に使い、rev で変更検出する。course は layout に内包。
            bool layoutChanged = false;
            if (state.layout != null && state.layout.HasData()
                && state.layout.rev != _appliedLayoutRev)
            {
                _layout = state.layout;
                _appliedLayoutRev = state.layout.rev;
                layoutChanged = true;
            }

            // 1.6) スケジュール（rev で変更検出）。rev>0 を present 判定に使い、JsonUtility が
            //      schedule 欠落時に書く既定オブジェクト（rev=0）で焼き込み/キャッシュを潰さない。
            //      実際の CueScheduler への供給は timeline supersede を調停する PushCueSource で行う。
            bool cueSourceChanged = false;
            if (state.schedule != null && state.schedule.rev > 0
                && state.schedule.rev != _appliedScheduleRev)
            {
                _schedule = state.schedule;
                _appliedScheduleRev = state.schedule.rev;
                cueSourceChanged = true;
            }

            // 1.62) タイムライン（スキーマ v2・rev で変更検出）。hasXxx present-flag はライブパース直後に確定
            //       （キャッシュ往復後は null 判定が壊れるため。CameraDef.hasPost と同手法）。
            //       rev>0 なら segments 空でも適用する（オペレータの「全消去」を通す）。
            if (state.timeline != null && state.timeline.rev > 0
                && state.timeline.rev != _appliedTimelineRev)
            {
                _timeline = state.timeline;
                _appliedTimelineRev = state.timeline.rev;
                TimelinePresentFlags.Reconcile(_timeline);
                cueSourceChanged = true;
            }

            // 1.63) BGM ライブラリ + ラン既定。トラックは毎回張り直し（参照更新）、
            //       既定はシグネチャ比較で変化時のみ適用する（rev が上がるたびに曲が鳴り直さないように）。
            _bgmTracks = state.bgmTracks ?? Array.Empty<ShowBgmTrackDef>();
            _bgmDefault = (state.bgm != null && state.bgm.IsActionable()) ? state.bgm : null;
            PushBgm();

            // cue 解決関数は毎回張り直す（_cues の参照が更新されるため）。CueScheduler / InsertController 共通。
            cueScheduler?.SetCueResolver(ResolveCue);
            timelineDirector?.SetCueResolver(ResolveCue);
            // schedule / timeline のどちらかが変わったら供給元を再分配する（timeline 優先）。
            if (cueSourceChanged) PushCueSource();

            // 1.65) ラン識別子（control.runEpoch）。変化＝新しい体験者のラン → 周回 / cue をリセット。
            //       SaveCache より前で更新し、次回起動へ「既知値」を持ち越す（同一 epoch の誤リセット防止）。
            int epoch = state.control?.runEpoch ?? _knownRunEpoch;
            if (epoch != _knownRunEpoch)
            {
                _knownRunEpoch = epoch;
                TriggerRunReset();
            }

            // 1.655) 演出の緊急中止（control.takeAbortEpoch）。卓の「■ 画面を取り返す」が増分する。
            //        走行中の演出だけを畳む（ラン・周回・once は保つ）。初回は現在値へ同期するだけで発火しない
            //        （起動のたびに「中止」が走らないように）。
            int abortEpoch = state.control?.takeAbortEpoch ?? _knownTakeAbortEpoch;
            if (!_takeAbortEpochKnown)
            {
                _knownTakeAbortEpoch = abortEpoch;
                _takeAbortEpochKnown = true;
            }
            else if (abortEpoch != _knownTakeAbortEpoch)
            {
                _knownTakeAbortEpoch = abortEpoch;
                Debug.Log($"[ShowControl] 演出の中止要求（takeAbortEpoch={abortEpoch}）");
                //  責務はタイムライン発火の演出だけ。卓の手動 cue は同時に送られる stopCue が
                //  activeCue の遷移として畳む（ここで StopOverlay すると _appliedCue と実状態がずれ、
                //  同じ cue を次に発火できなくなる）。
                timelineDirector?.AbortActive();
            }

            // 1.66) カメラ切替の現場調整（control.minDwellSec / switchCooldownSec）。
            //       control ブロック / キー欠落時は 0 → Director 側でコード既定へ戻る（present 判定 >0）。
            //       SaveCache より前で更新し、次回 PC 不在起動へ持ち越す。
            _switchDwellSec = state.control?.minDwellSec ?? 0f;
            _switchCooldownSec = state.control?.switchCooldownSec ?? 0f;
            ApplySwitchTiming();

            // 1.67) ゾーン切替へ重ねる乱れ（control.switchGlitch）。
            ResolveSwitchDirector()?.SetSwitchGlitch(state.control?.switchGlitch ?? 0f);

            // 1.68) 世代カウンタ 3 種（手動の乱れ / 導入を終える / 体験を終える）。
            //       いずれも初回は現在値へ同期するだけで発火しない（起動のたびに走らないように）。
            int glitchEpoch = state.control?.glitchEpoch ?? _knownGlitchEpoch;
            if (!_glitchEpochKnown) { _knownGlitchEpoch = glitchEpoch; _glitchEpochKnown = true; }
            else if (glitchEpoch != _knownGlitchEpoch)
            {
                _knownGlitchEpoch = glitchEpoch;
                float lv = state.control?.glitchLevel ?? 0f;
                float sec = state.control?.glitchSec ?? 0f;
                ResolveSwitchDirector()?.PulseGlitch(lv > 0f ? lv : 0.8f, sec > 0f ? sec : 0.3f);
            }

            int introEpoch = state.control?.introAdvanceEpoch ?? _knownIntroEpoch;
            if (!_introEpochKnown) { _knownIntroEpoch = introEpoch; _introEpochKnown = true; }
            else if (introEpoch != _knownIntroEpoch)
            {
                _knownIntroEpoch = introEpoch;
                // ⏭ は**文脈で意味が変わる**。演出がまだ走っているなら「次の段へ」、
                //   演出が終わっている（慣らし歩行中）なら「導入相を終えて本編へ」。
                //   旧実装は常に相ごと終わらせていたので、**開始位置が使えない現場では
                //   演出が一度も出ないまま本編に入っていた**（段 0 を抜ける手段が startSpot しか
                //   無く、IntroDirector.RequestAdvanceStage の呼び出し元がゼロだった）。
                var intro = ResolveIntroDirector();
                if (intro != null && intro.Active) intro.RequestAdvanceStage();
                else ResolveRunDirector()?.RequestAdvanceIntro();
            }

            int endEpoch = state.control?.runEndEpoch ?? _knownRunEndEpoch;
            if (!_runEndEpochKnown) { _knownRunEndEpoch = endEpoch; _runEndEpochKnown = true; }
            else if (endEpoch != _knownRunEndEpoch)
            {
                _knownRunEndEpoch = endEpoch;
                ResolveRunDirector()?.RequestFinish();
            }

            // 端末キャッシュへ保存（次回 PC 不在起動で参照）
            SaveCache();
            if (layoutChanged)
            {
                LayoutChanged?.Invoke();
                CourseChanged?.Invoke(); // course は layout に内包 → 同時通知
            }

            // 1.7) discovery キルスイッチ（control.discoveryEnabled、省略時 true）。
            //      DiscoveryClient がこれを AND して probe/切替を全停止できる（従来の静的 IP 運用へ縮退）。
            DiscoveryEnabled = state.control?.discoveryEnabled ?? true;

            // 2) カメラ手動 override（show.cameras の並び = registry sources の並びが前提）
            string ovr = state.control?.cameraOverride ?? "";
            if (ovr != _appliedOverride)
            {
                _appliedOverride = ovr;
                bool hasOverride = !string.IsNullOrEmpty(ovr);
                // override 中は tracker を無効化（enabled=false）、解除で再有効化（enabled=true）する。
                // 再有効化は PlayerZoneTracker.OnEnable を発火し、そこで記憶ゾーン（_current）が無効化される
                // → 次 Update で現在位置から再 Pick して通常経路でゾーンカメラへ復帰する。これが無いと
                // 同一ゾーン滞在のまま _current が不変で、次のゾーン跨ぎまで override カメラに表示が固着する。
                // （asmdef 循環回避のため Tracking 型は持ち込まず、再有効化＝自己回復に委ねる。）
                if (zoneTrackerToDisable != null) zoneTrackerToDisable.enabled = !hasOverride;
                // override を Director の第一級凍結にする（cue/insert 凍結と対称）。enter/exit で stale 保留を
                // 無条件クリアし、override 前に積まれたゾーン保留が cooldown 後に Zone commit して固定が破れるのを防ぐ。
                // Director 未配線（null）なら _logic 自体が無く stale-pending バグも起きないので null-safe skip で正しい。
                ResolveSwitchDirector()?.SetOverrideActive(hasOverride);
                if (hasOverride && registry != null)
                {
                    int idx = Array.FindIndex(state.cameras, c => c.id == ovr);
                    if (idx >= 0) SetActiveOverride(idx);
                    else Debug.LogWarning($"[ShowControl] unknown camera id: {ovr}");
                }
            }

            // 3) cue 発火 / 停止（ライブ手動オーバーライド）。
            //    activeCue 非空の間は CueScheduler を抑止する（ライブ優先）。毎回同期する。
            string cueId = state.control?.activeCue ?? "";
            cueScheduler?.SetLiveCueActive(!string.IsNullOrEmpty(cueId));
            // インサートも同条件で抑止する（activeCue 非空 or cameraOverride 非空中は発火しない）。
            bool liveSuppressed = !string.IsNullOrEmpty(cueId) || !string.IsNullOrEmpty(_appliedOverride);
            timelineDirector?.SetSuppressed(liveSuppressed);
            // スタッフが介入している間の区間は「体験者の滞在」として測らない
            //（カメラ固定中はゾーン追跡自体が止まるので、そのまま測ると滞在が水増しされる）。
            if (liveSuppressed) _dwell.Reset();
            if (cueId != _appliedCue)
            {
                if (_overlay == null) return;
                if (string.IsNullOrEmpty(cueId))
                {
                    _appliedCue = cueId;
                    _overlay.StopOverlay();
                }
                else
                {
                    OverlayCueData? data = ResolveCue(cueId);
                    if (data == null)
                    {
                        // _appliedCue は確定しない: Web 側で cue を保存し直した後の再 poll で
                        // 同じ activeCue 文字列でも再解決できるようにする。
                        Debug.LogWarning($"[ShowControl] unknown cue id: {cueId}");
                        return;
                    }
                    _appliedCue = cueId;
                    _overlay.PlayCue(data);
                }
            }
        }

        /// <summary>
        /// cueId を _cues から OverlayCueData へ解決する（sa:// / server 相対 URL を実機で開ける URL へ）。
        /// ライブ手動発火（Apply）と CueScheduler の自動発火の両方が使う。未定義なら null。
        /// </summary>
        private OverlayCueData? ResolveCue(string cueId)
        {
            if (string.IsNullOrEmpty(cueId)) return null;
            var def = Array.Find(_cues, c => c != null && c.id == cueId);
            if (def == null) return null;
            return new OverlayCueData
            {
                id = def.id,
                displayName = string.IsNullOrEmpty(def.name) ? def.id : def.name,
                sourceUrl = ShowAssetResolver.Resolve(def.sourceUrl, server),
                maskUrl = ShowAssetResolver.Resolve(def.maskUrl, server),
                strength = def.strength,
                loop = def.loop,
                fadeInSeconds = def.fadeIn,
                fadeOutSeconds = def.fadeOut,
                trimStart = def.trimStart,
                trimEnd = def.trimEnd,
                hasMatch = def.hasMatch,
                matchGain = ToVec3(def.matchGain, 1f),
                matchOffset = ToVec3(def.matchOffset, 0f),
            };
        }

        // JsonUtility が配列長を保証しないので、欠けた成分は既定値で埋める（黙って 0 倍にしない）。
        private static Vector3 ToVec3(float[]? a, float fallback)
        {
            float x = a != null && a.Length > 0 ? a[0] : fallback;
            float y = a != null && a.Length > 1 ? a[1] : fallback;
            float z = a != null && a.Length > 2 ? a[2] : fallback;
            return new Vector3(x, y, z);
        }

        // 焼き込み StreamingAssets / 端末キャッシュ由来の schedule / timeline / course を消費者へ供給する。
        private void PushCourseAndSchedule()
        {
            cueScheduler?.SetCueResolver(ResolveCue);
            timelineDirector?.SetCueResolver(ResolveCue);
            timelineDirector?.SetUrlResolver(ResolveAssetUrl);
            // 演出専用カメラ（role:"fx" = カメラ D）はスタッフ巡回・ゾーン自動切替に出さない。
            ResolveSwitchDirector()?.SetCameraSelectable(i => !IsFxCamera(i));
            PushCueSource();
            cueScheduler?.SetLiveCueActive(!string.IsNullOrEmpty(_appliedCue));
            timelineDirector?.SetSuppressed(!string.IsNullOrEmpty(_appliedCue) || !string.IsNullOrEmpty(_appliedOverride));
        }

        /// <summary>
        /// 素材 URL（<c>slot://</c> / <c>sa://</c> / 相対）を実 URL へ解決する。TakeRunner へ注入する。
        ///
        /// <c>slot://&lt;name&gt;</c> は **ラン中に卓が束縛する素材**（入口で撮って生成した人形動画など）。
        /// 未束縛なら空文字を返し、TakeRunner がそのカットを飛ばす（§6.4）。
        /// timeline を書き換えずに素材だけ差し替えられるのがこの仕組みの要点
        /// （timeline を保存し直すと発火済みの once 演出が再武装されてしまう）。
        /// </summary>
        private string ResolveAssetUrl(string url)
        {
            string slot = TakeSchema.SlotName(url);
            if (!string.IsNullOrEmpty(slot))
            {
                string bound = LookupSlot(slot);
                if (string.IsNullOrEmpty(bound))
                {
                    Debug.LogWarning($"[ShowControl] 素材スロット '{slot}' は未束縛 → このカットは飛ばす");
                    return "";
                }
                url = bound;
            }
            return ShowAssetResolver.Resolve(url, server);
        }

        private string LookupSlot(string name)
        {
            foreach (ShowSlotDef? s in _slots)
                if (s != null && s.name == name) return s.url ?? "";
            return "";
        }

        // cue の供給元を timeline(v3) / timeline(v2) / schedule のいずれかに一本化して分配する。
        //   - timeline が v3（schema>=3 または takes を持つ）→ TakeRunner が演出を実行（cue/insert 旧経路は空にする）
        //   - timeline が v2 → 従来どおり TimelineDirector が cues / insert / post を分配
        //   - timeline 不在 → schedule.entries を CueScheduler へ直接供給
        // v3 判定が偽なら**一切挙動が変わらない**（既存 show.json は従来経路のまま = 退避路）。
        private void PushCueSource()
        {
            if (TimelineActive && timelineDirector != null)
            {
                // **版に関係なく v3（演出・カット）として実行する**（2026-07-25 に一本化）。
                // 端末キャッシュや焼き込みに残っている v2（cues[] / insert）は EnsureTakes が
                // takes[] へ決定的に変換する（冪等・TimelineMigration が唯一の変換点）。
                TimelineMigration.EnsureTakes(_timeline!);
                timelineDirector.SetTimeline(_timeline!.segments);
                WarnUnreachableSegments();
            }
            else
            {
                timelineDirector?.Clear();
                cueScheduler?.SetScheduleFromDefs(_schedule?.entries);
            }
        }

        // 直近で警告した内容（同じ設定で毎回吠えない）。
        private string _lastUnreachableWarn = "";

        /// <summary>
        /// 体験中に踏まれない区間に演出が置かれていたら名指しで警告する。
        ///
        /// 卓の本番前チェックも同じ判定を持つが、**実機が使う設定は卓と食い違いうる**
        /// （焼き込み / 端末キャッシュ / ライブの 3 系統）。黙って落とすと著作者には発見手段が無いので、
        /// 実機ログにも 1 回出す。<b>実行は止めない</b>（区間が来れば従来どおり演出は走る）。
        /// </summary>
        private void WarnUnreachableSegments()
        {
            ShowTimelineSegmentDef[]? segs = _timeline?.segments;
            if (segs == null || segs.Length == 0) return;

            int laps = _run?.ResolveTotalLaps() ?? ShowRunDefaults.TotalLaps;
            int[]? order = CourseOrder;
            string dead = "";
            foreach (ShowTimelineSegmentDef? s in segs)
            {
                if (s == null || s.takes == null || s.takes.Length == 0) continue;
                if (ShowRunReach.IsSegmentReachable(s.lap, s.camera, laps, order)) continue;
                if (dead.Length > 0) dead += " ";
                dead += $"{s.lap}周目/カメラ{s.camera + 1}({s.takes.Length}本)";
            }
            if (dead.Length == 0) { _lastUnreachableWarn = ""; return; }
            if (dead == _lastUnreachableWarn) return;
            _lastUnreachableWarn = dead;
            Debug.LogWarning($"[ShowControl] 体験中に踏まれない区間に演出があります（出ません）: {dead} " +
                             $"— 走り切るのは {laps} 周と帰りのスタート区間まで");
        }

        // ---- 焼き込み StreamingAssets/show/show.json の起動時ロード（最下位優先）----

        private async Task LoadBakedShowAsync(CancellationToken ct)
        {
            string uri = ShowAssetResolver.StreamingAssetsUri("show/show.json");
            try
            {
                using var req = UnityWebRequest.Get(uri);
                req.timeout = 5;
                var op = req.SendWebRequest();
                while (!op.isDone) { ct.ThrowIfCancellationRequested(); await Task.Yield(); }
                // 焼き込みが無い（新規ビルドで export していない）のは正常。静かに続行。
                if (req.result != UnityWebRequest.Result.Success) return;
                var state = JsonUtility.FromJson<ShowState>(req.downloadHandler.text);
                if (state == null) return;
                ApplyBaked(state);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Debug.LogWarning($"[ShowControl] 焼き込み show.json 読込失敗: {e.Message}"); }
        }

        // 焼き込み値をフィールドへ流し込む（最下位優先。接続反映・イベント発火は InitializeAsync が一括で行う）。
        // ライブが既に適用済み（_rev>=0）なら焼き込みで上書きしない（ライブ優先）。
        private void ApplyBaked(ShowState state)
        {
            if (_rev >= 0) return;
            ConfigOrigin = "baked";
            _cameras = state.cameras ?? Array.Empty<CameraDef>();
            foreach (var c in _cameras)
            {
                if (c == null) continue;
                c.hasPost = c.post != null && !c.post.IsDefaultLike();  // 素通しの実体は「未著作」扱い（卓が "post": null を書くため）
                c.hasPose = c.pose != null;
                c.hasCalib = c.calib != null && c.calib.IsUsable();
            }
            if (state.post != null) _globalPost = state.post;
            if (state.record != null) _record = state.record;
            if (state.actors != null && state.actors.Length > 0) _actors = state.actors;
            if (state.run != null) _run = state.run;
            if (state.feel != null && !state.feel.LooksUnset()) _feel = state.feel;
            if (state.layout != null && state.layout.HasData())
            {
                _layout = state.layout;
                _appliedLayoutRev = state.layout.rev;
            }
            _cues = state.cues ?? Array.Empty<CueDef>();
            if (state.schedule != null && state.schedule.HasData())
            {
                _schedule = state.schedule;
                _appliedScheduleRev = state.schedule.rev;
            }
            // 焼き込み timeline（present-flag はフレッシュパースなので再導出できる）。
            if (state.timeline != null && state.timeline.HasData())
            {
                _timeline = state.timeline;
                _appliedTimelineRev = state.timeline.rev;
                TimelinePresentFlags.Reconcile(_timeline);
            }
            // 焼き込み BGM（ライブ / キャッシュがあれば後で上書きされる）。
            _bgmTracks = state.bgmTracks ?? Array.Empty<ShowBgmTrackDef>();
            _bgmDefault = (state.bgm != null && state.bgm.IsActionable()) ? state.bgm : null;
            // 焼き込み値の runEpoch を「既知値」として取り込む（端末キャッシュがあれば後で上書きされる）。
            _knownRunEpoch = state.control?.runEpoch ?? _knownRunEpoch;
            // 焼き込みのカメラ切替タイミング（端末キャッシュ / ライブがあれば後で上書きされる）。
            _switchDwellSec = state.control?.minDwellSec ?? _switchDwellSec;
            _switchCooldownSec = state.control?.switchCooldownSec ?? _switchCooldownSec;
            Debug.Log($"[ShowControl] 焼き込み show.json を適用: cameras={_cameras.Length}, " +
                      $"cues={_cues.Length}, schedule={( _schedule != null ? _schedule.entries.Length : 0)}, " +
                      $"timeline={( _timeline != null ? _timeline.segments.Length : 0)}, " +
                      $"course={( _layout?.course != null ? _layout.course.order.Length : 0)}");
        }

        // ---- コントローラ等からのローカル発火（演出トグル）----

        [Serializable] private class CommandMsg { public string type = ""; public string id = ""; }

        /// <summary>
        /// グリップ単押しの緊急復帰トグル。**何か再生中なら無条件で停止**し、何も再生していない時だけ
        /// 今アクティブなカメラの cue（id = cue_&lt;camId&gt;）を発火する。
        ///
        /// 停止を「cue_&lt;camId&gt; 一致」に依存させない理由: スケジューラ発火の別 id cue（cue_A_1 等）が
        /// 表示中だと固定一致では stop 側に入らず、黒/演出を止められない穴があった。緊急復帰の役目を果たすため、
        /// 再生中判定（<see cref="ScreenOverlayController.Current"/> が非 null）を最優先の無条件停止にする。
        ///
        /// - server 接続中: show.json を唯一の正に保つため、再生は /command（playCue）をサーバへ送る
        ///   → 自分の long-poll が即座に戻り Apply が実再生する（web UI 表示・heartbeat とも整合）。
        ///   **停止は server へ "stopCue"（空 id）を送りつつ、ローカルでも必ず StopOverlay を併用する**。
        ///   スケジューラ発火 cue は server の activeCue が空のままなので、stopCue 送信だけでは Apply の
        ///   遷移判定（cueId != _appliedCue）が起きずローカル再生が止まらない穴があった（緊急停止の役目を果たすため
        ///   ローカル停止を必ず走らせる。コマンド送信は Web 表示との整合維持のため残す）。
        /// - server 未設定 / 不通: 焼き込み・端末キャッシュの cue 定義から <see cref="ResolveCue"/> して
        ///   ScreenOverlayController を**ローカル直呼び**する（PC 不在では /command が届かず発火できない既知の穴を塞ぐ）。
        ///   CueScheduler が引くのと同じ _cues プールなので焼き込み cue はそのまま鳴る。
        /// </summary>
        public void ToggleActiveCameraCue()
        {
            if (registry == null) return;
            int idx = registry.ActiveIndex;
            if (idx < 0) return;

            // 最優先: 何か再生中なら無条件停止（緊急復帰）。id 一致に依存しない。
            bool anythingPlaying = _overlay != null && _overlay.Current != null;
            if (anythingPlaying)
            {
                if (ServerReachable)
                {
                    // server へ stopCue を送って Web 表示・heartbeat と整合させつつ、ローカルでも即停止する。
                    // スケジューラ発火 cue は server の activeCue が空のままで Apply の遷移判定が起きないため、
                    // stopCue 送信だけでは止まらない。ローカル StopOverlay を必ず併用する（緊急復帰の担保）。
                    SendCommand("stopCue", "");
                    _overlay!.StopOverlay();
                    Debug.Log("[ShowControl] grip cue toggle: 再生中 -> stop（server + ローカル併用・無条件）");
                }
                else
                {
                    _overlay!.StopOverlay();
                    Debug.Log("[ShowControl] grip cue toggle (local): 再生中 -> stop（無条件）");
                }
                return;
            }

            // 何も再生していない → アクティブカメラの cue を発火。
            string camId = (idx < _cameras.Length && _cameras[idx] != null && !string.IsNullOrEmpty(_cameras[idx]!.id))
                ? _cameras[idx]!.id
                : ((char)('A' + idx)).ToString(); // long-poll 前のフォールバック（show.json は A/B/C 順）
            string cueId = $"cue_{camId}";

            if (ServerReachable)
            {
                SendCommand("playCue", cueId);
                Debug.Log($"[ShowControl] grip cue toggle: cam={camId} -> {cueId}");
                return;
            }

            // ローカルフォールバック（server 未設定 / 不通）。
            if (_overlay == null)
            {
                Debug.LogWarning("[ShowControl] grip cue toggle: ScreenOverlayController 不在のためローカル発火不可");
                return;
            }
            OverlayCueData? data = ResolveCue(cueId);
            if (data == null)
            {
                Debug.LogWarning($"[ShowControl] grip cue toggle (local): cue 未定義 {cueId}" +
                                 "（焼き込み/端末キャッシュに cues があるか確認）");
                return;
            }
            _overlay.PlayCue(data);
            Debug.Log($"[ShowControl] grip cue toggle (local): cam={camId} -> {cueId}");
        }

        private void SendCommand(string type, string id)
        {
            if (server == null) { Debug.LogWarning("[ShowControl] server 未設定: 演出はオペレータ卓接続時のみ発火可"); return; }
            _ = SendCommandAsync(type, id, destroyCancellationToken);
        }

        private async Task SendCommandAsync(string type, string id, CancellationToken ct)
        {
            try
            {
                string json = JsonUtility.ToJson(new CommandMsg { type = type, id = id });
                using var req = UnityWebRequest.Post(server!.BuildUrl("/command"), json, "application/json");
                req.timeout = 3;
                var op = req.SendWebRequest();
                while (!op.isDone) { ct.ThrowIfCancellationRequested(); await Task.Yield(); }
                if (req.result != UnityWebRequest.Result.Success)
                    Debug.LogWarning($"[ShowControl] command '{type}' failed: {req.error}");
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Debug.LogWarning($"[ShowControl] command '{type}' error: {e.Message}"); }
        }

        // ---- カメラ設定 / 画像加工 の適用 ----

        /// <summary>cameras[i].host/port/auth を registry の各 stream へ実行時上書きする（変化時のみ再接続）。</summary>
        private void ApplyCameraEndpoints()
        {
            if (registry == null || _cameras.Length == 0) return;
            int n = Mathf.Min(_cameras.Length, registry.SourceCount);
            for (int i = 0; i < n; i++)
            {
                var c = _cameras[i];
                if (c == null) continue;
                SplitAuth(c.auth, out string user, out string pass);
                registry.ApplyEndpoint(i, c.host, c.port, user, pass);
            }
        }

        /// <summary>
        /// タイムライン区間の post 上書き層を設定する（TimelineDirector 用）。
        /// null で解除するとアクティブカメラ post → global へフォールバックする。
        /// </summary>
        public void SetPostOverride(PostParams? p)
        {
            _segmentPostOverride = p;
            ApplyPostForActive();
        }

        /// <summary>
        /// インサート表示中の post 層を設定する（InsertController 用）。active 中は segment 層より優先する。
        /// p が null なら「インサート先カメラの post / global」へフォールバックする（segment 層は素通ししない）。
        /// </summary>
        public void SetInsertPostOverride(bool active, PostParams? p)
        {
            _insertPostActive = active;
            _insertPostOverride = p;
            ApplyPostForActive();
        }

        /// <summary>
        /// アクティブカメラの映像へ適用する post を解決してマテリアルへ書く。
        /// 段階（上ほど優先）:
        ///   1. インサート層（_insertPostActive 中）: insert.post ?? アクティブ(=insert)カメラ post ?? global
        ///   2. 区間層（_segmentPostOverride）: 区間 post ?? アクティブカメラ post ?? global
        ///   3. アクティブカメラ個別 post（cameras[i].post）
        ///   4. global post
        /// インサート層と区間層は排他（インサート中は区間層を素通りせず insert 側で解決する）。
        /// </summary>
        /// <summary>
        /// いま画面へ書いた post（テレメトリ・診断用）。null = 一度も適用していない。
        /// <see cref="HasScreenMaterial"/> が false なら **画像加工は 1 つも効いていない**
        /// （ShowControlClient がスクリーンの Renderer と同じ GameObject に無い）。
        /// </summary>
        public PostParams? AppliedPost { get; private set; }

        /// <summary>post を書き込む material を解決できているか。false なら画像加工が丸ごと不発。</summary>
        public bool HasScreenMaterial => _material != null;

        /// <summary>
        /// HMD を実際に被っているか（<c>OVRManager.isUserPresent</c>）。
        /// Streaming asmdef は OVR を参照しない規約なので、Assembly-CSharp 側の
        /// <c>OvrControllerBridge</c> が実行時に差し込む。未設定なら true（被っている扱い）＝
        /// Editor / Standalone の検証では従来どおり動く。
        ///
        /// 導入演出は**被った状態でだけ始める**（机に置いた HMD が位置条件をたまたま満たして
        /// 勝手に始まり、体験者が被ったときには終わっている、という事故を防ぐ）。
        /// </summary>
        public Func<bool>? UserPresentProvider;

        /// <summary>
        /// パススルーを<b>アプリから実際に有効化できているか</b>（<c>-1</c>=判定不能 / <c>0</c>=無効 / <c>1</c>=有効）。
        /// <see cref="UserPresentProvider"/> と同じ理由で Assembly-CSharp 側の
        /// <c>PassthroughStyler</c> が実行時に差し込む（Streaming asmdef は OVR を参照しない規約）。
        ///
        /// 導入演出は主役が現実の映像なので、<c>OculusProjectConfig</c> の
        /// <c>_insightPassthroughSupport</c> が 0 だと <c>Failure_NotInitialized</c> で
        /// <b>演出が丸ごと死ぬ</b>。それでも段は進むのでテレメトリの遷移からは気づけない
        /// （2026-07-31 実害）。ここが「効果の実在」を出す口。
        /// </summary>
        public Func<int>? PassthroughStateProvider;

        /// <summary>
        /// <b>位置合わせ（登録モード）がいま動いているか。</b> Tracking asmdef を参照しない規約なので、
        /// 両方を知っている Assembly-CSharp 側の <c>OvrControllerBridge</c> が差し込む。
        /// 未設定なら false（＝従来どおり）。
        ///
        /// 位置合わせは**現実に線を重ねる作業**なので、動いている間は「現実を隠すもの」を全部どける:
        /// <list type="bullet">
        ///   <item>導入演出（<c>IntroDirector</c>）— 覆いは queue 4900 の乗算なので、
        ///         走っていると重ねる対象の線と文字（queue 3000）を黒へ潰す</item>
        ///   <item>中止・終了の黒（<c>ShowRunDirector.ShouldBlackout</c>）— 中止の唯一の直し方が
        ///         再登録なので、黒が居座ると**直す作業が黒に隠れて詰む**</item>
        ///   <item>パススルーの電源（<c>PassthroughStyler</c>）— 演出の外では切られていた</item>
        /// </list>
        /// これを起こせるのはコントローラを持っている人だけ（体験者は持たない運用）なので、
        /// 体験の最中に勝手に立つことはない。
        /// </summary>
        public Func<bool>? CourseRegistrationActiveProvider;

        /// <summary>位置合わせが動作中か（未配線なら false）。</summary>
        public bool CourseRegistrationActive => CourseRegistrationActiveProvider?.Invoke() ?? false;

        /// <summary>
        /// <b>人が「体験を始めてよい」と言ったか。</b> 実体は「タイトルが画面を手放したか」
        /// （＝ スタッフが A を押して題字が焼け切った）で、差し込むのは <c>OvrControllerBridge</c>。
        ///
        /// ⚠⚠ <b>これを <see cref="UserPresentProvider"/> に混ぜてはいけない</b>（2026-08-14 に分けた）。
        /// 旧実装は「被っている ＆ タイトルが立っていない」を 1 つの provider で表していたが、
        /// 自動走行（<c>ShowWalkDebugDriver</c>）が<b>被り検知だけを無効化する目的で全体を true に
        /// 上書きする</b>ので、走行ではタイトルが立ったまま導入が始まって題字が飛んでいた
        /// （＝ タイトルは自動走行で 1 度も検証されていなかった）。
        ///
        /// ⚠ <b>未配線なら true</b>（Editor・テスト・タイトルを持たない構成で体験が止まらない）。
        /// タイトルの実体を組めない現場でも <c>TitleScreen.IsBlocking</c> が false になるので、
        /// ここが false のまま居座ることは無い（「タイトルが壊れると二度と始まらない」を作らない）。
        /// </summary>
        public Func<bool>? StartAuthorizedProvider;

        /// <summary>人が始めてよいと言ったか（未配線なら true ＝ 止めない）。</summary>
        public bool StartAuthorized => StartAuthorizedProvider?.Invoke() ?? true;

        /// <summary>
        /// <b>スタッフがステータス表示（右 B）を開いているか。</b> Diagnostics asmdef を参照しない規約なので、
        /// 両方を知っている <c>OvrControllerBridge</c> が差し込む。未配線なら false。
        ///
        /// タイトルの黒は頭から 0.3m・queue 4950・ZTest Always で、<c>StatusHud</c>（1.6m・TMP）を
        /// <b>後から丸ごと塗り潰す</b>。引き渡し直前にカメラの○×や位置合わせの残差を確かめられないと、
        /// スタッフは「B が効いていない」としか読めない。開いているあいだはタイトルが譲る。
        /// </summary>
        public Func<bool>? StatusVisibleProvider;

        /// <summary>スタッフがステータスを開いているか（未配線なら false）。</summary>
        public bool StatusVisible => StatusVisibleProvider?.Invoke() ?? false;

        /// <summary>
        /// <b>体験者が記録ボタン（左 X）を押した回数。</b> ラン開始で 0 に戻る。
        ///
        /// 紙（調査依頼書）の「違和感を認めるたび、手元のボタンを一度押してください。
        /// ボタンを押した時刻は、自動的に記録されます」が指しているのがこれ。
        ///
        /// ⚠ <b>体験の進行には 1 ビットも使わない。</b> 押さなくても体験は同じように進む
        /// （判定に使うと、押さなかった人が失敗した気になる）。唯一の例外が 4 周目 A の締め
        /// （<c>durKind:"untilMark"</c>・`canon/LEDGER.md` 0050）。
        /// ⚠ <b>正誤は返す</b>（2026-08-16 に反転・`canon/LEDGER.md` 0054）。上司からの連絡が
        /// 「異常が記録されました」/「異常は検出されませんでした」を返す
        /// （分岐の材料が下の <see cref="LastMarkHadTake"/>）。
        /// ⭐ 押した時刻が残ると、<b>3 周目の反転に気づいたかが訊かずに分かる</b>
        /// （初見は消耗品なので、誘導せずに取れる観測の価値が高い）。
        /// </summary>
        public int VisitorMarkCount { get; private set; }

        /// <summary>
        /// <b>直近の報告が届いた瞬間に、演出が画面を握っていたか。</b>
        /// 上司からの連絡の文面がこれで分かれる（`canon/LEDGER.md` 0054 ②）—
        /// 走っていれば「異常が記録されました」、走っていなければ「異常は検出されませんでした」。
        ///
        /// ⚠⚠ <b>読み手は「いま走っているか」を後から見てはいけない。</b>
        /// <see cref="RecordVisitorMark"/> は <c>NotifyVisitorMark</c> で
        /// <b>「報告するまで」のカットをその場で畳む</b>ので、次のフレームには
        /// <c>ActiveTakeId</c> が空になっている ＝ <b>4 周目 A の締めで押したときだけ</b>
        /// 「演出が無かった」に化けて、意味が真逆の連絡が返る。
        /// だからここで<b>中継の前に</b>凍らせてある。<b>順序を入れ替えない。</b>
        /// </summary>
        public bool LastMarkHadTake { get; private set; }

        /// <summary>記録ボタンが押された（実行体は <c>OvrControllerBridge</c>）。</summary>
        public void RecordVisitorMark()
        {
            VisitorMarkCount++;
            // ⚠⚠ **畳む前に凍らせる。** 下の NotifyVisitorMark が締めのカットを終わらせるので、
            //     この 1 行を下へ動かすと 4 周目 A の連絡が必ず逆になる（上の注記）。
            LastMarkHadTake = !string.IsNullOrEmpty(timelineDirector?.ActiveTakeId);
            Debug.Log($"[ShowControl] 記録ボタン（体験者・左 X） {VisitorMarkCount} 回目"
                    + $"（そのとき演出は{(LastMarkHadTake ? "走っていた" : "走っていなかった")}）");
            // 「報告するまで」のカット（4 周目 A の締め）だけが反応する。ほかの進行には一切効かない。
            timelineDirector?.NotifyVisitorMark();
        }

        private void ApplyPostForActive()
        {
            if (_material == null) return;
            int idx = registry != null ? registry.ActiveIndex : -1;
            // ベース = アクティブカメラ個別 post（あれば）→ 無ければ global。
            PostParams basePost = _globalPost;
            if (idx >= 0 && idx < _cameras.Length && _cameras[idx] != null
                && _cameras[idx]!.hasPost && _cameras[idx]!.post != null)
                basePost = _cameras[idx]!.post!;
            // 上書き層: インサート中は insert 層（未指定ならベース）、そうでなければ区間層（未指定ならベース）。
            PostParams p = _insertPostActive
                ? (_insertPostOverride ?? basePost)
                : (_segmentPostOverride ?? basePost);
            _material.SetFloat(ExposureId, p.exposure);
            _material.SetFloat(ContrastId, p.contrast);
            _material.SetFloat(SaturationId, p.saturation);
            _material.SetFloat(TemperatureId, p.temperature);
            _material.SetFloat(VignetteId, p.vignette);
            _material.SetFloat(GrainId, p.grain);
            _material.SetFloat(ScanlineId, p.scanline);
            _material.SetFloat(LiftId, p.lift);
            _material.SetFloat(TintId, p.tint);
            _material.SetFloat(AberrationId, p.aberration);
            _material.SetFloat(PixelateId, p.pixelate);
            // 走査線の本数は「0 = 未指定」。実機 material の 240 固定と卓の canvas 高さで
            // 縞のピッチが 1.5〜2 倍食い違っていたので、値を持たせて両者を揃える。
            _material.SetFloat(ScanlineCountId, p.ResolveScanlineCount());
            AppliedPost = p;
        }

        private static void SplitAuth(string auth, out string user, out string pass)
        {
            user = ""; pass = "";
            if (string.IsNullOrEmpty(auth)) return;
            int i = auth.IndexOf(':');
            if (i < 0) { user = auth; return; }
            user = auth.Substring(0, i);
            pass = auth.Substring(i + 1);
        }

        // ---- 端末ローカル設定キャッシュ（PC 不在起動でも Web 設定を参照するため）----

        private void SaveCache()
        {
            try
            {
                var cfg = new CachedConfig
                {
                    cameras = _cameras,
                    post = _globalPost,
                    layout = _layout,   // course を内包
                    cues = _cues,
                    schedule = _schedule,
                    timeline = _timeline,
                    bgmTracks = _bgmTracks,
                    bgm = _bgmDefault,
                    record = _record,
                    actors = _actors,
                    run = _run,
                    feel = _feel,
                    runEpoch = _knownRunEpoch,
                    switchDwellSec = _switchDwellSec,
                    switchCooldownSec = _switchCooldownSec,
                    // どの APK が書いたキャッシュか。次の起動で照合して、別 APK のものなら捨てる。
                    buildGuid = Application.buildGUID ?? "",
                };
                File.WriteAllText(ConfigCachePath, JsonUtility.ToJson(cfg));
            }
            catch (Exception e) { Debug.LogWarning($"[ShowControl] 設定キャッシュ保存失敗: {e.Message}"); }
        }

        /// <summary>ログ用に識別子を頭 8 文字へ詰める（全部出しても現場では読めない）。</summary>
        private static string Short(string? s)
            => string.IsNullOrEmpty(s) ? "-" : (s!.Length <= 8 ? s : s.Substring(0, 8));

        // 端末キャッシュを読み、焼き込み値の上へ「データを持つ項目だけ」上書きする（空で潰さない）。
        // 接続反映・イベント発火・post 適用は呼び出し側（InitializeAsync）が一括で行う。
        private void LoadAndApplyCache()
        {
            try
            {
                if (!File.Exists(ConfigCachePath)) return;
                var cfg = JsonUtility.FromJson<CachedConfig>(File.ReadAllText(ConfigCachePath));
                if (cfg == null) return;

                // ⚠⚠ **APK が変わったらキャッシュを捨てる**（2026-08-14）。
                //    キャッシュは焼き込みより優先されるので、これが無いと
                //    **APK を焼き直しても古い設定のまま走る**（スタッフは直したつもりでいる）。
                //    2026-07-31 の実測では PC・焼き込み・Quest 2 台の 4 者がずれていて、
                //    しかも `timeline.rev` は全部一致していたので**版番号では気づけなかった**。
                //    ⚠ 消えるのは show の設定だけで、**位置合わせ（registration.json）は別ファイル**
                //    なので残る（現場で測り直しにならない）。
                //    ⚠ 卓が生きていれば long-poll が即座に配り直す。卓が無い現場では
                //    新しい APK の焼き込み値が使われる ＝ スタッフの直感どおりになる。
                string apk = Application.buildGUID ?? "";
                if (!string.IsNullOrEmpty(apk) && cfg.buildGuid != apk)
                {
                    Debug.LogWarning("[ShowControl] 別の APK が書いた設定キャッシュなので捨てました"
                                     + $"（焼き込み値で始めます / cache={Short(cfg.buildGuid)} apk={Short(apk)}）");
                    try { File.Delete(ConfigCachePath); } catch { /* 消せなくても焼き込みで走る */ }
                    return;
                }

                ConfigOrigin = "cache";  // 焼き込みより優先。古いまま残ると APK を焼き直しても設定が変わらない
                if (cfg.cameras != null && cfg.cameras.Length > 0) _cameras = cfg.cameras;
                if (cfg.post != null) _globalPost = cfg.post;
                // キャッシュ済み layout も復元（grid か cuts があるもののみ / course も内包）。
                if (cfg.layout != null && cfg.layout.HasData())
                {
                    _layout = cfg.layout;
                    _appliedLayoutRev = cfg.layout.rev;
                }
                if (cfg.cues != null && cfg.cues.Length > 0) _cues = cfg.cues;
                if (cfg.record != null) _record = cfg.record;
                if (cfg.actors != null && cfg.actors.Length > 0) _actors = cfg.actors;
                if (cfg.run != null) _run = cfg.run;
                if (cfg.feel != null && !cfg.feel.LooksUnset()) _feel = cfg.feel;
                if (cfg.schedule != null && cfg.schedule.HasData())
                {
                    _schedule = cfg.schedule;
                    _appliedScheduleRev = cfg.schedule.rev;
                }
                // キャッシュ済み timeline も復元。TimelinePresentFlags.Reconcile を一律適用する
                // （AND なので保存済み bool が保たれる＝実質 no-op。live/焼き込み/キャッシュで正規化経路を
                //  一本化し「どのパスが正規化するか」の認知負荷を消す）。
                if (cfg.timeline != null && cfg.timeline.HasData())
                {
                    _timeline = cfg.timeline;
                    _appliedTimelineRev = cfg.timeline.rev;
                    TimelinePresentFlags.Reconcile(_timeline);
                }
                // BGM（PC 不在起動でも前回と同じ曲・同じループ範囲で始まる）。
                if (cfg.bgmTracks != null && cfg.bgmTracks.Length > 0) _bgmTracks = cfg.bgmTracks;
                if (cfg.bgm != null && cfg.bgm.IsActionable()) _bgmDefault = cfg.bgm;
                // 既知の runEpoch を復元（この起動では発火しない = 同一 epoch の誤リセット防止）。
                _knownRunEpoch = cfg.runEpoch;
                // カメラ切替タイミングを復元（0=未指定でコード既定。ApplySwitchTiming は InitializeAsync が呼ぶ）。
                _switchDwellSec = cfg.switchDwellSec;
                _switchCooldownSec = cfg.switchCooldownSec;
                Debug.Log($"[ShowControl] 端末キャッシュ設定を適用: {ConfigCachePath} " +
                          $"(cameras={_cameras.Length}, layout={( _layout != null ? "yes" : "no")}, " +
                          $"cues={_cues.Length}, schedule={( _schedule != null ? _schedule.entries.Length : 0)}, " +
                          $"timeline={( _timeline != null ? _timeline.segments.Length : 0)})");
            }
            catch (Exception e) { Debug.LogWarning($"[ShowControl] 設定キャッシュ読込失敗: {e.Message}"); }
        }

        // ---- heartbeat ----

        [Serializable] private class Heartbeat
        {
            // 卓の他の全表示（ラッチ帯・プリフライト・リボン）が使うカメラ ID（A/B/C）に揃える。
            // 以前は DisplayName（"Phone 01"）を送っており、ヘッダだけ表記が違って現場の照合が増えていた。
            public string activeCamera = "";
            public int activeIndex = -1;
            // Unity が実際に受信しているカメラ本数。卓の「show.json の cameras 数と合っているか」検査用
            // （合っていないと演出のカメラ index が無言で別カメラへずれる）。
            public int cameraCount;
            public float recvFps;
            public string playingCue = "";
            public string cameraOverride = "";
            // コントローラ操作モード（NORMAL/REG）。スタッフが遠隔でモードを把握するため。
            // サーバ側は未知フィールドを無視するので送るだけでよい。
            public string mode = "NORMAL";
            // 現在の周回数（LapCounter 由来。未注入なら -1）とアクティブカメラ index。
            // Web ライブ運用パネルの「Lap N / cam B」表示用。既存 activeIndex と重複するが契約名は cam。
            public int lap = -1;
            public int cam = -1;
            // ここまで適用した show.json の rev。UI / 自動検証が「Unity 反映済み」を機械判定する。
            public int appliedRev = -1;
            // ライブモニタ用（任意）: HMD の course space XZ と現在ゾーンラベル。
            // 供給元（ZoneLayoutApplier）未注入なら 0 / 空文字。
            public float headCourseX;
            public float headCourseZ;
            public string currentZone = "";
            // ⚠ cam（上）は**画面に映っているカメラ**で、体験者の居場所ではない。
            //   演出のカットが別カメラを映している間、両者は食い違う。卓が「ゾーン」と称して cam を
            //   出していたため、演出中は別区間の「次の演出」を表示していた（2026-07-28）。
            //   zoneCam = ショーの時計が確定した**体験者の居るゾーンの担当カメラ index**（-1 = 未確定）。
            public int zoneCam = -1;
            // 走行中の演出 id（空 = 演出なし）。卓の「いま画面を握っているのは誰か」表示に使う。
            public string takeId = "";
            // 位置合わせの状態。registered=false / needsReReg=true のまま体験を始めるとゾーンがズレたまま動く。
            public bool registered;
            public bool needsReReg;
            // 体験の骨格（ShowRunDirector 由来）。卓のラン状態パネルが「導入中 0:12」「2 周目 ・
            // 経過 1:05 / 目安 3:00」「終了（次の体験者へ）」を出すのに要る。
            //   phase: "INTRO" | "RUN" | "END"
            public string phase = "RUN";
            public float runSec;        // 本編の経過（導入は含まない）
            public float lapSec;        // いまの周の経過
            public float introSec;      // 導入の経過
            public float targetSec;     // 目安の尺（超過は警告するだけで体験は止めない）
            public int totalLaps;       // 走り切る周数
            public bool endHolding;     // 終了条件は満たしたが走行中の演出を見せ切っている

            // 遅延の内訳（企画書「視覚遅延は 100ms 程度以内を目標として管理する」）。
            // **絶対の end-to-end ではない** — 配信端末と Unity で時計の基準が違い引き算できないため、
            // Unity が観測できる 3 つ（到着の揺らぎ / 展開 / 提示）と配信側の鮮度だけを送る。
            public float latencyJitterMs;
            public float latencyDecodeMs;
            public float latencyPresentMs;
            public float sourceAgeMs;
            public float displayHz;
            public int throttleStage;   // 配信側の熱による自動降格（0 = なし）
            // 前回の heartbeat 以降に確定した区間滞在（実測）。卓が集計して
            // リボン UI の「実測 平均 Ns」に使う。空配列で送ってよい（サーバ側は無視）。
            public DwellHb[] dwell = Array.Empty<DwellHb>();
        }

        private static string PhaseCode(ShowPhase p)
            => p == ShowPhase.Intro ? "INTRO" : (p == ShowPhase.Finished ? "END" : "RUN");

        /// <summary>heartbeat 用の滞在サンプル（JsonUtility は入れ子クラスの配列も往復できる）。</summary>
        [Serializable] private class DwellHb
        {
            public int lap;
            public int camera;
            public float sec;
        }

        private static DwellHb[] ToHb(SegmentDwellLog.Sample[] samples)
        {
            if (samples.Length == 0) return Array.Empty<DwellHb>();
            var arr = new DwellHb[samples.Length];
            for (int i = 0; i < samples.Length; i++)
                arr[i] = new DwellHb { lap = samples[i].lap, camera = samples[i].camera, sec = samples[i].sec };
            return arr;
        }

        private async Task HeartbeatLoopAsync(CancellationToken ct)
        {
            var hb = new Heartbeat();
            while (!ct.IsCancellationRequested)
            {
                // 取り出した分は送信が成功しなければ戻す（卓の再起動・一時断で実測を落とさない）。
                SegmentDwellLog.Sample[] taken = Array.Empty<SegmentDwellLog.Sample>();
                try
                {
                    taken = _dwell.TakePending();
                    hb.dwell = ToHb(taken);
                    var active = registry != null ? registry.GetActive() : null;
                    string activeId = registry != null
                        ? (registry.GetSource(registry.ActiveIndex)?.CameraId ?? "")
                        : "";
                    hb.activeCamera = !string.IsNullOrEmpty(activeId) ? activeId : (active?.DisplayName ?? "");
                    hb.activeIndex = registry != null ? registry.ActiveIndex : -1;
                    hb.cameraCount = registry != null ? registry.Count : 0;
                    hb.recvFps = active?.ReceivedFps ?? 0f;
                    hb.playingCue = _overlay?.Current?.id ?? "";
                    hb.cameraOverride = _appliedOverride;
                    hb.mode = _controllerMode;
                    hb.appliedRev = _rev;
                    hb.lap = CurrentLapProvider != null ? CurrentLapProvider() : -1;
                    hb.cam = registry != null ? registry.ActiveIndex : -1;
                    if (HeadCourseXZProvider != null)
                    {
                        Vector2 c = HeadCourseXZProvider();
                        hb.headCourseX = c.x;
                        hb.headCourseZ = c.y;
                    }
                    hb.currentZone = CurrentZoneLabelProvider != null ? CurrentZoneLabelProvider() : "";
                    // 人の居場所（時計の確定ゾーン）と画面のカメラを別々に送る。混ぜると卓が嘘をつく。
                    CameraSwitchDirector? zd = ResolveSwitchDirector();   // UnityEngine.Object の偽 null を踏まないよう != で判定する
                    hb.zoneCam = (zd != null && zd.TryGetCurrentZoneCamera(out int zc)) ? zc : -1;
                    hb.takeId = timelineDirector != null ? timelineDirector.ActiveTakeId : "";
                    hb.registered = CourseRegisteredProvider == null || CourseRegisteredProvider();
                    hb.needsReReg = CourseNeedsReRegProvider != null && CourseNeedsReRegProvider();

                    if (active != null)
                    {
                        LatencyEstimatorLogic lat = active.Latency;
                        hb.latencyJitterMs = lat.ArrivalJitterMs;
                        hb.latencyDecodeMs = lat.DecodeMs;
                        hb.latencyPresentMs = lat.PresentMs;
                        hb.sourceAgeMs = lat.SourceAgeMs;
                        hb.throttleStage = active.Health?.throttleStage ?? 0;
                    }
                    hb.displayHz = DisplayRateInfo.CurrentHz;

                    ShowRunDirector? run = ResolveRunDirector();
                    if (run != null)
                    {
                        hb.phase = PhaseCode(run.Phase);
                        hb.runSec = run.RunElapsedSec;
                        hb.lapSec = run.LapElapsedSec;
                        hb.introSec = run.IntroElapsedSec;
                        hb.targetSec = run.TargetSec;
                        hb.totalLaps = run.TotalLaps;
                        hb.endHolding = run.EndHolding;
                    }

                    string json = JsonUtility.ToJson(hb);
                    using var req = UnityWebRequest.Post(
                        server!.BuildUrl("/unity/heartbeat"), json, "application/json");
                    req.timeout = 3;
                    var op = req.SendWebRequest();
                    while (!op.isDone)
                    {
                        ct.ThrowIfCancellationRequested();
                        await Task.Yield();
                    }
                    if (req.result != UnityWebRequest.Result.Success) _dwell.PutBack(taken);
                }
                catch (OperationCanceledException) { _dwell.PutBack(taken); return; }
                catch { _dwell.PutBack(taken); /* heartbeat はベストエフォート */ }

                try { await Task.Delay((int)(heartbeatInterval * 1000), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
