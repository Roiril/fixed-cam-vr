#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace FixedCamVr.Streaming
{
    // ShowControlClient の一部: 卓とのやり取り — 状態の long-poll と適用・コマンド送信・heartbeat。
    // 全体の入口と役目の一覧は ShowControlClient.cs の先頭。
    public sealed partial class ShowControlClient
    {
        /// <summary>
        /// long-poll / heartbeat ループを掴み直す（server の接続先が発見で張り替わった時に呼ぶ）。
        /// 進行中の long-poll（最大 35s ハング）を即座に切って新エンドポイントで再起動する
        /// （URL 自体は BuildUrl が毎回 EffectiveHost を読むので次周回で反映されるが、
        ///  ハング中の request を待たせないためループごと差し替える。OnEnable と同じ _loopCts 再生成方式）。
        /// </summary>
        public void RestartServerLoop()
        {
            if (server == null) return;
            _loopCts?.Cancel();
            _loopCts?.Dispose();
            _loopCts = null;
            if (!isActiveAndEnabled) return;
            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
            if (!BakedOnly) _ = PollLoopAsync(_loopCts.Token);
            if (!Replay.XpReplay.Active) _ = HeartbeatLoopAsync(_loopCts.Token);
        }

        /// <summary>
        /// 発見した PC 卓（role="show-server"）の現在 IP を server へ反映しループを掴み直す。
        /// DiscoveryClient が「server 未通 N 秒」を確認した時のみ呼ぶ。接続先が実際に変わった時だけ再起動。
        /// </summary>
        public void ApplyDiscoveredServer(string host, int port)
        {
            if (server == null)
            {
                Debug.LogWarning("[ShowControl] server 未設定のため発見した show-server を適用できない（ShowServer.asset を割り当てよ）。");
                return;
            }
            string before = server.Endpoint;
            server.ApplyRuntimeEndpoint(host, port);
            if (server.Endpoint != before)
            {
                Debug.Log($"[ShowControl] 発見した show-server を適用: {host}:{port}（loop 再起動）");
                RestartServerLoop();
            }
        }

        // ---- state long-poll ----

        private async Task PollLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    string url = server!.BuildUrl($"/state?rev={_rev}");
                    using var req = UnityWebRequest.Get(url);
                    req.timeout = 35; // サーバ側 long-poll 上限 25s より長く
                    var op = req.SendWebRequest();
                    while (!op.isDone)
                    {
                        ct.ThrowIfCancellationRequested();
                        await Task.Yield();
                    }
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        await Task.Delay(2000, ct); // サーバ不在。静かにリトライ
                        continue;
                    }
                    // /state を成功受信した = 卓が通じている。DiscoveryClient の「不通 N 秒」判定の基準。
                    _lastServerContactTime = Time.realtimeSinceStartup;
                    var state = JsonUtility.FromJson<ShowState>(req.downloadHandler.text);
                    if (state == null)
                    {
                        // 200 で空/壊れ JSON を返し続ける異常系でホットループにしない
                        await Task.Delay(1000, ct);
                        continue;
                    }
                    if (state.rev != _rev)
                    {
                        // ⚠⚠ **版番号を進めるのは適用しきってから。**（2026-09-04）
                        //   進めた後で `Apply` が途中で落ちると、ポーリングは 2 秒後に再開するのに
                        //   **同じ設定は二度と来ない**（版が進んでいるので `!=` が偽）。卓は配れた顔を
                        //   しているので、当日は「操作したのに実機が反応しない」としか見えない。
                        int prevRev = _rev;
                        _rev = state.rev;
                        try
                        {
                            Apply(state);
                            _applyFailRev = int.MinValue;
                            _applyFailCount = 0;
                        }
                        catch (Exception e)
                        {
                            if (_applyFailRev != state.rev) { _applyFailRev = state.rev; _applyFailCount = 0; }
                            _applyFailCount++;
                            if (_applyFailCount < ApplyRetryMax)
                            {
                                _rev = prevRev;   // 巻き戻して次のポーリングで同じ版をもう一度受ける
                                Debug.LogError($"[ShowControl] 設定の適用に失敗（rev={state.rev}・"
                                               + $"{_applyFailCount}/{ApplyRetryMax} 回目・巻き戻して再試行）: {e}");
                            }
                            else
                            {
                                // 恒久的に壊れた設定。これ以上回しても同じなので先へ進める
                                // （半分だけ適用された状態は残るが、次の変更で上書きされる）。
                                Debug.LogError($"[ShowControl] 設定の適用に {ApplyRetryMax} 回失敗（rev={state.rev}）。"
                                               + $"この版は諦める: {e}");
                            }
                            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
                        }
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ShowControl] poll error: {e.Message}");
                    try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return; }
                }
            }
        }

        private void Apply(ShowState state)
        {
            if (BakedOnly) return;
            ConfigOrigin = "live";  // 卓が配った設定。以後キャッシュ・焼き込みでは上書きされない
            // 1) カメラ設定（IP / 認証 / カメラ別画像加工）を反映
            _cameras = state.cameras ?? Array.Empty<CameraDef>();
            // ライブ受信パース直後だけ post / pose の null 判定が信頼できる → ここで present-flag を確定
            foreach (var c in _cameras)
            {
                if (c == null) continue;
                c.hasPost = c.post != null && !c.post.IsDefaultLike();  // 素通しの実体は「未著作」扱い（卓が "post": null を書くため）
                c.hasPose = c.pose != null;
                c.hasCalib = c.calib != null && c.calib.IsUsable();
            }
            ApplyCameraEndpoints();
            if (state.post != null) _globalPost = state.post;
            ApplyPostForActive(); // global + アクティブカメラの個別 post をマテリアルへ

            // 1.3) cue 定義を保持（scheduler の cue 解決 + 手動 activeCue 発火が引く）。
            _cues = state.cues ?? Array.Empty<CueDef>();
            // 1.35) 端末内録画の設定 / CG 人形の定義 / 素材スロットの束縛。
            _record = state.record;
            _actors = state.actors ?? Array.Empty<ShowActorDef>();
            _slots = state.control?.slots ?? Array.Empty<ShowSlotDef>();
            EnsureRecorder();
            // 1.37) 体験の骨格。設定の反映だけで、相は動かさない（相を動かすのは runEpoch と明示操作だけ）。
            _run = state.run;
            ResolveRunDirector()?.Configure(_run);
            // 1.38) 撮像の質（装置らしさ）。キーが無くても既定で効くので、null をそのまま渡してよい。
            _feel = state.feel;
            PushFeel();

            // 1.5) ゾーン layout（cuts/floor/overlap/course）。JsonUtility は null 入れ子を既定値で書くため
            //      「cuts が空でない」を present 判定に使い、rev で変更検出する。course は layout に内包。
            bool layoutChanged = false;
            if (state.layout != null && state.layout.HasData()
                && state.layout.rev != _appliedLayoutRev)
            {
                _layout = state.layout;
                _appliedLayoutRev = state.layout.rev;
                layoutChanged = true;
            }

            // 1.6) スケジュール（rev で変更検出）。rev>0 を present 判定に使い、JsonUtility が
            //      schedule 欠落時に書く既定オブジェクト（rev=0）で焼き込み/キャッシュを潰さない。
            //      実際の CueScheduler への供給は timeline supersede を調停する PushCueSource で行う。
            bool cueSourceChanged = false;
            if (state.schedule != null && state.schedule.rev > 0
                && state.schedule.rev != _appliedScheduleRev)
            {
                _schedule = state.schedule;
                _appliedScheduleRev = state.schedule.rev;
                cueSourceChanged = true;
            }

            // 1.62) タイムライン（スキーマ v2・rev で変更検出）。hasXxx present-flag はライブパース直後に確定
            //       （キャッシュ往復後は null 判定が壊れるため。CameraDef.hasPost と同手法）。
            //       rev>0 なら segments 空でも適用する（オペレータの「全消去」を通す）。
            if (state.timeline != null && state.timeline.rev > 0
                && state.timeline.rev != _appliedTimelineRev)
            {
                _timeline = state.timeline;
                _appliedTimelineRev = state.timeline.rev;
                TimelinePresentFlags.Reconcile(_timeline);
                cueSourceChanged = true;
            }

            // 1.63) BGM ライブラリ + ラン既定。トラックは毎回張り直し（参照更新）、
            //       既定はシグネチャ比較で変化時のみ適用する（rev が上がるたびに曲が鳴り直さないように）。
            _bgmTracks = state.bgmTracks ?? Array.Empty<ShowBgmTrackDef>();
            _bgmDefault = (state.bgm != null && state.bgm.IsActionable()) ? state.bgm : null;
            PushBgm();

            // 1.64) 視界ジャックの当日写真。**ライブは正**なので空も含めてそのまま採る
            //       （卓のフォルダから写真を消したら、端末からも消えるのが正しい）。
            SetEyeJackPhotos(state.eyejack?.photos);

            // cue 解決関数は毎回張り直す（_cues の参照が更新されるため）。CueScheduler / InsertController 共通。
            cueScheduler?.SetCueResolver(ResolveCue);
            timelineDirector?.SetCueResolver(ResolveCue);
            // schedule / timeline のどちらかが変わったら供給元を再分配する（timeline 優先）。
            if (cueSourceChanged) PushCueSource();

            // 1.65) ラン識別子（control.runEpoch）。変化＝新しい体験者のラン → 周回 / cue をリセット。
            //       SaveCache より前で更新し、次回起動へ「既知値」を持ち越す（同一 epoch の誤リセット防止）。
            int epoch = state.control?.runEpoch ?? _knownRunEpoch;
            if (epoch != _knownRunEpoch)
            {
                _knownRunEpoch = epoch;
                TriggerRunReset();
            }

            // 1.655) 演出の緊急中止（control.takeAbortEpoch）。卓の「■ 画面を取り返す」が増分する。
            //        走行中の演出だけを畳む（ラン・周回・once は保つ）。初回は現在値へ同期するだけで発火しない
            //        （起動のたびに「中止」が走らないように）。
            int abortEpoch = state.control?.takeAbortEpoch ?? _knownTakeAbortEpoch;
            if (!_takeAbortEpochKnown)
            {
                _knownTakeAbortEpoch = abortEpoch;
                _takeAbortEpochKnown = true;
            }
            else if (abortEpoch != _knownTakeAbortEpoch)
            {
                _knownTakeAbortEpoch = abortEpoch;
                Debug.Log($"[ShowControl] 演出の中止要求（takeAbortEpoch={abortEpoch}）");
                //  責務はタイムライン発火の演出だけ。卓の手動 cue は同時に送られる stopCue が
                //  activeCue の遷移として畳む（ここで StopOverlay すると _appliedCue と実状態がずれ、
                //  同じ cue を次に発火できなくなる）。
                timelineDirector?.AbortActive();
                // 人が止めた演出は「終わった」に数えない（終幕の早撃ちを断つ・2026-08-30）。
                ResolveRunDirector()?.NotifyTakeInterrupted();
            }

            // 1.66) カメラ切替の現場調整（control.minDwellSec / switchCooldownSec）。
            //       control ブロック / キー欠落時は 0 → Director 側でコード既定へ戻る（present 判定 >0）。
            //       SaveCache より前で更新し、次回 PC 不在起動へ持ち越す。
            _switchDwellSec = state.control?.minDwellSec ?? 0f;
            _switchCooldownSec = state.control?.switchCooldownSec ?? 0f;
            ApplySwitchTiming();

            // 1.67) ゾーン切替へ重ねる乱れ（control.switchGlitch）。
            ResolveSwitchDirector()?.SetSwitchGlitch(state.control?.switchGlitch ?? 0f);

            // 1.68) 世代カウンタ 3 種（手動の乱れ / 導入を終える / 体験を終える）。
            //       いずれも初回は現在値へ同期するだけで発火しない（起動のたびに走らないように）。
            int glitchEpoch = state.control?.glitchEpoch ?? _knownGlitchEpoch;
            if (!_glitchEpochKnown) { _knownGlitchEpoch = glitchEpoch; _glitchEpochKnown = true; }
            else if (glitchEpoch != _knownGlitchEpoch)
            {
                _knownGlitchEpoch = glitchEpoch;
                float lv = state.control?.glitchLevel ?? 0f;
                float sec = state.control?.glitchSec ?? 0f;
                ResolveSwitchDirector()?.PulseGlitch(lv > 0f ? lv : 0.8f, sec > 0f ? sec : 0.3f);
            }

            int introEpoch = state.control?.introAdvanceEpoch ?? _knownIntroEpoch;
            if (!_introEpochKnown) { _knownIntroEpoch = introEpoch; _introEpochKnown = true; }
            else if (introEpoch != _knownIntroEpoch)
            {
                _knownIntroEpoch = introEpoch;
                // ⏭ は**文脈で意味が変わる**。演出がまだ走っているなら「次の段へ」、
                //   演出が終わっている（慣らし歩行中）なら「導入相を終えて本編へ」。
                //   旧実装は常に相ごと終わらせていたので、**開始位置が使えない現場では
                //   演出が一度も出ないまま本編に入っていた**（段 0 を抜ける手段が startSpot しか
                //   無く、IntroDirector.RequestAdvanceStage の呼び出し元がゼロだった）。
                var intro = ResolveIntroDirector();
                if (intro != null && intro.Active) intro.RequestAdvanceStage();
                else ResolveRunDirector()?.RequestAdvanceIntro();
            }

            int endEpoch = state.control?.runEndEpoch ?? _knownRunEndEpoch;
            if (!_runEndEpochKnown) { _knownRunEndEpoch = endEpoch; _runEndEpochKnown = true; }
            else if (endEpoch != _knownRunEndEpoch)
            {
                _knownRunEndEpoch = endEpoch;
                ResolveRunDirector()?.RequestFinish();
            }

            // 端末キャッシュへ保存（次回 PC 不在起動で参照）
            SaveCache();
            if (layoutChanged)
            {
                LayoutChanged?.Invoke();
                CourseChanged?.Invoke(); // course は layout に内包 → 同時通知
            }

            // 1.7) discovery キルスイッチ（control.discoveryEnabled、省略時 true）。
            //      DiscoveryClient がこれを AND して probe/切替を全停止できる（従来の静的 IP 運用へ縮退）。
            DiscoveryEnabled = state.control?.discoveryEnabled ?? true;

            // 2) カメラ手動 override（show.cameras の並び = registry sources の並びが前提）
            string ovr = state.control?.cameraOverride ?? "";
            if (ovr != _appliedOverride)
            {
                _appliedOverride = ovr;
                bool hasOverride = !string.IsNullOrEmpty(ovr);
                // override 中は tracker を無効化（enabled=false）、解除で再有効化（enabled=true）する。
                // 再有効化は PlayerZoneTracker.OnEnable を発火し、そこで記憶ゾーン（_current）が無効化される
                // → 次 Update で現在位置から再 Pick して通常経路でゾーンカメラへ復帰する。これが無いと
                // 同一ゾーン滞在のまま _current が不変で、次のゾーン跨ぎまで override カメラに表示が固着する。
                // （asmdef 循環回避のため Tracking 型は持ち込まず、再有効化＝自己回復に委ねる。）
                if (zoneTrackerToDisable != null) zoneTrackerToDisable.enabled = !hasOverride;
                // override を Director の第一級凍結にする（cue/insert 凍結と対称）。enter/exit で stale 保留を
                // 無条件クリアし、override 前に積まれたゾーン保留が cooldown 後に Zone commit して固定が破れるのを防ぐ。
                // Director 未配線（null）なら _logic 自体が無く stale-pending バグも起きないので null-safe skip で正しい。
                ResolveSwitchDirector()?.SetOverrideActive(hasOverride);
                // ⚠ `state.cameras` は卓の JSON 由来なので、欠けていれば null・要素も null でありうる
                //   （すぐ上の `_cameras` の走査が null 要素を飛ばしているのと同じ前提）。無防備に
                //   触ると `Apply` がここで落ち、この版の設定が丸ごと適用されない。
                if (hasOverride && registry != null && state.cameras != null)
                {
                    int idx = Array.FindIndex(state.cameras, c => c != null && c.id == ovr);
                    if (idx >= 0) SetActiveOverride(idx);
                    else Debug.LogWarning($"[ShowControl] unknown camera id: {ovr}");
                }
            }

            // 3) cue 発火 / 停止（ライブ手動オーバーライド）。
            //    activeCue 非空の間は CueScheduler を抑止する（ライブ優先）。毎回同期する。
            string cueId = state.control?.activeCue ?? "";
            cueScheduler?.SetLiveCueActive(!string.IsNullOrEmpty(cueId));
            // インサートも同条件で抑止する（activeCue 非空 or cameraOverride 非空中は発火しない）。
            bool liveSuppressed = !string.IsNullOrEmpty(cueId) || !string.IsNullOrEmpty(_appliedOverride);
            timelineDirector?.SetSuppressed(liveSuppressed);
            // ⚠ 卓が画面を握っているあいだは終幕の合図を武装させない（2026-08-30）。
            //    抑止は走行中の演出を畳んで ActiveTakeId を空にするので、そのままだと
            //    `EndingCueLogic` が「著作した演出が終わった」と読んで終幕を早撃ちする。
            if (liveSuppressed) ResolveRunDirector()?.NotifyTakeInterrupted();
            // スタッフが介入している間の区間は「体験者の滞在」として測らない
            //（カメラ固定中はゾーン追跡自体が止まるので、そのまま測ると滞在が水増しされる）。
            if (liveSuppressed) _dwell.Reset();
            if (cueId != _appliedCue)
            {
                if (_overlay == null) return;
                if (string.IsNullOrEmpty(cueId))
                {
                    _appliedCue = cueId;
                    _overlay.StopOverlay();
                }
                else
                {
                    OverlayCueData? data = ResolveCue(cueId);
                    if (data == null)
                    {
                        // _appliedCue は確定しない: Web 側で cue を保存し直した後の再 poll で
                        // 同じ activeCue 文字列でも再解決できるようにする。
                        Debug.LogWarning($"[ShowControl] unknown cue id: {cueId}");
                        return;
                    }
                    _appliedCue = cueId;
                    _overlay.PlayCue(data);
                }
            }
        }

        // JsonUtility が配列長を保証しないので、欠けた成分は既定値で埋める（黙って 0 倍にしない）。
        private static Vector3 ToVec3(float[]? a, float fallback)
        {
            float x = a != null && a.Length > 0 ? a[0] : fallback;
            float y = a != null && a.Length > 1 ? a[1] : fallback;
            float z = a != null && a.Length > 2 ? a[2] : fallback;
            return new Vector3(x, y, z);
        }

        // 焼き込み StreamingAssets / 端末キャッシュ由来の schedule / timeline / course を消費者へ供給する。
        private void PushCourseAndSchedule()
        {
            cueScheduler?.SetCueResolver(ResolveCue);
            timelineDirector?.SetCueResolver(ResolveCue);
            timelineDirector?.SetUrlResolver(ResolveAssetUrl);
            // 演出専用カメラ（role:"fx" = カメラ D）はスタッフ巡回・ゾーン自動切替に出さない。
            ResolveSwitchDirector()?.SetCameraSelectable(i => !IsFxCamera(i));
            PushCueSource();
            cueScheduler?.SetLiveCueActive(!string.IsNullOrEmpty(_appliedCue));
            timelineDirector?.SetSuppressed(!string.IsNullOrEmpty(_appliedCue) || !string.IsNullOrEmpty(_appliedOverride));
        }

        private void SendCommand(string type, string id)
        {
            if (BakedOnly) return;
            if (server == null) { Debug.LogWarning("[ShowControl] server 未設定: 演出はオペレータ卓接続時のみ発火可"); return; }
            _ = SendCommandAsync(type, id, destroyCancellationToken);
        }

        private async Task SendCommandAsync(string type, string id, CancellationToken ct)
        {
            try
            {
                string json = JsonUtility.ToJson(new CommandMsg { type = type, id = id });
                using var req = UnityWebRequest.Post(server!.BuildUrl("/command"), json, "application/json");
                req.timeout = 3;
                var op = req.SendWebRequest();
                while (!op.isDone) { ct.ThrowIfCancellationRequested(); await Task.Yield(); }
                if (req.result != UnityWebRequest.Result.Success)
                    Debug.LogWarning($"[ShowControl] command '{type}' failed: {req.error}");
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Debug.LogWarning($"[ShowControl] command '{type}' error: {e.Message}"); }
        }

        // ---- heartbeat ----

        [Serializable] private class Heartbeat
        {
            // 卓の他の全表示（ラッチ帯・プリフライト・リボン）が使うカメラ ID（A/B/C）に揃える。
            // 以前は DisplayName（"Phone 01"）を送っており、ヘッダだけ表記が違って現場の照合が増えていた。
            public string activeCamera = "";
            public int activeIndex = -1;
            // Unity が実際に受信しているカメラ本数。卓の「show.json の cameras 数と合っているか」検査用
            // （合っていないと演出のカメラ index が無言で別カメラへずれる）。
            public int cameraCount;
            public float recvFps;
            public string playingCue = "";
            public string cameraOverride = "";
            // コントローラ操作モード（NORMAL/REG）。スタッフが遠隔でモードを把握するため。
            // サーバ側は未知フィールドを無視するので送るだけでよい。
            public string mode = "NORMAL";
            // ⚠⚠ **ステータス表示（右 B）が出ているか**（2026-09-14）。
            //    体験者の視界の 1.6m 先に日本語の業務表示が出ている状態で、**画にも音にも出ない**。
            //    押しているあいだだけ出る形にして開けっ放しは構造的に消えたが、
            //    「本編中にどれだけ出していたか」を卓が見られないと運用の改善に繋がらない。
            public bool statusHud;
            // 現在の周回数（LapCounter 由来。未注入なら -1）とアクティブカメラ index。
            // Web ライブ運用パネルの「Lap N / cam B」表示用。既存 activeIndex と重複するが契約名は cam。
            public int lap = -1;
            public int cam = -1;
            // ここまで適用した show.json の rev。UI / 自動検証が「Unity 反映済み」を機械判定する。
            public int appliedRev = -1;
            public string contentId = "";
            public string contentPolicy = BakedShowManifest.Policy;
            public bool contentVerified;
            public string buildGuid = "";
            // ライブモニタ用（任意）: HMD の course space XZ と現在ゾーンラベル。
            // 供給元（ZoneLayoutApplier）未注入なら 0 / 空文字。
            public float headCourseX;
            public float headCourseZ;
            public string currentZone = "";
            // ⚠ cam（上）は**画面に映っているカメラ**で、体験者の居場所ではない。
            //   演出のカットが別カメラを映している間、両者は食い違う。卓が「ゾーン」と称して cam を
            //   出していたため、演出中は別区間の「次の演出」を表示していた（2026-07-28）。
            //   zoneCam = ショーの時計が確定した**体験者の居るゾーンの担当カメラ index**（-1 = 未確定）。
            public int zoneCam = -1;
            // 走行中の演出 id（空 = 演出なし）。卓の「いま画面を握っているのは誰か」表示に使う。
            public string takeId = "";
            // 位置合わせの状態。registered=false / needsReReg=true のまま体験を始めるとゾーンがズレたまま動く。
            public bool registered;
            public bool needsReReg;
            // ---- 当日「気づけない失敗」の 2 系統（2026-08-30 追加）------------------------------
            // どちらも**画にも録画にも 1 ビットも出ない**ので、卓がここを出さないと現場で検知できない。
            //
            // コントローラ。左は体験者の唯一の入力（報告）、右はスタッフの操作（ランリセット・位置合わせ）。
            // ⚠ 接続と位置は別物 — 電源が入っていれば接続は true だが、カメラから見えていないと姿勢は無効。
            public bool ctrlLConnected;
            public bool ctrlLTracked;
            public bool ctrlRConnected;
            public bool ctrlRTracked;
            // 音。⚠⚠ **音は録画に映らない**（CLAUDE.md）。掴めなかった音源が 1 つでもあれば
            //    設計どおりには鳴っておらず、audible が 0.0 なら**そもそも無音**。
            //    -1 = 音の実行体がシーンに居ない（`menu scene` の焼き直し漏れ）。
            public int sndResolved = -1;
            public int sndMissing = -1;
            public float sndAudible = -1f;
            // 体験の骨格（ShowRunDirector 由来）。卓のラン状態パネルが「導入中 0:12」「2 周目 ・
            // 経過 1:05 / 目安 3:00」「終了（次の体験者へ）」を出すのに要る。
            //   phase: "INTRO" | "RUN" | "END"
            public string phase = "RUN";
            public float runSec;        // 本編の経過（導入は含まない）
            public float lapSec;        // いまの周の経過
            public float introSec;      // 導入の経過
            public float targetSec;     // 目安の尺（超過は警告するだけで体験は止めない）
            public int totalLaps;       // 走り切る周数
            public bool endHolding;     // 終了条件は満たしたが走行中の演出を見せ切っている
            // 視界ジャックの写真（listed = show.json が言う枚数 / ready = この端末に用意できた枚数）。
            // 2 台のうち片方だけ届いていないのは無音の失敗 — 卓のこの表示にしか出ない。
            public int eyeJackListed = -1;
            public int eyeJackReady = -1;

            // 遅延の内訳（企画書「視覚遅延は 100ms 程度以内を目標として管理する」）。
            // **絶対の end-to-end ではない** — 配信端末と Unity で時計の基準が違い引き算できないため、
            // Unity が観測できる 3 つ（到着の揺らぎ / 展開 / 提示）と配信側の鮮度だけを送る。
            public float latencyJitterMs;
            public float latencyDecodeMs;
            public float latencyPresentMs;
            public float sourceAgeMs;
            public float displayHz;
            public int throttleStage;   // 配信側の熱による自動降格（0 = なし）
            // ---- 機の名乗りと、タブレットの口（0185 / 0187）------------------------------
            // deviceId = この機の名乗り（SystemInfo.deviceUniqueIdentifier）。卓は機ごとに heartbeat を
            //   持ち分ける（GET /unity/devices）。**これが無いと 2 台が 1 スロットを交互に上書きする**
            //   （memory/onsite_day_ops.md §3）。
            // visitorPort = タブレットの口が開いているポート（0 = 開けなかった）。**タブレットは卓を経由せず
            //   この機へ直接繋ぐ**ので、卓に載るのは「開いているか」と実値だけ。
            // lang / relief = いま実際にこの機が出している値（書いたつもりではなく static から読む）。
            public string deviceId = "";
            public string deviceModel = "";
            public string localIp = "";
            public int visitorPort;
            public int visitorReceived;
            public bool visitorPending;
            public VisitorPortal.HeartbeatSnapshot? visitorPortal;
            public string lang = "ja";
            public bool relief;
            // titleStage = タイトルの段（Off / Wait / In / Hold / Out / Done）。タブレットが
            //   「注意書きの段で待っているか（＝ いま書けるか）」を出すために要る。phase（INTRO）だけでは
            //   黒の待ちと導入の演出中を区別できない。
            public string titleStage = "";
            // 前回の heartbeat 以降に確定した区間滞在（実測）。卓が集計して
            // リボン UI の「実測 平均 Ns」に使う。空配列で送ってよい（サーバ側は無視）。
            public DwellHb[] dwell = Array.Empty<DwellHb>();
        }

        private static string PhaseCode(ShowPhase p)
            => p == ShowPhase.Intro ? "INTRO" : (p == ShowPhase.Finished ? "END" : "RUN");

        /// <summary>heartbeat 用の滞在サンプル（JsonUtility は入れ子クラスの配列も往復できる）。</summary>
        [Serializable] private class DwellHb
        {
            public int lap;
            public int camera;
            public float sec;
        }

        private static DwellHb[] ToHb(SegmentDwellLog.Sample[] samples)
        {
            if (samples.Length == 0) return Array.Empty<DwellHb>();
            var arr = new DwellHb[samples.Length];
            for (int i = 0; i < samples.Length; i++)
                arr[i] = new DwellHb { lap = samples[i].lap, camera = samples[i].camera, sec = samples[i].sec };
            return arr;
        }

        private async Task HeartbeatLoopAsync(CancellationToken ct)
        {
            var hb = new Heartbeat();
            while (!ct.IsCancellationRequested)
            {
                // 取り出した分は送信が成功しなければ戻す（卓の再起動・一時断で実測を落とさない）。
                SegmentDwellLog.Sample[] taken = Array.Empty<SegmentDwellLog.Sample>();
                try
                {
                    taken = _dwell.TakePending();
                    hb.dwell = ToHb(taken);
                    var active = registry != null ? registry.GetActive() : null;
                    string activeId = registry != null
                        ? (registry.GetSource(registry.ActiveIndex)?.CameraId ?? "")
                        : "";
                    hb.activeCamera = !string.IsNullOrEmpty(activeId) ? activeId : (active?.DisplayName ?? "");
                    hb.activeIndex = registry != null ? registry.ActiveIndex : -1;
                    hb.cameraCount = registry != null ? registry.Count : 0;
                    hb.recvFps = active?.ReceivedFps ?? 0f;
                    hb.playingCue = _overlay?.Current?.id ?? "";
                    hb.cameraOverride = _appliedOverride;
                    hb.mode = _controllerMode;
                    hb.statusHud = StatusVisible;
                    hb.appliedRev = _rev;
                    hb.contentId = ContentId;
                    hb.contentPolicy = ContentPolicy;
                    hb.contentVerified = ContentVerified;
                    hb.buildGuid = Application.buildGUID ?? "";
                    hb.lap = ProgressLapProvider != null ? ProgressLapProvider() : -1;
                    hb.cam = registry != null ? registry.ActiveIndex : -1;
                    if (HeadCourseXZProvider != null)
                    {
                        Vector2 c = HeadCourseXZProvider();
                        hb.headCourseX = c.x;
                        hb.headCourseZ = c.y;
                    }
                    hb.currentZone = CurrentZoneLabelProvider != null ? CurrentZoneLabelProvider() : "";
                    // 人の居場所（時計の確定ゾーン）と画面のカメラを別々に送る。混ぜると卓が嘘をつく。
                    CameraSwitchDirector? zd = ResolveSwitchDirector();   // UnityEngine.Object の偽 null を踏まないよう != で判定する
                    hb.zoneCam = (zd != null && zd.TryGetCurrentZoneCamera(out int zc)) ? zc : -1;
                    hb.takeId = timelineDirector != null ? timelineDirector.ActiveTakeId : "";
                    hb.registered = CourseRegisteredProvider == null || CourseRegisteredProvider();
                    hb.needsReReg = CourseNeedsReRegProvider != null && CourseNeedsReRegProvider();
                    hb.eyeJackListed = _eyeJackPhotos.Length;
                    hb.eyeJackReady = EyeJackReadyCountProvider != null ? EyeJackReadyCountProvider() : -1;

                    // 当日「気づけない失敗」の 2 系統（機序は Heartbeat の doc）。
                    if (ControllerStateProvider != null)
                    {
                        var (lc, lt, rc, rt) = ControllerStateProvider();
                        hb.ctrlLConnected = lc;
                        hb.ctrlLTracked = lt;
                        hb.ctrlRConnected = rc;
                        hb.ctrlRTracked = rt;
                    }
                    ShowSoundDirector? snd = ResolveSoundDirector();
                    hb.sndResolved = snd != null ? snd.ClipsResolved : -1;
                    hb.sndMissing = snd != null ? snd.ClipsMissing : -1;
                    hb.sndAudible = snd != null ? snd.AudibleSum : -1f;

                    if (active != null)
                    {
                        LatencyEstimatorLogic lat = active.Latency;
                        hb.latencyJitterMs = lat.ArrivalJitterMs;
                        hb.latencyDecodeMs = lat.DecodeMs;
                        hb.latencyPresentMs = lat.PresentMs;
                        hb.sourceAgeMs = lat.SourceAgeMs;
                        hb.throttleStage = active.Health?.throttleStage ?? 0;
                    }
                    hb.displayHz = DisplayRateInfo.CurrentHz;

                    // タブレットの設定（0185）。static から読み直す（書いたつもりを送らない）。
                    hb.deviceId = _deviceId;
                    hb.deviceModel = _deviceModel;
                    hb.localIp = _portal != null ? _portal.LocalIp : "";
                    hb.visitorPort = _portal != null && _portal.IsListening ? VisitorPortal.Port : 0;
                    hb.visitorReceived = _portal != null ? _portal.Received : 0;
                    hb.visitorPending = VisitorPrefs.HasPending;
                    hb.visitorPortal = _portal != null ? _portal.CreateHeartbeatSnapshot() : null;
                    hb.lang = ShowLanguage.Code(ShowLanguage.Current);
                    hb.relief = HorrorRelief.Enabled;
                    if (_titleForHb == null) _titleForHb = FindObjectOfType<TitleScreen>();
                    hb.titleStage = _titleForHb != null ? _titleForHb.Stage.ToString() : "";

                    ShowRunDirector? run = ResolveRunDirector();
                    if (run != null)
                    {
                        hb.phase = PhaseCode(run.Phase);
                        hb.runSec = run.RunElapsedSec;
                        hb.lapSec = run.LapElapsedSec;
                        hb.introSec = run.IntroElapsedSec;
                        hb.targetSec = run.TargetSec;
                        hb.totalLaps = run.TotalLaps;
                        hb.endHolding = run.EndHolding;
                    }

                    string json = JsonUtility.ToJson(hb);
                    using var req = UnityWebRequest.Post(
                        server!.BuildUrl("/unity/heartbeat"), json, "application/json");
                    req.timeout = 3;
                    var op = req.SendWebRequest();
                    while (!op.isDone)
                    {
                        ct.ThrowIfCancellationRequested();
                        await Task.Yield();
                    }
                    if (req.result != UnityWebRequest.Result.Success) _dwell.PutBack(taken);
                    else _lastServerContactTime = Time.realtimeSinceStartup;
                }
                catch (OperationCanceledException) { _dwell.PutBack(taken); return; }
                catch { _dwell.PutBack(taken); /* heartbeat はベストエフォート */ }

                try { await Task.Delay((int)(heartbeatInterval * 1000), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
