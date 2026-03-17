using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text;

namespace TimeTrackerBot;

public class TrackingLogic
{
    private readonly AppDb _db;
    private readonly IEmailService _email;
    private readonly BotConfig _cfg;
    private readonly ILogger<TrackingLogic>? _logger;
    private bool _initialized = false;

    public TrackingLogic(AppDb db, IEmailService email, IOptions<BotConfig> cfg, ILogger<TrackingLogic>? logger = null)
    {
        _db = db; _email = email; _cfg = cfg.Value; _logger = logger;
    }

    public async Task ProcessTickAsync()
    {
        if (!_initialized)
        {
            _logger?.LogInformation("Первая инициализация TrackingLogic");
            _initialized = true;
        }

        var now = DateTime.Now;
        _logger?.LogDebug("[TICK] {Now} — начало", now);
        _logger?.LogDebug("Время: {Time}, WorkStart={Start}, WorkEnd={End}", now.TimeOfDay, _cfg.WorkStart, _cfg.WorkEnd);

        if (!IsWorkTime(now))
        {
            _logger?.LogDebug("Вне рабочего времени");
            return;
        }

        var state = await _db.States.FindAsync(1);
        if (state == null)
        {
            state = new UserState { Id = 1, LastPromptTime = DateTime.MinValue };
            _db.States.Add(state);
            await _db.SaveChangesAsync();

            _logger?.LogDebug("[STATE] Создано новое");
        }

        if (state.IsPaused)
        {
            _logger?.LogDebug("На паузе");

            if (state.ActiveSessionId.HasValue)
            {
                await UpdateSessionDurationAsync(state.ActiveSessionId.Value, now);
                await _db.SaveChangesAsync();
            }
            return;
        }

        _logger?.LogDebug("Проверка почты после {LastPrompt}", state.LastPromptTime);
        var (hasResponse, answer, received) = await _email.CheckNewAsync(_cfg.TargetEmail, state.LastPromptTime);
        _logger?.LogDebug("Результат: HasNew={HasNew}, Answer=\"{Answer}\"", hasResponse, answer?.Substring(0, Math.Min(30, answer?.Length ?? 0)));

        if (hasResponse && !string.IsNullOrEmpty(answer))
        {
            await HandleResponseAsync(state, answer.Trim(), received);
        }
        else
        {
            if (state.ActiveSessionId.HasValue)
                await UpdateSessionDurationAsync(state.ActiveSessionId.Value, now);

            await PromptUserAsync(state, now);
        }

        state.LastPromptTime = now;
        await _db.SaveChangesAsync();
        _logger?.LogDebug("[TICK] Завершено");
    }

    private async Task HandleResponseAsync(UserState state, string answer, DateTime received)
    {
        var cmd = answer.ToLowerInvariant();
        if (cmd is "отчет" or "отчёт" or "report" or "/report" or "/стат" or "/отчет")
        {
            await SendDailyReportAsync();
            return;
        }

        if (cmd is "стоп" or "pause" or "/stop" or "/пауза")
        {
            if (state.ActiveSessionId.HasValue)
            {
                await CloseSessionAsync(state.ActiveSessionId.Value, received);
                state.ActiveSessionId = null;
            }
            state.IsPaused = true;
            await _email.SendAsync(_cfg.TargetEmail, "TimeBot", "Учет приостановлен. Напишите «продолжи» для возобновления.");
            return;
        }

        if (cmd is "продолжи" or "resume" or "/continue" or "/продолжи")
        {
            state.IsPaused = false;
            await _email.SendAsync(_cfg.TargetEmail, "▶️ TimeBot", "Учет возобновлен. Укажите задачу или продолжим последнюю.");
            return;
        }

        if (answer.Length < 3 || answer.Contains("не может быть отправлено") ||
            answer.Contains("mailer-daemon") || answer.Contains("delivery failed"))
        {
            _logger?.LogWarning("Игнорирован невалидный ответ: \"{Answer}\"", answer);
            return;
        }

        if (state.ActiveSessionId.HasValue)
        {
            await CloseSessionAsync(state.ActiveSessionId.Value, received);
        }

        var taskStart = received;
        var today = DateTime.Today;
        var hasSessionsToday = await _db.Sessions.AnyAsync(s => s.Date == today);

        if (!hasSessionsToday)
        {
            var workStartToday = today.Add(_cfg.WorkStart);
            if (received > workStartToday)
            {
                taskStart = workStartToday;
                _logger?.LogInformation("Первая задача дня: старт с {WorkStart} (бот запущен в {Received})", workStartToday, received);
            }
        }

        var parts = answer.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var session = new TaskSession
        {
            Project = parts.ElementAtOrDefault(0) ?? "General",
            Stage = parts.ElementAtOrDefault(1) ?? "Dev",
            TaskName = parts.ElementAtOrDefault(2) ?? answer,
            Start = taskStart,
            End = taskStart,
            DurationSeconds = 0,
            Date = today
        };

        _db.Sessions.Add(session);
        await _db.SaveChangesAsync();
        state.ActiveSessionId = session.Id;

        _logger?.LogInformation("Новая сессия: {Project}.{Stage}.{TaskName}", session.Project, session.Stage, session.TaskName);
    }

