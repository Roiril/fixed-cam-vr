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
        /// <b>カットの <c>switchSfx</c> が立つ所</b>の音（<c>canon/LEDGER.md</c> 0106・
        /// いまは人形視点の 5 発と 2 周目 B のバックルームズ 1 発）。
        /// 素の切替音の上に、もらった警告音を 420ms・-4dB・頭から 35ms 遅れで重ねた 6 本で、
        /// 焼くのは <c>tools/ingest-sounds.py</c> の <c>MIXES</c>。
        ///
        /// ⚠⚠ <b>2026-09-04 まで 240ms / -12dB だった</b>（0134・ユーザー逐語
        /// 「実際は他と同じに聞こえている」）。警告だけで -36 LUFS は、下に敷いてある劇伴（-27.7）と
        /// 同じフレームで鳴る乱れの一撃（-23）に埋もれて、素の切替音と区別が付かなかった。
        /// 旧値を決めたときは<b>警告を素の無音の上で聴いていた</b>のが原因。
        ///
        /// ⚠ <b>Inspector の口は持たない</b>（差し替えるなら <c>MIXES</c> の 3 つの数を直して焼き直す）。
        /// SerializeField にすると、シーンに焼かれた null と Resources の関係を次の人が調べることになる。
        /// </summary>
        public const string AlertResourcePrefix = "Sound/sfx_switch_alert_";

        /// <summary>
        /// <b>警告音だけ</b>（頭に 35ms の無音つき・<c>MIXES</c> が 1 本目から切り出して焼く）。
        /// 2026-09-04 に足した（<c>canon/LEDGER.md</c> 0145・ユーザー逐語
        /// 「警告音を、4回数を重ねるにつれて大きくなるようにしてほしい」）。
        ///
        /// ⚠⚠ <b>混ぜた 1 本では大きさを動かせない。</b> <see cref="AlertResourcePrefix"/> は
        /// 土台と警告を焼き込んであるので、音量を上げると<b>土台ごと大きくなる</b>
        /// （0106 / 0134 の「あくまで通常の切り替え音がメイン」が崩れる）。
        /// ⇒ 土台は素の変種を鳴らし、これを<b>同じ時刻に予約して重ねる</b>。
        /// 35ms のずれは<b>波形に焼いてある</b>ので、実行時に作らない。
        ///
        /// ⚠ これが無ければ<b>混ぜた 1 本へ落ちる</b>（＝ 0134 までの挙動・大きさは一定）。
        /// </summary>
        public const string WarnResourceName = "Sound/sfx_switch_warn";

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
        private AudioClip? _warnClip;
        private AudioSource? _warnSource;
        private readonly AlertEscalationLogic _esc = new AlertEscalationLogic();
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

        /// <summary>
        /// 警告だけの音源を掴めているか（0145）。
        /// <b>false なら混ぜた 1 本へ落ちる</b> ＝ 大きさは一定のまま（0134 までの挙動）。
        /// ⚠ 画にも録画にも出ないので、テレメトリの <c>swAlert</c> の 3 つ目がここを出す。
        /// </summary>
        public bool HasWarnClip => _warnClip != null;

        /// <summary>
        /// 直前の警告に掛けた倍率（テレメトリ用・0145）。まだ鳴っていなければ 0。
        /// ⚠⚠ <b>回を重ねて上がっているかは、ここでしか分からない。</b>
        /// </summary>
        public float AlertGain => _esc.LastGain;

        /// <summary>
        /// ラン開始で警告の育ちを落とす（0145）。
        /// ⚠⚠ <b>落とさないと 2 人目以降は 1 発目から最大で鳴る。</b>
        /// 呼ぶのは <c>ShowRunDirector.BeginRun</c>（乱れの育ちと同じ場所・同じ理由）。
        /// </summary>
        public void ResetRun() => _esc.ResetRun();

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
            // ⚠ 警告は**土台と同じフレームで別々に**鳴らすので声を 2 本目に持つ（0145）。
            //   1 本を使い回すと、警告を鳴らした時点で土台が `Stop()` される。
            //   ⚠ シーンには焼かない（AddComponent で足りるものを焼くと `menu scene` の
            //   工程が 1 つ増え、焼き忘れという新しい壊れ方を作る）。
            if (_warnSource == null) _warnSource = gameObject.AddComponent<AudioSource>();
            _warnSource.playOnAwake = false;
            _warnSource.loop = false;
            _warnSource.pitch = 1f;   // ⚠ 警告は音程を振らない（尺が変わるブザーなので）
            SpatialAudio.Configure(_warnSource);
            ResolveClips();
        }

        private void ResolveClips()
        {
            // ⚠ 警告つきは素の切替音とは独立に解決する（片方が無くてももう片方は鳴る）。
            _warnClip = Resources.Load<AudioClip>(WarnResourceName);
            if (_warnClip == null)
            {
                Debug.LogWarning($"[Sound] 警告だけの音源がありません（Resources/{WarnResourceName}）。"
                                 + "混ぜた 1 本で鳴らすので、警告は回を重ねても大きくなりません"
                                 + "（`canon/LEDGER.md` 0145）。"
                                 + "`py -3.11 tools/ingest-sounds.py --only sfx_switch_1` の後に "
                                 + "`.\\tools\\unity.ps1 menu sound-import` を走らせること。");
            }
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
            if (source == null) return;

            // ⭐ **警告だけを別の声で重ねる**（0145）。土台は素の変種そのままなので、
            //    警告の大きさを回ごとに動かしても**切替音は 1 ビットも変わらない**。
            if (_warnClip != null && _warnSource != null && _clips.Length > 0)
            {
                // ⚠⚠ **予約の時刻は 1 度だけ取って両方へ渡す。** それぞれで
                //    `AudioSettings.dspTime` を読むと、あいだに経った分だけずれる
                //    （35ms の関係は波形に焼いてあるので、実行時に足すものは何も無い）。
                double at = AudioSettings.dspTime + SfxPlayer.ScheduleLeadSec;
                PlayClip(_clips[Pick(_clips.Length)], at);

                _warnSource.clip = _warnClip;
                _warnSource.pitch = 1f;
                // ⚠ **警告は音量を散らさない。** 土台の ±1.5dB をここにも掛けると、
                //   1 段 1.2dB の育ちが乱数に埋もれて「重ねるにつれて大きくなる」が消える。
                _warnSource.volume = Mathf.Clamp01(gain * _esc.Next());
                _warnSource.Stop();
                _warnSource.PlayScheduled(at);
                AlertCount++;
                return;
            }

            // 落ちる先: 混ぜた 1 本（0134 までの挙動・大きさは一定）。
            if (_alertClips.Length == 0) { Play(); return; }
            PlayClip(_alertClips[Pick(_alertClips.Length)]);
            _esc.Next();
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

        /// <param name="at">
        /// 予約する DSP 時刻。<b>0 以下なら自分で取る。</b>
        /// ⚠ 警告と重ねるときは<b>呼ぶ側が 1 度だけ取って両方へ渡す</b>（0145）。
        /// </param>
        private void PlayClip(AudioClip? clip, double at = 0.0)
        {
            if (source == null || clip == null) return;

            source.clip = clip;
            source.pitch = 1f + Random.Range(-pitchSpread, pitchSpread);
            source.volume = Mathf.Clamp01(gain * SoundFade.DbToLin(Random.Range(-1.5f, 1.5f)));
            source.Stop();
            // ⚠ 予約の先読みは音声バッファ 1 個ぶんより長く取る（短いと「もう過ぎた時刻」を
            //    指す回ができ、その回だけ遅れる ＝ ときどきずれる、という最も追いにくい壊れ方）。
            source.PlayScheduled(at > 0.0 ? at : AudioSettings.dspTime + SfxPlayer.ScheduleLeadSec);
            PlayedCount++;
        }
    }
}
