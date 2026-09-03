#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 一撃の音の発声器。**声を複数持ち、DSP 時刻で予約し、毎回わずかに変える。**
    ///
    /// 3 つを引き受ける:
    ///
    /// 1. <b>声の数</b> — 1 本の <c>AudioSource</c> に <c>PlayOneShot</c> を重ねると
    ///    音程・フィルタを 1 発ごとに変えられない。声を分ければ変えられる
    /// 2. <b>時刻の正確さ</b> — <c>Play()</c> は「次の音声バッファの頭」で鳴る。
    ///    この機は 1024 標本 / 48kHz ＝ <b>21.3ms の粒</b>で、しかもフレームのどこで呼んだかで
    ///    ずれ幅が変わる。切替音は暗転の下り 70ms の中に置く約束なので、
    ///    <c>PlayScheduled</c> で <b>DSP 時刻</b>を指定して粒を消す
    /// 3. <b>同じにならないこと</b> — 音程と音量をわずかに散らす。1 回の体験で 9 回以上鳴る音が
    ///    毎回まったく同じだと、装置ではなく「効果音」に聞こえる
    ///
    /// ⚠ <b>予約の先読みは音声バッファ 1 個ぶんより長く取る。</b> 短いと「もう過ぎた時刻」を
    /// 指すことがあり、その回だけ遅れて鳴る（＝ ときどきずれる、という最も追いにくい壊れ方）。
    ///
    /// ⚠⚠ <b>1 発ごとに 2D / 3D が変わる。</b> 声は使い回すので、<see cref="Play"/> は毎回
    /// <c>spatialBlend</c> を書き直す（<paramref name="at"/> を渡さなければ 2D へ戻す）。
    /// 書き直さないと、直前の 3D の設定が次の 2D の音に残る。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SfxPlayer : MonoBehaviour
    {
        [Tooltip("同時に鳴らせる本数。足りないと古い方を奪う。")]
        [SerializeField] private int voices = 6;

        [Tooltip("この発声器全体の音量。")]
        [Range(0f, 1f)]
        [SerializeField] private float masterGain = 1f;

        /// <summary>DSP 予約の先読み（秒）。音声バッファ 1 個（21.3ms）より確実に長く取る。</summary>
        public const float ScheduleLeadSec = 0.035f;

        private AudioSource[] _pool = System.Array.Empty<AudioSource>();
        private int _next;

        /// <summary>これまでに鳴らした本数（テレメトリ用。**「鳴らした」であって「聞こえた」ではない**）。</summary>
        public int PlayedCount { get; private set; }

        /// <summary>直前に鳴らしたものの実効音量（テレメトリ用）。</summary>
        public float LastGain { get; private set; }

        /// <summary>3D で鳴らした累計（テレメトリ用）。<b>音は録画に映らないのでここだけが証拠。</b></summary>
        public int SpatialCount { get; private set; }

        private void Awake()
        {
            int n = Mathf.Clamp(voices, 1, 16);
            _pool = new AudioSource[n];
            for (int i = 0; i < n; i++)
            {
                var go = new GameObject($"[Sfx{i}]");
                go.transform.SetParent(transform, worldPositionStays: false);
                var s = go.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.loop = false;
                s.spatialBlend = 0f;      // 一撃は 2D（体験者の向きで大きさが変わってはいけない）
                s.volume = 0f;
                _pool[i] = s;
            }
        }

        /// <summary>
        /// 1 回鳴らす。<paramref name="pitchSpread"/> と <paramref name="gainSpreadDb"/> は
        /// 毎回ふる幅（0 なら振らない）。
        ///
        /// ⚠ <b>鳴らせたかを返す。</b> クリップが無いときと、<c>Awake</c> が走っていなくて
        /// 声が 1 本も無いときは <c>false</c>。**呼んだ側が沈黙に気づけるようにするため**で、
        /// 音は録画にも画にも出ないので、返り値を捨てると失敗が永久に見えない。
        ///
        /// <paramref name="at"/> を渡すと<b>その場所から</b>鳴る（渡さなければ 2D）。
        /// 置いた先は world 固定で、鳴っているあいだ追従しない
        /// （<see cref="SpatialAudio.Behind"/> の但し書き）。
        /// </summary>
        public bool Play(AudioClip? clip, float gain = 1f, float pitchSpread = 0.03f,
                         float gainSpreadDb = 1.2f, Vector3? at = null)
        {
            if (clip == null || _pool.Length == 0) return false;
            var src = _pool[_next];
            _next = (_next + 1) % _pool.Length;

            float g = Mathf.Clamp01(gain * masterGain
                                    * SoundFade.DbToLin(Random.Range(-gainSpreadDb, gainSpreadDb)));
            src.Stop();
            src.clip = clip;
            src.pitch = 1f + Random.Range(-pitchSpread, pitchSpread);
            src.volume = g;
            if (at.HasValue)
            {
                src.transform.position = at.Value;
                SpatialAudio.Configure(src);
                SpatialCount++;
            }
            else
            {
                SpatialAudio.MakeFlat(src);
            }
            src.PlayScheduled(AudioSettings.dspTime + ScheduleLeadSec);
            PlayedCount++;
            LastGain = g;
            return true;
        }

        /// <summary>ラン開始・中止で鳴っているものを黙らせる（前の体験者の音を持ち越さない）。</summary>
        public void StopAll()
        {
            foreach (var s in _pool)
            {
                if (s != null) s.Stop();
            }
        }
    }
}
