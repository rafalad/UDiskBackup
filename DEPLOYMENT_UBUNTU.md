# Instrukcje wdrożenia dla zotac@ubuntu

## Wymagania systemowe

### 1. Zainstaluj .NET 8 Runtime na Ubuntu:
```bash
# Dodaj repozytorium Microsoft
wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
rm packages-microsoft-prod.deb

# Zainstaluj .NET 8 Runtime
sudo apt update
sudo apt install -y aspnetcore-runtime-8.0
```

### 2. Zainstaluj dodatkowe narzędzia:
```bash
sudo apt install -y rsync udisks2 curl
```

## Wdrożenie aplikacji

### 1. Skopiuj pliki na serwer:
```bash
# Na lokalnej maszynie (rafal@ThinkPad)
scp -r /home/rafal/Git/UDiskBackup zotac@ubuntu:/home/zotac/

# LUB użyj git clone na serwerze
ssh zotac@ubuntu
cd /home/zotac
git clone https://github.com/rafalad/UDiskBackup.git
```

### 2. Zbuduj aplikację:
```bash
cd /home/zotac/UDiskBackup
./deploy.sh build
```

### 3. Uruchom aplikację:
```bash
./deploy.sh run
```

## Instalacja jako service systemd (opcjonalne)

### 1. Skopiuj plik service:
```bash
sudo cp udiskbackup.service /etc/systemd/system/
sudo systemctl daemon-reload
```

### 2. Włącz i uruchom service:
```bash
sudo systemctl enable udiskbackup
sudo systemctl start udiskbackup
```

### 3. Sprawdź status:
```bash
sudo systemctl status udiskbackup
./deploy.sh status
```

## Konfiguracja

### Ścieżka źródła:
- **Produkcja:** `/mnt/shared` (skonfigurowane w appsettings.json)
- **Port:** `5101`
- **URL:** `http://zotac-ip:5101`

### Dostęp do aplikacji:
```bash
# Lokalnie na zotac@ubuntu:
curl http://localhost:5101

# Zdalnie z innej maszyny:
curl http://IP-ZOTAC:5101
```

## Troubleshooting

### Sprawdź logi:
```bash
./deploy.sh logs
# lub
journalctl -u udiskbackup -f
```

### Sprawdź czy źródło jest dostępne:
```bash
ls -la /mnt/shared/
curl http://localhost:5101/api/source/status
```

### Sprawdź dyski USB:
```bash
curl http://localhost:5101/api/usb/eligible
curl http://localhost:5101/api/debug/eligible-usb
```

## Zarządzanie aplikacją

```bash
./deploy.sh status    # Sprawdź status
./deploy.sh stop      # Zatrzymaj
./deploy.sh restart   # Restart
./deploy.sh logs      # Pokaż logi
```