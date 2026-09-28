using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Ecr.Application.Common;
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
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>P3</c> (перф-аудит шляху запису): звернень до БД на одне подання аркуша
/// не більшає з кількістю таблиць документа.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Що було: <c>SubmitSheetHandler</c> читав зріз (<c>ReadSliceAsync</c>) і
/// рядки (<c>GetRowIdsAsync</c>) ПО ОДНОМУ екземпляру таблиці — і у валідації,
/// і в знімку подання, — під винятковим блокуванням аркуша
/// (<c>ISheetEditGate.EnterSubmitAsync</c>). На документі з ~90 таблицями —
/// ~90+ послідовних походів у базу, поки правки аркуша чекають.
/// </para>
/// <para>
/// ⚠ Лічильник — <see cref="SqlClientCommandCounter"/> (бачить і сирі команди
/// повз EF) з <c>AsyncLocal</c>-фільтром: зараховується лише те, що виконало
/// саме подання, а не сусідні тести процесу.
/// </para>
/// <para>
/// ⚠ Обсяг храповика — клітинки й рядки. <c>IMethodologyStore</c> тут
/// підставний: перевірка прив'язок методологій (<c>GetMethodologyIdsBoundToTableAsync</c>
/// у циклі по таблицях аркуша) має власний N+1, і пакетного методу в порту
/// для нього немає — окрема задача.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class SubmitSheetQueryCountTests(SqlServerFixture sql)
{
    private const int UserId = 1;

    private const int RowsPerTable = 3;

    private const int ColumnsPerTable = 3;

    /// <summary>Стеля звернень до БД на одне подання — незалежно від кількості таблиць.</summary>
    /// <remarks>
    /// ⛔ Лише знижується. Точка відліку — замір на коміті P3: 14 на 3 і на 12
    /// таблицях. До P3 (виміряно тим самим тестом): 3 таблиці — 20, 12 таблиць —
    /// 47, тобто 11 + 3N (зріз і рядки на таблицю у валідації, зріз на таблицю
    /// в знімку).
    /// </remarks>
    private const int MaxCommands = 14;

    private static readonly AsyncLocal<StrongBox<bool>?> Measuring = new();

    private static int _counter;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "P3")]
    public async Task Подання_аркуша_робить_стільки_ж_звернень_до_БД_на_3_і_на_12_таблицях()
    {
        // Розігрів: перше подання в процесі платить за модель EF і кеш планів.
        var warm = await ArrangeAsync(tableCount: 1);
        await SubmitMeasuredAsync(warm);

        var small = await ArrangeAsync(tableCount: 3);
        var large = await ArrangeAsync(tableCount: 12);

        var (smallCount, smallDetail) = await SubmitMeasuredAsync(small);
        var (largeCount, largeDetail) = await SubmitMeasuredAsync(large);

        // ⛔ Подання справді зняло всі клітинки — інакше «мало звернень»
        // доводило б лише, що зріз порожній.
        Assert.Equal(12 * RowsPerTable * ColumnsPerTable, (await SubmittedAsync(large)).Cells.Count);

        Assert.True(
            largeCount == smallCount,
            $"Подання: 3 таблиці — {smallCount} звернень, 12 таблиць — {largeCount}. "
            + "Кількість звернень росте з кількістю таблиць (N+1 під винятковим блокуванням аркуша).\n"
            + $"— 3 таблиці —\n{smallDetail}\n— 12 таблиць —\n{largeDetail}");

        Assert.True(
            largeCount <= MaxCommands,
            $"Подання: {largeCount} звернень до БД, стеля {MaxCommands}.\n{largeDetail}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "P3")]
    public async Task Зріз_подання_й_ContentHash_ті_самі_що_з_поштучного_читання_кожної_таблиці()
    {
        var doc = await ArrangeAsync(tableCount: 5);

        // Очікуване — рівно так, як рахував обробник до P3: поштучний
        // `ReadSliceAsync` кожного екземпляра, фільтр за періодом, той самий
        // серіалізатор і той самий SHA-256.
        string expected;
        await using (var db = CreateContext())
        {
            var bulk = new BulkCellLoader(sql.ConnectionString, 1000);
            var rows = new RowStore(db, bulk, new FixedClock(DateTime.UtcNow));
            var cells = new NormalizedCellStore(db);
            var instances = await rows.GetTableInstancesAsync(doc.DocumentId, doc.PeriodKey, CancellationToken.None);
            Assert.Equal(5, instances.Count);

            var all = new List<CellRecord>();
            foreach (var instance in instances)
            {
                all.AddRange(await cells.ReadSliceAsync(instance.TableInstanceId, CancellationToken.None));
            }

            expected = SubmissionPayload.Write(all.Where(c => c.Address.PeriodKey.Value == doc.PeriodKey.Value));
        }

        await SubmitMeasuredAsync(doc);
        var (payload, hash, cellsRead) = await SubmittedAsync(doc);

        Assert.Equal(5 * RowsPerTable * ColumnsPerTable, cellsRead.Count);
        Assert.Equal(expected, payload);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(expected))), hash);
    }

    /// <summary>Одне подання під лічильником; повертає число звернень і їх розклад.</summary>
    private async Task<(int Total, string Detail)> SubmitMeasuredAsync(Scenario doc)
    {
        await using var db = CreateContext();
        var handler = BuildSubmit(db, doc);

        var texts = new List<string>();
        using var counter = new SqlClientCommandCounter(new CommandTally(), command =>
        {
            if (Measuring.Value is not { Value: true })
            {
                return false;
            }

            lock (texts)
            {
                texts.Add(command.CommandText ?? string.Empty);
            }

            return true;
        });

        counter.Tally.Reset();
        var flag = new StrongBox<bool>(true);
        Measuring.Value = flag;
        try
        {
            await handler.HandleAsync(doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value, CancellationToken.None);
        }
        finally
        {
            flag.Value = false;
            Measuring.Value = null;
        }

        counter.AssertObserved();
        var seen = counter.Tally.Snapshot();

        var detail = new StringBuilder(seen.Format());
        var i = 0;
        foreach (var text in texts)
        {
            var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            detail.AppendLine(CultureInfo.InvariantCulture, $"  {++i,2}. {(flat.Length > 160 ? flat[..160] + "…" : flat)}");
        }

        return (seen.Total, detail.ToString());
    }

    private async Task<(string Payload, string Hash, IReadOnlyList<SubmissionPayloadCell> Cells)> SubmittedAsync(Scenario doc)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT TOP (1) PayloadJson, ContentHash FROM calc.SubmissionSnapshot "
            + $"WHERE DocumentId = {doc.DocumentId} AND SheetDefId = {doc.SheetDefId} "
            + $"AND PeriodKey = {doc.PeriodKey.Value} ORDER BY Id DESC";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "Зрізу подання немає.");
        var payload = reader.GetString(0);
        // `ContentHash` у базі — varbinary; обробник рахує його hex-рядком.
        var hash = Convert.ToHexStringLower((byte[])reader.GetValue(1));
        return (payload, hash, SubmissionPayload.Read(payload));
    }

    /// <summary>
    /// Документ з <paramref name="tableCount"/> таблицями одного аркуша; у кожній
    /// перша колонка обов'язкова (щоб валідація читала кожну таблицю), усі
    /// клітинки заповнені (щоб подання пройшло).
    /// </summary>
    private async Task<Scenario> ArrangeAsync(int tableCount)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: ColumnsPerTable, rowCount: RowsPerTable);
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);

        var tables = new List<(int TableDefId, IReadOnlyList<int> ColumnIds, IReadOnlyList<long> RowIds)>
        {
            (doc.TableDefId, doc.ColumnDefIds, doc.RowIds),
        };

        await using (var db = builder.CreateContext())
        {
            for (var t = 1; t < tableCount; t++)
            {
                var tag = $"{Interlocked.Increment(ref _counter)}X{Guid.NewGuid():N}"[..12].ToUpperInvariant();
                var table = new TableDef(
                    doc.SheetDefId, EcrCode.Create($"TQ{tag}"), Name($"Table {tag}"), t + 1,
                    TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
                db.TableDefs.Add(table);
                await db.SaveChangesAsync();

                var columns = new List<ColumnDef>();
                for (var c = 1; c <= ColumnsPerTable; c++)
                {
                    var column = new ColumnDef(table.Id, EcrCode.Create($"Q{c}_{tag}"), Name($"Col {c}"), c, CellDataType.Decimal);
                    columns.Add(column);
                    db.ColumnDefs.Add(column);
                }

                await db.SaveChangesAsync();

                var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
                var firstRowId = await loader.ReserveIdsAsync("doc.TableRowSeq", RowsPerTable, CancellationToken.None);
                db.TableInstances.Add(new TableInstance(doc.PeriodKey, instanceId, doc.DocumentId, table.Id, now));

                var rowIds = new List<long>();
                for (var r = 0; r < RowsPerTable; r++)
                {
                    db.TableRows.Add(new TableRow(
                        doc.PeriodKey, firstRowId + r, instanceId, RowKey.Create($"R{r + 1}_{tag}"), r + 1, now));
                    rowIds.Add(firstRowId + r);
                }

                await db.SaveChangesAsync();
                tables.Add((table.Id, [.. columns.Select(c => c.Id)], rowIds));
            }
        }

        var values = new StringBuilder();
        var n = 0;
        foreach (var (tableDefId, columnIds, rowIds) in tables)
        {
            foreach (var rowId in rowIds)
            {
                foreach (var columnId in columnIds)
                {
                    values.Append(values.Length == 0 ? string.Empty : ", ")
                          .Append(CultureInfo.InvariantCulture,
                              $"({doc.PeriodKey.Value}, {rowId}, {columnId}, {tableDefId}, {++n}.25, 0, 0)");
                }
            }
        }

        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty) VALUES "
                + values;
            await command.ExecuteNonQueryAsync();
        }

        return new Scenario(doc, tables);
    }

    private static SubmitSheetHandler BuildSubmit(EcrDbContext db, Scenario doc)
    {
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);
        var clock = new FixedClock(new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc));

        var documents = Substitute.For<IDocumentStore>();
        documents.HasSheetAsync(doc.DocumentId, doc.SheetDefId, Arg.Any<CancellationToken>()).Returns(true);
        documents.FindProjectIdAsync(doc.DocumentId, Arg.Any<CancellationToken>()).Returns(doc.ProjectId);

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(Profile());
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());
        access.CanSubmitAsync(
                  Arg.Any<AccessProfile>(), doc.DocumentId, doc.SheetDefId, Arg.Any<PeriodKey>(),
                  Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());
        access.CurrentApprovalStepAsync(doc.DocumentId, doc.SheetDefId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
              .Returns((ApprovalStepView?)null);

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

        var metadata = Metadata(doc);

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

    /// <summary>Знімок структури з реальними ідентифікаторами; перша колонка кожної таблиці обов'язкова.</summary>
    private static IMetadataCache Metadata(Scenario doc)
    {
        var sheet = new SheetDef(doc.TemplateVersionId, EcrCode.Create($"SH{doc.SheetDefId}"), Name("Sheet"), 1);
        SetId(sheet, doc.SheetDefId);

        var columnsById = new Dictionary<int, ColumnDef>();
        var ordinal = 0;
        foreach (var (tableDefId, columnIds, _) in doc.Tables)
        {
            var table = new TableDef(doc.SheetDefId, EcrCode.Create($"TB{tableDefId}"), Name("Table"), ++ordinal,
                TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
            SetId(table, tableDefId);

            for (var c = 0; c < columnIds.Count; c++)
            {
                var column = new ColumnDef(tableDefId, EcrCode.Create($"C{columnIds[c]}"), Name("Col"), c + 1, CellDataType.Decimal);
                SetId(column, columnIds[c]);
                column.SetRequired(c == 0);
                table.AddColumn(column);
                columnsById[column.Id] = column;
            }

            sheet.AddTable(table);
        }

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId: doc.TemplateVersionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: columnsById,
            RowsByKey: new Dictionary<(int, string), RowDef>());

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);
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

    private sealed record Scenario(
        TestDocument Document,
        IReadOnlyList<(int TableDefId, IReadOnlyList<int> ColumnIds, IReadOnlyList<long> RowIds)> Tables)
    {
        public long DocumentId => Document.DocumentId;

        public int SheetDefId => Document.SheetDefId;

        public int TemplateVersionId => Document.TemplateVersionId;

        public int ProjectId => Document.ProjectId;

        public PeriodKey PeriodKey => Document.PeriodKey;
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
