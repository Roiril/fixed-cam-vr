#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>連絡の面が上から塗り替わる頭の一撃</b>（<c>canon/LEDGER.md</c> 0230・憑依の出し方）。
    /// 既存の乱れの音（<c>sfx_glitch_1..3</c>・<c>rules/sound-design.md</c>）を 1 発。新しい音源は増やさない。
    ///
    /// 叩くのは <c>CommsPanel</c> で、**前線が降り始めたのと同じフレーム**から呼ぶ
    /// （<see cref="TypeAudioCue"/> と同じ構え — 毎フレーム外から状態を見る層では縁を取りこぼす）。
    ///
    /// ⚠ 3 種は<b>順に</b>選ぶ（乱数を使わない — 同じ走行は同じ音）。音程と音量の散らしは
    /// <see cref="SfxPlayer.Play"/> が持つ。
    /// ⚠ 音源が無ければ黙って何もしない（体験は止めない）。掴めたかは <see cref="HasClips"/> が観測に出る。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CurseSweepAudioCue : MonoBehaviour
    {
        /// <summary><c>Resources</c> の中の置き場（<c>_1..N</c> が付く）。乱れの一撃と同じ音源。</summary>
        public const string ResourcePrefix = "Sound/sfx_glitch_";

        /// <summary>変種の数（<c>tools/ingest-sounds.py</c> が焼く本数と対）。</summary>
        public const int VariantCount = 3;

        /// <summary>音量。乱れの一撃（<c>ShowSoundDirector</c> の Glitch）の頭打ちより少し下に置く。</summary>
        public const float Gain = 0.8f;

        [Tooltip("鳴らす発声器。null なら同 GameObject から取得（無ければ足す）。")]
        [SerializeField] private SfxPlayer? player;

        private readonly System.Collections.Generic.List<AudioClip> _clips =
            new System.Collections.Generic.List<AudioClip>(VariantCount);
        private int _next;

        /// <summary>鳴らした累計（テレメトリ用。「鳴らした」であって「聞こえた」ではない）。</summary>
        public int PlayedCount { get; private set; }

        /// <summary>音源を掴めているか。<b>false なら塗り替わりは無音のまま。</b></summary>
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
                Debug.LogWarning($"[Comms] 乱れの音が {_clips.Count}/{VariantCount} しか見つかりません"
                                 + "（`py -3.11 tools/ingest-sounds.py --only sfx_glitch` を走らせたか）");
            }
        }

        /// <summary>1 発、<paramref name="at"/>（＝ 面そのもの）から鳴らす。位置は呼ぶ側が渡す。</summary>
        public void Play(Vector3 at)
        {
            if (_clips.Count == 0 || player == null) return;
            AudioClip clip = _clips[_next % _clips.Count];
            _next = (_next + 1) % _clips.Count;
            player.Play(clip, Gain, 0.03f, 1.2f, at);
            PlayedCount++;
        }

        /// <summary>ラン開始・面を畳むときに鳴っているものを黙らせる。</summary>
        public void StopAll()
        {
            player?.StopAll();
            _next = 0;
        }
    }
}
