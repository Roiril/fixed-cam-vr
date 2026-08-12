#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// カメラ切替の音（監視装置のリレー）。
    /// <see cref="CameraSwitchDirector"/> が dip-to-black の開始と同時に <see cref="Play"/> を呼ぶ
    /// （黒 70ms が視覚差替に先行するので J カット相当が成立する）。
    ///
    /// **1 回の体験で 9 回以上鳴る、この作品でいちばん多い音。** だから 3 つ守る:
    ///
    /// 1. <b>変種を回す</b> — 同じ波形が並ぶと装置ではなく「効果音」に聞こえる
    /// 2. <b>音程と音量をわずかに散らす</b> — 実物の接点は毎回わずかに違う音がする
    /// 3. <b>DSP 時刻で予約する</b> — <c>PlayOneShot</c> は「次の音声バッファの頭」で鳴り、
    ///    この機の粒は <b>21.3ms</b>（1024 標本 / 48kHz）。暗転の下りが 70ms なので、
    ///    粒のままだと音が黒の中に入る回と外れる回ができる ＝ <b>J カットが毎回別物になる</b>
    ///
    /// ⚠ クリップ未設定なら無音スキップ（仕組みだけ残す）。既定の音は
    /// <c>Resources/Sound/sfx_switch_1..3</c> から拾う（`tools/make-sounds.py` が焼く）。
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class SwitchAudioCue : MonoBehaviour
    {
        /// <summary><c>Resources</c> の既定音（Inspector が空のときに拾う）。</summary>
        public const string DefaultResourcePrefix = "Sound/sfx_switch_";
        public const int DefaultVariantCount = 3;

        [Tooltip("再生に使う AudioSource。null なら同 GameObject から取得。")]
        [SerializeField] private AudioSource? source;

        [Tooltip("切替時に鳴らすクリップ。**空なら Resources/Sound/sfx_switch_1..3 を使う。**")]
        [SerializeField] private AudioClip[] switchClips = new AudioClip[0];

        [Tooltip("切替音の音量。")]
        [Range(0f, 1f)]
        [SerializeField] private float gain = 0.85f;

        [Tooltip("1 回ごとに音程を振る幅（±）。0 で振らない。")]
        [Range(0f, 0.2f)]
        [SerializeField] private float pitchSpread = 0.035f;

        private AudioClip?[] _clips = new AudioClip?[0];
        private int _next;

        /// <summary>鳴らした累計（テレメトリ用）。</summary>
        public int PlayedCount { get; private set; }

        /// <summary>音源を掴めているか。**false なら切替は無音のまま。**</summary>
        public bool HasClips => _clips.Length > 0;

        private void Awake()
        {
            if (source == null) source = GetComponent<AudioSource>();
            if (source != null)
            {
                source.playOnAwake = false;
                source.loop = false;
                source.spatialBlend = 0f; // 2D（配置に依らない）
            }
            ResolveClips();
        }

        private void ResolveClips()
        {
            if (switchClips != null && switchClips.Length > 0)
            {
                var keep = new System.Collections.Generic.List<AudioClip?>(switchClips.Length);
                foreach (var c in switchClips)
                {
                    if (c != null) keep.Add(c);
                }
                if (keep.Count > 0) { _clips = keep.ToArray(); return; }
            }
            var found = new System.Collections.Generic.List<AudioClip?>(DefaultVariantCount);
            for (int i = 1; i <= DefaultVariantCount; i++)
            {
                var c = Resources.Load<AudioClip>($"{DefaultResourcePrefix}{i}");
                if (c != null) found.Add(c);
            }
            _clips = found.ToArray();
            if (_clips.Length == 0)
            {
                Debug.LogWarning($"[Sound] 切替音がありません（Resources/{DefaultResourcePrefix}1..3）。"
                                 + "カメラ切替は無音のままになります。"
                                 + "`py -3.11 tools/make-sounds.py` を走らせたか確認すること。");
            }
        }

        /// <summary>切替音を 1 回鳴らす。クリップ未設定なら何もしない。</summary>
        public void Play()
        {
            if (source == null || _clips.Length == 0) return;
            var clip = _clips[_next];
            _next = (_next + 1) % _clips.Length;
            if (clip == null) return;

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
