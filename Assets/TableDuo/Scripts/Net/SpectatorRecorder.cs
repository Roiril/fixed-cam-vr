#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using TableDuoVr.Hands;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 観戦（PC ホスト）中に「俯瞰」と「人役 FPV」の 2 視点を同時に MJPEG-AVI へ録画する。
    /// FacilitatorPanel（運営パネル）の「映像記録」ボタンから開始/停止する。表示用の 1 台の観戦カメラ
    /// （<see cref="SpectatorController"/>）とは独立に、オフスクリーンのカメラ 2 本を生成して RenderTexture へ
    /// 描き、AsyncGPUReadback → JPEG エンコード → 別スレッドでファイル書き込みする。
    ///
    /// 頭ジオメトリの潰し（FPV でカメラに頭が埋まらないようにする処理）はグローバル状態なので、
    /// 録画中だけ <see cref="RenderPipelineManager"/> の begin/end で per-camera に切り替える:
    /// - FPV 録画カメラの描画直前は人役の頭を潰し、俯瞰録画カメラの描画直前は戻す
    /// - 自分の録画カメラの描画後は「表示側が潰している状態」へ必ず復元する（表示カメラの見た目を壊さない）
    ///
    /// 出力: persistentDataPath/tdv_recordings/tdv_rec_&lt;yyyyMMdd_HHmmss&gt;/{overhead.avi, person_fpv.avi}。
    /// </summary>
    public sealed class SpectatorRecorder : MonoBehaviour
    {
        // 定数（SerializeField にしない — 既存シーン YAML への焼き込みで型 default=0 に化ける罠回避）
        private const int RecWidth = 1280;
        private const int RecHeight = 720;
        private const int RecFps = 30;
        private const int JpegQuality = 75;
        private const int MaxInFlightPerCam = 4;   // 未回収 readback の上限（超えたら発行スキップ）
        private const int QueueCapacity = 60;      // writer スレッドへ渡す JPEG の bounded キュー

        private const int StreamOverhead = 0;
        private const int StreamPersonFpv = 1;

        private SpectatorController? _spectator;
        private TableDuoPlayer? _person;           // 人役（Role.Full）追従対象
        private bool _personFollowing;             // 直近フレームで頭 pose を取得できたか

        private Camera? _overheadCam;
        private Camera? _fpvCam;
        private RenderTexture? _overheadRt;
        private RenderTexture? _fpvRt;

        private MjpegAviWriter? _overheadWriter;
        private MjpegAviWriter? _fpvWriter;
        private Stream? _overheadStream;
        private Stream? _fpvStream;

        private BlockingCollection<(int stream, byte[] jpeg)>? _queue;
        private Thread? _writerThread;

        private bool _recording;
        private float _startTime;
        private float _accum;
        private int _dropped;
        private string _outputDir = "";

        private int _inFlightOverhead;
        private int _inFlightFpv;

        // メインスレッド専用の作業バッファ（readback コールバックは非再入なので使い回せる）
        private byte[]? _rawScratch;
        private byte[]? _flipScratch;

        // graphicsUVStartsAtTop で読み出しの上下が反転するプラットフォームがある。
        // ⚠ 実行時検証点: 実機で上下が逆に録れていたら、この判定を反転（! を付ける/外す）1 箇所で直る。
        private bool _flipVertical;

        public bool IsRecording => _recording;
        public float ElapsedSec => _recording ? Time.unscaledTime - _startTime : 0f;
        public int DroppedFrames => _dropped;
        public string OutputDir => _outputDir;
        /// <summary>人役 FPV が頭 pose を取得して追従できているか（未接続の間は俯瞰 pose に据え置き）。</summary>
        public bool PersonConnected => _personFollowing;

        public void StartRecording()
        {
            if (_recording) return;
            _spectator = FindObjectOfType<SpectatorController>();
            if (_spectator == null || !_spectator.IsActive)
            {
                Debug.LogWarning("[TableDuo][Rec] 観戦カメラが不在のため録画を開始できません");
                return;
            }

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _outputDir = Path.Combine(Application.persistentDataPath, "tdv_recordings", $"tdv_rec_{stamp}");
            try
            {
                Directory.CreateDirectory(_outputDir);
                _overheadStream = new FileStream(Path.Combine(_outputDir, "overhead.avi"), FileMode.Create, FileAccess.Write);
                _fpvStream = new FileStream(Path.Combine(_outputDir, "person_fpv.avi"), FileMode.Create, FileAccess.Write);
                _overheadWriter = new MjpegAviWriter(_overheadStream, RecWidth, RecHeight, RecFps);
                _fpvWriter = new MjpegAviWriter(_fpvStream, RecWidth, RecHeight, RecFps);
            }
            catch (Exception e)
            {
                Debug.LogError($"[TableDuo][Rec] 録画ファイルを開けません: {e.Message}");
                CleanupWriters();
                return;
            }

            _flipVertical = SystemInfo.graphicsUVStartsAtTop;
            int bytes = RecWidth * RecHeight * 4;
            _rawScratch = new byte[bytes];
            _flipScratch = new byte[bytes];

            _overheadRt = NewRt();
            _fpvRt = NewRt();
            _overheadCam = NewCam("SpectatorRecOverhead", _overheadRt);
            _fpvCam = NewCam("SpectatorRecPersonFpv", _fpvRt);

            // 俯瞰は静止。SpectatorController の俯瞰 framing/FOV をそのまま流用（重複実装しない）
            _spectator.GetOverheadPose(out var camPos, out var look, out var fov);
            var overheadRot = Quaternion.LookRotation(look - camPos, Vector3.up);
            _overheadCam.transform.SetPositionAndRotation(camPos, overheadRot);
            _overheadCam.fieldOfView = fov;
            // 人役 FPV は接続前は俯瞰と同じ pose に置いておく（LateUpdate で頭 pose 取得後に追従開始）
            _fpvCam.transform.SetPositionAndRotation(camPos, overheadRot);
            _fpvCam.fieldOfView = _spectator.FpvFieldOfView;

            _queue = new BlockingCollection<(int, byte[])>(QueueCapacity);
            _writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "SpectatorRecWriter" };
            _writerThread.Start();

            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;

            _inFlightOverhead = 0;
            _inFlightFpv = 0;
            _dropped = 0;
            _accum = 0f;
            _personFollowing = false;
            _startTime = Time.unscaledTime;
            _recording = true;
            Debug.Log($"[TableDuo][Rec] ● 録画開始（俯瞰+人役 FPV / {RecWidth}x{RecHeight}@{RecFps}）→ {_outputDir}");
        }

        public void StopRecording()
        {
            if (!_recording) return;
            _recording = false;

            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;

            // 発行済み readback を最後まで処理（コールバックはメインスレッドで走り _queue へ積む）
            AsyncGPUReadback.WaitAllRequests();

            _queue?.CompleteAdding();
            if (_writerThread != null && _writerThread.IsAlive) _writerThread.Join();
            _writerThread = null;

            try { _overheadWriter?.Close(); }
            catch (Exception e) { Debug.LogWarning($"[TableDuo][Rec] overhead close: {e.Message}"); }
            try { _fpvWriter?.Close(); }
            catch (Exception e) { Debug.LogWarning($"[TableDuo][Rec] fpv close: {e.Message}"); }
            CleanupWriters();
            _queue?.Dispose();
            _queue = null;

            DestroyCam(ref _overheadCam, ref _overheadRt);
            DestroyCam(ref _fpvCam, ref _fpvRt);
            _rawScratch = null;
            _flipScratch = null;

            RestoreHeadToDisplay();
            Debug.Log($"[TableDuo][Rec] ■ 録画終了 → {_outputDir}（俯瞰 overhead.avi / 人役 person_fpv.avi・drop={_dropped}）");
        }

        // ── キャプチャ発行（Update）と追従（LateUpdate）───────────────────────────────
        private void Update()
        {
            if (!_recording) return;
            _accum += Time.unscaledDeltaTime;
            float interval = 1f / RecFps;
            if (_accum < interval) return;
            _accum -= interval;
            if (_accum > interval) _accum = interval; // 過剰キャッチアップ抑止（1 フレーム最大 1 発）

            TryRequest(StreamOverhead);
            TryRequest(StreamPersonFpv);
        }

        // RemoteAvatarView が頭 pose を平滑した「後」に追従するため LateUpdate。
        private void LateUpdate()
        {
            if (!_recording || _fpvCam == null) return;
            if (_person == null) AcquirePerson();
            if (_person != null && _person.TryGetRemoteHeadWorldPose(out var hp, out var hr))
            {
                _fpvCam.transform.SetPositionAndRotation(hp, hr);
                if (_spectator != null) _fpvCam.fieldOfView = _spectator.FpvFieldOfView;
                _personFollowing = true;
            }
            else
            {
                _personFollowing = false; // 未接続 / pose 未着: StartRecording で置いた俯瞰 pose のまま
            }
        }

        private void AcquirePerson()
        {
            foreach (var p in FindObjectsOfType<TableDuoPlayer>())
            {
                if (p.Role == StudyConfig.Role.Full) { _person = p; return; }
            }
        }

        private void TryRequest(int stream)
        {
            var rt = stream == StreamOverhead ? _overheadRt : _fpvRt;
            if (rt == null) return;
            int inFlight = stream == StreamOverhead ? _inFlightOverhead : _inFlightFpv;
            if (inFlight >= MaxInFlightPerCam) { _dropped++; return; }
            if (stream == StreamOverhead) _inFlightOverhead++; else _inFlightFpv++;
            AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, req => OnReadback(stream, req));
        }

        // readback 完了コールバック（メインスレッド）。ここで JPEG まで作り、書き込みだけ別スレッドへ回す。
        private void OnReadback(int stream, AsyncGPUReadbackRequest req)
        {
            if (stream == StreamOverhead) _inFlightOverhead--; else _inFlightFpv--;

            if (req.hasError) { _dropped++; return; }
            if (_queue == null || _queue.IsAddingCompleted || _rawScratch == null) return;

            var data = req.GetData<byte>();
            int need = RecWidth * RecHeight * 4;
            if (data.Length < need) { _dropped++; return; }

            byte[] toEncode = _rawScratch;
            NativeArray<byte>.Copy(data, _rawScratch, need);
            if (_flipVertical && _flipScratch != null)
            {
                FlipVertical(_rawScratch, _flipScratch, RecWidth, RecHeight);
                toEncode = _flipScratch;
            }

            byte[] jpeg;
            try
            {
                jpeg = ImageConversion.EncodeArrayToJPG(
                    toEncode, GraphicsFormat.R8G8B8A8_UNorm,
                    (uint)RecWidth, (uint)RecHeight, 0, JpegQuality);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TableDuo][Rec] JPEG エンコード失敗（このフレームは破棄）: {e.Message}");
                _dropped++;
                return;
            }
            if (jpeg == null || jpeg.Length == 0) { _dropped++; return; }

            if (!_queue.TryAdd((stream, jpeg))) _dropped++; // 満杯はブロックせず drop 計上
        }

        private void WriterLoop()
        {
            try
            {
                foreach (var (stream, jpeg) in _queue!.GetConsumingEnumerable())
                {
                    try
                    {
                        var writer = stream == StreamOverhead ? _overheadWriter : _fpvWriter;
                        writer?.WriteFrame(jpeg, jpeg.Length);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[TableDuo][Rec] フレーム書き込み失敗（記録継続）: {e.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[TableDuo][Rec] writer スレッド異常終了: {e.Message}");
            }
        }

        // ── 頭の per-camera 表示制御（グローバル潰しと録画の両立）────────────────────────
        private void OnBeginCameraRendering(ScriptableRenderContext ctx, Camera cam)
        {
            if (_person == null) return;
            if (cam == _fpvCam) _person.SetRemoteHeadCollapsed(true);       // 人役 FPV は頭を潰す
            else if (cam == _overheadCam) _person.SetRemoteHeadCollapsed(false); // 俯瞰は頭を戻す
        }

        private void OnEndCameraRendering(ScriptableRenderContext ctx, Camera cam)
        {
            if (_person == null) return;
            // 自分の録画カメラの描画が終わったら「表示側の状態」へ必ず戻す（表示カメラを壊さない）
            if (cam == _fpvCam || cam == _overheadCam) RestoreHeadToDisplay();
        }

        private void RestoreHeadToDisplay()
        {
            if (_person == null) return;
            var display = _spectator != null ? _spectator.DisplayCollapsedPlayer : null;
            _person.SetRemoteHeadCollapsed(_person == display);
        }

        private void CleanupWriters()
        {
            try { _overheadStream?.Dispose(); } catch { /* best-effort */ }
            try { _fpvStream?.Dispose(); } catch { /* best-effort */ }
            _overheadStream = null;
            _fpvStream = null;
            _overheadWriter = null;
            _fpvWriter = null;
        }

        private static void DestroyCam(ref Camera? cam, ref RenderTexture? rt)
        {
            if (cam != null) { cam.targetTexture = null; Destroy(cam.gameObject); cam = null; }
            if (rt != null) { rt.Release(); Destroy(rt); rt = null; }
        }

        private static RenderTexture NewRt()
        {
            var rt = new RenderTexture(RecWidth, RecHeight, 24, RenderTextureFormat.ARGB32) { name = "SpectatorRecRT" };
            rt.Create();
            return rt;
        }

        private static Camera NewCam(string name, RenderTexture rt)
        {
            var go = new GameObject(name);
            var cam = go.AddComponent<Camera>();
            cam.targetTexture = rt;   // オフスクリーン（画面には出さない）。AudioListener は付けない
            cam.enabled = true;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 100f;
            cam.clearFlags = CameraClearFlags.Skybox;
            return cam;
        }

        private static void FlipVertical(byte[] src, byte[] dst, int w, int h)
        {
            int row = w * 4;
            for (int y = 0; y < h; y++)
                Buffer.BlockCopy(src, y * row, dst, (h - 1 - y) * row, row);
        }

        private void OnDestroy()
        {
            if (_recording) StopRecording();
        }

        private void OnApplicationQuit()
        {
            if (_recording) StopRecording();
        }
    }
}
