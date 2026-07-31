#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Video;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// ScreenComposite シェーダのオーバーレイ側（事前撮影クリップ × マスク）を駆動する。
    /// MjpegScreen と同じ Renderer のマテリアルインスタンスへプロパティを書き込む。
    ///
    /// 発火方法（すべて PlayCue(OverlayCueData) に集約、最後の命令が勝つ）:
    ///   - キーボード（Editor + Link 運用でのオペレータ操作）: bindings の key
    ///   - Web オペレータ卓: ShowControlClient → PlayCue(OverlayCueData)
    ///   - コード（ゾーントリガ等）: PlayCue / StopOverlay
    /// ソースは ローカル（VideoClip / Texture2D）と URL（mp4 直再生 / png ダウンロード）の両対応。
    /// </summary>
    [RequireComponent(typeof(Renderer))]
    public sealed class ScreenOverlayController : MonoBehaviour
    {
        private static readonly int OverlayTexId = Shader.PropertyToID("_OverlayTex");
        private static readonly int OverlayScaleId = Shader.PropertyToID("_OverlayScale");
        private static readonly int MaskTexId = Shader.PropertyToID("_MaskTex");
        private static readonly int OverlayStrengthId = Shader.PropertyToID("_OverlayStrength");
        private static readonly int OverlayGainId = Shader.PropertyToID("_OverlayGain");
        private static readonly int OverlayOffsetId = Shader.PropertyToID("_OverlayOffset");

        [Serializable]
        public struct CueBinding
        {
            public OverlayCue? cue;
            [Tooltip("Editor / Link 運用でこの cue を発火するキー。None なら手動コードのみ。")]
            public KeyCode key;
        }

        [SerializeField] private CueBinding[] bindings = Array.Empty<CueBinding>();

        [Tooltip("全オーバーレイ停止キー。")]
        [SerializeField] private KeyCode stopKey = KeyCode.Alpha0;

        [Tooltip("VideoClip 用 RenderTexture の縦解像度。クリップのアスペクトで横を決める。")]
        [SerializeField] private int videoHeightPx = 720;

        [Tooltip("動画 cue の Prepare がこの秒数（unscaled realtime）を超えたら失敗とみなし cue を中止して live へ戻す。" +
                 "DL 済みローカル mp4 の Prepare には十分な既定。現場で大容量動画に不足なら延長する。")]
        [SerializeField, Min(0.5f)] private float prepareTimeoutSec = 6f;

        private Material? _material;
        private MjpegScreen? _screen;
        private VideoPlayer? _player;
        private RenderTexture? _videoRt;
        private OverlayCueData? _current;

        // URL ロード物のキャッシュ（マスク / 静止画）。現場で同じ cue を繰り返し叩く前提。
        private readonly Dictionary<string, Texture2D> _urlTextureCache = new();
        // 動画 URL → DL 済みローカル mp4 パスのキャッシュ。
        private readonly Dictionary<string, string> _videoFileCache = new();
        // 世代（stale ロード/Prepare 破棄）+ 動画 Prepare ライフサイクル（保留/タイムアウト/受理/エラー中止）の純判定。
        // PlayCue が非同期ロードを挟む間に次の PlayCue が来たら古い方を破棄する。動画 cue の Prepare 失敗経路
        // （errorReceived / タイムアウト）で _current を解放し、CameraSwitchDirector が cueActive=true のまま
        // 自動切替を恒久凍結する穴を断つ（B3）。
        private readonly OverlayPlaybackLogic _logic = new();

        /// <summary>現在のオーバーレイ（フェードアウト中も含む）。null なら停止。</summary>
        public OverlayCueData? Current => _current;

        /// <summary>
        /// いま画面へ書いている合成の重み（シェーダの <c>_OverlayStrength</c> そのもの）。
        /// <b>「素材が実際に画面へ混ざったか」の唯一の証拠</b> — <see cref="Current"/> は動画の
        /// Prepare 完了<b>前</b>に代入されるので、cue の発火だけでは素材が来た証明にならない。
        /// </summary>
        public float Strength => _strength;

        /// <summary>スクリーンの material を掴めているか。false なら合成は 1 画素も効かない。</summary>
        public bool HasMaterial => _material != null;

        // フレーム列ソースの再生開始時刻（Time.time）。
        private float _framesStart;

        // フェード状態。target に向かって _strength を進める。
        private float _strength;
        private float _target;
        private float _fadeSpeed = 4f;
        private bool _stopWhenFadedOut;

        private void Awake()
        {
            // MjpegScreen が Awake で .material をインスタンス化するので同じものを共有する。
            _screen = GetComponent<MjpegScreen>();
            _material = GetComponent<Renderer>().material;

            _player = gameObject.AddComponent<VideoPlayer>();
            _player.playOnAwake = false;
            _player.renderMode = VideoRenderMode.RenderTexture;
            _player.audioOutputMode = VideoAudioOutputMode.None;
            _player.skipOnDrop = true;
            _player.prepareCompleted += OnPrepared;
            _player.loopPointReached += OnVideoEnd; // 自然終端 → 自動フェードアウト（ループしない cue のみ）
            _player.errorReceived += OnVideoError;

            ApplyStrength(0f);
        }

        private void OnDestroy()
        {
            if (_videoRt != null)
            {
                _videoRt.Release();
                if (Application.isPlaying) Destroy(_videoRt);
                else DestroyImmediate(_videoRt);
                _videoRt = null;
            }
            foreach (var tex in _urlTextureCache.Values)
            {
                if (tex != null)
                {
                    if (Application.isPlaying) Destroy(tex);
                    else DestroyImmediate(tex);
                }
            }
            _urlTextureCache.Clear();

            // DL 済み動画キャッシュ（temporaryCachePath の mp4）を掃除する。放置すると
            // 新しい AI 動画 cue を叩くたびに端末ストレージへ mp4 が無限に積もる。
            foreach (var path in _videoFileCache.Values)
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception e) { Debug.LogWarning($"[ScreenOverlay] video cache delete failed: {e.Message}"); }
            }
            _videoFileCache.Clear();
        }

        private void Update()
        {
            CheckPrepareTimeout();
            TickFrames();

            for (int i = 0; i < bindings.Length; i++)
            {
                if (bindings[i].key != KeyCode.None && Input.GetKeyDown(bindings[i].key))
                {
                    PlayCue(i);
                }
            }
            if (Input.GetKeyDown(stopKey)) StopOverlay();

            // 動画の再生区間終端で自動停止（ループしない cue・trimEnd>0 のとき）
            if (_current != null && !_current.loop && _current.trimEnd > 0f && !_stopWhenFadedOut
                && _player != null && _player.isPlaying && _player.time >= _current.trimEnd)
            {
                StopOverlay();
            }

            // フェード進行
            if (!Mathf.Approximately(_strength, _target))
            {
                _strength = Mathf.MoveTowards(_strength, _target, _fadeSpeed * Time.deltaTime);
                ApplyStrength(_strength);
            }
            else if (_stopWhenFadedOut && _strength <= 0f)
            {
                _stopWhenFadedOut = false;
                if (_player != null && _player.isPlaying) _player.Stop();
                _current = null;
            }
        }

        /// <summary>bindings の index で cue を発火。</summary>
        public void PlayCue(int index)
        {
            if (index < 0 || index >= bindings.Length) return;
            var cue = bindings[index].cue;
            if (cue != null) PlayCue(OverlayCueData.From(cue));
        }

        /// <summary>ScriptableObject 版 cue を発火（互換 API）。</summary>
        public void PlayCue(OverlayCue cue) => PlayCue(OverlayCueData.From(cue));

        /// <summary>
        /// cue を発火。再生中の cue があれば置き換える（最後の命令が勝つ）。
        /// 戻り値は**この発火を指すトークン**で、<see cref="IsFinished"/> に渡すと終端を判定できる。
        /// 発火できなかった場合（未初期化）は -1。
        /// </summary>
        public int PlayCue(OverlayCueData data)
        {
            if (_material == null || _player == null) return -1;
            int gen = _logic.BeginPlay();
            _ = RunPlayCueAsync(data, gen, destroyCancellationToken);
            return gen;
        }

        /// <summary>
        /// <see cref="PlayCue(OverlayCueData)"/> が返したトークンの再生が決着したか
        /// （自然終端 / 中止 / 別 cue や停止で置き換えられた）。
        ///
        /// **<c>Current == null</c> を直接見てはいけない**。<see cref="PlayCue(OverlayCueData)"/> は
        /// マスク / 静止画 / 動画のロードを await するため、発火直後の数フレームは <c>Current</c> が
        /// まだ null で、素朴に見ると「1 フレームで終わった」と誤判定する（2026-07-26 監査 HIGH）。
        /// </summary>
        public bool IsFinished(int token)
        {
            if (token < 0) return true;
            if (token != _logic.Generation) return true;   // 新しい発火 / 停止で置き換わった
            if (_logic.IsLoading(token)) return false;     // ロード中（Current 未確定）
            return _current == null;
        }

        // fire-and-forget の例外を無音で失わないための wrapper。
        // ここで catch しないと unobserved task exception になり「演出が出ないのにログも無い」になる。
        // finally で必ずロード中フラグを下ろす（どの return 経路・例外でも IsFinished が固まらないように）。
        private async Task RunPlayCueAsync(OverlayCueData data, int gen, CancellationToken ct)
        {
            try { await PlayCueAsync(data, gen, ct); }
            catch (OperationCanceledException) { }
            catch (Exception e) { Debug.LogError($"[ScreenOverlay] PlayCue '{data.displayName}' failed: {e}"); }
            finally { _logic.EndLoad(gen); }
        }

        private async Task PlayCueAsync(OverlayCueData data, int gen, CancellationToken ct)
        {
            // 1) マスク（URL ならロード、ローカルならそのまま、無指定なら全面白）
            Texture? mask = data.maskTexture;
            if (mask == null && !string.IsNullOrEmpty(data.maskUrl))
            {
                mask = await LoadTextureAsync(data.maskUrl, ct);
                if (gen != _logic.Generation || ct.IsCancellationRequested) return; // 古い発火は破棄
                if (mask == null)
                {
                    // maskUrl 指定ありでロード失敗 → 白フォールバックに落ちると全面差し替え（黒背景素材なら
                    // live が全面黒）になる。cue を中止して live 映像を守る（動画/静止画パス両方をここで防ぐ）。
                    // maskUrl 未指定（意図的な全面差し替え）はこの分岐に入らず従来どおり白フォールバック。
                    Debug.LogError($"[ScreenOverlay] mask load failed: {data.maskUrl} — cue '{data.displayName}' を中止（live 維持）");
                    return;
                }
            }

            // 2) ソース
            if (data.SourceIsFrames)
            {
                // 端末内録画（JPEG フレーム列）。デコード経路はライブ映像とまったく同じなので絵が一致する。
                _current = data;
                _stopWhenFadedOut = false;
                _material!.SetTexture(MaskTexId, mask != null ? mask : Texture2D.whiteTexture);
                ApplyColorMatch(data);
                _player!.Stop();
                _framesStart = Time.time;
                data.frames!.Tick(0f);
                SetOverlayTexture(data.frames.Texture, data.frames.Aspect);
                BeginFadeIn(data);
            }
            else if (data.SourceIsVideo)
            {
                _current = data;
                // StopOverlay のフェードアウト進行中に新 cue が来たケース:
                // クリアしないと Update のフェード完了分岐が preparing 中の player を Stop し
                // _current を null にして、新 cue が無言で殺される。
                _stopWhenFadedOut = false;
                _material!.SetTexture(MaskTexId, mask != null ? mask : Texture2D.whiteTexture);
                ApplyColorMatch(data);
                _player!.Stop();
                if (data.clip != null)
                {
                    _player.source = VideoSource.VideoClip;
                    _player.clip = data.clip;
                }
                else
                {
                    // Android ネイティブ VideoPlayer は Python http.server(HTTP/1.0) からの
                    // HTTP ストリーミングを扱えず NuCachedSource2 error -1 で落ちる。
                    // UnityWebRequest（=画像で実証済みのスタック）でローカルに DL してから再生する。
                    string localUrl = await GetLocalVideoUrlAsync(data.sourceUrl, ct);
                    if (gen != _logic.Generation || ct.IsCancellationRequested) return;
                    _player.source = VideoSource.Url;
                    _player.url = localUrl;
                }
                _player.isLooping = data.loop;
                _logic.BeginPrepare(gen, Time.realtimeSinceStartup);
                _player.Prepare(); // 完了後 OnPrepared で RT 接続 + 再生 + フェードイン
            }
            else
            {
                Texture? still = data.stillImage;
                if (still == null && !string.IsNullOrEmpty(data.sourceUrl))
                {
                    still = await LoadTextureAsync(data.sourceUrl, ct);
                    if (gen != _logic.Generation || ct.IsCancellationRequested) return;
                }
                if (still == null)
                {
                    Debug.LogWarning($"[ScreenOverlay] cue '{data.displayName}' has no source.");
                    return;
                }
                _current = data;
                _stopWhenFadedOut = false; // 動画パスと同じくフェードアウト完了分岐から守る
                _material!.SetTexture(MaskTexId, mask != null ? mask : Texture2D.whiteTexture);
                ApplyColorMatch(data);
                // 無条件 Stop: preparing 中（isPlaying=false）の動画 cue も中断しないと、
                // 後から prepareCompleted が届いてこの静止画を動画 RT で上書きする。
                _player!.Stop();
                SetOverlayTexture(still, (float)still.width / still.height);
                BeginFadeIn(data);
            }
        }

        /// <summary>
        /// 色統計マッチング（企画書 2.3）を素材へ掛ける係数をマテリアルへ書く。
        /// 卓が cue 保存時に「素材の統計を実写プレートへ合わせる」Reinhard を解き、per-channel の
        /// gain/offset に落として配っている。ここは 2 本の SetVector だけ。
        /// **指定が無い cue でも必ず書く**（前の cue の係数が残って次の素材の色が変わるのを防ぐ）。
        /// </summary>
        private void ApplyColorMatch(OverlayCueData data)
        {
            if (_material == null) return;
            Vector3 g = data.hasMatch ? data.matchGain : Vector3.one;
            Vector3 o = data.hasMatch ? data.matchOffset : Vector3.zero;
            _material.SetVector(OverlayGainId, new Vector4(g.x, g.y, g.z, 0f));
            _material.SetVector(OverlayOffsetId, new Vector4(o.x, o.y, o.z, 0f));
        }

        /// <summary>現在のオーバーレイをフェードアウトして停止。</summary>
        public void StopOverlay()
        {
            // _current==null でも世代を進める。ロード await 中の cue（PlayCueAsync が _current 代入前）は
            // _current が null のままなので、この return より前に世代を上げないと in-flight のロード完了が
            // 生き残って stop 後に live を差し替える穴が残る。
            _logic.Stop(); // ロード途中の発火も破棄・進行中 Prepare 保留も無効化
            if (_current == null) return;
            float fade = Mathf.Max(_current.fadeOutSeconds, 1e-3f);
            _target = 0f;
            _fadeSpeed = Mathf.Max(_strength, 0.01f) / fade;
            _stopWhenFadedOut = true;
        }

        // 動画 URL をローカルへ DL して file:// パスを返す（Android ネイティブ HTTP ストリーミング回避）。
        // 同一 URL はキャッシュして再 DL しない。失敗時は元 URL を返してストリーミングへ fallback。
        private async Task<string> GetLocalVideoUrlAsync(string url, CancellationToken ct)
        {
            if (_videoFileCache.TryGetValue(url, out var cached) && File.Exists(cached))
                return "file://" + cached;
            string path = Path.Combine(Application.temporaryCachePath, "ovr_" + StableFileKey(url) + ".mp4");
            try
            {
                using var req = UnityWebRequest.Get(url);
                req.downloadHandler = new DownloadHandlerFile(path);
                req.timeout = 20;
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Yield();
                }
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[ScreenOverlay] video download failed: {url} ({req.error}) → ストリーミングへ fallback");
                    TryDeleteFile(path); // DownloadHandlerFile が書きかけた部分ファイルを残さない
                    return url;
                }
                _videoFileCache[url] = path;
                Debug.Log($"[ScreenOverlay] video cached: {url} -> {path}");
                return "file://" + path;
            }
            catch (OperationCanceledException) { TryDeleteFile(path); throw; }
            catch (Exception e)
            {
                Debug.LogWarning($"[ScreenOverlay] video download error: {e.Message} → ストリーミングへ fallback");
                TryDeleteFile(path);
                return url;
            }
        }

        // URL → 衝突しない安定ファイル名キー。GetHashCode は 32bit で衝突可能・
        // Unity バージョン間の安定保証もないため SHA1 の hex を使う。
        private static string StableFileKey(string url)
        {
            using var sha = System.Security.Cryptography.SHA1.Create();
            var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(url));
            var sb = new System.Text.StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception e) { Debug.LogWarning($"[ScreenOverlay] partial file delete failed: {e.Message}"); }
        }

        private async Task<Texture2D?> LoadTextureAsync(string url, CancellationToken ct)
        {
            if (_urlTextureCache.TryGetValue(url, out var cached) && cached != null) return cached;
            try
            {
                using var req = UnityWebRequestTexture.GetTexture(url, nonReadable: true);
                req.timeout = 5;
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Yield();
                }
                if (req.result != UnityWebRequest.Result.Success) return null;
                var tex = DownloadHandlerTexture.GetContent(req);
                _urlTextureCache[url] = tex;
                return tex;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                Debug.LogWarning($"[ScreenOverlay] texture load error: {url} ({e.Message})");
                return null;
            }
        }

        private void OnPrepared(VideoPlayer vp)
        {
            // stale な Prepare 完了（準備中に別 cue へ切り替え済み）は現行 cue を乗っ取らない。
            if (!_logic.AcceptPrepared()) return;
            var cue = _current;
            if (cue == null || _material == null) return;

            int w = (int)vp.width, h = (int)vp.height;
            if (w <= 0 || h <= 0) { w = 16; h = 9; }
            float aspect = (float)w / h;

            int rtH = Mathf.Min(videoHeightPx, h);
            int rtW = Mathf.RoundToInt(rtH * aspect);
            if (_videoRt == null || _videoRt.width != rtW || _videoRt.height != rtH)
            {
                if (_videoRt != null)
                {
                    _videoRt.Release();
                    Destroy(_videoRt);
                }
                _videoRt = new RenderTexture(rtW, rtH, 0, RenderTextureFormat.ARGB32)
                {
                    name = "ScreenOverlayVideo",
                    useMipMap = false,
                };
                _videoRt.Create();
            }

            vp.targetTexture = _videoRt;
            SetOverlayTexture(_videoRt, aspect);
            if (cue.trimStart > 0f) vp.time = cue.trimStart; // 再生区間の頭へシーク
            vp.Play();
            BeginFadeIn(cue);
        }

        // 動画が自然終端（trimEnd 未指定で最後まで再生）に達した時。ループしない cue を自動で戻す。
        private void OnVideoEnd(VideoPlayer vp)
        {
            if (_current != null && !_current.loop && !_stopWhenFadedOut) StopOverlay();
        }

        private void OnVideoError(VideoPlayer vp, string message)
        {
            Debug.LogWarning($"[ScreenOverlay] VideoPlayer error: {message}");
            // 現行世代の動画 cue に対するエラーなら畳んで live を守る（_current 残留で
            // CameraSwitchDirector が cueActive=true のまま自動切替を恒久凍結するのを防ぐ）。
            if (_current == null || !_current.SourceIsVideo) return;
            if (_logic.ShouldAbortOnError()) AbortCurrentCue("video error");
        }

        // フレーム列ソース（端末内録画）を進める。終端に達したら動画の自然終端と同じ扱いで畳む
        // （＝ untilClipEnd のカットがここで終わる）。
        private void TickFrames()
        {
            if (_current == null || !_current.SourceIsFrames || _stopWhenFadedOut) return;
            var seq = _current.frames!;
            if (!seq.Tick(Time.time - _framesStart)) StopOverlay();
        }

        // Update から毎フレーム呼ぶ。unscaled realtime で timeScale=0 でも進む。
        private void CheckPrepareTimeout()
        {
            if (_logic.TimedOut(Time.realtimeSinceStartup, prepareTimeoutSec)
                && _current != null && _current.SourceIsVideo)
                AbortCurrentCue("prepare timeout");
        }

        // cue を即畳んで live へハードカット（Prepare 未完なので表示は出ていない）。_player/_material は null 安全。
        private void AbortCurrentCue(string reason)
        {
            if (_player != null && _player.isPlaying) _player.Stop();
            _current = null;
            _stopWhenFadedOut = false;
            _strength = 0f;
            _target = 0f;
            ApplyStrength(0f);
            Debug.LogWarning($"[ScreenOverlay] cue aborted ({reason}) — live 維持");
        }

        private void BeginFadeIn(OverlayCueData cue)
        {
            float fade = Mathf.Max(cue.fadeInSeconds, 1e-3f);
            _target = cue.strength;
            _fadeSpeed = Mathf.Max(_target - _strength, 0.01f) / fade;
        }

        private void SetOverlayTexture(Texture tex, float srcAspect)
        {
            if (_material == null) return;
            float screenAspect = _screen != null ? _screen.ScreenAspect : 16f / 9f;
            Vector2 scale = srcAspect < screenAspect
                ? new Vector2(srcAspect / screenAspect, 1f)
                : new Vector2(1f, screenAspect / srcAspect);
            _material.SetTexture(OverlayTexId, tex);
            _material.SetVector(OverlayScaleId, new Vector4(scale.x, scale.y, 0f, 0f));
        }

        private void ApplyStrength(float v)
        {
            _material?.SetFloat(OverlayStrengthId, v);
        }
    }
}
