#nullable enable
using TableDuoVr.Hands;
using Unity.Netcode;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// PC ホスト（実験者卓）専用のファシリテータ IMGUI。
    /// 設計思想: Quest 側＝体験（コントローラは視点リセットのみ）、PC ホスト側＝運用。
    /// ボドゲ切替・手の見た目強制・盤面リセット・マークをサーバ権威でここに集約する。
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
        private SessionLogger? _logger;
        private string _markText = "";

        private void Start()
        {
            _gameSwitcher = FindObjectOfType<GameSwitcher>();
            _boardReset = FindObjectOfType<BoardReset>();
            _algoDealer = FindObjectOfType<AlgoDealer>();
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

        private void DrawHandSection(NetworkManager nm)
        {
            GUILayout.Label("― 手の見た目 ―");
            foreach (var client in nm.ConnectedClientsList)
            {
                var po = client.PlayerObject;
                var player = po != null ? po.GetComponent<TableDuoPlayer>() : null;
                if (player == null) continue;
                GUILayout.Label($"client{client.ClientId} {RoleLabel(player.Role)}");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("白手")) player.ServerForceHandVariant(HandVariant.Default);
                if (GUILayout.Button("リアル")) player.ServerForceHandVariant(HandVariant.Realistic);
                if (GUILayout.Button("ロボ")) player.ServerForceHandVariant(HandVariant.Robot);
                GUILayout.EndHorizontal();
            }
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

        private static string RoleLabel(StudyConfig.Role? role) => role switch
        {
            StudyConfig.Role.Full => "人役",
            StudyConfig.Role.Hand => "手役",
            StudyConfig.Role.Spectator => "観戦",
            _ => "?",
        };
    }
}
