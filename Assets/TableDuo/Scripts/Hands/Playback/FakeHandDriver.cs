#nullable enable
using System.IO;
using UnityEngine;

namespace TableDuoVr.Hands.Playback
{
    /// <summary>
    /// 実機・OVR 無しで AvatarPose を供給するフェイクソース（L0 検証の主役）。
    /// - Synthetic: 手続き的な頭の揺れ + 両手の波モーション（録画ファイル不要）
    /// - File: HandPoseRecorder の録画をループ再生（layout 同梱なら指まで再現）
    /// 有効化すると Priority=10 で実トラッキングより優先される。
    /// </summary>
    public sealed class FakeHandDriver : MonoBehaviour, IHandPoseSource
    {
        public enum Mode
        {
            Synthetic,
            File,
        }

        [SerializeField] private Mode mode = Mode.Synthetic;
        [Tooltip("File モード時のパス。空なら persistentDataPath/tdv_handrec.bin")]
        [SerializeField] private string filePath = "";

        private readonly AvatarPose _pose = new();
        private PoseRecordingFile.Data? _data;
        private float _playTime;

        public AvatarPose Current => _pose;
        public bool IsValid => mode == Mode.Synthetic || (_data != null && _data.Frames.Count > 0);
        public int Priority => 10;

        private void OnEnable()
        {
            if (mode == Mode.File && _data == null)
            {
                string path = ResolveFilePath();
                _data = PoseRecordingFile.Load(path);
                if (_data == null)
                {
                    Debug.LogWarning($"[TableDuo] 録画ファイルが読めません: {path} → Synthetic にフォールバック");
                    mode = Mode.Synthetic;
                }
                else
                {
                    if (_data.LayoutL != null) HandSkeletonLayout.CapturedL ??= _data.LayoutL;
                    if (_data.LayoutR != null) HandSkeletonLayout.CapturedR ??= _data.LayoutR;
                    Debug.Log($"[TableDuo] 録画ロード {_data.Frames.Count}f layout={(_data.LayoutL != null)}");
                }
            }
            HandPoseSourceRegistry.Register(this);
        }

        private void OnDisable() => HandPoseSourceRegistry.Unregister(this);

        private string ResolveFilePath()
        {
            if (!string.IsNullOrEmpty(filePath)) return filePath;
            string persistent = Path.Combine(Application.persistentDataPath, "tdv_handrec.bin");
            if (File.Exists(persistent)) return persistent;
#if UNITY_EDITOR
            // Editor L0 では実機から回収した録画（TestData/）を既定で使う
            var dir = Path.GetFullPath(Path.Combine(Application.dataPath, "../TestData"));
            if (Directory.Exists(dir))
            {
                var files = Directory.GetFiles(dir, "tdv_handrec*.bin");
                if (files.Length > 0)
                {
                    System.Array.Sort(files);
                    return files[^1]; // 最新（名前順末尾）
                }
            }
#endif
            return persistent;
        }

        private void Update()
        {
            if (mode == Mode.File && _data != null && _data.Frames.Count > 0)
            {
                _playTime += Time.deltaTime;
                int idx = (int)(_playTime / _data.Interval) % _data.Frames.Count;
                _pose.CopyFrom(_data.Frames[idx]);
                return;
            }
            UpdateSynthetic();
        }

        private void UpdateSynthetic()
        {
            float t = Time.time;
            _pose.HeadPos = new Vector3(Mathf.Sin(t * 0.7f) * 0.04f, 1.1f + Mathf.Sin(t * 1.3f) * 0.02f, 0f);
            _pose.HeadRot = Quaternion.Euler(Mathf.Sin(t * 0.9f) * 6f, Mathf.Sin(t * 0.5f) * 12f, 0f);

            _pose.TrackedL = true;
            _pose.TrackedR = true;
            // 席はテーブル中心から 0.85m 後ろ。テーブル上の小物（local z≈0.85 相当）まで
            // 届くように手を前方へ伸ばす（人間の腕より長いが L0 検証用なので可）
            _pose.WristPosL = new Vector3(-0.28f + Mathf.Sin(t * 1.7f) * 0.06f, 0.80f + Mathf.Sin(t * 2.1f) * 0.05f, 0.6f + Mathf.Sin(t * 0.9f) * 0.15f);
            _pose.WristRotL = Quaternion.Euler(Mathf.Sin(t * 2f) * 30f, 0f, 90f);
            _pose.WristPosR = new Vector3(0.28f + Mathf.Sin(t * 1.4f + 1f) * 0.04f, 0.80f + Mathf.Cos(t * 1.9f) * 0.05f, 0.85f + Mathf.Sin(t * 0.8f) * 0.12f);
            _pose.WristRotR = Quaternion.Euler(Mathf.Sin(t * 2.2f + 0.5f) * 30f, 0f, -90f);
            // bone は identity のまま（synthetic では指は固定。指の検証は File モードで）

            // 掴み検証用: 右手が 4 秒周期で 1.5 秒ピンチする
            _pose.PinchR = Mathf.Repeat(t, 4f) < 1.5f;
            _pose.PinchL = false;
        }
    }
}