    private async Task PromptUserAsync(UserState state, DateTime now)
    {
        var today = DateTime.Today;
        var history = await _db.Sessions
            .Where(s => s.Date == today)
            .GroupBy(s => new { s.Project, s.Stage, s.TaskName })
            .Select(g => new DailyReportRow
            {
                Project = g.Key.Project,
                Stage = g.Key.Stage,
                Task = g.Key.TaskName,
                Seconds = g.Sum(x => x.DurationSeconds)
            })
            .OrderBy(x => x.Seconds)
            .ToListAsync();

        var msg = new StringBuilder();

        if (state.ActiveSessionId.HasValue)
        {
            var details = await GetSessionDetailsAsync(state.ActiveSessionId.Value);
            msg.AppendLine($"⏳ Продолжаем <b>{details}</b>?<br><br>Ответьте: «Да» или укажите новую задачу в формате:<br>Проект.Этап.Задача");
        }
        else
        {
            msg.AppendLine("❓ Над какой задачей работаете?<br>Формат: Проект.Этап.Задача");
        }

        if (history.Any())
        {
            var htmlReportBody = GenerateReportBody(history, false, today);
            msg.AppendLine($"<br><br>📂 Сегодня уже были:<br>{htmlReportBody}");
        }

        msg.AppendLine("<br><br>💡 Команды: «отчет» — показать статистику, «стоп» — пауза");

        await _email.SendHtmlAsync(_cfg.TargetEmail, $"⏰ TimeBot [{now:HH:mm}]", msg.ToString());
        _logger?.LogDebug("Отправлен опрос");
    }

    private async Task UpdateSessionDurationAsync(int sessionId, DateTime now)
    {
        var session = await _db.Sessions.FindAsync(sessionId);
        if (session != null && session.End < now)
        {
            var delta = (long)(now - session.End).TotalSeconds;
            if (delta > 0)
            {
                session.DurationSeconds += delta;
                session.End = now;
                _db.Update(session);
                _logger?.LogDebug("Обновлено: +{Seconds}с для сессии #{Id}", delta, sessionId);
            }
        }
    }

    private async Task CloseSessionAsync(int sessionId, DateTime now)
    {
        await UpdateSessionDurationAsync(sessionId, now);
        await _db.SaveChangesAsync();
    }

    private async Task<string> GetSessionDetailsAsync(int id)
    {
        var s = await _db.Sessions.FindAsync(id);
        return s == null ? "Unknown" : $"{s.Project}.{s.Stage}.{s.TaskName}";
    }

    public async Task SendDailyReportAsync()
    {
        var today = DateTime.Today;
        var report = await _db.Sessions
            .Where(s => s.Date == today)
            .GroupBy(s => new { s.Project, s.Stage, s.TaskName })
            .Select(g => new DailyReportRow
            {
                Project = g.Key.Project,
                Stage = g.Key.Stage,
                Task = g.Key.TaskName,
                Seconds = g.Sum(x => x.DurationSeconds),
                FirstStart = g.Min(x => x.Start)
            })
            .OrderBy(x => x.FirstStart)
            .ToListAsync();
        var total = report.Sum(r => r.Seconds);
        var htmlReportBody = GenerateReportBody(report, true, today, total);

        await _email.SendHtmlAsync(_cfg.TargetEmail, $"📈 TimeBot Report - {today:dd.MM.yyyy}", htmlReportBody);
        _logger?.LogInformation("HTML отчет отправлен: {Tasks} задач, {Total} ч.", report.Count, total / 3600.0);
    }

