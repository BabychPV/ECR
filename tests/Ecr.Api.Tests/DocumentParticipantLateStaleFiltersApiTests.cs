// tests/Ecr.Api.Tests/DocumentParticipantLateStaleFiltersApiTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// D-446 наскрізно через HTTP: документ, відкритий лише наданням учасника (D-443), під фільтрами <c>hasLateEdits</c> і
/// <c>resultsStale</c> та з позначками в рядку, картці й смузі зведення — у ВЛАСНИХ межах учасника.
/// </summary>
/// <remarks>
/// ⛔ Пізня правка рахується лише по тому, що учасник у документі бачить; застарілість результатів — по ВСЬОМУ документу,
/// тож для частково схованого документа вона не визначена (<c>null</c>, поза фільтром в обох напрямках і поза лічильником).
/// <para>Мутаційний доказ (локально, не в коміті):</para>
/// <list type="bullet">
/// <item>у <c>DocumentStore.LateEditDocumentIds</c> прибрати гілки <c>OPENJSON({3..5})</c> (пари «документ, Id») → червоніє
/// <c>Пізня_правка_рахується_лише_по_видимому_учасникові</c>: документ знаходиться під <c>hasLateEdits=true</c> і
/// позначений за правкою схованого аркуша (картка при цьому каже <c>false</c>);</item>
/// <item>повернути в <c>ListDocumentsHandler</c> умову <c>filter.HasLateEdits is null</c> для учасника (стара поведінка) →
/// той самий тест (документ зникає з обох напрямків фільтра) і тест цілком видимого документа (<c>resultsStale</c>);</item>
/// <item>у <c>DocumentStore.ListAsync</c> прибрати виключення <c>NarrowedDocumentIds</c> → червоніє
/// <c>Частково_схований_документ_поза_обома_напрямками_resultsStale</c> (документ під <c>resultsStale=false</c>);</item>
/// <item>у <c>DocumentListSummaryStore.StaleCountsAsync</c> прибрати гілку <c>WholeDocumentIds</c> → червоніє тест цілком
/// видимого документа (<c>staleResultsCount</c> = 0 при одному рядку під фільтром).</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentParticipantLateStaleFiltersApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Participant-Filters-2026!";
    private static readonly DateTime RunAt = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-446")]
    public async Task Пізня_правка_рахується_лише_по_видимому_учасникові()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        // Учасникові надано лише таблицю аркуша 1; аркуш 2 від нього схований.
        var participant = await NewParticipantAsync(s, "plt").ConfigureAwait(true);
        await GrantAsync(app, manager, s, s.SheetCode1, s.TableCode1, participant).ConfigureAwait(true);
        using var client = await SignedInAsync(app, participant.UserName).ConfigureAwait(true);

        // Пізня правка — лише в колонці схованого аркуша 2.
        await CellChangeAsync(s, s.Column2, participant.Id, isLate: true, at: RunAt).ConfigureAwait(true);

        // Контроль сценарію: менеджер (роль на проєкт) бачить пізню правку.
        using (var managerClient = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true))
        {
            Assert.Contains(s.DocumentId, Ids(await ListAsync(app, managerClient, s, "&hasLateEdits=true").ConfigureAwait(true)));
        }

        // ⛔ Учасник: не знаходиться під `true`, знаходиться під `false`, позначки немає ні в рядку, ні в картці.
        Assert.DoesNotContain(s.DocumentId, Ids(await ListAsync(app, client, s, "&hasLateEdits=true").ConfigureAwait(true)));
        Assert.Equal([s.DocumentId], Ids(await ListAsync(app, client, s, "&hasLateEdits=false").ConfigureAwait(true)));
        Assert.False(Row(await ListAsync(app, client, s, string.Empty).ConfigureAwait(true), s).GetProperty("hasLateEdits").GetBoolean());
        Assert.False((await CardAsync(app, client, s).ConfigureAwait(true)).GetProperty("hasLateEdits").GetBoolean());

        // Пізня правка у видимій колонці аркуша 1 — документ знаходиться під `true` і позначений (рядок = картка).
        await CellChangeAsync(s, s.Column1, participant.Id, isLate: true, at: RunAt).ConfigureAwait(true);

        Assert.Equal([s.DocumentId], Ids(await ListAsync(app, client, s, "&hasLateEdits=true").ConfigureAwait(true)));
        Assert.DoesNotContain(s.DocumentId, Ids(await ListAsync(app, client, s, "&hasLateEdits=false").ConfigureAwait(true)));
        Assert.True(Row(await ListAsync(app, client, s, string.Empty).ConfigureAwait(true), s).GetProperty("hasLateEdits").GetBoolean());
        Assert.True((await CardAsync(app, client, s).ConfigureAwait(true)).GetProperty("hasLateEdits").GetBoolean());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-446")]
    public async Task Цілком_видимий_документ_учасника_у_фільтрі_resultsStale_і_в_лічильнику()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        // Надання на ВЕСЬ документ: учасник бачить кожен аркуш.
        var participant = await NewParticipantAsync(s, "psw").ConfigureAwait(true);
        await GrantAsync(app, manager, s, null, null, participant).ConfigureAwait(true);
        await StaleAsync(s, participant.Id).ConfigureAwait(true);
        using var client = await SignedInAsync(app, participant.UserName).ConfigureAwait(true);

        var row = Row(await ListAsync(app, client, s, string.Empty).ConfigureAwait(true), s);
        Assert.Equal(JsonValueKind.True, Field(row, "resultsStale").ValueKind);

        // ✎ D-446: застарілість відома — причини «не визначено» немає ні в рядку, ні в картці.
        Assert.Equal(JsonValueKind.Null, Field(row, "resultsStaleUndetermined").ValueKind);
        var card = await CardAsync(app, client, s).ConfigureAwait(true);
        Assert.Equal(JsonValueKind.True, Field(card, "resultsStale").ValueKind);
        Assert.Equal(JsonValueKind.Null, Field(card, "resultsStaleUndetermined").ValueKind);

        Assert.Equal([s.DocumentId], Ids(await ListAsync(app, client, s, "&resultsStale=true").ConfigureAwait(true)));
        Assert.Empty(await ListAsync(app, client, s, "&resultsStale=false").ConfigureAwait(true));
        Assert.Equal([s.DocumentId], Ids(await ListAsync(app, client, s, "&resultsStale=true&staleBy=me").ConfigureAwait(true)));

        // Смуга зведення не розходиться з таблицею під фільтром.
        var summary = await SummaryAsync(app, client, s).ConfigureAwait(true);
        Assert.Equal(1, summary.GetProperty("staleResultsCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("staleResultsMineCount").GetInt32());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-446")]
    public async Task Частково_схований_документ_поза_обома_напрямками_resultsStale()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        var participant = await NewParticipantAsync(s, "psp").ConfigureAwait(true);
        await GrantAsync(app, manager, s, s.SheetCode1, s.TableCode1, participant).ConfigureAwait(true);
        await StaleAsync(s, participant.Id).ConfigureAwait(true);
        using var client = await SignedInAsync(app, participant.UserName).ConfigureAwait(true);

        // Контроль сценарію: менеджер бачить застарілі результати.
        using (var managerClient = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true))
        {
            Assert.Contains(s.DocumentId, Ids(await ListAsync(app, managerClient, s, "&resultsStale=true").ConfigureAwait(true)));
        }

        // ⛔ Учасник: документ у переліку є, але застарілість не визначена — `null`, і в жодному напрямку фільтра.
        var row = Row(await ListAsync(app, client, s, string.Empty).ConfigureAwait(true), s);
        Assert.Equal(JsonValueKind.Null, Field(row, "resultsStale").ValueKind);
        Assert.Equal(JsonValueKind.Null, Field(row, "resultsStaleSince").ValueKind);
        var card = await CardAsync(app, client, s).ConfigureAwait(true);
        Assert.Equal(JsonValueKind.Null, Field(card, "resultsStale").ValueKind);

        // ✎ D-446: той самий null пояснено причиною — частину документа учасникові не відкрито (рядок = картка).
        Assert.Equal("PartiallyHidden", Field(row, "resultsStaleUndetermined").GetString());
        Assert.Equal("PartiallyHidden", Field(card, "resultsStaleUndetermined").GetString());

        Assert.Empty(await ListAsync(app, client, s, "&resultsStale=true").ConfigureAwait(true));
        Assert.Empty(await ListAsync(app, client, s, "&resultsStale=false").ConfigureAwait(true));
        Assert.Empty(await ListAsync(app, client, s, "&resultsStale=true&staleBy=me").ConfigureAwait(true));

        var summary = await SummaryAsync(app, client, s).ConfigureAwait(true);
        Assert.Equal(0, summary.GetProperty("staleResultsCount").GetInt32());
        Assert.Equal(0, summary.GetProperty("staleResultsMineCount").GetInt32());
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static List<long> Ids(List<JsonElement> items) => [.. items.Select(d => d.GetProperty("id").GetInt64())];

    private static JsonElement Row(List<JsonElement> items, Scenario s) => items.Single(d => d.GetProperty("id").GetInt64() == s.DocumentId);

    /// <summary>Поле відповіді; відсутнє розглядається як <c>null</c> (серіалізатор може пропускати null).</summary>
    private static JsonElement Field(JsonElement doc, string name)
        => doc.TryGetProperty(name, out var value) ? value : JsonDocument.Parse("null").RootElement;

    private static async Task<List<JsonElement>> ListAsync(EcrApiFactory app, HttpClient client, Scenario s, string extra)
    {
        var list = await JsonAsync(
            await client.GetAsync(new Uri($"/api/v1/documents?limit=200&periodKey={s.PeriodKey}{extra}", UriKind.Relative)).ConfigureAwait(false),
            HttpStatusCode.OK, app).ConfigureAwait(false);
        return [.. list.GetProperty("items").EnumerateArray().Select(e => e.Clone())];
    }

    private static async Task<JsonElement> CardAsync(EcrApiFactory app, HttpClient client, Scenario s)
        => await JsonAsync(
            await client.GetAsync(new Uri($"/api/v1/documents/{Num(s.DocumentId)}?periodKey={s.PeriodKey}", UriKind.Relative)).ConfigureAwait(false),
            HttpStatusCode.OK, app).ConfigureAwait(false);

    private static async Task<JsonElement> SummaryAsync(EcrApiFactory app, HttpClient client, Scenario s)
        => await JsonAsync(
            await client.GetAsync(new Uri($"/api/v1/documents/summary?periodKey={s.PeriodKey}", UriKind.Relative)).ConfigureAwait(false),
            HttpStatusCode.OK, app).ConfigureAwait(false);

    private static async Task GrantAsync(
        EcrApiFactory app, HttpClient manager, Scenario s, string? sheet, string? table, Person recipient)
    {
        using var response = await manager.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{Num(s.DocumentId)}/edit-assignments", UriKind.Relative),
            new
            {
                slice = new { sheetCode = sheet, tableCode = table },
                recipientUserIds = new[] { recipient.Id },
                isExclusive = false,
                level = "Read",
            }).ConfigureAwait(false);
        await JsonAsync(response, HttpStatusCode.OK, app).ConfigureAwait(false);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status, EcrApiFactory app)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    /// <summary>Правка комірки документа в журналі (<c>aud.CellChange</c> поза моделлю EF — сирим SQL).</summary>
    private async Task CellChangeAsync(Scenario s, int columnDefId, int userId, bool isLate, DateTime at)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO aud.CellChange
                (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId,
                 OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit)
            VALUES (@at, @period, @document, 1, N'R1', @column, N'1', N'2', @user, N'UserEdit', @late);
            """;
        command.Parameters.AddWithValue("@at", at);
        command.Parameters.AddWithValue("@period", s.PeriodKey);
        command.Parameters.AddWithValue("@document", s.DocumentId);
        command.Parameters.AddWithValue("@column", columnDefId);
        command.Parameters.AddWithValue("@user", userId);
        command.Parameters.AddWithValue("@late", isLate);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>Прогін методології з числом документа, потім правка входу (видимої колонки) — результати застаріли.</summary>
    private async Task StaleAsync(Scenario s, int editorUserId)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        var methodology = new Methodology(EcrCode.Create($"PSM_{tag}"), Text("m"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, RunAt);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var run = new CalculationRun(s.ProjectId, s.PeriodKey, triggeredByUserId: null, RunAt);
        run.Complete("Succeeded", RunAt.AddMinutes(1), "{}", errorMessage: null);
        run.MakeCurrent();
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync().ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO calc.CalculationResult
                (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId)
            VALUES (NEXT VALUE FOR calc.CalculationResultSeq, {run.Id}, {version.Id},
                    {s.PeriodKey}, {s.DocumentId}, N'row-1', N'E_CO2', CAST(1 AS decimal(34,16)), {unitId})
            """).ConfigureAwait(false);

        await CellChangeAsync(s, s.Column1, editorUserId, isLate: false, at: RunAt.AddHours(1)).ConfigureAwait(false);
    }

    /// <summary>Учасник без жодного гранта на проєкт: власна роль без прав.</summary>
    private async Task<Person> NewParticipantAsync(Scenario s, string prefix)
        => await NewUserAsync(s.ProjectId, prefix, level: null, withView: false).ConfigureAwait(false);

    private async Task<Person> NewUserAsync(int projectId, string prefix, GrantLevel? level, bool withView)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var userName = $"{prefix}_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"DF_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Participant filters test" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        if (withView)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        }

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        if (level is { } l)
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, l));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        return new Person(user.Id, userName);
    }

    /// <summary>Документ із ДВОМА аркушами у складі (по таблиці, колонці й екземпляру за відкритий період) і менеджер (Manage).</summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(columnCount: 2, rowCount: 1).ConfigureAwait(false);
        await builder.OpenPeriodAsync(document).ConfigureAwait(false);

        await using var db = builder.CreateContext();
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        var sheet2 = new SheetDef(document.TemplateVersionId, EcrCode.Create($"SHF{tag}"), Text("Sheet F"), 2);
        db.SheetDefs.Add(sheet2);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var table2 = new TableDef(sheet2.Id, EcrCode.Create($"TBF{tag}"), Text("Table F"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table2);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var column2 = new ColumnDef(table2.Id, EcrCode.Create($"CF{tag}"), Text("Col F"), 1, CellDataType.Decimal);
        db.ColumnDefs.Add(column2);
        db.RowDefs.Add(new RowDef(table2.Id, RowKey.Create($"RF{tag}"), 1, Text("Row F"), RowKind.Item));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instance2 = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None).ConfigureAwait(false);
        var row2 = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None).ConfigureAwait(false);
        db.TableInstances.Add(new TableInstance(document.PeriodKey, instance2, document.DocumentId, table2.Id, now));
        db.TableRows.Add(new TableRow(document.PeriodKey, row2, instance2, RowKey.Create($"RF{tag}"), 1, now));
        db.DocumentSheets.Add(new DocumentSheet(document.DocumentId, document.SheetDefId));
        db.DocumentSheets.Add(new DocumentSheet(document.DocumentId, sheet2.Id));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var tableCode1 = await db.TableDefs.Where(t => t.Id == document.TableDefId).Select(t => t.Code).SingleAsync().ConfigureAwait(false);

        var manager = await NewUserAsync(document.ProjectId, "pfm", GrantLevel.Manage, withView: true).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE doc.Document SET CreatedByUserId = {manager.Id} WHERE Id = {document.DocumentId}").ConfigureAwait(false);

        return new Scenario(
            document.DocumentId, document.ProjectId, document.PeriodKey.Value,
            document.SheetCode, tableCode1, document.ColumnDefIds[0], column2.Id, manager);
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record Person(int Id, string UserName);

    private sealed record Scenario(
        long DocumentId, int ProjectId, int PeriodKey, string SheetCode1, string TableCode1, int Column1, int Column2,
        Person Manager);
}
