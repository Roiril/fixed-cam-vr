#nullable enable
using UnityEngine;

namespace TableDuoVr.Hands
{
    /// <summary>
    /// OVR bone のローカル回転を、別バインドポーズのメッシュ bone へ移すバインド差分リターゲット。
    ///
    /// 同期される <c>liveLocal</c> は OVRSkeleton bone の親相対ローカル回転（バインド込みの絶対ローカル）。
    /// Meta メッシュはバインドが OVR と同一なので <c>bone.localRotation = liveLocal</c> で正しく曲がる。
    /// 別リグ（Male/Robot）はバインドが違うため直接代入すると曲がり方が壊れる。そこで
    /// 「バインドからの動き（親フレーム）」= liveLocal * inv(ovrBind) を、メッシュ側バインド varBind に載せ替える。
    ///
    ///   target = liveLocal * inv(ovrBind) * varBind
    ///
    /// 静止時（liveLocal==ovrBind）は target==varBind（メッシュ自身の休めポーズ）に収束する。
    /// 前提: 両リグの各 bone の親相対軸がほぼ揃っていること（指がほぼ同方向に伸びた自然リグ）。
    /// 軸が大きく食い違う場合は曲がり軸がずれるので実機で要確認。
    /// </summary>
    public static class HandRetarget
    {
        public static Quaternion Solve(Quaternion liveLocal, Quaternion ovrBind, Quaternion varBind)
            => liveLocal * Quaternion.Inverse(ovrBind) * varBind;
    }
}
