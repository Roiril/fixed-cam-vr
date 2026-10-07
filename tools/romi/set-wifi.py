"""Romi（ESP32）へ LAN の参加情報を送る。値は環境変数 ROMI_WIFI_SSID / ROMI_WIFI_PASS から読む。
パスワードは表示もログもしない。保存先は ESP32 の NVS だけ（ソースにも git にも置かない）。

  入れ方（値はクリップボード経由）:
    & "$env:USERPROFILE\\.claude\\scripts\\set-secret.ps1" -Name ROMI_WIFI_PASS -Probe
  SSID は秘密ではないので ROMI_WIFI_SSID を [Environment]::SetEnvironmentVariable で入れてよい。
  使い方: py -3.11 tools/romi/set-wifi.py [COM17]      （先に evtlog.py などの COM 占有を止める）
"""
import os, sys, time, winreg, serial
sys.stdout.reconfigure(encoding="utf-8")


def env(name):
    v = os.environ.get(name)
    if v:
        return v
    # 設定直後の起動済みプロセスには環境変数が届かないので、ユーザー環境をレジストリから直に読む
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Environment") as k:
            return winreg.QueryValueEx(k, name)[0]
    except OSError:
        return None


ssid, pw = env("ROMI_WIFI_SSID"), env("ROMI_WIFI_PASS")
if not ssid or pw is None:
    sys.exit("ROMI_WIFI_SSID / ROMI_WIFI_PASS が未設定です")
if "|" in ssid:
    sys.exit("SSID に | を含むものは扱えません")
port = sys.argv[1] if len(sys.argv) > 1 else "COM17"
print("SSID=%s  パスワード長=%d" % (ssid, len(pw)))

s = serial.Serial()
s.port = port; s.baudrate = 115200; s.timeout = 0.1; s.dtr = False; s.rts = False
s.open(); time.sleep(0.2); s.reset_input_buffer()


def send(cmd, wait=2.0):
    s.write((cmd + "\n").encode("utf-8"))
    t0, buf = time.time(), b""
    while time.time() - t0 < wait:
        buf += s.read(4096)
    return buf.decode("utf-8", "replace").strip()


print(send("wifi set %s|%s" % (ssid, pw)))   # 応答は SSID と文字数だけで、パスワードは返さない
s.write(b"reboot\n")
t0, buf = time.time(), b""
while time.time() - t0 < 25:                 # 再起動後 STA 接続を待つ（最大 15 秒 + 起動）
    buf += s.read(4096)
    if b"ready" in buf:
        time.sleep(0.5); buf += s.read(4096); break
s.close()
for line in buf.decode("utf-8", "replace").splitlines():
    if "EVT net" in line or "ready" in line or "sta-failed" in line:
        print(line.strip())
