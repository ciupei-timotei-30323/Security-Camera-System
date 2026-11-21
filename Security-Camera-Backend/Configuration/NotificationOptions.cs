namespace Security_Camera_Backend.Configuration;

/// <summary>Outbound email alert settings. Bound from <c>Notifications</c>.</summary>
public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>Minimum seconds between alert e-mails for one camera system.</summary>
    public int EmailCooldownSeconds { get; set; } = 60;

    /// <summary>Base URL of the web dashboard, used in alert e-mail links.</summary>
    public string DashboardBaseUrl { get; set; } = "http://localhost:5173";

    /// <summary>Base delay for e-mail retries; grows exponentially per attempt.</summary>
    public int EmailRetryBaseDelayMilliseconds { get; set; } = 1000;
}

/// <summary>SMTP connection settings. Bound from <c>Smtp</c>.</summary>
public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 1025;
    public bool UseStartTls { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string FromAddress { get; set; } = "alerts@security-camera.local";
    public string FromName { get; set; } = "Security Camera";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}
