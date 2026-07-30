#nullable enable

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// CameraStream の受信健全性判定（suspend / resume-gap / stall / lag / decode-fail）を
    /// UnityEngine 非依存の純ロジックとして切り出したもの。時刻・dt はすべて引数注入する。
    /// 既存の YawFollowLogic / SwitchDirectorLogic と同じ流儀で EditMode から決定的に駆動できる。
    ///
    /// A1（resume-gap で suspend 解除）/ A2（stall watchdog が LastFrameRealtime を汚さない）を
    /// このクラス単体で回帰固定するために、判定に関わる状態と定数をすべてここへ集約する。
    /// </summary>
    public sealed class StreamWatchdogLogic
    {
        // Lag 検出（PHONE_FPS に対して RECV_FPS が一定割合を下回る状態が連続したら再接続）。
        public const float LagDetectWindowSec = 1.5f;
        public const float LagThresholdRatio = 0.7f;

        /// <summary>
        /// lag 判定で分母に使う「受信側が必要とする fps」の上限。
        ///
        /// 配信側は <c>AE_TARGET_FPS_RANGE=[30,60]</c> なので、明るい場所では 60fps に張り付く。
        /// 素の比（<c>recv/phone</c>）で測ると、**受信 40fps という十分な品質でも ratio=0.67 で誤爆**し、
        /// 再接続がフレームを落としてさらに受信を下げる正のフィードバックに入る
        /// （2026-07-30 の実機テスト: 69 秒の本編で再接続 152 回・取りこぼし 9,000 フレーム超。
        ///  `lag detected (recv=39.8/phone=59.4)` のような明らかな誤爆がログに並んだ）。
        ///
        /// 受信側が要るのは 30fps 程度なので、分母をここで頭打ちにする。配信がそれ未満（暗所で
        /// 15fps へ落ちた等）のときは従来どおり配信 fps を分母にする ＝ 本物の詰まりは今までどおり拾う。
        /// </summary>
        public const float LagReferenceFps = 30f;
        public const float LagReconnectCooldownSec = 5.0f;
        // この秒数を超える unscaledDeltaTime は「フリーズ明け（HMD 着脱 / OS pause）」とみなす。
        // Registry も resume-gap ラッチ自己回復で参照するため public 必須。
        public const float ResumeGapSec = 0.5f;
        // 「接続は張れている風なのに受信 0」(half-open socket 等) を強制再接続で自己修復する閾値。
        public const float StallReconnectSec = 10f;
        // 壊れ JPEG が連続すると LoadImage が false を返し texture 未更新になる。閾値超で強制再接続。
        public const int DecodeFailReconnectCount = 30;  // 連続 30 枚（~1s @30fps）
        public const float DecodeFailReconnectSec = 2f;   // または 2 秒相当（低 fps 用）

        public enum ReconnectReason { None, Lag, Stall, DecodeFail }

        // HMD を外す / システムメニュー等で app が pause された間は true。
        private bool _suspended;

        // 受信側 (Unity) の実 fps 計測。1 秒ウィンドウで decode 成功回数を数える。
        private float _recvWindowStart;
        private int _recvFramesInWindow;
        private float _recvFps;

        private float _lagWindowAccum;
        private float _lastReconnectTime;
        private float _lastFrameTime;

        private int _decodeFailStreak;
        private float _decodeFailSince;

        /// <summary>HMD 着脱 / OS pause で凍結中か。DiscoveryClient は suspend 中を「フレーム断」に数えない。</summary>
        public bool IsSuspended => _suspended;

        /// <summary>Unity 受信側の実 fps（直近 1 秒の decode 成功回数）。</summary>
        public float ReceivedFps => _recvFps;

        /// <summary>最後に decode したフレームの realtimeSinceStartup。未受信 / リセット直後は 0。</summary>
        public float LastFrameRealtime => _lastFrameTime;

        /// <summary>
        /// suspend 状態を変更する。無変化なら false（呼び手はログを出さない）。
        /// resume（suspended==false）では計測ウィンドウを now でリセットする。
        /// </summary>
        public bool SetSuspended(bool suspended, float now)
        {
            if (_suspended == suspended) return false;
            _suspended = suspended;
            if (!suspended) ResetWindows(now);
            return true;
        }

        /// <summary>
        /// resume-gap 検知（フレーム消費前に呼ぶ）。巨大 unscaledDeltaTime を「フリーズ明け」とみなし、
        /// 計測ウィンドウをリセットして suspend を解除する（OS resume コールバック非依存の A1 中核修正）。
        /// true を返したら呼び手はこのフレームをスキップする。
        /// </summary>
        public bool BeginTick(float now, float unscaledDt)
        {
            if (unscaledDt > ResumeGapSec)
            {
                ResetWindows(now);
                _lastFrameTime = now;
                _suspended = false; // resume-gap は resume 信号。suspend ラッチをここで解除する。
                return true;
            }
            return false;
        }

        /// <summary>LoadImage 成功時。decode-fail streak をクリアし受信統計を進める。</summary>
        public void OnFrameDecoded(float now)
        {
            _decodeFailStreak = 0;
            _recvFramesInWindow++;
            _lastFrameTime = now;
        }

        /// <summary>
        /// LoadImage 失敗時。連続失敗が枚数 or 時間の閾値を超え、かつ cooldown 明けなら true（要再接続）。
        /// true 返却時は streak をクリアし _lastReconnectTime を更新する。
        /// </summary>
        public bool OnFrameDecodeFailed(float now, out int streak, out float sinceSec)
        {
            if (_decodeFailStreak == 0) _decodeFailSince = now;
            _decodeFailStreak++;
            streak = _decodeFailStreak;
            sinceSec = now - _decodeFailSince;

            bool over = _decodeFailStreak >= DecodeFailReconnectCount || sinceSec >= DecodeFailReconnectSec;
            if (over && now - _lastReconnectTime >= LagReconnectCooldownSec)
            {
                _lastReconnectTime = now;
                _decodeFailStreak = 0;
                return true;
            }
            return false;
        }

        /// <summary>
        /// recv-fps 窓ロール + lag + stall watchdog。要再接続の理由を返す。
        /// stall は _lastFrameTime を書き換えない（A2 中核。SignalLostFx の砂嵐が消灯しないため）。
        /// </summary>
        public ReconnectReason EndTick(float now, float unscaledDt, float phoneFps)
            => EndTick(now, unscaledDt, phoneFps, sourceThrottling: false);

        /// <summary>
        /// recv-fps 窓ロール + lag + stall watchdog。
        /// <paramref name="sourceThrottling"/> が true（配信側が熱で fps・画質を自動降格中）のときは
        /// <b>lag 判定を行わない</b>。熱で落ちた fps は経路の詰まりではないので、張り直しても直らないどころか
        /// 黒 / 砂嵐が出たうえに再接続の負荷でさらに熱が上がる（5 秒ごとの再接続ループになる）。
        /// stall は熱でも「本当にフレームが来ていない」ので従来どおり効かせる。
        /// </summary>
        public ReconnectReason EndTick(float now, float unscaledDt, float phoneFps, bool sourceThrottling)
        {
            // lag 判定の前提（phoneFps>1）を外し、溜まった窓も捨てる
            // （捨てないと熱が引いた瞬間に古い蓄積で即再接続する）。
            if (sourceThrottling) { phoneFps = 0f; _lagWindowAccum = 0f; }
            // 1. 受信 fps 計測（1 秒ウィンドウ）
            if (_recvWindowStart == 0f) _recvWindowStart = now;
            if (now - _recvWindowStart >= 1f)
            {
                _recvFps = _recvFramesInWindow / (now - _recvWindowStart);
                _recvWindowStart = now;
                _recvFramesInWindow = 0;
            }

            // 2. Lag 検出。PHONE_FPS と RECV_FPS が両方読めるときのみ評価。
            if (phoneFps > 1f && _recvFps > 0f)
            {
                float reference = phoneFps < LagReferenceFps ? phoneFps : LagReferenceFps;
                float ratio = _recvFps / reference;
                if (ratio < LagThresholdRatio)
                {
                    _lagWindowAccum += unscaledDt;
                    if (_lagWindowAccum >= LagDetectWindowSec
                        && now - _lastReconnectTime >= LagReconnectCooldownSec)
                    {
                        _lastReconnectTime = now;
                        _lagWindowAccum = 0f;
                        return ReconnectReason.Lag;
                    }
                }
                else
                {
                    _lagWindowAccum = 0f;
                }
            }

            // 3. Stall watchdog（A2 修正版）: 無フレームが StallReconnectSec 続いたら強制再接続。
            // suspend 中は表示を止めているだけなので発火させない。初回はここで基準時刻をシード。
            // 再発火ゲートは StallReconnectSec（元の 10s 周期を保つ。cooldown に落とすと 5s 周期になる）。
            if (_lastFrameTime == 0f) _lastFrameTime = now;
            if (!_suspended
                && now - _lastFrameTime >= StallReconnectSec
                && now - _lastReconnectTime >= StallReconnectSec)
            {
                _lastReconnectTime = now;
                return ReconnectReason.Stall; // _lastFrameTime は書き換えない（A2）。
            }

            return ReconnectReason.None;
        }

        /// <summary>
        /// エンドポイント差し替え時に計測ウィンドウをリセットする（ReapplyConnection から使う）。
        /// _lastFrameTime は変更しない（stall 判定の基準を接続張り替えで無効化しないため）。
        /// </summary>
        public void NotifyEndpointChanged(float now) => ResetWindows(now);

        private void ResetWindows(float now)
        {
            _recvWindowStart = 0f;
            _recvFramesInWindow = 0;
            _recvFps = 0f;
            _lagWindowAccum = 0f;
            _lastReconnectTime = now;
        }
    }
}
