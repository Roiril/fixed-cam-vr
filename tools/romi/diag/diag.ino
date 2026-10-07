// Romi 台車の配線を調べる診断ファーム（ESP32）。シリアル 115200、行単位のコマンド。
// 元のスケッチ（Romi / Joy-Con 2 control v3）の逆アセンブルから得たピン:
//   side0: DIR=18 PWM=19 / side1: DIR=16 PWM=17 / AUX=2（左右の割り当ては実機で確認する）
// 安全: 起動時は全出力 LOW。駆動は時間・PWM に上限があり、終わると必ず止める。
#include <Arduino.h>
#include <Wire.h>
#include <WiFi.h>
#include <WebServer.h>
#include <ESPmDNS.h>
#include <Preferences.h>
#include "soc/gpio_reg.h"
#include "webui.h"

static const int DIR_PIN[2] = {18, 16};
static const int PWM_PIN[2] = {19, 17};
static const int AUX_PIN = 2;
static const int PWM_HZ = 20000;
static const int PWM_BITS = 10;
static const int DUTY_MAX = 800;   // 1023 満点のうち
static const int MS_MAX = 4000;

// エンコーダなど外部入力の候補（UART の 1,3 とフラッシュの 6〜11、出力に使う 2,16〜19 は除く）
static const int SCAN_PINS[] = {4, 5, 12, 13, 14, 15, 21, 22, 23, 25, 26, 27, 32, 33, 34, 35, 36, 39};
static const int N_SCAN = sizeof(SCAN_PINS) / sizeof(SCAN_PINS[0]);

static inline bool level(int pin) {
  if (pin < 32) return (REG_READ(GPIO_IN_REG) >> pin) & 1;
  return (REG_READ(GPIO_IN1_REG) >> (pin - 32)) & 1;
}

// --- Web 手動操作（時間で自動停止する非ブロッキング駆動） ---
static const char *AP_SSID = "ROMI-DIAG";
static const char *AP_PASS = "romi1234";       // 机上の検証用。現場へ持ち出すときは変える
static const int WEB_DUTY_MAX = 700;           // 元スケッチの上限（0x2BC）に合わせる
static const int WEB_MS_MAX = 4000;
static const int RAMP_MS = 120;                // 立ち上げの直線ランプ（スリップを減らす）
static WebServer server(80);

struct Run {
  bool active = false;
  uint32_t id = 0, t0 = 0, ms = 0;
  int dirL = 0, dirR = 0, dutyL = 0, dutyR = 0;
} run;
static uint32_t runCounter = 0;

static void stopAll() {
  run.active = false;
  for (int s = 0; s < 2; s++) {
    ledcWrite(PWM_PIN[s], 0);
    digitalWrite(DIR_PIN[s], LOW);
  }
}

static void scanInputs(uint32_t ms, bool drive, int side, int dir, int duty) {
  uint32_t cnt[N_SCAN] = {0};
  bool last[N_SCAN];
  for (int i = 0; i < N_SCAN; i++) last[i] = level(SCAN_PINS[i]);
  if (drive) {
    digitalWrite(DIR_PIN[side], dir);
    ledcWrite(PWM_PIN[side], duty);
  }
  uint32_t t0 = millis();
  while (millis() - t0 < ms) {
    for (int i = 0; i < N_SCAN; i++) {
      bool v = level(SCAN_PINS[i]);
      if (v != last[i]) { cnt[i]++; last[i] = v; }
    }
  }
  stopAll();
  Serial.printf("window=%ums", (unsigned)ms);
  if (drive) Serial.printf(" side=%d dir=%d duty=%d", side, dir, duty);
  Serial.println();
  bool any = false;
  for (int i = 0; i < N_SCAN; i++) {
    if (cnt[i]) { Serial.printf("  GPIO%-2d edges=%u\n", SCAN_PINS[i], (unsigned)cnt[i]); any = true; }
  }
  if (!any) Serial.println("  (no edges on any scanned pin)");
}

