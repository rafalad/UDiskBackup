# Rozwiązywanie problemów z montowaniem dysku USB_BACKUP

## Problem: Nie można zamontować dysku z etykietą USB_BACKUP

### Zmiany wprowadzone w celu rozwiązania problemu:

1. **Ulepszone logowanie błędów**
   - Dodano szczegółowe logi dla każdego kroku montowania
   - Logi są dostępne w konsoli aplikacji (.NET Core logs)

2. **Automatyczne wykrywanie i montowanie**
   - Nowy endpoint API: `POST /api/usb/auto-mount-backup`
   - Automatyczne wyszukiwanie dysku z etykietą `USB_BACKUP`
   - Preferowane montowanie na `/mnt/usb_backup`

3. **Alternatywne metody montowania**
   - Jeśli `udisksctl` zawiedzie, automatycznie próbuje `sudo mount`
   - Fallback na systemowe narzędzia montowania
   - Automatyczne tworzenie katalogów montowania

4. **Inteligentne odmontowywanie**
   - Automatyczne wykrywanie zamontowanego dysku USB_BACKUP
   - Jeśli `udisksctl unmount` zawiedzie, próbuje `sudo umount`
   - Alternatywne metody power-off (`udisksctl power-off` lub `eject`)
   - Obsługa dysków zamontowanych przez różne metody

5. **Ulepszone sprawdzenie stanu**
   - Sprawdzanie czy dysk już jest zamontowany
   - Weryfikacja istnienia urządzenia przed montowaniem
   - Lepsze raportowanie błędów

### Jak używać:

1. **Przez interfejs webowy:**
   - **Montowanie:** Kliknij przycisk "Zamontuj USB_BACKUP"
   - **Odmontowanie:** Kliknij przycisk "Odmontuj i wysuń" 
   - Aplikacja automatycznie znajdzie i obsłuży dysk USB_BACKUP

2. **Przez API:**
   ```bash
   # Montowanie
   curl -X POST http://localhost:5101/api/usb/auto-mount-backup
   
   # Odmontowanie (przez device)
   curl -X POST http://localhost:5101/api/usb/unmount \
     -H "Content-Type: application/json" \
     -d '{"device":"/dev/sdc1","powerOff":true}'
     
   # Odmontowanie (przez mount point)
   curl -X POST http://localhost:5101/api/usb/unmount \
     -H "Content-Type: application/json" \
     -d '{"mountPoint":"/mnt/usb_backup","powerOff":true}'
   ```

### Możliwe problemy i rozwiązania:

1. **Brak uprawnień:**
   - Upewnij się że użytkownik ma dostęp do `udisksctl`
   - Może być wymagane dodanie użytkownika do grupy `disk` lub `storage`
   ```bash
   sudo usermod -a -G disk $USER
   ```

2. **Katalog montowania już zajęty:**
   - Aplikacja automatycznie spróbuje alternatywne ścieżki
   - Ręcznie można odmontować: `sudo umount /mnt/usb_backup`

3. **Dysk nie może być odmontowany:**
   - Sprawdź czy żadne aplikacje nie używają plików na dysku: `lsof +D /mnt/usb_backup`
   - Aplikacja automatycznie próbuje różne metody odmontowania
   - Ręczny force unmount: `sudo umount -f /mnt/usb_backup`

4. **Dysk nie wykryty:**
   - Sprawdź czy dysk ma poprawną etykietę: `lsblk -f`
   - Odłącz i podłącz ponownie dysk USB
   - Sprawdź logi systemowe: `dmesg | tail`

### Diagnostyka:

1. **Sprawdź dostępne dyski:**
   - W interfejsie: sekcja "Wszystkie dyski"
   - Przez API: `GET /api/disks`

2. **Sprawdź logi aplikacji:**
   - Uruchom aplikację z konsoli: `dotnet run`
   - Logi pokażą szczegółowe informacje o próbach montowania

3. **Sprawdź uprawnienia:**
   ```bash
   # Test udisksctl
   udisksctl status
   
   # Test odmontowania ręcznego
   sudo umount /mnt/usb_backup
   # lub przez device
   sudo umount /dev/sdcX
   
   # Test eject
   eject /dev/sdcX
   ```

### Nowe funkcje:

- **Automatyczne wykrywanie:** Aplikacja sama znajdzie dysk z etykietą USB_BACKUP
- **Inteligentne montowanie:** Próbuje różne metody montowania automatycznie
- **Lepsze komunikaty błędów:** Szczegółowe informacje o tym co poszło nie tak
- **Fallback montowania:** Jeśli jedna metoda zawiedzie, próbuje inne

### Testowanie:

Po wprowadzeniu zmian, przetestuj:
1. Uruchom aplikację: `dotnet run`
2. Otwórz http://localhost:5101
3. Kliknij "Zamontuj USB_BACKUP"
4. Sprawdź logi w konsoli aplikacji