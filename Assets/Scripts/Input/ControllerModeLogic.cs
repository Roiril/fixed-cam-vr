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
    ///     └ 右 A <see cref="_holdSec"/> 秒長押し → <see cref="RunResetRequested"/>（体験者リセット。モードは変えない）
    ///   Registration（既存の N 点登録フロー）
    ///     ├ 登録終了（確定 = registrationActive が false へ）→ Normal
    ///     └ 右トリガー <see cref="_holdSec"/> 秒長押し（キャンセル）→ Normal（入場と対称）
    ///
    /// スタッフの右手は A / B / トリガーだけを使い、誤操作しやすいグリップは読まない。
    /// 長押し検出（トリガー・A の 2 秒ホールド）は 1 回の連続ホールドで 1 回だけ発火する
    /// （<see cref="HoldLatch"/>）。
    /// ⚠ <b>A と重なったトリガーは離すまで数えない</b>。登録の A 操作や体験者リセットの最中に
    /// 人差し指が自然にトリガーへ掛かっても、位置合わせを開始・中止しない
    /// （<see cref="Frame.faceButtonHeld"/> / <see cref="FaceButtonQuietSec"/>）。
    /// registrationActive は <c>CourseRegistrationController.IsActive</c>
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
            /// <summary>スタッフ初期機器確認中など。押下を解放するまで入場を抑止する。</summary>
            public bool triggerBlocked;
            /// <summary>右 A が押されているか。Normal での長押しで体験者リセット。</summary>
            public bool resetHeld;
            /// <summary>登録フロー中か（CourseRegistrationController.IsActive の外部真実）。</summary>
            public bool registrationActive;
            /// <summary>
            /// 右の A（面のボタン）が押されているか。<b>押されているあいだ、トリガーの
            /// 長押しは数えない</b>（<see cref="FaceButtonQuietSec"/> も参照）。
            /// </summary>
            public bool faceButtonHeld;
        }

        /// <summary>
        /// A を離してから、トリガーの長押しを数え始めてよいまでの間（秒）。
        ///
        /// A で点を記録するときや体験者リセットをするとき、人差し指がトリガーへ掛かりやすい。
        /// A を離した直後までトリガーを数えないことで、位置合わせへの誤入場を防ぐ。
        /// </summary>
        public const float FaceButtonQuietSec = 0.3f;

        // 単一ボタンの「N 秒長押しを 1 ホールド 1 回だけ発火」する計時 + ラッチ。トリガー / A で各 1 個。
        private sealed class HoldLatch
        {
            private float _hold;
            private bool _fired;
            // A と重なった（またはその直後に始まった）ホールド。離すまで数えず、発火もしない。
            private bool _voided;

            /// <summary>長押しの進捗 [0,1]。無効化されたホールドは 0（鳴らさない・表示しない）。</summary>
            public float Progress01(float holdSec) => (holdSec <= 0f || _voided) ? 0f : Clamp01(_hold / holdSec);

            /// <summary>いま押されているホールドが A と重なって無効化されているか。</summary>
            public bool Voided => _voided;

            /// <summary>計時とラッチをクリアする。</summary>
            public void Reset()
            {
                _hold = 0f;
                _fired = false;
                _voided = false;
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
            /// <paramref name="blocked"/> のあいだ（A が押されている・離した直後）に押されている
            /// ホールドは<b>離すまで無効</b> — 数えず、鳴らさず、発火しない。
            /// </summary>
            public bool Tick(bool held, float dt, float holdSec, bool blocked)
            {
                if (!held)
                {
                    _hold = 0f;
                    _fired = false;
                    _voided = false;
                    return false;
                }
                if (blocked) _voided = true;
                if (_voided)
                {
                    _hold = 0f;
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
        private readonly HoldLatch _reset = new();

        private float _holdSec = 2f;

        // A を離してからの静穏期の残り（秒）。> 0 のあいだホールドは数え始めない。
        private float _faceQuiet;
        private int _voidedHolds;

        /// <summary>現在のモード。</summary>
        public Mode Current => _mode;

        /// <summary>右トリガー長押しの進捗 [0,1]（HUD 表示等の任意用途）。無効化中は 0。</summary>
        public float TriggerHoldProgress01 => _trigger.Progress01(_holdSec);

        /// <summary>Normal の右 A 長押しの進捗 [0,1]（HUD 表示等の任意用途）。</summary>
        public float ResetHoldProgress01 => _reset.Progress01(_holdSec);

        /// <summary>
        /// A と重なって無効化されたトリガーホールドの累計。
        /// Bridge が増分を見てログを出す（振動は画にも音にも出ないので、ここが唯一の手掛かり）。
        /// </summary>
        public int VoidedHolds => _voidedHolds;

        /// <summary>いま押されているトリガーが A と重なって無効化されているか。</summary>
        public bool TriggerHoldVoided => _trigger.Voided;

        /// <summary>モードが変わった時に (from, to) で発火する。副作用（登録開始/停止等）は購読側で行う。</summary>
        public event Action<Mode, Mode>? ModeChanged;

        /// <summary>Normal で右 A 長押しが完了した時に発火（体験者リセット要求）。モードは変わらない。</summary>
        public event Action? RunResetRequested;

        /// <summary>長押し閾値（秒）を設定する。</summary>
        public void Configure(float holdSec) => _holdSec = Max0(holdSec);

        /// <summary>状態を初期化する（既定は Normal）。計時・ラッチもクリアする。</summary>
        public void Reset(Mode mode = Mode.Normal)
        {
            _mode = mode;
            _prevRegActive = false;
            _trigger.Reset();
            _reset.Reset();
            _faceQuiet = 0f;
            _voidedHolds = 0;
        }

        /// <summary>現在のモードを保ったまま、進行中の長押しと静穏期だけを破棄する。</summary>
        public void DiscardHolds()
        {
            _trigger.Reset();
            _reset.Reset();
            _faceQuiet = 0f;
        }

        /// <summary>毎フレームの評価。入力からモード遷移・ランリセット要求を決める。</summary>
        public void Tick(in Frame f)
        {
            // A が押されているあいだと、離してから FaceButtonQuietSec のあいだは、
            // トリガーのホールドを指が自然に掛かったものとみなして数えない。
            if (f.faceButtonHeld) _faceQuiet = FaceButtonQuietSec;
            else if (_faceQuiet > 0f) _faceQuiet = Max0(_faceQuiet - f.deltaTime);
            bool blocked = f.faceButtonHeld || _faceQuiet > 0f || f.triggerBlocked;

            bool wasVoided = _trigger.Voided;
            bool triggerFired = _trigger.Tick(f.triggerHeld, f.deltaTime, _holdSec, blocked);
            if (!wasVoided && _trigger.Voided) _voidedHolds++;

            // Registration の A は点サンプル専用。押したまま Normal へ戻っても、いったん離すまで
            // 体験者リセットの計時へ持ち越さない。
            bool resetFired = false;
            if (_mode == Mode.Normal)
                resetFired = _reset.Tick(f.resetHeld, f.deltaTime, _holdSec, blocked: false);
            else
                _reset.Tick(f.resetHeld, f.deltaTime, _holdSec, blocked: true);

            switch (_mode)
            {
                case Mode.Normal:
                    // 外部から登録が始まった（startInRegistration 等）場合は Registration へ追従。
                    // 立ち上がりエッジ限定：キャンセル直後に外部状態がまだ true でも引き戻さない
                    // （停止の副作用は Bridge が ModeChanged で行うため 1 フレーム遅れる）。
                    if (f.registrationActive && !_prevRegActive) { SetMode(Mode.Registration); break; }
                    if (triggerFired) { SetMode(Mode.Registration); break; }
                    // 右 A 長押し = 体験者リセット（モードは変えない）。
                    if (resetFired) RunResetRequested?.Invoke();
                    break;

                case Mode.Registration:
                    // 出口は「トリガー長押し（キャンセル）」か「登録が確定して IsActive=false」。
                    if (triggerFired) { SetMode(Mode.Normal); break; }
                    if (!f.registrationActive) SetMode(Mode.Normal);
                    break;
            }

            _prevRegActive = f.registrationActive;
        }

        private void SetMode(Mode next)
        {
            if (_mode == next) return;
            Mode prev = _mode;
            _mode = next;
            // モード遷移に使ったトリガーは消費し、離すまで逆向きの遷移を発火させない。
            // A は Registration 側の Tick が持ち越しを無効化するため、ここで消費しない。
            _trigger.Consume();
            ModeChanged?.Invoke(prev, next);
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
        private static float Max0(float v) => v < 0f ? 0f : v;
    }
}