static void printLevels() {
  Serial.print("levels (pullup on, input):");
  for (int i = 0; i < N_SCAN; i++) Serial.printf(" %d=%d", SCAN_PINS[i], level(SCAN_PINS[i]));
  Serial.println();
}

static void adcScan() {
  const int pins[] = {32, 33, 34, 35, 36, 39};
  Serial.print("adc mV:");
  for (int p : pins) {
    uint32_t sum = 0;
    for (int k = 0; k < 16; k++) sum += analogReadMilliVolts(p);
    Serial.printf(" %d=%u", p, (unsigned)(sum / 16));
  }
  Serial.println();
}

// プルアップ無しで ADC を読む（バッテリ分圧・電流センサの接続探し）。ADC2 は Wi-Fi 未使用なので読める
static void adcAll() {
  const int pins[] = {4, 12, 13, 14, 15, 25, 26, 27, 32, 33, 34, 35, 36, 39};
  for (int p : pins) pinMode(p, INPUT);
  delay(5);
  Serial.print("adc(no pull) mV:");
  for (int p : pins) {
    uint32_t sum = 0;
    for (int k = 0; k < 16; k++) sum += analogReadMilliVolts(p);
    Serial.printf(" %d=%u", p, (unsigned)(sum / 16));
  }
  Serial.println();
  for (int i = 0; i < N_SCAN; i++) pinMode(SCAN_PINS[i], SCAN_PINS[i] >= 34 ? INPUT : INPUT_PULLUP);
}

static void i2cScan(int sda, int scl) {
  Wire.end();
  Wire.begin(sda, scl, 100000);
  int found = 0;
  Serial.printf("i2c sda=%d scl=%d:", sda, scl);
  for (int addr = 1; addr < 127; addr++) {
    Wire.beginTransmission(addr);
    if (Wire.endTransmission() == 0) { Serial.printf(" 0x%02X", addr); found++; }
  }
  if (!found) Serial.print(" (none)");
  Serial.println();
  Wire.end();
  pinMode(sda, INPUT_PULLUP);
  pinMode(scl, INPUT_PULLUP);
}

