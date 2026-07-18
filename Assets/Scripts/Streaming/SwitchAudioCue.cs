#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// カメラ切替時の音マスク（CCTV 風の瞬断音・スイッチノイズ等）を鳴らすフック。
    /// <see cref="CameraSwitchDirector"/> が dip-to-black の開始と同時に <see cref="Play"/> を呼ぶ
    /// （黒 70ms が視覚差替に先行するので J カット相当が成立する）。
    ///
    /// <see cref="switchClip"/> 未設定なら無音スキップ（仕組みだけ用意して音源は現場で差す）。
    /// 2D 再生（spatialBlend=0）で配置に依らず一定音量。
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class SwitchAudioCue : MonoBehaviour
    {
        [Tooltip("再生に使う AudioSource。null なら同 GameObject から取得。")]
        [SerializeField] private AudioSource? source;

        [Tooltip("切替時に鳴らすクリップ。空なら無音（仕組みのみ）。")]
        [SerializeField] private AudioClip? switchClip;

        private void Awake()
        {
            if (source == null) source = GetComponent<AudioSource>();
            if (source != null)
            {
                source.playOnAwake = false;
                source.spatialBlend = 0f; // 2D（配置に依らない）
            }
        }

        /// <summary>切替音を 1 回鳴らす。クリップ未設定なら何もしない。</summary>
        public void Play()
        {
            if (switchClip == null || source == null) return;
            source.PlayOneShot(switchClip);
        }
    }
}
