# Project Memory Index

- [project_overview.md](project_overview.md) - スタック・構成・目的（fixed-cam-vr）
- [harness_design.md](harness_design.md) - CLAUDE.md / .claude/ の役割分担
- [feedback_response_style.md](feedback_response_style.md) - 端的・論理的・最低限の応答
- [user_name_shubie.md](user_name_shubie.md) - このClaude Codeの名前はシュビー
- [sdk_decision.md](sdk_decision.md) - Meta XR All-in-One + XRI 採用の経緯と却下候補
- [mcp_unity_setup.md](mcp_unity_setup.md) - Unity MCP 接続手順（user-scope登録 / `claude mcp add UnityMCP --offline --from mcpforunityserver` / 命名は大文字 UnityMCP / 再起動必須）
- [unity_pitfalls.md](unity_pitfalls.md) - Quad向き / OVRCameraRig Gameビュー / Scene YAML直編集 / Texture初期化 / UnityMCP execute_codeのWindows長さ制限 / manage_componentsのComponentID要件 / Texture2D.width=aspect真値 / DroidCam単一クライアント
- [camera_fleet.md](camera_fleet.md) - 配信スマホ実機 3 台構成（iPhone 13 Pro=IP Camera Lite + Pixel 7a/7 Pro=streamer、スロット割当・超広角可・IPは揮発）
- [droidcam_endpoint.md](droidcam_endpoint.md) - 【フォールバック専用】DroidCam IP/ポート（標準は fixed-cam-streamer）
- [verification_workflow.md](verification_workflow.md) - 検証は build/install せず Link+Play+MCP read_console（ユーザー確定方針）。build ループの罠と例外ケース
- [fixed_cam_review_backlog.md](fixed_cam_review_backlog.md) - 2026-06-10 fixed-cam 本体レビュー：修正済み/誤検知/残バックログ/「ルール追加時は既存コードもスイープ」
- [web_compositor.md](web_compositor.md) - tools/web-compositor（ブラウザ合成検証ツール）の場所/起動/パイプライン/キャプチャ録画/プロンプト管理/サーバAPI/AI動画生成知見
- [table_duo_study_status.md](table_duo_study_status.md) - TableDuo 手アバター調査アプリ：L2 実機2台で動作確認済み・運用 runbook・既知の罠（ビルド前クリーン化 等）
- [quest_adb_auth.md](quest_adb_auth.md) - Quest が adb unauthorized で許可ダイアログ出ない時の切り分け（中古機=別アカ=初期化が真因の実例）
- [parallel_projects_isolation.md](parallel_projects_isolation.md) - 廻リ視/TableDuo 同居の干渉防止（共有資源・ビルド逐次・コード分離・並列化可否）→ rules/parallel-projects.md
