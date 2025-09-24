using Microsoft.AspNetCore.SignalR;
using Tmds.DBus;

namespace UDiskBackup;

[DBusInterface("org.freedesktop.DBus.ObjectManager")]
interface IObjectManager : IDBusObject
{
    Task<IDictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>>> GetManagedObjectsAsync();
    Task<IDisposable> WatchInterfacesAddedAsync(Action<(ObjectPath path, IDictionary<string, IDictionary<string, object>> interfaces)> handler);
    Task<IDisposable> WatchInterfacesRemovedAsync(Action<(ObjectPath path, string[] interfaces)> handler);
}

public class UDisksMonitor : BackgroundService
{
    private readonly IHubContext<DiskHub> _hub;
    private Connection? _conn;
    private IDisposable? _subAdd, _subRem;

    public UDisksMonitor(IHubContext<DiskHub> hub) => _hub = hub;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _conn = new Connection(Address.System);
            await _conn.ConnectAsync();

            var mgr = _conn.CreateProxy<IObjectManager>("org.freedesktop.UDisks2", "/org/freedesktop/UDisks2");

            _subAdd = await mgr.WatchInterfacesAddedAsync(evt =>
            {
                if (IsUsbDrive(evt.interfaces))
                {
                    var name = PreferredDevice(evt.interfaces) ?? "(unknown)";
                    _ = _hub.Clients.All.SendAsync("deviceAdded", new { name, when = DateTimeOffset.UtcNow });
                }
            });

            _subRem = await mgr.WatchInterfacesRemovedAsync(evt =>
            {
                _ = _hub.Clients.All.SendAsync("deviceRemoved", new { path = evt.path.ToString(), when = DateTimeOffset.UtcNow });
            });

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (TaskCanceledException) { }
        catch
        {
            
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _subAdd?.Dispose();
        _subRem?.Dispose();
        _conn?.Dispose();
        return base.StopAsync(cancellationToken);
    }

    private static bool IsUsbDrive(IDictionary<string, IDictionary<string, object>> ifaces)
    {
        if (ifaces.TryGetValue("org.freedesktop.UDisks2.Drive", out var drive) &&
            drive.TryGetValue("ConnectionBus", out var bus) && bus is string s &&
            s.Equals("usb", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static string? PreferredDevice(IDictionary<string, IDictionary<string, object>> ifaces)
    {
        if (ifaces.TryGetValue("org.freedesktop.UDisks2.Block", out var blk) &&
            blk.TryGetValue("PreferredDevice", out var dev) && dev is string s)
            return s;

        return null;
    }
}
