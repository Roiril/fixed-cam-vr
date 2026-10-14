#nullable enable
using FixedCamVr.Streaming;
using UnityEngine;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// 終了結果を表示している間だけ流す曲。
    /// フェードインは<b>聴感が直線</b>になる形（<see cref="SoundFade.Curve.Perceptual"/>・2026-10-14）。
    /// 以前は振幅を一定速度で動かしていて（<c>MoveTowards</c>）、聴感は入りの頭で急に立ち上がり、
    /// 後半はほとんど変わらなく聞こえた。フェードアウトは無い（表示が終わったら即停止）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OutroResultMusic : MonoBehaviour
    {
        public const string ResourceName = "Sound/bgm_nocturnal_waters";

        private const float FadeInSeconds = 1.5f;
        // 提供された原曲は -15.07 LUFS。-8.93 dB で -24.0 LUFS に揃える。
        private const float MusicGain = 0.3576f;

        private AudioSource? _source;
        private bool _presented;
        private float _fadeSeconds;   // 表示開始からの経過（**速度ではなく進み**を持つ。形に通して音量にする）

        public bool HasClip => _source != null && _source.clip != null;
        public bool IsPlaying => _source != null && _source.isPlaying;
        public float Volume => _source != null ? _source.volume : 0f;
        public float PlaybackSeconds => IsPlaying ? _source!.time : 0f;

        private void Awake()
        {
            var clip = Resources.Load<AudioClip>(ResourceName);
            if (clip == null)
                Debug.LogWarning($"[OutroResultMusic] 曲が見つかりません: {ResourceName}");

            _source = gameObject.AddComponent<AudioSource>();
            _source.clip = clip;
            _source.loop = true;
            _source.playOnAwake = false;
            _source.spatialBlend = 0f;
            _source.spatialize = false;
            _source.volume = 0f;
        }

        public void SetPresented(bool wanted, float deltaTime)
        {
            if (_source == null) return;
            if (!wanted || !isActiveAndEnabled)
            {
                Stop();
                return;
            }

            if (_source.clip == null)
            {
                Stop();
                return;
            }
            if (!_presented)
            {
                _source.time = 0f;
                _source.volume = 0f;
                _fadeSeconds = 0f;
                _presented = true;
                _source.Play();
            }

            if (!float.IsNaN(deltaTime) && !float.IsInfinity(deltaTime))
            {
                _fadeSeconds = Mathf.Min(FadeInSeconds, _fadeSeconds + Mathf.Max(0f, deltaTime));
                _source.volume = MusicGain *
                    SoundFade.Gain(_fadeSeconds / FadeInSeconds, SoundFade.Curve.Perceptual);
            }
        }

        private void OnDisable() => Stop();

        private void Stop()
        {
            _presented = false;
            _fadeSeconds = 0f;
            if (_source == null) return;
            _source.Stop();
            _source.volume = 0f;
        }
    }
}
