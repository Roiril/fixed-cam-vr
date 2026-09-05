#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>ホラー軽減モードの音を 1 箇所で掛ける</b>（2026-09-05・<c>canon/LEDGER.md</c> 0154）。
    /// 仕事は 2 つだけ:
    ///
    ///   ① <b>既存の音ぜんぶを <see cref="HorrorRelief.Gain"/> 倍にする</b> —
    ///      <c>AudioListener.volume</c> を毎フレーム書く
    ///   ② <b>陽気な曲（<c>Sound/bed_relief</c>）を流す</b> — 専用の <c>AudioSource</c> 1 本
    ///
    /// ⚠⚠ <b>①を「各実行体に倍率を掛ける」形で書かない。</b> 音を出しているのは
    /// <see cref="ShowSoundDirector"/>（敷く音 12 系統 ＋ <see cref="SfxPlayer"/>）／
    /// <see cref="BgmDirector"/>（劇伴 2 声）／<see cref="SwitchAudioCue"/>（切替・警告つき）／
    /// <c>TypeAudioCue</c>（打鍵）／<c>LangSwitchAudioCue</c>（言語切替）と<b>散っている</b>。
    /// 1 箇所でも掛け忘れると<b>画にも録画にも一撃のログにも 1 ビットも出ない</b>
    /// （この codebase が何度も踏んだ型 — <c>memory/visitor_sound_reset.md</c>）。
    /// <c>AudioListener</c> なら**原理的に漏れない**し、将来 <c>AudioSource</c> を足しても自動で効く。
    /// ⚠ <see cref="ShowSoundDirector"/> の <c>masterGain</c> には掛けない
    /// （<c>SfxPlayer.Play</c> の中でも掛かるので**二乗になる**・同ファイルの警告と同じ罠）。
    ///
    /// ⚠⚠ <b>陽気な曲だけは <c>ignoreListenerVolume</c> で①の外に出す。</b>
    /// 出さないと「既存の音の倍率」を動かしたときに陽気な曲まで一緒に下がる。
    /// 曲の高さは<b>焼いた時点で決まっている</b>（-24.0 LUFS・<c>tools/ingest-sounds.py</c> の
    /// <c>PLAN</c>）ので、ここで倍率を掛けない（音量が 2 箇所で決まる形にしない）。
    ///
    /// ⚠ <b>敷く音の名簿に載せない。</b> <c>ShowSoundDirector.ApplyBeds</c> は毎フレーム
    /// 音量を書き潰すので、載せた瞬間に黙って消える。だから独立した <c>GameObject</c> に置く。
    ///
    /// ⚠ <b>異世界（バックルームズ）の区間でも止めない</b>（ユーザー指定
    /// 「バックルーム演出でも流しておく。既存に置き換えるというよりも常に流す感じ」）。
    /// あの区間は劇伴が切れて <see cref="ShowSoundDirector"/> の風が鳴る所だが、
    /// この曲はどちらの系統にも乗っていないので、何もしなくても鳴り続ける。
    ///
    /// ⚠ <b>状態を持たない</b> — <see cref="HorrorRelief.Enabled"/> を毎フレーム見るだけ。
    /// だから体験者の交代は <c>HorrorRelief.Reset()</c> の 1 行で足りる（曲は勝手に退いて頭へ戻る）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HorrorReliefAudio : MonoBehaviour
    {
        /// <summary><c>Resources</c> の中の置き場（<c>tools/ingest-sounds.py</c> が焼く）。</summary>
        public const string ResourceName = "Sound/bed_relief";

        /// <summary>入るときの立ち上がり (秒)。注意書きの黒の中なので、ふっと現れる速さでよい。</summary>
        private const float RiseSec = 1.5f;

        /// <summary>
        /// 出るときの落とし (秒)。<b>入るより速い</b> — 解除は「間違えた」ときの操作なので、
        /// 待たされると効いていないように見える。
        /// </summary>
        private const float FallSec = 0.8f;

        /// <summary>これを下回ったら止める（止めると <c>time</c> が 0 へ戻る ＝ 次の人は頭から）。</summary>
        private const float SilenceGain = 0.0005f;

        private AudioSource? _src;

        // ---- 観測（テレメトリが読む）--------------------------------------------
        // ⚠⚠ **音は録画に映らない。** ここが「効果が実在したか」を出す唯一の場所で、
        //    どれも「状態が進んだ」ではなく**実際に engine へ書けた値**を返す。

        /// <summary>曲を掴めたか。<b>false なら軽減モードでも陽気な曲は鳴らない</b>（音量は半分になる）。</summary>
        public bool ClipResolved => _src != null && _src.clip != null;

        /// <summary>
        /// <b>engine から読み直した</b>既存の音の倍率。軽減中は <see cref="HorrorRelief.Gain"/>。
        /// ⚠ 自分が書いた変数ではなく <c>AudioListener.volume</c> そのものを返す
        /// （「書いたつもり」と「書けた」を分ける）。
        /// </summary>
        public float ListenerVolume => AudioListener.volume;

        /// <summary>陽気な曲の音量（0..1）。<b>画にも一撃のログにも出ない。</b></summary>
        public float BgmGain => _src != null ? _src.volume : 0f;

        /// <summary>
        /// 陽気な曲の再生位置 (秒)。<b>これが進んでいることだけが「本当に鳴っている」の証拠</b>
        /// （音量を書いても、クリップを掴めていなければ 0 のまま動かない・
        /// <c>rules/work-style.md</c> §2-3「初期化されたかは通し番号か経過時間で見る」）。
        /// </summary>
        public float BgmTimeSec => _src != null && _src.isPlaying ? _src.time : 0f;

        private void Awake()
        {
            var clip = Resources.Load<AudioClip>(ResourceName);
            if (clip == null)
            {
                // ⚠ 体験は止めない（音源が無くても既存の音は半分になる）。
                Debug.LogWarning($"[HorrorRelief] 陽気な曲（{ResourceName}）が見つかりません。"
                                 + "軽減モードでは音量が半分になるだけになります"
                                 + "（`py -3.11 tools/ingest-sounds.py --only bed_relief`）");
            }

            var go = new GameObject("[ReliefBgm]");
            go.transform.SetParent(transform, worldPositionStays: false);
            _src = go.AddComponent<AudioSource>();
            _src.clip = clip;
            _src.loop = true;                 // 継ぎ目は焼いてある（`fold_loop`）
            _src.playOnAwake = false;
            _src.spatialBlend = 0f;           // 2D（どこから鳴っている物でもない）
            _src.spatialize = false;
            _src.volume = 0f;
            // ⚠⚠ **これが①の例外**。立てないと陽気な曲まで半分になる。
            _src.ignoreListenerVolume = true;
        }

        // ⚠⚠ **止めるときは必ず戻す。** `AudioListener.volume` はグローバルなので、
        //    軽減モードのままシーンを降りると **Editor の次の Play まで半分のまま**になる。
        private void OnDisable()
        {
            AudioListener.volume = 1f;
            if (_src != null)
            {
                _src.Stop();
                _src.volume = 0f;
            }
        }

        private void Update()
        {
            // ① 既存の音ぜんぶ。**毎フレーム書く**（誰かが触っても次のフレームで戻る）。
            AudioListener.volume = HorrorRelief.ShowGain;

            // ② 陽気な曲。
            if (_src == null || _src.clip == null) return;
            bool on = HorrorRelief.Enabled;
            if (on && !_src.isPlaying)
            {
                // ⚠ **必ず頭から。** 前の体験者が途中まで聴いた続きから始まると、
                //    走行ごとに違う所から鳴り出す（再現性が消える）。
                _src.time = 0f;
                _src.Play();
            }
            float sec = on ? RiseSec : FallSec;
            _src.volume = Mathf.MoveTowards(_src.volume, on ? 1f : 0f,
                                            Time.unscaledDeltaTime / Mathf.Max(0.01f, sec));
            if (!on && _src.isPlaying && _src.volume <= SilenceGain) _src.Stop();
        }
    }
}
