using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MambaSplit.Api.Data;
using MambaSplit.Api.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace MambaSplit.Api.Tests.Integration;

public class GroupMemberLeaveIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public async Task LeaveGroup_SingleUnsettledExpense_RebalancesAcrossRemainingMembers()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        const string password = "password123";
        var (accessA, _, userIdA, _) = await Signup(client, "Owner", password);
        var (accessB, _, userIdB, emailB) = await Signup(client, "Member B", password);
        var (accessC, _, userIdC, emailC) = await Signup(client, "Member C", password);

        var groupId = await CreateGroup(client, accessA, "Leave Single Expense");

        var tokenB = await InviteAndGetToken(client, groupId, emailB, accessA);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = tokenB }, accessB)).StatusCode);

        var tokenC = await InviteAndGetToken(client, groupId, emailC, accessA);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = tokenC }, accessC)).StatusCode);

        // 300c, paid by A, split A+B+C -> each 100
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Dinner",
            payerUserId = userIdA,
            amountCents = 300,
            participants = new[] { userIdA, userIdB, userIdC },
        }, accessA)).StatusCode);

        var beforeDetails = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessA));
        Assert.Equal(3, beforeDetails["expenses"]?[0]?["splits"]?.AsArray().Count);

        // C leaves
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(client, $"/api/v1/groups/{groupId}/members/me", accessC)).StatusCode);

        var afterDetails = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessA));

        // Splits should be rebalanced to 2-way: 300/2=150 each
        var splits = afterDetails["expenses"]?[0]?["splits"]?.AsArray() ?? [];
        Assert.Equal(2, splits.Count);
        Assert.Equal(300L, splits.Sum(s => s?["amountOwedCents"]?.GetValue<long>() ?? 0L));
        Assert.All(splits, s => Assert.Equal(150L, s?["amountOwedCents"]?.GetValue<long>()));

        // C is no longer a member
        var members = afterDetails["members"]?.AsArray() ?? [];
        Assert.Equal(2, members.Count);
        Assert.DoesNotContain(members, m => m?["userId"]?.GetValue<string>() == userIdC);
    }

    [Fact]
    public async Task LeaveGroup_MultipleUnsettledExpenses_RebalancesAllEligible()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        const string password = "password123";
        var (accessA, _, userIdA, _) = await Signup(client, "Owner", password);
        var (accessB, _, userIdB, emailB) = await Signup(client, "Member B", password);
        var (accessC, _, userIdC, emailC) = await Signup(client, "Member C", password);

        var groupId = await CreateGroup(client, accessA, "Leave Multi Expense");

        var tokenB = await InviteAndGetToken(client, groupId, emailB, accessA);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = tokenB }, accessB)).StatusCode);

        var tokenC = await InviteAndGetToken(client, groupId, emailC, accessA);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = tokenC }, accessC)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Expense 1",
            payerUserId = userIdA,
            amountCents = 300,
            participants = new[] { userIdA, userIdB, userIdC },
        }, accessA)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Expense 2",
            payerUserId = userIdA,
            amountCents = 600,
            participants = new[] { userIdA, userIdB, userIdC },
        }, accessA)).StatusCode);

        // C leaves -> both expenses must be rebalanced to 2-way
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(client, $"/api/v1/groups/{groupId}/members/me", accessC)).StatusCode);

        var details = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessA));
        var expenses = details["expenses"]?.AsArray() ?? [];
        Assert.Equal(2, expenses.Count);

        foreach (var expense in expenses)
        {
            var splits = expense?["splits"]?.AsArray() ?? [];
            Assert.Equal(2, splits.Count);
        }

        // A paid 300+600=900, A owes 150+300=450 -> A net=450; B owes 150+300=450 -> B net=-450
        var members = details["members"]?.AsArray() ?? [];
        Assert.Equal(450L, members.FirstOrDefault(m => m?["userId"]?.GetValue<string>() == userIdA)?["netBalanceCents"]?.GetValue<long>());
        Assert.Equal(-450L, members.FirstOrDefault(m => m?["userId"]?.GetValue<string>() == userIdB)?["netBalanceCents"]?.GetValue<long>());
    }

    [Fact]
    public async Task LeaveGroup_SettledExpensesExcludedFromRebalance()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        const string password = "password123";
        var (accessA, _, userIdA, _) = await Signup(client, "Owner", password);
        var (accessB, _, userIdB, emailB) = await Signup(client, "Member B", password);
        var (accessC, _, userIdC, emailC) = await Signup(client, "Member C", password);

        var groupId = await CreateGroup(client, accessA, "Leave Settled Excluded");

        var tokenB = await InviteAndGetToken(client, groupId, emailB, accessA);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = tokenB }, accessB)).StatusCode);

        var tokenC = await InviteAndGetToken(client, groupId, emailC, accessA);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = tokenC }, accessC)).StatusCode);

        // EA: settled 3-way
        var eaResp = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Settled expense",
            payerUserId = userIdA,
            amountCents = 300,
            participants = new[] { userIdA, userIdB, userIdC },
        }, accessA);
        Assert.Equal(HttpStatusCode.OK, eaResp.StatusCode);
        var eaId = (await ReadJsonObject(eaResp))["expenseId"]!.GetValue<string>();

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

        // EB: unsettled 3-way
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Unsettled expense",
            payerUserId = userIdA,
            amountCents = 600,
            participants = new[] { userIdA, userIdB, userIdC },
        }, accessA)).StatusCode);

        // C leaves
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(client, $"/api/v1/groups/{groupId}/members/me", accessC)).StatusCode);

        var details = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessA));
        var expenses = details["expenses"]?.AsArray() ?? [];

        var ea = expenses.FirstOrDefault(e => e?["description"]?.GetValue<string>() == "Settled expense");
        var eb = expenses.FirstOrDefault(e => e?["description"]?.GetValue<string>() == "Unsettled expense");
        Assert.NotNull(ea);
        Assert.NotNull(eb);

        // EA settled -> splits remain 3-way
        var eaSplits = ea?["splits"]?.AsArray() ?? [];
        Assert.Equal(3, eaSplits.Count);
        Assert.Equal(300L, eaSplits.Sum(s => s?["amountOwedCents"]?.GetValue<long>() ?? 0L));

        // EB unsettled -> splits rebalanced to 2-way
        var ebSplits = eb?["splits"]?.AsArray() ?? [];
        Assert.Equal(2, ebSplits.Count);
        Assert.Equal(600L, ebSplits.Sum(s => s?["amountOwedCents"]?.GetValue<long>() ?? 0L));
    }

    [Fact]
    public async Task LeaveGroup_ReversedExpensesExcludedFromRebalance()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        const string password = "password123";
        var (accessA, _, userIdA, _) = await Signup(client, "Owner", password);
        var (accessB, _, userIdB, emailB) = await Signup(client, "Member B", password);
        var (accessC, _, userIdC, emailC) = await Signup(client, "Member C", password);

        var groupId = await CreateGroup(client, accessA, "Leave Reversed Excluded");

        var tokenB = await InviteAndGetToken(client, groupId, emailB, accessA);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = tokenB }, accessB)).StatusCode);

        var tokenC = await InviteAndGetToken(client, groupId, emailC, accessA);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = tokenC }, accessC)).StatusCode);

        // EA: will be reversed
        var eaResp = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Reversed expense",
            payerUserId = userIdA,
            amountCents = 300,
            participants = new[] { userIdA, userIdB, userIdC },
        }, accessA);
        Assert.Equal(HttpStatusCode.OK, eaResp.StatusCode);
        var eaId = (await ReadJsonObject(eaResp))["expenseId"]!.GetValue<string>();

        Assert.Equal(HttpStatusCode.NoContent, (await Delete(client, $"/api/v1/groups/{groupId}/expenses/{eaId}", accessA)).StatusCode);

        // EB: unsettled, should be rebalanced
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Unsettled expense",
            payerUserId = userIdA,
            amountCents = 400,
            participants = new[] { userIdA, userIdB, userIdC },
        }, accessA)).StatusCode);

        // C leaves
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(client, $"/api/v1/groups/{groupId}/members/me", accessC)).StatusCode);

        var details = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessA));
        var expenses = details["expenses"]?.AsArray() ?? [];

        // EA (reversed original) and its reversal still have 3-way splits; EB rebalanced to 2-way
        var ea = expenses.FirstOrDefault(e => e?["description"]?.GetValue<string>() == "Reversed expense");
        var reversal = expenses.FirstOrDefault(e => e?["reversalOfExpenseId"]?.GetValue<string>() == eaId);
        var eb = expenses.FirstOrDefault(e => e?["description"]?.GetValue<string>() == "Unsettled expense");

        if (ea is not null)
        {
            Assert.Equal(3, ea?["splits"]?.AsArray().Count);
        }
        if (reversal is not null)
        {
            Assert.Equal(3, reversal?["splits"]?.AsArray().Count);
        }

        Assert.NotNull(eb);
        var ebSplits = eb?["splits"]?.AsArray() ?? [];
        Assert.Equal(2, ebSplits.Count);
        Assert.Equal(400L, ebSplits.Sum(s => s?["amountOwedCents"]?.GetValue<long>() ?? 0L));
    }

    [Fact]
    public async Task LeaveGroup_NoExpenses_SucceedsWithoutRebalanceWork()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        const string password = "password123";
        var (accessA, _, _, _) = await Signup(client, "Owner", password);
        var (accessB, _, userIdB, emailB) = await Signup(client, "Member B", password);

        var groupId = await CreateGroup(client, accessA, "Leave No Expenses");

        var tokenB = await InviteAndGetToken(client, groupId, emailB, accessA);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = tokenB }, accessB)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await Delete(client, $"/api/v1/groups/{groupId}/members/me", accessB)).StatusCode);

        var details = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessA));
        var members = details["members"]?.AsArray() ?? [];
        Assert.Single(members);
        Assert.DoesNotContain(members, m => m?["userId"]?.GetValue<string>() == userIdB);
    }

    [Fact]
    public async Task LeaveGroup_OnlySettledExpenses_SucceedsWithoutRebalanceWork()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        const string password = "password123";
        var (accessA, _, userIdA, _) = await Signup(client, "Owner", password);
        var (accessB, _, userIdB, emailB) = await Signup(client, "Member B", password);
        var (accessC, _, userIdC, emailC) = await Signup(client, "Member C", password);

        var groupId = await CreateGroup(client, accessA, "Leave Only Settled");

        var tokenB = await InviteAndGetToken(client, groupId, emailB, accessA);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = tokenB }, accessB)).StatusCode);

        var tokenC = await InviteAndGetToken(client, groupId, emailC, accessA);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = tokenC }, accessC)).StatusCode);

        var eResp = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Settled expense",
            payerUserId = userIdA,
            amountCents = 300,
            participants = new[] { userIdA, userIdB, userIdC },
        }, accessA);
        Assert.Equal(HttpStatusCode.OK, eResp.StatusCode);
        var eId = (await ReadJsonObject(eResp))["expenseId"]!.GetValue<string>();

        var settleResp = await PostJson(client, $"/api/v1/groups/{groupId}/settlements", new
        {
            fromUserId = userIdB,
            toUserId = userIdA,
            amountCents = 100,
            expenseIds = new[] { eId },
            note = (string?)null,
            settledAt = DateTimeOffset.UtcNow.ToString("O"),
        }, accessB);
        Assert.Equal(HttpStatusCode.Created, settleResp.StatusCode);

        // C leaves - no unsettled expenses to rebalance
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(client, $"/api/v1/groups/{groupId}/members/me", accessC)).StatusCode);

        var details = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessA));
        var members = details["members"]?.AsArray() ?? [];
        Assert.Equal(2, members.Count);

        // Settled expense splits unchanged (3-way)
        var splits = details["expenses"]?[0]?["splits"]?.AsArray() ?? [];
        Assert.Equal(3, splits.Count);
        Assert.Equal(300L, splits.Sum(s => s?["amountOwedCents"]?.GetValue<long>() ?? 0L));
    }

    [Fact]
    public async Task LeaveGroup_TransactionRollback_MembershipRetained()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        const string password = "password123";
        var (accessA, _, userIdA, _) = await Signup(client, "Owner", password);
        var (accessB, _, userIdB, emailB) = await Signup(client, "Member B", password);

        var groupId = await CreateGroup(client, accessA, "Rollback Test");

        var tokenB = await InviteAndGetToken(client, groupId, emailB, accessA);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = tokenB }, accessB)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Test expense",
            payerUserId = userIdA,
            amountCents = 200,
            participants = new[] { userIdA, userIdB },
        }, accessA)).StatusCode);

        var groupIdParsed = Guid.Parse(groupId);
        var userIdBParsed = Guid.Parse(userIdB);

        // Call RemoveMemberAndRebalanceAsync directly within a transaction that we explicitly rollback
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var membershipService = scope.ServiceProvider.GetRequiredService<GroupMembershipService>();

        await using var tx = await db.Database.BeginTransactionAsync();
        await membershipService.RemoveMemberAndRebalanceAsync(groupIdParsed, userIdBParsed);
        await tx.RollbackAsync();

        // After rollback, B should still be a member and splits unchanged
        var details = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessA));
        var members = details["members"]?.AsArray() ?? [];
        Assert.Equal(2, members.Count);
        Assert.Contains(members, m => m?["userId"]?.GetValue<string>() == userIdB);

        var splits = details["expenses"]?[0]?["splits"]?.AsArray() ?? [];
        Assert.Equal(2, splits.Count);
        Assert.Equal(200L, splits.Sum(s => s?["amountOwedCents"]?.GetValue<long>() ?? 0L));
    }

    [Fact]
    public async Task LeaveGroup_DeterministicRemainderAllocation_PreservedAfterLeave()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        const string password = "password123";
        var (accessA, _, userIdA, _) = await Signup(client, "Owner", password);
        var (accessB, _, userIdB, emailB) = await Signup(client, "Member B", password);
        var (accessC, _, _, emailC) = await Signup(client, "Member C", password);
        var (accessD, _, _, emailD) = await Signup(client, "Member D", password);

        var groupId = await CreateGroup(client, accessA, "Deterministic Remainder Leave");

        var tokenB = await InviteAndGetToken(client, groupId, emailB, accessA);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = tokenB }, accessB)).StatusCode);

        var tokenC = await InviteAndGetToken(client, groupId, emailC, accessA);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = tokenC }, accessC)).StatusCode);

        var tokenD = await InviteAndGetToken(client, groupId, emailD, accessA);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = tokenD }, accessD)).StatusCode);

        // 200c, split 4 ways -> 200/4=50 each (no remainder)
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Even split",
            payerUserId = userIdA,
            amountCents = 200,
            participants = new[] { userIdA, userIdB },
        }, accessA)).StatusCode);

        // 100c with 3 remaining after D leaves -> 100/3 = 33r1: 2 members get 34, 1 gets 33
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Remainder split",
            payerUserId = userIdA,
            amountCents = 100,
            participants = new[] { userIdA, userIdB },
        }, accessA)).StatusCode);

        // D leaves -> remaining = A, B, C (3 members)
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(client, $"/api/v1/groups/{groupId}/members/me", accessD)).StatusCode);

        var details = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessA));
        var expenses = details["expenses"]?.AsArray() ?? [];

        var remainderExpense = expenses.FirstOrDefault(e => e?["description"]?.GetValue<string>() == "Remainder split");
        Assert.NotNull(remainderExpense);

        var splits = remainderExpense?["splits"]?.AsArray() ?? [];
        Assert.Equal(3, splits.Count);
        Assert.Equal(100L, splits.Sum(s => s?["amountOwedCents"]?.GetValue<long>() ?? 0L));

        var amounts = splits.Select(s => s?["amountOwedCents"]?.GetValue<long>() ?? 0L).OrderBy(x => x).ToList();
        Assert.Equal(new List<long> { 33L, 33L, 34L }, amounts);
    }

    [Fact]
    public async Task LeaveGroup_OwnerCannotLeave()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        const string password = "password123";
        var (accessA, _, _, _) = await Signup(client, "Owner", password);
        var (accessB, _, _, emailB) = await Signup(client, "Member B", password);

        var groupId = await CreateGroup(client, accessA, "Owner Leave Rejected");

        var tokenB = await InviteAndGetToken(client, groupId, emailB, accessA);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = tokenB }, accessB)).StatusCode);

        var resp = await Delete(client, $"/api/v1/groups/{groupId}/members/me", accessA);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task LeaveGroup_LastRemainingMemberCannotLeave()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        const string password = "password123";
        var (accessA, _, _, _) = await Signup(client, "Owner", password);

        var groupId = await CreateGroup(client, accessA, "Last Member Leave Rejected");

        // Owner is the only member; attempting to leave should fail
        var resp = await Delete(client, $"/api/v1/groups/{groupId}/members/me", accessA);
        // Owner rule fires first (400), but if owner rule weren't there, last-member rule (400) would fire
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task LeaveGroup_NonMemberLeaveAttemptFailsCleanly()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        const string password = "password123";
        var (accessA, _, _, _) = await Signup(client, "Owner", password);
        var (accessB, _, _, _) = await Signup(client, "Non-member", password);

        var groupId = await CreateGroup(client, accessA, "Non Member Leave");

        var resp = await Delete(client, $"/api/v1/groups/{groupId}/members/me", accessB);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task LeaveGroup_GroupDetailsReflectUpdatedBalancesAfterLeave()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        const string password = "password123";
        var (accessA, _, userIdA, _) = await Signup(client, "Owner", password);
        var (accessB, _, userIdB, emailB) = await Signup(client, "Member B", password);
        var (accessC, _, userIdC, emailC) = await Signup(client, "Member C", password);

        var groupId = await CreateGroup(client, accessA, "Balance After Leave");

        var tokenB = await InviteAndGetToken(client, groupId, emailB, accessA);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = tokenB }, accessB)).StatusCode);

        var tokenC = await InviteAndGetToken(client, groupId, emailC, accessA);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = tokenC }, accessC)).StatusCode);

        // 300c, A pays, split A+B+C -> each 100
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Dinner",
            payerUserId = userIdA,
            amountCents = 300,
            participants = new[] { userIdA, userIdB, userIdC },
        }, accessA)).StatusCode);

        // Before: A net=200, B net=-100, C net=-100
        var before = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessA));
        var beforeMembers = before["members"]?.AsArray() ?? [];
        Assert.Equal(200L, beforeMembers.FirstOrDefault(m => m?["userId"]?.GetValue<string>() == userIdA)?["netBalanceCents"]?.GetValue<long>());
        Assert.Equal(-100L, beforeMembers.FirstOrDefault(m => m?["userId"]?.GetValue<string>() == userIdB)?["netBalanceCents"]?.GetValue<long>());
        Assert.Equal(-100L, beforeMembers.FirstOrDefault(m => m?["userId"]?.GetValue<string>() == userIdC)?["netBalanceCents"]?.GetValue<long>());

        // C leaves -> expense rebalanced A+B -> each 150; A net=150, B net=-150
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(client, $"/api/v1/groups/{groupId}/members/me", accessC)).StatusCode);

        var after = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessA));
        var afterMembers = after["members"]?.AsArray() ?? [];

        Assert.Equal(2, afterMembers.Count);
        Assert.Equal(150L, afterMembers.FirstOrDefault(m => m?["userId"]?.GetValue<string>() == userIdA)?["netBalanceCents"]?.GetValue<long>());
        Assert.Equal(-150L, afterMembers.FirstOrDefault(m => m?["userId"]?.GetValue<string>() == userIdB)?["netBalanceCents"]?.GetValue<long>());
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
