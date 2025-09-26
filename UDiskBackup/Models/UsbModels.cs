namespace UDiskBackup.Models;

public record UsbCandidate(
    string DiskPath, 
    string? Vendor, 
    string? Model, 
    string? Serial, 
    string Device, 
    string? MountPoint, 
    string? FsType
);

public record MountResult(
    bool Success, 
    string Device, 
    string? MountPoint, 
    string Output
);

public record UnmountResult(
    bool Success, 
    string Device, 
    string? MountPoint, 
    string Output
);

public record ParsedRsyncStats
{
    public int? NumberOfFiles { get; set; }
    public int? NumberOfDirs { get; set; }
    public int? NumberOfTransferredFiles { get; set; }
    public int? NumberOfDeletedFiles { get; set; }
    public long? TotalFileSize { get; set; }
    public long? TotalTransferredFileSize { get; set; }
    public long? LiteralData { get; set; }
    public long? MatchedData { get; set; }
    public long? FileListSize { get; set; }
    public long? TotalBytesSent { get; set; }
    public long? TotalBytesReceived { get; set; }
}