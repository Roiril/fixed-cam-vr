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

        // 第 2 の差し替え層の書き先。**uniform を書くのは CameraFeelFx だけ**（書き手が 2 つになると
        // どちらが最後に書いたかで画が変わる）。ここは素材を読んで渡すだけ。
        private CameraFeelFx? _feelFx;
        private bool _warnedNoFeelFx;

        // 第 2 層の世代（stale ロード破棄）と、いま出している / 出そうとしている cue id。
        // id が同じなら読み直さない — カットごとに毎回書かれるので、同じ素材を跨ぐカットで
        // 毎回ロードを起こさないため。失敗した時は "" へ戻して次のカットで再挑戦できるようにする。
        private int _layer2Gen;
        private string _layer2CueId = "";

        // URL ロード物のキャッシュ（マスク / 静止画）。現場で同じ cue を繰り返し叩く前提。
        private readonly Dictionary<string, Texture2D> _urlTextureCache = new();
        // 動画 URL → DL 済みローカル mp4 パスのキャッシュ。
        private readonly Dictionary<string, string> _videoFileCache = new();
        // 先読み中の URL（同じ素材を 2 回落としに行かないための門）。
        private readonly HashSet<string> _prefetching = new();

        // 動画カットが「発火してから画に出るまで」の内訳。カットの尺は発火時刻から数える
        // （TakeRunnerLogic.StepEndTime）ので、ここが伸びた分だけ画に出る時間が減る。
        // 0.5〜1.2 秒刻みで差し替える演出（2 周目 C の接近）ではそれが体験に直接出る。
        private float _clipFireAt;
        private float _clipDlSec;
        private bool _clipCached;
        private string _clipCueId = "";
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

        /// <summary>
        /// 動画カットが画に出るまでの内訳（cue id / ダウンロード秒 / Prepare 秒 / キャッシュ済みか）。
        ///
        /// <b>カットの尺は発火時刻から数える</b>ので、ここが伸びるとその分だけ画に出る時間が減る。
        /// 短いカットを連続で差し替える演出では、これが尺を食い切って
        /// 「著作した秒数は流れているのに画は前のカットのまま」になりうる。
        /// </summary>
        public event Action<string, float, float, bool>? ClipLatency;


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
            var rend = GetComponent<Renderer>();
            if (rend == null)
            {
                // 他の writer に合わせて null を許容する（MjpegScreen 側と対）。
                Debug.LogError("[ScreenOverlay] Renderer が無い — 差し替えが 1 枚も出せません");
                enabled = false;
                return;
            }
            _material = rend.material;

            // 前のセッションが残した動画キャッシュを掃除する（2026-08-30）。
            // ⚠ Quest のアプリは force-kill されるのが普通なので **OnDestroy はしばしば走らない**。
            //    素材 URL は時刻入り（`/captures/…_20260823_194234.mp4`）で撮り直すたびに変わるため、
            //    掃除しないと端末に mp4 が積み続ける（1 本 1.0〜1.6MB）。しかも SegmentRecorder が
            //    同じ領域を使うので、**録画の容量を食う**。
            //    起動時点で `_videoFileCache` は空 ＝ 残っている `ovr_*.mp4` は全部前のセッションの孤児。
            PruneOrphanVideoCache();

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

        /// <summary>
        /// 前のセッションが残した <c>ovr_*.mp4</c> を消す。<c>SegmentRecorder</c> は
        /// <c>rec/</c> の下に書くので巻き込まない（パターンで絞る）。失敗しても体験は続ける。
        /// </summary>
        private static void PruneOrphanVideoCache()
        {
            try
            {
                string dir = Application.temporaryCachePath;
                if (!Directory.Exists(dir)) return;
                int n = 0;
                long bytes = 0;
                foreach (string p in Directory.GetFiles(dir, "ovr_*.mp4"))
                {
                    try
                    {
                        bytes += new FileInfo(p).Length;
                        File.Delete(p);
                        n++;
                    }
                    catch { /* 使用中・権限。1 本落とせなくても残りは掃除する */ }
                }
                if (n > 0)
                    Debug.Log($"[ScreenOverlay] 前のセッションの動画キャッシュを掃除した: {n} 本 / {bytes / (1024 * 1024)}MB");
            }
            catch (Exception e) { Debug.LogWarning($"[ScreenOverlay] 動画キャッシュの掃除に失敗: {e.Message}"); }
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
            // 発火時刻はここで取る（マスクや素材のロードもカットの尺を食うので、動画分岐の中では遅い）。
            _clipFireAt = Time.realtimeSinceStartup;
            _clipDlSec = 0f;
            _clipCached = false;
            _clipCueId = string.IsNullOrEmpty(data.id) ? (data.displayName ?? "?") : data.id;
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
                    float dlStart = Time.realtimeSinceStartup;
                    _clipCached = _videoFileCache.ContainsKey(data.sourceUrl);
                    string localUrl = await GetLocalVideoUrlAsync(data.sourceUrl, ct);
                    _clipDlSec = Time.realtimeSinceStartup - dlStart;
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

        // ---- 第 2 の差し替え層（canon/LEDGER.md 0050）------------------------------------
        //
        //   1 層目（_OverlayTex）と**同時に**別の素材を出すための層。要るのは左右分割のカットだけで、
        //   3 周目 A（左＝1 周目の録画 / 右＝環境＋人形）と 4 周目 A（左＝大量の人形 / 右＝体験者人形）が使う。
        //   どちらへ出るかを決めるのは**マスク**で、シェーダに左右の区別は無い。
        //
        //   ⚠ **静止画専用**。ここに載るのは無人プレートと生成画像だけなので、動画の Prepare も
        //     フレーム列の Tick も持たない。動画を指したカットは出さずに警告する
        //     （黙って 1 層目と同じ絵を出すより、出ない方が原因に届く）。
        //   ⚠ **フェードしない**。切り替えは乱れが覆う（LEDGER 0050「向きが逆になるのは一瞬なので、
        //     映像の乱れでごまかそう」「右半分も、映像の乱れで自分を消して人形を出そう」）。
        //     ここでフェードを掛けると、乱れの下でゆっくり混ざって「すり替わった」に見えない。

        /// <summary>
        /// 第 2 の差し替え層へ素材を出す（カットの <c>overlay2CueId</c>）。null で畳む。
        /// 同じ cue を続けて指すカットでは読み直さない。
        /// </summary>
        public void ShowSecondLayer(OverlayCueData? cue)
        {
            if (cue == null) { ClearSecondLayer(); return; }
            if (_layer2CueId == cue.id && !string.IsNullOrEmpty(cue.id)) return;
            int gen = ++_layer2Gen;
            _layer2CueId = cue.id;
            _ = RunShowSecondLayerAsync(cue, gen, destroyCancellationToken);
        }

        /// <summary>
        /// 第 2 の差し替え層を畳む。<b>カットごとに必ず通る</b>ので、
        /// 指していないカットへ移った瞬間に消える（前のカットの素材を引き継がせない）。
        /// </summary>
        public void ClearSecondLayer()
        {
            _layer2Gen++;          // in-flight のロードを無効化（読み終わってから書かれるのを防ぐ）
            _layer2CueId = "";
            ResolveFeelFx();
            _feelFx?.SetOverlay2(null, null, Vector2.one, 0f);
        }

        // fire-and-forget の例外を無音で失わない（PlayCue と同じ流儀）。
        private async Task RunShowSecondLayerAsync(OverlayCueData cue, int gen, CancellationToken ct)
        {
            try { await ShowSecondLayerAsync(cue, gen, ct); }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Debug.LogError($"[ScreenOverlay] 第 2 層 '{cue.displayName}' の読み込みに失敗: {e}");
                if (gen == _layer2Gen) _layer2CueId = "";
            }
        }

        private async Task ShowSecondLayerAsync(OverlayCueData cue, int gen, CancellationToken ct)
        {
            if (cue.SourceIsFrames || cue.SourceIsVideo)
            {
                Debug.LogWarning($"[ScreenOverlay] 第 2 層は静止画専用 — cue '{cue.displayName}' は動画/録画なので出さない");
                if (gen == _layer2Gen) { _layer2CueId = ""; ClearSecondLayerNow(); }
                return;
            }

            // 1) マスク。**指定があってロードに失敗したら出さない**（1 層目と同じ規約）。
            //    白フォールバックに落ちると全面が第 2 層になり、1 層目ごと画を潰す。
            Texture? mask = cue.maskTexture;
            if (mask == null && !string.IsNullOrEmpty(cue.maskUrl))
            {
                mask = await LoadTextureAsync(cue.maskUrl, ct);
                if (gen != _layer2Gen || ct.IsCancellationRequested) return;
                if (mask == null)
                {
                    Debug.LogError($"[ScreenOverlay] 第 2 層のマスク読込に失敗: {cue.maskUrl} — " +
                                   $"cue '{cue.displayName}' を中止（1 層目を守る）");
                    _layer2CueId = "";
                    ClearSecondLayerNow();
                    return;
                }
            }

            // 2) 素材（静止画）。
            Texture? still = cue.stillImage;
            if (still == null && !string.IsNullOrEmpty(cue.sourceUrl))
            {
                still = await LoadTextureAsync(cue.sourceUrl, ct);
                if (gen != _layer2Gen || ct.IsCancellationRequested) return;
            }
            if (still == null)
            {
                Debug.LogWarning($"[ScreenOverlay] 第 2 層の素材が無い: cue '{cue.displayName}'");
                _layer2CueId = "";
                ClearSecondLayerNow();
                return;
            }

            ResolveFeelFx();
            if (_feelFx == null)
            {
                if (!_warnedNoFeelFx)
                {
                    _warnedNoFeelFx = true;
                    Debug.LogWarning("[ScreenOverlay] CameraFeelFx が見つからない → 第 2 層は 1 画素も出ない");
                }
                _layer2CueId = "";
                return;
            }
            float strength = cue.strength > 0f ? Mathf.Clamp01(cue.strength) : 1f;
            _feelFx.SetOverlay2(still, mask, ContainScale((float)still.width / still.height), strength);
        }

        // 世代を進めずに畳む（既に自分の世代であることを確認済みの失敗経路から呼ぶ）。
        private void ClearSecondLayerNow()
        {
            ResolveFeelFx();
            _feelFx?.SetOverlay2(null, null, Vector2.one, 0f);
        }

        private void ResolveFeelFx()
        {
            if (_feelFx != null) return;
            _feelFx = GetComponent<CameraFeelFx>();
            if (_feelFx == null) _feelFx = FindObjectOfType<CameraFeelFx>();
        }

        /// <summary>
        /// 動画素材を先にローカルへ落としておく（fire-and-forget・失敗しても何も起きない）。
        ///
        /// <b>カットの尺は発火時刻から数える</b>ので、発火してからダウンロードすると
        /// その分だけ画に出る時間が減る。実測（2026-08-22・testassets 24KB）で
        /// 落とすのに 32〜152ms かかり、0.5 秒のカットでは無視できない
        /// （本番素材は数 MB なのでさらに伸びる）。無人プレートの先読みと同型。
        ///
        /// ⚠ 用意（Prepare・実測 145ms）はここでは消えない。消せるのはダウンロードだけ。
        /// </summary>
        public void PrefetchVideo(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            if (_videoFileCache.ContainsKey(url)) return;   // 既に落としてある
            if (!_prefetching.Add(url)) return;             // 進行中（同じ素材を 2 回落とさない）
            _ = PrefetchVideoAsync(url, destroyCancellationToken);
        }

        private async Task PrefetchVideoAsync(string url, CancellationToken ct)
        {
            try { await GetLocalVideoUrlAsync(url, ct); }
            catch (OperationCanceledException) { }
            catch (Exception e) { Debug.LogWarning($"[ScreenOverlay] 動画の先読みに失敗: {url} ({e.Message})"); }
            finally { _prefetching.Remove(url); }
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

        /// <summary>
        /// 静止画素材を URL キャッシュ込みで読む（mip 付き）。**表示状態には触らない。**
        /// 入れ替わりの無人プレート先読み（<see cref="ShowControlClient"/> → SwapMorphFx）が使う。
        /// </summary>
        public Task<Texture2D?> LoadStillCachedAsync(string url, CancellationToken ct)
            => LoadTextureAsync(url, ct);

        private async Task<Texture2D?> LoadTextureAsync(string url, CancellationToken ct)
        {
            if (_urlTextureCache.TryGetValue(url, out var cached) && cached != null) return cached;
            try
            {
                // ⚠ nonReadable:false にしてあるのは、この後 **mip 付きへ作り直す**ため。
                //   周回で痩せる伝送とレンズのグレアは mip を引いて作るので、mip の無い素材は
                //   ライブだけ痩せて**プレートだけ鮮明**になり、切り替わった瞬間に画の素性が変わる。
                using var req = UnityWebRequestTexture.GetTexture(url, nonReadable: false);
                req.timeout = 5;
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Yield();
                }
                if (req.result != UnityWebRequest.Result.Success) return null;
                var tex = WithMipmaps(DownloadHandlerTexture.GetContent(req));
                // ⚠⚠ **待っているあいだに、別の要求が同じ URL を先に格納していることがある**
                //    （2026-09-04）。在庫の確認からここまでに待ちが 3 つあり、その間の要求を
                //    覚えていない。素材の先読み（`ShowControlClient.PrefetchStillsAsync`）と、
                //    その素材を出す cue が同じ URL を掴むのは設計上の通常動作。
                //    後から書いた方で上書きすると、**先に入っていた 1 枚が辞書から消えて
                //    `OnDestroy` の掃除が届かなくなる**（Unity のテクスチャは GC で消えない）。
                //    先に入った方を使い、自分が落とした方を捨てる。
                if (_urlTextureCache.TryGetValue(url, out var raced) && raced != null)
                {
                    if (Application.isPlaying) Destroy(tex);
                    else DestroyImmediate(tex);
                    return raced;
                }
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

        /// <summary>
        /// mip の無いテクスチャを mip 付きへ作り直す（<c>UnityWebRequestTexture</c> は mip 無しで返す）。
        /// 素材ごとに 1 回だけ走り、以後はキャッシュに乗る。作り直せなければ元のまま返す
        /// （mip が無くても絵は出る — 痩せ方が live と揃わないだけ）。
        /// </summary>
        private static Texture2D? WithMipmaps(Texture2D? src)
        {
            if (src == null || src.mipmapCount > 1) return src;
            try
            {
                var dst = new Texture2D(src.width, src.height, TextureFormat.RGB24, mipChain: true)
                {
                    name = src.name + "+mip",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Trilinear,
                };
                dst.SetPixels32(src.GetPixels32());
                dst.Apply(updateMipmaps: true, makeNoLongerReadable: true);
                Destroy(src);
                return dst;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ScreenOverlay] mip 生成に失敗（元のまま使う）: {e.Message}");
                return src;
            }
        }

        private void OnPrepared(VideoPlayer vp)
        {
            // stale な Prepare 完了（準備中に別 cue へ切り替え済み）は現行 cue を乗っ取らない。
            if (!_logic.AcceptPrepared()) return;
            // 発火 → 画に出るまでの内訳を 1 回だけ出す（連続カットの停滞を測る唯一の手段）。
            if (!string.IsNullOrEmpty(_clipCueId))
            {
                float total = Time.realtimeSinceStartup - _clipFireAt;
                ClipLatency?.Invoke(_clipCueId, _clipDlSec, Mathf.Max(0f, total - _clipDlSec), _clipCached);
                _clipCueId = "";
            }
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
            if (_frozen) return;   // 画のホールド中は素材の時計も止める（下の SetFrozen 参照）
            if (_current == null || !_current.SourceIsFrames || _stopWhenFadedOut) return;
            var seq = _current.frames!;
            if (!seq.Tick(Time.time - _framesStart)) StopOverlay();
        }

        private bool _frozen;
        private float _freezeStart;
        private bool _pausedByFreeze;

        /// <summary>
        /// 差し替え素材（録画・動画・静止画）の時計を止める / 動かす。
        /// <see cref="CameraFeelFx"/> の画のホールド演出から呼ぶ。
        ///
        /// **ライブだけ止めて録画が動くと「装置が固まった」に見えない**ので、凍らせる間は素材も止める。
        /// 解除では止まっていた分だけ開始時刻をずらす（＝続きから。飛ばさない）。
        /// </summary>
        public void SetFrozen(bool on)
        {
            if (_frozen == on) return;
            _frozen = on;
            if (on)
            {
                _freezeStart = Time.time;
                if (_player != null && _player.isPlaying) { _player.Pause(); _pausedByFreeze = true; }
                return;
            }
            _framesStart += Time.time - _freezeStart;
            if (_pausedByFreeze)
            {
                _pausedByFreeze = false;
                // 凍結中に cue が畳まれていたら再開しない（止めたものを勝手に鳴らさない）。
                if (_player != null && _current != null && !_stopWhenFadedOut) _player.Play();
            }
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
            Vector2 scale = ContainScale(srcAspect);
            _material.SetTexture(OverlayTexId, tex);
            _material.SetVector(OverlayScaleId, new Vector4(scale.x, scale.y, 0f, 0f));
        }

        /// <summary>
        /// 素材を枠へ contain-fit する倍率（ライブと同じ規約）。
        /// **第 2 層も必ずこれを通す** — 片方だけ生 uv で読むと、4:3 の素材が 16:9 の枠で
        /// 水平 1.33 倍ずれて 1 層目と位置が合わなくなる（マスクだけは枠空間なので通さない）。
        /// </summary>
        private Vector2 ContainScale(float srcAspect)
        {
            float screenAspect = _screen != null ? _screen.ScreenAspect : 16f / 9f;
            return srcAspect < screenAspect
                ? new Vector2(srcAspect / screenAspect, 1f)
                : new Vector2(1f, screenAspect / srcAspect);
        }

        private void ApplyStrength(float v)
        {
            _material?.SetFloat(OverlayStrengthId, v);
        }
    }
}
