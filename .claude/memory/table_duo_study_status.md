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
- **指ポーズが動かない（ずっとパー）バグ → 2026-06-15 修正済み**。原因: `OVRCustomHandPrefab_L/R` の `OVRCustomSkeleton._customBones_V2` が SDK 同梱状態で**全 null（未マッピング）**。`CustomBones` を読んで bone を回す実装だと全 null で指が一切動かず bind＝開いた手で固定（手首位置だけ別経路で追従）。修正＝`RemoteHandMeshProvider.MapHandBonesByName` で Meta の FBX 命名（`b_r_thumb0` 等、BoneId 順）を実体検索して回す（[[unity_pitfalls]] 参照）。**実機で指の曲げ同期は要再確認**（手首向きの二重適用が無いかも併せて見る）
- テーブル上の小物・カードは **Setup が天板の実バウンディング（高さ・XZ 範囲）を測って接地配置**（maxWidth で天板が 0.7→0.5 に縮むので固定 Y だと浮く。固定値ハードコード禁止）

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

## 未対応（要望待ち）
片手モードの左右選択/両手 / 人側の自分の胴体表示 / リプレイ音声同期実運用 / パイロット本番（所有者が手役を一度経験→プロトコル凍結） / 手アバターの client ローカル lossless 記録（上記データ整合性の完全版）
