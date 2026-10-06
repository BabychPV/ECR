// tests/Ecr.Api.Tests/Security/DenyBoundaryD276Tests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// Межа <c>D-276</c> наскрізно (HTTP, справжній SQL): <c>Deny</c> на колонку чи таблицю ДЖЕРЕЛА закриває саме
/// джерело (зріз таблиці, експорт), але НЕ ховає (а) Rollup-агрегат у видимій таблиці-приймачі і (б) вміст
/// регуляторного зрізу (<c>Report.ViewSnapshot</c> + грант на проєкт). Тест фіксує ПОТОЧНУ поведінку: будь-яка
/// випадкова зміна межі в будь-який бік червоніє.
/// </summary>
/// <remarks>
/// Заборонний бік (клітинки, експорт, validate/validation, Check із забороненим джерелом T1-01) детально
/// тримають <see cref="DenyReadTests"/> і <see cref="CheckSourceDenyValidationTests"/>; тут — лише контраст на тих
/// самих даних, щоб виняток не можна було «виправити» мовчки.
/// <para>
/// ⛔ МУТАЦІЙНІ ДОКАЗИ (локально, не в коміті):
/// (1) у <c>DocumentReadScope.CanReadColumn</c> ховати колонки основної таблиці (приймача), щойно в профілі є будь-яка
/// заборона, — червоніє «агрегат Rollup зник із приймача»; (2) у <c>GetSnapshotRowsHandler</c> відповідати 404, щойно
/// в профілі є будь-яка заборона, — червоніє регуляторна частина; (3) <c>EditRules.CanReadIn</c> повертає <c>true</c> —
/// червоніє контраст «джерело закрите».
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class DenyBoundaryD276Tests(SqlServerFixture sql)
{
    private const string Password = "Api-Deny-Boundary-D276!";

    // Значення, які не можуть випадково збігтися ні з чим у відповіді.
    private const decimal SourceSecret = 246813.5m;
    private const decimal RollupAggregate = 975311.5m;
    private const decimal ReportValue = 864209.5m;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    [Trait("Requirement", "ФВ-2.12")]
    [Trait("Decision", "D-276")]
    public async Task Rollup_агрегат_у_видимому_приймачі_видно_при_Deny_на_джерело_а_саме_джерело_закрите()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);

        foreach (var (key, user) in s.Users)
        {
            using var client = await SignedInAsync(app, user).ConfigureAwait(true);

            // (а) D-276: агрегат у видимій таблиці-приймачі — як звичайна формула, для ВСІХ трьох.
            var (targetStatus, target) = await GetAsync(client, Slice(s, s.Doc.TableInstanceId)).ConfigureAwait(true);
            Assert.True(targetStatus == HttpStatusCode.OK, $"{key}: приймач {targetStatus}: {target}\n{app.ErrorsText}");
            Assert.True(target.Contains(Digits(RollupAggregate), StringComparison.Ordinal), $"{key}: агрегат Rollup зник із приймача: {target}");
            Assert.Contains(s.TargetColumnCode, ColumnCodes(target));

            // Контраст: значення джерела в приймачі не з'являється (Rollup не розкриває джерело).
            Assert.DoesNotContain(Digits(SourceSecret), target, StringComparison.Ordinal);

            // Сама заборона на джерело діє.
            var (sourceStatus, source) = await GetAsync(client, Slice(s, s.Source.TableInstanceId)).ConfigureAwait(true);
            switch (key)
            {
                case "plain":
                    Assert.True(sourceStatus == HttpStatusCode.OK, $"{key}: джерело {sourceStatus}: {source}");
                    Assert.Contains(Digits(SourceSecret), source, StringComparison.Ordinal);
                    break;
                case "column":
                    Assert.True(sourceStatus == HttpStatusCode.OK, $"{key}: джерело {sourceStatus}: {source}");
                    Assert.DoesNotContain(Digits(SourceSecret), source, StringComparison.Ordinal);
                    Assert.DoesNotContain(s.SourceColumnCode, ColumnCodes(source));
                    break;
                default:
                    Assert.True(sourceStatus == HttpStatusCode.NotFound, $"{key}: джерело {sourceStatus}: {source}");
                    Assert.DoesNotContain(Digits(SourceSecret), source, StringComparison.Ordinal);
                    break;
            }

            // Експорт: агрегат є у всіх, значення джерела — лише без заборони.
            var export = await ExportJsonAsync(app, client, s).ConfigureAwait(true);
            Assert.True(export.Contains(Digits(RollupAggregate), StringComparison.Ordinal), $"{key}: агрегат Rollup зник з експорту");
            Assert.Equal(key == "plain", export.Contains(Digits(SourceSecret), StringComparison.Ordinal));
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    [Trait("Decision", "D-276")]
    public async Task Регуляторний_зріз_з_правом_на_вміст_повний_попри_Deny_на_колонку_й_таблицю()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var snapshotId = await SnapshotAsync(s).ConfigureAwait(true);
        await GrantReportViewerAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);

        foreach (var (key, user) in s.Users)
        {
            using var client = await SignedInAsync(app, user).ConfigureAwait(true);

            // (б) D-276: рядки зрізу — повністю, однаково для всіх трьох.
            var (rowsStatus, rows) = await GetAsync(client, $"/api/v1/reports/snapshots/{snapshotId}/rows?limit=10")
                .ConfigureAwait(true);
            Assert.True(rowsStatus == HttpStatusCode.OK, $"{key}: рядки {rowsStatus}: {rows}\n{app.ErrorsText}");
            var row = Assert.Single(JsonDocument.Parse(rows).RootElement.GetProperty("rows").EnumerateArray());
            Assert.Equal(s.SourceColumnCode, row.GetProperty("cells").GetProperty("OutputCode").GetString());
            Assert.True(rows.Contains(Digits(ReportValue), StringComparison.Ordinal), $"{key}: значення зникло з рядків зрізу: {rows}");

            // Книга зрізу — так само.
            using var book = await client
                .GetAsync(new Uri($"/api/v1/reports/snapshots/{snapshotId}/export.xlsx", UriKind.Relative))
                .ConfigureAwait(true);
            Assert.True(book.StatusCode == HttpStatusCode.OK, $"{key}: книга {book.StatusCode}\n{app.ErrorsText}");
            var text = ZipText(await book.Content.ReadAsByteArrayAsync().ConfigureAwait(true));
            Assert.True(text.Contains(Digits(ReportValue), StringComparison.Ordinal), $"{key}: значення зникло з книги зрізу");
            Assert.Contains(s.SourceColumnCode, text, StringComparison.Ordinal);
        }
    }

    // ────────────────────────────── збірка ────────────────────────────

    /// <summary>
    /// Документ: основна таблиця — ПРИЙМАЧ Rollup (агрегат у третій колонці, <c>IsCalculated</c>), друга таблиця на
    /// новому аркуші — ДЖЕРЕЛО. Активний зв'язок Rollup джерело → приймач. Три користувачі з <c>Read</c> на проєкт:
    /// <c>plain</c> без заборон, <c>column</c> — <c>Deny</c> на колонку джерела, <c>table</c> — <c>Deny</c> на
    /// таблицю джерела.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 3, rowCount: 1).ConfigureAwait(false);
        var source = (await MultiTableDocument.AddTablesAsync(builder, doc, [1], columnCount: 1, rowCount: 1).ConfigureAwait(false))[0];

        await using var db = builder.CreateContext();
        db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));

        var targetColumn = doc.ColumnDefIds[2];
        var targetCode = await db.ColumnDefs.Where(c => c.Id == targetColumn).Select(c => c.Code).SingleAsync().ConfigureAwait(false);
        var sourceColumn = source.ColumnDefIds[0];

        db.CellValues.Add(new CellValue(
            new CellAddress(doc.PeriodKey, source.RowIds[0], sourceColumn), source.TableDefId,
            new CellValueData { ValueNumeric = SourceSecret }));
        db.CellValues.Add(new CellValue(
            new CellAddress(doc.PeriodKey, doc.RowIds[0], targetColumn), doc.TableDefId,
            new CellValueData { ValueNumeric = RollupAggregate, IsCalculated = true }));

        var relation = new TableRelationDef(
            EcrCode.Create($"RL{Guid.NewGuid():N}"[..12].ToUpperInvariant()), source.TableDefId, doc.TableDefId,
            TableRelationKind.Rollup, "{}");
        relation.Update(
            source.TableDefId, doc.TableDefId, TableRelationKind.Rollup, "{}",
            $$"""{"sourceColumn":"{{source.ColumnCodes[0]}}","targetColumn":"{{targetCode}}","aggregate":"sum"}""", 0, true);
        db.TableRelations.Add(relation);

        var viewer = NewRole("D276_V");
        db.Roles.Add(viewer);
        var users = new Dictionary<string, User>
        {
            ["plain"] = NewUser("d6p"), ["column"] = NewUser("d6c"), ["table"] = NewUser("d6t"),
        };
        db.Users.AddRange(users.Values);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(viewer.Id, "Document.View"));
        db.RolePermissions.Add(new RolePermission(viewer.Id, "Document.Export"));
        db.ResourceGrants.Add(new ResourceGrant(viewer.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Read));
        foreach (var user in users.Values)
        {
            db.RoleAssignments.Add(new RoleAssignment(viewer.Id, user.Id, null));
        }

        foreach (var (key, kind, id) in new[] { ("column", ResourceKind.Column, sourceColumn), ("table", ResourceKind.Table, source.TableDefId) })
        {
            var denier = NewRole("D276_D");
            db.Roles.Add(denier);
            await db.SaveChangesAsync().ConfigureAwait(false);
            db.RoleAssignments.Add(new RoleAssignment(denier.Id, users[key].Id, null));
            db.ResourceGrants.Add(new ResourceGrant(denier.Id, kind, id, GrantLevel.Read, isDeny: true));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(
            doc, source, source.ColumnCodes[0], targetCode,
            users.ToDictionary(u => u.Key, u => u.Value.UserName),
            [.. users.Values.Select(u => u.Id)]);
    }

    /// <summary>Регуляторний зріз проєкту документа: рядок несе код і значення ЗАБОРОНЕНОЇ колонки джерела.</summary>
    private async Task<long> SnapshotAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var version = await db.ReportVersions.AsNoTracking().OrderBy(v => v.Id).FirstAsync().ConfigureAwait(false);
        var snapshot = new ReportSnapshot(
            version.Id, s.Doc.ProjectId, s.Doc.PeriodKey.Value, SnapshotStatus.Draft, DateTime.UtcNow, null);
        db.ReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var code = new ReportRow(snapshot.Id, 1, "OutputCode");
        code.SetValue(s.SourceColumnCode, null, null);
        var value = new ReportRow(snapshot.Id, 1, "Value");
        value.SetValue(null, ReportValue, null);
        db.ReportRows.AddRange(code, value);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return snapshot.Id;
    }

    /// <summary>Вбудована роль «Переглядач звітів» (<c>Report.ViewRegulatory</c> + <c>Report.ViewSnapshot</c> + книга) усім трьом.</summary>
    private async Task GrantReportViewerAsync(Scenario s)
    {
        int roleId;
        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id FROM sec.Role WHERE Code = N'ReportViewer' AND IsBuiltIn = 1";
            roleId = (int)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
        }

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        foreach (var userId in s.UserIds)
        {
            db.RoleAssignments.Add(new RoleAssignment(roleId, userId, null));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    // ────────────────────────────── HTTP ────────────────────────────

    /// <summary>Вивантаження документа в json — справжня задача в черзі, справжній файл.</summary>
    private static async Task<string> ExportJsonAsync(EcrApiFactory app, HttpClient client, Scenario s)
    {
        var start = await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId}/export", UriKind.Relative),
            new { includeFormulas = false, includeStyles = false, language = "en", periodKey = s.Doc.PeriodKey.Value, format = "json" })
            .ConfigureAwait(false);
        Assert.True(start.StatusCode == HttpStatusCode.Accepted, $"експорт: {start.StatusCode}: {app.ErrorsText}");
        var jobId = (await start.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false))
            .GetProperty("jobId").GetString()!;

        JsonElement job = default;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            job = await client.GetFromJsonAsync<JsonElement>(
                new Uri($"/api/v1/jobs/{Uri.EscapeDataString(jobId)}", UriKind.Relative)).ConfigureAwait(false);
            if (job.GetProperty("state").GetString() is not ("Queued" or "Running"))
            {
                break;
            }

            await Task.Delay(200).ConfigureAwait(false);
        }

        Assert.True(job.GetProperty("state").GetString() == "Succeeded", $"задача: {job.GetRawText()}\n{app.ErrorsText}");

        using var download = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Doc.DocumentId}/export/{job.GetProperty("message").GetString()}", UriKind.Relative))
            .ConfigureAwait(false);
        Assert.True(download.StatusCode == HttpStatusCode.OK, $"файл: {download.StatusCode}: {app.ErrorsText}");
        return await download.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    private static string ZipText(byte[] bytes)
    {
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));
        var all = new System.Text.StringBuilder();
        foreach (var entry in zip.Entries)
        {
            using var stream = new StreamReader(entry.Open());
            all.Append(entry.FullName).Append('\n').Append(stream.ReadToEnd());
        }

        return all.ToString();
    }

    private static List<string?> ColumnCodes(string slice)
        => [.. JsonDocument.Parse(slice).RootElement.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("code").GetString())];

    private static string Slice(Scenario s, long instance)
        => $"/api/v1/documents/{s.Doc.DocumentId}/tables/{instance}";

    /// <summary>Цифри значення без дробової частини — так воно видно в будь-якому поданні.</summary>
    private static string Digits(decimal value)
        => decimal.Truncate(value).ToString(CultureInfo.InvariantCulture);

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(false);
        return (response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
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

    private static User NewUser(string prefix)
    {
        var name = $"{prefix}_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        return user;
    }

    private static Role NewRole(string prefix)
        => new(
            EcrCode.Create($"{prefix}_{Guid.NewGuid():N}"[..24]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = prefix }));

    private sealed record Scenario(
        TestDocument Doc,
        ExtraTable Source,
        string SourceColumnCode,
        string TargetColumnCode,
        IReadOnlyDictionary<string, string> Users,
        IReadOnlyList<int> UserIds);
}
