using Microsoft.AspNetCore.Mvc;
using UDiskBackup.Services;

namespace UDiskBackup.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SourceController : ControllerBase
{
    private readonly SourceService _sourceService;
    private readonly IConfiguration _configuration;

    public SourceController(SourceService sourceService, IConfiguration configuration)
    {
        _sourceService = sourceService;
        _configuration = configuration;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        try
        {
            var sourcePath = _configuration["SourcePath"] ?? Environment.GetEnvironmentVariable("SourcePath") ?? "/mnt/shared";
            var status = await _sourceService.GetStatusAsync(sourcePath);
            return Ok(status);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}