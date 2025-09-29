namespace UDiskBackup.Models;

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

public record BackupRunSummary(
    string OperationId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    TimeSpan Duration,
    string Source,
    string Target,
    string DeletedDir,
    int ExitCode,
    bool Success,
    long? FreeBytesBefore,
    long? FreeBytesAfter,
    int? NumberOfFiles,
    int? NumberOfDirs,
    int? NumberOfTransferredFiles,
    int? NumberOfDeletedFiles,
    long? TotalFileSize,
    long? TotalTransferredFileSize,
    long? LiteralData,
    long? MatchedData,
    long? FileListSize,
    long? TotalBytesSent,
    long? TotalBytesReceived
);

public record BackupHistoryItem(
    string OperationId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    bool Success,
    TimeSpan Duration,
    string TargetMount,
    string? TargetLabel,
    string SummaryJsonPath,
    string? SummaryTxtPath,
    int? NumberOfTransferredFiles,
    long? TotalTransferredFileSize,
    string? BackupType
);