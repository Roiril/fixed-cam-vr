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

        private BedVoice _seal = new BedVoice(), _room = new BedVoice(), _device = new BedVoice();
        // 周ごとの環境音（2026-08-15・`canon/LEDGER.md` 0049）。1 周目は `_room`。
        private BedVoice _room2 = new BedVoice(), _room3 = new BedVoice();
        // 直前のフレームで鳴っていたか（黙り → 鳴り始めの縁で頭出しするために持つ）。
        private bool _room2Audible, _room3Audible;
        private BedVoice _worn = new BedVoice(), _noise = new BedVoice();
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

        // ---------------------------------------------------------------- 生成

        private void Awake()
        {
            _beds.Reset();
            _sfx = GetComponent<SfxPlayer>();
            if (_sfx == null) _sfx = gameObject.AddComponent<SfxPlayer>();

            _seal = MakeBed("Seal", "bed_seal", spatial: true);
            _room = MakeBed("Room", "bed_room", spatial: false, lowPass: true);
            // ⚠ **3 本とも同じ低域通過を通す。** 片方だけ素通しにすると、隔離が閉じる場面で
            //    「狭くなったのに 1 本だけ広いまま」になり、帯域で表しているものが壊れる。
            _room2 = MakeBed("RoomLap2", "bed_room_lap2", spatial: false, lowPass: true);
            _room3 = MakeBed("RoomLap3", "bed_room_lap3", spatial: false, lowPass: true);
            _device = MakeBed("Device", "bed_device", spatial: false);
            _worn = MakeBed("DeviceWorn", "bed_device_worn", spatial: false);
            _noise = MakeBed("Noise", "bed_static", spatial: false);

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
                //    `IntroWeights` を出さなくなった）。音が読むのは段と、その段の進みだけ。
                s.outroProgress01 = _outro.StageProgress01;
            }
            if (_run != null)
            {
                s.phase = _run.Phase;
                s.decay = _run.ScreenDecay;
                s.lap = _run.Lap;      // 周ごとの環境音の入れ替えに使う
            }
            if (_signal != null) s.signalLost = _signal.Level;
            if (_show != null) s.registrationActive = _show.CourseRegistrationActive;
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
            _sfx?.Play(clip, masterGain);
            _beds.PushSpotDuck(SoundCueLogic.DuckFor(c));
            LastCue = c;
        }

        private void ApplyBeds(in SoundBedGains g)
        {
            float m = bedsEnabled ? masterGain : 0f;
            float sum = 0f;
            sum += Set(_seal, g.seal * m);
            // 環境音は周で入れ替わる。取り分は二乗の和が 1 なので、合計の高さは動かない。
            // ⚠ 黙っていた側は、鳴り始める前に素材の決まった所へ頭出しする（下の説明）。
            float lap2Gain = g.room * g.roomLap2 * m;
            float lap3Gain = g.room * g.roomLap3 * m;
            CueAmbientStart(_room2, lap2Gain, ref _room2Audible);
            CueAmbientStart(_room3, lap3Gain, ref _room3Audible);
            sum += Set(_room, g.room * g.roomLap1 * m);
            sum += Set(_room2, lap2Gain);
            sum += Set(_room3, lap3Gain);
            sum += Set(_device, g.device * m);
            sum += Set(_worn, g.deviceWorn * m);
            sum += Set(_noise, g.noise * m);
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
            if (_room2.lpf != null) _room2.lpf.cutoffFrequency = RoomCutoffHz;
            if (_room3.lpf != null) _room3.lpf.cutoffFrequency = RoomCutoffHz;

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
        /// 黙っていた環境音が鳴り始めるとき、**素材の決まった所へ頭出しする**。
        ///
        /// ⚠⚠ 敷く音は起動時から音量 0 で回りっぱなしなので、そのままだと
        /// <b>入れ替わる瞬間に素材のどこに居るかが走行ごとに違う</b>。
        /// `bed_room_lap2` は 1 秒ごとの RMS が **33dB 振れる**（実測: 頭 -56 → 山 -23 → 尻 -50）ので、
        /// 静かな所で入れ替わると「音が消えた」に聞こえ、山で入れ替わると自然に繋がる
        /// ＝ **同じ設定で毎回違う体験になる**。
        ///
        /// 0.25 は測って選んだ値: lap2 は 4.7 秒 ＝ **山（-23dB）**、lap3 は 16.6 秒 ＝
        /// **平坦な本体（-37dB）**。どちらも素材の「厚い所」から入る。
        /// ⚠ 折り返してループにしてあるので、**途中から始めても継ぎ目は無い**。
        /// </summary>
        private const float AmbientStartFraction = 0.25f;

        private static void CueAmbientStart(BedVoice b, float gain, ref bool wasAudible)
        {
            bool audible = gain > 0.0005f;
            if (audible && !wasAudible && b.src != null && b.ok && b.src.clip != null)
                b.src.time = b.src.clip.length * AmbientStartFraction;
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
            // 次の体験者でも同じ所から環境音が入る（頭出しの縁を作り直す）。
            _room2Audible = _room3Audible = false;
        }
    }
}
