using UDiskBackup.Models;

namespace UDiskBackup.Models;

// Adaptery dla komponentów Blazor
public class DiskModel
{
    public string Path { get; set; } = "";
    public string? Type { get; set; }
    public string? Transport { get; set; }
    public string? Vendor { get; set; }
    public string? Model { get; set; }
    public string? Size { get; set; }
    public bool Rotational { get; set; }
    public long? SizeBytes { get; set; }
    public List<PartitionModel> Partitions { get; set; } = new();

    // Konwersja z DiskDevice do DiskModel
    public static DiskModel FromDiskDevice(DiskDevice device)
    {
        return new DiskModel
        {
            Path = device.Path,
            Type = device.Type,
            Transport = device.Transport,
            Vendor = device.Vendor,
            Model = device.Model,
            Size = device.Size,
            Rotational = device.Rotational,
            SizeBytes = device.SizeBytes,
            Partitions = device.Partitions.Select(PartitionModel.FromPartition).ToList()
        };
    }
}

public class PartitionModel
{
    public string Path { get; set; } = "";
    public string? Fstype { get; set; }
    public string? MountPoint { get; set; }
    public string? Size { get; set; }
    public long? SizeBytes { get; set; }
    public string? Label { get; set; }
    public long? UsedBytes { get; set; }
    public long? FreeBytes { get; set; }

    // Konwersja z Partition do PartitionModel
    public static PartitionModel FromPartition(Partition partition)
    {
        return new PartitionModel
        {
            Path = partition.Path,
            Fstype = partition.FsType,
            MountPoint = partition.MountPoint,
            Size = partition.Size,
            SizeBytes = partition.SizeBytes,
            Label = partition.Label,
            UsedBytes = partition.UsedBytes,
            FreeBytes = partition.FreeBytes
        };
    }
}

public class BackupTargetModel
{
    public string MountPoint { get; set; } = "";
    public string Device { get; set; } = "";
    public string Label { get; set; } = "";
    public string FsType { get; set; } = "";
    public long FreeBytes { get; set; }
    public long TotalBytes { get; set; }

    // Konwersja z UsbTarget do BackupTargetModel
    public static BackupTargetModel FromUsbTarget(UsbTarget target)
    {
        return new BackupTargetModel
        {
            MountPoint = target.MountPoint,
            Device = target.Device,
            Label = target.Label,
            FsType = target.FsType,
            FreeBytes = target.FreeBytes,
            TotalBytes = target.TotalBytes
        };
    }
}

public class BackupPlanModel
{
    public string Source { get; set; } = "";
    public string TargetBackupDir { get; set; } = "";
    public string DeletedDir { get; set; } = "";
    public string RsyncArgs { get; set; } = "";
    public long EstimatedBytes { get; set; }
    public long FreeBytes { get; set; }
    public bool EnoughSpace { get; set; }

    // Konwersja z BackupPlan do BackupPlanModel
    public static BackupPlanModel FromBackupPlan(BackupPlan plan)
    {
        return new BackupPlanModel
        {
            Source = plan.Source,
            TargetBackupDir = plan.TargetBackupDir,
            DeletedDir = plan.DeletedDir,
            RsyncArgs = plan.RsyncArgs,
            EstimatedBytes = plan.EstimatedBytes,
            FreeBytes = plan.FreeBytes,
            EnoughSpace = plan.EnoughSpace
        };
    }
}

public class BackupStatusModel
{
    public string OperationId { get; set; } = "";
    public string State { get; set; } = "Idle";
    public string Message { get; set; } = "Ready";

    // Konwersja z BackupStatus do BackupStatusModel
    public static BackupStatusModel FromBackupStatus(BackupStatus status)
    {
        return new BackupStatusModel
        {
            OperationId = status.OperationId,
            State = status.State,
            Message = status.Message
        };
    }
}

public class SourceStatusModel
{
    public bool IsAvailable { get; set; }
    public string Path { get; set; } = "";
    public long? UsedBytes { get; set; }
}

public class StartBackupResult
{
    public string OperationId { get; set; } = "";
}

public class StopBackupResult
{
    public string Message { get; set; } = "";
}

public class VersionInfo
{
    public string Version { get; set; } = "";
    public string BuildDate { get; set; } = "";
    public string GitCommit { get; set; } = "";
    public string GitBranch { get; set; } = "";
    public string Framework { get; set; } = "";
    public string Os { get; set; } = "";
    public string Architecture { get; set; } = "";
}