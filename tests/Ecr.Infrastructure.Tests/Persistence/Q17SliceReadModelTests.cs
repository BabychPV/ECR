// tests/Ecr.Infrastructure.Tests/Persistence/Q17SliceReadModelTests.cs
using System.Globalization;
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Q-17 (читання API-зрізу наборами): на синтетиці ФОРМИ Land (<c>LayoutKind</c> 0 і 3, розріджені комірки, багато
/// результатів на (документ, період), більшість — чужих таблиць і неактуальних прогонів) зріз і
/// <c>GET .../tables/status</c> мають фіксовану кількість запитів і НЕ читають чужі результати та чужі колонки.
/// </summary>
/// <remarks>
/// Межі — детерміновані (кількість запитів, рядків, що вийшли з бази), не таймінг. Еквівалентність значень — у
/// <c>ReportViewSliceEquivalenceTests</c> (вʼюха == реальний зріз) і в тестах нижче (набірне читання == читання
/// «усе, потім відбір у пам'яті»).
/// </remarks>
[Collection("SqlServer")]
public sealed class Q17SliceReadModelTests(SqlServerFixture sql, ITestOutputHelper output)
{
    private static readonly DateTime Now = new(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc);
    private const int ViewPeriod = 202610;
    private const int Anchor = 202601;

    private sealed record Landish(
        TestDocument Main,
        IReadOnlyList<TestDocument> Extras,
        IReadOnlyList<int> CalculatedColumnIds,
        IReadOnlyList<(int Methodology, int Version, string Output, int Column)> Bindings,
        int UnitId);

    private static int UniqueSuffix;

    // Спільний кеш метаданих: вимір зрізу - ТЕПЛИЙ, як у продукті (знімок версії вже в памʼяті).
    private static readonly Microsoft.Extensions.Caching.Memory.MemoryCache Memory = new(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());

    /// <summary>
    /// Будує документ за формою Land: головна таблиця (<paramref name="layout"/>), чужі таблиці з комірками, обчислювані
    /// колонки з прив'язками, результати актуального й неактуального прогонів (більшість — шум чужих виходів).
    /// </summary>
    private async Task<Landish> BuildAsync(
        TableLayoutKind layout, int columns, int filledEvery, int rows, int noiseResults, int extraTables)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var routed = layout == TableLayoutKind.MonthsInColumns;
        var main = await builder.BuildAsync(
            ViewPeriod, columnCount: columns, rowCount: rows, layoutKind: layout, monthColumns: routed ? 12 : 0);

        var calcColumns = new List<int>();
        await using (var db = sql.CreateContext())
        {
            for (var i = 0; i < 10; i++)
            {
                var column = new ColumnDef(main.TableDefId, EcrCode.Create("CLC" + Guid.NewGuid().ToString("N")[..8]),
                    new LocalizedText(new Dictionary<string, string> { ["en"] = "calc" }), 9000 + i, CellDataType.Calculated);
                db.ColumnDefs.Add(column);
                await db.SaveChangesAsync();
                calcColumns.Add(column.Id);
            }

        }

        var extras = new List<TestDocument>();
        if (routed)
        {
            for (var m = 1; m <= 12; m++)
            {
                if (202600 + m == ViewPeriod)
                {
                    continue;
                }

                await builder.AddPeriodInstanceAsync(main, new PeriodKey(202600 + m));
            }

            for (var t = 0; t < extraTables; t++)
            {
                var extra = await builder.AddTableAsync(main, TableLayoutKind.MonthsInColumns, monthColumns: 12, columnCount: columns, rowCount: rows);
                for (var m = 1; m <= 12; m++)
                {
                    if (202600 + m != ViewPeriod)
                    {
                        await builder.AddPeriodInstanceAsync(extra, new PeriodKey(202600 + m));
                    }
                }

                extras.Add(extra);
            }
        }

        await using (var pdb = sql.CreateContext())
        {
            var version = await pdb.TemplateVersions.SingleAsync(v => v.Id == main.TemplateVersionId);
            version.Publish(publishedByUserId: 1, Now);
            await pdb.SaveChangesAsync();
        }
        // Комірки: звичайні колонки (розріджено) живуть у якорі, місячні - у періоді свого місяця (layout 0);
        // layout 3 - усе у власному екземплярі.
        foreach (var table in new[] { main }.Concat(extras))
        {
            await FillCellsAsync(table, filledEvery, routed);
        }

        // Методології, прив'язки (по одній на обчислювану колонку; чотири спільні версії).
        var unitId = await sql.CreateContext().Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        var bindings = new List<(int, int, string, int)>();
        var tag = Interlocked.Increment(ref UniqueSuffix).ToString(CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N")[..6];
        await using (var db = sql.CreateContext())
        {
            for (var i = 0; i < calcColumns.Count; i++)
            {
                var methodology = new Methodology(EcrCode.Create($"SL{tag}_{i}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
                db.Methodologies.Add(methodology);
                await db.SaveChangesAsync();
                var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
                db.MethodologyVersions.Add(version);
                await db.SaveChangesAsync();
                db.CalculationBindings.Add(new CalculationBinding(main.TableDefId, calcColumns[i], methodology.Id, $"OUT{i}", "{}"));
                await db.SaveChangesAsync();
                bindings.Add((methodology.Id, version.Id, $"OUT{i}", calcColumns[i]));
            }
        }

        // Прогони: актуальний і неактуальний; результати прив'язок (по рядку головної таблиці) + шум.
        long current;
        long old;
        await using (var db = sql.CreateContext())
        {
            var cur = new CalculationRun(main.ProjectId, ViewPeriod, 1, Now, main.DocumentId);
            var sup = new CalculationRun(main.ProjectId, ViewPeriod, 1, Now, main.DocumentId);
            db.CalculationRuns.AddRange(cur, sup);
            await db.SaveChangesAsync();
            cur.Complete("Completed", Now, null, null);
            sup.Complete("Completed", Now, null, null);
            cur.MakeCurrent();
            sup.Supersede();
            await db.SaveChangesAsync();
            current = cur.Id;
            old = sup.Id;
        }

        var noiseVersion = bindings[0].Item2;
        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync();
            for (var i = 0; i < bindings.Count; i++)
            {
                var (_, versionId, output, _) = bindings[i];
                foreach (var run in new[] { current, old })
                {
                    // Граничні форми (усі в актуальному прогоні): код виходу в іншому регістрі (3); результат без екземпляра,
                    // до D-401 (4); чужий екземпляр поруч зі своїм (5); дві речовини - сума (6); версія поза прив'язкою (7).
                    var instance = i == 4 ? (long?)null : main.TableInstanceId;
                    await InsertResultsAsync(connection, main, run, versionId, i == 3 ? output.ToLowerInvariant() : output, instance, null, 1.25m, unitId);
                    if (run != current)
                    {
                        continue;
                    }

                    if (i == 5)
                    {
                        await InsertResultsAsync(connection, main, run, versionId, output, 999_000_001, null, 99m, unitId);
                    }

                    if (i == 6)
                    {
                        await InsertResultsAsync(connection, main, run, versionId, output, main.TableInstanceId, 1, 2m, unitId);
                        await InsertResultsAsync(connection, main, run, versionId, output, main.TableInstanceId, 2, 3m, unitId);
                    }

                    if (i == 7)
                    {
                        await InsertResultsAsync(connection, main, run, noiseVersion == versionId ? bindings[1].Item2 : noiseVersion, output, main.TableInstanceId, null, 77m, unitId);
                    }
                }
            }

            // Шум: результати чужих таблиць/виходів (того самого документа й періоду) - у актуальному й неактуальному прогонах.
            foreach (var run in new[] { current, old })
            {
                await ExecAsync(connection, """
                    ;WITH n AS (SELECT TOP (@n) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS k FROM sys.all_objects a CROSS JOIN sys.all_objects b)
                    INSERT calc.CalculationResult (Id, PeriodKey, CalculationRunId, MethodologyVersionId, DocumentId, SourceRowKey,
                                                   OutputCode, Value, UnitId, SubstanceEntryId, Kind, TableInstanceId)
                    SELECT NEXT VALUE FOR calc.CalculationResultSeq OVER (ORDER BY k), @p, @run, @v, @d, CONCAT('X', k % 400),
                           CONCAT('NOISE', k % 40), 2.5, @u, NULL, 0, 999000000 + (k % 30)
                    FROM n
                    """, ("@n", noiseResults), ("@p", ViewPeriod), ("@run", run), ("@v", noiseVersion), ("@d", main.DocumentId), ("@u", unitId));
            }
        }

        return new Landish(main, extras, calcColumns, bindings.Select(b => (b.Item1, b.Item2, b.Item3, b.Item4)).ToList(), unitId);
    }

    private static Task InsertResultsAsync(
        SqlConnection connection, TestDocument main, long run, int versionId, string output, long? instance, long? substance,
        decimal value, int unitId)
        => ExecAsync(connection, """
            INSERT calc.CalculationResult (Id, PeriodKey, CalculationRunId, MethodologyVersionId, DocumentId, SourceRowKey,
                                           OutputCode, Value, UnitId, SubstanceEntryId, Kind, TableInstanceId)
            SELECT NEXT VALUE FOR calc.CalculationResultSeq OVER (ORDER BY r.Id), @p, @run, @v, @d, r.RowKey,
                   @o, @val, @u, @s, 0, @i
            FROM doc.TableRow r WHERE r.PeriodKey = @p AND r.TableInstanceId = @main
            """, ("@p", ViewPeriod), ("@run", run), ("@v", versionId), ("@d", main.DocumentId), ("@o", output), ("@val", value),
            ("@u", unitId), ("@s", (object?)substance ?? DBNull.Value), ("@i", (object?)instance ?? DBNull.Value),
            ("@main", main.TableInstanceId));
    private async Task FillCellsAsync(TestDocument table, int filledEvery, bool routed)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await ExecAsync(connection, """
            INSERT doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty)
            SELECT r.PeriodKey, r.Id, c.Id, i.TableDefId, 1.5, 0, 0
            FROM doc.TableInstance i
            JOIN doc.TableRow r ON r.PeriodKey = i.PeriodKey AND r.TableInstanceId = i.Id
            JOIN cfg.ColumnDef c ON c.TableDefId = i.TableDefId AND c.DataType = @dec AND c.Ordinal < 9000
            WHERE i.DocumentId = @d AND i.TableDefId = @t
              AND (( @routed = 0 AND i.PeriodKey = @view AND c.Ordinal % @every = 0)
                OR ( @routed = 1 AND c.IsMonthColumn = 1 AND c.MonthNumber = i.PeriodKey % 100)
                OR ( @routed = 1 AND c.IsMonthColumn = 0 AND i.PeriodKey = @anchor AND c.Ordinal % @every = 0))
            """, ("@dec", (int)CellDataType.Decimal), ("@d", table.DocumentId), ("@t", table.TableDefId), ("@routed", routed ? 1 : 0),
            ("@view", ViewPeriod), ("@every", filledEvery), ("@anchor", Anchor));
    }

    private static async Task ExecAsync(SqlConnection connection, string text, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        command.CommandTimeout = 300;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static IConfiguration RoutingOn()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Collection:MonthColumnRouting"] = "true" })
            .Build();

    /// <summary>Вимір: звернення до бази, рядки, що вийшли, логічні читання (STATISTICS IO), мілісекунди.</summary>
    private sealed record Measure(int Queries, long Rows, long LogicalReads, long Ms, CommandTallySnapshot Tally);

    private async Task<(Measure Measure, T Result)> MeasureAsync<T>(Func<EcrDbContext, Task<T>> action)
    {
        var counter = new DbCommandCounter();
        await using var db = sql.CreateContext(counter);
        await db.Database.OpenConnectionAsync();
        var connection = (SqlConnection)db.Database.GetDbConnection();
        async Task<long> SessionReadsAsync()
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT logical_reads FROM sys.dm_exec_sessions WHERE session_id = @@SPID";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        var readsBefore = await SessionReadsAsync();
        connection.StatisticsEnabled = true;
        connection.ResetStatistics();
        counter.Tally.Reset();
        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await action(db);
        started.Stop();
        var reads = await SessionReadsAsync() - readsBefore - 1;
        var stats = connection.RetrieveStatistics();
        var rows = Convert.ToInt64(stats["SelectRows"], CultureInfo.InvariantCulture);
        var tally = counter.Tally.Snapshot();
        return (new Measure(tally.Total, rows, reads, started.ElapsedMilliseconds, tally), result);
    }

    /// <summary>Читач чисел БЕЗ звуження: лише <see cref="ICalculationResultStore"/> (повний набір, як до Q-17).</summary>
    private static ICalculationResultStore WideOnly(EcrDbContext db, int? ceiling = null)
    {
        var real = new CalculationResultStore(db, new TestClock(Now), readCeiling: ceiling);
        var wide = NSubstitute.Substitute.For<ICalculationResultStore>();
        NSubstitute.SubstituteExtensions.Returns(
            wide.ReadCurrentAsync(NSubstitute.Arg.Any<long>(), NSubstitute.Arg.Any<int>(), NSubstitute.Arg.Any<CancellationToken>()),
            call => real.ReadCurrentAsync(call.ArgAt<long>(0), call.ArgAt<int>(1), call.ArgAt<CancellationToken>(2)));
        return wide;
    }

    /// <summary>Знижена стеля читання чисел (замість 50 000 реальних рядків у спільній тестовій базі - стеля CI 192 МБ).</summary>
    private const int SmallCeiling = 1_500;

    private static async Task<IReadOnlyList<CellRecord>> ReadSliceAsync(EcrDbContext db, Landish world, bool narrowed, int? ceiling = null)
    {
        var store = new MonthRoutedCellStore(
            new NormalizedCellStore(db), new MonthColumnRouter(db, RoutingOn()),
            new Ecr.Infrastructure.Caching.MetadataCache(Memory, db), db);
        var cells = await store.ReadSliceAsync(world.Main.TableInstanceId, new PeriodKey(ViewPeriod), CancellationToken.None);
        var rowIds = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == ViewPeriod && r.TableInstanceId == world.Main.TableInstanceId && !r.IsDeleted)
            .ToDictionaryAsync(r => r.RowKeyValue, r => r.Id);
        ICalculationResultStore results = narrowed ? new CalculationResultStore(db, new TestClock(Now), readCeiling: ceiling) : WideOnly(db, ceiling);
        var overlaid = await new CalculatedCellOverlay(new MethodologyStore(db), results)
            .ApplyAsync(world.Main.DocumentId, ViewPeriod,
                [new OverlayTable(world.Main.TableInstanceId, world.Main.TableDefId, rowIds, cells, world.CalculatedColumnIds.ToHashSet())],
                CancellationToken.None);
        return overlaid[world.Main.TableInstanceId];
    }

    private static string Fingerprint(IEnumerable<CellRecord> cells)
        => string.Join(
            '\n',
            cells.OrderBy(c => c.Address.TableRowId).ThenBy(c => c.Address.ColumnDefId)
                .Select(c => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{c.Address.TableRowId}/{c.Address.ColumnDefId}={c.Value.ValueNumeric}|{c.Value.ValueString}|{c.Value.ValueUnitId}|{c.Value.IsCalculated}|{c.Value.IsEmpty}")));

    /// <summary>
    /// Зріз (ON, місяці в колонках) і зріз без маршрутизації (layout 3) із Land-подібними результатами: вибірка чисел
    /// звужена до результатів, які може використати накладення, число звернень фіксоване, а ЗНАЧЕННЯ ті самі, що й
    /// при читанні «усього набору з відбором у памʼяті» (код виходу в іншому регістрі, результат без екземпляра,
    /// чужий екземпляр, дві речовини, версія поза прив'язкою - усе в одному документі).
    /// </summary>
    [Theory]
    [InlineData(TableLayoutKind.MonthsInColumns, 190, 8, 100, 13000, 3, 11)]
    [InlineData(TableLayoutKind.PerPeriodInstance, 222, 8, 100, 13000, 0, 8)]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    public async Task Зріз_читає_лише_потрібні_числа_і_дає_ті_самі_значення(
        TableLayoutKind layout, int columns, int every, int rows, int noise, int extras, int maxQueries)
    {
        var world = await BuildAsync(layout, columns, every, rows, noise, extras);

        await MeasureAsync(db => ReadSliceAsync(db, world, narrowed: true));   // прогрів плану/метаданих
        var (narrow, narrowCells) = await MeasureAsync(db => ReadSliceAsync(db, world, narrowed: true));
        var (wide, wideCells) = await MeasureAsync(db => ReadSliceAsync(db, world, narrowed: false));

        output.WriteLine($"[{layout}] ЗВУЖЕНО: queries={narrow.Queries} rows={narrow.Rows} reads={narrow.LogicalReads} ms={narrow.Ms}");
        output.WriteLine($"[{layout}] ПОВНИЙ  : queries={wide.Queries} rows={wide.Rows} reads={wide.LogicalReads} ms={wide.Ms}");
        output.WriteLine(narrow.Tally.Format());

        // Те саме значення в кожній комірці (включно з EDGE: регістр, без екземпляра, чужий екземпляр, речовини, версія).
        Assert.Equal(Fingerprint(wideCells), Fingerprint(narrowCells));
        Assert.True(narrowCells.Count(c => c.Value.IsCalculated) >= 4 * rows, "накладення не дало чисел - тест порожній");

        // Детермінована межа: кількість звернень НЕ залежить від числа рядків/результатів.
        Assert.True(narrow.Queries <= maxQueries, $"запитів {narrow.Queries} > {maxQueries}\n{narrow.Tally.Format()}");

        // Чужі числа (шум: {noise} рядків × 2 прогони) з бази не виходять: рядків ≈ комірки + числа цієї таблиці.
        Assert.True(
            narrow.Rows < wide.Rows / 3 && narrow.Rows < 12_000,
            $"з бази вийшло {narrow.Rows} рядків (повний набір: {wide.Rows}); шум не відсічено?");
    }

    /// <summary>
    /// Раніше документ із понад 50 000 чисел актуального прогону (на Land p95 - 96 тис. на документ-період) давав зрізу
    /// <c>ECR-CALC-0422 resultsTooLarge</c>, хоча числа цієї таблиці - сотні. Тепер ліміт діє на ЗВУЖЕНИЙ набір.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    public async Task Зріз_не_падає_на_документі_з_понад_50_тисяч_чужих_чисел()
    {
        var world = await BuildAsync(TableLayoutKind.PerPeriodInstance, columns: 40, filledEvery: 4, rows: 20, noiseResults: SmallCeiling + 400, extraTables: 0);

        var cells = await ReadSliceAsync(sql.CreateContext(), world, narrowed: true, ceiling: SmallCeiling);
        Assert.True(cells.Count(c => c.Value.IsCalculated) >= 4 * 20);

        // Повний набір (старе читання) і далі відмовляє - стеля 50 000 на документ-період не знята, лише звужена.
        var ex = await Assert.ThrowsAsync<Ecr.Application.Errors.BusinessRuleException>(
            () => ReadSliceAsync(sql.CreateContext(), world, narrowed: false, ceiling: SmallCeiling));
        Assert.Equal("ECR-CALC-0422", ex.ErrorCode);
    }

    /// <summary>
    /// <c>GET .../tables/status</c> з прапором ON: заповненість колонок з чужими власниками - ОДИН запит на всі
    /// періоди-власники року, а не по запиту на період (раніше 13 звернень до <c>doc.TableInstance</c>); числа ті
    /// самі, що в колишнього покроково-періодного запиту (еталон нижче).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    public async Task Статус_заповненості_читає_власників_одним_запитом_і_дає_ті_самі_числа()
    {
        var world = await BuildAsync(TableLayoutKind.MonthsInColumns, columns: 190, filledEvery: 8, rows: 60, noiseResults: 10, extraTables: 4);
        var computed = world.CalculatedColumnIds.ToList();

        Func<EcrDbContext, Task<IReadOnlyList<TableFillCounts>>> status = async db
            => await new TableFillStore(db, new MonthColumnRouter(db, RoutingOn()), new Ecr.Infrastructure.Caching.MetadataCache(Memory, db))
                .GetFillCountsAsync(world.Main.DocumentId, new PeriodKey(ViewPeriod), computed, CancellationToken.None);

        await MeasureAsync(status);
        var (m, counts) = await MeasureAsync(status);
        output.WriteLine($"STATUS queries={m.Queries} rows={m.Rows} reads={m.LogicalReads} ms={m.Ms}");
        output.WriteLine(m.Tally.Format());

        // Еталон: попередня форма запиту - по запиту на період-власник (скаляр), та сама семантика групування.
        var expected = await LegacyForeignFillByTableAsync(world);
        foreach (var table in new[] { world.Main }.Concat(world.Extras))
        {
            var viewOwn = await ViewOwnCellsAsync(table);
            Assert.Equal(viewOwn + expected[table.TableDefId], counts.Single(c => c.TableDefId == table.TableDefId).FilledCells);
        }

        // doc.TableInstance: екземпляри перегляду (1) + рядки/комірки перегляду (2) + власники (1) - не 1 + 11 + ...
        Assert.True(m.Tally["SELECT doc.TableInstance"] <= 3, m.Tally.Format());
        Assert.True(m.Queries <= 6, m.Tally.Format());
    }

    /// <summary>Комірки перегляду, які ЛИШАЮТЬСЯ в чисельнику: власні (власник = перегляд) введені, не обчислювані.</summary>
    private async Task<int> ViewOwnCellsAsync(TestDocument table)
    {
        await using var db = sql.CreateContext();
        return await (from c in db.CellValues.AsNoTracking()
                      join col in db.ColumnDefs.AsNoTracking() on c.ColumnDefId equals col.Id
                      join row in db.TableRows.AsNoTracking() on new { P = c.PeriodKeyValue, R = c.TableRowId } equals new { P = row.PeriodKeyValue, R = row.Id }
                      where c.PeriodKeyValue == ViewPeriod && row.TableInstanceId == table.TableInstanceId && !row.IsDeleted && !c.IsCalculated
                            && col.IsMonthColumn && col.MonthNumber == ViewPeriod % 100
                      select c.ColumnDefId).CountAsync();
    }

    /// <summary>Еталонний рахунок чужих комірок: колишній запит - один період-власник на запит.</summary>
    private async Task<Dictionary<int, int>> LegacyForeignFillByTableAsync(Landish world)
    {
        await using var db = sql.CreateContext();
        var result = new Dictionary<int, int>();
        foreach (var table in new[] { world.Main }.Concat(world.Extras))
        {
            var total = 0;
            for (var owner = 202601; owner <= 202612; owner++)
            {
                if (owner == ViewPeriod)
                {
                    continue;
                }

                var groups = await (from instance in db.TableInstances.AsNoTracking()
                                    where instance.DocumentId == table.DocumentId && instance.PeriodKeyValue == ViewPeriod && instance.TableDefId == table.TableDefId
                                    join row in db.TableRows.AsNoTracking().Where(r => r.PeriodKeyValue == ViewPeriod && !r.IsDeleted)
                                        on instance.Id equals row.TableInstanceId
                                    join o in db.TableInstances.AsNoTracking()
                                            .Where(o => o.DocumentId == table.DocumentId && o.PeriodKeyValue == owner && o.TableDefId == table.TableDefId)
                                        on instance.TableDefId equals o.TableDefId
                                    join ownerRow in db.TableRows.AsNoTracking().Where(r => r.PeriodKeyValue == owner && !r.IsDeleted)
                                        on new { I = o.Id, K = row.RowKeyValue } equals new { I = ownerRow.TableInstanceId, K = ownerRow.RowKeyValue }
                                    join cell in db.CellValues.AsNoTracking().Where(c => c.PeriodKeyValue == owner && !c.IsCalculated)
                                        on ownerRow.Id equals cell.TableRowId
                                    join col in db.ColumnDefs.AsNoTracking() on cell.ColumnDefId equals col.Id
                                    where (col.IsMonthColumn && col.MonthNumber == owner % 100) || (!col.IsMonthColumn && owner == Anchor)
                                    where col.DataType != CellDataType.Calculated && col.DataType != CellDataType.Formula
                                    select cell.ColumnDefId).CountAsync();
                total += groups;
            }

            result[table.TableDefId] = total;
        }

        return result;
    }
}