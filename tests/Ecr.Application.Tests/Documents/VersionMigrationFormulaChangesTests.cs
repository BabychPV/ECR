// tests/Ecr.Application.Tests/Documents/VersionMigrationFormulaChangesTests.cs
using Ecr.Application.Documents.VersionMigration;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// LP-41: перенос версії лишав нові/змінені формули порожніми до явного «Перерахувати». Тут — РІШЕННЯ «чи ставити
/// перерахунок» (<c>VersionMigrationPlan.FormulaChanges</c>) і ФОРМА постановки (<c>MigrationRecalculation</c>).
/// </summary>
/// <remarks>
/// ⚠ Мутаційні докази (червоніють названі тести): у <c>CompareFormulas</c> прибрати перевірку виразу
/// (<c>old.Expression</c> …) — «змінений вираз» лишається червоним, «незмінна формула» зеленим не стане навпаки:
/// прибрати <c>return</c> з порівняння — «незмінна» червоніє; замінити <c>EnqueueCoalescedAsync</c> на
/// <c>EnqueueExclusiveAsync</c> у <c>MigrationRecalculation</c> — червоніють «злиття без витіснення» і «ціль проєкту».
/// </remarks>
public sealed class VersionMigrationFormulaChangesTests
{
    private const string Sheet = "S41";

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Нова_формула_на_наявній_колонці_є_змінною()
    {
        var plan = Plan(
            Version(1, [new Col("A", CellDataType.Decimal, null)]),
            Version(2, [new Col("A", CellDataType.Formula, "[B] * 2"), new Col("B", CellDataType.Decimal, null)]));

        Assert.Equal([$"{Sheet}.T41"], plan.FormulaChanges);
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Нова_Formula_колонка_з_формулою_є_змінною()
    {
        var plan = Plan(
            Version(1, [new Col("A", CellDataType.Decimal, null)]),
            Version(2, [new Col("A", CellDataType.Decimal, null), new Col("F", CellDataType.Formula, "[A] + 1")]));

        Assert.Equal([$"{Sheet}.T41"], plan.FormulaChanges);
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Змінений_вираз_є_змінним()
    {
        var plan = Plan(
            Version(1, [new Col("A", CellDataType.Decimal, null), new Col("F", CellDataType.Formula, "[A] + 1")]),
            Version(2, [new Col("A", CellDataType.Decimal, null), new Col("F", CellDataType.Formula, "[A] + 2")]));

        Assert.Equal([$"{Sheet}.T41"], plan.FormulaChanges);
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Незмінна_формула_перерахунку_не_потребує()
    {
        var plan = Plan(
            Version(1, [new Col("A", CellDataType.Decimal, null), new Col("F", CellDataType.Formula, "[A] + 1")]),
            Version(2, [new Col("A", CellDataType.Decimal, null), new Col("F", CellDataType.Formula, " [A] + 1 ")]));

        Assert.Empty(plan.FormulaChanges);
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Прибрана_формула_перерахунку_не_потребує()
    {
        var plan = Plan(
            Version(1, [new Col("A", CellDataType.Decimal, null), new Col("F", CellDataType.Formula, "[A] + 1")]),
            Version(2, [new Col("A", CellDataType.Decimal, null)]));

        Assert.Empty(plan.FormulaChanges);
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Нова_обчислювана_колонка_без_формули_таблиці_теж_є_змінною()
    {
        var plan = Plan(
            Version(1, [new Col("A", CellDataType.Decimal, null)]),
            Version(2, [new Col("A", CellDataType.Decimal, null), new Col("K", CellDataType.Calculated, null)]));

        Assert.Equal([$"{Sheet}.T41"], plan.FormulaChanges);
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Лише_підписи_без_формул_перерахунку_не_потребують()
    {
        var plan = Plan(
            Version(1, [new Col("A", CellDataType.Decimal, null)]),
            Version(2, [new Col("A", CellDataType.Decimal, null)]));

        Assert.Empty(plan.FormulaChanges);
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Зміна_діалекту_формули_є_змінною()
    {
        var plan = Plan(
            Version(1, [new Col("F", CellDataType.Formula, "1", ExpressionDialect.Template)]),
            Version(2, [new Col("F", CellDataType.Formula, "1", ExpressionDialect.Methodology)]));

        Assert.Equal([$"{Sheet}.T41"], plan.FormulaChanges);
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Перерахунок_проєкту_ставиться_без_витіснення_на_ціль_проєкту_системною_задачею()
    {
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        jobs.EnqueueCoalescedAsync<IRecalculationJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns("IRecalculationJob~p7");

        var jobId = await MigrationRecalculation.EnqueueAsync(
            jobs, NullLogger.Instance, new DateTime(2026, 10, 13, 8, 0, 0, DateTimeKind.Utc), 7, 2, CancellationToken.None);

        Assert.Equal("IRecalculationJob~p7", jobId);

        // ⛔ Злиття, а не витіснення: витіснення перервало б перерахунок, що вже йде; автор null — системна задача;
        // ціль — проєкт, не документ (розклад на документи робить сама задача).
        await jobs.Received(1).EnqueueCoalescedAsync<IRecalculationJob>(
            MigrationRecalculation.TargetOf(7), Arg.Any<object?>(), Arg.Any<CancellationToken>(), null);
        await jobs.DidNotReceiveWithAnyArgs().EnqueueExclusiveAsync<IRecalculationJob>(default!, default, default, default);

        var payload = System.Text.Json.JsonSerializer.Serialize(
            jobs.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IBackgroundJobScheduler.EnqueueCoalescedAsync))
                .GetArguments()[1]);
        using var doc = System.Text.Json.JsonDocument.Parse(payload);
        Assert.Equal(7, doc.RootElement.GetProperty("projectId").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, doc.RootElement.GetProperty("periodKey").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, doc.RootElement.GetProperty("triggeredByUserId").ValueKind);
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Збій_постановки_після_коміту_не_кидає_а_повертає_null()
    {
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        jobs.EnqueueCoalescedAsync<IRecalculationJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns<Task<string>>(_ => throw new InvalidOperationException("черга недоступна"));

        var jobId = await MigrationRecalculation.EnqueueAsync(
            jobs, NullLogger.Instance, DateTime.UtcNow, 7, 1, CancellationToken.None);

        Assert.Null(jobId);
    }

    private static VersionMigrationPlan Plan(TemplateVersionSnapshot from, TemplateVersionSnapshot to)
        => VersionMigrationPlanner.Plan(from, to, [], new Dictionary<int, long>());

    private sealed record Col(string Code, CellDataType Type, string? Expression, ExpressionDialect Dialect = ExpressionDialect.Template);

    private static TemplateVersionSnapshot Version(int versionId, IReadOnlyList<Col> columns)
    {
        var sheet = new SheetDef(versionId, EcrCode.Create(Sheet), Name("Sheet"), 1);
        SetId(sheet, versionId * 10);

        var tableId = versionId * 100;
        var table = new TableDef(
            sheetDefId: 1, EcrCode.Create("T41"), Name("T41"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        SetId(table, tableId);

        var next = versionId * 1000;
        foreach (var spec in columns)
        {
            var column = new ColumnDef(tableId, EcrCode.Create(spec.Code), Name(spec.Code), 1, spec.Type);
            SetId(column, ++next);
            table.AddColumn(column);

            if (spec.Expression is not null)
            {
                var formula = new FormulaDef(tableId, FormulaScope.Column, spec.Expression, spec.Dialect);
                formula.AssignColumn(column.Id);
                SetId(formula, ++next);
                table.AddFormula(formula);
            }
        }

        sheet.AddTable(table);

        return new TemplateVersionSnapshot(
            versionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef>(),
            RowsByKey: new Dictionary<(int, string), RowDef>());
    }

    private static void SetId(object entity, int id)
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
