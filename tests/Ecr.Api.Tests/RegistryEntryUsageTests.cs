// tests/Ecr.Api.Tests/RegistryEntryUsageTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>GET /api/v1/registries/{code}/entries/{entryId}/usage</c> — документи, у комірках чи шапці яких стоїть
/// ОДИН запис довідника (ФВ-8.14).
/// </summary>
/// <remarks>
/// Пастки: два значення запису в одному документі (рядок на документ, а не на комірку), значення в логічно
/// видаленому рядку, документ проєкту без <c>Document.View</c> у читача, запис ЧУЖОГО довідника під кодом цього.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryEntryUsageTests(SqlServerFixture sql)
{
    private const string Password = "Api-Entry-Usage-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.14")]
    public async Task Документи_з_записом_у_комірках_і_шапці_по_одному_лише_видимі_без_видалених_рядків()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.Reader).ConfigureAwait(true);

        var usage = await GetAsync(client, app, $"/api/v1/registries/{s.Code}/entries/{s.Used}/usage").ConfigureAwait(true);

        // Літералом: документ із двома комірками + документ із шапкою. Прихований проєкт і видалений рядок — ні.
        Assert.Equal(2, usage.GetProperty("total").GetInt32());
        var items = usage.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([s.CellsDocument, s.HeaderDocument], items.Select(i => long.Parse(i.GetProperty("id").GetString()!, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.All(items, i => Assert.Equal("document", i.GetProperty("kind").GetString()));
        Assert.Equal(s.CellsDocumentKey, items[0].GetProperty("label").GetString());
        Assert.Equal($"/documents/{s.CellsDocument}", items[0].GetProperty("route").GetString());

        var unused = await GetAsync(client, app, $"/api/v1/registries/{s.Code}/entries/{s.Unused}/usage").ConfigureAwait(true);
        Assert.Equal(0, unused.GetProperty("total").GetInt32());
        Assert.Empty(unused.GetProperty("items").EnumerateArray());

        // ⛔ Запис іншого довідника під кодом цього — 404, а не «нуль документів».
        var foreign = await client.GetAsync(
            new Uri($"/api/v1/registries/{s.Code}/entries/{s.Foreign}/usage", UriKind.Relative)).ConfigureAwait(true);
        var body = await foreign.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(foreign.StatusCode == HttpStatusCode.NotFound, $"{foreign.StatusCode}: {body}");
        Assert.Equal("ECR-REG-0404", JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.14")]
    public async Task Без_права_на_опис_довідника_403()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.DocumentsOnly).ConfigureAwait(true);

        var response = await client.GetAsync(
            new Uri($"/api/v1/registries/{s.Code}/entries/{s.Used}/usage", UriKind.Relative)).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// ⛔ F13 p227 (P2): на копії Land запис HSE401_REGION/7525 (6270 комірок) відповідав 13–15 с, під перерахунком — 503.
    /// Причина — форма запиту: <c>Documents WHERE ProjectId IN … AND (Id IN (SELECT … CellValue …) OR Id IN (SELECT …
    /// шапка))</c> — корельований IN з OR над з'єднанням у мільйони рядків, і так двічі (лічильник + сторінка).
    /// Храповик: важкий прохід по комірках — РІВНО один, без OR перед підзапитом; сторінка — окремим дешевим запитом.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.14")]
    public async Task Комірки_проходяться_одним_запитом_без_корельованого_OR_IN_а_Total_повний_при_обрізаній_сторінці()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var recorder = new SqlTextRecorder();
        await using var db = sql.CreateContext(recorder);

        var usage = await new Ecr.Infrastructure.Persistence.RegistryStore(db)
            .FindEntryDocumentsAsync(s.Used, s.VisibleProjects, take: 1, CancellationToken.None).ConfigureAwait(true);

        // Семантика: лічильник повний (2), сторінка обрізана до 1 і впорядкована за Id.
        Assert.Equal(2, usage.Total);
        Assert.Equal([s.CellsDocument.ToString(System.Globalization.CultureInfo.InvariantCulture)], usage.Items.Select(i => i.Id));

        var commands = recorder.Commands.ToList();
        var dump = string.Join("\n----\n", commands);
        Assert.True(commands.Count <= 2, $"Звернень {commands.Count} (стеля 2):\n{dump}");
        Assert.True(
            commands.Count(c => c.Contains("[CellValue]", StringComparison.Ordinal)) == 1,
            $"Прохід по doc.CellValue має бути рівно один:\n{dump}");
        Assert.DoesNotMatch(
            new System.Text.RegularExpressions.Regex(@"\bOR\b\s+(?:\w+\s+)?(?:\[[^\]]+\]\.\[[^\]]+\]\s+)?(?:IN|EXISTS)\s*\(\s*SELECT", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            dump);
    }

    private sealed class SqlTextRecorder : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Commands { get; } = new();

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Enqueue(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<object>> ScalarExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Enqueue(command.CommandText);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, EcrApiFactory app, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {url}: {response.StatusCode}: {body}\n{app.ErrorsText}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>
    /// Чотири документи в чотирьох проєктах: комірки (два рядки) із записом; шапка із записом; запис лише у
    /// видаленому рядку; запис у документі проєкту, якого читач не бачить. Плюс невикористаний і чужий записи.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var cells = await builder.BuildAsync(columnCount: 1, rowCount: 2).ConfigureAwait(false);
        var header = await builder.BuildAsync(columnCount: 1, rowCount: 1).ConfigureAwait(false);
        var deleted = await builder.BuildAsync(columnCount: 1, rowCount: 1).ConfigureAwait(false);
        var hidden = await builder.BuildAsync(columnCount: 1, rowCount: 1).ConfigureAwait(false);

        await using var db = builder.CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var now = DateTime.UtcNow;

        var subject = new RegistryDef(EcrCode.Create($"REU{tag}"), Text("Entry usage"), isTemporal: false);
        var other = new RegistryDef(EcrCode.Create($"REO{tag}"), Text("Other"), isTemporal: false);
        db.RegistryDefs.AddRange(subject, other);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var used = new RegistryEntry(subject.Id, EcrCode.Create($"USED{tag}"), Text("Used"), 1, now);
        var unused = new RegistryEntry(subject.Id, EcrCode.Create($"FREE{tag}"), Text("Free"), 1, now);
        var foreign = new RegistryEntry(other.Id, EcrCode.Create($"FRGN{tag}"), Text("Foreign"), 1, now);
        db.RegistryEntries.AddRange(used, unused, foreign);
        await db.SaveChangesAsync().ConfigureAwait(false);

        ColumnDef Column(TestDocument document)
        {
            var column = new ColumnDef(document.TableDefId, EcrCode.Create($"LK{tag}"), Text("Lookup"), 2, CellDataType.Lookup);
            column.SetLookup(subject.Id);
            db.ColumnDefs.Add(column);
            return column;
        }

        var cellsColumn = Column(cells);
        var deletedColumn = Column(deleted);
        var hiddenColumn = Column(hidden);
        var headerField = new HeaderFieldDef(header.TemplateVersionId, EcrCode.Create("ENTRY"), Text("Entry"), 1, CellDataType.Lookup);
        headerField.SetLookup(subject.Id);
        db.HeaderFieldDefs.Add(headerField);
        await db.SaveChangesAsync().ConfigureAwait(false);

        void Cell(TestDocument document, ColumnDef column, int row)
            => db.CellValues.Add(new CellValue(
                new CellAddress(document.PeriodKey, document.RowIds[row], column.Id), document.TableDefId,
                new CellValueData { ValueRegistryEntryId = used.Id }));

        Cell(cells, cellsColumn, 0);
        Cell(cells, cellsColumn, 1);
        Cell(deleted, deletedColumn, 0);
        Cell(hidden, hiddenColumn, 0);
        db.DocumentHeaderValues.Add(new DocumentHeaderValue(
            header.DocumentId, headerField.Id, new DocumentHeaderValueData { ValueRegistryEntryId = used.Id }));
        var deletedRow = await db.TableRows
            .SingleAsync(r => r.PeriodKeyValue == deleted.PeriodKey.Value && r.Id == deleted.RowIds[0]).ConfigureAwait(false);
        deletedRow.SoftDelete(now);

        var reader = NewUser(db, "reu_r", out var readerName);
        var documentsOnly = NewUser(db, "reu_d", out var documentsOnlyName);
        var role = new Role(EcrCode.Create($"REU_{Guid.NewGuid():N}"[..20]), Text("Entry usage"));
        var docRole = new Role(EcrCode.Create($"RED_{Guid.NewGuid():N}"[..20]), Text("Documents only"));
        db.Roles.AddRange(role, docRole);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.AddRange(
            new RolePermission(role.Id, "Registry.EditDefinition"), new RolePermission(role.Id, "Document.View"),
            new RolePermission(docRole.Id, "Document.View"));
        db.RoleAssignments.AddRange(new RoleAssignment(role.Id, reader.Id, null), new RoleAssignment(docRole.Id, documentsOnly.Id, null));
        foreach (var visible in new[] { cells, header, deleted })
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, visible.ProjectId, GrantLevel.Read));
            db.ResourceGrants.Add(new ResourceGrant(docRole.Id, ResourceKind.Project, visible.ProjectId, GrantLevel.Read));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        var cellsKey = await db.Documents.AsNoTracking()
            .Where(d => d.Id == cells.DocumentId).Select(d => d.BusinessKey).SingleAsync().ConfigureAwait(false);

        return new Scenario(
            subject.Code, used.Id, unused.Id, foreign.Id, cells.DocumentId, cellsKey, header.DocumentId,
            readerName, documentsOnlyName, [cells.ProjectId, header.ProjectId, deleted.ProjectId]);
    }

    private static User NewUser(Ecr.Infrastructure.Persistence.EcrDbContext db, string prefix, out string name)
    {
        name = $"{prefix}_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        return user;
    }

    private sealed record Scenario(
        string Code, long Used, long Unused, long Foreign, long CellsDocument, string CellsDocumentKey,
        long HeaderDocument, string Reader, string DocumentsOnly, IReadOnlyCollection<int> VisibleProjects);
}
