#nullable enable
#if UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Debug = UnityEngine.Debug;

/// <summary>
/// 再生（<see cref="XpReplayDriver"/>）を、広い画角のカメラで撮って mp4 にする。
///
/// 絵: 体験者の目と同じものを映すカメラ（cullingMask・背景・URP の設定を目から写す）を別に立て、
/// 画角だけ広げる。<c>view=eye</c> は頭の位置から少し遅れて追う（首の細かい揺れを均す）。
/// <c>view=follow</c> は頭の後ろ上から、スクリーンと体験者の周りを一緒に入れる。
/// 出力は決まった fps（既定 30）。再生は実時間なので、出すべきコマの時刻が来たときだけ描き、
/// PC が追いつかなかったコマは直前の絵を繰り返す（尺と音の同期を保つ方を取る）。
/// 生の画素を ffmpeg へ流す（途中の連番画像を作らない）。
///
/// 音: 体験者の耳（AudioListener）に届く最終の音を <c>OnAudioFilterRead</c> で写して WAV にし、最後に映像と合わせる。
/// ⚠ <c>AudioRenderer</c> は使わない（Editor の batchmode で 1 サンプルも返さなかった・2026-10-23 実測）。
/// </summary>
// ⚠ batchmode では WaitForEndOfFrame が来ない（画面が無いので「フレームの終わり」が発火しない）。
//    撮影は自分でカメラを描くので、他の全員の LateUpdate が済んだ後（実行順の最後）で撮る。
[DefaultExecutionOrder(32000)]
public sealed class XpReplayCapture : MonoBehaviour
{
    public const string KeyOut = "fcv.replay.out";       // 出力する mp4 のフルパス
    public const string KeyView = "fcv.replay.view";     // eye / follow
    public const string KeyFov = "fcv.replay.fov";       // 縦の画角（度）
    public const string KeyW = "fcv.replay.w";
    public const string KeyH = "fcv.replay.h";
    public const string KeyFps = "fcv.replay.fps";
    public const string KeyMaxSec = "fcv.replay.maxsec"; // 撮る長さの上限（0 = 記録の終わりまで）

    /// <summary>出力先のフォルダ。null なら撮らない。</summary>
    public static string? OutputDir
    {
        get
        {
            string p = SessionState.GetString(KeyOut, "");
            return string.IsNullOrEmpty(p) ? null : Path.GetDirectoryName(p);
        }
    }

    private XpReplayDriver _driver = null!;
    private Camera? _cam;
    private RenderTexture? _rt;
    private Texture2D? _readback;
    private int _w, _h, _fps;
    private float _fov, _maxSec;
    private string _view = "eye";
    private string _out = "";
    private string _videoTmp = "", _audioTmp = "";

    private Process? _ffmpeg;
    private Thread? _writer;
    private readonly BlockingCollection<byte[]> _frames = new(8);
    private readonly ConcurrentBag<byte[]> _pool = new();
    private byte[]? _last;
    private int _videoFrames, _rendered, _repeated;
    private float _captureT0;
    private bool _capturing, _closed;
    private bool _hideEye;
    private string _encoder = "";
    private int _msaa = 1;
    private readonly Stopwatch _swRender = new(), _swPush = new();
    private int? _eyeMask;

    private XpReplayAudioTap? _tap;

    private Vector3 _sp;
    private Quaternion _sq = Quaternion.identity;
    private bool _smoothInit;
    private float _smoothAt;

    private void Start()
    {
        _driver = GetComponent<XpReplayDriver>();
        _out = SessionState.GetString(KeyOut, "");
        _view = SessionState.GetString(KeyView, "eye");
        _fov = SessionState.GetFloat(KeyFov, _view == "follow" ? 62f : 72f);
        _w = Mathf.Clamp(SessionState.GetInt(KeyW, 1920), 320, 7680) & ~1;
        _h = Mathf.Clamp(SessionState.GetInt(KeyH, 1080), 240, 4320) & ~1;
        _fps = Mathf.Clamp(SessionState.GetInt(KeyFps, 30), 10, 120);
        _maxSec = SessionState.GetFloat(KeyMaxSec, 0f);
        int m = SessionState.GetInt("fcv.replay.msaa", 1);
        _msaa = m >= 8 ? 8 : m >= 4 ? 4 : m >= 2 ? 2 : 1;
        _hideEye = Application.isBatchMode && SessionState.GetInt("fcv.replay.eyecam", 0) == 0;
    }

