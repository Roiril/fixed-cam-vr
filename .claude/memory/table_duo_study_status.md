---
name: table_duo_study_status
description: TableDuo 手アバター調査アプリの現状（L2 実機2台で動作確認済み）・運用 runbook・既知の罠
metadata: 
  node_type: memory
  type: project
  originSessionId: 5d891bfb-921e-4220-a39c-18714e9babd1
---

TableDuo＝同居サブプロジェクト「手だけアバターとの対人インタラクション調査」VR（[[project_overview]] とは別アプリ）。2026-06-15 時点で **実機2台（L2）で接続〜役割〜目線〜手メッシュまで動作確認済み**。

詳細は CLAUDE.md「同居サブプロジェクト: TableDuo」/ docs/table-duo/（study-design・study-protocol・consent-template）/ .claude/plans/2026-06-11_table-duo_study.md（実装状況・runbook・罠）。

## 用語: 「人側」と「手側」（全部同じものの別名）
- **人側＝人役＝フルアバター＝`Role.Full`＝Seat0＝`tdv_role full`**: 発話可。相手には頭+胴体+両手で見える
- **手側＝手役＝手だけ＝`Role.Hand`＝Seat1＝`tdv_role hand`**: 発話不可（ジェスチャーのみ）。相手には手だけ（既定 右手・頭マーカー無し）
- **役割(tdv_role) と host/client(tdv_mode) は独立**。既定は host=人/client=手だが入替可。役割は tdv_role が正。
- 詳細表 → docs/table-duo/study-design.md §0

## 要点（他シュビーが最初に知るべきこと）
- **パッケージ `com.roiril.tableduo`**。ビルドは Unity メニュー `Tools/FixedCamVr/Build TableDuo APK`→`Builds/tableduo.apk`（[[mcp_unity_setup]] / quest-build スキル）。旧 `manage_build` 版（`com.UnityTechnologies...urpblank`）は使わない
- 起動は adb intent: `-e tdv_mode host|client -e tdv_role full|hand [-e tdv_ip <hostIP>]`。役割で席・アバター種別・片手モードが決まる（host/client と role は独立）
- 目線: **EyeLevel + 席=目線アンカー（[TableDuo]/Seats/Seat0,1, y=1.15）**。FloorLevel にすると身長で目線高が変わるので不可。`Preview Eye - Seat0/1` メニュー＋SeatEyeGizmo で調整
- リモートの手＝**Meta の白い手メッシュ**（OVRCustomHandPrefab を同期 bone で駆動。ローカル手と同じ見た目）。供給は `RemoteHandMeshProvider`（Systems・Setup が L/R プレハブ＋白マテリアルを配線）、プレハブ不在時はカプセル手にフォールバック
- **人側フルアバター＝Mixamo Remy（実人間・IK 駆動）に置換中**（2026-06-16, P0–P2 完了）。設計 [.claude/plans/2026-06-16_table-duo_remy-fullbody-avatar.md](../plans/2026-06-16_table-duo_remy-fullbody-avatar.md)。
  - 取り込み: `Assets/ThirdParty/Mixamo/Remy.fbx`（Generic・**×0.48 スケール**で標準身長。元は2倍）。マテリアルは暫定で `Resources/RemyBody.mat`（URP/Lit グレー単色＝**テクスチャ未対応は P4 仕上げ**）。プレハブ `Resources/RemyFullAvatar.prefab`（7メッシュ＋mixamorig 骨）
  - 駆動: [RemyAvatarRig](../../Assets/TableDuo/Scripts/Net/RemyAvatarRig.cs)（[RemoteAvatarView](../../Assets/TableDuo/Scripts/Net/RemoteAvatarView.cs) の Full 経路が Resources から引いて使用・無ければ procedural 人型へフォールバック）。**頭=受信回転（bind 補正）/ 腕=肩固定の解析2ボーン IK（[TwoBoneIK](../../Assets/TableDuo/Scripts/Net/TwoBoneIK.cs)・肘 pole で下外後ろ）/ 手首向き=受信回転 / 体幹・脚=固定の座位ポーズ**。SMR は updateWhenOffscreen=true
  - **2026-06-30 IK しっかり化（重要・旧「未校正で届かない」を解消）**: ① 座位ポーズを Mixamo ローカル Euler 当て推量 → **ワールド方向で各ボーンを向ける `AimBone` 方式**に書き換え（太もも水平前方・すね真下・足を床へ。一発で自然な座位に）。② TwoBoneIK を**ワールド前乗算の堅牢版**に置換＝「内角を余弦定理で曲げ → `FromToRotation(a→c, a→target)` で end を target 直線へ強制 → a→target 軸でひねって肘を pole 側へ」。**FromToRotation で照準を強制するので遠い/低い/高い wrist target にも必ず届く**（旧版は局所軸・後乗算＋別 twist が噛み合わず腕が上がっていた）。preview 4 ビュー（neutral/3-4/side/gesture）で到達・肘曲げ・座位を確認済み。③ **初期＝休めポーズ**: 構築時に `ApplyRestPose`（両手を卓上へ・指=前/手のひら=やや上）を適用＝**未トラッキング/接続直後/ロスト初期に腕が T 字（真横・手が外向き）で固まる不自然さを解消**（受信 pose が来れば Drive が上書き）。preview に `00_initial_rest` ショット追加で確認
  - **未: 指リターゲット（P3）** — 今は手が開いたまま（bind）。**テクスチャ（P4）**。実機検証
  - スクショ検証: `Tools/FixedCamVr/Diagnostics/Preview Full Avatar` が Remy を実 URP で隔離描画（エディタ同期実行では SMR が再スキンしないので `forceMatrixRecalculationPerRender=true` を付与）。`RemyAvatarRig`/`TwoBoneIK` は EditMode テスト済み（IK 到達/伸び切り）
  - ⚠ 取り込み作業で `TableDuoMain.unity` に紛れ Camera が入りうる（要 `git checkout` で破棄）。Remy はシーン配置不要・ランタイム生成
