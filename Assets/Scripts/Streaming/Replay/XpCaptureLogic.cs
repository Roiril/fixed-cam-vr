#nullable enable
using System.Globalization;
using System.IO;
using UnityEngine;

namespace FixedCamVr.Streaming.Replay
{
    /// <summary>
    /// 目線の動画を撮る側（Assembly-CSharp の <c>XpReplayCapture</c>）と、撮らせる入口
    /// （Editor の <c>XpReplayRun</c> / <c>XpTwinRun</c>）が同じ値で動くための純関数。
    /// Assembly-CSharp は Editor の asmdef からもテストからも参照できないので、共有する分だけここに置く。
    /// </summary>
    public static class XpCaptureLogic
    {
        /// <summary>ツイン走行で撮る目線の動画の縦の画角（度）。紹介動画の既定 72 度だと画面が幅の 3 分の 1 しかない。</summary>
        public const float SinglePassEyeFovDeg = 52f;

        /// <summary>撮影の値が無いときの縦の画角（度）。</summary>
        public static float DefaultFovDeg(string view) => view == "follow" ? 62f : 72f;

        /// <summary>ツイン走行の <c>capture=</c>。</summary>
        public enum TwinCapture
        {
            /// <summary>撮らない（値なし・off・none）。</summary>
            None,
            /// <summary>走行の Play の中で目線の動画を撮る。</summary>
            Eye,
        }

        /// <summary>
        /// ツイン走行の <c>capture=</c> を読む。読めなければ false と理由。
        /// follow（後ろ上）は同時撮影しない — 撮影カメラが 1 台なので、目線と並べて撮るには 2 回目の再生が要る。
        /// </summary>
        public static bool TryParseTwinCapture(string? value, out TwinCapture mode, out string error)
        {
            mode = TwinCapture.None;
            error = "";
            string v = (value ?? "").Trim().ToLowerInvariant();
            switch (v)
            {
                case "":
                case "off":
                case "none":
                    return true;
                case "eye":
                    mode = TwinCapture.Eye;
                    return true;
                case "follow":
                    error = "capture=follow は走行と同時に撮れない（後ろ上は記録を replay で撮る）";
                    return false;
                default:
                    error = $"capture は eye か off（受け取ったもの: {value}）";
                    return false;
            }
        }

        /// <summary>撮影の寸法・fps・MSAA を、撮れる範囲へ丸める（幅と高さは偶数・MSAA は 1/2/4/8）。</summary>
        public static void Normalize(ref int w, ref int h, ref int fps, ref int msaa)
        {
            w = Mathf.Clamp(w, 320, 7680) & ~1;
            h = Mathf.Clamp(h, 240, 4320) & ~1;
            fps = Mathf.Clamp(fps, 10, 120);
            msaa = msaa >= 8 ? 8 : msaa >= 4 ? 4 : msaa >= 2 ? 2 : 1;
        }

        /// <summary>
        /// <c>replay_xp.log</c> の 1 行（<c>tau=12.50 [XP] t=... ev=...</c>）。tau は動画の秒。
        /// <c>tools/twin-look.py</c> / <c>tools/fx-library/build.py</c> / <c>tools/quest-xprec.py compare</c> がこの形で読む。
        /// </summary>
        public static void WriteTauLine(TextWriter w, float tau, string msg)
        {
            w.Write("tau=");
            w.Write(tau.ToString("F2", CultureInfo.InvariantCulture));
            w.Write(' ');
            w.WriteLine(msg);
        }
    }
}
