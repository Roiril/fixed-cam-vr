---
name: romi-cart-esp32
description: 人形を載せる台車（Pololu Romi）の制御基板は ESP32 + CP2102N。Joy-Con 2 を BLE で受けて走る自作スケッチ入り。この PC での接続手順と、確かめていないこと
metadata:
  node_type: memory
  type: project
  originSessionId: 44c10525-d6cb-48d9-a45f-69c3ffad2f1e
  modified: 2026-10-07T07:04:11.592Z
---

2026-10-07 調査。台車（Pololu Romi シャーシ）の USB の先は **Romi 純正の 32U4 基板ではなく ESP32**。

- USB-UART は **CP2102N（VID 10C4 / PID EA60）**。この PC では初接続時に **ドライバ未導入（コード 28）** で COM が出なかった。Silicon Labs の CP210x Universal Windows Driver 11.6.0.420（Microsoft 署名・WHQL）を入れて **COM17** になった。基板を挿し直すと番号が変わりうる
- ⚠ **Silicon Labs の zip は curl だと CDN が 403 を返す**（Akamai）。アプリ内ブラウザで公式ページを開き、そのページ上の `fetch` で取得して `<a download>` で保存すると取れる（Downloads に `.tmp` 名で落ちる。PK で始まるか見て zip として使う）。インストールは管理者権限が要り、`Start-Process -Verb RunAs` の UAC で `pnputil /add-driver silabser.inf /install`
- チップは **ESP32-D0WD-V3 rev3.1**（40MHz・4MB フラッシュ・MAC 8c:94:df:52:b3:00）。4MB 標準のパーティション（app0/app1/otadata/spiffs/coredump）
- 中身は **Arduino-ESP32 の自作スケッチ「Romi / Joy-Con 2 control v3」**（BLE 機器名 `jc2-romi`、NimBLE-Arduino）。Joy-Con 2 の SYNC を押して BLE 接続し、スティックの入力を左右モーターの PWM に変換する。起動直後は `[DISARMED]` で、**スティックが中立になって初めて armed** になる。`duty limit` と `SPEED_LIMIT` で速度を絞る。ボタンとスティックの較正（中心 2048 の既定）あり
- 配線（モーターのピン・ドライバ IC・エンコーダの有無）は **未確認**。ビルド元は別の PC の `Documents\Arduino` で、ソースはこの PC に無い。USB シリアルは 115200 で聴いても無音だった（出力していないか別のボーレート）
- Romi シャーシ自体の公式仕様: 直径 165mm・163×149×70mm・160g（電池なし）、Mini Plastic Gearmotor 120:1 HP、ホイール 70×8mm、エンコーダは別売りで軸 1 回転 12 カウント、単三 6 本。**積載量・モーター定格電圧は公式ページに数値が無く、人形の重さに耐えるかは未確認**

**Why:** 台車の制御を作りこむとき、まず「何が載っていて、どこまで分かっているか」を探し直さないため。
**How to apply:** 台車を触る話が出たら、まず `[System.IO.Ports.SerialPort]::GetPortNames()` と `Get-PnpDevice -PresentOnly` で CP2102N が COM に出ているかを見る。esptool は `~/.platformio/packages/tool-esptoolpy/esptool.py`（`--after no_reset` で読むだけ、終わりに `--after hard_reset`）。書き込みは元のスケッチを消すので、先に app0（0x10000 から 0x140000）を `read_flash` で退避する。関連: [[quest-fleet-two-devices]]
