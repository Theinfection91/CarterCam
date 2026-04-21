#include "esp_camera.h"
#include <WiFi.h>

// Select camera model
#define CAMERA_MODEL_ESP32S3_EYE // Has PSRAM
#include "camera_pins.h"

// WiFi credentials
const char* ssid     = "CarterWIFI";
const char* password = "3366019747";

// TCP server info
const char* serverIp = "192.168.1.234";
const uint16_t serverPort = 5000;

WiFiClient client;

// ─── STEPPER (28BYJ-48 + ULN2003) ────────────────────────────────────────────
const int STEP_IN1 = 2;
const int STEP_IN2 = 3;
const int STEP_IN3 = 47;
const int STEP_IN4 = 48;

const int STEPS_PER_SIDE   = 525;
const int STEPS_NUDGE      = 30;   // small movement per L/R button press
const int STEP_DELAY_MS    = 10;
const int STEPPER_PAUSE_MS = 3000;

const int stepSequence[8][4] = {
  {1, 0, 0, 0},
  {1, 1, 0, 0},
  {0, 1, 0, 0},
  {0, 1, 1, 0},
  {0, 0, 1, 0},
  {0, 0, 1, 1},
  {0, 0, 0, 1},
  {1, 0, 0, 1}
};

// ─── Motor command shared state ───────────────────────────────────────────────
// Commands: 'L' left, 'R' right, 'C' center, 'S' sweep start, 'X' sweep stop
volatile char motorCommand = 0;
portMUX_TYPE motorMux = portMUX_INITIALIZER_UNLOCKED;

void setMotorCommand(char cmd) {
  portENTER_CRITICAL(&motorMux);
  motorCommand = cmd;
  portEXIT_CRITICAL(&motorMux);
}

char getMotorCommand() {
  portENTER_CRITICAL(&motorMux);
  char cmd = motorCommand;
  motorCommand = 0;
  portEXIT_CRITICAL(&motorMux);
  return cmd;
}

void stepMotor(int stepIndex) {
  digitalWrite(STEP_IN1, stepSequence[stepIndex][0]);
  digitalWrite(STEP_IN2, stepSequence[stepIndex][1]);
  digitalWrite(STEP_IN3, stepSequence[stepIndex][2]);
  digitalWrite(STEP_IN4, stepSequence[stepIndex][3]);
}

void stepperOff() {
  digitalWrite(STEP_IN1, 0);
  digitalWrite(STEP_IN2, 0);
  digitalWrite(STEP_IN3, 0);
  digitalWrite(STEP_IN4, 0);
}

void stepperMove(int steps, int dir, int& stepIndex) {
  for (int i = 0; i < steps; i++) {
    stepIndex = (stepIndex + dir + 8) % 8;
    stepMotor(stepIndex);
    vTaskDelay(STEP_DELAY_MS / portTICK_PERIOD_MS);
  }
}

// Center position = step index 0, track absolute position in steps from center
void stepperTask(void* pvParameters) {
  int stepIndex   = 0;
  int currentPos  = 0;   // steps from center; negative = left, positive = right
  bool sweeping   = false;
  int  sweepDir   = 1;

  stepMotor(stepIndex);
  vTaskDelay(300 / portTICK_PERIOD_MS);
  Serial.println("Stepper ready");

  while (true) {
    char cmd = getMotorCommand();

    if (cmd == 'L') {
      sweeping = false;
      stepperMove(STEPS_NUDGE, -1, stepIndex);
      currentPos -= STEPS_NUDGE;
      stepperOff();
      Serial.println("[Motor] Left nudge");
    }
    if (cmd == 'R') {
      sweeping = false;
      stepperMove(STEPS_NUDGE, 1, stepIndex);
      currentPos += STEPS_NUDGE;
      stepperOff();
      Serial.println("[Motor] Right nudge");
    }
    if (cmd == 'C') {
      sweeping = false;
      int dir = (currentPos > 0) ? -1 : 1;
      stepperMove(abs(currentPos), dir, stepIndex);
      currentPos = 0;
      stepperOff();
      Serial.println("[Motor] Centered");
    }

    if (cmd == 'S') {
      // Center first
      int cdir = (currentPos > 0) ? -1 : 1;
      stepperMove(abs(currentPos), cdir, stepIndex);
      currentPos = 0;
      // Drive to left end so center is the sweep midpoint
      stepperMove(STEPS_PER_SIDE, -1, stepIndex);
      currentPos = -STEPS_PER_SIDE;
      stepperOff();
      sweeping = true;
      sweepDir = 1;
      Serial.println("[Motor] Sweep start (at left end)");
    }
    if (cmd == 'X') { sweeping = false; stepperOff(); Serial.println("[Motor] Sweep stop"); }

    if (sweeping) {
      // Full width = 2x STEPS_PER_SIDE, center is midpoint
      stepperMove(2 * STEPS_PER_SIDE, sweepDir, stepIndex);
      currentPos += sweepDir * 2 * STEPS_PER_SIDE;
      stepperOff();
      Serial.printf("[Motor] Sweep at %s (pos=%d)\n", sweepDir == 1 ? "RIGHT" : "LEFT", currentPos);
      for (int i = 0; i < STEPPER_PAUSE_MS / 10; i++) {
        char c = getMotorCommand();
        if (c == 'X') { sweeping = false; break; }
        if (c != 0)   { setMotorCommand(c); break; }
        vTaskDelay(10 / portTICK_PERIOD_MS);
      }
      if (sweeping) sweepDir = -sweepDir;
    } else {
      vTaskDelay(10 / portTICK_PERIOD_MS);
    }
  }
}

