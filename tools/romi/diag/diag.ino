// Romi 台車の制御・診断ファーム（ESP32）。v4: 目標（cm / 度）指定の Web 操作 + 堅牢なネットワーク。
// 元のスケッチ（Romi / Joy-Con 2 control v3）の逆アセンブルから得たピン:
//   side0=左: DIR=18 PWM=19 / side1=右: DIR=16 PWM=17 / AUX=2
// 安全: 起動時は全出力 LOW。駆動は時間・PWM に上限があり、終わると必ず止める。
// シリアル 115200 の行コマンドは `help`。ネットワークは net.h、画面は webui.h。
#include <Arduino.h>
#include <Wire.h>
#include <WebServer.h>
#include <esp_task_wdt.h>
#include "soc/gpio_reg.h"
#include "net.h"
#include "webui.h"

static const int DIR_PIN[2] = {18, 16};
static const int PWM_PIN[2] = {19, 17};
static const int AUX_PIN = 2;
static const int PWM_HZ = 20000;
static const int PWM_BITS = 10;
static const int DUTY_MAX = 800;   // シリアルの d コマンドの上限（1023 満点）
static const int MS_MAX = 4000;

// エンコーダなど外部入力の候補（UART の 1,3 とフラッシュの 6〜11、出力に使う 2,16〜19 は除く）
static const int SCAN_PINS[] = {4, 5, 12, 13, 14, 15, 21, 22, 23, 25, 26, 27, 32, 33, 34, 35, 36, 39};
static const int N_SCAN = sizeof(SCAN_PINS) / sizeof(SCAN_PINS[0]);

static inline bool level(int pin) {
  if (pin < 32) return (REG_READ(GPIO_IN_REG) >> pin) & 1;
  return (REG_READ(GPIO_IN1_REG) >> (pin - 32)) & 1;
}

// --- 手動操作（時間で自動停止する非ブロッキング駆動） ---
static const int WEB_DUTY_MAX = 700;    // 元スケッチの上限（0x2BC）に合わせる
static const int WEB_MS_MAX = 8000;
static const int RAMP_MS = 120;         // 立ち上げの直線ランプ（スリップを減らす）
static WebServer server(80);

struct Run {
  bool active = false;
  uint32_t id = 0, t0 = 0, ms = 0;
  int dirL = 0, dirR = 0, dutyL = 0, dutyR = 0;
} run;
static uint32_t runCounter = 0, lastId = 0, lastEl = 0;

// 較正モデル（NVS に保存。/cal で更新できる）。実測が溜まったら私が当てはめて入れ直す。
//   直進 [cm/s] = sv * (強さ% - sd)         時間 = 目標cm / 速さ + st
//   回転 [度/s] = rv * (強さ% - rd)         時間 = 目標度 / 角速度 + rt
struct Cal { float sv = 0.75f, sd = 8.f, st = 0.f, rv = 4.5f, rd = 10.f, rt = 0.f; uint32_t ver = 0; } cal;

static void calLoad() {
  prefs.begin("romi", true);
  cal.sv = prefs.getFloat("sv", cal.sv); cal.sd = prefs.getFloat("sd", cal.sd); cal.st = prefs.getFloat("st", cal.st);
  cal.rv = prefs.getFloat("rv", cal.rv); cal.rd = prefs.getFloat("rd", cal.rd); cal.rt = prefs.getFloat("rt", cal.rt);
  cal.ver = prefs.getUInt("calver", 0);
  prefs.end();
}

static void calSave() {
  cal.ver++;
  prefs.begin("romi", false);
  prefs.putFloat("sv", cal.sv); prefs.putFloat("sd", cal.sd); prefs.putFloat("st", cal.st);
  prefs.putFloat("rv", cal.rv); prefs.putFloat("rd", cal.rd); prefs.putFloat("rt", cal.rt);
  prefs.putUInt("calver", cal.ver);
  prefs.end();
}

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
    esp_task_wdt_reset();
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

// プルアップ無しで ADC を読む（バッテリ分圧・電流センサの接続探し）
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

static String clean(String s, unsigned maxLen) {   // ログ 1 行に収める（空白・区切りを潰す）
  s.replace(' ', '_'); s.replace(',', '_'); s.replace('\n', '_'); s.replace('\r', '_');
  if (s.length() > maxLen) s = s.substring(0, maxLen);
  return s;
}

