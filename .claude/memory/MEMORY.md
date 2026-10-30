# Project Memory Index

技術の罠・実装の経緯の索引。**1 行 1 フック**（詳しくは本文）。世界観・演出の判定は `.claude/canon/`（混ぜない — `rules/canon-boundary.md`）。
足すときは該当する領域の節へ 1 行。段落を書かない（`tools/harness-check.py` が字数と対応を見る）。

## いま開いている指摘
- [scenario_audit_20261022.md](scenario_audit_20261022.md) — 10-22 シナリオ監査の未修正 17 件。最重要は 4-A で締めの線が消え、③a より先に人形 End が成立しうること
- [fixed_cam_review_backlog.md](fixed_cam_review_backlog.md) — 06-10 レビューの残り。rules に罠を足したら既存コードもスイープする
- [logic_audit_2026_07_23.md](logic_audit_2026_07_23.md) — 07-23 監査 11 件は修正済み。実機確認のチェックリストだけ残る

## 体験の骨格・演出の連続
- [show_run_skeleton.md](show_run_skeleton.md) — ゲートは CueScheduler 1 点／終了は次フレーム／導入で録画を消さない／終わり方の出口は 4 つ
- [take_continuity.md](take_continuity.md) — 持ち越しは因果条件で絞る／chainNext には安全網／遷移は割り込む側が持つ
- [backtrack_and_replay.md](backtrack_and_replay.md) — 引き返し。周は 2 つ／前進は直前のカメラも見る／once は決着した回数／録画は上書きしない
- [approach_and_veil_hold.md](approach_and_veil_hold.md) — 接近の再設計。startCovered で画面差し替えが消える／保持中の Hide で人形が消える
- [closing_line_halt.md](closing_line_halt.md) — 4-A の③a は締めの線を踏んだ瞬間（0233）。入場 3 秒は ending_result
- [ending_result.md](ending_result.md) — 最後の A の警告・帰還不能・解除・人形 End の条件と報告数の集計
- [ending_roll.md](ending_roll.md) — 帰還 End / 人形 End のエンドロール冒頭。ランごとの合成済み写真
- [ending_farewell_doll.md](ending_farewell_doll.md) — 帰還の別れの人形（0291）。CG 層の器を別れのモードで使う・写真は見つめる段
- [romi_cart_esp32.md](romi_cart_esp32.md) — ⚠ 10-30 から不使用。人形の台車（ESP32・Joy-Con 2）の記録
- [visitor_sound_reset.md](visitor_sound_reset.md) — 2 人目以降だけ壊れる型。ResetRun が本番から呼ばれていなかった
- [unexpected_event_recovery.md](unexpected_event_recovery.md) — 休止・固まり・通信の揺れからの復帰。休止を数えない ShowClock／強制リセット
- [experience_fixes_2026_09_24.md](experience_fixes_2026_09_24.md) — 3-A/4-A の素材の同梱漏れ・導入の順序・最後のお願いまで報告をロック

## タイトルと導入
- [title_screen.md](title_screen.md) — 壁を隠すのは描画順／題字は焼いた版 1 枚／組めなければ素通しへ倒す
- [intro_fracture_cinematic.md](intro_fracture_cinematic.md) — 現実が割れてスクリーンになる遷移。破片は部屋の面に乗る・描画順 4900〜4903・光の粒
- [presentation_redesign.md](presentation_redesign.md) — 導入とエージェント画面の再構築（0207）。割れた実景はワールド固定
- [sealbox_surface.md](sealbox_surface.md) — 封印の箱の地と熾。磨耗を明るく置き換えるとカビに見える／鏡面項を書かない
- [key_visual.md](key_visual.md) — キービジュアル。照明だけ描き直させ、Sobel の相関で物が動いていないか見る

## エージェント画面（スイ）
- [comms_takeover.md](comms_takeover.md) — 乗っ取りの時刻表（WARNING→遮断→侵食→失敗→消去）と入力の破棄
- [comms_curse.md](comms_curse.md) — 呪いの斑と前線。文字はステンシル bit 8 で切る／③a・③b は弱って戻る姿（0270）
- [comms_face.md](comms_face.md) — スイの顔を焼く罠。明暗が逆なので肌を塗らないと顔が消える
- [comms_clock_boundaries.md](comms_clock_boundaries.md) — 時刻境界の float 比較。境界ぴったりと低 FPS を別に試す
- [comms_performance_20261018.md](comms_performance_20261018.md) — 10-18 警告表示中の fps 低下を、見た目を変えずに 90fps へ戻した測定と削減
- [unauthorized_access_effect.md](unauthorized_access_effect.md) — 独立エラー演出の部品。描画順と映像面からの距離。現行は comms_takeover
- [show_language.md](show_language.md) — 日本語 / English / Français。訳す面の線・訳文の置き場はフォントのベイクが決める

