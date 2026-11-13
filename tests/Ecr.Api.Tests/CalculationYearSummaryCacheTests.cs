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
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// CRY03: кеш числової частини зведення року (<see cref="CalculationYearResultsCache"/>) на справжньому HTTP-шляху.
/// Кожен тест спершу ДОВОДИТЬ, що кеш живий: після теплого виклику в актуальний прогін дописується «зонд» — число
/// ще одного рядка, якого справжній прогін ніколи не дописує, — і зведення його НЕ бачить. Далі — що саме міняє ключ:
/// новий актуальний прогін, зміна екземплярів документа, інший документ; і що читач із Deny не отримує чужого теплого
/// запису.
/// </summary>
/// <remarks>
/// «Never mutate access/security filters, even temporarily; prove access with differential Deny tests on the real path.»
/// Мутації (локально, CRY03; кожна червонить свій тест): прибрати прогони з ключа — «новий прогін»; прибрати відбиток
/// екземплярів — «екземпляри»; прибрати документ і Id екземплярів з ключа — «два документи». Deny-тест доведено
/// червоним даними, а не кодом доступу: без Deny-гранту читач бачить число (контроль у самому тесті).
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationYearSummaryCacheTests(SqlServerFixture sql)
{
    private const string Password = "Calc-Year-Cache-R!42";
    private static readonly DateTime Now = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Permissions = ["Document.View", "Calculation.View"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "CALC-RESULTS")]
    public async Task Кеш_зведення_новий_актуальний_прогін_дає_свіжі_числа()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        Assert.Equal(1, await TotalRowsAsync(app, client, s.DocumentId).ConfigureAwait(true));
        await ProbeIsCachedAsync(app, client, s).ConfigureAwait(true);

        // Новий актуальний прогін (старий витіснено) з числами обох рядків.
        await using (var db = Context())
        {
            var old = await db.CalculationRuns.SingleAsync(r => r.Id == s.RunId).ConfigureAwait(true);
            old.Supersede();
            await db.SaveChangesAsync().ConfigureAwait(true);
            var next = CurrentRun(s.ProjectId);
            db.CalculationRuns.Add(next);
            await db.SaveChangesAsync().ConfigureAwait(true);
            await InsertResultAsync(db, next.Id, s, s.RowKeys[0], s.Document.TableInstanceId, s.DocumentId).ConfigureAwait(true);
            await InsertResultAsync(db, next.Id, s, s.RowKeys[1], s.Document.TableInstanceId, s.DocumentId).ConfigureAwait(true);
        }

        Assert.Equal(2, await TotalRowsAsync(app, client, s.DocumentId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "CALC-RESULTS")]
    public async Task Кеш_зведення_зміна_екземплярів_документа_дає_свіжі_числа()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        Assert.Equal(1, await TotalRowsAsync(app, client, s.DocumentId).ConfigureAwait(true));
        await ProbeIsCachedAsync(app, client, s).ConfigureAwait(true);

        // Склад екземплярів документа змінився (нова таблиця з екземпляром за той самий період) — як після перенесення
        // версії чи зміни складу аркушів: зонд (число того самого актуального прогону) стає видимим.
        await new TestDocumentBuilder(sql.ConnectionString)
            .AddTableAsync(s.Document, TableLayoutKind.PerPeriodInstance, monthColumns: 0, rowCount: 1).ConfigureAwait(true);

        Assert.Equal(2, await TotalRowsAsync(app, client, s.DocumentId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "CALC-RESULTS")]
    public async Task Кеш_зведення_два_документи_не_ділять_запис()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        // Документ B — у тому самому проєкті, та сама таблиця, той самий проєктний прогін; числа двох рядків.
        long documentB, instanceB;
        await using (var db = Context())
        {
            var document = new Document(s.ProjectId, $"DOCB{s.RunId}", 1, Now);
            db.Documents.Add(document);
            await db.SaveChangesAsync().ConfigureAwait(true);
            documentB = document.Id;
            db.DocumentSheets.Add(new DocumentSheet(documentB, s.Document.SheetDefId));
            var loader = new BulkCellLoader(sql.ConnectionString, 1000);
            instanceB = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None).ConfigureAwait(true);
            var row = await loader.ReserveIdsAsync("doc.TableRowSeq", 2, CancellationToken.None).ConfigureAwait(true);
            db.TableInstances.Add(new TableInstance(s.Document.PeriodKey, instanceB, documentB, s.Document.TableDefId, Now));
            db.TableRows.Add(new TableRow(s.Document.PeriodKey, row, instanceB, RowKey.Create(s.RowKeys[0]), 1, Now));
            db.TableRows.Add(new TableRow(s.Document.PeriodKey, row + 1, instanceB, RowKey.Create(s.RowKeys[1]), 2, Now));
            await db.SaveChangesAsync().ConfigureAwait(true);
            await InsertResultAsync(db, s.RunId, s, s.RowKeys[0], instanceB, documentB).ConfigureAwait(true);
            await InsertResultAsync(db, s.RunId, s, s.RowKeys[1], instanceB, documentB).ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        Assert.Equal(1, await TotalRowsAsync(app, client, s.DocumentId).ConfigureAwait(true));
        Assert.Equal(2, await TotalRowsAsync(app, client, documentB).ConfigureAwait(true));
        Assert.Equal(1, await TotalRowsAsync(app, client, s.DocumentId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "CALC-RESULTS")]
    public async Task Кеш_зведення_читач_з_Deny_після_теплого_виклику_не_бачить_чисел()
    {
        var s = await ArrangeAsync(denyColumn: true).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);

        // Контроль у тому самому застосунку: читач без Deny бачить число — і прогріває кеш.
        using (var allowed = await SignedInAsync(app, s.UserName).ConfigureAwait(true))
        {
            Assert.Equal(1, await TotalRowsAsync(app, allowed, s.DocumentId).ConfigureAwait(true));
            Assert.Equal(1, await TotalRowsAsync(app, allowed, s.DocumentId).ConfigureAwait(true));
        }

        // Той самий документ, теплий запис, читач із Deny на колонку виходу: числа немає.
        using var denied = await SignedInAsync(app, s.DeniedUserName!).ConfigureAwait(true);
        Assert.Equal(0, await TotalRowsAsync(app, denied, s.DocumentId).ConfigureAwait(true));

        // Різниця — лише Deny: холодний застосунок дає йому те саме.
        using var cold = new EcrApiFactory(sql);
        using var deniedCold = await SignedInAsync(cold, s.DeniedUserName!).ConfigureAwait(true);
        Assert.Equal(0, await TotalRowsAsync(cold, deniedCold, s.DocumentId).ConfigureAwait(true));
    }

    /// <summary>Зонд: число другого рядка в ТОМУ САМОМУ актуальному прогоні — теплий кеш його не бачить.</summary>
    private async Task ProbeIsCachedAsync(EcrApiFactory app, HttpClient client, Scenario s)
    {
        await using (var db = Context())
        {
            await InsertResultAsync(db, s.RunId, s, s.RowKeys[1], s.Document.TableInstanceId, s.DocumentId).ConfigureAwait(false);
        }

        Assert.Equal(1, await TotalRowsAsync(app, client, s.DocumentId).ConfigureAwait(false));
    }

    private static async Task<int> TotalRowsAsync(EcrApiFactory app, HttpClient client, long documentId)
    {
        var url = $"/api/v1/documents/{documentId.ToString(CultureInfo.InvariantCulture)}/calculation-results/year/summary?year=2026";
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {response.StatusCode}: {body}\n{app.ErrorsText}");
        return JsonDocument.Parse(body).RootElement.GetProperty("totalRows").GetInt32();
    }

    private sealed record Scenario(
        TestDocument Document, IReadOnlyList<string> RowKeys, long RunId, int VersionId, int Unit, string Output,
        string UserName, string? DeniedUserName)
    {
        public long DocumentId => Document.DocumentId;

        public int ProjectId => Document.ProjectId;
    }

    /// <summary>
    /// Документ (січень 2026, відкритий, рядки R1/R2), вихід методології, прив'язаний до колонки 2, актуальний проєктний
    /// прогін із числом лише рядка R1; читач (і, за потреби, другий читач із Deny на колонку виходу).
    /// </summary>
    private async Task<Scenario> ArrangeAsync(bool denyColumn = false)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var b = await builder.BuildAsync(rowCount: 2).ConfigureAwait(false);
        await builder.OpenPeriodAsync(b).ConfigureAwait(false);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var output = $"E_C{tag}";

        await using var db = Context();
        var rowKeys = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == b.PeriodKey.Value && r.TableInstanceId == b.TableInstanceId)
            .OrderBy(r => r.Ordinal).Select(r => r.RowKeyValue).ToListAsync().ConfigureAwait(false);

        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, b.SheetDefId));
        var methodology = new Methodology(EcrCode.Create($"CYC{tag}"), Name("Combustion"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(version);
        db.CalculationBindings.Add(new CalculationBinding(b.TableDefId, b.ColumnDefIds[1], methodology.Id, output, "{}"));
        var run = CurrentRun(b.ProjectId);
        db.CalculationRuns.Add(run);

        var userName = await UserAsync(db, b.ProjectId, deny: null).ConfigureAwait(false);
        var deniedName = denyColumn ? await UserAsync(db, b.ProjectId, deny: b.ColumnDefIds[1]).ConfigureAwait(false) : null;
        await db.SaveChangesAsync().ConfigureAwait(false);

        var unit = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync().ConfigureAwait(false);
        var s = new Scenario(b, rowKeys, run.Id, version.Id, unit, output, userName, deniedName);
        await InsertResultAsync(db, run.Id, s, rowKeys[0], b.TableInstanceId, b.DocumentId).ConfigureAwait(false);
        return s;
    }

    private static async Task<string> UserAsync(EcrDbContext db, int projectId, int? deny)
    {
        var userName = $"cyc{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Calc year cache"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);
        foreach (var permission in Permissions)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, GrantLevel.Write));
        if (deny is { } column)
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Column, column, GrantLevel.Read, isDeny: true));
        }

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
        return userName;
    }

    private static CalculationRun CurrentRun(int projectId)
    {
        var run = new CalculationRun(projectId, 202601, triggeredByUserId: null, Now);
        run.Complete("Succeeded", Now.AddMinutes(1), "{}", errorMessage: null);
        run.MakeCurrent();
        return run;
    }

    private static Task<int> InsertResultAsync(EcrDbContext db, long runId, Scenario s, string rowKey, long instance, long document)
        => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO calc.CalculationResult
                (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId, TableInstanceId)
            VALUES (NEXT VALUE FOR calc.CalculationResultSeq, {runId}, {s.VersionId}, 202601, {document},
                    {rowKey}, {s.Output}, CAST(1 AS decimal(34,16)), {s.Unit}, {instance})
            """);

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
