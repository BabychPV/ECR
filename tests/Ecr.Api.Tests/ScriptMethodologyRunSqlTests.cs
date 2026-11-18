using Ecr.Application;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Workflow;
using Ecr.Calculations;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.Scripting;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Версії-скрипти на РЕАЛЬНОМУ SQL Server крізь справжні <c>RecalculationJob</c> → <c>CalculationOrchestrator</c> →
/// <c>ScriptCalculationModule</c> (MS T2.5): рядки з <c>rows … where</c> потрапляють у <c>calc.CalculationResult</c>,
/// решта дають проблеми рядків; методологія-читач бачить результат джерела з ТОГО САМОГО прогону, а правка входу
/// джерела й перерахунок аркуша-джерела оновлюють читача.
/// </summary>
/// <remarks>
/// Скрипт пишеться через домен (<c>SetScript</c>), а виходи, прив'язки й таблиці читання вставлено напряму —
/// матеріалізація (§5.1) належить іншому коду. Періоди — 2099xx. Мутаційні докази (лише цей клас): прибрати
/// <c>ExtendTouchedWithScriptReadersAsync</c> з <c>RecalculationJob.BindingsAsync</c> — червоніє
/// <see cref="Правка_входу_джерела_й_перерахунок_аркуша_оновлює_читача_з_того_самого_прогону"/>; не заповнювати
/// <c>CalculationNode.DependsOn</c> у оркестраторі — читач рахується в одній пачці з джерелом і бачить порожнє.
/// </remarks>
[Collection("SqlServer")]
public sealed class ScriptMethodologyRunSqlTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2099, 1, 10, 9, 0, 0, DateTimeKind.Utc);

    private sealed record Fixture(
        TestDocumentBuilder Builder,
        TestDocument Document,
        string TableCode,
        string C1,
        string C2,
        int OutAColumnId,
        int UnitId,
        string Tag);

    private sealed record Second(
        int SheetDefId, int TableDefId, string TableCode, long InstanceId, int OutBColumnId, int MethodologyBId, int VersionBId);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Версія_скрипт_пише_результати_відібраних_рядків_а_решті_дає_проблеми()
    {
        var f = await ArrangeAsync(209901);
        await InsertValuesAsync(f, ("gas", 10m), ("gas", null), ("oil", 7m));
        await PublishScriptAsync(
            f, "A",
            $"rows from {f.TableCode} where row.{f.C1} == \"gas\";\noutput Y: kg = row.{f.C2} * 2;",
            [("Y", f.OutAColumnId)]);

        await RunJobAsync(f.Document, sheetDefId: null);

        await using var db = f.Builder.CreateContext();
        var rows = await new CalculationResultStore(db, new TestClock(Now))
            .ReadCurrentAsync(f.Document.DocumentId, f.Document.PeriodKey.Value, default);
        var issues = await db.CalculationIssues
            .AsNoTracking()
            .Where(i => i.DocumentId == f.Document.DocumentId && i.PeriodKey == f.Document.PeriodKey.Value)
            .Select(i => new { i.RowNumber, i.Kind })
            .ToListAsync();

        // Рядок 1 (gas, 10) → Y = 20; рядок 2 (gas без C2) — без результату; рядок 3 (oil) не відібраний.
        var result = Assert.Single(rows);
        Assert.Equal(("Y", 20m), (result.OutputCode, result.Value));
        Assert.Equal(f.Document.TableInstanceId, result.TableInstanceId);

        Assert.Contains(issues, i => i is { RowNumber: 2, Kind: CalculationIssue.MissingInputsKind });
        Assert.Contains(issues, i => i is { RowNumber: 3, Kind: CalculationIssue.NoMatchingRuleKind });
        Assert.DoesNotContain(issues, i => i.RowNumber == 1);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Правка_входу_джерела_й_перерахунок_аркуша_оновлює_читача_з_того_самого_прогону()
    {
        var f = await ArrangeAsync(209902);
        await InsertValuesAsync(f, ("gas", 10m), ("gas", 5m), ("oil", 7m));
        var a = await PublishScriptAsync(
            f, "A",
            $"rows from {f.TableCode} where row.{f.C1} == \"gas\";\noutput Y: kg = row.{f.C2} * 2;",
            [("Y", f.OutAColumnId)]);

        // B живе на ІНШОМУ аркуші й читає результат A через колонку Calculated таблиці A (OUTA).
        await ArrangeReaderAsync(f, a);

        // Прогін 1 (увесь документ): B у пізнішій пачці, ніж A, і бачить результат A з цього ж прогону.
        await RunJobAsync(f.Document, sheetDefId: null);
        Assert.Equal(["Y|10", "Y|20", "Z|30"], await ShapeAsync(f));

        // Правка входу A (10 → 30) і перерахунок АРКУША-ДЖЕРЕЛА: B не прив'язаний до цього аркуша, але читає його
        // таблицю — перераховується разом із ним і бачить свіжі числа A (60 + 10).
        await SetValueAsync(f, rowIndex: 0, 30m);
        await RunJobAsync(f.Document, sheetDefId: f.Document.SheetDefId);

        Assert.Equal(["Y|10", "Y|60", "Z|70"], await ShapeAsync(f));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Застарілість_аркуша_читача_охоплює_таблицю_яку_читає_скрипт_і_таблиці_джерел_результатів()
    {
        var f = await ArrangeAsync(209903);
        await InsertValuesAsync(f, ("gas", 10m));
        var a = await PublishScriptAsync(
            f, "A", $"rows from {f.TableCode};\noutput Y: kg = row.{f.C2};", [("Y", f.OutAColumnId)]);
        var b = await ArrangeReaderAsync(f, a);

        await using var db = f.Builder.CreateContext();
        var store = new ScriptRuntimeStore(db);
        await using var provider = Provider();
        await using var scope = provider.CreateAsyncScope();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IMetadataCache>()
            .GetAsync(f.Document.TemplateVersionId, default);

        // Аркуш читача (B): власна таблиця B, плюс таблиця A, яку B читає (`table Src = …`).
        var readerScope = new HashSet<int> { b.TableDefId };
        await ScriptFreshnessScope.ExtendAsync(store, snapshot, readerScope, [b.MethodologyBId], default);
        Assert.Equal([f.Document.TableDefId, b.TableDefId], readerScope.Order().ToArray());

        // Аркуш джерела (A) нічого чужого не читає: замикання лишається його таблицею.
        var own = new HashSet<int> { f.Document.TableDefId };
        await ScriptFreshnessScope.ExtendAsync(store, snapshot, own, [a.MethodologyId], default);
        Assert.Equal([f.Document.TableDefId], own.ToArray());

        // Читач результатів без читання таблиць (лише ребро залежності): замикання бере таблиці виходів джерела.
        var c = new HashSet<int> { b.TableDefId };
        db.MethodologyReadTables.RemoveRange(await db.MethodologyReadTables.Where(r => r.MethodologyVersionId == b.VersionBId).ToListAsync());
        await db.SaveChangesAsync();
        await ScriptFreshnessScope.ExtendAsync(store, snapshot, c, [b.MethodologyBId], default);
        Assert.Equal([f.Document.TableDefId, b.TableDefId], c.Order().ToArray());

        // Читачі таблиці й ребра залежностей — те, з чого `RecalculationJob` розширює перелік.
        var readers = await store.GetReadersOfTablesAsync([f.TableCode], default);
        Assert.Empty(readers);
        Assert.Equal([a.MethodologyId, b.MethodologyBId], (await store.GetReaderClosureAsync([a.MethodologyId], default)).Order().ToArray());
    }

    /// <summary>
    /// MS (рецензія F13, D): перегляд скрипта (<c>ScriptPreviewRunner</c>) і справжній прогін після публікації дають ті самі
    /// числа — для джерела й для читача його результату (читач у перегляді бере актуальні результати прогону).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перегляд_дає_ті_самі_числа_що_прогін_після_публікації_для_джерела_і_читача()
    {
        var f = await ArrangeAsync(209904);
        await InsertValuesAsync(f, ("gas", 10m), ("gas", null), ("oil", 7m));
        var textA = $"rows from {f.TableCode} where row.{f.C1} == \"gas\";\noutput Y: kg = row.{f.C2} * 2;";
        var a = await PublishScriptAsync(f, "A", textA, [("Y", f.OutAColumnId)]);
        var b = await ArrangeReaderAsync(f, a);
        string textB;
        await using (var db = f.Builder.CreateContext())
        {
            textB = $"rows from {b.TableCode};\ntable Src = {f.TableCode};\noutput Z: kg = Sum(Src.{await ColumnCodeAsync(db, f.OutAColumnId)});";
        }

        await RunJobAsync(f.Document, sheetDefId: null);
        var real = await ShapeAsync(f);
        Assert.Equal(["Y|20", "Z|20"], real);

        var previewA = await PreviewAsync(f, a.VersionId, textA);
        var previewB = await PreviewAsync(f, b.VersionBId, textB);

        Assert.Equal(real.Where(r => r.StartsWith("Y|", StringComparison.Ordinal)), OutputsOf(previewA));
        Assert.Equal(real.Where(r => r.StartsWith("Z|", StringComparison.Ordinal)), OutputsOf(previewB));

        // Рядок без числа й невибраний рядок — ті самі причини, що пише прогін (проблеми рядків).
        var rowsA = Assert.Single(previewA.Tables).Rows;
        Assert.Equal("missingInputs", rowsA.Single(r => r.RowNumber == 2).Issue);
        Assert.Equal("notSelected", rowsA.Single(r => r.RowNumber == 3).State);

        // Правка входу: перегляд бачить її одразу, і ті самі числа з'являються після перерахунку.
        await SetValueAsync(f, rowIndex: 0, 30m);
        var editedA = await PreviewAsync(f, a.VersionId, textA);
        await RunJobAsync(f.Document, sheetDefId: f.Document.SheetDefId);
        Assert.Equal(["Y|60", "Z|60"], await ShapeAsync(f));
        Assert.Equal(["Y|60"], OutputsOf(editedA));
        Assert.Equal(["Z|60"], OutputsOf(await PreviewAsync(f, b.VersionBId, textB)));
    }

    private async Task<ScriptPreviewResult> PreviewAsync(Fixture f, int versionId, string text)
    {
        await using var provider = Provider();
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IScriptPreviewRunner>().RunAsync(
            new ScriptPreviewRequest(
                versionId, text, f.Document.DocumentId, f.Document.PeriodKey.Value, TableInstanceId: null, MaxRows: 50),
            CancellationToken.None);
    }

    private static List<string> OutputsOf(ScriptPreviewResult preview)
        => [.. preview.Tables
            .SelectMany(t => t.Rows)
            .SelectMany(r => r.Values)
            .Where(v => v.Kind == "output" && v.Error is null)
            .Select(v => $"{v.Name}|{decimal.Parse(v.Text, System.Globalization.CultureInfo.InvariantCulture):0.####}")
            .Order(StringComparer.Ordinal)];

    // ───── стенд ─────

    private async Task<Fixture> ArrangeAsync(int periodKey)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(periodKey: periodKey, columnCount: 3, rowCount: 3);
        await using var db = builder.CreateContext();

        var period = await db.Periods.SingleAsync(
            p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == document.PeriodKey.Value);
        period.AdvanceTo(PeriodState.Open, Now);

        var table = await db.TableDefs.SingleAsync(t => t.Id == document.TableDefId);
        var codes = await db.ColumnDefs
            .Where(c => document.ColumnDefIds.Contains(c.Id))
            .OrderBy(c => c.Ordinal)
            .Select(c => c.Code)
            .ToListAsync();

        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var outA = new ColumnDef(table.Id, EcrCode.Create("OUTA_" + tag), Name("Out A"), 10, CellDataType.Calculated);
        db.ColumnDefs.Add(outA);
        await db.SaveChangesAsync();

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        return new Fixture(builder, document, table.Code, codes[0], codes[1], outA.Id, unitId, tag);
    }

    private async Task InsertValuesAsync(Fixture f, params (string Kind, decimal? Value)[] rows)
    {
        await using var db = f.Builder.CreateContext();
        var columns = f.Document.ColumnDefIds;
        for (var i = 0; i < rows.Length; i++)
        {
            db.CellValues.Add(new CellValue(
                new CellAddress(f.Document.PeriodKey, f.Document.RowIds[i], columns[0]),
                f.Document.TableDefId, new CellValueData { ValueString = rows[i].Kind }));
            if (rows[i].Value is { } value)
            {
                db.CellValues.Add(new CellValue(
                    new CellAddress(f.Document.PeriodKey, f.Document.RowIds[i], columns[1]),
                    f.Document.TableDefId, new CellValueData { ValueNumeric = value }));
            }
        }

        await db.SaveChangesAsync();
    }

    private async Task SetValueAsync(Fixture f, int rowIndex, decimal value)
    {
        await using var db = f.Builder.CreateContext();
        var rowId = f.Document.RowIds[rowIndex];
        var column = f.Document.ColumnDefIds[1];
        var cell = await db.CellValues.SingleAsync(c =>
            c.PeriodKeyValue == f.Document.PeriodKey.Value && c.TableRowId == rowId && c.ColumnDefId == column);
        cell.Apply(new CellValueData { ValueNumeric = value });
        await db.SaveChangesAsync();
    }

    /// <summary>Публікує версію-скрипт (текст через домен), вставляє виходи й прив'язки напряму.</summary>
    private async Task<(int MethodologyId, int VersionId)> PublishScriptAsync(
        Fixture f, string key, string text, (string Output, int ColumnId)[] outputs, int? tableDefId = null)
    {
        await using var db = f.Builder.CreateContext();
        var methodology = new Methodology(EcrCode.Create($"SC{key}_{f.Tag}"), Name("script"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(
            methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now, MethodologyEngine.Script);
        version.SetModes(NumericMode.Strict, CalendarMode.Actual, TraceLevel.Full);
        version.SetScript(text, ScriptCompiler.Hash(text));
        version.Publish(2, "test", new DateOnly(2099, 1, 1), testsPassed: true, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();

        for (var i = 0; i < outputs.Length; i++)
        {
            var output = new MethodologyOutput(version.Id, EcrCode.Create(outputs[i].Output), f.UnitId, i + 1);
            output.SetPerSubstance(false);
            db.MethodologyOutputs.Add(output);
            db.CalculationBindings.Add(new CalculationBinding(
                tableDefId ?? f.Document.TableDefId, outputs[i].ColumnId, methodology.Id, outputs[i].Output, "{}"));
        }

        await db.SaveChangesAsync();
        return (methodology.Id, version.Id);
    }

    /// <summary>Другий аркуш із таблицею й методологією-читачем B: <c>table Src = A; Sum(Src.OUTA)</c>.</summary>
    private async Task<Second> ArrangeReaderAsync(Fixture f, (int MethodologyId, int VersionId) a)
    {
        await using var db = f.Builder.CreateContext();
        var sheet = new SheetDef(f.Document.TemplateVersionId, EcrCode.Create("SHB" + f.Tag), Name("Sheet B"), 2);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        var table = new TableDef(
            sheet.Id, EcrCode.Create("TBB" + f.Tag), Name("Table B"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        var outB = new ColumnDef(table.Id, EcrCode.Create("OUTB_" + f.Tag), Name("Out B"), 1, CellDataType.Calculated);
        db.ColumnDefs.Add(outB);
        await db.SaveChangesAsync();

        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        var rowId = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None);
        db.TableInstances.Add(new TableInstance(f.Document.PeriodKey, instanceId, f.Document.DocumentId, table.Id, Now));
        db.TableRows.Add(new TableRow(f.Document.PeriodKey, rowId, instanceId, RowKey.Create("RB_" + f.Tag), 1, Now));
        await db.SaveChangesAsync();

        var text = $"rows from {table.Code};\ntable Src = {f.TableCode};\noutput Z: kg = Sum(Src.{await ColumnCodeAsync(db, f.OutAColumnId)});";
        var (methodologyBId, versionBId) = await PublishScriptAsync(
            f, "B", text, [("Z", outB.Id)], tableDefId: table.Id);

        await using var extra = f.Builder.CreateContext();
        extra.MethodologyReadTables.Add(new MethodologyReadTable(versionBId, f.TableCode));
        extra.MethodologyDependencies.Add(new MethodologyDependency(methodologyBId, a.MethodologyId));
        await extra.SaveChangesAsync();

        return new Second(sheet.Id, table.Id, table.Code, instanceId, outB.Id, methodologyBId, versionBId);
    }

    private static async Task<string> ColumnCodeAsync(EcrDbContext db, int columnId)
        => await db.ColumnDefs.Where(c => c.Id == columnId).Select(c => c.Code).SingleAsync();

    /// <summary>Форма актуальних результатів: <c>вихід|значення</c>, упорядковано.</summary>
    private async Task<List<string>> ShapeAsync(Fixture f)
    {
        await using var db = f.Builder.CreateContext();
        var rows = await new CalculationResultStore(db, new TestClock(Now))
            .ReadCurrentAsync(f.Document.DocumentId, f.Document.PeriodKey.Value, default);
        return [.. rows.Select(r => $"{r.OutputCode}|{r.Value:0.####}").Order(StringComparer.Ordinal)];
    }

    private async Task RunJobAsync(TestDocument document, int? sheetDefId)
    {
        await using var provider = Provider();
        await using var scope = provider.CreateAsyncScope();
        var job = scope.ServiceProvider.GetRequiredService<IRecalculationJob>();

        await job.ExecuteAsync(
            new RecalculationRequest(
                ProjectId: document.ProjectId,
                DocumentId: document.DocumentId,
                PeriodKey: document.PeriodKey.Value,
                TriggeredByUserId: 7,
                SheetDefId: sheetDefId),
            new NoOpProgress(),
            CancellationToken.None);
    }

    private ServiceProvider Provider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Ecr"] = sql.ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEcrInfrastructure(configuration);
        services.AddEcrCalculations();
        services.AddEcrApplication();
        services.AddSingleton<ICurrentUser>(Substitute.For<ICurrentUser>());
        services.AddSingleton<Ecr.Domain.Abstractions.IClock>(new TestClock(Now));

        // Задача не має ставити нову задачу в Quartz: у тесті планувальника немає.
        services.AddScoped(_ => Substitute.For<IBackgroundJobScheduler>());

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = false });
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private sealed class NoOpProgress : IJobProgress
    {
        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
