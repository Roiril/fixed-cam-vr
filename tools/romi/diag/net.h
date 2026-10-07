// Romi のネットワーク層。他の機器（Quest・配信スマホ・タブレット）の接続方針に合わせる:
//   - 静的 IP（展示網の割り当て: PC .10 / カメラ .21-.23 / Quest .31 .32 / タブレット .41 / Romi .51）
//   - 切断は指数バックオフで再接続（1→2→4→8→16→30 秒）。つながったらリセット
//   - 20 秒つながりきらなければ無線をいったん落として張り直す（スタール対策）
//   - UDP :8830 の発見プロトコル fixedcam-discovery/1 に role:"cart" で参加（probe へ unicast 応答 + 5 秒毎 announce）
//   - 60 秒つながらなければ非常用 AP（ROMI-DIAG）を足す。復旧して 15 秒たてば畳む
//   - 10 分つながらず、走行中でもなければ再起動する（最後の手段）
// 参加情報（SSID / パスワード）は NVS だけに置く。ソースにも git にも載せない。
#pragma once
#include <WiFi.h>
#include <WiFiUdp.h>
#include <ESPmDNS.h>
#include <Preferences.h>

static const IPAddress NET_IP(192, 168, 10, 51);
static const IPAddress NET_GW(192, 168, 10, 1);
static const IPAddress NET_MASK(255, 255, 255, 0);
static const IPAddress NET_DNS(192, 168, 10, 1);
static const IPAddress NET_BCAST(192, 168, 10, 255);
static const char *NET_HOST = "romi";
static const char *AP_SSID = "ROMI-DIAG";
static const char *AP_PASS = "romi1234";   // 机上の非常口。現場へ持ち出すときは変える
static const uint16_t DISC_PORT = 8830;
static const char *DISC_PROTO = "fixedcam-discovery/1";
static const char *DISC_SHOW = "mawarimi";
static const char *FW_VERSION = "romi-diag-4";

static Preferences prefs;
static String netSsid, netPass, netUuid;
static bool netHaveCreds = false, netApOn = false, netAttempting = false;
static volatile bool netUp = false, evGotIp = false, evDisc = false;
static volatile int netLastReason = 0;
static uint32_t netDrops = 0;       // つながっていたのに切れた回数
static uint32_t netFails = 0;       // 連続失敗（バックオフの段）
static uint32_t netNextTry = 0, netAttemptAt = 0, netDownSince = 0, netUpSince = 0, netLastAnnounce = 0;
static WiFiUDP udp;

static uint32_t backoffMs(uint32_t fails) {
  uint32_t s = fails >= 5 ? 30 : (1u << fails);   // 1,2,4,8,16,30
  return (s > 30 ? 30 : s) * 1000u;
}

static const char *reasonName(int r) {
  switch (r) {
    case 2: return "AUTH_EXPIRE"; case 8: return "ASSOC_LEAVE"; case 15: return "4WAY_TIMEOUT";
    case 200: return "BEACON_TIMEOUT"; case 201: return "NO_AP_FOUND"; case 202: return "AUTH_FAIL";
    case 203: return "ASSOC_FAIL"; case 204: return "HANDSHAKE_TIMEOUT"; case 205: return "CONNECTION_FAIL";
    default: return "other";
  }
}

static void netOnEvent(arduino_event_id_t ev, arduino_event_info_t info) {
  if (ev == ARDUINO_EVENT_WIFI_STA_GOT_IP) {
    netUp = true; netAttempting = false; netFails = 0; netUpSince = millis(); netDownSince = 0; evGotIp = true;
  } else if (ev == ARDUINO_EVENT_WIFI_STA_DISCONNECTED) {
    netLastReason = info.wifi_sta_disconnected.reason;
    // 1 回の失敗で切断イベントが 2 回届く（NO_AP_FOUND の直後にもう 1 つ）ので、2 つ目では段を進めない
    if (!netUp && !netAttempting) return;
    if (netUp) netDrops++;
    netUp = false; netAttempting = false;
    if (!netDownSince) netDownSince = millis();
    netNextTry = millis() + backoffMs(netFails);
    if (netFails < 100) netFails++;
    evDisc = true;
  }
}

static void netStaStart() {
  WiFi.disconnect(false, false);
  WiFi.config(NET_IP, NET_GW, NET_MASK, NET_DNS);   // 静的 IP。DHCP に頼らない
  WiFi.setHostname(NET_HOST);
  WiFi.begin(netSsid.c_str(), netPass.c_str());
  netAttempting = true;
  netAttemptAt = millis();
}

static void netStartAp() {
  WiFi.softAP(AP_SSID, AP_PASS, 6, 0, 4);
  netApOn = true;
  Serial.printf("EVT net ap-on ssid=%s ip=%s\n", AP_SSID, WiFi.softAPIP().toString().c_str());
}

