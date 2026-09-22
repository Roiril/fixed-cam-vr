# Project Memory Index

- [onsite_day_ops.md](onsite_day_ops.md) - 2026-09-22。演出素材・同梱・APK・Quest の内容を照合。Player は事前同梱のみ。博士タブレットの応答と設定反映は [visitor_tablet.md](visitor_tablet.md)。

- [ending_result.md](ending_result.md) - 2026-09-21。最後のAは凍結線OR入場3秒で警告。催促の全文表示後に未報告退出OR15秒で帰還不能。結果は報告回数/異常総数8件。Nocturnal Waters。専用フォントと3言語の実描画。

- [composite_mask_quality.md](composite_mask_quality.md) - 2026-09-19。シミの矩形切れと二重マスクを原動画から修正。生成されたパイプも一緒に合成し、保護用の穴を勝手に開けない。`composite-mask` スキルと保存レシピ。人物切り抜きは採用しない。実機未確認。

- [dark_eyes_slices.md](dark_eyes_slices.md) - 2026-09-19（0237 / 0238・R055 / R056）。闇の目の中身を **Codex に描かせた「壊れた画像データの目」の版**へ（`tools/make-eye-glitch.py` → `Resources/Eyes/EyeGlitch.png`）。**手続きの帯は「整然としすぎ・チープ」で不採用**（色の割合が参考に並んでも目には読めない）／シェーダは瞼の包絡で切ってずらして引くだけ／mip の段はずらす前の uv の微分で／`eyes=` の 8 つ目 ＝ 版を掴めたか／**プレビューは `-Set scale=2`**／`RiftExtend` ⇔ `RIFT_EXTEND`・`--ratio` ⇔ `TEX_RATIO`・`DefaultColor`/`DefaultGain` ⇔ シーンの値 は対。

- [unauthorized_access_effect.md](unauthorized_access_effect.md) - 2026-09-19の独立エラー演出。描画順と映像面からの距離。Editorでの停止と破棄。実描画の不在検出。通常の7秒デモは維持。2026-09-21に本編へ接続した乗っ取り専用表示の最新仕様は[comms_takeover.md](comms_takeover.md)。

- [website_plates_and_fonts.md](website_plates_and_fonts.md) - 2026-09-19。公式サイトの記録・クレジットを荷札と銘板へ。**文言を変えたら `npm run fonts`**（和文書体は画面の字だけのサブセット・check が収録漏れを落とす）／Vercel は `--scope roilils-projects` が無いと Not authorized・プレビューはログインが要る／**headless Chrome は 512px より狭く撮れない**（スマホ幅はアプリ内ブラウザで）・ペインが隠れていると IntersectionObserver が発火しない。

- [closing_line_halt.md](closing_line_halt.md) - 2026-09-19の線判定の記録。2026-09-21に[ending_result.md](ending_result.md)で入場3秒の条件も有効にした。

- [comms_curse.md](comms_curse.md) - 2026-09-18（0229）。連絡の面の壊れを「呪われた双子の画面が斑で重なる」形へ。斑の場は HLSL と C# の写し 1 対（顔・地・走り書き・文字の切断が共有）／**文字は地が書くステンシル bit 8 を TMP の材質が読んで切る**（1 パスでは書けない・fontMaterials 全部・ステンシルが無い形式では CPU の alpha 0 が保険）／面積は侵食度に等号で結ばない（0.25→22%・0.75→55%・1→全面）／「出た初めは通常 → 1 秒で重なる」は CommsPanelLogic が持つ（**0.25 だけ**）／**0.75 以降は斑ではなく前線**（0230・→ comms_takeover.md）。

- [intro_fracture_cinematic.md](intro_fracture_cinematic.md) - 2026-09-15（0221〜0226・**採用済み**・実機 3 走行で割れる遷移を確認）。**2026-09-19（0235・R053）: 面を「光を通す結晶」へ・破断と同時に 512＋6 粒の光を飛ばし、代表 6 粒の反射で片が順に瞬く。`_Crystal/_SparkLit/_Spark`=0 で旧描画と画素一致（校正済み）／きらめきの格子は写真 uv ではなく辺までの距離（両眼一致）／明るさの持ち上げは detail で掛ける／std/mean は「くすみ」の指標にならない。** 現実が割れる遷移を映画の速度変化へ書き直した。集結は中心の磁石（漂いが止まり、全片が同時に引かれ、重い片ほど遅れて一気に加速、減速せずに嵌まる）。**着地した破片からその場所の映像**（ステンシルで隙間だけ黒・描画順 4900/4901/4902/4903）。ScreenOn は閉じる瞬間。段 4 は 5.0 秒（5 か所＋卓の show.json）。時計の表はシェーダ 1 つで、音と IntroLogic はその写し。形は起点からの網、質感はガラスの縁の光。短い窓の LUFS は −70 を返す／速い粒は半分の長さ／平均乱数は 0.1 を下回らない。

