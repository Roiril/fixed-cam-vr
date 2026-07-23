#nullable enable

namespace TableDuoVr.Hands
{
    /// <summary>
    /// バリアント別の「BoneId → メッシュ側 bone Transform 名」対応表（単一情報源）。
    /// 同期される bone 配列は OVRSkeleton の BoneId 順（<see cref="HandBoneTable"/>）。各バリアントの
    /// リグはこれと別命名なので、駆動時に BoneId からメッシュ側の bone 名へ引き当てて Transform を探す。
    ///
    /// 実リグ確認（2026-07-01, VR Hands Starter Pack を Unity にインポートし SkinnedMeshRenderer.bones を実測）:
    /// - Male（Realistic）: 側サフィックス ".R"/".L"。各指 Pre(中手骨)→Lower(基節)→Medium(中節)→Upward(末節)。
    ///   親指のみ 3 本（Pre→Lower→Upward、Medium 無し）。手首＝Root。
    /// - Robot: 側サフィックス無し（両手プレハブとも "Bone_*"）。各指 Pre→Lower→Middle→Upper（親指も 4 本）。手首＝Bone_Hand。
    ///
    /// OVR 側の指は基節から（index1=基節）で中手骨を持たない（親指と小指のみ pinky0/thumb0 が中手骨相当）。
    /// よって各指の Pre(中手骨) は OVR に対応 bone が無く駆動しない（バインドのまま＝ほぼ剛体で自然）。
    /// </summary>
    public static class HandVariantTable
    {
        /// <summary>BoneId i のメッシュ側 bone 名。対応 bone が無ければ null（駆動せずバインド維持）。</summary>
        public static string? BoneName(HandVariant variant, int boneId, bool isRight) => variant switch
        {
            HandVariant.Default => HandBoneTable.FbxBoneName(boneId, isRight),
            HandVariant.Realistic => MaleName(boneId, isRight),
            HandVariant.Robot => RobotName(boneId),
            _ => null,
        };

        /// <summary>手首（BoneId 0）のメッシュ側 bone 名。placement / scale 基準に使う。</summary>
        public static string? WristBoneName(HandVariant variant, bool isRight) => BoneName(variant, 0, isRight);

        /// <summary>購入パックの別リグ（バインド差分リターゲット + placement/scale が要る）＝Realistic/Robot のみ。
        /// ⚠ FullBody は手メッシュ差し替えではなくフル Remy 経路（RemoteAvatarView/LocalSelfBody）なので
        /// ここに含めない（含めるとパック手構築へ流れて prefab 不在で失敗する）。</summary>
        public static bool IsExternalRig(HandVariant variant)
            => variant == HandVariant.Realistic || variant == HandVariant.Robot;

        // --- Male Hand（Realistic）。stem + 側サフィックス ---
        private static string? MaleName(int boneId, bool isRight)
        {
            string? stem = boneId switch
            {
                0 => "Root",           // 手首（skinning root）
                2 => "PreThumb",       // 親指 中手骨(CMC)
                3 => "LowerThumb",     // 親指 基節(MCP)
                // Male の親指は 3 節（Medium 無し）。末節 Upward には OVR **thumb3（先端）** を当てる。
                // 旧: thumb2(=4) を当てていたが、先端の曲げ（thumb3）が捨てられ Robot（親指 4 節フル対応）と
                // 曲がり方が食い違った（2026-07-12 実機指摘）。参照コピー式は白手 thumb3 の実ワールド回転を
                // コピーするので、先端の向きが白手/Robot と一致する（中間節分の位置差は 3 節リグの構造上の限界）。
                5 => "UpwardThumb",    // 親指 末節(IP) ← OVR thumb3
                6 => "LowerIndex", 7 => "MediumIndex", 8 => "UpwardIndex",
                9 => "LowerMiddle", 10 => "MediumMiddle", 11 => "UpwardMiddle",
                12 => "LowerRing", 13 => "MediumRing", 14 => "UpwardRing",
                15 => "PrePink",       // 小指 中手骨（OVR pinky0）
                16 => "LowerPink", 17 => "MediumPink", 18 => "UpwardPink",
                // 指先 tip（末節 Upward の Blender leaf "_end"）。末節 bone の aim 方向参照点のみ（回転駆動しない）。
                19 => "UpwardThumb", 20 => "UpwardIndex", 21 => "UpwardMiddle",
                22 => "UpwardRing", 23 => "UpwardPink",
                _ => null,
            };
            if (stem == null) return null;
            // _end leaf は側サフィックスの**後ろ**に付く（例 UpwardIndex.R_end）
            return stem + (isRight ? ".R" : ".L") + (HandBoneTable.IsFingerTip(boneId) ? "_end" : "");
        }

        // --- Robot Hand。側サフィックス無し（両手プレハブとも同名） ---
        private static string? RobotName(int boneId) => boneId switch
        {
            0 => "Bone_Hand",
            2 => "Bone_PreThumb", 3 => "Bone_ThumbLower", 4 => "Bone_ThumbMiddle", 5 => "Bone_ThumbUpper",
            6 => "Bone_IndexLower", 7 => "Bone_IndexMiddle", 8 => "Bone_IndexUpper",
            9 => "Bone_MiddleLower", 10 => "Bone_MiddleMiddle", 11 => "Bone_MiddleUpper",
            12 => "Bone_RingLower", 13 => "Bone_RingMiddle", 14 => "Bone_RingUpper",
            15 => "Bone_PrePink", 16 => "Bone_PinkLower", 17 => "Bone_PinkMiddle", 18 => "Bone_PinkUpper",
            // 指先 tip（末節 Upper の Blender leaf "_end"・左右同名）。aim 方向参照点のみ（回転駆動しない）。
            19 => "Bone_ThumbUpper_end", 20 => "Bone_IndexUpper_end", 21 => "Bone_MiddleUpper_end",
            22 => "Bone_RingUpper_end", 23 => "Bone_PinkUpper_end",
            _ => null,
        };
    }
}
