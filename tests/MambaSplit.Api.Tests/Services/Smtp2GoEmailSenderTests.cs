using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using MambaSplit.Api.Configuration;
using MambaSplit.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MambaSplit.Api.Tests.Services;

public class Smtp2GoEmailSenderTests
{
    [Fact]
    public async Task SendAsync_MapsPayloadAndAuthHeader()
    {
        string? capturedBody = null;
        string[]? capturedApiKeyValues = null;

        var handler = new StubHttpMessageHandler(async request =>
        {
            if (request.Headers.TryGetValues("X-Smtp2go-Api-Key", out var apiKeyValues))
            {
                capturedApiKeyValues = apiKeyValues.ToArray();
            }

            capturedBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":{\"email_id\":\"abc123\"}}", Encoding.UTF8, "application/json"),
            };
        });

        var sender = CreateSender(handler);
        var result = await sender.SendAsync(new EmailSendMessage(
            ["to@example.com"],
            [],
            [],
            "subject",
            "<p>html</p>",
            "text",
            ["invite"]));

        Assert.True(result.Accepted);
        Assert.Equal("abc123", result.ProviderMessageId);
        Assert.NotNull(capturedApiKeyValues);
        Assert.Contains("test-api-key", capturedApiKeyValues!);
        Assert.False(string.IsNullOrWhiteSpace(capturedBody));

        using var payload = JsonDocument.Parse(capturedBody!);
        Assert.Equal("from@example.com", payload.RootElement.GetProperty("sender").GetString());
        Assert.Equal("subject", payload.RootElement.GetProperty("subject").GetString());
        Assert.Equal("to@example.com", payload.RootElement.GetProperty("to")[0].GetString());
    }

    [Fact]
    public async Task SendAsync_MapsRateLimitToFailedResult()
    {
        var handler = new StubHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        }));

        var sender = CreateSender(handler);
        var result = await sender.SendAsync(new EmailSendMessage(["to@example.com"], [], [], "subject", "<p>html</p>", "text", []));

        Assert.False(result.Accepted);
        Assert.Equal("failed", result.Status);
        Assert.Equal("RATE_LIMITED", result.ErrorCode);
    }

    [Fact]
    public async Task SendAsync_MapsProviderErrorCode()
    {
        var handler = new StubHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"data\":{\"error_code\":\"INVALID_PAYLOAD\"}}", Encoding.UTF8, "application/json"),
        }));

        var sender = CreateSender(handler);
        var result = await sender.SendAsync(new EmailSendMessage(["to@example.com"], [], [], "subject", "<p>html</p>", "text", []));

        Assert.False(result.Accepted);
        Assert.Equal("SMTP2GO_HTTP_INVALID_PAYLOAD", result.ErrorCode);
    }

    private static Smtp2GoEmailSender CreateSender(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler);
        var options = Options.Create(new EmailOptions
        {
            Provider = "smtp2go",
            ApiBaseUrl = "https://api.smtp2go.com/v3",
            ApiKey = "test-api-key",
            FromEmail = "from@example.com",
            FromName = "MambaSplit",
        });
        return new Smtp2GoEmailSender(client, options, NullLogger<Smtp2GoEmailSender>.Instance);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request);
        }
    }
}
