#nullable enable
namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 区間 BGM 指示 → 実行すべき遷移、の純判定（UnityEngine 非依存）。
    ///
    /// 動画編集のオーディオトラックと同じ感覚で書けることを狙う:
    ///   - 指示の無い区間は「そのまま鳴り続ける」（continue が既定）。曲は区間で途切れない
    ///   - 同じトラックを指す区間は <see cref="BgmChange.Retune"/>（再生位置を保ったままループ範囲・音量だけ更新）
    ///   - 別トラックを指す区間は <see cref="BgmChange.Start"/>（クロスフェード）
    ///   - 明示的な停止区間は <see cref="BgmChange.Stop"/>（フェードアウト → 無音）
    ///
    /// セマンティクスを変えるときは <c>Assets/Tests/Streaming/BgmPlanLogicTests.cs</c> を先に直す。
    /// </summary>
    public static class BgmPlanLogic
    {
        public const string ActionContinue = "continue";
        public const string ActionPlay = "play";
        public const string ActionStop = "stop";

        public enum BgmChange
        {
            /// <summary>何もしない（鳴っていれば鳴り続ける）。</summary>
            None,
            /// <summary>指定トラックを頭出しして鳴らす（別トラックならクロスフェード）。</summary>
            Start,
            /// <summary>同一トラック継続。再生位置は保ち、ループ範囲・音量・loop 種別だけ更新。</summary>
            Retune,
            /// <summary>フェードアウトして無音にする。</summary>
            Stop,
        }

        /// <summary>
        /// 区間進入時の遷移を決める。
        /// </summary>
        /// <param name="present">区間が BGM 指示を持つか（hasBgm）。false なら常に None。</param>
        /// <param name="action">"play" / "stop" / "continue"（未知の値は continue 扱い）。</param>
        /// <param name="trackId">play 時の対象トラック id。空なら指示不成立で None。</param>
        /// <param name="restart">同一トラックでも頭出しし直すか。</param>
        /// <param name="playing">いま BGM が鳴っているか。</param>
        /// <param name="currentTrackId">いま鳴っているトラック id（無音なら空）。</param>
        public static BgmChange Decide(bool present, string? action, string? trackId, bool restart,
                                       bool playing, string? currentTrackId)
        {
            if (!present) return BgmChange.None;
            string a = string.IsNullOrEmpty(action) ? ActionContinue : action!;
            if (a == ActionStop) return playing ? BgmChange.Stop : BgmChange.None;
            if (a != ActionPlay) return BgmChange.None;            // continue / 未知
            if (string.IsNullOrEmpty(trackId)) return BgmChange.None;
            if (!playing) return BgmChange.Start;
            if (currentTrackId == trackId) return restart ? BgmChange.Start : BgmChange.Retune;
            return BgmChange.Start;
        }

        /// <summary>
        /// 演出（Take）が音を占有した後、レーン（区間が決めている音）へ戻るときの遷移を決める。
        ///
        /// 画面の「戻り先は再計算」と対称: <b>戻り先は演出開始時のスナップショットではなく、
        /// いまレーンがどうあるべきか</b>（演出中に体験者がゾーンを移れば、その区間の指示が正）。
        /// </summary>
        /// <param name="playing">いま音が鳴っているか（＝演出の曲）。</param>
        /// <param name="currentTrackId">いま鳴っているトラック id。</param>
        /// <param name="laneTrackId">レーンが鳴らしているべきトラック id（無音なら空）。</param>
        public static BgmChange DecideRestore(bool playing, string? currentTrackId, string? laneTrackId)
        {
            bool laneSilent = string.IsNullOrEmpty(laneTrackId);
            if (laneSilent) return playing ? BgmChange.Stop : BgmChange.None;
            if (playing && currentTrackId == laneTrackId) return BgmChange.None;   // 演出が触っていない
            return BgmChange.Start;
        }

        /// <summary>
        /// ループ範囲を実クリップ長へ丸める。
        /// loopEnd &lt;= loopStart（未設定 0 を含む）なら「末尾まで」＝ clipLength。
        /// 返り値は (start, loopStart, loopEnd) の秒。すべて [0, clipLength] に収まり loopStart &lt; loopEnd を保証する。
        /// </summary>
        public static (float start, float loopStart, float loopEnd) NormalizeWindow(
            float clipLength, float startSec, float loopStartSec, float loopEndSec)
        {
            if (clipLength <= 0f) return (0f, 0f, 0f);
            float ls = Clamp(loopStartSec, 0f, clipLength);
            float le = loopEndSec <= 0f ? clipLength : Clamp(loopEndSec, 0f, clipLength);
            if (le <= ls)
            {
                // 逆転・潰れは「そこから末尾まで」に開き、最後の砦として全長へ戻す（無限ループ回避）。
                le = clipLength;
                if (le <= ls) ls = 0f;
            }
            float st = Clamp(startSec, 0f, clipLength);
            // 開始位置がループ窓の外なら窓の頭から（窓外で鳴り始めて即ジャンプする違和感を消す）。
            if (st < ls || st >= le) st = ls;
            return (st, ls, le);
        }

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
