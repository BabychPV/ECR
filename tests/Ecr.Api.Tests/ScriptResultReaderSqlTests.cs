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
/// Методологія-читач B, що читає ЛИШЕ результати A (MS, план §13.9): у скрипті B немає <c>table</c>, а його власна таблиця має
/// колонку <c>Calculated</c>, прив'язану до виходу A. Перерахунок АРКУША-ДЖЕРЕЛА (рядки A) перераховує й B — замикання за
/// <c>calc.MethodologyDependency</c> у <c>RecalculationJob.BindingsAsync</c>; без нього B лишився б зі старими числами.
/// </summary>
/// <remarks>
/// Окремий клас від <c>ScriptMethodologyRunSqlTests</c>, де читач бачить джерело через <c>table Src = …</c> (тоді B перерахує
/// ще й <c>calc.MethodologyReadTable</c>); тут цього каналу нема, лишається лише ребро залежності. Періоди — 2099xx.
/// Мутаційний доказ: прибрати з <c>ExtendTouchedWithScriptReadersAsync</c> цикл по <c>readersOfSource</c> — тест червоніє.
/// </remarks>
[Collection("SqlServer")]
public sealed class ScriptResultReaderSqlTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2099, 1, 10, 9, 0, 0, DateTimeKind.Utc);

    private static readonly string[] ExpectedFirst = ["Y|22", "Y|22", "Z|220", "Z|220"];

    private static readonly string[] ExpectedSecond = ["Y|42", "Y|42", "Z|420", "Z|420"];

    private sealed record Scene(
        TestDocumentBuilder Builder, TestDocument Document, string TableCode, string C1, string C2, int UnitId, string Tag);

    private sealed record ReaderTable(
        int TableDefId, string TableCode, int ReadColumnId, string ReadColumnCode, int OutBColumnId);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перерахунок_аркуша_джерела_перераховує_читача_що_читає_лише_результати_через_ребро_залежності()
    {
        var f = await ArrangeAsync(209911);
        await InsertValuesAsync(f, ("gas", 10m), ("gas", 5m), ("oil", 7m));
        var t = await ArrangeReaderTableAsync(f);

        // A рахує на рядках таблиці B (його вихід Y прив'язано до колонки Calculated цієї таблиці) і читає таблицю джерела через `table`.
        var a = await PublishScriptAsync(
            f, "A",
            $"table Src = {f.TableCode};\nrows from {t.TableCode};\noutput Y: kg = Sum(Src.{f.C2});",
            [("Y", t.ReadColumnId, t.TableDefId)]);

        // B читає ЛИШЕ результат A (власна колонка Calculated), `table` у ньому немає.
        var b = await PublishScriptAsync(
            f, "B", $"rows from {t.TableCode};\noutput Z: kg = row.{t.ReadColumnCode} * 10;", [("Z", t.OutBColumnId, t.TableDefId)]);

        await using (var db = f.Builder.CreateContext())
        {
            db.MethodologyReadTables.Add(new MethodologyReadTable(a.VersionId, f.TableCode));
            db.MethodologyDependencies.Add(new MethodologyDependency(b.MethodologyId, a.MethodologyId));
            await db.SaveChangesAsync();
        }

        // Прогін усього документа: B у пізнішій пачці, ніж A, і читає Y з цього ж прогону (сума входів 10 + 5 + 7 = 22).
        await RunJobAsync(f.Document, sheetDefId: null);
        var first = await ShapeAsync(f);
        Assert.True(
            first.SequenceEqual(ExpectedFirst),
            "shape: " + string.Join(",", first) + "; issues: " + await IssuesAsync(f));

        // Правка входу A (10 → 30) і перерахунок АРКУША-ДЖЕРЕЛА. B не прив'язаний до цього аркуша й не читає його таблиць
        // (`table` немає) — його втягує лише ребро B → A; A втягує читання таблиці джерела.
        await SetValueAsync(f, rowIndex: 0, 30m);
        await RunJobAsync(f.Document, sheetDefId: f.Document.SheetDefId);

        var second = await ShapeAsync(f);
        Assert.True(
            second.SequenceEqual(ExpectedSecond),
            "shape: " + string.Join(",", second) + "; issues: " + await IssuesAsync(f));

        // Каналу «читач таблиці» для B немає: після прогону в нього лишається лише ребро залежності.
        Assert.Empty(await ReadTablesOfAsync(f, b.VersionId));
    }

    // ───── стенд ─────

    private async Task<Scene> ArrangeAsync(int periodKey)
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
        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        return new Scene(builder, document, table.Code, codes[0], codes[1], unitId, tag);
    }

    private async Task InsertValuesAsync(Scene f, params (string Kind, decimal? Value)[] rows)
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

    private async Task SetValueAsync(Scene f, int rowIndex, decimal value)
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
        Scene f, string key, string text, (string Output, int ColumnId, int TableDefId)[] outputs)
    {
        await using var db = f.Builder.CreateContext();
        var methodology = new Methodology(EcrCode.Create($"RD{key}_{f.Tag}"), Name("script"));
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
                outputs[i].TableDefId, outputs[i].ColumnId, methodology.Id, outputs[i].Output, "{}"));
        }

        await db.SaveChangesAsync();
        return (methodology.Id, version.Id);
    }

    /// <summary>
    /// Другий аркуш: таблиця з двома рядками, колонкою <c>Calculated</c> «Y від A» (під прив'язку до виходу A) і колонкою
    /// виходу B (обидві — <c>Calculated</c>).
    /// </summary>
    private async Task<ReaderTable> ArrangeReaderTableAsync(Scene f)
    {
        await using var db = f.Builder.CreateContext();
        var sheet = new SheetDef(f.Document.TemplateVersionId, EcrCode.Create("SHB" + f.Tag), Name("Sheet B"), 2);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        var table = new TableDef(
            sheet.Id, EcrCode.Create("TBB" + f.Tag), Name("Table B"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        var readY = new ColumnDef(table.Id, EcrCode.Create("YFROMA_" + f.Tag), Name("Y from A"), 1, CellDataType.Calculated);
        var outB = new ColumnDef(table.Id, EcrCode.Create("OUTB_" + f.Tag), Name("Out B"), 2, CellDataType.Calculated);
        db.ColumnDefs.AddRange(readY, outB);
        await db.SaveChangesAsync();

        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        var firstRowId = await loader.ReserveIdsAsync("doc.TableRowSeq", 2, CancellationToken.None);
        db.TableInstances.Add(new TableInstance(f.Document.PeriodKey, instanceId, f.Document.DocumentId, table.Id, Now));
        for (var i = 0; i < 2; i++)
        {
            db.TableRows.Add(new TableRow(
                f.Document.PeriodKey, firstRowId + i, instanceId, RowKey.Create($"RB{i + 1}_{f.Tag}"), i + 1, Now));
        }

        await db.SaveChangesAsync();
        return new ReaderTable(table.Id, table.Code, readY.Id, readY.Code, outB.Id);
    }

    /// <summary>Таблиці, які версія читає за <c>calc.MethodologyReadTable</c>.</summary>
    private async Task<IReadOnlyList<string>> ReadTablesOfAsync(Scene f, int versionId)
    {
        await using var db = f.Builder.CreateContext();
        return await db.MethodologyReadTables
            .AsNoTracking()
            .Where(r => r.MethodologyVersionId == versionId)
            .Select(r => r.TableCode)
            .ToListAsync();
    }

    private async Task<string> IssuesAsync(Scene f)
    {
        await using var db = f.Builder.CreateContext();
        var issues = await db.CalculationIssues
            .AsNoTracking()
            .Where(i => i.DocumentId == f.Document.DocumentId && i.PeriodKey == f.Document.PeriodKey.Value)
            .Select(i => new { i.RowNumber, i.Kind, i.MethodologyId })
            .ToListAsync();
        return string.Join(";", issues.Select(i => $"{i.MethodologyId}:{i.RowNumber}:{i.Kind}"));
    }

    /// <summary>Форма актуальних результатів: <c>вихід|значення</c>, упорядковано.</summary>
    private async Task<List<string>> ShapeAsync(Scene f)
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
        services.AddScoped(_ => Substitute.For<IBackgroundJobScheduler>());

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = false });
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private sealed class NoOpProgress : IJobProgress
    {
        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