static int clampi(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

// m: 's' 直進 / 'r' 回転。d>0: 前進・右回転（上から見て時計回り）。side0=左、side1=右。
// flip=0 のとき dir=0 を前進とみなす（実機で前後が逆なら UI の「前後を逆にする」で反転）。
static int startRun(char m, int d, int dutyPct, uint32_t ms, int trim, int flip) {
  if (run.active) return -1;
  if ((m != 's' && m != 'r') || ms < 50 || ms > WEB_MS_MAX || dutyPct < 1 || dutyPct > 100) return -2;
  int base = clampi(dutyPct * 1023 / 100, 0, WEB_DUTY_MAX);
  int F = flip ? 1 : 0;
  int fwd = F, back = 1 - F;
  trim = clampi(trim, -20, 20);
  if (m == 's') {
    int dir = d > 0 ? fwd : back;
    run.dirL = run.dirR = dir;
    run.dutyL = clampi(base * (100 + trim) / 100, 0, WEB_DUTY_MAX);
    run.dutyR = clampi(base * (100 - trim) / 100, 0, WEB_DUTY_MAX);
  } else {
    run.dirL = d > 0 ? fwd : back;      // 右回転: 左輪が前、右輪が後ろ
    run.dirR = d > 0 ? back : fwd;
    run.dutyL = run.dutyR = base;
  }
  run.id = ++runCounter;
  run.ms = ms;
  digitalWrite(DIR_PIN[0], run.dirL);
  digitalWrite(DIR_PIN[1], run.dirR);
  run.t0 = millis();
  run.active = true;
  Serial.printf("EVT run id=%u m=%c d=%d duty=%d ms=%u trim=%d flip=%d dutyL=%d dutyR=%d t=%lu\n", (unsigned)run.id, m, d, dutyPct,
                (unsigned)ms, trim, flip, run.dutyL, run.dutyR, millis());
  return (int)run.id;
}

static void runTick() {
  if (!run.active) return;
  uint32_t e = millis() - run.t0;
  if (e >= run.ms) {
    uint32_t id = run.id;
    stopAll();
    Serial.printf("EVT done id=%u el=%u t=%lu\n", (unsigned)id, (unsigned)e, millis());
    return;
  }
  float k = e >= (uint32_t)RAMP_MS ? 1.0f : (float)e / RAMP_MS;
  ledcWrite(PWM_PIN[0], (int)(run.dutyL * k));
  ledcWrite(PWM_PIN[1], (int)(run.dutyR * k));
}

static void jsonSend(const String &s) {
  server.sendHeader("Cache-Control", "no-store");
  server.send(200, "application/json", s);
}

static void httpRun() {
  char m = server.arg("m").length() ? server.arg("m")[0] : 0;
  int id = startRun(m, server.arg("d").toInt(), server.arg("duty").toInt(), (uint32_t)server.arg("ms").toInt(),
                    server.arg("trim").toInt(), server.arg("flip").toInt());
  if (id > 0) jsonSend("{\"ok\":1,\"id\":" + String(id) + "}");
  else jsonSend(String("{\"ok\":0,\"err\":\"") + (id == -1 ? "busy" : "range") + "\"}");
}

static void httpStop() {
  stopAll();
  Serial.printf("EVT stop t=%lu\n", millis());
  jsonSend("{\"ok\":1}");
}

static void httpState() {
  uint32_t el = run.active ? millis() - run.t0 : 0;
  jsonSend("{\"busy\":" + String(run.active ? 1 : 0) + ",\"id\":" + String(run.id) + ",\"el\":" + String(el) + ",\"ms\":" + String(run.ms) + "}");
}

static void httpRes() {
  String n = server.arg("n");
  n.replace(' ', '_'); n.replace('\n', '_'); n.replace('\r', '_');
  Serial.printf("EVT res id=%s v=%s u=%s note=%s t=%lu\n", server.arg("id").c_str(), server.arg("v").c_str(), server.arg("u").c_str(),
                n.c_str(), millis());
  jsonSend("{\"ok\":1}");
}

// LAN への参加情報は NVS（Preferences）に置く。ソースにも git にも載せない。
//   シリアルで `wifi set <ssid>|<password>` / `wifi clear` / `wifi status`
// 保存があれば STA（既存 LAN に参加）。15 秒つながらなければ机上用の AP にフォールバックする。
static Preferences prefs;
static bool staMode = false;

static String wifiStatus() {
  String s = staMode ? "STA " : "AP ";
  if (staMode) {
    s += (WiFi.status() == WL_CONNECTED) ? "connected ssid=" + WiFi.SSID() + " ip=" + WiFi.localIP().toString() + " rssi=" + String(WiFi.RSSI())
                                         : "disconnected";
  } else {
    s += "ssid=" + String(AP_SSID) + " ip=" + WiFi.softAPIP().toString() + " clients=" + String(WiFi.softAPgetStationNum());
  }
  return s;
}

static void startNetwork() {
  prefs.begin("romi", true);
  String ssid = prefs.getString("ssid", "");
  String pass = prefs.getString("pass", "");
  prefs.end();
  staMode = false;
  if (ssid.length()) {
    WiFi.mode(WIFI_STA);
    WiFi.setHostname("romi");
    WiFi.setAutoReconnect(true);
    WiFi.begin(ssid.c_str(), pass.c_str());
    uint32_t t0 = millis();
    while (WiFi.status() != WL_CONNECTED && millis() - t0 < 15000) delay(200);
    if (WiFi.status() == WL_CONNECTED) {
      staMode = true;
      WiFi.setSleep(false);              // 省電力を切って応答の揺れを減らす
      MDNS.begin("romi");                // http://romi.local/
      MDNS.addService("http", "tcp", 80);
    } else {
      WiFi.disconnect(true);
      Serial.println("EVT wifi sta-failed -> fallback AP");
    }
  }
  if (!staMode) {
    WiFi.mode(WIFI_AP);
    WiFi.softAP(AP_SSID, AP_PASS, 6, 0, 4);
  }
  Serial.printf("EVT net %s\n", wifiStatus().c_str());
}

static void startWeb() {
  startNetwork();
  server.on("/", HTTP_GET, []() { server.send_P(200, "text/html; charset=utf-8", PAGE_HTML); });
  server.on("/run", HTTP_GET, httpRun);
  server.on("/stop", HTTP_GET, httpStop);
  server.on("/state", HTTP_GET, httpState);
  server.on("/res", HTTP_GET, httpRes);
  server.onNotFound([]() { server.send(404, "text/plain", "not found"); });
  server.begin();
}

static void help() {
  Serial.println("commands:");
  Serial.println("  info | help | lv | adc | stop");
  Serial.println("  d <side 0|1> <dir 0|1> <duty 0..800> <ms 1..4000>   drive one motor, count edges on input pins");
  Serial.println("  scan <ms>      count edges with no motor (spin a wheel by hand)");
  Serial.println("  aux <0|1>      GPIO2");
  Serial.println("  raw <pin 16|17|18|19> <0|1>  digital write (PWM pins: detaches PWM until reboot)");
}

static void handle(String line) {
  line.trim();
  if (!line.length()) return;
  int a, b, c, d;
  if (line == "help") help();
  else if (line == "info") {
    Serial.printf("ROMI-DIAG v3 chip=%s rev=%d cpu=%dMHz heap=%u up=%lus net=%s\n", ESP.getChipModel(),
                  (int)ESP.getChipRevision(), (int)getCpuFrequencyMhz(), (unsigned)ESP.getFreeHeap(), millis() / 1000, wifiStatus().c_str());
    Serial.printf("side0 DIR=%d PWM=%d | side1 DIR=%d PWM=%d | AUX=%d | pwm=%dHz/%dbit\n", DIR_PIN[0], PWM_PIN[0], DIR_PIN[1],
                  PWM_PIN[1], AUX_PIN, PWM_HZ, PWM_BITS);
  } else if (line == "lv") printLevels();
  else if (line == "adc") adcScan();
  else if (line == "adcall") adcAll();
  else if (line == "wifi status") Serial.println(wifiStatus());
  else if (line == "wifi scan") {   // ESP32 から見える 2.4GHz の SSID を列挙（ESP32 は 5GHz を使えない）
    wifi_mode_t prev = WiFi.getMode();
    if (prev == WIFI_AP) WiFi.mode(WIFI_AP_STA);
    int n = WiFi.scanNetworks(false, true);
    Serial.printf("scan: %d networks\n", n);
    for (int i = 0; i < n; i++)
      Serial.printf("  ch%-2d rssi=%d enc=%d bssid=%s ssid=%s\n", WiFi.channel(i), WiFi.RSSI(i), (int)WiFi.encryptionType(i),
                    WiFi.BSSIDstr(i).c_str(), WiFi.SSID(i).c_str());
    WiFi.scanDelete();
    if (prev == WIFI_AP) WiFi.mode(WIFI_AP);
  }
  else if (line == "wifi clear") {
    prefs.begin("romi", false); prefs.remove("ssid"); prefs.remove("pass"); prefs.end();
    Serial.println("wifi credentials cleared (reboot to apply)");
  } else if (line.startsWith("wifi set ")) {
    String body = line.substring(9);
    int bar = body.indexOf('|');
    if (bar <= 0) { Serial.println("ERR usage: wifi set <ssid>|<password>"); return; }
    prefs.begin("romi", false);
    prefs.putString("ssid", body.substring(0, bar));
    prefs.putString("pass", body.substring(bar + 1));
    prefs.end();
    Serial.printf("wifi saved (ssid=%s, password %d chars). reboot to apply\n", body.substring(0, bar).c_str(), (int)(body.length() - bar - 1));
  } else if (line == "reboot") { Serial.println("rebooting"); delay(100); ESP.restart(); }
  else if (line.startsWith("w ")) {   // w <s|r> <d -1|1> <duty%> <ms> <trim>  Web と同じ駆動経路をシリアルから
    char m = 0; int dd = 0, du = 0, tr = 0; unsigned ms = 0;
    if (sscanf(line.c_str(), "w %c %d %d %u %d", &m, &dd, &du, &ms, &tr) < 4) { Serial.println("ERR args"); return; }
    int id = startRun(m, dd, du, ms, tr, 0);
    Serial.println(id > 0 ? "started" : (id == -1 ? "ERR busy" : "ERR range"));
  }
  else if (sscanf(line.c_str(), "i2c %d %d", &a, &b) == 2) {
    bool okA = false, okB = false;   // 候補ピンのうち出力もできるもの（34〜39 は入力専用）だけ許す
    for (int i = 0; i < N_SCAN; i++) {
      if (SCAN_PINS[i] < 34 && SCAN_PINS[i] == a) okA = true;
      if (SCAN_PINS[i] < 34 && SCAN_PINS[i] == b) okB = true;
    }
    if (!okA || !okB || a == b) { Serial.println("ERR pin"); return; }
    i2cScan(a, b);
  }
  else if (line == "stop") { stopAll(); Serial.println("stopped"); }
  else if (sscanf(line.c_str(), "d %d %d %d %d", &a, &b, &c, &d) == 4) {
    if (a < 0 || a > 1 || b < 0 || b > 1 || c < 0 || c > DUTY_MAX || d < 1 || d > MS_MAX) { Serial.println("ERR range"); return; }
    scanInputs(d, true, a, b, c);
  } else if (sscanf(line.c_str(), "scan %d", &a) == 1) {
    if (a < 1 || a > 20000) { Serial.println("ERR range"); return; }
    scanInputs(a, false, 0, 0, 0);
  } else if (sscanf(line.c_str(), "aux %d", &a) == 1) {
    digitalWrite(AUX_PIN, a ? HIGH : LOW);
    Serial.printf("aux=%d\n", a ? 1 : 0);
  } else if (sscanf(line.c_str(), "raw %d %d", &a, &b) == 2) {
    if (a < 16 || a > 19) { Serial.println("ERR pin"); return; }
    if (a == PWM_PIN[0] || a == PWM_PIN[1]) ledcDetach(a);
    pinMode(a, OUTPUT);
    digitalWrite(a, b ? HIGH : LOW);
    Serial.printf("GPIO%d=%d\n", a, b ? 1 : 0);
  } else Serial.println("ERR unknown (help)");
  Serial.println("ok");
}

void setup() {
  Serial.begin(115200);
  for (int s = 0; s < 2; s++) {
    pinMode(DIR_PIN[s], OUTPUT);
    digitalWrite(DIR_PIN[s], LOW);
    ledcAttach(PWM_PIN[s], PWM_HZ, PWM_BITS);
    ledcWrite(PWM_PIN[s], 0);
  }
  pinMode(AUX_PIN, OUTPUT);
  digitalWrite(AUX_PIN, LOW);
  for (int i = 0; i < N_SCAN; i++) pinMode(SCAN_PINS[i], SCAN_PINS[i] >= 34 ? INPUT : INPUT_PULLUP);
  startWeb();
  delay(300);
  Serial.println();
  Serial.printf("ROMI-DIAG v3 ready (type help) net=%s\n", wifiStatus().c_str());
}

void loop() {
  static String buf;
  server.handleClient();
  runTick();
  while (Serial.available()) {
    char ch = Serial.read();
    if (ch == '\n' || ch == '\r') { handle(buf); buf = ""; }
    else if (buf.length() < 200) buf += ch;
  }
}
