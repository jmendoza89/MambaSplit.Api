using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MambaSplit.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MambaSplit.Api.Tests.Integration;

public class FlowIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public async Task SignupLoginRefreshLogoutFlow_WorksAndRotatesRefreshTokens()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var email = $"user_{Guid.NewGuid()}@example.com";
        const string password = "password123";

        var signup = await PostJson(client, "/api/v1/auth/signup", new
        {
            email,
            password,
            displayName = "User A",
        });
        Assert.Equal(HttpStatusCode.OK, signup.StatusCode);
        var signupPayload = await ReadJsonObject(signup);
        var refreshToken1 = signupPayload["refreshToken"]!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(signupPayload["accessToken"]!.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(refreshToken1));

        var login = await PostJson(client, "/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var loginPayload = await ReadJsonObject(login);
        Assert.False(string.IsNullOrWhiteSpace(loginPayload["accessToken"]!.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(loginPayload["refreshToken"]!.GetValue<string>()));

        var refresh = await PostJson(client, "/api/v1/auth/refresh", new { refreshToken = refreshToken1 });
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var refreshPayload = await ReadJsonObject(refresh);
        var refreshToken2 = refreshPayload["refreshToken"]!.GetValue<string>();
        Assert.NotEqual(refreshToken1, refreshToken2);

        var oldRefresh = await PostJson(client, "/api/v1/auth/refresh", new { refreshToken = refreshToken1 });
        Assert.Equal(HttpStatusCode.Unauthorized, oldRefresh.StatusCode);

        var logout = await PostJson(client, "/api/v1/auth/logout", new { refreshToken = refreshToken2 });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var refreshAfterLogout = await PostJson(client, "/api/v1/auth/refresh", new { refreshToken = refreshToken2 });
        Assert.Equal(HttpStatusCode.Unauthorized, refreshAfterLogout.StatusCode);
    }

    [Fact]
    public async Task GroupInviteAcceptCreateExpenseAndDetailsFlow_Works()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        const string password = "password123";
        var (accessA, _, userIdA, _) = await Signup(client, "User A", password);
        var (accessB, _, userIdB, emailB) = await Signup(client, "User B", password);
        var (accessC, _, userIdC, emailC) = await Signup(client, "User C", password);

        var groupResp = await PostJson(client, "/api/v1/groups", new { name = "Test Group" }, accessA);
        Assert.Equal(HttpStatusCode.OK, groupResp.StatusCode);
        var groupId = (await ReadJsonObject(groupResp))["id"]!.GetValue<string>();

        var inviteResp = await PostJson(client, $"/api/v1/groups/{groupId}/invites", new { email = emailB }, accessA);
        Assert.Equal(HttpStatusCode.OK, inviteResp.StatusCode);
        var inviteToken = (await ReadJsonObject(inviteResp))["token"]!.GetValue<string>();

        var wrongAccept = await PostJson(client, "/api/v1/invites/accept", new { token = inviteToken }, accessC);
        Assert.Equal(HttpStatusCode.BadRequest, wrongAccept.StatusCode);

        var acceptResp = await PostJson(client, "/api/v1/invites/accept", new { token = inviteToken }, accessB);
        Assert.Equal(HttpStatusCode.OK, acceptResp.StatusCode);

        var inviteCResp = await PostJson(client, $"/api/v1/groups/{groupId}/invites", new { email = emailC }, accessA);
        Assert.Equal(HttpStatusCode.OK, inviteCResp.StatusCode);
        var tokenC = (await ReadJsonObject(inviteCResp))["token"]!.GetValue<string>();
        var acceptC = await PostJson(client, "/api/v1/invites/accept", new { token = tokenC }, accessC);
        Assert.Equal(HttpStatusCode.OK, acceptC.StatusCode);

        var createExpenseResp = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Dinner",
            payerUserId = userIdA,
            amountCents = 1000,
            participants = new[] { userIdA, userIdB, userIdC },
        }, accessA);
        Assert.Equal(HttpStatusCode.OK, createExpenseResp.StatusCode);

        var detailsResp = await Get(client, $"/api/v1/groups/{groupId}/details", accessB);
        Assert.Equal(HttpStatusCode.OK, detailsResp.StatusCode);
        var details = await ReadJsonObject(detailsResp);

        Assert.Equal("Test Group", details["group"]?["name"]?.GetValue<string>());
        Assert.Equal(3, details["members"]?.AsArray().Count);
        Assert.Equal(1, details["summary"]?["expenseCount"]?.GetValue<int>());
        Assert.Equal(1000, details["summary"]?["totalExpenseAmountCents"]?.GetValue<int>());
    }

    [Fact]
    public async Task GroupDelete_OnlyOwnerCanDelete()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        const string password = "password123";
        var (accessA, _, _, _) = await Signup(client, "Owner", password);
        var (accessB, _, _, emailB) = await Signup(client, "Member", password);

        var groupId = (await ReadJsonObject(await PostJson(client, "/api/v1/groups", new { name = "Delete Group" }, accessA)))["id"]!.GetValue<string>();
        var inviteToken = (await ReadJsonObject(await PostJson(client, $"/api/v1/groups/{groupId}/invites", new { email = emailB }, accessA)))["token"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = inviteToken }, accessB)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await Delete(client, $"/api/v1/groups/{groupId}", accessB)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(client, $"/api/v1/groups/{groupId}", accessA)).StatusCode);
    }

    [Fact]
    public async Task ExpenseDelete_OnlyPayerCanDelete()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        const string password = "password123";
        var (accessA, _, userIdA, _) = await Signup(client, "Owner", password);
        var (accessB, _, userIdB, emailB) = await Signup(client, "Member", password);

        var groupId = (await ReadJsonObject(await PostJson(client, "/api/v1/groups", new { name = "Delete Expense" }, accessA)))["id"]!.GetValue<string>();
        var inviteToken = (await ReadJsonObject(await PostJson(client, $"/api/v1/groups/{groupId}/invites", new { email = emailB }, accessA)))["token"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = inviteToken }, accessB)).StatusCode);

        var createExpense = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Lunch",
            payerUserId = userIdA,
            amountCents = 1200,
            participants = new[] { userIdA, userIdB },
        }, accessA);
        var expenseId = (await ReadJsonObject(createExpense))["expenseId"]!.GetValue<string>();

        Assert.Equal(HttpStatusCode.Forbidden, (await Delete(client, $"/api/v1/groups/{groupId}/expenses/{expenseId}", accessB)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(client, $"/api/v1/groups/{groupId}/expenses/{expenseId}", accessA)).StatusCode);
    }

    [Fact]
    public async Task NonMemberCannotGetGroupDetails()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var (accessA, _, _, _) = await Signup(client, "Member", "password123");
        var (accessB, _, _, _) = await Signup(client, "Outsider", "password123");

        var groupId = (await ReadJsonObject(await PostJson(client, "/api/v1/groups", new { name = "Private Group" }, accessA)))["id"]!.GetValue<string>();
        var detailsResp = await Get(client, $"/api/v1/groups/{groupId}/details", accessB);

        Assert.Equal(HttpStatusCode.Forbidden, detailsResp.StatusCode);
    }

    [Fact]
    public async Task CannotCreateDuplicatePendingInviteForSameGroupAndEmail()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var (accessA, _, _, _) = await Signup(client, "Owner", "password123");
        var (_, _, _, inviteeEmail) = await Signup(client, "Invitee", "password123");

        var groupId = (await ReadJsonObject(await PostJson(client, "/api/v1/groups", new { name = "No Duplicates" }, accessA)))["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, $"/api/v1/groups/{groupId}/invites", new { email = inviteeEmail }, accessA)).StatusCode);

        var duplicateResp = await PostJson(client, $"/api/v1/groups/{groupId}/invites", new { email = inviteeEmail.ToUpperInvariant() }, accessA);
        Assert.Equal(HttpStatusCode.Conflict, duplicateResp.StatusCode);
    }

    [Fact]
    public async Task MemberCanCreateExpenseOnBehalfOfAnotherMember_CurrentBehavior()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var (accessA, _, userIdA, _) = await Signup(client, "Owner", "password123");
        var (accessB, _, userIdB, emailB) = await Signup(client, "Member", "password123");

        var groupId = (await ReadJsonObject(await PostJson(client, "/api/v1/groups", new { name = "Delegated Entry" }, accessA)))["id"]!.GetValue<string>();
        var inviteToken = (await ReadJsonObject(await PostJson(client, $"/api/v1/groups/{groupId}/invites", new { email = emailB }, accessA)))["token"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = inviteToken }, accessB)).StatusCode);

        var createExpense = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Entered by member B",
            payerUserId = userIdA,
            amountCents = 1000,
            participants = new[] { userIdA, userIdB },
        }, accessB);
        Assert.Equal(HttpStatusCode.OK, createExpense.StatusCode);

        var detailsResp = await Get(client, $"/api/v1/groups/{groupId}/details", accessA);
        Assert.Equal(HttpStatusCode.OK, detailsResp.StatusCode);
        var details = await ReadJsonObject(detailsResp);
        Assert.Equal(userIdA, details["expenses"]?[0]?["payerUserId"]?.GetValue<string>());
        Assert.Equal(userIdB, details["expenses"]?[0]?["createdByUserId"]?.GetValue<string>());
        Assert.Null(details["expenses"]?[0]?["reversalOfExpenseId"]?.GetValue<string>());
        Assert.Equal(1000L, details["summary"]?["totalExpenseAmountCents"]?.GetValue<long>());
    }

    [Fact]
    public async Task GroupDetailsSummaryAndBalances_AreCorrectBeyond50Expenses()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var (accessA, _, userIdA, _) = await Signup(client, "Owner", "password123");
        var (accessB, _, userIdB, emailB) = await Signup(client, "Member", "password123");

        var groupId = (await ReadJsonObject(await PostJson(client, "/api/v1/groups", new { name = "High Volume Group" }, accessA)))["id"]!.GetValue<string>();
        var inviteToken = (await ReadJsonObject(await PostJson(client, $"/api/v1/groups/{groupId}/invites", new { email = emailB }, accessA)))["token"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = inviteToken }, accessB)).StatusCode);

        for (var i = 0; i < 51; i++)
        {
            var createExpense = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
            {
                description = $"Expense {i}",
                payerUserId = userIdA,
                amountCents = 100,
                participants = new[] { userIdA, userIdB },
            }, accessA);
            Assert.Equal(HttpStatusCode.OK, createExpense.StatusCode);
        }

        var detailsA = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessA));
        var detailsB = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessB));

        Assert.Equal(51, detailsA["summary"]?["expenseCount"]?.GetValue<int>());
        Assert.Equal(5100L, detailsA["summary"]?["totalExpenseAmountCents"]?.GetValue<long>());
        Assert.Equal(2550L, detailsA["me"]?["netBalanceCents"]?.GetValue<long>());
        Assert.Equal(-2550L, detailsB["me"]?["netBalanceCents"]?.GetValue<long>());
        Assert.Equal(50, detailsA["expenses"]?.AsArray().Count);
    }

    [Fact]
    public async Task ExactSplitExpense_HappyPath_ReflectsExpectedSplitsAndBalances()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var (accessA, _, userIdA, _) = await Signup(client, "Owner", "password123");
        var (accessB, _, userIdB, emailB) = await Signup(client, "Member B", "password123");
        var (accessC, _, userIdC, emailC) = await Signup(client, "Member C", "password123");

        var groupId = (await ReadJsonObject(await PostJson(client, "/api/v1/groups", new { name = "Exact Split Group" }, accessA)))["id"]!.GetValue<string>();
        var inviteTokenB = (await ReadJsonObject(await PostJson(client, $"/api/v1/groups/{groupId}/invites", new { email = emailB }, accessA)))["token"]!.GetValue<string>();
        var inviteTokenC = (await ReadJsonObject(await PostJson(client, $"/api/v1/groups/{groupId}/invites", new { email = emailC }, accessA)))["token"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = inviteTokenB }, accessB)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = inviteTokenC }, accessC)).StatusCode);

        var createExact = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/exact", new
        {
            description = "Hotel",
            payerUserId = userIdA,
            amountCents = 1000,
            items = new[]
            {
                new { userId = userIdA, amountCents = 100L },
                new { userId = userIdB, amountCents = 300L },
                new { userId = userIdC, amountCents = 600L },
            },
        }, accessA);
        Assert.Equal(HttpStatusCode.OK, createExact.StatusCode);

        var details = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessA));
        Assert.Equal(1, details["summary"]?["expenseCount"]?.GetValue<int>());
        Assert.Equal(1000L, details["summary"]?["totalExpenseAmountCents"]?.GetValue<long>());
        Assert.Equal(900L, details["me"]?["netBalanceCents"]?.GetValue<long>());

        var members = details["members"]?.AsArray() ?? [];
        var memberB = members.FirstOrDefault(m => m?["userId"]?.GetValue<string>() == userIdB);
        var memberC = members.FirstOrDefault(m => m?["userId"]?.GetValue<string>() == userIdC);
        Assert.Equal(-300L, memberB?["netBalanceCents"]?.GetValue<long>());
        Assert.Equal(-600L, memberC?["netBalanceCents"]?.GetValue<long>());

        var splits = details["expenses"]?[0]?["splits"]?.AsArray() ?? [];
        Assert.Equal(3, splits.Count);
        Assert.Equal(1000L, splits.Sum(s => s?["amountOwedCents"]?.GetValue<long>() ?? 0L));
    }

    [Fact]
    public async Task ExactSplitExpense_InvalidInputs_AreRejected()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var (accessA, _, userIdA, _) = await Signup(client, "Owner", "password123");
        var (accessB, _, userIdB, emailB) = await Signup(client, "Member B", "password123");
        var (_, _, userIdOutsider, _) = await Signup(client, "Outsider", "password123");

        var groupId = (await ReadJsonObject(await PostJson(client, "/api/v1/groups", new { name = "Exact Validation Group" }, accessA)))["id"]!.GetValue<string>();
        var inviteTokenB = (await ReadJsonObject(await PostJson(client, $"/api/v1/groups/{groupId}/invites", new { email = emailB }, accessA)))["token"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = inviteTokenB }, accessB)).StatusCode);

        var emptyItems = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/exact", new
        {
            description = "Invalid",
            payerUserId = userIdA,
            amountCents = 100,
            items = Array.Empty<object>(),
        }, accessA);
        Assert.Equal(HttpStatusCode.BadRequest, emptyItems.StatusCode);

        var sumMismatch = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/exact", new
        {
            description = "Invalid",
            payerUserId = userIdA,
            amountCents = 100,
            items = new[]
            {
                new { userId = userIdA, amountCents = 40L },
                new { userId = userIdB, amountCents = 50L },
            },
        }, accessA);
        Assert.Equal(HttpStatusCode.BadRequest, sumMismatch.StatusCode);

        var duplicateUser = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/exact", new
        {
            description = "Invalid",
            payerUserId = userIdA,
            amountCents = 100,
            items = new[]
            {
                new { userId = userIdA, amountCents = 50L },
                new { userId = userIdA, amountCents = 50L },
            },
        }, accessA);
        Assert.Equal(HttpStatusCode.BadRequest, duplicateUser.StatusCode);

        var negativeAmount = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/exact", new
        {
            description = "Invalid",
            payerUserId = userIdA,
            amountCents = 100,
            items = new[]
            {
                new { userId = userIdA, amountCents = 120L },
                new { userId = userIdB, amountCents = -20L },
            },
        }, accessA);
        Assert.Equal(HttpStatusCode.BadRequest, negativeAmount.StatusCode);

        var nonMemberItem = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/exact", new
        {
            description = "Invalid",
            payerUserId = userIdA,
            amountCents = 100,
            items = new[]
            {
                new { userId = userIdA, amountCents = 50L },
                new { userId = userIdOutsider, amountCents = 50L },
            },
        }, accessA);
        Assert.Equal(HttpStatusCode.BadRequest, nonMemberItem.StatusCode);
    }

    [Fact]
    public async Task EqualSplitExpense_RoundingAndBalances_AreCorrect()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var (accessA, _, userIdA, _) = await Signup(client, "Owner", "password123");
        var (accessB, _, userIdB, emailB) = await Signup(client, "Member B", "password123");
        var (accessC, _, userIdC, emailC) = await Signup(client, "Member C", "password123");

        var groupId = (await ReadJsonObject(await PostJson(client, "/api/v1/groups", new { name = "Rounding Group" }, accessA)))["id"]!.GetValue<string>();
        var inviteTokenB = (await ReadJsonObject(await PostJson(client, $"/api/v1/groups/{groupId}/invites", new { email = emailB }, accessA)))["token"]!.GetValue<string>();
        var inviteTokenC = (await ReadJsonObject(await PostJson(client, $"/api/v1/groups/{groupId}/invites", new { email = emailC }, accessA)))["token"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = inviteTokenB }, accessB)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = inviteTokenC }, accessC)).StatusCode);

        var createEqual = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Dinner",
            payerUserId = userIdA,
            amountCents = 1000,
            participants = new[] { userIdA, userIdB, userIdC },
        }, accessA);
        Assert.Equal(HttpStatusCode.OK, createEqual.StatusCode);

        var details = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessA));
        var splits = details["expenses"]?[0]?["splits"]?.AsArray() ?? [];
        Assert.Equal(3, splits.Count);

        var splitAmounts = splits.Select(s => s?["amountOwedCents"]?.GetValue<long>() ?? 0L).OrderBy(x => x).ToList();
        Assert.Equal(1000L, splitAmounts.Sum());
        Assert.Equal(new List<long> { 333L, 333L, 334L }, splitAmounts);

        var splitByUser = splits.ToDictionary(
            s => s?["userId"]?.GetValue<string>() ?? string.Empty,
            s => s?["amountOwedCents"]?.GetValue<long>() ?? 0L);

        var expectedMeNet = 1000L - splitByUser[userIdA];
        Assert.Equal(expectedMeNet, details["me"]?["netBalanceCents"]?.GetValue<long>());
    }

    [Fact]
    public async Task EqualSplitExpense_InvalidInputs_AreRejected()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var (accessA, _, userIdA, _) = await Signup(client, "Owner", "password123");
        var (accessB, _, userIdB, emailB) = await Signup(client, "Member B", "password123");
        var (_, _, userIdOutsider, _) = await Signup(client, "Outsider", "password123");

        var groupId = (await ReadJsonObject(await PostJson(client, "/api/v1/groups", new { name = "Equal Validation Group" }, accessA)))["id"]!.GetValue<string>();
        var inviteTokenB = (await ReadJsonObject(await PostJson(client, $"/api/v1/groups/{groupId}/invites", new { email = emailB }, accessA)))["token"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = inviteTokenB }, accessB)).StatusCode);

        var emptyParticipants = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Invalid",
            payerUserId = userIdA,
            amountCents = 100,
            participants = Array.Empty<string>(),
        }, accessA);
        Assert.Equal(HttpStatusCode.BadRequest, emptyParticipants.StatusCode);

        var duplicateParticipants = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Invalid",
            payerUserId = userIdA,
            amountCents = 100,
            participants = new[] { userIdA, userIdA },
        }, accessA);
        Assert.Equal(HttpStatusCode.BadRequest, duplicateParticipants.StatusCode);

        var nonMemberParticipant = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Invalid",
            payerUserId = userIdA,
            amountCents = 100,
            participants = new[] { userIdA, userIdOutsider },
        }, accessA);
        Assert.Equal(HttpStatusCode.BadRequest, nonMemberParticipant.StatusCode);

        var nonMemberPayer = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Invalid",
            payerUserId = userIdOutsider,
            amountCents = 100,
            participants = new[] { userIdA, userIdB },
        }, accessA);
        Assert.Equal(HttpStatusCode.BadRequest, nonMemberPayer.StatusCode);

        var zeroAmount = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Invalid",
            payerUserId = userIdA,
            amountCents = 0,
            participants = new[] { userIdA, userIdB },
        }, accessA);
        Assert.Equal(HttpStatusCode.BadRequest, zeroAmount.StatusCode);
    }

    [Fact]
    public async Task DeletingExpense_UpdatesSummaryAndBalances()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var (accessA, _, userIdA, _) = await Signup(client, "Owner", "password123");
        var (accessB, _, userIdB, emailB) = await Signup(client, "Member B", "password123");

        var groupId = (await ReadJsonObject(await PostJson(client, "/api/v1/groups", new { name = "Delete Recalc Group" }, accessA)))["id"]!.GetValue<string>();
        var inviteTokenB = (await ReadJsonObject(await PostJson(client, $"/api/v1/groups/{groupId}/invites", new { email = emailB }, accessA)))["token"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = inviteTokenB }, accessB)).StatusCode);

        var expense1 = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Expense 1",
            payerUserId = userIdA,
            amountCents = 1000,
            participants = new[] { userIdA, userIdB },
        }, accessA);
        var expenseId1 = (await ReadJsonObject(expense1))["expenseId"]!.GetValue<string>();

        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Expense 2",
            payerUserId = userIdB,
            amountCents = 600,
            participants = new[] { userIdA, userIdB },
        }, accessB)).StatusCode);

        var beforeDelete = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessA));
        Assert.Equal(2, beforeDelete["summary"]?["expenseCount"]?.GetValue<int>());
        Assert.Equal(1600L, beforeDelete["summary"]?["totalExpenseAmountCents"]?.GetValue<long>());

        Assert.Equal(HttpStatusCode.NoContent, (await Delete(client, $"/api/v1/groups/{groupId}/expenses/{expenseId1}", accessA)).StatusCode);

        var afterDeleteA = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessA));
        var afterDeleteB = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessB));
        Assert.Equal(3, afterDeleteA["summary"]?["expenseCount"]?.GetValue<int>());
        Assert.Equal(600L, afterDeleteA["summary"]?["totalExpenseAmountCents"]?.GetValue<long>());
        Assert.Equal(-300L, afterDeleteA["me"]?["netBalanceCents"]?.GetValue<long>());
        Assert.Equal(300L, afterDeleteB["me"]?["netBalanceCents"]?.GetValue<long>());

        var reversal = afterDeleteA["expenses"]?.AsArray()
            .FirstOrDefault(e => e?["reversalOfExpenseId"]?.GetValue<string>() == expenseId1);
        Assert.NotNull(reversal);
        Assert.Equal("Reversal: Expense 1", reversal?["description"]?.GetValue<string>());
        Assert.Equal(-1000L, reversal?["amountCents"]?.GetValue<long>());
    }

    [Fact]
    public async Task CreateExpense_WithSameIdempotencyKeyAndPayload_IsIdempotent()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var (accessA, _, userIdA, _) = await Signup(client, "Owner", "password123");
        var (accessB, _, userIdB, emailB) = await Signup(client, "Member B", "password123");

        var groupId = (await ReadJsonObject(await PostJson(client, "/api/v1/groups", new { name = "Idempotent Group" }, accessA)))["id"]!.GetValue<string>();
        var inviteTokenB = (await ReadJsonObject(await PostJson(client, $"/api/v1/groups/{groupId}/invites", new { email = emailB }, accessA)))["token"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = inviteTokenB }, accessB)).StatusCode);

        const string key = "expense-123";
        var first = await PostJson(
            client,
            $"/api/v1/groups/{groupId}/expenses/equal",
            new
            {
                description = "Coffee",
                payerUserId = userIdA,
                amountCents = 300,
                participants = new[] { userIdA, userIdB },
            },
            accessA,
            new Dictionary<string, string> { ["Idempotency-Key"] = key });
        var firstId = (await ReadJsonObject(first))["expenseId"]!.GetValue<string>();

        var second = await PostJson(
            client,
            $"/api/v1/groups/{groupId}/expenses/equal",
            new
            {
                description = "Coffee",
                payerUserId = userIdA,
                amountCents = 300,
                participants = new[] { userIdA, userIdB },
            },
            accessA,
            new Dictionary<string, string> { ["Idempotency-Key"] = key });
        var secondId = (await ReadJsonObject(second))["expenseId"]!.GetValue<string>();
        Assert.Equal(firstId, secondId);

        var details = await ReadJsonObject(await Get(client, $"/api/v1/groups/{groupId}/details", accessA));
        Assert.Equal(1, details["summary"]?["expenseCount"]?.GetValue<int>());
        Assert.Equal(300L, details["summary"]?["totalExpenseAmountCents"]?.GetValue<long>());
    }

    [Fact]
    public async Task CreateExpense_WithSameIdempotencyKeyDifferentPayload_ReturnsConflict()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var (accessA, _, userIdA, _) = await Signup(client, "Owner", "password123");
        var (accessB, _, userIdB, emailB) = await Signup(client, "Member B", "password123");

        var groupId = (await ReadJsonObject(await PostJson(client, "/api/v1/groups", new { name = "Idempotency Conflict Group" }, accessA)))["id"]!.GetValue<string>();
        var inviteTokenB = (await ReadJsonObject(await PostJson(client, $"/api/v1/groups/{groupId}/invites", new { email = emailB }, accessA)))["token"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = inviteTokenB }, accessB)).StatusCode);

        const string key = "expense-456";
        Assert.Equal(HttpStatusCode.OK, (await PostJson(
            client,
            $"/api/v1/groups/{groupId}/expenses/equal",
            new
            {
                description = "Snacks",
                payerUserId = userIdA,
                amountCents = 200,
                participants = new[] { userIdA, userIdB },
            },
            accessA,
            new Dictionary<string, string> { ["Idempotency-Key"] = key })).StatusCode);

        var second = await PostJson(
            client,
            $"/api/v1/groups/{groupId}/expenses/equal",
            new
            {
                description = "Snacks changed",
                payerUserId = userIdA,
                amountCents = 250,
                participants = new[] { userIdA, userIdB },
            },
            accessA,
            new Dictionary<string, string> { ["Idempotency-Key"] = key });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task LargeAmounts_BoundaryAndOverflow_AreHandled()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var (accessA, _, userIdA, _) = await Signup(client, "Owner", "password123");
        var (accessB, _, userIdB, emailB) = await Signup(client, "Member B", "password123");

        var groupId = (await ReadJsonObject(await PostJson(client, "/api/v1/groups", new { name = "Large Amount Group" }, accessA)))["id"]!.GetValue<string>();
        var inviteTokenB = (await ReadJsonObject(await PostJson(client, $"/api/v1/groups/{groupId}/invites", new { email = emailB }, accessA)))["token"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/invites/accept", new { token = inviteTokenB }, accessB)).StatusCode);

        var boundaryCreate = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/exact", new
        {
            description = "Boundary",
            payerUserId = userIdA,
            amountCents = long.MaxValue,
            items = new[]
            {
                new { userId = userIdA, amountCents = 0L },
                new { userId = userIdB, amountCents = long.MaxValue },
            },
        }, accessA);
        Assert.Equal(HttpStatusCode.OK, boundaryCreate.StatusCode);

        var boundaryDetails = await Get(client, $"/api/v1/groups/{groupId}/details", accessA);
        Assert.Equal(HttpStatusCode.OK, boundaryDetails.StatusCode);
        var boundaryPayload = await ReadJsonObject(boundaryDetails);
        Assert.Equal(long.MaxValue, boundaryPayload["summary"]?["totalExpenseAmountCents"]?.GetValue<long>());

        var overflowCreate = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/exact", new
        {
            description = "Overflow",
            payerUserId = userIdA,
            amountCents = long.MaxValue,
            items = new[]
            {
                new { userId = userIdA, amountCents = long.MaxValue },
                new { userId = userIdB, amountCents = 1L },
            },
        }, accessA);
        Assert.Equal(HttpStatusCode.BadRequest, overflowCreate.StatusCode);

        var secondLarge = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new
        {
            description = "Second large expense",
            payerUserId = userIdA,
            amountCents = long.MaxValue,
            participants = new[] { userIdA, userIdB },
        }, accessA);
        Assert.Equal(HttpStatusCode.OK, secondLarge.StatusCode);

        var overflowDetails = await Get(client, $"/api/v1/groups/{groupId}/details", accessA);
        Assert.Equal(HttpStatusCode.BadRequest, overflowDetails.StatusCode);
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

    private static async Task<HttpResponseMessage> Delete(HttpClient client, string url, string? bearer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, url);
        if (!string.IsNullOrWhiteSpace(bearer))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return await client.SendAsync(request);
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
        string? bearer = null,
        Dictionary<string, string>? headers = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrWhiteSpace(bearer))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
        if (headers is not null)
        {
            foreach (var header in headers)
            {
                request.Headers.Add(header.Key, header.Value);
            }
        }

        request.Content = JsonContent.Create(body, options: JsonOptions);
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
