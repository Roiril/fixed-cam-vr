# Project Memory Index

- [proposal_single_source.md](proposal_single_source.md) - 企画書の正は docs/proposal/ の 1 本だけ。docs/archive/ の旧版は体験構成が別物なので読まない（新旧の要求対応表つき）

- [take_continuity.md](take_continuity.md) - 演出の待ち・連続・継ぎ目：持ち越しは因果条件で絞る／chainNext には安全網／遷移は割り込む側が所有する／相乗り分岐にテストが無い
- [show_run_skeleton.md](show_run_skeleton.md) - 体験の骨格（導入→3周→終了）を触る前に：ゲートは CueScheduler 1 点／終了は次フレーム／導入で録画を消さない／凍結を増やさない
- [glitch_and_latency.md](glitch_and_latency.md) - 乱れ演出と遅延計測：_Glitch と _SignalLost は別系統／post は 4 箇所同時に直す／絶対 E2E は測っていない（配信側に /clock が要る）

- [codex_image_pipeline.md](codex_image_pipeline.md) - 卓の 🪄 から Codex で素材生成。背景保存は実測で成立（差分＝マスク）／入力画像はプロンプトに絶対パス／生成は 127.0.0.1 のみ

- [show_json_is_live_config.md](show_json_is_live_config.md) - show.json は git 管理外の現場設定。卓の検証で書き換えると復元できない（検証サーバもポートを分けただけでは隔離にならない）

- [sim_device_divergence.md](sim_device_divergence.md) - 卓のシミュレータが実機と食い違った3件（2026-07-27）。「卓で沈黙／卓だけ再生」を疑う時の最初の3点＋node で show.json を直接食わせる再現手順

- [cg_compositing.md](cg_compositing.md) - CG合成を触る前に：像空間の3点／卓とUnityで数値を突き合わせる箇所2つ／画角は一度だけ測って固定（27cm→2cm）／ステンシルとRT depth24

- [cg_actor_and_layer_traps.md](cg_actor_and_layer_traps.md) - CG人形を触る前に：レイヤ外部編集は消える(add_layerで足す)／EditModeスキニング固着／Remyは Generic・3.72m／Setupメニューのモーダルで Editor が止まる

- [logic_audit_2026_07_23.md](logic_audit_2026_07_23.md) - 2026-07-23 監査 11 件は全修正済み（EditMode 364/364・JVM 31/31）。実機確認チェックリストと P2 テスト候補はここ
- [controller_input_final.md](controller_input_final.md) - コントローラは右手4入力が最終形（機能追加禁止・2026-07-23 ユーザー宣言）。ToggleActiveCameraCue は未配線デッドコード
- [hud_font_and_preview.md](hud_font_and_preview.md) - HMD内文言を変えたらフォント再生成必須（静的ベイク・忘れると実機豆腐）／見た目確認は Play 禁止・HudPreviewScreenshot（batchmode可）