    private void LateUpdate()
    {
        if (_closed || !_driver.Started) return;

        bool over = _driver.Finished || (_maxSec > 0f && _driver.Tau > _maxSec);
        if (over) { Close(); return; }

        if (!_capturing && !Open()) { _closed = true; Done("error: 撮影を始められない"); return; }

        // 出すべきコマ数（実時間から）。追いついていれば描かない。遅れていれば 1 回描いて残りは繰り返す。
        int due = Mathf.FloorToInt((Time.realtimeSinceStartup - _captureT0) * _fps) + 1;
        if (_videoFrames >= due) return;
        _swRender.Start();
        byte[] frame = Render();
        _swRender.Stop();
        _rendered++;
        _swPush.Start();
        Push(frame);
        while (_videoFrames < due) { Push(frame); _repeated++; }
        _swPush.Stop();
    }

    // ------------------------------------------------------------------ 開く・閉じる

    private bool Open()
    {
        if (string.IsNullOrEmpty(_out)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(_out)!);
        _videoTmp = Path.ChangeExtension(_out, ".video.mp4");
        _audioTmp = Path.ChangeExtension(_out, ".audio.wav");

        string exe = FindFfmpeg();
        // 圧縮は GPU（Intel Quick Sync）が使えればそちら。CPU の x264 は再生と取り合い、書き出し待ちで描画が止まる。
        _encoder = ProbeQsv(exe) ? "-c:v h264_qsv -global_quality 20 -look_ahead 0" : "-c:v libx264 -preset veryfast -crf 19";
        var psi = new ProcessStartInfo(exe,
            $"-y -v error -f rawvideo -pix_fmt rgb24 -s {_w}x{_h} -r {_fps} -i - " +
            $"-vf vflip,format=nv12 {_encoder} \"{_videoTmp}\"")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        try { _ffmpeg = Process.Start(psi); }
        catch (Exception e) { Debug.LogError($"[Replay] ffmpeg を起動できない（{exe}）: {e.Message}"); return false; }
        if (_ffmpeg == null) return false;
        _ffmpeg.ErrorDataReceived += (_, a) => { if (!string.IsNullOrEmpty(a.Data)) Debug.LogWarning("[Replay][ffmpeg] " + a.Data); };
        _ffmpeg.BeginErrorReadLine();
        Stream stdin = _ffmpeg.StandardInput.BaseStream;
        int bytes = _w * _h * 3;
        _writer = new Thread(() =>
        {
            // 同じ配列が続けて来る（繰り返しのコマ）。別の配列に変わった時点で、前のものはもう来ないので使い回しへ戻す。
            byte[]? prev = null;
            try
            {
                foreach (byte[] f in _frames.GetConsumingEnumerable())
                {
                    stdin.Write(f, 0, bytes);
                    if (prev != null && !ReferenceEquals(prev, f)) _pool.Add(prev);
                    prev = f;
                }
            }
            catch (Exception e) { Debug.LogError("[Replay] 映像を書けない: " + e.Message); }
            finally { try { stdin.Flush(); stdin.Close(); } catch { } }
        }) { IsBackground = true, Name = "ReplayVideo" };
        _writer.Start();

        _rt = new RenderTexture(_w, _h, 24, RenderTextureFormat.ARGB32) { antiAliasing = _msaa };
        _rt.Create();
        _readback = new Texture2D(_w, _h, TextureFormat.RGB24, false);
        var go = new GameObject("[ReplayCam]");
        go.transform.SetParent(transform, false);
        _cam = go.AddComponent<Camera>();
        _cam.enabled = false;

        AudioListener? listener = FindObjectOfType<AudioListener>();
        if (listener != null)
        {
            _tap = listener.gameObject.AddComponent<XpReplayAudioTap>();
            _tap.Begin(_audioTmp);
        }
        else Debug.LogWarning("[Replay] AudioListener が居ない。音は撮れない");

        _captureT0 = Time.realtimeSinceStartup;
        _capturing = true;
        Debug.Log($"[Replay] 撮影を始めた {_w}x{_h} {_fps}fps msaa={_msaa} view={_view} fov={_fov:F0} " +
                  $"音={AudioSettings.outputSampleRate}Hz → {_out}");
        return true;
    }

