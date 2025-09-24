using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace UDiskBackup;

public class BackupService
{
    private readonly DiskInfoService _disks;
    private readonly IHubContext<BackupHub> _hub;
    private readonly object _lock = new();
    private bool _busy;
    private volatile string? _currentLogFile;

    public BackupService(DiskInfoService disks, IHubContext<BackupHub> hub)
    {
        _disks = disks;
        _hub = hub;
    }

    public async Task<IReadOnlyList<UsbTarget>> GetUsbTargetsAsync()
    {
        var all = await _disks.GetDisksAsync();
        var targets = new List<UsbTarget>();
        foreach (var d in all)
        {
            if (!string.Equals(d.Transport, "usb", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var p in d.Partitions)
            {
                if (string.IsNullOrEmpty(p.MountPoint)) continue;
                try
                {
                    var di = new DriveInfo(p.MountPoint!);
                    if (di.TotalSize <= 0) continue;
                    var mpName = Path.GetFileName(p.MountPoint!.TrimEnd('/'));
                    if (!string.Equals(mpName, "USB_BACKUP", StringComparison.OrdinalIgnoreCase)) continue;
                    targets.Add(new UsbTarget(
                        MountPoint: p.MountPoint!,
                        Device: p.Path,
                        Label: mpName,
                        FsType: p.FsType ?? "",
                        FreeBytes: di.AvailableFreeSpace,
                        TotalBytes: di.TotalSize
                    ));
                }
                catch { }
            }
        }
        return targets;
    }

    public async Task<BackupPlan> PlanAsync(string targetMount, string source = "/mnt/shared")
    {
        if (string.IsNullOrWhiteSpace(targetMount) || !targetMount.StartsWith('/'))
            throw new ArgumentException("Niepoprawny mountpoint.");

        var di = new DriveInfo(targetMount);
        var ts = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
        var dest = Path.Combine(targetMount, "UDiskBackup", "current");
        var deleted = Path.Combine(targetMount, "UDiskBackup", ".deleted", ts);

        var args = RsyncArgs(source, dest, deleted, dryRun: true);
        var (exit, stdout, stderr) = await Run("rsync", args);

        long estimated = ParseTransferredBytes(stdout + "\n" + stderr);
        var free = di.AvailableFreeSpace;
        var enough = free >= (long)(estimated * 1.05);

        return new BackupPlan(source, dest, deleted, args, estimated, free, enough);
    }

    public Task<string> StartAsync(string targetMount, string source = "/mnt/shared")
    {
        if (string.IsNullOrWhiteSpace(targetMount) || !targetMount.StartsWith('/'))
            throw new ArgumentException("Niepoprawny mountpoint.");
        if (!Directory.Exists(targetMount))
            throw new DirectoryNotFoundException($"Mountpoint nie istnieje: {targetMount}");
        string[] allowed = ["/media/", "/mnt/", "/run/media/"];
        if (!allowed.Any(a => targetMount.StartsWith(a, StringComparison.Ordinal)))
            throw new InvalidOperationException("Dozwolone są mountpointy w /media, /mnt lub /run/media.");

        lock (_lock)
        {
            if (_busy) throw new InvalidOperationException("Backup już trwa.");
            _busy = true;
        }

        try
        {
            var ts = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
            var startedAt = DateTimeOffset.UtcNow;

            var dest = Path.Combine(targetMount, "UDiskBackup", "current");
            var deleted = Path.Combine(targetMount, "UDiskBackup", ".deleted", ts);
            var logsDir = Path.Combine(targetMount, "UDiskBackup", "logs");
            Directory.CreateDirectory(dest);
            Directory.CreateDirectory(Path.GetDirectoryName(deleted)!);
            Directory.CreateDirectory(logsDir);

            long? freeBefore = null;
            try { freeBefore = new DriveInfo(targetMount).AvailableFreeSpace; } catch { }

            var plan = PlanAsync(targetMount, source).GetAwaiter().GetResult();
            if (!plan.EnoughSpace)
            {
                lock (_lock) { _busy = false; }
                throw new InvalidOperationException($"Za mało miejsca. Potrzeba {plan.EstimatedBytes} B, wolne {plan.FreeBytes} B.");
            }

            var args = RsyncArgs(source, dest, deleted, dryRun: false);
            var opId = Guid.NewGuid().ToString("N");

            _ = Task.Run(async () =>
            {
                await _hub.Clients.All.SendAsync("backupStatus", new BackupStatus(opId, "Running", "Backup rozpoczęty"));
                var allOutput = new StringBuilder(64 * 1024);

                var currentLog = Path.Combine(logsDir, $"current-{opId}.log");
                _currentLogFile = currentLog;

                try
                {
                    var psi = new ProcessStartInfo("rsync", args)
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    };

                    using var p = Process.Start(psi)!;

                    p.OutputDataReceived += async (_, e) =>
                    {
                        if (e.Data != null)
                        {
                            allOutput.AppendLine(e.Data);
                            AppendToFileSafe(currentLog, e.Data);
                            await EmitLog(opId, Classify(e.Data), e.Data);
                        }
                    };
                    p.ErrorDataReceived += async (_, e) =>
                    {
                        if (e.Data != null)
                        {
                            allOutput.AppendLine(e.Data);
                            AppendToFileSafe(currentLog, e.Data);
                            await EmitLog(opId, Classify(e.Data, err:true), e.Data);
                        }
                    };

                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    await p.WaitForExitAsync();

                    var endedAt = DateTimeOffset.UtcNow;
                    long? freeAfter = null;
                    try { freeAfter = new DriveInfo(targetMount).AvailableFreeSpace; } catch { }

                    var stats = ParseRsyncStats(allOutput.ToString());

                    var summary = new BackupRunSummary
                    (
                        OperationId: opId,
                        StartedAtUtc: startedAt,
                        EndedAtUtc: endedAt,
                        Duration: endedAt - startedAt,
                        Source: source,
                        Target: dest,
                        DeletedDir: deleted,
                        ExitCode: p.ExitCode,
                        Success: p.ExitCode == 0,
                        FreeBytesBefore: freeBefore,
                        FreeBytesAfter: freeAfter,
                        NumberOfFiles: stats.NumberOfFiles,
                        NumberOfDirs: stats.NumberOfDirs,
                        NumberOfTransferredFiles: stats.NumberOfTransferredFiles,
                        NumberOfDeletedFiles: stats.NumberOfDeletedFiles,
                        TotalFileSize: stats.TotalFileSize,
                        TotalTransferredFileSize: stats.TotalTransferredFileSize,
                        LiteralData: stats.LiteralData,
                        MatchedData: stats.MatchedData,
                        FileListSize: stats.FileListSize,
                        TotalBytesSent: stats.TotalBytesSent,
                        TotalBytesReceived: stats.TotalBytesReceived
                    );

                    var baseName = $"{ts}_{opId}";
                    var jsonPath = Path.Combine(logsDir, baseName + ".json");
                    var txtPath  = Path.Combine(logsDir, baseName + ".txt");
                    await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
                    await File.WriteAllTextAsync(txtPath, MakeHumanSummary(summary));

                    try
                    {
                        if (File.Exists(currentLog))
                        {
                            var finalLog = Path.Combine(logsDir, $"{opId}.log");
                            File.Move(currentLog, finalLog, true);
                            _currentLogFile = finalLog;
                        }
                    }
                    catch { }

                    if (p.ExitCode == 0)
                        await _hub.Clients.All.SendAsync("backupStatus", new BackupStatus(opId, "Completed", $"Backup zakończony. Podsumowanie: {txtPath}"));
                    else
                        await _hub.Clients.All.SendAsync("backupStatus", new BackupStatus(opId, "Failed", $"rsync exit {p.ExitCode}. Podsumowanie: {txtPath}"));
                }
                catch (Exception ex)
                {
                    await _hub.Clients.All.SendAsync("backupStatus", new BackupStatus(opId, "Failed", ex.Message));
                }
                finally
                {
                    lock (_lock) { _busy = false; }
                }
            });

            return Task.FromResult(opId);
        }
        catch
        {
            lock (_lock) { _busy = false; }
            throw;
        }
    }

