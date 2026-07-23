#nullable enable

namespace TableDuoVr.Hands
{
    /// <summary>
    /// 調査条件を 1 バイトへ詰める wire コーデック（cross-device 条件同期の単一情報源）。
    /// <see cref="TableDuoVr.Net.TableDuoPlayer"/> の _studyFlags（NetworkVariable&lt;byte&gt;）が運ぶ値の
    /// ビット配置をここに集約し、Pack/decode を NGO 非依存の純関数として固定する。
    ///
    /// ビット配置:
    ///   bit0   = 頭マーカー（marker）
    ///   bit1   = 片手モード（oneHand）
    ///   bit2-3 = 手バリアント（<see cref="HandVariant"/> 0..3。2bit にちょうど収まる）
    ///   bit4   = 自己ボディ表示（selfBodyActive）
    ///
    /// ⚠ variant が 4 値（2bit）を超えると bit4（selfBody）を汚す。<see cref="HandVariantCycle.Count"/>==4 と
    /// この不変は <see cref="HandVariant"/> 拡張時に必ず一緒に見直すこと（StudyFlagsCodecTests が pin）。
    /// </summary>
    public static class StudyFlags
    {
        /// <summary>調査条件を 1 バイトへ詰める。<see cref="TableDuoVr.Net.TableDuoPlayer"/>.WriteStudyFlags 由来。</summary>
        public static byte Pack(bool marker, bool oneHand, HandVariant variant, bool selfBodyActive)
            => (byte)((marker ? 1 : 0)
                | (oneHand ? 2 : 0)
                | ((byte)variant << 2)
                | (selfBodyActive ? 16 : 0));

        /// <summary>bit0=頭マーカー。</summary>
        public static bool Marker(byte f) => (f & 1) != 0;

        /// <summary>bit1=片手モード。</summary>
        public static bool OneHand(byte f) => (f & 2) != 0;

        /// <summary>bit4=自己ボディ表示。</summary>
        public static bool SelfBody(byte f) => (f & 16) != 0;

        /// <summary>bit2-3=手バリアント（申告値）。</summary>
        public static HandVariant Variant(byte f) => (HandVariant)((f >> 2) & 0x3);
    }
}
