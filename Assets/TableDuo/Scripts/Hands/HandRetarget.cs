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

        /// <summary>参照コピー式の適用（毎フレーム）。
        /// 1) metaBones へ live ローカル回転を代入（正解系＝白手そのものを駆動）
        /// 2) packBones[i].rotation = metaBones[i].rotation * offsets[i]（親→子の index 順）
        /// bone が null / 情報が無い index はスキップ。smooth&gt;=1 でスナップ。</summary>
        public static void ApplyFromReference(Quaternion[] liveLocals,
            Transform?[] metaBones, Transform?[] packBones, Quaternion[] offsets, float smooth)
        {
            int n = Mathf.Min(Mathf.Min(liveLocals.Length, metaBones.Length),
                              Mathf.Min(packBones.Length, offsets.Length));
            for (int i = 0; i < n; i++)
            {
                var m = metaBones[i];
                if (m != null) m.localRotation = liveLocals[i];
            }
            bool snap = smooth >= 1f;
            for (int i = 0; i < n; i++)
            {
                var m = metaBones[i];
                var p = packBones[i];
                if (m == null || p == null) continue;
                var target = m.rotation * offsets[i];
                p.rotation = snap ? target : Quaternion.Slerp(p.rotation, target, smooth);
            }
        }
    }
}
