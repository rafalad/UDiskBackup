# UDiskBackup - Project Structure

## Overview
UDiskBackup is a .NET 8 web application for managing USB backups with real-time monitoring and containerized deployment support.

## Project Structure

```
UDiskBackup/
├── Controllers/          # API Controllers (MVC pattern)
│   ├── BackupController.cs      # Backup operations (/api/backup/*)
│   ├── DisksController.cs       # Disk information (/api/disks/*)
│   ├── UsbController.cs         # USB operations (/api/usb/*)
│   └── DebugController.cs       # Debug endpoints (/api/debug/*)
├── Services/             # Business Logic Services
│   ├── BackupService.cs         # Core backup operations
│   ├── DiskInfoService.cs       # Disk detection and info
│   ├── SourceService.cs         # Source directory management
│   └── UDisksMonitor.cs         # Real-time USB monitoring
├── Hubs/                 # SignalR Hubs
│   ├── BackupHub.cs             # Real-time backup status
│   └── DiskHub.cs               # Real-time disk status
├── Models/               # Data Models
│   ├── ApiModels.cs             # API request/response models
│   ├── BackupModels.cs          # Backup-related models
│   ├── DiskModels.cs            # Disk information models
│   └── UsbModels.cs             # USB-specific models
├── wwwroot/              # Static Web Files
│   ├── index.html               # Main UI
│   └── app.css                  # Styles
├── Properties/
│   └── launchSettings.json
├── Program.cs            # Application entry point
├── Dockerfile            # Container configuration
└── k8s.yaml             # Kubernetes deployment
```

## Architecture

### Controllers (MVC Pattern)
- **BackupController**: Handles backup planning, starting, stopping, history
- **DisksController**: Provides disk information and summaries
- **UsbController**: Manages USB mounting/unmounting operations
- **DebugController**: Development and diagnostic endpoints

### Services (Business Logic)
- **BackupService**: Core backup functionality using rsync
- **DiskInfoService**: Linux disk detection via lsblk and filesystem APIs
- **SourceService**: Source directory validation and status
- **UDisksMonitor**: Real-time USB device monitoring via D-Bus

### Models (Data Transfer Objects)
- **ApiModels**: HTTP request/response objects
- **BackupModels**: Backup plans, status, history, summaries
- **DiskModels**: Disk devices, partitions, summaries
- **UsbModels**: USB-specific operations and results

### Real-time Communication
- **SignalR Hubs**: Provide real-time updates for backup progress and USB events
- **WebSocket**: Enables live log streaming and status updates

## API Endpoints

### Backup Operations
- `GET /api/backup/plan?targetMount={mount}` - Plan backup space requirements
- `POST /api/backup/start` - Start backup operation
- `POST /api/backup/stop` - Cancel running backup
- `GET /api/backup/history` - Get backup history
- `GET /api/backup/current-log` - Download current backup log

### USB Management  
- `GET /api/usb/eligible` - Get USB devices with USB_BACKUP label
- `POST /api/usb/mount` - Mount USB device
- `POST /api/usb/auto-mount-backup` - Auto-mount USB_BACKUP device
- `POST /api/usb/unmount` - Unmount USB device

### Disk Information
- `GET /api/disks/summary` - Get disk space summary across all disks
- `GET /api/disks/all` - Get all detected disks
- `GET /api/disks/usb` - Get USB targets for backup

## Key Features

### Container Support
- **Root Detection**: Automatically detects container environment
- **Mount Strategies**: Falls back from udisksctl to system mount commands
- **Privileged Execution**: Supports both containerized and host deployments

### Backup Management
- **Space Planning**: Pre-validates available space before backup
- **Real-time Progress**: Live updates via SignalR
- **Cancellation**: Graceful backup termination with cleanup
- **History Tracking**: JSON and human-readable backup summaries

### USB Detection
- **Label-based**: Automatically finds USB_BACKUP labeled devices
- **Case-insensitive**: Robust label matching
- **Auto-mounting**: Seamless USB device preparation

### Monitoring
- **Disk Summaries**: Total/used/free space across all disks
- **Real-time Events**: USB insertion/removal notifications
- **Progress Tracking**: Live backup progress with rsync stats

## Development

### Prerequisites
- .NET 8 SDK
- Linux environment (tested on Ubuntu)
- Docker (for containerization)
- Kubernetes (for deployment)

### Building
```bash
dotnet build
dotnet run
```

### Docker
```bash
docker build -t udiskbackup:latest .
docker run --privileged -p 5000:5000 udiskbackup:latest
```

### Kubernetes
```bash
kubectl apply -f k8s.yaml
```

## Best Practices Applied

1. **Separation of Concerns**: Controllers, Services, Models in separate folders
2. **Dependency Injection**: Services registered and injected properly
3. **Namespace Organization**: Consistent naming with UDiskBackup.* pattern
4. **API Standards**: RESTful endpoints with proper HTTP verbs
5. **Error Handling**: Consistent exception handling across controllers
6. **Real-time Updates**: SignalR for responsive UI experience
7. **Container Ready**: Proper containerization with privilege handling