#nullable enable
using System;
using UnityEngine;

namespace FixedCamVr.Streaming.Replay
{
    /// <summary>
    /// 体験の記録（Quest）と再生（PC の Editor）をつなぐ口。
    ///
    /// <b>記録</b>: <c>OvrControllerBridge</c> が毎フレーム読んだ生の入力を <see cref="Live"/> に置き、
    /// <see cref="ExperienceRecorder"/> がそれを頭の姿勢・カメラの JPEG と並べて書く。
    /// <b>再生</b>: <c>XpReplayDriver</c> が <see cref="Override"/> と <see cref="Feed"/> を立てる。
    /// 立っている間、入力は記録から、カメラの映像はファイルから来る。本番の処理はそのまま通る。
    ///
    /// Streaming は OVR を参照しない規約なので、OVR の値はこの型に詰め替えて受け渡す。
    /// </summary>
    public static class XpReplay
    {
        /// <summary>再生中か。立っている間、外への通信（卓・タブレット・発見・台車・配信）をしない。</summary>
        public static bool Active { get; private set; }

        /// <summary>このフレームの生の入力（記録側が読む）。</summary>
        public static RawInput Live;

        /// <summary>再生中に入力を差し替える。null なら OVR から読む。</summary>
        public static Func<RawInput>? Override { get; private set; }

        /// <summary>再生中のカメラ映像の出どころ。null なら MJPEG を受ける。</summary>
        public static IFrameFeed? Feed { get; private set; }

        public static void Begin(Func<RawInput> input, IFrameFeed feed)
        {
            Override = input;
            Feed = feed;
            Active = true;
        }

        public static void End()
        {
            Active = false;
            Override = null;
            Feed = null;
        }

        /// <summary>カメラ 1 台ぶんの映像の出どころ。</summary>
        public interface IFrameFeed
        {
            /// <summary>
            /// <paramref name="source"/> の、いま見えるべき最新のコマを渡す。前回渡したコマから変わっていなければ false
            /// （MJPEG の「新しいコマが来ていない」と同じ扱い）。
            /// </summary>
            bool TryConsume(string source, ref byte[]? buf, out int length, out long seq);

            /// <summary>まだ渡していない新しいコマがあるか（背景の展開の順番決めに使う）。</summary>
            bool HasPending(string source);
        }
    }

    /// <summary>1 フレームで読んだ生の入力。ビットの並びは記録ファイルの形式でもある（変えるなら版を上げる）。</summary>
    [Serializable]
    public struct RawInput
    {
        public const int LConnected = 1 << 0;
        public const int LTracked = 1 << 1;
        public const int RConnected = 1 << 2;
        public const int RTracked = 1 << 3;
        public const int X = 1 << 4;
        public const int Y = 1 << 5;
        public const int A = 1 << 6;
        public const int B = 1 << 7;
        public const int RTrigger = 1 << 8;
        public const int LGrip = 1 << 9;
        public const int UserPresent = 1 << 10;
        /// <summary>体験者が始めてよい（<c>ShowControlClient.StartAuthorized</c>）。記録器が足す。入力ではない。</summary>
        public const int StartAuthorized = 1 << 11;
        /// <summary>題字が閉じ切った（<c>TitleScreen.ClosedAlready</c>）。記録器が足す。入力ではない。</summary>
        public const int TitleClosed = 1 << 12;

        public int bits;

        public bool Has(int flag) => (bits & flag) != 0;

        public static RawInput From(bool lConn, bool lTrk, bool rConn, bool rTrk, bool x, bool y,
                                    bool a, bool b, bool trigger, bool grip, bool userPresent)
        {
            int v = 0;
            if (lConn) v |= LConnected;
            if (lTrk) v |= LTracked;
            if (rConn) v |= RConnected;
            if (rTrk) v |= RTracked;
            if (x) v |= X;
            if (y) v |= Y;
            if (a) v |= A;
            if (b) v |= B;
            if (trigger) v |= RTrigger;
            if (grip) v |= LGrip;
            if (userPresent) v |= UserPresent;
            return new RawInput { bits = v };
        }
    }
}