## 画面の加工・合成・目
- [cg_compositing.md](cg_compositing.md) — CG 合成。像空間の 3 点・画角は 1 度測って固定・なじませは数値と絵を並べる
- [cg_actor_and_layer_traps.md](cg_actor_and_layer_traps.md) — CG 人形の罠 4 つ（レイヤ外部編集の消失・EditMode スキニング固着・Remy は Generic）
- [swap_veil_mask.md](swap_veil_mask.md) — 黒い覆いは背景差分か CG の 2 系統。差分は linear・黒い服は 29% しか覆えない
- [screen_decay.md](screen_decay.md) — 位置と人形状態に連動する画面の劣化。人形の頭の光だまり `_ActorLight`（10-22）
- [crt_reference_screen.md](crt_reference_screen.md) — 参照画像から直接素材化するCRT。論理16:9と物理筐体の分離、盲検比較と色収差の黒帯混入
- [crt_reference_rule_layout.md](crt_reference_rule_layout.md) — 材質の詳細はreferenceへ。screen-postの12,000bytes上限と読む導線
- [warm_palette.md](warm_palette.md) — 色は 7 箇所に散っている。暖色へ寄せるとき直す所と直さない所
- [glitch_and_latency.md](glitch_and_latency.md) — _Glitch と _SignalLost は別系統／post は 4 箇所同時／絶対 E2E は未測定
- [camera_feel_flicker.md](camera_feel_flicker.md) — 「ちかちかする」は自前の色ノイズ。明るさと色を別々に測る
- [dark_eyes_slices.md](dark_eyes_slices.md) — 闇の目は Codex に描かせた壊れた画像の版。手続きの帯は「整然・チープ」で不採用
- [eye_jack.md](eye_jack.md) — 目の視界ジャック（当日写真）。全開で発火は多数派に届かない／正規化を端末でしない
- [composite_mask_quality.md](composite_mask_quality.md) — シミの矩形切れと二重マスクを原動画から直す。`composite-mask` スキル
- [material_flow.md](material_flow.md) — 素材フロー。マスクは枠空間 16:9／採用は show.json から導出
- [codex_image_pipeline.md](codex_image_pipeline.md) — **生成素材を作る前に読む**。判断は実機の経路を通した画で・歩留まりは枚数で解く
- [doll_reference_kit.md](doll_reference_kit.md) — 市松人形の資料（tools/doll-ref/）。写真は暗く緑に転ぶ

## 音
- [sound_pipeline.md](sound_pipeline.md) — 音を作る道具と耳を使わない判定。計器が 4 回嘘をついた・3D 化で音量が下がる

