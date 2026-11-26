/* CYD PC Monitor 1.2 -- ESP32-2432S028R, ILI9341 / XPT2046.
   ESP32 Dev Module, Huge APP, DIO, 40MHz. USB serial: 115200 8N1.
   Host must send PING and check identity before sending JSON frames.
   No Wi-Fi credentials or weather-clock preferences are changed. */
#include <Arduino.h>
#include <SPI.h>
#include <Adafruit_GFX.h>
#include <Adafruit_ILI9341.h>
#include <XPT2046_Touchscreen.h>
#include <ArduinoJson.h>
#include <Preferences.h>

SPIClass screenSPI(HSPI), touchSPI(VSPI);
Adafruit_ILI9341 tft(&screenSPI, 2, 15, -1);
XPT2046_Touchscreen touch(33, 36);
Preferences prefs;
GFXcanvas16 cardCanvas(152, 86), headerCanvas(320, 35);
bool dirtyCards[6] = {}, dirtyHeader = true;
uint32_t cardWrites = 0, headerWrites = 0, layoutClears = 0;
struct Card { String label, value, unit; float bar; uint16_t color; } cards[6];
char input[4096];
size_t inputSize = 0;
bool dropping = false, received = false, stale = false, inverted = false;
bool wasTouch = false, wasBoot = false;
uint32_t lastFrame = 0, lastHello = 0, lastTap = 0;
int brightness = 200, requestedBrightness = -1, count = 6;
String title = "PC MONITOR", hostClock = "--:--:--";
const uint16_t BG = 0x0863, PANEL = 0x10A5, MUTED = 0x8C92;

