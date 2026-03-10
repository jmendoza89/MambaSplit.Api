using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MambaSplit.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MambaSplit.Api.Tests.Integration;

public class SettlementIntegrityIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public async Task CreateGroup_PersistsOwnerMembership()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var (accessToken, _, userId, _) = await Signup(client, "Owner", "password123");
        var createGroup = await PostJson(client, "/api/v1/groups", new { name = "Integrity Group" }, accessToken);
        Assert.Equal(HttpStatusCode.OK, createGroup.StatusCode);
        var groupId = (await ReadJsonObject(createGroup))["id"]!.GetValue<string>();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ownerMembership = await db.GroupMembers
            .AsNoTracking()
            .SingleOrDefaultAsync(m => m.GroupId == Guid.Parse(groupId) && m.UserId == Guid.Parse(userId));

        Assert.NotNull(ownerMembership);
    }

    [Fact]
    public async Task CreateSettlement_ValidPayload_SucceedsAndLinksExpenseIds()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var (accessA, _, userIdA, _) = await Signup(client, "User A", "password123");
        var (accessB, _, userIdB, emailB) = await Signup(client, "User B", "password123");

        var groupId = await CreateGroup(client, accessA, "Settlement Group");
        var inviteToken = await Invite(client, groupId, accessA, emailB);
        var accept = await PostJson(client, "/api/v1/invites/accept", new { token = inviteToken }, accessB);
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);

        var createExpense = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Dinner",
            payerUserId = userIdA,
            amountCents = 5000L,
            participants = new[] { userIdA, userIdB },
        }, accessA);
        Assert.Equal(HttpStatusCode.OK, createExpense.StatusCode);
        var expenseId = (await ReadJsonObject(createExpense))["expenseId"]!.GetValue<string>();

        var createSettlement = await PostJson(client, $"/api/v1/groups/{groupId}/settlements", new
        {
            fromUserId = userIdB,
            toUserId = userIdA,
            amountCents = 5000L,
            expenseIds = new[] { expenseId },
            note = "settle dinner",
            settledAt = DateTimeOffset.UtcNow.ToString("O"),
        }, accessA);
        Assert.Equal(HttpStatusCode.Created, createSettlement.StatusCode);
        var settlementId = (await ReadJsonObject(createSettlement))["settlementId"]!.GetValue<string>();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var linkedExpenseIds = await db.SettlementExpenses
                .AsNoTracking()
                .Where(se => se.SettlementId == Guid.Parse(settlementId))
                .Select(se => se.ExpenseId)
                .ToListAsync();

            Assert.Single(linkedExpenseIds);
            Assert.Equal(Guid.Parse(expenseId), linkedExpenseIds[0]);
        }

        var settlementDetails = await Get(client, $"/api/v1/settlements/{settlementId}", accessA);
        Assert.Equal(HttpStatusCode.OK, settlementDetails.StatusCode);
        var detailsPayload = await ReadJsonObject(settlementDetails);
        var linkedExpenses = detailsPayload["expenseIds"]?.AsArray().Select(x => x?.GetValue<string>()).Where(x => x is not null).ToList() ?? [];
        Assert.Single(linkedExpenses);
        Assert.Equal(expenseId, linkedExpenses[0]);
    }

    [Fact]
    public async Task CreateSettlement_AmountMismatch_ReturnsValidationFailed()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var (accessA, _, userIdA, _) = await Signup(client, "User A", "password123");
        var (accessB, _, userIdB, emailB) = await Signup(client, "User B", "password123");

        var groupId = await CreateGroup(client, accessA, "Mismatch Group");
        var inviteToken = await Invite(client, groupId, accessA, emailB);
        var accept = await PostJson(client, "/api/v1/invites/accept", new { token = inviteToken }, accessB);
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);

        var createExpense = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Dinner",
            payerUserId = userIdA,
            amountCents = 5000L,
            participants = new[] { userIdA, userIdB },
        }, accessA);
        Assert.Equal(HttpStatusCode.OK, createExpense.StatusCode);
        var expenseId = (await ReadJsonObject(createExpense))["expenseId"]!.GetValue<string>();

        var mismatch = await PostJson(client, $"/api/v1/groups/{groupId}/settlements", new
        {
            fromUserId = userIdB,
            toUserId = userIdA,
            amountCents = 4999L,
            expenseIds = new[] { expenseId },
            note = "mismatch",
            settledAt = DateTimeOffset.UtcNow.ToString("O"),
        }, accessA);
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        var payload = await ReadJsonObject(mismatch);
        Assert.Equal("VALIDATION_FAILED", payload["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CreateSettlement_WithoutExpenseIds_AllowsCashSettlement()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var (accessA, _, userIdA, _) = await Signup(client, "User A", "password123");
        var (accessB, _, userIdB, emailB) = await Signup(client, "User B", "password123");

        var groupId = await CreateGroup(client, accessA, "Cash Settlement Group");
        var inviteToken = await Invite(client, groupId, accessA, emailB);
        var accept = await PostJson(client, "/api/v1/invites/accept", new { token = inviteToken }, accessB);
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);

        var createExpense = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Dinner",
            payerUserId = userIdA,
            amountCents = 5000L,
            participants = new[] { userIdA, userIdB },
        }, accessA);
        Assert.Equal(HttpStatusCode.OK, createExpense.StatusCode);

        var createSettlement = await PostJson(client, $"/api/v1/groups/{groupId}/settlements", new
        {
            fromUserId = userIdB,
            toUserId = userIdA,
            amountCents = 2500L,
            expenseIds = Array.Empty<string>(),
            note = "cash settle partial",
            settledAt = DateTimeOffset.UtcNow.ToString("O"),
        }, accessA);
        Assert.Equal(HttpStatusCode.Created, createSettlement.StatusCode);
        var settlementId = (await ReadJsonObject(createSettlement))["settlementId"]!.GetValue<string>();

        var settlementDetails = await Get(client, $"/api/v1/settlements/{settlementId}", accessA);
        Assert.Equal(HttpStatusCode.OK, settlementDetails.StatusCode);
        var detailsPayload = await ReadJsonObject(settlementDetails);
        var linkedExpenses = detailsPayload["expenseIds"]?.AsArray().ToList() ?? [];
        Assert.Empty(linkedExpenses);
    }

    [Fact]
    public async Task DeleteExpense_SettlementLinked_ReturnsConflictWithSpecificMessage()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var (accessA, _, userIdA, _) = await Signup(client, "User A", "password123");
        var (accessB, _, userIdB, emailB) = await Signup(client, "User B", "password123");

        var groupId = await CreateGroup(client, accessA, "Delete Conflict Group");
        var inviteToken = await Invite(client, groupId, accessA, emailB);
        var accept = await PostJson(client, "/api/v1/invites/accept", new { token = inviteToken }, accessB);
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);

        var createExpense = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Dinner",
            payerUserId = userIdA,
            amountCents = 5000L,
            participants = new[] { userIdA, userIdB },
        }, accessA);
        Assert.Equal(HttpStatusCode.OK, createExpense.StatusCode);
        var expenseId = (await ReadJsonObject(createExpense))["expenseId"]!.GetValue<string>();

        var createSettlement = await PostJson(client, $"/api/v1/groups/{groupId}/settlements", new
        {
            fromUserId = userIdB,
            toUserId = userIdA,
            amountCents = 5000L,
            expenseIds = new[] { expenseId },
            note = "settle dinner",
            settledAt = DateTimeOffset.UtcNow.ToString("O"),
        }, accessA);
        Assert.Equal(HttpStatusCode.Created, createSettlement.StatusCode);
        var settlementId = (await ReadJsonObject(createSettlement))["settlementId"]!.GetValue<string>();

        var details = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessA));
        var settledExpense = details["expenses"]?.AsArray()
            .FirstOrDefault(e => e?["id"]?.GetValue<string>() == expenseId);
        Assert.NotNull(settledExpense);
        Assert.Equal(settlementId, settledExpense?["settlementId"]?.GetValue<string>());
        Assert.True(settledExpense?["isSettled"]?.GetValue<bool>());

        var delete = await Delete(client, $"/api/v1/groups/{groupId}/expenses/{expenseId}", accessA);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        var payload = await ReadJsonObject(delete);
        Assert.Equal("CONFLICT", payload["code"]?.GetValue<string>());
        Assert.Equal("Expense is settled and cannot be deleted", payload["message"]?.GetValue<string>());
    }

    [Fact]
    public async Task DeleteExpense_UnsettledOwnedExpense_Succeeds()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var (accessA, _, userIdA, _) = await Signup(client, "User A", "password123");
        var (accessB, _, userIdB, emailB) = await Signup(client, "User B", "password123");

        var groupId = await CreateGroup(client, accessA, "Delete Unsettled Group");
        var inviteToken = await Invite(client, groupId, accessA, emailB);
        var accept = await PostJson(client, "/api/v1/invites/accept", new { token = inviteToken }, accessB);
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);

        var createExpense = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Lunch",
            payerUserId = userIdA,
            amountCents = 2400L,
            participants = new[] { userIdA, userIdB },
        }, accessA);
        Assert.Equal(HttpStatusCode.OK, createExpense.StatusCode);
        var expenseId = (await ReadJsonObject(createExpense))["expenseId"]!.GetValue<string>();

        var detailsBefore = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessA));
        var expenseBefore = detailsBefore["expenses"]?.AsArray()
            .FirstOrDefault(e => e?["id"]?.GetValue<string>() == expenseId);
        Assert.NotNull(expenseBefore);
        Assert.Null(expenseBefore?["settlementId"]?.GetValue<string>());
        Assert.False(expenseBefore?["isSettled"]?.GetValue<bool>());

        var delete = await Delete(client, $"/api/v1/groups/{groupId}/expenses/{expenseId}", accessA);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    private static async Task<string> CreateGroup(HttpClient client, string bearer, string name)
    {
        var response = await PostJson(client, "/api/v1/groups", new { name }, bearer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadJsonObject(response))["id"]!.GetValue<string>();
    }

    private static async Task<string> Invite(HttpClient client, string groupId, string ownerBearer, string inviteeEmail)
    {
        var response = await PostJson(client, $"/api/v1/groups/{groupId}/invites", new { email = inviteeEmail }, ownerBearer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadJsonObject(response))["token"]!.GetValue<string>();
    }

    private static async Task<(string AccessToken, string RefreshToken, string UserId, string Email)> Signup(
        HttpClient client,
        string displayName,
        string password)
    {
        var email = $"user_{Guid.NewGuid()}@example.com";
        var response = await PostJson(client, "/api/v1/auth/signup", new
        {
            email,
            password,
            displayName,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await ReadJsonObject(response);
        return (
            payload["accessToken"]!.GetValue<string>(),
            payload["refreshToken"]!.GetValue<string>(),
            payload["user"]!["id"]!.GetValue<string>(),
            email);
    }

    private static async Task<HttpResponseMessage> Get(HttpClient client, string url, string? bearer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(bearer))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostJson(
        HttpClient client,
        string url,
        object body,
        string? bearer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrWhiteSpace(bearer))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        request.Content = JsonContent.Create(body, options: JsonOptions);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Delete(HttpClient client, string url, string? bearer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, url);
        if (!string.IsNullOrWhiteSpace(bearer))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return await client.SendAsync(request);
    }

    private static async Task<JsonObject> ReadJsonObject(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadFromJsonAsync<JsonObject>(JsonOptions);
        return payload ?? new JsonObject();
    }

    private static async Task EnsureDatabaseCreated(CustomWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
    }
}
