// tests/Ecr.Api.Tests/SourceDocumentDependentsAcceptanceTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Application.Templates;
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
/// B179 L2 — ПРИЙМАННЯ (P-0118-34, «живий» дефект: зміна HSE400 doc 98 за жовтень не поставила перерахунку Land):
/// правка комірки документа-джерела звичайним шляхом користувача (<c>PATCH …/cells</c> → <c>FormulaRecalculationJob</c>)
/// сама, без жодного ручного перерахунку, змінює значення жовтня в документі-споживачі, чия ФОРМУЛА ШАБЛОНУ читає склад
/// через <c>REGFIND(…, [Period].Year, [Period].Sequence, …)</c> (як мости <c>Density_M10</c> Land).
/// </summary>
/// <remarks>
/// <para>
/// Справжній хост, SQL Server і черга (виконавець тестового хоста). Зв'язка — <c>Any</c>, біти 1/1; рядка
/// <c>cfg.DocumentSourceBindingConsumer</c> НЕМАЄ, ключа <c>Calculations:Hse400RecalculateLandDependents</c> НЕМАЄ (сам
/// <c>appsettings.json</c> — постачений). Підміна в хості лише одна за суттю: завантажувач складу на коди шаблону й
/// довідника цього тесту (робочих <c>HSE400</c>/<c>LAND_HSE400_COMPOSITION</c> у спільній базі тест не чіпає).
/// Перекриття «читати з документа» (<see cref="IDocumentSourceFlagOverrides"/>) — СПРАВЖНЄ, з DI.
/// </para>
/// <para>
/// ✎ P-0118-31/33 («один вимикач»): ключ <c>Calculations:Hse400CompositionFromDocument</c> виведено. Прогін і без ключа
/// (свіжа інсталяція), і із залишковою змінною <c>ECR_Calculations__Hse400CompositionFromDocument=false</c> (як на live,
/// лише значення протилежне) — вирішує біт зв'язки <c>ReadFromDocument</c> = 1.
/// </para>
/// <para>
/// Мутації (локально): прибрати виклик хука в <c>FormulaRecalculationJob</c> — значення споживача не змінюється, червоний;
/// повернути читання ключа в <c>DocumentSourceFlagOverrides</c> — випадок <c>"false"</c> червоний.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class SourceDocumentDependentsAcceptanceTests(SqlServerFixture sql)
{
    private const string RetiredKeyVariable = "ECR_Calculations__Hse400CompositionFromDocument";
    private const string Password = "Source-Dependents-B179-1!";
    private const int Year = 2099;
    private const int Month = 10;
    private const int PeriodKeyValue = (Year * 100) + Month;
    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(90);

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B179")]
    public async Task Правка_джерела_за_жовтень_сама_змінює_жовтень_споживача_і_лише_його(string? retiredKeyValue)
    {
        var previous = Environment.GetEnvironmentVariable(RetiredKeyVariable);
        Environment.SetEnvironmentVariable(RetiredKeyVariable, retiredKeyValue);
        try
        {
            await RunAsync().ConfigureAwait(true);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RetiredKeyVariable, previous);
        }
    }

    private async Task RunAsync()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql)
        {
            ExtraServices = services =>
            {
                // Лише коди шаблону й довідника цього тесту; enabled: null — як у DI продукту.
                services.AddScoped<IRegistrySnapshotLoader>(sp => new Hse400CompositionSnapshotLoader(
                    sp.GetRequiredService<RegistrySnapshotLoader>(),
                    sp.GetRequiredService<EcrDbContext>(),
                    sp.GetRequiredService<ICalculationResultStore>(),
                    enabled: null,
                    registryCode: s.RegistryCode,
                    templateCode: s.SourceTemplateCode));
            },
        };

        // Перекриття — справжнє з DI продукту: виведений ключ не дає значення.
        Assert.Null(app.Services.GetRequiredService<IDocumentSourceFlagOverrides>().ReadFromDocument);
        using var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        // 1. Перше значення складу за жовтень → формула жовтня споживача його показує (жодного ручного перерахунку).
        await PatchAsync(app, client, s, 0.81m).ConfigureAwait(true);
        await ExpectConsumerAsync(app, s, 0.81m).ConfigureAwait(true);

        // 2. Зміна значення → значення жовтня споживача ЗМІНИЛОСЬ.
        await PatchAsync(app, client, s, 0.95m).ConfigureAwait(true);
        await ExpectConsumerAsync(app, s, 0.95m).ConfigureAwait(true);

        // 3. Лише жовтень: перерахунку вересня й листопада споживача ніхто не ставив; батьківська задача — за місяцем.
        // ⚠ Хост тестів — Quartz: ціль задачі лежить у JobId (`{тип}~{ціль}~{guid}`), TargetKey порожній; черга в базі —
        // навпаки. Беремо обидва.
        await using var db = s.Builder.CreateContext();
        var consumerPrefix = $"IRecalculationJob~doc{s.Consumer.DocumentId}-";
        var targets = await db.JobProgresses.AsNoTracking()
            .Where(p => p.JobId.StartsWith(consumerPrefix) || (p.TargetKey != null && p.TargetKey.StartsWith(consumerPrefix)))
            .Select(p => p.TargetKey ?? p.JobId)
            .ToListAsync()
            .ConfigureAwait(true);
        Assert.NotEmpty(targets);
        Assert.All(targets, t => Assert.StartsWith($"{consumerPrefix}p{PeriodKeyValue}-", t, StringComparison.Ordinal));

        var parent = $"IRegistryImpactRecalculationJob~{RegistryImpactRecalculationTarget.ForSourceChange(s.BindingId, Year, Month)}";
        Assert.True(
            await db.JobProgresses.AsNoTracking()
                .AnyAsync(p => p.JobId.StartsWith(parent + "~") || p.TargetKey == parent)
                .ConfigureAwait(true),
            "Батьківської задачі «зв'язка × рік × місяць» немає.");
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Scenario(
        TestDocumentBuilder Builder,
        TestDocument Source,
        long SourceInstanceId,
        string SourceRowKey,
        string MonthColumnCode,
        string SourceTemplateCode,
        TestDocument Consumer,
        int ConsumerColumnId,
        string RegistryCode,
        int BindingId,
        string UserName);

    private static async Task PatchAsync(EcrApiFactory app, HttpClient client, Scenario s, decimal value)
    {
        var table = await ExpectAsync(
                app, HttpStatusCode.OK,
                client.GetAsync(new Uri(
                    $"/api/v1/documents/{s.Source.DocumentId}/tables/{s.SourceInstanceId}", UriKind.Relative)))
            .ConfigureAwait(false);
        var version = JsonDocument.Parse(table).RootElement.GetProperty("rows").EnumerateArray()
            .Single(r => r.GetProperty("rowKey").GetString() == s.SourceRowKey)
            .GetProperty("rowVersion").GetString();

        await ExpectAsync(
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
                                rowKey = s.SourceRowKey,
                                baseVersion = version,
                                cells = new object[]
                                {
                                    new { columnCode = s.MonthColumnCode, value = value.ToString(CultureInfo.InvariantCulture) },
                                },
                            },
                        },
                    }))
            .ConfigureAwait(false);
    }

    /// <summary>Опитує комірку формули споживача за жовтень у базі (черга асинхронна щодо PATCH).</summary>
    private static async Task ExpectConsumerAsync(EcrApiFactory app, Scenario s, decimal expected)
    {
        var until = DateTime.UtcNow + Deadline;
        decimal? seen = null;
        while (DateTime.UtcNow < until)
        {
            await using (var db = s.Builder.CreateContext())
            {
                seen = await db.CellValues.AsNoTracking()
                    .Where(c => c.PeriodKeyValue == PeriodKeyValue
                                && c.TableRowId == s.Consumer.RowIds[0]
                                && c.ColumnDefId == s.ConsumerColumnId)
                    .Select(c => c.ValueNumeric)
                    .FirstOrDefaultAsync()
                    .ConfigureAwait(false);
            }

            if (seen == expected)
            {
                return;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        Assert.Fail($"Споживач за {PeriodKeyValue}: очікувано {expected}, є {seen?.ToString(CultureInfo.InvariantCulture) ?? "null"}.\n{app.ErrorsText}");
    }

    private static async Task<string> ExpectAsync(EcrApiFactory app, HttpStatusCode expected, Task<HttpResponseMessage> call)
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

    /// <summary>
    /// Довідник складу (поля й первинний ключ — як <c>LAND_HSE400_COMPOSITION</c>); джерело — таблиця-потік
    /// <c>FG_Internal</c> з місячною колонкою жовтня й рядком <c>Vddg</c>; споживач — формула колонки, що читає склад за
    /// місяцем, опублікована (ребра видобуває публікація), документ із періодами вересень–листопад; зв'язка <c>Any</c>.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var registryCode = $"LAND_HSE400_COMPOSITION_{tag}";
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var now = DateTime.UtcNow;

        // Довідник складу.
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

        // Джерело.
        var source = await builder.BuildAsync(PeriodKeyValue, columnCount: 1, rowCount: 1).ConfigureAwait(false);
        long sourceInstance;
        const string rowKey = "Vddg";
        const string monthCode = "M10";
        string sourceTemplateCode;
        await using (var db = builder.CreateContext())
        {
            var table = new TableDef(
                source.SheetDefId, EcrCode.Create("FG_Internal"), Name("FG_Internal"), 50,
                TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
            db.TableDefs.Add(table);
            await db.SaveChangesAsync().ConfigureAwait(false);
            var month = new ColumnDef(table.Id, EcrCode.Create(monthCode), Name(monthCode), 1, CellDataType.Decimal);
            month.SetMonth(true, (byte)Month);
            db.ColumnDefs.Add(month);
            db.RowDefs.Add(new RowDef(table.Id, RowKey.Create(rowKey), 1, Name(rowKey), RowKind.Item));
            await db.SaveChangesAsync().ConfigureAwait(false);

            var loader = new BulkCellLoader(sql.ConnectionString, 1000);
            sourceInstance = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None).ConfigureAwait(false);
            var rowId = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None).ConfigureAwait(false);
            var key = new PeriodKey(PeriodKeyValue);
            db.TableInstances.Add(new TableInstance(key, sourceInstance, source.DocumentId, table.Id, now));
            db.TableRows.Add(new TableRow(key, rowId, sourceInstance, RowKey.Create(rowKey), 1, now));
            db.DocumentSheets.Add(new DocumentSheet(source.DocumentId, source.SheetDefId));
            await OpenAsync(db, source.ProjectId, now).ConfigureAwait(false);
            await db.SaveChangesAsync().ConfigureAwait(false);
            sourceTemplateCode = await db.Templates.Where(t => t.Id == source.TemplateId).Select(t => t.Code).SingleAsync().ConfigureAwait(false);
        }

        // Споживач: колонка-формула, що читає склад за місяцем (як мости Density_M10 Land).
        var consumer = await builder.BuildAsync(PeriodKeyValue, columnCount: 1, rowCount: 1).ConfigureAwait(false);
        await builder.AddPeriodInstanceAsync(consumer, new PeriodKey(PeriodKeyValue - 1)).ConfigureAwait(false);
        await builder.AddPeriodInstanceAsync(consumer, new PeriodKey(PeriodKeyValue + 1)).ConfigureAwait(false);
        int consumerColumn;
        await using (var db = builder.CreateContext())
        {
            var column = new ColumnDef(consumer.TableDefId, EcrCode.Create("DENS"), Name("DENS"), 10, CellDataType.Formula);
            db.ColumnDefs.Add(column);
            await db.SaveChangesAsync().ConfigureAwait(false);
            consumerColumn = column.Id;

            var formula = new FormulaDef(
                consumer.TableDefId, FormulaScope.Column,
                $"REGFIELD(REGFIND('{registryCode}', [Period].Year, [Period].Sequence, 'FG_Internal', '{rowKey}'), 'VALUE')",
                ExpressionDialect.Template);
            formula.AssignColumn(column.Id);
            db.FormulaDefs.Add(formula);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var store = new TemplateVersionStore(db);
            var version = await store.GetWithStructureAsync(consumer.TemplateVersionId, CancellationToken.None).ConfigureAwait(false);
            await store.ReplaceFormulaDependenciesAsync(
                    consumer.TemplateVersionId, PublishChecks.Dependencies(version, new RealFormulaEngine()), CancellationToken.None)
                .ConfigureAwait(false);
            version.Publish(1, now);
            await OpenAsync(db, consumer.ProjectId, now).ConfigureAwait(false);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        // Зв'язка Any, біти 1/1; жодного рядка споживачів.
        int bindingId;
        await using (var db = builder.CreateContext())
        {
            var binding = new DocumentSourceBinding($"B179ACC_{tag}", source.TemplateId, registryId);
            db.DocumentSourceBindings.Add(binding);
            await db.SaveChangesAsync().ConfigureAwait(false);
            bindingId = binding.Id;
            Assert.False(await db.DocumentSourceBindingConsumers.AnyAsync(c => c.BindingId == bindingId).ConfigureAwait(false));
        }

        string userName;
        await using (var db = builder.CreateContext())
        {
            userName = await NewUserAsync(db, source.ProjectId).ConfigureAwait(false);
        }

        return new Scenario(
            builder, source, sourceInstance, rowKey, monthCode, sourceTemplateCode, consumer, consumerColumn, registryCode,
            bindingId, userName);
    }

    /// <summary>Проєкт активний, усі його періоди відкриті з межами (як <c>CrossSheetFormulaRecalculationTests</c>).</summary>
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

    /// <summary>Користувач із правом перегляду й записом на проєкт джерела.</summary>
    private static async Task<string> NewUserAsync(EcrDbContext db, int projectId)
    {
        var name = $"sd_{Guid.NewGuid():N}"[..20];
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
