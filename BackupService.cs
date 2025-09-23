using System;
using System.Diagnostics;
using System.IO;
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

    public BackupService(DiskInfoService disks, IHubContext<BackupHub> hub)
    {
        _disks = disks;
        _hub = hub;
    }

    /// <summary>
    /// Zwraca listę zamontowanych partycji na dyskach USB jako potencjalne cele backupu.
    /// </summary>
    public async Task<IReadOnlyList<UsbTarget>> GetUsbTargetsAsync()
    {
        var all = await _disks.GetDisksAsync();
        var targets = new List<UsbTarget>();

        foreach (var d in all)
        {
            if (!string.Equals(d.Transport, "usb", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var p in d.Partitions)
            {
                if (string.IsNullOrEmpty(p.MountPoint))
                    continue;

                try
                {
                    var di = new DriveInfo(p.MountPoint!);
                    targets.Add(new UsbTarget(
                        MountPoint: p.MountPoint!,
                        Device: p.Path,
                        Label: Path.GetFileName(p.MountPoint!.TrimEnd('/')),
                        FsType: p.FsType ?? "",
                        FreeBytes: di.AvailableFreeSpace,
                        TotalBytes: di.TotalSize
                    ));
                }
                catch
                {
                    // Pomijamy mountpointy, które DriveInfo nie potrafi odczytać.
                }
            }
        }

        return targets;
    }

    /// <summary>
    /// Buduje plan backupu (dry-run rsync), szacuje wymaganą przestrzeń i weryfikuje czy jest jej wystarczająco.
    /// </summary>
    public async Task<BackupPlan> PlanAsync(string targetMount, string source = "/mnt/shared")
    {
        if (string.IsNullOrWhiteSpace(targetMount) || !targetMount.StartsWith('/'))
            throw new ArgumentException("Niepoprawny mountpoint.");

        var di = new DriveInfo(targetMount);
        var ts = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
        var dest = Path.Combine(targetMount, "UDiskBackup", "current");
        var deleted = Path.Combine(targetMount, "UDiskBackup", ".deleted", ts);

        // rsync dry-run, aby oszacować transfer
        var args = RsyncArgs(source, dest, deleted, dryRun: true);
        var (exit, stdout, stderr) = await Run("rsync", args);

        // Nawet gdy exit != 0, spróbujmy wyciągnąć estymację (czasem rsync zwróci 23/24 z powodu uprawnień do części plików).
        long estimated = ParseTransferredBytes(stdout + "\n" + stderr);
        var free = di.AvailableFreeSpace;
        var enough = free >= (long)(estimated * 1.05); // 5% zapasu

        return new BackupPlan(
            Source: source,
            TargetBackupDir: dest,
            DeletedDir: deleted,
            RsyncArgs: args,
            EstimatedBytes: estimated,
            FreeBytes: free,
            EnoughSpace: enough
        );
    }

    /// <summary>
    /// Uruchamia backup w tle. Zwraca operationId, a logi/status lecą przez SignalR (BackupHub).
    /// </summary>
    public Task<string> StartAsync(string targetMount, string source = "/mnt/shared")
    {
        lock (_lock)
        {
            if (_busy) throw new InvalidOperationException("Backup już trwa.");
            _busy = true;
        }

        try
        {
            var ts = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
            var dest = Path.Combine(targetMount, "UDiskBackup", "current");
            var deleted = Path.Combine(targetMount, "UDiskBackup", ".deleted", ts);

            Directory.CreateDirectory(dest);
            Directory.CreateDirectory(Path.GetDirectoryName(deleted)!);

            var args = RsyncArgs(source, dest, deleted, dryRun: false);
            var opId = Guid.NewGuid().ToString("N");

            _ = Task.Run(async () =>
            {
                await _hub.Clients.All.SendAsync("backupStatus", new BackupStatus(opId, "Running", "Backup rozpoczęty"));
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
                            await _hub.Clients.All.SendAsync("backupLog", new { operationId = opId, line = e.Data });
                    };
                    p.ErrorDataReceived += async (_, e) =>
                    {
                        if (e.Data != null)
                            await _hub.Clients.All.SendAsync("backupLog", new { operationId = opId, line = e.Data });
                    };

                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    await p.WaitForExitAsync();

                    if (p.ExitCode == 0)
                        await _hub.Clients.All.SendAsync("backupStatus", new BackupStatus(opId, "Completed", "Backup zakończony"));
                    else
                        await _hub.Clients.All.SendAsync("backupStatus", new BackupStatus(opId, "Failed", $"rsync exit {p.ExitCode}"));
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

    // ----------------- Helpers -----------------

    private static string RsyncArgs(string source, string dest, string deleted, bool dryRun)
    {
        // -a: archive (rekursja, prawa, czasy), --delete: usuń z dest pliki nieobecne w src
        // --backup --backup-dir: przenoś USUNIĘTE/NADPISANE do katalogu wersji (oznaczenie usuniętych)
        // --human-readable + --info=stats2,progress2: czytelne statystyki/log
        // Końcowy "/" w source jest ważny, by kopiować ZAWARTOŚĆ katalogu źródłowego.
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
        // Typowa linia rsync -stats:
        // "Total transferred file size: 12345 bytes"
        var m = Regex.Match(text, @"Total transferred file size:\s*([\d,]+)\s*bytes", RegexOptions.IgnoreCase);
        if (m.Success && long.TryParse(m.Groups[1].Value.Replace(",", ""), out var v))
            return v;

        // Zapasowy wariant:
        m = Regex.Match(text, @"Literal data:\s*([\d,]+)\s*bytes", RegexOptions.IgnoreCase);
        if (m.Success && long.TryParse(m.Groups[1].Value.Replace(",", ""), out v))
            return v;

        return 0L;
    }
}
