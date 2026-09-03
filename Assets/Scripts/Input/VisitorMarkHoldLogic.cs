#nullable enable

namespace FixedCamVr.Input
{
    /// <summary>
    /// <b>体験者の報告ボタン（左のどれか）の長押し</b>を数える純ロジック。
    /// UnityEngine / OVRInput へ一切依存せず、入力と経過時間だけを <see cref="Tick"/> で受ける。
    ///
    /// 判定は <c>canon/LEDGER.md</c> 0050（ユーザー逐語）:
    /// 「位置合わせの時の A ボタンみたいに、こっちは長押し 2s で報告できるように、
    /// かつ、長押し中の表示も位置合わせの時みたいにつけてください」。
    /// ⚠ <b>2026-08-16 に半分（1.0 秒）へ短くした</b>（0059・「異常を検出するのに必要な
    /// 長押し時間をいまの 1/2 に」）。
    ///
    /// ⚠ <b>短押しでは発火しない。</b> 体験者は歩きながら握り込むので、押した瞬間に決まると
    /// 「触れただけ」が報告になる。位置合わせの点サンプル（0.5 秒ホールド）と同じ形で、
    /// <b>意思のある長さ</b>だけを取る。
    ///
    /// ⚠ <b>1 回の押しで 1 回だけ。</b> 押しっぱなしにしても 2 回目は出ない（離すまでラッチ）。
    /// <c>ControllerModeLogic</c> の長押し（トリガー / グリップ）と同じ約束。
    ///
    /// ⚠ <b>1 フレームの dt は <see cref="MaxStepSec"/> で切る。</b> 起動直後・復帰直後・
    /// ドメインリロードで dt が数秒飛ぶことがあり、そのフレームに握っていると
    /// <b>押した瞬間に閾値ぶんが積まれて発火する</b>。同じ不連続ガードを
    /// <c>LineCrossLogic</c> / <c>StartSpotLogic</c> / <c>ApproachLogic</c> も持っている。
    /// </summary>
    public sealed class VisitorMarkHoldLogic
    {
        /// <summary>
        /// 長押しの閾値 (秒)。<b>2026-08-16 に 2.0 → 1.0</b>（<c>canon/LEDGER.md</c> 0058・
        /// ユーザー指定「いまの 1/2 に」）。
        ///
        /// ⚠ <b>スタッフ側の長押し（`ControllerModeLogic.LongPressSec` = 2 秒）とは別物になった。</b>
        /// 揃える理由はもう無い — あちらは誤操作したら体験が壊れる操作（ランリセット・位置合わせ）で、
        /// こちらは押さなくても体験が進む記録。**片方を変えても、もう片方は追随しない。**
        /// ⚠ 下げすぎると「歩きながら握り込んだだけ」が報告になる。0.5 秒より短くしない。
        /// </summary>
        public const float DefaultHoldSec = 1.0f;

        /// <summary>1 フレームで積める上限 (秒)。これを超える dt は切り詰める。</summary>
        public const float MaxStepSec = 0.25f;

        /// <summary>発火のあと「報告しました」を出しておく時間 (秒)。</summary>
        public const float DefaultConfirmSec = 1.2f;

        private float _holdSec = DefaultHoldSec;
        private float _confirmSec = DefaultConfirmSec;

        private float _elapsed;      // いまのホールドの積算 (秒)
        private bool _consumed;      // このホールドは発火済み（離すまで再発火しない）
        private float _confirmLeft;  // 「報告しました」の残り時間 (秒)

        /// <summary>長押しの閾値 (秒)。</summary>
        public float HoldSec => _holdSec;

        /// <summary>いまのホールドの積算 (秒)。離すと 0。</summary>
        public float ElapsedSec => _elapsed;

        /// <summary>長押しの進捗 [0,1]。離すと 0 に戻る（＝ゲージも戻る）。</summary>
        public float Progress01 => _holdSec <= 0f ? 0f : Clamp01(_elapsed / _holdSec);

        /// <summary>いま押している最中か（発火後もボタンを離すまで true）。</summary>
        public bool Holding => _elapsed > 0f;

        /// <summary>発火直後の余韻（「報告しました」を出している間）。</summary>
        public bool Confirming => _confirmLeft > 0f;

        /// <summary>閾値と余韻の長さを設定する。</summary>
        public void Configure(float holdSec, float confirmSec = DefaultConfirmSec)
        {
            _holdSec = Max0(holdSec);
            _confirmSec = Max0(confirmSec);
        }

        /// <summary>計時・ラッチ・余韻をすべて落とす（体験者の交代など）。</summary>
        public void Reset()
        {
            _elapsed = 0f;
            _consumed = false;
            _confirmLeft = 0f;
        }

        /// <summary>
        /// 1 フレーム進める。閾値へ達した最初の 1 フレームだけ true を返す。
        /// </summary>
        /// <param name="dt">このフレームの経過時間 (秒)。<see cref="MaxStepSec"/> で切られる。</param>
        /// <param name="held">左のボタンがどれか 1 つでも押されているか（一覧は
        /// <c>OvrControllerBridge.LeftAnyButtons</c>。2026-09-03 に X／Y から左の全ボタンへ広げた）。</param>
        public bool Tick(float dt, bool held)
        {
            float step = dt < 0f ? 0f : (dt > MaxStepSec ? MaxStepSec : dt);

            if (_confirmLeft > 0f)
            {
                _confirmLeft -= step;
                if (_confirmLeft < 0f) _confirmLeft = 0f;
            }

            if (!held)
            {
                _elapsed = 0f;
                _consumed = false;
                return false;
            }

            _elapsed += step;
            if (_consumed || _elapsed < _holdSec) return false;

            _consumed = true;
            _confirmLeft = _confirmSec;
            return true;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
        private static float Max0(float v) => v < 0f ? 0f : v;
    }
}
