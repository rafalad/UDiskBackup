using System.Diagnostics;
using System.Text.Json;

namespace UDiskBackup;

public class DiskInfoService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<IReadOnlyList<DiskDto>> GetDisksAsync()
    {
        // Wywołanie lsblk -JO (JSON + rozbudowane pola)
        var lsblkJson = await Run("lsblk", "-JO");
        using var doc = JsonDocument.Parse(lsblkJson);
        var blockdevices = doc.RootElement.GetProperty("blockdevices");

        var result = new List<DiskDto>();

        foreach (var dev in blockdevices.EnumerateArray())
        {
            var type = dev.GetProperty("type").GetString() ?? "";
            var name = dev.GetProperty("name").GetString() ?? "";

            // pomiń loop/ram, jeśli nie chcesz ich widzieć w tabeli
            if (type is "loop" or "ram") continue;

            string path  = "/dev/" + name;
            string size  = dev.TryGetProperty("size", out var sz) ? sz.GetString() ?? "" : "";
            string vendor = dev.TryGetProperty("vendor", out var v) ? v.GetString() ?? "" : "";
            string model  = dev.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
            string serial = dev.TryGetProperty("serial", out var s) ? s.GetString() ?? "" : "";

            bool rotational = false;
            if (dev.TryGetProperty("rota", out var rota))
            {
                if (rota.ValueKind == JsonValueKind.Number) rotational = rota.GetInt32() == 1;
                else if (rota.ValueKind == JsonValueKind.True) rotational = true;
            }

            // transport z sysfs (najpewniejsze)
            string transport = ReadSys($"/sys/class/block/{name}/device/transport")?.Trim().ToLowerInvariant()
                               ?? (ReadSys($"/sys/class/block/{name}/subsystem/uevent")?.Contains("nvme", StringComparison.OrdinalIgnoreCase) == true
                                   ? "nvme" : "unknown");

            string diskType = GuessDiskType(rotational, transport, type);

            // partycje
            var parts = new List<PartitionDto>();
            if (dev.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in ch.EnumerateArray())
                {
                    if ((p.GetProperty("type").GetString() ?? "") != "part") continue;
                    var pname = p.GetProperty("name").GetString() ?? "";
                    parts.Add(new PartitionDto(
                        Name: pname,
                        Path: "/dev/" + pname,
                        FsType: p.TryGetProperty("fstype", out var pf) ? pf.GetString() : null,
                        MountPoint: p.TryGetProperty("mountpoint", out var pm) ? pm.GetString() : null,
                        Uuid: p.TryGetProperty("uuid", out var pu) ? pu.GetString() : null,
                        Size: p.TryGetProperty("size", out var ps) ? ps.GetString() ?? "" : ""
                    ));
                }
            }

            result.Add(new DiskDto(
                Name: name,
                Path: path,
                Transport: transport,
                Type: diskType,
                Vendor: vendor,
                Model: model,
                Serial: serial,
                Size: size,
                Rotational: rotational,
                Partitions: parts
            ));
        }

        return result;
    }

    private static string GuessDiskType(bool rotational, string transport, string lsblkType)
        => transport == "nvme" ? "nvme"
         : lsblkType == "rom"  ? "rom"
         : rotational          ? "hdd"
         : "ssd";

    private static async Task<string> Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
            throw new Exception($"{file} {args} failed: {stderr}");
        return stdout;
    }

    private static string? ReadSys(string path)
        => System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null;
}
