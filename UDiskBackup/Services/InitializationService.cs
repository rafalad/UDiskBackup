namespace UDiskBackup.Services;

public class InitializationService
{
    private readonly DiskInfoService _diskService;
    private readonly BackupService _backupService;
    private readonly SourceService _sourceService;
    private readonly BlazorSignalRService _signalRService;
    private readonly ILogger<InitializationService> _logger;

    public InitializationService(
        DiskInfoService diskService,
        BackupService backupService,
        SourceService sourceService,
        BlazorSignalRService signalRService,
        ILogger<InitializationService> logger)
    {
        _diskService = diskService;
        _backupService = backupService;
        _sourceService = sourceService;
        _signalRService = signalRService;
        _logger = logger;
    }

    public async Task<List<InitializationStep>> InitializeApplicationAsync(Action<InitializationStep> onStepUpdate)
    {
        var steps = new List<InitializationStep>
        {
            new InitializationStep("source", "Źródło danych", "Sprawdzanie dostępności katalogu źródłowego"),
            new InitializationStep("disks", "Dyski USB", "Skanowanie dostępnych dysków USB"),
            new InitializationStep("backup", "Backup Service", "Inicjalizacja serwisu backup"),
            new InitializationStep("history", "Historia", "Ładowanie historii backup'ów"),
            new InitializationStep("signalr", "Real-time", "Nawiązywanie połączenia SignalR"),
            new InitializationStep("ready", "Gotowe", "Aplikacja gotowa do użycia")
        };

        try
        {
            // 1. Sprawdź źródło danych
            await UpdateStep(steps, "source", InitializationStatus.Loading, "Sprawdzanie katalogu źródłowego...", onStepUpdate);
            await Task.Delay(500); // Symulacja czasu ładowania
            var sourceStatus = await _sourceService.GetStatusAsync("/mnt/shared");
            await UpdateStep(steps, "source", 
                sourceStatus.Exists ? InitializationStatus.Success : InitializationStatus.Warning, 
                sourceStatus.Exists ? $"Źródło: {sourceStatus.Path}" : $"Ostrzeżenie: {sourceStatus.Path} niedostępny", 
                onStepUpdate);

            // 2. Skanuj dyski USB
            await UpdateStep(steps, "disks", InitializationStatus.Loading, "Skanowanie dysków USB...", onStepUpdate);
            await Task.Delay(800);
            var usbTargets = await _backupService.GetUsbTargetsAsync();
            await UpdateStep(steps, "disks", InitializationStatus.Success, 
                $"Znaleziono {usbTargets.Count} dysków USB", onStepUpdate);

            // 3. Inicjalizuj Backup Service
            await UpdateStep(steps, "backup", InitializationStatus.Loading, "Inicjalizacja Backup Service...", onStepUpdate);
            await Task.Delay(300);
            var backupStatus = _backupService.GetCurrentStatus();
            await UpdateStep(steps, "backup", InitializationStatus.Success, 
                backupStatus != null ? $"Status: {backupStatus.State}" : "Backup Service gotowy", onStepUpdate);

            // 4. Załaduj historię
            await UpdateStep(steps, "history", InitializationStatus.Loading, "Ładowanie historii backup'ów...", onStepUpdate);
            await Task.Delay(600);
            var history = await _backupService.GetHistoryAsync(null, 10);
            await UpdateStep(steps, "history", InitializationStatus.Success, 
                $"Załadowano {history.Count} pozycji historii", onStepUpdate);

            // 5. Nawiąż połączenie SignalR
            await UpdateStep(steps, "signalr", InitializationStatus.Loading, "Nawiązywanie połączenia real-time...", onStepUpdate);
            await Task.Delay(400);
            try
            {
                await _signalRService.InitializeAsync();
                await UpdateStep(steps, "signalr", InitializationStatus.Success, 
                    "Połączenie real-time aktywne", onStepUpdate);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SignalR connection failed");
                await UpdateStep(steps, "signalr", InitializationStatus.Warning, 
                    "Real-time niedostępny", onStepUpdate);
            }

            // 6. Aplikacja gotowa
            await UpdateStep(steps, "ready", InitializationStatus.Success, 
                "Aplikacja gotowa do użycia!", onStepUpdate);

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initialization failed");
            var failedStep = steps.FirstOrDefault(s => s.Status == InitializationStatus.Loading);
            if (failedStep != null)
            {
                failedStep.Status = InitializationStatus.Error;
                failedStep.Message = $"Błąd: {ex.Message}";
                onStepUpdate(failedStep);
            }
        }

        return steps;
    }

    private async Task UpdateStep(List<InitializationStep> steps, string id, InitializationStatus status, string message, Action<InitializationStep> onStepUpdate)
    {
        var step = steps.FirstOrDefault(s => s.Id == id);
        if (step != null)
        {
            step.Status = status;
            step.Message = message;
            step.CompletedAt = status != InitializationStatus.Loading ? DateTime.Now : null;
            onStepUpdate(step);
            await Task.Delay(100); // Krótka pauza dla efektu wizualnego
        }
    }
}

public class InitializationStep
{
    public string Id { get; set; }
    public string Title { get; set; }
    public string Description { get; set; }
    public string Message { get; set; }
    public InitializationStatus Status { get; set; }
    public DateTime? CompletedAt { get; set; }

    public InitializationStep(string id, string title, string description)
    {
        Id = id;
        Title = title;
        Description = description;
        Message = description;
        Status = InitializationStatus.Pending;
    }
}

public enum InitializationStatus
{
    Pending,
    Loading,
    Success,
    Warning,
    Error
}