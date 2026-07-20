#nullable enable
using System;

namespace FixedCamVr.Input
{
    /// <summary>
    /// コントローラの操作モード（Normal / Registration）を管理する純ロジック。
    /// MonoBehaviour（OvrControllerBridge）から分離して EditMode テスト可能にする。時刻・入力は
    /// すべて <see cref="Tick"/> の引数で受け、UnityEngine / OVRInput へ一切依存しない。
    ///
    /// モードモデル（計画 2026-07-20_staff-input-hud-redesign.md）:
    ///   Normal（既定）
    ///     ├ 右トリガー <see cref="_holdSec"/> 秒長押し → Registration（位置合わせ入場）
    ///     └ 右グリップ <see cref="_holdSec"/> 秒長押し → <see cref="RunResetRequested"/>（ランリセット。モードは変えない）
    ///   Registration（既存の N 点登録フロー）
    ///     ├ 登録終了（確定 = registrationActive が false へ）→ Normal
    ///     └ 右トリガー <see cref="_holdSec"/> 秒長押し（キャンセル）→ Normal（入場と対称）
    ///
    /// 体験者はコントローラを持たないため封印モード（旧 Run/Staff）は廃止した。右手 4 入力
    /// （A / B / グリップ / トリガー）だけで全操作を賄い、モード遷移はトリガー長押しに集約する。
    /// 長押し検出（トリガー・グリップの 2 秒ホールド）は 1 回の連続ホールドで 1 回だけ発火する
    /// （<see cref="HoldLatch"/>）。registrationActive は <c>CourseRegistrationController.IsActive</c>
    /// （外部の真実）を渡す。登録の開始・停止という副作用は Bridge が <see cref="ModeChanged"/> を
    /// 受けて行う（このロジックはモード遷移とランリセット要求だけを決める）。
    /// </summary>
    public sealed class ControllerModeLogic
    {
        public enum Mode { Normal, Registration }

        /// <summary>Bridge から毎フレーム渡す入力。すべて bool / float でテスト可能。</summary>
        public struct Frame
        {
            /// <summary>このフレームの経過時間 (秒)。</summary>
            public float deltaTime;
            /// <summary>右トリガー（PrimaryIndexTrigger）が押されているか。長押しで Registration 入場 / キャンセル。</summary>
            public bool triggerHeld;
            /// <summary>右グリップ（PrimaryHandTrigger）が押されているか。Normal での長押しでランリセット。</summary>
            public bool gripHeld;
            /// <summary>登録フロー中か（CourseRegistrationController.IsActive の外部真実）。</summary>
            public bool registrationActive;
        }

        // 単一ボタンの「N 秒長押しを 1 ホールド 1 回だけ発火」する計時 + ラッチ。トリガー / グリップで各 1 個。
        private sealed class HoldLatch
        {
            private float _hold;
            private bool _fired;

            /// <summary>長押しの進捗 [0,1]。</summary>
            public float Progress01(float holdSec) => holdSec <= 0f ? 0f : Clamp01(_hold / holdSec);

            /// <summary>計時とラッチをクリアする。</summary>
            public void Reset()
            {
                _hold = 0f;
                _fired = false;
            }

            /// <summary>
            /// 進行中のホールドを消費済みにする（離すまで発火しない）。モード遷移が起きたフレームで
            /// 呼ぶことで、1 回の連続ホールドが遷移をまたいで二重に作用する（例: 追従入場した直後に
            /// 同じホールドがキャンセルとして発火する）のを防ぐ。
            /// </summary>
            public void Consume() => _fired = true;

            /// <summary>
            /// 押下状態を進める。ホールドが holdSec に達した最初の 1 フレームだけ true を返す
            /// （離すまで再発火しない。離したら計時とラッチをリセット）。
            /// </summary>
            public bool Tick(bool held, float dt, float holdSec)
            {
                if (!held)
                {
                    _hold = 0f;
                    _fired = false;
                    return false;
                }
                _hold += dt;
                if (!_fired && _hold >= holdSec)
                {
                    _fired = true;
                    return true;
                }
                return false;
            }
        }

        private Mode _mode = Mode.Normal;
        private bool _prevRegActive;

        private readonly HoldLatch _trigger = new();
        private readonly HoldLatch _grip = new();

        private float _holdSec = 2f;

        /// <summary>現在のモード。</summary>
        public Mode Current => _mode;

        /// <summary>右トリガー長押しの進捗 [0,1]（HUD 表示等の任意用途）。</summary>
        public float TriggerHoldProgress01 => _trigger.Progress01(_holdSec);

        /// <summary>右グリップ長押しの進捗 [0,1]（HUD 表示等の任意用途）。</summary>
        public float GripHoldProgress01 => _grip.Progress01(_holdSec);

        /// <summary>モードが変わった時に (from, to) で発火する。副作用（登録開始/停止等）は購読側で行う。</summary>
        public event Action<Mode, Mode>? ModeChanged;

        /// <summary>Normal でグリップ長押しが完了した時に発火（ランリセット要求）。モードは変わらない。</summary>
        public event Action? RunResetRequested;

        /// <summary>長押し閾値（秒）を設定する。</summary>
        public void Configure(float holdSec) => _holdSec = Max0(holdSec);

        /// <summary>状態を初期化する（既定は Normal）。計時・ラッチもクリアする。</summary>
        public void Reset(Mode mode = Mode.Normal)
        {
            _mode = mode;
            _prevRegActive = false;
            _trigger.Reset();
            _grip.Reset();
        }

        /// <summary>毎フレームの評価。入力からモード遷移・ランリセット要求を決める。</summary>
        public void Tick(in Frame f)
        {
            bool triggerFired = _trigger.Tick(f.triggerHeld, f.deltaTime, _holdSec);
            bool gripFired = _grip.Tick(f.gripHeld, f.deltaTime, _holdSec);

            switch (_mode)
            {
                case Mode.Normal:
                    // 外部から登録が始まった（startInRegistration 等）場合は Registration へ追従。
                    // 立ち上がりエッジ限定：キャンセル直後に外部状態がまだ true でも引き戻さない
                    // （停止の副作用は Bridge が ModeChanged で行うため 1 フレーム遅れる）。
                    if (f.registrationActive && !_prevRegActive) { SetMode(Mode.Registration); break; }
                    if (triggerFired) { SetMode(Mode.Registration); break; }
                    // グリップ長押し = ランリセット（モードは変えない）。
                    if (gripFired) RunResetRequested?.Invoke();
                    break;

                case Mode.Registration:
                    // 出口は「トリガー長押し（キャンセル）」か「登録が確定して IsActive=false」。
                    if (triggerFired) { SetMode(Mode.Normal); break; }
                    if (!f.registrationActive) SetMode(Mode.Normal);
                    // グリップは Registration では未使用（予備）。
                    break;
            }

            _prevRegActive = f.registrationActive;
        }

        private void SetMode(Mode next)
        {
            if (_mode == next) return;
            Mode prev = _mode;
            _mode = next;
            // 遷移を起こした（または遷移中に押しっぱなしだった）ホールドは消費し、
            // 離すまで次の遷移を発火させない（1 ホールド 1 作用）。
            _trigger.Consume();
            _grip.Consume();
            ModeChanged?.Invoke(prev, next);
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
        private static float Max0(float v) => v < 0f ? 0f : v;
    }
}
