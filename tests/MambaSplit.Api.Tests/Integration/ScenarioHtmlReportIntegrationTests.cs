using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MambaSplit.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MambaSplit.Api.Tests.Integration;

public class ScenarioHtmlReportIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public async Task GenerateScenarioHtmlReport_WithIntegratedData()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);
        var scenarioEvents = new List<string>();

        var users = new List<(string AccessToken, string UserId, string Email)>();
        for (var i = 1; i <= 8; i++)
        {
            users.Add(await Signup(client, $"Scenario User {i}", "password123"));
        }

        var groupA = await CreateGroup(client, users[0].AccessToken, "Scenario Group A");
        var groupB = await CreateGroup(client, users[1].AccessToken, "Scenario Group B");
        scenarioEvents.Add("Created Scenario Group A and Scenario Group B.");
        await InviteMembers(client, groupA, users[0], users[2], users[3], users[4], users[5]);
        await InviteMembers(client, groupB, users[1], users[0], users[3], users[6], users[7]);
        scenarioEvents.Add("Invited and accepted group members.");
        var groupAMemberIds = new[] { users[0].UserId, users[2].UserId, users[3].UserId, users[4].UserId, users[5].UserId };
        var groupBMemberIds = new[] { users[0].UserId, users[1].UserId, users[3].UserId, users[6].UserId, users[7].UserId };

        for (var i = 0; i < 12; i++)
        {
            await CreateEqualExpense(client, groupA, users[0].AccessToken, groupAMemberIds[i % groupAMemberIds.Length], 1200 + (i * 75), groupAMemberIds, $"GA Equal {i + 1}");
        }
        scenarioEvents.Add("Created 12 equal-split expenses in Group A.");

        for (var i = 0; i < 8; i++)
        {
            var items = new[]
            {
                new { userId = users[0].UserId, amountCents = 200L + i },
                new { userId = users[2].UserId, amountCents = 300L + i },
                new { userId = users[3].UserId, amountCents = 400L + i },
                new { userId = users[4].UserId, amountCents = 500L + i },
            };
            var total = items.Sum(x => x.amountCents);
            await CreateExactExpense(client, groupA, users[2].AccessToken, users[2].UserId, total, items, $"GA Exact {i + 1}");
        }
        scenarioEvents.Add("Created 8 exact-split expenses in Group A.");

        for (var i = 0; i < 10; i++)
        {
            await CreateEqualExpense(client, groupB, users[1].AccessToken, groupBMemberIds[i % groupBMemberIds.Length], 900 + (i * 50), groupBMemberIds, $"GB Equal {i + 1}");
        }
        scenarioEvents.Add("Created 10 equal-split expenses in Group B.");

        var gaSettleExpenseId = await CreateEqualExpenseAndGetId(
            client,
            groupA,
            users[0].AccessToken,
            users[0].UserId,
            1000,
            new[] { users[0].UserId, users[3].UserId },
            "GA settle pair");
        _ = await CreateSettlement(
            client,
            groupA,
            users[3].AccessToken,
            users[3].UserId,
            users[0].UserId,
            500,
            new List<string> { gaSettleExpenseId },
            "GA settle pair");

        var gbSettleExpenseId = await CreateEqualExpenseAndGetId(
            client,
            groupB,
            users[1].AccessToken,
            users[1].UserId,
            1200,
            new[] { users[1].UserId, users[6].UserId },
            "GB settle pair");
        _ = await CreateSettlement(
            client,
            groupB,
            users[6].AccessToken,
            users[6].UserId,
            users[1].UserId,
            600,
            new List<string> { gbSettleExpenseId },
            "GB settle pair");
        scenarioEvents.Add("Created deterministic pair settlements for both groups.");

        var resetResponse = await AdminResetGroupSettlements(client, groupA, users[0].AccessToken);
        scenarioEvents.Add($"Admin reset settlements for Group A (deleted={resetResponse.deletedSettlementCount}, releasedExpenses={resetResponse.releasedExpenseCount}).");

        var gaResettleExpenseId = await CreateEqualExpenseAndGetId(
            client,
            groupA,
            users[2].AccessToken,
            users[2].UserId,
            1400,
            new[] { users[2].UserId, users[4].UserId },
            "GA re-settle pair");
        _ = await CreateSettlement(
            client,
            groupA,
            users[4].AccessToken,
            users[4].UserId,
            users[2].UserId,
            700,
            new List<string> { gaResettleExpenseId },
            "GA re-settle pair");
        scenarioEvents.Add("Re-settled Group A using a deterministic pair settlement after admin reset.");

        var outputPath = Path.Combine(
            FindRepositoryRoot(),
            "artifacts",
            "scenario-reports",
            $"scenario-report-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.html");
        outputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        await ExportHtmlReport(factory, outputPath, scenarioEvents);
        Assert.True(File.Exists(outputPath));
    }

    private static async Task ExportHtmlReport(CustomWebApplicationFactory factory, string outputPath, List<string> scenarioEvents)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var users = (await db.Users.AsNoTracking().ToListAsync()).OrderBy(x => x.CreatedAt).ToList();
        var groups = (await db.Groups.AsNoTracking().ToListAsync()).OrderBy(x => x.CreatedAt).ToList();
        var members = (await db.GroupMembers.AsNoTracking().ToListAsync()).OrderBy(x => x.JoinedAt).ToList();
        var expenses = (await db.Expenses.AsNoTracking().ToListAsync()).OrderByDescending(x => x.CreatedAt).ToList();
        var settlements = (await db.Settlements.AsNoTracking().ToListAsync()).OrderByDescending(x => x.CreatedAt).ToList();
        var settlementExpenses = (await db.SettlementExpenses.AsNoTracking().ToListAsync()).OrderBy(x => x.SettlementId).ToList();

        var summaryRows = groups.Select(g =>
        {
            var groupExpenses = expenses.Where(e => e.GroupId == g.Id).ToList();
            var groupSettlements = settlements.Where(s => s.GroupId == g.Id).ToList();
            return new
            {
                GroupId = g.Id.ToString(),
                GroupName = g.Name,
                ExpenseCount = groupExpenses.Count,
                ExpenseTotal = groupExpenses.Sum(e => e.AmountCents),
                SettlementCount = groupSettlements.Count,
                ActiveSettlementTotal = groupSettlements.Sum(s => s.AmountCents),
            };
        }).ToList();

        var maxExpense = Math.Max(1L, summaryRows.Select(x => x.ExpenseTotal).DefaultIfEmpty(1L).Max());
        var maxSettlement = Math.Max(1L, summaryRows.Select(x => x.ActiveSettlementTotal).DefaultIfEmpty(1L).Max());

        var html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html><head><meta charset=\"utf-8\" />");
        html.AppendLine("<title>MambaSplit Scenario Report</title>");
        html.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:20px;color:#1f2937}h1,h2{margin:0 0 12px}section{margin:22px 0}table{border-collapse:collapse;width:100%;font-size:12px}th,td{border:1px solid #d1d5db;padding:6px 8px;text-align:left}th{background:#f3f4f6}.kpi{display:grid;grid-template-columns:repeat(4,minmax(120px,1fr));gap:12px}.card{border:1px solid #d1d5db;border-radius:8px;padding:10px;background:#f9fafb}.bars{display:grid;gap:10px}.bar-row{display:grid;grid-template-columns:180px 1fr 90px;gap:10px;align-items:center}.bar{height:14px;border-radius:7px;background:#e5e7eb;overflow:hidden}.fill-exp{height:100%;background:#0f766e}.fill-set{height:100%;background:#1d4ed8}</style>");
        html.AppendLine("</head><body>");
        html.AppendLine("<h1>MambaSplit Scenario Report</h1>");
        html.AppendLine($"<p>Generated UTC: {DateTimeOffset.UtcNow:O}</p>");

        html.AppendLine("<section><h2>Overview</h2><div class=\"kpi\">");
        html.AppendLine($"<div class=\"card\"><b>Total Users</b><div>{users.Count}</div></div>");
        html.AppendLine($"<div class=\"card\"><b>Total Groups</b><div>{groups.Count}</div></div>");
        html.AppendLine($"<div class=\"card\"><b>Total Expenses</b><div>{expenses.Count}</div></div>");
        html.AppendLine($"<div class=\"card\"><b>Total Settlements</b><div>{settlements.Count}</div></div>");
        html.AppendLine("</div></section>");

        html.AppendLine("<section><h2>Scenario Steps</h2><ol>");
        foreach (var scenarioEvent in scenarioEvents)
        {
            html.AppendLine($"<li>{Encode(scenarioEvent)}</li>");
        }
        html.AppendLine("</ol></section>");

        html.AppendLine("<section><h2>Expense Totals By Group</h2><div class=\"bars\">");
        foreach (var row in summaryRows)
        {
            var width = (int)Math.Round((double)row.ExpenseTotal / maxExpense * 100);
            html.AppendLine($"<div class=\"bar-row\"><div>{Encode(row.GroupName)}</div><div class=\"bar\"><div class=\"fill-exp\" style=\"width:{width}%\"></div></div><div>{row.ExpenseTotal}</div></div>");
        }
        html.AppendLine("</div></section>");

        html.AppendLine("<section><h2>Settlement Totals By Group</h2><div class=\"bars\">");
        foreach (var row in summaryRows)
        {
            var width = (int)Math.Round((double)row.ActiveSettlementTotal / maxSettlement * 100);
            html.AppendLine($"<div class=\"bar-row\"><div>{Encode(row.GroupName)}</div><div class=\"bar\"><div class=\"fill-set\" style=\"width:{width}%\"></div></div><div>{row.ActiveSettlementTotal}</div></div>");
        }
        html.AppendLine("</div></section>");

        html.AppendLine("<section><h2>Summary By Group</h2>");
        html.AppendLine("<table><thead><tr><th>group_id</th><th>group_name</th><th>expense_count</th><th>expense_total_cents</th><th>settlement_count</th><th>settlement_total_cents</th></tr></thead><tbody>");
        foreach (var row in summaryRows)
        {
            html.AppendLine($"<tr><td>{Encode(row.GroupId)}</td><td>{Encode(row.GroupName)}</td><td>{row.ExpenseCount}</td><td>{row.ExpenseTotal}</td><td>{row.SettlementCount}</td><td>{row.ActiveSettlementTotal}</td></tr>");
        }
        html.AppendLine("</tbody></table></section>");

        AppendTable(html, "Users", new[] { "id", "email", "display_name", "created_at" }, users.Select(x => new object?[] { x.Id, x.Email, x.DisplayName, x.CreatedAt }));
        AppendTable(html, "Groups", new[] { "id", "name", "created_by", "created_at" }, groups.Select(x => new object?[] { x.Id, x.Name, x.CreatedBy, x.CreatedAt }));
        AppendTable(html, "GroupMembers", new[] { "id", "group_id", "user_id", "role", "joined_at" }, members.Select(x => new object?[] { x.Id, x.GroupId, x.UserId, x.Role.ToString(), x.JoinedAt }));
        AppendTable(html, "Expenses", new[] { "id", "group_id", "payer_user_id", "created_by_user_id", "description", "amount_cents", "reversal_of_expense_id", "created_at" }, expenses.Select(x => new object?[] { x.Id, x.GroupId, x.PayerUserId, x.CreatedByUserId, x.Description, x.AmountCents, x.ReversalOfExpenseId, x.CreatedAt }));
        AppendTable(html, "Settlements", new[] { "id", "group_id", "from_user_id", "to_user_id", "amount_cents", "note", "created_at" }, settlements.Select(x => new object?[] { x.Id, x.GroupId, x.FromUserId, x.ToUserId, x.AmountCents, x.Note, x.CreatedAt }));
        AppendTable(html, "SettlementExpenses", new[] { "id", "settlement_id", "expense_id" }, settlementExpenses.Select(x => new object?[] { x.Id, x.SettlementId, x.ExpenseId }));

        html.AppendLine("</body></html>");
        await File.WriteAllTextAsync(outputPath, html.ToString());
    }

    private static void AppendTable(StringBuilder html, string title, IReadOnlyList<string> headers, IEnumerable<object?[]> rows)
    {
        html.AppendLine($"<section><h2>{Encode(title)}</h2><table><thead><tr>");
        foreach (var header in headers)
        {
            html.AppendLine($"<th>{Encode(header)}</th>");
        }
        html.AppendLine("</tr></thead><tbody>");
        foreach (var row in rows)
        {
            html.AppendLine("<tr>");
            foreach (var cell in row)
            {
                html.AppendLine($"<td>{Encode(cell?.ToString() ?? string.Empty)}</td>");
            }
            html.AppendLine("</tr>");
        }
        html.AppendLine("</tbody></table></section>");
    }

    private static string Encode(string value) => System.Net.WebUtility.HtmlEncode(value);

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var solutionPath = Path.Combine(current.FullName, "MambaSplit.Api.sln");
            if (File.Exists(solutionPath))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static async Task<(string AccessToken, string UserId, string Email)> Signup(HttpClient client, string displayName, string password)
    {
        var email = $"scenario_{Guid.NewGuid():N}@example.com";
        var response = await PostJson(client, "/api/v1/auth/signup", new { email, password, displayName });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await ReadJsonObject(response);
        return (payload["accessToken"]!.GetValue<string>(), payload["user"]!["id"]!.GetValue<string>(), email);
    }

    private static async Task<string> CreateGroup(HttpClient client, string bearer, string name)
    {
        var response = await PostJson(client, "/api/v1/groups", new { name }, bearer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadJsonObject(response))["id"]!.GetValue<string>();
    }

    private static async Task InviteMembers(HttpClient client, string groupId, (string AccessToken, string UserId, string Email) owner, params (string AccessToken, string UserId, string Email)[] invitees)
    {
        foreach (var invitee in invitees)
        {
            var invite = await PostJson(client, $"/api/v1/groups/{groupId}/invites", new { email = invitee.Email }, owner.AccessToken);
            Assert.Equal(HttpStatusCode.OK, invite.StatusCode);
            var token = (await ReadJsonObject(invite))["token"]!.GetValue<string>();
            var accept = await PostJson(client, "/api/v1/invites/accept", new { token }, invitee.AccessToken);
            Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        }
    }

    private static async Task CreateEqualExpense(HttpClient client, string groupId, string bearer, string payerUserId, long amountCents, string[] participants, string description)
    {
        var response = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new { description, payerUserId, amountCents, participants }, bearer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<string> CreateEqualExpenseAndGetId(HttpClient client, string groupId, string bearer, string payerUserId, long amountCents, string[] participants, string description)
    {
        var response = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/equal", new { description, payerUserId, amountCents, participants }, bearer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadJsonObject(response))["expenseId"]!.GetValue<string>();
    }

    private static async Task CreateExactExpense(HttpClient client, string groupId, string bearer, string payerUserId, long amountCents, object[] items, string description)
    {
        var response = await PostJson(client, $"/api/v1/groups/{groupId}/expenses/exact", new { description, payerUserId, amountCents, items }, bearer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<string> CreateSettlement(HttpClient client, string groupId, string bearer, string fromUserId, string toUserId, long amountCents, List<string> expenseIds, string note)
    {
        var response = await PostJson(client, $"/api/v1/groups/{groupId}/settlements", new { fromUserId, toUserId, amountCents, expenseIds, note, settledAt = DateTimeOffset.UtcNow.ToString("O") }, bearer);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadJsonObject(response))["settlementId"]!.GetValue<string>();
    }

    private static async Task<(int deletedSettlementCount, int releasedExpenseCount)> AdminResetGroupSettlements(HttpClient client, string groupId, string bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/admin/groups/{groupId}/settlements");
        request.Headers.Add("X-Admin-Portal-Token", "test-admin-token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadJsonObject(response);
        return (
            body["deletedSettlementCount"]?.GetValue<int>() ?? 0,
            body["releasedExpenseCount"]?.GetValue<int>() ?? 0);
    }

    private static async Task<JsonObject> GetGroupDetails(HttpClient client, string groupId, string bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/groups/{groupId}/details");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonObject(response);
    }

    private static async Task<HttpResponseMessage> PostJson(HttpClient client, string url, object body, string? bearer = null)
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

    private static async Task EnsureDatabaseCreated(CustomWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
    }
}
