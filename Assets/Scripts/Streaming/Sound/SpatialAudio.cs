#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// **音を空間に置くための 1 か所**（2026-09-03・<c>canon/LEDGER.md</c> 0130）。
    ///
    /// ユーザー指示は 3 つ:
    ///
    /// | 何 | どこから |
    /// |---|---|
    /// | カメラ切替・打鍵など<b>スクリーン関係の音</b> | スクリーンから |
    /// | 人形の笑い | 体験者の<b>周囲のランダムな位置</b>から |
    /// | 人形の呼びかけ（あーそーぼー） | 体験者の<b>後ろ</b>から |
    ///
    /// ⚠⚠ <b>3D にするのは向きだけで、大きさは 1 ミリも変えない。</b>
    /// <c>rules/sound-design.md</c> §3 は素材ごとの高さを LUFS で揃えてある（敷く音 -32 /
    /// 繰り返す一撃 -23 / 一度きりの山 -3dBTP）。距離減衰を効かせると、その設計が
    /// <b>置いた場所によって黙って書き換わる</b>。だから <see cref="MinDistanceM"/> を
    /// 現実にありえない距離まで押し上げ、減衰の区間へ入らないようにしてある
    /// （対数減衰は <c>minDistance</c> より内側で必ず 1.0）。
    ///
    /// ⚠⚠ <b>spatializer はモノのクリップしか処理しない</b>（この機は Meta XR Audio）。
    /// ステレオのまま <c>spatialBlend=1</c> にしても<b>定位せず頭の中で鳴り続ける</b>。
    /// <see cref="MonoRequired"/> がその名簿で、<c>tools/soundkit.py</c> の <c>MONO3D</c> と
    /// <b>同じ中身でなければならない</b>（<c>tools/sound-lint.py</c> が両者を突き合わせる）。
    /// 焼く側がモノでも、取り込みで <c>forceToMono</c> が落ちていれば同じことなので、
    /// <see cref="CountStereo"/> が実行時に数え、テレメトリの <c>snd3d</c> に出る。
    /// </summary>
    public static class SpatialAudio
    {
        // ---- 距離（減衰させない）------------------------------------------------

        /// <summary>
        /// 減衰が始まる距離 (m)。<b>現実に届かない値をわざと入れてある。</b>
        /// 対数減衰は <c>minDistance</c> の内側で 1.0 なので、これで「向きだけ 3D」になる。
        /// </summary>
        public const float MinDistanceM = 50f;

        /// <summary>減衰が終わる距離 (m)。<see cref="MinDistanceM"/> より外なら何でもよい。</summary>
        public const float MaxDistanceM = 500f;

        // ---- 人形の笑いの輪 -----------------------------------------------------

        /// <summary>笑いを置く輪の半径 (m)。<b>大きさには効かない</b>（上の但し書き）。</summary>
        public const float LaughRadiusM = 2.5f;

        /// <summary>笑いを頭より下へ置く量 (m)。人形は背が低い。</summary>
        public const float LaughDropM = 0.30f;

        /// <summary>
        /// 笑いを置く方角の刻み（度）。4 声を <b>90° ごとの区画</b>へ 1 つずつ入れる。
        /// ⚠ 完全な一様乱数にすると 2 体が重なる回ができ、「周囲の」が成立しない回が出る。
        /// </summary>
        public const float LaughSectorDeg = 90f;

        /// <summary>区画の中で振る幅（±度）。区画の境を越えない値。</summary>
        public const float LaughJitterDeg = 35f;

        // ---- 人形の呼びかけ（後ろ）---------------------------------------------

        /// <summary>呼びかけを置く距離 (m)。</summary>
        public const float CallRadiusM = 2.0f;

        /// <summary>呼びかけを頭より下へ置く量 (m)。</summary>
        public const float CallDropM = 0.35f;

        /// <summary>
        /// 真後ろからどれだけ横へずらすか（度・最小）。
        ///
        /// ⚠⚠ <b>真後ろ（180°）に置くと前後が入れ替わって聞こえる。</b> HRTF は正中面で
        /// 左右差が消えるので、0° と 180° は聞き分けの手掛かりがいちばん少ない。
        /// 少し横へ振ると左右差が出て「後ろ」が立つ。
        /// </summary>
        public const float CallOffAxisMinDeg = 15f;

        /// <summary>同（最大）。これ以上振ると「真横」に寄る。</summary>
        public const float CallOffAxisMaxDeg = 35f;

        /// <summary>
        /// <b>モノでなければならない音源</b>（<c>Resources/Sound/</c> の拡張子なし）。
        ///
        /// ⚠⚠ <b><c>tools/soundkit.py</c> の <c>MONO3D</c> と同じ中身にすること。</b>
        /// 焼く側とここが食い違うと、**音は鳴るが定位しない**という気づけない壊れ方をする
        /// （画にも録画にも出ない）。<c>tools/sound-lint.py</c> が両者を突き合わせて落とす。
        ///
        /// ⚠ <b>鳴らなくなった音（<c>sfx_swap</c> / <c>sfx_seal_close</c> / <c>sfx_screen_noise</c> /
        /// <c>sfx_shell_open</c>）はここに入れない。</b> 鳴らないものを 3D にしても確かめる手が無い。
        /// </summary>
        public static readonly string[] MonoRequired =
        {
            // 装置の音（スクリーンから）
            "sfx_switch_1", "sfx_switch_2", "sfx_switch_3",
            "sfx_switch_4", "sfx_switch_5", "sfx_switch_6",
            "sfx_switch_alert_1", "sfx_switch_alert_2", "sfx_switch_alert_3",
            "sfx_switch_alert_4", "sfx_switch_alert_5", "sfx_switch_alert_6",
            "sfx_glitch_1", "sfx_glitch_2", "sfx_glitch_3",
            "sfx_screen_on", "sfx_power_off",
            "bed_device", "bed_device_worn", "bed_static",
            // 目（2026-09-03・`canon/LEDGER.md` 0131）。**1 つ 1 つがその目の方角から鳴る**
            "sfx_eye_1", "sfx_eye_2", "sfx_eye_3",
            "sfx_eye_4", "sfx_eye_5", "sfx_eye_6",
            "sfx_eye_big",
            // 連絡の面（AIエージェントのスクリーン）から
            "sfx_type_1", "sfx_type_2", "sfx_type_3", "sfx_type_4",
            "sfx_type_5", "sfx_type_6", "sfx_type_7", "sfx_type_8",
            // 人形（周囲・後ろ）
            "bed_dolls_laugh", "bed_doll_one", "bed_dolls_grow_a", "bed_dolls_grow_b",
            "sfx_doll_call",
            // 封印の箱（2026-08-15 から鳴らない。モノで焼いてあるので名簿に残す）
            "bed_seal",
        };

        /// <summary>
        /// 声を 3D にする。<b>向きだけ</b>で、距離では大きさが変わらない
        /// （<paramref name="minDistance"/> を渡さない限り）。
        /// </summary>
        public static void Configure(AudioSource? src,
                                     float minDistance = MinDistanceM,
                                     float maxDistance = MaxDistanceM)
        {
            if (src == null) return;
            src.spatialBlend = 1f;
            src.spatialize = true;
            src.spatializePostEffects = false;
            src.rolloffMode = AudioRolloffMode.Logarithmic;
            src.minDistance = minDistance;
            src.maxDistance = maxDistance;
            src.dopplerLevel = 0f;   // 体験者が歩いた程度で音程が変わってはいけない
            src.spread = 0f;
        }

        /// <summary>
        /// 声を 2D へ落とす。<b>置き場所を掴めなかったときの逃げ道。</b>
        /// 黙って別の場所から鳴らすより、定位を捨てる方が事故が小さい。
        /// </summary>
        public static void MakeFlat(AudioSource? src)
        {
            if (src == null) return;
            src.spatialBlend = 0f;
            src.spatialize = false;
        }

        /// <summary>
        /// 頭を中心とした輪の上の点。<paramref name="bearingDeg"/> は<b>ワールドの方角</b>
        /// （頭の向きではない）ので、体験者が振り向いても音の居場所は動かない。
        ///
        /// ⚠ <b>頭の位置には付いていく。</b> 体験者は区間ごとに歩くので、ワールドに釘で
        /// 留めると距離が変わる ＝ 減衰を切ってあっても「遠くの人形」に聞こえなくなる
        /// （近づけば頭の中に入る）。<b>「周囲に居る」を保つのは相対位置</b>。
        /// </summary>
        public static Vector3 Ring(Transform head, float bearingDeg, float radius, float drop)
        {
            float rad = bearingDeg * Mathf.Deg2Rad;
            var offset = new Vector3(Mathf.Sin(rad) * radius, -drop, Mathf.Cos(rad) * radius);
            return head.position + offset;
        }

        /// <summary>
        /// 頭の<b>後ろ</b>の点と、そのときの見かけの方角（度・0 が正面 / 180 が真後ろ）。
        ///
        /// ⚠ <b>置いたら world に固定する。</b> 頭に貼り付けると、振り向いても声が後ろに
        /// 回り込み続ける ＝ 装置の音になる。ここが返すのは<b>鳴らした瞬間の後ろ</b>で、
        /// 体験者が振り向けば声のした方を向ける。
        /// </summary>
        public static Vector3 Behind(Transform head, float offAxisDeg, out float azimuthDeg)
        {
            float yaw = head.eulerAngles.y;
            azimuthDeg = 180f + offAxisDeg;
            float bearing = yaw + azimuthDeg;
            return Ring(head, bearing, CallRadiusM, CallDropM);
        }

        /// <summary>
        /// 真後ろから振る量を 1 つ引く（左右どちらかへ
        /// <see cref="CallOffAxisMinDeg"/>〜<see cref="CallOffAxisMaxDeg"/>）。
        /// </summary>
        public static float PickCallOffAxisDeg()
        {
            float mag = Random.Range(CallOffAxisMinDeg, CallOffAxisMaxDeg);
            return Random.value < 0.5f ? -mag : mag;
        }

        /// <summary>
        /// 笑いを置く方角を <paramref name="count"/> 個、<b>重ならないように</b>引く。
        /// 90° ごとの区画へ 1 つずつ入れ、区画ごとに振る（輪ぜんたいの向きも毎回変わる）。
        /// </summary>
        public static void PickLaughBearings(float[] into)
        {
            float baseDeg = Random.Range(0f, 360f);
            for (int i = 0; i < into.Length; i++)
            {
                into[i] = Mathf.Repeat(baseDeg + i * LaughSectorDeg
                                       + Random.Range(-LaughJitterDeg, LaughJitterDeg), 360f);
            }
        }

        /// <summary>
        /// <b>ステレオのまま 3D に置かれている音源を数える。</b>
        ///
        /// ⚠⚠ <b>これが「定位しているか」の唯一の観測。</b> ステレオのクリップは
        /// <c>spatialBlend=1</c> でも頭の中で鳴るだけで、<b>画にも録画にもログにも出ない</b>。
        /// 焼き直し・<c>menu sound-import</c> 忘れ・<c>forceToMono</c> の落ちは全部ここに出る。
        /// </summary>
        public static int CountStereo(string resourceDir, out int checkedCount)
        {
            int bad = 0, seen = 0;
            foreach (string res in MonoRequired)
            {
                var clip = Resources.Load<AudioClip>(resourceDir + res);
                if (clip == null) continue;    // 掴めない側は sndBuilt が数える
                seen++;
                if (clip.channels == 1) continue;
                bad++;
                Debug.LogWarning($"[Sound] {res} がステレオのままです（{clip.channels}ch）。"
                                 + "3D で鳴らしても定位しません。"
                                 + "`py -3.11 tools/make-sounds.py` / `tools/ingest-sounds.py` で"
                                 + "焼き直したあと `.\\tools\\unity.ps1 menu sound-import` を走らせること。");
            }
            checkedCount = seen;
            return bad;
        }
    }
}
