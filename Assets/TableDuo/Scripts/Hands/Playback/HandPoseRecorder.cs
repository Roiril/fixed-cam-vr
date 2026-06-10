#nullable enable
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace TableDuoVr.Hands.Playback
{
    /// <summary>
    /// IHandPoseSource の出力を一定レートで録画し、persistentDataPath にバイナリ保存する診断ツール。
    /// 実機 Quest で録って adb pull → FakeHandDriver で PC 再生（L0 検証）が主用途。
    /// バッファは StartRecording 時に全フレーム分を事前確保する（録画中のアロケーションゼロ）。
    /// </summary>
    public sealed class HandPoseRecorder : MonoBehaviour
    {
        [SerializeField] private float sampleRate = 30f;
        [SerializeField] private float maxSeconds = 60f;
        [SerializeField] private bool autoStartOnPlay;
        [SerializeField] private string fileName = "tdv_handrec.bin";

        private List<AvatarPose>? _buffer;
        private int _count;
        private float _next;
        private bool _recording;

        public bool IsRecording => _recording;
        public string FilePath => Path.Combine(Application.persistentDataPath, fileName);

        private void Start()
        {
            if (autoStartOnPlay) StartRecording();
        }

        [ContextMenu("Start Recording")]
        public void StartRecording()
        {
            int capacity = Mathf.CeilToInt(sampleRate * maxSeconds);
            _buffer = new List<AvatarPose>(capacity);
            for (int i = 0; i < capacity; i++) _buffer.Add(new AvatarPose());
            _count = 0;
            _next = Time.time;
            _recording = true;
            Debug.Log($"[TableDuo] 録画開始 rate={sampleRate} max={maxSeconds}s");
        }

        [ContextMenu("Stop And Save")]
        public void StopAndSave()
        {
            if (!_recording || _buffer == null) return;
            _recording = false;
            var frames = _buffer.GetRange(0, _count);
            PoseRecordingFile.Save(FilePath, frames, 1f / sampleRate,
                HandSkeletonLayout.CapturedL, HandSkeletonLayout.CapturedR);
            Debug.Log($"[TableDuo] 録画保存 {_count}f → {FilePath}（adb pull で取得可）");
        }

        private void Update()
        {
            if (!_recording || _buffer == null) return;
            if (Time.time < _next) return;
            _next += 1f / sampleRate;

            var src = HandPoseSourceRegistry.Best;
            if (src == null) return;

            if (_count >= _buffer.Count)
            {
                StopAndSave();
                return;
            }
            _buffer[_count].CopyFrom(src.Current);
            _count++;
        }

        private void OnDestroy()
        {
            if (_recording) StopAndSave();
        }
    }
}
