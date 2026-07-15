using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace RssReader;

public interface IEmailService
{
    Task SendEmailAsync(string toEmail, string subject, string htmlBody);
}

public class EmailService : IEmailService
{
    private readonly IConfiguration _config;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IConfiguration config, ILogger<EmailService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task SendEmailAsync(string toEmail, string subject, string htmlBody)
    {
        var host = _config["Smtp:Host"];
        if (string.IsNullOrWhiteSpace(host))
        {
            _logger.LogWarning("SMTP not configured. Skipping email to {Email}: {Subject}", toEmail, subject);
            return;
        }

        var port = int.Parse(_config["Smtp:Port"] ?? "587");
        var username = _config["Smtp:Username"] ?? "";
        var password = _config["Smtp:Password"] ?? "";
        var senderEmail = _config["Smtp:SenderEmail"] ?? "noreply@rssreader.local";
        var senderName = _config["Smtp:SenderName"] ?? "RSS Reader";

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(senderName, senderEmail));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = subject;
        message.Body = new TextPart("html") { Text = htmlBody };

        using var client = new SmtpClient();

        try
        {
            await client.ConnectAsync(host, port, SecureSocketOptions.StartTls);
        }
        catch (SslHandshakeException)
        {
            await client.ConnectAsync(host, port, SecureSocketOptions.Auto);
        }

        if (!string.IsNullOrWhiteSpace(username))
        {
            await client.AuthenticateAsync(username, password);
        }

        await client.SendAsync(message);
        await client.DisconnectAsync(true);

        _logger.LogInformation("Email sent to {Email}: {Subject}", toEmail, subject);
    }
}
