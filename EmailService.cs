using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using Microsoft.Extensions.Options;
using MimeKit;

namespace TimeTrackerBot;

public interface IEmailService
{
    Task SendAsync(string to, string subject, string body);
    Task SendHtmlAsync(string to, string subject, string htmlBody);
    Task<(bool HasNew, string? Text, DateTime Received)> CheckNewAsync(string expectedFrom, DateTime after);
}

public class MailKitEmailService : IEmailService
{
    private readonly EmailConfig _cfg;
    private readonly ILogger<MailKitEmailService>? _logger;
    private static readonly string[] _systemSenders = {
        "mailer-daemon", "postmaster", "noreply", "no-reply",
        "mailer@yandex.ru", "postmaster@yandex.ru"
    };

    public MailKitEmailService(IOptions<EmailConfig> cfg, ILogger<MailKitEmailService>? logger = null)
    {
        _cfg = cfg.Value;
        _logger = logger;
    }

    public async Task SendAsync(string to, string subject, string body)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress("TimeBot", _cfg.Login));
        msg.To.Add(new MailboxAddress("", to));
        msg.Subject = subject;
        msg.Body = new TextPart("plain") { Text = body };

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(_cfg.SmtpServer, _cfg.SmtpPort, _cfg.SmtpUseSsl);
        await smtp.AuthenticateAsync(_cfg.Login, _cfg.Password);
        await smtp.SendAsync(msg);
        await smtp.DisconnectAsync(true);

        _logger?.LogDebug("Отправлено: {To} — {Subject}", to, subject);
    }

    public async Task SendHtmlAsync(string to, string subject, string htmlBody)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress("TimeBot", _cfg.Login));
        msg.To.Add(new MailboxAddress("", to));
        msg.Subject = subject;

        var builder = new BodyBuilder
        {
            HtmlBody = htmlBody,
            TextBody = "Пожалуйста, используйте почтовый клиент с поддержкой HTML для просмотра отчета"
        };

        msg.Body = builder.ToMessageBody();

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(_cfg.SmtpServer, _cfg.SmtpPort, _cfg.SmtpUseSsl);
        await smtp.AuthenticateAsync(_cfg.Login, _cfg.Password);
        await smtp.SendAsync(msg);
        await smtp.DisconnectAsync(true);

        _logger?.LogDebug("HTML отправлено: {To} — {Subject}", to, subject);
    }

    public async Task<(bool HasNew, string? Text, DateTime Received)> CheckNewAsync(string expectedFrom, DateTime after)
    {
        _logger?.LogDebug("CheckNewAsync: after={After} (UTC: {Utc}), expectedFrom={From}",
            after, after.ToUniversalTime(), expectedFrom);

        using var imap = new ImapClient();
        await imap.ConnectAsync(_cfg.ImapServer, _cfg.ImapPort, _cfg.ImapUseSsl);
        await imap.AuthenticateAsync(_cfg.Login, _cfg.Password);
        await imap.Inbox.OpenAsync(FolderAccess.ReadOnly);

        var uids = await imap.Inbox.SearchAsync(SearchQuery.DeliveredAfter(after));

        _logger?.LogDebug("Найдено писем за сегодня: {Count}", uids.Count);

        foreach (var uid in uids.Reverse())
        {
            var msg = await imap.Inbox.GetMessageAsync(uid);
            var msgDate = msg.Date.LocalDateTime;

            if (msgDate <= after)
            {
                _logger?.LogDebug("Пропуск (старое): {Date} <= {After}", msgDate, after);
                continue;
            }

            _logger?.LogDebug("Письмо новое: {Date} > {After}", msgDate, after);

            var from = msg.From.ToString().ToLowerInvariant();
            if (_systemSenders.Any(s => from.Contains(s)))
            {
                _logger?.LogDebug("Пропуск: системный отправитель {From}", msg.From);
                continue;
            }

            var subject = msg.Subject?.ToLowerInvariant() ?? string.Empty;
            if (subject.Contains("не доставлено") || subject.Contains("delivery failed") ||
                subject.Contains("undeliverable") || subject.Contains("возврат") ||
                subject.Contains("не может быть отправлено"))
            {
                _logger?.LogDebug("Пропуск: тема-отбой \"{Subject}\"", msg.Subject);
                continue;
            }

            var isFromUser = false;
            foreach (var mailbox in msg.From.Mailboxes)
            {
                var emailAddr = mailbox.Address?.ToLowerInvariant() ?? string.Empty;
                var expectedAddr = expectedFrom.ToLowerInvariant();

                if (emailAddr == expectedAddr || emailAddr.Contains(expectedAddr) || expectedAddr.Contains(emailAddr))
                {
                    isFromUser = true;
                    _logger?.LogDebug("Адрес совпал: {Email}", emailAddr);
                    break;
                }
            }

            if (!isFromUser)
            {
                _logger?.LogDebug("Пропуск: не от нашего пользователя {From}", msg.From);
                continue;
            }

            var body = ExtractCleanText(msg);
            if (string.IsNullOrWhiteSpace(body) || body.Length < 2)
            {
                _logger?.LogDebug("Пропуск: пустое тело письма");
                continue;
            }

            _logger?.LogInformation("Письмо обработано: \"{Body}\"", body.Substring(0, Math.Min(50, body.Length)));

            await imap.DisconnectAsync(true);
            return (true, body, msgDate);
        }

        await imap.DisconnectAsync(true);
        _logger?.LogDebug("Подходящих писем не найдено");
        return (false, null, DateTime.MinValue);
    }

    private string ExtractCleanText(MimeMessage msg)
    {
        var text = string.Empty;
        var textPart = msg.TextBody;

        if (!string.IsNullOrWhiteSpace(textPart))
            text = textPart;
        else if (!string.IsNullOrWhiteSpace(msg.HtmlBody))
            text = ConvertHtmlToText(msg.HtmlBody);

        if (string.IsNullOrWhiteSpace(text)) 
            return string.Empty;

        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]*>", "");

        var quoteMarkers = new[] {
            "----------------", "----", "____",
            "Кому:", "От:", "Дата:", "Subject:",
            "> ", "•", "─"
        };

        foreach (var marker in quoteMarkers)
        {
            var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                text = text.Substring(0, idx).Trim();
                break;
            }
        }

        var signatureMarkers = new[] { "\nС уважением,", "\nBest regards,", "\n--\n", "\n___" };
        foreach (var marker in signatureMarkers)
        {
            var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                text = text.Substring(0, idx).Trim();
                break;
            }
        }

        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n\s*\n", "\n");
        text = text.Trim();

        var firstLine = text.Split('\n')[0].Trim();

        return firstLine.Length > 100 ? firstLine.Substring(0, 100) : firstLine;
    }

    private string ConvertHtmlToText(string html)
    {
        html = html.Replace("<br>", "\n").Replace("<br/>", "\n").Replace("<br />", "\n");
        html = html.Replace("<p>", "\n").Replace("</p>", "\n");
        html = html.Replace("<div>", "\n").Replace("</div>", "\n");
        html = html.Replace("&nbsp;", " ");
        html = html.Replace("&quot;", "\"");
        html = html.Replace("&amp;", "&");

        return html;
    }
}