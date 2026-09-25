#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// **体験の音を 1 箇所で鳴らす。** 導入・終幕・タイトル・乱れ・信号断・周回の劣化を
    /// <b>外から読むだけ</b>で、既存の演出コードには 1 行も足さない
    /// （<see cref="CameraFeelFx"/> が post を外から書くのと同じ構え）。
    ///
    /// 判断は純ロジックに置いてある:
    ///   - 敷く音の高さ … <see cref="SoundBedLogic"/>（設計そのもの）
    ///   - 節目の検出 … <see cref="SoundCueLogic"/>
    ///   - 増減の形 … <see cref="SoundFade"/>
    ///
    /// ここがやるのは <c>Resources</c> からの読み込み・声の生成・毎フレームの適用だけ。
    ///
    /// ⚠⚠ <b>観測は「段が進んだ」ではなく「音が出たか」を出す。</b> 2026-07-31 に、
    /// 判定が「FAIL ゼロ・演出 7 本 OK」と出した走行の画に導入演出が 1 段も出ていなかった
    /// （<c>rules/visual-verification.md</c> §6）。音は<b>録画にも映らない</b>ので同じ穴がもっと深い。
    /// <see cref="ClipsMissing"/> / <see cref="AudibleSum"/> / <see cref="SpotCount"/> が
    /// 「実際に鳴らせたか」を出し、<c>ShowTelemetryHost</c> が <c>ev=snd</c> として吐く。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShowSoundDirector : MonoBehaviour
    {
        /// <summary><c>Resources</c> の中の置き場。</summary>
        public const string ResourceDir = "Sound/";

        /// <summary>
        /// <b>別の場所（異世界）の素材 id の頭</b>（2026-09-03・<c>canon/LEDGER.md</c> 0131）。
        ///
        /// ⚠ <b>正本は <see cref="TakeRunner.OtherworldCuePrefix"/></b>（2026-09-06 に移した）。
        /// 判定も <see cref="TakeRunner.OtherworldActive"/> 1 本で、音（風・劇伴）と
        /// 時計（<c>ScreenOsd</c>）が同じ 1 本を読む。ここは呼び名として残してある。
        /// </summary>
        public const string OtherworldCuePrefix = TakeRunner.OtherworldCuePrefix;

        /// <summary>隔離が開いているときの部屋の帯域（＝ 素通し）。</summary>
        public const float OpenCutoffHz = 22000f;

        /// <summary>
        /// 隔離が閉じ切ったときの部屋の帯域。**部屋のトーンの明るい線（3.15kHz）が消える高さ**。
        /// ここより上に置くと「閉じた」が聞こえない。
        /// </summary>
        public const float ClosedCutoffHz = 420f;

        [Tooltip("全体の音量。現場で下げるならここ（素材の相対関係は崩れない）。")]
        [Range(0f, 1f)]
        [SerializeField] private float masterGain = 1f;

        [Tooltip("敷く音を出すか。切ると節目の一撃だけになる（切り分け用）。")]
        [SerializeField] private bool bedsEnabled = true;

        [Tooltip("節目の一撃を出すか。")]
        [SerializeField] private bool cuesEnabled = true;

        // ---- 敷く音の 1 本ぶん ---------------------------------------------------
        private sealed class BedVoice
        {
            public AudioSource? src;
            public AudioLowPassFilter? lpf;
            public bool ok;             // クリップを掴めたか
        }

        private readonly SoundBedLogic _beds = new SoundBedLogic();
        private readonly SoundCueLogic _cues = new SoundCueLogic();
        private SfxPlayer? _sfx;

        // ⚠⚠ **周ごとの環境音（`bed_room_lap2` / `bed_room_lap3`）は 2026-08-23 に退役した**
        //    （`canon/LEDGER.md` 0115）。`_room` は導入と終幕でだけ鳴る部屋のトーン。
        private BedVoice _seal = new BedVoice(), _room = new BedVoice(), _device = new BedVoice();
        private BedVoice _worn = new BedVoice(), _noise = new BedVoice();

        /// <summary>
        /// <b>別の場所（バックルームズ）の風</b>（2026-09-03・<c>canon/LEDGER.md</c> 0131）。
        /// ⚠ <b>2D。</b> 異世界そのものの音なので全周にある（部屋のトーンと同じ理屈）。
        /// </summary>
        private BedVoice _wind = new BedVoice();

        /// <summary>
        /// <b>3 周目 B から呪いが排除されるまでの 2 本</b>（同上）。⚠ <b>2 本で 1 つの背景</b>で、
        /// 音量は常に同じ値。⚠ 2D（劇伴の代わりなので定位しない）。
        /// </summary>
        private BedVoice _beat = new BedVoice(), _horror2 = new BedVoice();

        /// <summary>
        /// <b>呪いが排除された後のホワイトノイズ</b>（同上）。⚠ 2D。
        /// </summary>
        private BedVoice _white = new BedVoice();

        /// <summary>
        /// <b>心音</b>（2026-09-06・<c>canon/LEDGER.md</c> 0175）。追いつきから入れ替わりまで。
        ///
        /// ⚠ <b>2D。</b> 体験者自身の鼓動なので出どころが無い（部屋のトーンを 2D に残すのと同じ理屈で、
        /// 1 点から鳴らすと「鼓動が聞こえる何か」がそこに居ることになる）。
        /// </summary>
        private BedVoice _heart = new BedVoice();

        /// <summary>
        /// <b>笑いの 1 層</b>（体ごとの声の束・2026-09-04・<c>canon/LEDGER.md</c> 0139）。
        ///
        /// ⚠⚠ <b>1 層 ＝ 1 点ではない。</b> 焼く側（<c>ingest-sounds.py</c> の <c>LAUGH_BODIES</c>）が
        /// 表の左右の位置で体ごとに割ってあり、ここはその**体の数だけ声を持って別々の方角へ置く**。
        /// ユーザー指定「いろんな場所から同時に少しずらして鳴らすくらいしっかりしたい」。
        /// </summary>
        private sealed class LaughLayer
        {
            public BedVoice[] bodies = System.Array.Empty<BedVoice>();
            /// <summary>体ごとの輪のスロット（<see cref="_laughSpots"/> の添字）。</summary>
            public int[] slots = System.Array.Empty<int>();
            /// <summary>直前まで鳴っていたか（頭出しの縁を作るため・体で共有する）。</summary>
            public bool audible;
        }

        /// <summary>
        /// 人形の笑い。**報告を押すまでループ**（<c>canon/LEDGER.md</c> 0066）。
        /// 敷く音の器に載せているが地の音ではない — ループして出し入れできるのがここだけだから。
        /// <b>8 体が輪をひとまわり埋める</b>（0139）。
        /// </summary>
        private readonly LaughLayer _dolls = new LaughLayer();

        /// <summary>
        /// 入れ替わった人形の笑い（3 周目・<c>canon/LEDGER.md</c> 0086）。
        /// **一人 ＋ 増える 2 枚**で、C のあいだに後ろの 2 枚が入ってくる。
        /// ⚠ ループ長を互いに素にしてある（11 / 13 / 17 秒）ので、3 枚が同じ所で巻き戻らない。
        /// </summary>
        private readonly LaughLayer _dollOne = new LaughLayer();
        private readonly LaughLayer _dollGrowA = new LaughLayer();
        private readonly LaughLayer _dollGrowB = new LaughLayer();
        private readonly System.Collections.Generic.Dictionary<string, AudioClip?> _spot =
            new System.Collections.Generic.Dictionary<string, AudioClip?>();
        private readonly System.Collections.Generic.Dictionary<SoundCue, int> _variant =
            new System.Collections.Generic.Dictionary<SoundCue, int>();

        private IntroDirector? _intro;
        private OutroDirector? _outro;
        private TitleScreen? _title;
        private ShowRunDirector? _run;
        private SignalLostFx? _signal;
        private GlitchFx? _glitch;
        private ShowControlClient? _show;
        private SealedBox? _box;
        private BgmDirector? _bgm;

        /// <summary>
        /// 締めのカットが報告を待っているかを見るために読む
        /// （人形がたくさん出てくる所で笑いを鳴らす・<c>canon/LEDGER.md</c> 0062）。
        /// </summary>
        private TimelineDirector? _timeline;

        /// <summary>
        /// 映像の中に人形が立っているか（＝ 体験者と入れ替わったか）を見るために読む
        /// （<c>canon/LEDGER.md</c> 0086）。**「3 周目」とは書かない** — 人形が立っていること自体を見る。
        /// </summary>
        private Cg.ShowCgLayer? _cg;

        /// <summary>
        /// いま画面を取っているカットを見るために読む（異世界が映っているか・0131）。
        /// </summary>
        private TakeRunner? _takes;
        private float _resolveAccum;

        // ---- 音を置く先（2026-09-03・`canon/LEDGER.md` 0130）----------------------

        /// <summary>
        /// 体験者の頭。<b>笑いの輪の中心</b>と、呼びかけの「後ろ」を決める。
        /// 掴めなければ音は 2D へ落ちる（黙って別の場所から鳴らさない）。
        /// </summary>
        private Transform? _head;

        /// <summary>
        /// スクリーン（<see cref="ScreenOverlayController"/> の GameObject）。
        /// <c>ScreenAnchor</c> がこれを頭の正面へ運んでいるので、**装置の音はここに付ければ足りる**。
        /// </summary>
        private Transform? _screen;

        /// <summary>
        /// 人形の笑いの置き場所（方角・距離・高さ）。<b>体験者ごとに引き直す</b>
        /// （<see cref="ResetRun"/>）。360° を等分した区画へ 1 つずつ入るので重ならない。
        ///
        /// ⚠ <b>群れ（8 体）と 3 周目の 3 層（1 ＋ 2 ＋ 4 ＝ 7 体）は同じ輪を分け合う</b>
        /// （同時には鳴らない）。3 周目に一人で笑っていた体が、4 周目 A では同じ方角の 1 体になる。
        /// </summary>
        private readonly SpatialAudio.LaughSpot[] _laughSpots =
            new SpatialAudio.LaughSpot[SpatialAudio.LaughSlots];

        // ---- 観測（テレメトリが読む）--------------------------------------------

        /// <summary>読めなかったクリップの本数。**0 でなければ音は設計どおりに出ていない。**</summary>
        public int ClipsMissing { get; private set; }
        /// <summary>読めたクリップの本数。</summary>
        public int ClipsResolved { get; private set; }
        /// <summary>いま実際に <c>AudioSource</c> へ書いている音量の合計。**0 なら無音。**</summary>
        public float AudibleSum { get; private set; }
        /// <summary>鳴らした一撃の累計。</summary>
        public int SpotCount => _sfx != null ? _sfx.PlayedCount : 0;
        /// <summary>直前に鳴らした節目。</summary>
        public SoundCue LastCue { get; private set; }
        /// <summary>いまの敷く音の倍率。</summary>
        public SoundBedGains Bed => _beds.Gains;
        /// <summary>部屋の低域通過の実効遮断周波数（Hz）。隔離が閉じると下がる。</summary>
        public float RoomCutoffHz { get; private set; } = 22000f;

        /// <summary>
        /// 人形の笑いの音量（0..1）。**画にも一撃のログにも出ない**ので、
        /// 鳴っているかを外から知る唯一の手（<c>canon/LEDGER.md</c> 0066）。
        /// </summary>
        public float DollsGain { get; private set; }

        /// <summary>
        /// 入れ替わった人形の笑いの音量（3 枚の合計・<c>canon/LEDGER.md</c> 0086）。
        /// **画にも一撃のログにも出ない**ので、鳴っているかを外から知る唯一の手。
        /// </summary>
        public float DollSwapGain { get; private set; }

        /// <summary>笑う人形の増え具合 0..1（C に居るあいだ増える）。</summary>
        public float DollSwellNow => _beds.Swell01;

        // ---- 劇伴を置き換える 3 つ（`canon/LEDGER.md` 0131）----------------------
        // ⚠⚠ **どれも画にも一撃のログにも出ない。** 鳴っているかを外から知る唯一の手。

        /// <summary>別の場所の風の音量（0..1）。異世界が映っているあいだだけ立つ。</summary>
        public float WindGain { get; private set; }

        /// <summary>呪いの 2 本の音量（0..1・<b>2 本とも同じ値</b>）。3 周目 B から排除まで。</summary>
        public float CurseGain { get; private set; }

        /// <summary>ホワイトノイズの音量（0..1）。呪いが排除された後。</summary>
        public float WhiteGain { get; private set; }

        /// <summary>
        /// <b>心音の音量</b>（0..1・2026-09-06・<c>canon/LEDGER.md</c> 0175）。
        /// 追いつきから 3 周目 A の入れ替わりの再生が終わるまで立つ。
        /// ⚠⚠ <b>画にも録画にも一撃のログにも出ない。</b> 鳴っているかを外から知る唯一の手。
        /// </summary>
        public float HeartGain { get; private set; }

        /// <summary>無音で回っている時間も含め、心音そのものの再生位置を返す。</summary>
        public bool TryGetHeartPlayback(out float seconds, out float gain)
        {
            seconds = 0f;
            gain = 0f;
            var source = _heart.src;
            if (!isActiveAndEnabled || source == null || source.clip == null
                || !source.isPlaying || source.mute || source.volume <= 0.0005f) return false;
            seconds = source.timeSamples / (float)source.clip.frequency;
            gain = source.volume;
            return true;
        }

        // ---- 3D の観測（`canon/LEDGER.md` 0130）----------------------------------

        /// <summary>3D で鳴らす名簿のうち、実際に掴めた本数。</summary>
        public int SpatialClipCount { get; private set; }

        /// <summary>
        /// そのうち<b>ステレオのまま</b>だった本数。<b>0 でなければ定位していない。</b>
        /// 音は鳴るので、画にも録画にも一撃のログにも出ない — ここだけが証拠。
        /// </summary>
        public int StereoInSpatial { get; private set; }

        /// <summary>
        /// いま実際に 3D で鳴っている敷く音の本数（音量 &gt; 0 かつ <c>spatialBlend</c> が 1）。
        /// <b>置き場所を掴めずに 2D へ落ちたら減る</b>ので、頭やスクリーンの取りこぼしがここに出る。
        /// </summary>
        public int SpatialBedsAudible { get; private set; }

        /// <summary>
        /// 人形の笑いの方角（度）を「/」で繋いだもの。テレメトリ用。
        /// <b>2026-09-04 から 8 個</b>（体ごとに 1 つ・<c>canon/LEDGER.md</c> 0139）。
        /// </summary>
        public string LaughBearingsText
        {
            get
            {
                var sb = new System.Text.StringBuilder(40);
                for (int i = 0; i < _laughSpots.Length; i++)
                {
                    if (i > 0) sb.Append('/');
                    sb.Append(Mathf.RoundToInt(_laughSpots[i].bearingDeg));
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// 笑いを「聞こえている」と数える下限（0139）。<b>-34dB</b>。
        /// ⚠ 層の切り替わりで消えかけの声（50dB 下）を数に入れないための線。
        /// </summary>
        public const float LaughAudibleGain = 0.02f;

        /// <summary>
        /// <b>いま何か所から笑いが聞こえているか</b>（テレメトリ用・0139）。
        /// ⚠⚠ **「周囲に大勢いる」が成立したかの唯一の証拠。** 1 なら 1 点から鳴っていて、
        /// 体ごとに分けた意味が消えている（画にも録画にも出ない）。
        /// 4 周目 A の群れで <b>8</b>・3 周目 C で <b>7</b>（一人 1 ＋ 2 体 ＋ 4 体）。
        /// ⚠⚠ <b>満点は 15</b> — 入れ替わる 2 秒は両方が実際に鳴る
        /// （実測 t=121.4 で 群れ 0.11 / 入替 2.26）。<b>16 以上なら数え方が壊れている。</b>
        /// </summary>
        public int LaughPoints { get; private set; }

        /// <summary>人形の呼びかけを鳴らした累計。</summary>
        public int CallCount { get; private set; }

        /// <summary>
        /// 直前の呼びかけの見かけの方角（度・0 が正面 / 180 が真後ろ）。
        /// <b>後ろから鳴った証拠はここにしか無い。</b> 頭を掴めずに 2D で鳴らしたときは負。
        /// </summary>
        public float LastCallAzimuthDeg { get; private set; } = -1f;

        /// <summary>
        /// 劇伴（`HorrBGM`）の取り分 0..1。<b>リセット後の黒から 3 周目の終わりまで 1</b>で、
        /// 終幕で退く（<c>canon/LEDGER.md</c> 0115）。**画にも一撃のログにも出ない**ので、
        /// 「本編で劇伴が鳴っているか」を外から知る唯一の手。
        /// </summary>
        public float ScoreGain { get; private set; }

        // ---------------------------------------------------------------- 生成

        private void Awake()
        {
            _beds.Reset();
            _sfx = GetComponent<SfxPlayer>();
            if (_sfx == null) _sfx = gameObject.AddComponent<SfxPlayer>();

            // ⚠⚠ **どれを 3D にするかは `canon/LEDGER.md` 0130 の 3 行**（2026-09-03）。
            //    装置の音（搬送・電源・砂嵐）はスクリーンから、人形の笑いは体験者の周囲から。
            //    部屋のトーンだけは 2D のまま — **現実の音は全周にある**ので、
            //    1 点から鳴らすと「部屋」ではなく「部屋の音が出る何か」になる。
            _seal = MakeBed("Seal", "bed_seal", spatial: true,
                            minDistance: 1.6f, maxDistance: 30f);   // 箱は world の固定物（いまは鳴らない）
            _room = MakeBed("Room", "bed_room", spatial: false, lowPass: true);
            _device = MakeBed("Device", "bed_device", spatial: true);
            _worn = MakeBed("DeviceWorn", "bed_device_worn", spatial: true);
            _noise = MakeBed("Noise", "bed_static", spatial: true);
            // ⚠⚠ **体ごとに 1 声。輪のスロットは層をまたいで割り振る**（0139）。
            //    群れ 8 体が輪を埋め、3 周目の 3 層は同じ輪の一部を使う（同時に鳴らない）。
            MakeLaughLayer(_dolls, "Dolls", "bed_dolls_laugh", 8, firstSlot: 0);
            MakeLaughLayer(_dollOne, "DollOne", "bed_doll_one", 1, firstSlot: 0);
            MakeLaughLayer(_dollGrowA, "DollGrowA", "bed_dolls_grow_a", 2, firstSlot: 1);
            MakeLaughLayer(_dollGrowB, "DollGrowB", "bed_dolls_grow_b", 4, firstSlot: 3);
            // ⚠ **劇伴を置き換える 3 つは 2D**（0131）。背景そのものなので出どころを作らない。
            _wind = MakeBed("Wind", "bed_wind", spatial: false);
            _beat = MakeBed("Beat", "bed_beat", spatial: false);
            _horror2 = MakeBed("Horror2", "bed_horror2", spatial: false);
            _white = MakeBed("White", "bed_white", spatial: false);
            // ⚠ 心音も 2D（0175）。体験者自身の鼓動なので出どころを作らない。
            _heart = MakeBed("Heart", "bed_heart", spatial: false);
            SpatialAudio.PickLaughSpots(_laughSpots);

            // ⚠⚠ **ステレオのまま 3D に置かれていないかを起動時に数える。**
            //    定位していないことは画にも録画にも出ない（音が鳴ってはいる）ので、
            //    ここで数えないと永久に気づけない。テレメトリの `snd3d` に出る。
            StereoInSpatial = SpatialAudio.CountStereo(ResourceDir, out int spatialSeen);
            SpatialClipCount = spatialSeen;

            foreach (SoundCue c in System.Enum.GetValues(typeof(SoundCue)))
            {
                if (c == SoundCue.None) continue;
                int n = SoundCueLogic.VariantCount(c);
                string baseName = SoundCueLogic.ResourceName(c);
                for (int i = 0; i < n; i++)
                {
                    string res = n == 1 ? baseName : $"{baseName}_{i + 1}";
                    LoadSpot(res);
                }
                _variant[c] = 0;
            }
        }

        /// <summary>
        /// 笑いの 1 層を<b>体の数だけ</b>作る（<c>canon/LEDGER.md</c> 0139）。
        /// 体が 1 つなら焼いた名前はそのまま、2 つ以上なら <c>_1..N</c>（焼く側の <c>body_name</c> と対）。
        ///
        /// ⚠ スロットは <paramref name="firstSlot"/> から順に取る。輪を 1 周ぶんしか持たないので、
        /// はみ出したら折り返す（体数を増やすときは <see cref="SpatialAudio.LaughSlots"/> も増やす）。
        /// </summary>
        private void MakeLaughLayer(LaughLayer layer, string label, string stem, int bodies,
                                    int firstSlot)
        {
            layer.bodies = new BedVoice[bodies];
            layer.slots = new int[bodies];
            for (int i = 0; i < bodies; i++)
            {
                string res = bodies == 1 ? stem : $"{stem}_{i + 1}";
                layer.bodies[i] = MakeBed(bodies == 1 ? label : $"{label}{i + 1}", res, spatial: true);
                layer.slots[i] = (firstSlot + i) % Mathf.Max(1, _laughSpots.Length);
            }
        }

        private BedVoice MakeBed(string label, string res, bool spatial, bool lowPass = false,
                                 float minDistance = SpatialAudio.MinDistanceM,
                                 float maxDistance = SpatialAudio.MaxDistanceM)
        {
            var bed = new BedVoice();
            var go = new GameObject($"[Bed{label}]");
            go.transform.SetParent(transform, worldPositionStays: false);
            var s = go.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.loop = true;
            s.volume = 0f;
            s.clip = Resources.Load<AudioClip>(ResourceDir + res);
            bed.ok = s.clip != null;
            if (bed.ok) ClipsResolved++;
            else
            {
                ClipsMissing++;
                Debug.LogWarning($"[Sound] 音が見つかりません: Resources/{ResourceDir}{res}"
                                 + "（`py -3.11 tools/make-sounds.py` を走らせたか）");
            }

            // ⚠ **取り込み設定の事故を実行時に捕まえる。** `menu sound-import` を忘れると
            //    Unity の既定（Streaming / 正規化あり）で入り、ループの頭出しが引っかかるうえ
            //    素材どうしの音量の関係が消える。**どちらも音を聞かないと分からない**ので、
            //    掴んだ時点で型を見て言う（実機ログに残る唯一の手掛かりになる）。
            if (bed.ok && s.clip != null && s.clip.loadType == AudioClipLoadType.Streaming)
            {
                Debug.LogWarning($"[Sound] {res} が Streaming で取り込まれています。"
                                 + "ループの頭出しで引っかかります。"
                                 + "`.\\tools\\unity.ps1 menu sound-import` を実行すること。");
            }

            // ⚠ **spatializer はモノのクリップしか処理しない。** ステレオを渡すと
            //    `spatialBlend=1` にしても頭の中で鳴り続ける（`SpatialAudio.CountStereo` が数える）。
            if (spatial) SpatialAudio.Configure(s, minDistance, maxDistance);
            else SpatialAudio.MakeFlat(s);

            if (lowPass)
            {
                bed.lpf = go.AddComponent<AudioLowPassFilter>();
                bed.lpf.cutoffFrequency = 22000f;
                bed.lpf.lowpassResonanceQ = 1f;
            }
            bed.src = s;
            if (bed.ok) s.Play();          // 常に回しておき、音量だけで出し入れする
            return bed;
        }

        private void LoadSpot(string res)
        {
            if (_spot.ContainsKey(res)) return;
            var clip = Resources.Load<AudioClip>(ResourceDir + res);
            _spot[res] = clip;
            if (clip != null) ClipsResolved++;
            else
            {
                ClipsMissing++;
                Debug.LogWarning($"[Sound] 音が見つかりません: Resources/{ResourceDir}{res}");
            }
        }

        // ---------------------------------------------------------------- 毎フレーム

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;   // 演出で timeScale を触っても音は実時間で進む
            Resolve(dt);

            var st = ReadState();
            float glitchLevel = _glitch != null ? _glitch.Level : 0f;

            if (cuesEnabled)
            {
                var fired = _cues.Tick(dt, st, glitchLevel, out int n);
                for (int i = 0; i < n; i++) FireCue(fired[i]);
            }

            var g = _beds.Tick(dt, st);
            ApplyBeds(g);
            _bgm?.SetDuck(g.duck);
            // ⚠ **劇伴は敷く音ではない**（`masterGain` も `bedsEnabled` も掛けない）。
            //    高さは show.json の bgm 側が持っていて、ここが決めるのは「居てよいか」だけ。
            ScoreGain = g.score;
            _bgm?.SetScoreGain(g.score);
        }

        private void Resolve(float dt)
        {
            _resolveAccum += dt;
            if (_intro != null && _run != null && _resolveAccum < 2f) return;
            _resolveAccum = 0f;
            if (_intro == null) _intro = FindObjectOfType<IntroDirector>();
            if (_outro == null) _outro = FindObjectOfType<OutroDirector>();
            if (_title == null) _title = FindObjectOfType<TitleScreen>();
            if (_run == null) _run = FindObjectOfType<ShowRunDirector>();
            if (_signal == null) _signal = FindObjectOfType<SignalLostFx>();
            if (_glitch == null) _glitch = FindObjectOfType<GlitchFx>();
            if (_show == null) _show = FindObjectOfType<ShowControlClient>();
            if (_box == null) _box = FindObjectOfType<SealedBox>();
            if (_bgm == null) _bgm = FindObjectOfType<BgmDirector>();
            if (_timeline == null) _timeline = FindObjectOfType<TimelineDirector>();
            if (_cg == null) _cg = FindObjectOfType<Cg.ShowCgLayer>();
            if (_takes == null) _takes = FindObjectOfType<TakeRunner>();
            // 音を置く先（`canon/LEDGER.md` 0130）。**掴めるまで 2 秒ごとに探し続ける** —
            // スクリーンは prefab instance なので、ここが起きる順番に依らない。
            if (_screen == null)
            {
                var overlay = FindObjectOfType<ScreenOverlayController>();
                if (overlay != null) _screen = overlay.transform;
            }
            if (_head == null)
            {
                var anchor = GameObject.Find("CenterEyeAnchor");
                if (anchor != null) _head = anchor.transform;
                else if (Camera.main != null) _head = Camera.main.transform;
            }
        }

        private SoundShowState ReadState()
        {
            var s = SoundShowState.Idle;
            if (_title != null)
            {
                s.titleVisible = _title.IsBlocking;
                s.titleGlyphShowing = _title.GlyphShowing;
            }
            if (_intro != null)
            {
                s.introActive = _intro.Active;
                s.introStage = _intro.Stage;
                s.introWeights = _intro.Weights;
            }
            if (_outro != null && _outro.Active)
            {
                s.outroActive = true;
                s.outroStage = _outro.Stage;
                // ⚠ 2026-08-15 から**重みは受け取らない**（終幕は覆いにも隔離にも触らないので
                //    `IntroWeights` を出さなくなった）。
                // ⚠⚠ 2026-08-23 から**段の進みも渡さない**（`canon/LEDGER.md` 0111）。
                //    段相対の進みを音のランプに使うと、段の尺を変えたときに音だけが黙って
                //    速くなる。音は終幕の頭からの経過だけを読み、固定の尺で引く。
                s.outroElapsedSec = _outro.TotalElapsedSec;
            }
            if (_run != null)
            {
                s.phase = _run.Phase;
                s.decay = _run.ScreenDecay;
            }
            if (_signal != null) s.signalLost = _signal.Level;
            if (_show != null) s.registrationActive = _show.CourseRegistrationActive;
            // ⚠ ここが立った縁で人形が笑う。**「4 周目 A」とは書かない** — 締めのカットが
            //    待っていること自体を見るので、著作が変わっても追随する。
            if (_timeline != null)
            {
                s.markWaiting = _timeline.IsWaitingForVisitorMark;
                // 増えるのは C だけ（区間のカメラ。画面に映っているカメラではない）。
                s.camera = _timeline.CurrentCamera;
                // ⚠ **区間の周**を読む（進行の周ではない・`rules/show-design.md`「周回数は 2 つある」）。
                //    呪いの 2 本は「体験者がいま居る区間」で決まる。
                s.lap = _timeline.CurrentLap;
            }
            // ⚠ **呪いが排除されたか**（0129 / 0131）。画が戻るのと同じ 1 本を読む。
            if (_run != null) s.curseReleased = _run.ScreenDecayReleased;
            // ⚠ **異世界が映っているか。** 画面を取っているカットの素材 id で判じる（上の但し書き）。
            //   判定は TakeRunner の 1 本（時計も同じものを読む・`canon/LEDGER.md` 0167）。
            if (_takes != null)
            {
                s.otherworld = _takes.OtherworldActive;
                // ⚠ 心音の始点と終点（0175）。**どちらも「縁」で、区間の条件ではない** —
                //    判定は `SoundBedLogic.Tick` のラッチが持つ（引き返しで消えないため）。
                //    始点 = 呼びかけのカット（人形視点の連なりの最後）が画面から降りた所。
                //    終点 = 入れ替わりの再生（3 周目 A の録画カット）が終わった所。
                s.dollCallShowing = _takes.DollCallShowing;
                s.recPlaying = _takes.ActiveRecordingLap >= 0;
            }
            // ⚠ ここが立った縁 ＝ **体験者と人形が入れ替わった瞬間**（`canon/LEDGER.md` 0086）。
            //    「3 周目」と書かず、人形が立っていること自体を見る。
            // ⚠⚠ **DollVisible を読む（IsVisible ではなく）。** 持続の覆い（swapHold）と
            //    ほどける段は人の代役を立てるので、IsVisible だと**黒に包まれた「まだ人形で
            //    ないもの」に人形の笑いが付く**。実測（2026-08-22 走行 20260822_080731）:
            //    2 周目 C の保持中に sndSwap 0 → 2.29 / swell 0 → 0.71 まで育っていた。
            //    人形の声が鳴り始めてよいのは、晴れて実体が出た縁だけ。
            if (_cg != null) s.dollPresent = _cg.DollVisible;
            return s;
        }

        /// <summary>
        /// その節目の音源を 1 つ選ぶ（変種を持つものは順に回す）。
        /// ⚠ <b>回す状態は 1 か所</b>（<see cref="FireCue"/> と <see cref="PlaySpot"/> が共有する）。
        /// 2 か所に持つと、同じ音を別の経路から鳴らしたときに同じ波形が並ぶ。
        /// </summary>
        private string NextResource(SoundCue c)
        {
            string baseName = SoundCueLogic.ResourceName(c);
            if (baseName.Length == 0) return "";
            int n = SoundCueLogic.VariantCount(c);
            if (n <= 1) return baseName;
            int v = _variant.TryGetValue(c, out int cur) ? cur : 0;
            _variant[c] = (v + 1) % n;
            return $"{baseName}_{v + 1}";
        }

        private void FireCue(SoundCue c)
        {
            string res = NextResource(c);
            if (!_spot.TryGetValue(res, out var clip) || clip == null) return;
            // ⚠ **乱れだけ、起きた回数で少しずつ大きくなる**（`canon/LEDGER.md` 0055）。
            //   数えているのは GlitchFx 1 か所で、画と同じ進みを読む — 音が別に数えると
            //   「画は激しいのに音は同じ」が沈黙して起きる。
            //   ⚠ 上げ幅は 1.94 dB しかない。SfxPlayer が Clamp01 するので上へは伸ばせず、
            //     天井の内側で下から上げている（GlitchEscalationLogic.SfxGainAtFirst）。
            float gain = c == SoundCue.Glitch && _glitch != null ? _glitch.SfxGain : 1f;
            // ⚠ masterGain は SfxPlayer.Play の中で掛かる。ここで渡すと**二乗になる**
            //   （既定 1.0 なので今まで見えていなかっただけ。下げた瞬間に効果音だけ沈む）。
            _sfx?.Play(clip, gain, at: PlaceFor(c));
            _beds.PushSpotDuck(SoundCueLogic.DuckFor(c));
            LastCue = c;
        }

        /// <summary>
        /// <b>外から 1 発鳴らす</b>（カットが持つ音。<c>canon/LEDGER.md</c> 0109 の人形の呼びかけ）。
        /// 掴めていなければ <c>false</c> を返す（呼んだ側が沈黙に気づける）。
        ///
        /// ⚠⚠ <b>音程も音量も散らさない。</b> <see cref="SfxPlayer.Play"/> の既定は
        /// 音程 ±3% / 音量 ±1.2dB で、それは「1 回の体験で 9 回以上並ぶ装置の音」のための値。
        /// <b>もらった人の声を毎回わずかに変えるのは、もらった音を別の音にすること</b>
        /// （`rules/sound-design.md` §4.5）。1 度しか鳴らないので散らす理由も無い。
        ///
        /// ⚠ <see cref="SwitchAudioCue"/> には相乗りさせない。あちらは <c>AudioSource</c> を
        /// 1 本使い回して <c>Stop()</c> してから鳴らすので、<b>1.6 秒の声は次の切替で打ち切られる</b>
        /// （2 周目 C の刻みは実測 0.8 秒）。<see cref="SfxPlayer"/> は 6 声あるので生き残る。
        /// </summary>
        /// <param name="follow">
        /// 渡すと、鳴っているあいだ<b>この Transform に付いていく</b>（0139）。
        /// 目のように「置き場所そのものが動く」音に要る。
        /// ⚠ 呼びかけ（後ろから）は<b>渡してはいけない</b> — 振り向いても声が回り込み続ける。
        /// </param>
        public bool PlaySpot(SoundCue c, Vector3? at = null,
                             float pitchSpread = 0f, float gainSpreadDb = 0f,
                             Transform? follow = null)
        {
            string res = NextResource(c);
            if (res.Length == 0) return false;
            if (!_spot.TryGetValue(res, out var clip) || clip == null) return false;
            // ⚠ **鳴らせたかを見る。** 声が 1 本も無い（発声器が起きていない）ときに
            //    true を返すと、呼んだ側の警告が出ないまま無音になる。
            // ⚠ 置き場所は呼んだ側が指せる（目は「その目の方角」から鳴る・0131）。
            //    指さなければ <see cref="PlaceFor"/> が決める。
            if (_sfx == null || !_sfx.Play(clip, gain: 1f, pitchSpread: pitchSpread,
                                           gainSpreadDb: gainSpreadDb, at: at ?? PlaceFor(c),
                                           follow: follow))
                return false;
            _beds.PushSpotDuck(SoundCueLogic.DuckFor(c));
            LastCue = c;
            return true;
        }

        /// <summary>
        /// その節目をどこから鳴らすか（<c>null</c> ＝ 2D・2026-09-03・<c>canon/LEDGER.md</c> 0130）。
        ///
        /// **スクリーンから鳴るのは装置が出す音だけ。** 割れる音（<see cref="SoundCue.Shatter"/>）は
        /// <b>現実が割れる</b>音で視界ぜんたいに起きるし、鈴（<see cref="SoundCue.Bell"/>）と
        /// 題字の音は誰も鳴らしていない ＝ どちらも装置の外側。1 点に置くと出どころが
        /// できてしまうので 2D のまま置く。
        ///
        /// ⚠ 置き場所を掴めなければ <c>null</c> を返して 2D へ落とす。
        /// <b>黙って別の場所から鳴らすより、定位を捨てる方が事故が小さい。</b>
        /// </summary>
        private Vector3? PlaceFor(SoundCue c)
        {
            switch (c)
            {
                // 装置の音 — スクリーンから
                case SoundCue.Glitch:
                case SoundCue.ScreenOn:
                case SoundCue.PowerOff:
                    return _screen != null ? _screen.position : (Vector3?)null;
                // 人形の呼びかけ — 鳴らした瞬間の**後ろ**（そこに置いたまま動かさない）
                case SoundCue.DollCall:
                    if (_head == null)
                    {
                        LastCallAzimuthDeg = -1f;
                        CallCount++;
                        return null;
                    }
                    var at = SpatialAudio.Behind(_head, SpatialAudio.PickCallOffAxisDeg(),
                                                 out float az);
                    LastCallAzimuthDeg = az;
                    CallCount++;
                    return at;
                default:
                    return null;
            }
        }

        private void ApplyBeds(in SoundBedGains g)
        {
            float m = bedsEnabled ? masterGain : 0f;
            float sum = 0f;
            sum += Set(_seal, g.seal * m);
            // 部屋のトーンは 1 本（導入と終幕だけ・`canon/LEDGER.md` 0115）。
            sum += Set(_room, g.room * m);
            sum += Set(_device, g.device * m);
            sum += Set(_worn, g.deviceWorn * m);
            sum += Set(_noise, g.noise * m);
            // 劇伴を置き換える 3 つ（0131）。⚠ **`masterGain` は掛ける**（現場で全体を下げたときに
            //    ここだけ残ると、下げた意味が無くなる）。劇伴と違って敷く音の器に載っているため。
            WindGain = Set(_wind, g.wind * m);
            CurseGain = Set(_beat, g.beat * m);
            sum += WindGain + CurseGain;
            sum += Set(_horror2, g.horror2 * m);
            WhiteGain = Set(_white, g.white * m);
            sum += WhiteGain;
            // 心音（0175）。⚠ **置き換えではなく足す**ので、劇伴の取り分（`score`）には触らない。
            HeartGain = Set(_heart, g.heart * m);
            sum += HeartGain;
            // ⚠ **鳴り始めは必ず輪の同じ所から。** 12 秒の輪を常時回しているので、
            //    頭出ししないと**体験者ごとに違う所から笑い出す**（走行の再現性が消える）。
            // ⚠⚠ **体ぜんぶを同じ所へ頭出しする**（0139）。表は 8 体の掛け合いとして composed して
            //    あるので（「順番を変えながら次々に笑う」）、体ごとにずらすと**その composition が壊れる**。
            //    ずれは表の中の時刻が既に持っている。
            float dollsGain = g.dolls * m;
            sum += SetLaughLayer(_dolls, dollsGain);
            DollsGain = dollsGain;

            // ⚠⚠ **入れ替わった人形も輪の頭から。** 一人ぶんの 1 声目は輪の 0 秒に置いてあるので、
            //    頭出ししないと**入れ替わった瞬間に笑い声が来ない**（無音の所から鳴り始める）。
            //    増える 2 枚も同じ扱いにする（走行ごとに違う所から入ると再現性が消える）。
            float oneGain = g.dollOne * m;
            float growAGain = g.dollsGrowA * m;
            float growBGain = g.dollsGrowB * m;
            sum += SetLaughLayer(_dollOne, oneGain);
            sum += SetLaughLayer(_dollGrowA, growAGain);
            sum += SetLaughLayer(_dollGrowB, growBGain);
            DollSwapGain = oneGain + growAGain + growBGain;
            AudibleSum = sum;

            // 隔離は音量ではなく**帯域**で表す（音量を下げると「遠ざかった」に聞こえる）。
            //
            // ⚠⚠ **周波数は対数で写す。** 2026-08-12 の初版は `Lerp(380, 22000, open^0.45)` で、
            //    閉じ切った状態（open=0.16）でも **9.9kHz** までしか下がらなかった（実機ログ実測）。
            //    部屋のトーンは中身の大半が 4kHz より下なので、**10kHz の低域通過は何もしないのと同じ**。
            //    絵は閉じているのに音は開いたまま、という食い違いが実機で起きていた。
            //    人の音高の知覚は対数なので、線形補間は必ず上へ偏る。
            RoomCutoffHz = ClosedCutoffHz
                           * Mathf.Pow(OpenCutoffHz / ClosedCutoffHz, Mathf.Clamp01(g.roomOpen));
            if (_room.lpf != null) _room.lpf.cutoffFrequency = RoomCutoffHz;

            // 封印の箱の唸りは**箱そのものから**鳴る。箱が無ければ 2D へ落とす
            // （黙って別の場所から鳴らすより、定位を捨てる方が事故が小さい）。
            if (_seal.src != null)
            {
                if (_box != null && _box.IsBuilt)
                {
                    _seal.src.transform.position = _box.transform.position;
                    _seal.src.spatialBlend = 1f;
                }
                else
                {
                    _seal.src.spatialBlend = 0f;
                }
            }

            PlaceBeds();
        }

        /// <summary>
        /// 敷く音を毎フレーム置き直す（2026-09-03・<c>canon/LEDGER.md</c> 0130）。
        ///
        /// | 何 | どこ |
        /// |---|---|
        /// | 装置の唸り・砂嵐 | スクリーン（<c>ScreenAnchor</c> が運んでいる Transform） |
        /// | 人形の笑い 4 声 | 頭を中心とした輪の <see cref="_laughBearings"/> |
        ///
        /// ⚠⚠ <b>笑いは頭の位置に付いていく（向きには付いていかない）。</b> 体験者は区間ごとに
        /// 歩くので、ワールドへ釘で留めると「周囲に居る」が保てない。方角はワールドで固定なので、
        /// <b>振り向けば笑い声の方を向ける</b>。
        ///
        /// ⚠ 置き先を掴めなければ 2D へ落とす。<see cref="SpatialBedsAudible"/> がそのぶん減るので、
        /// 「3D にしたつもりで頭を掴めていない」がテレメトリに出る。
        /// </summary>
        private void PlaceBeds()
        {
            int n = 0;
            n += PlaceAt(_device, _screen);
            n += PlaceAt(_worn, _screen);
            n += PlaceAt(_noise, _screen);
            int audible = 0;
            n += PlaceLaughLayer(_dolls, ref audible) + PlaceLaughLayer(_dollOne, ref audible)
                 + PlaceLaughLayer(_dollGrowA, ref audible) + PlaceLaughLayer(_dollGrowB, ref audible);
            LaughPoints = audible;
            SpatialBedsAudible = n;
        }

        /// <summary>層ぜんぶへ同じ高さを書く（体は同じ層の中では同じ大きさ）。</summary>
        private float SetLaughLayer(LaughLayer layer, float gain)
        {
            // 頭出しの縁は層で 1 度だけ見る（体ごとに見ても同じ縁になるが、意図を 1 か所に置く）。
            bool audible = gain > 0.0005f;
            bool cue = audible && !layer.audible;
            layer.audible = audible;

            float sum = 0f;
            foreach (BedVoice b in layer.bodies)
            {
                if (cue && b.src != null && b.ok && b.src.clip != null) b.src.time = 0f;
                sum += Set(b, gain);
            }
            return sum;
        }

        /// <summary>
        /// 層の体を輪のそれぞれのスロットへ置く。返すのは<b>鳴っていて 3D な体の数</b>で、
        /// <paramref name="audible"/> には<b>実際に聞こえる大きさで鳴っている体</b>だけを足す。
        ///
        /// ⚠⚠ <b>2 つは別の数</b>（2026-09-04 の走行で分かった）。
        /// 層の出し入れは半減期で寄せるので、切り替わった後も消えかけの声が長く 0 に届かない。
        /// 「置けているか」の閾値（<c>0.0005</c>）だと <b>40dB 下の尾まで数える</b>
        /// （旧: 群れ 0.01 で 8 か所）。⇒ 「何か所から聞こえるか」は
        /// <see cref="LaughAudibleGain"/> で数える（走行 20260904_131840 で 0.00 → 0 か所を確認）。
        /// </summary>
        private int PlaceLaughLayer(LaughLayer layer, ref int audible)
        {
            int n = 0;
            for (int i = 0; i < layer.bodies.Length; i++)
            {
                BedVoice b = layer.bodies[i];
                n += PlaceOnRing(b, layer.slots[i]);
                if (b.ok && b.src != null && b.src.volume > LaughAudibleGain
                    && b.src.spatialBlend >= 1f) audible++;
            }
            return n;
        }

        /// <summary>1 本を <paramref name="at"/> へ置く。鳴っていて 3D なら 1 を返す。</summary>
        private static int PlaceAt(BedVoice b, Transform? at)
        {
            if (b.src == null || !b.ok) return 0;
            if (at == null) { SpatialAudio.MakeFlat(b.src); return 0; }
            b.src.transform.position = at.position;
            if (b.src.spatialBlend < 1f) SpatialAudio.Configure(b.src);
            return b.src.volume > 0.0005f ? 1 : 0;
        }

        /// <summary>1 本を笑いの輪の <paramref name="slot"/> 番へ置く。</summary>
        private int PlaceOnRing(BedVoice b, int slot)
        {
            if (b.src == null || !b.ok) return 0;
            if (_head == null) { SpatialAudio.MakeFlat(b.src); return 0; }
            SpatialAudio.LaughSpot spot = _laughSpots[slot];
            b.src.transform.position = SpatialAudio.Ring(_head, spot.bearingDeg,
                                                         spot.radiusM, spot.dropM);
            if (b.src.spatialBlend < 1f) SpatialAudio.Configure(b.src);
            return b.src.volume > 0.0005f ? 1 : 0;
        }

        private static float Set(BedVoice b, float gain)
        {
            if (b.src == null || !b.ok) return 0f;
            float v = Mathf.Clamp01(gain);
            b.src.volume = v;
            if (v > 0.0005f && !b.src.isPlaying) b.src.Play();
            return v;
        }

        // ⚠⚠ **頭出しは <see cref="SetLaughLayer"/> が持つ**（2026-09-04・0139）。
        //    敷く音は起動時から音量 0 で回りっぱなしなので、そのままだと**鳴り始める瞬間に
        //    素材のどこに居るかが走行ごとに違う**（＝ 同じ設定で毎回違う体験）。
        //    旧 `CueAmbientStart` は 1 本ずつ縁を見る形だったが、笑いが体ごとに分かれて
        //    **層の中の体は同じ縁で同じ所へ**戻さなければならなくなった（掛け合いが崩れる）ので、
        //    層でまとめて見る形へ移した。
        //    ⚠ 周ごとの環境音（`bed_room_lap2` / `_lap3`）でも使っていたが 0115 で退役した。

        // ---------------------------------------------------------------- 外からの号令

        /// <summary>ラン開始（体験者交代）。**前の体験者の音を持ち越さない。**</summary>
        public void ResetRun()
        {
            _cues.ResetRun();
            _beds.Reset();
            _sfx?.StopAll();
            LastCue = SoundCue.None;
            // 次の体験者でも同じ所から笑いが入る（頭出しの縁を作り直す）。
            _dolls.audible = _dollOne.audible = _dollGrowA.audible = _dollGrowB.audible = false;
            // ⚠ **方角は体験者ごとに引き直す**（`canon/LEDGER.md` 0130「ランダムな位置」）。
            //    走行のあいだは動かさない — 鳴っている最中に動かすと、人形が歩いて聞こえる。
            SpatialAudio.PickLaughSpots(_laughSpots);
            LastCallAzimuthDeg = -1f;
            CallCount = 0;
        }
    }
}
