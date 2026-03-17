using Application = System.Windows.Forms.Application;
using Font = System.Drawing.Font;

namespace TimeTrackerBot;

public class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly IHost _host;
    private readonly ILogger<TrayApplicationContext>? _logger;

    public TrayApplicationContext(IHost host, ILogger<TrayApplicationContext>? logger = null)
    {
        _host = host;
        _logger = logger;

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "TimeBot — Учёт времени",
            Visible = true
        };

        var baseDir = AppContext.BaseDirectory;
        var logPath = Path.Combine(baseDir, "logs", "bot.log");

        var contextMenu = new ContextMenuStrip();
        var showItem = new ToolStripMenuItem("Показать лог") { Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
        showItem.Click += (s, e) => ShowLog(logPath);

        var reportItem = new ToolStripMenuItem("Запросить отчёт");
        reportItem.Click += async (s, e) => await RequestReportAsync();

        var pauseItem = new ToolStripMenuItem("Пауза");
        pauseItem.Click += async (s, e) => await TogglePauseAsync();

        contextMenu.Items.Add(showItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(reportItem);
        contextMenu.Items.Add(pauseItem);
        contextMenu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Выход");
        exitItem.Click += async (s, e) => await ExitAsync();
        contextMenu.Items.Add(exitItem);

        _trayIcon.ContextMenuStrip = contextMenu;
        _trayIcon.MouseDoubleClick += (s, e) => {
            if (e.Button == MouseButtons.Left) 
                ShowLog(logPath);
        };

        _logger?.LogInformation("Иконка в трее активирована");
    }

    private void ShowLog(string logPath)
    {
        try
        {
            System.Diagnostics.Process.Start("notepad.exe", logPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось открыть лог: {ex.Message}");
        }
    }

    private async Task RequestReportAsync()
    {
        try
        {
            using var scope = _host.Services.CreateScope();
            var logic = scope.ServiceProvider.GetRequiredService<TrackingLogic>();
            await logic.SendDailyReportAsync();

            _trayIcon.ShowBalloonTip(
                2000,
                "TimeBot",
                "Отчёт отправлен на почту",
                ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _trayIcon.ShowBalloonTip(
                2000,
                "TimeBot — Ошибка",
                $"Не удалось отправить отчёт: {ex.Message}",
                ToolTipIcon.Error);
        }
    }

    private async Task TogglePauseAsync()
    {
        try
        {
            using var scope = _host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var state = await db.States.FindAsync(1);

            if (state != null)
            {
                state.IsPaused = !state.IsPaused;
                await db.SaveChangesAsync();

                _trayIcon.ShowBalloonTip(
                    2000,
                    "TimeBot",
                    state.IsPaused ? "Учёт приостановлен" : "▶️ Учёт возобновлён",
                    ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            _trayIcon.ShowBalloonTip(
                2000,
                "TimeBot — Ошибка",
                ex.Message,
                ToolTipIcon.Error);
        }
    }

    private async Task ExitAsync()
    {
        _trayIcon.Visible = false;

        try
        {
            using var scope = _host.Services.CreateScope();
            var logic = scope.ServiceProvider.GetRequiredService<TrackingLogic>();
            await logic.FinalizeDayAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Ошибка при финальном отчёте");
        }

        await _host.StopAsync();
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _trayIcon.Dispose();

        base.Dispose(disposing);
    }
}