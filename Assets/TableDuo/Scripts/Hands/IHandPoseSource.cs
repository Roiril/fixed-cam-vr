#nullable enable
using System.Collections.Generic;

namespace TableDuoVr.Hands
{
    /// <summary>
    /// アバター姿勢の供給元。実トラッキング（<see cref="HandPoseSampler"/>）と
    /// 再生・合成（FakeHandDriver）を同一インターフェースで差し替える。
    /// ネット層以降はこのインターフェースしか見ない。
    /// </summary>
    public interface IHandPoseSource
    {
        /// <summary>毎フレーム更新される最新姿勢。呼び出し側は保持せず読むだけ。</summary>
        AvatarPose Current { get; }

        /// <summary>有効なデータを供給できている間 true。</summary>
        bool IsValid { get; }

        /// <summary>複数ソース併存時の優先度（大が勝つ）。Fake=10 / 実トラッキング=0。</summary>
        int Priority { get; }
    }

    /// <summary>アクティブな IHandPoseSource の静的レジストリ。OnEnable/OnDisable で登録する。</summary>
    public static class HandPoseSourceRegistry
    {
        private static readonly List<IHandPoseSource> Sources = new();

        public static void Register(IHandPoseSource s)
        {
            if (!Sources.Contains(s)) Sources.Add(s);
        }

        public static void Unregister(IHandPoseSource s) => Sources.Remove(s);

        /// <summary>優先度最大の有効ソース。無ければ null。</summary>
        public static IHandPoseSource? Best
        {
            get
            {
                IHandPoseSource? best = null;
                foreach (var s in Sources)
                {
                    if (!s.IsValid) continue;
                    if (best == null || s.Priority > best.Priority) best = s;
                }
                return best;
            }
        }
    }
}
