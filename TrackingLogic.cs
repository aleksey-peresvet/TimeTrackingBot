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

    public TrackingLogic(AppDb db, IEmailService email, IOptions<BotConfig> cfg, ILogger<TrackingLogic>? logger = null)
    {
        _db = db;
        _email = email;
        _cfg = cfg.Value;
        _logger = logger;
    }

    public async Task ProcessTickAsync()
    {
        var now = DateTime.Now;
        _logger?.LogDebug("[TICK] {0}. Начало нового опроса", now);
        _logger?.LogDebug("График пользователя:\nWorkStart={0}, WorkEnd={1}, LunchStart={2}, LunchEnd={3}", _cfg.WorkStart, _cfg.WorkEnd, _cfg.LunchStart, _cfg.LunchEnd);

        if (!IsWorkTime(now))
        {
            _logger?.LogDebug("Опрос не будет выполнен, причина - у пользователя нерабочее время");
            return;
        }

        var state = await _db.States.FirstOrDefaultAsync();
        if (state == null)
        {
            state = new UserState { Id = 1, LastPromptTime = DateTime.UtcNow.AddHours(-1) };
            _db.States.Add(state);
            await _db.SaveChangesAsync();

            _logger?.LogDebug("[STATE] Создана новая запись состояния");
        }

        if (state.ActiveSessionId.HasValue)
        {
            var session = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == state.ActiveSessionId.Value);
            if (session != null && session.Date != DateTime.Today)
            {
                _logger?.LogWarning("Обнаружена сессия #{Id} за предыдущий день ({Date}). Производится сброс сессии.", session.Id, session.Date);
                state.ActiveSessionId = null;
                await _db.SaveChangesAsync();
            }
        }

        _logger?.LogDebug("Проверка почты после {LastPrompt}", state.LastPromptTime);
        var (hasResponse, answer, received) = await _email.CheckNewAsync(_cfg.TargetEmail, state.LastPromptTime);
        _logger?.LogDebug("Результат: {0}, Ответ=\"{1}\"", hasResponse ? "Есть ответное письмо" : "Нет ответного письма", answer);

        try
        {
            if (hasResponse && !string.IsNullOrEmpty(answer))
            {
                await HandleResponseAsync(state, answer.Trim(), received);
            }
            else if (state.IsPaused)
            {
                _logger?.LogDebug("[STATE] На паузе");

                if (state.LastPromptTime.Date < DateTime.Today)
                {
                    await _email.SendAsync(_cfg.TargetEmail, "⏸️ TimeBot", "Учёт времени приостановлен. Напишите «продолжи» для возобновления.");
                    _logger?.LogDebug("Отправлено напоминание о паузе");

                    state.LastPromptTime = now;
                    await _db.SaveChangesAsync();
                }
                
                if (state.ActiveSessionId.HasValue)
                    await UpdateSessionDurationAsync(state.ActiveSessionId.Value, now);

                return;
            }
            else
            {
                if (state.ActiveSessionId.HasValue)
                    await UpdateSessionDurationAsync(state.ActiveSessionId.Value, now);

                await PromptUserAsync(state, now);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Ошибка обработки ответа: {Answer}", answer);
            return;
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
                await CloseSessionAsync(state, received);

            state.IsPaused = true;
            await _db.SaveChangesAsync();
            await _email.SendAsync(_cfg.TargetEmail, "TimeBot", "Учет приостановлен. Напишите «продолжи» для возобновления.");

            return;
        }

        if (cmd is "продолжи" or "resume" or "/continue" or "/продолжи")
        {
            state.IsPaused = false;
            await _db.SaveChangesAsync();
            await _email.SendAsync(_cfg.TargetEmail, "▶️ TimeBot", "Учет возобновлен. Укажите задачу или продолжим последнюю.");

            return;
        }

        if ((!answer.StartsWith("#") && answer.Length < 3) ||
            answer.Contains("не может быть отправлено") ||
            answer.Contains("mailer-daemon") ||
            answer.Contains("delivery failed"))
        {
            _logger?.LogWarning("Игнорирован невалидный ответ: \"{Answer}\"", answer);
            return;
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

        if (answer.StartsWith("#") && int.TryParse(answer.Trim('#'), out var dailyIndex))
        {
            var todaysSessions = await GetTodaysSessionsAsync();
            if (dailyIndex > 0 && dailyIndex <= todaysSessions.Count)
            {
                var previousSession = todaysSessions[dailyIndex - 1];

                if (state.ActiveSessionId.HasValue)
                {
                    var currentSession = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == state.ActiveSessionId.Value);
                    if (currentSession != null && currentSession.Id == previousSession.Id)
                    {
                        await _email.SendAsync(_cfg.TargetEmail, "✅ TimeBot", $"Продолжаем: {currentSession.Project}.{currentSession.Stage}.{currentSession.TaskName}");
                        _logger?.LogDebug("Клик по той же задаче, сессия не пересоздаётся: #{Id}", state.ActiveSessionId.Value);

                        return;
                    }

                    await CloseSessionAsync(state, received);
                }

                previousSession.End = received;
                state.ActiveSessionId = previousSession.Id;
                state.IsPaused = false;

                await _db.SaveChangesAsync();
                await _email.SendAsync(_cfg.TargetEmail, "✅ TimeBot", $"Возобновлён учёт: {previousSession.Project}.{previousSession.Stage}.{previousSession.TaskName}");
            }
            else
            {
                _logger?.LogWarning("Неверный индекс задачи: {Index}", dailyIndex);
            }

            return;
        }

        if (state.ActiveSessionId.HasValue)
            await CloseSessionAsync(state, received);

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
        await _db.Entry(session).ReloadAsync();

        state.ActiveSessionId = session.Id;
        await _db.SaveChangesAsync();

        _logger?.LogInformation("Создана новая сессия: {Project}.{Stage}.{TaskName}", session.Project, session.Stage, session.TaskName);
    }

    private async Task PromptUserAsync(UserState state, DateTime now)
    {
        var today = DateTime.Today;
        var sessions = await GetTodaysSessionsAsync();
        var msg = new StringBuilder();

        if (state.ActiveSessionId.HasValue)
        {
            var details = await GetSessionDetailsAsync(state.ActiveSessionId.Value);
            msg.AppendLine($"⏳ В данный момент выполняется:<br><b>{details}</b><br>");
            msg.AppendLine("  <p style='background-color: #e3f2fd; padding: 10px; border-left: 4px solid #2196F3; margin: 20px 0;'>");
            msg.AppendLine("    <b>Как продолжить задачу:<br></b> нажмите «Ответить» и укажите <b>#номер</b> в теме или тексте письма.<br>");
            msg.AppendLine("    <i>Пример: напишите «#1» чтобы продолжить первую задачу из списка.</i><br><br>");
            msg.AppendLine("    <b>Если требуется создать новую задачу:<br></b> нажмите «Ответить» и укажите задачу в формате: <b>Проект.Этап.Задача</b>");
            msg.AppendLine("  </p>");
        }
        else
        {
            msg.AppendLine("❓ Над какой задачей работаете?<br>Формат: Проект.Этап.Задача");
        }

        if (sessions.Any())
        {
            var htmlReportBody = GenerateReportBody(sessions, false, today);
            msg.AppendLine($"<br>📂 Сегодня уже были:<br>{htmlReportBody}");
        }

        msg.AppendLine("<br><br>💡 Команды: «отчет» — показать статистику, «стоп» — пауза");

        await _email.SendHtmlAsync(_cfg.TargetEmail, $"⏰ TimeBot [{now:HH:mm}]", msg.ToString());
        _logger?.LogDebug("Отправлен опрос");
    }

    private async Task UpdateSessionDurationAsync(int sessionId, DateTime now)
    {
        if (sessionId < 1)
            return;

        var session = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId);
        if (session != null && session.End < now)
        {
            var delta = CalculateWorkDuration(session.End, now);
            if (delta > 0)
            {
                session.DurationSeconds += delta;
                session.End = now;
                await _db.SaveChangesAsync();

                _logger?.LogDebug("Обновлена трудоёмкость: +{Seconds}с для сессии #{Id}", delta, sessionId);
            }
        }
    }

    private async Task CloseSessionAsync(UserState state, DateTime now)
    {
        if (state?.ActiveSessionId == null)
            return;

        await UpdateSessionDurationAsync(state.ActiveSessionId.Value, now);
        state.ActiveSessionId = null;
        await _db.SaveChangesAsync();
    }

    private async Task<string> GetSessionDetailsAsync(int id)
    {
        if (id < 1)
        {
            _logger?.LogWarning("Не найдена сессия #{Id}", id);
            return "Задача не обнаружена";
        }

        var session = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session == null)
        {
            _logger?.LogWarning("Не найдена сессия #{Id}", id);
            return "Задача не обнаружена";
        }

        return $"{session.Project}.{session.Stage}.{session.TaskName}";
    }

    public async Task SendDailyReportAsync()
    {
        var today = DateTime.Today;
        var sessions = await GetTodaysSessionsAsync();
        var htmlReportBody = GenerateReportBody(sessions, true, today);

        await _email.SendHtmlAsync(_cfg.TargetEmail, $"📈 TimeBot Report - {today:dd.MM.yyyy}", htmlReportBody);
        _logger?.LogInformation("Отчет отправлен: {Tasks} задач", sessions.Count);
    }

    public async Task FinalizeDayAsync()
    {
        await SendDailyReportAsync();

        var state = await _db.States.FirstOrDefaultAsync();
        if (state != null && state.ActiveSessionId.HasValue)
            await CloseSessionAsync(state, DateTime.Now);
    }

    private bool IsWorkTime(DateTime now)
    {
        var t = now.TimeOfDay;
        if (t < _cfg.WorkStart || t > _cfg.WorkEnd || t >= _cfg.LunchStart && t <= _cfg.LunchEnd)
            return false;

        return true;
    }

    private async Task<List<TaskSession>> GetTodaysSessionsAsync()
    {
        return await _db.Sessions
            .Where(s => s.Date == DateTime.Today)
            .OrderBy(s => s.Id)
            .ToListAsync();
    }

    private string GenerateReportBody(List<TaskSession> sessions, bool needFullReport, DateTime? today = null, long? total = null)
    {
        today ??= DateTime.Today;

        var htmlTable = new StringBuilder();
        htmlTable.AppendLine("<table style='width: 100%; font-family: Arial, sans-serif;'>");
        htmlTable.AppendLine("  <thead>");
        htmlTable.AppendLine("    <tr style='background-color: #4CAF50; color: white;'>");
        htmlTable.AppendLine("      <th style='width: 50px;'>#</th>");
        htmlTable.AppendLine("      <th>Проект</th>");
        htmlTable.AppendLine("      <th>Этап проекта</th>");
        htmlTable.AppendLine("      <th>Описание работ</th>");
        htmlTable.AppendLine("      <th>Трудоёмкость</th>");
        htmlTable.AppendLine("    </tr>");
        htmlTable.AppendLine("  </thead>");
        htmlTable.AppendLine("  <tbody>");

        var rowNumber = 0;
        foreach (var session in sessions)
        {
            rowNumber++;
            var hours = session.DurationSeconds / 3600.0;

            htmlTable.AppendLine("    <tr>");
            htmlTable.AppendLine($"      <td style='color:#666;font-weight:bold;'>#{rowNumber}</td>");
            htmlTable.AppendLine($"      <td style='max-width: 200px'>{System.Net.WebUtility.HtmlEncode(session.Project)}</td>");
            htmlTable.AppendLine($"      <td style='max-width: 200px'>{System.Net.WebUtility.HtmlEncode(session.Stage)}</td>");
            htmlTable.AppendLine($"      <td style='max-width: 300px'>{System.Net.WebUtility.HtmlEncode(session.TaskName)}</td>");
            htmlTable.AppendLine($"      <td style='text-align: right;'>{hours:F2} ч.</td>");
            htmlTable.AppendLine("    </tr>");
        }

        if (needFullReport)
        {
            total ??= sessions.Sum(s => s.DurationSeconds);
            htmlTable.AppendLine("    <tr style='background-color: #f2f2f2; font-weight: bold;'>");
            htmlTable.AppendLine("      <td colspan='4' style='text-align: right;'>ИТОГО:</td>");
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
        fullHtml.AppendLine("    th, td { border: 1px solid #ddd; padding: 8px; word-wrap: break-word; }");
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

    private long CalculateWorkDuration(DateTime start, DateTime end)
    {
        if (start >= end || start.Date != end.Date)
            return 0;

        var day = start.Date;
        var workStart = day.Add(_cfg.WorkStart);
        var workEnd = day.Add(_cfg.WorkEnd);
        var lunchStart = day.Add(_cfg.LunchStart);
        var lunchEnd = day.Add(_cfg.LunchEnd);
        var effectiveStart = start < workStart ? workStart : start;
        var effectiveEnd = end > workEnd ? workEnd : end;

        if (effectiveStart >= effectiveEnd)
            return 0;

        var totalSeconds = (long)(effectiveEnd - effectiveStart).TotalSeconds;

        if (effectiveStart < lunchEnd && effectiveEnd > lunchStart)
        {
            var overlapStart = effectiveStart < lunchStart ? lunchStart : effectiveStart;
            var overlapEnd = effectiveEnd > lunchEnd ? lunchEnd : effectiveEnd;
            totalSeconds -= (long)(overlapEnd - overlapStart).TotalSeconds;
        }

        return Math.Max(0, totalSeconds);
    }
}