- [staff_status_view.md](staff_status_view.md) - 2026-09-14。右 B のステータスは押しているあいだだけ出す。トグルをやめた理由／黒の上に描く 3 点（3D TMP・Overlay・queue 5000。sortingOrder では越えられない）／題字を譲らせない／faceButtonHeld から B を外した／電池 API は使わない／`ev=status` と `hudSec` と heartbeat `statusHud` の読み方。

- [hmd_onboarding.md](hmd_onboarding.md) - 2026-09-14。HMD 内は左コントローラーの接続確認 → X/Y の報告練習 → 本人の短押しでタイトル開始 → 歩行誘導。注意・言語選択はタブレット。入力持ち越しと切断時の復帰条件。

- [comms_clock_boundaries.md](comms_clock_boundaries.md) - CommsTakeoverの時刻境界で再発したfloat比較の注意。境界ぴったりと低FPSを別に検証する。

- [comms_takeover.md](comms_takeover.md) - 2026-09-21。追いつきの自然完了から不正アクセスへ。大きなWARNINGと横並びの接続情報。3秒に遮断ラベルと空バー。4.17〜5.67秒でバーと人形への侵食を同時に左から右へ進める。5.92秒に赤い失敗。7.02秒に面とエラーを同時消去。長文時も開始時計を共有する。人形だけ1秒残す旧仕様は廃止。開始前の返信を破棄し、終了後はボタンを離すまで再入力しない。実描画と実機の証拠は本文の最新節。

- [presentation_redesign.md](presentation_redesign.md) - 2026-09-13 の導入とエージェント画面の再構築。連続開口と固定位置の通信面。実装案の採否は未判定。比較画像と検証手順。**2026-09-14: 割れた実景は割れ始めの姿勢にワールド固定**（代替経路だけ頭についてきた）／落ちる条件は `shatCam=`／権限は起動時／頭を振る探針 `anchor-proof.json`。

> ここは**技術の罠・実装の経緯**の索引。
> **世界観・演出の判定は `.claude/canon/` が正本**（`LEDGER` = ユーザーが言ったこと /
> `OPEN` = 未確定とシュビーの案 / `ROUNDS` = 周回の賭けと差）。混ぜない — 規律は `rules/canon-boundary.md`。
> 作りこみの思想は `.claude/reference/why.md`。

- [visitor_tablet.md](visitor_tablet.md) - タブレットから Quest 自身の :8090 に直接接続する。PC は不要。注意画面で設定し、実値と反映番号で確認する。2026-09-12 に UI を全面再設計。博士の構図と黒・生成り・朱赤を維持。言語3択と軽減2択を直接選ぶ。注意は狭い画面でも省略しない。通信の期限と再送を用意。机上検証は `tools/visitor-portal-stub.py`、手順は `tools/visitor-ui/README.md`。HTML は APK に入るので実機の反映には再ビルドが必要。
- [l_wall_geometry.md](l_wall_geometry.md) - ⚠⚠ **L 字の壁は 2 辺とも等長 0.955m**（ユーザーが 2 回言った・0164/0183）。show.json には壁の表現が 2 つあり、`room.walls` は 8/5〜9/11 の間 0.59/1.22 の非対称で、機械で読んだセッションが全員そこから間違えた。`layout.wall`（位置合わせの既定点）は 1.0/1.0 のまま
- [key_visual.md](key_visual.md) - **キービジュアルを触る前に**。地は Codex に照明だけ描き直させる（暗さはマスクではなく光の不在）／「照明だけ」は Sobel の相関で見る（0.5 を切ったら物が動いている）／着物の赤はプロンプトでは沈まないので組むときに落とす／Codex は 1672×941 で返す／文字の書体と位置
- [no_internet_autojoin_kill.md](no_internet_autojoin_kill.md) - ⚠⚠ **上流の無い展示網を Android が恒久無効化する**（`NO_INTERNET_PERMANENT`）。繋がっている間は 1 ビットも出ず、**次に電源を入れた時だけ戻らない ＝ 展示の朝に全機が同時に踏む**。Quest も配信スマホも。解けるのは端末の設定で手で選び直すことだけ（adb 不可）／予防は接続チェックを切る（これからにしか効かない）／同時に確かめた「問題なかったこと」4 つ
- [onsite_day_ops.md](onsite_day_ops.md) - **当日（9/6・7）の運用**。PC を触らない 3 層（機械が勝手に / スマホの当日パネル / シュビー）／`onsite.py check` は卓の本番前チェックと**重ならない**（統合しない）／**古い heartbeat で判定させない**・2 台の欠けは 20 秒の窓の最悪値で見る／目の写真の 3 経路／ログオン自動起動は冪等が必須・`.cmd` は ASCII だけ／自動復旧で Wi-Fi には触らない
- [visitor_sound_reset.md](visitor_sound_reset.md) - **2 人目以降だけ壊れていた**：`ShowSoundDirector.ResetRun` が本番から 1 度も呼ばれておらず、笑いの方角が 1 人目のまま固定・前の人の音が次のタイトルへ流れ込んでいた（画にも録画にも出ない）。同じ型の探し方と、まだ確かめていないこと
- [onsite_experience_test.md](onsite_experience_test.md) - 体験を実機で丸ごと検証する 4 点セット（[XP] テレメトリ / HMD 不要の自動走行 / 判定スクリプト / 機の選択）＋ logcat 収集の真因（他プロセスが 76%）と「解析が嘘をつく経路」
- [quest_fleet_two_devices.md](quest_fleet_two_devices.md) - **クエストα = `2G0YC1ZF890864`（自動走行を回す機）／ クエストβ = `2G0YC1ZF7S06BW`**。Quest 2 台を交互に使う道具と、機ごとに違って必ず食い違う 4 つ（キャッシュ / APK / 帯域 / 位置合わせ）。熱で選ぶ理由・adb pull が使えない話

