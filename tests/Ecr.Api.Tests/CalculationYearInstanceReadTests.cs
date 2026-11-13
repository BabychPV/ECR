using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.Application.Ports;
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
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Огляд року читає екземпляри таблиць документа за 12 місяців ОДНИМ запитом
/// (<see cref="ICalculationYearReader.ReadTableInstancesAsync"/>), а не <c>IRowStore.GetTableInstancesAsync</c> на кожен
/// місяць, і дає той самий перелік, що й поштучне читання (порядок, архівний фолбек F-13 для місяця без гарячих).
/// </summary>
/// <remarks>
/// Мутація (локально): повернути в <c>CalculationYearViewBuilder</c> цикл <c>GetTableInstancesAsync</c> по місяцях —
/// храповик червоніє (12 гарячих + 10 архівних пошуків екземплярів замість одного).
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationYearInstanceReadTests(SqlServerFixture sql)
{
    private const string Password = "Calc-Year-Inst-R!42";
    private static readonly DateTime Now = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Permissions = ["Document.View", "Calculation.View"];
    private static readonly AsyncLocal<StrongBox<bool>?> Measuring = new();

    /// <summary>Команда, що читає екземпляри таблиць як ведучу таблицю (гарячу чи архівну).</summary>
    private static readonly Regex InstanceLookup = new(
        @"FROM\s+\[?(doc|arc)\]?\.\[?TableInstance\]?(\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "CALC-RESULTS")]
    public async Task Екземпляри_року_одним_запитом_збігаються_з_читанням_по_місяцях()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var scope = app.Services.CreateScope();
        var rows = scope.ServiceProvider.GetRequiredService<IRowStore>();
        var reader = scope.ServiceProvider.GetRequiredService<ICalculationYearReader>();

        var expected = new List<TableInstanceRef>();
        for (var m = 1; m <= 12; m++)
        {
            expected.AddRange(await rows.GetTableInstancesAsync(s.DocumentId, PeriodKey.Create(2026, m), CancellationToken.None)
                .ConfigureAwait(true));
        }

        var keys = Enumerable.Range(1, 12).Select(m => PeriodKey.Create(2026, m).Value).ToList();
        var actual = await reader.ReadTableInstancesAsync(s.DocumentId, keys, CancellationToken.None).ConfigureAwait(true);

        // Контроль: перелік не порожній і має всі три джерела — січень (гарячий, архівний двійник ігнорується), березень
        // (гарячий), травень (лише архів).
        Assert.Equal([202601, 202603, 202605], expected.Select(i => i.PeriodKey).Distinct());
        Assert.DoesNotContain(expected, i => i.TableInstanceId == s.ArchivedJanuaryTwinId);
        // ✎ C-1: січневий гарячий екземпляр має помилку формули — прапорець мусить збігтися з поштучним читанням.
        Assert.Single(expected, i => i.HasFormulaErrors);
        Assert.Equal(expected, actual);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "CALC-RESULTS")]
    public async Task Зведення_року_читає_екземпляри_одним_запитом()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        app.Server.PreserveExecutionContext = true;
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        var url = $"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/calculation-results/year/summary?year=2026";

        var texts = new ConcurrentQueue<string>();
        using var counter = new SqlClientCommandCounter(new CommandTally(), command =>
        {
            if (Measuring.Value is not { Value: true })
            {
                return false;
            }

            texts.Enqueue(command.CommandText ?? string.Empty);
            return true;
        });

        // Розігрів (профіль, метадані, кеш зведення), потім замір теплого виклику.
        Assert.Equal(1, await TotalRowsAsync(app, client, url).ConfigureAwait(true));
        counter.Tally.Reset();
        texts.Clear();
        var flag = new StrongBox<bool>(true);
        Measuring.Value = flag;
        int total;
        try
        {
            total = await TotalRowsAsync(app, client, url).ConfigureAwait(true);
        }
        finally
        {
            flag.Value = false;
            Measuring.Value = null;
        }

        counter.AssertObserved();
        Assert.Equal(1, total);

        var lookups = texts.Where(t => InstanceLookup.IsMatch(t)).ToList();
        var detail = string.Join("\n", lookups.Select(t =>
        {
            var flat = string.Join(' ', t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return "  " + (flat.Length > 200 ? flat[..200] + "…" : flat);
        }));

        // ⛔ Храповик: екземпляри року — одним запитом. Ще два ведуть від doc.TableInstance і від кількості місяців не
        // залежать: відбиток екземплярів у ключі кешу зведення (CRY03) і агрегат комірок формул.
        Assert.True(
            lookups.Count == 3,
            $"Зведення року: {lookups.Count} запитів від doc/arc.TableInstance (очікувано 3 — екземпляри року, відбиток "
            + "кешу, агрегат формул); "
            + $"усього команд {counter.Tally.Snapshot().Total}.\n{detail}");
    }

    private static async Task<int> TotalRowsAsync(EcrApiFactory app, HttpClient client, string url)
    {
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {response.StatusCode}: {body}\n{app.ErrorsText}");
        return JsonDocument.Parse(body).RootElement.GetProperty("totalRows").GetInt32();
    }

    private sealed record Scenario(long DocumentId, string UserName, long ArchivedJanuaryTwinId);

    /// <summary>
    /// Документ: січень 2026 (відкритий, гарячий екземпляр із рядками R1/R2 і архівний «двійник», який фолбек мусить
    /// ігнорувати), березень (гарячий екземпляр), травень (лише архівний екземпляр); вихід методології, прив'язаний до
    /// колонки 2, актуальний проєктний прогін січня з числом рядка R1; читач із правом на проєкт.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var b = await builder.BuildAsync(rowCount: 2).ConfigureAwait(false);
        await builder.OpenPeriodAsync(b).ConfigureAwait(false);
        await builder.AddPeriodInstanceAsync(b, PeriodKey.Create(2026, 3)).ConfigureAwait(false);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var output = $"E_I{tag}";

        await using var db = Context();
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var archived = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 2, CancellationToken.None).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO arc.TableInstance (PeriodKey, Id, DocumentId, TableDefId, CreatedAt, ModifiedAt)
            VALUES (202605, {archived}, {b.DocumentId}, {b.TableDefId}, {Now}, {Now}),
                   (202601, {archived + 1}, {b.DocumentId}, {b.TableDefId}, {Now}, {Now})
            """).ConfigureAwait(false);

        var tableRows = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == b.PeriodKey.Value && r.TableInstanceId == b.TableInstanceId)
            .OrderBy(r => r.Ordinal).Select(r => new { r.Id, r.RowKeyValue }).ToListAsync().ConfigureAwait(false);
        var rowKeys = tableRows.Select(r => r.RowKeyValue).ToList();

        // ✎ C-1: збережена помилка формули на січневому гарячому екземплярі (HasFormulaErrors = true).
        db.FormulaErrors.Add(new FormulaError(
            b.DocumentId, b.PeriodKey.Value, b.TableInstanceId, b.TableDefId, sheetCopyId: null,
            tableRows[0].Id, tableRows[0].RowKeyValue, b.ColumnDefIds[1], formulaDefId: 1, FormulaError.ErrorKind,
            "#DIV/0", inputsJson: null, sourcePeriodKey: null, sourceTableDefId: null, sourceRowKey: null,
            sourceColumnDefId: null, Now));

        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, b.SheetDefId));
        var methodology = new Methodology(EcrCode.Create($"CYI{tag}"), Name("Combustion"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(version);
        db.CalculationBindings.Add(new CalculationBinding(b.TableDefId, b.ColumnDefIds[1], methodology.Id, output, "{}"));
        var run = new CalculationRun(b.ProjectId, 202601, triggeredByUserId: null, Now);
        run.Complete("Succeeded", Now.AddMinutes(1), "{}", errorMessage: null);
        run.MakeCurrent();
        db.CalculationRuns.Add(run);
        var userName = await UserAsync(db, b.ProjectId).ConfigureAwait(false);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var unit = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync().ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO calc.CalculationResult
                (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId, TableInstanceId)
            VALUES (NEXT VALUE FOR calc.CalculationResultSeq, {run.Id}, {version.Id}, 202601, {b.DocumentId},
                    {rowKeys[0]}, {output}, CAST(1 AS decimal(34,16)), {unit}, {b.TableInstanceId})
            """).ConfigureAwait(false);

        return new Scenario(b.DocumentId, userName, archived + 1);
    }

    private static async Task<string> UserAsync(EcrDbContext db, int projectId)
    {
        var userName = $"cyi{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Calc year instances"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);
        foreach (var permission in Permissions)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, GrantLevel.Write));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
        return userName;
    }

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
