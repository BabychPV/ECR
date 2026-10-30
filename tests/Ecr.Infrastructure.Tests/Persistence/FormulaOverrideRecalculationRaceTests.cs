using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
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
/// ✎ D-461 на справжньому SQL Server: перерахунок НІКОЛИ не перезаписує ручне значення замість формули — ні наявне
/// (позначка прочитана на старті прогону), ні поставлене ПАРАЛЕЛЬНО з прогоном (позначки адрес запису блокуються
/// перед MERGE: <c>IFormulaOverrideStore.LockKeysAsync</c>; правка пише позначку ДО значення).
/// </summary>
/// <remarks>
/// Мутації (локально, не комітяться): у <c>RecalculationService.IsManualTarget</c> не дивитися на <c>overridden</c> —
/// червоніє перший тест (OUT = 20 замість 999); у <c>WriteBatchAsync</c> прибрати <c>LockKeysAsync</c> — червоніє
/// детермінована гонка (прогін, що прочитав позначки до правки, пише обчислене поверх щойно закоміченого ручного).
/// Випадкова гонка (20 ітерацій) таку мутацію ловить не завжди — вікно вузьке; вона стереже відсутність дедлоків і
/// загублених значень у реальному паралелізмі.
/// </remarks>
[Collection("SqlServer")]
public sealed class FormulaOverrideRecalculationRaceTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc);
    private const int FormulaId = 920_461;
    private const decimal Manual = 999m;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-461")]
    public async Task Повний_прогін_не_перезаписує_ручне_значення_а_решту_рядків_рахує()
    {
        var doc = await ArrangeAsync();
        await SetManualAsync(doc, doc.RowIds[0], useLockingOrder: false);

        await using (var db = CreateContext())
        {
            await Service(db, doc).RecalculateAllAsync(doc.DocumentId, doc.PeriodKey, CancellationToken.None);
        }

        Assert.Equal(Manual, await OutputAsync(doc, doc.RowIds[0]));
        Assert.Equal(40m, await OutputAsync(doc, doc.RowIds[1]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-461")]
    public async Task Ручне_значення_паралельне_з_перерахунком_не_перезаписується()
    {
        var doc = await ArrangeAsync();

        for (var i = 0; i < 20; i++)
        {
            await ResetAsync(doc);

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var recalc = Task.Run(async () =>
            {
                await start.Task;
                await using var db = CreateContext();
                await Service(db, doc).RecalculateAllAsync(doc.DocumentId, doc.PeriodKey, CancellationToken.None);
            });
            var edit = Task.Run(async () =>
            {
                await start.Task;
                await SetManualAsync(doc, doc.RowIds[0], useLockingOrder: true);
            });

            start.SetResult();
            await Task.WhenAll(recalc, edit);

            Assert.True(
                await OutputAsync(doc, doc.RowIds[0]) == Manual,
                $"Ітерація {i}: перерахунок перезаписав ручне значення ({await OutputAsync(doc, doc.RowIds[0])}).");
            Assert.Equal(40m, await OutputAsync(doc, doc.RowIds[1]));
        }
    }

    /// <summary>
    /// Детермінована гонка: прогін уже прочитав позначки (ручного значення ще немає), правка ставить ручне значення й
    /// КОМІТИТЬСЯ, і лише тоді прогін пише. Без блокування позначок перед MERGE прогін перезаписав би ручне обчисленим.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-461")]
    public async Task Ручне_значення_між_читанням_позначок_і_записом_прогону_не_перезаписується()
    {
        var doc = await ArrangeAsync();
        var gate = new GatedStore();

        var recalc = Task.Run(async () =>
        {
            await using var db = CreateContext();
            gate.Inner = new FormulaOverrideStore(db);
            await Service(db, doc, gate).RecalculateAllAsync(doc.DocumentId, doc.PeriodKey, CancellationToken.None);
        });

        await gate.ReadDone.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await SetManualAsync(doc, doc.RowIds[0], useLockingOrder: true);
        gate.Proceed.SetResult();
        await recalc.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(Manual, await OutputAsync(doc, doc.RowIds[0]));
        Assert.Equal(40m, await OutputAsync(doc, doc.RowIds[1]));
    }

    /// <summary>Сховище, що зупиняє прогін одразу після читання позначок на старті.</summary>
    private sealed class GatedStore : IFormulaOverrideStore
    {
        public FormulaOverrideStore Inner { get; set; } = null!;
        public TaskCompletionSource ReadDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Proceed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlySet<CellAddress>> ReadKeysAsync(
            IReadOnlyCollection<long> tableInstanceIds, PeriodKey periodKey, CancellationToken ct)
        {
            var keys = await Inner.ReadKeysAsync(tableInstanceIds, periodKey, ct);
            ReadDone.TrySetResult();
            await Proceed.Task.WaitAsync(ct);
            return keys;
        }

        public Task<IReadOnlySet<CellAddress>> LockKeysAsync(IReadOnlyCollection<CellAddress> addresses, CancellationToken ct)
            => Inner.LockKeysAsync(addresses, ct);

        public Task<FormulaOverrideTarget?> FindFormulaAsync(int templateVersionId, int formulaId, CancellationToken ct)
            => Inner.FindFormulaAsync(templateVersionId, formulaId, ct);

        public Task SetAllowManualOverrideAsync(int formulaId, bool allow, CancellationToken ct)
            => Inner.SetAllowManualOverrideAsync(formulaId, allow, ct);

        public Task<FormulaOverrideCounts> CountAsync(FormulaOverrideTarget target, CancellationToken ct)
            => Inner.CountAsync(target, ct);

        public Task<IReadOnlyList<FormulaOverrideMark>> ReadInstanceAsync(long tableInstanceId, PeriodKey periodKey, CancellationToken ct)
            => Inner.ReadInstanceAsync(tableInstanceId, periodKey, ct);

        public Task<IReadOnlyDictionary<CellAddress, CellFormulaOverride>> ReadAsync(
            IReadOnlyCollection<CellAddress> addresses, CancellationToken ct)
            => Inner.ReadAsync(addresses, ct);

        public Task<IReadOnlyList<ProjectFormulaOverride>> ReadProjectAsync(int projectId, CancellationToken ct)
            => Inner.ReadProjectAsync(projectId, ct);

        public Task ReleaseAsync(IReadOnlyList<CellAddress> addresses, CancellationToken ct)
            => Inner.ReleaseAsync(addresses, ct);

        public Task ApplyAsync(IReadOnlyList<CellFormulaOverride> upserts, IReadOnlyList<CellAddress> removals, CancellationToken ct)
            => Inner.ApplyAsync(upserts, removals, ct);
    }

    private async Task<TestDocument> ArrangeAsync()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(columnCount: 3, rowCount: 2, rowMode: TableRowMode.Dynamic, ct: CancellationToken.None);
        await ExecuteAsync(
            "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty) VALUES " +
            $"({doc.PeriodKey.Value}, {doc.RowIds[0]}, {doc.ColumnDefIds[1]}, {doc.TableDefId}, 10, 0, 0), " +
            $"({doc.PeriodKey.Value}, {doc.RowIds[1]}, {doc.ColumnDefIds[1]}, {doc.TableDefId}, 20, 0, 0);");
        return doc;
    }

    private Task ResetAsync(TestDocument doc)
        => ExecuteAsync(
            $"DELETE FROM doc.CellFormulaOverride WHERE TableInstanceId = {doc.TableInstanceId}; " +
            $"DELETE FROM doc.CellValue WHERE PeriodKey = {doc.PeriodKey.Value} AND ColumnDefId = {doc.ColumnDefIds[2]} " +
            $"AND TableRowId IN ({doc.RowIds[0]}, {doc.RowIds[1]});");

    /// <summary>
    /// Те, що робить <c>PatchCellsHandler</c>: позначка (через сховище) і ручне значення — однією транзакцією; з
    /// <paramref name="useLockingOrder"/> — у бойовому порядку (позначка ПЕРЕД значенням).
    /// </summary>
    private async Task SetManualAsync(TestDocument doc, long rowId, bool useLockingOrder)
    {
        await using var db = CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        var address = new CellAddress(doc.PeriodKey, rowId, doc.ColumnDefIds[2]);
        var store = new FormulaOverrideStore(db);
        var mark = new CellFormulaOverride(address, doc.TableInstanceId, "race", 1, Now);

        if (useLockingOrder)
        {
            await store.ApplyAsync([mark], [], CancellationToken.None);
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             MERGE doc.CellValue WITH (HOLDLOCK) AS t
             USING (SELECT {doc.PeriodKey.Value} AS p, {rowId} AS r, {doc.ColumnDefIds[2]} AS c) AS s
                ON t.PeriodKey = s.p AND t.TableRowId = s.r AND t.ColumnDefId = s.c
             WHEN MATCHED THEN UPDATE SET ValueNumeric = {Manual}, IsCalculated = 0, IsEmpty = 0
             WHEN NOT MATCHED THEN INSERT (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty)
                  VALUES (s.p, s.r, s.c, {doc.TableDefId}, {Manual}, 0, 0);
             """);

        if (!useLockingOrder)
        {
            await store.ApplyAsync([mark], [], CancellationToken.None);
        }

        await tx.CommitAsync();
    }

    private static RecalculationService Service(EcrDbContext db, TestDocument doc, IFormulaOverrideStore? store = null)
    {
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);
        var clock = new TestClock(Now);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(doc.DocumentId, doc.PeriodKey.Value, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));
        periods.FindPeriodStateAsync(doc.DocumentId, doc.PeriodKey.Value, Arg.Any<CancellationToken>())
               .Returns((PeriodState?)PeriodState.Open);

        var versions = Substitute.For<ITemplateVersionStore>();
        versions.ListFormulaDependenciesAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(
        [
            FormulaDependency.ForFormula(
                FormulaId, dependsOnKind: 0, doc.TableDefId, rowKey: null, doc.ColumnDefIds[1],
                filterJson: null, periodOffset: null, sortOrder: 0),
        ]);

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<string, ExpressionValue>());

        return new RecalculationService(
            new NormalizedCellStore(db), new RowStore(db, bulk, clock), periods, Metadata(doc), versions,
            new RealFormulaEngine(), units, Substitute.For<IRegistryStore>(), headers,
            new AuditWriter(db), clock, new UnitOfWork(db), new SheetEditGate(db),
            formulaOverrides: store ?? new FormulaOverrideStore(db));
    }

    /// <summary>Знімок: OUT (Formula) = [IN] * 2 з «Дозволити ручне значення».</summary>
    private static IMetadataCache Metadata(TestDocument doc)
    {
        var text = Column(doc, 1, "TXT", CellDataType.String);
        var input = Column(doc, 2, "IN", CellDataType.Decimal);
        var output = Column(doc, 3, "OUT", CellDataType.Formula);

        var sheet = new SheetDef(doc.TemplateVersionId, EcrCode.Create($"SH{doc.SheetDefId}"), Name("Sheet"), 1);
        SetId(sheet, doc.SheetDefId);
        var table = new TableDef(doc.SheetDefId, EcrCode.Create($"TB{doc.TableDefId}"), Name("Table"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        SetId(table, doc.TableDefId);
        table.AddColumn(text);
        table.AddColumn(input);
        table.AddColumn(output);

        var formula = new FormulaDef(table.Id, FormulaScope.Column, "[IN] * 2", ExpressionDialect.Template);
        SetId(formula, FormulaId);
        formula.AssignColumn(output.Id);
        formula.SetAllowManualOverride(true);
        table.AddFormula(formula);
        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId: doc.TemplateVersionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef> { [text.Id] = text, [input.Id] = input, [output.Id] = output },
            RowsByKey: new Dictionary<(int, string), RowDef>());

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);
        return metadata;
    }

    private static ColumnDef Column(TestDocument doc, int ordinal, string code, CellDataType type)
    {
        var column = new ColumnDef(doc.TableDefId, EcrCode.Create(code), Name(code), ordinal, type);
        SetId(column, doc.ColumnDefIds[ordinal - 1]);
        return column;
    }

    private static void SetId(object entity, int id) => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext CreateContext() => new TestDocumentBuilder(sql.ConnectionString).CreateContext();

    private async Task<decimal?> OutputAsync(TestDocument doc, long rowId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT ValueNumeric FROM doc.CellValue WHERE PeriodKey = {doc.PeriodKey.Value} " +
            $"AND TableRowId = {rowId} AND ColumnDefId = {doc.ColumnDefIds[2]}";
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : (decimal)result;
    }

    private async Task ExecuteAsync(string statement)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync();
    }
}
