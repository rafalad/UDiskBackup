using Microsoft.AspNetCore.Mvc;
using UDiskBackup.Services;
using System.Reflection;

namespace UDiskBackup.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DisksController : ControllerBase
{
    private readonly DiskInfoService _diskInfoService;
    private readonly BackupService _backupService;

    public DisksController(DiskInfoService diskInfoService, BackupService backupService)
    {
        _diskInfoService = diskInfoService;
        _backupService = backupService;
    }

    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary()
    {
        try
        {
            var summary = await _diskInfoService.GetDisksSummaryAsync();
            return Ok(summary);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("all")]
    public async Task<IActionResult> GetAllDisks()
    {
        try
        {
            var allDisks = await _diskInfoService.GetDisksAsync();
            return Ok(allDisks);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("usb")]
    public async Task<IActionResult> GetUsbTargets()
    {
        try
        {
            var usbTargets = await _backupService.GetUsbTargetsAsync();
            return Ok(usbTargets);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}