- 旧 procedural 簡易人型はフォールバックとして残存（2026-06-16）。頭（マネキン頭＋目2＝視線キュー）/首/肩/テーパー胴 + **肩→手首を直結した cosmetic な袖（肘 IK なし＝破綻しない・手の位置を体に反映）** + 既存の動く白手。全部 [RemoteAvatarView](../../Assets/TableDuo/Scripts/Net/RemoteAvatarView.cs) のプリミティブ procedural（外部アセット/依存なし）。配色は研究中立な無個性トーン（肌=TableDuoSkin / 袖&胴=TableDuoShirt / 目=TableDuoEye、Resources 優先・無ければランタイム URP/Lit 生成で Setup 未実行でも肌色で出る）。追跡が頭＋手首＋指のみなので realタイプ（Ready Player Me+IK）はアンカニー&肘破綻リスクで不採用、procedural を選択。**実機で見た目・腕の自然さ要確認**
  - **見た目の自己テスト**: メニュー `Tools/FixedCamVr/Diagnostics/Preview Full Avatar (screenshot)`（[TableDuoAvatarPreview](../../Assets/TableDuo/Scripts/Editor/TableDuoAvatarPreview.cs)）で Play/実機なしにアバターを**実 URP カメラ**で隔離描画し `Temp/AvatarPreview/*.png` に正面/3-4/側面/ジェスチャーを保存（Read で確認可）。`PreviewRenderUtility` はビルトイン経路で URP マテリアルが暗く出るため不可 → 専用レイヤ(31)+cullingMask の実カメラ描画にした。手は provider 不在で placeholder 立方体になる（体/腕/頭の形状・配色確認用）。`RemoteAvatarView.PoseImmediate` で平滑なし即時ポーズ（リプレイ seek にも流用可）
