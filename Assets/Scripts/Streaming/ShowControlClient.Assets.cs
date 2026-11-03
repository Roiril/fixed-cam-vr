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
    // ShowControlClient の一部: 素材の解決 — cue と素材の URL・入れ替わりの無人プレート・先読み・目の視界ジャックの写真。
    // 全体の入口と役目の一覧は ShowControlClient.cs の先頭。
    public sealed partial class ShowControlClient
    {
        // ---- 目の視界ジャック（canon/LEDGER.md 0099）----

        // show.json トップレベル eyejack.photos[]（生 URL）。ライブ / キャッシュ / 焼き込みで更新。
        private string[] _eyeJackPhotos = Array.Empty<string>();
        private int _eyeJackPhotosRev;

        /// <summary>写真リストが変わるたびに増える（<see cref="EyeJackPhotoStore"/> の変更検出用）。</summary>
        public int EyeJackPhotosRev => _eyeJackPhotosRev;

        /// <summary>著作された写真の枚数（届いた・デコードできたとは別 — そちらは AnomalyEyes 側）。</summary>
        public int EyeJackPhotoListedCount => _eyeJackPhotos.Length;

        /// <summary>
        /// 端末に実際に用意できた写真の枚数（<see cref="AnomalyEyes"/> が配線する）。
        /// heartbeat で卓へ送る — 2 台のうち片方だけ写真が届いていないのは**無音の失敗**で、
        /// ここに出さないと当日誰も気づけない。null = 目の実体が居ない。
        /// </summary>
        public Func<int>? EyeJackReadyCountProvider;

        /// <summary>視界ジャックの写真（解決済み URL 列。ファイル名順のまま。空要素はそのまま返す —
        /// 読む側（<see cref="EyeJackPhotoStore"/>）が飛ばす）。</summary>
        public string[] ResolveEyeJackPhotoUrls()
        {
            if (_eyeJackPhotos.Length == 0) return Array.Empty<string>();
            var arr = new string[_eyeJackPhotos.Length];
            for (int i = 0; i < _eyeJackPhotos.Length; i++)
            {
                string u = _eyeJackPhotos[i] ?? "";
                arr[i] = string.IsNullOrEmpty(u) ? "" : ShowAssetResolver.Resolve(u, server);
            }
            return arr;
        }

        private void SetEyeJackPhotos(string[]? photos)
        {
            photos ??= Array.Empty<string>();
            if (photos.Length == _eyeJackPhotos.Length)
            {
                bool same = true;
                for (int i = 0; i < photos.Length; i++)
                    if (photos[i] != _eyeJackPhotos[i]) { same = false; break; }
                if (same) return;
            }
            _eyeJackPhotos = photos;
            _eyeJackPhotosRev++;
        }

        /// <summary>
        /// cueId を _cues から OverlayCueData へ解決する（sa:// / server 相対 URL を実機で開ける URL へ）。
        /// ライブ手動発火（Apply）と CueScheduler の自動発火の両方が使う。未定義なら null。
        /// </summary>
        private OverlayCueData? ResolveCue(string cueId)
        {
            if (string.IsNullOrEmpty(cueId)) return null;
            var def = Array.Find(_cues, c => c != null && c.id == cueId);
            if (def == null) return null;
            return new OverlayCueData
            {
                id = def.id,
                displayName = string.IsNullOrEmpty(def.name) ? def.id : def.name,
                sourceUrl = ShowAssetResolver.Resolve(def.sourceUrl, server),
                maskUrl = ShowAssetResolver.Resolve(def.maskUrl, server),
                strength = def.strength,
                loop = def.loop,
                fadeInSeconds = def.fadeIn,
                fadeOutSeconds = def.fadeOut,
                trimStart = def.trimStart,
                trimEnd = def.trimEnd,
                hasMatch = def.hasMatch,
                matchGain = ToVec3(def.matchGain, 1f),
                matchOffset = ToVec3(def.matchOffset, 0f),
            };
        }

        /// <summary>
        /// 素材 URL（<c>slot://</c> / <c>sa://</c> / 相対）を実 URL へ解決する。TakeRunner へ注入する。
        ///
        /// <c>slot://&lt;name&gt;</c> は **ラン中に卓が束縛する素材**（入口で撮って生成した人形動画など）。
        /// 未束縛なら空文字を返し、TakeRunner がそのカットを飛ばす（§6.4）。
        /// timeline を書き換えずに素材だけ差し替えられるのがこの仕組みの要点
        /// （timeline を保存し直すと発火済みの once 演出が再武装されてしまう）。
        /// </summary>
        private string ResolveAssetUrl(string url)
        {
            if (BakedOnly && TakeSchema.SlotName(url).Length > 0) return "";
            string slot = TakeSchema.SlotName(url);
            if (!string.IsNullOrEmpty(slot))
            {
                string bound = LookupSlot(slot);
                if (string.IsNullOrEmpty(bound))
                {
                    Debug.LogWarning($"[ShowControl] 素材スロット '{slot}' は未束縛 → このカットは飛ばす");
                    return "";
                }
                url = bound;
            }
            return ShowAssetResolver.Resolve(url, server);
        }

        private string LookupSlot(string name)
        {
            foreach (ShowSlotDef? s in _slots)
                if (s != null && s.name == name) return s.url ?? "";
            return "";
        }

        // cue の供給元を timeline(v3) / timeline(v2) / schedule のいずれかに一本化して分配する。
        //   - timeline が v3（schema>=3 または takes を持つ）→ TakeRunner が演出を実行（cue/insert 旧経路は空にする）
        //   - timeline が v2 → 従来どおり TimelineDirector が cues / insert / post を分配
        //   - timeline 不在 → schedule.entries を CueScheduler へ直接供給
        // v3 判定が偽なら**一切挙動が変わらない**（既存 show.json は従来経路のまま = 退避路）。
        // ---- 入れ替わりの無人プレート（0095 の実機側の配線）----------------------------
        //
        // `plate_<カメラid>` cue の静止画を**前もって**読んで持っておき、SwapMorphFx が
        // Begin の瞬間に同期で掴めるようにする。Begin してから読み始めると、覆いの 2.6 秒に
        // ダウンロードが間に合わず、最初の数十フレームだけ CG の形で覆ってから差分へ
        // 切り替わる ＝ 覆いの形が途中で跳ねる。
        private SwapMorphFx? _swapFx;
        private Texture2D?[] _swapPlates = Array.Empty<Texture2D?>();
        private int _swapPlateGen;

        /// <summary>
        /// そのカメラの<b>無人プレート</b>（<c>plate_&lt;カメラid&gt;</c> cue の静止画）。読めていなければ null。
        ///
        /// 読み手は 2 つ: <see cref="SwapMorphFx"/>（覆いの形を背景差分で引く相手）と
        /// <see cref="TakeRunner"/>（覆いが育つあいだ 1 層目へ出す絵）。
        /// </summary>
        public Texture? SwapPlateFor(int cameraIndex)
            => cameraIndex >= 0 && cameraIndex < _swapPlates.Length ? _swapPlates[cameraIndex] : null;

        private void RefreshSwapPlates()
        {
            int gen = ++_swapPlateGen;
            if (_swapPlates.Length != _cameras.Length)
                _swapPlates = new Texture2D?[_cameras.Length];
            for (int i = 0; i < _cameras.Length; i++)
            {
                CameraDef? cam = _cameras[i];
                if (cam == null || string.IsNullOrEmpty(cam.id)) continue;
                OverlayCueData? cue = ResolveCue(TakeSchema.SwapPlateCueId(cam.id));
                if (cue == null || string.IsNullOrEmpty(cue.sourceUrl)) continue;
                _ = LoadSwapPlateAsync(i, cue.sourceUrl, gen);
            }
        }

        private async Task LoadSwapPlateAsync(int index, string url, int gen)
        {
            if (_overlay == null) return;
            try
            {
                Texture2D? tex = await _overlay.LoadStillCachedAsync(url, destroyCancellationToken);
                if (gen != _swapPlateGen || tex == null) return;
                if (index < _swapPlates.Length) _swapPlates[index] = tex;
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Debug.LogWarning($"[ShowControl] 無人プレートの先読みに失敗（camera index {index}）: {e.Message}");
            }
        }

        private void PushCueSource()
        {
            if (TimelineActive && timelineDirector != null)
            {
                // **版に関係なく v3（演出・カット）として実行する**（2026-07-25 に一本化）。
                // 端末キャッシュや焼き込みに残っている v2（cues[] / insert）は EnsureTakes が
                // takes[] へ決定的に変換する（冪等・TimelineMigration が唯一の変換点）。
                TimelineMigration.EnsureTakes(_timeline!);
                timelineDirector.SetTimeline(_timeline!.segments);
                WarnUnreachableSegments();
            }
            else
            {
                timelineDirector?.Clear();
                cueScheduler?.SetScheduleFromDefs(_schedule?.entries);
            }

            // cue の定義が変わるたびに、入れ替わり用の無人プレートも先読みし直す
            // （URL は ScreenOverlayController がキャッシュするので、同じ素材なら 2 回目は無料）。
            RefreshSwapPlates();
            PrefetchTimelineClips();
        }

        /// <summary>
        /// 台本が参照する素材（動画・静止画・マスク）を先に落としておく。
        ///
        /// <b>カットの尺は発火時刻から数える</b>ので、発火してから落とすとその分だけ画に出る時間が減る。
        /// 実測（2026-08-22・testassets 24KB）で発火 → 画まで最大 280ms、うち落とすのに 152ms。
        /// 0.5〜1.2 秒刻みで差し替える演出（2 周目 C の接近）では体験に直接出るし、
        /// 本番素材は数 MB なのでさらに伸びる。無人プレートの先読みと同じ立ち位置。
        ///
        /// ⚠ 消えるのは落とす時間だけ。用意（Prepare・実測 145ms）は発火時に必ず掛かる。
        ///
        /// <b>静止画を含めたのは 2026-08-23。</b>それまで温めていたのは <c>plate_*</c> だけで、
        /// 生成素材は発火してから読み始めていた。実測（1 周目 B の手形・541KB の PNG）で
        /// 発火 t=54.01 → 画 t=54.22 ＝ <b>210ms のあいだ生映像が見えていた</b>。
        /// 同じ走行の無人プレート（3 周目 B / C）は先読み済みなので 0ms で出ている。
        /// </summary>
        private void PrefetchTimelineClips()
        {
            if (_overlay == null || _timeline?.segments == null) return;
            var stills = new List<string>();
            foreach (ShowTimelineSegmentDef seg in _timeline.segments)
            {
                if (seg?.takes == null) continue;
                foreach (ShowTakeDef take in seg.takes)
                {
                    if (take?.steps == null) continue;
                    // 報告の押し始めに出す動画も先読みする。ここを漏らすと初回だけ
                    // DL とマスク読込が長押し中に走り、押し終わってから動き始める。
                    CollectCueAssets(take.markStartCueId, stills);
                    foreach (ShowStepDef step in take.steps)
                    {
                        if (step == null) continue;
                        CollectCueAssets(step.cueId, stills);
                        CollectCueAssets(step.overlay2CueId, stills);
                    }
                }
            }
            if (stills.Count > 0) _ = PrefetchStillsAsync(stills, ++_prefetchStillGen);
        }

        /// <summary>
        /// cue 1 つぶんの素材を先読みへ回す。動画はここで落とし始め、静止画とマスクは
        /// <paramref name="stills"/> へ積む（逐次に落とすため）。
        ///
        /// <b>マスクは動画 cue にも付く。</b><c>PlayCueAsync</c> はマスクを素材より先に await するので、
        /// 素材だけ温めてもマスクのぶんの待ちが残る。
        /// </summary>
        private void CollectCueAssets(string cueId, List<string> stills)
        {
            if (_overlay == null || string.IsNullOrEmpty(cueId)) return;
            OverlayCueData? cue = ResolveCue(cueId);
            if (cue == null) return;
            if (cue.SourceIsVideo)
            {
                if (!string.IsNullOrEmpty(cue.sourceUrl)) _overlay.PrefetchVideo(cue.sourceUrl);
            }
            else if (!string.IsNullOrEmpty(cue.sourceUrl) && !stills.Contains(cue.sourceUrl))
            {
                stills.Add(cue.sourceUrl);
            }
            if (!string.IsNullOrEmpty(cue.maskUrl) && !stills.Contains(cue.maskUrl))
                stills.Add(cue.maskUrl);
        }

        // 静止画の先読みの世代（cue の定義が変わったら古いループを捨てる）。
        private int _prefetchStillGen;

        /// <summary>
        /// 静止画素材とマスクを<b>1 本ずつ</b>読んでキャッシュへ乗せる。
        ///
        /// 一気に投げないのは、同じ Wi-Fi を 3 台ぶんの MJPEG が使っているため。
        /// 体験が始まるまでに終わればよく、急ぐ必要は無い。
        /// </summary>
        private async Task PrefetchStillsAsync(List<string> urls, int gen)
        {
            if (_overlay == null) return;
            foreach (string url in urls)
            {
                if (gen != _prefetchStillGen) return;   // 新しい定義が来た
                try { await _overlay.LoadStillCachedAsync(url, destroyCancellationToken); }
                catch (OperationCanceledException) { return; }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ShowControl] 素材の先読みに失敗: {url} ({e.Message})");
                }
            }
        }

        // 直近で警告した内容（同じ設定で毎回吠えない）。
        private string _lastUnreachableWarn = "";

        /// <summary>
        /// 体験中に踏まれない区間に演出が置かれていたら名指しで警告する。
        ///
        /// 卓の本番前チェックも同じ判定を持つが、**実機が使う設定は卓と食い違いうる**
        /// （焼き込み / 端末キャッシュ / ライブの 3 系統）。黙って落とすと著作者には発見手段が無いので、
        /// 実機ログにも 1 回出す。<b>実行は止めない</b>（区間が来れば従来どおり演出は走る）。
        /// </summary>
        private void WarnUnreachableSegments()
        {
            ShowTimelineSegmentDef[]? segs = _timeline?.segments;
            if (segs == null || segs.Length == 0) return;

            int laps = _run?.ResolveTotalLaps() ?? ShowRunDefaults.TotalLaps;
            int[]? order = CourseOrder;
            string dead = "";
            foreach (ShowTimelineSegmentDef? s in segs)
            {
                if (s == null || s.takes == null || s.takes.Length == 0) continue;
                if (ShowRunReach.IsSegmentReachable(s.lap, s.camera, laps, order)) continue;
                if (dead.Length > 0) dead += " ";
                dead += $"{s.lap}周目/カメラ{s.camera + 1}({s.takes.Length}本)";
            }
            if (dead.Length == 0) { _lastUnreachableWarn = ""; return; }
            if (dead == _lastUnreachableWarn) return;
            _lastUnreachableWarn = dead;
            Debug.LogWarning($"[ShowControl] 体験中に踏まれない区間に演出があります（出ません）: {dead} " +
                             $"— 走り切るのは {laps} 周と帰りのスタート区間まで");
        }
    }
}
