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

app.MapGet("/api/disks", async (DiskInfoService svc) => await svc.GetDisksAsync());

app.MapGet("/api/disks/usb", async (DiskInfoService svc) =>
{
    var all = await svc.GetDisksAsync();
    return all.Where(d => string.Equals(d.Transport, "usb", StringComparison.OrdinalIgnoreCase));
});

app.MapGet("/api/source/status", async (SourceService svc, string? path) =>
    await svc.GetStatusAsync(path ?? "/mnt/shared"));

app.MapGet("/api/backup/targets", async (BackupService svc) => await svc.GetUsbTargetsAsync());

app.MapGet("/api/backup/plan", async (string targetMount, BackupService svc) =>
{
    var plan = await svc.PlanAsync(targetMount);
    return Results.Json(plan);
});

app.MapPost("/api/backup/start", async (StartBackupRequest req, BackupService svc) =>
{
    var plan = await svc.PlanAsync(req.TargetMount);
    if (!plan.EnoughSpace)
    {
        var need = plan.EstimatedBytes;
        var free = plan.FreeBytes;
        return Results.BadRequest($"Za mało miejsca na {req.TargetMount}. Potrzeba ~{need} B, wolne {free} B. Katalog docelowy: {plan.TargetBackupDir}");
    }
    var id = await svc.StartAsync(req.TargetMount);
    return Results.Json(new BackupStartResponse(id));
});

app.MapGet("/api/backup/history", async (BackupService svc, string? targetMount, int? take) =>
{
    var list = await svc.GetHistoryAsync(targetMount, take ?? 50);
    return Results.Json(list);
});

app.MapGet("/api/backup/current-log", (BackupService svc, bool? download) =>
    svc.CurrentLog(download == true));

app.MapHub<DiskHub>("/hubs/disks");
app.MapHub<BackupHub>("/hubs/backup");

app.UseDefaultFiles();
app.UseStaticFiles();

app.Run();