    private void Push(byte[] frame)
    {
        _frames.Add(frame);   // 8 コマ溜まったら書き出しを待つ
        _videoFrames++;
    }

    private void Close()
    {
        if (_closed) return;
        _closed = true;
        if (!_capturing) { Done("error: 1 コマも撮れなかった"); return; }
        double aSec = _tap != null ? _tap.Finish() : 0;
        _frames.CompleteAdding();
        _writer?.Join();
        if (_ffmpeg != null && !_ffmpeg.WaitForExit(600_000)) _ffmpeg.Kill();
        int vCode = _ffmpeg?.ExitCode ?? -1;
        double vSec = _videoFrames / (double)_fps;
        Debug.Log($"[Replay] 撮り終えた: 映像 {_videoFrames} コマ = {vSec:F2}s（描いた {_rendered}・繰り返し {_repeated}）" +
                  $" / 音 {aSec:F2}s / 進行 {_driver.Tau:F1}s（ffmpeg exit={vCode}）");
        if (_rendered > 0)
            Debug.Log($"[Replay] 1 コマあたり: 描画と読み出し {_swRender.Elapsed.TotalMilliseconds / _rendered:F1}ms / " +
                      $"書き出し待ち {_swPush.Elapsed.TotalMilliseconds / _rendered:F1}ms / 圧縮 {_encoder}");
        if (_rendered > 0 && _repeated > _rendered / 4)
            Debug.LogWarning($"[Replay] ⚠ PC が追いつかず {_repeated} コマを繰り返した（カクつく）。" +
                             "w/h を下げるか fps=24 で撮り直す");
        if (vCode != 0) { Done("error: ffmpeg が失敗した"); return; }

        if (aSec > 0.5)
        {
            var mux = Process.Start(new ProcessStartInfo(FindFfmpeg(),
                $"-y -v error -i \"{_videoTmp}\" -i \"{_audioTmp}\" -c:v copy -c:a aac -b:a 192k -shortest \"{_out}\"")
                { UseShellExecute = false, CreateNoWindow = true });
            mux?.WaitForExit();
            if (mux == null || mux.ExitCode != 0) { Done("error: 音の多重化に失敗した"); return; }
            File.Delete(_videoTmp);
            File.Delete(_audioTmp);
        }
        else
        {
            Debug.LogWarning("[Replay] 音が撮れていない。映像だけ書く");
            if (File.Exists(_out)) File.Delete(_out);
            File.Move(_videoTmp, _out);
        }
        Done(_out);
    }

    private void Done(string result)
    {
        SessionState.SetString(XpReplayDriver.KeyResult, result);
        SessionState.SetBool(XpReplayDriver.KeyDone, true);
        Debug.Log("[Replay] 結果: " + result);
        EditorApplication.ExitPlaymode();
    }

    private void OnDestroy()
    {
        if (_capturing && !_closed)
        {
            // Play を途中で止めた。書いたところまでは閉じて残す。
            try { _tap?.Finish(); } catch { }
            _frames.CompleteAdding();
            _writer?.Join(5000);
        }
        if (_rt != null) Destroy(_rt);
        if (_readback != null) Destroy(_readback);
    }

    // ------------------------------------------------------------------ 1 コマ