- [ivrc_video_pages_naming.md](ivrc_video_pages_naming.md) - IVRC動画 pages/ の透過PNGはファイル名固定（編集ソフト参照中・リネーム禁止／追加は既存をずらさない名で）
- [project_overview.md](project_overview.md) - スタック・構成・目的（fixed-cam-vr）
- [harness_design.md](harness_design.md) - CLAUDE.md / .claude/ の役割分担
- [feedback_response_style.md](feedback_response_style.md) - 端的・論理的・最低限の応答
- [user_name_shubie.md](user_name_shubie.md) - このClaude Codeの名前はシュビー
- [sdk_decision.md](sdk_decision.md) - Meta XR All-in-One + XRI 採用の経緯と却下候補
- [mcp_unity_setup.md](mcp_unity_setup.md) - Unity MCP 接続手順（user-scope登録 / `claude mcp add UnityMCP --offline --from mcpforunityserver` / 命名は大文字 UnityMCP / 再起動必須）
- [unity_pitfalls.md](unity_pitfalls.md) - Quad向き / OVRCameraRig Gameビュー / Scene YAML直編集 / Texture初期化 / UnityMCP execute_codeのWindows長さ制限 / manage_componentsのComponentID要件 / Texture2D.width=aspect真値 / DroidCam単一クライアント / OVRCustomHandPrefabのCustomBones全null（手ポーズ駆動は名前マッピング）/ SkinnedMeshのper-cameraボーン切替は原理的に無効（nearclip/レイヤーで解く）
- [camera_fleet.md](camera_fleet.md) - 配信スマホ実機 3 台構成（iPhone 13 Pro=IP Camera Lite + Pixel 7a/7 Pro=streamer、スロット割当・超広角可・IPは揮発）
- [camera_source_alternatives.md](camera_source_alternatives.md) - 配信カメラをスマホ以外に替える検討の全体像（判定=MJPEG直pull／RTSPはQuestネイティブ受信可=Vizario/vlc-unity／技適／ATOM Cam2・ESP32-CAM・C120却下）
- [camera_c120_go2rtc_plan.md](camera_c120_go2rtc_plan.md) - 【C120は2026-07-13却下】go2rtc中継(RTSP→MJPEG)ならUnity改修ゼロの技術検証（RTSPカメラ全般に適用可）
- [iphone_camera_streamer_plan.md](iphone_camera_streamer_plan.md) - iPhone配信カメラ方針：デモはIP Cam Lite継続（ウォーターマーク許容）、自作するならPWA+WSリレー設計（Quest無改造）。Unity iOSはMac必須で却下
- [droidcam_endpoint.md](droidcam_endpoint.md) - 【フォールバック専用】DroidCam IP/ポート（標準は fixed-cam-streamer）
- [verification_workflow.md](verification_workflow.md) - 検証は build/install せず Link+Play+MCP read_console（ユーザー確定方針）。build ループの罠と例外ケース
- [fixed_cam_review_backlog.md](fixed_cam_review_backlog.md) - 2026-06-10 fixed-cam 本体レビュー：修正済み/誤検知/残バックログ/「ルール追加時は既存コードもスイープ」
- [web_compositor.md](web_compositor.md) - tools/web-compositor（ブラウザ合成検証ツール）の場所/起動/パイプライン/キャプチャ録画/プロンプト管理/サーバAPI/AI動画生成知見
- [table_duo_test_coverage.md](table_duo_test_coverage.md) - TableDuo 純ロジック 12 クラス抽出 + EditMode テスト 28 ファイル（2026-07-23）。該当ロジック変更はテストを先に・NUnit/float 境界/static リセットの罠
- [table_duo_study_status.md](table_duo_study_status.md) - TableDuo 手アバター調査アプリ：L2 実機2台で動作確認済み・運用 runbook・既知の罠（ビルド前クリーン化 等）
- [quest_adb_auth.md](quest_adb_auth.md) - Quest が adb unauthorized で許可ダイアログ出ない時の切り分け（中古機=別アカ=初期化が真因の実例）
- [parallel_projects_isolation.md](parallel_projects_isolation.md) - 廻リ視/TableDuo 同居の干渉防止（共有資源・ビルド逐次・コード分離・並列化可否）→ rules/parallel-projects.md
- [quest_build_and_camera_ip.md](quest_build_and_camera_ip.md) - 実機体験2大ハマり：ビルドメニュー「即success=未実行/Timeout=実行中」＋ Phone*.asset host の DHCP ズレ(errno113=Web見えるがQuest黒)
- [table_duo_l0_desktop_test.md](table_duo_l0_desktop_test.md) - TableDuo を実機ゼロ・MCP ゼロで検証（L0 Standalone build を CLI で host/client/観戦 3 プロセス起動・観戦 PNG 自動保存・batchmode の罠）
- [table_duo_hand_variants.md](table_duo_hand_variants.md) - 手の見た目3バリアント（Default/Realistic=Male/Robot）切替：tdv_hand フラグ+ホスト FacilitatorPanel 強制（左Yトグルは2026-07-18撤去）、別リグへのバインド差分リターゲット、実機で指の曲がり軸/向き/大きさ要確認
- [table_duo_wrist_anchor_basis.md](table_duo_wrist_anchor_basis.md) - OVR 手アンカーの実基準は「指≈(-0.27,-0.57,0.78)・グリップ様傾き」（±X 仮定は誤り）。Remy 手首ズレ真因＋prefab authored 姿勢を校正基準にしない
- [table_duo_layout_tuning.md](table_duo_layout_tuning.md) - 卓/椅子/席/駒の初期配置をビルド不要ループで微調整（Setup 再生成→Remy 着座プレビュー→PNG 確認・数秒/周）
- [table_duo_tabletop_prop_authoring.md](table_duo_tabletop_prop_authoring.md) - TableDuo 卓上に新ボードゲーム/プロップを追加する再利用レシピ（PlaceModelRealScale の grabbable/physics/scale/ccd/faceDown・コンポーネント一式・TableProps 手非衝突・SetSurfaceClamp・DiceRoller 静止面読み・BoardReset 全Grabbable自動・GLB前提・冪等Setup・罠）
- [table_duo_pc_host_and_wiretap.md](table_duo_pc_host_and_wiretap.md) - PCホスト+Quest2台client運用フロー（tableduo-pc-host.ps1・起動の罠=スリープ/Linkダイアログ/ゴーストポート/**再接続ラチェット=「接続上限超過」連発は3プロセス全再起動**）/ 映像記録（俯瞰+FPV 2本AVI・rec_toggle・FPV自頭消し=nearclip 0.15・縦反転は実測確定済み）/ WireTap記録(F9・左下デバッグパネル)+診断タグ4種 / 手アバター3大バグ根治記録 / 実機確認チェックリスト
