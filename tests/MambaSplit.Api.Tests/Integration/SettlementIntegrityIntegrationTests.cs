using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MambaSplit.Api.Data;
using MambaSplit.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

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
    public async Task CreateSettlement_ByNonPayerActor_ReturnsForbidden()
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

    private sealed class SettlementEmailTestFactory : WebApplicationFactory<Program>
    {
        private readonly Func<EmailSendMessage, EmailSendResult> _resultFactory;
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"mambasplit-settlement-email-tests-{Guid.NewGuid():N}.db");
        private readonly string _postgresSchema = $"test_{Guid.NewGuid():N}";
        private readonly object _postgresInitLock = new();
        private readonly TestDatabaseProvider _databaseProvider = TestDatabaseProviderSettings.GetProvider();
        private bool _postgresSchemaInitialized;

        public SettlementEmailTestFactory(Func<EmailSendMessage, EmailSendResult> resultFactory)
        {
            _resultFactory = resultFactory;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var connectionString = BuildConnectionString();
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
                    if (_databaseProvider == TestDatabaseProvider.Postgres)
                    {
                        EnsurePostgresSchemaInitialized(connectionString);
                        options.UseNpgsql(connectionString);
                    }
                    else
                    {
                        options.UseSqlite(connectionString);
                    }
                });
                services.AddSingleton<IEmailSender>(new SettlementEmailSenderStub(_resultFactory));
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _databaseProvider == TestDatabaseProvider.Postgres)
            {
                DropPostgresSchema();
            }

            base.Dispose(disposing);
        }

        private string BuildConnectionString()
        {
            if (_databaseProvider == TestDatabaseProvider.Postgres)
            {
                var baseConnectionString = TestDatabaseProviderSettings.GetPostgresConnectionString();
                var builder = new NpgsqlConnectionStringBuilder(baseConnectionString)
                {
                    SearchPath = _postgresSchema,
                };
                return builder.ConnectionString;
            }

            return $"Data Source={_databasePath}";
        }

        private void EnsurePostgresSchemaInitialized(string connectionString)
        {
            if (_postgresSchemaInitialized)
            {
                return;
            }

            lock (_postgresInitLock)
            {
                if (_postgresSchemaInitialized)
                {
                    return;
                }

                var adminConnectionBuilder = new NpgsqlConnectionStringBuilder(connectionString)
                {
                    SearchPath = string.Empty,
                };
                using var connection = new NpgsqlConnection(adminConnectionBuilder.ConnectionString);
                connection.Open();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = $"drop schema if exists \"{_postgresSchema}\" cascade; create schema \"{_postgresSchema}\";";
                    command.ExecuteNonQuery();
                }

                var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
                    .UseNpgsql(connectionString)
                    .Options;
                using var db = new AppDbContext(dbOptions);
                var createScript = db.Database.GenerateCreateScript();

                using (var createCommand = connection.CreateCommand())
                {
                    createCommand.CommandText = $"set search_path to \"{_postgresSchema}\"; {createScript}";
                    createCommand.ExecuteNonQuery();
                }

                _postgresSchemaInitialized = true;
            }
        }

        private void DropPostgresSchema()
        {
            var builder = new NpgsqlConnectionStringBuilder(TestDatabaseProviderSettings.GetPostgresConnectionString())
            {
                SearchPath = string.Empty,
            };

            using var connection = new NpgsqlConnection(builder.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"drop schema if exists \"{_postgresSchema}\" cascade;";
            command.ExecuteNonQuery();
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
