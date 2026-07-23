#!/usr/bin/env node
// PreToolUse ガード: Unity MCP の manage_editor action=play をブロックする。
//
// 背景: Link/HMD 未接続で OVR シーンを Play すると EnterPlayMode がデッドロックし
// Unity ごと落ちる（.claude/rules/mcp-unity.md の既知事故。2026-06-29 / 2026-07-23 の 2 回実害）。
// 「ルールに書いてあるのに読まれず実行される」を防ぐため、hook で機械的に強制する。
//
// 解錠（ワンショット）: Link または Meta XR Simulator の接続を確認した上で
//   .claude/allow-unity-play を作成（touch）してから play を呼ぶ。実行時にマーカーは自動削除される。
"use strict";

const fs = require("fs");
const path = require("path");

let raw = "";
process.stdin.on("data", (d) => (raw += d));
process.stdin.on("end", () => {
  let input;
  try {
    input = JSON.parse(raw);
  } catch {
    process.exit(0); // パース不能は素通し（ガード対象を誤爆させない）
  }

  const toolName = input.tool_name || "";
  if (!/manage_editor$/i.test(toolName)) process.exit(0);

  const action = (input.tool_input && input.tool_input.action) || "";
  if (action !== "play") process.exit(0);

  const projectDir =
    process.env.CLAUDE_PROJECT_DIR || path.resolve(__dirname, "..", "..");
  const marker = path.join(projectDir, ".claude", "allow-unity-play");

  if (fs.existsSync(marker)) {
    try {
      fs.unlinkSync(marker); // ワンショット解錠（次回はまたブロック）
    } catch {}
    process.exit(0);
  }

  process.stderr.write(
    "[guard-unity-play] BLOCKED: Link/HMD 未接続の OVR シーンで Play すると EnterPlayMode が" +
      "デッドロックし Unity ごと落ちる（mcp-unity.md 既知事故・実害 2 回）。\n" +
      "HUD/見た目の確認なら Play 不要の Tools/FixedCamVr/Preview/HUD Preview (screenshot) か " +
      "batchmode CaptureBatch を使う。\n" +
      "どうしても Play が必要なら: Quest Link または Meta XR Simulator の接続を確認 → " +
      ".claude/allow-unity-play を作成（ワンショット解錠・実行時に自動削除）→ 再実行。\n"
  );
  process.exit(2);
});
