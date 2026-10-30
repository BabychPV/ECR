// tests/Ecr.Api.Tests/PiCollectionEndToEndTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// D-462, наскрізно на справжній базі й справжньому контейнері: точки PI → збір → матеріалізація в комірку →
/// перерахунок методології → НОВИЙ <c>calc.CalculationResult</c>; виправлення значення в PI змінює результат,
/// видалення точки в PI — теж; незмінне повторне читання перерахунку не ставить; збір з увімкненим
/// <c>Collection:MonthColumnRouting</c> (Q-17) кладе значення в екземпляр-власник.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Справжнє все, крім двох речей. (1) Джерело — <see cref="FakePi"/> замість обох транспортів PI: точки в пам'яті,
/// які тест змінює між зборами. (2) Черга — записувач постановок: задачі (<c>ICollectionJob</c> →
/// <c>IMaterializeCollectedDataJob</c> → <c>IRecalculationJob</c>) тест виконує САМ, по черзі постановок, тими самими
/// реалізаціями з контейнера застосунку. Так ланцюг детермінований (без опитування фонового воркера), а кожна ланка —
/// та, що в проді: збирач, сховище, патчер, тригер перерахунку, оркестратор методологій.
/// </para>
/// <para>
/// Методологія заводиться маршрутами API (як <c>S-24</c>): <c>EMISSION = @A × CST.EF</c>, <c>EF = 2.5</c>; <c>A</c> —
/// колонка, яку пише інтеграція (згортка <c>Sum</c> точок атрибута за місяць).
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class PiCollectionEndToEndTests(SqlServerFixture sql)
{
    private const string Password = "Pi-Collection-E2E-D462!";
    private const string Flow = "E1|Flow";
    private const string RoutingVariable = "ECR_Collection__MonthColumnRouting";

    private static readonly DateTime Mid = new(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime From = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <remarks>
    /// ⛔ МУТАЦІЙНІ ДОКАЗИ: прибрати видалення (<c>RemoveMissingAsync</c> нічого не робить) — третій результат
    /// лишається 20 замість 16.25; повернути безумовний <c>WHEN MATCHED</c> у MERGE — не міняє чисел (патчер і так
    /// не пише незмінне), але останнє твердження про незмінний CollectionRunId сирої точки червоніє.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-462")]
    public async Task Точки_PI_доходять_до_результату_методології_виправлення_і_видалення_в_PI_його_змінюють()
    {
        var pi = new FakePi();
        pi.Set(Flow, Mid, 1.5m);
        pi.Set(Flow, Mid.AddHours(6), 2.5m);

        var stand = await ArrangeAsync(pi, routing: false);
        using var app = stand.App;

        try
        {
            // 1. Перший збір: A = 1.5 + 2.5 = 4 → EMISSION = 4 × 2.5 = 10.
            await stand.CollectAsync(From, To);
            Assert.Equal(4m, await stand.CellAsync(stand.ColumnA));
            var first = await stand.EmissionAsync();
            Assert.Equal(10m, first.Value);

            // 2. Виправлення в PI заднім числом (у вікні): 2.5 → 6.5 ⇒ A = 8, EMISSION = 20, НОВИЙ прогін.
            pi.Set(Flow, Mid.AddHours(6), 6.5m);
            await stand.CollectAsync(From, To);
            Assert.Equal(8m, await stand.CellAsync(stand.ColumnA));
            var corrected = await stand.EmissionAsync();
            Assert.Equal(20m, corrected.Value);
            Assert.NotEqual(first.RunId, corrected.RunId);

            // 3. Точку 1.5 видалили в PI ⇒ сира точка зникає, A = 6.5, EMISSION = 16.25.
            pi.Remove(Flow, Mid);
            await stand.CollectAsync(From, To);
            Assert.Equal([Mid.AddHours(6)], await stand.RawTimestampsAsync());
            Assert.Equal(6.5m, await stand.CellAsync(stand.ColumnA));
            var afterDeletion = await stand.EmissionAsync();
            Assert.Equal(16.25m, afterDeletion.Value);

            // 4. Незмінне повторне читання: ні перезапису сирої точки, ні перерахунку.
            var rawRun = await stand.RawRunIdAsync(Mid.AddHours(6));
            var recalculations = stand.Enqueued<IRecalculationJob>();
            await stand.CollectAsync(From, To);
            Assert.Equal(recalculations, stand.Enqueued<IRecalculationJob>());
            Assert.Equal(rawRun, await stand.RawRunIdAsync(Mid.AddHours(6)));
            Assert.Equal(afterDeletion.RunId, (await stand.EmissionAsync()).RunId);
        }
        finally
        {
            await stand.DeactivateAsync();
        }
    }

    /// <summary>
    /// Правка людини в колонці інтеграції не перезаписується виправленням із PI, і перерахунку за ним немає.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-462")]
    public async Task Виправлення_в_PI_не_перезаписує_правку_людини()
    {
        var pi = new FakePi();
        pi.Set(Flow, Mid, 4m);

        var stand = await ArrangeAsync(pi, routing: false);
        using var app = stand.App;

        try
        {
            await stand.CollectAsync(From, To);
            Assert.Equal(10m, (await stand.EmissionAsync()).Value);

            await stand.HumanEditAsync(stand.ColumnACode, 100m);

            pi.Set(Flow, Mid, 7m);
            var recalculations = stand.Enqueued<IRecalculationJob>();
            await stand.CollectAsync(From, To);

            Assert.Equal(100m, await stand.CellAsync(stand.ColumnA));
            Assert.Equal(recalculations, stand.Enqueued<IRecalculationJob>());
        }
        finally
        {
            await stand.DeactivateAsync();
        }
    }

    /// <summary>
    /// Q-17 разом зі збором: таблиця «місяці в колонках», прапор <c>Collection:MonthColumnRouting</c> увімкнено.
    /// Атрибут, змаплений на колонку жовтня, лягає в екземпляр жовтня; змаплений на не-місячну колонку — у якір
    /// року; повторне незмінне читання не пише й не ставить перерахунку; правка людини в колонці якоря не
    /// перезаписується.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-462")]
    [Trait("Decision", "Q-17")]
    public async Task Збір_із_маршрутизацією_місячних_колонок_пише_у_власника_без_повторних_записів()
    {
        var october = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
        var pi = new FakePi();
        pi.Set(Flow, october, 3m);
        pi.Set(Flow, october.AddHours(1), 4m);
        pi.Set("E1|Plain", october, 9m);

        var stand = await ArrangeAsync(pi, routing: true);
        using var app = stand.App;

        try
        {
            var from = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
            var to = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

            await stand.CollectAsync(from, to);

            Assert.Equal([(202610, 7m)], await stand.StoredAsync(stand.MonthColumn!.Value));
            Assert.Equal([(202601, 9m)], await stand.StoredAsync(stand.ColumnA));

            var recalculations = stand.Enqueued<IRecalculationJob>();
            var writes = await stand.CellWritesAsync();

            await stand.CollectAsync(from, to);

            // ⛔ МУТАЦІЯ: у IntegrationCellPatcher читати чинні значення фізично (без LogicalValuesAsync) — значення
            // якоря «змінюється» щопрогону: новий запис і новий перерахунок на кожен збір.
            Assert.Equal(recalculations, stand.Enqueued<IRecalculationJob>());
            Assert.Equal(writes, await stand.CellWritesAsync());

            // Правка людини колонки з чужим власником (лягає в якір) не перезаписується виправленням із PI.
            // ⛔ МУТАЦІЯ: шукати правки людини лише в екземплярі запиту (ManualCellsSql) — 12 замість 100.
            await stand.HumanEditAsync(stand.ColumnACode, 100m);
            Assert.Equal([(202601, 100m)], await stand.StoredAsync(stand.ColumnA));

            pi.Set("E1|Plain", october, 12m);
            await stand.CollectAsync(from, to);
            Assert.Equal([(202601, 100m)], await stand.StoredAsync(stand.ColumnA));
        }
        finally
        {
            await stand.DeactivateAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Стенд
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<Stand> ArrangeAsync(FakePi pi, bool routing)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = routing
            ? await builder.BuildAsync(202610, columnCount: 2, rowCount: 1, layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12)
            : await builder.BuildAsync(202601, columnCount: 2, rowCount: 1);

        var now = DateTime.UtcNow;
        string rowKey;
        int columnA;
        string columnACode;
        var emissionColumn = 0;
        int? monthColumn = routing ? doc.MonthColumnDefIds[9] : null;

        await using (var db = builder.CreateContext())
        {
            db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));

            if (routing)
            {
                // Календар року: усі місяці відкриті (як MonthColumnRoutedWriteApiTests).
                for (var m = 1; m <= 12; m++)
                {
                    var key = 202600 + m;
                    var period = key == 202610
                        ? await db.Periods.SingleAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == key)
                        : db.Periods.Add(new Period(
                            doc.ProjectId, new PeriodKey(key), (byte)m,
                            new DateOnly(2026, m, 1), new DateOnly(2026, m, DateTime.DaysInMonth(2026, m)))).Entity;
                    period.AdvanceTo(PeriodState.Open, now);
                }
            }
            else
            {
                var period = await db.Periods.SingleAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == doc.PeriodKey.Value);
                period.TransitionTo(PeriodState.Open, now);
            }

            await db.SaveChangesAsync();

            rowKey = await db.TableRows.AsNoTracking().Where(r => r.Id == doc.RowIds[0]).Select(r => r.RowKeyValue).SingleAsync();
            columnA = doc.ColumnDefIds[1];
            columnACode = await db.ColumnDefs.AsNoTracking().Where(c => c.Id == columnA).Select(c => c.Code).SingleAsync();

            if (!routing)
            {
                // Колонка-приймач виходу методології (тип Calculated, ECR-TMPL-4227).
                var emission = new ColumnDef(
                    doc.TableDefId, EcrCode.Create("EMISSION"), Name("Emission"), 10, CellDataType.Calculated);
                db.ColumnDefs.Add(emission);
                await db.SaveChangesAsync();
                emissionColumn = emission.Id;
            }
        }

        var tag = Guid.NewGuid().ToString("N")[..8];
        int entityId;
        var userName = $"pie2e_{tag}";

        await using (var db = builder.CreateContext())
        {
            var dataSource = new DataSource(
                EcrCode.Create($"PI{tag}"), Name("D-462 PI"), ExternalTransport.PiSqlClient, "pi-sql.example", $"PiAf.{tag}");
            db.DataSources.Add(dataSource);
            await db.SaveChangesAsync();

            var entity = new SourceEntity(dataSource.Id, $"E1{tag}", RegistrySourceKind.External);
            db.SourceEntities.Add(entity);
            await db.SaveChangesAsync();
            entityId = entity.Id;

            var flowTarget = routing ? monthColumn!.Value : columnA;
            var flow = EntityFieldMap.ToColumn(entity.Id, Flow, flowTarget);
            flow.SetMaterialization(rowKey, AggregationKind.Sum);
            db.EntityFieldMaps.Add(flow);

            if (routing)
            {
                var plain = EntityFieldMap.ToColumn(entity.Id, "E1|Plain", columnA);
                plain.SetMaterialization(rowKey, AggregationKind.Sum);
                db.EntityFieldMaps.Add(plain);
            }

            var user = new User(userName, userName, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var role = new Role(EcrCode.Create($"PIE{tag}"), Name("D-462 e2e"));
            db.Roles.Add(role);
            await db.SaveChangesAsync();

            foreach (var permission in new[]
                     {
                         "Document.View", "Calculation.View", "Calculation.EditFormula",
                         "Calculation.EditConstant", "Calculation.EditRule", "Calculation.Publish", "Calculation.Recalculate",
                     })
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Submit));
            await db.SaveChangesAsync();
        }

        var jobs = Substitute.For<IBackgroundJobScheduler>();
        var factory = new EcrApiFactory(sql);
        var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<IBackgroundJobScheduler>();
            services.AddSingleton(jobs);
            services.RemoveAll<IExternalDataSource>();
            services.AddScoped<IExternalDataSource>(_ => pi);
        }));

        // ⚠ Прапор Q-17 — змінною оточення на час СТАРТУ хоста (як MonthColumnRoutedWriteApiTests): хост
        // будується першим `CreateClient`, і саме тоді конфігурація читає змінну.
        HttpClient client;
        Environment.SetEnvironmentVariable(RoutingVariable, routing ? "true" : "false");
        try
        {
            client = app.CreateClient();
        }
        finally
        {
            Environment.SetEnvironmentVariable(RoutingVariable, null);
        }

        var login = await client.PostAsJsonAsync(new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password });
        Assert.True(login.IsSuccessStatusCode, $"вхід: {login.StatusCode}: {factory.ErrorsText}");

        var stand = new Stand(
            factory, app, client, jobs, builder, doc, entityId, rowKey, columnA, columnACode, emissionColumn, monthColumn);

        if (!routing)
        {
            await stand.ArrangeMethodologyAsync();
        }

        return stand;
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Результат методології: число і прогін, що його дав.</summary>
    private sealed record Emission(decimal Value, long RunId);

    private sealed class Stand(
        EcrApiFactory factory,
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app,
        HttpClient client,
        IBackgroundJobScheduler jobs,
        TestDocumentBuilder builder,
        TestDocument doc,
        int entityId,
        string rowKey,
        int columnA,
        string columnACode,
        int emissionColumn,
        int? monthColumn) : IDisposable
    {
        private int pumped;

        public Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> App { get; } = app;

        public int ColumnA { get; } = columnA;

        public string ColumnACode { get; } = columnACode;

        public int? MonthColumn { get; } = monthColumn;

        /// <summary>Скільки разів ставили задачу цього типу.</summary>
        public int Enqueued<TJob>() => EnqueueCalls().Count(c => c.Type == typeof(TJob));

        /// <summary>Ручний збір діапазону: задача збору і все, що вона поставила, — до порожньої черги.</summary>
        public async Task CollectAsync(DateTime from, DateTime to)
        {
            await RunJobAsync(typeof(ICollectionJob), new CollectionJobRequest(entityId, from, to, Manual: true));
            await PumpAsync();
        }

        public async Task<decimal?> CellAsync(int columnDefId)
        {
            await using var db = builder.CreateContext();

            return await db.CellValues.AsNoTracking()
                .Where(c => c.PeriodKeyValue == doc.PeriodKey.Value && c.TableRowId == doc.RowIds[0] && c.ColumnDefId == columnDefId)
                .Select(c => c.ValueNumeric)
                .SingleOrDefaultAsync();
        }

        /// <summary>Збережені значення колонки за періодами (усі екземпляри документа).</summary>
        public async Task<List<(int Period, decimal Value)>> StoredAsync(int columnDefId)
        {
            await using var db = builder.CreateContext();

            var rows = await (
                    from c in db.CellValues.AsNoTracking()
                    join r in db.TableRows.AsNoTracking() on new { c.PeriodKeyValue, Id = c.TableRowId } equals new { r.PeriodKeyValue, r.Id }
                    join i in db.TableInstances.AsNoTracking() on new { r.PeriodKeyValue, Id = r.TableInstanceId } equals new { i.PeriodKeyValue, i.Id }
                    where i.DocumentId == doc.DocumentId && c.ColumnDefId == columnDefId && c.ValueNumeric != null
                    select new { c.PeriodKeyValue, c.ValueNumeric })
                .ToListAsync();

            return [.. rows.Select(r => (r.PeriodKeyValue, r.ValueNumeric!.Value)).OrderBy(r => r.PeriodKeyValue)];
        }

        /// <summary>
        /// Відбиток запису в документ: версії всіх рядків документа (будь-який запис комірки «торкає» рядок) і
        /// кількість рядків журналу комірок.
        /// </summary>
        public async Task<string> CellWritesAsync()
        {
            await using var db = builder.CreateContext();

            var versions = await (
                    from r in db.TableRows.AsNoTracking()
                    join i in db.TableInstances.AsNoTracking() on new { r.PeriodKeyValue, Id = r.TableInstanceId } equals new { i.PeriodKeyValue, i.Id }
                    where i.DocumentId == doc.DocumentId
                    orderby r.PeriodKeyValue, r.Id
                    select r.RowVersion)
                .ToListAsync();

            var audit = await db.Database
                .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM aud.CellChange WHERE DocumentId = {doc.DocumentId}")
                .SingleAsync();

            return string.Join(",", versions.Select(Convert.ToBase64String)) + "|" + audit.ToString(CultureInfo.InvariantCulture);
        }

        public async Task<List<DateTime>> RawTimestampsAsync()
        {
            await using var db = builder.CreateContext();

            return await db.RawDataPoints.AsNoTracking()
                .Where(p => p.SourceEntityId == entityId && p.SourcePath == Flow)
                .OrderBy(p => p.Timestamp)
                .Select(p => p.Timestamp)
                .ToListAsync();
        }

        public async Task<long> RawRunIdAsync(DateTime timestamp)
        {
            await using var db = builder.CreateContext();

            return await db.RawDataPoints.AsNoTracking()
                .Where(p => p.SourceEntityId == entityId && p.SourcePath == Flow && p.Timestamp == timestamp)
                .Select(p => p.CollectionRunId)
                .SingleAsync();
        }

        /// <summary>EMISSION актуального прогону — так, як його бачить користувач (<c>calculation-results</c>).</summary>
        public async Task<Emission> EmissionAsync()
        {
            var response = await client.GetAsync(new Uri(
                $"/api/v1/documents/{doc.DocumentId}/calculation-results?periodKey={doc.PeriodKey.Value}", UriKind.Relative));
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"результати: {response.StatusCode}: {body}: {factory.ErrorsText}");

            var emission = JsonDocument.Parse(body).RootElement.EnumerateArray().FirstOrDefault(
                r => string.Equals(r.GetProperty("outputCode").GetString(), "EMISSION", StringComparison.Ordinal));
            Assert.True(emission.ValueKind == JsonValueKind.Object, $"немає EMISSION: {body}; {factory.ErrorsText}");

            await using var db = builder.CreateContext();
            var runId = await db.CalculationResults.AsNoTracking()
                .Where(r => r.DocumentId == doc.DocumentId && r.PeriodKey == doc.PeriodKey.Value && r.OutputCode == "EMISSION")
                .OrderByDescending(r => r.CalculationRunId)
                .Select(r => r.CalculationRunId)
                .FirstAsync();

            return new Emission(JsonNumber.AsDecimal(emission.GetProperty("value")), runId);
        }

        public async Task HumanEditAsync(string columnCode, decimal value)
        {
            var response = await client.PatchAsJsonAsync(
                new Uri($"/api/v1/documents/{doc.DocumentId}/cells", UriKind.Relative),
                new
                {
                    tableInstanceId = doc.TableInstanceId,
                    periodKey = doc.PeriodKey.Value,
                    origin = "UserEdit",
                    rows = new[]
                    {
                        new
                        {
                            rowKey,
                            baseVersion = await RowVersionAsync(),
                            cells = new object[] { new { columnCode, value } },
                        },
                    },
                });
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"правка людини: {response.StatusCode}: {await response.Content.ReadAsStringAsync()}: {factory.ErrorsText}");
            await PumpAsync();
        }

        /// <summary>Методологія через API (як S-24): EF = 2.5, EMISSION = @A × CST.EF, прив'язка до EMISSION, публікація.</summary>
        public async Task ArrangeMethodologyAsync()
        {
            var unitId = (await GetJsonAsync("/api/v1/units")).EnumerateArray()
                .First(u => string.Equals(u.GetProperty("code").GetString(), "t", StringComparison.Ordinal))
                .GetProperty("id").GetInt32();

            var methodology = await SendAsync(HttpMethod.Post, "/api/v1/methodologies", new
            {
                code = $"PIE_{Guid.NewGuid():N}"[..20],
                nameL10n = new Dictionary<string, string> { ["en"] = "D-462 e2e" },
                kind = "DataDriven",
                group = (string?)null,
            });
            var methodologyId = methodology.GetProperty("id").GetInt32();
            var root = $"/api/v1/methodologies/{methodologyId}";

            var versionId = (await SendAsync(HttpMethod.Post, $"{root}/versions", new
            {
                versionNumber = "1.0.0",
                copyFromVersionId = (int?)null,
                level = "Configuration",
            })).GetProperty("id").GetInt32();
            var version = $"{root}/versions/{versionId}";

            await SendAsync(HttpMethod.Put, $"{version}/constants/EF", new
            {
                kind = "Numeric",
                value = 2.5m,
                unitId,
                textValue = (string?)null,
                validFrom = (string?)null,
                validTo = (string?)null,
                category = (string?)null,
                substanceEntryId = (long?)null,
                source = "D-462 e2e",
            });
            await SendAsync(HttpMethod.Put, $"{version}/formulas/EMISSION", new
            {
                expression = $"@{ColumnACode} * CST.EF",
                resultType = "Number",
                outputUnitId = unitId,
                argumentsCsv = ColumnACode,
            });
            await SendAsync(HttpMethod.Put, $"{version}/outputs/EMISSION", new { unitId, ordinal = 1 });
            await SendAsync(HttpMethod.Put, $"{version}/rules/ALL_ROWS", new { matchJson = "{}", priority = 1, isActive = true });

            var input = string.Create(
                CultureInfo.InvariantCulture,
                $$"""
                  {"documentId":{{doc.DocumentId}},"tableInstanceId":{{doc.TableInstanceId}},
                   "periodKey":{"value":{{doc.PeriodKey.Value}}},"sourceRowKey":"{{rowKey}}",
                   "arguments":[{"argumentCode":"{{ColumnACode}}","value":4,"valueString":null,"unitId":null}]}
                  """);
            await SendAsync(HttpMethod.Put, $"{version}/tests/GOLDEN", new
            {
                inputJson = input,
                expectedJson = """{"EMISSION":10}""",
                tolerance = 0.0001m,
            });
            await SendAsync(HttpMethod.Put, $"{root}/bindings/{emissionColumn}/EMISSION", new { matchJson = "{}", isActive = true });
            await SendAsync(HttpMethod.Post, $"{version}/publish", new
            {
                changeReason = "D-462 e2e",
                effectiveFrom = "2025-01-01",
            });
        }

        /// <summary>Знімає сутність з активних: стенд не має фарбувати здоров'я джерел для сусідніх тестів.</summary>
        public async Task DeactivateAsync()
        {
            await using var db = builder.CreateContext();
            await db.SourceEntities.Where(e => e.Id == entityId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsActive, false));
        }

        public void Dispose()
        {
            client.Dispose();
            App.Dispose();
            factory.Dispose();
        }

        private async Task<string?> RowVersionAsync()
        {
            await using var db = builder.CreateContext();
            var version = await db.TableRows.AsNoTracking()
                .Where(r => r.PeriodKeyValue == doc.PeriodKey.Value && r.Id == doc.RowIds[0])
                .Select(r => r.RowVersion)
                .SingleAsync();
            return Convert.ToBase64String(version);
        }

        /// <summary>Виконує постановки, що з'явилися після попереднього прогону, доки нові не скінчаться.</summary>
        private async Task PumpAsync()
        {
            for (var guard = 0; guard < 50; guard++)
            {
                var calls = EnqueueCalls();
                if (pumped >= calls.Count)
                {
                    return;
                }

                var (type, payload) = calls[pumped++];
                if (type == typeof(ICollectionJob) || type == typeof(IMaterializeCollectedDataJob)
                    || type == typeof(IRecalculationJob) || type == typeof(IFormulaRecalculationJob))
                {
                    await RunJobAsync(type, payload);
                }
            }

            Assert.Fail("черга задач не вичерпалася за 50 кроків");
        }

        private async Task RunJobAsync(Type type, object? payload)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var job = (IBackgroundJob)scope.ServiceProvider.GetRequiredService(type);
            await job.ExecuteAsync(payload, NoProgress.Instance, CancellationToken.None);
        }

        /// <summary>Усі постановки в чергу в порядку викликів: тип задачі і payload.</summary>
        private List<(Type Type, object? Payload)> EnqueueCalls()
            => [.. jobs.ReceivedCalls()
                .Where(c => c.GetMethodInfo().Name is "EnqueueAsync" or "EnqueueExclusiveAsync" or "EnqueueCoalescedAsync")
                .Select(c => (
                    c.GetMethodInfo().GetGenericArguments()[0],
                    c.GetMethodInfo().Name == "EnqueueAsync" ? c.GetArguments()[0] : c.GetArguments()[1]))];

        private async Task<JsonElement> GetJsonAsync(string url)
        {
            var response = await client.GetAsync(new Uri(url, UriKind.Relative));
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{url}: {response.StatusCode}: {body}: {factory.ErrorsText}");
            return JsonDocument.Parse(body).RootElement.Clone();
        }

        private async Task<JsonElement> SendAsync(HttpMethod method, string url, object body)
        {
            using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative)) { Content = JsonContent.Create(body) };
            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{method} {url}: {response.StatusCode}: {text}: {factory.ErrorsText}");
            return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
        }
    }

    /// <summary>Прогрес без запису.</summary>
    private sealed class NoProgress : IJobProgress
    {
        public static readonly NoProgress Instance = new();

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// PI в пам'яті: атрибут → точки за міткою. Поводиться як адаптери PI: <c>[From, To)</c>, не більше
    /// <c>MaxPoints</c>, повний батч — хвіст від мітки останньої точки.
    /// </summary>
    private sealed class FakePi : IExternalDataSource
    {
        private readonly Dictionary<string, SortedDictionary<DateTime, decimal>> points = new(StringComparer.Ordinal);

        public ExternalTransport Transport => ExternalTransport.PiSqlClient;

        public void Set(string path, DateTime at, decimal value)
        {
            lock (points)
            {
                if (!points.TryGetValue(path, out var series))
                {
                    points[path] = series = [];
                }

                series[at] = value;
            }
        }

        public void Remove(string path, DateTime at)
        {
            lock (points)
            {
                points[path].Remove(at);
            }
        }

        public Task<IReadOnlyList<SourceEntityDescriptor>> DiscoverAsync(int dataSourceId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SourceEntityDescriptor>>([]);

        public Task<CollectionResult> ReadAsync(CollectionRequest request, CancellationToken ct)
        {
            List<SourceDataPoint> page;

            lock (points)
            {
                page = points.TryGetValue(request.SourcePath, out var series)
                    ? [.. series
                        .Where(p => p.Key >= request.FromUtc && p.Key < request.ToUtc)
                        .Take(request.MaxPoints)
                        .Select(p => new SourceDataPoint(request.SourcePath, p.Key, p.Value, null, null, "Good"))]
                    : [];
            }

            var truncated = page.Count > 0 && page.Count >= request.MaxPoints;

            return Task.FromResult(new CollectionResult(
                page, truncated ? [new TimeInterval(page[^1].Timestamp, request.ToUtc)] : [], null));
        }
    }
}