// 目標から時間を求める。返り値: 0 以上=ms / -2=範囲外 / -3=強さが小さすぎる
static int planMs(char m, int dutyPct, float tgt, float *rate) {
  float r = (m == 's') ? cal.sv * (dutyPct - cal.sd) : cal.rv * (dutyPct - cal.rd);
  if (rate) *rate = r;
  if (r < 0.5f) return -3;
  if (tgt < 1.f || tgt > (m == 's' ? 150.f : 720.f)) return -2;
  float ms = tgt / r * 1000.f + (m == 's' ? cal.st : cal.rt);
  if (ms < 50.f || ms > WEB_MS_MAX) return -2;
  return (int)(ms + 0.5f);
}

// m: 's' 直進 / 'r' 回転。d>0: 前進・右回転（上から見て時計回り）。side0=左、side1=右。
// tgt>0 なら較正モデルから時間を決める。msOverride>0 なら時間を直接指定。
// flip=0 のとき dir=1 を前進とみなす（実機で確認済み）。UI の「前後を逆にする」で反転できる。
// 返り値: id(>0) / -1=実行中 / -2=範囲外 / -3=強さが小さすぎる
static int startRun(char m, int d, int dutyPct, float tgt, uint32_t msOverride, int trim, int flip, const String &bat, const String &load, uint32_t *plannedMs, float *rateOut) {
  if (run.active) return -1;
  if ((m != 's' && m != 'r') || dutyPct < 1 || dutyPct > 100) return -2;
  float rate = 0;
  int ms;
  if (msOverride > 0) {
    if (msOverride < 50 || msOverride > WEB_MS_MAX) return -2;
    ms = (int)msOverride;
    planMs(m, dutyPct, 1.f, &rate);
  } else {
    ms = planMs(m, dutyPct, tgt, &rate);
    if (ms < 0) return ms;
  }
  int base = clampi(dutyPct * 1023 / 100, 0, WEB_DUTY_MAX);
  int F = flip ? 0 : 1;   // 実測（2026-10-07）: 前進は dir=1。flip は「それを逆にする」
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
  run.ms = (uint32_t)ms;
  digitalWrite(DIR_PIN[0], run.dirL);
  digitalWrite(DIR_PIN[1], run.dirR);
  run.t0 = millis();
  run.active = true;
  if (plannedMs) *plannedMs = (uint32_t)ms;
  if (rateOut) *rateOut = rate;
  Serial.printf("EVT run id=%u m=%c d=%d duty=%d tgt=%.1f ms=%u rate=%.2f trim=%d flip=%d dutyL=%d dutyR=%d bat=%s load=%s cal=%u t=%lu\n",
                (unsigned)run.id, m, d, dutyPct, tgt, (unsigned)ms, rate, trim, flip, run.dutyL, run.dutyR, clean(bat, 40).c_str(),
                clean(load, 60).c_str(), (unsigned)cal.ver, millis());
  return (int)run.id;
}

