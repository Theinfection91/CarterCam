#include "esp_camera.h"
#include <WiFi.h>

// Select camera model
#define CAMERA_MODEL_ESP32S3_EYE // Has PSRAM
#include "camera_pins.h"

// WiFi credentials
const char* ssid     = "CarterWIFI";
const char* password = "3366019747";

// TCP server info
const char* serverIp = "192.168.1.118"; // Your C# server IP
const uint16_t serverPort = 5000;       // TCP port from TcpFrameServer

WiFiClient client;

void cameraInit();
bool sendFrame(camera_fb_t* fb);

void setup() {
  Serial.begin(115200);
  Serial.setDebugOutput(true);
  Serial.println();

  cameraInit();

  WiFi.begin(ssid, password);
  while (WiFi.status() != WL_CONNECTED) {
    delay(500);
    Serial.print(".");
  }
  Serial.println();
  Serial.println("WiFi connected");
  Serial.print("Camera Ready! Use IP: ");
  Serial.println(WiFi.localIP());

  // Connect to TCP server
  if (!client.connect(serverIp, serverPort)) {
    Serial.println("Failed to connect to TCP server");
  } else {
    Serial.println("Connected to TCP server");
  }
}

void loop() {
  if (!client.connected()) {
    Serial.println("TCP disconnected, reconnecting...");
    client.stop();
    if (!client.connect(serverIp, serverPort)) {
      Serial.println("Reconnect failed");
      delay(1000);
      return;
    }
    Serial.println("Reconnected to TCP server");
  }

  camera_fb_t* fb = esp_camera_fb_get();
  if (!fb) {
    Serial.println("Camera capture failed");
    delay(100);
    return;
  }

  if (sendFrame(fb)) {
    Serial.printf("Frame sent, size: %d bytes\n", fb->len);
  } else {
    Serial.println("Failed to send frame");
  }

  esp_camera_fb_return(fb);
  delay(30); // ~30 FPS
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
  config.xclk_freq_hz = 10000000;
  config.frame_size = FRAMESIZE_VGA;
  config.pixel_format = PIXFORMAT_JPEG;
  config.grab_mode = CAMERA_GRAB_WHEN_EMPTY;
  config.fb_location = CAMERA_FB_IN_PSRAM;
  config.jpeg_quality = 12;
  config.fb_count = 1;

  if (psramFound()) {
    config.jpeg_quality = 10;
    config.fb_count = 2;
    config.grab_mode = CAMERA_GRAB_LATEST;
  } else {
    config.frame_size = FRAMESIZE_SVGA;
    config.fb_location = CAMERA_FB_IN_DRAM;
  }

  esp_err_t err = esp_camera_init(&config);
  if (err != ESP_OK) {
    Serial.printf("Camera init failed with error 0x%x\n", err);
    return;
  }

  sensor_t* s = esp_camera_sensor_get();
  s->set_brightness(s, 1);
  s->set_saturation(s, 0);
}

bool sendFrame(camera_fb_t* fb) {
  if (!client.connected()) return false;

  // First send frame length (4 bytes)
  uint32_t len = fb->len;
  client.write((uint8_t*)&len, sizeof(len));

  // Then send raw frame data
  size_t sent = client.write(fb->buf, fb->len);

  return sent == fb->len;
}
