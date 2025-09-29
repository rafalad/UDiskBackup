using UDiskBackup.Models;

namespace UDiskBackup.Services;

/// <summary>
/// Rozszerzenia serwisów dla komponentów Blazor
/// </summary>
public static class ServiceExtensions
{
    public static async Task<List<DiskModel>> GetAllDisksAsync(this DiskInfoService service)
    {
        var disks = await service.GetDisksAsync();
        return disks.Select(DiskModel.FromDiskDevice).ToList();
    }
    
    public static async Task<List<DiskModel>> GetUsbDisksAsync(this DiskInfoService service)
    {
        var allDisks = await service.GetDisksAsync();
        var usbDisks = allDisks.Where(d => 
            !string.IsNullOrEmpty(d.Transport) && 
            d.Transport.ToLower().Contains("usb")
        );
        return usbDisks.Select(DiskModel.FromDiskDevice).ToList();
    }
    
    public static async Task<DisksSummary> GetSummaryAsync(this DiskInfoService service)
    {
        return await service.GetDisksSummaryAsync();
    }
    
    public static async Task<List<BackupTargetModel>> GetTargetsAsync(this BackupService service)
    {
        var targets = await service.GetUsbTargetsAsync();
        return targets.Select(BackupTargetModel.FromUsbTarget).ToList();
    }
    
    public static async Task<BackupPlanModel> PlanAsync(this BackupService service, string targetMount)
    {
        var plan = await service.PlanAsync(targetMount);
        return BackupPlanModel.FromBackupPlan(plan);
    }
    
    public static async Task<StartBackupResult> StartAsync(this BackupService service, string targetMount)
    {
        var operationId = await service.StartAsync(targetMount);
        return new StartBackupResult { OperationId = operationId };
    }
    
    public static async Task<StopBackupResult> StopAsync(this BackupService service)
    {
        var success = await service.StopBackupAsync();
        return new StopBackupResult { Message = success ? "Backup zatrzymany" : "Nie udało się zatrzymać backupu" };
    }
    
    public static BackupStatusModel GetStatusAsync(this BackupService service)
    {
        var status = service.GetCurrentStatus();
        if (status == null)
        {
            return new BackupStatusModel { State = "Idle", Message = "Ready", OperationId = "" };
        }
        return BackupStatusModel.FromBackupStatus(status);
    }
    
    public static async Task<List<Models.BackupHistoryItem>> GetHistoryAsync(this BackupService service, string? query = null)
    {
        // Parse query parameter for targetMount filter
        string? targetMount = null;
        if (!string.IsNullOrEmpty(query) && query.StartsWith("?targetMount="))
        {
            targetMount = Uri.UnescapeDataString(query.Substring("?targetMount=".Length));
        }
        
        var history = await service.GetHistoryAsync(targetMount);
        // Convert from service BackupHistoryItem to Models.BackupHistoryItem
        return history.Select(h => new Models.BackupHistoryItem(
            h.OperationId,
            h.StartedAtUtc,
            h.EndedAtUtc,
            h.Success,
            h.Duration,
            h.TargetMount,
            h.TargetLabel,
            h.SummaryJsonPath,
            h.SummaryTxtPath,
            h.NumberOfTransferredFiles,
            h.TotalTransferredFileSize,
            h.BackupType
        )).ToList();
    }
    
    public static async Task<SourceStatusModel> GetStatusAsync(this SourceService service)
    {
        var status = await service.GetStatusAsync();
        return new SourceStatusModel
        {
            IsAvailable = status.Exists && status.Readable,
            Path = status.Path,
            UsedBytes = status.UsedBytes
        };
    }
}