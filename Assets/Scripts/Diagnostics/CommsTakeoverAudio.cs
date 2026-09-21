#nullable enable
using System.Globalization;
using FixedCamVr.Streaming;
using UnityEngine;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// 乗っ取りの表示時計で、提供された二つの動画から切った音だけを鳴らす。
    /// 警告の前半は0秒。遮断と侵食は共通の実行時 fillStartSec から始まる。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CommsTakeoverAudio : MonoBehaviour
    {
        public const string AlertResource = "Sound/sfx_comms_alert";
        public const string BlockResource = "Sound/sfx_comms_block";
        public const string SweepResource = "Sound/sfx_comms_sweep";

        private readonly AudioSource?[] _sources = new AudioSource?[3];
        private AudioClip?[]? _clips;
        private Transform? _screen;
        private Transform? _agent;
        private bool _active;
        private bool _alertStarted, _blockStarted, _sweepStarted;

        public bool HasSweepClip => _clips != null && _clips[2] != null;
        public int ScheduledCount { get; private set; }

        public void Begin(Transform screen, Transform agent)
        {
            StopAll();
            _screen = screen;
            _agent = agent;
            _clips ??= new[]
            {
                Resources.Load<AudioClip>(AlertResource),
                Resources.Load<AudioClip>(BlockResource),
                Resources.Load<AudioClip>(SweepResource),
            };
            if (_clips[0] == null || _clips[1] == null || _clips[2] == null)
                Debug.LogWarning("[Comms] 乗っ取りの提供音源が不足。Resources/Sound/sfx_comms_* を確認してください");
            for (int i = 0; i < _sources.Length; i++)
            {
                if (_sources[i] != null) continue;
                var go = new GameObject($"[Comms takeover sound {i}]");
                go.transform.SetParent(transform, false);
                var source = go.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = false;
                source.volume = 1f; // 音量は切り出した WAV が持つ。
                SpatialAudio.Configure(source);
                _sources[i] = source;
            }
            _active = true;
        }

        public void Tick(float elapsed, float fillStartSec)
        {
            if (!_active) return;
            _sources[0]!.transform.position = _screen!.position;
            _sources[1]!.transform.position = _screen.position;
            _sources[2]!.transform.position = _agent!.position;
            if (!_alertStarted)
            {
                _alertStarted = true;
                Schedule(0, Mathf.Max(0f, elapsed), "alert", elapsed);
            }
            if (elapsed + .0001f < fillStartSec) return;
            float offset = Mathf.Max(0f, elapsed - fillStartSec);
            if (!_blockStarted)
            {
                _blockStarted = true;
                Schedule(1, offset, "block", elapsed);
            }
            if (!_sweepStarted)
            {
                _sweepStarted = true;
                Schedule(2, offset, "sweep", elapsed);
            }
        }

        private void Schedule(int index, float offset, string part, float elapsed)
        {
            AudioClip? clip = _clips?[index];
            AudioSource? source = _sources[index];
            if (clip == null || source == null || offset >= clip.length) return;
            source.clip = clip;
            source.timeSamples = Mathf.Clamp(Mathf.RoundToInt(offset * clip.frequency), 0, clip.samples - 1);
            source.PlayScheduled(AudioSettings.dspTime + SfxPlayer.ScheduleLeadSec);
            ScheduledCount++;
            Debug.Log("[XP] ev=commsSound part=" + part + " sec="
                + elapsed.ToString("0.000", CultureInfo.InvariantCulture));
        }

        public void StopAll()
        {
            foreach (AudioSource? source in _sources) source?.Stop();
            _alertStarted = _blockStarted = _sweepStarted = false;
            _active = false;
            _screen = _agent = null;
        }

        private void OnDisable() => StopAll();
    }
}
