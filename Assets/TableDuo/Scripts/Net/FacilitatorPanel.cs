#nullable enable
using TableDuoVr.Hands;
using Unity.Netcode;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// PC ホスト（実験者卓）専用のファシリテータ IMGUI。
    /// 設計思想: Quest 側＝体験（コントローラは視点リセットのみ）、PC ホスト側＝運用。
    /// ボドゲ切替・手役の手の見た目切替・盤面リセット・マークをサーバ権威でここに集約する。
    /// 手の見た目は「手役（手だけアバター）の手」だけを対象にした巡回ボタン 1 個。切替は
    /// 手役の申告値同期で人役の視界・ホスト観戦の視界にも自動反映される（TableDuoPlayer 参照）。
    /// 調査本番（tdv フラグ起動）でも表示する（実験者用のため）。
    /// 画面右端に置き、左上の ConnectionManager / WireTap / Spectator GUI と重ねない。F10 で表示トグル。
    /// TableDuoSceneSetup が Systems へ AddComponent する。
    /// </summary>
    public sealed class FacilitatorPanel : MonoBehaviour
    {
        private bool _visible = true;
        private GameSwitcher? _gameSwitcher;
        private BoardReset? _boardReset;
        private AlgoDealer? _algoDealer;
        private BandidoDealer? _bandidoDealer;
        private SessionLogger? _logger;
        private string _markText = "";

        private void Start()
        {
            _gameSwitcher = FindObjectOfType<GameSwitcher>();
            _boardReset = FindObjectOfType<BoardReset>();
            _algoDealer = FindObjectOfType<AlgoDealer>();
            _bandidoDealer = FindObjectOfType<BandidoDealer>();
            _logger = FindObjectOfType<SessionLogger>();
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F10)) _visible = !_visible;
        }

        private void OnGUI()
        {
            if (Application.platform == RuntimePlatform.Android) return; // PC ホスト専用
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening || !nm.IsServer) return;
            if (!_visible) return;

            const float w = 250f;
            var area = new Rect(Screen.width - w - 10f, 10f, w, Screen.height - 20f);
            GUILayout.BeginArea(area, GUI.skin.box);
            GUILayout.Label("ファシリテータ卓 (F10)");

            DrawGameSection();
            GUILayout.Space(8f);
            DrawHandSection(nm);
            GUILayout.Space(8f);
            DrawBoardSection();
            GUILayout.Space(8f);
            DrawMarkSection();

            GUILayout.EndArea();
        }

        private void DrawGameSection()
        {
            GUILayout.Label("― ボドゲ ―");
            if (_gameSwitcher == null) _gameSwitcher = FindObjectOfType<GameSwitcher>();
            if (_gameSwitcher == null)
            {
                GUILayout.Label("(GameSwitcher 不在)");
                return;
            }
            int active = _gameSwitcher.ActiveIndex;
            GUILayout.BeginHorizontal();
            for (int i = 0; i < _gameSwitcher.GameCount; i++)
            {
                bool isActive = i == active;
                GUI.enabled = !isActive; // アクティブは押せない見た目で強調
                string label = isActive ? "▶ " + _gameSwitcher.GetDisplayName(i) : _gameSwitcher.GetDisplayName(i);
                if (GUILayout.Button(label)) _gameSwitcher.ServerSetActiveGame(i);
                GUI.enabled = true;
            }
            GUILayout.EndHorizontal();
        }

        // 手役（Role=Hand）の見た目バリアント選択ボタン。白手/リアル/ロボ=従来の手だけアバター、
        // Remy=人役と同じフル Remy 化（本人は頭潰し自己ボディ + 両手トラッキング / 他視点は Remy IK）。
        // 押すと申告値同期（_studyFlags）で手役本人・人役の視界・ホスト観戦の全端末に反映される
        //（TableDuoPlayer.ServerForceHandVariant）。人役自身の見た目には触らない。
        private static readonly HandVariant[] HandChoices =
            { HandVariant.Default, HandVariant.Realistic, HandVariant.Robot, HandVariant.FullBody };

        private void DrawHandSection(NetworkManager nm)
        {
            GUILayout.Label("― 手役の見た目 ―");
            var hand = FindHandPlayer(nm);
            if (hand == null)
            {
                GUILayout.Label("(手役 未接続)");
                return;
            }
            // 現在の見た目 = 手役が全視点へ申告している同期値。現在値は押せない見た目で強調
            HandVariant current = hand.DeclaredHandVariant;
            GUILayout.BeginHorizontal();
            foreach (var v in HandChoices)
            {
                bool isActive = v == current;
                GUI.enabled = !isActive;
                string label = isActive ? "▶ " + HandVariantCycle.Label(v) : HandVariantCycle.Label(v);
                if (GUILayout.Button(label)) hand.ServerForceHandVariant(v);
                GUI.enabled = true;
            }
            GUILayout.EndHorizontal();
        }

        /// <summary>接続中プレイヤーから手役（Role=Hand）を1体探す。居なければ null。</summary>
        private static TableDuoPlayer? FindHandPlayer(NetworkManager nm)
        {
            foreach (var client in nm.ConnectedClientsList)
            {
                var po = client.PlayerObject;
                var player = po != null ? po.GetComponent<TableDuoPlayer>() : null;
                if (player != null && player.Role == StudyConfig.Role.Hand) return player;
            }
            return null;
        }

        private void DrawBoardSection()
        {
            GUILayout.Label("― 盤面 ―");
            if (GUILayout.Button("盤面リセット"))
            {
                if (_boardReset == null) _boardReset = FindObjectOfType<BoardReset>();
                _boardReset?.ResetBoard();
            }
            if (GUILayout.Button("アルゴ配り直し"))
            {
                if (_algoDealer == null) _algoDealer = FindObjectOfType<AlgoDealer>();
                _algoDealer?.ServerShuffleDeal();
            }
            if (GUILayout.Button("バンディド配り直し"))
            {
                if (_bandidoDealer == null) _bandidoDealer = FindObjectOfType<BandidoDealer>();
                _bandidoDealer?.ServerShuffleDeal();
            }
        }

        private void DrawMarkSection()
        {
            GUILayout.Label("― マーク ―");
            GUILayout.BeginHorizontal();
            _markText = GUILayout.TextField(_markText);
            if (GUILayout.Button("送信", GUILayout.Width(48f)))
            {
                if (_logger == null) _logger = FindObjectOfType<SessionLogger>();
                if (!string.IsNullOrEmpty(_markText))
                {
                    _logger?.LogEvent("mark", _markText);
                    _markText = "";
                }
            }
            GUILayout.EndHorizontal();
        }
    }
}
