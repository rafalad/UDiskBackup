# UDiskBackup

Lekka aplikacja .NET 8 pokazująca dyski serwera (w tym USB), z przyciskiem backupu z `/mnt/shared` na podłączony dysk USB.
- UI: proste HTML/CSS + SignalR (zdarzenia USB + logi backupu)
- Backup: `rsync` (z przenoszeniem usuniętych do `.deleted/<timestamp>`)
- Gotowa do uruchomienia lokalnie, w Dockerze i w k3s (manifest `k8s.yaml`)

## Wymagania
- .NET 8 SDK (dev), ASP.NET 8 runtime (prod)
- Linux: `util-linux` (lsblk), `rsync`
- (k3s) hostPath do: `/mnt/shared`, `/media`/`/mnt`, `/run/dbus/system_bus_socket`, `/sys`

## Uruchomienie lokalne
```bash
dotnet build
ASPNETCORE_URLS=http://127.0.0.1:5200 dotnet run --no-build

