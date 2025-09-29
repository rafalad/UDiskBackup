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

// Dodaj Blazor SignalR Service
builder.Services.AddScoped<BlazorSignalRService>();

builder.Services.AddControllers();
builder.Services.AddSignalR();

// Dodaj Blazor Server
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

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

app.MapControllers();
app.MapRazorPages();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.MapHub<DiskHub>("/hubs/disks");
app.MapHub<BackupHub>("/hubs/backup");

app.Run();