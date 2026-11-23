// tests/Ecr.Infrastructure.Tests/Jobs/YearPressSkipsUnchangedMonthsTests.SourceMonths.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ PERF-06b (<c>D-572</c>, закриває «не зроблено» D-566): гілка документів-джерел B179 детектора кнопки «Перерахувати» —
/// ПОМІСЯЧНА. Після правки джерела за березень (і його перерахунку) натискання споживача рахує березень і місяць на екрані, а не
/// всі місяці, новіші за прогін джерела.
/// </summary>
/// <remarks>
/// МУТАЦІЇ (локально): (1) <c>DocumentLevelProbeAsync</c> знову передає документи джерел у <c>FirstChangeAsync</c> (рівень
/// документа) — червоний рядок «місяць»; (2) у <c>SourceMonthsProbe</c> обсяг завжди «рік» — червоний рядок «місяць»;
/// (3) <c>SourceMonthsProbe</c> повертає порожньо — червоні рядки «рік» і «річне джерело».
/// </remarks>
public sealed partial class YearPressSkipsUnchangedMonthsTests
{
    /// <summary>
    /// Ребро «місяць» (<c>[Period].Year, [Period].Sequence</c>), місячне джерело: правка місячної колонки джерела в березні будить
    /// березень; «рік» (<c>[Period].Year</c>) і річне джерело (період року володіє всіма місяцями) — усі. Доти (рівень документа)
    /// рядок «місяць» давав усі шість місяців.
    /// </summary>
    [Theory]
    [InlineData("month", new[] { 202601, 202603 })]
    [InlineData("year", new[] { 202601, 202602, 202603, 202604, 202605, 202606 })]
    [InlineData("yearly-source", new[] { 202601, 202602, 202603, 202604, 202605, 202606 })]
    public async Task Зміна_джерела_за_березень_будить_лише_місяці_що_читають_березень(string edge, int[] expected)
    {
        _withGraph = true;
        var (builder, document) = await ArrangeAsync();
        var registryCode = await ConsumerReadsSourceAsync(builder, document, edge, yearlySource: edge == "yearly-source");

        // Правка джерела й новий актуальний прогін його березня — після базових прогонів споживача.
        var (source, sourcePeriod) = registryCode;
        await CellChangeAsync(
            source, sourcePeriod, source.MonthColumnDefIds[2], Application.Documents.CellChangeOrigins.UserEdit, Baseline.AddMinutes(1));

        var run = await PressAsync(builder, document, screen: 202601, Press);

        Assert.Equal(expected, run.Steps);
    }

    /// <summary>
    /// Зв'язка «джерело → довідник», споживач (шаблон <paramref name="document"/>) читає довідник формулою з ключами за
    /// <paramref name="edge"/>; документ джерела — березень (місячний проєкт) або рік (річний).
    /// </summary>
    private async Task<(TestDocument Source, int Period)> ConsumerReadsSourceAsync(
        TestDocumentBuilder builder, TestDocument document, string edge, bool yearlySource)
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var sourcePeriod = yearlySource ? 202601 : 202603;
        var source = await builder.BuildAsync(periodKey: sourcePeriod, monthColumns: 12);
        await using var db = builder.CreateContext();
        if (yearlySource)
        {
            await db.Projects.Where(p => p.Id == source.ProjectId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.PeriodKind, PeriodKind.Yearly));
        }

        var registry = new RegistryDef(
            EcrCode.Create($"P6BR{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "p6b" }), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        var keys = edge == "year" ? "[Period].Year" : "[Period].Year, [Period].Sequence";
        var formula = new FormulaDef(
            document.TableDefId, FormulaScope.Column,
            $"REGFIELD(REGFIND('{registry.Code}', {keys}, 'S', 'I'), 'VALUE')", ExpressionDialect.Template);
        formula.AssignColumn(document.ColumnDefIds[2]);
        db.FormulaDefs.Add(formula);

        var binding = new DocumentSourceBinding($"P6B_{tag}", source.TemplateId, registry.Id);
        binding.AddConsumer(document.TemplateId);
        db.DocumentSourceBindings.Add(binding);
        await db.SaveChangesAsync();
        return (source, sourcePeriod);
    }
}
