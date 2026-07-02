#nullable enable
using System;
using System.IO;
using UnityEngine;

namespace TableDuoVr.Hands.Playback
{
    /// <summary>
    /// 調査セッション用のローカル lossless 手 pose 録画（study-design.md §4 の「完全忠実度バックアップ」）。
    /// SessionLogger（ホスト集約）はネット往復遅延 + half 量子化 + パケット欠落を含むため、
    /// 各端末が自分の生 pose を全 bone・float 精度で persistentDataPath に逐次書き込む。
    ///
    /// HandPoseRecorder（診断用・60s 固定バッファ）と違い、フレームをファイルへ直接ストリーム書き込み
    /// するので長時間セッション（30 分超）でもメモリを食わない。形式は PoseRecordingFile と同じ TDV2
    /// （frameCount はヘッダ位置を覚えておき、保存確定時にシークして書き戻す）。FakeHandDriver でそのまま再生可。
    ///
    /// 稼働条件: 調査セッション起動（StudyConfig.LaunchedWithStudyFlags）時のみ自動録画。
    /// トラッキング初検知で開始、pause（HMD 外し / ホーム遷移）ごとに frameCount を確定して flush する
    /// （force-stop でも直前 pause までのフレームが有効ファイルとして残る）。
    /// </summary>
    public sealed class StreamingPoseRecorder : MonoBehaviour
    {
        [SerializeField] private float sampleRate = 60f;
        [Tooltip("OFF にすると調査フラグ無し起動（Editor 手動 Play 等）でも録画する（検証用）")]
        [SerializeField] private bool onlyDuringStudy = true;

        private FileStream? _stream;
        private BinaryWriter? _writer;
        private long _frameCountPos = -1;
        private int _count;
        private float _next;

        public bool IsRecording => _writer != null;
        public string? FilePath { get; private set; }

        private void Update()
        {
            if (onlyDuringStudy && !StudyConfig.LaunchedWithStudyFlags) return;
            var src = HandPoseSourceRegistry.Best;
            if (src == null) return;
            if (Time.time < _next) return;
            // 処理落ちで遅れても現在時刻へ追従（負債を溜めてバースト書き込みしない）
            _next = Mathf.Max(_next + 1f / sampleRate, Time.time);

            // トラッキング初検知まで開始しない（装着前デッドタイムを入れない）。
            // 開始時点の Captured layout をヘッダへ同梱（この端末の手寸法で FK 再生できる）
            if (_writer == null)
            {
                if (!src.Current.TrackedL && !src.Current.TrackedR) return;
                if (!TryOpen()) { enabled = false; return; }
            }

            if (_count == int.MaxValue) return;
            PoseRecordingFile.WritePose(_writer!, src.Current);
            _count++;
        }

        private bool TryOpen()
        {
            try
            {
                string tag = "";
                if (!string.IsNullOrEmpty(StudyConfig.PairId)) tag += $"_pair{SafeTag(StudyConfig.PairId)}";
                if (!string.IsNullOrEmpty(StudyConfig.ParticipantId)) tag += $"_pid{SafeTag(StudyConfig.ParticipantId)}";
                string role = StudyConfig.ForcedRole?.ToString() ?? "auto";
                FilePath = Path.Combine(Application.persistentDataPath,
                    $"tdv_local_{DateTime.Now:yyyyMMdd_HHmmss}{tag}_{role}.bin");

                // FileShare.Read: 録画中もファシリテータが adb pull できる
                _stream = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
                _writer = new BinaryWriter(_stream);

                // PoseRecordingFile.Save と同一ヘッダ。frameCount は位置を覚えて 0 を仮書きし、
                // Finalize 時にシークして実数を書き戻す
                _writer.Write(0x54445632); // "TDV2"
                _writer.Write(AvatarPose.BonesPerHand);
                _writer.Write(true);
                PoseRecordingFile.WriteLayout(_writer, HandSkeletonLayout.CapturedL);
                PoseRecordingFile.WriteLayout(_writer, HandSkeletonLayout.CapturedR);
                _frameCountPos = _stream.Position;
                _writer.Write(0);
                _writer.Write(1f / sampleRate);
                _count = 0;
                Debug.Log($"[TableDuo] ローカル lossless 録画開始 rate={sampleRate} → {FilePath}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TableDuo] ローカル録画を開始できない（ホスト集約ログのみで継続）: {e.Message}");
                CloseQuietly();
                return false;
            }
        }

        /// <summary>frameCount を書き戻して flush（ファイルを有効な TDV2 として確定）。録画は継続できる。</summary>
        private void Commit()
        {
            if (_writer == null || _stream == null || _frameCountPos < 0) return;
            try
            {
                long end = _stream.Position;
                _stream.Seek(_frameCountPos, SeekOrigin.Begin);
                _writer.Write(_count);
                _stream.Seek(end, SeekOrigin.Begin);
                _writer.Flush();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TableDuo] ローカル録画の確定に失敗: {e.Message}");
            }
        }

        private void CloseQuietly()
        {
            _writer?.Dispose();
            _writer = null;
            _stream = null;
            _frameCountPos = -1;
        }

        private void OnApplicationPause(bool paused)
        {
            // Quest はホーム遷移 / HMD 外しで pause。force-stop では OnDestroy が来ないため、
            // pause ごとに frameCount を確定しておく（それ以降のフレームは次の確定で有効化）
            if (paused) Commit();
        }

        private void OnDestroy()
        {
            if (_writer == null) return;
            Commit();
            Debug.Log($"[TableDuo] ローカル lossless 録画保存 {_count}f → {FilePath}（adb pull で取得可）");
            CloseQuietly();
        }

        private static string SafeTag(string s)
        {
            var chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-')) chars[i] = '_';
            }
            return new string(chars);
        }
    }
}
