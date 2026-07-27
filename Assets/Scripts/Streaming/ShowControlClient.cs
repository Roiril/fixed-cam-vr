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

    /// <summary>
    /// HMD 位置合わせのタッチ基準点（course space XZ、床マーカー運用）。順序 = タッチ順。
    /// フロアマップ UI で 2〜5 点をオーサリングする。label は HMD ガイダンス表示用（空可）。
    /// CourseRegistrationController が読む（Tracking → Streaming の既存参照方向を守る純データ）。
    /// </summary>
    [Serializable] public sealed class ShowRegPointDef { public float x; public float z; public string label = ""; }

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
        // 通過ライン（演出の発火点となる床の線分）。ゾーン生成・HasData() には関与しない純データで、
        // TakeRunner だけが読む（regPoints と同じ立ち位置）。
        public ShowLineDef[] lines = System.Array.Empty<ShowLineDef>();
        public float overlapM = 0.08f;
        public float hysteresisM = 0.12f;

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
    /// 卓のフロアマップで著作する。<c>hasPose</c> が present-flag。
    /// </summary>
    [Serializable] public sealed class ShowCameraPoseDef
    {
        public float x;          // course 空間 X (m)
        public float z;          // course 空間 Z (m)
        public float y = 1.2f;   // 床からの高さ (m)
        public float yawDeg;     // course +Z を 0 とする水平角
        public float pitchDeg;   // 下向きが負
        public float fovDeg = 60f;
    }

    /// <summary>端末内録画（1 周目を録って 3 周目に流す）の設定。show.json トップレベル <c>record</c>。</summary>
    [Serializable] public sealed class ShowRecordDef
    {
        public bool enabled;
        public int[] laps = { 1 };          // 録る周（既定は 1 周目だけ）
        public float maxSegmentSec = 60f;   // 1 区間の上限（超えたら古いフレームから捨てる）
        public int maxTotalMB = 200;        // ラン全体の上限
        public float fpsCap = 15f;          // 録画側の間引き（受信 fps より低くしてよい）

        public bool RecordsLap(int lap)
        {
            if (!enabled || laps == null) return false;
            foreach (int l in laps) if (l == lap) return true;
            return false;
        }
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
            public ShowCameraPoseDef? pose;
            public bool hasPose;
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
            // 直近に既知だった runEpoch。起動時にこれを「既知値」として復元し、
            // PC 不在の再起動で同一 epoch を誤リセットしない。
            public int runEpoch;
            // カメラ切替の現場調整（control 由来）。0=未指定でコード既定。PC 不在起動でも値が生きるよう往復させる。
            public float switchDwellSec;
            public float switchCooldownSec;
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
            // カメラ切替の現場調整（CameraSwitchDirector へ流す）。present 判定は「>0 で適用 / 0=未指定でコード既定」。
            public float minDwellSec;
            public float switchCooldownSec;
            // 素材スロットの束縛（slot://name → 実 URL）。timeline を触らずに素材だけ差し替えるための面。
            public ShowSlotDef[] slots = Array.Empty<ShowSlotDef>();
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
            _dwell.Reset();   // 計時中の部分区間は「体験者 1 人分の滞在」として成立しないので捨てる
            cueScheduler?.ResetRun();
            timelineDirector?.ResetRun();
            ResolveBgmDirector()?.ResetRun();
            // 端末内録画も世代を切り替える（前の体験者の映像を次のランへ持ち越さない・端末に残さない）。
            ResolveRecorder()?.ResetRun(_knownRunEpoch);
            RunReset?.Invoke();
        }

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
            // 1) カメラ設定（IP / 認証 / カメラ別画像加工）を反映
            _cameras = state.cameras ?? Array.Empty<CameraDef>();
            // ライブ受信パース直後だけ post / pose の null 判定が信頼できる → ここで present-flag を確定
            foreach (var c in _cameras)
            {
                if (c == null) continue;
                c.hasPost = c.post != null;
                c.hasPose = c.pose != null;
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

            // 1.66) カメラ切替の現場調整（control.minDwellSec / switchCooldownSec）。
            //       control ブロック / キー欠落時は 0 → Director 側でコード既定へ戻る（present 判定 >0）。
            //       SaveCache より前で更新し、次回 PC 不在起動へ持ち越す。
            _switchDwellSec = state.control?.minDwellSec ?? 0f;
            _switchCooldownSec = state.control?.switchCooldownSec ?? 0f;
            ApplySwitchTiming();

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
            };
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
            }
            else
            {
                timelineDirector?.Clear();
                cueScheduler?.SetScheduleFromDefs(_schedule?.entries);
            }
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
            _cameras = state.cameras ?? Array.Empty<CameraDef>();
            foreach (var c in _cameras)
            {
                if (c == null) continue;
                c.hasPost = c.post != null;
                c.hasPose = c.pose != null;
            }
            if (state.post != null) _globalPost = state.post;
            if (state.record != null) _record = state.record;
            if (state.actors != null && state.actors.Length > 0) _actors = state.actors;
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
                    runEpoch = _knownRunEpoch,
                    switchDwellSec = _switchDwellSec,
                    switchCooldownSec = _switchCooldownSec,
                };
                File.WriteAllText(ConfigCachePath, JsonUtility.ToJson(cfg));
            }
            catch (Exception e) { Debug.LogWarning($"[ShowControl] 設定キャッシュ保存失敗: {e.Message}"); }
        }

        // 端末キャッシュを読み、焼き込み値の上へ「データを持つ項目だけ」上書きする（空で潰さない）。
        // 接続反映・イベント発火・post 適用は呼び出し側（InitializeAsync）が一括で行う。
        private void LoadAndApplyCache()
        {
            try
            {
                if (!File.Exists(ConfigCachePath)) return;
                var cfg = JsonUtility.FromJson<CachedConfig>(File.ReadAllText(ConfigCachePath));
                if (cfg == null) return;
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
            // 前回の heartbeat 以降に確定した区間滞在（実測）。卓が集計して
            // リボン UI の「実測 平均 Ns」に使う。空配列で送ってよい（サーバ側は無視）。
            public DwellHb[] dwell = Array.Empty<DwellHb>();
        }

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