static void netBegin() {
  char u[24];
  snprintf(u, sizeof(u), "romi-%012llx", (unsigned long long)ESP.getEfuseMac());
  netUuid = u;
  prefs.begin("romi", true);
  netSsid = prefs.getString("ssid", "");
  netPass = prefs.getString("pass", "");
  prefs.end();
  netHaveCreds = netSsid.length() > 0;
  WiFi.persistent(false);
  WiFi.onEvent(netOnEvent);
  WiFi.setAutoReconnect(false);   // 再接続は自分で管理（バックオフ・張り直し）
  if (netHaveCreds) {
    WiFi.mode(WIFI_STA);
    WiFi.setSleep(false);         // 省電力を切って応答の揺れを減らす
    netNextTry = 0;
    netStaStart();
  } else {
    WiFi.mode(WIFI_AP);
    netStartAp();
  }
}

static String netAnnounce() {
  return String("{\"proto\":\"") + DISC_PROTO + "\",\"type\":\"announce\",\"show\":\"" + DISC_SHOW + "\",\"role\":\"cart\",\"id\":\"R\",\"uuid\":\"" +
         netUuid + "\",\"httpPort\":80,\"version\":\"" + FW_VERSION + "\",\"name\":\"romi\"}";
}

static void netSendTo(IPAddress ip, uint16_t port) {
  udp.beginPacket(ip, port);
  udp.print(netAnnounce());
  udp.endPacket();
}

static String netStatus() {
  String s;
  if (netHaveCreds) {
    s = netUp ? ("STA up ssid=" + WiFi.SSID() + " ip=" + WiFi.localIP().toString() + " rssi=" + String(WiFi.RSSI()) + " up=" + String((millis() - netUpSince) / 1000) + "s")
              : ("STA down for=" + String(netDownSince ? (millis() - netDownSince) / 1000 : 0) + "s next=" + String(netNextTry > millis() ? (netNextTry - millis()) / 1000 : 0) + "s");
    s += " drops=" + String(netDrops) + " fails=" + String(netFails) + " last=" + String(netLastReason) + "(" + reasonName(netLastReason) + ")";
  } else {
    s = "no-credentials";
  }
  if (netApOn) s += " | AP " + String(AP_SSID) + " ip=" + WiFi.softAPIP().toString() + " clients=" + String(WiFi.softAPgetStationNum());
  return s;
}

// busy: 走行中かどうか（走行中は最後の手段の再起動をしない）
static void netTick(bool busy) {
  uint32_t now = millis();
  if (evGotIp) {
    evGotIp = false;
    MDNS.end();
    MDNS.begin(NET_HOST);
    MDNS.addService("http", "tcp", 80);
    udp.stop();
    udp.begin(DISC_PORT);
    netLastAnnounce = 0;
    Serial.printf("EVT net up ip=%s rssi=%d drops=%u\n", WiFi.localIP().toString().c_str(), WiFi.RSSI(), (unsigned)netDrops);
  }
  if (evDisc) {
    evDisc = false;
    Serial.printf("EVT net down reason=%d(%s) drops=%u retry-in=%ums\n", netLastReason, reasonName(netLastReason), (unsigned)netDrops,
                  (unsigned)(netNextTry > now ? netNextTry - now : 0));
  }
  if (netHaveCreds) {
    if (!netUp) {
      if (!netAttempting && (int32_t)(now - netNextTry) >= 0) netStaStart();
      else if (netAttempting && now - netAttemptAt > 20000) {   // 20 秒たっても結果が出ない: 無線を張り直す
        Serial.println("EVT net stall -> radio restart");
        WiFi.mode(WIFI_OFF);
        delay(100);
        WiFi.mode(netApOn ? WIFI_AP_STA : WIFI_STA);
        WiFi.setSleep(false);
        if (netApOn) WiFi.softAP(AP_SSID, AP_PASS, 6, 0, 4);
        netAttempting = false;
        if (netFails < 100) netFails++;
        netNextTry = now + backoffMs(netFails);
      }
      if (netDownSince && !netApOn && now - netDownSince > 60000) {   // 非常口
        WiFi.mode(WIFI_AP_STA);
        netStartAp();
        netAttempting = false;
        netNextTry = now;
      }
      if (netDownSince && !busy && now - netDownSince > 600000) {    // 最後の手段
        Serial.println("EVT net down 10min -> reboot");
        delay(100);
        ESP.restart();
      }
    } else if (netApOn && now - netUpSince > 15000) {                // 復旧して安定したら非常口を畳む
      WiFi.softAPdisconnect(true);
      WiFi.mode(WIFI_STA);
      netApOn = false;
      Serial.println("EVT net ap-off (sta stable)");
    }
  }
  if (netUp) {
    int n = udp.parsePacket();
    if (n > 0) {
      char b[300];
      int l = udp.read(b, sizeof(b) - 1);
      if (l > 0) {
        b[l] = 0;
        if (strstr(b, DISC_PROTO) && strstr(b, DISC_SHOW) && strstr(b, "\"probe\"")) netSendTo(udp.remoteIP(), udp.remotePort());
      }
    }
    if (now - netLastAnnounce > 5000) {
      netLastAnnounce = now;
      netSendTo(NET_BCAST, DISC_PORT);
    }
  }
}
