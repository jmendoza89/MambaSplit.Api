using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MambaSplit.Api.Data;
using MambaSplit.Api.Services;
using MambaSplit.Api.Tests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
            amountCents = 2500L,
            note = "settle dinner",
            settledAt = DateTimeOffset.UtcNow.ToString("O"),
        }, accessB);
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
    public async Task CreateSettlement_SendsSettlementEmail_ToOtherGroupMembers()
    {
        var sentMessages = new List<EmailSendMessage>();
        using var factory = new SettlementEmailTestFactory(message =>
        {
            sentMessages.Add(message);
            return EmailSendResult.Success("provider-123");
        });
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var (accessA, _, userIdA, emailA) = await Signup(client, "User A", "password123");
        var (accessB, _, userIdB, emailB) = await Signup(client, "User B", "password123");
        var (accessC, _, userIdC, emailC) = await Signup(client, "User C", "password123");

        var groupId = await CreateGroup(client, accessA, "Settlement Email Group");
        var inviteTokenB = await Invite(client, groupId, accessA, emailB);
        var inviteTokenC = await Invite(client, groupId, accessA, emailC);

        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = inviteTokenB }, accessB)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = inviteTokenC }, accessC)).StatusCode);

        var createExpense = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Dinner",
            payerUserId = userIdA,
            amountCents = 9000L,
            participants = new[] { userIdA, userIdB, userIdC },
        }, accessA);
        Assert.Equal(HttpStatusCode.OK, createExpense.StatusCode);
        var expenseId = (await ReadJsonObject(createExpense))["expenseId"]!.GetValue<string>();

        sentMessages.Clear();

        var createSettlement = await PostJson(client, $"/api/v1/groups/{groupId}/settlements", new
        {
            fromUserId = userIdB,
            toUserId = userIdA,
            amountCents = 3000L,
            note = "Paid back for dinner",
            settledAt = DateTimeOffset.UtcNow.ToString("O"),
        }, accessB);

        Assert.Equal(HttpStatusCode.Created, createSettlement.StatusCode);
        Assert.Single(sentMessages);

        var message = sentMessages[0];
        var actualRecipients = message.To.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var expectedRecipients = new[] { emailA, emailB, emailC }.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        Assert.Equal(expectedRecipients, actualRecipients);
        Assert.Contains("Settlement recorded in Settlement Email Group", message.Subject);
        Assert.Contains("$30.00", message.HtmlBody);
        Assert.Contains($"https://app.mambasplit.test?groupId={groupId}", message.HtmlBody);
        Assert.Contains("Paid back for dinner", message.TextBody);
        Assert.Contains("settlement", message.Tags);
        Assert.Contains("group:" + Guid.Parse(groupId).ToString("N"), message.Tags);
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
            note = "mismatch",
            settledAt = DateTimeOffset.UtcNow.ToString("O"),
        }, accessB);
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        var payload = await ReadJsonObject(mismatch);
        Assert.Equal("VALIDATION_FAILED", payload["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CreateSettlement_NoOutstandingBalance_ReturnsValidationFailed()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var (accessA, _, userIdA, _) = await Signup(client, "User A", "password123");
        var (accessB, _, userIdB, emailB) = await Signup(client, "User B", "password123");

        var groupId = await CreateGroup(client, accessA, "No Balance Group");
        var inviteToken = await Invite(client, groupId, accessA, emailB);
        var accept = await PostJson(client, "/api/v1/invites/accept", new { token = inviteToken }, accessB);
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);

        // No expenses created — pair has no outstanding balance.
        var createSettlement = await PostJson(client, $"/api/v1/groups/{groupId}/settlements", new
        {
            fromUserId = userIdB,
            toUserId = userIdA,
            amountCents = 1000L,
            note = "no balance",
            settledAt = DateTimeOffset.UtcNow.ToString("O"),
        }, accessB);
        Assert.Equal(HttpStatusCode.BadRequest, createSettlement.StatusCode);
        var payload = await ReadJsonObject(createSettlement);
        Assert.Equal("VALIDATION_FAILED", payload["code"]?.GetValue<string>());
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
            amountCents = 2500L,
            note = "settle dinner",
            settledAt = DateTimeOffset.UtcNow.ToString("O"),
        }, accessB);
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
    public async Task CreateSettlement_ByPayeeActor_Succeeds()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var (accessA, _, userIdA, _) = await Signup(client, "User A", "password123");
        var (accessB, _, userIdB, emailB) = await Signup(client, "User B", "password123");

        var groupId = await CreateGroup(client, accessA, "Settlement Auth Group");
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
            amountCents = 2500L,
            note = "settle dinner",
            settledAt = DateTimeOffset.UtcNow.ToString("O"),
        }, accessA);

        Assert.Equal(HttpStatusCode.Created, createSettlement.StatusCode);
    }

    [Fact]
    public async Task CreateSettlement_ByUnrelatedGroupMember_ReturnsForbidden()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var (accessA, _, userIdA, _) = await Signup(client, "User A", "password123");
        var (accessB, _, userIdB, emailB) = await Signup(client, "User B", "password123");
        var (accessC, _, _, emailC) = await Signup(client, "User C", "password123");

        var groupId = await CreateGroup(client, accessA, "Settlement Unrelated Actor Group");
        var inviteTokenB = await Invite(client, groupId, accessA, emailB);
        var acceptB = await PostJson(client, "/api/v1/invites/accept", new { token = inviteTokenB }, accessB);
        Assert.Equal(HttpStatusCode.OK, acceptB.StatusCode);
        var inviteTokenC = await Invite(client, groupId, accessA, emailC);
        var acceptC = await PostJson(client, "/api/v1/invites/accept", new { token = inviteTokenC }, accessC);
        Assert.Equal(HttpStatusCode.OK, acceptC.StatusCode);

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
            note = "settle dinner",
            settledAt = DateTimeOffset.UtcNow.ToString("O"),
        }, accessC);

        Assert.Equal(HttpStatusCode.Forbidden, createSettlement.StatusCode);
        var payload = await ReadJsonObject(createSettlement);
        Assert.Equal("FORBIDDEN", payload["code"]?.GetValue<string>());
        Assert.Equal("Not authorized to create settlement for another member", payload["message"]?.GetValue<string>());
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

    [Fact]
    public async Task CreateSettlement_FourPersonGroup_DoesNotCrossContaminateOtherPairExpenses()
    {
        // Regression test for Bug 4: backend must only link pair-relevant expenses.
        // Alex/Blair settle first. Carol/Dave must still be able to settle afterward.
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var (aAccess, _, aId, _) = await Signup(client, "Alex", "password123");
        var (bAccess, _, bId, bEmail) = await Signup(client, "Blair", "password123");
        var (cAccess, _, cId, cEmail) = await Signup(client, "Carol", "password123");
        var (dAccess, _, dId, dEmail) = await Signup(client, "Dave", "password123");

        var gId = await CreateGroup(client, aAccess, "4P Group");
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = await Invite(client, gId, aAccess, bEmail) }, bAccess)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = await Invite(client, gId, aAccess, cEmail) }, cAccess)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = await Invite(client, gId, aAccess, dEmail) }, dAccess)).StatusCode);

        // Alex pays $10 000; Blair/Carol/Dave each owe $2 500.
        var expAlex = await PostJson(client, $"/api/v1/groups/{gId}/expenses/equal", new
        {
            description = "Alex pays",
            payerUserId = aId,
            amountCents = 10000L,
            participants = new[] { aId, bId, cId, dId },
        }, aAccess);
        Assert.Equal(HttpStatusCode.OK, expAlex.StatusCode);

        // Carol pays $6 000; Dave owes $3 000.
        var expCarol = await PostJson(client, $"/api/v1/groups/{gId}/expenses/equal", new
        {
            description = "Carol pays",
            payerUserId = cId,
            amountCents = 6000L,
            participants = new[] { cId, dId },
        }, cAccess);
        Assert.Equal(HttpStatusCode.OK, expCarol.StatusCode);
        var carolExpenseId = (await ReadJsonObject(expCarol))["expenseId"]!.GetValue<string>();

        // Blair settles with Alex (Blair owes Alex $2 500).
        var blairSettle = await PostJson(client, $"/api/v1/groups/{gId}/settlements", new
        {
            fromUserId = bId,
            toUserId = aId,
            amountCents = 2500L,
            note = "Blair pays Alex",
            settledAt = DateTimeOffset.UtcNow.ToString("O"),
        }, bAccess);
        Assert.Equal(HttpStatusCode.Created, blairSettle.StatusCode);
        var blairSettlementId = (await ReadJsonObject(blairSettle))["settlementId"]!.GetValue<string>();

        // Verify Carol/Dave's expense is NOT linked to Blair's settlement.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var linkedToBlairSettlement = await db.SettlementExpenses
                .AsNoTracking()
                .Where(se => se.SettlementId == Guid.Parse(blairSettlementId))
                .Select(se => se.ExpenseId.ToString())
                .ToListAsync();
            Assert.DoesNotContain(carolExpenseId, linkedToBlairSettlement);
        }

        // Dave can now settle with Carol without CONFLICT (Bug 4 would have locked carolExpenseId).
        var daveSettle = await PostJson(client, $"/api/v1/groups/{gId}/settlements", new
        {
            fromUserId = dId,
            toUserId = cId,
            amountCents = 3000L,
            note = "Dave pays Carol",
            settledAt = DateTimeOffset.UtcNow.ToString("O"),
        }, dAccess);
        Assert.Equal(HttpStatusCode.Created, daveSettle.StatusCode);
    }

    [Fact]
    public async Task CreateSettlement_ManyExpenses_AutoSelectsAllAndSucceeds()
    {
        // Regression: backend must auto-select ALL unsettled pair expenses, not just
        // a capped subset (e.g. the 50-expense window the old client used).
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var (accessA, _, userIdA, _) = await Signup(client, "User A", "password123");
        var (accessB, _, userIdB, emailB) = await Signup(client, "User B", "password123");

        var groupId = await CreateGroup(client, accessA, "Many Expenses Group");
        var inviteToken = await Invite(client, groupId, accessA, emailB);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = inviteToken }, accessB)).StatusCode);

        // Create 55 expenses so the old 50-expense client window would miss 5.
        long totalOwedCents = 0;
        for (var i = 0; i < 55; i++)
        {
            var exp = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
            {
                description = $"Expense {i + 1}",
                payerUserId = userIdA,
                amountCents = 1000L,
                participants = new[] { userIdA, userIdB },
            }, accessA);
            Assert.Equal(HttpStatusCode.OK, exp.StatusCode);
            totalOwedCents += 500L; // userB owes half
        }

        // Backend auto-selects all 55 expenses; amount = 55 * 500 = 27 500.
        var settle = await PostJson(client, $"/api/v1/groups/{groupId}/settlements", new
        {
            fromUserId = userIdB,
            toUserId = userIdA,
            amountCents = totalOwedCents,
            note = "settle all",
            settledAt = DateTimeOffset.UtcNow.ToString("O"),
        }, accessB);
        Assert.Equal(HttpStatusCode.Created, settle.StatusCode);

        var settlementId = (await ReadJsonObject(settle))["settlementId"]!.GetValue<string>();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var linkedCount = await db.SettlementExpenses
            .AsNoTracking()
            .CountAsync(se => se.SettlementId == Guid.Parse(settlementId));
        Assert.Equal(55, linkedCount);
    }

    [Fact]
    public async Task SettlementSuggestion_FromDebtChain_IsRejectedByCreateSettlement_TwoSourcesOfTruthBug()
    {
        // Regression test for issue #36: the group-details "settlementSuggestions" are computed
        // group-wide via debt-simplification over each member's overall NetBalanceCents
        // (GroupService.BuildSettlementSuggestions), while CreateSettlementAsync validates the
        // submitted amount against a strict pairwise sum of unsettled expenses directly between
        // the two chosen users. These two computations are two independent sources of truth and
        // are not guaranteed to agree — the client should never need to reconcile them, and the
        // API should never suggest an amount its own validator will reject.
        //
        // Chain: A pays for A+B (B owes A 150). B pays for B+C (C owes B 150).
        // B's overall net balance is 0, so BuildSettlementSuggestions treats B as neither a
        // creditor nor a debtor and greedily nets the *only* remaining creditor/debtor pair:
        // "C pays A 150" — even though A and C have no expense or split history together at all.
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var (accessA, _, userIdA, emailA) = await Signup(client, "User A", "password123");
        var (accessB, _, userIdB, emailB) = await Signup(client, "User B", "password123");
        var (accessC, _, userIdC, emailC) = await Signup(client, "User C", "password123");

        var groupId = await CreateGroup(client, accessA, "Debt Chain Group");
        var inviteTokenB = await Invite(client, groupId, accessA, emailB);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = inviteTokenB }, accessB)).StatusCode);
        var inviteTokenC = await Invite(client, groupId, accessA, emailC);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = inviteTokenC }, accessC)).StatusCode);

        // A pays 300, split A+B -> B owes A 150.
        var expA = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "A covers A+B",
            payerUserId = userIdA,
            amountCents = 300L,
            participants = new[] { userIdA, userIdB },
        }, accessA);
        Assert.Equal(HttpStatusCode.OK, expA.StatusCode);

        // B pays 300, split B+C -> C owes B 150. B's net balance is now 300 - 150 (owed to A) - 150 (owed by C, i.e. B's own owed share) = 0.
        var expB = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "B covers B+C",
            payerUserId = userIdB,
            amountCents = 300L,
            participants = new[] { userIdB, userIdC },
        }, accessB);
        Assert.Equal(HttpStatusCode.OK, expB.StatusCode);

        var details = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessA));
        var members = details["members"]?.AsArray() ?? [];
        Assert.Equal(150L, members.FirstOrDefault(m => m?["userId"]?.GetValue<string>() == userIdA)?["netBalanceCents"]?.GetValue<long>());
        Assert.Equal(0L, members.FirstOrDefault(m => m?["userId"]?.GetValue<string>() == userIdB)?["netBalanceCents"]?.GetValue<long>());
        Assert.Equal(-150L, members.FirstOrDefault(m => m?["userId"]?.GetValue<string>() == userIdC)?["netBalanceCents"]?.GetValue<long>());

        var suggestions = details["settlementSuggestions"]?.AsArray() ?? [];
        var suggestion = suggestions.FirstOrDefault(s =>
            s?["fromUserId"]?.GetValue<string>() == userIdC && s?["toUserId"]?.GetValue<string>() == userIdA);
        Assert.NotNull(suggestion);
        var suggestedAmountCents = suggestion!["amountCents"]!.GetValue<long>();
        Assert.Equal(150L, suggestedAmountCents);

        // Submitting exactly what the API suggested should succeed if suggestions and the
        // create-settlement validator were a single source of truth. Today it does not:
        // CreateSettlementAsync finds no direct expense/split history between C and A, so it
        // throws "No outstanding balance exists for this pair" (VALIDATION_FAILED / 400).
        var createSettlement = await PostJson(client, $"/api/v1/groups/{groupId}/settlements", new
        {
            fromUserId = userIdC,
            toUserId = userIdA,
            amountCents = suggestedAmountCents,
            note = "settle per suggestion",
            settledAt = DateTimeOffset.UtcNow.ToString("O"),
        }, accessC);

        Assert.Equal(HttpStatusCode.BadRequest, createSettlement.StatusCode);
        var payload = await ReadJsonObject(createSettlement);
        Assert.Equal("VALIDATION_FAILED", payload["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CreateSettlement_ReversedExpense_IsExcludedFromPairwiseBalance()
    {
        // Regression test for issue #36: a reversed expense's negative splits were silently
        // dropped by the `AmountOwedCents > 0` filter in candidate selection, so the (now moot)
        // original kept counting as live debt forever. Fixed by excluding both reversed
        // originals and reversal entries from candidate selection, mirroring
        // GroupMembershipService's existing reversal exclusion.
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var (accessA, _, userIdA, _) = await Signup(client, "User A", "password123");
        var (accessB, _, userIdB, emailB) = await Signup(client, "User B", "password123");

        var groupId = await CreateGroup(client, accessA, "Reversed Expense Group");
        var inviteToken = await Invite(client, groupId, accessA, emailB);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = inviteToken }, accessB)).StatusCode);

        // Expense 1: 5000, split A+B -> B owes A 2500. Immediately reversed (e.g. a typo fix).
        var expToReverse = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Typo'd expense",
            payerUserId = userIdA,
            amountCents = 5000L,
            participants = new[] { userIdA, userIdB },
        }, accessA);
        Assert.Equal(HttpStatusCode.OK, expToReverse.StatusCode);
        var expenseToReverseId = (await ReadJsonObject(expToReverse))["expenseId"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(client, $"/api/v1/groups/{groupId}/expenses/{expenseToReverseId}", accessA)).StatusCode);

        // Expense 2: 3000, split A+B -> B owes A 1500. This is the only real outstanding debt.
        var realExpense = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Real expense",
            payerUserId = userIdA,
            amountCents = 3000L,
            participants = new[] { userIdA, userIdB },
        }, accessA);
        Assert.Equal(HttpStatusCode.OK, realExpense.StatusCode);

        var details = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessA));
        var members = details["members"]?.AsArray() ?? [];
        Assert.Equal(1500L, members.FirstOrDefault(m => m?["userId"]?.GetValue<string>() == userIdA)?["netBalanceCents"]?.GetValue<long>());
        Assert.Equal(-1500L, members.FirstOrDefault(m => m?["userId"]?.GetValue<string>() == userIdB)?["netBalanceCents"]?.GetValue<long>());

        // Settling for the real 1500 balance must succeed — the reversed 2500 must not count.
        var settle = await PostJson(client, $"/api/v1/groups/{groupId}/settlements", new
        {
            fromUserId = userIdB,
            toUserId = userIdA,
            amountCents = 1500L,
            note = "settle real debt only",
            settledAt = DateTimeOffset.UtcNow.ToString("O"),
        }, accessB);
        Assert.Equal(HttpStatusCode.Created, settle.StatusCode);
    }

    private sealed class SettlementEmailTestFactory : WebApplicationFactory<Program>
    {
        private readonly Func<EmailSendMessage, EmailSendResult> _resultFactory;
        private readonly PostgresTestDatabase _database = new();

        public SettlementEmailTestFactory(Func<EmailSendMessage, EmailSendResult> resultFactory)
        {
            _resultFactory = resultFactory;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var connectionString = _database.ConnectionString;
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["app:security:jwt:issuer"] = "mambasplit-api-test",
                    ["app:security:jwt:secret"] = "test-secret-change-me-test-secret-change-me",
                    ["app:security:jwt:accessTokenMinutes"] = "15",
                    ["app:security:jwt:refreshTokenDays"] = "30",
                    ["app:admin:portalToken"] = "test-admin-token",
                    ["app:database:runMigrationsOnStartup"] = "false",
                    ["ConnectionStrings:Default"] = connectionString,
                    ["Email:Provider"] = "smtp2go",
                    ["Email:ApiBaseUrl"] = "https://api.smtp2go.com/v3",
                    ["Email:ApiKey"] = "test-key",
                    ["Email:FromEmail"] = "mambasplit@mambatech.io",
                    ["Email:FromName"] = "MambaSplit",
                    ["Email:FrontendBaseUrl"] = "https://app.mambasplit.test",
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.RemoveAll<AppDbContext>();
                services.RemoveAll<IEmailSender>();

                services.AddDbContext<AppDbContext>((_, options) =>
                {
                    _database.EnsureCreated();
                    options.UseNpgsql(connectionString);
                });
                services.AddSingleton<IEmailSender>(new SettlementEmailSenderStub(_resultFactory));
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _database.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class SettlementEmailSenderStub : IEmailSender
    {
        private readonly Func<EmailSendMessage, EmailSendResult> _resultFactory;

        public SettlementEmailSenderStub(Func<EmailSendMessage, EmailSendResult> resultFactory)
        {
            _resultFactory = resultFactory;
        }

        public Task<EmailSendResult> SendAsync(EmailSendMessage message, CancellationToken ct = default)
        {
            return Task.FromResult(_resultFactory(message));
        }
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

    private static async Task EnsureDatabaseCreated(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
    }
}
