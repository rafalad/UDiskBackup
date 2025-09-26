# Instrukcje wdrożenia poprawki do k3s

## Problem rozwiązany:
Aplikacja w kontenerze k3s działała jako `root`, ale kod próbował używać `sudo`, które nie istnieje w kontenerze.

## Zmiany w kodzie:
1. Dodano wykrywanie czy aplikacja działa jako root
2. Zmodyfikowano funkcje montowania aby używać `mount` zamiast `sudo mount` gdy jesteśmy root
3. Poprawiono tworzenie katalogów (`mkdir` vs `sudo mkdir`)

## Kroki wdrożenia:

### 1. Zbuduj nowy obraz kontenera:
```bash
# Z katalogu głównego projektu
cd /home/rafal/Git/UDiskBackup
docker build -t udiskbackup:fixed .
```

### 2. Znajdź aktualny deployment:
```bash
kubectl get deployment -n apps | grep udisk
```

### 3. Zaktualizuj obraz w deployment:
```bash
# Jeśli używasz lokalnego registry
kubectl set image deployment/udiskbackup -n apps udiskbackup=udiskbackup:fixed

# LUB edytuj deployment bezpośrednio
kubectl edit deployment udiskbackup -n apps
# I zmień `image:` na nową wersję
```

### 4. Sprawdź czy nowy pod się uruchomił:
```bash
kubectl get pods -n apps | grep udisk
kubectl logs -n apps udiskbackup-xxx-xxx  # nowy pod
```

### 5. Przetestuj montowanie:
```bash
curl -X POST "http://localhost:30004/api/usb/auto-mount-backup"
```

### Alternatywna metoda - restart z nowym kodem:

Jeśli używasz CI/CD lub automatycznego budowania:

1. **Zatwierdź zmiany:**
```bash
git add .
git commit -m "Fix container mount issues - handle root user properly"
git push
```

2. **Jeśli masz automatyczne budowanie, poczekaj na nowy obraz**

3. **Zrestartuj deployment:**
```bash
kubectl rollout restart deployment/udiskbackup -n apps
```

### Weryfikacja:

Po wdrożeniu sprawdź w logach:
```bash
kubectl logs -n apps deployment/udiskbackup
```

Powinieneś zobaczyć:
```
info: UDiskBackup.BackupService[0]
      BackupService initialized - Running as root: True
```

Oraz przy próbie montowania:
```
info: UDiskBackup.BackupService[0]
      Attempting to mount /dev/sdc1 at specific path /mnt/usb_backup (as root: True)
```

### Troubleshooting:

Jeśli nadal są problemy:

1. **Sprawdź czy kontenery mają dostęp do /dev:**
```bash
kubectl exec -n apps udiskbackup-xxx-xxx -- ls -la /dev/sdc*
```

2. **Sprawdź uprawnienia:**
```bash
kubectl exec -n apps udiskbackup-xxx-xxx -- id
```

3. **Przetestuj montowanie ręcznie:**
```bash
kubectl exec -n apps udiskbackup-xxx-xxx -- mkdir -p /mnt/test
kubectl exec -n apps udiskbackup-xxx-xxx -- mount /dev/sdc1 /mnt/test
```

Jeśli ręczne montowanie działa, ale API nie - sprawdź logi aplikacji.