---
name: romi-cart-esp32
description: 人形を載せる台車（Pololu Romi）の制御基板は ESP32 + CP2102N。Joy-Con 2 を BLE で受けて走る自作スケッチ入り。この PC での接続手順と、確かめていないこと
metadata:
  node_type: memory
  type: project
  originSessionId: 44c10525-d6cb-48d9-a45f-69c3ffad2f1e
  modified: 2026-10-07T07:47:40.389Z
---

2026-10-07 調査。台車（Pololu Romi シャーシ）の USB の先は **Romi 純正の 32U4 基板ではなく ESP32**。

- USB-UART は **CP2102N（VID 10C4 / PID EA60）**。この PC では初接続時に **ドライバ未導入（コード 28）** で COM が出なかった。Silicon Labs の CP210x Universal Windows Driver 11.6.0.420（Microsoft 署名・WHQL）を入れて **COM17** になった。基板を挿し直すと番号が変わりうる
- ⚠ **Silicon Labs の zip は curl だと CDN が 403 を返す**（Akamai）。アプリ内ブラウザで公式ページを開き、そのページ上の `fetch` で取得して `<a download>` で保存すると取れる（Downloads に `.tmp` 名で落ちる。PK で始まるか見て zip として使う）。インストールは管理者権限が要り、`Start-Process -Verb RunAs` の UAC で `pnputil /add-driver silabser.inf /install`
- チップは **ESP32-D0WD-V3 rev3.1**（40MHz・4MB フラッシュ・MAC 8c:94:df:52:b3:00）。4MB 標準のパーティション（app0/app1/otadata/spiffs/coredump）
- 中身は **Arduino-ESP32 の自作スケッチ「Romi / Joy-Con 2 control v3」**（BLE 機器名 `jc2-romi`、NimBLE-Arduino）。Joy-Con 2 の SYNC を押して BLE 接続し、スティックの入力を左右モーターの PWM に変換する。起動直後は `[DISARMED]` で、**スティックが中立になって初めて armed** になる。`duty limit` と `SPEED_LIMIT` で速度を絞る。ボタンとスティックの較正（中心 2048 の既定）あり
- **ピン配置（元スケッチの逆アセンブルから確定・実機で確認済み）**: side0 = **左**車輪 DIR=GPIO18 / PWM=GPIO19、side1 = **右**車輪 DIR=GPIO16 / PWM=GPIO17、AUX=GPIO2（起動時 LOW・用途未確定）。PWM は 20kHz・10bit。**左右とも同じ dir で同じ向きに回る**（dir=0 → 手前、dir=1 → 奥。左右の反転補正は不要）。モーター電源（電池）は入っていて 30% で回る
- **付いていないもの（実測）**: エンコーダ（候補 18 ピンを駆動中に監視して変化なし）、I2C 機器（8 組のピンで 0 件）、バッテリ分圧（ADC は全て浮き電位）。**現状は完全な開ループ**
- ビルド元は別の PC の `Documents\Arduino`（NimBLE-Arduino 使用）で、元ソースはこの PC に無い。**元ファームの全フラッシュは `output/romi/esp32-flash-backup-20261007.bin`（4MB・git 管理外）にバックアップ済み**。復元は `esptool.py --port COMxx write_flash 0x0 <bin>`
- 今は **診断ファーム `tools/romi/diag/diag.ino`（ROMI-DIAG v1）が焼いてある**（元の Joy-Con 操作は動かない）。操作は `py -3.11 tools/romi/rc.py "d 0 0 300 1500" ...`。ビルドは Arduino IDE 同梱の arduino-cli、FQBN `esp32:esp32:esp32`（ESP32 コア 3.3.8）。**PlatformIO の ESP32 は Arduino コア 2.x で `ledcAttach` が無いので使えない**
- **手動操作の WebUI（診断ファーム v2・2026-10-07）**: ESP32 が Wi-Fi アクセスポイント `ROMI-DIAG`（パスワード `romi1234`・机上用）を立て、`http://192.168.4.1/` に直進/回転のバー操作画面を出す。「時間」と「強さ」のバーで量を決め、前進/後退/左右回転ボタンで実行。結果（cm・度）を入力欄に入れると `/res` でシリアルに `EVT res ...` が出る。画面は `tools/romi/diag/webui.h`、API は `/run /stop /state /res`。駆動は時間で自動停止・上限 68%（700/1023）・4 秒・120ms の立ち上げランプ。**前進は dir=0 と仮定**（実機で逆なら画面の「前後を逆にする」）。シリアルの `w <s|r> <±1> <duty%> <ms> <trim>` で同じ経路を叩ける。机上の画面確認は `tools/romi/webui-stub.py`（ESP32 を模す代役サーバ）。**`hidden` 属性は CSS の `display:grid` に負ける**（`.go[hidden]{display:none}` が要った）。**PC を `ROMI-DIAG` に繋ぐとインターネットが切れる**ので、PC からの操作確認は代役サーバで行い、実機の画面は電話で開く。結果の受け取りは `tools/romi/evtlog.py`（`output/romi/evt.log` に追記・COM を占有する）
- USB シリアルの往復遅延は中央値 15.5ms（p99 16.6ms）、ファーム内の駆動窓は ±3ms
- Romi シャーシ自体の公式仕様: 直径 165mm・163×149×70mm・160g（電池なし）、Mini Plastic Gearmotor 120:1 HP、ホイール 70×8mm、エンコーダは別売りで軸 1 回転 12 カウント、単三 6 本。**積載量・モーター定格電圧は公式ページに数値が無く、人形の重さに耐えるかは未確認**

**Why:** 台車の制御を作りこむとき、まず「何が載っていて、どこまで分かっているか」を探し直さないため。
**How to apply:** 台車を触る話が出たら、まず `[System.IO.Ports.SerialPort]::GetPortNames()` と `Get-PnpDevice -PresentOnly` で CP2102N が COM に出ているかを見る。esptool は `~/.platformio/packages/tool-esptoolpy/esptool.py`（`--after no_reset` で読むだけ、終わりに `--after hard_reset`）。書き込みは元のスケッチを消すので、先に app0（0x10000 から 0x140000）を `read_flash` で退避する。関連: [[quest-fleet-two-devices]]
