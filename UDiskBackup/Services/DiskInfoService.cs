using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using UDiskBackup.Models;

namespace UDiskBackup.Services;

public class DiskInfoService
{
    public async Task<IReadOnlyList<DiskDevice>> GetDisksAsync()
    {
        var cols = "NAME,TYPE,SIZE,ROTA,TRAN,VENDOR,MODEL,SERIAL,PATH,FSTYPE,MOUNTPOINT,MOUNTPOINTS,LABEL";
        var (exit, stdout, stderr) = await Run("lsblk", $"-J -b -o {cols}");
        if (exit == 0 && !string.IsNullOrWhiteSpace(stdout))
        {
            var parsed = ParseLsblk(stdout);
            if (parsed.Count > 0)
                return await FixTransportsAsync(parsed); // udev/sysfs fallback dla USB
        }

        var (exit2, stdout2, _) = await Run("lsblk", "-J -b");
        if (exit2 == 0 && !string.IsNullOrWhiteSpace(stdout2))
        {
            var parsed = ParseLsblk(stdout2);
            if (parsed.Count > 0)
                return await FixTransportsAsync(parsed);
        }

        var sysParsed = EnumerateFromSysfs();
        return await FixTransportsAsync(sysParsed);
    }

    public async Task<DisksSummary> GetDisksSummaryAsync()
    {
        var disks = await GetDisksAsync();
        
        long totalBytes = 0;
        long totalUsedBytes = 0;
        long totalFreeBytes = 0;
        int diskCount = 0;
        
        foreach (var disk in disks)
        {
            if (disk.SizeBytes.HasValue && disk.SizeBytes > 0)
            {
                diskCount++;
                totalBytes += disk.SizeBytes.Value;
                
                foreach (var partition in disk.Partitions)
                {
                    if (!string.IsNullOrEmpty(partition.MountPoint))
                    {
                        try
                        {
                            var driveInfo = new DriveInfo(partition.MountPoint);
                            if (driveInfo.IsReady)
                            {
                                totalFreeBytes += driveInfo.AvailableFreeSpace;
                                totalUsedBytes += (driveInfo.TotalSize - driveInfo.AvailableFreeSpace);
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }
        
        return new DisksSummary
        {
            TotalDisks = diskCount,
            TotalSizeBytes = totalBytes,
            TotalSizeGB = Math.Round(totalBytes / (1024.0 * 1024.0 * 1024.0), 2),
            UsedSizeBytes = totalUsedBytes,
            UsedSizeGB = Math.Round(totalUsedBytes / (1024.0 * 1024.0 * 1024.0), 2),
            FreeSizeBytes = totalFreeBytes,
            FreeSizeGB = Math.Round(totalFreeBytes / (1024.0 * 1024.0 * 1024.0), 2)
        };
    }


    private static List<DiskDevice> ParseLsblk(string json)
    {
        var result = new List<DiskDevice>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("blockdevices", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var node in arr.EnumerateArray())
        {
            var type = GetString(node, "type")?.ToLowerInvariant();
            if (type is not ("disk" or "rom")) continue;

            var dev = BuildFromNode(node);
            if (dev != null) result.Add(dev);
        }
        return result;
    }

    private static DiskDevice? BuildFromNode(JsonElement node)
    {
        var name = GetString(node, "name");
        if (string.IsNullOrEmpty(name)) return null;

        var path = GetString(node, "path") ?? ("/dev/" + name);
        var sizeBytes = GetLong(node, "size");
        var sizeHuman = sizeBytes.HasValue ? Human(sizeBytes.Value) : null;

        var rotational = GetInt(node, "rota") == 1;
        var tran = GetString(node, "tran");
        var vendor = OrNull(GetString(node, "vendor"));
        var model  = OrNull(GetString(node, "model"));
        var serial = OrNull(GetString(node, "serial"));

        var parts = new List<Partition>();
        if (node.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in ch.EnumerateArray())
            {
                var pType = GetString(p, "type");
                if (pType is not ("part" or "crypt" or "raid")) continue;

                var pName = GetString(p, "name");
                var pPath = GetString(p, "path") ?? ("/dev/" + pName);
                var pSize = GetLong(p, "size");
                var pSizeHuman = pSize.HasValue ? Human(pSize.Value) : null;

                var mp = GetString(p, "mountpoint");
                if (string.IsNullOrWhiteSpace(mp) && p.TryGetProperty("mountpoints", out var mps) && mps.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in mps.EnumerateArray())
                    {
                        if (m.ValueKind == JsonValueKind.String) { mp = m.GetString(); break; }
                    }
                }

                var fstype = GetString(p, "fstype");
                var label  = GetString(p, "label");
                var (usedBytes, freeBytes) = GetDiskUsage(mp);

                parts.Add(new Partition(
                    Path: pPath!,
                    FsType: fstype,
                    MountPoint: string.IsNullOrWhiteSpace(mp) ? null : mp,
                    Size: pSizeHuman,
                    SizeBytes: pSize,
                    Label: string.IsNullOrWhiteSpace(label) ? null : label,
                    UsedBytes: usedBytes,
                    FreeBytes: freeBytes
                ));
            }
        }

        return new DiskDevice(
            Path: path,
            Type: GetString(node, "type") ?? "disk",
            Transport: string.IsNullOrWhiteSpace(tran) ? null : tran,
            Vendor: vendor,
            Model: model,
            Serial: serial,
            Size: sizeHuman,
            Rotational: rotational,
            SizeBytes: sizeBytes,
            Partitions: parts
        );
    }


    private static List<DiskDevice> EnumerateFromSysfs()
    {
        var res = new List<DiskDevice>();
        var sysBlock = "/sys/block";
        if (!Directory.Exists(sysBlock)) return res;

        var mounts = ReadMounts();
        var labelMap = ReadLabelsFromByLabel();

        foreach (var devDir in Directory.EnumerateDirectories(sysBlock))
        {
            var name = Path.GetFileName(devDir);
            if (!Regex.IsMatch(name, @"^(sd[a-z]+|nvme\d+n\d+|vd[a-z]+|hd[a-z]+|mmcblk\d+)$")) continue;

            string devPath = "/dev/" + name;
            long? sectors = ReadLongFile(Path.Combine(devDir, "size"));
            long? bytes = sectors.HasValue ? sectors.Value * 512L : null;
            var rotational = (ReadLongFile(Path.Combine(devDir, "queue", "rotational")) ?? 0) == 1;

            string? vendor = ReadString(Path.Combine(devDir, "device", "vendor"));
            string? model  = ReadString(Path.Combine(devDir, "device", "model"));
            string? serial = ReadString(Path.Combine(devDir, "device", "serial"));

            string? transport = null;
            if (name.StartsWith("nvme", StringComparison.OrdinalIgnoreCase)) transport = "nvme";
            var real = TryReadLink(devDir);
            if (!string.IsNullOrEmpty(real) && real!.Contains("/usb", StringComparison.OrdinalIgnoreCase)) transport = "usb";

            var parts = new List<Partition>();
            foreach (var child in Directory.EnumerateFileSystemEntries(devDir))
            {
                var bn = Path.GetFileName(child);
                if (!bn.StartsWith(name, StringComparison.Ordinal)) continue;
                if (!Regex.IsMatch(bn, @"^\w+\d+$")) continue;

                var pPath = "/dev/" + bn;
                long? pSectors = ReadLongFile(Path.Combine("/sys/class/block", bn, "size"));
                long? pBytes = pSectors.HasValue ? pSectors.Value * 512L : null;
                var mp = mounts.TryGetValue(pPath, out var m) ? m.mountPoint : null;
                var fs = mounts.TryGetValue(pPath, out var m2) ? m2.fstype : null;
                var label = labelMap.TryGetValue(pPath, out var L) ? L : null;
                var (usedBytes, freeBytes) = GetDiskUsage(mp);

                parts.Add(new Partition(
                    Path: pPath,
                    FsType: fs,
                    MountPoint: mp,
                    Size: pBytes.HasValue ? Human(pBytes.Value) : null,
                    SizeBytes: pBytes,
                    Label: label,
                    UsedBytes: usedBytes,
                    FreeBytes: freeBytes
                ));
            }

            res.Add(new DiskDevice(
                Path: devPath,
                Type: "disk",
                Transport: transport,
                Vendor: OrNull(vendor),
                Model: OrNull(model),
                Serial: OrNull(serial),
                Size: bytes.HasValue ? Human(bytes.Value) : null,
                Rotational: rotational,
                SizeBytes: bytes,
                Partitions: parts
            ));
        }

        return res;
    }

    private static Dictionary<string,(string mountPoint,string fstype)> ReadMounts()
    {
        var dict = new Dictionary<string,(string,string)>(StringComparer.Ordinal);
        try
        {
            foreach (var line in File.ReadLines("/proc/self/mounts"))
            {
                var parts = line.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;
                var src = parts[0]; var dst = parts[1]; var type = parts[2];
                if (!src.StartsWith("/dev/")) continue;
                dict[src] = (dst, type);
            }
        }
        catch { /* ignore */ }
        return dict;
    }

    private static Dictionary<string,string> ReadLabelsFromByLabel()
    {
        var map = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var dir = "/dev/disk/by-label";
        if (!Directory.Exists(dir)) return map;

        foreach (var link in Directory.EnumerateFileSystemEntries(dir))
        {
            try
            {
                var label = Path.GetFileName(link);
                var target = File.ResolveLinkTarget(link, true);
                if (target == null) continue;
                var full = target.FullName;
                var devName = Path.GetFileName(full);
                if (!string.IsNullOrEmpty(devName))
                {
                    var devPath = "/dev/" + devName;
                    map[devPath] = label;
                }
            }
            catch { /* ignore */ }
        }
        return map;
    }


    private static async Task<IReadOnlyList<DiskDevice>> FixTransportsAsync(List<DiskDevice> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var d = list[i];
            if (!string.IsNullOrWhiteSpace(d.Transport) && !d.Transport.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                continue;

            var name = Path.GetFileName(d.Path);

            var (e1, props, _) = await Run("udevadm", $"info -q property -n {d.Path}");
            if (e1 == 0 && !string.IsNullOrWhiteSpace(props))
            {
                foreach (var ln in props.Split('\n'))
                {
                    var t = ln.Trim();
                    if (t.StartsWith("ID_BUS=usb", StringComparison.OrdinalIgnoreCase) ||
                        t.StartsWith("ID_USB_DRIVER=", StringComparison.OrdinalIgnoreCase))
                    {
                        list[i] = d with { Transport = "usb" };
                        goto Next;
                    }
                }
            }

            var (e2, sysPath, _) = await Run("udevadm", $"info -q path -n {d.Path}");
            if (e2 == 0 && sysPath.Contains("/usb", StringComparison.OrdinalIgnoreCase))
            {
                list[i] = d with { Transport = "usb" };
                goto Next;
            }

            if (name.StartsWith("nvme", StringComparison.OrdinalIgnoreCase))
                list[i] = d with { Transport = "nvme" };

        Next: ;
        }
        return list;
    }


    private static string? GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String ? v.GetString() : null;
    private static int? GetInt(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Number ? v.GetInt32() : null;
    private static long? GetLong(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Number ? v.GetInt64() : null;

    private static string Human(long bytes)
    {
        string[] u = ["B","KB","MB","GB","TB","PB"];
        double n = bytes; int i = 0;
        while (n >= 1024 && i < u.Length - 1) { n /= 1024; i++; }
        return n < 10 ? $"{n:0.##} {u[i]}" : n < 100 ? $"{n:0.#} {u[i]}" : $"{n:0} {u[i]}";
    }

    private static string? OrNull(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static long? ReadLongFile(string path)
    {
        try { var t = File.ReadAllText(path).Trim(); return long.Parse(t); } catch { return null; }
    }
    private static string? ReadString(string path)
    {
        try { return File.ReadAllText(path).Trim(); } catch { return null; }
    }
    private static string? TryReadLink(string path)
    {
        try { return Path.GetFullPath(path); } catch { return null; }
    }

    private static async Task<(int exit, string stdout, string stderr)> Run(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute = false
            };
            using var p = Process.Start(psi)!;
            var so = await p.StandardOutput.ReadToEndAsync();
            var se = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode, so, se);
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }

    private static (long? usedBytes, long? freeBytes) GetDiskUsage(string? mountPoint)
    {
        if (string.IsNullOrWhiteSpace(mountPoint))
            return (null, null);

        try
        {
            var driveInfo = new DriveInfo(mountPoint);
            if (driveInfo.IsReady)
            {
                var totalSize = driveInfo.TotalSize;
                var freeSpace = driveInfo.AvailableFreeSpace;
                var usedSpace = totalSize - freeSpace;
                return (usedSpace, freeSpace);
            }
        }
        catch
        {
            // Ignore errors for unmounted or inaccessible drives
        }

        return (null, null);
    }
}
