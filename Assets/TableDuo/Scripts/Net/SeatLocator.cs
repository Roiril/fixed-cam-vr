#nullable enable
using Unity.Netcode;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>席アンカーの検索（シーン階層パス規約: [TableDuo]/Seats/SeatN）。</summary>
    public static class SeatLocator
    {
        public static Transform? Find(int index)
        {
            var go = GameObject.Find($"[TableDuo]/Seats/Seat{index}");
            return go != null ? go.transform : null;
        }

        /// <summary>clientId → 席 index（ホスト=0 / それ以外=1）。</summary>
        public static int SeatIndexOf(ulong clientId) =>
            clientId == NetworkManager.ServerClientId ? 0 : 1;

        public static Transform? FindByClient(ulong clientId) => Find(SeatIndexOf(clientId));
    }
}
