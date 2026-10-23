#nullable enable
#if UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using UnityEngine;


/// <summary>
/// AudioListener と同じ GameObject に付けて、耳に届く最終の音を写す（音は変えずにそのまま通す）。
/// 音の糸から呼ばれるので、配列を溜めて別の糸で 32bit float の WAV に書く。
/// </summary>
public sealed class XpReplayAudioTap : MonoBehaviour
{
    private readonly ConcurrentQueue<float[]> _q = new();
    private readonly AutoResetEvent _signal = new(false);
    private Thread? _thread;
    private volatile bool _on, _stop;
    private int _channels, _rate;
    private long _frames;
    private string _path = "";

    public void Begin(string path)
    {
        _path = path;
        _rate = AudioSettings.outputSampleRate;
        _thread = new Thread(Run) { IsBackground = true, Name = "ReplayAudio" };
        _thread.Start();
        _on = true;
    }

    private void OnAudioFilterRead(float[] data, int channels)
    {
        if (!_on || _stop) return;
        if (_channels == 0) _channels = channels;
        var copy = new float[data.Length];
        Array.Copy(data, copy, data.Length);
        _q.Enqueue(copy);
        _signal.Set();
    }

    /// <summary>止めて書き切る。撮れた秒数を返す。</summary>
    public double Finish()
    {
        _on = false;
        _stop = true;
        _signal.Set();
        _thread?.Join(10000);
        return _rate > 0 && _channels > 0 ? _frames / (double)_rate : 0;
    }

    private void Run()
    {
        try
        {
            using var fs = new FileStream(_path, FileMode.Create, FileAccess.Write);
            using var w = new BinaryWriter(fs);
            Header(w, 0);
            while (true)
            {
                _signal.WaitOne(100);
                while (_q.TryDequeue(out float[]? buf))
                {
                    foreach (float v in buf) w.Write(v);
                    if (_channels > 0) _frames += buf.Length / _channels;
                }
                if (_stop && _q.IsEmpty) break;
            }
            w.Flush();
            fs.Position = 0;
            Header(w, _frames);
        }
        catch (Exception e) { Debug.LogError("[Replay] 音を書けない: " + e.Message); }
    }

    private void Header(BinaryWriter w, long frames)
    {
        int ch = Math.Max(1, _channels == 0 ? 2 : _channels);
        long data = frames * ch * 4;
        w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
        w.Write((uint)Math.Min(uint.MaxValue, 36 + data));
        w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
        w.Write(16u);
        w.Write((ushort)3);
        w.Write((ushort)ch);
        w.Write((uint)_rate);
        w.Write((uint)(_rate * ch * 4));
        w.Write((ushort)(ch * 4));
        w.Write((ushort)32);
        w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
        w.Write((uint)Math.Min(uint.MaxValue, data));
    }
}
#endif
