using Microsoft.AspNetCore.SignalR;
using UDiskBackup.Services;
using UDiskBackup.Hubs;
using UDiskBackup.Models;
using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<DiskInfoService>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<SourceService>();
builder.Services.AddHostedService<UDisksMonitor>();

builder.Services.AddControllers();
builder.Services.AddSignalR();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseStaticFiles();
app.UseCors("AllowAll");
app.UseRouting();

app.MapGet("/", () => Results.Redirect("/index.html"));

app.MapControllers();

app.MapHub<DiskHub>("/hubs/disks");
app.MapHub<BackupHub>("/hubs/backup");

app.Run();