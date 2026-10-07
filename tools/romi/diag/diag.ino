// Romi 台車の配線を調べる診断ファーム（ESP32）。シリアル 115200、行単位のコマンド。
// 元のスケッチ（Romi / Joy-Con 2 control v3）の逆アセンブルから得たピン:
//   side0: DIR=18 PWM=19 / side1: DIR=16 PWM=17 / AUX=2（左右の割り当ては実機で確認する）
// 安全: 起動時は全出力 LOW。駆動は時間・PWM に上限があり、終わると必ず止める。
#include <Arduino.h>
#include <Wire.h>
#include "soc/gpio_reg.h"

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

static void stopAll() {
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
    Serial.printf("ROMI-DIAG v1 chip=%s rev=%d cpu=%dMHz heap=%u up=%lus\n", ESP.getChipModel(), (int)ESP.getChipRevision(),
                  (int)getCpuFrequencyMhz(), (unsigned)ESP.getFreeHeap(), millis() / 1000);
    Serial.printf("side0 DIR=%d PWM=%d | side1 DIR=%d PWM=%d | AUX=%d | pwm=%dHz/%dbit\n", DIR_PIN[0], PWM_PIN[0], DIR_PIN[1],
                  PWM_PIN[1], AUX_PIN, PWM_HZ, PWM_BITS);
  } else if (line == "lv") printLevels();
  else if (line == "adc") adcScan();
  else if (line == "adcall") adcAll();
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
  delay(300);
  Serial.println();
  Serial.println("ROMI-DIAG v1 ready (type help)");
}

void loop() {
  static String buf;
  while (Serial.available()) {
    char ch = Serial.read();
    if (ch == '\n' || ch == '\r') { handle(buf); buf = ""; }
    else if (buf.length() < 80) buf += ch;
  }
}
