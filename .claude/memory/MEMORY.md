# Project Memory Index

> ここは**技術の罠・実装の経緯**の索引。
> **世界観・演出の判定は `.claude/canon/` が正本**（`LEDGER` = ユーザーが言ったこと /
> `OPEN` = 未確定とシュビーの案 / `ROUNDS` = 周回の賭けと差）。混ぜない — 規律は `rules/canon-boundary.md`。
> 作りこみの思想は `.claude/reference/why.md`。

- [onsite_experience_test.md](onsite_experience_test.md) - 体験を実機で丸ごと検証する 4 点セット（[XP] テレメトリ / HMD 不要の自動走行 / 判定スクリプト / 機の選択）＋ logcat 収集の真因（他プロセスが 76%）と「解析が嘘をつく経路」
- [quest_fleet_two_devices.md](quest_fleet_two_devices.md) - Quest 2 台を交互に使う道具と、機ごとに違って必ず食い違う 4 つ（キャッシュ / APK / 帯域 / 位置合わせ）。熱で選ぶ理由・adb pull が使えない話

- [proposal_single_source.md](proposal_single_source.md) - **企画書は docs/proposal/ も docs/archive/ も読まない**（2026-08-08 作りこみの段へ移行）。骨格は実装と機械が守る／旧版にしか無い要求 4 つは実装しない

- [sealbox_surface.md](sealbox_surface.md) - 封印の箱の地（焼いた版 4ch）と熾：磨耗を明るい色で置き換えると白いカビに見える／オクターブノイズは分位で切る／版のタイルは周長の約数へ（小さすぎると反復が見える）／hearth² は面積では埋まらない／テカらせない＝鏡面項を書かない／依頼を数値で確かめる 1 行
- [warm_palette.md](warm_palette.md) - 色は 7 箇所に散っている（post / 箱 / 題字 / 輪郭 / 構造線 / 人形 / 焼き込み）。暖色へ寄せたとき直した所と、直さなかった所の理由／彩度を上げないと色温度は 65% 捨てられる／赤は R を大きく取れば逆に明るくなる
- [title_screen.md](title_screen.md) - タイトル画面「廻リ視」：壁を隠しているのは重みではなく描画順／題字は焼いた版 1 枚（4ch が別の意味を持つマスク・押し出さず 3 層を離す・かすれは割合から閾値を逆算）／Normal の A は閉じるだけ／組めなければ素通しへ倒す
- [take_continuity.md](take_continuity.md) - 演出の待ち・連続・継ぎ目：持ち越しは因果条件で絞る／chainNext には安全網／遷移は割り込む側が所有する／相乗り分岐にテストが無い
- [backtrack_and_replay.md](backtrack_and_replay.md) - 体験者が引き返したとき：周は 2 つ（進行／区間）／前進は直前のカメラも見る／once は「決着した回数」で未報告の中断は頭から再演／録画は上書きしない
- [show_run_skeleton.md](show_run_skeleton.md) - 体験の骨格（導入→3周→終了）を触る前に：ゲートは CueScheduler 1 点／終了は次フレーム／導入で録画を消さない／凍結を増やさない／**終わり方の出口は 4 つ（合図・周回・時間切れ・卓）**
- [glitch_and_latency.md](glitch_and_latency.md) - 乱れ演出と遅延計測：_Glitch と _SignalLost は別系統／post は 4 箇所同時に直す／絶対 E2E は測っていない（配信側に /clock が要る）
- [screen_decay.md](screen_decay.md) - 周回で進む解像度の劣化：post ではなく別系統 uniform（対応表は C# にしかない）／段差を作らない 3 つの仕掛け／進むのは Run 相だけで終了では保持／3 周目の録画も同じだけ粗くなる（2026-08-06 の設計判断をユーザーが覆した）

- [comms_face.md](comms_face.md) - AIエージェントの顔（連絡の面の左の角丸）を焼く罠。**明暗が逆なので「肌を塗らない」と顔が消える**（塗り分けを 6 通り試して全滅）／白銀の髪は明るさで分けられず手掛かりは赤みだけ／実機 110 画素で残す線の太さ・目だけ別の閾値・顎の影を目と誤認する話／**人形は正本の写真からは焼けない**（背景も髪も暗い）・写実には線が無いので生え際を自分で引く・侵食は溶暗ではなく斑
- [hmd_text_style.md](hmd_text_style.md) - HMD 内テキストの正（`HmdTextStyle` 1 か所）と、**2 回続けて実機で読めなくした 0.1 倍の罠**（3D の TMP は透視カメラで fontSize に 0.1 を掛ける）／見かけ角を測る道具 `menu text-audit`（textBounds も lineInfo.width も枠の幅を返すので使えない）／語の規約が 2 つの asmdef にまたがる話／**フォントの収集はコメントの日本語も焼く**（収集元 .cs のコメントを 1 行直すだけでビルドのガードが落ちる・文字数が減っても慌てない）