    public async Task<IReadOnlyList<BackupHistoryItem>> GetHistoryAsync(string? targetMount = null, int take = 50)
    {
        var items = new List<BackupHistoryItem>();
        var targets = new List<UsbTarget>();

        if (!string.IsNullOrWhiteSpace(targetMount))
        {
            targets.Add(new UsbTarget(targetMount, Device: "", Label: Path.GetFileName(targetMount.TrimEnd('/')), FsType: "", FreeBytes: 0, TotalBytes: 0));
        }
        else
        {
            targets.AddRange(await GetUsbTargetsAsync());
        }

        foreach (var t in targets)
        {
            var logsDir = Path.Combine(t.MountPoint, "UDiskBackup", "logs");
            if (!Directory.Exists(logsDir)) continue;

            foreach (var json in Directory.EnumerateFiles(logsDir, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    using var fs = File.OpenRead(json);
                    var summary = await JsonSerializer.DeserializeAsync<BackupRunSummary>(fs);
                    if (summary == null) continue;

                    var txt = Path.ChangeExtension(json, ".txt");
                    items.Add(new BackupHistoryItem(
                        OperationId: summary.OperationId,
                        StartedAtUtc: summary.StartedAtUtc,
                        EndedAtUtc: summary.EndedAtUtc,
                        Success: summary.Success,
                        Duration: summary.Duration,
                        TargetMount: t.MountPoint,
                        TargetLabel: t.Label,
                        SummaryJsonPath: json,
                        SummaryTxtPath: File.Exists(txt) ? txt : null,
                        NumberOfTransferredFiles: summary.NumberOfTransferredFiles,
                        TotalTransferredFileSize: summary.TotalTransferredFileSize
                    ));
                }
                catch { }
            }
        }

        return items
            .OrderByDescending(i => i.StartedAtUtc)
            .Take(Math.Max(1, take))
            .ToList();
    }

