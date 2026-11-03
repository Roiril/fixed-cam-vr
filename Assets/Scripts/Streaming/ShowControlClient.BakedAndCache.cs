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
    // ShowControlClient の一部: 焼き込みの show.json の読み込みと、端末の設定キャッシュ。
    // 全体の入口と役目の一覧は ShowControlClient.cs の先頭。
    public sealed partial class ShowControlClient
    {
        // ---- 焼き込み StreamingAssets/show/show.json の起動時ロード（最下位優先）----

        private async Task LoadBakedShowAsync(CancellationToken ct)
        {
            string uri = ShowAssetResolver.StreamingAssetsUri("show/show.json");
            try
            {
                using var req = UnityWebRequest.Get(uri);
                req.timeout = 5;
                var op = req.SendWebRequest();
                while (!op.isDone) { ct.ThrowIfCancellationRequested(); await Task.Yield(); }
                if (req.result != UnityWebRequest.Result.Success)
                    throw new IOException("show.json: " + req.error);
                byte[] configBytes = req.downloadHandler.data;
                string configJson = req.downloadHandler.text;
                string verifiedId = "";
                if (BakedOnly)
                {
                    using var manifestReq = UnityWebRequest.Get(ShowAssetResolver.StreamingAssetsUri("show/manifest.json"));
                    manifestReq.timeout = 5;
                    var manifestOp = manifestReq.SendWebRequest();
                    while (!manifestOp.isDone) { ct.ThrowIfCancellationRequested(); await Task.Yield(); }
                    if (manifestReq.result != UnityWebRequest.Result.Success)
                        throw new FileNotFoundException("manifest.json: " + manifestReq.error);
                    if (!BakedShowManifest.TryVerify(configBytes, manifestReq.downloadHandler.text, out verifiedId))
                        throw new InvalidDataException("manifest.json と show.json が一致しない");
                }
                var state = JsonUtility.FromJson<ShowState>(configJson);
                if (state == null) throw new InvalidDataException("show.json が空");
                ApplyBaked(state);
                if (BakedOnly)
                {
                    ContentId = verifiedId;
                    ContentVerified = true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { ContentId = ""; ContentVerified = false; Debug.LogError($"[ShowControl] 焼き込み show.json 読込失敗: {e.Message}"); }
        }

        // 焼き込み値をフィールドへ流し込む（最下位優先。接続反映・イベント発火は InitializeAsync が一括で行う）。
        // ライブが既に適用済み（_rev>=0）なら焼き込みで上書きしない（ライブ優先）。
        private void ApplyBaked(ShowState state)
        {
            if (_rev >= 0) return;
            ConfigOrigin = "baked";
            // 素材の対応表を先に取る（この後に走る端末キャッシュの適用が使う）。
            BuildBakedAssetMap(state.assetMap);
            _cameras = state.cameras ?? Array.Empty<CameraDef>();
            foreach (var c in _cameras)
            {
                if (c == null) continue;
                c.hasPost = c.post != null && !c.post.IsDefaultLike();  // 素通しの実体は「未著作」扱い（卓が "post": null を書くため）
                c.hasPose = c.pose != null;
                c.hasCalib = c.calib != null && c.calib.IsUsable();
            }
            if (state.post != null) _globalPost = state.post;
            if (state.record != null) _record = state.record;
            if (state.actors != null && state.actors.Length > 0) _actors = state.actors;
            if (state.run != null) _run = state.run;
            if (state.feel != null && !state.feel.LooksUnset()) _feel = state.feel;
            if (state.eyejack != null && state.eyejack.HasData()) SetEyeJackPhotos(state.eyejack.photos);
            if (state.layout != null && state.layout.HasData())
            {
                _layout = state.layout;
                _appliedLayoutRev = state.layout.rev;
            }
            _cues = state.cues ?? Array.Empty<CueDef>();
            if (state.schedule != null && state.schedule.HasData())
            {
                _schedule = state.schedule;
                _appliedScheduleRev = state.schedule.rev;
            }
            // 焼き込み timeline（present-flag はフレッシュパースなので再導出できる）。
            if (state.timeline != null && state.timeline.HasData())
            {
                _timeline = state.timeline;
                _appliedTimelineRev = state.timeline.rev;
                TimelinePresentFlags.Reconcile(_timeline);
            }
            // 焼き込み BGM（ライブ / キャッシュがあれば後で上書きされる）。
            _bgmTracks = state.bgmTracks ?? Array.Empty<ShowBgmTrackDef>();
            _bgmDefault = (state.bgm != null && state.bgm.IsActionable()) ? state.bgm : null;
            // 焼き込み値の runEpoch を「既知値」として取り込む（端末キャッシュがあれば後で上書きされる）。
            _knownRunEpoch = state.control?.runEpoch ?? _knownRunEpoch;
            // 焼き込みのカメラ切替タイミング（端末キャッシュ / ライブがあれば後で上書きされる）。
            _switchDwellSec = state.control?.minDwellSec ?? _switchDwellSec;
            _switchCooldownSec = state.control?.switchCooldownSec ?? _switchCooldownSec;
            Debug.Log($"[ShowControl] 焼き込み show.json を適用: cameras={_cameras.Length}, " +
                      $"cues={_cues.Length}, schedule={( _schedule != null ? _schedule.entries.Length : 0)}, " +
                      $"timeline={( _timeline != null ? _timeline.segments.Length : 0)}, " +
                      $"course={( _layout?.course != null ? _layout.course.order.Length : 0)}");
        }

        // ---- 端末ローカル設定キャッシュ（PC 不在起動でも Web 設定を参照するため）----

        // ---- 端末キャッシュの素材を焼き込みへ読み替える（2026-09-05）----
        //
        // ⚠⚠ 設定の優先順位は **焼き込み < 端末キャッシュ < ライブ**。キャッシュ（<see cref="SaveCache"/>）は
        //    卓から受け取った URL をそのまま保存するので、**卓に一度でも繋いだ機は「卓を指す相対 URL」を
        //    持ったまま再起動する**。その機を卓なしで起動すると素材が 1 つも解決できない
        //    （実機 Quest α のキャッシュを実測: 素材 URL 39 本すべてが `/captures/...` の相対で
        //    `sa://` は 0 本だった）。動画は `AbortCurrentCue` で畳まれてライブ映像へ戻り、
        //    静止画とマスクは出ないまま体験が進む。
        //
        // ⚠ 読み替えるのは**キャッシュ経路だけ**。ライブが来れば `_cues` ごと上書きされるので、
        //   卓が生きているときは卓の最新素材が出る（会期中の差し替え運用は保たれる）。
        //   焼き込みに無い素材（最後の焼き込み後に卓で足したもの）は触らない ＝ 従来どおりの挙動。

        private readonly Dictionary<string, string> _bakedAssetMap = new();

        private void BuildBakedAssetMap(AssetMapEntry[]? entries)
        {
            _bakedAssetMap.Clear();
            if (entries == null) return;
            foreach (AssetMapEntry e in entries)
            {
                if (e == null || string.IsNullOrEmpty(e.from) || string.IsNullOrEmpty(e.to)) continue;
                _bakedAssetMap[e.from] = e.to;
            }
        }

        // 1 本を読み替える。判定は ShowAssetResolver に置いてある（EditMode テストで固定するため）。
        private bool TryRemap(string url, out string baked)
            => ShowAssetResolver.TryRemapToBaked(_bakedAssetMap, url, out baked);

        private void RemapCachedAssetsToBaked()
        {
            if (_bakedAssetMap.Count == 0)
            {
                // 焼き込みが無い / 対応表を持たない古い APK。**黙って旧挙動へ落ちるのがいちばん危ない**ので必ず言う。
                Debug.LogWarning("[ShowControl] 焼き込みの素材対応表が無いので、端末キャッシュの素材 URL は"
                                 + "卓を指したままです（卓が落ちると素材が出ません）。APK を焼き直してください");
                return;
            }

            int n = 0;
            foreach (CueDef c in _cues)
            {
                if (c == null) continue;
                if (TryRemap(c.maskUrl, out string m)) { c.maskUrl = m; n++; }
                if (TryRemap(c.sourceUrl, out string s)) { c.sourceUrl = s; n++; }
            }
            foreach (ShowBgmTrackDef t in _bgmTracks)
            {
                if (t == null) continue;
                if (TryRemap(t.url, out string u)) { t.url = u; n++; }
            }
            // v3 の演出はカット自身が素材 URL を持つ。ここが抜けると「演出の映像だけ出ない」。
            if (_timeline != null && _timeline.segments != null)
            {
                foreach (ShowTimelineSegmentDef seg in _timeline.segments)
                {
                    if (seg?.takes == null) continue;
                    foreach (ShowTakeDef take in seg.takes)
                    {
                        if (take?.steps == null) continue;
                        foreach (ShowStepDef step in take.steps)
                        {
                            if (step == null) continue;
                            if (TryRemap(step.assetUrl, out string a)) { step.assetUrl = a; n++; }
                        }
                    }
                }
            }
            // 目の写真。実バイトは EyeJackPhotoStore が別に持つが、URL も揃えておく。
            if (_eyeJackPhotos.Length > 0)
            {
                var photos = new string[_eyeJackPhotos.Length];
                bool changed = false;
                for (int i = 0; i < _eyeJackPhotos.Length; i++)
                {
                    photos[i] = TryRemap(_eyeJackPhotos[i], out string p) ? p : _eyeJackPhotos[i];
                    if (photos[i] != _eyeJackPhotos[i]) { changed = true; n++; }
                }
                if (changed) SetEyeJackPhotos(photos);
            }

            if (n > 0)
            {
                // 走行レポートの「実機が使った設定」に出るので、現地で 1 行で判る。
                ConfigOrigin = "cache+baked";
                Debug.Log($"[ShowControl] 端末キャッシュの素材 {n} 本を焼き込み（sa://）へ読み替えた"
                          + "（卓が落ちても素材が出る）");
            }
        }

        private void SaveCache()
        {
            if (BakedOnly) return;
            try
            {
                var cfg = new CachedConfig
                {
                    cameras = _cameras,
                    post = _globalPost,
                    layout = _layout,   // course を内包
                    cues = _cues,
                    schedule = _schedule,
                    timeline = _timeline,
                    bgmTracks = _bgmTracks,
                    bgm = _bgmDefault,
                    record = _record,
                    actors = _actors,
                    run = _run,
                    feel = _feel,
                    eyejack = new ShowEyeJackDef { photos = _eyeJackPhotos },
                    runEpoch = _knownRunEpoch,
                    switchDwellSec = _switchDwellSec,
                    switchCooldownSec = _switchCooldownSec,
                    // どの APK が書いたキャッシュか。次の起動で照合して、別 APK のものなら捨てる。
                    buildGuid = Application.buildGUID ?? "",
                };
                WriteAtomic(ConfigCachePath, JsonUtility.ToJson(cfg));
            }
            catch (Exception e) { Debug.LogWarning($"[ShowControl] 設定キャッシュ保存失敗: {e.Message}"); }
        }

        /// <summary>
        /// <c>.tmp</c> へ書いてから差し替える。**書いている途中で電源が落ちても、既にある版が壊れない。**
        ///
        /// ⚠ 素の <c>File.WriteAllText</c> は既存ファイルを先に truncate するので、そこで切れると
        /// **壊れた JSON が残る**。次の起動は毎回パースに失敗して焼き込み値へ落ちる ＝
        /// 卓が居ない現場では設定が黙って巻き戻る。同じ手当てが
        /// <c>CourseFrame.SaveRegistration</c> にもある（あちらが壊れると位置合わせが消えて
        /// 導入が自動では二度と始まらない）。
        /// </summary>
        internal static void WriteAtomic(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            if (!File.Exists(path)) { File.Move(tmp, path); return; }
            // File.Replace は同一ボリューム前提。使えない環境（一部の Android）では消してから移す。
            try { File.Replace(tmp, path, null); }
            catch { File.Delete(path); File.Move(tmp, path); }
        }

        /// <summary>ログ用に識別子を頭 8 文字へ詰める（全部出しても現場では読めない）。</summary>
        private static string Short(string? s)
            => string.IsNullOrEmpty(s) ? "-" : (s!.Length <= 8 ? s : s.Substring(0, 8));

        // 端末キャッシュを読み、焼き込み値の上へ「データを持つ項目だけ」上書きする（空で潰さない）。
        // 接続反映・イベント発火・post 適用は呼び出し側（InitializeAsync）が一括で行う。
        private void LoadAndApplyCache()
        {
            if (BakedOnly) return;
            try
            {
                if (!File.Exists(ConfigCachePath)) return;
                var cfg = JsonUtility.FromJson<CachedConfig>(File.ReadAllText(ConfigCachePath));
                if (cfg == null) return;

                // ⚠⚠ **APK が変わったらキャッシュを捨てる**（2026-08-14）。
                //    キャッシュは焼き込みより優先されるので、これが無いと
                //    **APK を焼き直しても古い設定のまま走る**（スタッフは直したつもりでいる）。
                //    2026-07-31 の実測では PC・焼き込み・Quest 2 台の 4 者がずれていて、
                //    しかも `timeline.rev` は全部一致していたので**版番号では気づけなかった**。
                //    ⚠ 消えるのは show の設定だけで、**位置合わせ（registration.json）は別ファイル**
                //    なので残る（現場で測り直しにならない）。
                //    ⚠ 卓が生きていれば long-poll が即座に配り直す。卓が無い現場では
                //    新しい APK の焼き込み値が使われる ＝ スタッフの直感どおりになる。
                string apk = Application.buildGUID ?? "";
                if (!string.IsNullOrEmpty(apk) && cfg.buildGuid != apk)
                {
                    Debug.LogWarning("[ShowControl] 別の APK が書いた設定キャッシュなので捨てました"
                                     + $"（焼き込み値で始めます / cache={Short(cfg.buildGuid)} apk={Short(apk)}）");
                    try { File.Delete(ConfigCachePath); } catch { /* 消せなくても焼き込みで走る */ }
                    return;
                }

                ConfigOrigin = "cache";  // 焼き込みより優先。古いまま残ると APK を焼き直しても設定が変わらない
                if (cfg.cameras != null && cfg.cameras.Length > 0) _cameras = cfg.cameras;
                if (cfg.post != null) _globalPost = cfg.post;
                // キャッシュ済み layout も復元（grid か cuts があるもののみ / course も内包）。
                if (cfg.layout != null && cfg.layout.HasData())
                {
                    _layout = cfg.layout;
                    _appliedLayoutRev = cfg.layout.rev;
                }
                if (cfg.cues != null && cfg.cues.Length > 0) _cues = cfg.cues;
                if (cfg.record != null) _record = cfg.record;
                if (cfg.actors != null && cfg.actors.Length > 0) _actors = cfg.actors;
                if (cfg.run != null) _run = cfg.run;
                if (cfg.feel != null && !cfg.feel.LooksUnset()) _feel = cfg.feel;
                if (cfg.eyejack != null && cfg.eyejack.HasData()) SetEyeJackPhotos(cfg.eyejack.photos);
                if (cfg.schedule != null && cfg.schedule.HasData())
                {
                    _schedule = cfg.schedule;
                    _appliedScheduleRev = cfg.schedule.rev;
                }
                // キャッシュ済み timeline も復元。TimelinePresentFlags.Reconcile を一律適用する
                // （AND なので保存済み bool が保たれる＝実質 no-op。live/焼き込み/キャッシュで正規化経路を
                //  一本化し「どのパスが正規化するか」の認知負荷を消す）。
                if (cfg.timeline != null && cfg.timeline.HasData())
                {
                    _timeline = cfg.timeline;
                    _appliedTimelineRev = cfg.timeline.rev;
                    TimelinePresentFlags.Reconcile(_timeline);
                }
                // BGM（PC 不在起動でも前回と同じ曲・同じループ範囲で始まる）。
                if (cfg.bgmTracks != null && cfg.bgmTracks.Length > 0) _bgmTracks = cfg.bgmTracks;
                if (cfg.bgm != null && cfg.bgm.IsActionable()) _bgmDefault = cfg.bgm;
                // 既知の runEpoch を復元（この起動では発火しない = 同一 epoch の誤リセット防止）。
                _knownRunEpoch = cfg.runEpoch;
                // カメラ切替タイミングを復元（0=未指定でコード既定。ApplySwitchTiming は InitializeAsync が呼ぶ）。
                _switchDwellSec = cfg.switchDwellSec;
                _switchCooldownSec = cfg.switchCooldownSec;
                // ⚠⚠ **卓の URL を焼き込みへ読み替える**（2026-09-05）。ここが無いと、一度でも
                //    卓に繋いだ機は「卓を指す URL」を持ったまま再起動し、卓が落ちていると
                //    素材が 1 つも解決できない（実機のキャッシュを実測: 素材 URL 39 本すべてが相対）。
                RemapCachedAssetsToBaked();
                Debug.Log($"[ShowControl] 端末キャッシュ設定を適用: {ConfigCachePath} " +
                          $"(cameras={_cameras.Length}, layout={( _layout != null ? "yes" : "no")}, " +
                          $"cues={_cues.Length}, schedule={( _schedule != null ? _schedule.entries.Length : 0)}, " +
                          $"timeline={( _timeline != null ? _timeline.segments.Length : 0)})");
            }
            catch (Exception e) { Debug.LogWarning($"[ShowControl] 設定キャッシュ読込失敗: {e.Message}"); }
        }
    }
}