- **指ポーズが動かない（ずっとパー）バグ → 2026-06-15 修正済み**。原因: `OVRCustomHandPrefab_L/R` の `OVRCustomSkeleton._customBones_V2` が SDK 同梱状態で**全 null（未マッピング）**。`CustomBones` を読んで bone を回す実装だと全 null で指が一切動かず bind＝開いた手で固定（手首位置だけ別経路で追従）。修正＝`RemoteHandMeshProvider.MapHandBonesByName` で Meta の FBX 命名（`b_r_thumb0` 等、BoneId 順）を実体検索して回す（[[unity_pitfalls]] 参照）。**実機で指の曲げ同期は要再確認**（手首向きの二重適用が無いかも併せて見る）
- テーブル上の小物・カードは **Setup が天板の実バウンディング（高さ・XZ 範囲）を測って接地配置**（maxWidth で天板が 0.7→0.5 に縮むので固定 Y だと浮く。固定値ハードコード禁止）
- **2026-06-30 卓上を「海底探検（Deep Sea Adventure）」一式に差し替え**（旧: Kenney 食べ物プロップ＋アイコンカードデッキを撤去）。ボードゲーム 3D モデルは [Assets/TableDuo/ThirdParty/DeepSeaAdventure/glb/*.glb](../../Assets/TableDuo/ThirdParty/DeepSeaAdventure)（**glTFast `com.unity.cloud.gltfast` 6.14.1 を追加**してエディタ取込・テクスチャ埋込・実スケール=メートル）。配置は [TableDuoSceneSetup.PlaceDeepSeaAdventure](../../Assets/TableDuo/Scripts/Editor/TableDuoSceneSetup.cs)：潜水艦ボード中央奥に静置／宝物チップ16・裏トークン5・空気マーカーを手前にグリッド静置／駒2+サイコロは掴める（NetworkObject+Grabbable）。`PlaceModelRealScale` が glb プレハブを実寸のまま接地。`CreateCardDeck`/`CreateProp`/`CardIcons` は未使用化（コードは残置）。PatternPanel（空中・既定非アクティブ）は据置
  - **罠**: glTFast 追加直後は **Burst が壊れて GLB import が失敗**しがち（`GLTFast.Jobs.* Burst failed to compile` + XR Hands 等エディタ全体の Burst 失敗）→ **Unity 再起動**で fresh Burst になり GLB が正しく import される。再起動後の**初回 desktop ビルドは Standalone 切替のドメインリロードに BuildPlayer が競合して `result=Unknown`**（在席 MCP 経由）→ キービジュアル更新は batchmode 推奨。卓の見た目確認は **`manage_camera` の positioned scene 撮影**（Play/ビルド不要・OVR ハング回避）が速い。証跡 [docs/table-duo/deep-sea-table.png](../../docs/table-duo/deep-sea-table.png)
  - **要確認（実機 Quest）**: glTFast 生成マテリアル（URP）の shader が Android ビルドで stripping されずに出るか（マゼンタ化しないか）

## 手動視点リセット（2026-06-16）
- **人側/手側 両方**: コントローラ**両手グリップ同時 3 秒長押し**で頭を席（初期目線アンカー）へ戻す。
- **コントローラ限定**: 入力は OVRInput の grip 軸（LTouch/RTouch）だけ。ハンドトラッキング/ピンチは一切見ないのでジェスチャーでは発火しない。両手必須で片手偶発も防止（誤検知防止の明示要件）。閾値/秒数は `ControllerRecenterWatcher` の SerializeField
- 実装: [ControllerRecenterWatcher](../../Assets/TableDuo/Scripts/Hands/ControllerRecenterWatcher.cs)(OVR依存・Hands, event 発火) → [TableDuoPlayer](../../Assets/TableDuo/Scripts/Net/TableDuoPlayer.cs) が購読 → [RigRecenter.HeadToSeat](../../Assets/TableDuo/Scripts/Net/RigRecenter.cs)（純関数・yaw＋位置を頭→席へ、pitch/roll は保持＝水平維持。EditMode テスト済み）+ recenter ログ送信。OS recenter（[[ ]] RecenterWatcher）は従来通り AlignLocalRig
- **Setup 再実行が必要**: `ControllerRecenterWatcher` は `Setup TableDuo Scene` が Systems に追加する。既存シーンに無ければ再実行

## 既知の罠
- **ビルド直前に `Setup TableDuo Scene` を再実行**してクリーン状態にする。L0/リプレイ検証で OVRCameraRig 無効・DebugCamera/ReplayViewer 有効・FakeHandDriver 有効のまま保存→ビルドすると実機で VR にならず平面表示（2 回踏んだ → [[unity_pitfalls]] 同種）
- ハンドトラッキングは「自分の手を見てる（カメラ FOV 内）」時だけ高精度。不調切り分けは `HandPoseSampler.logDiagnostics=true`→`[TDV-DIAG]` ログ（既定オフ）
- 片手モードは右手固定（左抑制）。人側は自分の体を一人称描画しない（手のみ）。どちらも要望次第で変更可

## 2026-06-16 品質レビュー＋改善（多角的レビュー後の修正適用）
4視点（netcode/設計/性能/調査妥当性）で自己レビュー → 致命/高/中/安価な低を修正済み（大規模リファクタは defer）。

**データ整合性（研究計測器として重要）**:
- pose ワイヤに **`Seq`（連番）+ `CaptureMs`（送信端末壁時計）** 追加（[PoseCodec](../../Assets/TableDuo/Scripts/Net/PoseCodec.cs)）。CSV pose 行に列追加 → **歯抜け＝パケット欠落を「凍結」と区別でき**、client 内タイミングも復元可。手アバターは受信側 lossy 記録のままなので、厳密には各 client のローカル lossless 記録併用が理想（未実装・将来）
- **条件を clientId 別に記録**（CSV `condition` イベント, role/marker/oneHand）。host ローカル StudyConfig だと役割入替時に相手条件を取り違えるため、`TableDuoPlayer._studyFlags`（NetworkVariable）で同期
- **OS recenter を記録**（`TableDuoPlayer.ReportRecenterServerRpc`→ CSV `recenter`）。座標系不連続を解析で分割可能に
- カードは掴み中=毎サンプル + **全カード 2Hz スナップショット**（holder=-1）。掴まず「指す」RQ 用に静止カード位置も残す

**堅牢性/性能/保守**:
- domain-reload 無効 Play で前回値継承を防ぐ **static リセット**（`StudyConfig`/`HandSkeletonLayout.Captured`/`HandPoseSourceRegistry`、`[RuntimeInitializeOnLoadMethod]`）
- host の毎サンプル `GameObject.Find` を **`TableDuoPlayer.Seat` キャッシュ**に（Grabbable も掴み時1回解決）
- ボーン名/index を **[HandBoneTable](../../Assets/TableDuo/Scripts/Hands/HandBoneTable.cs) に集約**（mesh駆動・FKランドマーク・送信が同じ BoneId 順を共有、drift 防止）
- pose 送信の hitch 後バーストを clamp / ConnectionManager singleton dedup + 再接続時 pose バッファクリア / メッシュ手構築の例外&再Instantiate ガード / CSV escape を改行まで / Grabbable release で holderHand リセット

**defer（理由付き・要望時に着手）**: ConnectionManager god-object 全面分割 / `TryGetPose` 参照返し API / RemoteAvatarView の描画⇔調査方針分離 / AvatarPose 三役解体 / アセットパス定数集約 / テスト用 seam interface 化 — いずれも稼働中コードへの大規模リファクタで回帰リスク>価値。

## 2026-06-29 遅延対策（手アバター同期の体感レイテンシ削減）
送信→描画の遅延を ≈130–170ms → ≈60–80ms 目標に圧縮。全て TableDuo 単独・調査データ非影響（SessionLogger は受信生 pose を記録、平滑は描画のみ）。
- **送信 30→60Hz**: [TableDuoPlayer](../../Assets/TableDuo/Scripts/Net/TableDuoPlayer.cs) `sendRate=60`（prefab YAML も 60）。Quest ハンドトラッキング ~60Hz に整合。帯域 ≈29KB/s/プレイヤーで LAN 無視可
- **NGO TickRate 30→60**: named message(pose) の flush 間隔を 33→16ms に。[TableDuoSceneSetup](../../Assets/TableDuo/Scripts/Editor/TableDuoSceneSetup.cs) `TickRate=60` + シーンの NetworkConfig.TickRate=60。**送信レートと必ず揃える**（速く送っても tick で律速されるため）
- **送信を Update→LateUpdate**: `[DefaultExecutionOrder(100)]` で HandPoseSampler/FakeHandDriver（順0）採取の後に送る → 1フレ古い pose を送る問題を解消
- **受信平滑を 60Hz 向けに再調整**: [RemoteAvatarView](../../Assets/TableDuo/Scripts/Net/RemoteAvatarView.cs) `SmoothK 20→32`（τ 50→31ms）/ 胴 `ChestSmoothK=9`。収束遅れを削減
- **dead-reckoning（外挿）は意図的に不採用**: 手の overshoot が調査刺激を歪めるため。ロスト時は従来通り「最終姿勢でフリーズ」
- ⚠ **未検証**: 編集時 Unity が非フォーカスで MCP コンパイルが走らず、ドメイン再コンパイル＋実機の体感は未確認。次の Editor フォーカス／ビルドで自動コンパイルされる。**実機で遅延体感・jitter（K上げの副作用）を要確認**

## 2026-06-29 自律監査＋全面改善（netcode / 調査妥当性 / アバター品質）
3観点サブエージェント監査 → 全件改善（コンパイル確認: TableDuoVr.Tests 6/6 / 全 TableDuo asmdef ビルド green）。
- **手 layout 同期（最重要・研究妥当性）**: 各 client が起動時に手 bind 構造をホストへ1回送信（`tdv_layout` reliable named msg / [PoseCodec.WriteLayout/ReadLayout](../../Assets/TableDuo/Scripts/Net/PoseCodec.cs)）。[ConnectionManager.GetHandLayout](../../Assets/TableDuo/Scripts/Net/ConnectionManager.cs) を [SessionLogger](../../Assets/TableDuo/Scripts/Net/SessionLogger.cs) が使い、**指先7ランドマークFKを本人の手寸法で計算**（旧: ホスト自身の手骨格で全員分FK＝系統誤差）。未受信はホスト layout フォールバック＝最悪でも現状維持
- **接続上限 enforcement**: `ConnectionManager.maxClients=2`。3人目を server が `DisconnectClient`（席は2択固定なので席衝突・アバター重なり防止）
- **pose 受信の堅牢化**: `OnPoseMessage` を try/catch（壊れた Unreliable パケット1発で配送・リレーが死ぬのを防ぐ）。切断時 `OnClientDisconnectCallback` で stale バッファ掃除＋状態表示更新
- **参加者ID/ペアID**: 起動フラグ `tdv_pid` / `tdv_pair` → CSV ヘッダ＋ファイル名（紙記録突合・取り違え防止）。[StudyConfig](../../Assets/TableDuo/Scripts/Hands/StudyConfig.cs)
- **CSV 破損耐性**: `SessionLogger.Escape` が `"`/タブも無害化。`FacilitatorMarkServer` の label を 200 字 clamp
- **掴み対象の取得を都度化**: [PinchGrabInteractor](../../Assets/TableDuo/Scripts/Net/PinchGrabInteractor.cs) がピンチ時に `FindObjectsOfType<Grabbable>` 再取得（旧: 生成時1回固定で動的/遅延 spawn が掴めなかった）
- **`CreatePlayerPrefab` を冪等化**: 既存 prefab に必須コンポーネントが欠ければ非破壊で補完（旧: あれば即 return で構成変更が Setup 再実行で反映されなかった）
- **#7 Remy IK は 2026-06-30 に「しっかり化」して解消**（旧記述「変更不要だった」は誤り＝実際はベースラインで腕が T 字のまま wrist target に届いていなかった）。座位ポーズ AimBone 化＋TwoBoneIK 堅牢版で到達保証。`Preview Full Avatar (screenshot)` の neutral/gesture/side/3-4 で到達・肘曲げ・座位を確認（上の P0–P2 ブロック内「IK しっかり化」参照）
- **#8 メッシュ手の手首**: `_root`=wristRot ＋ wrist bone localRotation は OVRSkeleton の元構成（anchor×wrist.local）と一致＝二重でない（[RemoteAvatarView](../../Assets/TableDuo/Scripts/Net/RemoteAvatarView.cs) にコメント注記）。実機で最終確認余地は残す
- **解析ノートを [study-design.md](../../docs/table-duo/study-design.md) §4 に追記**: layout 同期 / 手役の遅延・seq/captureMs 整列 / half 量子化 / lossless 録画未配線 / pid・pair フラグ

**2 Quest 実機で検証済み（2026-06-29・adb intent host/client 起動 + logcat）**: 接続・役割確定（Full/Hand）・席アライン・片手モード・参加者/ペアID（CSV ファイル名 `_pairXX_pidYY`）・**手 layout 同期の往復成立**（client「layout を host へ送信」→ host「client1 から受信 L=True R=True」）・クラッシュ/例外ゼロ。
- ⚠ **実機で発覚し修正したバグ**: 手 layout(~1450B) は `NetworkDelivery.Reliable`(単一パケット上限 1264B)で `OverflowException` → **`ReliableFragmentedSequenced`** に修正（[ConnectionManager.SubmitLocalLayout](../../Assets/TableDuo/Scripts/Net/ConnectionManager.cs)）。Editor では出ず実機 logcat で判明
- **残る人手確認**（HMD 装着が要る）: 遅延の体感 / SmoothK 上げ由来の jitter / 掴み・指の見た目 / Remy の被験者目線での自然さ

## 2026-06-30 ライブ観戦（第三者視点）完成 + 段階診断
- **観戦ロール完成・end-to-end 実機検証**: host(Quest) + 観戦(PC=Editor Play) で、観戦が両プレイヤーを俯瞰で見られる。詳細・真因・運用注意 → [.claude/plans/2026-06-29_table-duo_spectator.md](../plans/2026-06-29_table-duo_spectator.md)
- **despawn の真因**: 観戦者(3人目)が接続直後に落ちていたのは host ビルドの `maxClients` が古い 2 で焼かれ kick していたため。clean シーン再ビルド（C# 既定 3）で解消
- **段階診断 [SeatAvatarPreview](../../Assets/TableDuo/Scripts/Net/SeatAvatarPreview.cs)**: `tdv_preplace=on` で各席に静的アバター先置き→接続でライブ差替。「静的出ない=描画/カメラ / 接続で消えライブ出る=疎通 / 出て固まる=トラッキング」と切り分く。研究本番は OFF
- ⚠ **Editor 観戦運用**: Play 突入中は MCP を叩かない（デッドロック実害）/ Play・ビルドで TableDuoMain に stray camera が serialize されたら `git checkout --` で破棄

## 未対応（要望待ち）
片手モードの左右選択/両手 / 人側の自分の胴体表示 / リプレイ音声同期実運用 / パイロット本番（所有者が手役を一度経験→プロトコル凍結） / 手アバターの client ローカル lossless 記録（上記データ整合性の完全版）
