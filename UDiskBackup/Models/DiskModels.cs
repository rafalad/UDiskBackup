namespace UDiskBackup.Models;

public record DiskDevice(
    string Path,
    string Type,
    string? Transport,
    string? Vendor,
    string? Model,
    string? Serial,
    string? Size,        // ludzki format
    bool   Rotational,
    long?  SizeBytes,    // liczbowo, przydatne do filtrów > 0
    List<Partition> Partitions
);

public record Partition(
    string Path,
    string? FsType,
    string? MountPoint,
    string? Size,        // ludzki format
    long?  SizeBytes,    // liczbowo
    string? Label,       // etykieta FS (LABEL)
    long?  UsedBytes,    // zajęte miejsce w bajtach
    long?  FreeBytes     // wolne miejsce w bajtach
);

public record DisksSummary
{
    public int TotalDisks { get; set; }
    public long TotalSizeBytes { get; set; }
    public double TotalSizeGB { get; set; }
    public long UsedSizeBytes { get; set; }
    public double UsedSizeGB { get; set; }
    public long FreeSizeBytes { get; set; }
    public double FreeSizeGB { get; set; }
}