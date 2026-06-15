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
- リモートの手＝**Meta の白い手メッシュ**（OVRCustomHandPrefab を同期 bone で駆動。ローカル手と同じ見た目）。供給は `RemoteHandMeshProvider`（Systems・Setup が L/R プレハブ＋白マテリアルを配線）、プレハブ不在時はカプセル手にフォールバック。**実機での見た目は要装着検証**（bone 向き補正が要るかも）
- テーブル上の小物・カードは **Setup が天板の実バウンディング（高さ・XZ 範囲）を測って接地配置**（maxWidth で天板が 0.7→0.5 に縮むので固定 Y だと浮く。固定値ハードコード禁止）

## 既知の罠
- **ビルド直前に `Setup TableDuo Scene` を再実行**してクリーン状態にする。L0/リプレイ検証で OVRCameraRig 無効・DebugCamera/ReplayViewer 有効・FakeHandDriver 有効のまま保存→ビルドすると実機で VR にならず平面表示（2 回踏んだ → [[unity_pitfalls]] 同種）
- ハンドトラッキングは「自分の手を見てる（カメラ FOV 内）」時だけ高精度。不調切り分けは `HandPoseSampler.logDiagnostics=true`→`[TDV-DIAG]` ログ（既定オフ）
- 片手モードは右手固定（左抑制）。人側は自分の体を一人称描画しない（手のみ）。どちらも要望次第で変更可

## 未対応（要望待ち）
片手モードの左右選択/両手 / 人側の自分の胴体表示 / リプレイ音声同期実運用 / パイロット本番（所有者が手役を一度経験→プロトコル凍結）
