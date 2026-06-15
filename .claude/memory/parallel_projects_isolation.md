---
name: parallel-projects-isolation
description: 廻リ視 と TableDuo を並列に進める時の干渉防止 — 共有資源・ビルド逐次・コード分離
metadata: 
  node_type: memory
  type: project
  originSessionId: d65504d0-1c8d-4824-abd6-262ece3d8453
---

1 Unity プロジェクトに **廻リ視（FixedCam / `FixedCamVr.*` / `Assets/Scripts/` / `Main.unity` / `com.roiril.mawarimi`）** と **TableDuo（`TableDuoVr.*` / `Assets/TableDuo/` / `TableDuoMain.unity` / `com.roiril.tableduo`）** が同居。別シュビー / 並列エージェント / worktree と本シュビーが別アプリを同時に進めることがある。

**Why:** 1 プロジェクト共有なので、片アプリの都合で共有資源（Unity Editor 単一インスタンス / `ProjectSettings/` / 共有 `.asset` URP・OVR・XR / `manifest.json`）を弄ると**もう片方を巻き込んで壊す**。ビルドは productName/ID を一時 swap → finally 復元するので、2 ビルド同時起動で片方が他方の ID で焼ける（[BuildVariants.cs](../../Assets/Editor/BuildVariants.cs)）。

**How to apply:**
- コードは完全分離。`FixedCamVr.*` ↔ `TableDuoVr.*` asmdef 相互参照禁止。相手のコード/シーン/prefab に手を出さない
- 共有資源は触ったら両方に効く。片アプリ専用都合で弄らない。触る時は逐次 + 影響説明
- Unity Editor 編集・Play・Build は単一 Editor を奪い合う → **並列化せず逐次**
- ビルドは 2 つ同時起動厳禁。片方の PlayerSettings 復元を確認してから次
- Quest 2 台運用は adb `-s <serial>` で対象指定（無指定は `more than one device` で失敗）
- 全規約は CLAUDE.md 冒頭「⚠ 同居 2 アプリ」+ [.claude/rules/parallel-projects.md](../rules/parallel-projects.md)。並列化可否は [[harness-design]] の worktree 判断とも整合
