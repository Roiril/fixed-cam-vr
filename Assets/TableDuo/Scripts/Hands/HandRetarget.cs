#nullable enable
using UnityEngine;

namespace TableDuoVr.Hands
{
    /// <summary>
    /// OVR bone の回転を、別リグ（Male/Robot 等の外部パック手）へ移すリターゲット。
    ///
    /// 【参照コピー式（現行・軸規約非依存）】<see cref="ApplyFromReference"/>
    ///   隠し Meta 白手（authored bind のまま生成・非表示）に live ローカル回転をそのまま流し込み
    ///   （＝ Default 白手と同一の「正解系」）、その実ワールド回転に**構築時に測った定数オフセット
    ///   C_i = inv(metaWorld_i) * packWorld_i** を掛けてパック bone のワールド回転へコピーする。
    ///   リグのローカル軸規約・階層構造（中間ノードの有無）・bind 規約に一切依存しない。
    ///
    /// 【やってはいけない（2026-07-11 の実測教訓）】
    ///   - 式A（親相対バインド差分 live*inv(ovrBind)*varBind）: リグ間の軸規約差で曲げ軸が壊れる
    ///     （実機実害: Robot は右へ・Realistic は逆へ曲がる）
    ///   - layout.BindLocalRot を「live の中立」とみなす: pinky0 で live と ~173° 乖離
    ///   - layout.ParentIndex で FK 合成して「メッシュのワールド回転」を推定する: メッシュ実階層の
    ///     中間ノードで ~100° 乖離
    /// </summary>
    public static class HandRetarget
    {
        /// <summary>式A（レガシー・親相対バインド差分）: target = live * inv(ovrBind) * varBind。
        /// 親相対軸がほぼ揃ったリグ間でのみ有効。現在の利用者は RemyAvatarRig の指
        /// （OVR→mixamo。2026-07-07 実機検証済みなので温存）。**外部パック手には使わない**（上記教訓）。</summary>
        public static Quaternion Solve(Quaternion liveLocal, Quaternion ovrBind, Quaternion varBind)
            => liveLocal * Quaternion.Inverse(ovrBind) * varBind;

        /// <summary>手首の幾何フレーム（指方向×甲法線）。両リグを同じ規約で測って整列に使う。
        /// 甲法線の cross 向きは AlignRestForward（2026-07-09 検証済み）と同一規約。</summary>
        public static Quaternion WristFrame(Vector3 wrist, Vector3 index1, Vector3 middle1, Vector3 pinky1, bool isRight)
        {
            Vector3 fwd = middle1 - wrist;
            if (fwd.sqrMagnitude < 1e-10f) return Quaternion.identity;
            fwd.Normalize();
            Vector3 lateral = pinky1 - index1;
            Vector3 up = isRight ? Vector3.Cross(fwd, lateral) : Vector3.Cross(lateral, fwd);
            if (up.sqrMagnitude < 1e-10f) up = Vector3.up;
            return Quaternion.LookRotation(fwd, up.normalized);
        }

        // 各指の BoneId チェーン（親→子）。aim 補正で「白手の同関節が指す方向」に pack を実際に向ける。
        private static readonly int[][] FingerChains =
        {
            new[] { 2, 3, 4, 5 },      // thumb
            new[] { 6, 7, 8 },         // index
            new[] { 9, 10, 11 },       // middle
            new[] { 12, 13, 14 },      // ring
            new[] { 15, 16, 17, 18 },  // pinky
        };

        /// <summary>参照コピー式 + aim 補正の適用（毎フレーム）。
        /// 1) metaBones（隠し白手＝正解系）へ live ローカル回転を代入。
        /// 2) packBones[i].rotation = metaBones[i].rotation * offsets[i]（回転の大枠 + roll を白手から移植）。
        /// 3) **aim 補正**: 各指チェーンを親→子に辿り、pack bone の「子への向き」を白手の同関節の
        ///    「子への向き」へ実際に回して合わせる。参照コピーだけだとリグ固有のボーン軸差で
        ///    見える指方向がズレる（2026-07-12 実測: 親指 100°）ため、視覚的な指方向を構造的に一致させる。
        /// bone が null の index はスキップ。smooth&gt;=1 でスナップ。</summary>
        public static void ApplyFromReference(Quaternion[] liveLocals,
            Transform?[] metaBones, Transform?[] packBones, Quaternion[] offsets, float smooth)
        {
            int n = Mathf.Min(Mathf.Min(liveLocals.Length, metaBones.Length),
                              Mathf.Min(packBones.Length, offsets.Length));
            // (1) 白手を live で駆動
            for (int i = 0; i < n; i++)
            {
                var m = metaBones[i];
                if (m != null) m.localRotation = liveLocals[i];
            }
            // (2) 参照コピー（roll と大枠の姿勢）
            for (int i = 0; i < n; i++)
            {
                var m = metaBones[i];
                var p = packBones[i];
                if (m == null || p == null) continue;
                p.rotation = m.rotation * offsets[i];
            }
            // (3) aim 補正: 指チェーンを親→子に辿り、pack の子方向を白手の子方向へ向ける。
            //     親を回すと子の位置が動くので、必ず親から順に処理する。
            foreach (var chain in FingerChains)
            {
                for (int k = 0; k < chain.Length - 1; k++)
                {
                    int a = chain[k];
                    // 次の「両手ともマップ済み」bone を子とする（Realistic の親指 thumb2 欠けを跨ぐ）
                    int cIdx = -1;
                    for (int j = k + 1; j < chain.Length; j++)
                    {
                        int cand = chain[j];
                        if (cand < n && packBones[cand] != null && metaBones[cand] != null) { cIdx = cand; break; }
                    }
                    if (cIdx < 0 || a >= n) continue;
                    var pa = packBones[a]; var pc = packBones[cIdx];
                    var ma = metaBones[a]; var mc = metaBones[cIdx];
                    if (pa == null || pc == null || ma == null || mc == null) continue;

                    Vector3 packDir = pc.position - pa.position;
                    Vector3 whiteDir = mc.position - ma.position;
                    if (packDir.sqrMagnitude < 1e-10f || whiteDir.sqrMagnitude < 1e-10f) continue;
                    var aim = Quaternion.FromToRotation(packDir, whiteDir);
                    pa.rotation = aim * pa.rotation; // bone a を回すと子 pc が whiteDir 側へ動く
                }
            }
            // 平滑は簡潔化のためスナップ運用（外部リグは smooth 前提で呼ばれていない。将来必要なら
            // aim 後の姿勢を前フレームと Slerp する）。
        }
    }
}
