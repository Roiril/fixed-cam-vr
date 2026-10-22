"""Romi（ESP32）のファームを Wi-Fi 越しに書き換える（romi-diag-7〜・2026-10-22）。

  py -3.11 tools/romi/ota.py setpass [COM17]   # 初回だけ。合言葉を作って台車の NVS と User 環境変数へ（USB が要る）
  py -3.11 tools/romi/ota.py push [bin]        # 書き換え。既定は diag/build/esp32.esp32.esp32/diag.ino.bin
  py -3.11 tools/romi/ota.py status            # 台車の /info の ota 欄
  py -3.11 tools/romi/ota.py crashtest         # 起動 3 秒で落ちる版を送り、前の版へ自動で戻るかを見る

合言葉は User 環境変数 ROMI_OTA_PASS と台車の NVS だけに置く。表示もログもしない。
push は 走行中なら断る → espota で送る → 再起動を待つ → 新しい版が「確認待ち」から「正常」へ
決まるまで見る。正常と決まる前に台車が落ちれば、ブートローダが前の版へ戻す（diag/ota.h）。
走行中なら止まるまで待つ（台車も走行中は招待に応じない）。1.1MB の転送は 13〜16 秒、その間台車は止まる。
⚠ espota は PC 側で待ち受けて台車から繋がせる（この PC では 2026-10-22 に通った。別の PC で
  認証の OK の後に止まるならファイアウォールの受信を疑う）。
"""
import json, os, pathlib, secrets, subprocess, sys, time, urllib.request, winreg

sys.stdout.reconfigure(encoding="utf-8")
HOST = os.environ.get("ROMI_HOST", "192.168.10.51")
HERE = pathlib.Path(__file__).resolve().parent
BIN = HERE / "diag" / "build" / "esp32.esp32.esp32" / "diag.ino.bin"
ESPOTA = pathlib.Path(os.environ["LOCALAPPDATA"]) / "Arduino15/packages/esp32/hardware/esp32/3.3.8/tools/espota.exe"
NAME = "ROMI_OTA_PASS"


def env(name):
    v = os.environ.get(name)
    if v:
        return v
    try:   # 設定直後の起動済みプロセスには届かないので、ユーザー環境をレジストリから直に読む
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Environment") as k:
            return winreg.QueryValueEx(k, name)[0]
    except OSError:
        return None


def info(timeout=3.0):
    try:
        with urllib.request.urlopen(f"http://{HOST}/info", timeout=timeout) as res:
            return json.loads(res.read())
    except Exception:  # noqa: BLE001
        return None


def setpass(port):
    import serial
    pw = env(NAME)
    if not pw:
        pw = secrets.token_urlsafe(18)
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Environment", 0, winreg.KEY_SET_VALUE) as k:
            winreg.SetValueEx(k, NAME, 0, winreg.REG_SZ, pw)
        print(f"{NAME} を新しく作って User 環境変数に置いた（{len(pw)} 文字）")
    s = serial.Serial()
    s.port, s.baudrate, s.timeout, s.dtr, s.rts = port, 115200, 0.1, False, False
    s.open()
    time.sleep(0.2)
    s.reset_input_buffer()
    s.write(f"ota pass {pw}\n".encode())
    t0, buf = time.time(), b""
    while time.time() - t0 < 1.5:
        buf += s.read(4096)
    s.close()
    out = buf.decode("utf-8", "replace")
    assert pw not in out
    print(out.strip())


def wait_for(pred, sec, step=0.5):
    end = time.time() + sec
    while time.time() < end:
        got = info(1.5)
        if got and pred(got):
            return got
        time.sleep(step)
    return None


