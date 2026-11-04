// 無線でのファーム書き換え（ArduinoOTA・UDP/TCP 3232・2026-10-22）。
// USB を挿さずに `py -3.11 tools/romi/ota.py` で書き換える。diag.ino から include する（net.h の後）。
//
// ・パスワードは NVS（"romi" の "otapw"）にだけ置く。未設定なら OTA は開かない（ソース・git に値を置かない）
// ・走行中（時間指定・ジョイスティック・ショートカット）は招待に応じない。始まったらモーターを止める
// ・書き換え後の初回起動は「確認待ち」。STA で 10 秒つながったら正常と決める。
//   決める前に再起動したら（新しいファームが落ちる・固まる）ブートローダが前のファームへ戻す
//   （CONFIG_BOOTLOADER_APP_ROLLBACK_ENABLE=y の arduino-esp32 3.3.8 で有効）
#pragma once
#include <ArduinoOTA.h>
#include <esp_ota_ops.h>
#include <esp_task_wdt.h>

static const uint32_t OTA_VALID_AFTER_MS = 10000;   // STA がこれだけ続いたら新しいファームを正常と決める
static bool otaOn = false;           // パスワードが入っていて招待を待っている
static bool otaRunning = false;      // 受信中
static uint32_t otaCount = 0;        // この起動で受けた回数（成功すれば再起動するので、失敗の回数とほぼ同じ）
static String otaLastErr = "";
static int otaLastPct = -1;
static void (*otaStopHook)() = nullptr;

// 起動直後に arduino-esp32 が「正常」と決めてしまうのを止める（決めるのは otaTick）。
// 弱いシンボルは C の esp32-hal-misc.c にあるので extern "C" でないと上書きにならない（名前修飾で別物になる）
extern "C" bool verifyRollbackLater() { return true; }

static const char *otaImgState() {
  esp_ota_img_states_t st;
  if (esp_ota_get_state_partition(esp_ota_get_running_partition(), &st) != ESP_OK) return "none";
  switch (st) {
    case ESP_OTA_IMG_VALID: return "valid";
    case ESP_OTA_IMG_PENDING_VERIFY: return "pending";
    case ESP_OTA_IMG_NEW: return "new";
    case ESP_OTA_IMG_INVALID: return "invalid";
    case ESP_OTA_IMG_ABORTED: return "aborted";
    default: return "undefined";
  }
}

static const char *otaErrName(ota_error_t e) {
  switch (e) {
    case OTA_AUTH_ERROR: return "auth";       case OTA_BEGIN_ERROR: return "begin";  case OTA_CONNECT_ERROR: return "connect";
    case OTA_RECEIVE_ERROR: return "receive"; case OTA_END_ERROR: return "end";      default: return "unknown";
  }
}

static void otaApplyPassword(const String &pw) {
  if (!pw.length()) return;
  ArduinoOTA.setPassword(pw.c_str());
  if (!otaOn) {
    ArduinoOTA.begin();
    otaOn = true;
  }
}

static void otaBegin(void (*stopHook)()) {
  otaStopHook = stopHook;
  ArduinoOTA.setHostname(NET_HOST).setMdnsEnabled(false).setRebootOnSuccess(true);   // mDNS は net.h が持つ
  ArduinoOTA.onStart([]() {
    if (otaStopHook) otaStopHook();
    otaRunning = true; otaCount++; otaLastPct = -1; otaLastErr = "";
    Serial.printf("EVT ota start size=%u t=%lu\n", (unsigned)Update.size(), millis());
  });
  ArduinoOTA.onProgress([](unsigned int done, unsigned int total) {
    esp_task_wdt_reset();   // 受信の間ループは回らない（1MB で 10 秒前後）
    int pct = total ? (int)(done * 100ULL / total) : 0;
    if (pct / 25 != otaLastPct / 25) Serial.printf("EVT ota %d%%\n", pct);
    otaLastPct = pct;
  });
  ArduinoOTA.onEnd([]() { Serial.printf("EVT ota done -> reboot t=%lu\n", millis()); });
  ArduinoOTA.onError([](ota_error_t e) {
    otaRunning = false;
    otaLastErr = otaErrName(e);
    Serial.printf("EVT ota error=%s t=%lu\n", otaLastErr.c_str(), millis());
  });
  prefs.begin("romi", true);
  String pw = prefs.getString("otapw", "");
  prefs.end();
  otaApplyPassword(pw);
  Serial.printf("EVT ota %s app=%s img=%s\n", otaOn ? "ready" : "off(no password)", esp_ota_get_running_partition()->label, otaImgState());
}

// busy: 走行中。招待はそのまま UDP に残り、止まってから応じる（送り手は数秒で諦めるので実質は断り）
static void otaTick(bool busy, bool staUp, uint32_t staUpSince) {
  static bool decided = false;
#ifdef ROMI_OTA_CRASH_TEST
  // 戻す仕組みの試験用（ota.py crashtest）: 正常と決まる前に必ず落ちる版。ブートローダが前の版へ戻せば合格
  if (millis() > 3000) { Serial.println("EVT ota crash-test reboot"); delay(50); ESP.restart(); }
#endif
  if (!decided && staUp && millis() - staUpSince > OTA_VALID_AFTER_MS) {
    decided = true;
    esp_ota_img_states_t st;
    if (esp_ota_get_state_partition(esp_ota_get_running_partition(), &st) == ESP_OK && st == ESP_OTA_IMG_PENDING_VERIFY) {
      esp_ota_mark_app_valid_cancel_rollback();
      Serial.printf("EVT ota valid app=%s t=%lu\n", esp_ota_get_running_partition()->label, millis());
    }
  }
  if (otaOn && !busy) ArduinoOTA.handle();
}

static String otaJson() {
  return String("{\"on\":") + (otaOn ? 1 : 0) + ",\"app\":\"" + esp_ota_get_running_partition()->label + "\",\"img\":\"" + otaImgState() +
         "\",\"count\":" + String(otaCount) + ",\"err\":\"" + otaLastErr + "\"}";
}

// シリアル: ota / ota pass <pw> / ota clear。値は返さない（長さだけ）
static bool otaSerial(const String &line) {
  if (line == "ota") { Serial.println(otaJson()); return true; }
  if (line.startsWith("ota pass ")) {
    String pw = line.substring(9);
    if (pw.length() < 8) { Serial.println("ERR password too short (8+)"); return true; }
    prefs.begin("romi", false); prefs.putString("otapw", pw); prefs.end();
    otaApplyPassword(pw);
    Serial.printf("ota password saved (%d chars), listening on %d\n", (int)pw.length(), 3232);
    return true;
  }
  if (line == "ota clear") {
    prefs.begin("romi", false); prefs.remove("otapw"); prefs.end();
    Serial.println("ota password cleared (reboot to close the port)");
    return true;
  }
  return false;
}
