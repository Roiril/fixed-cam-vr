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

        /// <summary>
        /// clientId → 席。役割制（tdv_role）のため TableDuoPlayer の確定済み席を引く。
        /// サーバ側での利用を想定（Grabbable の手 pose 解決）。
        /// </summary>
        public static Transform? FindByClient(ulong clientId)
        {
            var nm = NetworkManager.Singleton;
            var playerObj = nm != null ? nm.SpawnManager.GetPlayerNetworkObject(clientId) : null;
            if (playerObj == null) return null;
            var player = playerObj.GetComponent<TableDuoPlayer>();
            if (player == null || player.SeatIndex < 0) return null;
            return Find(player.SeatIndex);
        }
    }
}
