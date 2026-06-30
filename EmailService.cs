using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using System.Net.Sockets;

namespace TimeTrackerBot;

public interface IEmailService
{
    Task SendAsync(string to, string subject, string body);
    Task SendHtmlAsync(string to, string subject, string htmlBody);
    Task<(bool HasNew, string? Text, DateTime Received)> CheckNewAsync(string expectedFrom, DateTime after);
}

public class EmailSendingException : Exception
{
    public EmailSendingException(string message, Exception? inner = null) : base(message, inner) { }
}

public class EmailAuthenticationException : Exception
{
    public EmailAuthenticationException(string message, Exception? inner = null) : base(message, inner) { }
}

public class EmailConnectionException : Exception
{
    public EmailConnectionException(string message, Exception? inner = null) : base(message, inner) { }
}

public class MailKitEmailService : IEmailService
{
    private readonly EmailConfig _cfg;
    private readonly ILogger<MailKitEmailService>? _logger;
    private static readonly string[] _systemSenders = {
        "mailer-daemon", "postmaster", "noreply", "no-reply",
        "mailer@yandex.ru", "postmaster@yandex.ru"
    };
    private const int MaxRetryAttempts = 3;
    private static readonly TimeSpan[] RetryDelays = {
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10)
    };

    public MailKitEmailService(IOptions<EmailConfig> cfg, ILogger<MailKitEmailService>? logger = null)
    {
        _cfg = cfg.Value;
        _logger = logger;
    }

    public async Task SendAsync(string to, string subject, string body)
    {
        await ExecuteWithRetryAsync(
            async () => await SendEmailCoreAsync(to, subject, body, isHtml: false),
            $"Отправка письма {to}: {subject}"
        );
    }

    public async Task SendHtmlAsync(string to, string subject, string htmlBody)
    {
        await ExecuteWithRetryAsync(
            async () => await SendEmailCoreAsync(to, subject, htmlBody, isHtml: true),
            $"Отправка HTML письма {to}: {subject}"
        );
    }

    private async Task SendEmailCoreAsync(string to, string subject, string body, bool isHtml)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress("TimeBot", _cfg.Login));
        msg.To.Add(new MailboxAddress("", to));
        msg.Subject = subject;

        if (isHtml)
        {
            var builder = new BodyBuilder
            {
                HtmlBody = body,
                TextBody = "Пожалуйста, используйте почтовый клиент с поддержкой HTML для просмотра отчета"
            };
            msg.Body = builder.ToMessageBody();
        }
        else
        {
            msg.Body = new TextPart("plain") { Text = body };
        }

        using var smtp = new SmtpClient();

        smtp.ServerCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
        {
            if (sslPolicyErrors == System.Net.Security.SslPolicyErrors.None)
                return true;

            if (sslPolicyErrors == System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors && chain != null)
            {
                var allowedStatuses = new[]
                {
                    System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.RevocationStatusUnknown,
                    System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.OfflineRevocation
                };

                if (chain.ChainStatus.All(s => allowedStatuses.Contains(s.Status)))
                    return true;
            }

            _logger?.LogWarning("Ошибка SSL-сертификата SMTP: {Errors}", sslPolicyErrors);
            return false;
        };

        try
        {
            _logger?.LogDebug("Подключение к SMTP серверу {Server}:{Port}", _cfg.SmtpServer, _cfg.SmtpPort);
            await smtp.ConnectAsync(_cfg.SmtpServer, _cfg.SmtpPort, _cfg.SmtpUseSsl, CancellationToken.None);
        }
        catch (SocketException ex)
        {
            _logger?.LogError(ex, "Ошибка подключения к SMTP серверу");
            throw new EmailConnectionException($"Не удалось подключиться к SMTP серверу {_cfg.SmtpServer}:{_cfg.SmtpPort}", ex);
        }
        catch (TimeoutException ex)
        {
            _logger?.LogError(ex, "Таймаут подключения к SMTP серверу");
            throw new EmailConnectionException("Таймаут подключения к SMTP серверу", ex);
        }

        try
        {
            _logger?.LogDebug("Аутентификация на SMTP сервере");
            await smtp.AuthenticateAsync(_cfg.Login, _cfg.Password, CancellationToken.None);
        }
        catch (AuthenticationException ex)
        {
            _logger?.LogError(ex, "Ошибка аутентификации на SMTP сервере");
            throw new EmailAuthenticationException("Неверный логин или пароль для SMTP", ex);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("authentication"))
        {
            _logger?.LogError(ex, "Ошибка аутентификации на SMTP сервере");
            throw new EmailAuthenticationException("SMTP сервер отклонил аутентификацию", ex);
        }

        try
        {
            _logger?.LogDebug("Отправка письма");
            await smtp.SendAsync(msg, CancellationToken.None);
        }
        catch (SmtpCommandException ex)
        {
            _logger?.LogError(ex, "SMTP ошибка при отправке");
            throw new EmailSendingException($"SMTP ошибка: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            _logger?.LogError(ex, "Ошибка сети при отправке");
            throw new EmailConnectionException("Разрыв соединения при отправке письма", ex);
        }

        try
        {
            await smtp.DisconnectAsync(true, CancellationToken.None);
            _logger?.LogInformation("Письмо успешно отправлено: {To} — {Subject}", to, subject);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Предупреждение при отключении от SMTP");
        }
    }

    private async Task ExecuteWithRetryAsync(Func<Task> action, string operationName)
    {
        var lastException = default(Exception);

        for (int attempt = 0; attempt < MaxRetryAttempts; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (EmailAuthenticationException ex)
            {
                _logger?.LogCritical(ex, "Критическая ошибка аутентификации, повтор не имеет смысла");
                throw;
            }
            catch (EmailSendingException ex) when (!IsTransientError(ex.InnerException))
            {
                _logger?.LogError(ex, "Постоянная ошибка отправки, повтор не имеет смысла");
                throw;
            }
            catch (Exception ex) when (attempt < MaxRetryAttempts - 1)
            {
                lastException = ex;
                var delay = RetryDelays[attempt];
                _logger?.LogWarning(ex,
                    "Ошибка {Operation} (попытка {Attempt}/{Max}). Повтор через {Delay}s",
                    operationName, attempt + 1, MaxRetryAttempts, delay.TotalSeconds);

                await Task.Delay(delay);
            }
            catch (Exception ex)
            {
                lastException = ex;
                break;
            }
        }

        _logger?.LogError(lastException, "Все попытки {Operation} исчерпаны", operationName);

        throw new EmailConnectionException(
            $"Не удалось выполнить операцию '{operationName}' после {MaxRetryAttempts} попыток",
            lastException);
    }

    private bool IsTransientError(Exception? ex)
    {
        if (ex == null)
            return false;

        return ex switch
        {
            SocketException socketEx => socketEx.SocketErrorCode == SocketError.TimedOut ||
                                        socketEx.SocketErrorCode == SocketError.ConnectionReset ||
                                        socketEx.SocketErrorCode == SocketError.HostUnreachable,
            IOException ioEx => true,
            TimeoutException => true,
            _ => false
        };
    }

    public async Task<(bool HasNew, string? Text, DateTime Received)> CheckNewAsync(string expectedFrom, DateTime after)
    {
        _logger?.LogDebug("CheckNewAsync: after={After} (UTC: {Utc}), expectedFrom={From}",
            after, after.ToUniversalTime(), expectedFrom);

        using var imap = new ImapClient();

        imap.ServerCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
        {
            if (sslPolicyErrors == System.Net.Security.SslPolicyErrors.None)
                return true;

            if (sslPolicyErrors == System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors && chain != null)
            {
                var allowedStatuses = new[]
                {
                    System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.RevocationStatusUnknown,
                    System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.OfflineRevocation
                };

                if (chain.ChainStatus.All(s => allowedStatuses.Contains(s.Status)))
                    return true;
            }

            _logger?.LogWarning("Ошибка SSL-сертификата IMAP: {Errors}", sslPolicyErrors);
            return false;
        };

        try
        {
            _logger?.LogDebug("Подключение к IMAP серверу {Server}:{Port}", _cfg.ImapServer, _cfg.ImapPort);
            await imap.ConnectAsync(_cfg.ImapServer, _cfg.ImapPort, _cfg.ImapUseSsl, CancellationToken.None);
        }
        catch (SocketException ex)
        {
            _logger?.LogError(ex, "Ошибка подключения к IMAP серверу");
            throw new EmailConnectionException($"Не удалось подключиться к IMAP серверу {_cfg.ImapServer}:{_cfg.ImapPort}", ex);
        }
        catch (TimeoutException ex)
        {
            _logger?.LogError(ex, "Таймаут подключения к IMAP серверу");
            throw new EmailConnectionException("Таймаут подключения к IMAP серверу", ex);
        }

        try
        {
            _logger?.LogDebug("Аутентификация на IMAP сервере");
            await imap.AuthenticateAsync(_cfg.Login, _cfg.Password, CancellationToken.None);
        }
        catch (AuthenticationException ex)
        {
            _logger?.LogError(ex, "Ошибка аутентификации на IMAP сервере");
            throw new EmailAuthenticationException("Неверный логин или пароль для IMAP", ex);
        }

        try
        {
            await imap.Inbox.OpenAsync(FolderAccess.ReadOnly, CancellationToken.None);
            _logger?.LogDebug("Папка Inbox открыта");

            var uids = await imap.Inbox.SearchAsync(SearchQuery.DeliveredAfter(after), CancellationToken.None);
            _logger?.LogDebug("Найдено писем за сегодня: {Count}", uids.Count);

            foreach (var uid in uids.Reverse())
            {
                var msg = await imap.Inbox.GetMessageAsync(uid, CancellationToken.None);
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

                _logger?.LogInformation("Письмо обработано: \"{Body}\"", body);

                await imap.DisconnectAsync(true, CancellationToken.None);
                return (true, body, msgDate);
            }

            await imap.DisconnectAsync(true, CancellationToken.None);
            _logger?.LogDebug("Подходящих писем не найдено");

            return (false, null, DateTime.MinValue);
        }
        catch (ImapProtocolException ex)
        {
            _logger?.LogError(ex, "Ошибка протокола IMAP");
            throw new EmailConnectionException("Ошибка протокола IMAP", ex);
        }
        catch (IOException ex)
        {
            _logger?.LogError(ex, "Ошибка сети при работе с IMAP");
            throw new EmailConnectionException("Разрыв соединения с IMAP сервером", ex);
        }
        finally
        {
            if (imap.IsConnected)
            {
                try
                {
                    await imap.DisconnectAsync(true, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Предупреждение при отключении от IMAP");
                }
            }
        }
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

        return firstLine;
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