## 準備・入力・来場者
- [staff_setup.md](staff_setup.md) — タブレット中心の準備・交代。Quest の並行位置合わせ・終幕中のリセット保護
- [hmd_onboarding.md](hmd_onboarding.md) — 被った後：左コントローラ確認 → X/Y の報告練習 → 短押しでタイトル
- [onboarding_audit_20261009.md](onboarding_audit_20261009.md) — 起動〜引き渡しの画面改善。位置合わせのやり直しは A を離してから採取
- [controller_input_final.md](controller_input_final.md) — 体験者は左 X / Y だけ。スタッフは右 A / B / トリガー。左グリップは撮影
- [staff_status_view.md](staff_status_view.md) — 右 B のステータスは押している間だけ。黒の上に描く 3 点
- [visitor_tablet.md](visitor_tablet.md) — 博士タブレットは独立 APK。α／β を選び Quest の :8090 へ設定だけ送る
- [redmi_tablet_install.md](redmi_tablet_install.md) — Redmi の USB インストール確認は時間で自動拒否する
- [tablet_apk_zip_names.md](tablet_apk_zip_names.md) — Windows の aapt2 は資産名に `\` を残す。raw の名前で検査する
- [tablet_webview_media_range.md](tablet_webview_media_range.md) — WebView への資産ストリームは先頭から返す。Range を二重に進めない
- [flow_walk_harness.md](flow_walk_harness.md) — 起動〜回収〜リセットを押す順に通しで撮る `tools/visitor-ui/flow-walk.cjs`
- [l_wall_geometry.md](l_wall_geometry.md) — ⚠ L 字の壁は 2 辺とも 0.955m。show.json の `room.walls` は信じない
- [hmd_text_style.md](hmd_text_style.md) — HMD の文字の正は `HmdTextStyle`。3D TMP の 0.1 倍・`menu text-audit`
- [hud_font_and_preview.md](hud_font_and_preview.md) — HMD 内の文言を変えたらフォント再生成。見た目は HudPreviewScreenshot

## 検証・計器
- [onsite_experience_test.md](onsite_experience_test.md) — 実機を丸ごと検証する 4 点（[XP]・自動走行・判定・機の選択）と解析が嘘をつく経路
- [virtual_walk.md](virtual_walk.md) — PC の仮想体験 `menu virtualwalk`。前後比較は `virtualwalk-video.py --compare`
- [sim_device_divergence.md](sim_device_divergence.md) — 卓のシミュレータと実機の食い違い 5 件。最初に疑う 3 点
- [experience_shots.md](experience_shots.md) — 左グリップで体験中の画を 4 種類保存。**展示本番の機は OFF**
- [experience_replay.md](experience_replay.md) — 紹介動画用。Quest で入力を記録し PC で本番を流し直して広角 mp4（`unity.ps1 replay`）。時計は実時間
- [verification_workflow.md](verification_workflow.md) — 旧来の Link+Play+MCP。いまの正は CLI と `quest-record.py --walk`

## 機材・ビルド・ネットワーク
- [quest_fleet_two_devices.md](quest_fleet_two_devices.md) — **α = `2G0YC1ZF890864` / β = `2G0YC1ZF7S06BW`**。機ごとに必ず食い違う 4 つ
- [quest_build_and_camera_ip.md](quest_build_and_camera_ip.md) — 即 success は未実行／Phone*.asset の host の DHCP ずれ
- [quest_adb_auth.md](quest_adb_auth.md) — adb unauthorized で許可が出ない。中古機は別アカウント＝初期化
- [show_json_is_live_config.md](show_json_is_live_config.md) — show.json は git 管理外の現場設定。サーバはメモリを配る
- [no_internet_autojoin_kill.md](no_internet_autojoin_kill.md) — ⚠ 上流の無い展示網を Android が恒久無効化する。展示の朝に全機が踏む
- [onsite_day_ops.md](onsite_day_ops.md) — 会期中の運用の道具（onsite.py・当日パネル・自動起動）。古い heartbeat で判定しない
- [camera_fleet.md](camera_fleet.md) — 配信スマホ Pixel 7a ×3。IP は揮発／傾きで「カメラが動いたか」が分かる
- [streamer_operator_ui.md](streamer_operator_ui.md) — 配信スマホ側の面（補助線・鏡合わせ・撮影パネル・卓の自動発見）
- [approach_shoot_console.md](approach_shoot_console.md) — 当日の素材撮り。停滞は `ev=clip` にしか出ない
- [camera_source_alternatives.md](camera_source_alternatives.md) — スマホ以外の配信カメラの検討（MJPEG 直 pull／RTSP）
- [camera_c120_go2rtc_plan.md](camera_c120_go2rtc_plan.md) — 却下済み。go2rtc 中継なら Unity 改修ゼロ
- [iphone_camera_streamer_plan.md](iphone_camera_streamer_plan.md) — iPhone はデモなら IP Cam Lite。自作は PWA＋WS
- [droidcam_endpoint.md](droidcam_endpoint.md) — フォールバック専用の DroidCam
- [web_compositor.md](web_compositor.md) — `tools/web-compositor`（卓・合成検証・プロンプト管理・サーバ API）

## Unity・SDK
- [unity_pitfalls.md](unity_pitfalls.md) — Unity / Meta XR の地雷集（Quad 向き・Texture 初期化・SkinnedMesh のカメラ別切替は無効 ほか）
- [sdk_decision.md](sdk_decision.md) — Meta XR All-in-One + XRI 採用の経緯
- [mcp_unity_setup.md](mcp_unity_setup.md) — Unity MCP の登録手順（退避路。この PC は未登録）

## 対外資料・その他
- [paper_dcexpo_env.md](paper_dcexpo_env.md) — DCEXPO 向け資料 `paper/dcexpo`。HTML→Edge→PDF・図は実物へ差し替え
- [website_plates_and_fonts.md](website_plates_and_fonts.md) — 公式サイト。文言を変えたら `npm run fonts`・Vercel は `--scope`
- [ivrc_video_pages_naming.md](ivrc_video_pages_naming.md) — IVRC 動画の pages/ の透過 PNG はファイル名固定
- [proposal_single_source.md](proposal_single_source.md) — 企画書は読まない（08-08）。骨格は実装と機械が守る
- [harness_design.md](harness_design.md) — CLAUDE.md / .claude/ の役割分担と根拠

手のアプリ（TableDuo / MyCobotHand）の memory は 2026-09-28 に table-duo-vr へ移した。
