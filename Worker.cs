using Microsoft.Extensions.Options;

namespace TimeTrackerBot;

public class TimeTrackingWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly BotConfig _cfg;
    private readonly ILogger<TimeTrackingWorker>? _logger;
    private DateTime? _lastReportDate;

    public TimeTrackingWorker(IServiceProvider sp, IOptions<BotConfig> cfg, ILogger<TimeTrackingWorker>? logger = null)
    {
        _sp = sp; _cfg = cfg.Value; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        _logger?.LogInformation("Worker запущен");

        while (!stop.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;
                using var scope = _sp.CreateScope();
                var logic = scope.ServiceProvider.GetRequiredService<TrackingLogic>();

                if (now.TimeOfDay >= _cfg.WorkEnd && _lastReportDate != now.Date)
                {
                    await logic.FinalizeDayAsync();
                    _lastReportDate = now.Date;
                    _logger?.LogInformation("Автоотчет за день отправлен");

                    await Task.Delay(TimeSpan.FromHours(24) - now.TimeOfDay + _cfg.WorkStart, stop);
                    continue;
                }

                await logic.ProcessTickAsync();
                await Task.Delay(TimeSpan.FromMinutes(_cfg.PollIntervalMinutes), stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Ошибка в цикле воркера");
                await Task.Delay(TimeSpan.FromMinutes(1), stop);
            }
        }
    }
}