# TableDuo ライブ観戦（第三者視点）実装計画 — 2026-06-29

## 目的
ファシリテータが人役 vs 手役のインタラクションを**ライブで外から客観視**する。idea-audit 結果は partial（3人目 NGO client は席モデル破綻リスク）だが、ユーザーは B=ライブ客観視を選択。**リスク最小版**で実装する。

## 方針（低リスク）
- **ConnectionApproval は使わない**（all-or-nothing で既存2人接続を壊す事故リスク大）。観戦者は「席を持たない seatless ロール（Spectator）」として扱う。
- `maxClients` 2→3（2プレイヤー + 観戦1）。2人接続パスは実質不変。
- 観戦カメラは**固定俯瞰**（自由カメラは defer。legacy/new Input の罠回避）。
- 観戦先 = PC（Editor Play）。Android ビルド対象のまま別ビルド不要。

## 実装
1. `StudyConfig.Role.Spectator=2` + `tdv_role=spectator` パース
2. `ConnectionManager`: `RoleOverride.Spectator`、`maxClients 2→3`、ApplyStudyFlags で spectator 解釈
3. `TableDuoPlayer`:
   - `SeatIndexOf(Spectator) → -1`
   - `SetupOwner(Spectator)`: 席アライン/pose送信/PinchGrab を作らず、SpectatorController を起動
   - `LateUpdate`: Spectator は pose を送らない
   - `SetupRemote`/`OnRoleSynced`: Spectator ロールの相手はアバター生成しない（observer は描画されない）
4. `SpectatorController`（新規・Net asmdef）: 起動時に OVRCameraRig を無効化し、テーブルを俯瞰する固定 Camera を有効化。両プレイヤーの NetworkObject は自動 replicate → RemoteAvatarView が描画
5. `TableDuoSceneSetup`: SpectatorController + 俯瞰 Camera（既定無効）を配置

## 検証
1. コンパイル（run_tests 強制）
2. **回帰**: Quest 再ビルド→2台 reinstall→host/client がこれまで通り接続するか（Spectator enum 追加・maxClients=3 で壊れてないか）
3. 観戦: Editor Play を spectator（autoMode=Client / studyRole=Spectator / IP=host Quest）で起動 → Game ビューに両アバターが俯瞰で出るか screenshot

## defer（audit と一致）
自由飛行カメラのライブ操作 / 観戦カメラ視点の録画（動画キャプチャで代替）/ 3台目 Quest 観戦 / 観戦カメラの遠隔制御（将来 FacilitatorMarkServer 拡張）/ 観戦音声

## 倫理メモ
観戦の存在が観察対象（RQ2/RQ3）を変えうる。被験者への開示要否は研究者判断（OPEN）。

## 2026-06-30 完成（end-to-end 実機検証済み）
**観戦は host(Quest) + 観戦(PC=Editor Play) で end-to-end 成立を確認。**
- **真因だった despawn を解消**: 観戦者(3人目)が接続直後に落ちていたのは、**実行中 host ビルドの `maxClients` が古い 2 で焼かれていて 3人目を kick**していたため（決定的ログ「接続上限(2)超過のため client 切断」。`OnClientDisconnected` の DisconnectReason ログで特定）。コミット済みシーンは maxClients 未serialize＝C# 既定 3 が効くので、**host を clean シーンから再ビルド**して解消。観戦者は kick されず接続維持を確認
- **段階診断（静的アバター先置き）を追加** [SeatAvatarPreview](../../Assets/TableDuo/Scripts/Net/SeatAvatarPreview.cs): `tdv_preplace=on`（Editor は ConnectionManager.preplaceAvatars）で各席に静的アバターを先置き→接続でライブに差替。検証手順:
  - Editor 単体（host+spectator+preplace）: 俯瞰カメラに人役 Remy が着座描画＝**描画OK**（screenshot 確認）
  - host(Quest,full,preplace) + 観戦(Editor,client+spectator+preplace): 静的先置き→`席0 ライブ接続→静的撤去`→ライブ人役が host の腕上げを追従＝**疎通・トラッキングOK**（screenshot 確認）
- **Editor 観戦の運用注意**:
  - **Play 突入中は MCP を一切叩かない**（叩くとブリッジ競合で EnterPlayMode デッドロック→Unity 強制終了の実害。プレーン sleep で待ってから MCP）
  - Play/ビルドで **TableDuoMain.unity に stray camera（URP の UniversalAdditionalCameraData）が serialize される**ことがある→ビルド後 `git checkout --` で破棄（disk を clean に保つ。in-memory は保存しない）
- **未検証**: 手役(client) を繋いだ時の席1 ライブ差替（今回 Quest 1台のみ接続で host だけ検証）。3台揃えば確認可。被験者装着時の見た目・遅延体感は人手
