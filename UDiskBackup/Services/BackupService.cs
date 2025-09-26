using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using UDiskBackup.Hubs;
using UDiskBackup.Models;

namespace UDiskBackup.Services;

public class BackupService
{
    private readonly DiskInfoService _disks;
    private readonly IHubContext<BackupHub> _hub;
    private readonly ILogger<BackupService> _logger;
    private readonly object _lock = new();
    private bool _busy;
    private readonly StringBuilder _liveBuffer = new();
    
    private Process? _currentBackupProcess;
    private CancellationTokenSource? _backupCancellation;
    private BackupStatus? _currentStatus;
    
    private readonly bool _isRoot = Environment.UserName == "root" || Environment.GetEnvironmentVariable("USER") == "root";
    
    private readonly Lazy<bool> _hasUdisksctl = new Lazy<bool>(() =>
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = "udisksctl",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    });

    public BackupService(DiskInfoService disks, IHubContext<BackupHub> hub, ILogger<BackupService> logger)
    {
        _disks = disks;
        _hub = hub;
        _logger = logger;
        _logger.LogInformation("BackupService initialized - Running as root: {IsRoot}, udisksctl available: {HasUdisksctl}", 
            _isRoot, _hasUdisksctl.Value);
    }

    public BackupStatus? GetCurrentStatus()
    {
        lock (_lock)
        {
            return _currentStatus;
        }
    }

    private async Task SetCurrentStatusAsync(BackupStatus status)
    {
        lock (_lock)
        {
            _currentStatus = status;
        }
        await _hub.Clients.All.SendAsync("backupStatus", status);
    }

    public async Task<bool> StopBackupAsync()
    {
        _logger.LogInformation("Stop backup requested");
        
        try
        {
            _backupCancellation?.Cancel();
            
            if (_currentBackupProcess != null && !_currentBackupProcess.HasExited)
            {
                _logger.LogInformation("Killing rsync process {ProcessId}", _currentBackupProcess.Id);
                _currentBackupProcess.Kill();
                await _currentBackupProcess.WaitForExitAsync();
                _logger.LogInformation("Backup process stopped");
                
                await SetCurrentStatusAsync(new BackupStatus("stopped", "Stopped", "Backup został zatrzymany przez użytkownika"));
                
                return true;
            }
            
            _logger.LogWarning("No backup process to stop");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping backup");
            return false;
        }
        finally
        {
            lock (_lock)
            {
                _busy = false;
                _currentBackupProcess = null;
                _backupCancellation = null;
            }
        }
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
                if (!string.Equals(p.Label, "USB_BACKUP", StringComparison.Ordinal)) continue;
                try
                {
                    var di = new DriveInfo(p.MountPoint!);
                    targets.Add(new UsbTarget(
                        MountPoint: p.MountPoint!,
                        Device: p.Path,
                        Label: p.Label ?? Path.GetFileName(p.MountPoint!.TrimEnd('/')),
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
        
        // Nowa struktura dla kopii przyrostowej
        var backupRoot = Path.Combine(targetMount, "UDiskBackup");
        var currentBackup = Path.Combine(backupRoot, "current");           // Ostatni pełny backup
        var incrementalBackup = Path.Combine(backupRoot, "incremental", ts); // Nowy backup przyrostowy
        var deleted = Path.Combine(backupRoot, ".deleted", ts);

        var args = RsyncArgs(source, currentBackup, incrementalBackup, deleted, dryRun: true);
        var (exit, stdout, stderr) = await Run("rsync", args);

        long estimated = ParseTransferredBytes(stdout + "\n" + stderr);
        var free = di.AvailableFreeSpace;
        var enough = free >= (long)(estimated * 1.05);

        return new BackupPlan(source, incrementalBackup, deleted, args, estimated, free, enough);
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

            // Nowa struktura dla kopii przyrostowej
            var backupRoot = Path.Combine(targetMount, "UDiskBackup");
            var currentBackup = Path.Combine(backupRoot, "current");           // Ostatni pełny backup
            var incrementalBackup = Path.Combine(backupRoot, "incremental", ts); // Nowy backup przyrostowy
            var deleted = Path.Combine(backupRoot, ".deleted", ts);
            var logsDir = Path.Combine(backupRoot, "logs");
            
            Directory.CreateDirectory(incrementalBackup);
            Directory.CreateDirectory(Path.GetDirectoryName(deleted)!);
            Directory.CreateDirectory(logsDir);

            long? freeBefore = null;
            try { freeBefore = new DriveInfo(targetMount).AvailableFreeSpace; } catch { }

            var args = RsyncArgs(source, currentBackup, incrementalBackup, deleted, dryRun: false);
            var opId = Guid.NewGuid().ToString("N");
            _liveBuffer.Clear();

            _backupCancellation = new CancellationTokenSource();
            var cancellationToken = _backupCancellation.Token;

            _ = Task.Run(async () =>
            {
                await SetCurrentStatusAsync(new BackupStatus(opId, "Running", "Backup rozpoczęty"));

                try
                {
                    var psi = new ProcessStartInfo("rsync", args)
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    };

                    using var p = Process.Start(psi)!;
                    
                    lock (_lock)
                    {
                        _currentBackupProcess = p;
                    }

                    p.OutputDataReceived += async (_, e) =>
                    {
                        if (e.Data != null && !cancellationToken.IsCancellationRequested)
                        {
                            lock (_liveBuffer) _liveBuffer.AppendLine(e.Data);
                            await _hub.Clients.All.SendAsync("backupLog", new { operationId = opId, level = Classify(e.Data), line = e.Data, ts = DateTimeOffset.UtcNow });
                        }
                    };
                    p.ErrorDataReceived += async (_, e) =>
                    {
                        if (e.Data != null && !cancellationToken.IsCancellationRequested)
                        {
                            lock (_liveBuffer) _liveBuffer.AppendLine(e.Data);
                            await _hub.Clients.All.SendAsync("backupLog", new { operationId = opId, level = Classify(e.Data), line = e.Data, ts = DateTimeOffset.UtcNow });
                        }
                    };

                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    var processTask = p.WaitForExitAsync(cancellationToken);
                    await processTask;

                    var endedAt = DateTimeOffset.UtcNow;
                    
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogInformation("Backup was cancelled");
                        await SetCurrentStatusAsync(new BackupStatus(opId, "Cancelled", "Backup został zatrzymany"));
                        return;
                    }

                    long? freeAfter = null;
                    try { freeAfter = new DriveInfo(targetMount).AvailableFreeSpace; } catch { }

                    var stats = ParseRsyncStats(_liveBuffer.ToString());

                    var summary = new BackupRunSummary
                    (
                        OperationId: opId,
                        StartedAtUtc: startedAt,
                        EndedAtUtc: endedAt,
                        Duration: endedAt - startedAt,
                        Source: source,
                        Target: incrementalBackup,
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
                    
                    // Standardowy JSON z BackupRunSummary dla kompatybilności
                    await System.IO.File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
                    
                    // Rozszerzony JSON z dodatkowymi informacjami o backup'ie przyrostowym
                    var extendedJsonPath = Path.Combine(logsDir, baseName + "_extended.json");
                    var backupInfo = new Dictionary<string, object>
                    {
                        ["summary"] = summary,
                        ["backupType"] = Directory.Exists(currentBackup) ? "incremental" : "full",
                        ["backupPath"] = incrementalBackup,
                        ["linkDestPath"] = Directory.Exists(currentBackup) ? (object)currentBackup : string.Empty,
                        ["deletedPath"] = deleted,
                        ["spaceSavings"] = new 
                        {
                            literalData = stats.LiteralData ?? 0,
                            matchedData = stats.MatchedData ?? 0,
                            totalFileSize = stats.TotalFileSize ?? 0,
                            savingsRatio = (stats.TotalFileSize ?? 0) > 0 ? (double)(stats.MatchedData ?? 0) / (stats.TotalFileSize ?? 0) : 0,
                            explanation = "matchedData = dane które już istniały (hard links), literalData = nowo skopiowane dane"
                        }
                    };
                    await System.IO.File.WriteAllTextAsync(extendedJsonPath, JsonSerializer.Serialize(backupInfo, new JsonSerializerOptions { WriteIndented = true }));
                    
                    // Ulepszone tekstowe podsumowanie z informacjami o przyroście
                    var currentBackupType = (string)backupInfo["backupType"];
                    var extendedMetadata = new Dictionary<string, object>
                    {
                        ["space_savings_ratio"] = ((dynamic)backupInfo["spaceSavings"]).savingsRatio,
                        ["linked_from"] = backupInfo["linkDestPath"] ?? "",
                        ["literal_data"] = ((dynamic)backupInfo["spaceSavings"]).literalData,
                        ["matched_data"] = ((dynamic)backupInfo["spaceSavings"]).matchedData
                    };
                    var enhancedSummary = MakeEnhancedHumanSummary(summary, currentBackupType, extendedMetadata);
                    await System.IO.File.WriteAllTextAsync(txtPath, enhancedSummary);

                    if (p.ExitCode == 0)
                    {
                        // Po udanym backup'ie aktualizuj "current" jako symlink do najnowszego backup'u
                        try
                        {
                            if (Directory.Exists(currentBackup) && !IsSymbolicLink(currentBackup))
                            {
                                // Przenieś stary current do archived (tylko przy pierwszym uruchomieniu)
                                var archivedPath = Path.Combine(backupRoot, "archived", "initial");
                                Directory.CreateDirectory(Path.GetDirectoryName(archivedPath)!);
                                Directory.Move(currentBackup, archivedPath);
                            }
                            else if (Directory.Exists(currentBackup))
                            {
                                // Usuń stary symlink
                                Directory.Delete(currentBackup);
                            }
                            
                            // Utwórz symlink do najnowszego backup'u
                            CreateSymbolicLink(currentBackup, incrementalBackup);
                        }
                        catch (Exception linkEx)
                        {
                            _logger.LogWarning(linkEx, "Nie udało się zaktualizować symlink'u 'current'");
                        }
                        
                        await SetCurrentStatusAsync(new BackupStatus(opId, "Completed", $"Backup zakończony. Podsumowanie: {txtPath}"));
                    }
                    else
                        await SetCurrentStatusAsync(new BackupStatus(opId, "Failed", $"rsync exit {p.ExitCode}. Podsumowanie: {txtPath}"));
                }
                catch (Exception ex)
                {
                    await SetCurrentStatusAsync(new BackupStatus(opId, "Failed", ex.Message));
                }
                finally
                {
                    lock (_lock) 
                    { 
                        _busy = false;
                        _currentBackupProcess = null;
                        _backupCancellation?.Dispose();
                        _backupCancellation = null;
                    }
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

            // Szukamy tylko standardowych plików JSON, pomijając pliki *_extended.json
            foreach (var json in Directory.EnumerateFiles(logsDir, "*.json", SearchOption.TopDirectoryOnly)
                .Where(file => !Path.GetFileName(file).Contains("_extended.json")))
            {
                try
                {
                    using var fs = File.OpenRead(json);
                    var summary = await JsonSerializer.DeserializeAsync<BackupRunSummary>(fs);
                    if (summary == null) continue;

                    var txt = Path.ChangeExtension(json, ".txt");
                    
                    // Sprawdźmy czy istnieje plik extended dla dodatkowych informacji
                    var baseName = Path.GetFileNameWithoutExtension(json);
                    var extendedJsonPath = Path.Combine(logsDir, baseName + "_extended.json");
                    string? backupType = null;
                    
                    if (File.Exists(extendedJsonPath))
                    {
                        try
                        {
                            var extendedContent = await File.ReadAllTextAsync(extendedJsonPath);
                            var extendedData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(extendedContent);
                            if (extendedData?.TryGetValue("backupType", out var typeElement) == true)
                            {
                                backupType = typeElement.GetString();
                            }
                        }
                        catch { /* Ignore extended file errors */ }
                    }

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
                        TotalTransferredFileSize: summary.TotalTransferredFileSize,
                        BackupType: backupType
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

    public IResult CurrentLog(bool download)
    {
        string text;
        lock (_liveBuffer) text = _liveBuffer.ToString();
        if (download)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            return Results.File(bytes, "text/plain; charset=utf-8", $"udiskbackup-live-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        }
        return Results.Text(text, "text/plain; charset=utf-8");
    }

    public async Task<IReadOnlyList<UsbCandidate>> GetEligibleUsbAsync()
    {
        _logger.LogInformation("Starting GetEligibleUsbAsync - looking for USB devices with USB_BACKUP label");
        
        var res = new List<UsbCandidate>();
        var all = await _disks.GetDisksAsync();
        
        _logger.LogInformation("Found {DiskCount} total disks", all.Count);
        
        foreach (var d in all)
        {
            _logger.LogDebug("Checking disk {DiskPath} - Transport: {Transport}, Vendor: {Vendor}, Model: {Model}", 
                d.Path, d.Transport, d.Vendor, d.Model);
                
            if (!string.Equals(d.Transport, "usb", StringComparison.OrdinalIgnoreCase)) 
            {
                _logger.LogDebug("Skipping disk {DiskPath} - not USB transport (transport: {Transport})", d.Path, d.Transport);
                continue;
            }
            
            _logger.LogInformation("Found USB disk: {DiskPath} ({Vendor} {Model}), checking {PartitionCount} partitions", 
                d.Path, d.Vendor, d.Model, d.Partitions.Count);
                
            foreach (var p in d.Partitions)
            {
                _logger.LogDebug("Checking partition {PartitionPath} - Label: '{Label}', FsType: {FsType}, MountPoint: {MountPoint}", 
                    p.Path, p.Label ?? "null", p.FsType, p.MountPoint ?? "null");
                    
                if (!string.Equals(p.Label, "USB_BACKUP", StringComparison.OrdinalIgnoreCase)) 
                {
                    _logger.LogDebug("Skipping partition {PartitionPath} - label '{Label}' doesn't match 'USB_BACKUP'", 
                        p.Path, p.Label ?? "null");
                    continue;
                }
                
                _logger.LogInformation("Found eligible USB_BACKUP partition: {PartitionPath} (mounted at: {MountPoint})", 
                    p.Path, p.MountPoint ?? "not mounted");
                    
                res.Add(new UsbCandidate(
                    DiskPath: d.Path,
                    Vendor: d.Vendor,
                    Model: d.Model,
                    Serial: d.Serial,
                    Device: p.Path,
                    MountPoint: p.MountPoint,
                    FsType: p.FsType
                ));
            }
        }
        
        _logger.LogInformation("GetEligibleUsbAsync completed - found {EligibleCount} eligible USB_BACKUP devices", res.Count);
        return res;
    }

    public async Task<MountResult> MountAsync(string device)
    {
        if (string.IsNullOrWhiteSpace(device)) throw new ArgumentException("device");
        
        _logger.LogInformation("Attempting to mount device: {Device}", device);
        
        var disks = await _disks.GetDisksAsync();
        foreach (var disk in disks)
        {
            foreach (var partition in disk.Partitions)
            {
                if (partition.Path == device && !string.IsNullOrEmpty(partition.MountPoint))
                {
                    _logger.LogInformation("Device {Device} is already mounted at {MountPoint}", device, partition.MountPoint);
                    return new MountResult(true, device, partition.MountPoint, $"Already mounted at {partition.MountPoint}");
                }
            }
        }

        if (!File.Exists(device))
        {
            var errorMsg = $"Device {device} does not exist";
            _logger.LogError(errorMsg);
            return new MountResult(false, device, null, errorMsg);
        }

        var (exit, so, se) = await Run("udisksctl", $"mount -b {device}");
        var all = (so ?? "") + "\n" + (se ?? "");
        
        _logger.LogInformation("udisksctl mount result - Exit: {ExitCode}, Output: {Output}", exit, all.Trim());
        
        string? mp = null;
        var m = Regex.Match(all, @"\bat\s+(.+?)\.\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (m.Success) 
        {
            mp = m.Groups[1].Value.Trim();
            _logger.LogInformation("Extracted mount point from output: {MountPoint}", mp);
        }
        
        if (exit != 0)
        {
            _logger.LogWarning("udisksctl failed, trying alternative mount method");
            return await TryAlternativeMount(device, all.Trim());
        }
        
        return new MountResult(exit == 0, device, mp, all.Trim());
    }

    private async Task<MountResult> TryAlternativeMount(string device, string previousError)
    {
        try
        {
            var mountPoint = $"/mnt/usb_backup_{Path.GetFileName(device)}";
            
            _logger.LogInformation("Trying alternative mount for {Device} at {MountPoint} (as root: {IsRoot})", 
                device, mountPoint, _isRoot);
            
            var mkdirCommand = _isRoot ? "mkdir" : "sudo";
            var mkdirArgs = _isRoot ? $"-p {mountPoint}" : $"mkdir -p {mountPoint}";
            
            var (exitMkdir, soMkdir, seMkdir) = await Run(mkdirCommand, mkdirArgs);
            if (exitMkdir != 0)
            {
                var errorMsg = $"Failed to create mount directory: {soMkdir} {seMkdir}";
                _logger.LogError(errorMsg);
                return new MountResult(false, device, null, $"{previousError}\n{errorMsg}");
            }

            var mountCommand = _isRoot ? "mount" : "sudo";
            var mountArgs = _isRoot ? $"{device} {mountPoint}" : $"mount {device} {mountPoint}";
            
            var (exitMount, soMount, seMount) = await Run(mountCommand, mountArgs);
            var mountOutput = $"{soMount} {seMount}".Trim();
            
            if (exitMount == 0)
            {
                _logger.LogInformation("Successfully mounted {Device} at {MountPoint} using alternative method", device, mountPoint);
                return new MountResult(true, device, mountPoint, $"Mounted using {mountCommand} at {mountPoint}");
            }
            else
            {
                var errorMsg = $"Alternative mount failed: {mountOutput}";
                _logger.LogError(errorMsg);
                return new MountResult(false, device, null, $"{previousError}\n{errorMsg}");
            }
        }
        catch (Exception ex)
        {
            var errorMsg = $"Exception during alternative mount: {ex.Message}";
            _logger.LogError(ex, errorMsg);
            return new MountResult(false, device, null, $"{previousError}\n{errorMsg}");
        }
    }

    public async Task<MountResult?> AutoMountUsbBackupAsync()
    {
        _logger.LogInformation("Starting auto-mount process for USB_BACKUP device");
        
        try
        {
            var eligibleDevices = await GetEligibleUsbAsync();
            _logger.LogInformation("Found {Count} eligible USB_BACKUP devices", eligibleDevices.Count);
            
            if (eligibleDevices.Count == 0)
            {
                _logger.LogWarning("No eligible USB_BACKUP devices found - checking all disks for diagnostics");
                
                var allDisks = await _disks.GetDisksAsync();
                _logger.LogInformation("Total disks found: {Count}", allDisks.Count);
                
                foreach (var disk in allDisks)
                {
                    _logger.LogInformation("Disk {Path}: Transport={Transport}, Vendor={Vendor}, Model={Model}, Partitions={PartitionCount}",
                        disk.Path, disk.Transport, disk.Vendor, disk.Model, disk.Partitions.Count);
                        
                    foreach (var partition in disk.Partitions)
                    {
                        _logger.LogInformation("  Partition {Path}: Label='{Label}', FsType={FsType}, MountPoint={MountPoint}",
                            partition.Path, partition.Label ?? "null", partition.FsType ?? "null", partition.MountPoint ?? "not mounted");
                    }
                }
                
                return null;
            }
            
            var targetDevice = eligibleDevices.FirstOrDefault(d => string.IsNullOrEmpty(d.MountPoint)) 
                             ?? eligibleDevices.First();
            
            _logger.LogInformation("Selected target device: {Device} (currently mounted at: {MountPoint})", 
                targetDevice.Device, targetDevice.MountPoint ?? "not mounted");
            
            if (!string.IsNullOrEmpty(targetDevice.MountPoint))
            {
                _logger.LogInformation("USB_BACKUP already mounted at {MountPoint}", targetDevice.MountPoint);
                return new MountResult(true, targetDevice.Device, targetDevice.MountPoint, 
                    $"Already mounted at {targetDevice.MountPoint}");
            }
            
            var preferredMountPoint = "/mnt/usb_backup";
            _logger.LogInformation("Attempting to mount {Device} at {MountPoint}", targetDevice.Device, preferredMountPoint);
            
            var mountResult = await MountToSpecificPathAsync(targetDevice.Device, preferredMountPoint);
            
            if (mountResult.Success)
            {
                _logger.LogInformation("Successfully auto-mounted USB_BACKUP at {MountPoint}", preferredMountPoint);
            }
            else
            {
                _logger.LogError("Failed to auto-mount USB_BACKUP: {Error}", mountResult.Output);
            }
            
            return mountResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during auto-mount process: {Message}", ex.Message);
            return new MountResult(false, "unknown", null, $"Exception: {ex.Message}");
        }
    }

    private async Task<MountResult> MountToSpecificPathAsync(string devicePath, string mountPath)
    {
        _logger.LogInformation("Attempting to mount {DevicePath} to {MountPath}", devicePath, mountPath);
        
        try
        {
            if (IsMountPointAlreadyMounted(mountPath))
            {
                _logger.LogInformation("Device already mounted at {MountPath}", mountPath);
                return new MountResult(true, devicePath, mountPath, "Already mounted");
            }

            if (!Directory.Exists(mountPath))
            {
                _logger.LogInformation("Creating mount directory {MountPath}", mountPath);
                Directory.CreateDirectory(mountPath);
            }

            if (_hasUdisksctl.Value)
            {
                return await MountWithUdisksctl(devicePath, mountPath);
            }
            else
            {
                return await MountWithSystemMount(devicePath, mountPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mount {DevicePath} to {MountPath}", devicePath, mountPath);
            return new MountResult(false, devicePath, null, $"Exception: {ex.Message}");
        }
    }

    public async Task<UnmountResult> UnmountAsync(string? device, string? mountPoint, bool powerOff)
    {
        _logger.LogInformation("Starting unmount - Device: {Device}, MountPoint: {MountPoint}, PowerOff: {PowerOff}", 
            device, mountPoint, powerOff);

        string? dev = device;
        string? mp = mountPoint;
        
        if (string.IsNullOrWhiteSpace(dev))
        {
            if (string.IsNullOrWhiteSpace(mountPoint)) throw new ArgumentException("Brak device i mountPoint");
            var all = await _disks.GetDisksAsync();
            foreach (var d in all)
            foreach (var p in d.Partitions)
                if (string.Equals(p.MountPoint, mountPoint, StringComparison.Ordinal))
                {
                    dev = p.Path;
                    break;
                }
            if (string.IsNullOrWhiteSpace(dev)) 
            {
                _logger.LogError("Nie znaleziono urządzenia dla mountpointu: {MountPoint}", mountPoint);
                throw new InvalidOperationException("Nie znaleziono urządzenia dla mountpointu");
            }
        }

        var output = new StringBuilder();
        bool unmountSuccess = false;

        _logger.LogInformation("Trying udisksctl unmount for device: {Device}", dev);
        var (e1, so1, se1) = await Run("udisksctl", $"unmount -b {dev}");
        var udisksOutput = $"{so1?.Trim()} {se1?.Trim()}".Trim();
        output.AppendLine($"udisksctl unmount: {udisksOutput}");
        
        if (e1 == 0)
        {
            _logger.LogInformation("udisksctl unmount successful for {Device}", dev);
            unmountSuccess = true;
        }
        else
        {
            _logger.LogWarning("udisksctl unmount failed for {Device}: {Output}", dev, udisksOutput);
            
            if (!string.IsNullOrEmpty(mp))
            {
                _logger.LogInformation("Trying sudo umount for mount point: {MountPoint}", mp);
                var (e2, so2, se2) = await Run("sudo", $"umount {mp}");
                var umountOutput = $"{so2?.Trim()} {se2?.Trim()}".Trim();
                output.AppendLine($"sudo umount: {umountOutput}");
                
                if (e2 == 0)
                {
                    _logger.LogInformation("sudo umount successful for {MountPoint}", mp);
                    unmountSuccess = true;
                }
                else
                {
                    _logger.LogWarning("sudo umount failed for {MountPoint}: {Output}", mp, umountOutput);
                }
            }
            
            if (!unmountSuccess)
            {
                _logger.LogInformation("Trying sudo umount for device: {Device}", dev);
                var (e3, so3, se3) = await Run("sudo", $"umount {dev}");
                var umountDevOutput = $"{so3?.Trim()} {se3?.Trim()}".Trim();
                output.AppendLine($"sudo umount device: {umountDevOutput}");
                
                if (e3 == 0)
                {
                    _logger.LogInformation("sudo umount device successful for {Device}", dev);
                    unmountSuccess = true;
                }
                else
                {
                    _logger.LogError("All unmount methods failed for {Device}", dev);
                }
            }
        }

        bool powerOffSuccess = true;
        if (powerOff && unmountSuccess)
        {
            _logger.LogInformation("Attempting power-off for device: {Device}", dev);
            var baseDev = BaseBlockDevice(dev!);
            var (e4, so4, se4) = await Run("udisksctl", $"power-off -b {baseDev}");
            var powerOffOutput = $"{so4?.Trim()} {se4?.Trim()}".Trim();
            output.AppendLine($"udisksctl power-off: {powerOffOutput}");
            
            powerOffSuccess = e4 == 0;
            if (powerOffSuccess)
            {
                _logger.LogInformation("Power-off successful for {Device}", baseDev);
            }
            else
            {
                _logger.LogWarning("Power-off failed for {Device}: {Output}", baseDev, powerOffOutput);
                
                _logger.LogInformation("Trying eject as power-off alternative for {Device}", baseDev);
                var (e5, so5, se5) = await Run("eject", baseDev);
                var ejectOutput = $"{so5?.Trim()} {se5?.Trim()}".Trim();
                output.AppendLine($"eject: {ejectOutput}");
                
                if (e5 == 0)
                {
                    _logger.LogInformation("Eject successful for {Device}", baseDev);
                    powerOffSuccess = true;
                }
            }
        }

        var finalSuccess = unmountSuccess && (powerOff ? powerOffSuccess : true);
        var result = new UnmountResult(finalSuccess, dev!, mountPoint, output.ToString().Trim());
        
        _logger.LogInformation("Unmount operation completed - Success: {Success}, Device: {Device}, Output: {Output}", 
            finalSuccess, dev, result.Output);
            
        return result;
    }

    private static string BaseBlockDevice(string device)
    {
        var name = Path.GetFileName(device);
        if (name.StartsWith("nvme", StringComparison.OrdinalIgnoreCase))
            name = Regex.Replace(name, @"p\d+$", "");
        else
            name = Regex.Replace(name, @"\d+$", "");
        return "/dev/" + name;
    }

    private static string Classify(string line)
    {
        if (Regex.IsMatch(line, @"ERROR|rsync error|failed", RegexOptions.IgnoreCase)) return "error";
        if (Regex.IsMatch(line, @"\d+%|\bto-check=\d+/\d+\b", RegexOptions.IgnoreCase)) return "progress";
        return "info";
    }

    private static string RsyncArgs(string source, string linkDest, string dest, string deleted, bool dryRun)
    {
        // Kopia przyrostowa z --link-dest do oszczędzania miejsca
        var common = $"-a --delete --backup --backup-dir=\"{deleted}\" --link-dest=\"{linkDest}\" --human-readable --info=stats2,progress2 --no-inc-recursive";
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

    private string MakeEnhancedHumanSummary(BackupRunSummary s, string backupType, Dictionary<string, object>? extendedData = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("KOPIA ZAPASOWA - PODSUMOWANIE ROZSZERZONE");
        sb.AppendLine("=".PadLeft(50, '='));
        sb.AppendLine($"Typ kopii       : {backupType.ToUpper()}");
        sb.AppendLine($"Rozpoczęta      : {s.StartedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Zakończona      : {s.EndedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Czas trwania    : {s.Duration}");
        sb.AppendLine($"Status          : {(s.Success ? "✓ SUKCES" : "✗ BŁĄD")} (kod: {s.ExitCode})");
        sb.AppendLine();
        
        sb.AppendLine("ŚCIEŻKI:");
        sb.AppendLine($"  Źródło        : {s.Source}");
        sb.AppendLine($"  Cel           : {s.Target}");
        if (!string.IsNullOrEmpty(s.DeletedDir))
            sb.AppendLine($"  .deleted      : {s.DeletedDir}");
        sb.AppendLine();

        // Informacje o miejscu na dysku
        sb.AppendLine("MIEJSCE NA DYSKU:");
        sb.AppendLine($"  Wolne przed   : {Human(s.FreeBytesBefore ?? 0)}");
        sb.AppendLine($"  Wolne po      : {Human(s.FreeBytesAfter ?? 0)}");
        var diskChange = (s.FreeBytesAfter ?? 0) - (s.FreeBytesBefore ?? 0);
        if (diskChange < 0)
            sb.AppendLine($"  Użyte         : {Human(-diskChange)} (↓ zmniejszenie miejsca)");
        else if (diskChange > 0)
            sb.AppendLine($"  Zwolnione     : {Human(diskChange)} (↑ zwiększenie miejsca)");
        sb.AppendLine();

        // Statystyki rsync
        sb.AppendLine("STATYSTYKI RSYNC:");
        sb.AppendLine($"  Pliki razem         : {s.NumberOfFiles?.ToString("N0") ?? "-"}");
        sb.AppendLine($"  Katalogi            : {s.NumberOfDirs?.ToString("N0") ?? "-"}");
        sb.AppendLine($"  Przesłane pliki     : {s.NumberOfTransferredFiles?.ToString("N0") ?? "-"}");
        if ((s.NumberOfDeletedFiles ?? 0) > 0)
            sb.AppendLine($"  Usunięte pliki      : {s.NumberOfDeletedFiles?.ToString("N0") ?? "-"}");
        
        sb.AppendLine($"  Rozmiar danych      : {Human(s.TotalFileSize ?? 0)}");
        sb.AppendLine($"  Przesłane dane      : {Human(s.TotalTransferredFileSize ?? 0)}");
        
        if ((s.TotalFileSize ?? 0) > 0)
        {
            var transferRatio = (double)(s.TotalTransferredFileSize ?? 0) / (s.TotalFileSize ?? 0) * 100;
            sb.AppendLine($"  Transfer ratio      : {transferRatio:0.##}%");
        }
        
        if ((s.LiteralData ?? 0) > 0)
            sb.AppendLine($"  Literal data        : {Human(s.LiteralData ?? 0)}");
        if ((s.MatchedData ?? 0) > 0)
            sb.AppendLine($"  Matched data        : {Human(s.MatchedData ?? 0)}");
        
        sb.AppendLine($"  Wysłane/Odebrane    : {Human(s.TotalBytesSent ?? 0)} / {Human(s.TotalBytesReceived ?? 0)}");

        // Dodatkowe informacje z extendedData
        if (extendedData != null && extendedData.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("DODATKOWE INFORMACJE:");
            
            if (extendedData.TryGetValue("space_savings_ratio", out var savingsRatio))
            {
                if (savingsRatio is double ratio && ratio > 0)
                {
                    sb.AppendLine($"  Oszczędność miejsca : {ratio * 100:0.##}%");
                }
            }

            if (extendedData.TryGetValue("literal_data", out var literalData) && extendedData.TryGetValue("matched_data", out var matchedData))
            {
                if (literalData is long literal && matchedData is long matched)
                {
                    sb.AppendLine($"  Nowe dane           : {Human(literal)}");
                    sb.AppendLine($"  Zdeduplikowane      : {Human(matched)}");
                }
            }

            if (extendedData.TryGetValue("linked_from", out var linkedFrom) && !string.IsNullOrEmpty(linkedFrom?.ToString()))
            {
                sb.AppendLine($"  Link-dest z         : {linkedFrom}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("=".PadLeft(50, '='));
        
        return sb.ToString();
    }

    private static string Human(long bytes)
    {
        string[] u = ["B","KB","MB","GB","TB","PB"];
        double n = bytes; int i = 0;
        while (n >= 1024 && i < u.Length - 1) { n /= 1024; i++; }
        return n < 10 ? $"{n:0.##} {u[i]}" : n < 100 ? $"{n:0.#} {u[i]}" : $"{n:0} {u[i]}";
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
        long? TotalTransferredFileSize,
        string? BackupType = null
    );

    private bool IsMountPointAlreadyMounted(string mountPath)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "findmnt",
                    Arguments = $"-t ext2,ext3,ext4,vfat,ntfs,exfat -n -o TARGET",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            
            return output.Split('\n').Any(line => line.Trim() == mountPath);
        }
        catch
        {
            try
            {
                return Directory.Exists(mountPath) && Directory.GetFileSystemEntries(mountPath).Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    private async Task<MountResult> MountWithUdisksctl(string devicePath, string mountPath)
    {
        _logger.LogInformation("Mounting with udisksctl: {DevicePath} -> {MountPath}", devicePath, mountPath);
        
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "udisksctl",
                Arguments = $"mount -b {devicePath}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        
        if (process.ExitCode == 0)
        {
            _logger.LogInformation("udisksctl mount successful: {Output}", output);
            return new MountResult(true, devicePath, mountPath, output);
        }
        else
        {
            _logger.LogWarning("udisksctl mount failed: {Error}", error);
            return new MountResult(false, devicePath, null, error);
        }
    }

    private async Task<MountResult> MountWithSystemMount(string devicePath, string mountPath)
    {
        _logger.LogInformation("Mounting with system mount: {DevicePath} -> {MountPath}", devicePath, mountPath);
        
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "mount",
                Arguments = $"{devicePath} {mountPath}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        
        if (process.ExitCode == 0)
        {
            _logger.LogInformation("System mount successful");
            return new MountResult(true, devicePath, mountPath, output);
        }
        else
        {
            _logger.LogWarning("System mount failed: {Error}", error);
            return new MountResult(false, devicePath, null, error);
        }
    }

    private static bool IsSymbolicLink(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }

    private static void CreateSymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            // Na Linux używamy ln -s
            var psi = new ProcessStartInfo("ln", $"-sf \"{targetPath}\" \"{linkPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            process?.WaitForExit();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Nie udało się utworzyć symlink'u: {ex.Message}", ex);
        }
    }
}
