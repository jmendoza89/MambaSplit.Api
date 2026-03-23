using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MambaSplit.Api.Data;
using MambaSplit.Api.Domain;
using MambaSplit.Api.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace MambaSplit.Api.Tests.Integration;

public class GroupMembershipRebalanceIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public async Task TokenAccept_RebalancesSingleUnsettledExpense_AndGroupDetailsReflectUpdatedBalances()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        const string password = "password123";
        var (accessA, _, userIdA, _) = await Signup(client, "Owner", password);
        var (accessB, _, userIdB, emailB) = await Signup(client, "Member B", password);
        var (accessC, _, userIdC, emailC) = await Signup(client, "Member C", password);

        var groupId = await CreateGroup(client, accessA, "Rebalance Single Expense");

        var tokenB = await InviteAndGetToken(client, groupId, emailB, accessA);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = tokenB }, accessB)).StatusCode);

        // 300c, paid by A, split A+B -> each owes 150
        var expResp = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Dinner",
            payerUserId = userIdA,
            amountCents = 300,
            participants = new[] { userIdA, userIdB },
        }, accessA);
        Assert.Equal(HttpStatusCode.OK, expResp.StatusCode);

        var beforeDetails = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessA));
        Assert.Equal(2, beforeDetails["expenses"]?[0]?["splits"]?.AsArray().Count);

        // C accepts invite -> rebalance triggered
        var tokenC = await InviteAndGetToken(client, groupId, emailC, accessA);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = tokenC }, accessC)).StatusCode);

        var afterDetails = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessA));

        // Splits should be rebalanced to 3-way: 300/3=100 each
        var splits = afterDetails["expenses"]?[0]?["splits"]?.AsArray() ?? [];
        Assert.Equal(3, splits.Count);
        Assert.Equal(300L, splits.Sum(s => s?["amountOwedCents"]?.GetValue<long>() ?? 0L));
        Assert.All(splits, s => Assert.Equal(100L, s?["amountOwedCents"]?.GetValue<long>()));

        // Net balances: A paid 300, A owes 100 -> net=200; B owes 100 -> net=-100; C owes 100 -> net=-100
        var members = afterDetails["members"]?.AsArray() ?? [];
        var memberA = members.FirstOrDefault(m => m?["userId"]?.GetValue<string>() == userIdA);
        var memberB = members.FirstOrDefault(m => m?["userId"]?.GetValue<string>() == userIdB);
        var memberC = members.FirstOrDefault(m => m?["userId"]?.GetValue<string>() == userIdC);
        Assert.Equal(200L, memberA?["netBalanceCents"]?.GetValue<long>());
        Assert.Equal(-100L, memberB?["netBalanceCents"]?.GetValue<long>());
        Assert.Equal(-100L, memberC?["netBalanceCents"]?.GetValue<long>());
    }

    [Fact]
    public async Task IdAccept_RebalancesSingleUnsettledExpense_WithDeterministicRemainderAllocation()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        const string password = "password123";
        var (accessA, _, userIdA, _) = await Signup(client, "Owner", password);
        var (accessB, _, userIdB, emailB) = await Signup(client, "Member B", password);
        var (accessC, _, _, emailC) = await Signup(client, "Member C", password);

        var groupId = await CreateGroup(client, accessA, "Id Accept Rebalance");

        var tokenB = await InviteAndGetToken(client, groupId, emailB, accessA);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = tokenB }, accessB)).StatusCode);

        // 200c, paid by A, split A+B -> each owes 100
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Lunch",
            payerUserId = userIdA,
            amountCents = 200,
            participants = new[] { userIdA, userIdB },
        }, accessA)).StatusCode);

        // Invite C, get invite ID, accept by ID
        await PostJson(client, $"/api/v1/groups/{groupId}/invites", new { email = emailC }, accessA);
        var pendingResp = await Get(client, $"/api/v1/invites?email={Uri.EscapeDataString(emailC)}", accessC);
        Assert.Equal(HttpStatusCode.OK, pendingResp.StatusCode);
        var pending = await pendingResp.Content.ReadFromJsonAsync<JsonArray>(JsonOptions) ?? [];
        var inviteId = pending[0]?["id"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(inviteId));

        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, $"/api/v1/invites/{inviteId}/accept", new { }, accessC)).StatusCode);

        var details = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessA));
        var splits = details["expenses"]?[0]?["splits"]?.AsArray() ?? [];
        Assert.Equal(3, splits.Count);
        Assert.Equal(200L, splits.Sum(s => s?["amountOwedCents"]?.GetValue<long>() ?? 0L));

        // 200/3 = 66r2 -> 2 members get 67, 1 gets 66 (deterministic by Guid sort)
        var amounts = splits.Select(s => s?["amountOwedCents"]?.GetValue<long>() ?? 0L).OrderBy(x => x).ToList();
        Assert.Equal(new List<long> { 66L, 67L, 67L }, amounts);
    }

    [Fact]
    public async Task TokenAccept_RebalancesMultipleUnsettledExpenses()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        const string password = "password123";
        var (accessA, _, userIdA, _) = await Signup(client, "Owner", password);
        var (accessB, _, userIdB, emailB) = await Signup(client, "Member B", password);
        var (accessC, _, userIdC, emailC) = await Signup(client, "Member C", password);

        var groupId = await CreateGroup(client, accessA, "Multi Expense Rebalance");

        var tokenB = await InviteAndGetToken(client, groupId, emailB, accessA);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = tokenB }, accessB)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Expense 1",
            payerUserId = userIdA,
            amountCents = 300,
            participants = new[] { userIdA, userIdB },
        }, accessA)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Expense 2",
            payerUserId = userIdA,
            amountCents = 600,
            participants = new[] { userIdA, userIdB },
        }, accessA)).StatusCode);

        var tokenC = await InviteAndGetToken(client, groupId, emailC, accessA);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = tokenC }, accessC)).StatusCode);

        var details = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessA));
        var expenses = details["expenses"]?.AsArray() ?? [];
        Assert.Equal(2, expenses.Count);

        // Both expenses must have 3-way splits
        foreach (var expense in expenses)
        {
            var splits = expense?["splits"]?.AsArray() ?? [];
            Assert.Equal(3, splits.Count);
        }

        Assert.Equal(900L, details["summary"]?["totalExpenseAmountCents"]?.GetValue<long>());

        // A paid 300+600=900, A owes 100+200=300 -> A net=600; B net=-300; C net=-300
        var members = details["members"]?.AsArray() ?? [];
        Assert.Equal(600L, members.FirstOrDefault(m => m?["userId"]?.GetValue<string>() == userIdA)?["netBalanceCents"]?.GetValue<long>());
        Assert.Equal(-300L, members.FirstOrDefault(m => m?["userId"]?.GetValue<string>() == userIdB)?["netBalanceCents"]?.GetValue<long>());
        Assert.Equal(-300L, members.FirstOrDefault(m => m?["userId"]?.GetValue<string>() == userIdC)?["netBalanceCents"]?.GetValue<long>());
    }

    [Fact]
    public async Task TokenAccept_SettledExpensesAreExcludedFromRebalance()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        const string password = "password123";
        var (accessA, _, userIdA, _) = await Signup(client, "Owner", password);
        var (accessB, _, userIdB, emailB) = await Signup(client, "Member B", password);
        var (accessC, _, _, emailC) = await Signup(client, "Member C", password);

        var groupId = await CreateGroup(client, accessA, "Settled Excluded Group");

        var tokenB = await InviteAndGetToken(client, groupId, emailB, accessA);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = tokenB }, accessB)).StatusCode);

        // EA: 200c, A pays, A+B -> each 100 -- will be settled
        var eaResp = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Settled expense",
            payerUserId = userIdA,
            amountCents = 200,
            participants = new[] { userIdA, userIdB },
        }, accessA);
        Assert.Equal(HttpStatusCode.OK, eaResp.StatusCode);
        var eaId = (await ReadJsonObject(eaResp))["expenseId"]!.GetValue<string>();

        // Settle EA
        var settleResp = await PostJson(client, $"/api/v1/groups/{groupId}/settlements", new
        {
            fromUserId = userIdB,
            toUserId = userIdA,
            amountCents = 100,
            expenseIds = new[] { eaId },
            note = (string?)null,
            settledAt = DateTimeOffset.UtcNow.ToString("O"),
        }, accessB);
        Assert.Equal(HttpStatusCode.Created, settleResp.StatusCode);

        // EB: 300c, A pays, A+B -> each 150 -- will be rebalanced
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Unsettled expense",
            payerUserId = userIdA,
            amountCents = 300,
            participants = new[] { userIdA, userIdB },
        }, accessA)).StatusCode);

        // C joins
        var tokenC = await InviteAndGetToken(client, groupId, emailC, accessA);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = tokenC }, accessC)).StatusCode);

        var details = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessA));
        var expenses = details["expenses"]?.AsArray() ?? [];

        var ea = expenses.FirstOrDefault(e => e?["description"]?.GetValue<string>() == "Settled expense");
        var eb = expenses.FirstOrDefault(e => e?["description"]?.GetValue<string>() == "Unsettled expense");
        Assert.NotNull(ea);
        Assert.NotNull(eb);

        // EA settled -> splits remain 2-way
        var eaSplits = ea?["splits"]?.AsArray() ?? [];
        Assert.Equal(2, eaSplits.Count);
        Assert.Equal(200L, eaSplits.Sum(s => s?["amountOwedCents"]?.GetValue<long>() ?? 0L));

        // EB unsettled -> splits rebalanced to 3-way
        var ebSplits = eb?["splits"]?.AsArray() ?? [];
        Assert.Equal(3, ebSplits.Count);
        Assert.Equal(300L, ebSplits.Sum(s => s?["amountOwedCents"]?.GetValue<long>() ?? 0L));
    }

    [Fact]
    public async Task TokenAccept_ReversedExpensesAreExcludedFromRebalance()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        const string password = "password123";
        var (accessA, _, userIdA, _) = await Signup(client, "Owner", password);
        var (accessB, _, userIdB, emailB) = await Signup(client, "Member B", password);
        var (accessC, _, _, emailC) = await Signup(client, "Member C", password);

        var groupId = await CreateGroup(client, accessA, "Reversed Excluded Group");

        var tokenB = await InviteAndGetToken(client, groupId, emailB, accessA);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = tokenB }, accessB)).StatusCode);

        // EA: 300c - will be reversed
        var eaResp = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Reversed expense",
            payerUserId = userIdA,
            amountCents = 300,
            participants = new[] { userIdA, userIdB },
        }, accessA);
        Assert.Equal(HttpStatusCode.OK, eaResp.StatusCode);
        var eaId = (await ReadJsonObject(eaResp))["expenseId"]!.GetValue<string>();

        // Reverse EA (delete)
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(client, $"/api/v1/groups/{groupId}/expenses/{eaId}", accessA)).StatusCode);

        // EC: 400c, A pays, A+B - unsettled, should be rebalanced
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Unsettled expense",
            payerUserId = userIdA,
            amountCents = 400,
            participants = new[] { userIdA, userIdB },
        }, accessA)).StatusCode);

        // C joins
        var tokenC = await InviteAndGetToken(client, groupId, emailC, accessA);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = tokenC }, accessC)).StatusCode);

        var details = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessA));
        var expenses = details["expenses"]?.AsArray() ?? [];

        // 3 expenses: original (EA), its reversal, and EC
        Assert.Equal(3, expenses.Count);

        var ec = expenses.FirstOrDefault(e => e?["description"]?.GetValue<string>() == "Unsettled expense");
        Assert.NotNull(ec);

        // EC must have 3-way split summing to 400
        var ecSplits = ec?["splits"]?.AsArray() ?? [];
        Assert.Equal(3, ecSplits.Count);
        Assert.Equal(400L, ecSplits.Sum(s => s?["amountOwedCents"]?.GetValue<long>() ?? 0L));

        // EA original and its reversal must still have 2-way splits
        var ea = expenses.FirstOrDefault(e => e?["description"]?.GetValue<string>() == "Reversed expense");
        var reversal = expenses.FirstOrDefault(e => e?["reversalOfExpenseId"]?.GetValue<string>() == eaId);
        if (ea is not null)
        {
            Assert.Equal(2, ea?["splits"]?.AsArray().Count);
        }
        if (reversal is not null)
        {
            Assert.Equal(2, reversal?["splits"]?.AsArray().Count);
        }
    }

    [Fact]
    public async Task AlreadyMember_NoRebalanceOccurs()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        const string password = "password123";
        var (accessA, _, userIdA, _) = await Signup(client, "Owner", password);
        var (accessB, _, userIdB, emailB) = await Signup(client, "Member B", password);

        var groupId = await CreateGroup(client, accessA, "Already Member Group");

        var tokenB = await InviteAndGetToken(client, groupId, emailB, accessA);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = tokenB }, accessB)).StatusCode);

        // 200c, A pays, A+B -> each 100
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Coffee",
            payerUserId = userIdA,
            amountCents = 200,
            participants = new[] { userIdA, userIdB },
        }, accessA)).StatusCode);

        var before = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessA));
        var splitsBefore = before["expenses"]?[0]?["splits"]?.AsArray() ?? [];
        Assert.Equal(2, splitsBefore.Count);
        Assert.Equal(200L, splitsBefore.Sum(s => s?["amountOwedCents"]?.GetValue<long>() ?? 0L));

        // Call GroupMembershipService directly for B (already a member) - no rebalance should occur
        using var scope = factory.Services.CreateScope();
        var membershipService = scope.ServiceProvider.GetRequiredService<GroupMembershipService>();
        await membershipService.AddMemberAndRebalanceAsync(Guid.Parse(groupId), Guid.Parse(userIdB), Role.MEMBER);

        var after = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessA));
        var splitsAfter = after["expenses"]?[0]?["splits"]?.AsArray() ?? [];
        Assert.Equal(2, splitsAfter.Count);
        Assert.Equal(200L, splitsAfter.Sum(s => s?["amountOwedCents"]?.GetValue<long>() ?? 0L));
    }

    private static async Task<string> CreateGroup(HttpClient client, string bearer, string name)
    {
        var resp = await PostJson(client, "/api/v1/groups", new { name }, bearer);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await ReadJsonObject(resp))["id"]!.GetValue<string>();
    }

    private static async Task<string> InviteAndGetToken(HttpClient client, string groupId, string inviteeEmail, string bearer)
    {
        var resp = await PostJson(client, $"/api/v1/groups/{groupId}/invites", new { email = inviteeEmail }, bearer);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await ReadJsonObject(resp))["token"]!.GetValue<string>();
    }

    private static async Task<(string AccessToken, string RefreshToken, string UserId, string Email)> Signup(
        HttpClient client,
        string displayName,
        string password)
    {
        var email = $"user_{Guid.NewGuid()}@example.com";
        var response = await PostJson(client, "/api/v1/auth/signup", new { email, password, displayName });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await ReadJsonObject(response);
        return (
            payload["accessToken"]!.GetValue<string>(),
            payload["refreshToken"]!.GetValue<string>(),
            payload["user"]!["id"]!.GetValue<string>(),
            email);
    }

    private static async Task<HttpResponseMessage> Delete(HttpClient client, string url, string? bearer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, url);
        if (!string.IsNullOrWhiteSpace(bearer))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Get(HttpClient client, string url, string? bearer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(bearer))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostJson(HttpClient client, string url, object body, string? bearer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrWhiteSpace(bearer))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
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
}