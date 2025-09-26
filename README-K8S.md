# UDiskBackup - USB Backup System

🚀 **Aplikacja do automatycznego tworzenia przyrostowych kopii zapasowych na dyskach USB**

Zbudowana w .NET 8 z wykorzystaniem rsync i hard-link deduplication dla oszczędności miejsca.

## ⚡ Szybkie uruchomienie na k3s (zotac@ubuntu)

```bash
# Wdrożenie zdalne na k3s w namespace 'apps'
./k8s-deploy.sh

# Dostęp do aplikacji
# http://udiskbackup.local
```

## 🎯 Funkcjonalności

- ✅ **Przyrostowe kopie zapasowe** z deduplikacją hard-link
- ✅ **Automatyczne wykrywanie USB** z labelami `USB_BACKUP`
- ✅ **Real-time monitoring** via SignalR WebSocket
- ✅ **Historia backup'ów** z oznaczeniami Full/Incremental
- ✅ **Rozszerzone logowanie** (JSON + tekstowe + extended metadata)
- ✅ **Informacje o systemie** i wersji Git
- ✅ **Web UI** z responsywnym designem

## 🔧 Konfiguracja dla k3s

### Namespace i Deployment
- **Namespace**: `apps`
- **Node**: `zotac@ubuntu` (nodeSelector)
- **Privileges**: Wymagane dla operacji USB
- **Volumes**: Host paths dla `/mnt/shared`, `/media`, `/sys`, D-Bus

### Źródło danych
```json
{
  "SourcePath": "/mnt/shared"  // Konfiguracja w ConfigMap
}
```

### Dyski docelowe
- **Label**: `USB_BACKUP`
- **Format**: ext4/ntfs/exfat
- **Miejsce**: wystarczające dla przyrostowych kopii

## 📊 Endpointy API

| Endpoint | Opis |
|----------|------|
| `GET /api/disks/all` | Lista wszystkich dysków z użyciem |
| `GET /api/usb/targets` | Kwalifikujące się dyski USB |
| `GET /api/source/status` | Status katalogu źródłowego |
| `POST /api/backup/start` | Rozpoczęcie backup'u |
| `GET /api/backup/history` | Historia z typami backup'ów |
| `GET /api/debug/version` | Informacje o wersji i Git |

## 🏗️ Architektura w k3s

```yaml
# ConfigMap -> konfiguracja aplikacji
# Deployment -> pod z privileged mode  
# Service -> dostęp wewnętrzny
# Ingress -> dostęp zewnętrzny (Traefik)
```

### Wolumeny Host Path
```
Host (zotac@ubuntu)     →    Kontener
/mnt/shared             →    /mnt/shared (źródło - ro)
/media,/mnt,/run/media  →    punkty montowania USB
/sys                    →    /sys (sprzęt - ro)
/run/dbus/system_bus... →    D-Bus socket (ro)
```

## 🔄 Zarządzanie w k3s

```bash
# Status
sudo k3s kubectl get all -n apps -l app=udiskbackup

# Logi
sudo k3s kubectl logs -n apps -l app=udiskbackup -f

# Restart
sudo k3s kubectl rollout restart deployment/udiskbackup -n apps

# Ponowne wdrożenie
./k8s-deploy.sh
```

## 📋 Wymagania

- **k3s** na zotac@ubuntu
- **Docker** do budowania obrazów
- **udisksctl** i D-Bus dla operacji USB
- **rsync** do backup'ów
- **/mnt/shared** jako źródło danych
- **Dyski USB** z labelami `USB_BACKUP`

## 🐛 Diagnostyka

### Sprawdzenie stanu w k3s
```bash
# Pod status
sudo k3s kubectl describe pod -n apps -l app=udiskbackup

# Dostęp do kontenera
sudo k3s kubectl exec -n apps -it deployment/udiskbackup -- bash

# Test źródła i USB w kontenerze
ls -la /mnt/shared
lsblk
udisksctl status
```

### Problemy z dyskami
```bash
# Na hoście zotac@ubuntu sprawdź:
lsblk -f
sudo blkid | grep USB_BACKUP
systemctl status udisks2
```

## 📈 Struktura backup'ów

```
/target/UDiskBackup/
├── backups/
│   ├── 2025-09-26_14-30-00_abc123/  ← Full backup
│   ├── 2025-09-26_15-30-00_def456/  ← Incremental (hard-links)
│   └── current → 2025-09-26_15-30-00_def456/
└── logs/
    ├── 2025-09-26_14-30-00_abc123.json
    ├── 2025-09-26_14-30-00_abc123.txt  
    └── 2025-09-26_14-30-00_abc123_extended.json
```

## 🔗 Pliki wdrożenia

- **k8s.yaml** - Definicja Kubernetes (ConfigMap, Deployment, Service, Ingress)
- **k8s-deploy.sh** - Skrypt wdrażający na zdalne k3s
- **Dockerfile** - Multi-stage build z .NET 8
- **DEPLOYMENT_K8S.md** - Szczegółowe instrukcje k3s

## 💡 Wskazówki k3s

1. **Traefik Ingress**: Domyślny w k3s, skonfigurowany dla `udiskbackup.local`
2. **Privileged Mode**: Wymagany dla dostępu do USB i D-Bus
3. **Node Selector**: Zapewnia uruchomienie na właściwym węźle
4. **Health Checks**: Readiness/Liveness na `/api/disks`

---

**🎯 Cel**: Niezawodny system backup'ów USB w środowisku Kubernetes z deduplikacją i monitoringiem real-time.