    private byte[] Render()
    {
        Camera? eye = Camera.main;
        if (eye == null || _cam == null || _rt == null || _readback == null)
            return _last ?? new byte[_w * _h * 3];
        int bytes = _w * _h * 3;
        if (!_pool.TryTake(out byte[]? buf) || buf.Length < bytes) buf = new byte[bytes];

        // 目のカメラは誰も見ない（batchmode に画面は無い）のに毎フレーム全画面を描く。撮る側だけ描けば足りる。
        // ⚠ enabled は下ろさない（Camera.main が null になり、頭を Camera.main で引く本番の処理が全部外れる）。
        //    映すものを空にし、撮る側へ元の cullingMask を戻す。
        if (_eyeMask == null) _eyeMask = eye.cullingMask;
        _cam.CopyFrom(eye);
        _cam.cullingMask = _eyeMask.Value;
        _cam.enabled = false;
        if (_hideEye) eye.cullingMask = 0;
        _cam.stereoTargetEye = StereoTargetEyeMask.None;
        _cam.targetTexture = _rt;
        _cam.fieldOfView = _fov;
        _cam.aspect = _w / (float)_h;
        CopyUrp(eye, _cam);
        PlaceCamera();

        _cam.Render();
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = _rt;
        _readback.ReadPixels(new Rect(0, 0, _w, _h), 0, 0, false);
        RenderTexture.active = prev;
        NativeArray<byte>.Copy(_readback.GetRawTextureData<byte>(), buf, bytes);
        _last = buf;
        return buf;
    }

    private void PlaceCamera()
    {
        _driver.HeadPose(out Vector3 hp, out Quaternion hq);
        Vector3 fwd = hq * Vector3.forward;
        Vector3 flat = new Vector3(fwd.x, 0f, fwd.z);
        Quaternion yaw = flat.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(flat.normalized, Vector3.up) : Quaternion.identity;

        Vector3 tp;
        Quaternion tq;
        float posTc, rotTc;
        if (_view == "follow")
        {
            tp = hp + yaw * new Vector3(0f, 0.35f, -1.4f);
            Vector3 look = hp + yaw * new Vector3(0f, -0.25f, 2.0f);
            tq = Quaternion.LookRotation(look - tp, Vector3.up);
            posTc = 0.6f; rotTc = 0.8f;
        }
        else
        {
            tp = hp;
            tq = hq;
            posTc = 0.12f; rotTc = 0.25f;
        }

        // 実時間で均す（描くコマの間隔が揃わなくても、動きの速さは変わらない）。
        float now = Time.realtimeSinceStartup;
        float dt = _smoothInit ? Mathf.Clamp(now - _smoothAt, 0f, 0.5f) : 0f;
        _smoothAt = now;
        if (!_smoothInit) { _sp = tp; _sq = tq; _smoothInit = true; }
        _sp = Vector3.Lerp(_sp, tp, 1f - Mathf.Exp(-dt / posTc));
        _sq = Quaternion.Slerp(_sq, tq, 1f - Mathf.Exp(-dt / rotTc));
        // 傾き（ロール）は動画では酔うので落とす。
        Vector3 f = _sq * Vector3.forward;
        _cam!.transform.SetPositionAndRotation(_sp, Quaternion.LookRotation(f, Vector3.up));
    }

    private static void CopyUrp(Camera from, Camera to)
    {
        var src = from.GetUniversalAdditionalCameraData();
        var dst = to.GetUniversalAdditionalCameraData();
        if (src == null || dst == null) return;
        dst.renderPostProcessing = src.renderPostProcessing;
        dst.antialiasing = src.antialiasing;
        dst.requiresColorOption = src.requiresColorOption;
        dst.requiresDepthOption = src.requiresDepthOption;
        dst.renderShadows = src.renderShadows;
        dst.volumeLayerMask = src.volumeLayerMask;
        dst.volumeTrigger = src.volumeTrigger != null ? src.volumeTrigger : from.transform;
    }

    private static bool ProbeQsv(string exe)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(exe,
                "-v error -f lavfi -i nullsrc=s=256x256:r=30 -t 0.2 -c:v h264_qsv -f null -")
                { UseShellExecute = false, CreateNoWindow = true });
            return p != null && p.WaitForExit(10_000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string FindFfmpeg()
    {
        string links = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        return File.Exists(links) ? links : "ffmpeg";
    }
}
#endif
