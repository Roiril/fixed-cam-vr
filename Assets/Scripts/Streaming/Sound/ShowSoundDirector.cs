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
        /// 人形の笑い。**報告を押すまでループ**（<c>canon/LEDGER.md</c> 0066）。
        /// 敷く音の器に載せているが地の音ではない — ループして出し入れできるのがここだけだから。
        /// </summary>
        private BedVoice _dolls = new BedVoice();
        private bool _dollsAudible;

        /// <summary>
        /// 入れ替わった人形の笑い（3 周目・<c>canon/LEDGER.md</c> 0086）。
        /// **一人 ＋ 増える 2 枚**で、C のあいだに後ろの 2 枚が入ってくる。
        /// ⚠ ループ長を互いに素にしてある（11 / 13 / 17 秒）ので、3 枚が同じ所で巻き戻らない。
        /// </summary>
        private BedVoice _dollOne = new BedVoice();
        private BedVoice _dollGrowA = new BedVoice(), _dollGrowB = new BedVoice();
        private bool _dollOneAudible, _dollGrowAAudible, _dollGrowBAudible;
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
        private float _resolveAccum;

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

            _seal = MakeBed("Seal", "bed_seal", spatial: true);
            _room = MakeBed("Room", "bed_room", spatial: false, lowPass: true);
            _device = MakeBed("Device", "bed_device", spatial: false);
            _worn = MakeBed("DeviceWorn", "bed_device_worn", spatial: false);
            _noise = MakeBed("Noise", "bed_static", spatial: false);
            _dolls = MakeBed("Dolls", "bed_dolls_laugh", spatial: false);
            _dollOne = MakeBed("DollOne", "bed_doll_one", spatial: false);
            _dollGrowA = MakeBed("DollGrowA", "bed_dolls_grow_a", spatial: false);
            _dollGrowB = MakeBed("DollGrowB", "bed_dolls_grow_b", spatial: false);

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

        private BedVoice MakeBed(string label, string res, bool spatial, bool lowPass = false)
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

            if (spatial)
            {
                // ⚠ **spatializer はモノのクリップしか処理しない。** ステレオを渡すと
                //    `spatialBlend=1` にしても頭の中で鳴り続ける（`tools/soundkit.py` の write_wav 参照）。
                s.spatialBlend = 1f;
                s.spatialize = true;
                s.rolloffMode = AudioRolloffMode.Logarithmic;
                s.minDistance = 1.6f;
                s.maxDistance = 30f;
                s.dopplerLevel = 0f;      // 体験者が歩いた程度で音程が変わってはいけない
            }
            else
            {
                s.spatialBlend = 0f;
            }

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

        private void FireCue(SoundCue c)
        {
            string baseName = SoundCueLogic.ResourceName(c);
            int n = SoundCueLogic.VariantCount(c);
            string res = baseName;
            if (n > 1)
            {
                int v = _variant.TryGetValue(c, out int cur) ? cur : 0;
                res = $"{baseName}_{v + 1}";
                _variant[c] = (v + 1) % n;
            }
            if (!_spot.TryGetValue(res, out var clip) || clip == null) return;
            // ⚠ **乱れだけ、起きた回数で少しずつ大きくなる**（`canon/LEDGER.md` 0055）。
            //   数えているのは GlitchFx 1 か所で、画と同じ進みを読む — 音が別に数えると
            //   「画は激しいのに音は同じ」が沈黙して起きる。
            //   ⚠ 上げ幅は 1.94 dB しかない。SfxPlayer が Clamp01 するので上へは伸ばせず、
            //     天井の内側で下から上げている（GlitchEscalationLogic.SfxGainAtFirst）。
            float gain = c == SoundCue.Glitch && _glitch != null ? _glitch.SfxGain : 1f;
            // ⚠ masterGain は SfxPlayer.Play の中で掛かる。ここで渡すと**二乗になる**
            //   （既定 1.0 なので今まで見えていなかっただけ。下げた瞬間に効果音だけ沈む）。
            _sfx?.Play(clip, gain);
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
        public bool PlaySpot(SoundCue c)
        {
            string res = SoundCueLogic.ResourceName(c);
            if (res.Length == 0) return false;
            if (SoundCueLogic.VariantCount(c) > 1) return false;   // 変種を持つものは FireCue の側
            if (!_spot.TryGetValue(res, out var clip) || clip == null) return false;
            // ⚠ **鳴らせたかを見る。** 声が 1 本も無い（発声器が起きていない）ときに
            //    true を返すと、呼んだ側の警告が出ないまま無音になる。
            if (_sfx == null || !_sfx.Play(clip, gain: 1f, pitchSpread: 0f, gainSpreadDb: 0f))
                return false;
            _beds.PushSpotDuck(SoundCueLogic.DuckFor(c));
            LastCue = c;
            return true;
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
            // ⚠ **鳴り始めは必ず輪の同じ所から。** 12 秒の輪を常時回しているので、
            //    頭出ししないと**体験者ごとに違う所から笑い出す**（走行の再現性が消える）。
            float dollsGain = g.dolls * m;
            CueAmbientStart(_dolls, dollsGain, ref _dollsAudible, fraction: 0f);
            sum += Set(_dolls, dollsGain);
            DollsGain = dollsGain;

            // ⚠⚠ **入れ替わった人形も輪の頭から。** 一人ぶんの 1 声目は輪の 0 秒に置いてあるので、
            //    頭出ししないと**入れ替わった瞬間に笑い声が来ない**（無音の所から鳴り始める）。
            //    増える 2 枚も同じ扱いにする（走行ごとに違う所から入ると再現性が消える）。
            float oneGain = g.dollOne * m;
            float growAGain = g.dollsGrowA * m;
            float growBGain = g.dollsGrowB * m;
            CueAmbientStart(_dollOne, oneGain, ref _dollOneAudible, fraction: 0f);
            CueAmbientStart(_dollGrowA, growAGain, ref _dollGrowAAudible, fraction: 0f);
            CueAmbientStart(_dollGrowB, growBGain, ref _dollGrowBAudible, fraction: 0f);
            sum += Set(_dollOne, oneGain);
            sum += Set(_dollGrowA, growAGain);
            sum += Set(_dollGrowB, growBGain);
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
        }

        private static float Set(BedVoice b, float gain)
        {
            if (b.src == null || !b.ok) return 0f;
            float v = Mathf.Clamp01(gain);
            b.src.volume = v;
            if (v > 0.0005f && !b.src.isPlaying) b.src.Play();
            return v;
        }

        /// <summary>
        /// 黙っていた敷く音が鳴り始めるとき、**素材の決まった所へ頭出しする**。
        ///
        /// ⚠⚠ 敷く音は起動時から音量 0 で回りっぱなしなので、そのままだと
        /// <b>鳴り始める瞬間に素材のどこに居るかが走行ごとに違う</b>（＝ 同じ設定で毎回違う体験）。
        /// いま使っているのは人形の笑い 3 本で、どれも <c>fraction: 0</c> ＝ 輪の頭から入る
        /// （1 声目がそこに置いてある）。
        ///
        /// ⚠ 周ごとの環境音（<c>bed_room_lap2</c> / <c>_lap3</c>）でも使っていたが、
        /// 2026-08-23 に退役した（<c>canon/LEDGER.md</c> 0115）。あちらは素材の途中
        /// （厚い所）から入れる必要があったので <c>fraction</c> を持たせてある。
        /// </summary>
        private static void CueAmbientStart(BedVoice b, float gain, ref bool wasAudible,
                                            float fraction)
        {
            bool audible = gain > 0.0005f;
            if (audible && !wasAudible && b.src != null && b.ok && b.src.clip != null)
                b.src.time = b.src.clip.length * fraction;
            wasAudible = audible;
        }

        // ---------------------------------------------------------------- 外からの号令

        /// <summary>ラン開始（体験者交代）。**前の体験者の音を持ち越さない。**</summary>
        public void ResetRun()
        {
            _cues.ResetRun();
            _beds.Reset();
            _sfx?.StopAll();
            LastCue = SoundCue.None;
            // 次の体験者でも同じ所から笑いが入る（頭出しの縁を作り直す）。
            _dollsAudible = false;
            _dollOneAudible = _dollGrowAAudible = _dollGrowBAudible = false;
        }
    }
}
