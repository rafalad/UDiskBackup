# UDiskBackup - Kubernetes (k3s) Deployment Guide

## Przewodnik wdrożenia w k3s na systemie zotac@ubuntu

### 📋 Wymagania wstępne

1. **k3s zainstalowany na zotac@ubuntu**
   ```bash
   # Na serwerze zotac@ubuntu:
   curl -sfL https://get.k3s.io | sh -
   sudo systemctl enable k3s
   sudo systemctl start k3s
   ```

2. **Docker zainstalowany**
   ```bash
   sudo apt update
   sudo apt install docker.io
   sudo systemctl enable docker
   sudo usermod -aG docker zotac
   ```

3. **kubectl skonfigurowany** (opcjonalnie dla zarządzania zdalnego)
   ```bash
   # Na maszynie deweloperskiej:
   scp zotac@ubuntu:/etc/rancher/k3s/k3s.yaml ~/.kube/config-zotac
   # Edytuj plik i zmień server: https://127.0.0.1:6443 na https://IP_ZOTAC:6443
   export KUBECONFIG=~/.kube/config-zotac
   ```

### 🚀 Wdrożenie

#### Opcja 1: Zdalne wdrożenie (zalecane)
```bash
# Z maszyny deweloperskiej:
./k8s-deploy.sh

# Skrypt automatycznie:
# 1. Skopiuje pliki na zotac@ubuntu
# 2. Zbuduje obraz Docker
# 3. Wdroży w namespace 'apps'
# 4. Sprawdzi status
```

#### Opcja 2: Lokalne wdrożenie na k3s
```bash
# Jeśli masz kubectl skonfigurowany lokalnie:
./k8s-deploy.sh local
```

#### Opcja 3: Manualne wdrożenie
```bash
# Na serwerze zotac@ubuntu:
cd /path/to/udiskbackup
sudo docker build -t udiskbackup:latest UDiskBackup/
sudo k3s kubectl create namespace apps
sudo k3s kubectl apply -f UDiskBackup/k8s.yaml
```

### 🔧 Konfiguracja

#### ConfigMap z ustawieniami
Aplikacja używa ConfigMap do konfiguracji:
```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: udiskbackup-config
  namespace: apps
data:
  appsettings.Production.json: |
    {
      "SourcePath": "/mnt/shared"
    }
```

#### Wolumeny Host Path
Aplikacja potrzebuje dostępu do:
- `/mnt/shared` - źródło danych
- `/media`, `/mnt`, `/run/media` - punkty montowania USB
- `/sys` - informacje o systemie
- `/run/dbus/system_bus_socket` - D-Bus dla UDisks2
- `/run/udev` - metadata urządzeń

### 📊 Zarządzanie

#### Sprawdzanie statusu
```bash
# Pods
sudo k3s kubectl get pods -n apps -l app=udiskbackup

# Service
sudo k3s kubectl get service -n apps udiskbackup

# Ingress
sudo k3s kubectl get ingress -n apps udiskbackup

# Logi
sudo k3s kubectl logs -n apps -l app=udiskbackup -f
```

#### Zarządzanie aplikacją
```bash
# Restart
sudo k3s kubectl rollout restart deployment/udiskbackup -n apps

# Skalowanie
sudo k3s kubectl scale deployment/udiskbackup --replicas=1 -n apps

# Usunięcie
sudo k3s kubectl delete -f UDiskBackup/k8s.yaml

# Ponowne wdrożenie
sudo k3s kubectl apply -f UDiskBackup/k8s.yaml
```

### 🌐 Dostęp do aplikacji

1. **Przez Ingress**: http://udiskbackup.local
   - Dodaj do `/etc/hosts`: `IP_ZOTAC udiskbackup.local`

2. **Przez Port Forward**:
   ```bash
   sudo k3s kubectl port-forward -n apps service/udiskbackup 8080:80
   # Dostęp: http://localhost:8080
   ```

3. **Przez NodePort** (modyfikuj Service w k8s.yaml):
   ```yaml
   spec:
     type: NodePort
     ports:
     - nodePort: 30080
   ```

### 🐛 Rozwiązywanie problemów

#### Sprawdzenie czy pod ma dostęp do systemu
```bash
sudo k3s kubectl exec -n apps -it deployment/udiskbackup -- ls -la /mnt/shared
sudo k3s kubectl exec -n apps -it deployment/udiskbackup -- lsblk
sudo k3s kubectl exec -n apps -it deployment/udiskbackup -- which udisksctl
```

#### Problemy z uprawnieniami
```bash
# Pod działa z privileged: true i runAsUser: 0 (root)
# Sprawdź czy kontener ma dostęp do D-Bus:
sudo k3s kubectl exec -n apps -it deployment/udiskbackup -- ls -la /run/dbus/system_bus_socket
```

#### Restart całego klastra
```bash
sudo systemctl restart k3s
```

### 📁 Struktura wolumenów

```
Kontener                    Host (zotac@ubuntu)
/mnt/shared          ->     /mnt/shared
/media               ->     /media  
/mnt                 ->     /mnt
/run/media           ->     /run/media
/sys                 ->     /sys (readonly)
/run/dbus/system_bus_socket -> /run/dbus/system_bus_socket (readonly)
/run/udev            ->     /run/udev (readonly)
```

### 🔒 Bezpieczeństwo

⚠️ **Uwaga**: Aplikacja działa w trybie privileged z dostępem root - przeznaczona do użytku w środowisku domowym/testowym.

Dla produkcji rozważ:
- Ograniczenie uprawnień kontenera
- Użycie SecurityContext z określonymi capabilities
- Skanowanie obrazów na podatności
- Network policies

### 📈 Monitoring

Aplikacja udostępnia endpointy health check:
- Readiness: `/api/disks`  
- Liveness: `/api/disks`
- Metrics: `/api/debug/version`

### 🔄 Aktualizacje

```bash
# Aktualizacja kodu i redeploy
./k8s-deploy.sh

# Lub manualne ponowne budowanie
sudo docker build -t udiskbackup:latest UDiskBackup/
sudo k3s kubectl rollout restart deployment/udiskbackup -n apps
```