#nullable enable
using TableDuoVr.Hands;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 診断用: 各席に静的アバター（既定の着座ポーズ）を先置きする。**疎通ゼロで「描画」だけ先に確認**でき、
    /// その席に実プレイヤーが接続したら静的を撤去してライブ（追従）アバターに差し替える（<see cref="HideSeat"/>）。
    /// これで「描画 / 疎通 / トラッキング」を段階的に切り分けられる:
    ///   静的が出ない＝描画/カメラ問題 / 接続で静的が消えライブが出る＝疎通OK / 出たが固まる＝pose/トラッキング問題。
    ///
    /// opt-in（<see cref="StudyConfig.PreplaceAvatars"/> / tdv_preplace=on）。**研究本番は無効**
    /// （相手不在時に席へアバターが居ると体験が変わり妥当性を汚すため）。
    /// 自分の席には置かない（一人称視点に自分のアバターが被らないように）。
    /// ライブ接続パス（NGO）には一切干渉しない純粋な add-on（先置きは非ネットワークのローカル描画）。
    /// </summary>
    public sealed class SeatAvatarPreview : MonoBehaviour
    {
        public static SeatAvatarPreview? Instance { get; private set; }

        private const int SeatCount = 2;
        private readonly RemoteAvatarView?[] _previews = new RemoteAvatarView?[SeatCount];

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Start()
        {
            int ownSeat = StudyConfig.ForcedRole is { } r ? SeatIndexOf(r) : -1;
            PlaceIfNeeded(0, ownSeat, handsOnly: false); // 席0=人役（Remy 等）
            PlaceIfNeeded(1, ownSeat, handsOnly: true);  // 席1=手役（手メッシュ）
        }

        private void PlaceIfNeeded(int seatIndex, int ownSeat, bool handsOnly)
        {
            if (seatIndex == ownSeat) return; // 自分の席には置かない
            var seat = SeatLocator.Find(seatIndex);
            if (seat == null) return;
            var view = RemoteAvatarView.Create(seat, handsOnly);
            view.PoseImmediate(NeutralPose());
            _previews[seatIndex] = view;
            Debug.Log($"[TableDuo] 診断: 席{seatIndex} に静的アバターを先置き（{(handsOnly ? "手役" : "人役")}）");
        }

        /// <summary>その席に実プレイヤーが接続したら静的を撤去（ライブアバターに差し替え）。</summary>
        public void HideSeat(int seatIndex)
        {
            if (seatIndex < 0 || seatIndex >= _previews.Length) return;
            var v = _previews[seatIndex];
            if (v != null)
            {
                Destroy(v.gameObject);
                _previews[seatIndex] = null;
                Debug.Log($"[TableDuo] 診断: 席{seatIndex} ライブ接続 → 静的アバター撤去");
            }
        }

        private static int SeatIndexOf(StudyConfig.Role role) => role switch
        {
            StudyConfig.Role.Full => 0,
            StudyConfig.Role.Hand => 1,
            _ => -1, // Spectator 等は自分の席を持たない → 両席に先置き
        };

        // 既定の着座ポーズ（席ローカル空間）。頭=席原点（目線アンカー）、両手=机上前方。
        private static AvatarPose NeutralPose() => new()
        {
            HeadPos = Vector3.zero,
            HeadRot = Quaternion.identity,
            WristPosR = new Vector3(0.24f, -0.40f, 0.34f),
            WristRotR = Quaternion.Euler(10f, 0f, 0f),
            WristPosL = new Vector3(-0.24f, -0.40f, 0.34f),
            WristRotL = Quaternion.Euler(10f, 0f, 0f),
            TrackedR = true,
            TrackedL = true,
        };
    }
}
