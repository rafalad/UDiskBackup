using Microsoft.AspNetCore.Mvc;
using UDiskBackup.Models;
using UDiskBackup.Services;

namespace UDiskBackup.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UsbController : ControllerBase
{
    private readonly BackupService _backupService;

    public UsbController(BackupService backupService)
    {
        _backupService = backupService;
    }

    [HttpGet("eligible")]
    public async Task<IActionResult> GetEligible()
    {
        try
        {
            var list = await _backupService.GetEligibleUsbAsync();
            return Ok(list);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("mount")]
    public async Task<IActionResult> Mount([FromBody] UsbMountRequest request)
    {
        try
        {
            var result = await _backupService.MountAsync(request.Device);
            return result.Success ? Ok(result) : BadRequest(new { error = result.Output });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("auto-mount-backup")]
    public async Task<IActionResult> AutoMountBackup()
    {
        try
        {
            var result = await _backupService.AutoMountUsbBackupAsync();
            if (result == null)
            {
                return NotFound(new { error = "USB_BACKUP device not found" });
            }
            return result.Success ? Ok(result) : BadRequest(new { error = result.Output });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("unmount")]
    public async Task<IActionResult> Unmount([FromBody] UsbUnmountRequest request)
    {
        try
        {
            var result = await _backupService.UnmountAsync(request.Device, request.MountPoint, request.PowerOff);
            return result.Success ? Ok(result) : BadRequest(new { error = result.Output });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}