using System.Net.Mail;
using Microsoft.Extensions.Logging;
using ShopApplication;

namespace ShopInfrastructure.Email;

public sealed record SmtpOptions(string Host, string From, bool Local);

/// <summary>Skickar e-post via SMTP.</summary>
public sealed class SmtpEmailSender(SmtpOptions options, ILogger<SmtpEmailSender> log) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        if (options.Local)
        {
            log.LogInformation("Lokalt läge: e-post till {To} ({Subject}) skickas inte.", to, subject);
            return;
        }

        try
        {
            using var message = new MailMessage(options.From, to, subject, body);
            using var client = new SmtpClient(options.Host);
            await client.SendMailAsync(message, ct);
        }
        catch (Exception e) when (e is SmtpException or FormatException)
        {
            // E-posten är en bekräftelse, inte en del av köpet: ett fel här stoppar inte anropet.
            log.LogWarning(e, "Kunde inte skicka e-post till {To}.", to);
        }
    }
}
