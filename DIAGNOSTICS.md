# Diagnostyka problemu z montowaniem USB_BACKUP

## Problem: "Brak kwalifikujących się dysków USB" mimo podłączonego dysku

### Kroki diagnostyczne:

1. **Uruchom aplikację z logami:**
   ```bash
   cd /home/rafal/Git/UDiskBackup/UDiskBackup
   dotnet run --verbosity normal
   ```

2. **Sprawdź wszystkie wykryte dyski (API):**
   ```bash
   # Otwórz w przeglądarce lub użyj curl:
   curl http://localhost:5101/api/debug/all-disks
   ```

3. **Sprawdź eligible USB devices:**
   ```bash
   curl http://localhost:5101/api/debug/eligible-usb
   ```

4. **Spróbuj montowanie i obserwuj logi w konsoli aplikacji**

### Co sprawdzić w wynikach:

#### W `/api/debug/all-disks`:
- Czy Twój dysk USB jest wykryty?
- Czy ma `transport: "usb"`?
- Czy partycja ma `label: "USB_BACKUP"`?
- Sprawdź wielkość liter w etykiecie!

#### Typowe problemy i rozwiązania:

1. **Etykieta w małych literach:**
   - Jeśli widzisz `"label": "usb_backup"` zamiast `"USB_BACKUP"`
   - Zmień etykietę dysku: `sudo e2label /dev/sdcX USB_BACKUP`

2. **Brak etykiety:**
   - Jeśli `"label": null`
   - Dodaj etykietę: `sudo e2label /dev/sdcX USB_BACKUP`

3. **Nieprawidłowy transport:**
   - Jeśli `transport` nie pokazuje `"usb"`
   - Sprawdź czy dysk jest podłączony przez USB, a nie SATA

4. **System plików:**
   - Upewnij się że partycja ma system plików ext4/ext3/ntfs
   - Przeformatuj jeśli potrzeba: `sudo mkfs.ext4 -L USB_BACKUP /dev/sdcX`

### Przykład prawidłowego wyniku:

```json
{
  "success": true,
  "count": 4,
  "disks": [
    {
      "path": "/dev/sdc",
      "transport": "usb",
      "vendor": "JMicron",
      "model": "Generic",
      "partitions": [
        {
          "path": "/dev/sdc1",
          "label": "USB_BACKUP",
          "fsType": "ext4",
          "mountPoint": null
        }
      ]
    }
  ]
}
```

### Jeśli nadal nie działa:

1. **Sprawdź lsblk ręcznie:**
   ```bash
   lsblk -f
   ```

2. **Sprawdź czy dysk jest wykrywany przez system:**
   ```bash
   dmesg | grep -i usb | tail -10
   ```

3. **Sprawdz czy partycja ma etykietę:**
   ```bash
   sudo blkid | grep USB_BACKUP
   ```

Po zdiagnozowaniu problemu będę mógł wprowadzić odpowiednie poprawki w kodzie.