#include "esp_camera.h"
#include <WiFi.h>

// Select camera model
#define CAMERA_MODEL_ESP32S3_EYE // Has PSRAM
#include "camera_pins.h"

// WiFi credentials
const char* ssid     = "CarterWIFI";
const char* password = "3366019747";

// TCP server info
const char* serverIp = "192.168.1.118";
const uint16_t serverPort = 5000;

WiFiClient client;

void cameraInit();
bool sendFrame(camera_fb_t* fb);

void setup() {
  Serial.begin(115200);
  Serial.setDebugOutput(false);
  Serial.println();

  cameraInit();

  // Optimize WiFi
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
  static uint32_t frameCount = 0;
  static uint32_t lastFpsTime = 0;
  static uint32_t sendTime = 0;
  
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
  config.ledc_timer = LEDC_TIMER_0;
  config.pin_d0 = Y2_GPIO_NUM;
  config.pin_d1 = Y3_GPIO_NUM;
  config.pin_d2 = Y4_GPIO_NUM;
  config.pin_d3 = Y5_GPIO_NUM;
  config.pin_d4 = Y6_GPIO_NUM;
  config.pin_d5 = Y7_GPIO_NUM;
  config.pin_d6 = Y8_GPIO_NUM;
  config.pin_d7 = Y9_GPIO_NUM;
  config.pin_xclk = XCLK_GPIO_NUM;
  config.pin_pclk = PCLK_GPIO_NUM;
  config.pin_vsync = VSYNC_GPIO_NUM;
  config.pin_href = HREF_GPIO_NUM;
  config.pin_sccb_sda = SIOD_GPIO_NUM;
  config.pin_sccb_scl = SIOC_GPIO_NUM;
  config.pin_pwdn = PWDN_GPIO_NUM;
  config.pin_reset = RESET_GPIO_NUM;
  
  config.xclk_freq_hz = 20000000;
  config.frame_size = FRAMESIZE_QVGA;
  config.pixel_format = PIXFORMAT_JPEG;
  config.grab_mode = CAMERA_GRAB_LATEST;
  config.fb_location = CAMERA_FB_IN_PSRAM;
  config.jpeg_quality = 12;
  config.fb_count = 2;

  if (!psramFound()) {
    config.fb_location = CAMERA_FB_IN_DRAM;
    config.fb_count = 1;
  }

  esp_err_t err = esp_camera_init(&config);
  if (err != ESP_OK) {
    Serial.printf("Camera init failed: 0x%x\n", err);
    return;
  }

  sensor_t* s = esp_camera_sensor_get();
  if (s) {
    s->set_brightness(s, 0);     // Normal brightness (was 1)
    s->set_saturation(s, 0);     // Normal saturation
    s->set_vflip(s, 1);          // Flip vertically
    s->set_hmirror(s, 1);        // Mirror horizontally
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
