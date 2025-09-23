using Microsoft.AspNetCore.SignalR;
using UDiskBackup;
using System.Linq;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
builder.Services.AddSingleton<DiskInfoService>();
builder.Services.AddHostedService<UDisksMonitor>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<SourceService>();

var app = builder.Build();

app.MapGet("/", () => Results.Redirect("/index.html"));

// Wszystkie dyski
app.MapGet("/api/disks", async (DiskInfoService svc) => await svc.GetDisksAsync());

// Tylko dyski USB
app.MapGet("/api/disks/usb", async (DiskInfoService svc) =>
{
    var all = await svc.GetDisksAsync();
    return all.Where(d => string.Equals(d.Transport, "usb", StringComparison.OrdinalIgnoreCase));
});

// Status źródła
app.MapGet("/api/source/status", async (SourceService svc, string? path) =>
    await svc.GetStatusAsync(path ?? "/mnt/shared"));

// Backup: cele USB
app.MapGet("/api/backup/targets", async (BackupService svc) => await svc.GetUsbTargetsAsync());

// Backup: plan
app.MapGet("/api/backup/plan", async (string targetMount, BackupService svc) =>
{
    var plan = await svc.PlanAsync(targetMount);
    return Results.Json(plan);
});

// Backup: start
app.MapPost("/api/backup/start", async (StartBackupRequest req, BackupService svc) =>
{
    var plan = await svc.PlanAsync(req.TargetMount);
    if (!plan.EnoughSpace)
    {
        var need = plan.EstimatedBytes;
        var free = plan.FreeBytes;
        return Results.BadRequest(
            $"Za mało miejsca na {req.TargetMount}. Potrzeba ~{need} B, wolne {free} B. " +
            $"Katalog docelowy: {plan.TargetBackupDir}");
    }

    var id = await svc.StartAsync(req.TargetMount);
    return Results.Json(new BackupStartResponse(id));
});

// Backup: historia (opcjonalnie query: ?targetMount=/media/pendrive&take=50)
app.MapGet("/api/backup/history", async (BackupService svc, string? targetMount, int? take) =>
{
    var list = await svc.GetHistoryAsync(targetMount, take ?? 50);
    return Results.Json(list);
});

app.MapHub<DiskHub>("/hubs/disks");
app.MapHub<BackupHub>("/hubs/backup");

app.UseDefaultFiles();
app.UseStaticFiles();

app.Run();
