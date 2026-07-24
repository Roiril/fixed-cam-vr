#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// タイムライン区間で切り替わる BGM の再生器（AudioSource 2 本でクロスフェード）。
    ///
    /// 区間からの指示は <see cref="TimelineDirector"/> がゾーン進入時に <see cref="ApplySegment"/> で流す。
    /// 判定は純ロジック <see cref="BgmPlanLogic"/> に集約（テストがセマンティクスを固定する）。
    ///
    /// - 指示の無い区間では鳴っている曲がそのまま続く（区間ごとに途切れない）
    /// - ループ範囲（loopStart/loopEnd）は <see cref="Update"/> で監視して巻き戻す。
    ///   AudioSource.loop はクリップ全長ループ専用なので、範囲指定時は自前で戻す
    /// - show.json に bgm 指定が無い場合は <see cref="defaultClip"/>（Inspector 設定・従来の固定ループ）を鳴らす。
    ///   つまり未オーサリングのショーは今までと同じ挙動になる
    /// - クリップは URL からの非同期 DL（sa:// 焼き込み / PC 卓のライブ URL 双方）。DL 中は無音のまま待つ
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BgmDirector : MonoBehaviour
    {
        /// <summary>show.json に bgm 指定が無い時に鳴らす既定クリップ（従来の固定ループ相当）。</summary>
        [Tooltip("show.json に bgm 指定が無い時に鳴らす既定クリップ（従来の固定ループ相当）。空なら無音で始まる。")]
        [SerializeField] private AudioClip? defaultClip;

        [Tooltip("既定クリップの音量。")]
        [Range(0f, 1f)]
        [SerializeField] private float defaultVolume = 0.5f;

        [Tooltip("クロスフェード / フェードイン・アウトの既定秒（区間指示に無い場合）。")]
        [SerializeField] private float defaultFadeSec = 1.0f;

        /// <summary>
        /// APK 同梱の既定クリップ（<see cref="defaultClip"/>）を指す擬似トラック id。
        /// 卓のタイムラインからも "__default__" で指定できる（音源を web 側へ二重に置かずに戻せる）。
        /// </summary>
        public const string DefaultTrackId = "__default__";

        private AudioSource? _a;
        private AudioSource? _b;
        private Voice _cur;      // いま鳴らしている（or 鳴らそうとしている）系統
        private Voice _prev;     // フェードアウト中の系統
        private ShowBgmTrackDef[] _tracks = Array.Empty<ShowBgmTrackDef>();
        private ShowBgmDef? _showDefault;
        private bool _hasShowDefault;
        private ShowServerSource? _server;
        private readonly Dictionary<string, AudioClip> _clips = new();
        private int _generation;                       // in-flight な DL を無効化する世代トークン
        private bool _started;

        private struct Voice
        {
            public AudioSource? src;
            public string trackId;
            public float targetVolume;
            public float fadeRate;      // volume/sec（0 なら即時）
            public bool loop;
            public float loopStart;
            public float loopEnd;       // <=0 = 監視しない（クリップ全長）
            public bool stopping;
        }

        /// <summary>いま鳴っているトラック id（無音なら空）。</summary>
        public string CurrentTrackId => _cur.src != null && _cur.src.isPlaying ? _cur.trackId : "";

        /// <summary>いま鳴っているか（フェードイン中を含む）。</summary>
        public bool IsPlaying => _cur.src != null && _cur.src.isPlaying;

        private void Awake()
        {
            _a = CreateSource("A");
            _b = CreateSource("B");
            _cur = new Voice { src = _a, trackId = "" };
            _prev = new Voice { src = _b, trackId = "" };
        }

        private AudioSource CreateSource(string label)
        {
            var go = new GameObject($"[BgmVoice{label}]");
            go.transform.SetParent(transform, worldPositionStays: false);
            var s = go.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.loop = false;
            s.spatialBlend = 0f;   // 2D（HMD の向き・位置に依存しない環境 BGM）
            s.volume = 0f;
            return s;
        }

        /// <summary>URL 解決に使う卓サーバ参照（ライブ URL の絶対化）。</summary>
        public void SetServer(ShowServerSource? server) => _server = server;

        /// <summary>show.json の bgmTracks を差し替える。</summary>
        public void SetTracks(ShowBgmTrackDef[]? tracks)
        {
            _tracks = tracks ?? Array.Empty<ShowBgmTrackDef>();
        }

        /// <summary>
        /// show.json トップレベルの bgm（ラン開始時に戻る既定）を差し替える。
        /// 起動直後・ラン開始時にこれが適用される。present=false なら <see cref="defaultClip"/>。
        /// </summary>
        public void SetShowDefault(ShowBgmDef? def, bool present)
        {
            _showDefault = def;
            _hasShowDefault = present && def != null;
            if (!_started) return;
            ApplyDefault();
        }

        /// <summary>起動時の初回適用（ShowControlClient の初期化列から呼ぶ）。</summary>
        public void Begin()
        {
            _started = true;
            ApplyDefault();
        }

        /// <summary>ラン開始（runEpoch 変化 / 現地手動）で既定 BGM へ戻す。</summary>
        public void ResetRun()
        {
            if (!_started) return;
            ApplyDefault();
        }

        private void ApplyDefault()
        {
            if (_hasShowDefault && _showDefault != null)
            {
                Apply(_showDefault, present: true, forceRestart: true);
                return;
            }
            if (defaultClip == null) { StopAll(0f); return; }
            StartVoice(DefaultTrackId, defaultClip, startSec: 0f, loopStart: 0f, loopEnd: 0f,
                       loop: true, volume: defaultVolume, fadeIn: 0f);
        }

        /// <summary>区間 BGM 指示を適用する（TimelineDirector から）。</summary>
        public void ApplySegment(ShowBgmDef? def, bool present) => Apply(def, present, forceRestart: false);

        private void Apply(ShowBgmDef? def, bool present, bool forceRestart)
        {
            if (!present || def == null) return;
            bool playing = IsPlaying;
            var change = BgmPlanLogic.Decide(true, def.action, def.trackId,
                                             def.restart || forceRestart, playing, CurrentTrackId);
            switch (change)
            {
                case BgmPlanLogic.BgmChange.None:
                    return;
                case BgmPlanLogic.BgmChange.Stop:
                    StopAll(def.fadeOutSec >= 0f ? def.fadeOutSec : defaultFadeSec);
                    return;
                case BgmPlanLogic.BgmChange.Retune:
                    RetuneCurrent(def);
                    return;
                case BgmPlanLogic.BgmChange.Start:
                    _ = StartTrackAsync(def);
                    return;
            }
        }

        private ShowBgmTrackDef? FindTrack(string id)
        {
            foreach (var t in _tracks)
                if (t != null && t.id == id) return t;
            return null;
        }

        // 区間指示 + トラック既定 から実効値を解決する（-1 = トラック既定を継承）。
        private static float Pick(float segValue, float trackValue) => segValue < 0f ? trackValue : segValue;

        private void RetuneCurrent(ShowBgmDef def)
        {
            if (_cur.src == null || _cur.src.clip == null) return;
            var track = FindTrack(def.trackId);
            // 既定クリップ（__default__）は表に無いのでトラック既定＝0 / defaultVolume を使う。
            float trackLoopStart = track?.loopStartSec ?? 0f;
            float trackLoopEnd = track?.loopEndSec ?? 0f;
            float trackVolume = track?.volume ?? defaultVolume;
            if (track == null && def.trackId != DefaultTrackId) return;
            float len = _cur.src.clip.length;
            var (_, ls, le) = BgmPlanLogic.NormalizeWindow(
                len, _cur.src.time,
                Pick(def.loopStartSec, trackLoopStart), Pick(def.loopEndSec, trackLoopEnd));
            _cur.loopStart = ls;
            _cur.loopEnd = le;
            _cur.loop = def.loop;
            _cur.targetVolume = Mathf.Clamp01(Pick(def.volume, trackVolume));
            _cur.fadeRate = FadeRate(_cur.src.volume, _cur.targetVolume,
                                     def.fadeInSec >= 0f ? def.fadeInSec : defaultFadeSec);
            _cur.src.loop = false;   // 範囲監視は Update が行う
        }

        private async Task StartTrackAsync(ShowBgmDef def)
        {
            // 卓が "__default__" を指した場合は APK 同梱の既定クリップ（HorrBGM）を鳴らす。
            // 音源を web 側へ二重に置かずに「元の BGM へ戻す」が書けるようにするための擬似トラック。
            if (def.trackId == DefaultTrackId)
            {
                if (defaultClip == null) return;
                StartVoice(DefaultTrackId, defaultClip,
                           def.startSec,
                           def.loopStartSec < 0f ? 0f : def.loopStartSec,
                           def.loopEndSec < 0f ? 0f : def.loopEndSec,
                           def.loop,
                           def.volume < 0f ? defaultVolume : Mathf.Clamp01(def.volume),
                           def.fadeInSec >= 0f ? def.fadeInSec : defaultFadeSec);
                return;
            }
            var track = FindTrack(def.trackId);
            if (track == null)
            {
                Debug.LogWarning($"[Bgm] 未定義のトラック id: {def.trackId}（bgmTracks に無い）。無視して継続。");
                return;
            }
            int gen = ++_generation;
            AudioClip? clip = await LoadClipAsync(track.url);
            if (clip == null || gen != _generation) return;   // 失敗 / より新しい指示に追い越された
            StartVoice(track.id, clip,
                       startSec: def.startSec,
                       loopStart: Pick(def.loopStartSec, track.loopStartSec),
                       loopEnd: Pick(def.loopEndSec, track.loopEndSec),
                       loop: def.loop,
                       volume: Mathf.Clamp01(Pick(def.volume, track.volume)),
                       fadeIn: def.fadeInSec >= 0f ? def.fadeInSec : defaultFadeSec);
            // 直前の系統はフェードアウトさせる（クロスフェード）。
            if (_prev.src != null && _prev.src.isPlaying)
                _prev.fadeRate = FadeRate(_prev.src.volume, 0f,
                                          def.fadeOutSec >= 0f ? def.fadeOutSec : defaultFadeSec);
        }

        private void StartVoice(string trackId, AudioClip clip, float startSec, float loopStart, float loopEnd,
                                bool loop, float volume, float fadeIn)
        {
            // 役割を入れ替える（旧 cur が prev になりフェードアウト、空いた側で新トラックを鳴らす）。
            Voice old = _cur;
            Voice next = _prev;
            if (old.src != null && old.src.isPlaying)
            {
                old.stopping = true;
                old.targetVolume = 0f;
                old.fadeRate = FadeRate(old.src.volume, 0f, defaultFadeSec);
            }
            else if (old.src != null)
            {
                old.src.Stop();
                old.src.volume = 0f;
            }
            _prev = old;

            var src = next.src;
            if (src == null) return;
            var (st, ls, le) = BgmPlanLogic.NormalizeWindow(clip.length, startSec, loopStart, loopEnd);
            src.Stop();
            src.clip = clip;
            src.loop = false;              // ループ範囲は Update が見る（全長ループも同じ経路で扱う）
            src.volume = fadeIn > 0f ? 0f : Mathf.Clamp01(volume);
            src.time = st;
            src.Play();
            next.trackId = trackId;
            next.targetVolume = Mathf.Clamp01(volume);
            next.fadeRate = FadeRate(src.volume, next.targetVolume, fadeIn);
            next.loop = loop;
            next.loopStart = ls;
            next.loopEnd = le;
            next.stopping = false;
            _cur = next;
        }

        private void StopAll(float fadeOutSec)
        {
            _generation++;   // in-flight な DL の結果で鳴り出さないようにする
            if (_cur.src != null && _cur.src.isPlaying)
            {
                _cur.stopping = true;
                _cur.targetVolume = 0f;
                _cur.fadeRate = FadeRate(_cur.src.volume, 0f, fadeOutSec);
                if (fadeOutSec <= 0f) { _cur.src.Stop(); _cur.src.volume = 0f; }
            }
            if (_prev.src != null && _prev.src.isPlaying)
            {
                _prev.stopping = true;
                _prev.targetVolume = 0f;
                _prev.fadeRate = FadeRate(_prev.src.volume, 0f, fadeOutSec);
                if (fadeOutSec <= 0f) { _prev.src.Stop(); _prev.src.volume = 0f; }
            }
            _cur.trackId = "";
        }

        private static float FadeRate(float from, float to, float sec)
            => sec <= 0f ? 0f : Mathf.Abs(to - from) / Mathf.Max(0.01f, sec);

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;   // 演出で timeScale を触っても BGM は実時間で進む
            AdvanceVoice(ref _cur, dt);
            AdvanceVoice(ref _prev, dt);
        }

        private void AdvanceVoice(ref Voice v, float dt)
        {
            var src = v.src;
            if (src == null) return;

            // 音量フェード
            if (!Mathf.Approximately(src.volume, v.targetVolume))
            {
                src.volume = v.fadeRate <= 0f
                    ? v.targetVolume
                    : Mathf.MoveTowards(src.volume, v.targetVolume, v.fadeRate * dt);
            }
            if (v.stopping && src.volume <= 0.0001f && src.isPlaying)
            {
                src.Stop();
                v.stopping = false;
            }
            if (!src.isPlaying || src.clip == null) return;

            // ループ範囲の監視（AudioSource.loop はクリップ全長専用なので自前で巻き戻す）
            float end = v.loopEnd > 0f ? v.loopEnd : src.clip.length;
            if (src.time >= end - 0.02f)
            {
                if (v.loop) src.time = v.loopStart;
                else
                {
                    v.stopping = true;
                    v.targetVolume = 0f;
                    v.fadeRate = FadeRate(src.volume, 0f, defaultFadeSec);
                }
            }
        }

        // ---- クリップ読み込み（URL → AudioClip・キャッシュ付き）------------------
        private async Task<AudioClip?> LoadClipAsync(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (_clips.TryGetValue(url, out var cached)) return cached;

            string resolved = ShowAssetResolver.Resolve(url, _server);
            AudioType type = GuessAudioType(resolved);
            using var req = UnityWebRequestMultimedia.GetAudioClip(resolved, type);
            if (req.downloadHandler is DownloadHandlerAudioClip dh)
                dh.streamAudio = false;   // ループ範囲のシークが要るので全体を展開する
            var op = req.SendWebRequest();
            while (!op.isDone) await Task.Yield();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[Bgm] クリップ取得に失敗: {resolved} ({req.error})");
                return null;
            }
            var clip = DownloadHandlerAudioClip.GetContent(req);
            if (clip == null) return null;
            _clips[url] = clip;
            return clip;
        }

        private static AudioType GuessAudioType(string url)
        {
            string u = url.ToLowerInvariant();
            int q = u.IndexOf('?');
            if (q >= 0) u = u.Substring(0, q);
            if (u.EndsWith(".ogg")) return AudioType.OGGVORBIS;
            if (u.EndsWith(".wav")) return AudioType.WAV;
            if (u.EndsWith(".m4a") || u.EndsWith(".aac")) return AudioType.ACC;
            return AudioType.MPEG;   // .mp3 既定
        }
    }
}