def push(binpath):
    pw = env(NAME)
    if not pw:
        sys.exit(f"{NAME} が無い。先に setpass")
    binpath = pathlib.Path(binpath)
    before = info()
    if not before:
        sys.exit(f"{HOST} の /info が返らない")
    if not before.get("ota", {}).get("on"):
        sys.exit("台車の OTA が閉じている（romi-diag-7 以降か・合言葉が入っているか）")
    # 台車は走行中（時間指定・ショートカット・終幕・ジョイスティック）の招待に応じず、止まってから受ける。
    # espota は招待を 10 回 × -t 秒送り直すので、その間に止まれば通る。ここでは先に止まるのを待つ
    for _ in range(120):
        try:
            with urllib.request.urlopen(f"http://{HOST}/state", timeout=2) as res:
                st = json.loads(res.read())
        except Exception:  # noqa: BLE001
            st = {"busy": 1}
        if not st.get("busy") and not st.get("seq"):
            break
        time.sleep(0.5)
    else:
        sys.exit("台車が 60 秒止まらない（走行中）")
    print(f"前: {before['version']}  app={before['ota']['app']}  up={before['up']}s  → {binpath.name} {binpath.stat().st_size} バイト")
    t0 = time.time()
    proc = subprocess.run([str(ESPOTA), "-i", HOST, "-p", "3232", "-a", pw, "-f", str(binpath), "-t", "20"],
                          capture_output=True, text=True, errors="replace")
    sent = time.time() - t0
    log = (proc.stdout + proc.stderr).replace(pw, "***")
    tail = [l for l in log.splitlines() if l.strip() and "Uploading" not in l][-4:]
    print(f"espota rc={proc.returncode}  {sent:.1f} 秒")
    for line in tail:
        print("  ", line.strip())
    if proc.returncode != 0:
        sys.exit(1)
    after = wait_for(lambda j: j["up"] < 30 and j["ota"]["app"] != before["ota"]["app"], 40)
    if not after:
        sys.exit("再起動後の台車が別の区画で上がってこない（前の版へ戻ったか、まだ起動中）")
    print(f"後: {after['version']}  app={after['ota']['app']}  img={after['ota']['img']}  reset={after['reset']}  "
          f"送信から応答まで {time.time() - t0:.1f} 秒")
    valid = wait_for(lambda j: j["ota"]["img"] == "valid", 30, 1.0)
    print("正常と確定" if valid else "まだ確認待ち（STA が 10 秒続かないと決まらない）")


def crashtest():
    """正常と決まる前に落ちる版（ROMI_OTA_CRASH_TEST）を送り、前の版へ自動で戻るかを見る。"""
    cli = pathlib.Path(os.environ["USERPROFILE"]) / "arduino-cli-home/cli/arduino-cli.exe"
    build = HERE / "diag" / "build" / "crashtest"
    r = subprocess.run([str(cli), "compile", "--fqbn", "esp32:esp32:esp32", "--build-path", str(build),
                        "--build-property", "compiler.cpp.extra_flags=-DROMI_OTA_CRASH_TEST", str(HERE / "diag")],
                       capture_output=True, text=True, errors="replace")
    if r.returncode != 0:
        sys.exit(r.stdout[-2000:] + r.stderr[-2000:])
    before = info()
    pw = env(NAME)
    print(f"前: app={before['ota']['app']} img={before['ota']['img']} up={before['up']}s")
    proc = subprocess.run([str(ESPOTA), "-i", HOST, "-p", "3232", "-a", pw, "-f", str(build / "diag.ino.bin"), "-t", "20"],
                          capture_output=True, text=True, errors="replace")
    print(f"espota rc={proc.returncode}")
    seen, end = [], time.time() + 45
    while time.time() < end:
        got = info(1.0)
        key = (got["ota"]["app"], got["ota"]["img"], got["reset"]) if got else None
        if not seen or seen[-1][1] != key:
            seen.append((round(time.time() - end + 45, 1), key))
        time.sleep(0.3)
    for t, key in seen:
        print(f"  {t:5.1f}s  {key}")
    last = seen[-1][1]
    ok = last and last[0] == before["ota"]["app"] and last[1] == "valid"
    print("合格: 前の版へ戻った" if ok else "不合格: 前の版に戻っていない")


def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else "status"
    if cmd == "setpass":
        setpass(sys.argv[2] if len(sys.argv) > 2 else "COM17")
    elif cmd == "push":
        push(sys.argv[2] if len(sys.argv) > 2 else BIN)
    elif cmd == "crashtest":
        crashtest()
    else:
        got = info()
        print(json.dumps(got.get("ota") if got else None, ensure_ascii=False), got and got.get("version"))


if __name__ == "__main__":
    main()