    public IResult CurrentLog(bool download = false)
    {
        var path = _currentLogFile;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return Results.Text("");
        if (download)
            return Results.File(path, "text/plain", Path.GetFileName(path));
        var text = File.ReadAllText(path);
        return Results.Text(text, "text/plain");
    }

    private static string RsyncArgs(string source, string dest, string deleted, bool dryRun)
    {
        var common = $"-a --delete --backup --backup-dir=\"{deleted}\" --human-readable --info=stats2,progress2 --no-inc-recursive";
        if (dryRun) common = "--dry-run " + common;
        return $"{common} \"{source.TrimEnd('/')}/\" \"{dest.TrimEnd('/')}/\"";
    }

    private static async Task<(int exit, string stdout, string stderr)> Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi)!;
        var so = await p.StandardOutput.ReadToEndAsync();
        var se = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, so, se);
    }

    private static long ParseTransferredBytes(string text)
    {
        var m = Regex.Match(text, @"Total transferred file size:\s*([\d,]+)\s*bytes", RegexOptions.IgnoreCase);
        if (m.Success && long.TryParse(m.Groups[1].Value.Replace(",", ""), out var v)) return v;
        m = Regex.Match(text, @"Literal data:\s*([\d,]+)\s*bytes", RegexOptions.IgnoreCase);
        if (m.Success && long.TryParse(m.Groups[1].Value.Replace(",", ""), out v)) return v;
        return 0;
    }

    private static ParsedRsyncStats ParseRsyncStats(string text)
    {
        long? L(string pattern)
        {
            var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (!m.Success) return null;
            return long.TryParse(m.Groups[1].Value.Replace(",", ""), out var v) ? v : null;
        }
        int? I(string pattern)
        {
            var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (!m.Success) return null;
            return int.TryParse(m.Groups[1].Value.Replace(",", ""), out var v) ? v : null;
        }

        var numberOfFiles             = I(@"^Number of files:\s*([\d,]+)");
        var numberOfDirs              = I(@"^Number of (?:directories|dirs):\s*([\d,]+)");
        var numberOfXferFiles         = I(@"^Number of (?:regular )?files transferred:\s*([\d,]+)");
        var numberOfDeleted           = I(@"^Number of deleted files:\s*([\d,]+)");
        var totalFileSize             = L(@"^Total file size:\s*([\d,]+)\s*bytes");
        var totalTransferredFileSize  = L(@"^Total transferred file size:\s*([\d,]+)\s*bytes");
        var literalData               = L(@"^Literal data:\s*([\d,]+)\s*bytes");
        var matchedData               = L(@"^Matched data:\s*([\d,]+)\s*bytes");
        var fileListSize              = L(@"^File list size:\s*([\d,]+)");
        var totalBytesSent            = L(@"^Total bytes sent:\s*([\d,]+)");
        var totalBytesReceived        = L(@"^Total bytes received:\s*([\d,]+)");

        return new ParsedRsyncStats
        {
            NumberOfFiles = numberOfFiles,
            NumberOfDirs = numberOfDirs,
            NumberOfTransferredFiles = numberOfXferFiles,
            NumberOfDeletedFiles = numberOfDeleted,
            TotalFileSize = totalFileSize,
            TotalTransferredFileSize = totalTransferredFileSize,
            LiteralData = literalData,
            MatchedData = matchedData,
            FileListSize = fileListSize,
            TotalBytesSent = totalBytesSent,
            TotalBytesReceived = totalBytesReceived
        };
    }

    private static string MakeHumanSummary(BackupRunSummary s)
    {
        string H(long? v) => v is null ? "-" : Human(v.Value);

        var sb = new StringBuilder();
        sb.AppendLine("UDiskBackup – Podsumowanie backupu");
        sb.AppendLine("----------------------------------");
        sb.AppendLine($"OperationId     : {s.OperationId}");
        sb.AppendLine($"Start (UTC)     : {s.StartedAtUtc:u}");
        sb.AppendLine($"Koniec (UTC)    : {s.EndedAtUtc:u}");
        sb.AppendLine($"Czas trwania    : {s.Duration}");
        sb.AppendLine($"Źródło          : {s.Source}");
        sb.AppendLine($"Cel             : {s.Target}");
        sb.AppendLine($".deleted        : {s.DeletedDir}");
        sb.AppendLine($"Sukces          : {s.Success} (exit {s.ExitCode})");
        sb.AppendLine($"Wolne przed     : {H(s.FreeBytesBefore)}");
        sb.AppendLine($"Wolne po        : {H(s.FreeBytesAfter)}");
        sb.AppendLine();
        sb.AppendLine("Statystyki rsync (--stats):");
        sb.AppendLine($"  Plików ogółem           : {s.NumberOfFiles?.ToString() ?? "-"}");
        sb.AppendLine($"  Katalogów               : {s.NumberOfDirs?.ToString() ?? "-"}");
        sb.AppendLine($"  Przesł. plików          : {s.NumberOfTransferredFiles?.ToString() ?? "-"}");
        sb.AppendLine($"  Usuniętych (--delete)   : {s.NumberOfDeletedFiles?.ToString() ?? "-"}");
        sb.AppendLine($"  Rozmiar plików (total)  : {H(s.TotalFileSize)}");
        sb.AppendLine($"  Faktycznie przesłane    : {H(s.TotalTransferredFileSize)}");
        sb.AppendLine($"  Literal data            : {H(s.LiteralData)}");
        sb.AppendLine($"  Matched data            : {H(s.MatchedData)}");
        sb.AppendLine($"  File list size          : {H(s.FileListSize)}");
        sb.AppendLine($"  Bytes sent/received     : {H(s.TotalBytesSent)} / {H(s.TotalBytesReceived)}");
        return sb.ToString();
    }

    private static string Human(long bytes)
    {
        string[] u = ["B","KB","MB","GB","TB","PB"];
        double n = bytes; int i = 0;
        while (n >= 1024 && i < u.Length - 1) { n /= 1024; i++; }
        return n < 10 ? $"{n:0.##} {u[i]}" : n < 100 ? $"{n:0.#} {u[i]}" : $"{n:0} {u[i]}";
    }

    private static string Classify(string line, bool err = false)
    {
        if (Regex.IsMatch(line, @"error|failed|IO error|rsync\s+error", RegexOptions.IgnoreCase)) return "error";
        if (Regex.IsMatch(line, @"\d+%|\bto-check=\d+/\d+\b|xfer", RegexOptions.IgnoreCase)) return "progress";
        if (err) return "progress";
        return "info";
    }

    private Task EmitLog(string opId, string level, string line)
        => _hub.Clients.All.SendAsync("backupLog", new { operationId = opId, level, line, ts = DateTimeOffset.UtcNow });

    private static void AppendToFileSafe(string path, string line)
    {
        try { File.AppendAllText(path, line + Environment.NewLine); } catch { }
    }

    private class ParsedRsyncStats
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
        long? TotalTransferredFileSize
    );

    public record UsbTarget(
        string MountPoint,
        string Device,
        string Label,
        string FsType,
        long FreeBytes,
        long TotalBytes
    );

    public record BackupPlan(
        string SourceDir,
        string TargetBackupDir,
        string DeletedDir,
        string RsyncArgs,
        long EstimatedBytes,
        long FreeBytes,
        bool EnoughSpace
    );

    public record BackupStatus(
        string OperationId,
        string State,
        string Message
    );
}
