using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MambaSplit.Api.Data;
using MambaSplit.Api.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MambaSplit.Api.Tests.Integration;

public class GroupPaginationIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public async Task ListExpenses_ReturnsBoundedPageWithSplitsSettlementIdsAndCursor()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var context = await CreateTwoMemberGroup(client, "Expense Pagination Group");
        var createdAt = DateTimeOffset.UtcNow.AddHours(-1);
        var expenseIds = await SeedExpenses(factory, context.GroupId, context.UserIdA, context.UserIdB, 28, createdAt);
        var linkedSettlementId = await SeedSettlement(factory, context.GroupId, context.UserIdB, context.UserIdA, createdAt.AddMinutes(1), expenseIds[1]);

        var first = await Get(client, $"/api/v1/groups/{context.GroupId}/expenses?limit=100", context.AccessA);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstPayload = await ReadJsonObject(first);
        var firstExpenses = firstPayload["expenses"]!.AsArray();

        Assert.Equal(25, firstExpenses.Count);
        Assert.True(firstPayload["hasMoreExpenses"]!.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(firstPayload["nextBefore"]?.GetValue<string>()));

        var firstIds = firstExpenses.Select(e => e!["id"]!.GetValue<string>()).ToList();
        Assert.Equal(expenseIds.Take(25).Select(id => id.ToString()).ToList(), firstIds);

        var linkedExpense = firstExpenses.Single(e => e!["id"]!.GetValue<string>() == expenseIds[1].ToString())!;
        Assert.Equal(linkedSettlementId.ToString(), linkedExpense["settlementId"]!.GetValue<string>());
        Assert.True(linkedExpense["isSettled"]!.GetValue<bool>());
        Assert.Equal(2, linkedExpense["splits"]!.AsArray().Count);

        var nextBefore = Uri.EscapeDataString(firstPayload["nextBefore"]!.GetValue<string>());
        var second = await Get(client, $"/api/v1/groups/{context.GroupId}/expenses?before={nextBefore}&limit=25", context.AccessA);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondPayload = await ReadJsonObject(second);
        var secondExpenses = secondPayload["expenses"]!.AsArray();

        Assert.Equal(3, secondExpenses.Count);
        Assert.False(secondPayload["hasMoreExpenses"]!.GetValue<bool>());
        Assert.Null(secondPayload["nextBefore"]);
        Assert.DoesNotContain(firstIds[^1], secondExpenses.Select(e => e!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ListExpenses_RequiresGroupMembership()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var context = await CreateTwoMemberGroup(client, "Expense Auth Group");
        var outsider = await Signup(client, "Outsider", "password123");

        var response = await Get(client, $"/api/v1/groups/{context.GroupId}/expenses", outsider.AccessToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ListSettlements_ReturnsMetadataOnlyWithExpenseCountAndCursor()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var context = await CreateTwoMemberGroup(client, "Settlement Pagination Group");
        var createdAt = DateTimeOffset.UtcNow.AddHours(-2);
        var expenseIds = await SeedExpenses(factory, context.GroupId, context.UserIdA, context.UserIdB, 12, createdAt);
        var settlementIds = new List<Guid>();

        for (var i = 0; i < 7; i++)
        {
            settlementIds.Add(await SeedSettlement(
                factory,
                context.GroupId,
                context.UserIdB,
                context.UserIdA,
                createdAt.AddMinutes(100 + i),
                expenseIds[i]));
        }

        var first = await Get(client, $"/api/v1/groups/{context.GroupId}/settlements?limit=100", context.AccessA);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstPayload = await ReadJsonObject(first);
        var firstRows = firstPayload["settlements"]!.AsArray();

        Assert.Equal(5, firstRows.Count);
        Assert.True(firstPayload["hasMoreSettlements"]!.GetValue<bool>());
        Assert.Equal(settlementIds.AsEnumerable().Reverse().Take(5).Select(id => id.ToString()).ToList(), firstRows.Select(s => s!["id"]!.GetValue<string>()).ToList());
        Assert.All(firstRows, row =>
        {
            Assert.Equal(1, row!["expenseCount"]!.GetValue<int>());
            Assert.Null(row["expenseIds"]);
        });

        var outsider = await Signup(client, "Settlement Outsider", "password123");
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(client, $"/api/v1/groups/{context.GroupId}/settlements", outsider.AccessToken)).StatusCode);

        var nextBefore = Uri.EscapeDataString(firstPayload["nextBefore"]!.GetValue<string>());
        var second = await Get(client, $"/api/v1/groups/{context.GroupId}/settlements?before={nextBefore}", context.AccessA);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondPayload = await ReadJsonObject(second);
        Assert.Equal(2, secondPayload["settlements"]!.AsArray().Count);
        Assert.False(secondPayload["hasMoreSettlements"]!.GetValue<bool>());
        Assert.Null(secondPayload["nextBefore"]);
    }

    [Fact]
    public async Task ListSettlementExpenses_RequiresMembershipAndOwnershipAndReturnsBoundedPages()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var context = await CreateTwoMemberGroup(client, "Settlement Detail Group");
        var otherContext = await CreateTwoMemberGroup(client, "Other Settlement Detail Group");
        var createdAt = DateTimeOffset.UtcNow.AddHours(-3);
        var expenseIds = await SeedExpenses(factory, context.GroupId, context.UserIdA, context.UserIdB, 30, createdAt);
        var settlementId = await SeedSettlement(factory, context.GroupId, context.UserIdB, context.UserIdA, createdAt.AddHours(1), expenseIds);

        var first = await Get(client, $"/api/v1/groups/{context.GroupId}/settlements/{settlementId}/expenses?limit=100", context.AccessA);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstPayload = await ReadJsonObject(first);

        Assert.Equal(settlementId.ToString(), firstPayload["settlement"]!["id"]!.GetValue<string>());
        Assert.Equal(30, firstPayload["settlement"]!["expenseCount"]!.GetValue<int>());
        Assert.Null(firstPayload["settlement"]!["expenseIds"]);
        Assert.Equal(25, firstPayload["expenses"]!.AsArray().Count);
        Assert.True(firstPayload["hasMoreExpenses"]!.GetValue<bool>());

        var outsider = await Signup(client, "Detail Outsider", "password123");
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(client, $"/api/v1/groups/{context.GroupId}/settlements/{settlementId}/expenses", outsider.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(client, $"/api/v1/groups/{otherContext.GroupId}/settlements/{settlementId}/expenses", otherContext.AccessA)).StatusCode);

        var nextBefore = Uri.EscapeDataString(firstPayload["nextBefore"]!.GetValue<string>());
        var second = await Get(client, $"/api/v1/groups/{context.GroupId}/settlements/{settlementId}/expenses?before={nextBefore}", context.AccessA);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondPayload = await ReadJsonObject(second);

        Assert.Equal(5, secondPayload["expenses"]!.AsArray().Count);
        Assert.False(secondPayload["hasMoreExpenses"]!.GetValue<bool>());
        Assert.Null(secondPayload["nextBefore"]);
    }

    [Fact]
    public async Task GroupDetails_ReturnsNewestExpenseAndSettlementWindows()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var context = await CreateTwoMemberGroup(client, "Group Details Pagination Group");
        var createdAt = DateTimeOffset.UtcNow.AddHours(-4);
        var expenseIds = await SeedExpenses(factory, context.GroupId, context.UserIdA, context.UserIdB, 26, createdAt);
        for (var i = 0; i < 6; i++)
        {
            await SeedSettlement(factory, context.GroupId, context.UserIdB, context.UserIdA, createdAt.AddMinutes(100 + i), expenseIds[i]);
        }

        var response = await Get(client, $"/api/v1/groups/{context.GroupId}/details", context.AccessA);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await ReadJsonObject(response);

        Assert.Equal(25, payload["expenses"]!.AsArray().Count);
        Assert.Equal(5, payload["settlements"]!.AsArray().Count);
        Assert.True(payload["hasMoreExpenses"]!.GetValue<bool>());
        Assert.True(payload["hasMoreSettlements"]!.GetValue<bool>());
        Assert.Equal(26, payload["summary"]!["expenseCount"]!.GetValue<int>());
        Assert.Equal(6, payload["summary"]!["settlementCount"]!.GetValue<int>());
        Assert.All(payload["settlements"]!.AsArray(), row =>
        {
            Assert.NotNull(row!["expenseCount"]);
            Assert.Null(row["expenseIds"]);
        });
    }

    private static async Task<GroupContext> CreateTwoMemberGroup(HttpClient client, string groupName)
    {
        var userA = await Signup(client, "User A", "password123");
        var userB = await Signup(client, "User B", "password123");
        var groupId = await CreateGroup(client, userA.AccessToken, groupName);
        var inviteToken = await Invite(client, groupId, userA.AccessToken, userB.Email);

        var accept = await PostJson(client, "/api/v1/invites/accept", new { token = inviteToken }, userB.AccessToken);
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);

        return new GroupContext(
            Guid.Parse(groupId),
            Guid.Parse(userA.UserId),
            Guid.Parse(userB.UserId),
            userA.AccessToken,
            userB.AccessToken);
    }

    private static async Task<List<Guid>> SeedExpenses(
        WebApplicationFactory<Program> factory,
        Guid groupId,
        Guid payerUserId,
        Guid otherUserId,
        int count,
        DateTimeOffset oldestCreatedAt)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var expenseIds = new List<Guid>();

        for (var i = 0; i < count; i++)
        {
            var expenseId = Guid.NewGuid();
            expenseIds.Add(expenseId);
            db.Expenses.Add(new ExpenseEntity
            {
                Id = expenseId,
                GroupId = groupId,
                PayerUserId = payerUserId,
                CreatedByUserId = payerUserId,
                Description = $"Seed expense {i + 1}",
                AmountCents = 1000,
                CreatedAt = oldestCreatedAt.AddMinutes(count - i),
            });
            db.ExpenseSplits.Add(new ExpenseSplitEntity
            {
                Id = Guid.NewGuid(),
                ExpenseId = expenseId,
                UserId = payerUserId,
                AmountOwedCents = 500,
            });
            db.ExpenseSplits.Add(new ExpenseSplitEntity
            {
                Id = Guid.NewGuid(),
                ExpenseId = expenseId,
                UserId = otherUserId,
                AmountOwedCents = 500,
            });
        }

        await db.SaveChangesAsync();
        return expenseIds;
    }

    private static Task<Guid> SeedSettlement(
        WebApplicationFactory<Program> factory,
        Guid groupId,
        Guid fromUserId,
        Guid toUserId,
        DateTimeOffset createdAt,
        params Guid[] expenseIds) =>
        SeedSettlement(factory, groupId, fromUserId, toUserId, createdAt, expenseIds.AsEnumerable());

    private static async Task<Guid> SeedSettlement(
        WebApplicationFactory<Program> factory,
        Guid groupId,
        Guid fromUserId,
        Guid toUserId,
        DateTimeOffset createdAt,
        IEnumerable<Guid> expenseIds)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settlementId = Guid.NewGuid();
        db.Settlements.Add(new SettlementEntity
        {
            Id = settlementId,
            GroupId = groupId,
            FromUserId = fromUserId,
            ToUserId = toUserId,
            AmountCents = 500,
            CreatedAt = createdAt,
        });

        foreach (var expenseId in expenseIds)
        {
            db.SettlementExpenses.Add(new SettlementExpenseEntity
            {
                Id = Guid.NewGuid(),
                SettlementId = settlementId,
                ExpenseId = expenseId,
            });
        }

        await db.SaveChangesAsync();
        return settlementId;
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

    private static async Task<UserContext> Signup(HttpClient client, string displayName, string password)
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
        return new UserContext(
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

    private static async Task<JsonObject> ReadJsonObject(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadFromJsonAsync<JsonObject>(JsonOptions);
        return payload ?? new JsonObject();
    }

    private static async Task EnsureDatabaseCreated(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    private record UserContext(string AccessToken, string RefreshToken, string UserId, string Email);
    private record GroupContext(Guid GroupId, Guid UserIdA, Guid UserIdB, string AccessA, string AccessB);
}
