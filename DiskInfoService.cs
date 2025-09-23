using System.Diagnostics;
using System.Text.Json;

namespace UDiskBackup;

public class DiskInfoService
{
    public async Task<IReadOnlyList<DiskDevice>> GetDisksAsync()
    {
        var result = new List<DiskDevice>();

        // Minimalny zestaw kolumn, których używa UI
        var cols = "NAME,TYPE,SIZE,ROTA,TRAN,VENDOR,MODEL,SERIAL,PATH,FSTYPE,MOUNTPOINT";
        var (exit, stdout, stderr) = await Run("lsblk", $"-J -O -b -o {cols}");
        if (exit != 0 || string.IsNullOrWhiteSpace(stdout))
            return result;

        using var doc = JsonDocument.Parse(stdout);
        if (!doc.RootElement.TryGetProperty("blockdevices", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var node in arr.EnumerateArray())
        {
            var type = GetString(node, "type");
            if (type is not ("disk" or "rom"))
                continue;

            var dev = BuildDisk(node);
            if (dev != null)
                result.Add(dev);
        }

        // Fallback: popraw "transport" na podstawie udevadm/sysfs,
        // jeśli lsblk podał pusty/unknown (typowe dla JMicron).
        for (int i = 0; i < result.Count; i++)
        {
            var d = result[i];
            if (string.IsNullOrWhiteSpace(d.Transport) || d.Transport!.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            {
                var name = System.IO.Path.GetFileName(d.Path);
                var detected = await DetectTransportFallbackAsync(d.Path, name);
                if (!string.IsNullOrWhiteSpace(detected))
                {
                    result[i] = d with { Transport = detected };
                }
                else if (name.StartsWith("nvme", StringComparison.OrdinalIgnoreCase))
                {
                    result[i] = d with { Transport = "nvme" };
                }
            }
        }

        return result;
    }

    // ---- helpers ----

    private static DiskDevice? BuildDisk(JsonElement node)
    {
        var name = GetString(node, "name");
        if (string.IsNullOrEmpty(name)) return null;

        var path = GetString(node, "path");
        if (string.IsNullOrEmpty(path))
            path = "/dev/" + name;

        var sizeBytes = GetLong(node, "size");
        var sizeHuman = sizeBytes.HasValue ? Human(sizeBytes.Value) : null;

        var rotational = GetInt(node, "rota") == 1;

        var tran = GetString(node, "tran"); // bywa null/unknown dla niektórych mostków USB

        var vendor = GetString(node, "vendor");
        var model  = GetString(node, "model");
        var serial = GetString(node, "serial");

        var parts = new List<Partition>();
        if (node.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in ch.EnumerateArray())
            {
                var pType = GetString(p, "type");
                if (pType is not ("part" or "crypt" or "raid"))
                    continue;

                var pName = GetString(p, "name");
                var pPath = GetString(p, "path");
                if (string.IsNullOrEmpty(pPath))
                    pPath = "/dev/" + pName;

                var pSizeBytes = GetLong(p, "size");
                var pSizeHuman = pSizeBytes.HasValue ? Human(pSizeBytes.Value) : null;

                var fstype = GetString(p, "fstype");
                var mp     = GetString(p, "mountpoint"); // lsblk zwraca 'mountpoint' lower-case

                parts.Add(new Partition(
                    Path: pPath!,
                    FsType: fstype,
                    MountPoint: string.IsNullOrWhiteSpace(mp) ? null : mp,
                    Size: pSizeHuman
                ));
            }
        }

        return new DiskDevice(
            Path: path!,
            Type: GetString(node, "type") ?? "disk",
            Transport: string.IsNullOrWhiteSpace(tran) ? null : tran,
            Vendor: string.IsNullOrWhiteSpace(vendor) ? null : vendor,
            Model: string.IsNullOrWhiteSpace(model) ? null : model,
            Serial: string.IsNullOrWhiteSpace(serial) ? null : serial,
            Size: sizeHuman,
            Rotational: rotational,
            Partitions: parts
        );
    }

    private static async Task<string?> DetectTransportFallbackAsync(string devPath, string devName)
    {
        // 1) udevadm properties -> ID_BUS=usb (najpewniejsze)
        var (e1, props, _) = await Run("udevadm", $"info -q property -n {devPath}");
        if (e1 == 0 && !string.IsNullOrWhiteSpace(props))
        {
            foreach (var line in props.Split('\n'))
            {
                var ln = line.Trim();
                if (ln.StartsWith("ID_BUS=", StringComparison.OrdinalIgnoreCase))
                {
                    var bus = ln.Substring("ID_BUS=".Length).Trim().ToLowerInvariant();
                    if (bus == "usb") return "usb";
                }
                if (ln.StartsWith("ID_USB_DRIVER=", StringComparison.OrdinalIgnoreCase))
                {
                    // np. usb-storage
                    return "usb";
                }
            }
        }

        // 2) udevadm path -> ścieżka sysfs zawiera /usb/
        var (e2, sysPath, _) = await Run("udevadm", $"info -q path -n {devPath}");
        if (e2 == 0 && sysPath.Contains("/usb", StringComparison.OrdinalIgnoreCase))
            return "usb";

        // 3) heurystyka po nazwie (NVMe już wyłapujemy wyżej)
        if (devName.StartsWith("sd", StringComparison.OrdinalIgnoreCase))
        {
            // Bezpiecznie zwróć null zamiast 'unknown' – UI pokaże „-”,
            // a jeżeli to faktycznie USB, zwykle pkt 1-2 to wykryją.
            return null;
        }

        return null;
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
        var n = (double)bytes; var i = 0;
        while (n >= 1024 && i < u.Length - 1) { n /= 1024; i++; }
        return n < 10 ? $"{n:0.##} {u[i]}" : n < 100 ? $"{n:0.#} {u[i]}" : $"{n:0} {u[i]}";
    }

    private static async Task<(int exit, string stdout, string stderr)> Run(string file, string args)
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
}
