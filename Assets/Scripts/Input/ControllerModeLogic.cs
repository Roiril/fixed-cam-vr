#nullable enable
using System;

namespace FixedCamVr.Input
{
    /// <summary>
    /// コントローラの操作モード（Run / Staff / Registration）を管理する純ロジック。
    /// MonoBehaviour（OvrControllerBridge）から分離して EditMode テスト可能にする。時刻・入力は
    /// すべて <see cref="Tick"/> の引数で受け、UnityEngine / OVRInput へ一切依存しない。
    ///
    /// モードモデル（計画 2026-07-19_controller-roles.md）:
    ///   Run（既定・ゲスト安全。両グリップ 3 秒の Staff 入口以外すべて不活性）
    ///     └ 両グリップ 3 秒長押し → Staff
    ///   Staff（スタッフ操作可能・チートシート表示）
    ///     ├ 右スティック押し込み → Registration
    ///     ├ 無操作 <see cref="_staffIdleTimeoutSec"/> 秒 → Run
    ///     └ 両グリップ 3 秒長押し → Run
    ///   Registration（既存の 2 点登録フロー）
    ///     ├ 登録終了（確定 = registrationActive が false へ）→ Staff
    ///     └ 両グリップ 3 秒長押し（キャンセル）→ Staff
    ///
    /// 両グリップ 3 秒儀式は「1 回の連続ホールドで 1 回だけ発火」し、Run⇄Staff の相互切替と
    /// Registration キャンセルの共通トリガとして働く（発火の意味はモードで決まる）。
    /// registrationActive は <c>CourseRegistrationController.IsActive</c>（外部の真実）を渡す。
    /// 登録の開始・停止という副作用は Bridge が <see cref="ModeChanged"/> を受けて行う
    /// （このロジックはモード遷移だけを決める）。
    /// </summary>
    public sealed class ControllerModeLogic
    {
        public enum Mode { Run, Staff, Registration }

        /// <summary>Bridge から毎フレーム渡す入力。すべて bool / float でテスト可能。</summary>
        public struct Frame
        {
            /// <summary>このフレームの経過時間 (秒)。</summary>
            public float deltaTime;
            /// <summary>両手グリップ（PrimaryHandTrigger）が同時に押されているか。</summary>
            public bool bothGrips;
            /// <summary>登録フロー中か（CourseRegistrationController.IsActive の外部真実）。</summary>
            public bool registrationActive;
            /// <summary>右スティック押し込みのエッジ（Staff → Registration の入口）。</summary>
            public bool stickPressDown;
            /// <summary>Staff 中の何らかの操作入力があったか（無操作タイムアウトのリセット用）。</summary>
            public bool staffActivity;
        }

        private Mode _mode = Mode.Run;

        // 両グリップ長押しの計時とラッチ（連続ホールド 1 回で 1 回だけ発火させる）。
        private float _gripHold;
        private bool _gripFired;

        // Staff 無操作の計時（活動があれば 0 に戻す）。
        private float _idle;

        private float _gripHoldSec = 3f;
        private float _staffIdleTimeoutSec = 120f;

        /// <summary>現在のモード。</summary>
        public Mode Current => _mode;

        /// <summary>両グリップ長押しの進捗 [0,1]（HUD 表示等の任意用途）。</summary>
        public float GripHoldProgress01
            => _gripHoldSec <= 0f ? 0f : Clamp01(_gripHold / _gripHoldSec);

        /// <summary>Staff 無操作の残り秒（Run へ戻るまで）。Staff 以外では意味を持たない。</summary>
        public float StaffIdleRemainingSec => Max0(_staffIdleTimeoutSec - _idle);

        /// <summary>モードが変わった時に (from, to) で発火する。副作用（HUD / 登録開始等）は購読側で行う。</summary>
        public event Action<Mode, Mode>? ModeChanged;

        /// <summary>ホールド秒・無操作タイムアウト秒を設定する。</summary>
        public void Configure(float gripHoldSec, float staffIdleTimeoutSec)
        {
            _gripHoldSec = Max0(gripHoldSec);
            _staffIdleTimeoutSec = Max0(staffIdleTimeoutSec);
        }

        /// <summary>状態を初期化する（既定は Run）。計時・ラッチもクリアする。</summary>
        public void Reset(Mode mode = Mode.Run)
        {
            _mode = mode;
            _gripHold = 0f;
            _gripFired = false;
            _idle = 0f;
        }

        /// <summary>毎フレームの評価。入力からモード遷移を決める（副作用は起こさない）。</summary>
        public void Tick(in Frame f)
        {
            // 両グリップ 3 秒儀式（全モード共通の計時。ホールド 1 回につき 1 回発火）。
            bool gripFired = false;
            if (f.bothGrips)
            {
                _gripHold += f.deltaTime;
                if (!_gripFired && _gripHold >= _gripHoldSec)
                {
                    _gripFired = true;
                    gripFired = true;
                }
            }
            else
            {
                _gripHold = 0f;
                _gripFired = false;
            }

            switch (_mode)
            {
                case Mode.Run:
                    // Run は封印。唯一の出口が両グリップ 3 秒儀式（→ Staff）。
                    if (gripFired) SetMode(Mode.Staff);
                    break;

                case Mode.Staff:
                    // 活動があれば無操作計時をリセット（両グリップ操作中も活動扱い）。
                    if (f.staffActivity || f.bothGrips) _idle = 0f;
                    else _idle += f.deltaTime;

                    if (gripFired) { SetMode(Mode.Run); break; }
                    // 外部から登録が始まった（startInRegistration 等）場合も Registration へ追従。
                    if (f.registrationActive) { SetMode(Mode.Registration); break; }
                    if (f.stickPressDown) { SetMode(Mode.Registration); break; }
                    if (_idle >= _staffIdleTimeoutSec) SetMode(Mode.Run);
                    break;

                case Mode.Registration:
                    // 登録は自身のフロー（A=マーク / B=確定 / スティック微調整）で進む。
                    // 出口は「両グリップ儀式（キャンセル）」か「登録が確定して IsActive=false」。
                    if (gripFired) { SetMode(Mode.Staff); break; }
                    if (!f.registrationActive) SetMode(Mode.Staff);
                    break;
            }
        }

        private void SetMode(Mode next)
        {
            if (_mode == next) return;
            Mode prev = _mode;
            _mode = next;
            _idle = 0f; // どのモードへ移っても Staff 無操作計時は仕切り直す
            ModeChanged?.Invoke(prev, next);
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
        private static float Max0(float v) => v < 0f ? 0f : v;
    }
}
