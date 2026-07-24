#nullable enable
using TableDuoVr.Hands;
using Unity.Netcode;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// PC ホスト（実験者卓）専用の運営パネル（IMGUI）。
    /// 設計思想: Quest 側＝体験（コントローラは視点リセットのみ）、PC ホスト側＝運用。
    /// ゲーム切替・手役の見た目切替・盤面操作・映像記録・記録マークをサーバ権威でここに集約する。
    /// 手の見た目は「手役（手だけアバター）の手」だけを対象にした選択グリッド。切替は
    /// 手役の申告値同期で人役の視界・ホスト観戦の視界にも自動反映される（TableDuoPlayer 参照）。
    /// 調査本番（tdv フラグ起動）でも表示する（実験者用のため）。
    /// 画面右端に置き、左上の Spectator GUI と重ねない。映像記録（俯瞰+人役 FPV の 2 本 AVI）は
    /// <see cref="SpectatorRecorder"/> をここから開始/停止する。通信記録（WireTap）はデバッグ専用パネル
    /// （左下・WireTapRecorder.OnGUI）へ分離（2026-07-24）。F10 で表示トグル。
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
        private SpectatorController? _spectator;
        private SpectatorRecorder? _recorder;
        private string _markText = "";

        // 手役の見た目バリアント（白手/リアル/ロボ/Remy）。Remy=人役と同じフル Remy 化。
        // 押すと申告値同期で手役本人・人役の視界・ホスト観戦の全端末に反映される。
        private static readonly HandVariant[] HandChoices =
            { HandVariant.Default, HandVariant.Realistic, HandVariant.Robot, HandVariant.FullBody };
        private static readonly string[] HandLabels =
            { "白手", "リアル", "ロボ", "Remy" };

        // 遅延生成する GUI スタイル（OnGUI 内でしか GUI.skin を触れないため）
        private GUIStyle? _titleStyle;
        private GUIStyle? _headerStyle;
        private GUIStyle? _hintStyle;
        private GUIStyle? _recOnStyle;
        private GUIStyle? _recOffStyle;

        private void Start()
        {
            _gameSwitcher = FindObjectOfType<GameSwitcher>();
            _boardReset = FindObjectOfType<BoardReset>();
            _algoDealer = FindObjectOfType<AlgoDealer>();
            _bandidoDealer = FindObjectOfType<BandidoDealer>();
            _logger = FindObjectOfType<SessionLogger>();
            _spectator = FindObjectOfType<SpectatorController>();
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F10)) _visible = !_visible;
        }

        private void EnsureStyles()
        {
            if (_titleStyle != null) return;
            _titleStyle = new GUIStyle(GUI.skin.label)
                { fontStyle = FontStyle.Bold, fontSize = 15 };
            _headerStyle = new GUIStyle(GUI.skin.label)
                { fontStyle = FontStyle.Bold };
            _headerStyle.normal.textColor = new Color(0.62f, 0.80f, 1f);
            _hintStyle = new GUIStyle(GUI.skin.label) { fontSize = 10, wordWrap = true };
            _hintStyle.normal.textColor = new Color(0.7f, 0.7f, 0.7f);
            _recOnStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            _recOnStyle.normal.textColor = new Color(0.4f, 0.9f, 0.45f);
            _recOffStyle = new GUIStyle(GUI.skin.label);
            _recOffStyle.normal.textColor = new Color(0.75f, 0.75f, 0.75f);
        }

        private void OnGUI()
        {
            if (Application.platform == RuntimePlatform.Android) return; // PC ホスト専用
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening || !nm.IsServer) return;
            if (!_visible) return;

            EnsureStyles();

            const float w = 280f;
            var area = new Rect(Screen.width - w - 10f, 10f, w, Screen.height - 20f);
            GUILayout.BeginArea(area, GUI.skin.box);

            GUILayout.Label("運営パネル", _titleStyle);
            GUILayout.Label("F10 で表示切替", _hintStyle);
            GUILayout.Space(6f);

            DrawGameSection();
            DrawHandSection(nm);
            DrawBoardSection();
            DrawVideoSection();
            DrawMarkSection();

            GUILayout.EndArea();
        }

        /// <summary>セクション見出し（前に区切りの余白 + 色付き太字）。</summary>
        private void Header(string text)
        {
            GUILayout.Space(10f);
            GUILayout.Label(text, _headerStyle);
        }

        private void DrawGameSection()
        {
            Header("ゲーム選択");
            if (_gameSwitcher == null) _gameSwitcher = FindObjectOfType<GameSwitcher>();
            if (_gameSwitcher == null)
            {
                GUILayout.Label("(GameSwitcher 不在)", _hintStyle);
                return;
            }
            int n = _gameSwitcher.GameCount;
            var names = new string[n];
            for (int i = 0; i < n; i++) names[i] = _gameSwitcher.GetDisplayName(i);
            int active = _gameSwitcher.ActiveIndex;
            // SelectionGrid: 選択中セルが押下状態でハイライトされ、折返しも自動（2 列）
            int sel = GUILayout.SelectionGrid(active, names, 2);
            if (sel != active) _gameSwitcher.ServerSetActiveGame(sel);
        }

        private void DrawHandSection(NetworkManager nm)
        {
            Header("手役の見た目");
            var hand = FindHandPlayer(nm);
            if (hand == null)
            {
                GUILayout.Label("手役はまだ接続していません", _hintStyle);
                return;
            }
            HandVariant current = hand.DeclaredHandVariant;
            int curIdx = System.Array.IndexOf(HandChoices, current);
            if (curIdx < 0) curIdx = 0;
            int sel = GUILayout.SelectionGrid(curIdx, HandLabels, 2);
            if (sel != curIdx) hand.ServerForceHandVariant(HandChoices[sel]);
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
            Header("盤面操作");
            if (GUILayout.Button("盤面を初期配置に戻す"))
            {
                if (_boardReset == null) _boardReset = FindObjectOfType<BoardReset>();
                _boardReset?.ResetBoard();
            }
            // 配り直しは、そのゲームがアクティブなときだけ出す（他ゲーム中は無関係なので隠す）
            string? activeId = _gameSwitcher != null
                ? _gameSwitcher.GetGameId(_gameSwitcher.ActiveIndex) : null;
            if (activeId == "algo" && GUILayout.Button("カードを配り直す（アルゴ）"))
            {
                if (_algoDealer == null) _algoDealer = FindObjectOfType<AlgoDealer>();
                _algoDealer?.ServerShuffleDeal();
            }
            if (activeId == "bandido" && GUILayout.Button("カードを配り直す（バンディド）"))
            {
                if (_bandidoDealer == null) _bandidoDealer = FindObjectOfType<BandidoDealer>();
                _bandidoDealer?.ServerShuffleDeal();
            }
        }

        private void DrawVideoSection()
        {
            Header("映像記録");
            if (_spectator == null) _spectator = FindObjectOfType<SpectatorController>();
            if (_spectator == null || !_spectator.IsActive)
            {
                GUILayout.Label("(観戦カメラ不在)", _hintStyle);
                return;
            }
            // scene 配置に依存せず on-demand 生成（Setup/scene を変えずに済む）
            if (_recorder == null)
                _recorder = FindObjectOfType<SpectatorRecorder>()
                    ?? new GameObject("SpectatorRecorder").AddComponent<SpectatorRecorder>();

            if (_recorder.IsRecording)
            {
                int sec = (int)_recorder.ElapsedSec;
                GUILayout.Label($"● 録画中  {sec / 60:00}:{sec % 60:00}", _recOnStyle);
                if (!_recorder.PersonConnected)
                    GUILayout.Label("人役 未接続（接続後に追従開始）", _hintStyle);
                if (_recorder.DroppedFrames > 0)
                    GUILayout.Label($"ドロップ {_recorder.DroppedFrames} フレーム", _hintStyle);
                if (GUILayout.Button("■ 録画停止")) _recorder.StopRecording();
            }
            else
            {
                GUILayout.Label("停止中", _recOffStyle);
                if (GUILayout.Button("● 録画開始（俯瞰+人役）")) _recorder.StartRecording();
            }
            GUILayout.Label("保存先: persistentDataPath/tdv_recordings", _hintStyle);
        }

        private void DrawMarkSection()
        {
            Header("記録マーク");
            GUILayout.Label("セッション CSV に注記を残します", _hintStyle);
            GUILayout.BeginHorizontal();
            _markText = GUILayout.TextField(_markText);
            if (GUILayout.Button("送信", GUILayout.Width(56f)))
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
