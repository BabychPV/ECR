using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Application.Reporting;
using Ecr.Application.Security;
using Ecr.Application.Workflow;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Порядок блокувальних повідомлень у тілі 422 подання аркуша — детермінований:
/// таблиця (порядок на аркуші) → рядок (порядок створення) → колонка (порядок
/// на екрані).
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Що було: <c>MissingRequiredColumnMessages</c> перебирав словник рядків у
/// порядку, в якому їх повернула база (без <c>ORDER BY</c>), а таблиці йшли за
/// <c>TableInstance.Id</c> — порядком створення екземплярів, а не таблиць на
/// аркуші. Колонки — за порядком у списку знімка, а не за <c>Ordinal</c>.
/// </para>
/// <para>
/// ⚠ Дані зібрані так, щоб кожен із цих «природних» порядків РОЗХОДИВСЯ з
/// очікуваним: таблиці створюються у зворотному до <c>TableDef.Ordinal</c>
/// порядку, колонки лежать у знімку у зворотному до <c>ColumnDef.Ordinal</c>
/// порядку, а ключі рядків ідуть рядково у зворотному до <c>TableRow.Id</c>
/// порядку (сортування за <c>RowKey</c> теж дало б хибне).
/// </para>
/// <para>
/// ⚠ Рядки — за ПОРЯДКОМ СТВОРЕННЯ (<c>TableRow.Id</c>), а не «як на екрані»:
/// екранний <c>TableRow.Ordinal</c> пакетно жоден порт не віддає (наближення,
/// названий борг у <c>SubmitSheetHandler.OrderAsOnScreen</c>).
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class SubmitBlockedMessageOrderTests(SqlServerFixture sql)
{
    private const int UserId = 1;

    private const int TableCount = 3;

    private const int RowsPerTable = 3;

    private const int ColumnsPerTable = 3;

    /// <summary>Скільки перших за <c>Ordinal</c> колонок кожної таблиці обов'язкові.</summary>
    private const int RequiredColumns = 2;

    private static int _counter;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "P3")]
    public async Task Два_подання_на_тих_самих_даних_дають_однаковий_порядок_повідомлень()
    {
        var doc = await ArrangeAsync();

        var first = await BlockedMessagesAsync(doc);
        var second = await BlockedMessagesAsync(doc);

        Assert.Equal(TableCount * RowsPerTable * RequiredColumns, first.Count);
        Assert.Equal(first, second);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "P3")]
    public async Task Порядок_повідомлень_таблиця_аркуша_порядок_створення_рядків_колонка_екрана()
    {
        var doc = await ArrangeAsync();

        // Очікуване будується явно, не з обробника: таблиці за Ordinal на
        // аркуші, рядки за порядком створення (TableRow.Id), колонки за Ordinal.
        var expected = doc.Tables
            .OrderBy(t => t.Ordinal)
            .SelectMany(t => t.Rows
                .OrderBy(r => r.Id)
                .SelectMany(r => t.Columns
                    .Where(c => c.Required)
                    .OrderBy(c => c.Ordinal)
                    .Select(c => (RowKey: (string?)r.Key, ColumnCode: (string?)c.Code))))
            .ToList();

        var actual = await BlockedMessagesAsync(doc);

        Assert.True(
            expected.SequenceEqual(actual),
            "Порядок повідомлень 422 не «таблиця аркуша → порядок створення рядків → колонка екрана».\n"
            + $"Очікувано:\n  {string.Join("\n  ", expected)}\nФактично:\n  {string.Join("\n  ", actual)}");
    }

    /// <summary>Подає аркуш і повертає (RowKey, ColumnCode) повідомлень із тіла 422 у їхньому порядку.</summary>
    private async Task<List<(string? RowKey, string? ColumnCode)>> BlockedMessagesAsync(Scenario doc)
    {
        await using var db = CreateContext();
        var handler = BuildSubmit(db, doc);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            handler.HandleAsync(doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value, CancellationToken.None));

        Assert.Equal("err.ECR-SUB-4221.validationBlocked", error.Details?["messageKey"]);
        var messages = Assert.IsAssignableFrom<System.Collections.IEnumerable>(error.Details?["messages"]);

        var result = new List<(string? RowKey, string? ColumnCode)>();
        foreach (var message in messages)
        {
            var type = message.GetType();
            Assert.Equal("ECR-CELL-0422", (string?)type.GetProperty("RuleCode")!.GetValue(message));
            result.Add((
                (string?)type.GetProperty("RowKey")!.GetValue(message),
                (string?)type.GetProperty("ColumnCode")!.GetValue(message)));
        }

        return result;
    }

    /// <summary>
    /// Документ з <see cref="TableCount"/> таблицями одного аркуша, без жодної
    /// клітинки: кожен рядок порушує кожну обов'язкову колонку.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: ColumnsPerTable, rowCount: RowsPerTable);
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);

        // Таблиці: [0] — із будівника, створена ПЕРШОЮ (найменший TableInstance.Id).
        var created = new List<(int TableDefId, IReadOnlyList<int> ColumnIds, IReadOnlyList<(long Id, string Key)> Rows)>();

        await using (var db = builder.CreateContext())
        {
            var builderRows = await db.TableRows.AsNoTracking()
                .Where(r => r.PeriodKeyValue == doc.PeriodKey.Value && r.TableInstanceId == doc.TableInstanceId)
                .Select(r => new { r.Id, r.RowKeyValue })
                .ToListAsync();
            created.Add((doc.TableDefId, doc.ColumnDefIds, [.. builderRows.Select(r => (r.Id, r.RowKeyValue))]));

            for (var t = 1; t < TableCount; t++)
            {
                var tag = $"{Interlocked.Increment(ref _counter)}O{Guid.NewGuid():N}"[..12].ToUpperInvariant();
                var table = new TableDef(
                    doc.SheetDefId, EcrCode.Create($"TO{tag}"), Name($"Table {tag}"), t + 1,
                    TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
                db.TableDefs.Add(table);
                await db.SaveChangesAsync();

                var columnIds = new List<int>();
                for (var c = 1; c <= ColumnsPerTable; c++)
                {
                    var column = new ColumnDef(table.Id, EcrCode.Create($"O{c}_{tag}"), Name($"Col {c}"), c, CellDataType.Decimal);
                    db.ColumnDefs.Add(column);
                    await db.SaveChangesAsync();
                    columnIds.Add(column.Id);
                }

                var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
                var firstRowId = await loader.ReserveIdsAsync("doc.TableRowSeq", RowsPerTable, CancellationToken.None);
                db.TableInstances.Add(new TableInstance(doc.PeriodKey, instanceId, doc.DocumentId, table.Id, now));

                // ⚠ Ключі рядків — рядково у ЗВОРОТНОМУ до Id порядку: сортування
                // за RowKey дало б інший порядок, ніж порядок створення.
                var rows = new List<(long Id, string Key)>();
                for (var r = 0; r < RowsPerTable; r++)
                {
                    var key = $"R{RowsPerTable - r}_{tag}";
                    db.TableRows.Add(new TableRow(doc.PeriodKey, firstRowId + r, instanceId, RowKey.Create(key), r + 1, now));
                    rows.Add((firstRowId + r, key));
                }

                await db.SaveChangesAsync();
                created.Add((table.Id, columnIds, rows));
            }
        }

        // ⚠ Ordinal на аркуші — ЗВОРОТНИЙ до порядку створення: перша створена
        // таблиця (найменший TableInstance.Id) стоїть на екрані останньою.
        var tables = created.Select((t, i) => new TableSpec(
            t.TableDefId,
            Ordinal: TableCount - i,
            Columns: [.. t.ColumnIds.Select((id, c) => new ColumnSpec(id, $"C{id}", Ordinal: c + 1, Required: c < RequiredColumns))],
            Rows: [.. t.Rows.Select(r => new RowSpec(r.Id, r.Key))])).ToList();

        return new Scenario(doc, tables);
    }

    private static SubmitSheetHandler BuildSubmit(EcrDbContext db, Scenario doc)
    {
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);
        var clock = new FixedClock(new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc));

        var documents = Substitute.For<IDocumentStore>();
        documents.HasSheetAsync(doc.DocumentId, doc.SheetDefId, Arg.Any<CancellationToken>()).Returns(true);
        documents.FindProjectIdAsync(doc.DocumentId, Arg.Any<CancellationToken>()).Returns(doc.Document.ProjectId);

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(Profile());
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());
        access.CanSubmitAsync(
                  Arg.Any<AccessProfile>(), doc.DocumentId, doc.SheetDefId, Arg.Any<PeriodKey>(),
                  Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());

        var snapshots = Substitute.For<IReportSnapshotBuilder>();
        snapshots.ListAsync(
                     Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<IReadOnlyCollection<int>?>(),
                     Arg.Any<CancellationToken>())
                 .Returns<IReadOnlyList<ReportSnapshotSummary>>([]);

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<string, ExpressionValue>());
        headers.GetValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<int, DocumentHeaderValueData>());

        var methodologies = Substitute.For<IMethodologyStore>();
        methodologies.GetMethodologyIdsBoundToTableAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                     .Returns(new List<int>());
        methodologies.GetMethodologyIdsBoundToTablesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                     .Returns(new List<int>());

        var metadata = Metadata(doc);

        // ⚠ S6: тіло 422 фільтрується межами читання подавача; тут він бачить усе.
        access.ReadScopeAsync(Arg.Any<AccessProfile>(), doc.DocumentId, Arg.Any<CancellationToken>())
              .Returns(async _ => ReadScopes.Everything(
                  await metadata.GetAsync(doc.Document.TemplateVersionId, CancellationToken.None)));

        return new SubmitSheetHandler(
            new NormalizedCellStore(db), new RowStore(db, bulk, clock), new WorkflowStore(db), documents,
            metadata, access,
            new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
            headers,
            new ReportSnapshotSync(snapshots, documents),
            new UnitOfWork(db), User(), clock, new SheetEditGate(db),
            Recalculation(db, doc, metadata, clock),
            methodologies,
            new TemplateVersionStore(db),
            new RegistryStore(db));
    }

    /// <summary>Перерахунок аркуша на реальних сховищах (формул у знімку немає).</summary>
    private static RecalculationService Recalculation(EcrDbContext db, Scenario doc, IMetadataCache metadata, IClock clock)
    {
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(doc.DocumentId, doc.PeriodKey.Value, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));
        periods.FindPeriodStateAsync(doc.DocumentId, doc.PeriodKey.Value, Arg.Any<CancellationToken>())
               .Returns((PeriodState?)PeriodState.Open);

        var versions = Substitute.For<ITemplateVersionStore>();
        versions.ListFormulaDependenciesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<string, ExpressionValue>());

        return new RecalculationService(
            new NormalizedCellStore(db), new RowStore(db, bulk, clock), periods, metadata, versions,
            new RealFormulaEngine(), units, Substitute.For<IRegistryStore>(), headers,
            new AuditWriter(db), clock, new UnitOfWork(db), new SheetEditGate(db));
    }

    /// <summary>
    /// Знімок структури: таблиці з Ordinal зі сценарію; колонки кожної таблиці
    /// додані в список у ЗВОРОТНОМУ до Ordinal порядку.
    /// </summary>
    private static IMetadataCache Metadata(Scenario doc)
    {
        var sheet = new SheetDef(doc.Document.TemplateVersionId, EcrCode.Create($"SH{doc.SheetDefId}"), Name("Sheet"), 1);
        SetId(sheet, doc.SheetDefId);

        var columnsById = new Dictionary<int, ColumnDef>();
        foreach (var spec in doc.Tables)
        {
            var table = new TableDef(doc.SheetDefId, EcrCode.Create($"TB{spec.TableDefId}"), Name("Table"), spec.Ordinal,
                TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
            SetId(table, spec.TableDefId);

            foreach (var columnSpec in spec.Columns.OrderByDescending(c => c.Ordinal))
            {
                var column = new ColumnDef(spec.TableDefId, EcrCode.Create(columnSpec.Code), Name("Col"), columnSpec.Ordinal, CellDataType.Decimal);
                SetId(column, columnSpec.Id);
                column.SetRequired(columnSpec.Required);
                table.AddColumn(column);
                columnsById[column.Id] = column;
            }

            sheet.AddTable(table);
        }

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId: doc.Document.TemplateVersionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: columnsById,
            RowsByKey: new Dictionary<(int, string), RowDef>());

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(doc.Document.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);
        return metadata;
    }

    private static void SetId(object entity, int id)
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p", UserId = UserId, SecurityStamp = "s",
        Permissions = new HashSet<string>(), Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
    };

    private static ICurrentUser User()
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);
        return user;
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private sealed record ColumnSpec(int Id, string Code, int Ordinal, bool Required);

    private sealed record RowSpec(long Id, string Key);

    private sealed record TableSpec(int TableDefId, int Ordinal, IReadOnlyList<ColumnSpec> Columns, IReadOnlyList<RowSpec> Rows);

    private sealed record Scenario(TestDocument Document, IReadOnlyList<TableSpec> Tables)
    {
        public long DocumentId => Document.DocumentId;

        public int SheetDefId => Document.SheetDefId;

        public PeriodKey PeriodKey => Document.PeriodKey;
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
