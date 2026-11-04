#nullable enable
using FixedCamVr.Diagnostics;
using FixedCamVr.Input;
using FixedCamVr.Streaming;
using FixedCamVr.Streaming.Replay;
using FixedCamVr.Tracking;
using UnityEngine;

namespace FixedCamVr.OvrBridge
{
    // OvrControllerBridge の一部: 現地の「新しい体験者」の号令（右 A 2 秒長押し・タブレットの次の体験者・強制リセット）。
    // 全体の入口とファイルの一覧は OvrControllerBridge.cs の先頭。
    public sealed partial class OvrControllerBridge
    {
        // タブレットの「次の体験者」。位置が確定し、通常モードで、体験の途中でも終幕中でもないときだけ受ける。
        private bool TryResetVisitorFromTablet()
        {
            if (_staffSetup == null || !_staffSetup.Logic.PositionConfirmed
                || _modeLogic.Current != ControllerModeLogic.Mode.Normal
                || (courseRegistration != null && courseRegistration.IsActive)) return false;
            var run = FindObjectOfType<ShowRunDirector>();
            var outro = FindObjectOfType<OutroDirector>();
            bool runFinished = run != null && run.Phase == ShowPhase.Finished;
            bool outroPlaying = outro != null && outro.Stage != OutroStage.Off && outro.Stage != OutroStage.Done;
            if (!StaffSetupLogic.CanResetFromTablet(_staffSetup.Logic.Started, runFinished, outroPlaying)) return false;
            ResetVisitor("タブレットの次の体験者");
            return _staffSetup.Logic.VisitorStage == VisitorPreparationStage.SettingsRequired;
        }

        /// <summary>
        /// タブレットの強制リセット。体験の途中・終幕中・位置がずれた後でも次の体験者へ戻す
        /// （右 A 2 秒長押しと同じ処理。HMD の右コントローラが手元に無いときの出口）。
        /// 位置合わせの作業中だけは拒否する（スタッフが HMD で点を採っている最中に画を奪わない）。
        /// </summary>
        private bool TryForceResetFromTablet()
        {
            if (_modeLogic.Current != ControllerModeLogic.Mode.Normal
                || (courseRegistration != null && courseRegistration.IsActive)) return false;
            ResetVisitor("タブレットの強制リセット");
            return true;
        }

        private void OnRunResetRequested() => ResetVisitor("右 A 2 秒長押し");

        /// <summary>
        /// 現地の「新しい体験者」の号令。入口は 3 つ（右 A 2 秒長押し・タブレットの次の体験者・タブレットの強制リセット）で、
        /// <paramref name="origin"/> はどれから来たかをログに出すためのもの。体験 1 回ぶんの状態を落とす並びそのものは
        /// <c>ShowControlClient.ResetForNewVisitor</c>。ここは HMD 側の導入・長押し・準備画面を戻す。
        /// </summary>
        private void ResetVisitor(string origin)
        {
            // **号令元は ShowControlClient 1 か所に寄せる**（卓の ▶ ラン開始と同じ経路）。
            // 個別に叩いていた旧実装は、実測滞在が前の体験者の分と混ざる・体験の骨格（相）が
            // 戻らない、という非対称を持っていた。
            var show = FindObjectOfType<ShowControlClient>();
            int previousTitleSequence = titleScreen != null ? titleScreen.Sequence : -1;
            if (show?.Portal != null) show.Portal.BeginVisitorSession();
            else VisitorPrefs.BeginVisitor();
            if (show != null)
            {
                show.BeginNewVisitorRunLocal(origin);
            }
            else
            {
                // ShowControlClient が居ないシーン（診断用の最小構成など）向けのフォールバック。
                // ⚠ 本番の Main シーンでは通らない。報告数と滞在は ShowControlClient が持つので、ここでは落とせない
                //   （落とす相手が居ない）。本番の並びは ShowControlClient.ResetForNewVisitor。
                // 走行中の演出を先に畳む（不変条件 8）。これが無いと、演出が画面を凍結したまま周回だけ
                // リセットされ、卓が無い現場では watchdog（45s）まで出口が無くなる。
                FindObjectOfType<TimelineDirector>()?.ResetRun();
                FindObjectOfType<FixedCamVr.Streaming.Recording.SegmentRecorder>()?.ResetRunLocal();
                if (lapCounter != null) lapCounter.ResetRun();
                else cueScheduler?.ResetRun();
                FindObjectOfType<BgmDirector>()?.ResetRun();
                FindObjectOfType<ShowSoundDirector>()?.ResetRun();
                FindObjectOfType<ShowRunDirector>()?.BeginRun();
            }
            // 体験者が代わるので、進行中の報告の長押しと余韻も落とす。
            // 実リセット完了を確認。通常のTitle.Sequence通知と二重に新訪問を作らない。
            if (titleScreen != null && titleScreen.Sequence != previousTitleSequence && titleScreen.Stage == TitleStage.Wait)
            {
                _titleSequence = titleScreen.Sequence;
                _onboarding.Reset();
                _setupButton.Tick(false, 0f, false);
                _staffSetup?.VisitorResetCompleted();
            }
            else _staffSetup?.NewVisitor();
            _markNeedsRelease = true;
            _markHold.Reset();
            haptics?.Fire(); // 長押し発火（ランリセット）
            Debug.Log($"[OvrBridge] 体験者リセット（{origin}）");
        }
    }
}
