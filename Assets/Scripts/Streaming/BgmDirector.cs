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
    ///   AudioSource.loop はクリップ全長ループ専用なので、範囲指定時は自前で戻す。
    ///   **折り返しは等パワーのクロスフェード**（2026-10-14・LEDGER 0049）: 終端の手前から 3 本目の
    ///   AudioSource（<c>_loopTail</c>）に窓の頭を鳴らして重ね、終わったら鳴らす側を入れ替える
    /// - show.json に bgm 指定が無い場合は <see cref="defaultClip"/>（Inspector 設定・従来の固定ループ）を鳴らす。
    ///   つまり未オーサリングのショーは今までと同じ挙動になる
    /// - クリップは URL からの非同期 DL（sa:// 焼き込み / PC 卓のライブ URL 双方）。DL 中は無音のまま待つ
    ///
    /// **レーンと演出の関係（2026-07-26）**: 区間が決める音を「レーン」と呼ぶ。演出（Take）は画面と同じく
    /// 音も一時的に占有でき（<see cref="BeginTakeOverride"/>）、終わったらレーンへ戻る（<see cref="EndTakeOverride"/>）。
    /// 戻り先は開始時のスナップショットではなく **いまのレーン**（演出中に体験者がゾーンを移れば
    /// その区間の指示が正）。同じトラックへ戻るときは中断位置から続けるので、演出を挟んでも曲が頭に戻らない。
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
        private AudioSource? _loopTail;   // 折り返しのクロスフェードで「窓の頭」を鳴らす 3 本目
        private Voice _cur;      // いま鳴らしている（or 鳴らそうとしている）系統
        private Voice _prev;     // フェードアウト中の系統
        private ShowBgmTrackDef[] _tracks = Array.Empty<ShowBgmTrackDef>();
        private ShowBgmDef? _showDefault;
        private bool _hasShowDefault;
        private ShowServerSource? _server;
        private readonly Dictionary<string, AudioClip> _clips = new();
        private int _generation;                       // in-flight な DL を無効化する世代トークン
        private bool _started;

        // ---- レーン（区間が決めている音）と演出の占有 ----------------------------
        // レーンは「演出が無ければ鳴っているはずの音」。演出中に届いた区間指示はここだけ更新し、
        // 音は演出が終わってから追いつかせる（画面の占有と同じ考え方）。
        private string _laneTrackId = "";              // レーンのトラック（無音なら空）
        private ShowBgmDef? _laneDef;                  // レーンを作った指示（戻すときに再利用）
        private bool _takeOverrideActive;              // 演出が音を占有中か
        private float _laneResumeSec;                  // 占有開始時のレーン再生位置（戻る時に続きから）

        // ---- 折り返しのクロスフェード（_cur だけが行う）----------------------------
        private bool _loopXfActive;
        private float _loopXfT;                        // 0..1 の進み
        private float _loopXfDur;                      // 秒

        /// <summary>
        /// フェードの役。**クロスフェードの 2 本は必ず対で In / Out を持つ。**
        /// 片方でも Solo にすると合成パワーが保たれず、中央で音が凹む。
        /// </summary>
        private enum FadeRole
        {
            /// <summary>相手が居ない出し入れ。聴感が直線になる形（<see cref="SoundFade.Curve.Perceptual"/>）。</summary>
            Solo,
            /// <summary>入ってくる側（<c>sin</c>）。</summary>
            In,
            /// <summary>出ていく側（<c>cos</c>）。</summary>
            Out,
        }

        private struct Voice
        {
            public AudioSource? src;
            public string trackId;
            public float targetVolume;
            public float fadeT;         // 0..1 の進み（**速度ではなく進み**を持つ）
            public float fadeDur;       // 秒（<=0 なら即時）
            public float fadeFrom;      // 開始時のレーン音量（_duck / _score を掛ける前）
            public float lane;          // いまのレーン音量（_duck / _score を掛ける前）。BeginFade が読む
            public FadeRole fadeRole;
            public bool loop;
            public float loopStart;
            public float loopEnd;       // <=0 = 監視しない（クリップ全長）
            public bool stopping;
        }

        /// <summary>
        /// 演出が引く量（0 = そのまま / 1 = 無音）。<see cref="ShowSoundDirector"/> が毎フレーム書く。
        /// **一撃の音が鳴った瞬間だけ曲を退かせる**ので、装置の音が必ず前に出る。
        /// </summary>
        private float _duck;

        /// <summary>劇伴が居てよい量（0..1）。<see cref="SetScoreGain"/>。**既定は 1**。</summary>
        private float _score = 1f;

        /// <summary>劇伴を引く量を外から与える（0..1）。</summary>
        public void SetDuck(float duck) => _duck = Mathf.Clamp01(duck);

        /// <summary>
        /// 劇伴が居てよい量（0 = 鳴らさない / 1 = そのまま）。<see cref="ShowSoundDirector"/> が
        /// 毎フレーム書く。<b>リセット後の黒だけ 1 で、題字が立つと退く</b>
        /// （2026-08-18・<c>canon/LEDGER.md</c> 0088）。
        ///
        /// ⚠ <b>既定は 1。</b> `[Sound]` がシーンに焼かれていない構成では**従来どおり全編で鳴る** —
        /// 音を配る側が居ないことを理由に劇伴まで黙らせると、切り分けが 1 段増える。
        /// ⚠ <see cref="_duck"/> とは別の口（掛け算で両方効く）。あちらは一撃のたびの一時的な退き。
        /// </summary>
        public void SetScoreGain(float gain) => _score = Mathf.Clamp01(gain);

        /// <summary>いま鳴っているトラック id（無音なら空）。</summary>
        public string CurrentTrackId => _cur.src != null && _cur.src.isPlaying ? _cur.trackId : "";

        /// <summary>いま鳴っているか（フェードイン中を含む）。</summary>
        public bool IsPlaying => _cur.src != null && _cur.src.isPlaying;

        private void Awake()
        {
            _a = CreateSource("A");
            _b = CreateSource("B");
            _loopTail = CreateSource("LoopTail");
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

        /// <summary>show.json の bgmTracks を差し替え、**まだ落としていない曲を先に落とす**。</summary>
        public void SetTracks(ShowBgmTrackDef[]? tracks)
        {
            _tracks = tracks ?? Array.Empty<ShowBgmTrackDef>();
            _ = PrefetchTracksAsync();
        }

        /// <summary>
        /// 表にある曲を全部落としてキャッシュへ入れる（<c>ShowControlClient.PrefetchTimelineClips</c> と同じ立ち位置）。
        ///
        /// ⚠⚠ <b>指示が来てから落とすと、その分だけ曲の入りが遅れる。</b> 実測（2026-08-23・
        /// <c>canon/LEDGER.md</c> 0119）: 人形の呼びかけ（t=91.9）の次のカットで劇伴を差し替える著作に対し、
        /// 実機でレーンが替わったのは <b>t=94.2</b>。カットの尺（1.4 秒）を差し引いた
        /// <b>0.9 秒が 3.6MB の取得と復号</b>だった。会場の電波が細ければもっと延びる。
        /// ⭐ <b>先に落としたら 2.27 秒 → 1.39 秒</b>（呼びかけからレーンが替わるまで）＝
        /// <b>カットの尺そのもの</b>になった（実測・同日の走行 2 本）。
        ///
        /// ⚠ 失敗しても黙って諦める（`LoadClipAsync` が警告を出す）。必要になった時にもう一度落とす。
        /// </summary>
        private async Task PrefetchTracksAsync()
        {
            foreach (var t in _tracks)
            {
                if (t == null || string.IsNullOrEmpty(t.url) || _clips.ContainsKey(t.url)) continue;
                await LoadClipAsync(t.url);
            }
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
            // 演出の占有はランをまたがない（体験者交代で音が演出のまま残らない）。
            _takeOverrideActive = false;
            ApplyDefault();
        }

        private void ApplyDefault()
        {
            if (_hasShowDefault && _showDefault != null)
            {
                UpdateLaneBook(_showDefault);
                Apply(_showDefault, present: true, forceRestart: true);
                return;
            }
            _laneDef = null;
            _laneResumeSec = 0f;
            if (defaultClip == null) { _laneTrackId = ""; StopAll(0f); return; }
            _laneTrackId = DefaultTrackId;
            StartVoice(DefaultTrackId, defaultClip, startSec: 0f, loopStart: 0f, loopEnd: 0f,
                       loop: true, volume: defaultVolume, fadeIn: 0f);
        }

        /// <summary>
        /// 区間 BGM 指示を適用する（TimelineDirector から）。
        /// **演出が音を占有中なら鳴らさず「戻り先」だけ更新する** — 演出中に体験者がゾーンを移った場合、
        /// 演出明けに移った先の区間の音になる（画面の「戻り先は再計算」と対称）。
        /// </summary>
        public void ApplySegment(ShowBgmDef? def, bool present)
        {
            if (!present || def == null) return;
            UpdateLaneBook(def);
            if (_takeOverrideActive) return;
            Apply(def, present: true, forceRestart: false);
        }

        // ---- 演出（Take）による一時占有 -------------------------------------------

        /// <summary>
        /// 演出の BGM 指示を適用して音を占有する。指示が無い / 何もしない指示なら占有せず false
        /// （＝区間の曲がそのまま鳴り続ける。これが「指定しなければそのカメラの曲のまま」の実体）。
        /// </summary>
        public bool BeginTakeOverride(ShowBgmDef? def, bool present)
        {
            if (!present || def == null || !def.IsActionable()) return false;
            if (_takeOverrideActive) EndTakeOverride();   // 多重占有を作らない
            // 同じ曲へ戻るときに頭出しし直さないよう、レーンの再生位置を覚えておく。
            if (_cur.src != null && _cur.src.isPlaying && _cur.trackId == _laneTrackId)
                _laneResumeSec = _cur.src.time;
            _takeOverrideActive = true;
            Apply(def, present: true, forceRestart: false);
            return true;
        }

        /// <summary>演出が終わった。いまのレーン（区間が決めている音）へ戻す。</summary>
        public void EndTakeOverride()
        {
            if (!_takeOverrideActive) return;
            _takeOverrideActive = false;
            switch (BgmPlanLogic.DecideRestore(IsPlaying, CurrentTrackId, _laneTrackId))
            {
                case BgmPlanLogic.BgmChange.None:
                    return;
                case BgmPlanLogic.BgmChange.Stop:
                    StopAll(defaultFadeSec);
                    return;
                default:
                    RestoreLane();
                    return;
            }
        }

        /// <summary>レーンのトラックを「中断した位置から」鳴らし直す。</summary>
        private void RestoreLane()
        {
            ShowBgmDef? src = _laneDef;
            _ = StartTrackAsync(new ShowBgmDef
            {
                action = BgmPlanLogic.ActionPlay,
                trackId = _laneTrackId,
                loop = src?.loop ?? true,
                startSec = _laneResumeSec,
                loopStartSec = src?.loopStartSec ?? -1f,
                loopEndSec = src?.loopEndSec ?? -1f,
                volume = src?.volume ?? -1f,
                fadeInSec = src?.fadeInSec ?? defaultFadeSec,
                fadeOutSec = src?.fadeOutSec ?? defaultFadeSec,
                restart = true,
            });
        }

        // レーンの帳簿（何が鳴っているべきか）を更新する。音は鳴らさない。
        private void UpdateLaneBook(ShowBgmDef def)
        {
            string a = string.IsNullOrEmpty(def.action) ? BgmPlanLogic.ActionContinue : def.action;
            if (a == BgmPlanLogic.ActionStop)
            {
                _laneTrackId = "";
                _laneDef = def;
                _laneResumeSec = 0f;
                return;
            }
            if (a != BgmPlanLogic.ActionPlay || string.IsNullOrEmpty(def.trackId)) return;   // continue = 据え置き
            if (_laneTrackId != def.trackId || def.restart) _laneResumeSec = def.startSec;
            _laneTrackId = def.trackId;
            _laneDef = def;
        }

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
            BeginFade(ref _cur, Mathf.Clamp01(Pick(def.volume, trackVolume)),
                      def.fadeInSec >= 0f ? def.fadeInSec : defaultFadeSec, FadeRole.Solo);
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
            // ⚠ **入る側と同じ尺で、対の形（cos / sin）で動かす。** 尺が違うと二乗の和が 1 を割り、
            //    やはり真ん中で凹む。ここは必ず対にすること。
            if (_prev.src != null && _prev.src.isPlaying)
            {
                BeginFade(ref _prev, 0f,
                          def.fadeOutSec >= 0f ? def.fadeOutSec : defaultFadeSec, FadeRole.Out);
                _prev.stopping = true;
            }
        }

        private void StartVoice(string trackId, AudioClip clip, float startSec, float loopStart, float loopEnd,
                                bool loop, float volume, float fadeIn)
        {
            // 折り返しのクロスフェード中なら畳む（旧 cur が prev になるので、2 本目を残さない）。
            CancelLoopCrossfade();

            // 役割を入れ替える（旧 cur が prev になりフェードアウト、空いた側で新トラックを鳴らす）。
            Voice old = _cur;
            Voice next = _prev;
            if (old.src != null && old.src.isPlaying)
            {
                old.stopping = true;
                BeginFade(ref old, 0f, defaultFadeSec, FadeRole.Out);
            }
            else if (old.src != null)
            {
                old.src.Stop();
                old.src.volume = 0f;
                old.lane = 0f;
            }
            _prev = old;

            var src = next.src;
            if (src == null) return;
            var (st, ls, le) = BgmPlanLogic.NormalizeWindow(clip.length, startSec, loopStart, loopEnd);
            src.Stop();
            src.clip = clip;
            src.loop = false;              // ループ範囲は Update が見る（全長ループも同じ経路で扱う）
            // BeginFade が fadeFrom に読むのは lane（src.volume は _duck / _score 込みなので読まない）。
            next.lane = fadeIn > 0f ? 0f : Mathf.Clamp01(volume);
            src.volume = Mathf.Clamp01(next.lane * (1f - _duck) * _score);
            src.time = st;
            src.Play();
            next.trackId = trackId;
            // 入る側。前の曲が鳴っていたならクロス（対の形）、鳴っていなかったなら単独の出し入れ。
            BeginFade(ref next, Mathf.Clamp01(volume), fadeIn,
                      old.stopping ? FadeRole.In : FadeRole.Solo);
            next.loop = loop;
            next.loopStart = ls;
            next.loopEnd = le;
            next.stopping = false;
            _cur = next;
        }

        /// <summary>
        /// 鳴っている音を全部止める（<paramref name="fadeOutSec"/> でフェードアウト）。
        /// 体験の終了（<see cref="ShowRunDirector"/>）が暗転と一緒に呼ぶ。
        /// </summary>
        public void StopAll(float fadeOutSec)
        {
            _generation++;   // in-flight な DL の結果で鳴り出さないようにする
            CancelLoopCrossfade();   // 止める最中に 2 本目を残さない
            if (_cur.src != null && _cur.src.isPlaying)
            {
                _cur.stopping = true;
                BeginFade(ref _cur, 0f, fadeOutSec, FadeRole.Solo);
                if (fadeOutSec <= 0f) { _cur.src.Stop(); _cur.src.volume = 0f; _cur.lane = 0f; }
            }
            if (_prev.src != null && _prev.src.isPlaying)
            {
                _prev.stopping = true;
                BeginFade(ref _prev, 0f, fadeOutSec, FadeRole.Solo);
                if (fadeOutSec <= 0f) { _prev.src.Stop(); _prev.src.volume = 0f; _prev.lane = 0f; }
            }
            _cur.trackId = "";
        }

        /// <summary>
        /// フェードを始める。**速度ではなく「何秒で」を持つ**ので、書いたとおりの時間で届く。
        ///
        /// ⚠⚠ <b>2026-08-12 まで、ここは振幅を一定速度で動かしていた（`MoveTowards`）。</b>
        /// 別々の曲を入れ替えるときそれをやると、真ん中の合成パワーが 0.5²+0.5² = 0.5
        /// ＝ <b>-3.01 dB の谷</b>になる。区間をまたぐたびに音が一瞬引っ込んでいた。
        /// 形の理屈は <see cref="SoundFade"/>。
        /// </summary>
        private static void BeginFade(ref Voice v, float to, float sec, FadeRole role)
        {
            // ⚠ 起点は **lane**（_duck / _score を掛ける前）。src.volume を読むと AdvanceVoice が掛けた
            //    (1-_duck)*_score をフェード側がもう一度掛けて、始まりの瞬間に二重に引いていた（2026-10-14）。
            v.fadeFrom = v.src != null ? v.lane : 0f;
            v.targetVolume = Mathf.Clamp01(to);
            v.fadeDur = Mathf.Max(0f, sec);
            v.fadeT = v.fadeDur <= 0f ? 1f : 0f;
            v.fadeRole = role;
        }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;   // 演出で timeScale を触っても BGM は実時間で進む
            AdvanceVoice(ref _cur, dt, isCurrent: true);
            AdvanceVoice(ref _prev, dt, isCurrent: false);
        }

        /// <param name="isCurrent">
        /// <see cref="_cur"/> か。折り返しのクロスフェードは _cur だけが行う
        /// （_prev はフェードアウト中なので従来どおり即時に巻き戻す）。
        /// </param>
        private void AdvanceVoice(ref Voice v, float dt, bool isCurrent)
        {
            var src = v.src;
            if (src == null) return;

            // 音量フェード（進みを進めて、形に通してから書く）
            if (v.fadeT < 1f)
            {
                v.fadeT = v.fadeDur <= 0f ? 1f : Mathf.Min(1f, v.fadeT + dt / v.fadeDur);
            }
            float lane;
            switch (v.fadeRole)
            {
                case FadeRole.In:
                    SoundFade.Cross(v.fadeT, out _, out float gin);
                    lane = v.targetVolume * gin;
                    break;
                case FadeRole.Out:
                    SoundFade.Cross(v.fadeT, out float gout, out _);
                    lane = v.fadeFrom * gout;
                    break;
                default:
                    lane = Mathf.Lerp(v.fadeFrom, v.targetVolume,
                                      SoundFade.Gain(v.fadeT, SoundFade.Curve.Perceptual));
                    break;
            }
            // 演出が引いている分と、劇伴が居てよい量をここで掛ける。
            // **レーンの音量そのものは変えない**ので、引き終われば必ず元の高さへ戻る
            // （引いた状態が居座る事故を作らない）。
            v.lane = lane;   // BeginFade の起点（掛ける前の値を持っておく）
            float gain = lane * (1f - _duck) * _score;

            // 折り返しのクロスフェード中は、元の位置（cos）と窓の頭（sin）へ同じ gain を等パワーで配る。
            if (isCurrent && _loopXfActive)
            {
                if (v.stopping || _loopTail == null || !_loopTail.isPlaying)
                {
                    CancelLoopCrossfade();
                    src.volume = Mathf.Clamp01(gain);
                }
                else
                {
                    _loopXfT = Mathf.Min(1f, _loopXfT + dt / _loopXfDur);
                    BgmPlanLogic.LoopCrossfadeGains(_loopXfT, out float gOld, out float gNew);
                    src.volume = Mathf.Clamp01(gain * gOld);
                    _loopTail.volume = Mathf.Clamp01(gain * gNew);
                    // 元の位置がクリップ末尾で先に鳴り終えたときも、残りを待たずに受け渡す。
                    if (_loopXfT >= 1f || !src.isPlaying)
                    {
                        CompleteLoopCrossfade(ref v);
                        return;   // 鳴らす側が入れ替わった。位置の監視は次のフレームから
                    }
                }
            }
            else
            {
                src.volume = Mathf.Clamp01(gain);
            }

            if (v.stopping && v.fadeT >= 1f && v.targetVolume <= 0.0001f && src.isPlaying)
            {
                src.Stop();
                v.stopping = false;
            }
            if (!src.isPlaying || src.clip == null) return;

            // ループ範囲の監視（AudioSource.loop はクリップ全長専用なので自前で巻き戻す）
            float end = v.loopEnd > 0f ? v.loopEnd : src.clip.length;
            if (isCurrent && v.loop && !v.stopping)
            {
                if (_loopXfActive) return;   // 巻き戻しは 2 本目への受け渡しが引き受けている
                if (BgmPlanLogic.ShouldStartLoopCrossfade(true, src.time, v.loopStart, end)
                    && BeginLoopCrossfade(ref v, end))
                    return;
                // 窓が短すぎる / 2 本目が無いときは、下の従来の即時巻き戻しへ落ちる。
            }
            if (src.time >= end - 0.02f)
            {
                if (v.loop) src.time = v.loopStart;
                else
                {
                    v.stopping = true;
                    BeginFade(ref v, 0f, defaultFadeSec, FadeRole.Solo);
                }
            }
        }

        // ---- 折り返しのクロスフェード -------------------------------------------------

        /// <summary>
        /// 折り返しのクロスフェードを始める。2 本目（<c>_loopTail</c>）に**同じクリップの窓の頭**を鳴らし、
        /// 元の位置は cos、2 本目は sin で <c>xf</c> 秒かけて受け渡す（<see cref="BgmPlanLogic.LoopCrossfadeSec"/>）。
        /// 終わったら <see cref="CompleteLoopCrossfade"/> が鳴らす側を入れ替える。
        /// 始められなければ false（呼び出し側が従来の即時巻き戻しへ落ちる）。
        /// </summary>
        private bool BeginLoopCrossfade(ref Voice v, float end)
        {
            var src = v.src;
            var tail = _loopTail;
            if (src == null || tail == null || src.clip == null) return false;

            float xf = BgmPlanLogic.LoopCrossfadeSec(v.loopStart, end);
            // フレームが粗くて開始が遅れた分は、受け側の頭出しと進みへ織り込む（元の位置と歩調を揃える）。
            float overlap = BgmPlanLogic.LoopCrossfadeOverlap(src.time, end, xf);

            tail.Stop();
            tail.clip = src.clip;
            tail.loop = false;
            tail.volume = 0f;   // 初回の gain は AdvanceVoice が次のフレームで書く（t≈0 なので 0 のまま）
            tail.time = Mathf.Min(v.loopStart + overlap, Mathf.Max(0f, src.clip.length - 0.01f));
            tail.Play();

            _loopXfActive = true;
            _loopXfDur = xf;
            _loopXfT = overlap / xf;
            return true;
        }

        /// <summary>
        /// 受け渡しが終わった。2 本目が窓の頭 + xf 秒の位置を鳴らしているので、それを <see cref="_cur"/> にして
        /// 元の AudioSource を止める（次の折り返しではこちらが 2 本目になる）。
        /// </summary>
        private void CompleteLoopCrossfade(ref Voice v)
        {
            var old = v.src;
            var tail = _loopTail;
            _loopXfActive = false;
            _loopXfT = 0f;
            if (old == null || tail == null) return;
            // 受け渡し直後の 1 フレームを無音にしない: 新しい側の音量は AdvanceVoice が毎フレーム書くが、
            // その前に今の gain を持たせておく。
            tail.volume = Mathf.Clamp01(v.lane * (1f - _duck) * _score);
            old.Stop();
            old.volume = 0f;
            v.src = tail;
            _loopTail = old;
        }

        /// <summary>
        /// 折り返しのクロスフェードを取り消す（トラックの切り替え / 止める / 想定外の停止）。
        /// 2 本目を止め、元の位置の音量に掛けていた cos 分をレーンへ畳む
        /// （畳まないと、取り消した瞬間に元の位置だけ音量が跳ね上がる）。
        /// </summary>
        private void CancelLoopCrossfade()
        {
            if (!_loopXfActive) return;
            _loopXfActive = false;
            BgmPlanLogic.LoopCrossfadeGains(_loopXfT, out float gOld, out _);
            _cur.lane *= gOld;
            _loopXfT = 0f;
            if (_loopTail != null)
            {
                _loopTail.Stop();
                _loopTail.volume = 0f;
            }
        }

        /// <summary>
        /// クリップ取得の試行回数と、待ちの初期値（指数バックオフ: 1s → 2s → 4s → 8s ＝ 最大 15 秒待つ）。
        ///
        /// ⚠⚠ **リトライが無いと、その体験は最後まで無音になる**（2026-08-30 実測）。
        /// 起動直後は Wi-Fi がまだ立ち上がっていないことがあり、実機の走行では
        /// アプリ起動の <b>7 秒後</b>に `Cannot connect to destination host` で 1 度失敗しただけで、
        /// BGM が二度と鳴らなかった。BGM は起動時の <c>Begin()</c> で 1 回要求されるだけなので、
        /// そこで落ちると再要求の機会が無い（区間指示を書いていない台本では特にそう）。
        /// しかも**警告は logcat にしか出ない** ＝ 当日は誰も気づけない。
        /// </summary>
        private const int ClipFetchAttempts = 4;
        private const float ClipFetchBackoffSec = 1f;

        // ---- クリップ読み込み（URL → AudioClip・キャッシュ付き）------------------
        private async Task<AudioClip?> LoadClipAsync(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (_clips.TryGetValue(url, out var cached)) return cached;

            AudioClip? clip = null;
            string resolved = "";
            for (int attempt = 1; attempt <= ClipFetchAttempts; attempt++)
            {
                // ⚠⚠ **解決は毎回やり直す**（2026-08-31 実測）。show.json の bgm は相対 URL
                //    （`/audio/HorrBGM.mp3`）で、絶対 URL にするには卓の接続先が要る。ところが
                //    `PushBgm()` の `SetTracks` が先読みを起こす経路では **`SetServer` より先に**
                //    ここへ来ることがあり、そのとき `_server` は null ＝ 相対のまま残る。
                //    ループの外で 1 度だけ解決していたので、**4 回とも同じ壊れた URL を叩いていた**
                //    （`Cannot connect to destination host` ×4 → 無音のまま体験が走る）。
                resolved = ShowAssetResolver.Resolve(url, _server);
                AudioType type = GuessAudioType(resolved);
                using var req = UnityWebRequestMultimedia.GetAudioClip(resolved, type);
                if (req.downloadHandler is DownloadHandlerAudioClip dh)
                    dh.streamAudio = false;   // ループ範囲のシークが要るので全体を展開する
                var op = req.SendWebRequest();
                while (!op.isDone) await Task.Yield();

                if (req.result == UnityWebRequest.Result.Success)
                {
                    clip = DownloadHandlerAudioClip.GetContent(req);
                    if (attempt > 1)
                        Debug.Log($"[Bgm] クリップ取得に成功（{attempt} 回目）: {resolved}");
                    break;
                }

                if (attempt >= ClipFetchAttempts)
                {
                    // ⚠ 最後の 1 回だけ Error にする。走行レポートの「実機ログの警告」節が拾い、
                    //    **無音のまま全員を通す**経路を機械で検出できるようにする。
                    Debug.LogError($"[Bgm] クリップ取得に失敗（{ClipFetchAttempts} 回試行）: {resolved} ({req.error})" +
                                   " — この体験は BGM 無しで走ります");
                    return null;
                }
                float wait = ClipFetchBackoffSec * (1 << (attempt - 1));
                Debug.LogWarning($"[Bgm] クリップ取得に失敗（{attempt}/{ClipFetchAttempts}・{wait:F0}s 後に再試行）: " +
                                 $"{resolved} ({req.error})");
                await Task.Delay((int)(wait * 1000f));
            }
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
