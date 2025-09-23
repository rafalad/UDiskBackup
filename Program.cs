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

// Wszystkie dyski (lsblk -JO + /sys)
app.MapGet("/api/disks", async (DiskInfoService svc) => await svc.GetDisksAsync());

// Tylko dyski USB
app.MapGet("/api/disks/usb", async (DiskInfoService svc) =>
{
    var all = await svc.GetDisksAsync();
    return all.Where(d => string.Equals(d.Transport, "usb", StringComparison.OrdinalIgnoreCase));
});

// Source status (domyślnie /mnt/shared, można podać ?path=/inna/sciezka)
app.MapGet("/api/source/status", async (SourceService svc, string? path) =>
    await svc.GetStatusAsync(path ?? "/mnt/shared"));

// Backup: lista celów (zamontowane partycje USB)
app.MapGet("/api/backup/targets", async (BackupService svc) => await svc.GetUsbTargetsAsync());

// Backup: plan (dry-run + sprawdzenie miejsca)
app.MapGet("/api/backup/plan", async (string targetMount, BackupService svc) =>
{
    var plan = await svc.PlanAsync(targetMount);
    return Results.Json(plan);
});

// Backup: start
app.MapPost("/api/backup/start", async (StartBackupRequest req, BackupService svc) =>
{
    var id = await svc.StartAsync(req.TargetMount);
    return Results.Json(new BackupStartResponse(id));
});

app.MapHub<DiskHub>("/hubs/disks");
app.MapHub<BackupHub>("/hubs/backup");

app.UseDefaultFiles();
app.UseStaticFiles();

app.Run();
