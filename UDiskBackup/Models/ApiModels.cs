using System.Text.Json.Serialization;

namespace UDiskBackup.Models;

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
    string Transport,
    string Type,
    string Vendor,
    string Model,
    string Serial,
    string Size,
    bool   Rotational,
    IReadOnlyList<PartitionDto> Partitions);

public record UsbMountRequest(string Device);
public record UsbUnmountRequest(string? Device, string? MountPoint, bool PowerOff = true);