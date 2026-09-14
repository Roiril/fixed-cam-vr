#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>旧 HMD 注意書きで言語を切り替えたときの音。</b>
    /// 注意書きはタブレットへ移行したため、通常の体験では鳴らさない。Editor 検査との互換用に残す。
    ///
    /// ⚠⚠ <b>交互。乱数で選ばない。</b> 2 本を <c>1 → 2 → 1 → 2</c> と順に鳴らす
    /// （<see cref="TypeAudioCue"/> は 8 本から「直前と同じものを引かない」乱数で選ぶが、
    /// あれは 1 通で 10〜20 発並ぶ音。ここは<b>押すたびに 1 発</b>なので、
    /// 乱数だと「同じ音が 2 回続いた ＝ 切り替わらなかった」と読まれる）。
    ///
    /// ⚠ <b>叩くのは <c>TitleNotice</c></b>（面の字を書き替えているのと同じ場所）。
    /// 入力の側（<c>OvrControllerBridge</c>）ではなく<b>画が替わる側</b>から鳴らすので、
    /// 「押したが面が組めていなくて何も変わらなかった」ときに音だけ鳴ることがない。
    ///
    /// ⚠ 音の高さは<b>素材が持っている</b>（<c>tools/ingest-sounds.py</c> の <c>PLAN</c> が
    /// 2 本を尖頭 -3dBTP で揃えて焼く）。ここで倍率を掛けない — 掛けると 2 箇所で高さが決まる。
    ///
    /// ⚠ <b>2D で鳴らす</b>（<c>SpatialAudio.MonoRequired</c> に入れない）。この面は黒の中に
    /// 頭を向けて立っていて「その音が出ている物」が画に無い（`rules/sound-design.md` §2.5 の
    /// 「誰も鳴らしていない音」— 題字の音と同じ扱い）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LangSwitchAudioCue : MonoBehaviour
    {
        /// <summary><c>Resources</c> の中の置き場（<c>_1</c> / <c>_2</c> が付く）。</summary>
        public const string ResourcePrefix = "Sound/sfx_lang_";

        /// <summary>
        /// 本数。<b><c>tools/ingest-sounds.py</c> の <c>PLAN</c> が焼く本数と対</b>。
        /// ⚠ 増やすなら「交互」が「順ぐり」になる — それはユーザーの指定と別のものなので、
        /// 増やす前に <c>canon/LEDGER.md</c> を見る。
        /// </summary>
        public const int VariantCount = 2;

        [Tooltip("鳴らす発声器。null なら同 GameObject から取得（無ければ足す）。")]
        [SerializeField] private SfxPlayer? player;

        // ⚠ **掴めたものだけを詰めて持つ**（`TypeAudioCue` と同じ流儀）。穴のある配列を
        //    番号で引くと、1 本欠けた日に「片方だけ無音」という気づけない壊れ方をする。
        private readonly System.Collections.Generic.List<AudioClip> _clips =
            new System.Collections.Generic.List<AudioClip>(VariantCount);

        /// <summary>鳴らした累計（次にどちらを鳴らすかもここから決まる）。</summary>
        public int PlayedCount { get; private set; }

        /// <summary>音源を掴めているか。<b>false なら切り替えは無音のまま。</b></summary>
        public bool HasClips => _clips.Count > 0;

        /// <summary>
        /// <paramref name="played"/> 発目の次に鳴らす音の番号（0 始まり）。
        /// <b>交互</b>なので偶数回目は 1 本目・奇数回目は 2 本目。
        /// </summary>
        public static int IndexFor(int played, int count)
            => count <= 1 ? 0 : ((played % count) + count) % count;

        private void Awake()
        {
            if (player == null) player = GetComponent<SfxPlayer>();
            if (player == null) player = gameObject.AddComponent<SfxPlayer>();
            for (int i = 0; i < VariantCount; i++)
            {
                var clip = Resources.Load<AudioClip>($"{ResourcePrefix}{i + 1}");
                if (clip != null) _clips.Add(clip);
            }
            if (_clips.Count < VariantCount)
            {
                Debug.LogWarning($"[TitleNotice] 言語切り替えの音が {_clips.Count}/{VariantCount} "
                                 + "しか見つかりません"
                                 + "（`py -3.11 tools/ingest-sounds.py --only sfx_lang_1 sfx_lang_2`）");
            }
        }

        /// <summary>
        /// 1 回ぶん鳴らす。<b>音源が無ければ黙って何もしない</b>（体験は止めない）。
        /// ⚠ 位置は渡さない（2D）。
        /// </summary>
        public void Play()
        {
            if (_clips.Count == 0 || player == null) return;
            // ⚠⚠ **散らさない**（音程 0 / 音量 0）。<see cref="SfxPlayer.Play"/> の既定は
            //    ±3% / ±1.2dB で、あれは<b>同じ波形が続けて並ぶ音</b>（打鍵・切替）が
            //    「効果音」に聞こえないための仕掛け。ここは 2 本が交互に来るので、
            //    散らすと**押すたびに高さと大きさが揺れて「交互」が読めなくなる**
            //    （2 本を尖頭で揃えて 0.5dB 差に収めた意味も無くなる）。
            // 音量 0.5（2026-09-05 ユーザー指定「今の 1/2 の音量」）。
            // ⚠ 掛けるのはここ 1 か所。2 本の相対（尖頭で揃えた 0.5dB 差）は保たれる。
            player.Play(_clips[IndexFor(PlayedCount, _clips.Count)], 0.5f,
                        pitchSpread: 0f, gainSpreadDb: 0f);
            PlayedCount++;
        }

        /// <summary>体験者が替わったら 1 本目から。<b>次の人には同じ順で聞こえる。</b></summary>
        public void ResetRun()
        {
            player?.StopAll();
            PlayedCount = 0;
        }
    }
}
