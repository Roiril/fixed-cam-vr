#nullable enable
using System;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 実機の GPU 負荷を切り分けるための診断の口（Development ビルドだけ）。
    /// intent の extra <c>perfcrt</c> で、ブラウン管の信号処理をどこまで回すかを選ぶ。
    /// <list type="bullet">
    /// <item><c>freeze</c> — 信号の前段を最初の 1 回だけ組み、以後は回さない（画は止まるが材質は正しく束ねたまま）</item>
    /// <item><c>legacy</c> — ブラウン管の参照面そのものを使わず、旧来の平らなスクリーンで描く</item>
    /// </list>
    /// 本番（Release）と Editor では常に通常。読めなければ通常に倒す。
    /// </summary>
    public static class CrtPerfProbe
    {
        public enum Mode { Normal, Freeze, Legacy, NoPsf, NoGlass, Count, Half }

        /// <summary>通常以外の口では、信号処理の回数を 5 秒ごとにログへ出す（<c>[PerfCrt] rate</c>）。</summary>
        public static bool Logs => Current != Mode.Normal;

        private static Mode? _mode;

        public static Mode Current => _mode ??= Read();

        private static Mode Read()
        {
#if UNITY_ANDROID && !UNITY_EDITOR && DEVELOPMENT_BUILD
            try
            {
                using var up = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var act = up.GetStatic<AndroidJavaObject>("currentActivity");
                using var intent = act.Call<AndroidJavaObject>("getIntent");
                string v = intent.Call<string>("getStringExtra", "perfcrt");
                Mode m = Parse(v);
                if (m != Mode.Normal) Debug.Log($"[PerfCrt] 診断の口: perfcrt={v} → {m}");
                return m;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PerfCrt] intent extra 読取失敗（perfcrt）: {e.Message}");
                return Mode.Normal;
            }
#else
            return Mode.Normal;
#endif
        }

        public static Mode Parse(string? v) => v switch
        {
            "freeze" => Mode.Freeze,
            "legacy" => Mode.Legacy,
            "nopsf" => Mode.NoPsf,       // ぼかし（受信ごと）を最初の 1 回だけにする
            "noglass" => Mode.NoGlass,   // 前段の合成と濾波（毎フレーム）を最初の 1 回だけにする
            "count" => Mode.Count,       // 処理は通常のまま、回数だけ出す
            "half" => Mode.Half,         // 中間の画を 16bit 浮動小数（ARGBHalf）で持つ
            _ => Mode.Normal,
        };
    }
}
