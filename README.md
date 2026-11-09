# 📹 RPi Camera Stream

Real-time video streaming from a Raspberry Pi CSI camera via a **C# .NET 10 Blazor Web App**.

The app captures MJPEG frames from the CSI camera using `rpicam-vid` (or `libcamera-vid`), and serves them through both a Blazor Server UI and a raw MJPEG HTTP endpoint.

## Features

- **Live MJPEG streaming** via `<img>` tag (no JavaScript or WebSocket required)
- **Blazor Server UI** with dark theme, status indicator, and camera info
- **Direct MJPEG endpoint** — works in any browser, VLC, OBS, etc.
- **Snapshot endpoint** — grab a single JPEG frame via HTTP
- **Status API** — JSON endpoint for monitoring
- **Auto-reconnect** — camera process restarts automatically on failure
- **Configurable** — resolution, FPS, quality, flip via `appsettings.json`
- **systemd service** — auto-start on boot
- **Self-contained deployment** — single binary, no .NET runtime needed on Pi

## Architecture

```
┌──────────────────────────────────────────────┐
│  Raspberry Pi                                │
│                                              │
│  CSI Camera                                  │
│      │                                       │
│      ▼                                       │
│  rpicam-vid (MJPEG to stdout)                │
│      │                                       │
│      ▼                                       │
│  CameraService (parse JPEG frames)           │
│      │                                       │
│      ├──► /api/camera/stream  (MJPEG)        │
│      ├──► /api/camera/snapshot (JPEG)        │
│      └──► Blazor UI (/)                      │
│                                              │
│  Kestrel HTTP on port 5000                   │
└──────────────────────────────────────────────┘
       │
       ▼
  Browser / VLC / OBS (via LAN IP)
```

## Prerequisites

### On the Raspberry Pi:
1. **Raspberry Pi OS** (Bookworm or newer recommended)
2. **CSI camera** connected and enabled
3. **rpicam-apps** installed:
   ```bash
   sudo apt update
   sudo apt install rpicam-apps
   ```
4. Verify the camera works:
   ```bash
   rpicam-hello -t 5000
   ```

### On the build machine:
1. **.NET 10 SDK** installed

## Quick Start

### Option 1: Deploy via script (from build machine)

```bash
# Clone / copy the project
cd RpiCameraStream

# Deploy to your Pi (builds, copies, installs service)
chmod +x deploy.sh
./deploy.sh pi@192.168.1.100
```

Then open: **http://192.168.1.100:5000**

### Option 2: Build and run on the Pi directly

If you have .NET 10 SDK on the Pi:

```bash
cd RpiCameraStream/RpiCameraStream
dotnet run
```

### Option 3: Publish and copy manually

```bash
# Build on your machine
dotnet publish -c Release -r linux-arm64 --self-contained true \
    -p:PublishSingleFile=true -p:PublishTrimmed=true \
    -o ./publish

# Copy to Pi
scp -r ./publish/* pi@192.168.1.100:/opt/RpiCameraStream/

# SSH into Pi and run
ssh pi@192.168.1.100
chmod +x /opt/RpiCameraStream/RpiCameraStream
/opt/RpiCameraStream/RpiCameraStream
```

## Endpoints

| Endpoint | Description |
|---|---|
| `/` | Blazor UI with live video player |
| `/api/camera/stream` | Raw MJPEG stream (for `<img>`, VLC, OBS) |
| `/api/camera/snapshot` | Latest frame as JPEG image |
| `/api/camera/status` | Camera status as JSON |

## Configuration

Edit `appsettings.json` (or environment variables):

```json
{
  "Camera": {
    "Width": 1280,
    "Height": 720,
    "Framerate": 30,
    "Quality": 80,
    "HFlip": false,
    "VFlip": false
  }
}
```

