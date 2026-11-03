using System.Net;
using System.Net.Mail;

namespace FocusTime.Api.Modules.Identity;

public interface IEmailSender
{
    Task SendCodeAsync(string email, string purpose, string code, CancellationToken cancellationToken);
}

public sealed class EmailSender(IConfiguration configuration, IHostEnvironment environment) : IEmailSender
{
    public async Task SendCodeAsync(string email, string purpose, string code, CancellationToken cancellationToken)
    {
        var subject = purpose == "register" ? "FocusTime 注册验证码" : "FocusTime 密码重置验证码";
        var body = $"您的验证码是：{code}。10 分钟内有效，请勿向他人提供。";
        if (environment.IsDevelopment() && configuration["Email:Mode"] == "Pickup")
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
