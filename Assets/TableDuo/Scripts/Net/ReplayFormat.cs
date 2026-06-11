#nullable enable

namespace TableDuoVr.Net
{
    /// <summary>
    /// セッションリプレイのバイナリフォーマット定義（TDVR1）。
    /// 書き手: SessionReplayRecorder（ホスト）/ 読み手: ReplayViewer（Editor）。
    ///
    /// ヘッダ: magic(int) / version(byte) / startEpochMs(long) / studyConfig(string)
    /// 以降レコード列: type(byte) + epochMs(long) + ペイロード
    /// </summary>
    public static class ReplayFormat
    {
        public const int Magic = 0x54445652; // "TDVR"
        public const byte Version = 1;

        public const byte RecPose = 0x01;         // clientId(ulong) + AvatarPose（PoseRecordingFile.WritePose）
        public const byte RecProp = 0x02;         // propId(byte) + pos(3f) + rot(4f)
        public const byte RecEvent = 0x03;        // label(string) + detail(string)
        public const byte RecRole = 0x04;         // clientId(ulong) + role(byte) + seat(byte)
        public const byte RecPropRegistry = 0x05; // propId(byte) + name(string)
        public const byte RecLayouts = 0x06;      // layoutL + layoutR（PoseRecordingFile.WriteLayout）
    }
}