String safeText(JsonVariantConst v, int limit, const char *fallback) {
  if (!v.is<const char *>()) return String(fallback);
  String s = v.as<String>();
  if (s.length() > (unsigned)limit) s.remove(limit);
  for (unsigned i = 0; i < s.length(); ++i)
    if (s[i] < 32 || s[i] > 126) s.setCharAt(i, '?');
  return s;
}
void hello() {
  Serial.print("{\"type\":\"hello\",\"device\":\"CYD_PC_MONITOR\",\"v\":1,\"fw\":\"1.2\",\"invert\":");
  Serial.print(inverted ? "true" : "false"); Serial.println("}");
}
void setBrightness(int value) { brightness = constrain(value, 20, 255); ledcWrite(21, brightness); }
void pushCanvas(int x, int y, int w, int h, GFXcanvas16 &canvas) {
  // All callers use an on-screen rectangle and a canvas whose row width equals w.
  tft.startWrite(); tft.setAddrWindow(x, y, w, h);
  tft.writePixels(canvas.getBuffer(), (uint32_t)w * h);
  tft.endWrite();
}
int barPixels(float bar) { return bar < 0 ? -1 : (int)(94 * constrain(bar, 0.f, 100.f) / 100.f); }
void header() {
  headerCanvas.fillScreen(BG);
  headerCanvas.setTextWrap(false); headerCanvas.setTextSize(1); headerCanvas.setTextColor(ILI9341_WHITE);
  headerCanvas.setCursor(8, 6); headerCanvas.print(title);
  headerCanvas.setCursor(260, 6); headerCanvas.print(hostClock);
  headerCanvas.setCursor(8, 21); headerCanvas.setTextColor(stale ? 0xFBE0 : 0x4F79);
  headerCanvas.print(stale ? "DATA LOST - LAST VALUES" : "USB CONNECTED / LIVE");
  pushCanvas(0, 0, 320, 35, headerCanvas); ++headerWrites;
}
void drawCard(int i) {
  int rows = count == 4 ? 2 : 3;
  int h = rows == 2 ? 86 : 60;
  int x = 6 + (i % 2) * 158, y = 39 + (i / 2) * (h + 5);
  Card &c = cards[i];
  int offset = (h - 60) / 2;
  cardCanvas.fillScreen(PANEL);
  cardCanvas.setTextWrap(false); cardCanvas.setTextColor(c.color);
  cardCanvas.setTextSize(1); cardCanvas.setCursor(8, offset + 6); cardCanvas.print(c.label);
  cardCanvas.setTextColor(stale ? MUTED : ILI9341_WHITE);
  cardCanvas.setTextSize(c.value.length() > 7 ? 2 : 3);
  cardCanvas.setCursor(8, offset + 20); cardCanvas.print(c.value);
  cardCanvas.setTextSize(1); cardCanvas.setTextColor(MUTED);
  cardCanvas.setCursor(8, offset + 46); cardCanvas.print(c.unit);
  if (c.bar >= 0) {
    cardCanvas.fillRect(50, offset + 49, 94, 4, BG);
    cardCanvas.fillRect(50, offset + 49, barPixels(c.bar), 4, c.color);
  }
  pushCanvas(x, y, 152, h, cardCanvas); ++cardWrites;
}
void dashboard(bool clear, bool force = false) {
  if (clear) { tft.fillScreen(BG); ++layoutClears; }
  if (clear || force || dirtyHeader) header();
  dirtyHeader = false;
  for (int i = 0; i < count; ++i) {
    if (clear || force || dirtyCards[i]) drawCard(i);
    dirtyCards[i] = false;
  }
  if (clear) {
    tft.setTextSize(1); tft.setTextColor(MUTED);
    tft.setCursor(8, 229); tft.print("TAP: BRIGHTNESS    BOOT: INVERT COLORS");
  }
}
void waiting() {
  tft.fillScreen(BG); tft.setTextColor(ILI9341_WHITE); tft.setTextSize(2);
  tft.setCursor(34, 55); tft.print("PC MONITOR");
  tft.setTextSize(1); tft.setTextColor(MUTED);
  tft.setCursor(34, 98); tft.print("1. Open CYD PC Monitor on Windows");
  tft.setCursor(34, 118); tft.print("2. Select the USB COM port");
  tft.setCursor(34, 138); tft.print("3. Click Connect");
  tft.setCursor(34, 174); tft.print("Waiting for PC data...");
}
void processLine() {
  input[inputSize] = 0;
  if (strcmp(input, "PING") == 0) { hello(); return; }
  if (strcmp(input, "STATS") == 0) {
    Serial.printf("{\"type\":\"stats\",\"cardWrites\":%lu,\"headerWrites\":%lu,\"layoutClears\":%lu}\n",
      (unsigned long)cardWrites, (unsigned long)headerWrites, (unsigned long)layoutClears);
    return;
  }
  JsonDocument doc;
  if (deserializeJson(doc, input)) { Serial.println("{\"type\":\"error\",\"reason\":\"json\"}"); return; }
  if (doc["type"] != "frame" || doc["v"] != 1 || !doc["seq"].is<uint32_t>()) return;
  JsonArrayConst array = doc["cards"].as<JsonArrayConst>();
  if (array.size() != 4 && array.size() != 6) return;
  // Validate before changing the displayed state; rejected packets do not refresh the timeout.
  for (JsonObjectConst c : array) {
    if (!c["label"].is<const char *>() || !c["value"].is<const char *>() ||
        !c["unit"].is<const char *>() || !c["color"].is<uint32_t>() ||
        !c["bar"].is<float>()) return;
  }
  bool clear = !received || (int)array.size() != count;
  bool recovered = stale;
  count = array.size();
  String nextTitle = safeText(doc["title"], 28, "PC MONITOR");
  String nextClock = safeText(doc["clock"], 8, "--:--:--");
  dirtyHeader = dirtyHeader || title != nextTitle || hostClock != nextClock;
  title = nextTitle; hostClock = nextClock;
  int i = 0;
  for (JsonObjectConst c : array) {
    Card next;
    next.label = safeText(c["label"], 22, "METRIC");
    next.value = safeText(c["value"], 10, "N/A");
    next.unit = safeText(c["unit"], 6, "");
    next.bar = c["bar"].as<float>();
    if (!isfinite(next.bar)) next.bar = -1;
    uint32_t rgb = c["color"].as<uint32_t>();
    next.color = tft.color565(rgb >> 16, rgb >> 8, rgb);
    dirtyCards[i] = cards[i].label != next.label || cards[i].value != next.value ||
      cards[i].unit != next.unit || cards[i].color != next.color || barPixels(cards[i].bar) != barPixels(next.bar);
    cards[i] = next; ++i;
  }
  int b = constrain(doc["brightness"] | 200, 20, 255);
  if (b != requestedBrightness) { requestedBrightness = b; setBrightness(b); }
  received = true; stale = false; lastFrame = millis(); dashboard(clear, recovered);
  Serial.print("{\"type\":\"ack\",\"v\":1,\"seq\":"); Serial.print(doc["seq"].as<uint32_t>()); Serial.println("}");
}
void setup() {
  pinMode(0, INPUT_PULLUP);
  pinMode(5, OUTPUT); digitalWrite(5, HIGH);
  for (int pin : {4, 16, 17}) { pinMode(pin, OUTPUT); digitalWrite(pin, HIGH); }
  Serial.begin(115200);
  screenSPI.begin(14, 12, 13, 15); tft.begin(); tft.setRotation(1);
  prefs.begin("pc-monitor", false);
  // Migrate the original inverted palette once; retain subsequent BOOT preferences.
  if (prefs.getUInt("color-ver", 0) < 2) {
    prefs.putBool("invert", false); prefs.putUInt("color-ver", 2);
  }
  inverted = prefs.getBool("invert", false);
  tft.invertDisplay(inverted);
  ledcAttach(21, 5000, 8); setBrightness(200);
  touchSPI.begin(25, 39, 32, 33); touch.begin(touchSPI); touch.setRotation(1);
  waiting(); hello();
}
void loop() {
  // A bounded line reader keeps touch and timeout handling responsive during a partial packet.
  int budget = 512;
  while (budget-- > 0 && Serial.available()) {
    char c = Serial.read();
    if (c == '\r') continue;
    if (c == '\n') {
      if (!dropping && inputSize > 0) processLine();
      inputSize = 0; dropping = false;
    } else if (!dropping) {
      if (inputSize < sizeof(input) - 1) input[inputSize++] = c;
      else { dropping = true; inputSize = 0; }
    }
  }
  uint32_t now = millis();
  if (received && !stale && now - lastFrame > 8000) { stale = true; dashboard(false, true); }
  if (!received && now - lastHello > 2000) { hello(); lastHello = now; }
  bool touched = touch.touched();
  if (touched && !wasTouch && now - lastTap > 350) {
    setBrightness(brightness > 180 ? 100 : brightness > 70 ? 35 : 230); lastTap = now;
  }
  wasTouch = touched;
  bool boot = digitalRead(0) == LOW;
  if (boot && !wasBoot && now - lastTap > 350) {
    inverted = !inverted; tft.invertDisplay(inverted); prefs.putBool("invert", inverted); lastTap = now;
  }
  wasBoot = boot; delay(2);
}