    public async Task FinalizeDayAsync()
    {
        await SendDailyReportAsync();

        var state = await _db.States.FindAsync(1);
        if (state?.ActiveSessionId.HasValue == true)
        {
            await CloseSessionAsync(state.ActiveSessionId.Value, DateTime.Now);
            state.ActiveSessionId = null;
            await _db.SaveChangesAsync();
        }
    }

    private bool IsWorkTime(DateTime now)
    {
        var t = now.TimeOfDay;
        if (t < _cfg.WorkStart || t > _cfg.WorkEnd || t >= _cfg.LunchStart && t <= _cfg.LunchEnd)
            return false;

        return true;
    }

    private string GenerateReportBody(List<DailyReportRow> report, bool needFullReport, DateTime? today = null, long? total = null)
    {
        today ??= DateTime.Today;
        total ??= report.Sum(r => r.Seconds);

        var htmlTable = new StringBuilder();
        htmlTable.AppendLine("<table width: 100%; font-family: Arial, sans-serif;'>");
        htmlTable.AppendLine("  <thead>");
        htmlTable.AppendLine("    <tr style='background-color: #4CAF50; color: white;'>");
        htmlTable.AppendLine("      <th>Проект</th>");
        htmlTable.AppendLine("      <th>Этап проекта</th>");
        htmlTable.AppendLine("      <th>Описание работ</th>");
        htmlTable.AppendLine("      <th>Трудоёмкость</th>");
        htmlTable.AppendLine("    </tr>");
        htmlTable.AppendLine("  </thead>");
        htmlTable.AppendLine("  <tbody>");

        foreach (var item in report)
        {
            var hours = item.Seconds / 3600.0;
            htmlTable.AppendLine("    <tr>");
            htmlTable.AppendLine($"      <td>{System.Net.WebUtility.HtmlEncode(item.Project)}</td>");
            htmlTable.AppendLine($"      <td>{System.Net.WebUtility.HtmlEncode(item.Stage)}</td>");
            htmlTable.AppendLine($"      <td>{System.Net.WebUtility.HtmlEncode(item.Task)}</td>");
            htmlTable.AppendLine($"      <td style='text-align: right;'>{hours:F2} ч.</td>");
            htmlTable.AppendLine("    </tr>");
        }

        if (needFullReport)
        {
            htmlTable.AppendLine("    <tr style='background-color: #f2f2f2; font-weight: bold;'>");
            htmlTable.AppendLine("      <td colspan='3' style='text-align: right;'>ИТОГО:</td>");
            htmlTable.AppendLine($"      <td style='text-align: right;'>{total / 3600.0:F2} ч.</td>");
            htmlTable.AppendLine("    </tr>");
        }

        htmlTable.AppendLine("  </tbody>");
        htmlTable.AppendLine("</table>");

        var fullHtml = new StringBuilder();
        fullHtml.AppendLine("<!DOCTYPE html>");
        fullHtml.AppendLine("<html>");
        fullHtml.AppendLine("<head>");
        fullHtml.AppendLine("  <meta charset='utf-8'>");
        fullHtml.AppendLine("  <style>");
        fullHtml.AppendLine("    table { border-collapse: collapse; width: 100%; }");
        fullHtml.AppendLine("    th, td { border: 1px solid #ddd; padding: 8px; }");
        fullHtml.AppendLine("    body {{ font-family: Arial, sans-serif; margin: 20px; }}");
        fullHtml.AppendLine("    h2 {{ color: #333; }}");
        fullHtml.AppendLine("  </style>");
        fullHtml.AppendLine("</head>");
        fullHtml.AppendLine("<body>");
        if (needFullReport)
            fullHtml.AppendLine($"  <h2>📊 Отчет за {today:dd.MM.yyyy}</h2>");
        fullHtml.AppendLine($"  {htmlTable}");
        fullHtml.AppendLine("</body>");
        fullHtml.AppendLine("</html>");

        return fullHtml.ToString();
    }
}