- [proposal_single_source.md](proposal_single_source.md) - **企画書は docs/proposal/ も docs/archive/ も読まない**（2026-08-08 作りこみの段へ移行）。骨格は実装と機械が守る／旧版にしか無い要求 4 つは実装しない

- [sealbox_surface.md](sealbox_surface.md) - 封印の箱の地（焼いた版 4ch）と熾：磨耗を明るい色で置き換えると白いカビに見える／オクターブノイズは分位で切る／版のタイルは周長の約数へ（小さすぎると反復が見える）／hearth² は面積では埋まらない／テカらせない＝鏡面項を書かない／依頼を数値で確かめる 1 行
- [warm_palette.md](warm_palette.md) - 色は 7 箇所に散っている（post / 箱 / 題字 / 輪郭 / 構造線 / 人形 / 焼き込み）。暖色へ寄せたとき直した所と、直さなかった所の理由／彩度を上げないと色温度は 65% 捨てられる／赤は R を大きく取れば逆に明るくなる
- [title_screen.md](title_screen.md) - タイトル画面「廻リ視」：壁を隠しているのは重みではなく描画順／題字は焼いた版 1 枚（4ch が別の意味を持つマスク・押し出さず 3 層を離す・かすれは割合から閾値を逆算）／Normal の A は閉じるだけ／組めなければ素通しへ倒す
- [take_continuity.md](take_continuity.md) - 演出の待ち・連続・継ぎ目：持ち越しは因果条件で絞る／chainNext には安全網／遷移は割り込む側が所有する／相乗り分岐にテストが無い
- [streamer_operator_ui.md](streamer_operator_ui.md) - **配信スマホ側の面**（補助線・鏡合わせ・撮影パネル・卓の自動発見・v0.13.0）。役割で置き場を決める芯／鏡の軸は splitX ではない／鏡合わせを需要に数えないと画が固まる／Kotlin のブロックコメントは入れ子／ショット定義が shots.json へ出た（空を緑にしない門）／卓の在り処は既にブロードキャストされていた
- [approach_shoot_console.md](approach_shoot_console.md) - **当日の素材撮り**（撮影コンソール / streamer v0.10.0 の端末内録画 / 動画カットの先読み）。停滞は `ev=clip` にしか出ない（用意 135ms は先読みでも消えない）／8 カットの連続再生は実測で成立・mp4 のままでよい／ショットの定義を cue に足すと消える／採用名を一意にしないと「撮り直したのに変わらない」／配信スマホの更新は USB が要る
- [approach_and_veil_hold.md](approach_and_veil_hold.md) - 接近の再設計（0102）を触る前に：**startCovered で _covered を true にすると画面差し替えが 1 度も起きない**／保持中に Hide を呼ぶと終わりに人形が消える／シェーダの hand は「画面が差し替わった」前提／偽ライブは trim を進める（trimEnd は指定しない）／卓の白名簿と mirrorOf／鏡の軸は splitX ではなく枠の中央
- [backtrack_and_replay.md](backtrack_and_replay.md) - 体験者が引き返したとき：周は 2 つ（進行／区間）／前進は直前のカメラも見る／once は「決着した回数」で未報告の中断は頭から再演／録画は上書きしない
- [show_run_skeleton.md](show_run_skeleton.md) - 体験の骨格（導入→3周→終了）を触る前に：ゲートは CueScheduler 1 点／終了は次フレーム／導入で録画を消さない／凍結を増やさない／**終わり方の出口は 4 つ（合図・周回・時間切れ・卓）**
- [camera_feel_flicker.md](camera_feel_flicker.md) - 「カメラ映像がちかちかする」の切り分け（配信スマホを直に測る / `feel` を卓から切る＝焼き直し不要 / **明るさと色を別々に測る**）。犯人は配信側ではなく自前の色ノイズで、**1-A だけなのは画がいちばん暗いから**
- [glitch_and_latency.md](glitch_and_latency.md) - 乱れ演出と遅延計測：_Glitch と _SignalLost は別系統／post は 4 箇所同時に直す／絶対 E2E は測っていない（配信側に /clock が要る）
- [swap_veil_mask.md](swap_veil_mask.md) - 黒い覆いの形は 2 系統（持続の覆い＝背景差分 100% / 入れ替わりの段＝CG 100%。混ざる区間は無い）／差分は linear なので明るい面は 4% の変化で拾い暗い面は 3.7 倍でも拾わない／実測：プレートが同日なら誤検出 2.8%・設営が変われば 52.6%／**カメラがずれても全体は黒くならない**（輪郭だけ・4° で 19% 飽和・後半ほど強い）／**拾えなかった画素の受け皿が無い**（黒い服は 29% しか覆えない）／2 段 mip が実質 1 段
- [screen_decay.md](screen_decay.md) - 位置と人形状態に連動する画面加工（0242 / 0243）：通常は確定した A/B/C の位置で中程度まで／2-C の人形視点だけ強／3-A 人形化後は強を保持／4-A は 0.5 秒で解除。音の進行度は独立して維持。現行シェーダで比較する。

