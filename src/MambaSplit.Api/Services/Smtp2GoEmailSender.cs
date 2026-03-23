using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MambaSplit.Api.Configuration;
using Microsoft.Extensions.Options;

namespace MambaSplit.Api.Services;

public class Smtp2GoEmailSender : IEmailSender
{
    private const int MaxAttempts = 3;

    private readonly HttpClient _httpClient;
    private readonly EmailOptions _options;
    private readonly ILogger<Smtp2GoEmailSender> _logger;

    public Smtp2GoEmailSender(HttpClient httpClient, IOptions<EmailOptions> options, ILogger<Smtp2GoEmailSender> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<EmailSendResult> SendAsync(EmailSendMessage message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return EmailSendResult.Failed("EMAIL_PROVIDER_NOT_CONFIGURED", "Email API key is not configured");
        }

        if (string.IsNullOrWhiteSpace(_options.FromEmail))
        {
            return EmailSendResult.Failed("EMAIL_PROVIDER_NOT_CONFIGURED", "Email from address is not configured");
        }

        var endpoint = _options.ApiBaseUrl.TrimEnd('/') + "/email/send";
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = JsonContent.Create(new
                    {
                        api_key = _options.ApiKey,
                        sender = _options.FromEmail,
                        sender_name = _options.FromName,
                        to = message.To,
                        cc = message.Cc,
                        bcc = message.Bcc,
                        subject = message.Subject,
                        html_body = message.HtmlBody,
                        text_body = message.TextBody,
                        custom_headers = message.Tags.Count == 0
                            ? Array.Empty<object>()
                            : new[]
                            {
                                new
                                {
                                    header = "X-MambaSplit-Tags",
                                    value = string.Join(",", message.Tags),
                                },
                            },
                        reply_to = string.IsNullOrWhiteSpace(_options.ReplyToEmail) ? null : _options.ReplyToEmail,
                    }),
                };

                _logger.LogInformation(
                    "SMTP2GO send attempt {Attempt} for {RecipientCount} recipients",
                    attempt,
                    message.To.Count + message.Cc.Count + message.Bcc.Count);

                var response = await _httpClient.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    return EmailSendResult.Failed("RATE_LIMITED", "SMTP2GO rate limit reached");
                }

                if ((int)response.StatusCode >= 500 && attempt < MaxAttempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var mappedError = TryReadErrorCode(body) ?? ((int)response.StatusCode).ToString();
                    var mappedMessage = TryReadErrorMessage(body) ?? "SMTP2GO returned a non-success response";
                    _logger.LogWarning(
                        "SMTP2GO non-success response status={StatusCode} errorCode={ErrorCode} details={Details}",
                        (int)response.StatusCode,
                        mappedError,
                        mappedMessage);
                    return EmailSendResult.Failed("SMTP2GO_HTTP_" + mappedError, mappedMessage);
                }

                var providerMessageId = TryReadProviderMessageId(body);
                return EmailSendResult.Success(providerMessageId);
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts)
            {
                _logger.LogWarning(ex, "SMTP2GO send transient failure on attempt {Attempt}", attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested && attempt < MaxAttempts)
            {
                _logger.LogWarning(ex, "SMTP2GO send timed out on attempt {Attempt}", attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SMTP2GO send failed unexpectedly");
                return EmailSendResult.Failed("SMTP2GO_UNEXPECTED_ERROR", "Unexpected SMTP2GO send failure");
            }
        }

        return EmailSendResult.Failed("SMTP2GO_RETRY_EXHAUSTED", "SMTP2GO request failed after retries");
    }

    private static string? TryReadProviderMessageId(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("email_id", out var emailId))
            {
                return emailId.GetString();
            }

            if (document.RootElement.TryGetProperty("request_id", out var requestId))
            {
                return requestId.GetString();
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadErrorCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("error_code", out var errorCode))
            {
                return errorCode.GetString();
            }

            if (document.RootElement.TryGetProperty("error", out var error))
            {
                return error.GetString();
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("error", out var dataError) && dataError.ValueKind == JsonValueKind.String)
                {
                    return dataError.GetString();
                }

                if (data.TryGetProperty("message", out var dataMessage) && dataMessage.ValueKind == JsonValueKind.String)
                {
                    return dataMessage.GetString();
                }

                if (data.TryGetProperty("failures", out var failures) &&
                    failures.ValueKind == JsonValueKind.Array &&
                    failures.GetArrayLength() > 0)
                {
                    var firstFailure = failures[0];
                    if (firstFailure.ValueKind == JsonValueKind.Object)
                    {
                        if (firstFailure.TryGetProperty("message", out var failureMessage) && failureMessage.ValueKind == JsonValueKind.String)
                        {
                            return failureMessage.GetString();
                        }

                        if (firstFailure.TryGetProperty("error", out var failureError) && failureError.ValueKind == JsonValueKind.String)
                        {
                            return failureError.GetString();
                        }
                    }
                }
            }

            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            {
                return error.GetString();
            }

            if (document.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
