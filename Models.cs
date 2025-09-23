using System.Text.Json.Serialization;

namespace UDiskBackup;

public record PartitionDto(
    string Name,
    string Path,
    string? FsType,
    string? MountPoint,
    string? Uuid,
    string Size);

public record DiskDto(
    string Name,
    string Path,
    string Transport,     // usb/sata/nvme/unknown
    string Type,          // hdd/ssd/nvme/rom
    string Vendor,
    string Model,
    string Serial,
    string Size,
    bool   Rotational,
    IReadOnlyList<PartitionDto> Partitions);
