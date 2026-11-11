using System.Net;
using System.Net.Mail;

namespace FocusTime.Api.Modules.Identity;

public interface IEmailSender
{
    Task SendCodeAsync(string email, string purpose, string code, CancellationToken cancellationToken);
    Task SendSecurityNoticeAsync(string email, string subject, string body, CancellationToken cancellationToken);
}

public sealed class EmailSender(IConfiguration configuration, IHostEnvironment environment, ILogger<EmailSender> logger) : IEmailSender
{
    public async Task SendCodeAsync(string email, string purpose, string code, CancellationToken cancellationToken)
    {
        if (environment.IsDevelopment() && string.Equals(configuration["Email:Mode"], "Log", StringComparison.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogInformation("Development email code for {Email} ({Purpose}): {Code}", email, purpose, code);
            return;
        }
        await SendAsync(email, purpose switch { "register" => "FocusTime 注册验证码", "reset" => "FocusTime 密码重置验证码",
            "closure" => "FocusTime 注销账号验证码", _ => "FocusTime 修改登录邮箱验证码" },
            $"您的验证码是：{code}。10 分钟内有效，请勿向他人提供。如非本人操作，请忽略此邮件。", cancellationToken);
    }

    public Task SendSecurityNoticeAsync(string email, string subject, string body, CancellationToken cancellationToken) =>
        SendAsync(email, subject, body, cancellationToken);

    private async Task SendAsync(string email, string subject, string body, CancellationToken cancellationToken)
    {
        var mode = configuration["Email:Mode"];
        if (string.Equals(mode, "Log", StringComparison.OrdinalIgnoreCase))
        {
            if (!environment.IsDevelopment())
                throw new AuthFault(503, "email_unavailable", "Development email delivery is unavailable.");
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogInformation("Development email delivery requested: {Subject}", subject);
            return;
        }
        if (environment.IsDevelopment() && mode == "Pickup")
        {
            var folder = configuration["Email:PickupDirectory"] ??
                Path.Combine(environment.ContentRootPath, ".artifacts", "mail");
            Directory.CreateDirectory(folder);
            using var message = new MailMessage("development@focustime.local", email, subject, body);
            using var pickup = new SmtpClient { DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory,
                PickupDirectoryLocation = folder };
            await pickup.SendMailAsync(message, cancellationToken);
            return;
        }
        var host = configuration["Email:Host"];
        var from = configuration["Email:From"];
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(from))
            throw new AuthFault(503, "email_unavailable", "Email delivery is not configured.");
        try
        {
            // STARTTLS is mandatory. Implicit TLS/465 requires a different transport.
            using var smtp = new SmtpClient(host, configuration.GetValue("Email:Port", 587))
            {
                EnableSsl = true, UseDefaultCredentials = false,
                Credentials = new NetworkCredential(configuration["Email:Username"], configuration["Email:Password"]),
                Timeout = 15000
            };
            using var message = new MailMessage(from, email, subject, body);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await smtp.SendMailAsync(message, timeout.Token);
        }
        catch (SmtpException)
        {
            // SMTP diagnostics can include addresses; keep them outside API responses and application logs.
            throw new AuthFault(503, "email_unavailable", "Email delivery failed. Please retry later.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AuthFault(503, "email_unavailable", "Email delivery timed out. Please retry later.");
        }
    }
}
