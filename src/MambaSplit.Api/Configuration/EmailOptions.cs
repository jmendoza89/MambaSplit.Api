namespace MambaSplit.Api.Configuration;

public class EmailOptions
{
    public string Provider { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = "https://api.smtp2go.com/v3";
    public string ApiKey { get; set; } = string.Empty;
    public string FromEmail { get; set; } = string.Empty;
    public string FromName { get; set; } = "MambaSplit";
    public string? ReplyToEmail { get; set; }
    public string FrontendBaseUrl { get; set; } = string.Empty;
    public string[] InternalAllowedEmails { get; set; } = [];
}
