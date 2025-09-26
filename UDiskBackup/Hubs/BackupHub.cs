using Microsoft.AspNetCore.SignalR;
using UDiskBackup.Services;

namespace UDiskBackup.Hubs;

public class BackupHub : Hub
{
    private readonly BackupService _backupService;

    public BackupHub(BackupService backupService)
    {
        _backupService = backupService;
    }

    public override async Task OnConnectedAsync()
    {
        var currentStatus = _backupService.GetCurrentStatus();
        if (currentStatus != null)
        {
            await Clients.Caller.SendAsync("backupStatus", currentStatus);
        }
        await base.OnConnectedAsync();
    }
}