// ─── CAMERA / WIFI ────────────────────────────────────────────────────────────
void cameraInit();
bool sendFrame(camera_fb_t* fb);

void setup() {
  Serial.begin(115200);
  Serial.setDebugOutput(false);
  Serial.println();

  cameraInit();

  pinMode(STEP_IN1, OUTPUT);
  pinMode(STEP_IN2, OUTPUT);
  pinMode(STEP_IN3, OUTPUT);
  pinMode(STEP_IN4, OUTPUT);
  stepperOff();
  Serial.println("Stepper ready on GPIO 2,3,47,48");

  xTaskCreatePinnedToCore(stepperTask, "StepperTask", 2048, NULL, 1, NULL, 1);

  WiFi.mode(WIFI_STA);
  WiFi.setSleep(false);
  WiFi.begin(ssid, password);

  while (WiFi.status() != WL_CONNECTED) {
    delay(500);
    Serial.print(".");
  }

  Serial.println("\nWiFi connected");
  Serial.print("IP: ");
  Serial.println(WiFi.localIP());
  Serial.print("RSSI: ");
  Serial.println(WiFi.RSSI());

  client.setTimeout(1000);
  client.setNoDelay(true);

  if (!client.connect(serverIp, serverPort)) {
    Serial.println("Failed to connect to TCP server");
  } else {
    Serial.println("Connected to TCP server");
  }
}

void loop() {
  static uint32_t frameCount  = 0;
  static uint32_t lastFpsTime = 0;
  static uint32_t sendTime    = 0;

  if (!client.connected()) {
    Serial.println("TCP disconnected, reconnecting...");
    client.stop();
    delay(100);
    if (!client.connect(serverIp, serverPort)) {
      Serial.println("Reconnect failed");
      delay(1000);
      return;
    }
    client.setNoDelay(true);
    Serial.println("Reconnected");
  }

  // ── Read incoming motor commands (non-blocking) ──
  while (client.available() > 0) {
    char cmd = (char)client.read();
    if (cmd == 'L' || cmd == 'R' || cmd == 'C' || cmd == 'S' || cmd == 'X') {
      Serial.printf("[Motor] Received command: %c\n", cmd);
      setMotorCommand(cmd);
    }
  }

  uint32_t captureStart = millis();
  camera_fb_t* fb = esp_camera_fb_get();
  uint32_t captureTime = millis() - captureStart;

  if (!fb) {
    Serial.println("Capture failed");
    return;
  }

  uint32_t sendStart = millis();
  bool sent = sendFrame(fb);
  sendTime = millis() - sendStart;

  esp_camera_fb_return(fb);

  if (sent) {
    frameCount++;
    uint32_t now = millis();
    if (now - lastFpsTime >= 1000) {
      Serial.printf("FPS: %d | Size: %d | Capture: %dms | Send: %dms | RSSI: %d\n",
                    frameCount, fb->len, captureTime, sendTime, WiFi.RSSI());
      frameCount = 0;
      lastFpsTime = now;
    }
  }
}

void cameraInit() {
  camera_config_t config;
  config.ledc_channel = LEDC_CHANNEL_0;
  config.ledc_timer   = LEDC_TIMER_0;
  config.pin_d0 = Y2_GPIO_NUM;
  config.pin_d1 = Y3_GPIO_NUM;
  config.pin_d2 = Y4_GPIO_NUM;
  config.pin_d3 = Y5_GPIO_NUM;
  config.pin_d4 = Y6_GPIO_NUM;
  config.pin_d5 = Y7_GPIO_NUM;
  config.pin_d6 = Y8_GPIO_NUM;
  config.pin_d7 = Y9_GPIO_NUM;
  config.pin_xclk     = XCLK_GPIO_NUM;
  config.pin_pclk     = PCLK_GPIO_NUM;
  config.pin_vsync    = VSYNC_GPIO_NUM;
  config.pin_href     = HREF_GPIO_NUM;
  config.pin_sccb_sda = SIOD_GPIO_NUM;
  config.pin_sccb_scl = SIOC_GPIO_NUM;
  config.pin_pwdn     = PWDN_GPIO_NUM;
  config.pin_reset    = RESET_GPIO_NUM;

  config.xclk_freq_hz = 20000000;
  config.frame_size   = FRAMESIZE_QVGA;
  config.pixel_format = PIXFORMAT_JPEG;
  config.grab_mode    = CAMERA_GRAB_LATEST;
  config.fb_location  = CAMERA_FB_IN_PSRAM;
  config.jpeg_quality = 12;
  config.fb_count     = 2;

  if (!psramFound()) {
    config.fb_location = CAMERA_FB_IN_DRAM;
    config.fb_count    = 1;
  }

  esp_err_t err = esp_camera_init(&config);
  if (err != ESP_OK) {
    Serial.printf("Camera init failed: 0x%x\n", err);
    return;
  }

  sensor_t* s = esp_camera_sensor_get();
  if (s) {
    s->set_brightness(s, 0);
    s->set_saturation(s, 0);
    s->set_vflip(s, 1);
    s->set_hmirror(s, 1);
  }
}

bool sendFrame(camera_fb_t* fb) {
  if (!client.connected()) return false;

  uint32_t len = fb->len;

  size_t written = client.write((uint8_t*)&len, 4);
  if (written != 4) return false;

  written = client.write(fb->buf, fb->len);
  return written == fb->len;
}
