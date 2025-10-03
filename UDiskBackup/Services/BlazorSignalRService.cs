using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Components;
using UDiskBackup.Hubs;
using UDiskBackup.Services;

namespace UDiskBackup.Services;

/// <summary>
/// Serwis do obsługi SignalR w komponentach Blazor
/// </summary>
public class BlazorSignalRService : IAsyncDisposable
{
    private readonly IHubContext<DiskHub> _diskHubContext;
    private readonly IHubContext<BackupHub> _backupHubContext;
    private readonly List<ISignalRSubscription> _subscriptions = new();

    public BlazorSignalRService(
        IHubContext<DiskHub> diskHubContext, 
        IHubContext<BackupHub> backupHubContext)
    {
        _diskHubContext = diskHubContext;
        _backupHubContext = backupHubContext;
    }

    /// <summary>
    /// Subskrybuje zdarzenia dysku dla komponentu
    /// </summary>
    public ISignalRSubscription SubscribeToDisks(ComponentBase component, 
        Func<Task> onDeviceChanged)
    {
        var subscription = new DiskSubscription(component, onDeviceChanged, _diskHubContext);
        _subscriptions.Add(subscription);
        return subscription;
    }

    /// <summary>
    /// Subskrybuje zdarzenia backupu dla komponentu
    /// </summary>
    public ISignalRSubscription SubscribeToBackup(ComponentBase component,
        Func<string, string, Task> onBackupLog,
        Func<string, string, string, Task> onBackupStatus)
    {
        var subscription = new BackupSubscription(component, onBackupLog, onBackupStatus, _backupHubContext);
        _subscriptions.Add(subscription);
        return subscription;
    }

    /// <summary>
    /// Wysyła aktualizację urządzenia do wszystkich klientów
    /// </summary>
    public async Task NotifyDeviceAddedAsync(string deviceName)
    {
        await _diskHubContext.Clients.All.SendAsync("deviceAdded", new { name = deviceName });
    }

    /// <summary>
    /// Wysyła usunięcie urządzenia do wszystkich klientów
    /// </summary>
    public async Task NotifyDeviceRemovedAsync(string devicePath)
    {
        await _diskHubContext.Clients.All.SendAsync("deviceRemoved", new { path = devicePath });
    }

    /// <summary>
    /// Wysyła log backupu do wszystkich klientów
    /// </summary>
    public async Task NotifyBackupLogAsync(string level, string message, DateTime? timestamp = null)
    {
        await _backupHubContext.Clients.All.SendAsync("backupLog", new 
        { 
            level, 
            message, 
            timestamp = timestamp ?? DateTime.Now
        });
    }

    /// <summary>
    /// Wysyła status backupu do wszystkich klientów
    /// </summary>
    public async Task NotifyBackupStatusAsync(string operationId, string state, string message)
    {
        await _backupHubContext.Clients.All.SendAsync("backupStatus", new 
        { 
            operationId, 
            state, 
            message 
        });
    }

    /// <summary>
    /// Inicjalizuje połączenia SignalR - symuluje test połączenia
    /// </summary>
    public async Task InitializeAsync()
    {
        // W rzeczywistej implementacji Blazor Server, hubContext jest już gotowy
        // Tutaj symulujemy test połączenia
        await Task.Delay(200);
        
        // Testuj czy można wysłać wiadomość testową
        await _diskHubContext.Clients.All.SendAsync("InitializationTest");
        await _backupHubContext.Clients.All.SendAsync("InitializationTest");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var subscription in _subscriptions)
        {
            await subscription.DisposeAsync();
        }
        _subscriptions.Clear();
    }
}

/// <summary>
/// Interface dla subskrypcji SignalR
/// </summary>
public interface ISignalRSubscription : IAsyncDisposable
{
    bool IsActive { get; }
}

/// <summary>
/// Subskrypcja zdarzeń dysków
/// </summary>
internal class DiskSubscription : ISignalRSubscription
{
    private readonly ComponentBase _component;
    private readonly Func<Task> _onDeviceChanged;
    private readonly IHubContext<DiskHub> _hubContext;
    private bool _isActive = true;

    public DiskSubscription(ComponentBase component, Func<Task> onDeviceChanged, IHubContext<DiskHub> hubContext)
    {
        _component = component;
        _onDeviceChanged = onDeviceChanged;
        _hubContext = hubContext;
    }

    public bool IsActive => _isActive;

    public async ValueTask DisposeAsync()
    {
        _isActive = false;
        // W prawdziwej implementacji można tu wypisać się z grup SignalR
        await Task.CompletedTask;
    }
}

/// <summary>
/// Subskrypcja zdarzeń backupu
/// </summary>
internal class BackupSubscription : ISignalRSubscription
{
    private readonly ComponentBase _component;
    private readonly Func<string, string, Task> _onBackupLog;
    private readonly Func<string, string, string, Task> _onBackupStatus;
    private readonly IHubContext<BackupHub> _hubContext;
    private bool _isActive = true;

    public BackupSubscription(ComponentBase component, 
        Func<string, string, Task> onBackupLog,
        Func<string, string, string, Task> onBackupStatus,
        IHubContext<BackupHub> hubContext)
    {
        _component = component;
        _onBackupLog = onBackupLog;
        _onBackupStatus = onBackupStatus;
        _hubContext = hubContext;
    }

    public bool IsActive => _isActive;

    public async ValueTask DisposeAsync()
    {
        _isActive = false;
        await Task.CompletedTask;
    }
}