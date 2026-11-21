// tests/Ecr.Api.Tests/ScriptMethodologyRunSqlTests.TraceSteps.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// ✎ D-565 (PERF-03) наскрізно: справжній <c>RecalculationJob</c> → оркестратор → модуль скрипта → запис, і HTTP API.
/// Прогін без прапора не пише кроків, а проблема рядка <c>StepError</c> каже, що кроків цей прогін не записав;
/// <c>PUT …/trace-steps</c> → перерахунок → кроки записано, проблема веде до кроку, що впав, з його входами.
/// </summary>
/// <remarks>
/// ⛔ Мутації (локально): не записувати факт прогону (<c>RecordTraceSteps</c> в оркестраторі) — червоний (стан проблеми
/// <c>null</c> замість <c>NotRecorded</c>); брати поточний прапор замість факту прогону — червоний крок «прапор увімкнено
/// ПІСЛЯ прогону»; не віддавати входи в <c>calculation-steps</c> — червоний останній блок.
/// </remarks>
public sealed partial class ScriptMethodologyRunSqlTests
{
    private const string TracePassword = "Api-TraceSteps-E2E-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-565")]
    public async Task D565_PUT_прапора_перерахунок_кроки_записано_а_проблема_веде_до_кроку_що_впав()
    {
        var f = await ArrangeAsync(209904);
        await InsertValuesAsync(f, ("gas", 10m), ("oil", 7m), ("gas", 1m));
        var (methodologyId, _) = await PublishScriptAsync(
            f, "TR",
            $"const EF = {{ gas: 2 }};\nrows from {f.TableCode};\noutput Y: kg = EF[row.{f.C1}] * row.{f.C2};",
            [("Y", f.OutAColumnId)]);

        using var app = new EcrApiFactory(sql);
        using var client = await TraceUserAsync(app, f).ConfigureAwait(true);
        var doc = f.Document.DocumentId;
        var period = f.Document.PeriodKey.Value;
        var rowKey = await RowKeyAsync(f, 1);

        // 1. Прапор вимкнено (типово): кроків немає, проблема чесно каже «кроків не записано».
        await RunJobAsync(f.Document, sheetDefId: null);
        Assert.Equal(0, await StepCountAsync(f));
        var offIssue = await StepErrorIssueAsync(client, doc, period);
        Assert.Equal("NotRecorded", offIssue.GetProperty("traceSteps").GetString());
        Assert.Equal(methodologyId, offIssue.GetProperty("methodologyId").GetInt32());

        var offSteps = await GetJsonAsync(client, StepsUrl(doc, period, f.Document.TableInstanceId, rowKey));
        Assert.Equal(0, offSteps.GetProperty("steps").GetArrayLength());
        Assert.Contains(methodologyId, offSteps.GetProperty("notRecordedMethodologyIds").EnumerateArray().Select(e => e.GetInt32()));

        // 2. Прапор увімкнено ПІСЛЯ прогону: факт того прогону не змінюється — кроків він не записав.
        var put = await client.PutAsJsonAsync(
            new Uri($"/api/v1/methodologies/{methodologyId}/trace-steps", UriKind.Relative), new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("NotRecorded", (await StepErrorIssueAsync(client, doc, period)).GetProperty("traceSteps").GetString());

        // 3. Перерахунок: кроки записано, проблема веде до кроку, що впав, з його входами.
        await RunJobAsync(f.Document, sheetDefId: null);
        Assert.True(await StepCountAsync(f) > 0);
        Assert.Equal("Recorded", (await StepErrorIssueAsync(client, doc, period)).GetProperty("traceSteps").GetString());

        var steps = await GetJsonAsync(client, StepsUrl(doc, period, f.Document.TableInstanceId, rowKey));
        // Скрипт пише крок формули виходу (з виразом і входами) і крок самого виходу — обидва «впали» з #N/A.
        var failedSteps = steps.GetProperty("steps").EnumerateArray().Where(s => s.GetProperty("failed").GetBoolean()).ToList();
        Assert.NotEmpty(failedSteps);
        Assert.All(failedSteps, s => Assert.Equal(("Y", "#N/A"), (s.GetProperty("stepCode").GetString(), s.GetProperty("error").GetString())));
        var failed = Assert.Single(failedSteps, s => s.GetProperty("expression").ValueKind == JsonValueKind.String);
        Assert.Contains(
            failed.GetProperty("inputs").EnumerateArray(),
            i => i.GetProperty("code").GetString() == f.C1 && i.GetProperty("value").GetString() == "oil");
        Assert.Contains(methodologyId, steps.GetProperty("recordedMethodologyIds").EnumerateArray().Select(e => e.GetInt32()));
    }

    private static Uri StepsUrl(long documentId, int periodKey, long tableInstanceId, string rowKey)
        => new(
            $"/api/v1/documents/{documentId}/calculation-steps?periodKey={periodKey}&tableInstanceId={tableInstanceId}"
            + $"&rowKey={Uri.EscapeDataString(rowKey)}",
            UriKind.Relative);

    private static async Task<JsonElement> StepErrorIssueAsync(HttpClient client, long documentId, int periodKey)
    {
        var issues = await GetJsonAsync(
            client, new Uri($"/api/v1/documents/{documentId}/calculation-issues?periodKey={periodKey}", UriKind.Relative));
        return Assert.Single(issues.GetProperty("items").EnumerateArray(), i => i.GetProperty("kind").GetString() == "StepError");
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, Uri url)
    {
        var response = await client.GetAsync(url).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<int> StepCountAsync(Fixture f)
    {
        await using var db = f.Builder.CreateContext();
        var current = await db.CalculationRuns.AsNoTracking()
            .Where(r => r.DocumentId == f.Document.DocumentId && r.PeriodKey == f.Document.PeriodKey.Value
                        && r.Status == "Current")
            .Select(r => r.Id)
            .SingleAsync();
        return await db.CalculationSteps.CountAsync(s => s.PeriodKey == f.Document.PeriodKey.Value && s.CalculationRunId == current);
    }

    private async Task<string> RowKeyAsync(Fixture f, int rowIndex)
    {
        await using var db = f.Builder.CreateContext();
        var rowId = f.Document.RowIds[rowIndex];
        return await db.TableRows.AsNoTracking().Where(r => r.Id == rowId).Select(r => r.RowKey).SingleAsync();
    }

    private async Task<HttpClient> TraceUserAsync(EcrApiFactory app, Fixture f)
    {
        var name = $"trs_{Guid.NewGuid():N}"[..20];
        int userId;
        await using (var db = f.Builder.CreateContext())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(TracePassword));
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Trace steps e2e"));
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            foreach (var permission in new[] { "Document.View", "Calculation.View", "Calculation.EditFormula" })
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync();
        }

        await f.Builder.GrantProjectAsync(userId, f.Document.ProjectId, GrantLevel.Read);

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password = TracePassword });
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");
        return client;
    }
}
