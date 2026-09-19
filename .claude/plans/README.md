# 実装計画の索引

**ここは「実装の段」の記録で、いま進めるものの置き場ではない。**
作りこみの段でいま動いているのは `.claude/canon/`（`ROUNDS.md` の周回 / `OPEN.md` の未確定）と
`skills/work-round`。plans を開くのは「**この機能はなぜこう作られたか**」を知りたいときだけ。

読むときの注意 2 つ。

- **`status:` は書かれた日の申告**で、その後の実機検証は反映されていない。いま動くかどうかは
  コードと `.claude/memory/` を見る
- **企画書（`docs/proposal/` `docs/archive/`）は開かない**（CLAUDE.md）。plans が企画書を引用している
  箇所も、追いかけない

新しく plan を足すときは frontmatter に **`status:`** を書く（SessionStart hook とこの索引が読む）。

## 廻リ視（FixedCam）

| 日付 | slug | 状態 | 内容 |
|---|---|---|---|
| 05-03 | `phase4-offscreen-cg` | planned | スクリーン外 3D 演出 / CG 合成 |
| 05-03 | `video-fx-research` | done | 映像加工 / CG 合成手法の検討 |
| 05-04 | `phase3-onsite-demo` | in-progress | Phase 3 までの実機デモ統合 |
| 06-11 | `web-operator-console` | — | Web オペレータ卓（映像選択・合成範囲指定） |
| 06-16 | `web-config-to-quest` | — | Web のカメラ設定を Quest 実機へ流す |
| 07-16 | `zone-authoring-redesign` | phase 1-3 実装済 | 形状は PC・位置合わせは HMD |
| 07-17 | `pre-authored-cue-schedule` | in-progress | 周回×ゾーンの cue 事前オーサリング |
| 07-18 | `connection-robustness` | done | 端末内在 ID + 発見プロトコル + 自己修復 |
| 07-19 | `controller-roles` | done | コントローラ操作の根本設計 |
| 07-19 | `reg-points-authoring` | done | 位置合わせ基準点の Web オーサリング |
| 07-19 | `viewer-ux` | in-progress | 追従の緩急・切替作法・フェイルソフト |
| 07-19 | `web-ui-redesign` | done | Web UI の根本再設計 |
| 07-19 | `webui-timeline-authoring` | implemented | 卓タイムラインの第一級化 |
| 07-20 | `staff-input-hud-redesign` | — | 右手 4 入力集約 + HMD 単一サーフェス |
| 07-22 | `zone-switch-stability` | — | ゾーン→カメラ切替の安定化 |
| 07-23 | `verification-infrastructure` | — | 検証基盤の 4 層化（対話的 Unity からの脱却） |
| 07-25 | `shot-timeline-foundation` | design-fixed | ショット・タイムライン 3 層モデル |
| 07-25 | `show-simulator` | S1–S4 実装済 | 実機なしでショーを検証 |
| 07-26 | `material-atelier` | — | 素材工房を試写室として作り直す |
| 07-26 | `show-sources-and-cg-layer` | design-fixed | 録画 / スロット / CG レイヤ / 演出専用カメラ |
| 07-27 | `cg-actor-hand-tracking` | implemented | CG 人形をハンドトラッキングで動かす |
| 07-27 | `cg-compositing-rebuild` | Step 4 残 | 合成を実写になじませる基盤 |
| 07-27 | `position-trigger` | implemented | 「このラインを通過したら演出」 |
| 07-28 | `web-console-ux-rebuild` | in-progress | 卓の UI / UX 立て直し |
| 07-29 | `calib-ui-rebuild` | — | 姿勢を合わせる UI の立て直し |
| 07-29 | `proposal-v3-implementation` | — | 企画書（学会論文版）の要求を実装 |
| 07-29 | `take-continuity` | — | 演出の連続と、継ぎ目の遷移 |
| 07-30 | `intro-passthrough-to-screen` | — | 導入 — 現実が映像になる |
| 07-30 | `material-flow-rebuild` | **in-progress** | 素材フロー・マスクと合成・プロンプト管理 |
| 07-31 | `mask-determinism` | **planned** | 合成マスクを決定論的に決める |
| 08-02 | `lap4-and-floor-height` | — | 帰りの A まで延ばす / 床の高さ |
| 08-03 | `doll-scan-to-showactor` | — | 実物の日本人形を CG 人形にする |
| 08-04 | `doll-handoff` | — | 同上の引き継ぎ |
| 08-04 | `manual-camera-align` | — | カメラ位置合わせを「解く」から「合わせる」へ |
| 08-05 | `actor-follow-and-arms` | — | 人形の追従と腕（まず測る） |
| 08-05 | `device-tilt-align` | — | 端末の傾きで位置合わせを 4 自由度に |
| 08-12 | `title-screen` | implemented | タイトル画面「廻リ視」（導入の段 0 に被さる層・右 A で閉じる） |
| 09-19 | `eye-decode-motion` | implemented | 闇の目の出現と消失を「行が届く・ブロックが落ちる」へ（0240・R058・Editor 検証済み・実機未走行） |

## TableDuo（手アバター調査）

作りこみの対象外（CLAUDE.md）。触るのは TableDuo の作業に入ったときだけ。

| 日付 | slug | 状態 | 内容 |
|---|---|---|---|
| 06-10 | `table-duo_phase0-3` | — | Phase 0–3 実行計画 |
| 06-11 | `table-duo_replay` | — | セッションリプレイ（stimulated recall 用） |
| 06-11 | `table-duo_study` | — | 手アバター調査機能 |
| 06-16 | `table-duo_remy-fullbody-avatar` | — | 人側アバターを Remy へ置換 |
| 06-29 | `table-duo_spectator` | — | ライブ観戦（第三者視点） |
| 07-08 | `remy-hip-hinge-lean` | — | 上体リーンを骨盤ヒンジ方式へ |
| 07-08 | `tableduo-piece-physics` | in-progress | 卓上ピースの物理統合 |
| 07-10 | `tableduo-auto-connect` | in-progress | ホスト自動発見・自動接続 |
| 07-16 | `tableduo-controller-hand-basis` | — | コントローラ保持中の手の基底修正 |
| 07-18 | `tableduo-game-switcher-host-ui` | in-progress | ボドゲのランタイム切替 + 操作系 |
| 07-20 | `tableduo-geister` | implemented | ガイスター追加 |
| 07-22 | `tableduo-bandido` | implemented | バンディド追加 |
| 07-23 | `tableduo-six-strokes-bear` | — | あと6画のくま追加 |
| 07-24 | `tableduo-pen-grip-redesign` | — | ペングリップ再設計（ToolGripDriver） |