- [sound_pipeline.md](sound_pipeline.md) - 音を作る道具と、耳を使わずに判定する方法。**計器の方が 4 回嘘をついた**（true peak / 直流の発散 / 書き出しでの再正規化 / 継ぎ目の物差し）／内蔵スピーカーは 200Hz 以下を返さない・遅延で広げるとモノで消える／実機ログとテストがそれぞれ実装の欠陥を 1 件ずつ捕まえた

- [material_flow.md](material_flow.md) - 素材フローを触る前に：マスクは枠空間 16:9（実機は contain-fit を通さない）／**マスクの決め方は未着手＝工房の差分は見た秒数で変わる**／採用は show.json から導出／ラプラシアンは卓だけ／プロンプトの棚は 1 つでスロット無し
- [codex_image_pipeline.md](codex_image_pipeline.md) - **生成素材を作る前に読む**。層は 異変（場所に依らない）/ 場所（当日 1 ファイル）/ 伝送（機械が計算）／**判断は実機の経路を通した画で**（素材の中で見える欠点の半分は届かない・帰りの A は無彩）／**継ぎ目は届いた画でしか出ない**／プロンプトで動くのは視線となじませ規則の 2 つだけ、歩留まりは枚数で解く／計器のバグ 5 件と掃引の使い方。道具は [tools/gen-plate/](../../tools/gen-plate/README.md)、記録は [runs.md](../../tools/gen-plate/runs.md)

- [show_json_is_live_config.md](show_json_is_live_config.md) - show.json は git 管理外の現場設定。卓の検証で書き換えると復元できない（検証サーバもポートを分けただけでは隔離にならない）

- [sim_device_divergence.md](sim_device_divergence.md) - 卓のシミュレータが実機と食い違った5件。「卓で沈黙／卓だけ再生」を疑う最初の3点＋node で show.json を直接食わせる再現手順／**演出の起点はゾーン確定で画面切替とは別ゲート**／再生中は設定を読み直さない問題

- [cg_compositing.md](cg_compositing.md) - CG合成を触る前に：像空間の3点／卓とUnityで数値を突き合わせる箇所2つ／画角は一度だけ測って固定（27cm→2cm）／ステンシルとRT depth24／較正UIは判断がcalib-session.js（テストを先に直す）・測っていない幾何を実測扱いしない・画角はlenses[]が正／**なじませは数値と絵を並べて見る（彩度はpostでは埋まらない・陰影をalbedoに比例させない・合成プレビューが実機より綺麗だった）**

- [cg_actor_and_layer_traps.md](cg_actor_and_layer_traps.md) - CG人形を触る前に：レイヤ外部編集は消える(add_layerで足す)／EditModeスキニング固着／Remyは Generic・3.72m／Setupメニューのモーダルで Editor が止まる
- [doll_reference_kit.md](doll_reference_kit.md) - 実物の市松人形を画像生成 AI に描かせる資料（tools/doll-ref/）。3D 用マスクは資料に使えない／写真は暗く緑に転ぶ／正面の T 字は撮影の都合／探針は目で置く

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
- [camera_fleet.md](camera_fleet.md) - 配信スマホ実機 3 台構成（Pixel 7a ×3 = streamer。iPhone+IP Camera Lite は予備）。IP は揮発／**3 台とも v0.9.0（2026-08-05 導入）で、これが上流の最新**／**端末の傾きと保存した較正を突き合わせると「カメラが動いたか」が 10 秒で分かる**
- [camera_source_alternatives.md](camera_source_alternatives.md) - 配信カメラをスマホ以外に替える検討の全体像（判定=MJPEG直pull／RTSPはQuestネイティブ受信可=Vizario/vlc-unity／技適／ATOM Cam2・ESP32-CAM・C120却下）
- [camera_c120_go2rtc_plan.md](camera_c120_go2rtc_plan.md) - 【C120は2026-07-13却下】go2rtc中継(RTSP→MJPEG)ならUnity改修ゼロの技術検証（RTSPカメラ全般に適用可）
- [iphone_camera_streamer_plan.md](iphone_camera_streamer_plan.md) - iPhone配信カメラ方針：デモはIP Cam Lite継続（ウォーターマーク許容）、自作するならPWA+WSリレー設計（Quest無改造）。Unity iOSはMac必須で却下
- [droidcam_endpoint.md](droidcam_endpoint.md) - 【フォールバック専用】DroidCam IP/ポート（標準は fixed-cam-streamer）
- [verification_workflow.md](verification_workflow.md) - **⚠ 位置づけ訂正済み（2026-08-09）**。いまの正は CLI + `quest-record.py --walk`。Link+Play+MCP は Editor Play でしか出ない挙動を追うときだけ
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
