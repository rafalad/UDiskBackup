using Microsoft.AspNetCore.Mvc;
using UDiskBackup.Services;
using System.Reflection;
using System.IO;

namespace UDiskBackup.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DebugController : ControllerBase
{
    private readonly BackupService _backupService;
    private readonly DiskInfoService _diskInfoService;

    public DebugController(BackupService backupService, DiskInfoService diskInfoService)
    {
        _backupService = backupService;
        _diskInfoService = diskInfoService;
    }

    [HttpGet("all-disks")]
    public async Task<IActionResult> GetAllDisks()
    {
        try
        {
            var allDisks = await _diskInfoService.GetDisksAsync();
            return Ok(new { 
                success = true, 
                count = allDisks.Count, 
                disks = allDisks.Select(d => new {
                    path = d.Path,
                    transport = d.Transport,
                    vendor = d.Vendor,
                    model = d.Model,
                    partitions = d.Partitions.Select(p => new {
                        path = p.Path,
                        label = p.Label,
                        fsType = p.FsType,
                        mountPoint = p.MountPoint
                    })
                })
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("eligible-usb")]
    public async Task<IActionResult> GetEligibleUsb()
    {
        try
        {
            var eligible = await _backupService.GetEligibleUsbAsync();
            return Ok(new { 
                success = true, 
                count = eligible.Count, 
                devices = eligible 
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("version")]
    public IActionResult GetVersionInfo()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version?.ToString() ?? "Unknown";
            var buildDate = GetBuildDate(assembly);
            var gitCommit = GetGitCommitInfo();

            return Ok(new
            {
                version = version,
                buildDate = buildDate,
                gitCommit = gitCommit.commitHash,
                gitCommitDate = gitCommit.commitDate,
                gitBranch = gitCommit.branch,
                assemblyName = assembly.GetName().Name,
                framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString()
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private static DateTime GetBuildDate(Assembly assembly)
    {
        var buildDateAttribute = assembly.GetCustomAttribute<System.Reflection.AssemblyMetadataAttribute>();
        if (buildDateAttribute != null && buildDateAttribute.Key == "BuildDate")
        {
            if (DateTime.TryParse(buildDateAttribute.Value, out var buildDate))
                return buildDate;
        }

        // Fallback to file creation time
        try
        {
            var location = assembly.Location;
            if (!string.IsNullOrEmpty(location) && System.IO.File.Exists(location))
            {
                return System.IO.File.GetCreationTime(location);
            }
        }
        catch { }

        return DateTime.UtcNow;
    }

    private static (string commitHash, string commitDate, string branch) GetGitCommitInfo()
    {
        try
        {
            // Simplified git info - just return basic info
            return ("dev-commit", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"), "main");
        }
        catch
        {
            return ("unknown", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"), "unknown");
        }
    }
}