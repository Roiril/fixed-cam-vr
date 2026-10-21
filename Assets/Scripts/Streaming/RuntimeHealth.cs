#nullable enable
using System;
using System.Threading;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 実行中に起きた例外を数え、リセットの工程を 1 つずつ隔離する。
    ///
    /// ⚠ 例外はそれまで logcat にしか出ておらず、展示中は誰にも見えなかった。数をタブレットへ渡すと、
    /// 「画が変・進まない」が<b>コードの例外によるものか</b>をスタッフがその場で見分けられる
    /// （直す手はどちらもタブレットの強制リセット）。
    ///
    /// ⚠ リセットの連鎖は 1 本の同期処理なので、途中の 1 か所が投げると<b>後ろの初期化が全部飛び、
    /// 一部だけ前の体験者の状態が残る</b>。<see cref="Step"/> で工程ごとに捕まえて先へ進める。
    /// </summary>
    public static class RuntimeHealth
    {
        private static int _exceptions;
        private static string _lastException = "";
        private static bool _installed;

        /// <summary>起動から捕まえた例外の数。</summary>
        public static int ExceptionCount => Volatile.Read(ref _exceptions);

        /// <summary>直近の例外の 1 行目（観測用・ログに既に出ているもの）。</summary>
        public static string LastException => Volatile.Read(ref _lastException);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Install()
        {
            _exceptions = 0;
            _lastException = "";
            if (_installed) Application.logMessageReceivedThreaded -= OnLog;
            Application.logMessageReceivedThreaded += OnLog;
            _installed = true;
        }

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Exception) return;
            Interlocked.Increment(ref _exceptions);
            string head = condition ?? "";
            int nl = head.IndexOf('\n');
            if (nl >= 0) head = head.Substring(0, nl);
            if (head.Length > 160) head = head.Substring(0, 160);
            Volatile.Write(ref _lastException, head);
        }

        /// <summary>リセットの 1 工程を走らせる。投げても次の工程へ進む。</summary>
        public static void Step(string name, Action? action)
        {
            if (action == null) return;
            try { action(); }
            catch (Exception e)
            {
                Debug.LogWarning($"[RuntimeHealth] リセットの工程 {name} が失敗したので飛ばして続けます");
                Debug.LogException(e);
            }
        }

        /// <summary>イベントの購読者を 1 つずつ隔離して呼ぶ。</summary>
        public static void Raise(string name, Action? handlers)
        {
            if (handlers == null) return;
            foreach (Delegate d in handlers.GetInvocationList())
                Step(name, (Action)d);
        }
    }
}
