namespace UDiskBackup;

public record UsbTarget(string MountPoint, string Device, string Label, string FsType, long FreeBytes, long TotalBytes);

public record BackupPlan(
    string Source,
    string TargetBackupDir,
    string DeletedDir,
    string RsyncArgs,
    long EstimatedBytes,
    long FreeBytes,
    bool EnoughSpace
);

public record StartBackupRequest(string TargetMount);
public record BackupStartResponse(string OperationId);
public record BackupStatus(string OperationId, string State, string Message);