static void runTick() {
  if (!run.active) return;
  uint32_t e = millis() - run.t0;
  if (e >= run.ms) {
    uint32_t id = run.id;
    stopAll();
    lastId = id;
    lastEl = e;
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

static String calJson() {
  return "{\"sv\":" + String(cal.sv, 4) + ",\"sd\":" + String(cal.sd, 3) + ",\"st\":" + String(cal.st, 1) + ",\"rv\":" + String(cal.rv, 4) +
         ",\"rd\":" + String(cal.rd, 3) + ",\"rt\":" + String(cal.rt, 1) + ",\"ver\":" + String(cal.ver) + "}";
}

static void httpRun() {
  char m = server.arg("m").length() ? server.arg("m")[0] : 0;
  uint32_t planned = 0;
  float rate = 0;
  int id = startRun(m, server.arg("d").toInt(), server.arg("duty").toInt(), server.arg("tgt").toFloat(), (uint32_t)server.arg("ms").toInt(),
                    server.arg("trim").toInt(), server.arg("flip").toInt(), server.arg("bat"), server.arg("load"), &planned, &rate);
  if (id > 0) jsonSend("{\"ok\":1,\"id\":" + String(id) + ",\"ms\":" + String(planned) + ",\"rate\":" + String(rate, 2) + "}");
  else jsonSend(String("{\"ok\":0,\"err\":\"") + (id == -1 ? "busy" : (id == -3 ? "weak" : "range")) + "\"}");
}

static void httpStop() {
  stopAll();
  Serial.printf("EVT stop t=%lu\n", millis());
  jsonSend("{\"ok\":1}");
}

static void httpState() {
  uint32_t el = run.active ? millis() - run.t0 : 0;
  jsonSend("{\"busy\":" + String(run.active ? 1 : 0) + ",\"id\":" + String(run.id) + ",\"el\":" + String(el) + ",\"ms\":" + String(run.ms) +
           ",\"lastId\":" + String(lastId) + ",\"lastEl\":" + String(lastEl) + "}");
}

static void httpRes() {
  Serial.printf("EVT res id=%s a=%s lat=%s u=%s bat=%s load=%s note=%s t=%lu\n", server.arg("id").c_str(), server.arg("a").c_str(),
                server.arg("lat").c_str(), server.arg("u").c_str(), clean(server.arg("bat"), 40).c_str(), clean(server.arg("load"), 60).c_str(),
                clean(server.arg("n"), 80).c_str(), millis());
  jsonSend("{\"ok\":1}");
}

static void httpCal() {
  bool any = false;
  struct { const char *k; float *p; float lo, hi; } t[] = {
    {"sv", &cal.sv, 0.05f, 5.f}, {"sd", &cal.sd, 0.f, 40.f}, {"st", &cal.st, -500.f, 1000.f},
    {"rv", &cal.rv, 0.5f, 50.f}, {"rd", &cal.rd, 0.f, 40.f}, {"rt", &cal.rt, -500.f, 1000.f}};
  for (auto &e : t) {
    if (server.hasArg(e.k)) {
      float v = server.arg(e.k).toFloat();
      *e.p = v < e.lo ? e.lo : (v > e.hi ? e.hi : v);
      any = true;
    }
  }
  if (any) {
    calSave();
    Serial.printf("EVT cal %s\n", calJson().c_str());
  }
  jsonSend(calJson());
}

static String infoJson() {
  return String("{\"proto\":\"") + DISC_PROTO + "\",\"show\":\"" + DISC_SHOW + "\",\"role\":\"cart\",\"id\":\"R\",\"uuid\":\"" + netUuid +
         "\",\"version\":\"" + FW_VERSION + "\",\"ip\":\"" + WiFi.localIP().toString() + "\",\"rssi\":" + String(WiFi.RSSI()) +
         ",\"up\":" + String(millis() / 1000) + ",\"drops\":" + String(netDrops) + ",\"lastReason\":" + String(netLastReason) +
         ",\"heap\":" + String(ESP.getFreeHeap()) + ",\"busy\":" + String(run.active ? 1 : 0) + ",\"calVer\":" + String(cal.ver) + "}";
}

static void startWeb() {
  server.on("/", HTTP_GET, []() { server.send_P(200, "text/html; charset=utf-8", PAGE_HTML); });
  server.on("/run", HTTP_GET, httpRun);
  server.on("/stop", HTTP_GET, httpStop);
  server.on("/state", HTTP_GET, httpState);
  server.on("/res", HTTP_GET, httpRes);
  server.on("/cal", HTTP_GET, httpCal);
  server.on("/info", HTTP_GET, []() { jsonSend(infoJson()); });
  server.on("/health", HTTP_GET, []() { jsonSend("{\"ok\":1,\"up\":" + String(millis() / 1000) + ",\"busy\":" + String(run.active ? 1 : 0) + "}"); });
  server.onNotFound([]() { server.send(404, "text/plain", "not found"); });
  server.begin();
}

static void help() {
  Serial.println("commands:");
  Serial.println("  info | help | lv | adc | adcall | stop | reboot");
  Serial.println("  w <s|r> <d -1|1> <duty%> <ms> <trim>   run by time (same path as the web UI)");
  Serial.println("  g <s|r> <d -1|1> <duty%> <target cm|deg> <trim>   run by target");
  Serial.println("  d <side 0|1> <dir 0|1> <duty 0..800> <ms 1..4000>   drive one motor, count edges on input pins");
  Serial.println("  scan <ms> | aux <0|1> | raw <pin 16..19> <0|1> | i2c <sda> <scl>");
  Serial.println("  cal | cal set <key>=<v> ...   keys: sv sd st rv rd rt");
  Serial.println("  wifi status | wifi scan | wifi set <ssid>|<pass> | wifi ssid <ssid> | wifi clear | wifi kick");
}

static void handle(String line) {
  line.trim();
  if (!line.length()) return;
  int a, b, c, d;
  if (line == "help") help();
  else if (line == "info") {
    Serial.printf("ROMI %s chip=%s rev=%d cpu=%dMHz heap=%u up=%lus uuid=%s\n", FW_VERSION, ESP.getChipModel(), (int)ESP.getChipRevision(),
                  (int)getCpuFrequencyMhz(), (unsigned)ESP.getFreeHeap(), millis() / 1000, netUuid.c_str());
    Serial.printf("net: %s\n", netStatus().c_str());
    Serial.printf("side0 DIR=%d PWM=%d | side1 DIR=%d PWM=%d | AUX=%d | pwm=%dHz/%dbit\n", DIR_PIN[0], PWM_PIN[0], DIR_PIN[1], PWM_PIN[1],
                  AUX_PIN, PWM_HZ, PWM_BITS);
    Serial.printf("cal: %s\n", calJson().c_str());
  } else if (line == "lv") printLevels();
  else if (line == "adc") adcScan();
  else if (line == "adcall") adcAll();
  else if (line == "cal") Serial.println(calJson());
  else if (line.startsWith("cal set ")) {
    String rest = line.substring(8);
    int pos = 0;
    while (pos < (int)rest.length()) {
      int sp = rest.indexOf(' ', pos);
      String tok = rest.substring(pos, sp < 0 ? rest.length() : sp);
      int eq = tok.indexOf('=');
      if (eq > 0) {
        String k = tok.substring(0, eq);
        float v = tok.substring(eq + 1).toFloat();
        if (k == "sv") cal.sv = v; else if (k == "sd") cal.sd = v; else if (k == "st") cal.st = v;
        else if (k == "rv") cal.rv = v; else if (k == "rd") cal.rd = v; else if (k == "rt") cal.rt = v;
      }
      if (sp < 0) break;
      pos = sp + 1;
    }
    calSave();
    Serial.println(calJson());
  }
  else if (line == "wifi status") Serial.println(netStatus());
  else if (line == "wifi kick") { Serial.println("kicking STA (expect auto reconnect)"); WiFi.disconnect(false, false); }
  else if (line.startsWith("wifi fake ")) {   // 試験用: RAM 上の SSID だけ偽物にして「つながらない状況」を作る（NVS は触らない）
    netSsid = line.substring(10);
    Serial.printf("fake ssid '%s' (RAM only). `wifi unfake` restores\n", netSsid.c_str());
    WiFi.disconnect(false, false);
  } else if (line == "wifi unfake") {
    prefs.begin("romi", true); netSsid = prefs.getString("ssid", ""); prefs.end();
    Serial.println("ssid restored from NVS");
  }
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
  } else if (line.startsWith("wifi ssid ")) {   // パスワードはそのまま、SSID だけ差し替える（試験用）
    prefs.begin("romi", false); prefs.putString("ssid", line.substring(10)); prefs.end();
    Serial.printf("ssid set to '%s' (reboot to apply)\n", line.substring(10).c_str());
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
  else if (line.startsWith("w ") || line.startsWith("g ")) {   // w: 時間指定 / g: 目標指定。Web と同じ駆動経路
    char m = 0; int dd = 0, du = 0, tr = 0; float v = 0;
    if (sscanf(line.c_str() + 2, "%c %d %d %f %d", &m, &dd, &du, &v, &tr) < 4) { Serial.println("ERR args"); return; }
    uint32_t planned = 0;
    int id = line[0] == 'w' ? startRun(m, dd, du, 0, (uint32_t)v, tr, 0, "", "", &planned, nullptr)
                            : startRun(m, dd, du, v, 0, tr, 0, "", "", &planned, nullptr);
    Serial.println(id > 0 ? "started" : (id == -1 ? "ERR busy" : (id == -3 ? "ERR weak" : "ERR range")));
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
  // ループが 30 秒止まったら自動で再起動する（起動時は全出力 LOW なので安全に戻る）
  esp_task_wdt_config_t twdt = {.timeout_ms = 30000, .idle_core_mask = 0, .trigger_panic = true};
  esp_task_wdt_reconfigure(&twdt);
  esp_task_wdt_add(NULL);
  calLoad();
  netBegin();       // ブロックしない。接続はイベントとバックオフで進める
  startWeb();
  delay(300);
  Serial.println();
  Serial.printf("ROMI %s ready (type help) net: %s\n", FW_VERSION, netStatus().c_str());
}

void loop() {
  static String buf;
  esp_task_wdt_reset();
  server.handleClient();
  runTick();
  netTick(run.active);
  while (Serial.available()) {
    char ch = Serial.read();
    if (ch == '\n' || ch == '\r') { handle(buf); buf = ""; }
    else if (buf.length() < 200) buf += ch;
  }
}
