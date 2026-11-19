// tests/Ecr.Api.Tests/ScriptSourceDocumentDependentsTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.Scripting;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// MS x B179 L2: правка комірки документа-джерела (HSE400-подібного) САМА, без ручного перерахунку, перераховує документ
/// споживача, чия методологія - СКРИПТ (<c>Engine = Script</c>), що читає склад через
/// <c>REGFIELD(REGFIND(..., period.Year, period.Month, ...), "VALUE")</c>: граф бачить скриптове читання довідника
/// (<c>cfg.RegistryUse</c> з кодом <c>script</c> + прив'язка виходу до колонки), хук джерела ставить батьківську задачу,
/// споживач перераховується справжнім <c>ScriptCalculationModule</c>, число точне.
/// </summary>
/// <remarks>
/// <para>
/// Світ - як у <c>SourceDocumentDependentsAcceptanceTests</c> (справжній хост, SQL, черга; завантажувач складу на коди
/// тесту; зв'язка <c>Any</c>, біти 1/1), але споживач - не формула колонки, а опублікована версія-скрипт з виходом
/// <c>DENS</c> у колонці <c>Calculated</c>. Другий споживач - скрипт, що читає РЕЗУЛЬТАТ першого
/// (<c>table Src = ...; Sum(Src.DENS)</c>) і пише на іншому аркуші того самого документа: його теж перераховано
/// (читання таблиць - лише свого документа).
/// </para>
/// <para>
/// Мутації (локально): у <c>DependencyGraphAssembler.Collapse</c> брати лише прямих читачів (без
/// <c>AffectedMethodologies</c>) - перший споживач лишається (пряме читання); не писати ребро <c>RegistryUse</c> -
/// червоніє перший споживач.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class ScriptSourceDocumentDependentsTests(SqlServerFixture sql)
{
    private const string Password = "Script-Source-B179-1!";
    private const int Year = 2099;
    private const int Month = 10;
    private const int PeriodKeyValue = (Year * 100) + Month;
    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(120);

    private sealed record Scenario(
        TestDocumentBuilder Builder,
        TestDocument Source,
        long SourceInstanceId,
        string SourceTemplateCode,
        string RegistryCode,
        TestDocument First,
        string UserName);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B179")]
    public async Task Правка_джерела_сама_перераховує_скриптових_споживачів_і_читача_їхніх_результатів_точними_числами()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql)
        {
            ExtraServices = services => services.AddScoped<IRegistrySnapshotLoader>(sp => new Hse400CompositionSnapshotLoader(
                sp.GetRequiredService<RegistrySnapshotLoader>(),
                sp.GetRequiredService<EcrDbContext>(),
                sp.GetRequiredService<ICalculationResultStore>(),
                enabled: null,
                registryCode: s.RegistryCode,
                templateCode: s.SourceTemplateCode)),
        };
        using var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        await PatchAsync(app, client, s, 0.81m).ConfigureAwait(true);
        await ExpectAsync(app, s, s.First, "DENS", 0.81m).ConfigureAwait(true);
        await ExpectAsync(app, s, s.First, "TOT", 0.81m).ConfigureAwait(true);

        await PatchAsync(app, client, s, 0.95m).ConfigureAwait(true);
        await ExpectAsync(app, s, s.First, "DENS", 0.95m).ConfigureAwait(true);
        await ExpectAsync(app, s, s.First, "TOT", 0.95m).ConfigureAwait(true);
    }

    private async Task<Scenario> ArrangeAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var registryCode = $"LAND_HSE400_COMPOSITION_{tag}";
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var now = DateTime.UtcNow;

        int registryId;
        await using (var db = builder.CreateContext())
        {
            var registry = new RegistryDef(EcrCode.Create(registryCode), Name(registryCode), isTemporal: false);
            db.RegistryDefs.Add(registry);
            await db.SaveChangesAsync().ConfigureAwait(false);
            var fields = new List<RegistryFieldDef>();
            foreach (var (code, type, ordinal) in new[]
                     {
                         ("YEAR", CellDataType.Int, 1), ("MONTH", CellDataType.Int, 2), ("STREAM", CellDataType.String, 3),
                         ("INDICATOR", CellDataType.String, 4), ("VALUE", CellDataType.Decimal, 5),
                     })
            {
                var field = new RegistryFieldDef(registry.Id, EcrCode.Create(code), Name(code), type, ordinal);
                field.Update(Name(code), ordinal, ordinal < 5);
                db.RegistryFieldDefs.Add(field);
                fields.Add(field);
                await db.SaveChangesAsync().ConfigureAwait(false);
            }

            db.RegistryKeyDefs.Add(new RegistryKeyDef(
                registry.Id, EcrCode.Create("PK"), Name("PK"), [.. fields.Take(4)], isPrimary: true, ignoreCase: true, 1, now));
            await db.SaveChangesAsync().ConfigureAwait(false);
            registryId = registry.Id;
        }

        // Джерело: таблиця-потік FG_Internal, місячна колонка M10, рядок Vddg.
        var source = await builder.BuildAsync(PeriodKeyValue, columnCount: 1, rowCount: 1).ConfigureAwait(false);
        long sourceInstance;
        string sourceTemplateCode;
        await using (var db = builder.CreateContext())
        {
            var table = new TableDef(
                source.SheetDefId, EcrCode.Create("FG_Internal"), Name("FG_Internal"), 50,
                TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
            db.TableDefs.Add(table);
            await db.SaveChangesAsync().ConfigureAwait(false);
            var month = new ColumnDef(table.Id, EcrCode.Create("M10"), Name("M10"), 1, CellDataType.Decimal);
            month.SetMonth(true, (byte)Month);
            db.ColumnDefs.Add(month);
            db.RowDefs.Add(new RowDef(table.Id, RowKey.Create("Vddg"), 1, Name("Vddg"), RowKind.Item));
            await db.SaveChangesAsync().ConfigureAwait(false);

            var loader = new BulkCellLoader(sql.ConnectionString, 1000);
            sourceInstance = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None).ConfigureAwait(false);
            var rowId = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None).ConfigureAwait(false);
            var key = new PeriodKey(PeriodKeyValue);
            db.TableInstances.Add(new TableInstance(key, sourceInstance, source.DocumentId, table.Id, now));
            db.TableRows.Add(new TableRow(key, rowId, sourceInstance, RowKey.Create("Vddg"), 1, now));
            db.DocumentSheets.Add(new DocumentSheet(source.DocumentId, source.SheetDefId));
            await OpenAsync(db, source.ProjectId, now).ConfigureAwait(false);
            await db.SaveChangesAsync().ConfigureAwait(false);
            sourceTemplateCode = await db.Templates.Where(t => t.Id == source.TemplateId).Select(t => t.Code).SingleAsync().ConfigureAwait(false);
        }

        // Перший споживач: скрипт читає склад. Другий - в ІНШОМУ документі, читає результат першого.
        var first = await builder.BuildAsync(PeriodKeyValue, columnCount: 1, rowCount: 1).ConfigureAwait(false);
        await using (var db = builder.CreateContext())
        {
            // Другий аркуш ТОГО САМОГО документа (скрипт читає таблиці лише свого документа): таблиця, колонка TOT, один рядок.
            var sheet = new SheetDef(first.TemplateVersionId, EcrCode.Create("SHB" + tag), Name("Sheet B"), 2);
            db.SheetDefs.Add(sheet);
            await db.SaveChangesAsync().ConfigureAwait(false);
            var secondTable = new TableDef(
                sheet.Id, EcrCode.Create("TB" + tag), Name("Table B"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
            db.TableDefs.Add(secondTable);
            await db.SaveChangesAsync().ConfigureAwait(false);
            var firstTable = await db.TableDefs.AsNoTracking().SingleAsync(t => t.Id == first.TableDefId).ConfigureAwait(false);
            var dens = new ColumnDef(first.TableDefId, EcrCode.Create("DENS"), Name("DENS"), 10, CellDataType.Calculated);
            var total = new ColumnDef(secondTable.Id, EcrCode.Create("TOT"), Name("TOT"), 1, CellDataType.Calculated);
            db.ColumnDefs.AddRange(dens, total);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var secondLoader = new BulkCellLoader(sql.ConnectionString, 1000);
            var secondInstance = await secondLoader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None).ConfigureAwait(false);
            var secondRow = await secondLoader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None).ConfigureAwait(false);
            var firstKey = new PeriodKey(PeriodKeyValue);
            db.TableInstances.Add(new TableInstance(firstKey, secondInstance, first.DocumentId, secondTable.Id, now));
            db.TableRows.Add(new TableRow(firstKey, secondRow, secondInstance, RowKey.Create("RB" + tag), 1, now));
            await db.SaveChangesAsync().ConfigureAwait(false);

            var unit = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync().ConfigureAwait(false);
            var firstScript = $"rows from {firstTable.Code};\n"
                + $"output DENS : kg = REGFIELD(REGFIND(\"{registryCode}\", period.Year, period.Month, \"FG_Internal\", \"Vddg\"), \"VALUE\");";
            var (firstMethodology, firstVersion) = await AddScriptAsync(db, tag + "A", firstScript, "DENS", first.TableDefId, dens.Id, unit, now)
                .ConfigureAwait(false);
            db.RegistryUses.Add(RegistryUse.ForMethodologyFormula(firstVersion, "script", registryId, fieldPath: null));

            var secondScript = $"rows from {secondTable.Code};\ntable Src = {firstTable.Code};\noutput TOT : kg = Sum(Src.DENS);";
            var (secondMethodology, secondVersion) = await AddScriptAsync(db, tag + "B", secondScript, "TOT", secondTable.Id, total.Id, unit, now)
                .ConfigureAwait(false);
            db.MethodologyReadTables.Add(new MethodologyReadTable(secondVersion, firstTable.Code));
            db.MethodologyDependencies.Add(new MethodologyDependency(secondMethodology, firstMethodology));
            await db.SaveChangesAsync().ConfigureAwait(false);

            await OpenAsync(db, first.ProjectId, now).ConfigureAwait(false);

            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        await using (var db = builder.CreateContext())
        {
            db.DocumentSourceBindings.Add(new DocumentSourceBinding($"MSB179_{tag}", source.TemplateId, registryId));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        string userName;
        await using (var db = builder.CreateContext())
        {
            userName = await NewUserAsync(db, source.ProjectId).ConfigureAwait(false);
        }

        return new Scenario(builder, source, sourceInstance, sourceTemplateCode, registryCode, first, userName);
    }

    private static async Task<(int MethodologyId, int VersionId)> AddScriptAsync(
        EcrDbContext db, string key, string text, string output, int tableDefId, int columnId, int unitId, DateTime now)
    {
        var methodology = new Methodology(EcrCode.Create($"SB{key}"), Name("script"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, now, MethodologyEngine.Script);
        version.SetModes(NumericMode.Strict, CalendarMode.Actual, TraceLevel.Full);
        version.SetScript(text, ScriptCompiler.Hash(text));
        version.Publish(2, "test", new DateOnly(2099, 1, 1), testsPassed: true, now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var methodologyOutput = new MethodologyOutput(version.Id, EcrCode.Create(output), unitId, 1);
        methodologyOutput.SetPerSubstance(false);
        db.MethodologyOutputs.Add(methodologyOutput);
        db.CalculationBindings.Add(new CalculationBinding(tableDefId, columnId, methodology.Id, output, "{}"));
        await db.SaveChangesAsync().ConfigureAwait(false);
        return (methodology.Id, version.Id);
    }

    private static async Task ExpectAsync(EcrApiFactory app, Scenario s, TestDocument document, string output, decimal expected)
    {
        var until = DateTime.UtcNow + Deadline;
        decimal? seen = null;
        while (DateTime.UtcNow < until)
        {
            await using (var db = s.Builder.CreateContext())
            {
                var rows = await new CalculationResultStore(db, new TestClock(DateTime.UtcNow))
                    .ReadCurrentAsync(document.DocumentId, PeriodKeyValue, CancellationToken.None).ConfigureAwait(false);
                seen = rows.Where(r => r.OutputCode == output).Select(r => (decimal?)r.Value).FirstOrDefault();
            }

            if (seen == expected)
            {
                return;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        Assert.Fail($"{output} документа {document.DocumentId}: очікувано {expected}, є {seen?.ToString(CultureInfo.InvariantCulture) ?? "null"}.\n{app.ErrorsText}");
    }

    private static async Task PatchAsync(EcrApiFactory app, HttpClient client, Scenario s, decimal value)
    {
        var table = await SendAsync(
                app, HttpStatusCode.OK,
                client.GetAsync(new Uri($"/api/v1/documents/{s.Source.DocumentId}/tables/{s.SourceInstanceId}", UriKind.Relative)))
            .ConfigureAwait(false);
        var version = JsonDocument.Parse(table).RootElement.GetProperty("rows").EnumerateArray()
            .Single(r => r.GetProperty("rowKey").GetString() == "Vddg")
            .GetProperty("rowVersion").GetString();

        await SendAsync(
                app, HttpStatusCode.OK,
                client.PatchAsJsonAsync(
                    new Uri($"/api/v1/documents/{s.Source.DocumentId}/cells", UriKind.Relative),
                    new
                    {
                        tableInstanceId = s.SourceInstanceId,
                        periodKey = PeriodKeyValue,
                        origin = "UserEdit",
                        rows = new[]
                        {
                            new
                            {
                                rowKey = "Vddg",
                                baseVersion = version,
                                cells = new object[] { new { columnCode = "M10", value = value.ToString(CultureInfo.InvariantCulture) } },
                            },
                        },
                    }))
            .ConfigureAwait(false);
    }

    private static async Task<string> SendAsync(EcrApiFactory app, HttpStatusCode expected, Task<HttpResponseMessage> call)
    {
        using var response = await call.ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode} замість {(int)expected}\n{body}\n{app.ErrorsText}");
        return body;
    }

    private static async Task<HttpClient> SignInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        using var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private static async Task OpenAsync(EcrDbContext db, int projectId, DateTime now)
    {
        var project = await db.Projects.FirstAsync(p => p.Id == projectId).ConfigureAwait(false);
        project.Activate(now);
        var policy = await db.PeriodPolicies.FirstAsync(p => p.Id == project.PeriodPolicyId).ConfigureAwait(false);
        foreach (var period in await db.Periods.Where(p => p.ProjectId == projectId).ToListAsync().ConfigureAwait(false))
        {
            period.RecomputeBoundaries(policy, SiteZone);
            period.AdvanceTo(PeriodState.Open, now);
        }
    }

    private static async Task<string> NewUserAsync(EcrDbContext db, int projectId)
    {
        var name = $"ss_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12].ToUpperInvariant()), Name("Source editor"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, GrantLevel.Write));
        await db.SaveChangesAsync().ConfigureAwait(false);
        return name;
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