| Setting | Default | Description |
|---|---|---|
| `Width` | 1280 | Video width in pixels |
| `Height` | 720 | Video height in pixels |
| `Framerate` | 30 | Frames per second |
| `Quality` | 80 | JPEG quality (1-100) |
| `HFlip` | false | Horizontal flip |
| `VFlip` | false | Vertical flip |

### Override via environment variables:

```bash
export Camera__Width=1920
export Camera__Height=1080
export Camera__Framerate=15
```

## Usage with VLC / OBS

### VLC
```
Media → Open Network Stream → http://<pi-ip>:5000/api/camera/stream
```

### OBS
```
Sources → + → Media Source → Uncheck "Local File"
Input: http://<pi-ip>:5000/api/camera/stream
```

### Embed in any HTML page
```html
<img src="http://<pi-ip>:5000/api/camera/stream" />
```

## systemd Service Management

```bash
# Status
sudo systemctl status rpicamerastream.service

# Restart
sudo systemctl restart rpicamerastream.service

# Stop
sudo systemctl stop rpicamerastream.service

# View logs
sudo journalctl -u rpicamerastream.service -f
```

## Troubleshooting

| Issue | Solution |
|---|---|
| "Camera is offline" | Check CSI cable, run `rpicam-hello -t 5000` |
| "Neither rpicam-vid nor libcamera-vid found" | `sudo apt install libcamera-apps` |
| Can't access by IP | Check firewall: `sudo ufw allow 5000` |
| Low FPS | Reduce resolution or quality in config |
| High CPU usage | Reduce framerate to 15 FPS, set `DOTNET_gcServer=0` |

## License


-------------------------------------------------------------------------------------------
## 1. Створення файлу служби (демона)
Відкрийте термінал на Raspberry Pi та створіть файл конфігурації з унікальною назвою (наприклад, rpi-camera.service):
```bash
sudo nano /etc/systemd/system/rpi-camera.service
```

2. Заповнення конфігурації
Вставте наступний шаблон. Обов'язково перевірте та змініть шляхи під свій реальний проєкт та ім'я користувача (якщо у вас не стандартний pi):
```bash
[Unit]
Description=Blazor Raspberry Pi Camera Service
After=network.target

[Service]
Type=simple
User=pi
WorkingDirectory=/home/pi/RpiCsiCameraApp
ExecStart=/usr/bin/dotnet /home/pi/RpiCsiCameraApp/RpiCsiCameraApp.dll
Restart=always
RestartSec=10
StandardOutput=syslog
StandardError=syslog
SyslogIdentifier=rpi-camera

[Install]
WantedBy=multi-user.target
```

WorkingDirectory — шлях до папки, де лежить ваш зібраний .NET проєкт.

ExecStart — повний шлях до dotnet та шлях до вашої зібраної .dll файлу програми.

Restart=always — говорить системі автоматично перезапускати програму, якщо вона несподівано завершиться або станеться збій.

Збережіть файл у редакторі nano: натисніть Ctrl + O, потім Enter, а для виходу — Ctrl + X.

3. Реєстрація та запуск демона
Після створення файлу потрібно повідомити систему systemd про новий сервіс та активувати його:

Оновіть конфігурацію systemd:
```bash
sudo systemctl daemon-reload
```
Увімкніть автозапуск програми при кожному вмиканні Raspberry Pi:
```bash
sudo systemctl enable rpi-camera.service
```

Запустіть демон просто зараз, не чекаючи перезавантаження:
```bash
sudo systemctl start rpi-camera.service
```

## 4. Перевірка статусу та логів
Щоб переконатися, що все працює коректно і демон активний:
```bash
sudo systemctl status rpi-camera.service
```
Якщо ви побачите зелений напис active (running), значить ваша програма успішно працює у фоновому режимі як демон!

Переглянути логи програми в реальному часі:
```bash
sudo journalctl -u rpi-camera.service -f
```

Зупинити програму:
```bash
sudo systemctl stop rpi-camera.service
```