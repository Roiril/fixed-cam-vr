#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>連絡の面の打鍵音。</b> 1 文字が出るたびに 1 発鳴る（<c>canon/LEDGER.md</c> 0056）。
    /// 叩くのは <c>CommsPanel</c> で、**字を画へ書いているのと同じ場所**から呼ぶ。
    ///
    /// ⚠⚠ <b>なぜ <see cref="SoundCueLogic"/> に入れないか。</b> あちらは毎フレーム状態を外から見て
    /// 縁を検出する層で、**1 秒に 12 回・字の刻みちょうどに鳴らす**用途には合わない
    /// （見に行った時には次の字になっている）。同じ理由でカメラ切替も
    /// <see cref="SwitchAudioCue"/> を <see cref="CameraSwitchDirector"/> が直接叩いている。
    /// <b>絵と音を 1 か所の数えから出す</b>ので、ずれようがない。
    ///
    /// <b>鳴らし方</b>（`rules/sound-design.md` §3 と同じ考え）:
    ///
    /// 1. <b>間隔は規則的</b> — 装置が印字している音なので。不揃いにすると
    ///    「誰かが手で打っている」という別の意味が出る。刻みは絵の側（<see cref="CommsPanelLogic"/>）が持つ
    /// 2. <b>音は毎回変える</b> — 8 種を**直前と同じものを引かずに**選び、音程と音量を散らす。
    ///    1 通で 10〜20 発並ぶので、同じ波形が続くと装置ではなく「効果音」に聞こえる
    /// 3. <b>劇伴は退かせない</b> — 節目の一撃は鳴るたび劇伴を引かせるが、
    ///    12 回/秒でそれをやると劇伴が波打つ。ここは <c>PushSpotDuck</c> を通さない
    ///
    /// ⚠ 音の高さは<b>素材が持っている</b>（<c>tools/ingest-sounds.py</c> の <c>CUTS</c> が
    /// 「12 発/秒で連ねて -28 LUFS」へ揃えて焼く）。ここで倍率を掛けない —
    /// 掛けると 2 箇所で高さが決まることになる。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TypeAudioCue : MonoBehaviour
    {
        /// <summary><c>Resources</c> の中の置き場（<c>_1..N</c> が付く）。</summary>
        public const string ResourcePrefix = "Sound/sfx_type_";

        /// <summary>
        /// 変種の数。<b><c>tools/ingest-sounds.py</c> の <c>CUTS</c> が焼く本数と対</b>。
        /// 向こうを増減したらここも直す（足りないと無い音を掴もうとして黙る）。
        /// </summary>
        public const int VariantCount = 8;

        /// <summary>1 発ごとに振る音程の幅（±）。切替音（0.035）より少し広い。</summary>
        public const float PitchSpread = 0.04f;

        /// <summary>1 発ごとに振る音量の幅（± dB）。**連なるぶん切替音より広く取る。**</summary>
        public const float GainSpreadDb = 2.0f;

        [Tooltip("鳴らす発声器。null なら同 GameObject から取得（無ければ足す）。")]
        [SerializeField] private SfxPlayer? player;

        // ⚠ **掴めたものだけを詰めて持つ。** 穴のある配列のまま番号で引くと、
        //    1 本欠けた日に「その番号だけ無音」という気づけない壊れ方をする。
        private readonly System.Collections.Generic.List<AudioClip> _clips =
            new System.Collections.Generic.List<AudioClip>(VariantCount);
        private int _last = -1;

        /// <summary>鳴らした累計（テレメトリ用。<b>「鳴らした」であって「聞こえた」ではない</b>）。</summary>
        public int PlayedCount { get; private set; }

        /// <summary>音源を掴めているか。<b>false なら打鍵は無音のまま。</b></summary>
        public bool HasClips => _clips.Count > 0;

        private void Awake()
        {
            if (player == null) player = GetComponent<SfxPlayer>();
            if (player == null) player = gameObject.AddComponent<SfxPlayer>();
            for (int i = 0; i < VariantCount; i++)
            {
                var clip = Resources.Load<AudioClip>($"{ResourcePrefix}{i + 1}");
                if (clip != null) _clips.Add(clip);
            }
            if (_clips.Count < VariantCount)
            {
                Debug.LogWarning($"[Comms] 打鍵音が {_clips.Count}/{VariantCount} しか見つかりません"
                                 + "（`py -3.11 tools/ingest-sounds.py --only sfx_type` を走らせたか）");
            }
        }

        /// <summary>1 文字ぶん鳴らす。<b>音源が無ければ黙って何もしない</b>（体験は止めない）。</summary>
        public void Play()
        {
            if (_clips.Count == 0 || player == null) return;
            player.Play(Pick(), 1f, PitchSpread, GainSpreadDb);
            PlayedCount++;
        }

        /// <summary>ラン開始・面を畳むときに鳴っているものを黙らせる。</summary>
        public void StopAll()
        {
            player?.StopAll();
            _last = -1;
        }

        /// <summary>
        /// 変種を 1 つ選ぶ。<b>直前と同じものは引かない</b>（同じ波形が 2 つ並ぶと、
        /// 12 発/秒の中でそこだけ「繰り返し」として聞こえる）。
        /// </summary>
        private AudioClip Pick()
        {
            if (_clips.Count == 1) return _clips[0];
            int v = Random.Range(0, _clips.Count - 1);
            if (v >= _last) v++;                 // 直前を飛ばす（一様なまま 1 つ除ける）
            _last = v;
            return _clips[v];
        }
    }
}
