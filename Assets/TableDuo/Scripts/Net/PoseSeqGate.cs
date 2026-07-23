#nullable enable
namespace TableDuoVr.Net
{
    /// <summary>
    /// Unreliable pose の後着・重複を棄却する連番ゲート（純述語）。
    /// uint wraparound を符号付き差分で判定する（一周しても直近との前後関係を正しく取る）。
    /// 状態（clientId ごとの lastAccepted）は <see cref="ConnectionManager"/> の Dictionary に残し、
    /// 判定式だけをここへ切り出す（behavior-preserving・wraparound をテストで直接固定するため）。
    /// </summary>
    public static class PoseSeqGate
    {
        /// <summary>incoming が lastAccepted より新しければ true（採用）。</summary>
        public static bool Accept(uint incoming, uint lastAccepted) => (int)(incoming - lastAccepted) > 0;
    }
}