- [eye_jack.md](eye_jack.md) - 目の視界ジャック（当日写真）を触る前に。**「全開で発火」は多数派に届かない**（実測滞在 7.04 秒に対し半分 3.5 秒 < 全開 4.92 秒 ＝ Hold は 1 フレームしか無い。0084 の待機の視線も同じ理由で走っていない）／覆いの裏で目を閉じさせない（0084 の「閉じたのではなく消えた」に戻る）／正規化を端末でやらない（12MP と EXIF は実機でしか失敗しない）／**動いている卓サーバがメモリに show.json を持っているので、ディスクを直接書くと黙って巻き戻る**
- [comms_face.md](comms_face.md) - AIエージェントの顔（連絡の面の左の角丸）を焼く罠。**明暗が逆なので「肌を塗らない」と顔が消える**（塗り分けを 6 通り試して全滅）／白銀の髪は明るさで分けられず手掛かりは赤みだけ／実機 110 画素で残す線の太さ・目だけ別の閾値・顎の影を目と誤認する話／**人形は正本の写真からは焼けない**（背景も髪も暗い）・写実には線が無いので生え際を自分で引く・侵食は溶暗ではなく斑
- [hmd_text_style.md](hmd_text_style.md) - HMD 内テキストの正（`HmdTextStyle` 1 か所）と、**2 回続けて実機で読めなくした 0.1 倍の罠**（3D の TMP は透視カメラで fontSize に 0.1 を掛ける）／見かけ角を測る道具 `menu text-audit`（textBounds も lineInfo.width も枠の幅を返すので使えない）／語の規約が 2 つの asmdef にまたがる話／**フォントの収集はコメントの日本語も焼く**（収集元 .cs のコメントを 1 行直すだけでビルドのガードが落ちる・文字数が減っても慌てない）
- [show_language.md](show_language.md) - **言語選択（日本語 / English / Français）を触る前に**。訳す面と訳さない面の線（スタッフの面は日本語のまま）／訳文の置き場はフォントの静的ベイクが決める（収集元の 4 ファイルの中に書けば配線を触らなくてよい）／**「非 ASCII ＝ 全角」ではない**（フランス語のアクセントで踏んだ）／打鍵の速さは言語で変えられないので尺が伸びる／門は「面が画に出ているか」で、そのあいだ報告のゲージを進めない

