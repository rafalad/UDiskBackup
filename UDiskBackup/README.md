# UDiskBackup

A lightweight .NET 8 application for monitoring server disks (including USB drives) with automated backup functionality from `/mnt/shared` to connected USB drives.

## Features

- **Real-time disk monitoring**: Live detection of USB devices and disk changes
- **Web-based UI**: Simple HTML/CSS interface with SignalR for real-time updates
- **Automated backup**: `rsync`-based backup with deleted files moved to `.deleted/<timestamp>`
- **Multiple deployment options**: Local, Docker, and Kubernetes (k3s) ready
- **Clean architecture**: Organized following .NET best practices

## Project Structure

```
├── Controllers/          # API endpoints (MVC pattern)
│   ├── BackupController.cs
│   ├── DisksController.cs
│   ├── UsbController.cs
│   └── DebugController.cs
├── Services/            # Business logic and system integrations
│   ├── BackupService.cs
│   ├── DiskInfoService.cs
│   ├── SourceService.cs
│   └── UDisksMonitor.cs
├── Hubs/               # SignalR communication hubs
│   ├── BackupHub.cs
│   └── DiskHub.cs
├── Models/             # Data models and DTOs
│   ├── ApiModels.cs
│   ├── BackupModels.cs
│   ├── DiskModels.cs
│   └── UsbModels.cs
└── wwwroot/            # Static web assets
    ├── index.html
    └── app.css
```

## Requirements

- **.NET 8 SDK** (development) / **ASP.NET 8 Runtime** (production)
- **Linux utilities**: `util-linux` (lsblk), `rsync`
- **For Kubernetes**: hostPath access to `/mnt/shared`, `/media` or `/mnt`, `/run/dbus/system_bus_socket`, `/sys`

## Local Development

```bash
dotnet build
ASPNETCORE_URLS=http://127.0.0.1:5200 dotnet run --no-build
```

Open browser at: http://127.0.0.1:5200

## Docker Deployment

```bash
# Build image
docker build -t udiskbackup .

# Run container (requires privileged mode for USB access)
docker run -d \
  --name udiskbackup \
  --privileged \
  -p 5200:5200 \
  -v /mnt/shared:/mnt/shared:ro \
  -v /media:/media \
  -v /run/dbus/system_bus_socket:/run/dbus/system_bus_socket \
  -v /sys:/sys:ro \
  udiskbackup
```

## Kubernetes Deployment

```bash
kubectl apply -f k8s.yaml
```

The application will be available on port 30200.

## API Endpoints

### Disks & USB Management
- `GET /api/disks` - List all detected disks and partitions
- `POST /api/usb/mount` - Mount USB device
- `POST /api/usb/unmount` - Unmount and optionally power off USB device

### Backup Operations
- `GET /api/backup/targets` - List available backup targets
- `POST /api/backup/start` - Start backup operation
- `POST /api/backup/stop` - Cancel running backup
- `GET /api/backup/status` - Get current backup status
- `GET /api/backup/history` - Get backup operation history

### Debug & Diagnostics
- `GET /api/debug/info` - System diagnostics and configuration
- `GET /api/debug/processes` - List running processes
- `GET /api/debug/mounts` - Show mount points

## Real-time Updates

The application uses SignalR for real-time communication:
- **BackupHub**: Backup progress and status updates
- **DiskHub**: USB device connect/disconnect events

## Architecture

The application follows clean architecture principles:

- **Controllers**: Handle HTTP requests and responses
- **Services**: Contain business logic and system integrations
- **Hubs**: Manage real-time SignalR connections
- **Models**: Define data structures and DTOs

All services are registered with dependency injection in `Program.cs` for loose coupling and testability.

