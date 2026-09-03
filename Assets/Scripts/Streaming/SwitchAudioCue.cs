#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// カメラ切替の音（監視装置のリレー）。
    /// <see cref="CameraSwitchDirector"/> が dip-to-black の開始と同時に <see cref="Play"/> を呼ぶ
    /// （黒 70ms が視覚差替に先行するので J カット相当が成立する）。
    ///
    /// **1 回の体験で 16 回鳴る、この作品でいちばん多い音。** だから 3 つ守る:
    ///
    /// 1. <b>変種を回す</b> — 同じ波形が並ぶと装置ではなく「効果音」に聞こえる
    /// 2. <b>音程と音量をわずかに散らす</b> — 実物の接点は毎回わずかに違う音がする
    /// 3. <b>DSP 時刻で予約する</b> — <c>PlayOneShot</c> は「次の音声バッファの頭」で鳴り、
    ///    この機の粒は <b>21.3ms</b>（1024 標本 / 48kHz）。暗転の下りが 70ms なので、
    ///    粒のままだと音が黒の中に入る回と外れる回ができる ＝ <b>J カットが毎回別物になる</b>
    ///
    /// ⚠⚠ <b>2026-08-23 から変種は 6 本</b>（<c>canon/LEDGER.md</c> 0112・ユーザー指示
    /// 「カメラが切り替わるときの音を毎回同じではなく、加工して毎回違うようにしたい」
    /// 「使う音声素材を増やすのではなくうまく加工して」）。
    /// <b>音源は 0057 でもらった 1 本のまま</b>で、<c>tools/ingest-sounds.py</c> の
    /// <c>SWITCH_VARIANTS</c> が**尾の来る時刻・長さ・高さ・音程**を変えて 6 本に焼く。
    /// 頭の一撃（0〜50ms）はどの変種にもそのまま入っている ＝ 同じ装置に聞こえる。
    ///
    /// ⚠ <b>合成で変種を作らない。</b> 2026-08-16 以前にあった <c>make-sounds.py</c> の
    /// 合成 3 変種とは別物で、いまの 6 本はすべて<b>もらった音源 1 本から</b>出ている
    /// （もらった音を別の音にしない・<c>rules/sound-design.md</c> §4.5）。
    ///
    /// ⚠ クリップ未設定なら無音スキップ（仕組みだけ残す）。既定の音は
    /// <c>Resources/Sound/sfx_switch_1..6</c> から拾う（`tools/ingest-sounds.py` が焼く）。
    ///
    /// ⚠⚠ <b>人形視点が差し込まれるカットだけ、警告音つきの音で鳴る</b>
    /// （<c>canon/LEDGER.md</c> 0106 ・<see cref="PlayAlert"/>）。ゾーン切替は素の音のまま。
    /// 呼び分けているのは <see cref="CameraSwitchDirector.PlaySwitchSfx"/> 1 か所だけで、
    /// そこを通るのは<b>カットの <c>switchSfx</c></b>（＝ カメラを動かさない素材カットが
    /// 「視点が急に切り替わった」として鳴らす音）。
    /// <b>警告つきも同じ 6 本に対応して焼いてある</b> — 素だけ変種にすると、
    /// いちばん連続して鳴る 2 周目 C の 5 発が「毎回同じ」に戻る。
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class SwitchAudioCue : MonoBehaviour
    {
        /// <summary><c>Resources</c> の既定音（Inspector が空のときに拾う）。</summary>
        public const string DefaultResourcePrefix = "Sound/sfx_switch_";

        /// <summary>
        /// <b>人形視点が差し込まれるカット</b>の音（<c>canon/LEDGER.md</c> 0106）。
        /// 素の切替音の上に、もらった警告音を 240ms だけ -12dB で重ねた 6 本で、
        /// 焼くのは <c>tools/ingest-sounds.py</c> の <c>MIXES</c>。
        ///
        /// ⚠ <b>Inspector の口は持たない</b>（差し替えるなら <c>MIXES</c> の 2 つの数を直して焼き直す）。
        /// SerializeField にすると、シーンに焼かれた null と Resources の関係を次の人が調べることになる。
        /// </summary>
        public const string AlertResourcePrefix = "Sound/sfx_switch_alert_";

        /// <summary>
        /// 既定音の本数。<b>2026-08-23 に 1 → 6</b>（<c>canon/LEDGER.md</c> 0112）。
        /// ⚠ ここを増やしても音は増えない — 先に <c>tools/ingest-sounds.py</c> の
        /// <c>SWITCH_VARIANTS</c> へ行を足して焼くこと（素と警告つきの<b>両方</b>）。
        /// </summary>
        public const int DefaultVariantCount = 6;

        [Tooltip("再生に使う AudioSource。null なら同 GameObject から取得。")]
        [SerializeField] private AudioSource? source;

        [Tooltip("切替時に鳴らすクリップ。**空なら Resources/Sound/sfx_switch_1..6 を使う。**")]
        [SerializeField] private AudioClip[] switchClips = new AudioClip[0];

        [Tooltip("切替音の音量。")]
        [Range(0f, 1f)]
        [SerializeField] private float gain = 0.85f;

        [Tooltip("1 回ごとに音程を振る幅（±）。0 で振らない。")]
        [Range(0f, 0.2f)]
        [SerializeField] private float pitchSpread = 0.035f;

        private AudioClip[] _clips = new AudioClip[0];
        private AudioClip[] _alertClips = new AudioClip[0];
        private int _last = -1;
        private int _usedMask;

        /// <summary>鳴らした累計（テレメトリ用）。<b>警告つきもここに乗る。</b></summary>
        public int PlayedCount { get; private set; }

        /// <summary>警告つきで鳴らした累計（テレメトリ用）。</summary>
        public int AlertCount { get; private set; }

        /// <summary>掴めている変種の本数（テレメトリ用）。<b>焼き忘れはここに出る。</b></summary>
        public int ClipCount => _clips.Length;

        /// <summary>
        /// この走行で<b>実際に鳴った異なり数</b>（テレメトリ用）。
        /// ⚠ 累計（<see cref="PlayedCount"/>）だけでは「6 本焼いたのに 1 本しか鳴っていない」を
        /// 見分けられない。音は録画にも画にも出ないので、ここが唯一の証拠になる。
        /// </summary>
        public int DistinctUsedCount
        {
            get
            {
                int n = 0;
                for (int m = _usedMask; m != 0; m >>= 1) n += m & 1;
                return n;
            }
        }

        /// <summary>音源を掴めているか。**false なら切替は無音のまま。**</summary>
        public bool HasClips => _clips.Length > 0;

        /// <summary>
        /// 警告つきの音源を掴めているか。**false なら人形視点の差し込みも素の切替音で鳴る**
        /// （無音にはしない — 切替音そのものが消えると体験が壊れる）。
        /// </summary>
        public bool HasAlertClip => _alertClips.Length > 0;

        private void Awake()
        {
            if (source == null) source = GetComponent<AudioSource>();
            if (source != null)
            {
                source.playOnAwake = false;
                source.loop = false;
                // ⚠⚠ **切替音はスクリーンから鳴る**（2026-09-03・`canon/LEDGER.md` 0130）。
                //    この部品は Screen（`ScreenOverlayController` の GameObject）に乗っていて、
                //    その Transform は `ScreenAnchor` が頭の正面へ運んでいる ＝
                //    **置き直す必要が無い**。ここで 3D にするだけで音がスクリーンに付く。
                //    ⚠ 大きさは変えない（`SpatialAudio` の但し書き。減衰の区間へ入らない）。
                SpatialAudio.Configure(source);
            }
            ResolveClips();
        }

        private void ResolveClips()
        {
            // ⚠ 警告つきは素の切替音とは独立に解決する（片方が無くてももう片方は鳴る）。
            _alertClips = LoadSeries(AlertResourcePrefix);
            if (_alertClips.Length < DefaultVariantCount)
            {
                Debug.LogWarning($"[Sound] 警告つきの切替音が {_alertClips.Length}/{DefaultVariantCount} "
                                 + $"しかありません（Resources/{AlertResourcePrefix}1..{DefaultVariantCount}）。"
                                 + "足りないぶんは素の切替音で鳴ります。"
                                 + "`py -3.11 tools/ingest-sounds.py --only sfx_switch_1` の後に "
                                 + "`.\\tools\\unity.ps1 menu sound-import` を走らせること。");
            }

            if (switchClips != null && switchClips.Length > 0)
            {
                var keep = new System.Collections.Generic.List<AudioClip>(switchClips.Length);
                foreach (var c in switchClips)
                {
                    if (c != null) keep.Add(c);
                }
                if (keep.Count > 0) { _clips = keep.ToArray(); return; }
            }
            _clips = LoadSeries(DefaultResourcePrefix);
            if (_clips.Length == 0)
            {
                Debug.LogWarning($"[Sound] 切替音がありません"
                                 + $"（Resources/{DefaultResourcePrefix}1..{DefaultVariantCount}）。"
                                 + "カメラ切替は無音のままになります。"
                                 + "`py -3.11 tools/ingest-sounds.py --only sfx_switch_1` を走らせたか確認すること。");
            }
            else if (_clips.Length < DefaultVariantCount)
            {
                Debug.LogWarning($"[Sound] 切替音が {_clips.Length}/{DefaultVariantCount} しかありません。"
                                 + "そのぶん同じ波形が並びます（`canon/LEDGER.md` 0112）。");
            }
        }

        private static AudioClip[] LoadSeries(string prefix)
        {
            var found = new System.Collections.Generic.List<AudioClip>(DefaultVariantCount);
            for (int i = 1; i <= DefaultVariantCount; i++)
            {
                var c = Resources.Load<AudioClip>($"{prefix}{i}");
                if (c != null) found.Add(c);
            }
            return found.ToArray();
        }

        /// <summary>切替音を 1 回鳴らす。クリップ未設定なら何もしない。</summary>
        public void Play()
        {
            if (source == null || _clips.Length == 0) return;
            PlayClip(_clips[Pick(_clips.Length)]);
        }

        /// <summary>
        /// <b>警告音つき</b>で 1 回鳴らす（人形視点が差し込まれるカット・<c>canon/LEDGER.md</c> 0106）。
        /// 警告つきの音源を掴めていなければ<b>素の切替音へ落ちる</b>（無音にはしない）。
        /// </summary>
        public void PlayAlert()
        {
            if (_alertClips.Length == 0) { Play(); return; }
            if (source == null) return;
            PlayClip(_alertClips[Pick(_alertClips.Length)]);
            AlertCount++;
        }

        /// <summary>
        /// 変種を 1 つ選ぶ。<b>直前と同じものは引かない</b>（`TypeAudioCue.Pick` と同じ流儀）。
        ///
        /// ⚠ <b>袋（1 巡で全部使う）にはしない。</b> ラン跨ぎの状態を持つと、2026-08-15 に踏んだ
        /// 「落とす経路の呼び出し元がどこにも無く、2 人目以降だけ挙動が変わる」型に当たる。
        /// 直前 1 つだけを覚える形なら、体験者が替わっても壊れようが無い。
        ///
        /// ⚠ <b>素と警告つきで番号を共有する。</b> 2 周目 C は素と警告つきが交ざって鳴るので、
        /// 別々に覚えると「素 → 警告 → 素」で土台が同じものに戻る。
        /// </summary>
        private int Pick(int count)
        {
            if (count <= 1) { _last = 0; _usedMask |= 1; return 0; }
            int v = Random.Range(0, count - 1);
            if (v >= _last) v++;                 // 直前を飛ばす（一様なまま 1 つ除ける）
            _last = v;
            _usedMask |= 1 << v;
            return v;
        }

        private void PlayClip(AudioClip? clip)
        {
            if (source == null || clip == null) return;

            source.clip = clip;
            source.pitch = 1f + Random.Range(-pitchSpread, pitchSpread);
            source.volume = Mathf.Clamp01(gain * SoundFade.DbToLin(Random.Range(-1.5f, 1.5f)));
            source.Stop();
            // ⚠ 予約の先読みは音声バッファ 1 個ぶんより長く取る（短いと「もう過ぎた時刻」を
            //    指す回ができ、その回だけ遅れる ＝ ときどきずれる、という最も追いにくい壊れ方）。
            source.PlayScheduled(AudioSettings.dspTime + SfxPlayer.ScheduleLeadSec);
            PlayedCount++;
        }
    }
}
