namespace MambaSplit.Api.Services;

public interface IEmailSender
{
    Task<EmailSendResult> SendAsync(EmailSendMessage message, CancellationToken ct = default);
}

public record EmailSendMessage(
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    IReadOnlyList<string> Bcc,
    string Subject,
    string HtmlBody,
    string TextBody,
    IReadOnlyList<string> Tags);

public record EmailSendResult(
    bool Accepted,
    string Status,
    string? ProviderMessageId,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static EmailSendResult Success(string? providerMessageId) =>
        new(true, "accepted", providerMessageId, null, null);

    public static EmailSendResult Failed(string errorCode, string errorMessage) =>
        new(false, "failed", null, errorCode, errorMessage);
}
