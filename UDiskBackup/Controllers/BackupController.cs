using Microsoft.AspNetCore.Mvc;
using UDiskBackup.Models;
using UDiskBackup.Services;

namespace UDiskBackup.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BackupController : ControllerBase
{
    private readonly BackupService _backupService;

    public BackupController(BackupService backupService)
    {
        _backupService = backupService;
    }

    [HttpGet("plan")]
    public async Task<IActionResult> GetPlan([FromQuery] string targetMount)
    {
        try
        {
            var plan = await _backupService.PlanAsync(targetMount);
            return Ok(plan);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("start")]
    public async Task<IActionResult> StartBackup([FromBody] StartBackupRequest request)
    {
        try
        {
            var plan = await _backupService.PlanAsync(request.TargetMount);
            if (!plan.EnoughSpace)
            {
                var need = plan.EstimatedBytes;
                var free = plan.FreeBytes;
                return BadRequest($"Za mało miejsca na {request.TargetMount}. Potrzeba ~{need} B, wolne {free} B. Katalog docelowy: {plan.TargetBackupDir}");
            }
            
            var id = await _backupService.StartAsync(request.TargetMount);
            return Ok(new { operationId = id });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("stop")]
    public async Task<IActionResult> StopBackup()
    {
        try
        {
            var success = await _backupService.StopBackupAsync();
            if (success)
            {
                return Ok(new { message = "Backup zatrzymany" });
            }
            else
            {
                return BadRequest("Nie można zatrzymać backupu - prawdopodobnie nie jest uruchomiony");
            }
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var status = _backupService.GetCurrentStatus();
        if (status == null)
        {
            return Ok(new { state = "Idle", message = "No backup in progress" });
        }
        return Ok(status);
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory([FromQuery] string? targetMount, [FromQuery] int? take)
    {
        try
        {
            var list = await _backupService.GetHistoryAsync(targetMount, take ?? 50);
            return Ok(list);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("current-log")]
    public IActionResult GetCurrentLog([FromQuery] bool? download)
    {
        try
        {
            string text;
            lock (_backupService.GetType().GetField("_liveBuffer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(_backupService) ?? new object())
            {
                var liveBuffer = _backupService.GetType().GetField("_liveBuffer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(_backupService) as System.Text.StringBuilder;
                text = liveBuffer?.ToString() ?? "";
            }
            
            if (download == true)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                return File(bytes, "text/plain; charset=utf-8", $"udiskbackup-live-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
            }
            
            return Content(text, "text/plain; charset=utf-8");
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("targets")]
    public async Task<IActionResult> GetTargets()
    {
        try
        {
            var eligibleUsb = await _backupService.GetEligibleUsbAsync();
            var targets = new List<object>();
            
            foreach (var usb in eligibleUsb)
            {
                if (string.IsNullOrEmpty(usb.MountPoint))
                    continue;

                var plan = await _backupService.PlanAsync(usb.MountPoint);
                targets.Add(new
                {
                    mountPoint = usb.MountPoint,
                    fsType = usb.FsType,
                    freeBytes = plan.FreeBytes,
                    totalBytes = plan.FreeBytes * 2 // Prosty fallback - załóż że dysk jest w połowie zapełniony
                });
            }
            
            return Ok(targets);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("log/{operationId}")]
    public async Task<IActionResult> GetBackupLog(string operationId, [FromQuery] bool? download)
    {
        try
        {
            // Szukamy logi w historii backupów
            var history = await _backupService.GetHistoryAsync(null, 1000); // Pobierz więcej historii
            var item = history.FirstOrDefault(h => h.OperationId.Equals(operationId, StringComparison.OrdinalIgnoreCase));
            
            if (item == null)
            {
                return NotFound(new { error = $"Nie znaleziono backupu o ID: {operationId}" });
            }

            // Sprawdź czy istnieje plik .txt z logami
            string logContent = "";
            if (!string.IsNullOrEmpty(item.SummaryTxtPath) && System.IO.File.Exists(item.SummaryTxtPath))
            {
                logContent = await System.IO.File.ReadAllTextAsync(item.SummaryTxtPath);
            }
            else if (System.IO.File.Exists(item.SummaryJsonPath))
            {
                // Fallback - czytaj z JSON i sformatuj
                var jsonContent = await System.IO.File.ReadAllTextAsync(item.SummaryJsonPath);
                var summary = System.Text.Json.JsonSerializer.Deserialize<object>(jsonContent);
                logContent = System.Text.Json.JsonSerializer.Serialize(summary, new System.Text.Json.JsonSerializerOptions 
                { 
                    WriteIndented = true 
                });
            }
            else
            {
                return NotFound(new { error = "Plik z logami nie został znaleziony" });
            }

            if (download == true)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(logContent);
                var fileName = $"backup-log-{operationId}-{item.StartedAtUtc:yyyyMMdd-HHmmss}.txt";
                return File(bytes, "text/plain; charset=utf-8", fileName);
            }

            return Content(logContent, "text/plain; charset=utf-8");
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}