- [sound_pipeline.md](sound_pipeline.md) - 音を作る道具と、耳を使わずに判定する方法。**計器の方が 4 回嘘をついた**（true peak / 直流の発散 / 書き出しでの再正規化 / 継ぎ目の物差し）／内蔵スピーカーは 200Hz 以下を返さない・遅延で広げるとモノで消える／実機ログとテストがそれぞれ実装の欠陥を 1 件ずつ捕まえた／**3D 化でモノへ落とすと音量が下がる**（切替 -2.6dB）・名簿は焼く側と鳴らす側の 2 か所・Meta XR は距離減衰を持たない（Unity の rolloff がそのまま効く）

- [material_flow.md](material_flow.md) - 素材フローを触る前に：マスクは枠空間 16:9（実機は contain-fit を通さない）／**マスクの決め方は未着手＝工房の差分は見た秒数で変わる**／採用は show.json から導出／ラプラシアンは卓だけ／プロンプトの棚は 1 つでスロット無し
- [codex_image_pipeline.md](codex_image_pipeline.md) - **生成素材を作る前に読む**。層は 異変（場所に依らない）/ 場所（当日 1 ファイル）/ 伝送（機械が計算）／**判断は実機の経路を通した画で**（素材の中で見える欠点の半分は届かない・帰りの A は無彩）／**継ぎ目は届いた画でしか出ない**／プロンプトで動くのは視線となじませ規則の 2 つだけ、歩留まりは枚数で解く／計器のバグ 5 件と掃引の使い方。道具は [tools/gen-plate/](../../tools/gen-plate/README.md)、記録は [runs.md](../../tools/gen-plate/runs.md)

- [show_json_is_live_config.md](show_json_is_live_config.md) - show.json は git 管理外の現場設定。卓の検証で書き換えると復元できない（検証サーバもポートを分けただけでは隔離にならない）／**サーバはメモリを配るのでファイル直書きは届かない**／APK の焼き込みは優先順位がいちばん下なので古くても気づけない（2026-09-03 からビルドが毎回焼き込む）

- [sim_device_divergence.md](sim_device_divergence.md) - 卓のシミュレータが実機と食い違った5件。「卓で沈黙／卓だけ再生」を疑う最初の3点＋node で show.json を直接食わせる再現手順／**演出の起点はゾーン確定で画面切替とは別ゲート**／再生中は設定を読み直さない問題

- [cg_compositing.md](cg_compositing.md) - CG合成を触る前に：像空間の3点／卓とUnityで数値を突き合わせる箇所2つ／画角は一度だけ測って固定（27cm→2cm）／ステンシルとRT depth24／較正UIは判断がcalib-session.js（テストを先に直す）・測っていない幾何を実測扱いしない・画角はlenses[]が正／**なじませは数値と絵を並べて見る（彩度はpostでは埋まらない・陰影をalbedoに比例させない・合成プレビューが実機より綺麗だった）**

- [cg_actor_and_layer_traps.md](cg_actor_and_layer_traps.md) - CG人形を触る前に：レイヤ外部編集は消える(add_layerで足す)／EditModeスキニング固着／Remyは Generic・3.72m／Setupメニューのモーダルで Editor が止まる
- [doll_reference_kit.md](doll_reference_kit.md) - 実物の市松人形を画像生成 AI に描かせる資料（tools/doll-ref/）。3D 用マスクは資料に使えない／写真は暗く緑に転ぶ／正面の T 字は撮影の都合／探針は目で置く

- [logic_audit_2026_07_23.md](logic_audit_2026_07_23.md) - 2026-07-23 監査 11 件は全修正済み（EditMode 364/364・JVM 31/31）。実機確認チェックリストと P2 テスト候補はここ
- [controller_input_final.md](controller_input_final.md) - 体験者入力は左 X / Y だけ。左グリップ、左インデックストリガー、左スティック押し込みは読まない。スタッフ操作は右 A / B / トリガー（2026-09-14）。
- [hud_font_and_preview.md](hud_font_and_preview.md) - HMD内文言を変えたらフォント再生成必須（静的ベイク・忘れると実機豆腐）／見た目確認は Play 禁止・HudPreviewScreenshot（batchmode可）

- [ivrc_video_pages_naming.md](ivrc_video_pages_naming.md) - IVRC動画 pages/ の透過PNGはファイル名固定（編集ソフト参照中・リネーム禁止／追加は既存をずらさない名で）
- [harness_design.md](harness_design.md) - CLAUDE.md / .claude/ の役割分担
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
