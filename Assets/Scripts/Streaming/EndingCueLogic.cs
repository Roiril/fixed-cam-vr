#nullable enable

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>終幕の合図。</b> 「この演出が終わったら終わる」を持つ 1 ビットのラッチ
    /// （<c>canon/LEDGER.md</c> 0048・ユーザー逐語「周回リセットのときにリセットされるフラグを用意して、
    /// そのフラグで流すタイミングを制御しようと思う。なので、周回中の例えば4週目Aの指定された演出終了後に
    /// 終幕演出を流すみたいな」）。
    ///
    /// 対象は <c>show.json</c> の <c>run.outro.afterTakeId</c>。UnityEngine 非依存。
    ///
    /// <b>「走っているのを見た」を先に立ててから、「走っていない」で撃つ。</b> 演出が始まる前は
    /// <c>ActiveTakeId</c> も空なので、空だけを見ると本編に入った瞬間に撃ってしまう。
    ///
    /// ⚠ <b>これは終端そのものではない。</b> 指した演出が最後まで走らない現場
    /// （体験者が別の区間へ抜けた / カットが「次にカメラが切り替わるまで」で終わらない）でも、
    /// 従来の <c>run.endHoldMaxSec</c> の安全網がそのまま体験を終わらせる。
    /// <b>合図を足しただけで、出口は減らしていない。</b>
    ///
    /// ⚠ <b>撃つのは 1 回だけ。</b> 落ちるのは <see cref="ResetRun"/>（＝ 周回リセット）のみ。
    /// 終幕が終わって次の体験者が来るまで再武装しない。
    /// </summary>
    public sealed class EndingCueLogic
    {
        private string _afterTakeId = "";
        private bool _seen;
        private bool _fired;

        /// <summary>合図が著作されているか（空なら従来どおり周回の走り切りで終わる）。</summary>
        public bool Configured => !string.IsNullOrEmpty(_afterTakeId);

        /// <summary>
        /// 指した演出が走っているのを見たか（＝ 周回リセットで落ちるフラグ）。
        /// これが立っていないと、その演出が終わっても撃たない。
        /// </summary>
        public bool Armed => _seen;

        /// <summary>もう撃ったか。</summary>
        public bool Fired => _fired;

        /// <summary>対象の演出 id を差し替える（show.json の更新で毎回呼ばれる）。</summary>
        public void Configure(string? afterTakeId)
        {
            string id = afterTakeId ?? "";
            if (id == _afterTakeId) return;
            _afterTakeId = id;
            // 別の演出を指し直したら、前の演出で立てた武装は意味を失う。
            _seen = false;
            _fired = false;
        }

        /// <summary>周回リセット（新しい体験者）。<b>武装と発火の両方が落ちる唯一の場所。</b></summary>
        public void ResetRun()
        {
            _seen = false;
            _fired = false;
        }

        /// <summary>
        /// <b>人が演出を止めた</b>（卓の 📺 カメラ固定 / 手動 cue / ■ 画面を取り返す）。武装だけ落とす。
        ///
        /// ⚠⚠ これが無いと**終幕が早撃ちされる**（2026-08-30）。<see cref="Tick"/> は
        /// 「指した演出が走っていた ＆ いま走っていない」で撃つが、<b>走らなくなった理由を見ていない</b>。
        /// <c>TakeRunner.AbortActive</c> と <c>SetSuppressed(true)</c> はどちらも <c>CleanupActive</c> を通って
        /// <c>ActiveTakeId</c> を空にするので、**締めの演出の最中にオペレータがカメラを固定するだけで
        /// 相が Finished へ落ちる**。現行の台本（<c>L4C0#0</c>）ではそこで著作された
        /// 「現実へ戻る 4.5 秒」のカットが丸ごと飛ぶ。
        ///
        /// 落とすのは <c>_seen</c> だけ（<c>_fired</c> は触らない）。介入が解けた後にその演出が
        /// もう一度走って終われば、そのときは正しく撃つ。
        /// </summary>
        public void NotifyInterrupted()
        {
            _seen = false;
        }

        /// <summary>
        /// 毎フレーム呼ぶ。<b>true を返したフレームで終幕へ入る</b>（返すのは 1 回だけ）。
        /// </summary>
        /// <param name="inRun">本編の相か。導入・終了では武装も発火もしない。</param>
        /// <param name="activeTakeId">いま画面を握っている演出の id（走っていなければ空）。</param>
        public bool Tick(bool inRun, string? activeTakeId)
        {
            if (!Configured || _fired) return false;
            if (!inRun) return false;

            string id = activeTakeId ?? "";
            if (id == _afterTakeId)
            {
                _seen = true;
                return false;
            }
            // 指した演出が走っていた ＆ いま走っていない ＝ 終わった。
            // 次の演出へそのまま渡した（chainNext）場合も id が変わるので、ここで拾える。
            if (!_seen) return false;
            _fired = true;
            return true;
        }
    }
}
