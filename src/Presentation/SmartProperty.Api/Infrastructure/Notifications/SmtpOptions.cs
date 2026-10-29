namespace SmartProperty.Api.Infrastructure.Notifications;

/// <summary>
/// SMTP settings from the <c>Smtp</c> configuration section. Leaving <see cref="Host"/> empty keeps the logging
/// stand-in that sends nothing. Set the password through User Secrets or the <c>Smtp__Password</c> environment
/// variable, never in a committed file.
/// </summary>
public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    /// <summary>True for STARTTLS or implicit TLS, depending on the port. Leave on unless the server is local.</summary>
    public bool EnableSsl { get; set; } = true;

    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string FromAddress { get; set; } = string.Empty;

    public string FromName { get; set; } = "SmartProperty";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}
